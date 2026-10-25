using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed record FoundationJob(string Id, string CustomerId, string CellId, string PlanId, string ProjectId,
    string EnvironmentId, string OperatorKey, long OperatorVersion, string State, string? WorkerId,
    DateTimeOffset? LeaseUntil, long Fence, int Attempt, string? ReceiptJson, string? Reason,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);
public sealed record FoundationDispatch(FoundationJob Job, FoundationPlan Plan, FoundationConnection Connection);
public sealed record FoundationOutboxEvent(string EventId, string CustomerId, string JobId, string Kind, DateTimeOffset CreatedAt);

public sealed partial class FoundationStore
{
    public const int MaximumPendingJobs = 128;
    public const int MaximumClaimAttempts = 3;

    public FoundationJob EnqueuePlan(string customerId, string operatorKey, string planId, string expectedDigest, string idempotencyKey)
    {
        Scope(customerId); ValidateId(planId); ValidateId(idempotencyKey);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var plan = ReadPlan(db, tx, planId);
        var version = Require(db, tx, operatorKey, plan.ProjectId, plan.EnvironmentId, "Operator");
        MatchDigest(plan.Digest, expectedDigest);
        var existing = Rows(db, tx, "SELECT id,plan_id,operator_key FROM jobs WHERE customer_id=$c AND idempotency_key=$key",
            r => (Id: r.GetString(0), Plan: r.GetString(1), Operator: r.GetString(2)), ("$c", CustomerId), ("$key", idempotencyKey)).SingleOrDefault();
        if (existing.Id is not null)
        {
            if (existing.Plan != planId || existing.Operator != operatorKey) throw new FoundationStoreException("IdempotencyConflict");
            var duplicate = ReadJob(db, tx, existing.Id); tx.Commit(); return duplicate;
        }
        if (Number(db, tx, "SELECT count(*) FROM jobs WHERE customer_id=$c AND plan_id=$plan", ("$c", CustomerId), ("$plan", planId)) != 0)
            throw new FoundationStoreException("PlanAlreadyQueued");
        ValidatePlanAuthority(db, tx, plan);
        if (plan.ChangesExternalState && plan.ApproverKey is null) throw new FoundationStoreException("ApprovalRequired", 403);
        if (Number(db, tx, "SELECT count(*) FROM jobs WHERE customer_id=$c AND state IN ('Queued','Claimed','Dispatched')", ("$c", CustomerId)) >= MaximumPendingJobs)
            throw new FoundationStoreException("QueueCapacityExceeded", 429);
        var id = NewId(); var now = Now; var eventId = NewId();
        Exec(db, tx, """
            INSERT INTO jobs VALUES($c,$id,$plan,$operator,$version,$key,'Queued',NULL,NULL,0,0,NULL,NULL,$now,$now)
            """, ("$c", CustomerId), ("$id", id), ("$plan", planId), ("$operator", operatorKey), ("$version", version),
            ("$key", idempotencyKey), ("$now", now));
        Exec(db, tx, "INSERT INTO outbox VALUES($c,$event,$job,'JobReady',$now,NULL)", ("$c", CustomerId),
            ("$event", eventId), ("$job", id), ("$now", now));
        Audit(db, tx, operatorKey, plan.ProjectId, plan.EnvironmentId, "JobQueued", id, "Accepted", "IntentCommitted", planId);
        var result = ReadJob(db, tx, id); tx.Commit(); return result;
    }

    public FoundationJob GetJob(string customerId, string actorKey, string jobId, string? projectId = null, string? environmentId = null)
    {
        Scope(customerId); ValidateId(jobId); using var db = Open(); var job = ReadJob(db, null, jobId);
        ReadScope(projectId, environmentId, job.ProjectId, job.EnvironmentId);
        Require(db, null, actorKey, job.ProjectId, job.EnvironmentId, ["Viewer", "Requester", "Approver", "Operator", "Auditor"]);
        return job;
    }

