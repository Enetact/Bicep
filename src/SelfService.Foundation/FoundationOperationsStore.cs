namespace SelfService.Foundation;

public sealed record FoundationOperationalStatus(int SchemaVersion, string Lifecycle, bool Enabled,
    IReadOnlyDictionary<string, long> JobCounts, long PendingOutbox, long ArtifactCount, long ArtifactBytes,
    double? OldestQueuedSeconds, int ActiveJobLimit, int MaximumClaimAttempts)
{
    public bool RecoveryExecutionHeld { get; init; }
    public string? ExecutionHoldReason { get; init; }
}

public sealed partial class FoundationStore
{
    public const int MaximumArtifactsPerCell = 1000;
    public const long MaximumArtifactBytesPerCell = 256L * 1024 * 1024;
    public const int MaximumPlansPerCell = 10000;
    public const int MaximumAuditEventsPerCell = 100000;
    public IReadOnlyList<FoundationMembership> GetOperationalMemberships(string customerId, string actorKey)
    {
        Scope(customerId); ValidateId(actorKey); using var db = Open();
        return Rows(db, null, """
            SELECT m.project_id,m.environment_id,m.roles,a.version,m.enabled FROM memberships m
            JOIN actors a ON a.customer_id=m.customer_id AND a.actor_key=m.actor_key WHERE m.customer_id=$c AND m.actor_key=$a
            """, r => new FoundationMembership(CustomerId, CellId, actorKey, r.GetString(0), r.GetString(1),
                System.Text.Json.JsonSerializer.Deserialize<string[]>(r.GetString(2))!.Where(role => role == "PlatformOperator").ToArray(),
                r.GetInt64(3), r.GetInt64(4) == 1), ("$c", CustomerId), ("$a", actorKey)).Where(m => m.Roles.Length > 0).ToArray();
    }

    public bool StorageReady()
    {
        using var db = Open();
        return Number(db, null, "SELECT schema_version FROM cell WHERE singleton=1") == CurrentSchemaVersion &&
            Number(db, null, "SELECT coalesce(max(sequence),0) FROM audit") < MaximumAuditEventsPerCell;
    }

    // Operations identities receive aggregate cell health, never customer evidence, actors, credentials or source.
    public FoundationOperationalStatus OperationalStatus(string customerId, string actorKey, string projectId, string environmentId)
    {
        Scope(customerId); ValidateId(projectId); ValidateId(environmentId);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        Require(db, tx, actorKey, projectId, environmentId, ["PlatformOperator"], requireActive: false);
        var jobs = Rows(db, tx, "SELECT state,count(*) FROM jobs WHERE customer_id=$c GROUP BY state",
            r => (State: r.GetString(0), Count: r.GetInt64(1)), ("$c", CustomerId)).ToDictionary(x => x.State, x => x.Count, StringComparer.Ordinal);
        var oldest = Rows(db, tx, "SELECT min(created) FROM jobs WHERE customer_id=$c AND state='Queued'",
            r => r.IsDBNull(0) ? (long?)null : r.GetInt64(0), ("$c", CustomerId)).Single();
        var lifecycle = ReadLifecycle(db, tx);
        var recoveryHold = ReadCurrentRecoveryExecutionHold(db, tx);
        var result = new FoundationOperationalStatus((int)Number(db, tx, "SELECT schema_version FROM cell WHERE singleton=1"),
            lifecycle.State, lifecycle.Enabled, jobs,
            Number(db, tx, "SELECT count(*) FROM outbox WHERE customer_id=$c AND delivered IS NULL", ("$c", CustomerId)),
            Number(db, tx, "SELECT count(*) FROM artifacts WHERE customer_id=$c", ("$c", CustomerId)),
            Number(db, tx, "SELECT coalesce(sum(length(content)),0) FROM artifacts WHERE customer_id=$c", ("$c", CustomerId)),
            oldest is null ? null : Math.Max(0, (Now - oldest.Value) / 1000d), MaximumPendingJobs, MaximumClaimAttempts)
        { RecoveryExecutionHeld = recoveryHold is not null, ExecutionHoldReason = recoveryHold is null ? null : "RecoveryRequiresReconciliation" };
        tx.Commit(); return result;
    }
}
