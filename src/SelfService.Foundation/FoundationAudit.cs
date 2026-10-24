using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed record FoundationAuditEvent(long Sequence, string CustomerId, string CellId, string EventId,
    string ActorKey, string ProjectId, string EnvironmentId, string Action, string TargetId, string Outcome,
    string Reason, string CorrelationId, DateTimeOffset CreatedAt, string PreviousHash, string EventHash);
public sealed record FoundationArtifact(string Id, string CustomerId, string ProjectId, string EnvironmentId,
    string Name, string Sha256, byte[] Content, DateTimeOffset CreatedAt);

public sealed partial class FoundationStore
{
    void Audit(SqliteConnection db, SqliteTransaction tx, string actorKey, string projectId, string environmentId,
        string action, string targetId, string outcome, string reason, string correlation)
    {
        var previous = Rows(db, tx, "SELECT sequence,event_hash FROM audit ORDER BY sequence DESC LIMIT 1",
            r => (Sequence: r.GetInt64(0), Hash: r.GetString(1))).SingleOrDefault();
        if (previous.Sequence >= MaximumAuditEventsPerCell) throw new FoundationStoreException("AuditCapacityExceeded", 507);
        var sequence = previous.Sequence + 1; var previousHash = previous.Hash ?? ""; var eventId = NewId(); var created = Now;
        var hash = AuditHash(sequence, CustomerId, CellId, eventId, actorKey, projectId, environmentId,
            action, targetId, outcome, reason, correlation, created, previousHash);
        Exec(db, tx, """
            INSERT INTO audit VALUES($sequence,$customer,$cell,$event,$actor,$project,$environment,$action,$target,$outcome,$reason,$correlation,$created,$previous,$hash)
            """, ("$sequence", sequence), ("$customer", CustomerId), ("$cell", CellId), ("$event", eventId),
            ("$actor", actorKey), ("$project", projectId), ("$environment", environmentId), ("$action", action),
            ("$target", targetId), ("$outcome", outcome), ("$reason", reason), ("$correlation", correlation),
            ("$created", created), ("$previous", previousHash), ("$hash", hash));
    }

    // This server-owned entry point has no customer selector and accepts no raw exception/token/body.
    public void RecordSecurityEvent(string reasonCode, string correlationId, string actorKey = "unauthenticated")
    {
        ValidateId(actorKey); ValidateId(correlationId);
        if (reasonCode is null || !Regex.IsMatch(reasonCode, "\\A[A-Za-z0-9._-]{1,64}\\z", RegexOptions.CultureInvariant))
            throw new FoundationStoreException("InvalidReasonCode", 400);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var scope = Rows(db, tx, "SELECT project_id,environment_id FROM audit WHERE action='CustomerBootstrapped' ORDER BY sequence LIMIT 1",
            r => (Project: r.GetString(0), Environment: r.GetString(1))).SingleOrDefault();
        Audit(db, tx, actorKey, scope.Project ?? "default", scope.Environment ?? "dev", "SecurityDenied", CellId, "Denied", reasonCode, correlationId);
        tx.Commit();
    }

    public IReadOnlyList<FoundationAuditEvent> ListAudit(string customerId, string actorKey, string projectId = "default",
        string environmentId = "dev", int limit = 200, long afterSequence = 0)
    {
        Scope(customerId); ValidateId(projectId); ValidateId(environmentId);
        if (limit is < 1 or > 1000 || afterSequence < 0) throw new FoundationStoreException("InvalidPage", 400);
        using var db = Open();
        Require(db, null, actorKey, projectId, environmentId, ["Auditor"]);
        return Rows(db, null, """
            SELECT sequence,customer_id,cell_id,event_id,actor_key,project_id,environment_id,action,target_id,outcome,reason,
              correlation,created,previous_hash,event_hash FROM audit
            WHERE customer_id=$c AND project_id=$p AND environment_id=$e AND sequence>$after ORDER BY sequence LIMIT $limit
            """, AuditRow, ("$c", CustomerId), ("$p", projectId), ("$e", environmentId), ("$after", afterSequence), ("$limit", limit));
    }

