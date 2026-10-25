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
public sealed class CredentialRecoverySetBoundaryTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Private_artifact_roundtrip_is_exact_detached_and_preserves_all_source_bytes_and_permissions(bool strong)
    {
        using var f = new SetFixture(strong); await f.Put(); var before = Snapshot(f.Cell.Root);
        using var captured = await f.Capture(); var bytes = Export(captured); var original = bytes.ToArray();
        Assert.Equal(captured.SerializedLength, bytes.Length); Assert.Equal(captured.Sha256, Hash(bytes));
        using var parsed = CredentialRecoverySets.Parse(bytes); bytes[0] ^= 1; captured.Dispose();
        Assert.Equal(original, Export(parsed)); f.Cell.Keys.Requests.Clear();
        var verified = CredentialRecoverySets.Verify(parsed, CredentialRecoverySets.Authority(parsed), f.Signer, f.Identity, f.Cell.Clock);
        Assert.Single(verified.Inventory.ProfileDependencies); Assert.Equal(strong ? 8 : 3, verified.FileCount);
        AssertConservative(verified); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
        var publicJson = JsonSerializer.Serialize(verified, Json);
        foreach (var excluded in new[] { CredentialFixture.Token, f.Cell.Root, f.Cell.Scope.AuthProfileId, "ciphertext", "protectedKey" })
            Assert.DoesNotContain(excluded, publicJson, StringComparison.OrdinalIgnoreCase);
        var content = Content(parsed); var marker = content["files"]!.AsArray().Single(n => n!["name"]!.GetValue<string>() == "vault/.credential-namespace.json")!;
        // Export is deliberately private: original custody metadata is not made confidential by base64.
        var retainedMarker = JsonNode.Parse(Convert.FromBase64String(marker["bytes"]!.GetValue<string>()))!;
        Assert.Equal(f.Options.RootPath, retainedMarker["rootPath"]!.GetValue<string>());
    }

    [WindowsCredentialFact]
    public async Task Opaque_artifacts_refuse_ordinary_JSON_and_disposed_export_parse_validation_has_no_key_lookup()
    {
        using var f = new SetFixture(); await f.Put(); var set = await f.Capture(); var pin = CredentialRecoverySets.Authority(set);
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(set));
        Assert.Throws<JsonException>(() => JsonSerializer.Deserialize<CredentialRecoverySet>("{}"));
        f.Cell.Keys.Requests.Clear(); set.Dispose(); set.Dispose();
        Assert.Equal("credential-recovery-set-disposed", Assert.Throws<CredentialException>(() => Export(set)).Code);
        Assert.Throws<CredentialException>(() => CredentialRecoverySets.Verify(set, pin, f.Signer, f.Identity, f.Cell.Clock));
        Assert.Empty(f.Cell.Keys.Requests);
    }

    [WindowsCredentialFact]
    public async Task A_valid_historical_set_requires_its_own_external_exact_selection_and_does_not_supply_latest_custody()
    {
        using var f = new SetFixture(); await f.Put(); using var old = await f.Capture(1); using var current = await f.Capture(2);
        var oldPin = CredentialRecoverySets.Authority(old); var currentPin = CredentialRecoverySets.Authority(current);
        Assert.Throws<CredentialException>(() => CredentialRecoverySets.Verify(old, currentPin, f.Signer, f.Identity, f.Cell.Clock));
        Assert.Throws<CredentialException>(() => CredentialRecoverySets.Verify(current, oldPin, f.Signer, f.Identity, f.Cell.Clock));
        AssertConservative(CredentialRecoverySets.Verify(old, oldPin, f.Signer, f.Identity, f.Cell.Clock));
        AssertConservative(CredentialRecoverySets.Verify(current, currentPin, f.Signer, f.Identity, f.Cell.Clock));
    }

    [Theory]
    [InlineData("customer")] [InlineData("cell")] [InlineData("set-id")] [InlineData("generation")]
    [InlineData("artifact-hash")] [InlineData("signer-id")] [InlineData("signer-hash")] [InlineData("malformed-hash")]
    public async Task Exact_authority_mismatch_is_refused_before_public_key_access(string field)
    {
        using var f = new SetFixture(); await f.Put(); using var set = await f.Capture(); var pin = CredentialRecoverySets.Authority(set);
        pin = field switch
        {
            "customer" => pin with { CustomerId = "10000000-0000-0000-0000-000000000099" },
            "cell" => pin with { CellId = "20000000-0000-0000-0000-000000000099" },
            "set-id" => pin with { SetId = new string('9', 32) }, "generation" => pin with { Generation = pin.Generation + 1 },
            "artifact-hash" => pin with { ArtifactSha256 = Hash("different-artifact"u8.ToArray()) },
            "signer-id" => pin with { SignerKeyId = "different-signer" },
            "signer-hash" => pin with { PublicKeySha256 = new string('9', 64) }, _ => pin with { ArtifactSha256 = "invalid" }
        };
        using var publicDependency = new UnavailablePublicKey();
        Assert.Throws<CredentialException>(() => CredentialRecoverySets.Verify(set, pin, publicDependency, f.Identity, f.Cell.Clock));
        Assert.False(publicDependency.Accessed);
    }

    [Theory]
    [InlineData("customer")] [InlineData("cell")] [InlineData("vault")] [InlineData("commitment")]
    [InlineData("authority-id")] [InlineData("authority-binding")]
    public async Task Full_source_namespace_pin_is_required_before_public_key_access(string field)
    {
        using var f = new SetFixture(strong: true); await f.Put(); using var set = await f.Capture(); var expected = f.Identity;
        expected = field switch
        {
            "customer" => expected with { CustomerId = "10000000-0000-0000-0000-000000000099" },
            "cell" => expected with { CellId = "20000000-0000-0000-0000-000000000099" },
            "vault" => expected with { VaultNamespaceId = new string('9', 32) },
            "commitment" => expected with { NamespaceCommitmentSha256 = new string('9', 64) },
            "authority-id" => expected with { AuthorityNamespaceId = new string('9', 32) },
            _ => expected with { AuthorityBindingSha256 = new string('9', 64) }
        };
        using var publicDependency = new UnavailablePublicKey();
        Assert.Throws<CredentialException>(() => CredentialRecoverySets.Verify(set, CredentialRecoverySets.Authority(set), publicDependency, expected, f.Cell.Clock));
        Assert.False(publicDependency.Accessed);
    }

    [Theory]
    [InlineData("wrong-key")] [InlineData("changed-signature")] [InlineData("expired")] [InlineData("not-before")]
    [InlineData("future-issued")] [InlineData("rsa-pkcs1")]
    public async Task Valid_structure_does_not_substitute_for_pinned_RSA_PSS_signature_and_current_lifetime(string attack)
    {
        using var f = new SetFixture(); await f.Put(); using var set = await f.Capture(); var json = Artifact(set);
        var signature = set.Signature;
        if (attack == "changed-signature") json["signature"]!["signature"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(256));
        if (attack == "rsa-pkcs1") json["signature"]!["signature"] = Convert.ToBase64String(f.Signer.SignData(Canonical(signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        if (attack == "not-before") { signature = signature with { NotBeforeUtc = f.Cell.Clock.Now.AddMinutes(10) }; SetSignature(json, signature, f.Signer); }
        using var altered = CredentialRecoverySets.Parse(Bytes(json)); using var key = attack == "wrong-key" ? RSA.Create(2048) : Public(f.Signer);
        if (attack == "expired") f.Cell.Clock.Now = set.Signature.ExpiresUtc;
        if (attack == "future-issued") f.Cell.Clock.Now = set.Signature.IssuedUtc.AddMilliseconds(-1);
        var before = Snapshot(f.Cell.Root); f.Cell.Keys.Requests.Clear();
        Assert.Throws<CredentialException>(() => CredentialRecoverySets.Verify(altered, CredentialRecoverySets.Authority(altered), key, f.Identity, f.Cell.Clock));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [WindowsCredentialFact]
    public async Task Signature_pad_bit_alias_with_identical_decoded_bytes_is_rejected()
    {
        using var f = new SetFixture(); await f.Put(); using var set = await f.Capture(); var json = Artifact(set);
        const string alphabet = "ABCDEFGHIJKLMNOPQRSTUVWXYZabcdefghijklmnopqrstuvwxyz0123456789+/";
        var canonical = set.Signature.Signature; var position = canonical.Length - (canonical.EndsWith("==", StringComparison.Ordinal) ? 3 : 2);
        var chars = canonical.ToCharArray(); chars[position] = alphabet[alphabet.IndexOf(chars[position]) + 1]; var alias = new string(chars);
        Assert.Equal(Convert.FromBase64String(canonical), Convert.FromBase64String(alias)); json["signature"]!["signature"] = alias;
        Assert.Throws<CredentialException>(() =>
        {
            using var parsed = CredentialRecoverySets.Parse(Bytes(json));
            CredentialRecoverySets.Verify(parsed, CredentialRecoverySets.Authority(parsed), f.Signer, f.Identity, f.Cell.Clock);
        });
    }

    [WindowsCredentialFact]
    public async Task Public_only_capture_signer_is_refused_before_any_source_key_lookup_or_source_change()
    {
        using var f = new SetFixture(); await f.Put(); using var publicOnly = Public(f.Signer); var before = Snapshot(f.Cell.Root); f.Cell.Keys.Requests.Clear();
        await Assert.ThrowsAsync<CredentialException>(() => f.Vault.CaptureSignedRecoverySetAsync(f.Identity, publicOnly, "synthetic-set-signer", 1,
            f.Cell.Clock.Now, f.Cell.Clock.Now.AddHours(1)));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [WindowsCredentialFact]
    public async Task Valid_probe_but_invalid_actual_signature_never_returns_a_signed_set_or_exports_private_key_material()
    {
        using var f = new SetFixture(); await f.Put(); using var signer = new InvalidFinalSignature(f.Signer); var before = Snapshot(f.Cell.Root); f.Cell.Keys.Requests.Clear();
        await Assert.ThrowsAsync<CredentialException>(() => f.Vault.CaptureSignedRecoverySetAsync(f.Identity, signer, "synthetic-set-signer", 1,
            f.Cell.Clock.Now, f.Cell.Clock.Now.AddHours(1)));
        Assert.True(signer.SignCalls >= 2); Assert.False(signer.PrivateExportRequested); Assert.NotEmpty(f.Cell.Keys.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData("unknown")] [InlineData("duplicate")] [InlineData("case-alias")] [InlineData("missing-content")]
    [InlineData("wrong-schema")] [InlineData("base64-whitespace")] [InlineData("truncated")]
    public async Task Outer_artifact_parsing_is_strict_bounded_and_never_creates_source_or_output_files(string attack)
    {
        using var f = new SetFixture(); await f.Put(); using var set = await f.Capture(); var json = Artifact(set);
        switch (attack)
        {
            case "unknown": json["unreviewed"] = true; break; case "case-alias": json["Schema"] = json["schema"]!.DeepClone(); break;
            case "missing-content": json.Remove("content"); break; case "wrong-schema": json["schema"] = "unreviewed.v1"; break;
            case "base64-whitespace": json["content"] = " " + json["content"]!.GetValue<string>(); break;
        }
        var raw = attack switch { "duplicate" => Encoding.UTF8.GetBytes("{\"schema\":\"duplicate\"," + json.ToJsonString()[1..]),
            "truncated" => "{\"schema\":"u8.ToArray(), _ => Bytes(json) };
        var before = Snapshot(f.Cell.Root); Assert.Throws<CredentialException>(() => { using var unexpected = CredentialRecoverySets.Parse(raw); });
        Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData("duplicate-file")] [InlineData("unsorted-files")] [InlineData("traversal-name")] [InlineData("key-file")]
    [InlineData("missing-profile")] [InlineData("length")] [InlineData("hash")] [InlineData("base64-whitespace")]
    [InlineData("nonempty-guard")] [InlineData("file-count")] [InlineData("keys-hidden")]
    [InlineData("retirement-claim")] [InlineData("backup-claim")] [InlineData("generation")]
    public async Task Even_resigned_content_cannot_adopt_inconsistent_private_files_or_false_coverage_claims(string attack)
    {
        using var f = new SetFixture(); await f.Put(); using var set = await f.Capture(); var before = Snapshot(f.Cell.Root);
        var bytes = MutateContent(set, f.Signer, content =>
        {
            var files = content["files"]!.AsArray(); var profile = files.Single(n => n!["name"]!.GetValue<string>().EndsWith(".credential.json", StringComparison.Ordinal))!;
            switch (attack)
            {
                case "duplicate-file": files.Add(files[^1]!.DeepClone()); break;
                case "unsorted-files": var first = files[0]!.DeepClone(); files[0] = files[^1]!.DeepClone(); files[^1] = first; break;
                case "traversal-name": profile["name"] = "vault/../unreviewed"; break;
                case "key-file": profile["name"] = "keys/synthetic.dpapi.json"; break;
                case "missing-profile": files.Remove(profile); break;
                case "length": profile["length"] = profile["length"]!.GetValue<long>() + 1; break;
                case "hash": profile["sha256"] = new string('9', 64); break;
                case "base64-whitespace": profile["bytes"] = " " + profile["bytes"]!.GetValue<string>(); break;
                case "nonempty-guard": var guard = files.Single(n => n!["name"]!.GetValue<string>() == "vault/.vault.lock")!; UpdateFile(guard, "not-empty"u8.ToArray()); break;
                case "file-count": while (files.Count <= CredentialRecoverySets.MaximumFiles) files.Add(profile.DeepClone()); break;
                case "keys-hidden": content["inventory"]!["currentRequiredKeyIds"] = new JsonArray(); break;
                case "retirement-claim": content["inventory"]!["keyRetirementAuthorized"] = true; break;
                case "backup-claim": content["inventory"]!["independentBackupCoverage"] = "Verified"; break;
                case "generation": content["inventory"]!["profileDependencies"]![0]!["generation"] = 0; break;
            }
        });
        Assert.Throws<CredentialException>(() => { using var unexpected = CredentialRecoverySets.Parse(bytes); }); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Retained_namespace_marker_tampering_is_refused_even_when_file_body_and_signer_hashes_are_recomputed(bool authority)
    {
        using var f = new SetFixture(strong: true); await f.Put(); using var set = await f.Capture();
        var changed = MutateContent(set, f.Signer, body =>
        {
            var name = authority ? "authority/.credential-namespace.json" : "vault/.credential-namespace.json";
            var file = body["files"]!.AsArray().Single(n => n!["name"]!.GetValue<string>() == name)!;
            var marker = JsonNode.Parse(Convert.FromBase64String(file["bytes"]!.GetValue<string>()))!;
            marker["customerId"] = "10000000-0000-0000-0000-000000000099"; UpdateFile(file, Bytes(marker));
        });
        Assert.Throws<CredentialException>(() => { using var unexpected = CredentialRecoverySets.Parse(changed); });
    }

    [WindowsCredentialFact]
    public void Whole_artifact_capacity_is_checked_before_parsing_or_allocation_from_declared_fields()
    {
        Assert.Throws<CredentialException>(() => CredentialRecoverySets.Parse(new byte[CredentialRecoverySets.MaximumArtifactBytes + 1]));
        Assert.Throws<CredentialException>(() => CredentialRecoverySets.Parse([]));
    }

    [Theory]
    [InlineData("files", CredentialRecoverySets.MaximumFiles)]
    [InlineData("profileDependencies", CredentialRecoverySets.MaximumProfiles)]
    [InlineData("currentRequiredKeyIds", CredentialRecoverySets.MaximumProfiles)]
    public async Task Array_count_caps_win_before_hydrating_invalid_typed_elements(string field, int maximum)
    {
        using var f = new SetFixture(); await f.Put(); using var set = await f.Capture();
        var bytes = MutateContent(set, f.Signer, content =>
        {
            var oversized = new JsonArray(); for (var i = 0; i <= maximum; i++) oversized.Add(17);
            if (field == "files") content[field] = oversized; else content["inventory"]![field] = oversized;
        });
        // Scalar elements cannot hydrate as file/profile/string records. Capacity must be refused first.
        Assert.Equal("credential-recovery-set-capacity-exceeded", Assert.Throws<CredentialException>(() =>
        { using var unexpected = CredentialRecoverySets.Parse(bytes); }).Code);
    }

    [WindowsCredentialFact]
    public async Task Retained_state_profile_cap_wins_before_hydrating_invalid_dictionary_values()
    {
        using var f = new SetFixture(strong: true); await f.Put(); using var set = await f.Capture();
        var bytes = MutateContent(set, f.Signer, content =>
        {
            var file = content["files"]!.AsArray().Single(n => n!["name"]!.GetValue<string>() == "authority/state.json")!;
            var state = JsonNode.Parse(Convert.FromBase64String(file["bytes"]!.GetValue<string>()))!;
            var profiles = new JsonObject();
            for (var i = 0; i <= CredentialRecoverySets.MaximumProfiles; i++) profiles[Hash(Encoding.UTF8.GetBytes("synthetic-profile-" + i))] = 17;
            state["profiles"] = profiles; UpdateFile(file, Bytes(state));
        });
        Assert.Equal("credential-recovery-set-capacity-exceeded", Assert.Throws<CredentialException>(() =>
        { using var unexpected = CredentialRecoverySets.Parse(bytes); }).Code);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Unknown_or_partial_capture_entries_are_preserved_and_never_return_a_signed_artifact(bool strong)
    {
        using var f = new SetFixture(strong); await f.Put(); var root = strong ? f.Options.RollbackProtection!.AuthorityDirectory : f.Options.RootPath;
        File.WriteAllText(Path.Combine(root, strong ? ".state-write-" + Guid.NewGuid().ToString("N") : "unreviewed.credential.json"), "synthetic partial entry");
        var before = Snapshot(f.Cell.Root); f.Cell.Keys.Requests.Clear();
        await Assert.ThrowsAsync<CredentialException>(() => f.Capture()); Assert.Equal(before, Snapshot(f.Cell.Root)); Assert.Empty(f.Cell.Keys.Requests);
    }

    [WindowsCredentialFact]
    public async Task Canceled_capture_creates_no_artifact_and_changes_no_source_custody()
    {
        using var f = new SetFixture(); await f.Put(); var before = Snapshot(f.Cell.Root); f.Cell.Keys.Requests.Clear();
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Capture(cancellationToken: cancellation.Token));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [WindowsCredentialFact]
    public async Task Capture_holds_the_source_guard_until_exact_bytes_and_signature_are_fixed()
    {
        using var f = new SetFixture(); await f.Put(); var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        f.Cell.Keys.OnGet = () => { f.Cell.Keys.OnGet = null; entered.TrySetResult(); release.Task.GetAwaiter().GetResult(); };
        var capture = Task.Run(() => f.Capture()); Task<CredentialProfile>? mutation = null;
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); mutation = f.Vault.RevokeAsync(f.Cell.Scope.AuthProfileId, 1);
            await Task.Delay(30); Assert.False(mutation.IsCompleted); release.TrySetResult();
            using var set = await capture.WaitAsync(TimeSpan.FromSeconds(10)); await mutation.WaitAsync(TimeSpan.FromSeconds(10));
            var verified = CredentialRecoverySets.Verify(set, CredentialRecoverySets.Authority(set), f.Signer, f.Identity, f.Cell.Clock);
            Assert.Equal(1, Assert.Single(verified.Inventory.ProfileDependencies).Generation);
            Assert.Equal(2, (await f.Vault.GetProfileAsync(f.Cell.Scope.AuthProfileId)).Generation);
        }
        finally
        {
            release.TrySetResult(); try { (await capture.WaitAsync(TimeSpan.FromSeconds(10))).Dispose(); } catch { }
            if (mutation is not null) try { await mutation.WaitAsync(TimeSpan.FromSeconds(10)); } catch { }
        }
    }

    static byte[] Export(CredentialRecoverySet set) { using var stream = new MemoryStream(); set.WritePrivateArtifact(stream); return stream.ToArray(); }
    static JsonObject Artifact(CredentialRecoverySet set) => JsonNode.Parse(Export(set))!.AsObject();
    static JsonObject Content(CredentialRecoverySet set) => JsonNode.Parse(Convert.FromBase64String(Artifact(set)["content"]!.GetValue<string>()))!.AsObject();
    static byte[] Bytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString(Json));
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static byte[] Canonical(CredentialRecoverySetSignature signature) => (byte[])typeof(CredentialRecoverySets)
        .GetMethod("CanonicalPayload", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [signature])!;
    static void SetSignature(JsonObject artifact, CredentialRecoverySetSignature signature, RSA signer)
    {
        signature = signature with { Signature = "" };
        signature = signature with { Signature = Convert.ToBase64String(signer.SignData(Canonical(signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) };
        artifact["signature"] = JsonSerializer.SerializeToNode(signature, Json);
    }
    static byte[] MutateContent(CredentialRecoverySet set, RSA signer, Action<JsonObject> mutation)
    {
        var artifact = Artifact(set); var content = JsonNode.Parse(Convert.FromBase64String(artifact["content"]!.GetValue<string>()))!.AsObject(); mutation(content);
        var bytes = Bytes(content); artifact["content"] = Convert.ToBase64String(bytes);
        SetSignature(artifact, set.Signature with { ContentSha256 = Hash(bytes), ContentLength = bytes.Length }, signer); return Bytes(artifact);
    }
    static void UpdateFile(JsonNode file, byte[] bytes) { file["bytes"] = Convert.ToBase64String(bytes); file["length"] = bytes.Length; file["sha256"] = Hash(bytes); }
    static RSA Public(RSA source) { var key = RSA.Create(); key.ImportSubjectPublicKeyInfo(source.ExportSubjectPublicKeyInfo(), out _); return key; }
    static string[] Snapshot(string root) => new[] { root }.Concat(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData((Directory.Exists(path)
            ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorBinaryForm())) + ":" +
            (Directory.Exists(path) ? "directory" : Hash(File.ReadAllBytes(path)))).ToArray();
    static void AssertConservative(CredentialRecoverySetVerification verified)
    {
        Assert.Equal("NotAssessed", verified.IndependentBackupCoverage); Assert.False(verified.KeyBackupVerified); Assert.False(verified.KeyRetirementAuthorized);
        Assert.False(verified.RestoreAuthorized); Assert.False(verified.ActivationPerformed); Assert.False(verified.ExternalExecutionAuthorized); Assert.False(verified.ProductionRecoveryQualified);
    }

    sealed class SetFixture : IDisposable
    {
        public CredentialFixture Cell { get; } = new(); public RSA Signer { get; } = RSA.Create(2048);
        public CredentialVaultOptions Options { get; } public CredentialVault Vault { get; } public CredentialVaultNamespaceIdentity Identity { get; }
        public SetFixture(bool strong = false)
        {
            Options = strong ? Cell.Options with { RootPath = Path.Combine(Cell.Root, "set-vault"), RollbackProtection = new(Path.Combine(Cell.Root, "set-authority")) } : Cell.Options;
            Vault = strong ? CredentialVault.ProvisionStrong(Options, Cell.Keys, Cell.Clock) : Cell.Vault; Identity = Vault.DescribeNamespace();
        }
        public async Task Put() { using var value = CredentialSecret.FromUtf8(CredentialFixture.Token); await Vault.PutAsync(Cell.Scope, value, Cell.Clock.Now.AddHours(1)); }
        public Task<CredentialRecoverySet> Capture(long generation = 1, CancellationToken cancellationToken = default) =>
            Vault.CaptureSignedRecoverySetAsync(Identity, Signer, "synthetic-set-signer", generation, Cell.Clock.Now, Cell.Clock.Now.AddHours(1), cancellationToken);
        public void Dispose() { Signer.Dispose(); Cell.Dispose(); }
    }
    sealed class UnavailablePublicKey : RSA
    {
        public bool Accessed { get; private set; }
        Exception Unavailable() { Accessed = true; return new NotSupportedException("Synthetic public-key dependency unavailable."); }
        public override int KeySize { get => throw Unavailable(); set => throw Unavailable(); }
        public override RSAParameters ExportParameters(bool includePrivateParameters) => throw Unavailable();
        public override void ImportParameters(RSAParameters parameters) => throw Unavailable();
        public override byte[] Encrypt(byte[] data, RSAEncryptionPadding padding) => throw Unavailable();
        public override byte[] Decrypt(byte[] data, RSAEncryptionPadding padding) => throw Unavailable();
        public override byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) => throw Unavailable();
        public override bool VerifyHash(byte[] hash, byte[] signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) => throw Unavailable();
    }
    sealed class InvalidFinalSignature(RSA source) : RSA
    {
        public int SignCalls { get; private set; }
        public bool PrivateExportRequested { get; private set; }
        public override int KeySize { get => source.KeySize; set => throw new NotSupportedException(); }
        public override RSAParameters ExportParameters(bool includePrivateParameters)
        { PrivateExportRequested |= includePrivateParameters; if (includePrivateParameters) throw new NotSupportedException(); return source.ExportParameters(false); }
        public override byte[] SignHash(byte[] hash, HashAlgorithmName algorithm, RSASignaturePadding padding) =>
            ++SignCalls == 1 ? source.SignHash(hash, algorithm, padding) : new byte[source.KeySize / 8];
        public override bool VerifyHash(byte[] hash, byte[] signature, HashAlgorithmName algorithm, RSASignaturePadding padding) => source.VerifyHash(hash, signature, algorithm, padding);
        public override byte[] Encrypt(byte[] data, RSAEncryptionPadding padding) => throw new NotSupportedException();
        public override byte[] Decrypt(byte[] data, RSAEncryptionPadding padding) => throw new NotSupportedException();
        public override void ImportParameters(RSAParameters parameters) => throw new NotSupportedException();
    }
}