    public IReadOnlyList<FoundationJob> ListJobs(string customerId, string actorKey, string projectId = "default", string environmentId = "dev", int limit = 100)
    {
        Scope(customerId); ValidateId(projectId); ValidateId(environmentId);
        if (limit is < 1 or > 1000) throw new FoundationStoreException("InvalidPage", 400);
        using var db = Open(); Require(db, null, actorKey, projectId, environmentId, ["Viewer", "Requester", "Approver", "Operator", "Auditor"]);
        var ids = Rows(db, null, """
            SELECT j.id FROM jobs j JOIN plans p ON p.customer_id=j.customer_id AND p.id=j.plan_id
            WHERE j.customer_id=$c AND p.project_id=$p AND p.environment_id=$e ORDER BY j.created DESC,j.id LIMIT $limit
            """, r => r.GetString(0), ("$c", CustomerId), ("$p", projectId), ("$e", environmentId), ("$limit", limit));
        return ids.Select(id => ReadJob(db, null, id)).ToArray();
    }

    // Internal worker entry points remain cell-bound. The host must not expose them as human API commands.
    public FoundationJob? ClaimNext(string customerId, string workerId, TimeSpan leaseDuration)
    {
        Scope(customerId); ValidateId(workerId); ValidateLease(leaseDuration);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        if (Number(db, tx, "SELECT enabled FROM cell WHERE singleton=1") != 1) { tx.Commit(); return null; }
        var expired = Rows(db, tx, "SELECT id,state FROM jobs WHERE customer_id=$c AND state IN ('Claimed','Dispatched') AND lease_until<=$now ORDER BY created LIMIT 128",
            r => (Id: r.GetString(0), State: r.GetString(1)), ("$c", CustomerId), ("$now", Now));
        foreach (var old in expired)
        {
            var job = ReadJob(db, tx, old.Id);
            var state = old.State == "Dispatched" ? "OutcomeUnknown" : job.Attempt >= MaximumClaimAttempts ? "Blocked" : "Queued";
            var reason = old.State == "Dispatched" ? "LeaseExpiredAfterDispatch" : state == "Blocked" ? "ClaimAttemptsExceeded" : "LeaseExpiredBeforeDispatch";
            Exec(db, tx, "UPDATE jobs SET state=$state,worker_id=NULL,lease_until=NULL,reason=$reason,updated=$now WHERE customer_id=$c AND id=$id",
                ("$state", state), ("$reason", reason),
                ("$now", Now), ("$c", CustomerId), ("$id", old.Id));
            Audit(db, tx, workerId, job.ProjectId, job.EnvironmentId, "JobLeaseExpired", old.Id, state,
                old.State == "Dispatched" ? "ReconciliationRequired" : state == "Blocked" ? "ClaimAttemptsExceeded" : "NoEffectDispatched", job.PlanId);
        }
        var next = Rows(db, tx, "SELECT id FROM jobs WHERE customer_id=$c AND state='Queued' ORDER BY created,id LIMIT 1",
            r => r.GetString(0), ("$c", CustomerId)).SingleOrDefault();
        if (next is null) { tx.Commit(); return null; }
        Exec(db, tx, """
            UPDATE jobs SET state='Claimed',worker_id=$worker,lease_until=$lease,fence=fence+1,attempt=attempt+1,updated=$now
            WHERE customer_id=$c AND id=$id AND state='Queued'
            """, ("$worker", workerId), ("$lease", Now + (long)leaseDuration.TotalMilliseconds), ("$now", Now), ("$c", CustomerId), ("$id", next));
        var claimed = ReadJob(db, tx, next);
        Audit(db, tx, workerId, claimed.ProjectId, claimed.EnvironmentId, "JobClaimed", next, "Claimed", "LeaseGranted", claimed.PlanId);
        tx.Commit(); return claimed;
    }

