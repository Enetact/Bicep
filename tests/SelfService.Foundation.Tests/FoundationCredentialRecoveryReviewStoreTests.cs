using System.Collections;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationCredentialRecoveryReviewStoreTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [WindowsCredentialFact]
    public async Task Prepared_and_independently_approved_review_roundtrips_exact_public_bindings_without_key_access()
    {
        using var f = new Fixture(); await f.Initialize(); f.ClearKeys(); var request = await f.Prepare(); var approved = await f.Approve(request);
        var history = await f.Read(request.Id);
        Assert.Equal("Prepared", request.State); Assert.Equal("Approved", history.State); Assert.Equal(approved.Digest, history.Digest);
        Assert.Equal(f.Plan.Sha256, history.Target.PlanSha256); Assert.Equal(f.Receipt.Sha256, history.Target.ReceiptSha256);
        Assert.Equal(f.Set.Sha256, history.Target.SourceSetSha256); Assert.Equal(f.Plan.DestinationKey, history.Target.DestinationKey);
        Assert.Equal(f.Store.GetCellInstanceIdentity().DatabaseInstanceId, history.DatabaseInstanceId); Assert.Null(history.Execution);
        Assert.Equal(MaintenanceExecutorFixture.Operator, history.RequesterKey); Assert.Equal(MaintenanceExecutorFixture.Approver, history.ApproverKey);
        Assert.Single(history.Target.Scopes); Assert.Throws<NotSupportedException>(() => ((IList)history.Target.Scopes).Clear());
        Assert.Empty(f.Base.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests);
        Assert.Equal(1, f.Base.Count("CredentialRecoveryReviewPrepared")); Assert.Equal(1, f.Base.Count("CredentialRecoveryReviewApproved"));
        var json = JsonSerializer.Serialize(history, Json); Assert.DoesNotContain(f.Base.Cell.Root, json); Assert.DoesNotContain(CredentialFixture.Token, json);
        Assert.DoesNotContain(f.Base.Cell.Scope.AuthProfileId, json); Assert.DoesNotContain("ciphertext", json, StringComparison.OrdinalIgnoreCase);
    }

    [WindowsCredentialFact]
    public async Task One_use_admission_and_all_scope_audit_are_committed_before_destination_key_and_success_stays_held()
    {
        using var f = new Fixture(); await f.Initialize(); var request = await f.Prepare(); await f.Approve(request); f.ClearKeys();
        f.Destination.OnGet = () =>
        {
            Assert.Equal(1, f.Base.Scalar("SELECT count(*) FROM credential_recovery_stage_review_executions WHERE state='OutcomeUnknown'"));
            Assert.Equal(1, f.Base.Count("CredentialRecoveryReviewAdmitted"));
        };
        var result = await f.Execute(request); Assert.Equal("Succeeded", result.Execution!.State); Assert.Single(f.Destination.Requests); Assert.Empty(f.Base.Cell.Keys.Requests);
        Assert.Equal("HeldStageVerified", result.Execution.Result!.Status); Assert.True(result.Execution.Result.InactiveRecoveryHold);
        Assert.False(result.Execution.Result.PromotionAuthorized); Assert.False(result.Execution.Result.LiveAdoptionAuthorized);
        Assert.False(result.Execution.Result.ActivationPerformed); Assert.False(result.Execution.Result.ExternalExecutionAuthorized);
        f.ClearKeys(); await Assert.ThrowsAsync<FoundationStoreException>(() => f.Execute(request));
        Assert.Empty(f.Destination.Requests); Assert.Equal(1, f.Base.Scalar("SELECT count(*) FROM credential_recovery_stage_review_executions"));
        Assert.Equal(1, f.Base.Count("CredentialRecoveryReviewCompleted"));
    }

    [WindowsCredentialFact]
    public async Task Review_approval_admission_and_completed_result_bindings_are_sql_immutable_and_retained()
    {
        using var f = new Fixture(); await f.Initialize(); var request = await f.Prepare(); await f.Approve(request); await f.Execute(request);
        foreach (var sql in new[]
        {
            "UPDATE credential_recovery_stage_review_requests SET requester_version=requester_version+1",
            "UPDATE credential_recovery_stage_review_requests SET target_sha256='changed'",
            "UPDATE credential_recovery_stage_review_requests SET state='Prepared'",
            "DELETE FROM credential_recovery_stage_review_requests",
            "UPDATE credential_recovery_stage_review_approvals SET digest='changed'",
            "DELETE FROM credential_recovery_stage_review_approvals",
            "UPDATE credential_recovery_stage_review_executions SET executor_version=executor_version+1",
            "UPDATE credential_recovery_stage_review_executions SET reason='changed'",
            "DELETE FROM credential_recovery_stage_review_executions"
        }) Assert.Throws<SqliteException>(() => f.Base.Exec(sql));
        Assert.Equal("Succeeded", (await f.Read(request.Id)).Execution!.State);
    }

    [WindowsCredentialFact]
    public async Task Static_read_integrity_refuses_changed_target_commitment_without_consulting_stage_or_keys()
    {
        using var f = new Fixture(); await f.Initialize(); var request = await f.Prepare();
        f.Base.Exec("DROP TRIGGER credential_recovery_review_requests_immutable; UPDATE credential_recovery_stage_review_requests SET target_sha256='" + new string('a', 64) + "'");
        f.ClearKeys(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryRead);
        Assert.Equal("RecoveryStageReviewIntegrityFailed", Assert.Throws<FoundationStoreException>(() => f.Store.GetCredentialRecoveryStageReview(contexts, request.Id)).Code);
        Assert.Empty(f.Destination.Requests); Assert.Empty(f.Base.Cell.Keys.Requests);
    }

    [WindowsCredentialFact]
    public async Task Static_read_integrity_refuses_a_changed_success_result_without_relabeling_it_as_valid_history()
    {
        using var f = new Fixture(); await f.Initialize(); var request = await f.Prepare(); await f.Approve(request); await f.Execute(request);
        f.Base.Exec("DROP TRIGGER credential_recovery_review_success_immutable; UPDATE credential_recovery_stage_review_executions SET result_sha256='" + new string('b', 64) + "'");
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryRead);
        Assert.Equal("RecoveryStageReviewIntegrityFailed", Assert.Throws<FoundationStoreException>(() => f.Store.GetCredentialRecoveryStageReview(contexts, request.Id)).Code);
    }

    [WindowsCredentialFact]
    public async Task Current_approver_grant_change_blocks_new_admission_and_destination_key_access()
    {
        using var f = new Fixture(); await f.Initialize(); var request = await f.Prepare(); await f.Approve(request);
        f.Store.SetMembership(f.Customer, "admin", MaintenanceExecutorFixture.Approver, "default", "dev", []); f.ClearKeys();
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Execute(request));
        Assert.Empty(f.Destination.Requests); Assert.Equal(0, f.Base.Scalar("SELECT count(*) FROM credential_recovery_stage_review_executions"));
    }

    [WindowsCredentialFact]
    public async Task Fresh_current_review_can_verify_historical_completed_stage_after_its_owner_plan_and_source_signature_expire()
    {
        using var f = new Fixture(); await f.Initialize(); f.Base.Cell.Clock.Now = f.Base.Cell.Clock.Now.AddHours(2);
        var request = await f.Prepare(); await f.Approve(request); var completed = await f.Execute(request);
        Assert.Equal("Succeeded", completed.Execution!.State); Assert.True(completed.Execution.Result!.InactiveRecoveryHold); Assert.False(completed.Execution.Result.PromotionAuthorized);
    }

    [WindowsCredentialFact]
    public async Task Pending_review_quota_refuses_atomically_without_key_access_or_extra_prepared_audit()
    {
        using var f = new Fixture(); await f.Initialize(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare);
        for (var i = 0; i < FoundationStore.MaximumPendingCredentialRecoveryStageReviews; i++)
            f.Store.PrepareCredentialRecoveryStageReview(contexts, f.Plan, f.Receipt, "Synthetic bounded pending recovery review", f.Base.Cell.Clock.Now.AddMinutes(5));
        f.ClearKeys();
        Assert.Equal("RecoveryStageReviewPendingCapacityExceeded", Assert.Throws<FoundationStoreException>(() =>
            f.Store.PrepareCredentialRecoveryStageReview(contexts, f.Plan, f.Receipt, "Synthetic pending overflow", f.Base.Cell.Clock.Now.AddMinutes(5))).Code);
        Assert.Equal(64, f.Base.Count("CredentialRecoveryReviewPrepared")); Assert.Equal(64, f.Base.Scalar("SELECT count(*) FROM credential_recovery_stage_review_requests")); Assert.Empty(f.Destination.Requests);
    }

    [WindowsCredentialFact]
    public async Task Expired_prepared_review_remains_integrity_checked_history_and_cannot_gain_a_fresh_approval()
    {
        using var f = new Fixture(); await f.Initialize(); var request = await f.Prepare(); f.Base.Cell.Clock.Now = f.Base.Cell.Clock.Now.AddMinutes(6);
        Assert.Equal("Prepared", (await f.Read(request.Id)).State); await Assert.ThrowsAsync<FoundationStoreException>(() => f.Approve(request));
        Assert.Equal(0, f.Base.Scalar("SELECT count(*) FROM credential_recovery_stage_review_approvals"));
    }

    [WindowsCredentialFact]
    public async Task Preparation_uses_one_validated_cutoff_despite_later_clock_discontinuity_and_returns_readable_history()
    {
        foreach (var forward in new[] { false, true })
        {
            using var f = new Fixture(); await f.Initialize();
            var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare);
            var now = f.Base.Cell.Clock.Now;
            var clock = new PreparationBoundaryClock(now, now.AddMinutes(forward ? 11 : -11));
            var store = new FoundationStore(Path.Combine(f.Base.Cell.Root, "cell.db"), f.Base.Cell.Scope.CellId, f.Customer, clock, initializeSchema: false);
            f.ClearKeys(); clock.Begin();
            var request = store.PrepareCredentialRecoveryStageReview(contexts, f.Plan, f.Receipt, "Synthetic frozen preparation cutoff", now.AddMinutes(5));
            Assert.True(clock.ObservedDiscontinuity); Assert.Equal(now, request.PreparedAt);
            Assert.Equal(request.Digest, (await f.Read(request.Id)).Digest); Assert.Equal(1, f.Base.Count("CredentialRecoveryReviewPrepared"));
            Assert.Empty(f.Base.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests);
        }
    }

    [WindowsCredentialFact]
    public async Task Full_256_scope_target_exceeds_generic_payload_size_but_all_scopes_and_audits_remain_required()
    {
        using var f = new Fixture(); await f.Base.Initialize();
        for (var i = 1; i < 256; i++)
        {
            var project = "p" + new string('x', 118) + i.ToString("D3"); var environment = "e" + new string('y', 118) + i.ToString("D3");
            var admin = "scope-admin-" + i; var onboard = f.Store.PrepareScopeOnboarding(f.Customer, "admin", project, environment, admin, "Synthetic full scoped review");
            f.Store.ApproveAdministrativePlan(f.Customer, "approver", onboard.Id, onboard.Digest); f.Store.ExecuteScopeOnboarding(f.Customer, "admin", onboard.Id, onboard.Digest);
            foreach (var pair in new[] { (MaintenanceExecutorFixture.Operator, "Operator"), (MaintenanceExecutorFixture.Approver, "Approver"), (MaintenanceExecutorFixture.Auditor, "Auditor") })
                f.Store.SetMembership(f.Customer, admin, pair.Item1, project, environment, [pair.Item2]);
            using var material = CredentialSecret.FromUtf8(CredentialFixture.Token);
            await f.Base.Source.PutAsync(f.Base.Cell.Scope with { ProjectId = project, EnvironmentId = environment, AuthProfileId = "profile/full-review-" + i }, material, f.Base.Cell.Clock.Now.AddHours(1));
        }
        await f.FreezeStage(); var request = await f.Prepare();
        Assert.Equal(256, request.Target.Scopes.Count); Assert.True(Encoding.UTF8.GetByteCount(JsonSerializer.Serialize(request.Target, Json)) > 65536);
        Assert.Equal(256, f.Base.Count("CredentialRecoveryReviewPrepared")); await f.Approve(request);
        Assert.Equal(256, f.Base.Count("CredentialRecoveryReviewApproved")); var done = await f.Execute(request);
        Assert.Equal("Succeeded", done.Execution!.State); Assert.Equal(256, f.Base.Count("CredentialRecoveryReviewAdmitted")); Assert.Equal(256, f.Base.Count("CredentialRecoveryReviewCompleted"));
        Assert.Equal(256, (await f.Read(request.Id)).Target.Scopes.Count);
    }

    sealed class PreparationBoundaryClock(DateTimeOffset initial, DateTimeOffset discontinuity) : TimeProvider
    {
        bool armed; int calls;
        internal bool ObservedDiscontinuity { get; private set; }
        internal void Begin() { calls = 0; armed = true; }
        public override DateTimeOffset GetUtcNow()
        {
            if (armed && ++calls > 2) { ObservedDiscontinuity = true; return discontinuity; }
            return initial;
        }
    }

    sealed class Fixture : IDisposable
    {
        internal MaintenanceExecutorFixture Base { get; } = new();
        internal FoundationStore Store => Base.Store;
        internal string Customer => Base.Cell.Scope.CustomerId;
        readonly IdentityTestIssuer issuer = new(); readonly RSA signer = RSA.Create(2048);
        internal CredentialTestKeys Destination { get; } = new();
        internal CredentialRecoverySet Set { get; private set; } = null!;
        internal CredentialRecoveryStagePlan Plan { get; private set; } = null!;
        internal CredentialRecoveryStageReceipt Receipt { get; private set; } = null!;
        internal IReadOnlyList<FoundationCredentialRecoveryScope> Scopes { get; private set; } = [];
        internal Fixture() { Base.Cell.Clock.Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z"); Destination.Values["review-destination"] = RandomNumberGenerator.GetBytes(32); }
        internal async Task Initialize() { await Base.Initialize(); await FreezeStage(); }
        internal async Task FreezeStage()
        {
            var ns = Base.Source.DescribeNamespace(); Set = await Base.Source.CaptureSignedRecoverySetAsync(ns, signer, "review-signer", 1, Base.Cell.Clock.Now, Base.Cell.Clock.Now.AddHours(1));
            var authority = CredentialRecoverySets.Authority(Set); var selected = CredentialRecoverySets.Verify(Set, authority, signer, ns, Base.Cell.Clock);
            Scopes = selected.Inventory.ProfileDependencies.Select(p => new FoundationCredentialRecoveryScope(p.ProjectId, p.EnvironmentId)).Distinct().OrderBy(s => s.ProjectId, StringComparer.Ordinal).ThenBy(s => s.EnvironmentId, StringComparer.Ordinal).ToArray();
            var destination = await CredentialRecoveryStaging.ObserveDestinationKeyAsync(Customer, Base.Cell.Scope.CellId, "review-destination", Destination);
            Plan = CredentialRecoveryStaging.PreparePlan(selected, authority, ns, Path.Combine(Base.Cell.Root, "held-store-review"), destination,
                "Synthetic complete current review", Base.Cell.Clock.Now.AddMinutes(20), Base.Cell.Clock);
            Receipt = await new CredentialRecoveryStageExecutor(Base.Cell.Keys, Destination, Base.Cell.Clock).StageAsync(Set, Plan, Plan.Sha256, signer);
        }
        internal async Task<IReadOnlyList<CustomerContext>> Contexts(string capability)
        {
            var role = capability == FoundationCapabilities.CredentialRecoveryApprove ? "Approver" : capability == FoundationCapabilities.CredentialRecoveryRead ? "Auditor" : "Operator";
            var subject = role == "Approver" ? IdentityTestIssuer.OtherSubject : role == "Auditor" ? IdentityTestIssuer.WorkerSubject : IdentityTestIssuer.Subject;
            var options = issuer.Options(Customer, Base.Cell.Scope.CellId); var claims = issuer.UserClaims(role, subject);
            claims["iat"] = claims["nbf"] = Base.Cell.Clock.Now.AddSeconds(-30).ToUnixTimeSeconds(); claims["exp"] = Base.Cell.Clock.Now.AddMinutes(10).ToUnixTimeSeconds();
            var principal = await new IdentityTokenValidator(options, Base.Cell.Clock).ValidateAsync(issuer.Token(claims));
            var resolver = new CustomerContextResolver(options, new Memberships(Store), Base.Cell.Clock);
            return Scopes.Select(s => resolver.Require(principal, Customer, s.ProjectId, s.EnvironmentId, capability)).ToArray();
        }
        internal async Task<FoundationCredentialRecoveryStageReview> Prepare() => Store.PrepareCredentialRecoveryStageReview(await Contexts(FoundationCapabilities.CredentialRecoveryPrepare), Plan, Receipt, "Synthetic review of held stage", Base.Cell.Clock.Now.AddMinutes(5));
        internal async Task<FoundationCredentialRecoveryStageReview> Approve(FoundationCredentialRecoveryStageReview request) => Store.ApproveCredentialRecoveryStageReview(await Contexts(FoundationCapabilities.CredentialRecoveryApprove), request.Id, request.Digest);
        internal async Task<FoundationCredentialRecoveryStageReview> Read(string id) => Store.GetCredentialRecoveryStageReview(await Contexts(FoundationCapabilities.CredentialRecoveryRead), id);
        internal async Task<FoundationCredentialRecoveryStageReview> Execute(FoundationCredentialRecoveryStageReview request) => await new FoundationCredentialRecoveryStageReviewCoordinator(Store,
            new CredentialRecoveryStageVerifier(Destination, Base.Cell.Clock), Base.Instance).ExecuteAsync(await Contexts(FoundationCapabilities.CredentialRecoveryVerify), request.Id, request.Digest, Plan, Plan.Sha256, Receipt.Sha256);
        internal void ClearKeys() { Base.Cell.Keys.Requests.Clear(); Destination.Requests.Clear(); }
        public void Dispose() { Set?.Dispose(); signer.Dispose(); issuer.Dispose(); Base.Dispose(); }
        sealed class Memberships(FoundationStore store) : ICustomerMembershipSource
        { public IReadOnlyList<MembershipGrant> Find(string c, string actor) => store.GetCredentialMaintenanceMemberships(c, actor).Select(m => new MembershipGrant(m.CustomerId, m.CellId, m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version, m.Enabled)).ToArray(); }
    }
}
