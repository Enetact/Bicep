using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationCredentialRestrictedRecoveryAdoptionCoreTests
{
    [WindowsCredentialFact]
    public async Task Each_intent_phase_uses_one_durable_observation_and_all_scope_audits_without_granting_or_reserving()
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize();
        var clock = new CountingClock(f.Clock.Now); var store = new FoundationStore(f.Database, f.Review.CellId, f.Review.Customer, clock);
        var requesters = await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare, store: store);
        var approvers = await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove, store: store);
        var coordinator = f.Coordinator(store: store); var before = f.ProtectedSnapshot(); f.ClearKeys(); clock.Reset();
        var intent = await f.Prepare(coordinator, requesters);
        Assert.Equal(2, clock.Reads); // One authorization preflight, one frozen durable transaction; audit/final fences do not resample SQL time.
        Assert.Equal(f.Clock.Now, intent.PreparedAt); Assert.Equal(f.SourceBinding, intent.Target.SourceRuntimeBinding);
        Assert.True(intent.Target.RuntimeBindingRequired); Assert.Null(intent.Target.CurrentRuntimeBindingSha256); Assert.Equal(1, intent.Target.RuntimeBindingHistoryCount);
        Assert.Equal(2, f.Number("SELECT count(*) FROM audit WHERE action='CredentialRestrictedRecoveryAdoptionPrepared' AND created=" + intent.PreparedAt.ToUnixTimeMilliseconds()));
        clock.Reset(); var approved = await f.Approve(intent, coordinator, approvers);
        Assert.Equal(2, clock.Reads); Assert.Equal("Approved", approved.State); Assert.NotNull(approved.Approval);
        Assert.Equal(2, f.Number("SELECT count(*) FROM audit WHERE action='CredentialRestrictedRecoveryAdoptionApproved' AND created=" + approved.Approval.ApprovedAt.ToUnixTimeMilliseconds()));
        Assert.Equal("ProvisionFreshRestrictedNamespacesForRestoredInstance", approved.Operation);
        Assert.False(approved.ExecutionAdmissionAvailable); Assert.False(approved.AdoptionAuthorized); Assert.False(approved.ActivationPerformed);
        Assert.False(approved.RecoveryHoldReleased); Assert.False(approved.FreshDestinationReservationPerformed); Assert.False(approved.SourceHostFenced);
        Assert.Equal(before, f.ProtectedSnapshot()); Assert.False(Path.Exists(f.FutureVault)); Assert.False(Path.Exists(f.FutureRollback));
        var expectedKey = (f.Review.Customer, f.Review.CellId, f.Plan.DestinationKey.KeyId);
        Assert.Collection(f.Destination.Requests, request => Assert.Equal(expectedKey, request), request => Assert.Equal(expectedKey, request)); Assert.Empty(f.Cell.Keys.Requests);
        Assert.True(store.VerifyAuditIntegrity(f.Review.Customer, MaintenanceExecutorFixture.Auditor)); Assert.True((await f.Hold()).ExecutionHeld);
    }

    [WindowsCredentialFact]
    public async Task Second_scope_prepare_audit_failure_rolls_back_intent_without_reservation_or_mutating_original_custody()
    {
        using var f = new RestrictedRecoveryAdoptionFixture(); await f.Initialize(); var before = f.ProtectedSnapshot(); var audit = f.Number("SELECT count(*) FROM audit");
        f.Sql("CREATE TRIGGER synthetic_adoption_prepare_audit BEFORE INSERT ON audit WHEN NEW.action='CredentialRestrictedRecoveryAdoptionPrepared' AND NEW.project_id='other' BEGIN SELECT RAISE(ABORT,'synthetic second scope unavailable'); END;");
        var denied = await Assert.ThrowsAsync<CredentialException>(() => f.Prepare());
        Assert.Equal("credential-restricted-recovery-adoption-unavailable", denied.Code);
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_restricted_recovery_adoption_intents"));
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_restricted_recovery_adoption_approvals"));
        Assert.Equal(audit, f.Number("SELECT count(*) FROM audit")); Assert.Equal(before, f.ProtectedSnapshot());
        Assert.False(Path.Exists(f.FutureVault)); Assert.False(Path.Exists(f.FutureRollback)); Assert.True((await f.Hold()).ExecutionHeld);
        Assert.True(f.RestoredStore.VerifyAuditIntegrity(f.Review.Customer, MaintenanceExecutorFixture.Auditor));
    }
    sealed class CountingClock(DateTimeOffset now) : TimeProvider
    {
        public int Reads { get; private set; }
        public void Reset() => Reads = 0;
        public override DateTimeOffset GetUtcNow() { Reads++; return now; }
    }
}