    public bool VerifyAuditIntegrity(string customerId, string actorKey, string projectId = "default", string environmentId = "dev")
    {
        Scope(customerId); using var db = Open();
        Require(db, null, actorKey, projectId, environmentId, ["Auditor"]);
        var rows = Rows(db, null, """
            SELECT sequence,customer_id,cell_id,event_id,actor_key,project_id,environment_id,action,target_id,outcome,reason,
              correlation,created,previous_hash,event_hash FROM audit ORDER BY sequence LIMIT 100001
            """, AuditRow);
        if (rows.Count > 100000) throw new FoundationStoreException("AuditVerificationLimit");
        var previous = ""; long sequence = 0;
        foreach (var row in rows)
        {
            if (row.CustomerId != CustomerId || row.CellId != CellId || row.Sequence != ++sequence || row.PreviousHash != previous ||
                row.EventHash != AuditHash(row.Sequence, row.CustomerId, row.CellId, row.EventId, row.ActorKey, row.ProjectId,
                    row.EnvironmentId, row.Action, row.TargetId, row.Outcome, row.Reason, row.CorrelationId,
                    row.CreatedAt.ToUnixTimeMilliseconds(), row.PreviousHash)) return false;
            previous = row.EventHash;
        }
        return true;
    }

    static FoundationAuditEvent AuditRow(SqliteDataReader r) => new(r.GetInt64(0), r.GetString(1), r.GetString(2),
        r.GetString(3), r.GetString(4), r.GetString(5), r.GetString(6), r.GetString(7), r.GetString(8), r.GetString(9),
        r.GetString(10), r.GetString(11), At(r.GetInt64(12)), r.GetString(13), r.GetString(14));
    static string AuditHash(long sequence, string customer, string cell, string eventId, string actor, string project,
        string environment, string action, string target, string outcome, string reason, string correlation, long created, string previous) =>
        Hash(JsonSerializer.Serialize(new { sequence, customer, cell, eventId, actor, project, environment, action, target, outcome,
            reason, correlation, created, previous }, Json));

    public FoundationArtifact StoreArtifact(string customerId, string actorKey, string projectId, string environmentId, string name, byte[] content)
    {
        Scope(customerId); ValidateId(projectId); ValidateId(environmentId);
        RequireHuman(actorKey);
        if (name is null || !Regex.IsMatch(name, "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z") || content is null || content.Length > 1048576)
            throw new FoundationStoreException("InvalidArtifact", 400);
        var id = NewId(); var created = Now;
        var copy = content.ToArray(); var digest = Convert.ToHexString(SHA256.HashData(copy)).ToLowerInvariant();
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        Require(db, tx, actorKey, projectId, environmentId, ["Requester"]);
        if (Number(db, tx, "SELECT count(*) FROM artifacts WHERE customer_id=$c", ("$c", CustomerId)) >= MaximumArtifactsPerCell ||
            Number(db, tx, "SELECT coalesce(sum(length(content)),0) FROM artifacts WHERE customer_id=$c", ("$c", CustomerId)) + copy.LongLength > MaximumArtifactBytesPerCell)
            throw new FoundationStoreException("ArtifactCapacityExceeded", 429);
        Exec(db, tx, "INSERT INTO artifacts VALUES($c,$id,$p,$e,$name,$content,$digest,$created)", ("$c", CustomerId),
            ("$id", id), ("$p", projectId), ("$e", environmentId), ("$name", name), ("$content", copy), ("$digest", digest), ("$created", created));
        Audit(db, tx, actorKey, projectId, environmentId, "ArtifactStored", id, "Succeeded", "DigestBound", id);
        tx.Commit(); return new(id, CustomerId, projectId, environmentId, name, digest, copy.ToArray(), At(created));
    }

    public FoundationArtifact GetArtifact(string customerId, string actorKey, string id, string? projectId = null, string? environmentId = null)
    {
        Scope(customerId); ValidateId(id); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var artifact = Rows(db, tx, "SELECT id,project_id,environment_id,name,content,digest,created FROM artifacts WHERE customer_id=$c AND id=$id",
            r => new FoundationArtifact(r.GetString(0), CustomerId, r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(5),
                (byte[])r[4], At(r.GetInt64(6))), ("$c", CustomerId), ("$id", id)).SingleOrDefault() ?? throw new FoundationStoreException("NotFound", 404);
        ReadScope(projectId, environmentId, artifact.ProjectId, artifact.EnvironmentId);
        Require(db, tx, actorKey, artifact.ProjectId, artifact.EnvironmentId, ["Viewer", "Requester", "Approver", "Operator", "Auditor"]);
        if (Convert.ToHexString(SHA256.HashData(artifact.Content)).ToLowerInvariant() != artifact.Sha256)
            throw new FoundationStoreException("ArtifactIntegrityFailed");
        Audit(db, tx, actorKey, artifact.ProjectId, artifact.EnvironmentId, "ArtifactRead", id, "Succeeded", "AuthorizedRead", id);
        tx.Commit(); return artifact;
    }
}
