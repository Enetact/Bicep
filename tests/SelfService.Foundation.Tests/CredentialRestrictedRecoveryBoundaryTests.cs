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
public sealed class CredentialRestrictedRecoveryBoundaryTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("signature", "general")]
    [InlineData("signature", "missing")]
    [InlineData("signature", "null")]
    [InlineData("content", "general")]
    [InlineData("content", "missing")]
    [InlineData("content", "null")]
    [InlineData("inventory", "general")]
    [InlineData("inventory", "missing")]
    [InlineData("inventory", "null")]
    public async Task Coherently_signed_profile_stripping_never_downgrades_the_restricted_protocol(string location, string mutation)
    {
        using var f = new RestrictedSetFixture(); await f.Initialize(); using var set = await f.Capture();
        var artifact = Artifact(set); var content = Content(artifact);
        var target = location == "signature" ? artifact["signature"]!.AsObject() : location == "content" ? content : content["inventory"]!.AsObject();
        if (mutation == "missing") target.Remove("accessProfile");
        else target["accessProfile"] = mutation == "null" ? null : JsonValue.Create("General");
        var inventory = content["inventory"]!.AsObject();
        inventory["sha256"] = Hash(Bytes(new JsonObject
        {
            ["schema"] = inventory["schema"]!.DeepClone(), ["accessProfile"] = inventory["accessProfile"]?.DeepClone(),
            ["sourceSha256"] = SourceInventory(content)["sha256"]!.DeepClone()
        }));
        var body = Bytes(content); artifact["content"] = Convert.ToBase64String(body);
        artifact["signature"]!["inventorySha256"] = Text(inventory, "sha256");
        artifact["signature"]!["contentSha256"] = Hash(body); artifact["signature"]!["contentLength"] = body.Length;
        var signature = artifact["signature"]!.Deserialize<CredentialRestrictedRecoverySetSignature>(Json)!;
        var payload = SignaturePayload(signature); var signed = f.Signer.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        artifact["signature"]!["signature"] = Convert.ToBase64String(signed);
        using var publicKey = f.Public(); Assert.True(publicKey.VerifyData(payload, signed, HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        var before = f.Snapshot(); f.Cell.Keys.Requests.Clear();
        Assert.Throws<CredentialException>(() => { using var ignored = CredentialRestrictedRecoverySets.Parse(Bytes(artifact)); });
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("vault", "credential-vault-scoped-fenced")]
    [InlineData("vault", "credential-vault-fenced")]
    [InlineData("authority", "credential-rollback-scoped-authority")]
    [InlineData("authority", "credential-rollback-authority")]
    public async Task Rehashed_and_signed_marker_kind_downgrade_is_refused_even_with_valid_retained_AES(string location, string kind)
    {
        using var f = new RestrictedSetFixture();
        using (var secret = CredentialSecret.FromUtf8(CredentialFixture.Token)) await f.Source.PutAsync(f.Positive, secret, f.Cell.Clock.Now.AddHours(1));
        using var set = await f.Capture();
        var encoded = RewriteBytes(set, f, content =>
        {
            var file = FileNode(content, location + "/.credential-namespace.json");
            var marker = JsonNode.Parse(FileBytes(file))!.AsObject(); marker["kind"] = kind;
            marker["sha256"] = Hash(Bytes(new JsonObject
            {
                ["schema"] = marker["schema"]!.DeepClone(), ["kind"] = marker["kind"]!.DeepClone(),
                ["customerId"] = marker["customerId"]!.DeepClone(), ["cellId"] = marker["cellId"]!.DeepClone(),
                ["rootPath"] = marker["rootPath"]!.DeepClone(), ["ownerSid"] = marker["ownerSid"]!.DeepClone(),
                ["namespaceId"] = marker["namespaceId"]!.DeepClone()
            }));
            UpdateFile(file, Bytes(marker));
            if (location == "vault") SourceInventory(content)["namespaceCommitmentSha256"] = Text(marker, "sha256");
        });
        var artifact = JsonNode.Parse(encoded)!.AsObject(); var signature = artifact["signature"]!.Deserialize<CredentialRestrictedRecoverySetSignature>(Json)!;
        using var signer = f.Public(); Assert.True(signer.VerifyData(SignaturePayload(signature), Convert.FromBase64String(signature.Signature), HashAlgorithmName.SHA256, RSASignaturePadding.Pss));
        var before = f.Snapshot(); f.Cell.Keys.Requests.Clear();
        Assert.Throws<CredentialException>(() => { using var ignored = CredentialRestrictedRecoverySets.Parse(encoded); });
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData(PipelineRequestFactory.GitHub)]
    [InlineData(PipelineRequestFactory.AzureDevOps)]
    public async Task Retained_AES_and_absence_MAC_authenticate_after_both_live_roots_are_removed(string provider)
    {
        using var f = new RestrictedSetFixture(provider); await f.Initialize(); using var set = await f.Capture(); var authority = CredentialRestrictedRecoverySets.Authority(set);
        f.RemoveLiveRoots(); var before = f.Snapshot(); f.Cell.Keys.Requests.Clear(); using var signer = f.Public();
        var verification = CredentialRestrictedRecoverySets.Verify(set, authority, signer, f.Identity, f.Cell.Clock);
        Assert.False(verification.EncryptedProfileAuthenticationPerformed); Assert.False(verification.AbsenceProofAuthenticationPerformed);
        Assert.Equal(["key-1", "key-2"], verification.Inventory.CurrentRequiredKeyIds); Assert.Single(verification.Inventory.ProfileDependencies); Assert.Single(verification.Inventory.AbsenceScopes);
        var result = await f.Rehearsal().RehearseAsync(set, authority, signer);
        AssertAuthenticatedResult(result, f, profiles: 1, absences: 1);
        Assert.Equal(new[] { "key-1", "key-2" }, f.Cell.Keys.Requests.Select(k => k.KeyId).Order(StringComparer.Ordinal));
        Assert.False(Directory.Exists(f.Options.RootPath)); Assert.False(Directory.Exists(f.AuthorityDirectory)); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("wrong-bytes")]
    public async Task A_valid_signature_cannot_substitute_for_the_missing_or_changed_historical_proof_key(string mutation)
    {
        using var f = new RestrictedSetFixture(); await f.Initialize(); using var set = await f.Capture(); f.RemoveLiveRoots();
        if (mutation == "missing") f.Cell.Keys.Values.Remove("key-1"); else f.Cell.Keys.Values["key-1"] = RandomNumberGenerator.GetBytes(32);
        var before = f.Snapshot(); using var key = f.Public(); var pin = CredentialRestrictedRecoverySets.Authority(set);
        Assert.True(CredentialRestrictedRecoverySets.Verify(set, pin, key, f.Identity, f.Cell.Clock).SignatureVerified);
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Rehearsal().RehearseAsync(set, pin, key)); AssertOpaque(error, f); Assert.Equal(before, f.Snapshot());
        Assert.DoesNotContain(f.Cell.Keys.Requests, request => request.KeyId == "unavailable-configured-write-key");
    }

    public static IEnumerable<object[]> BadMacFields()
    {
        foreach (var field in new[] { "project", "environment", "connection", "connection-generation", "connection-digest", "permissions", "key-alias", "first-transaction", "first-candidate", "first-candidate-length", "tag" }) yield return [field];
    }

    [Theory]
    [MemberData(nameof(BadMacFields))]
    public async Task Coherently_rehashed_and_RSA_signed_proof_tampering_passes_metadata_but_never_source_MAC_authentication(string field)
    {
        using var f = new RestrictedSetFixture(); await f.Initialize(); using var original = await f.Capture();
        if (field == "key-alias") f.Cell.Keys.Values["historical-key-alias"] = f.Cell.Keys.Values["key-1"].ToArray();
        using var changed = Rewrite(original, f, content =>
        {
            var state = RetainedState(content); var entry = StateEntry(state, f.AbsenceHash); var proof = entry["absenceProof"]!.AsObject();
            switch (field)
            {
                case "project": proof["scope"]!["projectId"] = "another-project"; break;
                case "environment": proof["scope"]!["environmentId"] = "test"; break;
                case "connection": proof["scope"]!["connectionId"] = "another-ci"; break;
                case "connection-generation": proof["scope"]!["connectionGeneration"] = 2L; break;
                case "connection-digest": proof["scope"]!["connectionDigest"] = new string('b', 64); break;
                case "permissions": proof["scope"]!["requiredScopes"] = new JsonArray("actions:write", "contents:read"); break;
                case "key-alias": proof["keyId"] = "historical-key-alias"; break;
                case "first-transaction": proof["firstTransactionId"] = Guid.NewGuid().ToString("N"); break;
                case "first-candidate": proof["firstCandidateSha256"] = new string('b', 64); break;
                case "first-candidate-length": proof["firstCandidateLength"] = proof["firstCandidateLength"]!.GetValue<long>() + 1; break;
                default: proof["tag"] = new string('b', 64); break;
            }
            proof["scopeSha256"] = Hash(Bytes(proof["scope"]!)); RehashProof(proof); UpdateAbsenceProjection(content, proof); StoreState(content, state);
        });
        f.RemoveLiveRoots(); var before = f.Snapshot(); f.Cell.Keys.Requests.Clear(); using var key = f.Public(); var authority = CredentialRestrictedRecoverySets.Authority(changed);
        var verified = CredentialRestrictedRecoverySets.Verify(changed, authority, key, f.Identity, f.Cell.Clock);
        Assert.True(verified.SignatureVerified); Assert.False(verified.EncryptedProfileAuthenticationPerformed); Assert.False(verified.AbsenceProofAuthenticationPerformed);
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Rehearsal().RehearseAsync(changed, authority, key));
        Assert.Equal("credential-absence-proof-authentication-failed", error.Code); AssertOpaque(error, f); Assert.Equal(before, f.Snapshot());
        Assert.Contains(f.Cell.Keys.Requests, request => request.KeyId == (field == "key-alias" ? "historical-key-alias" : "key-1"));
    }

    [WindowsCredentialFact]
    public async Task A_later_bad_AES_envelope_does_not_return_an_authenticated_absence_or_earlier_profile_as_partial_success()
    {
        using var f = new RestrictedSetFixture(); await f.Initialize(); using var original = await f.Capture();
        using var changed = Rewrite(original, f, content =>
        {
            var file = FileNode(content, "vault/" + f.PositiveHash + ".credential.json"); var envelope = JsonNode.Parse(FileBytes(file))!.AsObject(); envelope["tag"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
            var bytes = Bytes(envelope); UpdateFile(file, bytes); var profile = SourceInventory(content)["profileDependencies"]!.AsArray().Single()!;
            profile["envelopeSha256"] = Hash(bytes); profile["envelopeLength"] = bytes.LongLength;
            var state = RetainedState(content); var entry = StateEntry(state, f.PositiveHash); entry["envelopeSha256"] = Hash(bytes); entry["envelopeLength"] = bytes.LongLength; StoreState(content, state);
        });
        f.RemoveLiveRoots(); var before = f.Snapshot(); using var key = f.Public(); var pin = CredentialRestrictedRecoverySets.Authority(changed);
        Assert.True(CredentialRestrictedRecoverySets.Verify(changed, pin, key, f.Identity, f.Cell.Clock).SignatureVerified);
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Rehearsal().RehearseAsync(changed, pin, key)); Assert.Equal("credential-envelope-integrity", error.Code); AssertOpaque(error, f); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("cell")]
    [InlineData("vault")]
    [InlineData("commitment")]
    [InlineData("authority")]
    [InlineData("authority-binding")]
    [InlineData("set-id")]
    [InlineData("generation")]
    [InlineData("artifact")]
    [InlineData("signer-id")]
    [InlineData("public-key")]
    [InlineData("profile")]
    public async Task Every_exact_trusted_selection_pin_is_checked_before_any_source_key(string field)
    {
        using var f = new RestrictedSetFixture(); await f.Initialize(); using var set = await f.Capture(); var identity = f.Identity; var pin = CredentialRestrictedRecoverySets.Authority(set);
        switch (field)
        {
            case "customer": identity = identity with { CustomerId = "10000000-0000-0000-0000-000000000099" }; break;
            case "cell": identity = identity with { CellId = "20000000-0000-0000-0000-000000000099" }; break;
            case "vault": identity = identity with { VaultNamespaceId = Guid.NewGuid().ToString("N") }; break;
            case "commitment": identity = identity with { NamespaceCommitmentSha256 = new string('b', 64) }; break;
            case "authority": identity = identity with { AuthorityNamespaceId = Guid.NewGuid().ToString("N") }; break;
            case "authority-binding": identity = identity with { AuthorityBindingSha256 = new string('b', 64) }; break;
            case "set-id": pin = pin with { SetId = Guid.NewGuid().ToString("N") }; break;
            case "generation": pin = pin with { Generation = pin.Generation + 1 }; break;
            case "artifact": pin = pin with { ArtifactSha256 = new string('b', 64) }; break;
            case "signer-id": pin = pin with { SignerKeyId = "another-signer" }; break;
            case "profile": pin = pin with { AccessProfile = "General" }; break;
        }
        var before = f.Snapshot(); f.Cell.Keys.Requests.Clear(); using var key = field == "public-key" ? RSA.Create(2048) : f.Public();
        var error = await Assert.ThrowsAsync<CredentialException>(() => new CredentialRestrictedRecoveryRehearsal(identity, f.Cell.Keys, f.Cell.Clock).RehearseAsync(set, pin, key));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot()); AssertOpaque(error, f);
    }

    [Theory]
    [InlineData("proof-missing")]
    [InlineData("proof-null")]
    [InlineData("proof-positive-graft")]
    [InlineData("proof-unknown-field")]
    [InlineData("pending")]
    [InlineData("state-version")]
    [InlineData("binding-version")]
    [InlineData("vault-marker-kind")]
    [InlineData("authority-marker-kind")]
    public async Task Signed_but_incomplete_or_mixed_source_metadata_cannot_bypass_required_v2_proofs(string field)
    {
        using var f = new RestrictedSetFixture(); await f.Initialize(); using var set = await f.Capture();
        var bytes = RewriteBytes(set, f, content =>
        {
            var state = RetainedState(content); var entry = StateEntry(state, f.AbsenceHash);
            switch (field)
            {
                case "proof-missing": entry.Remove("absenceProof"); SourceInventory(content)["absenceScopes"] = new JsonArray(); break;
                case "proof-null": entry["absenceProof"] = null; SourceInventory(content)["absenceScopes"] = new JsonArray(); break;
                case "proof-positive-graft": StateEntry(state, f.PositiveHash)["absenceProof"] = entry["absenceProof"]!.DeepClone(); break;
                case "proof-unknown-field": entry["absenceProof"]!["unexpected"] = "unknown-metadata"; break;
                case "pending": entry["pending"] = new JsonObject { ["transactionId"] = Guid.NewGuid().ToString("N"), ["previousGeneration"] = 0, ["previousEnvelopeSha256"] = "", ["newGeneration"] = 1, ["newEnvelopeSha256"] = new string('b', 64), ["newEnvelopeLength"] = 100, ["pendingSha256"] = new string('b', 64) }; break;
                case "state-version": state["schema"] = 1; break;
                case "binding-version": var bindingFile = FileNode(content, "vault/rollback-binding.json"); var binding = JsonNode.Parse(FileBytes(bindingFile))!; binding["schema"] = 1; UpdateFile(bindingFile, Bytes(binding)); UpdateFile(FileNode(content, "authority/authority.json"), Bytes(binding)); break;
                default: var markerName = field == "vault-marker-kind" ? "vault/.credential-namespace.json" : "authority/.credential-namespace.json"; var markerFile = FileNode(content, markerName); var marker = JsonNode.Parse(FileBytes(markerFile))!; marker["kind"] = field == "vault-marker-kind" ? "credential-vault-fenced" : "credential-rollback-authority"; UpdateFile(markerFile, Bytes(marker)); break;
            }
            StoreState(content, state);
        });
        var before = f.Snapshot(); f.Cell.Keys.Requests.Clear(); Assert.Throws<CredentialException>(() => { using var ignored = CredentialRestrictedRecoverySets.Parse(bytes); });
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("overlap")]
    [InlineData("duplicate-positive")]
    [InlineData("duplicate-absence")]
    [InlineData("missing-key")]
    [InlineData("extra-key")]
    [InlineData("unordered-key")]
    [InlineData("wrong-scope-projection")]
    public async Task Key_union_and_disjoint_unique_profile_sets_are_not_caller_selected(string field)
    {
        using var f = new RestrictedSetFixture(); await f.Initialize(); using var set = await f.Capture();
        var bytes = RewriteBytes(set, f, content =>
        {
            var inventory = SourceInventory(content); var profiles = inventory["profileDependencies"]!.AsArray(); var absences = inventory["absenceScopes"]!.AsArray();
            switch (field)
            {
                case "overlap": absences[0]!["profileIdSha256"] = f.PositiveHash; break;
                case "duplicate-positive": profiles.Add(profiles[0]!.DeepClone()); break;
                case "duplicate-absence": absences.Add(absences[0]!.DeepClone()); break;
                case "missing-key": inventory["currentRequiredKeyIds"] = new JsonArray("key-2"); break;
                case "extra-key": inventory["currentRequiredKeyIds"] = new JsonArray("key-1", "key-2", "not-a-dependency"); break;
                case "unordered-key": inventory["currentRequiredKeyIds"] = new JsonArray("key-2", "key-1"); break;
                default: absences[0]!["projectId"] = "not-the-proof-project"; break;
            }
        }, rebuildInventory: false);
        f.Cell.Keys.Requests.Clear(); Assert.Throws<CredentialException>(() => { using var ignored = CredentialRestrictedRecoverySets.Parse(bytes); }); Assert.Empty(f.Cell.Keys.Requests);
    }

    [Theory]
    [InlineData("files")]
    [InlineData("profileDependencies")]
    [InlineData("absenceScopes")]
    [InlineData("currentRequiredKeyIds")]
    [InlineData("combined")]
    [InlineData("state")]
    public async Task Capacity_is_checked_before_hydrating_invalid_collection_elements(string field)
    {
        using var f = new RestrictedSetFixture(); await f.Initialize(); using var set = await f.Capture();
        var bytes = RewriteBytes(set, f, content =>
        {
            var array = new JsonArray(); var count = field == "files" ? 265 : 257; for (var i = 0; i < count; i++) array.Add(17);
            if (field == "files") content["files"] = array;
            else if (field == "state") { var state = RetainedState(content); var profiles = new JsonObject(); for (var i = 0; i < 257; i++) profiles[i.ToString("x64")] = 17; state["profiles"] = profiles; UpdateFile(FileNode(content, "authority/state.json"), Bytes(state)); }
            else if (field == "combined") { var positives = new JsonArray(); var absences = new JsonArray(); for (var i = 0; i < 129; i++) positives.Add(17); for (var i = 0; i < 128; i++) absences.Add(17); SourceInventory(content)["profileDependencies"] = positives; SourceInventory(content)["absenceScopes"] = absences; }
            else SourceInventory(content)[field] = array;
        }, rebuildInventory: false);
        f.Cell.Keys.Requests.Clear(); var error = Assert.Throws<CredentialException>(() => { using var ignored = CredentialRestrictedRecoverySets.Parse(bytes); });
        Assert.Equal(field == "state" ? "credential-scoped-recovery-set-capacity-exceeded" : "credential-restricted-recovery-set-capacity-exceeded", error.Code);
        Assert.Empty(f.Cell.Keys.Requests);
    }

    [Theory]
    [InlineData("artifact")]
    [InlineData("content")]
    [InlineData("content-kind")]
    [InlineData("inventory")]
    [InlineData("signature")]
    [InlineData("signature-kind")]
    [InlineData("unknown-artifact")]
    [InlineData("duplicate-artifact")]
    [InlineData("content-base64")]
    [InlineData("signature-base64")]
    public async Task Protocol_version_and_canonical_encoding_cannot_fall_back_to_an_older_format(string field)
    {
        using var f = new RestrictedSetFixture(); await f.Initialize(); using var set = await f.Capture(); var artifact = Artifact(set);
        var content = Content(artifact);
        switch (field)
        {
            case "artifact": artifact["schema"] = "platform-studio.credential-recovery-set-artifact.v1"; break;
            case "content": content["version"] = 1; break;
            case "content-kind": content["kind"] = "credential-recovery-set-content"; break;
            case "inventory": SourceInventory(content)["schema"] = "platform-studio.credential-recovery-inventory.v1"; break;
            case "signature": artifact["signature"]!["version"] = 1; break;
            case "signature-kind": artifact["signature"]!["kind"] = "credential-recovery-set"; break;
            case "unknown-artifact": artifact["unexpected"] = 17; break;
            case "content-base64": artifact["content"] = artifact["content"]!.GetValue<string>() + "\n"; break;
            case "signature-base64": artifact["signature"]!["signature"] = artifact["signature"]!["signature"]!.GetValue<string>() + "\n"; break;
        }
        if (field is "content" or "content-kind" or "inventory") { var bytes = Bytes(content); artifact["content"] = Convert.ToBase64String(bytes); SetSignature(artifact, set.Signature with { ContentSha256 = Hash(bytes), ContentLength = bytes.Length }, f.Signer); }
        var encoded = Bytes(artifact); if (field == "duplicate-artifact") encoded = Encoding.UTF8.GetBytes(Encoding.UTF8.GetString(encoded).Replace("\"schema\":", "\"Schema\":\"duplicate\",\"schema\":", StringComparison.Ordinal));
        f.Cell.Keys.Requests.Clear(); Assert.Throws<CredentialException>(() => { using var ignored = CredentialRestrictedRecoverySets.Parse(encoded); }); Assert.Empty(f.Cell.Keys.Requests);
    }

    [WindowsCredentialFact]
    public async Task Old_artifact_bytes_roundtrip_unchanged_and_both_old_parsers_refuse_restricted_sets()
    {
        using var f = new RestrictedSetFixture(); await f.Cell.StoreToken(); using var legacy = await f.Cell.Vault.CaptureSignedRecoverySetAsync(f.Cell.Vault.DescribeNamespace(), f.Signer, "legacy-signer", 1, f.Cell.Clock.Now, f.Cell.Clock.Now.AddDays(1));
        using var legacyBytes = new MemoryStream(); legacy.WritePrivateArtifact(legacyBytes); var golden = legacyBytes.ToArray(); using var parsedLegacy = CredentialRecoverySets.Parse(golden); using var roundtrip = new MemoryStream(); parsedLegacy.WritePrivateArtifact(roundtrip);
        Assert.Equal(golden, roundtrip.ToArray()); var legacyArtifact = JsonNode.Parse(golden)!.AsObject();
        Assert.Equal(new[] { "version", "kind", "setId", "customerId", "cellId", "sourceNamespace", "generation", "capturedUtc", "issuedUtc", "notBeforeUtc", "expiresUtc", "inventorySha256", "profileCount", "contentSha256", "contentLength", "keyId", "publicKeySha256", "algorithm", "signature" }, legacyArtifact["signature"]!.AsObject().Select(p => p.Key));
        var legacyContent = JsonNode.Parse(Convert.FromBase64String(Text(legacyArtifact, "content")))!;
        Assert.False(legacyContent["inventory"]!.AsObject().ContainsKey("absenceScopes")); Assert.Equal(1, legacyContent["version"]!.GetValue<int>());
        Assert.Throws<CredentialException>(() => { using var ignored = CredentialRestrictedRecoverySets.Parse(golden); });
        await f.Initialize(); using var scoped = await f.Capture(); var bytes = Export(scoped); Assert.Throws<CredentialException>(() => { using var ignored = CredentialRecoverySets.Parse(bytes); });
        using var parsedScoped = CredentialRestrictedRecoverySets.Parse(bytes); Assert.Equal(bytes, Export(parsedScoped));
        Assert.Throws<CredentialException>(() => { using var ignored = CredentialScopedRecoverySets.Parse(bytes); });
        var generalOptions = f.Cell.Options with
        {
            RootPath = Path.Combine(f.Cell.Root, "general-scoped-vault"),
            RollbackProtection = new(Path.Combine(f.Cell.Root, "general-scoped-authority")) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 }
        };
        var general = CredentialVault.ProvisionStrongScoped(generalOptions, f.Cell.Keys, f.Cell.Clock);
        using (var secret = CredentialSecret.FromUtf8(CredentialFixture.Token)) await general.PutAsync(f.Positive, secret, f.Cell.Clock.Now.AddHours(1));
        using var oldScoped = await general.CaptureScopedSignedRecoverySetAsync(general.DescribeNamespace(), f.Signer, "old-scoped-signer", 1, f.Cell.Clock.Now, f.Cell.Clock.Now.AddDays(1));
        using var oldBytes = new MemoryStream(); oldScoped.WritePrivateArtifact(oldBytes); var oldGolden = oldBytes.ToArray();
        using var oldParsed = CredentialScopedRecoverySets.Parse(oldGolden); using var oldRoundtrip = new MemoryStream(); oldParsed.WritePrivateArtifact(oldRoundtrip); Assert.Equal(oldGolden, oldRoundtrip.ToArray());
        Assert.Throws<CredentialException>(() => { using var ignored = CredentialRestrictedRecoverySets.Parse(oldGolden); });
        Assert.DoesNotContain(typeof(CredentialRecoveryStaging).GetMethods(BindingFlags.Public | BindingFlags.Static).SelectMany(m => m.GetParameters()), p => p.ParameterType == typeof(CredentialRestrictedRecoverySetVerification));
        Assert.DoesNotContain(typeof(CredentialRecoverySetCheckpointAuthority).GetMethods(BindingFlags.Public | BindingFlags.Instance).SelectMany(m => m.GetParameters()), p => p.ParameterType == typeof(CredentialRestrictedRecoverySet));
        Assert.DoesNotContain(typeof(CredentialScopedRecoveryStaging).GetMethods(BindingFlags.Public | BindingFlags.Static).SelectMany(m => m.GetParameters()), p => p.ParameterType == typeof(CredentialRestrictedRecoverySetVerification));
        Assert.DoesNotContain(typeof(CredentialScopedRecoverySetCheckpointAuthority).GetMethods(BindingFlags.Public | BindingFlags.Instance).SelectMany(m => m.GetParameters()), p => p.ParameterType == typeof(CredentialRestrictedRecoverySet));
    }

    [WindowsCredentialFact]
    public async Task A_shared_source_key_is_frozen_once_for_both_positive_AES_and_absence_MAC_authentication()
    {
        using var f = new RestrictedSetFixture(); await f.Initialize(sameKey: true); using var set = await f.Capture(); f.RemoveLiveRoots(); var before = f.Snapshot();
        var rotating = new RotateAfterBorrow(f.Cell.Keys.Values["key-1"]); using var key = f.Public();
        var result = await new CredentialRestrictedRecoveryRehearsal(f.Identity, rotating, f.Cell.Clock).RehearseAsync(set, CredentialRestrictedRecoverySets.Authority(set), key);
        Assert.Equal(1, rotating.Requests); AssertAuthenticatedResult(result, f, 1, 1); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task The_exact_256_slot_union_authenticates_from_bounded_retained_history_without_repeated_source_writes()
    {
        using var f = new RestrictedSetFixture(); await f.Initialize(); using var original = await f.Capture();
        using var maximum = Rewrite(original, f, content =>
        {
            var state = RetainedState(content); var prototype = StateEntry(state, f.AbsenceHash); var projections = SourceInventory(content)["absenceScopes"]!.AsArray();
            for (var i = 0; i < 254; i++)
            {
                var entry = prototype.DeepClone().AsObject(); var proof = entry["absenceProof"]!.AsObject(); var scope = proof["scope"]!.Deserialize<CredentialScope>(Json)! with { AuthProfileId = "profile/retained-capacity-" + i };
                var profileHash = Hash(Encoding.UTF8.GetBytes(scope.AuthProfileId)); proof["profileIdSha256"] = profileHash; proof["scope"] = JsonSerializer.SerializeToNode(scope, Json); proof["scopeSha256"] = Hash(Bytes(proof["scope"]!)); proof["firstTransactionId"] = Guid.NewGuid().ToString("N");
                proof["tag"] = ProofMac(proof, f.Cell.Keys.Values["key-1"]); RehashProof(proof); state["profiles"]![profileHash] = entry;
                projections.Add(JsonSerializer.SerializeToNode(new CredentialAbsenceScopeDependency(profileHash, Text(proof, "scopeSha256"), scope.ProjectId, scope.EnvironmentId, "key-1", Text(proof, "sha256")), Json));
            }
            state["revision"] = 512L; SourceInventory(content)["authorityRevision"] = 512L; StoreState(content, state);
        });
        f.RemoveLiveRoots(); var before = f.Snapshot(); f.Cell.Keys.Requests.Clear(); using var key = f.Public(); var pin = CredentialRestrictedRecoverySets.Authority(maximum);
        var verification = CredentialRestrictedRecoverySets.Verify(maximum, pin, key, f.Identity, f.Cell.Clock);
        Assert.Equal(256, verification.Inventory.ProfileDependencies.Count + verification.Inventory.AbsenceScopes.Count); Assert.Equal(["key-1", "key-2"], verification.Inventory.CurrentRequiredKeyIds);
        var result = await f.Rehearsal().RehearseAsync(maximum, pin, key); AssertAuthenticatedResult(result, f, 1, 255); Assert.Equal(2, f.Cell.Keys.Requests.Count); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Original_artifact_and_public_key_disposal_during_lookup_cannot_retarget_the_detached_retained_operation()
    {
        using var f = new RestrictedSetFixture(); await f.Initialize(); using var set = await f.Capture(); var pin = CredentialRestrictedRecoverySets.Authority(set);
        var paused = new PausedKeys(f.Cell.Keys); using var key = f.Public(); var operation = f.Rehearsal(paused).RehearseAsync(set, pin, key);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); set.Dispose(); key.Dispose(); f.RemoveLiveRoots();
        Directory.CreateDirectory(f.Options.RootPath); Directory.CreateDirectory(f.AuthorityDirectory); File.WriteAllText(Path.Combine(f.Options.RootPath, "unrelated-original-location.txt"), "Synthetic original location must remain unconsulted.");
        var before = f.Snapshot();
        try { paused.Release.TrySetResult(); AssertAuthenticatedResult(await operation, f, 1, 1); Assert.Equal(before, f.Snapshot()); }
        finally { paused.Release.TrySetResult(); try { await operation; } catch { } }
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Caller_cancellation_and_original_artifact_disposal_never_detach_retained_source_key_awaits(bool cooperative)
    {
        using var f = new RestrictedSetFixture(); await f.Initialize(); using var set = await f.Capture(); var pin = CredentialRestrictedRecoverySets.Authority(set); f.RemoveLiveRoots(); var before = f.Snapshot();
        using var cancel = new CancellationTokenSource(); var paused = new PausedKeys(f.Cell.Keys, cooperative); using var key = f.Public(); var operation = f.Rehearsal(paused).RehearseAsync(set, pin, key, cancel.Token);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel(); set.Dispose();
        try { if (!cooperative) { await Task.Delay(80); Assert.False(operation.IsCompleted); } Assert.Equal(before, f.Snapshot()); }
        finally { paused.Release.TrySetResult(); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); Assert.Equal(cancel.Token, error.CancellationToken); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Set_lifetime_is_rechecked_after_retained_key_lookup_before_publishing_a_result(bool rewind)
    {
        using var f = new RestrictedSetFixture(); await f.Initialize(); using var set = await f.Capture(); var paused = new PausedKeys(f.Cell.Keys); using var key = f.Public();
        var operation = f.Rehearsal(paused).RehearseAsync(set, CredentialRestrictedRecoverySets.Authority(set), key); await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
        f.Cell.Clock.Now = rewind ? set.Signature.IssuedUtc.AddMilliseconds(-1) : set.Signature.ExpiresUtc; var before = f.Snapshot(); paused.Release.TrySetResult();
        var error = await Assert.ThrowsAsync<CredentialException>(() => operation); AssertOpaque(error, f); Assert.Equal(before, f.Snapshot());
    }

    static void AssertAuthenticatedResult(CredentialRestrictedRecoveryRehearsalResult result, RestrictedSetFixture f, int profiles, int absences)
    {
        Assert.Equal("platform-studio.credential-restricted-recovery-rehearsal.v1", result.Schema);
        Assert.Equal("FoundationBoundDispatchOnly", result.AccessProfile);
        Assert.Equal("FoundationBoundDispatchOnly", result.SignedSetVerification.AccessProfile);
        Assert.Equal("FoundationBoundDispatchOnly", result.SignedSetVerification.Inventory.AccessProfile);
        Assert.True(result.AllRetainedEnvelopesAuthenticated); Assert.True(result.AllRetainedAbsenceProofsAuthenticated);
        Assert.Equal(profiles, result.AuthenticatedProfileCount); Assert.Equal(absences, result.AuthenticatedAbsenceCount);
        Assert.Equal(profiles > 0, result.EncryptedProfileAuthenticationPerformed); Assert.Equal(absences > 0, result.AbsenceProofAuthenticationPerformed);
        Assert.Equal(profiles > 0, result.TemporaryInMemoryDecryptionPerformed); Assert.False(result.CustomerReviewAuthorized); Assert.False(result.FullRecoverySupported);
        var value = JsonSerializer.SerializeToNode(result, result.GetType(), Json)!.AsObject();
        Assert.Equal(profiles, value["profileDependencies"]!.AsArray().Count); Assert.Equal(absences, value["absenceScopes"]!.AsArray().Count);
        foreach (var field in new[] { "currentVaultConsulted", "plaintextCredentialMaterialExported", "encryptionKeyMaterialExported", "plaintextPersisted", "restorePerformed", "namespaceAdopted", "externalExecutionAuthorized", "keyRetirementAuthorized", "independentKeyBackupVerified", "productionRecoveryQualified" }) Assert.False(value[field]!.GetValue<bool>());
        Assert.Equal("NotAssessed", value["independentBackupCoverage"]!.GetValue<string>());
        var json = value.ToJsonString(Json); foreach (var excluded in new[] { CredentialFixture.Token, f.Cell.Root, f.Positive.AuthProfileId, f.Absence.AuthProfileId, f.Positive.Audience, "actions:write", "\"tag\":", "\"ciphertext\":", "ownerSid", "requiredScopes" }) Assert.DoesNotContain(excluded, json, StringComparison.OrdinalIgnoreCase);
        Assert.All(result.GetType().GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.Throws<NotSupportedException>(() => ((IList)result.GetType().GetProperty("ProfileDependencies")!.GetValue(result)!).Clear());
    }
    static void AssertOpaque(Exception error, RestrictedSetFixture f) { Assert.DoesNotContain(CredentialFixture.Token, error.ToString()); Assert.DoesNotContain(f.Cell.Root, error.ToString()); }
    static byte[] Export(CredentialRestrictedRecoverySet set) { using var stream = new MemoryStream(); set.WritePrivateArtifact(stream); return stream.ToArray(); }
    static JsonObject Artifact(CredentialRestrictedRecoverySet set) => JsonNode.Parse(Export(set))!.AsObject();
    static JsonObject SourceInventory(JsonObject content) => content["inventory"]!["source"]!.AsObject();
    static CredentialVaultNamespaceIdentity ContentIdentity(JsonObject content)
    {
        var source = SourceInventory(content);
        return new(Text(source, "customerId"), Text(source, "cellId"), Text(source, "vaultNamespaceId"),
            Text(source, "namespaceCommitmentSha256"), Text(source, "authorityNamespaceId"), Text(source, "authorityBindingSha256"));
    }
    static JsonObject Content(JsonObject artifact) => JsonNode.Parse(Convert.FromBase64String(artifact["content"]!.GetValue<string>()))!.AsObject();
    static byte[] Bytes(JsonNode node) => JsonSerializer.SerializeToUtf8Bytes(node, Json);
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static JsonObject FileNode(JsonObject content, string name) => content["files"]!.AsArray().Single(n => n!["name"]!.GetValue<string>() == name)!.AsObject();
    static byte[] FileBytes(JsonObject file) => Convert.FromBase64String(file["bytes"]!.GetValue<string>());
    static void UpdateFile(JsonObject file, byte[] bytes) { file["bytes"] = Convert.ToBase64String(bytes); file["length"] = bytes.Length; file["sha256"] = Hash(bytes); }
    static JsonObject RetainedState(JsonObject content) => JsonNode.Parse(FileBytes(FileNode(content, "authority/state.json")))!.AsObject();
    static JsonObject StateEntry(JsonObject state, string hash) => state["profiles"]![hash]!.AsObject();
    static string Text(JsonNode node, string name) => node[name]!.GetValue<string>();
    static void StoreState(JsonObject content, JsonObject state)
    {
        var binding = JsonNode.Parse(FileBytes(FileNode(content, "vault/rollback-binding.json")))!;
        state["sha256"] = Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "platform-studio.credential-rollback-scoped-state.v2", customerId = Text(binding, "customerId"), cellId = Text(binding, "cellId"), vaultNamespaceId = Text(binding, "vaultNamespaceId"), authorityNamespaceId = Text(binding, "authorityNamespaceId"),
            schema = state["schema"]!.GetValue<int>(), revision = state["revision"]!.GetValue<long>(), profiles = state["profiles"]!.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { id = p.Key, entry = p.Value }).ToArray()
        }, Json));
        UpdateFile(FileNode(content, "authority/state.json"), Bytes(state)); SourceInventory(content)["authorityStateSha256"] = Text(state, "sha256");
    }
    static JsonObject ProofPayload(JsonObject proof)
    {
        var payload = new JsonObject { ["domain"] = "platform-studio.credential-absence-scope-proof.v2" };
        foreach (var field in new[] { "version", "kind", "customerId", "cellId", "vaultNamespaceId", "vaultNamespaceCommitmentSha256", "authorityNamespaceId", "authorityNamespaceCommitmentSha256", "bindingSha256", "profileIdSha256", "scope", "scopeSha256", "keyId", "firstTransactionId", "firstCandidateSha256", "firstCandidateLength", "firstGeneration", "algorithm" }) payload[field] = proof[field]!.DeepClone();
        return payload;
    }
    static void RehashProof(JsonObject proof) => proof["sha256"] = Hash(JsonSerializer.SerializeToUtf8Bytes(new { payload = ProofPayload(proof), tag = Text(proof, "tag") }, Json));
    static string ProofMac(JsonObject proof, byte[] key)
    {
        var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, Encoding.UTF8.GetBytes("platform-studio.credential-absence-scope-proof.v2.hkdf-salt"), Encoding.UTF8.GetBytes("platform-studio.credential-absence-scope-proof.v2.mac-key")); var payload = Bytes(ProofPayload(proof));
        try { return Convert.ToHexStringLower(HMACSHA256.HashData(derived, payload)); }
        finally { CryptographicOperations.ZeroMemory(derived); CryptographicOperations.ZeroMemory(payload); }
    }
    static void UpdateAbsenceProjection(JsonObject content, JsonObject proof)
    {
        var projection = SourceInventory(content)["absenceScopes"]!.AsArray().Single(n => Text(n!, "profileIdSha256") == Text(proof, "profileIdSha256"))!;
        projection["scopeSha256"] = Text(proof, "scopeSha256"); projection["projectId"] = Text(proof["scope"]!, "projectId"); projection["environmentId"] = Text(proof["scope"]!, "environmentId"); projection["sourceKeyId"] = Text(proof, "keyId"); projection["proofSha256"] = Text(proof, "sha256");
    }
    static void RebuildInventory(JsonObject content, RestrictedSetFixture f)
    {
        var raw = SourceInventory(content); var profiles = raw["profileDependencies"]!.AsArray().Select(node =>
        {
            var p = node!; return (CredentialRecoveryProfileDependency)typeof(CredentialRecoveryProfileDependency).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([
                Text(p, "profileIdSha256"), Text(p, "scopeSha256"), Text(p, "projectId"), Text(p, "environmentId"), p["generation"]!.GetValue<long>(), p["enabled"]!.GetValue<bool>(), p["expiresUnixMilliseconds"]!.GetValue<long>(), Text(p, "sourceKeyId"), Text(p, "envelopeSha256"), p["envelopeLength"]!.GetValue<long>()]);
        }).ToArray();
        var absences = raw["absenceScopes"]!.AsArray().Select(node => node!.Deserialize<CredentialAbsenceScopeDependency>(Json)!).ToArray();
        var inventory = (CredentialScopedRecoveryInventory)typeof(CredentialScopedRecoveryInventory).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([
            ContentIdentity(content), raw["observedUtc"]!.GetValue<DateTimeOffset>(), profiles, absences, raw["authorityRevision"]!.GetValue<long>(), Text(raw, "authorityStateSha256")]);
        var restricted = (CredentialRestrictedRecoveryInventory)typeof(CredentialRestrictedRecoveryInventory).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single(c => c.GetParameters().Length == 1).Invoke([inventory]);
        content["inventory"] = new JsonObject
        {
            ["schema"] = restricted.Schema, ["accessProfile"] = restricted.AccessProfile,
            ["source"] = JsonSerializer.SerializeToNode(inventory, Json), ["sha256"] = restricted.Sha256
        };
    }
    static CredentialRestrictedRecoverySet Rewrite(CredentialRestrictedRecoverySet original, RestrictedSetFixture f, Action<JsonObject> mutate) => CredentialRestrictedRecoverySets.Parse(RewriteBytes(original, f, mutate));
    static byte[] RewriteBytes(CredentialRestrictedRecoverySet original, RestrictedSetFixture f, Action<JsonObject> mutate, bool rebuildInventory = true)
    {
        var artifact = Artifact(original); var content = Content(artifact); mutate(content); if (rebuildInventory) RebuildInventory(content, f); var bytes = Bytes(content);
        artifact["content"] = Convert.ToBase64String(bytes); SetSignature(artifact, original.Signature with { SourceNamespace = ContentIdentity(content), InventorySha256 = Text(content["inventory"]!, "sha256"), ProfileCount = rebuildInventory ? SourceInventory(content)["profileDependencies"]!.AsArray().Count : original.Signature.ProfileCount, AbsenceProofCount = rebuildInventory ? SourceInventory(content)["absenceScopes"]!.AsArray().Count : original.Signature.AbsenceProofCount, ContentSha256 = Hash(bytes), ContentLength = bytes.Length }, f.Signer); return Bytes(artifact);
    }
    static void SetSignature(JsonObject artifact, CredentialRestrictedRecoverySetSignature signature, RSA signer)
    {
        var payload = SignaturePayload(signature);
        artifact["signature"] = JsonSerializer.SerializeToNode(signature with { Signature = Convert.ToBase64String(signer.SignData(payload, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) }, Json);
    }
    static byte[] SignaturePayload(CredentialRestrictedRecoverySetSignature signature) => JsonSerializer.SerializeToUtf8Bytes(new
        {
            signature.Version, signature.Kind, signature.AccessProfile, signature.SetId, signature.CustomerId, signature.CellId, signature.SourceNamespace, signature.Generation,
            capturedUnixMilliseconds = signature.CapturedUtc.ToUnixTimeMilliseconds(), issuedUnixMilliseconds = signature.IssuedUtc.ToUnixTimeMilliseconds(), notBeforeUnixMilliseconds = signature.NotBeforeUtc.ToUnixTimeMilliseconds(), expiresUnixMilliseconds = signature.ExpiresUtc.ToUnixTimeMilliseconds(),
            signature.InventorySha256, signature.ProfileCount, signature.AbsenceProofCount, signature.ContentSha256, signature.ContentLength, signature.KeyId, signature.PublicKeySha256, signature.Algorithm
        }, Json);
    sealed class PausedKeys(ICredentialKeyProvider inner, bool cooperative = true) : ICredentialKeyProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken token)
        { Entered.TrySetResult(); if (cooperative) await Release.Task.WaitAsync(token); else await Release.Task; return await inner.GetKeyAsync(customerId, cellId, keyId, cooperative ? token : CancellationToken.None); }
    }
    sealed class RotateAfterBorrow(byte[] source) : ICredentialKeyProvider
    {
        public int Requests { get; private set; }
        public ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken token)
        { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new CredentialEncryptionKey(++Requests == 1 ? source : RandomNumberGenerator.GetBytes(32))); }
    }
    sealed class RestrictedSetFixture : IDisposable
    {
        public CredentialFixture Cell { get; }
        public RSA Signer { get; } = RSA.Create(2048);
        public CredentialVaultOptions Options { get; }
        public CredentialVault Source { get; }
        public CredentialVaultNamespaceIdentity Identity { get; }
        public CredentialScope Positive => Cell.Scope with { AuthProfileId = "profile/scoped-present" };
        public CredentialScope Absence => Cell.Scope with { AuthProfileId = "profile/scoped-absent", ProjectId = "absent-project", EnvironmentId = "prod" };
        public string PositiveHash => Hash(Encoding.UTF8.GetBytes(Positive.AuthProfileId));
        public string AbsenceHash => Hash(Encoding.UTF8.GetBytes(Absence.AuthProfileId));
        public string AuthorityDirectory => Options.RollbackProtection!.AuthorityDirectory;
        public RestrictedSetFixture(string provider = PipelineRequestFactory.GitHub)
        {
            Cell = new(provider); Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32);
            Options = Cell.Options with { AccessProfile = CredentialVaultAccessProfile.FoundationBoundDispatchOnly, RootPath = Path.Combine(Cell.Root, "restricted-set-vault"), RollbackProtection = new(Path.Combine(Cell.Root, "restricted-set-authority")) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 } };
            Source = CredentialVault.ProvisionStrongBoundDispatch(Options, Cell.Keys, Cell.Clock); Identity = Source.DescribeNamespace();
        }
        public async Task Initialize(bool sameKey = false)
        {
            using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token);
            var fault = typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.Instance | BindingFlags.NonPublic)!;
            fault.SetValue(Source, (Action<string>)(phase => { if (phase == "PendingPersisted") throw new IOException("Synthetic stopped initial write."); }));
            try { await Assert.ThrowsAsync<IOException>(() => Source.PutAsync(Absence, secret, Cell.Clock.Now.AddHours(1))); }
            finally { fault.SetValue(Source, null); }
            var pending = await Source.InspectRollbackStateAsync(Absence.AuthProfileId); await Source.ReconcilePendingWriteAsync(Absence.AuthProfileId, pending.AuthorityRevision, pending.AuthorityStateSha256, pending.Pending!.PendingSha256, CredentialRollbackResolution.AbandonUnpublished);
            var writer = sameKey ? Source : new CredentialVault(Options with { EncryptionKeyId = "key-2" }, Cell.Keys, Cell.Clock); await writer.PutAsync(Positive, secret, Cell.Clock.Now.AddHours(1));
        }
        public Task<CredentialRestrictedRecoverySet> Capture(CancellationToken token = default) => new CredentialVault(Options with { EncryptionKeyId = "unavailable-configured-write-key" }, Cell.Keys, Cell.Clock).CaptureRestrictedSignedRecoverySetAsync(Identity, Signer, "restricted-set-signer", 1, Cell.Clock.Now, Cell.Clock.Now.AddDays(1), token);
        public CredentialRestrictedRecoveryRehearsal Rehearsal(ICredentialKeyProvider? keys = null) => new(Identity, keys ?? Cell.Keys, Cell.Clock);
        public RSA Public() { var result = RSA.Create(); result.ImportSubjectPublicKeyInfo(Signer.ExportSubjectPublicKeyInfo(), out _); return result; }
        public void RemoveLiveRoots() { Directory.Move(Options.RootPath, Path.Combine(Cell.Root, "removed-live-restricted-vault")); Directory.Move(AuthorityDirectory, Path.Combine(Cell.Root, "removed-live-restricted-authority")); }
        public string[] Snapshot()
        {
            var entries = new List<string>(); Walk(Cell.Root); return entries.Order(StringComparer.Ordinal).ToArray();
            void Walk(string path)
            {
                var directory = Directory.Exists(path); FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
                var prefix = Path.GetRelativePath(Cell.Root, path) + ":" + Hash(acl.GetSecurityDescriptorBinaryForm());
                if (directory) { entries.Add(prefix + ":directory"); foreach (var child in Directory.EnumerateFileSystemEntries(path)) Walk(child); }
                else { var length = new FileInfo(path).Length; byte[] bytes = length == 0 ? [] : File.ReadAllBytes(path); entries.Add(prefix + ":" + length + ":" + Hash(bytes)); }
            }
        }
        public void Dispose() { Signer.Dispose(); Cell.Dispose(); }
    }
}
