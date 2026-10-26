using System.Runtime.Versioning;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationCredentialRestrictedRecoveryReviewCoreTests
{
    [WindowsCredentialFact]
    public async Task Prepare_and_approve_use_one_store_observation_for_both_scope_audits_and_persisted_authority_without_keys()
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize();
        var requesters = await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare);
        var approvers = await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove);
        var clock = new CountingClock(f.Clock.Now); var store = new FoundationStore(f.Database, f.CellId, f.Customer, clock);
        clock.Reset(); f.ClearKeys();
        var review = store.PrepareCredentialRestrictedRecoveryStageReview(requesters, f.Plan, f.Receipt, "One observed restricted review transaction", f.Clock.Now.AddMinutes(5));
        Assert.Equal(1, clock.Reads); Assert.Equal(f.Clock.Now, review.PreparedAt);
        Assert.Equal(2, f.Number("SELECT count(*) FROM audit WHERE action='CredentialRestrictedRecoveryReviewPrepared' AND created=" + review.PreparedAt.ToUnixTimeMilliseconds()));
        Assert.Equal(1, f.Number("SELECT count(DISTINCT created) FROM audit WHERE action='CredentialRestrictedRecoveryReviewPrepared'"));
        clock.Reset(); var approved = store.ApproveCredentialRestrictedRecoveryStageReview(approvers, review.Id, review.Digest);
        Assert.Equal(1, clock.Reads); Assert.NotNull(approved.ApprovedAt); Assert.Equal(f.Clock.Now, approved.ApprovedAt.Value);
        Assert.Equal(2, f.Number("SELECT count(*) FROM audit WHERE action='CredentialRestrictedRecoveryReviewApproved' AND created=" + approved.ApprovedAt.Value.ToUnixTimeMilliseconds()));
        Assert.Equal("FoundationBoundDispatchOnly", approved.Target.AccessProfile); Assert.Equal(f.Receipt.CompletionProofSha256, approved.Target.CompletionProofSha256);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.DestinationKeys.Requests);
        Assert.True(f.Store.VerifyAuditIntegrity(f.Customer, MaintenanceExecutorFixture.Auditor));
    }

    [WindowsCredentialFact]
    public async Task Second_scope_audit_failure_rolls_back_prepared_request_and_first_scope_audit_atomically()
    {
        using var f = new RestrictedRecoveryReviewFixture(); await f.Initialize(); var requesters = await f.Contexts(FoundationCapabilities.CredentialRecoveryPrepare);
        var before = f.Number("SELECT count(*) FROM audit"); var contents = f.StageAndSourceSnapshot(); f.ClearKeys();
        f.Sql("CREATE TRIGGER synthetic_restricted_prepare_audit_fault BEFORE INSERT ON audit WHEN NEW.action='CredentialRestrictedRecoveryReviewPrepared' AND NEW.project_id='other' BEGIN SELECT RAISE(ABORT,'synthetic second scope unavailable'); END;");
        Assert.Throws<SqliteException>(() => f.Store.PrepareCredentialRestrictedRecoveryStageReview(requesters, f.Plan, f.Receipt,
            "Synthetic atomic all-scope review", f.Clock.Now.AddMinutes(5)));
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_restricted_recovery_stage_review_requests"));
        Assert.Equal(0, f.Count("CredentialRestrictedRecoveryReviewPrepared")); Assert.Equal(before, f.Number("SELECT count(*) FROM audit"));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(contents, f.StageAndSourceSnapshot());
        Assert.True(f.Store.VerifyAuditIntegrity(f.Customer, MaintenanceExecutorFixture.Auditor));
    }
    sealed class CountingClock(DateTimeOffset now) : TimeProvider
    {
        public int Reads { get; private set; }
        public void Reset() => Reads = 0;
        public override DateTimeOffset GetUtcNow() { Reads++; return now; }
    }
}