    public FoundationDispatch StartDispatch(string customerId, string jobId, string workerId, long fence) =>
        StartDispatchCore(customerId, jobId, workerId, fence, null, null);

    // Disconnected runners must use this path: feature/config policy and authority share the dispatch transaction.
    public FoundationDispatch StartMockDispatch(string customerId, string jobId, string workerId, long fence,
        EntitlementVerifier verifier, string runtimeVersion)
    {
        ArgumentNullException.ThrowIfNull(verifier);
        return StartDispatchCore(customerId, jobId, workerId, fence, verifier, runtimeVersion);
    }

    FoundationDispatch StartDispatchCore(string customerId, string jobId, string workerId, long fence,
        EntitlementVerifier? verifier, string? runtimeVersion)
    {
        Scope(customerId); ValidateId(jobId); ValidateId(workerId);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        AssertActivation(db, tx);
        var job = ReadJob(db, tx, jobId); Lease(job, workerId, fence, "Claimed");
        var plan = ReadPlan(db, tx, job.PlanId);
        try
        {
            ValidatePlanAuthority(db, tx, plan);
            if (Require(db, tx, job.OperatorKey, job.ProjectId, job.EnvironmentId, "Operator") != job.OperatorVersion)
                throw new FoundationStoreException("OperatorGrantChanged", 403);
            if (plan.ChangesExternalState && plan.ApproverKey is null) throw new FoundationStoreException("ApprovalRequired", 403);
            if (verifier is not null) RequireMockDispatchFeature(db, tx, plan.RequesterKey, job.ProjectId, job.EnvironmentId, verifier, runtimeVersion!);
        }
        catch (FoundationStoreException error)
        {
            Exec(db, tx, "UPDATE jobs SET state='Blocked',reason=$reason,worker_id=NULL,lease_until=NULL,updated=$now WHERE customer_id=$c AND id=$id",
                ("$reason", error.Code), ("$now", Now), ("$c", CustomerId), ("$id", jobId));
            Audit(db, tx, workerId, job.ProjectId, job.EnvironmentId, "DispatchDenied", jobId, "Blocked", error.Code, plan.Id);
            tx.Commit(); throw;
        }
        var connection = FindConnection(db, tx, plan.ConnectionId)!;
        Exec(db, tx, "UPDATE jobs SET state='Dispatched',updated=$now WHERE customer_id=$c AND id=$id", ("$now", Now), ("$c", CustomerId), ("$id", jobId));
        Audit(db, tx, workerId, job.ProjectId, job.EnvironmentId, "DispatchIntent", jobId, "Dispatched", "AuthorityRevalidated", plan.Id);
        var dispatched = ReadJob(db, tx, jobId); tx.Commit(); return new(dispatched, plan, connection);
    }

    public FoundationJob RenewLease(string customerId, string jobId, string workerId, long fence, TimeSpan leaseDuration)
    {
        Scope(customerId); ValidateLease(leaseDuration); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var job = ReadJob(db, tx, jobId);
        if (job.State is not ("Claimed" or "Dispatched")) throw new FoundationStoreException("LeaseConflict");
        Lease(job, workerId, fence, job.State);
        if (Number(db, tx, "SELECT enabled FROM cell WHERE singleton=1") != 1) throw new FoundationStoreException("CustomerSuspended", 403);
        Exec(db, tx, "UPDATE jobs SET lease_until=$lease,updated=$now WHERE customer_id=$c AND id=$id",
            ("$lease", Now + (long)leaseDuration.TotalMilliseconds), ("$now", Now), ("$c", CustomerId), ("$id", jobId));
        var result = ReadJob(db, tx, jobId); tx.Commit(); return result;
    }

