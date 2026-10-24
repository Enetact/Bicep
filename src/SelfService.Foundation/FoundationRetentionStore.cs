using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed record FoundationArtifactRetentionCandidate(string Id, string Sha256, long ContentLength, DateTimeOffset CreatedAt);
public sealed record FoundationArtifactRetentionPlan(FoundationAdministrativePlan Plan, FoundationArtifactRetentionCandidate[] Candidates);
public sealed record FoundationRetentionReceipt(string PlanId, string CustomerId, string ProjectId, string EnvironmentId,
    int ArtifactsArchived, string ManifestDigest, bool AuditHistoryPreserved, bool SecureErasureVerified = false);

public sealed partial class FoundationStore
{
    public const int MinimumArtifactRetentionDays = 30;
    sealed record RetentionPayload(DateTimeOffset CutoffUtc, string Purpose, FoundationArtifactRetentionCandidate[] Candidates);

    public FoundationArtifactRetentionPlan PrepareArtifactRetention(string customerId, string requesterKey, string projectId,
        string environmentId, DateTimeOffset cutoffUtc, string purpose)
    {
        Scope(customerId); RequireHuman(requesterKey); ValidateApplicationScopeId(projectId); ValidateApplicationScopeId(environmentId); ValidatePurpose(purpose);
        if (cutoffUtc > clock.GetUtcNow().AddDays(-MinimumArtifactRetentionDays)) throw new FoundationStoreException("MinimumRetentionNotMet", 400);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        Require(db, tx, requesterKey, projectId, environmentId, ["CustomerAdmin"], requireActive: false);
        RequireRetentionQuiescence(db, tx, projectId, environmentId);
        var candidates = Rows(db, tx, """
            SELECT id,digest,length(content),created FROM artifacts WHERE customer_id=$c AND project_id=$p AND environment_id=$e
            AND created<=$cutoff ORDER BY id LIMIT 101
            """, r => new FoundationArtifactRetentionCandidate(r.GetString(0), r.GetString(1), r.GetInt64(2), At(r.GetInt64(3))),
            ("$c", CustomerId), ("$p", projectId), ("$e", environmentId), ("$cutoff", cutoffUtc.ToUnixTimeMilliseconds())).ToArray();
        if (candidates.Length == 0) throw new FoundationStoreException("NothingEligibleForRetention");
        if (candidates.Length > 100) throw new FoundationStoreException("RetentionBatchLimit", 413);
        var plan = CreateAdministrativePlan(db, tx, requesterKey, projectId, environmentId, "artifact.retention",
            JsonSerializer.Serialize(new RetentionPayload(At(cutoffUtc.ToUnixTimeMilliseconds()), purpose, candidates), Json));
        tx.Commit(); return new(plan, candidates);
    }

