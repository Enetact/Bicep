using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    // Call only inside the same immediate write transaction as the changing admission, before any mutations.
    void RequireBoundConsumerAdmission(SqliteConnection db, SqliteTransaction tx, FoundationCredentialBoundPipelineRun? run)
    {
        var rows = ReadCredentialRuntimeBindings(db, tx);
        if (run is null)
        {
            if (CredentialRuntimeBindingRequired(db, tx, rows.Count))
                throw new FoundationStoreException("BoundPipelineConsumerRequired", 403);
            return;
        }
        var session = run.SessionForAdmission(this);
        _ = RequireCredentialRuntimeBinding(db, tx, session);
    }

    void RequireBoundPlanEligibility(SqliteConnection db, SqliteTransaction tx, FoundationCredentialBoundPipelineRun? run, FoundationPlan plan)
    {
        if (run is null) return; // The policy guard already restricted this to a never-bound legacy cell.
        var connection = FindConnection(db, tx, plan.ConnectionId);
        if (connection is null || !connection.Enabled || plan.Operation != "pipeline.dispatch" || !plan.ChangesExternalState ||
            plan.CustomerId != CustomerId || plan.CellId != CellId || connection.CustomerId != CustomerId ||
            connection.Generation != plan.ConnectionGeneration || connection.ProjectId != plan.ProjectId || connection.EnvironmentId != plan.EnvironmentId)
            throw new FoundationStoreException("BoundPipelineConnectionNotAllowed", 403);
        var mapped = FoundationPipelineConnection.FromStored(connection, CellId);
        run.RequireConnectionForAdmission(this, mapped, plan.ProjectId, plan.EnvironmentId);
    }

    void CommitBoundConsumerAdmission(SqliteConnection db, SqliteTransaction tx, FoundationCredentialBoundPipelineRun? run)
    {
        // A proof refusal here is still precommit and rolls back. A commit-attempt failure cannot assert that SQL had no effect.
        RequireBoundConsumerAdmission(db, tx, run);
        if (run is null) { tx.Commit(); return; }
        try { tx.Commit(); }
        catch (Exception error) when (error is not StackOverflowException and not OutOfMemoryException)
        { throw new FoundationStoreException("BoundConsumerAdmissionUncertain", 503); }
    }
}