    public FoundationJob CompleteJob(string customerId, string jobId, string workerId, long fence, string receiptJson)
    {
        Scope(customerId); var receipt = CanonicalJson(receiptJson);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var job = ReadJob(db, tx, jobId); Lease(job, workerId, fence, "Dispatched");
        Exec(db, tx, "UPDATE jobs SET state='Succeeded',receipt=$receipt,reason=NULL,worker_id=NULL,lease_until=NULL,updated=$now WHERE customer_id=$c AND id=$id",
            ("$receipt", receipt), ("$now", Now), ("$c", CustomerId), ("$id", jobId));
        Audit(db, tx, workerId, job.ProjectId, job.EnvironmentId, "JobCompleted", jobId, "Succeeded", "ReceiptCommitted", job.PlanId);
        var result = ReadJob(db, tx, jobId); tx.Commit(); return result;
    }

    public FoundationJob FailJob(string customerId, string jobId, string workerId, long fence, bool outcomeMayHaveOccurred, string reasonCode)
    {
        Scope(customerId); ValidateId(reasonCode); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var job = ReadJob(db, tx, jobId);
        if (job.State is not ("Claimed" or "Dispatched")) throw new FoundationStoreException("LeaseConflict");
        Lease(job, workerId, fence, job.State);
        var plan = ReadPlan(db, tx, job.PlanId);
        var state = outcomeMayHaveOccurred || job.State == "Dispatched" && plan.ChangesExternalState ? "OutcomeUnknown" : "Failed";
        Exec(db, tx, "UPDATE jobs SET state=$state,reason=$reason,worker_id=NULL,lease_until=NULL,updated=$now WHERE customer_id=$c AND id=$id",
            ("$state", state), ("$reason", reasonCode), ("$now", Now), ("$c", CustomerId), ("$id", jobId));
        Audit(db, tx, workerId, job.ProjectId, job.EnvironmentId, "JobInterrupted", jobId, state,
            state == "OutcomeUnknown" ? "ReconciliationRequired" : "NoEffectReported", job.PlanId);
        var result = ReadJob(db, tx, jobId); tx.Commit(); return result;
    }

    public FoundationJob CancelJob(string customerId, string operatorKey, string jobId)
    {
        Scope(customerId); ValidateId(jobId); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var job = ReadJob(db, tx, jobId); Require(db, tx, operatorKey, job.ProjectId, job.EnvironmentId, "Operator");
        if (job.State is not ("Queued" or "Claimed" or "Dispatched")) throw new FoundationStoreException("JobNotCancellable");
        var state = job.State == "Dispatched" ? "OutcomeUnknown" : "Cancelled";
        Exec(db, tx, "UPDATE jobs SET state=$state,reason=$reason,worker_id=NULL,lease_until=NULL,fence=fence+1,updated=$now WHERE customer_id=$c AND id=$id",
            ("$state", state), ("$reason", state == "Cancelled" ? "CancelledBeforeDispatch" : "CancellationAfterDispatch"),
            ("$now", Now), ("$c", CustomerId), ("$id", jobId));
        Audit(db, tx, operatorKey, job.ProjectId, job.EnvironmentId, "CancellationRequested", jobId, state,
            state == "Cancelled" ? "NoEffectDispatched" : "ReconciliationRequired", job.PlanId);
        var result = ReadJob(db, tx, jobId); tx.Commit(); return result;
    }

    public FoundationJob ReconcileJob(string customerId, string operatorKey, string jobId, string outcome, string receiptJson)
    {
        Scope(customerId); ValidateId(jobId);
        if (outcome is not ("Succeeded" or "FailedNoEffect")) throw new FoundationStoreException("InvalidReconciliation", 400);
        var receipt = CanonicalJson(receiptJson);
        if (receipt == "{}") throw new FoundationStoreException("ReconciliationEvidenceRequired", 400);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var job = ReadJob(db, tx, jobId); Require(db, tx, operatorKey, job.ProjectId, job.EnvironmentId, "Operator");
        if (job.State != "OutcomeUnknown") throw new FoundationStoreException("ReconciliationNotRequired");
        var state = outcome == "Succeeded" ? "ReconciledSucceeded" : "ReconciledNoEffect";
        Exec(db, tx, "UPDATE jobs SET state=$state,receipt=$receipt,reason='OperatorEvidenceRecorded',updated=$now WHERE customer_id=$c AND id=$id",
            ("$state", state), ("$receipt", receipt), ("$now", Now), ("$c", CustomerId), ("$id", jobId));
        Audit(db, tx, operatorKey, job.ProjectId, job.EnvironmentId, "JobReconciled", jobId, state, "OperatorEvidenceRecorded", job.PlanId);
        var result = ReadJob(db, tx, jobId); tx.Commit(); return result;
    }

