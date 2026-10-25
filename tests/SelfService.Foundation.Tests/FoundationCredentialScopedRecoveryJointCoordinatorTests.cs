using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationCredentialScopedRecoveryJointCoordinatorTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Exact_approved_stage_is_jointly_verified_at_current_checkpoint_without_source_keys_or_adoption(bool absenceOnly)
    {
        using var f = new JointFixture(); await f.Initialize(absenceOnly); var review = await f.Approved();
        var before = f.ProtectedSnapshot(); f.SourceKeys.Values.Clear(); f.ClearKeys();
        var result = await f.Execute(review);
        Assert.Equal("Succeeded", result.State); Assert.Equal("CurrentCheckpointHeldStageVerified", result.Result!.Status);
        Assert.Equal(f.Head, result.Target.Checkpoint); Assert.Equal(f.Identity, result.Target.CheckpointAuthority);
        Assert.True(result.Result.CheckpointReadLeaseRetainedDuringVerification); Assert.True(result.Result.InactiveRecoveryHold);
        Assert.False(result.Result.PromotionAuthorized); Assert.False(result.Result.LiveAdoptionAuthorized);
        Assert.False(result.Result.ActivationPerformed); Assert.False(result.Result.ExternalExecutionAuthorized);
        Assert.False(result.Result.IndependentKeyBackupVerified); Assert.Equal("NotAssessed", result.Result.IndependentBackupCoverage);
        Assert.Single(f.Destination.Requests); Assert.Empty(f.SourceKeys.Requests); Assert.Equal(before, f.ProtectedSnapshot());
        Assert.Equal("Succeeded", (await f.Read(review.Id)).Execution!.State); Assert.Equal(1, f.JointAdmissions); Assert.Equal(1, f.Admissions);
        var json = JsonSerializer.Serialize(result);
        foreach (var forbidden in new[] { f.Cell.Root, CredentialFixture.Token, "profile/recovery-absence", "\"tag\":", "\"scope\":" }) Assert.DoesNotContain(forbidden, json);
        f.ClearKeys(); await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(review));
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify);
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Legacy().ExecuteAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256));
        Assert.Empty(f.Destination.Requests); Assert.Equal(1, f.JointAdmissions);
    }

    [Theory]
    [InlineData("sequence")] [InlineData("head")] [InlineData("advanced")]
    public async Task Stale_or_changed_current_checkpoint_refuses_before_admission_and_destination_key(string change)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved();
        if (change == "advanced") { using var next = await f.Capture(2); await f.Accept(next); }
        var before = f.ProtectedSnapshot(); f.ClearKeys();
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator().ExecuteApprovedAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256,
            f.Receipt.Sha256, change == "sequence" ? 2 : f.Head.Sequence, change == "head" ? new string('f', 64) : f.Head.Sha256));
        Assert.Equal(0, f.Admissions); Assert.Equal(0, f.JointAdmissions); Assert.Empty(f.Destination.Requests);
        Assert.Empty(f.SourceKeys.Requests); Assert.Equal(before, f.ProtectedSnapshot()); Assert.Null((await f.Read(review.Id)).Execution);
    }

    [WindowsCredentialFact]
    public async Task Both_actual_guards_span_destination_await_and_database_transactions_are_not_held_across_it()
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved(); using var next = await f.Capture(2);
        var paused = new PausedKeys(f.Destination); var operation = f.Execute(review, f.Coordinator(paused));
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Equal(1, f.Admissions); Assert.Equal(1, f.JointAdmissions);
            Assert.Throws<IOException>(() => new FileStream(f.StageGuard, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            var error = await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(next)); Assert.Equal("CredentialScopedRecoveryCheckpointBusy", error.Code);
            using (var db = f.Open()) using (var tx = db.BeginTransaction(deferred: false)) tx.Commit();
            paused.Release.TrySetResult(); Assert.Equal("Succeeded", (await operation).State);
        }
        finally { paused.Release.TrySetResult(); }
        using (var released = new FileStream(f.StageGuard, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Assert.Equal(2, (await f.Accept(next)).Sequence);
    }

    [WindowsCredentialFact]
    public async Task Uncooperative_canceled_destination_retains_both_guards_until_real_return_then_records_consumed_unknown()
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved(); using var next = await f.Capture(2);
        var paused = new PausedKeys(f.Destination, cooperative: false); using var caller = new CancellationTokenSource();
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify); var before = f.ProtectedSnapshot();
        var operation = f.Coordinator(paused).ExecuteApprovedAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256, f.Head.Sequence, f.Head.Sha256, caller.Token);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); caller.Cancel(); await Task.Delay(50); Assert.False(operation.IsCompleted);
            Assert.Throws<IOException>(() => new FileStream(f.StageGuard, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            Assert.Equal("CredentialScopedRecoveryCheckpointBusy", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(next))).Code);
            paused.Release.TrySetResult(); var result = await operation; Assert.Equal("OutcomeUnknown", result.State); Assert.Null(result.Result);
        }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(before, f.ProtectedSnapshot()); Assert.Equal("OutcomeUnknown", (await f.Read(review.Id)).Execution!.State);
        Assert.Equal(2, (await f.Accept(next)).Sequence); f.ClearKeys(); await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(review)); Assert.Empty(f.Destination.Requests);
    }

    [Theory]
    [InlineData("checkpoint")] [InlineData("stage")]
    public async Task Final_external_custody_tamper_after_authentication_cannot_publish_either_success(string change)
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved(); var coordinator = f.Coordinator();
        Fault(coordinator, phase =>
        {
            if (phase != "AfterVerification") return;
            if (change == "checkpoint") File.WriteAllBytes(Path.Combine(f.CheckpointRoot, CredentialScopedRecoverySetCheckpointAuthority.MarkerName), "{}"u8.ToArray());
            else f.PrivateStageFile("unexpected.json", "{}"u8.ToArray());
        });
        var result = await f.Execute(review, coordinator); Assert.Equal("OutcomeUnknown", result.State); Assert.Null(result.Result);
        Assert.Equal("OutcomeUnknown", (await f.Read(review.Id)).Execution!.State); Assert.Equal(1, f.JointAdmissions); Assert.Single(f.Destination.Requests);
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_scoped_recovery_joint_executions WHERE state='Succeeded'"));
        if (change == "stage") Assert.True(File.Exists(Path.Combine(f.StageRoot, "unexpected.json")));
        else Assert.Equal("{}"u8.ToArray(), File.ReadAllBytes(Path.Combine(f.CheckpointRoot, CredentialScopedRecoverySetCheckpointAuthority.MarkerName)));
    }

    [WindowsCredentialFact]
    public async Task Missing_one_scope_is_denied_before_private_checkpoint_or_stage_access()
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved(); var coordinator = f.Coordinator();
        var incomplete = (await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify)).Take(1).ToArray(); f.ClearKeys();
        using (var checkpoint = new FileStream(f.CheckpointGuard, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
        using (var stage = new FileStream(f.StageGuard, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<FoundationStoreException>(() => coordinator.ExecuteApprovedAsync(incomplete, review.Id, review.Digest,
                f.Plan, f.Plan.Sha256, f.Receipt.Sha256, f.Head.Sequence, f.Head.Sha256));
        Assert.Equal(0, f.JointAdmissions); Assert.Equal(0, f.Admissions); Assert.Empty(f.Destination.Requests); Assert.Empty(f.SourceKeys.Requests);
    }

    [WindowsCredentialFact]
    public async Task Current_standing_authority_revocation_after_destination_lookup_records_unknown_under_both_guards()
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved(); var paused = new PausedKeys(f.Destination);
        var operation = f.Execute(review, f.Coordinator(paused));
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); f.Grant(IdentityTestIssuer.OtherSubject, "Approver", enabled: false);
            paused.Release.TrySetResult(); Assert.Equal("OutcomeUnknown", (await operation).State);
        }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal("OutcomeUnknown", (await f.Read(review.Id)).Execution!.State); Assert.Equal(1, f.JointAdmissions); Assert.Single(f.Destination.Requests);
    }

    [WindowsCredentialFact]
    public async Task Failed_unknown_audit_releases_both_guards_and_retains_both_default_unknown_admissions()
    {
        using var f = new JointFixture(); await f.Initialize(); var review = await f.Approved(); var coordinator = f.Coordinator();
        f.Sql("CREATE TRIGGER reject_joint_unknown BEFORE INSERT ON audit WHEN NEW.action='CredentialScopedRecoveryJointUnknown' BEGIN SELECT RAISE(ABORT,'synthetic audit refusal'); END;");
        Fault(coordinator, phase => { if (phase == "AfterAdmission") throw new IOException("synthetic interrupted path " + f.Cell.Root); });
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Execute(review, coordinator));
        Assert.Equal("credential-scoped-recovery-joint-review-outcome-unknown", error.Code); Assert.DoesNotContain(f.Cell.Root, error.ToString());
        Assert.Equal(1, f.JointAdmissions); Assert.Equal("OutcomeUnknown", (await f.Read(review.Id)).Execution!.State); Assert.Empty(f.Destination.Requests);
        using (var stage = new FileStream(f.StageGuard, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        using (var checkpoint = new FileStream(f.CheckpointGuard, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
    }

    static void Fault(FoundationCredentialScopedRecoveryJointReviewCoordinator coordinator, Action<string> callback) =>
        typeof(FoundationCredentialScopedRecoveryJointReviewCoordinator).GetProperty("ReviewFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, callback);

    sealed class PausedKeys(ICredentialKeyProvider inner, bool cooperative = true) : ICredentialKeyProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken token)
        { Entered.TrySetResult(); if (cooperative) await Release.Task.WaitAsync(token); else await Release.Task; return await inner.GetKeyAsync(customer, cell, key, cooperative ? token : CancellationToken.None); }
    }

    internal sealed class JointFixture : IDisposable
    {
        const string RequesterSubject = "dddddddd-dddd-dddd-dddd-dddddddddddd";
        readonly IdentityTestIssuer issuer = new(); readonly RSA signer = RSA.Create(2048);
        public CredentialFixture Cell { get; } = new();
        public CredentialTestKeys SourceKeys => Cell.Keys;
        public CredentialTestKeys Destination { get; } = new();
        public FoundationStore Store { get; }
        public CredentialVault Source { get; }
        public CredentialScopedRecoverySet Set { get; private set; } = null!;
        public CredentialScopedRecoverySetCheckpointAuthority Checkpoints { get; private set; } = null!;
        public CredentialScopedRecoverySetCheckpointAuthorityIdentity Identity { get; private set; } = null!;
        public CredentialScopedRecoverySetCheckpoint Head { get; private set; } = null!;
        public CredentialScopedRecoveryStagePlan Plan { get; private set; } = null!;
        public CredentialScopedRecoveryStageReceipt Receipt { get; private set; } = null!;
        public string StageRoot => Child("joint-stage");
        public string CheckpointRoot => Child("joint-checkpoint");
        public string StageGuard => Path.Combine(StageRoot, ".credential-recovery-stage.guard");
        public string CheckpointGuard => Path.Combine(CheckpointRoot, CredentialScopedRecoverySetCheckpointAuthority.GuardName);
        string Child(string name) => Path.Combine(Cell.Root, name);
        public JointFixture()
        {
            Cell.Clock.Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z"); Store = new(Child("cell.db"), Cell.Scope.CellId, Cell.Scope.CustomerId, Cell.Clock);
            var onboarding = Store.PrepareScopeOnboarding(Cell.Scope.CustomerId, "admin", "recovery-absence", "qa", "absence-admin", "Synthetic recovery scope");
            Store.ApproveAdministrativePlan(Cell.Scope.CustomerId, "approver", onboarding.Id, onboarding.Digest); Store.ExecuteScopeOnboarding(Cell.Scope.CustomerId, "admin", onboarding.Id, onboarding.Digest);
            Grant(RequesterSubject, "Operator"); Grant(IdentityTestIssuer.Subject, "Operator"); Grant(IdentityTestIssuer.OtherSubject, "Approver");
            Source = CredentialVault.ProvisionStrongScoped(Cell.Options with { RootPath = Child("joint-source"),
                RollbackProtection = new(Child("joint-source-authority")) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 } }, SourceKeys, Cell.Clock);
            Destination.Values.Clear(); Destination.Values.Add("joint-destination", RandomNumberGenerator.GetBytes(32));
        }
        public void Grant(string subject, string role, bool enabled = true)
        {
            var actor = "user:" + IdentityTestIssuer.Tenant + ":" + subject;
            Store.SetMembership(Cell.Scope.CustomerId, "admin", actor, "default", "dev", [role], enabled);
            Store.SetMembership(Cell.Scope.CustomerId, "absence-admin", actor, "recovery-absence", "qa", [role], enabled);
        }
        public async Task Initialize(bool absenceOnly = false)
        {
            using var material = CredentialSecret.FromUtf8(CredentialFixture.Token);
            var absent = Cell.Scope with { AuthProfileId = "profile/recovery-absence", ProjectId = "recovery-absence", EnvironmentId = "qa" };
            var fault = typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.Instance | BindingFlags.NonPublic)!;
            fault.SetValue(Source, (Action<string>)(phase => { if (phase == "PendingPersisted") throw new IOException("Synthetic unpublished source"); }));
            try { await Assert.ThrowsAsync<IOException>(() => Source.PutAsync(absent, material, Cell.Clock.Now.AddHours(1))); }
            finally { fault.SetValue(Source, null); }
            var pending = await Source.InspectRollbackStateAsync(absent.AuthProfileId);
            await Source.ReconcilePendingWriteAsync(absent.AuthProfileId, pending.AuthorityRevision, pending.AuthorityStateSha256, pending.Pending!.PendingSha256, CredentialRollbackResolution.AbandonUnpublished);
            if (!absenceOnly) await Source.PutAsync(Cell.Scope, material, Cell.Clock.Now.AddHours(1));
            Set = await Capture(); var authority = CredentialScopedRecoverySets.Authority(Set); var identity = Source.DescribeNamespace();
            Checkpoints = CredentialScopedRecoverySetCheckpointAuthority.Provision(CheckpointRoot, identity, authority.SignerKeyId, authority.PublicKeySha256, Cell.Clock);
            Identity = Checkpoints.DescribeIdentity(); Head = await Accept(Set);
            var verified = CredentialScopedRecoverySets.Verify(Set, authority, signer, identity, Cell.Clock);
            var destination = await CredentialScopedRecoveryStaging.ObserveDestinationKeyAsync(identity.CustomerId, identity.CellId, "joint-destination", Destination);
            Plan = CredentialScopedRecoveryStaging.PreparePlan(verified, authority, identity, StageRoot, destination, "Synthetic joint held evidence", Cell.Clock.Now.AddMinutes(20), Cell.Clock);
            Receipt = (await new CredentialScopedRecoveryCheckpointStageExecutor(Checkpoints, Identity, new CredentialScopedRecoveryStageExecutor(SourceKeys, Destination, Cell.Clock))
                .StageLatestAsync(Set, Plan, Plan.Sha256, signer, Head.Sequence, Head.Sha256)).StageReceipt;
            ClearKeys();
        }
        public Task<CredentialScopedRecoverySet> Capture(long generation = 1) => Source.CaptureScopedSignedRecoverySetAsync(Source.DescribeNamespace(), signer,
            "joint-signer", generation, Cell.Clock.Now, Cell.Clock.Now.AddHours(1));
        public async Task<CredentialScopedRecoverySetCheckpoint> Accept(CredentialScopedRecoverySet set)
        { var current = Checkpoints.ReadCurrent(); return await Checkpoints.AcceptNextAsync(set, set.Sha256, signer, SourceKeys, current?.Sequence ?? 0, current?.Sha256); }
        public FoundationCredentialScopedRecoveryStageReviewCoordinator Legacy() => new(Store, new(Destination, Cell.Clock), Store.GetCellInstanceIdentity().DatabaseInstanceId);
        public FoundationCredentialScopedRecoveryJointReviewCoordinator Coordinator(ICredentialKeyProvider? keys = null) =>
            new(Store, Checkpoints, Identity, new(keys ?? Destination, Cell.Clock), Store.GetCellInstanceIdentity().DatabaseInstanceId);
        public async Task<FoundationCredentialScopedRecoveryStageReview> Approved()
        {
            var review = await Legacy().PrepareAsync(await Contexts(FoundationCapabilities.CredentialRecoveryPrepare), Plan, Plan.Sha256, Receipt.Sha256,
                "Synthetic approved exact inactive stage", Cell.Clock.Now.AddMinutes(5));
            return Store.ApproveCredentialScopedRecoveryStageReview(await Contexts(FoundationCapabilities.CredentialRecoveryApprove), review.Id, review.Digest);
        }
        public async Task<FoundationCredentialScopedRecoveryJointVerification> Execute(FoundationCredentialScopedRecoveryStageReview review,
            FoundationCredentialScopedRecoveryJointReviewCoordinator? coordinator = null) => await (coordinator ?? Coordinator()).ExecuteApprovedAsync(
                await Contexts(FoundationCapabilities.CredentialRecoveryVerify), review.Id, review.Digest, Plan, Plan.Sha256, Receipt.Sha256, Head.Sequence, Head.Sha256);
        public async Task<FoundationCredentialScopedRecoveryStageReview> Read(string id) => Store.GetCredentialScopedRecoveryStageReview(await Contexts(FoundationCapabilities.CredentialRecoveryRead), id);
        public async Task<IReadOnlyList<CustomerContext>> Contexts(string capability)
        {
            var approve = capability == FoundationCapabilities.CredentialRecoveryApprove;
            var subject = approve ? IdentityTestIssuer.OtherSubject : capability == FoundationCapabilities.CredentialRecoveryPrepare ? RequesterSubject : IdentityTestIssuer.Subject;
            var options = issuer.Options(Cell.Scope.CustomerId, Cell.Scope.CellId); var claims = issuer.UserClaims(approve ? "Approver" : "Operator", subject);
            claims["iat"] = claims["nbf"] = Cell.Clock.Now.AddSeconds(-30).ToUnixTimeSeconds(); claims["exp"] = Cell.Clock.Now.AddMinutes(10).ToUnixTimeSeconds();
            var principal = await new IdentityTokenValidator(options, Cell.Clock).ValidateAsync(issuer.Token(claims));
            var resolver = new CustomerContextResolver(options, new Memberships(Store), Cell.Clock);
            return Plan.AffectedScopes.Select(scope => resolver.Require(principal, Cell.Scope.CustomerId, scope.ProjectId, scope.EnvironmentId, capability)).ToArray();
        }
        public long Admissions => Number("SELECT count(*) FROM credential_scoped_recovery_stage_review_executions");
        public long JointAdmissions => Number("SELECT count(*) FROM credential_scoped_recovery_joint_executions");
        public long Number(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        public void Sql(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public SqliteConnection Open() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Child("cell.db"), Pooling = false, DefaultTimeout = 1 }.ToString()); db.Open(); return db; }
        public void ClearKeys() { SourceKeys.Requests.Clear(); Destination.Requests.Clear(); }
        public string[] ProtectedSnapshot() => new[] { StageRoot, CheckpointRoot }.SelectMany(root => Directory.GetFiles(root, "*", SearchOption.AllDirectories)).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Cell.Root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        public void PrivateStageFile(string name, byte[] bytes)
        {
            using var current = System.Security.Principal.WindowsIdentity.GetCurrent(); var security = new System.Security.AccessControl.FileSecurity();
            security.SetOwner(current.User!); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new System.Security.AccessControl.FileSystemAccessRule(current.User!, System.Security.AccessControl.FileSystemRights.FullControl, System.Security.AccessControl.AccessControlType.Allow));
            using var file = new FileInfo(Path.Combine(StageRoot, name)).Create(FileMode.CreateNew, System.Security.AccessControl.FileSystemRights.FullControl, FileShare.Read, 4096, FileOptions.WriteThrough, security);
            file.Write(bytes); file.Flush(true);
        }
        public void Dispose() { Set?.Dispose(); signer.Dispose(); issuer.Dispose(); Cell.Dispose(); }
        sealed class Memberships(FoundationStore store) : ICustomerMembershipSource
        { public IReadOnlyList<MembershipGrant> Find(string customer, string actor) => store.GetCredentialMaintenanceMemberships(customer, actor)
            .Select(m => new MembershipGrant(m.CustomerId, m.CellId, m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version, m.Enabled)).ToArray(); }
    }
}
