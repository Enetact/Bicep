namespace SelfService.Foundation;

public sealed record FoundationWorkerHandle(string WorkerId, string InstanceId, long Generation);
public sealed record FoundationWorkerHealth(long Registered, long Live, long Disabled, long Stopped, long Stale,
    DateTimeOffset? LastHeartbeatUtc, long ActiveLeases, long OutcomeUnknownJobs);

public sealed partial class FoundationStore
{
    // Installed only by the cell's normal, checksummed migration path. Workers never install schema.
    internal const string WorkerOperationsSchemaV4 = """
        CREATE TABLE worker_heartbeats(customer_id TEXT NOT NULL,cell_id TEXT NOT NULL,worker_id TEXT NOT NULL,
          instance_id TEXT NOT NULL,generation INTEGER NOT NULL CHECK(generation>0),process_id INTEGER NOT NULL CHECK(process_id>0),
          process_started INTEGER NOT NULL,mode TEXT NOT NULL CHECK(mode IN ('Disabled','MockLoopback','ClaimHold')),
          state TEXT NOT NULL CHECK(state IN ('Running','Disabled','Stopped')),heartbeat INTEGER NOT NULL,
          registered INTEGER NOT NULL,stopped INTEGER,PRIMARY KEY(customer_id,worker_id));
        CREATE INDEX worker_heartbeat_health ON worker_heartbeats(customer_id,cell_id,state,heartbeat);
        """;

    // Trusted local worker API; deliberately has no HTTP/MCP route and grants no human or provider authority.
    public FoundationWorkerHandle RegisterWorker(string customerId, string workerId, int processId,
        DateTimeOffset processStartedUtc, string mode)
    {
        Scope(customerId); ValidateId(workerId);
        if (processId <= 0 || mode is not ("Disabled" or "MockLoopback" or "ClaimHold"))
            throw new FoundationStoreException("InvalidWorkerRegistration", 400);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var now = Now; var cutoff = now - 30000;
        var existing = Rows(db, tx, "SELECT generation,state,heartbeat FROM worker_heartbeats WHERE customer_id=$c AND worker_id=$w",
            r => (Generation: r.GetInt64(0), State: r.GetString(1), Heartbeat: r.GetInt64(2)), ("$c", CustomerId), ("$w", workerId)).SingleOrDefault();
        if (existing.Generation > 0 && existing.State != "Stopped" && existing.Heartbeat >= cutoff)
            throw new FoundationStoreException("WorkerAlreadyLive");
        if (Number(db, tx, "SELECT count(*) FROM worker_heartbeats WHERE customer_id=$c AND state!='Stopped' AND heartbeat>=$cutoff",
            ("$c", CustomerId), ("$cutoff", cutoff)) >= 16) throw new FoundationStoreException("WorkerCapacityExceeded", 429);
        // Old process metadata is expendable; its registration/stop events remain in the append-only audit.
        Exec(db, tx, "DELETE FROM worker_heartbeats WHERE customer_id=$c AND heartbeat<$cutoff AND worker_id!=$w",
            ("$c", CustomerId), ("$cutoff", now - 86400000), ("$w", workerId));
        if (existing.Generation == 0 && Number(db, tx, "SELECT count(*) FROM worker_heartbeats WHERE customer_id=$c", ("$c", CustomerId)) >= 1024)
            throw new FoundationStoreException("WorkerRegistryCapacityExceeded", 429);
        var handle = new FoundationWorkerHandle(workerId, NewId(), existing.Generation + 1);
        Exec(db, tx, """
            INSERT INTO worker_heartbeats VALUES($c,$cell,$w,$instance,$generation,$pid,$started,$mode,$state,$now,$now,NULL)
            ON CONFLICT(customer_id,worker_id) DO UPDATE SET cell_id=$cell,instance_id=$instance,generation=$generation,
              process_id=$pid,process_started=$started,mode=$mode,state=$state,heartbeat=$now,registered=$now,stopped=NULL
            """, ("$c", CustomerId), ("$cell", CellId), ("$w", workerId), ("$instance", handle.InstanceId), ("$generation", handle.Generation),
            ("$pid", processId), ("$started", processStartedUtc.ToUnixTimeMilliseconds()), ("$mode", mode),
            ("$state", mode == "Disabled" ? "Disabled" : "Running"), ("$now", now));
        var scope = BootstrapScope(db, tx);
        Audit(db, tx, "worker:" + workerId, scope.Project, scope.Environment, "WorkerRegistered", workerId,
            "Succeeded", mode == "Disabled" ? "DispatchDisabled" : "SyntheticQualificationOnly", handle.InstanceId);
        tx.Commit(); return handle;
    }

