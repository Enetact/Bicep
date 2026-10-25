using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialScopedRecoveryCheckpointStageTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub)]
    [InlineData(PipelineRequestFactory.AzureDevOps)]
    public async Task Current_configured_checkpoint_produces_exact_inactive_stage_without_changing_source_or_authority(string provider)
    {
        using var f = new Fixture(provider: provider); await f.Put(); await f.Put(f.Cell.Scope with { AuthProfileId = "profile/checkpoint-other", ProjectId = "other-project" });
        await f.Vault.RevokeAsync(f.Cell.Scope.AuthProfileId, 1); f.Cell.Clock.Now = f.Cell.Clock.Now.AddHours(2);
        using var set = await f.Capture(); var head = await f.Accept(set); var plan = await f.Plan(set); var before = f.Snapshot(); f.ClearKeys(); using var key = f.Public();
        var result = await f.Wrapper().StageLatestAsync(set, plan, plan.Sha256, key, head.Sequence, head.Sha256);
        Assert.Equal(head, result.Checkpoint); Assert.Equal(f.Identity, result.AuthorityIdentity); Assert.Equal(plan.Sha256, result.StageReceipt.PlanSha256);
        Assert.Equal(set.Sha256, result.StageReceipt.SourceSetSha256); Assert.Equal(2, result.StageReceipt.Profiles.Count); Assert.Single(f.Destination.Requests);
        Assert.NotEmpty(f.Cell.Keys.Requests); Assert.All(f.Cell.Keys.Requests, r => Assert.Equal("key-1", r.KeyId));
        Assert.Equal(before, f.Snapshot(f.StagePath)); AssertLimits(result, f);
        var verified = await new CredentialScopedRecoveryStageVerifier(f.Destination, f.Cell.Clock).VerifyAsync(plan, plan.Sha256, result.StageReceipt.Sha256);
        Assert.Equal(2, verified.AuthenticatedProfileCount); Assert.Contains(result.StageReceipt.Profiles, p => !p.Destination.Enabled);
        Assert.All(result.StageReceipt.Profiles, p => Assert.True(p.Destination.ExpiresUnixMilliseconds < f.Cell.Clock.Now.ToUnixTimeMilliseconds()));
    }

    [WindowsCredentialFact]
    public async Task Empty_latest_set_does_not_mint_profiles_or_scope_proofs()
    {
        using var f = new Fixture(); using var set = await f.Capture(); var head = await f.Accept(set); var plan = await f.Plan(set); var before = f.Snapshot(); f.ClearKeys(); using var key = f.Public();
        var result = await f.Wrapper().StageLatestAsync(set, plan, plan.Sha256, key, head.Sequence, head.Sha256);
        Assert.Empty(result.StageReceipt.Profiles); Assert.Empty(result.StageReceipt.Absences); Assert.Empty(f.Cell.Keys.Requests); Assert.Single(f.Destination.Requests);
        Assert.Equal(before, f.Snapshot(f.StagePath)); AssertLimits(result, f);
    }

    [WindowsCredentialFact]
    public async Task Authenticated_committed_absence_keeps_its_scoped_source_provenance_without_minting_review_authority()
    {
        using var f = new Fixture(); await f.Put(); var absent = f.Cell.Scope with { AuthProfileId = "profile/checkpoint-absent" };
        typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Vault,
            (Action<string>)(phase => { if (phase == "PendingPersisted") throw new IOException("Synthetic abandoned write."); }));
        await Assert.ThrowsAsync<IOException>(() => f.Put(absent)); typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Vault, null);
        var pending = await f.Vault.InspectRollbackStateAsync(absent.AuthProfileId);
        await f.Vault.ReconcilePendingWriteAsync(absent.AuthProfileId, pending.AuthorityRevision, pending.AuthorityStateSha256, pending.Pending!.PendingSha256, CredentialRollbackResolution.AbandonUnpublished);
        using var set = await f.Capture(); var head = await f.Accept(set); var plan = await f.Plan(set); var before = f.Snapshot(); using var key = f.Public();
        var result = await f.Wrapper().StageLatestAsync(set, plan, plan.Sha256, key, head.Sequence, head.Sha256);
        Assert.Single(result.StageReceipt.Profiles); Assert.Equal(new[] { Hash(Encoding.UTF8.GetBytes(absent.AuthProfileId)) }, result.StageReceipt.Absences.Select(a => a.Source.ProfileIdSha256));
        Assert.Equal(plan.AbsenceScopes, result.StageReceipt.Absences.Select(a => a.Source)); Assert.Equal(before, f.Snapshot(f.StagePath)); Assert.False(result.CustomerReviewAdmissionPerformed);
    }

    [WindowsCredentialFact]
    public async Task Absence_only_latest_set_produces_an_actual_destination_authenticated_scope_proof_without_live_adoption()
    {
        using var f = new Fixture(); var absent = f.Cell.Scope with { AuthProfileId = "profile/checkpoint-only-absence", ProjectId = "absent-project", EnvironmentId = "prod" };
        var fault = typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.Instance | BindingFlags.NonPublic)!;
        fault.SetValue(f.Vault, (Action<string>)(phase => { if (phase == "PendingPersisted") throw new IOException("Synthetic interrupted initial write."); }));
        try { await Assert.ThrowsAsync<IOException>(() => f.Put(absent)); } finally { fault.SetValue(f.Vault, null); }
        var pending = await f.Vault.InspectRollbackStateAsync(absent.AuthProfileId);
        await f.Vault.ReconcilePendingWriteAsync(absent.AuthProfileId, pending.AuthorityRevision, pending.AuthorityStateSha256, pending.Pending!.PendingSha256, CredentialRollbackResolution.AbandonUnpublished);
        using var set = await f.Capture(); var head = await f.Accept(set); var plan = await f.Plan(set); var before = f.Snapshot(); using var key = f.Public();
        var result = await f.Wrapper().StageLatestAsync(set, plan, plan.Sha256, key, head.Sequence, head.Sha256);
        Assert.Empty(result.StageReceipt.Profiles); var proof = Assert.Single(result.StageReceipt.Absences);
        Assert.Equal("checkpoint-destination", proof.Destination.SourceKeyId); Assert.NotEqual(proof.Source.ProofSha256, proof.Destination.ProofSha256);
        var verified = await new CredentialScopedRecoveryStageVerifier(f.Destination, f.Cell.Clock).VerifyAsync(plan, plan.Sha256, result.StageReceipt.Sha256);
        Assert.Equal(0, verified.AuthenticatedProfileCount); Assert.Equal(1, verified.AuthenticatedAbsenceCount);
        Assert.True(verified.DestinationAbsenceProofAuthenticationPerformed); Assert.False(verified.EncryptedProfileAuthenticationPerformed);
        Assert.Equal(before, f.Snapshot(f.StagePath)); Assert.Equal(new[] { new FoundationCredentialRecoveryScope("absent-project", "prod") }, plan.AffectedScopes);
        AssertLimits(result, f);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Old_still_valid_set_and_its_own_plan_cannot_override_the_newest_accepted_head(bool supplyCurrentHead)
    {
        using var f = new Fixture(); await f.Put(); using var old = await f.Capture(); var first = await f.Accept(old); var oldPlan = await f.Plan(old);
        using var latest = await f.Capture(2); var current = await f.Accept(latest); using var publicKey = f.Public();
        _ = CredentialScopedRecoverySets.Verify(old, CredentialScopedRecoverySets.Authority(old), publicKey, f.SourceNamespace, f.Cell.Clock); // The older set remains valid for explicit scoped exact-set selection.
        var before = f.Snapshot(); f.ClearKeys();
        var operation = f.Wrapper().StageLatestAsync(old, oldPlan, oldPlan.Sha256, publicKey, supplyCurrentHead ? current.Sequence : first.Sequence, supplyCurrentHead ? current.Sha256 : first.Sha256);
        if (supplyCurrentHead) Assert.Equal("credential-scoped-recovery-checkpoint-stage-selection-mismatch", (await Assert.ThrowsAsync<CredentialException>(() => operation)).Code);
        else Assert.Equal("CredentialScopedRecoveryCheckpointGenerationChanged", (await Assert.ThrowsAsync<FoundationStoreException>(() => operation)).Code);
        Assert.False(Path.Exists(f.StagePath)); Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("customer")] [InlineData("cell")] [InlineData("source")] [InlineData("authority")]
    [InlineData("signer")] [InlineData("public-key")] [InlineData("commitment")]
    public void Configured_authority_identity_is_an_exact_independent_constraint(string field)
    {
        using var f = new Fixture(); var identity = f.Identity; var wrong = field switch
        {
            "customer" => identity with { CustomerId = Guid.NewGuid().ToString() }, "cell" => identity with { CellId = Guid.NewGuid().ToString() },
            "source" => identity with { SourceNamespace = identity.SourceNamespace with { VaultNamespaceId = Guid.NewGuid().ToString("N") } },
            "authority" => identity with { AuthorityId = Guid.NewGuid().ToString("N") }, "signer" => identity with { SignerKeyId = "other-signer" },
            "public-key" => identity with { PublicKeySha256 = new string('a', 64) }, _ => identity with { CommitmentSha256 = new string('b', 64) }
        };
        var before = f.Snapshot(); Assert.Equal("credential-scoped-recovery-checkpoint-stage-identity-mismatch", Assert.Throws<CredentialException>(() => new CredentialScopedRecoveryCheckpointStageExecutor(f.Authority, wrong, f.Executor())).Code);
        Assert.Equal(before, f.Snapshot()); Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests);
    }

    [WindowsCredentialFact]
    public async Task Empty_checkpoint_refuses_before_caller_RSA_source_keys_or_output_access()
    {
        using var f = new Fixture(); await f.Put(); using var set = await f.Capture(); var plan = await f.Plan(set); var before = f.Snapshot(); f.ClearKeys(); var key = RSA.Create(2048); key.Dispose();
        Assert.Equal("CredentialScopedRecoveryCheckpointMissing", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Wrapper().StageLatestAsync(set, plan, plan.Sha256, key, 1, new string('a', 64)))).Code);
        Assert.False(Path.Exists(f.StagePath)); Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("sequence")] [InlineData("head")] [InlineData("plan-pin")] [InlineData("signer")]
    public async Task Wrong_reviewed_inputs_never_resolve_credential_keys_or_reserve_a_destination(string input)
    {
        using var f = new Fixture(); await f.Put(); using var set = await f.Capture(); var head = await f.Accept(set); var plan = await f.Plan(set); var before = f.Snapshot(); f.ClearKeys();
        using var key = input == "signer" ? RSA.Create(2048) : f.Public();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Wrapper().StageLatestAsync(set, plan, input == "plan-pin" ? new string('a', 64) : plan.Sha256, key,
            input == "sequence" ? head.Sequence + 1 : head.Sequence, input == "head" ? new string('b', 64) : head.Sha256));
        Assert.False(Path.Exists(f.StagePath)); Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("source")] [InlineData("destination")]
    public async Task Actual_checkpoint_read_lease_blocks_append_through_every_external_key_await(string dependency)
    {
        using var f = new Fixture(); await f.Put(); using var set = await f.Capture(); var head = await f.Accept(set); using var next = await f.Capture(2); var plan = await f.Plan(set);
        var paused = new PausedKeys(dependency == "source" ? f.Cell.Keys : f.Destination); var wrapper = f.Wrapper(dependency == "source" ? paused : null, dependency == "destination" ? paused : null);
        var before = f.Snapshot(); f.ClearKeys(); using var key = f.Public(); var operation = wrapper.StageLatestAsync(set, plan, plan.Sha256, key, head.Sequence, head.Sha256);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(Path.Exists(f.StagePath));
            var requests = f.Cell.Keys.Requests.Count; Assert.Equal("CredentialScopedRecoveryCheckpointBusy", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(next))).Code);
            Assert.Equal(requests, f.Cell.Keys.Requests.Count); Assert.Equal(head, f.Authority.ReadCurrent()); paused.Release.TrySetResult(); Assert.Equal(head, (await operation).Checkpoint);
        }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(before, f.Snapshot(f.StagePath)); Assert.Equal(2, (await f.Accept(next)).Sequence);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Cancellation_holds_uncooperative_dependency_and_checkpoint_until_the_real_await_finishes(bool uncooperative)
    {
        using var f = new Fixture(); await f.Put(); using var set = await f.Capture(); var head = await f.Accept(set); using var next = await f.Capture(2); var plan = await f.Plan(set); var before = f.Snapshot();
        var paused = new PausedKeys(f.Destination, cooperative: !uncooperative); using var key = f.Public(); using var caller = new CancellationTokenSource();
        var operation = f.Wrapper(destination: paused).StageLatestAsync(set, plan, plan.Sha256, key, head.Sequence, head.Sha256, caller.Token);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
            if (uncooperative) { await Task.Delay(50); Assert.False(operation.IsCompleted); Assert.Equal("CredentialScopedRecoveryCheckpointBusy", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(next))).Code); }
            paused.Release.TrySetResult(); var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); Assert.Equal(caller.Token, error.CancellationToken);
        }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(before, f.Snapshot()); Assert.False(Path.Exists(f.StagePath)); Assert.Equal(2, (await f.Accept(next)).Sequence);
    }

    [Theory]
    [InlineData("source")] [InlineData("destination")]
    public async Task Credential_provider_failure_preserves_checkpoint_source_and_absent_destination(string dependency)
    {
        using var f = new Fixture(); await f.Put(); using var set = await f.Capture(); var head = await f.Accept(set); var plan = await f.Plan(set); var before = f.Snapshot(); using var key = f.Public();
        var wrapper = f.Wrapper(dependency == "source" ? new FailedKeys(f.Cell.Root) : null, dependency == "destination" ? new FailedKeys(f.Cell.Root) : null);
        var error = await Assert.ThrowsAsync<CredentialException>(() => wrapper.StageLatestAsync(set, plan, plan.Sha256, key, head.Sequence, head.Sha256));
        Assert.DoesNotContain(f.Cell.Root, error.ToString()); Assert.DoesNotContain(CredentialFixture.Token, error.ToString()); Assert.Equal(before, f.Snapshot()); Assert.False(Path.Exists(f.StagePath));
    }

    [WindowsCredentialFact]
    public async Task Expired_plan_after_dependency_await_cannot_publish_and_checkpoint_is_unchanged()
    {
        using var f = new Fixture(); await f.Put(); using var set = await f.Capture(); var head = await f.Accept(set); var plan = await f.Plan(set); var before = f.Snapshot();
        var paused = new PausedKeys(f.Destination); using var key = f.Public(); var operation = f.Wrapper(destination: paused).StageLatestAsync(set, plan, plan.Sha256, key, head.Sequence, head.Sha256);
        try { await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.Cell.Clock.Now = plan.ExpiresUtc; paused.Release.TrySetResult(); await Assert.ThrowsAsync<CredentialException>(() => operation); }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(before, f.Snapshot()); Assert.False(Path.Exists(f.StagePath)); Assert.Equal(head, f.Authority.ReadCurrent());
    }

    [WindowsCredentialFact]
    public async Task Completed_stage_cannot_hide_checkpoint_tamper_at_the_final_custody_recheck()
    {
        using var f = new Fixture(); await f.Put(); using var set = await f.Capture(); var head = await f.Accept(set); var plan = await f.Plan(set); var wrapper = f.Wrapper(); using var key = f.Public();
        var marker = Path.Combine(f.AuthorityPath, CredentialScopedRecoverySetCheckpointAuthority.MarkerName); byte[] changed = "{}"u8.ToArray();
        f.Destination.OnGet = () => File.WriteAllBytes(marker, changed); // Explicit same-owner guard bypass is detected, never repaired.
        var error = await Assert.ThrowsAsync<CredentialException>(() => wrapper.StageLatestAsync(set, plan, plan.Sha256, key, head.Sequence, head.Sha256));
        Assert.Equal("credential-scoped-recovery-checkpoint-stage-outcome-unknown", error.Code); Assert.True(File.Exists(Path.Combine(f.StagePath, "completion.json"))); Assert.Equal(changed, File.ReadAllBytes(marker));
        var completion = JsonSerializer.Deserialize<JsonElement>(File.ReadAllBytes(Path.Combine(f.StagePath, "completion.json"))); Assert.True(completion.GetProperty("inactiveRecoveryHold").GetBoolean());
    }

    [WindowsCredentialFact]
    public async Task Cancellation_before_read_lease_reads_no_keys_and_preserves_the_existing_authority_and_absent_stage()
    {
        using var f = new Fixture(); await f.Put(); using var set = await f.Capture(); var head = await f.Accept(set); var plan = await f.Plan(set); var before = f.Snapshot(); f.ClearKeys(); using var key = f.Public(); using var caller = new CancellationTokenSource(); caller.Cancel();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Wrapper().StageLatestAsync(set, plan, plan.Sha256, key, head.Sequence, head.Sha256, caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken); Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests); Assert.False(Path.Exists(f.StagePath)); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Existing_destination_is_never_adopted_or_repaired_by_checkpoint_backed_staging()
    {
        using var f = new Fixture(); await f.Put(); using var set = await f.Capture(); var head = await f.Accept(set); var plan = await f.Plan(set);
        Directory.CreateDirectory(f.StagePath); File.WriteAllText(Path.Combine(f.StagePath, "preserved-owner.txt"), "Synthetic preserved owner destination.");
        var before = f.Snapshot(); f.ClearKeys(); using var key = f.Public(); await Assert.ThrowsAsync<CredentialException>(() => f.Wrapper().StageLatestAsync(set, plan, plan.Sha256, key, head.Sequence, head.Sha256));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Inner_failure_after_fresh_reservation_retains_partial_stage_and_does_not_advance_checkpoint()
    {
        using var f = new Fixture(); await f.Put(); using var set = await f.Capture(); var head = await f.Accept(set); var plan = await f.Plan(set); var before = f.Snapshot();
        var clock = new ReservationExpiryClock(f.Cell.Clock.Now, plan.ExpiresUtc, f.StagePath);
        var wrapper = new CredentialScopedRecoveryCheckpointStageExecutor(f.Authority, f.Identity, new(f.Cell.Keys, f.Destination, clock)); using var key = f.Public();
        var error = await Assert.ThrowsAsync<CredentialException>(() => wrapper.StageLatestAsync(set, plan, plan.Sha256, key, head.Sequence, head.Sha256));
        Assert.True(clock.ObservedReservation); Assert.Equal("credential-scoped-recovery-stage-outcome-unknown", error.Code);
        Assert.Equal(new[] { ".credential-recovery-stage.guard" }, Directory.GetFileSystemEntries(f.StagePath).Select(p => Path.GetFileName(p)!).ToArray());
        Assert.Equal(before, f.Snapshot(f.StagePath)); Assert.Equal(head, f.Authority.ReadCurrent());
    }

    [WindowsCredentialFact]
    public async Task Live_source_namespace_can_be_removed_after_acceptance_without_current_vault_reads()
    {
        using var f = new Fixture(); await f.Put(); using var set = await f.Capture(); var head = await f.Accept(set); var plan = await f.Plan(set);
        Directory.Move(f.Options.RootPath, Path.Combine(f.Cell.Root, "moved-original-vault")); Directory.Move(f.Options.RollbackProtection!.AuthorityDirectory, Path.Combine(f.Cell.Root, "moved-original-authority"));
        var before = f.Snapshot(); using var key = f.Public(); var result = await f.Wrapper().StageLatestAsync(set, plan, plan.Sha256, key, head.Sequence, head.Sha256);
        Assert.Single(result.StageReceipt.Profiles); Assert.Equal(before, f.Snapshot(f.StagePath)); Assert.False(Path.Exists(f.Options.RootPath)); AssertLimits(result, f);
    }

    [WindowsCredentialFact]
    public async Task Borrowed_caller_RSA_may_be_disposed_after_first_key_await_without_changing_the_pinned_signer()
    {
        using var f = new Fixture(); await f.Put(); using var set = await f.Capture(); var head = await f.Accept(set); var plan = await f.Plan(set); using var key = f.Public();
        f.Destination.OnGet = key.Dispose;
        var result = await f.Wrapper().StageLatestAsync(set, plan, plan.Sha256, key, head.Sequence, head.Sha256); Assert.Single(result.StageReceipt.Profiles); Assert.Equal(head, result.Checkpoint);
    }

    static void AssertLimits(CredentialScopedRecoveryCheckpointStageResult result, Fixture f)
    {
        Assert.True(result.CheckpointReadLeaseRetainedDuringStaging); Assert.True(result.InactiveRecoveryHold); Assert.False(result.NormalVaultUsable); Assert.False(result.PromotionAuthorized);
        Assert.False(result.ActivationPerformed); Assert.False(result.NamespaceAdopted); Assert.False(result.SourceHostFenced); Assert.False(result.ExternalExecutionAuthorized); Assert.False(result.KeyRetirementAuthorized);
        Assert.False(result.IndependentKeyBackupVerified); Assert.False(result.CustomerReviewAdmissionPerformed); Assert.False(result.ProductionRecoveryQualified); Assert.Equal("NotAssessed", result.IndependentBackupCoverage);
        var text = JsonSerializer.Serialize(result, new JsonSerializerOptions(JsonSerializerDefaults.Web)); Assert.DoesNotContain(f.Cell.Root, text); Assert.DoesNotContain(CredentialFixture.Token, text);
        Assert.DoesNotContain(f.Cell.Scope.AuthProfileId, text); Assert.DoesNotContain("\"ciphertext\":", text, StringComparison.OrdinalIgnoreCase); Assert.Matches("^[a-f0-9]{64}$", result.Sha256);
    }
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    sealed class ReservationExpiryClock(DateTimeOffset initial, DateTimeOffset expired, string stage) : TimeProvider
    {
        public bool ObservedReservation { get; private set; }
        public override DateTimeOffset GetUtcNow() { if (Directory.Exists(stage)) { ObservedReservation = true; return expired; } return initial; }
    }
    sealed class PausedKeys(ICredentialKeyProvider inner, bool cooperative = true) : ICredentialKeyProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken token)
        { Entered.TrySetResult(); if (cooperative) await Release.Task.WaitAsync(token); else await Release.Task; return await inner.GetKeyAsync(customer, cell, key, cooperative ? token : CancellationToken.None); }
    }
    sealed class FailedKeys(string path) : ICredentialKeyProvider
    { public ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken token) => throw new IOException("Synthetic private provider at " + path + CredentialFixture.Token); }
    sealed class Fixture : IDisposable
    {
        public CredentialFixture Cell { get; }
        public CredentialVaultOptions Options { get; }
        public CredentialVault Vault { get; }
        public CredentialVaultNamespaceIdentity SourceNamespace { get; }
        public CredentialTestKeys Destination { get; } = new();
        public RSA Signer { get; } = RSA.Create(2048);
        public CredentialScopedRecoverySetCheckpointAuthority Authority { get; }
        public CredentialScopedRecoverySetCheckpointAuthorityIdentity Identity { get; }
        public string AuthorityPath => Path.Combine(Cell.Root, "credential-set-checkpoint");
        public string StagePath => Path.Combine(Cell.Root, "checkpoint-latest-held-stage");
        public Fixture(string provider = PipelineRequestFactory.GitHub)
        {
            Cell = new(provider); Cell.Clock.Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
            Options = Cell.Options with { RootPath = Path.Combine(Cell.Root, "checkpoint-source-vault"), RollbackProtection = new(Path.Combine(Cell.Root, "checkpoint-source-authority")) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 } };
            Vault = CredentialVault.ProvisionStrongScoped(Options, Cell.Keys, Cell.Clock); SourceNamespace = Vault.DescribeNamespace();
            Destination.Values.Clear(); Destination.Values["checkpoint-destination"] = RandomNumberGenerator.GetBytes(32);
            Authority = CredentialScopedRecoverySetCheckpointAuthority.Provision(AuthorityPath, SourceNamespace, "checkpoint-signer", Hash(Signer.ExportSubjectPublicKeyInfo()), Cell.Clock); Identity = Authority.DescribeIdentity();
        }
        public async Task Put(CredentialScope? scope = null) { using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token); await Vault.PutAsync(scope ?? Cell.Scope, secret, Cell.Clock.Now.AddHours(1)); }
        public Task<CredentialScopedRecoverySet> Capture(long generation = 1) => Vault.CaptureScopedSignedRecoverySetAsync(SourceNamespace, Signer, "checkpoint-signer", generation, Cell.Clock.Now, Cell.Clock.Now.AddHours(1));
        public async Task<CredentialScopedRecoverySetCheckpoint> Accept(CredentialScopedRecoverySet set)
        { var head = Authority.ReadCurrent(); using var key = Public(); return await Authority.AcceptNextAsync(set, set.Sha256, key, Cell.Keys, head?.Sequence ?? 0, head?.Sha256); }
        public RSA Public() { var key = RSA.Create(); key.ImportSubjectPublicKeyInfo(Signer.ExportSubjectPublicKeyInfo(), out _); return key; }
        public async Task<CredentialScopedRecoveryStagePlan> Plan(CredentialScopedRecoverySet set)
        {
            using var key = Public(); var authority = CredentialScopedRecoverySets.Authority(set); var selected = CredentialScopedRecoverySets.Verify(set, authority, key, SourceNamespace, Cell.Clock);
            var destination = await CredentialRecoveryStaging.ObserveDestinationKeyAsync(Cell.Scope.CustomerId, Cell.Scope.CellId, "checkpoint-destination", Destination);
            return CredentialScopedRecoveryStaging.PreparePlan(selected, authority, SourceNamespace, StagePath, destination, "Synthetic latest-checkpoint inactive staging", Cell.Clock.Now.AddMinutes(10), Cell.Clock);
        }
        public CredentialScopedRecoveryStageExecutor Executor(ICredentialKeyProvider? source = null, ICredentialKeyProvider? destination = null) => new(source ?? Cell.Keys, destination ?? Destination, Cell.Clock);
        public CredentialScopedRecoveryCheckpointStageExecutor Wrapper(ICredentialKeyProvider? source = null, ICredentialKeyProvider? destination = null) => new(Authority, Identity, Executor(source, destination));
        public void ClearKeys() { Cell.Keys.Requests.Clear(); Destination.Requests.Clear(); }
        public string[] Snapshot(string? exclude = null) => Directory.EnumerateFileSystemEntries(Cell.Root, "*", SearchOption.AllDirectories).Prepend(Cell.Root)
            .Where(p => exclude is null || p != exclude && !p.StartsWith(exclude + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(Cell.Root, p) + ":" + (Directory.Exists(p) ? "directory" : new FileInfo(p).Length == 0 ? Hash([]) : Hash(File.ReadAllBytes(p))) + ":" +
                (Directory.Exists(p) ? (FileSystemSecurity)new DirectoryInfo(p).GetAccessControl() : new FileInfo(p).GetAccessControl()).GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access)).ToArray();
        public void Dispose() { Signer.Dispose(); Cell.Dispose(); }
    }
}