    public FoundationRetentionReceipt ExecuteArtifactRetention(string customerId, string executorKey, string id, string expectedDigest)
    {
        Scope(customerId); RequireHuman(executorKey); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var plan = RequireAdministrativeExecution(db, tx, executorKey, id, expectedDigest, "artifact.retention");
        var payload = JsonSerializer.Deserialize<RetentionPayload>(plan.PayloadJson, Json)!;
        if (payload.CutoffUtc > clock.GetUtcNow().AddDays(-MinimumArtifactRetentionDays) || payload.Candidates.Length is 0 or > 100)
            throw new FoundationStoreException("InvalidRetentionPlan");
        RequireRetentionQuiescence(db, tx, plan.ProjectId, plan.EnvironmentId);
        foreach (var candidate in payload.Candidates)
        {
            var artifact = Rows(db, tx, """
                SELECT name,content,digest,created FROM artifacts WHERE customer_id=$c AND project_id=$p AND environment_id=$e AND id=$id
                """, r => (Name: r.GetString(0), Content: (byte[])r[1], Digest: r.GetString(2), Created: r.GetInt64(3)),
                ("$c", CustomerId), ("$p", plan.ProjectId), ("$e", plan.EnvironmentId), ("$id", candidate.Id)).SingleOrDefault();
            if (artifact.Name is null || artifact.Digest != candidate.Sha256 || artifact.Content.LongLength != candidate.ContentLength ||
                artifact.Created != candidate.CreatedAt.ToUnixTimeMilliseconds() || artifact.Created > payload.CutoffUtc.ToUnixTimeMilliseconds() ||
                Convert.ToHexString(SHA256.HashData(artifact.Content)).ToLowerInvariant() != candidate.Sha256)
                throw new FoundationStoreException("RetentionSnapshotChanged");
            // Application retention removes the current blob, preserving its metadata and immutable audit history.
            // SQLite WAL/free pages, snapshots, copies and storage media are not claimed to be securely erased.
            Exec(db, tx, "INSERT INTO artifact_tombstones VALUES($c,$id,$p,$e,$name,$digest,$length,$created,$now,$plan)",
                ("$c", CustomerId), ("$id", candidate.Id), ("$p", plan.ProjectId), ("$e", plan.EnvironmentId), ("$name", artifact.Name),
                ("$digest", candidate.Sha256), ("$length", candidate.ContentLength), ("$created", artifact.Created), ("$now", Now), ("$plan", id));
            Exec(db, tx, "DELETE FROM artifacts WHERE customer_id=$c AND id=$id AND project_id=$p AND environment_id=$e",
                ("$c", CustomerId), ("$id", candidate.Id), ("$p", plan.ProjectId), ("$e", plan.EnvironmentId));
            Audit(db, tx, executorKey, plan.ProjectId, plan.EnvironmentId, "ArtifactRetainedAsMetadata", candidate.Id,
                "Succeeded", "ReviewedContentRetention", id);
        }
        CompleteAdministrativePlan(db, tx, executorKey, plan, "ArtifactRetentionCompleted");
        tx.Commit(); return new(id, CustomerId, plan.ProjectId, plan.EnvironmentId, payload.Candidates.Length,
            Hash(JsonSerializer.Serialize(payload.Candidates, Json)), AuditHistoryPreserved: true);
    }

    public IReadOnlyList<FoundationArtifactRetentionCandidate> ListArchivedArtifacts(string customerId, string auditorKey,
        string projectId, string environmentId)
    {
        Scope(customerId); RequireHuman(auditorKey); using var db = Open();
        Require(db, null, auditorKey, projectId, environmentId, ["Auditor"], requireActive: false);
        var items = Rows(db, null, """
            SELECT id,digest,content_length,created FROM artifact_tombstones WHERE customer_id=$c AND project_id=$p AND environment_id=$e
            ORDER BY archived,id LIMIT 1001
            """, r => new FoundationArtifactRetentionCandidate(r.GetString(0), r.GetString(1), r.GetInt64(2), At(r.GetInt64(3))),
            ("$c", CustomerId), ("$p", projectId), ("$e", environmentId));
        if (items.Count > 1000) throw new FoundationStoreException("ExportPageRequired", 413);
        return items;
    }

    void RequireRetentionQuiescence(SqliteConnection db, SqliteTransaction tx, string project, string environment)
    {
        if (Number(db, tx, """
            SELECT count(*) FROM plans WHERE customer_id=$c AND project_id=$p AND environment_id=$e AND expires>$now
            """, ("$c", CustomerId), ("$p", project), ("$e", environment), ("$now", Now)) > 0 ||
            Number(db, tx, """
                SELECT count(*) FROM jobs j JOIN plans p ON p.customer_id=j.customer_id AND p.id=j.plan_id
                WHERE j.customer_id=$c AND p.project_id=$p AND p.environment_id=$e AND j.state IN ('Queued','Claimed','Dispatched','OutcomeUnknown')
                """, ("$c", CustomerId), ("$p", project), ("$e", environment)) > 0)
            throw new FoundationStoreException("RetentionBlockedByWork");
    }
}