    public IReadOnlyList<FoundationOutboxEvent> PendingOutbox(string customerId, int limit = 100)
    {
        Scope(customerId); if (limit is < 1 or > 1000) throw new FoundationStoreException("InvalidPage", 400);
        using var db = Open(); return Rows(db, null, "SELECT event_id,job_id,kind,created FROM outbox WHERE customer_id=$c AND delivered IS NULL ORDER BY created,event_id LIMIT $limit",
            r => new FoundationOutboxEvent(r.GetString(0), CustomerId, r.GetString(1), r.GetString(2), At(r.GetInt64(3))), ("$c", CustomerId), ("$limit", limit));
    }

    public bool ConsumeOutbox(string customerId, string eventId)
    {
        Scope(customerId); ValidateId(eventId); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        if (Number(db, tx, "SELECT count(*) FROM outbox WHERE customer_id=$c AND event_id=$id", ("$c", CustomerId), ("$id", eventId)) == 0)
            throw new FoundationStoreException("NotFound", 404);
        var first = Exec(db, tx, "INSERT OR IGNORE INTO inbox VALUES($c,$id,$now)", ("$c", CustomerId), ("$id", eventId), ("$now", Now)) == 1;
        Exec(db, tx, "UPDATE outbox SET delivered=COALESCE(delivered,$now) WHERE customer_id=$c AND event_id=$id", ("$now", Now), ("$c", CustomerId), ("$id", eventId));
        tx.Commit(); return first;
    }

    FoundationJob ReadJob(SqliteConnection db, SqliteTransaction? tx, string id) =>
        Rows(db, tx, """
            SELECT j.id,j.plan_id,p.project_id,p.environment_id,j.operator_key,j.operator_version,j.state,j.worker_id,j.lease_until,
              j.fence,j.attempt,j.receipt,j.reason,j.created,j.updated FROM jobs j
            JOIN plans p ON p.customer_id=j.customer_id AND p.id=j.plan_id WHERE j.customer_id=$c AND j.id=$id
            """, r => new FoundationJob(r.GetString(0), CustomerId, CellId, r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4),
                r.GetInt64(5), r.GetString(6), r.IsDBNull(7) ? null : r.GetString(7), r.IsDBNull(8) ? null : At(r.GetInt64(8)),
                r.GetInt64(9), r.GetInt32(10), r.IsDBNull(11) ? null : r.GetString(11), r.IsDBNull(12) ? null : r.GetString(12),
                At(r.GetInt64(13)), At(r.GetInt64(14))), ("$c", CustomerId), ("$id", id)).SingleOrDefault() ?? throw new FoundationStoreException("NotFound", 404);

    void Lease(FoundationJob job, string workerId, long fence, string expectedState)
    {
        if (job.State != expectedState || job.WorkerId != workerId || job.Fence != fence || job.LeaseUntil is null || job.LeaseUntil.Value.ToUnixTimeMilliseconds() <= Now)
            throw new FoundationStoreException("LeaseConflict");
    }
    static void ValidateLease(TimeSpan duration)
    {
        if (duration < TimeSpan.FromSeconds(1) || duration > TimeSpan.FromMinutes(5)) throw new FoundationStoreException("InvalidLease", 400);
    }
}
