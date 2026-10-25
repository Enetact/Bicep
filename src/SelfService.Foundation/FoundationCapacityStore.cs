namespace SelfService.Foundation;

public sealed record FoundationCapacityStatus(long PipelinePlans, long AuditEvents, long ExportMetadataBytes,
    int PipelinePlanLimit, int AuditEventLimit, long ExportMetadataByteLimit)
{
    public bool PhysicalDiskQuotaEnforced => false;
}

public sealed partial class FoundationStore
{
    public FoundationCapacityStatus CapacityStatus(string customerId, string actorKey, string projectId, string environmentId)
    {
        Scope(customerId); ValidateId(projectId); ValidateId(environmentId);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        Require(db, tx, actorKey, projectId, environmentId, ["PlatformOperator"], requireActive: false);
        var result = new FoundationCapacityStatus(
            Number(db, tx, "SELECT count(*) FROM plans WHERE customer_id=$c", ("$c", CustomerId)),
            Number(db, tx, "SELECT count(*) FROM audit WHERE customer_id=$c", ("$c", CustomerId)),
            Number(db, tx, "SELECT coalesce(sum(length(CAST(manifest AS BLOB))),0) FROM scope_exports WHERE customer_id=$c", ("$c", CustomerId)),
            MaximumPlansPerCell, MaximumAuditEventsPerCell, MaximumExportStorageBytes);
        tx.Commit(); return result;
    }
}
