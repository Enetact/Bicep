using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationJobTests
{
    [Fact]
    public void Request_job_audit_and_outbox_commit_atomically_and_delivery_is_idempotent()
    {
        using var f = new StoreFixture(); var plan = f.Plan();
        Assert.Equal("ApprovalRequired", Assert.Throws<FoundationStoreException>(() => f.Store.EnqueuePlan(f.Customer, "operator", plan.Id, plan.Digest, "request-1")).Code);
        Assert.Empty(f.Store.ListJobs(f.Customer, "operator")); Assert.Empty(f.Store.PendingOutbox(f.Customer));
        f.Store.ApprovePlan(f.Customer, "approver", plan.Id, plan.Digest);
        var job = f.Store.EnqueuePlan(f.Customer, "operator", plan.Id, plan.Digest, "request-1");
        var reopened = f.Reopen();
        Assert.Equal(job.Id, reopened.EnqueuePlan(f.Customer, "operator", plan.Id, plan.Digest, "request-1").Id);
        Assert.Single(reopened.ListJobs(f.Customer, "operator"));
        var message = Assert.Single(reopened.PendingOutbox(f.Customer));
        Assert.Equal(job.Id, message.JobId); Assert.True(reopened.ConsumeOutbox(f.Customer, message.EventId));
        Assert.False(f.Reopen().ConsumeOutbox(f.Customer, message.EventId)); Assert.Empty(reopened.PendingOutbox(f.Customer));
        Assert.Single(reopened.ListAudit(f.Customer, "auditor"), x => x.Action == "JobQueued");
        Assert.Equal("PlanAlreadyQueued", Assert.Throws<FoundationStoreException>(() => reopened.EnqueuePlan(f.Customer, "operator", plan.Id, plan.Digest, "a-different-key")).Code);
        var other = f.Plan(); f.Store.ApprovePlan(f.Customer, "approver", other.Id, other.Digest);
        Assert.Equal("IdempotencyConflict", Assert.Throws<FoundationStoreException>(() => reopened.EnqueuePlan(f.Customer, "operator", other.Id, other.Digest, "request-1")).Code);
    }

    [Fact]
    public void Failure_before_commit_rolls_back_job_outbox_and_audit_together()
    {
        using var f = new StoreFixture(); var plan = f.Plan(); f.Store.ApprovePlan(f.Customer, "approver", plan.Id, plan.Digest);
        var before = f.Scalar("SELECT count(*) FROM audit");
        f.Execute("CREATE TRIGGER crash_before_audit BEFORE INSERT ON audit WHEN NEW.action='JobQueued' BEGIN SELECT RAISE(ABORT,'simulated-process-fault'); END");
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => f.Store.EnqueuePlan(f.Customer, "operator", plan.Id, plan.Digest, "request-1"));
        var reopened = f.Reopen(); Assert.Empty(reopened.ListJobs(f.Customer, "operator")); Assert.Empty(reopened.PendingOutbox(f.Customer));
        Assert.Equal(before, f.Scalar("SELECT count(*) FROM audit"));
        f.Execute("DROP TRIGGER crash_before_audit");
        Assert.Equal("Queued", reopened.EnqueuePlan(f.Customer, "operator", plan.Id, plan.Digest, "request-1").State);
    }

    [Theory]
    [InlineData("requester", "RequesterGrantChanged")]
    [InlineData("approver", "ApproverGrantChanged")]
    [InlineData("operator", "OperatorGrantChanged")]
    public void Dispatch_checks_current_role_versions_even_if_role_is_regranted(string actor, string expected)
    {
        using var f = new StoreFixture(); var job = f.Queue();
        var claim = f.Store.ClaimNext(f.Customer, "worker", TimeSpan.FromMinutes(1))!;
        var role = actor == "requester" ? "Requester" : actor == "approver" ? "Approver" : "Operator";
        f.Store.SetMembership(f.Customer, "admin", actor, "default", "dev", [role]);
        var adapterCalls = 0;
        var error = Assert.Throws<FoundationStoreException>(() => { f.Store.StartDispatch(f.Customer, job.Id, "worker", claim.Fence); adapterCalls++; });
        Assert.Equal(expected, error.Code); Assert.Equal(0, adapterCalls);
        Assert.Equal("Blocked", f.Store.GetJob(f.Customer, "auditor", job.Id).State);
        Assert.Contains(f.Store.ListAudit(f.Customer, "auditor"), e => e.Action == "DispatchDenied" && e.Reason == expected);
    }

    [Theory]
    [InlineData("requester")]
    [InlineData("approver")]
    [InlineData("operator")]
    public void Revoked_membership_blocks_dispatch_before_adapter(string actor)
    {
        using var f = new StoreFixture(); var job = f.Queue(); var claim = f.Store.ClaimNext(f.Customer, "worker", TimeSpan.FromMinutes(1))!;
        f.Store.SetMembership(f.Customer, "admin", actor, "default", "dev", [], false);
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.StartDispatch(f.Customer, job.Id, "worker", claim.Fence)).Code);
        Assert.Equal("Blocked", f.Store.GetJob(f.Customer, "auditor", job.Id).State);
    }

    [Fact]
    public void Connection_generation_or_suspended_customer_cannot_dispatch_stale_plan()
    {
        using var f = new StoreFixture(); var job = f.Queue(); var claim = f.Store.ClaimNext(f.Customer, "worker", TimeSpan.FromMinutes(1))!;
        f.Store.PutConnection(f.Customer, "admin", "pipeline", "github", "{\"repository\":\"new-repository\"}");
        Assert.Equal("ConnectionChanged", Assert.Throws<FoundationStoreException>(() => f.Store.StartDispatch(f.Customer, job.Id, "worker", claim.Fence)).Code);
        var queued = f.Queue("request-2"); f.Store.SetCustomerEnabled(f.Customer, "admin", false);
        Assert.Null(f.Store.ClaimNext(f.Customer, "worker", TimeSpan.FromMinutes(1)));
        Assert.All(f.Store.GetMemberships(f.Customer, "operator"), g => Assert.False(g.Enabled));
        f.Store.SetCustomerEnabled(f.Customer, "admin", true);
        var next = f.Store.ClaimNext(f.Customer, "worker", TimeSpan.FromMinutes(1))!; Assert.Equal(queued.Id, next.Id);
        Assert.Equal("PolicyChanged", Assert.Throws<FoundationStoreException>(() => f.Store.StartDispatch(f.Customer, queued.Id, "worker", next.Fence)).Code);
    }

    [Fact]
    public async Task Two_database_clients_cannot_claim_the_same_job()
    {
        using var f = new StoreFixture(); var queued = f.Queue(); var reopened = f.Reopen();
        var claims = await Task.WhenAll(Task.Run(() => f.Store.ClaimNext(f.Customer, "worker-a", TimeSpan.FromMinutes(1))),
            Task.Run(() => reopened.ClaimNext(f.Customer, "worker-b", TimeSpan.FromMinutes(1))));
        var claim = Assert.Single(claims, c => c is not null); Assert.Equal(queued.Id, claim!.Id); Assert.Equal(1, claim.Fence);
    }

    [Fact]
    public void Crash_before_dispatch_reclaims_with_new_fence_and_rejects_old_worker()
    {
        using var f = new StoreFixture(); var job = f.Queue(); var old = f.Store.ClaimNext(f.Customer, "worker-old", TimeSpan.FromSeconds(2))!;
        f.Clock.Advance(TimeSpan.FromSeconds(3)); var resumed = f.Reopen(); var claim = resumed.ClaimNext(f.Customer, "worker-new", TimeSpan.FromMinutes(1))!;
        Assert.Equal(job.Id, claim.Id); Assert.Equal(old.Fence + 1, claim.Fence); Assert.Equal(2, claim.Attempt);
        Assert.Equal("LeaseConflict", Assert.Throws<FoundationStoreException>(() => f.Store.StartDispatch(f.Customer, job.Id, "worker-old", old.Fence)).Code);
        var dispatch = resumed.StartDispatch(f.Customer, job.Id, "worker-new", claim.Fence);
        Assert.Equal("Dispatched", dispatch.Job.State); Assert.Equal("pipeline", dispatch.Connection.Id);
        var completed = resumed.CompleteJob(f.Customer, job.Id, "worker-new", claim.Fence, "{\"simulated\":true,\"runId\":42}");
        Assert.Equal("Succeeded", completed.State); Assert.Equal("Succeeded", f.Reopen().GetJob(f.Customer, "viewer", job.Id).State);
    }

    [Fact]
    public void Crash_after_dispatch_requires_reconciliation_and_never_replays_changing_effect()
    {
        using var f = new StoreFixture(); var job = f.Queue(); var claim = f.Store.ClaimNext(f.Customer, "worker", TimeSpan.FromSeconds(2))!;
        f.Store.StartDispatch(f.Customer, job.Id, "worker", claim.Fence); f.Clock.Advance(TimeSpan.FromSeconds(3));
        var resumed = f.Reopen(); Assert.Null(resumed.ClaimNext(f.Customer, "new-worker", TimeSpan.FromMinutes(1)));
        Assert.Equal("OutcomeUnknown", resumed.GetJob(f.Customer, "operator", job.Id).State);
        Assert.Equal("LeaseConflict", Assert.Throws<FoundationStoreException>(() => f.Store.CompleteJob(f.Customer, job.Id, "worker", claim.Fence, "{}")).Code);
        Assert.Equal("ReconciliationEvidenceRequired", Assert.Throws<FoundationStoreException>(() => resumed.ReconcileJob(f.Customer, "operator", job.Id, "FailedNoEffect", "{}")).Code);
        Assert.Equal("ReconciledNoEffect", resumed.ReconcileJob(f.Customer, "operator", job.Id, "FailedNoEffect", "{\"simulated\":true,\"providerLookup\":\"no-matching-run\"}").State);
        Assert.Null(resumed.ClaimNext(f.Customer, "new-worker", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Ambiguous_changing_failure_and_cancellation_after_dispatch_preserve_unknown_outcome()
    {
        using var f = new StoreFixture(); var first = f.Queue(); var claim = f.Store.ClaimNext(f.Customer, "worker", TimeSpan.FromMinutes(1))!;
        f.Store.StartDispatch(f.Customer, first.Id, "worker", claim.Fence);
        Assert.Equal("OutcomeUnknown", f.Store.FailJob(f.Customer, first.Id, "worker", claim.Fence, false, "TransportInterrupted").State);
        var second = f.Queue("request-2"); var next = f.Store.ClaimNext(f.Customer, "worker", TimeSpan.FromMinutes(1))!;
        f.Store.StartDispatch(f.Customer, second.Id, "worker", next.Fence);
        Assert.Equal("OutcomeUnknown", f.Store.CancelJob(f.Customer, "operator", second.Id).State);
        Assert.Equal("LeaseConflict", Assert.Throws<FoundationStoreException>(() => f.Store.CompleteJob(f.Customer, second.Id, "worker", next.Fence, "{}")).Code);
        var third = f.Queue("request-3"); Assert.Equal("Cancelled", f.Store.CancelJob(f.Customer, "operator", third.Id).State);
        Assert.Null(f.Store.ClaimNext(f.Customer, "worker", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Expired_plan_and_invalid_leases_never_dispatch()
    {
        using var f = new StoreFixture(); var job = f.Queue();
        Assert.Equal("InvalidLease", Assert.Throws<FoundationStoreException>(() => f.Store.ClaimNext(f.Customer, "worker", TimeSpan.Zero)).Code);
        var claim = f.Store.ClaimNext(f.Customer, "worker", TimeSpan.FromMinutes(5))!;
        f.Clock.Advance(TimeSpan.FromMinutes(21)); var reopened = f.Reopen();
        var next = reopened.ClaimNext(f.Customer, "worker", TimeSpan.FromMinutes(1))!;
        Assert.Equal("PlanExpired", Assert.Throws<FoundationStoreException>(() => reopened.StartDispatch(f.Customer, job.Id, "worker", next.Fence)).Code);
        Assert.True(next.Fence > claim.Fence);
    }

    [Fact]
    public void Repeated_predispatch_worker_crashes_are_bounded_and_audited_as_poison_work()
    {
        using var f = new StoreFixture(); var job = f.Queue();
        for (var attempt = 1; attempt <= FoundationStore.MaximumClaimAttempts; attempt++)
        {
            var claim = f.Reopen().ClaimNext(f.Customer, "worker-" + attempt, TimeSpan.FromSeconds(1))!;
            Assert.Equal(attempt, claim.Attempt); f.Clock.Advance(TimeSpan.FromSeconds(2));
        }
        Assert.Null(f.Store.ClaimNext(f.Customer, "worker-final", TimeSpan.FromMinutes(1)));
        var blocked = f.Store.GetJob(f.Customer, "operator", job.Id);
        Assert.Equal("Blocked", blocked.State); Assert.Equal("ClaimAttemptsExceeded", blocked.Reason);
        Assert.Contains(f.Store.ListAudit(f.Customer, "auditor"), x => x.TargetId == job.Id && x.Reason == "ClaimAttemptsExceeded");
    }
}
