namespace SelfService.Foundation;

// Constructed only by trusted host composition; these pins restrict a database read, never supply current authority.
internal sealed record FoundationCredentialDispatchBinding(string DatabaseInstanceId, string JobId, string WorkerId,
    long Fence, CredentialScope Scope, string PlanDigest, string SourceRevision, string CatalogDigest);

public sealed partial class FoundationStore
{
    internal CredentialDispatchPermit AuthorizeCurrentCredentialDispatch(FoundationCredentialDispatchBinding binding,
        PipelineWireRequest wire, EntitlementVerifier verifier, string runtimeVersion, CancellationToken cancellationToken,
        FoundationCredentialUseSession? session = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var scope = CredentialRules.Snapshot(binding.Scope);
        Scope(scope.CustomerId);
        if (scope.CellId != CellId) throw new FoundationStoreException("CredentialDispatchCellMismatch", 403);
        ValidateId(binding.JobId); ValidateId(binding.WorkerId);
        if (binding.Fence <= 0 || !ValidDatabaseInstanceId(binding.DatabaseInstanceId))
            throw new FoundationStoreException("CredentialDispatchBindingInvalid", 403);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        AssertActivation(db, tx);
        RequireNoRecoveryExecutionHold(db, tx);
        RequireCredentialRuntimeDispatch(db, tx, session);
        if (ReadInstanceIdentity(db, tx, CustomerId, CellId).DatabaseInstanceId != binding.DatabaseInstanceId)
            throw new FoundationStoreException("CredentialDispatchInstanceChanged", 403);
        var job = ReadJob(db, tx, binding.JobId);
        Lease(job, binding.WorkerId, binding.Fence, "Dispatched");
        if (job.ProjectId != scope.ProjectId || job.EnvironmentId != scope.EnvironmentId)
            throw new FoundationStoreException("CredentialDispatchScopeMismatch", 403);
        var plan = ReadPlan(db, tx, job.PlanId);
        ValidatePlanAuthority(db, tx, plan);
        if (Require(db, tx, job.OperatorKey, job.ProjectId, job.EnvironmentId, "Operator") != job.OperatorVersion)
            throw new FoundationStoreException("OperatorGrantChanged", 403);
        if (plan.Operation != "pipeline.dispatch" || !plan.ChangesExternalState || plan.ApproverKey is null)
            throw new FoundationStoreException("CredentialDispatchPlanInvalid", 403);
        MatchDigest(plan.Digest, binding.PlanDigest);
        if (plan.SourceRevision != binding.SourceRevision || plan.CatalogDigest != binding.CatalogDigest)
            throw new FoundationStoreException("CredentialDispatchSourceChanged", 403);
        // The existing foundation admits only disconnected pipeline dispatch. No live feature is created here.
        RequireMockDispatchFeature(db, tx, plan.RequesterKey, plan.ProjectId, plan.EnvironmentId, verifier, runtimeVersion);
        var connection = FindConnection(db, tx, plan.ConnectionId)
            ?? throw new FoundationStoreException("ConnectionChanged", 403);
        cancellationToken.ThrowIfCancellationRequested();
        var permit = CredentialDispatchPermit.FromDurableDispatch(new(job, plan, connection), wire, scope, clock.GetUtcNow());
        cancellationToken.ThrowIfCancellationRequested();
        tx.Commit(); return permit;
    }
}
