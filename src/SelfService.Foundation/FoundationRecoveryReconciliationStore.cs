using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

// A bounded recovery projection deliberately excludes plan payloads, provider receipts and actor identities.
public sealed record FoundationRecoveryCase(string Id, string CustomerId, string CellId, string ProjectId,
    string EnvironmentId, string PlanId, string PlanDigest, string State, long Fence, DateTimeOffset UpdatedAt,
    string? Reason, string CaseSha256);
public sealed record FoundationRecoveryCases(IReadOnlyList<FoundationRecoveryCase> Items, bool HasMore);

public sealed partial class FoundationStore
{
    public IReadOnlyList<FoundationMembership> GetRecoveryMemberships(string customerId, string actorKey)
    {
        Scope(customerId); RequireHuman(actorKey); using var db = Open();
        return Rows(db, null, """
            SELECT m.project_id,m.environment_id,m.roles,a.version,m.enabled FROM memberships m
            JOIN actors a ON a.customer_id=m.customer_id AND a.actor_key=m.actor_key
            WHERE m.customer_id=$c AND m.actor_key=$a ORDER BY m.project_id,m.environment_id
            """, r => new FoundationMembership(CustomerId, CellId, actorKey, r.GetString(0), r.GetString(1),
                JsonSerializer.Deserialize<string[]>(r.GetString(2))!.Where(role => role == "Operator").ToArray(),
                r.GetInt64(3), r.GetInt64(4) == 1), ("$c", CustomerId), ("$a", actorKey))
            .Where(m => m.Roles.Length != 0).ToArray();
    }

    public FoundationRecoveryCases ListRecoveryCases(string customerId, string actorKey, string projectId,
        string environmentId, int limit = 100)
    {
        Scope(customerId); ValidateId(projectId); ValidateId(environmentId);
        if (limit is < 1 or > 100) throw new FoundationStoreException("InvalidPage", 400);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        RequireRecoveryOperator(db, tx, actorKey, projectId, environmentId);
        var ids = Rows(db, tx, """
            SELECT j.id FROM jobs j JOIN plans p ON p.customer_id=j.customer_id AND p.id=j.plan_id
            WHERE j.customer_id=$c AND p.project_id=$p AND p.environment_id=$e AND j.state='OutcomeUnknown'
            ORDER BY j.updated,j.id LIMIT $limit
            """, r => r.GetString(0), ("$c", CustomerId), ("$p", projectId), ("$e", environmentId), ("$limit", limit + 1));
        var result = new FoundationRecoveryCases(ids.Take(limit).Select(id => RecoveryCase(db, tx, ReadJob(db, tx, id))).ToArray(), ids.Count > limit);
        Audit(db, tx, actorKey, projectId, environmentId, "RecoveryCasesRead", "jobs", "Succeeded", "InactiveReconciliationOnly", "jobs");
        tx.Commit(); return result;
    }

    public FoundationRecoveryCase GetRecoveryCase(string customerId, string actorKey, string projectId,
        string environmentId, string jobId)
    {
        Scope(customerId); ValidateId(jobId); ValidateId(projectId); ValidateId(environmentId);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        RequireRecoveryOperator(db, tx, actorKey, projectId, environmentId);
        var job = ReadRecoveryJob(db, tx, projectId, environmentId, jobId);
        var result = RecoveryCase(db, tx, job);
        Audit(db, tx, actorKey, projectId, environmentId, "RecoveryCaseRead", jobId, "Succeeded", "InactiveReconciliationOnly", job.PlanId);
        tx.Commit(); return result;
    }

    public FoundationRecoveryCase ReconcileRecoveryCase(string customerId, string actorKey, string projectId,
        string environmentId, string jobId, string expectedCaseSha256, string outcome, string evidenceJson)
    {
        Scope(customerId); ValidateId(jobId); ValidateId(projectId); ValidateId(environmentId);
        if (outcome is not ("Succeeded" or "FailedNoEffect")) throw new FoundationStoreException("InvalidReconciliation", 400);
        var evidence = CanonicalJson(evidenceJson);
        if (evidence == "{}") throw new FoundationStoreException("ReconciliationEvidenceRequired", 400);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        RequireRecoveryOperator(db, tx, actorKey, projectId, environmentId);
        var job = ReadRecoveryJob(db, tx, projectId, environmentId, jobId);
        if (job.State != "OutcomeUnknown") throw new FoundationStoreException("ReconciliationNotRequired");
        MatchDigest(RecoveryCase(db, tx, job).CaseSha256, expectedCaseSha256);
        var state = outcome == "Succeeded" ? "ReconciledSucceeded" : "ReconciledNoEffect";
        Exec(db, tx, """
            UPDATE jobs SET state=$state,receipt=$receipt,reason='OperatorEvidenceRecorded',updated=$now
            WHERE customer_id=$c AND id=$id
            """, ("$state", state), ("$receipt", evidence), ("$now", Now), ("$c", CustomerId), ("$id", jobId));
        Audit(db, tx, actorKey, projectId, environmentId, "JobReconciled", jobId, state, "InactiveOperatorEvidenceRecorded", job.PlanId);
        var result = RecoveryCase(db, tx, ReadJob(db, tx, jobId)); tx.Commit(); return result;
    }

    void RequireRecoveryOperator(SqliteConnection db, SqliteTransaction tx, string actorKey, string projectId, string environmentId)
    {
        RequireHuman(actorKey);
        Require(db, tx, actorKey, projectId, environmentId, ["Operator"], requireActive: false);
        var lifecycle = ReadLifecycle(db, tx);
        if (lifecycle.Enabled || lifecycle.State is not ("Suspended" or "Offboarding"))
            throw new FoundationStoreException("InactiveRecoveryRequired");
    }

    FoundationJob ReadRecoveryJob(SqliteConnection db, SqliteTransaction tx, string projectId, string environmentId, string jobId)
    {
        var job = ReadJob(db, tx, jobId); ReadScope(projectId, environmentId, job.ProjectId, job.EnvironmentId);
        if (job.State is not ("OutcomeUnknown" or "ReconciledSucceeded" or "ReconciledNoEffect"))
            throw new FoundationStoreException("NotFound", 404);
        return job;
    }

    FoundationRecoveryCase RecoveryCase(SqliteConnection db, SqliteTransaction tx, FoundationJob job)
    {
        var plan = ReadPlan(db, tx, job.PlanId);
        var version = Hash(JsonSerializer.Serialize(new { schema = "platform-studio.recovery-case.v1", job.CustomerId,
            job.CellId, job.Id, job.ProjectId, job.EnvironmentId, job.PlanId, PlanDigest = plan.Digest,
            job.State, job.Fence, Updated = job.UpdatedAt.ToUnixTimeMilliseconds(), job.Reason }, Json));
        return new(job.Id, job.CustomerId, job.CellId, job.ProjectId, job.EnvironmentId, job.PlanId, plan.Digest,
            job.State, job.Fence, job.UpdatedAt, job.Reason, version);
    }
}
