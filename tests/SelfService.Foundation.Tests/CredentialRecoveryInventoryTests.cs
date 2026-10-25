using System.Collections;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialRecoveryInventoryTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Authenticated_all_profile_inventory_includes_expired_revoked_and_rewrapped_dependencies_without_any_write(bool strong)
    {
        using var f = new RecoveryFixture(strong); var alpha = f.Cell.Scope with { AuthProfileId = "profile/private-alpha-source" };
        var zeta = f.Cell.Scope with { AuthProfileId = "profile/private-zeta-source", ProjectId = "sibling-project" };
        await f.Put(alpha); await f.Put(zeta); f.Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32);
        var rotated = new CredentialVault(f.Options with { EncryptionKeyId = "key-2" }, f.Cell.Keys, f.Cell.Clock);
        await rotated.RewrapAsync(alpha.AuthProfileId, 1, "key-1"); await f.Vault.RevokeAsync(zeta.AuthProfileId, 1);
        f.Cell.Clock.Now = f.Cell.Clock.Now.AddHours(2);
        var inspection = new CredentialVault(f.Options with { EncryptionKeyId = "unprovisioned-write-key" }, f.Cell.Keys, f.Cell.Clock);
        var expected = inspection.DescribeNamespace(); var before = Snapshot(f.Cell.Root); f.Cell.Keys.Requests.Clear();
        var report = await inspection.InspectRecoveryDependenciesAsync(expected);
        Assert.Equal(2, report.ProfileDependencies.Count); Assert.Equal(new[] { "key-1", "key-2" }, report.CurrentRequiredKeyIds);
        Assert.Equal(report.ProfileDependencies.Select(p => p.ProfileIdSha256).Order(StringComparer.Ordinal), report.ProfileDependencies.Select(p => p.ProfileIdSha256));
        var revoked = Assert.Single(report.ProfileDependencies, p => p.ProfileIdSha256 == Hash(zeta.AuthProfileId)); Assert.False(revoked.Enabled); Assert.Equal(2, revoked.Generation);
        var rewrapped = Assert.Single(report.ProfileDependencies, p => p.ProfileIdSha256 == Hash(alpha.AuthProfileId)); Assert.True(rewrapped.Enabled); Assert.Equal("key-2", rewrapped.SourceKeyId);
        Assert.All(report.ProfileDependencies, p => Assert.True(p.ExpiresUnixMilliseconds < report.ObservedUtc.ToUnixTimeMilliseconds()));
        Assert.Equal(2, f.Cell.Keys.Requests.Count); Assert.All(f.Cell.Keys.Requests, r => Assert.Contains(r.KeyId, new[] { "key-1", "key-2" }));
        Assert.Equal(before, Snapshot(f.Cell.Root)); Assert.Equal("NotAssessed", report.IndependentBackupCoverage); Assert.False(report.KeyRetirementAuthorized);
        Assert.Equal(expected.CustomerId, report.CustomerId); Assert.Equal(expected.CellId, report.CellId); Assert.Equal(expected.VaultNamespaceId, report.VaultNamespaceId);
        Assert.Equal(expected.NamespaceCommitmentSha256, report.NamespaceCommitmentSha256); Assert.Equal(expected.AuthorityNamespaceId, report.AuthorityNamespaceId);
        Assert.Equal(expected.AuthorityBindingSha256, report.AuthorityBindingSha256);
        Assert.Equal(report.Sha256, CanonicalDigest(report));
        var text = JsonSerializer.Serialize(report, Json); Assert.DoesNotContain(CredentialFixture.Token, text); Assert.DoesNotContain(f.Cell.Root, text);
        Assert.DoesNotContain(alpha.AuthProfileId, text); Assert.DoesNotContain(zeta.AuthProfileId, text); Assert.DoesNotContain("profileScopeJson", text);
        Assert.DoesNotContain("ciphertext", text, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("protectedKey", text);
        Assert.All(typeof(CredentialRecoveryInventory).GetProperties(), p => Assert.Null(p.SetMethod)); Assert.Empty(typeof(CredentialRecoveryInventory).GetConstructors());
        Assert.Empty(typeof(CredentialRecoveryProfileDependency).GetConstructors());
        Assert.Throws<NotSupportedException>(() => ((IList)report.ProfileDependencies).Clear()); Assert.Throws<NotSupportedException>(() => ((IList)report.CurrentRequiredKeyIds).Clear());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Empty_existing_namespace_is_still_exactly_bound_and_never_requires_a_write_key(bool strong)
    {
        using var f = new RecoveryFixture(strong); f.Cell.Keys.Values.Clear(); var identity = f.Vault.DescribeNamespace(); var before = Snapshot(f.Cell.Root);
        var report = await f.Vault.InspectRecoveryDependenciesAsync(identity);
        Assert.Empty(report.ProfileDependencies); Assert.Empty(report.CurrentRequiredKeyIds); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(strong, report.AuthorityNamespaceId is not null); Assert.Equal(0, report.AuthorityRevision); Assert.Equal(strong, report.AuthorityStateSha256 is not null);
        Assert.Equal(before, Snapshot(f.Cell.Root)); Assert.Equal(report.Sha256, CanonicalDigest(report)); Assert.False(report.KeyRetirementAuthorized);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Real_DPAPI_source_keys_remain_untouched_and_rewrap_inventory_never_retires_old_custody(bool strong)
    {
        using var cell = new CredentialFixture(); var keys = cell.ProvisionNativeKeys("recovery-native-keys");
        foreach (var id in new[] { "key-1", "key-2" }) using (await keys.ProvisionKeyAsync(cell.Scope.CustomerId, cell.Scope.CellId, id)) { }
        var options = cell.Options with { RootPath = Path.Combine(cell.Root, "native-inventory-vault"),
            RollbackProtection = strong ? new(Path.Combine(cell.Root, "native-inventory-authority")) : null };
        var vault = strong ? CredentialVault.ProvisionStrong(options, keys, cell.Clock) : CredentialVault.Provision(options, keys, cell.Clock);
        var other = cell.Scope with { AuthProfileId = "profile/second-native" };
        using (var token = CredentialSecret.FromUtf8(CredentialFixture.Token))
        { await vault.PutAsync(cell.Scope, token, cell.Clock.Now.AddHours(1)); await vault.PutAsync(other, token, cell.Clock.Now.AddHours(1)); }
        var rotated = new CredentialVault(options with { EncryptionKeyId = "key-2" }, keys, cell.Clock);
        await rotated.RewrapAsync(cell.Scope.AuthProfileId, 1, "key-1"); var before = Snapshot(cell.Root);
        var report = await rotated.InspectRecoveryDependenciesAsync(rotated.DescribeNamespace()); Assert.Equal(new[] { "key-1", "key-2" }, report.CurrentRequiredKeyIds);
        Assert.Equal(before, Snapshot(cell.Root)); await rotated.RewrapAsync(other.AuthProfileId, 1, "key-1");
        var after = await rotated.InspectRecoveryDependenciesAsync(rotated.DescribeNamespace()); Assert.Equal("key-2", Assert.Single(after.CurrentRequiredKeyIds));
        Assert.False(after.KeyRetirementAuthorized); Assert.Equal("NotAssessed", after.IndependentBackupCoverage);
        Assert.Equal(2, Directory.GetFiles(Path.Combine(cell.Root, "recovery-native-keys"), "*.dpapi.json", SearchOption.AllDirectories).Length);
        Assert.Equal(new[] { "key-1", "key-2" }, report.CurrentRequiredKeyIds); // Prior immutable report remains its original observation.
    }

    [Theory]
    [InlineData("offset-sub-millisecond")]
    [InlineData("negative-unix-expiry")]
    public async Task Expiry_projection_preserves_valid_existing_precision_and_offsets_through_the_exact_envelope_commitment(string variant)
    {
        using var f = new RecoveryFixture();
        if (variant == "negative-unix-expiry") f.Cell.Clock.Now = DateTimeOffset.Parse("1960-01-01T00:00:00Z");
        var expiry = f.Cell.Clock.Now.AddHours(1).AddTicks(1234).ToOffset(TimeSpan.FromHours(5.5)); await f.Put(f.Cell.Scope, expiry);
        f.Cell.Clock.Now = DateTimeOffset.Parse("2026-10-24T12:00:00Z"); var path = Assert.Single(Directory.GetFiles(f.Options.RootPath, "*.credential.json"));
        var envelope = File.ReadAllBytes(path); var report = await f.Vault.InspectRecoveryDependenciesAsync(f.Vault.DescribeNamespace());
        var profile = Assert.Single(report.ProfileDependencies); Assert.Equal(expiry.ToUnixTimeMilliseconds(), profile.ExpiresUnixMilliseconds);
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(envelope)), profile.EnvelopeSha256); Assert.Equal(envelope, File.ReadAllBytes(path));
        if (variant == "negative-unix-expiry") Assert.True(profile.ExpiresUnixMilliseconds < 0);
    }

    [WindowsCredentialFact]
    public async Task Terminal_safe_integer_generation_is_readable_but_larger_authenticated_generation_is_refused()
    {
        using var f = new RecoveryFixture(); await f.Put(f.Cell.Scope); var identity = f.Vault.DescribeNamespace();
        RewriteGeneration(f, CredentialRecoveryInventory.MaximumCounter);
        Assert.Equal(CredentialRecoveryInventory.MaximumCounter, Assert.Single((await f.Vault.InspectRecoveryDependenciesAsync(identity)).ProfileDependencies).Generation);
        RewriteGeneration(f, CredentialRecoveryInventory.MaximumCounter + 1); var before = Snapshot(f.Cell.Root);
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Vault.InspectRecoveryDependenciesAsync(identity));
        Assert.Equal("credential-recovery-inventory-metadata-invalid", error.Code); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData("source-key")]
    [InlineData("project")]
    [InlineData("environment")]
    public async Task Authenticated_secret_shaped_public_identifiers_cannot_escape_into_inventory_metadata(string field)
    {
        using var f = new RecoveryFixture(); var marker = string.Concat("ghp", "_", new string('S', 25)); var scope = f.Cell.Scope;
        if (field == "project") scope = scope with { ProjectId = marker };
        if (field == "environment") scope = scope with { EnvironmentId = marker };
        var vault = f.Vault;
        if (field == "source-key") { f.Cell.Keys.Values[marker] = RandomNumberGenerator.GetBytes(32); vault = new(f.Options with { EncryptionKeyId = marker }, f.Cell.Keys, f.Cell.Clock); }
        using (var token = CredentialSecret.FromUtf8(CredentialFixture.Token)) await vault.PutAsync(scope, token, f.Cell.Clock.Now.AddHours(1));
        var before = Snapshot(f.Cell.Root); var error = await Assert.ThrowsAsync<CredentialException>(() => vault.InspectRecoveryDependenciesAsync(vault.DescribeNamespace()));
        Assert.Equal("credential-recovery-inventory-metadata-invalid", error.Code); Assert.DoesNotContain(marker, error.ToString()); Assert.DoesNotContain(f.Cell.Root, error.ToString());
        Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [WindowsCredentialFact]
    public async Task Full_256_profile_inventory_authenticates_every_profile_and_can_exceed_the_small_generic_payload_limit()
    {
        using var f = new RecoveryFixture();
        for (var i = 0; i < CredentialRecoveryInventory.MaximumProfiles; i++) await f.Put(f.Cell.Scope with { AuthProfileId = "profile/inventory-" + i });
        f.Cell.Keys.Requests.Clear(); var before = Snapshot(f.Cell.Root); var report = await f.Vault.InspectRecoveryDependenciesAsync(f.Vault.DescribeNamespace());
        Assert.Equal(CredentialRecoveryInventory.MaximumProfiles, report.ProfileDependencies.Count); Assert.Equal(CredentialRecoveryInventory.MaximumProfiles, f.Cell.Keys.Requests.Count);
        Assert.Equal("key-1", Assert.Single(report.CurrentRequiredKeyIds)); Assert.Equal(report.Sha256, CanonicalDigest(report));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(report, Json); Assert.InRange(bytes.Length, 65537, CredentialRecoveryInventory.MaximumReportBytes);
        Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    static string CanonicalDigest(CredentialRecoveryInventory report) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    {
        report.Schema, report.CustomerId, report.CellId, report.VaultNamespaceId, report.NamespaceCommitmentSha256, report.AuthorityNamespaceId,
        report.AuthorityBindingSha256, report.AuthorityRevision, report.AuthorityStateSha256, observedUnixMilliseconds = report.ObservedUtc.ToUnixTimeMilliseconds(),
        report.ProfileDependencies, report.CurrentRequiredKeyIds, report.IndependentBackupCoverage, report.KeyRetirementAuthorized
    }, Json)));
    static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static string[] Snapshot(string root) => new[] { root }.Concat(Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories)).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + (Directory.Exists(path) ? "directory" : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))) + ":" +
            (Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner)).ToArray();

    static void RewriteGeneration(RecoveryFixture fixture, long generation)
    {
        var path = Assert.Single(Directory.GetFiles(fixture.Options.RootPath, "*.credential.json")); var node = JsonNode.Parse(File.ReadAllText(path))!;
        var profile = node["profile"]!.Deserialize<CredentialProfile>(Json)! with { Generation = generation }; var keyId = node["keyId"]!.GetValue<string>();
        var aad = (byte[])typeof(CredentialVault).GetMethod("Aad", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [keyId, profile])!;
        var clear = Encoding.UTF8.GetBytes(CredentialFixture.Token); var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[clear.Length]; var tag = new byte[16];
        try
        {
            using var aes = new AesGcm(fixture.Cell.Keys.Values[keyId], 16); aes.Encrypt(nonce, clear, cipher, tag, aad);
            node["profile"] = JsonSerializer.SerializeToNode(profile, Json); node["nonce"] = Convert.ToBase64String(nonce);
            node["ciphertext"] = Convert.ToBase64String(cipher); node["tag"] = Convert.ToBase64String(tag); File.WriteAllText(path, node.ToJsonString(Json));
        }
        finally { CryptographicOperations.ZeroMemory(clear); }
    }

    sealed class RecoveryFixture : IDisposable
    {
        public CredentialFixture Cell { get; } = new();
        public CredentialVaultOptions Options { get; }
        public CredentialVault Vault { get; }
        public RecoveryFixture(bool strong = false)
        {
            Options = strong ? Cell.Options with { RootPath = Path.Combine(Cell.Root, "inventory-strong"),
                RollbackProtection = new(Path.Combine(Cell.Root, "inventory-authority")) } : Cell.Options;
            Vault = strong ? CredentialVault.ProvisionStrong(Options, Cell.Keys, Cell.Clock) : Cell.Vault;
        }
        public async Task Put(CredentialScope scope, DateTimeOffset? expires = null)
        { using var token = CredentialSecret.FromUtf8(CredentialFixture.Token); await Vault.PutAsync(scope, token, expires ?? Cell.Clock.Now.AddHours(1)); }
        public void Dispose() => Cell.Dispose();
    }
}
