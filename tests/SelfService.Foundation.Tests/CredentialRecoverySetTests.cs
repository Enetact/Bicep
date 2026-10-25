using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialRecoverySetTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(PipelineRequestFactory.GitHub, false)] [InlineData(PipelineRequestFactory.GitHub, true)]
    [InlineData(PipelineRequestFactory.AzureDevOps, false)] [InlineData(PipelineRequestFactory.AzureDevOps, true)]
    public async Task Capture_freezes_exact_profiles_and_custody_with_inactive_dependencies_and_private_explicit_roundtrip(string provider, bool strong)
    {
        using var f = new CredentialFixture(provider); var options = Options(f, strong); var vault = Vault(f, options, strong);
        var alpha = f.Scope with { AuthProfileId = "profile/private-alpha" }; var beta = f.Scope with { AuthProfileId = "profile/private-beta" };
        var expiry = f.Clock.Now.AddHours(1).AddTicks(1234).ToOffset(TimeSpan.FromHours(5.5));
        using (var material = CredentialSecret.FromUtf8(CredentialFixture.Token))
        { await vault.PutAsync(alpha, material, expiry); await vault.PutAsync(beta, material, expiry); }
        await vault.RevokeAsync(beta.AuthProfileId, 1); f.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32);
        await new CredentialVault(options with { EncryptionKeyId = "key-2" }, f.Keys, f.Clock).RewrapAsync(alpha.AuthProfileId, 1, "key-1");
        f.Clock.Now = f.Clock.Now.AddHours(2); var inspection = new CredentialVault(options with { EncryptionKeyId = "absent-write-key" }, f.Keys, f.Clock);
        var identity = inspection.DescribeNamespace(); var before = Snapshot(f.Root); f.Keys.Requests.Clear();
        using var signer = RSA.Create(2048);
        using var set = await inspection.CaptureSignedRecoverySetAsync(identity, signer, "set-signer", 12, f.Clock.Now, f.Clock.Now.AddDays(1));
        Assert.Equal(2, f.Keys.Requests.Count); Assert.All(f.Keys.Requests, r => Assert.Contains(r.KeyId, new[] { "key-1", "key-2" }));
        var pin = CredentialRecoverySets.Authority(set); using var key = Public(signer);
        var verified = CredentialRecoverySets.Verify(set, pin, key, identity, f.Clock); Assert.Equal(12, verified.Signature.Generation);
        Assert.True(verified.SignatureVerified); Assert.True(verified.RetainedContentVerified); Assert.False(verified.EncryptedProfileAuthenticationPerformed);
        Assert.Equal(identity, verified.Signature.SourceNamespace); Assert.Equal(2, verified.Inventory.ProfileDependencies.Count);
        Assert.Equal(new[] { "key-1", "key-2" }, verified.Inventory.CurrentRequiredKeyIds);
        Assert.False(Assert.Single(verified.Inventory.ProfileDependencies, p => p.ProfileIdSha256 == Hash(beta.AuthProfileId)).Enabled);
        Assert.All(verified.Inventory.ProfileDependencies, p => Assert.Equal(expiry.ToUnixTimeMilliseconds(), p.ExpiresUnixMilliseconds));
        Assert.Equal("NotAssessed", verified.IndependentBackupCoverage); Assert.False(verified.KeyBackupVerified); Assert.False(verified.KeyRetirementAuthorized);
        Assert.False(verified.RestoreAuthorized); Assert.False(verified.ActivationPerformed); Assert.False(verified.ExternalExecutionAuthorized); Assert.False(verified.ProductionRecoveryQualified);
        var publicJson = JsonSerializer.Serialize(verified, Json); Assert.DoesNotContain(f.Root, publicJson); Assert.DoesNotContain(alpha.AuthProfileId, publicJson);
        Assert.DoesNotContain("ciphertext", publicJson, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain(CredentialFixture.Token, publicJson);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(set, Json));
        using var stream = new MemoryStream(); set.WritePrivateArtifact(stream); var artifact = stream.ToArray();
        Assert.Equal(set.Sha256, Hash(artifact)); Assert.Equal(set.SerializedLength, artifact.Length);
        using var document = JsonDocument.Parse(artifact); var contentBytes = Convert.FromBase64String(document.RootElement.GetProperty("content").GetString()!);
        using var content = JsonDocument.Parse(contentBytes); var files = content.RootElement.GetProperty("files").EnumerateArray().ToArray();
        Assert.Equal(strong ? 9 : 4, verified.FileCount); Assert.Equal(verified.FileCount, files.Length);
        foreach (var file in files)
        {
            var name = file.GetProperty("name").GetString()!; var root = name.StartsWith("vault/", StringComparison.Ordinal) ? options.RootPath : options.RollbackProtection!.AuthorityDirectory;
            var retained = Convert.FromBase64String(file.GetProperty("bytes").GetString()!);
            Assert.Equal(File.ReadAllBytes(Path.Combine(root, name[(name.IndexOf('/') + 1)..])), retained);
            Assert.DoesNotContain(".dpapi.json", name); Assert.DoesNotContain(".keys.lock", name);
            Assert.DoesNotContain(CredentialFixture.Token, Encoding.UTF8.GetString(retained));
        }
        using var privateMarker = JsonDocument.Parse(Convert.FromBase64String(files.Single(file => file.GetProperty("name").GetString() == "vault/.credential-namespace.json").GetProperty("bytes").GetString()!));
        Assert.Equal(options.RootPath, privateMarker.RootElement.GetProperty("rootPath").GetString());
        using var parsed = CredentialRecoverySets.Parse(artifact); Array.Fill(artifact, (byte)0); set.Dispose();
        Assert.Equal(pin.ArtifactSha256, CredentialRecoverySets.Verify(parsed, pin, key, identity, f.Clock).Sha256);
        Assert.Throws<CredentialException>(() => set.WritePrivateArtifact(new MemoryStream()));
        Assert.Equal(before, Snapshot(f.Root)); CryptographicOperations.ZeroMemory(contentBytes);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Empty_committed_namespace_is_a_valid_bounded_private_set_but_grants_no_backup_or_retirement_authority(bool strong)
    {
        using var f = new CredentialFixture(); var options = Options(f, strong); var vault = Vault(f, options, strong); using var signer = RSA.Create(2048);
        var identity = vault.DescribeNamespace(); var before = Snapshot(f.Root); f.Keys.Requests.Clear();
        using var set = await vault.CaptureSignedRecoverySetAsync(identity, signer, "set-signer", 1, f.Clock.Now, f.Clock.Now.AddHours(1));
        using var key = Public(signer); var verified = CredentialRecoverySets.Verify(set, CredentialRecoverySets.Authority(set), key, identity, f.Clock);
        Assert.Empty(verified.Inventory.ProfileDependencies); Assert.Empty(verified.Inventory.CurrentRequiredKeyIds); Assert.Empty(f.Keys.Requests);
        Assert.Equal(strong ? 7 : 2, verified.FileCount); Assert.Equal("NotAssessed", verified.IndependentBackupCoverage); Assert.False(verified.KeyRetirementAuthorized);
        Assert.Equal(before, Snapshot(f.Root));
    }

    [WindowsCredentialFact]
    public async Task Public_only_signer_is_rejected_before_any_source_key_lookup_and_does_not_strand_guard()
    {
        using var f = new CredentialFixture(); await f.StoreToken(); using var signer = RSA.Create(2048); using var publicOnly = Public(signer);
        var identity = f.Vault.DescribeNamespace(); var before = Snapshot(f.Root); f.Keys.Requests.Clear();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Vault.CaptureSignedRecoverySetAsync(identity, publicOnly, "set-signer", 1, f.Clock.Now, f.Clock.Now.AddHours(1)));
        Assert.Empty(f.Keys.Requests); Assert.DoesNotContain(f.Root, error.ToString()); Assert.Equal(before, Snapshot(f.Root));
        using var successful = await f.Vault.CaptureSignedRecoverySetAsync(identity, signer, "set-signer", 1, f.Clock.Now, f.Clock.Now.AddHours(1));
        Assert.Equal(1, successful.Signature.ProfileCount);
    }

    [WindowsCredentialFact]
    public async Task Actual_signature_must_verify_against_preflight_frozen_public_key_after_successful_probe()
    {
        using var f = new CredentialFixture(); await f.StoreToken(); using var signer = new ChangingSigner();
        var identity = f.Vault.DescribeNamespace(); var before = Snapshot(f.Root);
        Assert.Equal("credential-recovery-set-signing-unavailable", (await Assert.ThrowsAsync<CredentialException>(() => f.Vault.CaptureSignedRecoverySetAsync(
            identity, signer, "set-signer", 1, f.Clock.Now, f.Clock.Now.AddHours(1)))).Code);
        Assert.Equal(before, Snapshot(f.Root)); using var validSigner = RSA.Create(2048);
        using var successful = await f.Vault.CaptureSignedRecoverySetAsync(identity, validSigner, "set-signer", 1, f.Clock.Now, f.Clock.Now.AddHours(1));
        Assert.Equal(1, successful.Signature.ProfileCount);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Native_current_user_keys_authenticate_capture_without_exporting_key_namespace_or_changing_it(bool strong)
    {
        using var f = new CredentialFixture(); var keys = f.ProvisionNativeKeys("recovery-set-native-keys");
        using (await keys.ProvisionKeyAsync(f.Scope.CustomerId, f.Scope.CellId, "key-1")) { }
        var options = f.Options with { RootPath = Path.Combine(f.Root, "native-set-vault"), RollbackProtection = strong ? new(Path.Combine(f.Root, "native-set-authority")) : null };
        var vault = strong ? CredentialVault.ProvisionStrong(options, keys, f.Clock) : CredentialVault.Provision(options, keys, f.Clock);
        using (var material = CredentialSecret.FromUtf8(CredentialFixture.Token)) await vault.PutAsync(f.Scope, material, f.Clock.Now.AddHours(1));
        var identity = vault.DescribeNamespace(); var before = Snapshot(f.Root); using var signer = RSA.Create(2048);
        using var set = await vault.CaptureSignedRecoverySetAsync(identity, signer, "set-signer", 1, f.Clock.Now, f.Clock.Now.AddHours(1));
        using var publicKey = Public(signer); var result = CredentialRecoverySets.Verify(set, CredentialRecoverySets.Authority(set), publicKey, identity, f.Clock);
        Assert.Equal("key-1", Assert.Single(result.Inventory.CurrentRequiredKeyIds)); Assert.False(result.KeyBackupVerified);
        using var bytes = new MemoryStream(); set.WritePrivateArtifact(bytes); using var artifact = JsonDocument.Parse(bytes.ToArray());
        using var content = JsonDocument.Parse(Convert.FromBase64String(artifact.RootElement.GetProperty("content").GetString()!));
        Assert.All(content.RootElement.GetProperty("files").EnumerateArray(), file => Assert.DoesNotContain("keys", file.GetProperty("name").GetString()!, StringComparison.OrdinalIgnoreCase));
        Assert.Single(Directory.GetFiles(Path.Combine(f.Root, "recovery-set-native-keys"), "*.dpapi.json", SearchOption.AllDirectories)); Assert.Equal(before, Snapshot(f.Root));
    }

    static CredentialVaultOptions Options(CredentialFixture f, bool strong) => strong ? f.Options with
    { RootPath = Path.Combine(f.Root, "recovery-set-strong"), RollbackProtection = new(Path.Combine(f.Root, "recovery-set-authority")) } : f.Options;
    static CredentialVault Vault(CredentialFixture f, CredentialVaultOptions options, bool strong) => strong ? CredentialVault.ProvisionStrong(options, f.Keys, f.Clock) : f.Vault;
    static RSA Public(RSA signer) { var result = RSA.Create(); result.ImportSubjectPublicKeyInfo(signer.ExportSubjectPublicKeyInfo(), out _); return result; }
    static string Hash(string value) => Hash(Encoding.UTF8.GetBytes(value));
    static string Hash(byte[] value) => Convert.ToHexStringLower(SHA256.HashData(value));
    static string[] Snapshot(string root) => new[] { root }.Concat(Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + (Directory.Exists(path) ? "directory" : Hash(File.ReadAllBytes(path))) + ":" +
            (Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner)).ToArray();

    sealed class ChangingSigner : RSA
    {
        readonly RSA first = RSA.Create(2048), second = RSA.Create(2048); int signatures;
        public override int KeySize { get => first.KeySize; set => throw new NotSupportedException(); }
        public override byte[] ExportSubjectPublicKeyInfo() => first.ExportSubjectPublicKeyInfo();
        public override RSAParameters ExportParameters(bool includePrivateParameters) => includePrivateParameters ? throw new NotSupportedException() : first.ExportParameters(false);
        public override void ImportParameters(RSAParameters parameters) => throw new NotSupportedException();
        public override byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) =>
            (Interlocked.Increment(ref signatures) == 1 ? first : second).SignHash(hash, hashAlgorithm, padding);
        public override bool VerifyHash(byte[] hash, byte[] signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) => first.VerifyHash(hash, signature, hashAlgorithm, padding);
        public override byte[] Encrypt(byte[] data, RSAEncryptionPadding padding) => throw new NotSupportedException();
        public override byte[] Decrypt(byte[] data, RSAEncryptionPadding padding) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) { first.Dispose(); second.Dispose(); } base.Dispose(disposing); }
    }
}
