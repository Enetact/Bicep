using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    // New review transactions use their one observed instant. Existing audit callers retain their prior behavior.
    void RestrictedRecoveryAuditAt(SqliteConnection db, SqliteTransaction tx, string actorKey, string projectId, string environmentId,
        string action, string targetId, string outcome, string reason, string correlation, DateTimeOffset observed)
    {
        var previous = Rows(db, tx, "SELECT sequence,event_hash FROM audit ORDER BY sequence DESC LIMIT 1",
            r => (Sequence: r.GetInt64(0), Hash: r.GetString(1))).SingleOrDefault();
        if (previous.Sequence >= MaximumAuditEventsPerCell) throw new FoundationStoreException("AuditCapacityExceeded", 507);
        var sequence = previous.Sequence + 1; var previousHash = previous.Hash ?? ""; var eventId = NewId(); var created = observed.ToUnixTimeMilliseconds();
        var hash = AuditHash(sequence, CustomerId, CellId, eventId, actorKey, projectId, environmentId,
            action, targetId, outcome, reason, correlation, created, previousHash);
        Exec(db, tx, """
            INSERT INTO audit VALUES($sequence,$customer,$cell,$event,$actor,$project,$environment,$action,$target,$outcome,$reason,$correlation,$created,$previous,$hash)
            """, ("$sequence", sequence), ("$customer", CustomerId), ("$cell", CellId), ("$event", eventId),
            ("$actor", actorKey), ("$project", projectId), ("$environment", environmentId), ("$action", action),
            ("$target", targetId), ("$outcome", outcome), ("$reason", reason), ("$correlation", correlation),
            ("$created", created), ("$previous", previousHash), ("$hash", hash));
    }
}
