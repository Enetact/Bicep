using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
[Collection(nameof(CredentialAbsenceScopeCapacityCollection))]
public sealed class CredentialAbsenceScopeBoundaryTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [WindowsCredentialFact]
    public void Legacy_empty_binding_state_and_options_keep_their_exact_v1_bytes()
    {
        using var f = new ScopeFixture(scoped: false);
        var binding = Node(f.Binding);
        var expectedBinding = new
        {
            schema = 1, kind = "credential-rollback-binding", customerId = f.Scope.CustomerId, cellId = f.Scope.CellId,
            vaultPath = f.Options.RootPath, vaultNamespaceId = Text(binding, "vaultNamespaceId"),
            authorityPath = f.Authority, authorityNamespaceId = Text(binding, "authorityNamespaceId")
        };
        var expected = JsonSerializer.SerializeToNode(expectedBinding, Json)!.AsObject(); expected["sha256"] = Hash(JsonSerializer.SerializeToUtf8Bytes(expectedBinding, Json));
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(expected, Json), File.ReadAllBytes(f.Binding));
        Assert.Equal(File.ReadAllBytes(f.Binding), File.ReadAllBytes(Path.Combine(f.Authority, "authority.json")));
        var preimage = new { f.Scope.CustomerId, f.Scope.CellId, vaultNamespaceId = Text(binding, "vaultNamespaceId"), authorityNamespaceId = Text(binding, "authorityNamespaceId"), schema = 1, revision = 0L, profiles = Array.Empty<object>() };
        var state = new { schema = 1, revision = 0L, profiles = new Dictionary<string, object>(), sha256 = Hash(JsonSerializer.SerializeToUtf8Bytes(preimage, Json)) };
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(state, Json), File.ReadAllBytes(f.State));
        Assert.Equal(JsonSerializer.Serialize(new { authorityDirectory = f.Authority }, Json), JsonSerializer.Serialize(f.Options.RollbackProtection, Json));
        Assert.Equal("credential-vault-fenced", Text(Node(Path.Combine(f.Options.RootPath, ".credential-namespace.json")), "kind"));
        Assert.Equal("credential-rollback-authority", Text(Node(Path.Combine(f.Authority, ".credential-namespace.json")), "kind"));
    }

    [WindowsCredentialFact]
    public async Task Legacy_pending_and_abandoned_entry_keep_exact_v1_wire_shape_without_backfilling_scope()
    {
        using var f = new ScopeFixture(scoped: false); await f.InterruptFirst();
        var pending = Node(f.State); var entry = Entry(pending, f.ProfileHash);
        Assert.Equal(new[] { "generation", "envelopeSha256", "envelopeLength", "pending" }, entry.Select(p => p.Key));
        Assert.Equal(7, entry["pending"]!.AsObject().Count);
        await f.Abandon(); var abandoned = Node(f.State); var final = Entry(abandoned, f.ProfileHash);
        Assert.Null(final["pending"]); Assert.False(final.ContainsKey("absenceProof"));
        Assert.Equal(1, abandoned["schema"]!.GetValue<int>());
        var expectedEntry = new LegacyEntry(0, "", 0, null);
        var expectedHash = LegacyStateDigest(abandoned, Node(f.Binding));
        var expected = new { schema = 1, revision = 2L, profiles = new Dictionary<string, LegacyEntry> { [f.ProfileHash] = expectedEntry }, sha256 = expectedHash };
        Assert.Equal(JsonSerializer.SerializeToUtf8Bytes(expected, Json), File.ReadAllBytes(f.State));
    }

    [WindowsCredentialFact]
    public async Task Legacy_reader_still_rejects_an_explicit_null_v2_proof_field_without_normalizing_it_away()
    {
        using var f = new ScopeFixture(scoped: false); await f.InterruptFirst(); await f.Abandon();
        var state = Node(f.State); Entry(state, f.ProfileHash)["absenceProof"] = null;
        File.WriteAllText(f.State, state.ToJsonString(Json)); var before = f.Snapshot(); f.Cell.Keys.Requests.Clear();
        Assert.Throws<CredentialException>(() => f.Reopen()); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("legacy-open-v2")]
    [InlineData("scoped-open-v1")]
    [InlineData("weak-open-v2")]
    [InlineData("legacy-factory-v2")]
    [InlineData("scoped-factory-v1")]
    [InlineData("weak-factory-v2")]
    [InlineData("scoped-reprovision")]
    [InlineData("unknown-format")]
    public void Modes_cannot_adopt_repair_downgrade_or_reprovision_existing_namespaces(string mode)
    {
        using var v2 = new ScopeFixture(); using var v1 = new ScopeFixture(scoped: false); var before2 = v2.Snapshot(); var before1 = v1.Snapshot();
        Assert.Throws<CredentialException>(() =>
        {
            _ = mode switch
            {
                "legacy-open-v2" => new CredentialVault(v2.Options with { RollbackProtection = new(v2.Authority) }, v2.Cell.Keys, v2.Cell.Clock),
                "scoped-open-v1" => new CredentialVault(v1.Options with { RollbackProtection = new(v1.Authority) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 } }, v1.Cell.Keys, v1.Cell.Clock),
                "weak-open-v2" => new CredentialVault(v2.Options with { RollbackProtection = null }, v2.Cell.Keys, v2.Cell.Clock),
                "legacy-factory-v2" => CredentialVault.ProvisionStrong(v2.Options, v2.Cell.Keys, v2.Cell.Clock),
                "scoped-factory-v1" => CredentialVault.ProvisionStrongScoped(v1.Options, v1.Cell.Keys, v1.Cell.Clock),
                "weak-factory-v2" => CredentialVault.Provision(v2.Options, v2.Cell.Keys, v2.Cell.Clock),
                "scoped-reprovision" => CredentialVault.ProvisionStrongScoped(v2.Options, v2.Cell.Keys, v2.Cell.Clock),
                _ => new CredentialVault(v2.Options with { RollbackProtection = new(v2.Authority) { Format = (CredentialRollbackFormat)99 } }, v2.Cell.Keys, v2.Cell.Clock)
            };
        });
        Assert.Equal(before2, v2.Snapshot()); Assert.Equal(before1, v1.Snapshot());
        Assert.Empty(v2.Cell.Keys.Requests); Assert.Empty(v1.Cell.Keys.Requests);
    }

    [WindowsCredentialFact]
    public async Task First_pending_proof_is_source_authenticated_and_abandon_preserves_its_original_bytes()
    {
        using var f = new ScopeFixture(); await f.InterruptFirst(); var status = await f.Vault.InspectRollbackStateAsync(f.Scope.AuthProfileId);
        var state = Node(f.State); var proof = Proof(state, f.ProfileHash); var serialized = proof.ToJsonString();
        Assert.Equal(2, state["schema"]!.GetValue<int>()); Assert.Equal("credential-absence-scope-proof", Text(proof, "kind"));
        Assert.Equal(status.Pending!.TransactionId, Text(proof, "firstTransactionId")); Assert.Equal(status.Pending.NewEnvelopeSha256, Text(proof, "firstCandidateSha256"));
        Assert.Equal(status.Pending.NewEnvelopeLength, proof["firstCandidateLength"]!.GetValue<long>()); Assert.Equal(1, proof["firstGeneration"]!.GetValue<long>());
        Assert.Equal("key-1", Text(proof, "keyId")); Assert.Equal(f.Scope, proof["scope"]!.Deserialize<CredentialScope>(Json)!, ScopeComparer.Instance);
        AssertProofMac(proof, f.Cell.Keys.Values["key-1"]);
        var resolved = await f.Abandon(); Assert.Equal(0, resolved.CommittedGeneration); Assert.Null(resolved.Pending); Assert.False(File.Exists(f.Profile));
        Assert.Equal(serialized, Proof(Node(f.State), f.ProfileHash).ToJsonString());
        var report = await f.Vault.InspectAbsenceScopeProvenanceAsync(f.Vault.DescribeNamespace());
        var dependency = Assert.Single(report.AbsenceScopes); Assert.Equal(f.ProfileHash, dependency.ProfileIdSha256); Assert.Equal(["key-1"], report.AbsenceRequiredKeyIds);
    }

    [WindowsCredentialFact]
    public async Task A_repeated_abandoned_attempt_preserves_the_original_proof_even_with_a_new_candidate_and_key()
    {
        using var f = new ScopeFixture(); await f.InterruptFirst(); await f.Abandon(); var original = Proof(Node(f.State), f.ProfileHash).ToJsonString();
        f.Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32); var rotated = f.Reopen(keyId: "key-2");
        await f.InterruptFirst(rotated, token: "synthetic-second-opaque-candidate"); var pending = await rotated.InspectRollbackStateAsync(f.Scope.AuthProfileId);
        Assert.NotEqual(Text(JsonNode.Parse(original)!.AsObject(), "firstCandidateSha256"), pending.Pending!.NewEnvelopeSha256);
        Assert.Equal(original, Proof(Node(f.State), f.ProfileHash).ToJsonString()); await f.Abandon(rotated);
        Assert.Equal(original, Proof(Node(f.State), f.ProfileHash).ToJsonString()); Assert.False(File.Exists(f.Profile));
        var report = await rotated.InspectAbsenceScopeProvenanceAsync(rotated.DescribeNamespace()); Assert.Equal(["key-1"], report.AbsenceRequiredKeyIds);
    }

    [WindowsCredentialFact]
    public async Task Positive_publication_removes_the_resolved_absence_proof_and_keeps_the_new_envelope_key()
    {
        using var f = new ScopeFixture(); await f.InterruptFirst(); await f.Abandon();
        f.Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32); var rotated = f.Reopen(keyId: "key-2"); f.Cell.Keys.Requests.Clear(); await f.Store(rotated);
        Assert.Contains(f.Cell.Keys.Requests, key => key.KeyId == "key-1"); Assert.Contains(f.Cell.Keys.Requests, key => key.KeyId == "key-2");
        var entry = Entry(Node(f.State), f.ProfileHash); Assert.Equal(1, entry["generation"]!.GetValue<long>()); Assert.Null(entry["absenceProof"]); Assert.Null(entry["pending"]);
        var report = await rotated.InspectAbsenceScopeProvenanceAsync(rotated.DescribeNamespace()); Assert.Empty(report.AbsenceScopes); Assert.Empty(report.AbsenceRequiredKeyIds);
        using var lease = await rotated.ResolveAsync(f.Scope); Assert.Equal(1, lease.Profile.Generation);
    }

    [Theory]
    [InlineData("project")]
    [InlineData("environment")]
    [InlineData("connection")]
    [InlineData("connection-generation")]
    [InlineData("connection-digest")]
    [InlineData("permissions")]
    [InlineData("provider")]
    public async Task Abandoned_profile_cannot_be_retargeted_even_when_no_envelope_was_ever_published(string field)
    {
        using var f = new ScopeFixture(); await f.InterruptFirst(); await f.Abandon(); var before = f.Snapshot();
        var target = ChangeScope(f.Scope, field); f.Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32); var rotated = f.Reopen(keyId: "key-2"); f.Cell.Keys.Requests.Clear();
        using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token);
        var error = await Assert.ThrowsAsync<CredentialException>(() => rotated.PutAsync(target, secret, f.Cell.Clock.Now.AddHours(1)));
        AssertOpaque(error, f); Assert.Equal(before, f.Snapshot()); Assert.False(File.Exists(f.Profile));
        Assert.DoesNotContain(f.Cell.Keys.Requests, key => key.KeyId == "key-2");
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Missing_or_changed_original_key_cannot_be_replaced_by_the_current_write_key(bool changed)
    {
        using var f = new ScopeFixture(); await f.InterruptFirst(); await f.Abandon(); f.Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32);
        if (changed) f.Cell.Keys.Values["key-1"] = RandomNumberGenerator.GetBytes(32); else f.Cell.Keys.Values.Remove("key-1");
        var before = f.Snapshot(); f.Cell.Keys.Requests.Clear(); var rotated = f.Reopen(keyId: "key-2");
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Store(rotated)); AssertOpaque(error, f);
        Assert.Equal(before, f.Snapshot()); Assert.DoesNotContain(f.Cell.Keys.Requests, key => key.KeyId == "key-2");
    }

    public static IEnumerable<object[]> ForgedProofFields()
    {
        foreach (var field in new[] { "project", "environment", "connection", "connection-generation", "connection-digest", "permissions", "provider", "key", "transaction", "candidate", "candidate-length", "vault", "vault-commitment", "authority", "authority-commitment", "binding", "tag" }) yield return [field];
    }

    [Theory]
    [MemberData(nameof(ForgedProofFields))]
    public async Task Rehashed_public_metadata_cannot_replace_a_source_key_mac(string field)
    {
        using var f = new ScopeFixture(); await f.InterruptFirst(); await f.Abandon();
        var state = Node(f.State); var proof = Proof(state, f.ProfileHash);
        if (new[] { "project", "environment", "connection", "connection-generation", "connection-digest", "permissions", "provider" }.Contains(field))
        {
            var scope = ChangeScope(proof["scope"]!.Deserialize<CredentialScope>(Json)!, field); proof["scope"] = JsonSerializer.SerializeToNode(scope, Json); proof["scopeSha256"] = Hash(JsonSerializer.SerializeToUtf8Bytes(scope, Json));
        }
        else switch (field)
        {
            case "key": f.Cell.Keys.Values["same-material-key-alias"] = f.Cell.Keys.Values["key-1"].ToArray(); proof["keyId"] = "same-material-key-alias"; break;
            case "transaction": proof["firstTransactionId"] = Guid.NewGuid().ToString("N"); break;
            case "candidate": proof["firstCandidateSha256"] = new string('b', 64); break;
            case "candidate-length": proof["firstCandidateLength"] = proof["firstCandidateLength"]!.GetValue<long>() + 1; break;
            case "vault": proof["vaultNamespaceId"] = Guid.NewGuid().ToString("N"); break;
            case "vault-commitment": proof["vaultNamespaceCommitmentSha256"] = new string('b', 64); break;
            case "authority": proof["authorityNamespaceId"] = Guid.NewGuid().ToString("N"); break;
            case "authority-commitment": proof["authorityNamespaceCommitmentSha256"] = new string('b', 64); break;
            case "binding": proof["bindingSha256"] = new string('b', 64); break;
            default: proof["tag"] = new string('b', 64); break;
        }
        RehashProof(proof); f.SaveState(state); var before = f.Snapshot(); f.Cell.Keys.Requests.Clear();
        var error = await Assert.ThrowsAsync<CredentialException>(async () => { var reopened = f.Reopen(); await reopened.InspectAbsenceScopeProvenanceAsync(reopened.DescribeNamespace()); });
        AssertOpaque(error, f); Assert.Equal(before, f.Snapshot()); Assert.False(File.Exists(f.Profile));
        if (new[] { "project", "environment", "connection", "connection-generation", "connection-digest", "permissions", "provider", "key", "transaction", "candidate", "candidate-length", "tag" }.Contains(field))
            Assert.Contains(f.Cell.Keys.Requests, key => key.KeyId == Text(proof, "keyId"));
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("null")]
    [InlineData("version")]
    [InlineData("kind")]
    [InlineData("algorithm")]
    [InlineData("generation")]
    [InlineData("unknown")]
    [InlineData("uppercase-tag")]
    public async Task Missing_or_malformed_v2_proofs_fail_without_repair_or_key_access(string field)
    {
        using var f = new ScopeFixture(); await f.InterruptFirst(); await f.Abandon(); var state = Node(f.State); var entry = Entry(state, f.ProfileHash); var proof = Proof(state, f.ProfileHash);
        switch (field)
        {
            case "missing": entry.Remove("absenceProof"); break;
            case "null": entry["absenceProof"] = null; break;
            case "version": proof["version"] = 1; RehashProof(proof); break;
            case "kind": proof["kind"] = "credential-scope-proof"; RehashProof(proof); break;
            case "algorithm": proof["algorithm"] = "HMAC-SHA256"; RehashProof(proof); break;
            case "generation": proof["firstGeneration"] = 2; RehashProof(proof); break;
            case "uppercase-tag": proof["tag"] = Text(proof, "tag").ToUpperInvariant(); RehashProof(proof); break;
            default: proof["unexpected"] = "unknown-metadata"; break;
        }
        f.SaveState(state); var before = f.Snapshot(); f.Cell.Keys.Requests.Clear();
        await Assert.ThrowsAsync<CredentialException>(async () => { var vault = f.Reopen(); await vault.InspectAbsenceScopeProvenanceAsync(vault.DescribeNamespace()); });
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Cancellation_after_first_pending_preserves_proof_and_requires_explicit_reconciliation()
    {
        using var f = new ScopeFixture(); using var cancel = new CancellationTokenSource(); Fault(f.Vault, phase => { if (phase == "PendingPersisted") cancel.Cancel(); });
        using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token);
        try { await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Vault.PutAsync(f.Scope, secret, f.Cell.Clock.Now.AddHours(1), cancellationToken: cancel.Token)); }
        finally { Fault(f.Vault, null); }
        var proof = Proof(Node(f.State), f.ProfileHash).ToJsonString(); var before = f.Snapshot(); f.Cell.Keys.Requests.Clear();
        await Assert.ThrowsAsync<CredentialException>(() => f.Store()); Assert.Equal(before, f.Snapshot()); Assert.Empty(f.Cell.Keys.Requests);
        await f.Abandon(); Assert.Equal(proof, Proof(Node(f.State), f.ProfileHash).ToJsonString());
        await f.Store(); Assert.Equal(1, (await f.Vault.GetProfileAsync(f.Scope.AuthProfileId)).Generation);
    }

    [WindowsCredentialFact]
    public async Task Native_dpapi_key_custody_authenticates_the_absence_without_exporting_a_key_or_token()
    {
        using var f = new ScopeFixture(native: true); await f.InitializeNative(); await f.InterruptFirst(); await f.Abandon(); var before = f.Snapshot();
        var report = await f.Vault.InspectAbsenceScopeProvenanceAsync(f.Vault.DescribeNamespace()); Assert.Single(report.AbsenceScopes);
        var json = JsonSerializer.Serialize(report, Json); Assert.DoesNotContain(CredentialFixture.Token, json); Assert.DoesNotContain(f.Scope.AuthProfileId, json); Assert.DoesNotContain(f.Cell.Root, json);
        Assert.DoesNotContain("firstTransactionId", json); Assert.DoesNotContain("tag", json); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Competing_retry_writers_consume_the_original_absence_as_one_generation()
    {
        using var f = new ScopeFixture(); await f.InterruptFirst(); await f.Abandon();
        async Task<bool> Write(CredentialVault vault)
        {
            try { await f.Store(vault); return true; }
            catch (CredentialException e) when (e.Code == "credential-generation-conflict") { return false; }
        }
        var writers = Enumerable.Range(0, 4).Select(_ => f.Reopen()).ToArray();
        Assert.Single(await Task.WhenAll(writers.Select(Write)), success => success);
        var status = await f.Vault.InspectRollbackStateAsync(f.Scope.AuthProfileId); Assert.Equal(1, status.CommittedGeneration); Assert.Null(status.Pending);
        Assert.Null(Entry(Node(f.State), f.ProfileHash)["absenceProof"]); Assert.Single(Directory.GetFiles(f.Options.RootPath, "*.credential.json"));
    }

    [WindowsCredentialFact]
    public async Task Counter_exhaustion_refuses_before_a_new_pending_record_and_preserves_the_absence()
    {
        using var f = new ScopeFixture(); await f.InterruptFirst(); await f.Abandon(); var state = Node(f.State); state["revision"] = 9007199254740990L; f.SaveState(state); var before = f.Snapshot();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Store()); Assert.Equal("credential-rollback-state-limit", error.Code); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("project")]
    [InlineData("environment")]
    public async Task Reconciliation_cannot_accept_an_authentic_other_scope_envelope_using_rehashed_pending_metadata(string field)
    {
        using var f = new ScopeFixture(); await f.InterruptFirst();
        using var other = new ScopeFixture(); other.Cell.Keys.Values["key-1"] = f.Cell.Keys.Values["key-1"].ToArray();
        await other.Store(scope: ChangeScope(f.Scope, field)); var copied = File.ReadAllBytes(other.Profile); f.PrivateProfile(copied);
        var state = Node(f.State); var proofBefore = Proof(state, f.ProfileHash).ToJsonString(); var pending = Entry(state, f.ProfileHash)["pending"]!.AsObject();
        pending["transactionId"] = Guid.NewGuid().ToString("N"); pending["newEnvelopeSha256"] = Hash(copied); pending["newEnvelopeLength"] = copied.LongLength;
        var binding = Node(f.Binding);
        pending["pendingSha256"] = Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            customerId = f.Scope.CustomerId, cellId = f.Scope.CellId, vaultNamespaceId = Text(binding, "vaultNamespaceId"), authorityNamespaceId = Text(binding, "authorityNamespaceId"), profile = f.ProfileHash,
            transactionId = Text(pending, "transactionId"), previousGeneration = 0L, previousEnvelopeSha256 = "", newGeneration = 1L, newEnvelopeSha256 = Hash(copied), newEnvelopeLength = copied.LongLength
        }, Json));
        f.SaveState(state); var status = await f.Vault.InspectRollbackStateAsync(f.Scope.AuthProfileId); var before = f.Snapshot();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ReconcilePendingWriteAsync(f.Scope.AuthProfileId,
            status.AuthorityRevision, status.AuthorityStateSha256, status.Pending!.PendingSha256, CredentialRollbackResolution.AcceptPublished));
        Assert.Equal("credential-absence-proof-scope-changed", error.Code); Assert.Equal(before, f.Snapshot());
        Assert.Equal(proofBefore, Proof(Node(f.State), f.ProfileHash).ToJsonString()); Assert.Equal(copied, File.ReadAllBytes(f.Profile));
        Assert.NotNull((await f.Vault.InspectRollbackStateAsync(f.Scope.AuthProfileId)).Pending);
    }

    [WindowsCredentialFact]
    public async Task Positive_and_unpublished_profiles_share_the_256_slot_limit_without_repairing_the_257th()
    {
        using var f = new ScopeFixture(); await f.Store(); var first = f.Scope with { AuthProfileId = "profile/absence-capacity-0" };
        await f.InterruptFirst(scope: first); await f.Abandon(profileId: first.AuthProfileId);
        var state = Node(f.State); var firstHash = Hash(Encoding.UTF8.GetBytes(first.AuthProfileId)); var prototype = Entry(state, firstHash);
        // Independently authenticated synthetic history avoids O(n^2) repeated guarded setup. Actual overflow/retry admissions follow.
        for (var i = 1; i < 255; i++)
        {
            var scope = f.Scope with { AuthProfileId = "profile/absence-capacity-" + i }; var hash = Hash(Encoding.UTF8.GetBytes(scope.AuthProfileId));
            var entry = prototype.DeepClone().AsObject(); var proof = entry["absenceProof"]!.AsObject();
            proof["profileIdSha256"] = hash; proof["scope"] = JsonSerializer.SerializeToNode(scope, Json); proof["scopeSha256"] = Hash(JsonSerializer.SerializeToUtf8Bytes(scope, Json));
            proof["firstTransactionId"] = Guid.NewGuid().ToString("N"); proof["tag"] = ProofMac(proof, f.Cell.Keys.Values["key-1"]); RehashProof(proof);
            state["profiles"]![hash] = entry;
        }
        state["revision"] = 512L; f.SaveState(state);
        var report = await f.Vault.InspectAbsenceScopeProvenanceAsync(f.Vault.DescribeNamespace()); Assert.Equal(255, report.AbsenceScopes.Count); Assert.Equal(1, report.PositiveEnvelopeCount);
        var before = f.Snapshot(); using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token);
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Vault.PutAsync(f.Scope with { AuthProfileId = "profile/over-capacity" }, secret, f.Cell.Clock.Now.AddHours(1)));
        Assert.Equal("credential-rollback-profile-limit", error.Code); Assert.Equal(before, f.Snapshot());
        await f.Store(scope: f.Scope with { AuthProfileId = "profile/absence-capacity-0" });
        report = await f.Vault.InspectAbsenceScopeProvenanceAsync(f.Vault.DescribeNamespace()); Assert.Equal(254, report.AbsenceScopes.Count); Assert.Equal(2, report.PositiveEnvelopeCount);
    }

    [WindowsCredentialFact]
    public async Task Oversized_state_profile_map_is_refused_before_typed_proof_hydration_or_key_access()
    {
        using var f = new ScopeFixture(); var state = Node(f.State); var profiles = state["profiles"]!.AsObject();
        for (var i = 0; i < 257; i++) profiles[i.ToString("x64")] = "not-an-entry";
        f.SaveState(state); var before = f.Snapshot(); f.Cell.Keys.Requests.Clear();
        var error = Assert.Throws<CredentialException>(() => f.Reopen()); Assert.Equal("credential-rollback-state-limit", error.Code); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot());
    }

    static CredentialScope ChangeScope(CredentialScope scope, string field) => field switch
    {
        "project" => scope with { ProjectId = "other-project" }, "environment" => scope with { EnvironmentId = "prod" }, "connection" => scope with { ConnectionId = "other-ci" },
        "connection-generation" => scope with { ConnectionGeneration = 2 }, "connection-digest" => scope with { ConnectionDigest = new string('c', 64) },
        "permissions" => scope with { RequiredScopes = ["actions:write", "contents:read"] },
        _ => scope with { Provider = PipelineRequestFactory.AzureDevOps, Audience = "499b84ac-1321-427f-aa17-267ca6975798", RequiredScopes = ["499b84ac-1321-427f-aa17-267ca6975798/.default"] }
    };
    static JsonObject Node(string path) => JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
    static JsonObject Entry(JsonObject state, string id) => state["profiles"]![id]!.AsObject();
    static JsonObject Proof(JsonObject state, string id) => Entry(state, id)["absenceProof"]!.AsObject();
    static string Text(JsonObject node, string field) => node[field]!.GetValue<string>();
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static void Fault(CredentialVault vault, Action<string>? action) => typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vault, action);
    static void AssertOpaque(Exception error, ScopeFixture f)
    { Assert.DoesNotContain(CredentialFixture.Token, error.ToString()); Assert.DoesNotContain(f.Scope.AuthProfileId, error.ToString()); Assert.DoesNotContain(f.Cell.Root, error.ToString()); }
    static JsonObject ProofPayload(JsonObject proof)
    {
        var payload = new JsonObject { ["domain"] = "platform-studio.credential-absence-scope-proof.v2" };
        foreach (var field in new[] { "version", "kind", "customerId", "cellId", "vaultNamespaceId", "vaultNamespaceCommitmentSha256", "authorityNamespaceId", "authorityNamespaceCommitmentSha256", "bindingSha256", "profileIdSha256", "scope", "scopeSha256", "keyId", "firstTransactionId", "firstCandidateSha256", "firstCandidateLength", "firstGeneration", "algorithm" }) payload[field] = proof[field]!.DeepClone();
        return payload;
    }
    static void RehashProof(JsonObject proof) => proof["sha256"] = Hash(JsonSerializer.SerializeToUtf8Bytes(new { payload = ProofPayload(proof), tag = Text(proof, "tag") }, Json));
    static void AssertProofMac(JsonObject proof, byte[] key)
        => Assert.Equal(Text(proof, "tag"), ProofMac(proof, key));
    static string ProofMac(JsonObject proof, byte[] key)
    {
        var payload = JsonSerializer.SerializeToUtf8Bytes(ProofPayload(proof), Json);
        var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, key, 32, Encoding.UTF8.GetBytes("platform-studio.credential-absence-scope-proof.v2.hkdf-salt"), Encoding.UTF8.GetBytes("platform-studio.credential-absence-scope-proof.v2.mac-key"));
        try { return Convert.ToHexStringLower(HMACSHA256.HashData(derived, payload)); }
        finally { CryptographicOperations.ZeroMemory(derived); CryptographicOperations.ZeroMemory(payload); }
    }
    sealed record LegacyEntry(long Generation, string EnvelopeSha256, long EnvelopeLength, CredentialRollbackPending? Pending);
    static string LegacyStateDigest(JsonObject state, JsonObject binding) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
    {
        customerId = Text(binding, "customerId"), cellId = Text(binding, "cellId"), vaultNamespaceId = Text(binding, "vaultNamespaceId"), authorityNamespaceId = Text(binding, "authorityNamespaceId"),
        schema = state["schema"]!.GetValue<int>(), revision = state["revision"]!.GetValue<long>(),
        profiles = state["profiles"]!.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { id = p.Key, entry = p.Value!.Deserialize<LegacyEntry>(Json) }).ToArray()
    }, Json));

    sealed class ScopeComparer : IEqualityComparer<CredentialScope>
    {
        internal static readonly ScopeComparer Instance = new();
        public bool Equals(CredentialScope? x, CredentialScope? y) => JsonSerializer.Serialize(x, Json) == JsonSerializer.Serialize(y, Json);
        public int GetHashCode(CredentialScope obj) => StringComparer.Ordinal.GetHashCode(JsonSerializer.Serialize(obj, Json));
    }
    sealed class ScopeFixture : IDisposable
    {
        public CredentialFixture Cell { get; } = new();
        public CredentialScope Scope => Cell.Scope;
        public string Authority => Path.Combine(Cell.Root, "scope-authority");
        public string State => Path.Combine(Authority, "state.json");
        public string Binding => Path.Combine(Options.RootPath, "rollback-binding.json");
        public string ProfileHash => Hash(Encoding.UTF8.GetBytes(Scope.AuthProfileId));
        public string Profile => Path.Combine(Options.RootPath, ProfileHash + ".credential.json");
        public CredentialVaultOptions Options { get; }
        public CredentialVault Vault { get; }
        readonly ICredentialKeyProvider keys;
        readonly CredentialWindowsUserKeyProvider? nativeKeys;
        public ScopeFixture(bool scoped = true, bool native = false)
        {
            nativeKeys = native ? Cell.ProvisionNativeKeys("absence-native-keys") : null; keys = nativeKeys ?? (ICredentialKeyProvider)Cell.Keys;
            Options = Cell.Options with { RootPath = Path.Combine(Cell.Root, "scope-vault"), RollbackProtection = new(Authority) { Format = scoped ? CredentialRollbackFormat.AuthenticatedScopeV2 : CredentialRollbackFormat.LegacyV1 } };
            Vault = scoped ? CredentialVault.ProvisionStrongScoped(Options, keys, Cell.Clock) : CredentialVault.ProvisionStrong(Options, keys, Cell.Clock);
        }
        public async Task InitializeNative() { using var key = await nativeKeys!.ProvisionKeyAsync(Scope.CustomerId, Scope.CellId, "key-1"); }
        public CredentialVault Reopen(string keyId = "key-1") => new(Options with { EncryptionKeyId = keyId }, keys, Cell.Clock);
        public async Task Store(CredentialVault? vault = null, string? token = null, CredentialScope? scope = null)
        { using var secret = CredentialSecret.FromUtf8(token ?? CredentialFixture.Token); await (vault ?? Vault).PutAsync(scope ?? Scope, secret, Cell.Clock.Now.AddHours(1)); }
        public async Task InterruptFirst(CredentialVault? vault = null, string? token = null, CredentialScope? scope = null)
        {
            vault ??= Vault; Fault(vault, phase => { if (phase == "PendingPersisted") throw new IOException("Synthetic interrupted publication."); });
            try { await Assert.ThrowsAsync<IOException>(() => Store(vault, token, scope)); }
            finally { Fault(vault, null); }
        }
        public async Task<CredentialRollbackStatus> Abandon(CredentialVault? vault = null, string? profileId = null)
        {
            vault ??= Vault; profileId ??= Scope.AuthProfileId; var pending = await vault.InspectRollbackStateAsync(profileId);
            return await vault.ReconcilePendingWriteAsync(profileId, pending.AuthorityRevision, pending.AuthorityStateSha256, pending.Pending!.PendingSha256, CredentialRollbackResolution.AbandonUnpublished);
        }
        public void SaveState(JsonObject state)
        {
            var binding = Node(Binding); var profiles = state["profiles"]!.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { id = p.Key, entry = p.Value }).ToArray();
            state["sha256"] = Hash(JsonSerializer.SerializeToUtf8Bytes(new
            {
                domain = "platform-studio.credential-rollback-scoped-state.v2", customerId = Text(binding, "customerId"), cellId = Text(binding, "cellId"), vaultNamespaceId = Text(binding, "vaultNamespaceId"), authorityNamespaceId = Text(binding, "authorityNamespaceId"),
                schema = state["schema"]!.GetValue<int>(), revision = state["revision"]!.GetValue<long>(), profiles
            }, Json));
            File.WriteAllText(State, state.ToJsonString(Json));
        }
        public void PrivateProfile(byte[] bytes)
        {
            using var current = WindowsIdentity.GetCurrent(); var security = new FileSecurity(); security.SetOwner(current.User!); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(current.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            using var stream = new FileInfo(Profile).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.Read, 4096, FileOptions.WriteThrough, security); stream.Write(bytes); stream.Flush(true);
        }
        public string[] Snapshot()
        {
            var snapshots = new List<string>();
            foreach (var root in new[] { Options.RootPath, Authority }) Walk(root);
            return snapshots.Order(StringComparer.Ordinal).ToArray();
            void Walk(string path)
            {
                var attributes = File.GetAttributes(path); var relative = Path.GetRelativePath(Cell.Root, path);
                if ((attributes & FileAttributes.ReparsePoint) != 0) { snapshots.Add(relative + ":link"); return; }
                var directory = Directory.Exists(path); FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
                var acl = Hash(security.GetSecurityDescriptorBinaryForm());
                if (directory) { snapshots.Add(relative + ":directory:" + acl); foreach (var child in Directory.EnumerateFileSystemEntries(path)) Walk(child); }
                else { var length = new FileInfo(path).Length; byte[] bytes = length == 0 ? [] : File.ReadAllBytes(path); snapshots.Add(relative + ":file:" + length + ":" + Hash(bytes) + ":" + acl); }
            }
        }
        public void Dispose() => Cell.Dispose();
    }
}

// Maximum-cell crypto/custody scans retain the production 20-second cooperative deadline.
// Run this native cohort without competing test collections; explicit in-test writer races remain concurrent.
[CollectionDefinition(nameof(CredentialAbsenceScopeCapacityCollection), DisableParallelization = true)]
public sealed class CredentialAbsenceScopeCapacityCollection { }