    public void HeartbeatWorker(string customerId, FoundationWorkerHandle handle)
    {
        Scope(customerId); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        using var command = Command(db, tx, """
            UPDATE worker_heartbeats SET heartbeat=$now WHERE customer_id=$c AND cell_id=$cell AND worker_id=$w
              AND instance_id=$instance AND generation=$generation AND state!='Stopped'
            """, ("$now", Now), ("$c", CustomerId), ("$cell", CellId), ("$w", handle.WorkerId),
            ("$instance", handle.InstanceId), ("$generation", handle.Generation));
        if (command.ExecuteNonQuery() != 1) throw new FoundationStoreException("WorkerFenceConflict");
        tx.Commit();
    }

    public void StopWorker(string customerId, FoundationWorkerHandle handle)
    {
        Scope(customerId); using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        using var command = Command(db, tx, """
            UPDATE worker_heartbeats SET state='Stopped',heartbeat=$now,stopped=$now WHERE customer_id=$c AND cell_id=$cell
              AND worker_id=$w AND instance_id=$instance AND generation=$generation AND state!='Stopped'
            """, ("$now", Now), ("$c", CustomerId), ("$cell", CellId), ("$w", handle.WorkerId),
            ("$instance", handle.InstanceId), ("$generation", handle.Generation));
        if (command.ExecuteNonQuery() != 1) throw new FoundationStoreException("WorkerFenceConflict");
        var scope = BootstrapScope(db, tx);
        Audit(db, tx, "worker:" + handle.WorkerId, scope.Project, scope.Environment, "WorkerStopped", handle.WorkerId,
            "Succeeded", "OwnedWorkerStopped", handle.InstanceId);
        tx.Commit();
    }

    // Operations readers receive aggregates only: no process IDs, paths, worker IDs or customer payloads.
    public FoundationWorkerHealth WorkerHealth(string customerId, string actorKey, string projectId, string environmentId,
        TimeSpan? staleAfter = null)
    {
        Scope(customerId);
        var interval = staleAfter ?? TimeSpan.FromSeconds(30);
        if (interval < TimeSpan.FromSeconds(1) || interval > TimeSpan.FromMinutes(5))
            throw new FoundationStoreException("InvalidWorkerHealthInterval", 400);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        Require(db, tx, actorKey, projectId, environmentId, ["PlatformOperator"], requireActive: false);
        var workers = Rows(db, tx, "SELECT state,heartbeat FROM worker_heartbeats WHERE customer_id=$c AND cell_id=$cell",
            r => (State: r.GetString(0), Heartbeat: r.GetInt64(1)), ("$c", CustomerId), ("$cell", CellId));
        var cutoff = Now - (long)interval.TotalMilliseconds;
        var result = new FoundationWorkerHealth(workers.Count, workers.LongCount(w => w.State == "Running" && w.Heartbeat >= cutoff),
            workers.LongCount(w => w.State == "Disabled" && w.Heartbeat >= cutoff), workers.LongCount(w => w.State == "Stopped"),
            workers.LongCount(w => w.State != "Stopped" && w.Heartbeat < cutoff),
            workers.Count == 0 ? null : At(workers.Max(w => w.Heartbeat)),
            Number(db, tx, "SELECT count(*) FROM jobs WHERE customer_id=$c AND state IN ('Claimed','Dispatched') AND lease_until>=$now",
                ("$c", CustomerId), ("$now", Now)),
            Number(db, tx, "SELECT count(*) FROM jobs WHERE customer_id=$c AND state='OutcomeUnknown'", ("$c", CustomerId)));
        tx.Commit(); return result;
    }
}
