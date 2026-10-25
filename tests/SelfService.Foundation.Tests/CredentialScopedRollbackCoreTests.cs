using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialScopedRollbackCoreTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [WindowsCredentialFact]
    public void Explicit_fresh_factory_creates_distinct_schema2_roots_and_all_downgrade_modes_refuse_them()
    {
        using var f = new Fixture(provision: false);
        Assert.Equal("credential-rollback-scoped-explicit-provision-required", Assert.Throws<CredentialException>(() => CredentialVault.ProvisionStrong(f.Options, f.Cell.Keys, f.Cell.Clock)).Code);
        Assert.False(Directory.Exists(f.Options.RootPath)); Assert.False(Directory.Exists(f.Authority));
        f.Provision(); var before = f.Snapshot();
        Assert.Equal("credential-vault-scoped-fenced", JsonNode.Parse(File.ReadAllText(Path.Combine(f.Options.RootPath, ".credential-namespace.json")))!["kind"]!.GetValue<string>());
        Assert.Equal("credential-rollback-scoped-authority", JsonNode.Parse(File.ReadAllText(Path.Combine(f.Authority, ".credential-namespace.json")))!["kind"]!.GetValue<string>());
        Assert.Equal(2, f.State()["schema"]!.GetValue<int>()); Assert.Equal(2, JsonNode.Parse(File.ReadAllText(Path.Combine(f.Authority, "authority.json")))!["schema"]!.GetValue<int>());
        Assert.Throws<CredentialException>(() => new CredentialVault(f.Options with { RollbackProtection = null }, f.Cell.Keys, f.Cell.Clock));
        Assert.Throws<CredentialException>(() => new CredentialVault(f.Options with { RollbackProtection = new(f.Authority) }, f.Cell.Keys, f.Cell.Clock));
        Assert.Throws<CredentialException>(() => CredentialVault.ProvisionStrongScoped(f.Options, f.Cell.Keys, f.Cell.Clock));
        Assert.Equal(before, f.Snapshot()); Assert.Equal(f.Vault.DescribeNamespace(), f.Reopen().DescribeNamespace());
    }

    [WindowsCredentialFact]
    public async Task First_pending_uses_same_candidate_key_and_abandon_preserves_authenticated_original_scope_without_token()
    {
        using var f = new Fixture(); f.Fault("PendingPersisted"); f.Cell.Keys.Requests.Clear();
        await Assert.ThrowsAsync<CredentialException>(() => f.Put()); Assert.Single(f.Cell.Keys.Requests);
        var proof = f.Proof(); Assert.Equal(2, proof["version"]!.GetValue<int>()); Assert.Equal(1, proof["firstGeneration"]!.GetValue<int>());
        Assert.Equal(f.Scope.ProjectId, proof["scope"]!["projectId"]!.GetValue<string>()); Assert.Equal(f.Hash, proof["profileIdSha256"]!.GetValue<string>());
        Assert.Equal("key-1", proof["keyId"]!.GetValue<string>()); Assert.Equal(f.Entry()["pending"]!["transactionId"]!.GetValue<string>(), proof["firstTransactionId"]!.GetValue<string>());
        Assert.Equal(f.Entry()["pending"]!["newEnvelopeSha256"]!.GetValue<string>(), proof["firstCandidateSha256"]!.GetValue<string>());
        var original = proof.ToJsonString(Json); Assert.DoesNotContain(CredentialFixture.Token, original); Assert.DoesNotContain("ciphertext", original, StringComparison.OrdinalIgnoreCase);
        await f.Reconcile(CredentialRollbackResolution.AbandonUnpublished); Assert.Equal(0, f.Entry()["generation"]!.GetValue<int>());
        Assert.Null(f.Entry()["pending"]); Assert.Equal(original, f.Proof().ToJsonString(Json)); Assert.False(File.Exists(f.ProfilePath));
    }

    [WindowsCredentialFact]
    public async Task Repeated_abandon_and_new_key_retry_keep_exact_original_proof_until_generation1_publication_drops_it()
    {
        using var f = new Fixture(); f.Fault("PendingPersisted"); await Assert.ThrowsAsync<CredentialException>(() => f.Put());
        var original = f.Proof().ToJsonString(Json); var originalCandidate = f.Proof()["firstCandidateSha256"]!.GetValue<string>();
        await f.Reconcile(CredentialRollbackResolution.AbandonUnpublished); f.Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32);
        var retry = f.Reopen("key-2"); SetFault(retry, "PendingPersisted"); f.Cell.Keys.Requests.Clear();
        await Assert.ThrowsAsync<CredentialException>(() => f.Put(vault: retry));
        Assert.Equal(new[] { "key-1", "key-2" }, f.Cell.Keys.Requests.Select(r => r.KeyId)); Assert.Equal(original, f.Proof().ToJsonString(Json));
        Assert.NotEqual(originalCandidate, f.Entry()["pending"]!["newEnvelopeSha256"]!.GetValue<string>());
        await f.Reconcile(CredentialRollbackResolution.AbandonUnpublished); Assert.Equal(original, f.Proof().ToJsonString(Json));
        SetFault(retry, null); var published = await f.Put(vault: retry); Assert.Equal(1, published.Generation);
        Assert.Null(f.Entry()["absenceProof"]); Assert.Equal(1, f.Entry()["generation"]!.GetValue<int>());
        Assert.Equal("key-2", JsonNode.Parse(File.ReadAllText(f.ProfilePath))!["keyId"]!.GetValue<string>());
        using var lease = await retry.ResolveAsync(f.Scope); Assert.Equal(JsonSerializer.Serialize(published, Json), JsonSerializer.Serialize(lease.Profile, Json));
    }

    [WindowsCredentialFact]
    public async Task Proven_absence_cannot_retarget_even_connection_version_or_digest_before_any_key_lookup()
    {
        using var f = new Fixture(); f.Fault("PendingPersisted"); await Assert.ThrowsAsync<CredentialException>(() => f.Put()); await f.Reconcile(CredentialRollbackResolution.AbandonUnpublished);
        f.Fault(null); var before = f.Snapshot(); f.Cell.Keys.Requests.Clear();
        var scope = f.Scope with { ConnectionGeneration = 2, ConnectionDigest = new string('b', 64) };
        Assert.Equal("credential-absence-proof-scope-changed", (await Assert.ThrowsAsync<CredentialException>(() => f.Put(scope))).Code);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Missing_original_proof_key_is_not_replaced_by_configured_new_key_and_never_publishes_candidate()
    {
        using var f = new Fixture(); f.Fault("PendingPersisted"); await Assert.ThrowsAsync<CredentialException>(() => f.Put()); await f.Reconcile(CredentialRollbackResolution.AbandonUnpublished);
        var old = f.Cell.Keys.Values["key-1"]; f.Cell.Keys.Values.Remove("key-1"); f.Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32);
        var retry = f.Reopen("key-2"); var before = f.Snapshot(); f.Cell.Keys.Requests.Clear();
        Assert.Equal("credential-key-unavailable", (await Assert.ThrowsAsync<CredentialException>(() => f.Put(vault: retry))).Code);
        Assert.Equal("key-1", Assert.Single(f.Cell.Keys.Requests).KeyId); Assert.Equal(before, f.Snapshot());
        f.Cell.Keys.Values["key-1"] = old; Assert.Equal(1, (await f.Put(vault: retry)).Generation);
    }

    [WindowsCredentialFact]
    public async Task Rehashed_scope_tamper_cannot_forge_original_MAC_or_advance_reconciliation()
    {
        using var f = new Fixture(); f.Fault("PendingPersisted"); await Assert.ThrowsAsync<CredentialException>(() => f.Put());
        var state = f.State(); var proof = f.Entry(state)["absenceProof"]!.AsObject(); proof["scope"]!["projectId"] = "different-project";
        proof["scopeSha256"] = Hash(Encoding.UTF8.GetBytes(proof["scope"]!.ToJsonString(Json))); f.Rehash(state, proof);
        var before = f.Snapshot(); f.Cell.Keys.Requests.Clear();
        Assert.Equal("credential-absence-proof-authentication-failed", (await Assert.ThrowsAsync<CredentialException>(() => f.Reconcile(CredentialRollbackResolution.AbandonUnpublished))).Code);
        Assert.Single(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot()); Assert.False(File.Exists(f.ProfilePath));
    }

    [WindowsCredentialFact]
    public async Task Legitimate_cross_scope_AES_replay_with_rehashed_retry_pending_is_refused_before_scope_authority_advances()
    {
        using var f = new Fixture(); f.Fault("EnvelopePublished"); await Assert.ThrowsAsync<CredentialException>(() => f.Put());
        var other = await f.ForeignEnvelope(); File.WriteAllBytes(f.ProfilePath, other); f.SetPendingCandidate(other); var before = f.Snapshot();
        Assert.Equal("credential-absence-proof-scope-changed", (await Assert.ThrowsAsync<CredentialException>(() => f.Reconcile(CredentialRollbackResolution.AcceptPublished))).Code);
        Assert.Equal(before, f.Snapshot()); Assert.Equal(0, f.Entry()["generation"]!.GetValue<int>());
    }

    [WindowsCredentialFact]
    public async Task Source_key_await_cannot_swap_authenticated_bytes_for_another_valid_pending_candidate()
    {
        using var f = new Fixture(); f.Fault("EnvelopePublished"); await Assert.ThrowsAsync<CredentialException>(() => f.Put()); var other = await f.ForeignEnvelope();
        f.SetPendingCandidate(other); var stateBefore = File.ReadAllBytes(f.StatePath); var calls = 0;
        f.Cell.Keys.OnGet = () => { if (++calls == 2) File.WriteAllBytes(f.ProfilePath, other); };
        Assert.Equal("credential-rollback-envelope-mismatch", (await Assert.ThrowsAsync<CredentialException>(() => f.Reconcile(CredentialRollbackResolution.AcceptPublished))).Code);
        Assert.Equal(stateBefore, File.ReadAllBytes(f.StatePath)); Assert.Equal(0, f.Entry()["generation"]!.GetValue<int>());
        f.Cell.Keys.OnGet = null;
    }

    [WindowsCredentialFact]
    public async Task Accepting_authentic_first_publication_removes_absence_proof_and_normal_revoke_rewrap_remain_supported()
    {
        using var f = new Fixture(); f.Fault("EnvelopePublished"); await Assert.ThrowsAsync<CredentialException>(() => f.Put());
        await f.Reconcile(CredentialRollbackResolution.AcceptPublished); f.Fault(null); Assert.Null(f.Entry()["absenceProof"]);
        Assert.Equal(1, (await f.Vault.GetProfileAsync(f.Scope.AuthProfileId)).Generation);
        Assert.False((await f.Vault.RevokeAsync(f.Scope.AuthProfileId, 1)).Enabled); f.Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32);
        var after = await f.Reopen("key-2").RewrapAsync(f.Scope.AuthProfileId, 2, "key-1"); Assert.Equal(3, after.Generation); Assert.False(after.Enabled);
        Assert.Null(f.Entry()["absenceProof"]); Assert.Equal(3, f.Entry()["generation"]!.GetValue<int>());
    }

    [WindowsCredentialFact]
    public async Task Cancellation_after_original_key_lookup_retains_proof_and_guard_can_be_used_again()
    {
        using var f = new Fixture(); f.Fault("PendingPersisted"); await Assert.ThrowsAsync<CredentialException>(() => f.Put()); await f.Reconcile(CredentialRollbackResolution.AbandonUnpublished);
        f.Fault(null); using var cancellation = new CancellationTokenSource(); var before = f.Snapshot();
        f.Cell.Keys.OnGet = cancellation.Cancel;
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Put(cancellationToken: cancellation.Token));
        Assert.Equal(before, f.Snapshot()); f.Cell.Keys.OnGet = null; Assert.Equal(1, (await f.Put()).Generation);
    }

    static void SetFault(CredentialVault vault, string? phase) => typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.Instance | BindingFlags.NonPublic)!
        .SetValue(vault, phase is null ? null : (Action<string>)(actual => { if (actual == phase) throw new CredentialException("synthetic-scoped-interruption"); }));
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    sealed class Fixture : IDisposable
    {
        internal CredentialFixture Cell { get; } = new();
        internal CredentialScope Scope => Cell.Scope;
        internal string Authority => Path.Combine(Cell.Root, "scoped-authority");
        internal string StatePath => Path.Combine(Authority, "state.json");
        internal string Hash => CredentialScopedRollbackCoreTests.Hash(Encoding.UTF8.GetBytes(Scope.AuthProfileId));
        internal string ProfilePath => Path.Combine(Options.RootPath, Hash + ".credential.json");
        internal CredentialVaultOptions Options { get; }
        internal CredentialVault Vault { get; private set; } = null!;
        internal Fixture(bool provision = true)
        {
            Cell.Clock.Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
            Options = Cell.Options with { RootPath = Path.Combine(Cell.Root, "scoped-vault"), RollbackProtection = new(Authority) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 } };
            if (provision) Provision();
        }
        internal void Provision() => Vault = CredentialVault.ProvisionStrongScoped(Options, Cell.Keys, Cell.Clock);
        internal CredentialVault Reopen(string? key = null) => new(Options with { EncryptionKeyId = key ?? Options.EncryptionKeyId }, Cell.Keys, Cell.Clock);
        internal void Fault(string? phase) => SetFault(Vault, phase);
        internal async Task<CredentialProfile> Put(CredentialScope? scope = null, CredentialVault? vault = null, CancellationToken cancellationToken = default)
        { using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token); return await (vault ?? Vault).PutAsync(scope ?? Scope, secret, Cell.Clock.Now.AddHours(1), cancellationToken: cancellationToken); }
        internal async Task<CredentialRollbackStatus> Reconcile(CredentialRollbackResolution disposition)
        {
            var current = await Vault.InspectRollbackStateAsync(Scope.AuthProfileId);
            return await Vault.ReconcilePendingWriteAsync(Scope.AuthProfileId, current.AuthorityRevision, current.AuthorityStateSha256, current.Pending!.PendingSha256, disposition);
        }
        internal JsonObject State() => JsonNode.Parse(File.ReadAllText(StatePath))!.AsObject();
        internal JsonObject Entry(JsonObject? state = null) => (state ?? State())["profiles"]![Hash]!.AsObject();
        internal JsonObject Proof() => Entry()["absenceProof"]!.AsObject();
        internal async Task<byte[]> ForeignEnvelope()
        {
            var vault = CredentialVault.Provision(Cell.Options with { RootPath = Path.Combine(Cell.Root, "foreign-scope-vault") }, Cell.Keys, Cell.Clock);
            using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token);
            await vault.PutAsync(Scope with { ProjectId = "other-scope", EnvironmentId = "prod" }, secret, Cell.Clock.Now.AddHours(1));
            return File.ReadAllBytes(Path.Combine(Cell.Root, "foreign-scope-vault", Hash + ".credential.json"));
        }
        internal void SetPendingCandidate(byte[] bytes)
        {
            var state = State(); var pending = Entry(state)["pending"]!; pending["transactionId"] = Guid.NewGuid().ToString("N");
            pending["newEnvelopeSha256"] = CredentialScopedRollbackCoreTests.Hash(bytes); pending["newEnvelopeLength"] = bytes.Length;
            var typed = pending.Deserialize<CredentialRollbackPending>(Json)!;
            pending["pendingSha256"] = (string)Rollback().GetType().GetMethod("PendingDigest", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(Rollback(), [Hash, typed])!;
            Rehash(state);
        }
        object Rollback() => typeof(CredentialVault).GetField("rollback", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Vault)!;
        internal void Rehash(JsonObject state, JsonObject? proof = null)
        {
            var assembly = typeof(CredentialVault).Assembly;
            if (proof is not null)
            {
                var type = assembly.GetType("SelfService.Foundation.CredentialAbsenceScopeProof")!;
                var value = JsonSerializer.Deserialize(proof.ToJsonString(Json), type, Json)!;
                proof["sha256"] = (string)assembly.GetType("SelfService.Foundation.CredentialAbsenceScopeProofs")!
                    .GetMethod("Digest", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [value])!;
            }
            File.WriteAllText(StatePath, state.ToJsonString(Json)); var rollback = Rollback(); var typeOfAuthority = rollback.GetType();
            var parsed = typeOfAuthority.GetMethod("ReadScopedState", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(rollback, null)!;
            var binding = typeOfAuthority.GetField("binding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rollback)!;
            state["sha256"] = (string)typeOfAuthority.GetMethod("ScopedStateDigest", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [parsed, binding])!;
            File.WriteAllText(StatePath, state.ToJsonString(Json));
        }
        internal string[] Snapshot() => Directory.GetFiles(Options.RootPath).Concat(Directory.GetFiles(Authority)).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Cell.Root, path) + ":" + CredentialScopedRollbackCoreTests.Hash(File.ReadAllBytes(path))).ToArray();
        public void Dispose() => Cell.Dispose();
    }
}
