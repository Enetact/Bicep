using SelfService.Foundation;

namespace SelfService.Foundation.Host;

internal sealed class AdministrativeMembershipSource(FoundationStore store) : ICustomerMembershipSource
{
    public IReadOnlyList<MembershipGrant> Find(string customerId, string actorKey) => store.GetAdministrativeMemberships(customerId, actorKey)
        .Select(m => new MembershipGrant(m.CustomerId, m.CellId, m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version, m.Enabled)).ToArray();
}
internal sealed class AdministrativeContextResolver(FoundationHostOptions options, FoundationStore store)
{
    public CustomerContextResolver Resolver { get; } = new(options.Identity, new AdministrativeMembershipSource(store));
}
internal sealed class OperationalMembershipSource(FoundationStore store) : ICustomerMembershipSource
{
    public IReadOnlyList<MembershipGrant> Find(string customerId, string actorKey) => store.GetOperationalMemberships(customerId, actorKey)
        .Select(m => new MembershipGrant(m.CustomerId, m.CellId, m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version, m.Enabled)).ToArray();
}
internal sealed class OperationalContextResolver(FoundationHostOptions options, FoundationStore store)
{
    public CustomerContextResolver Resolver { get; } = new(options.Identity, new OperationalMembershipSource(store));
}
internal sealed class RecoveryMembershipSource(FoundationStore store) : ICustomerMembershipSource
{
    public IReadOnlyList<MembershipGrant> Find(string customerId, string actorKey) => store.GetRecoveryMemberships(customerId, actorKey)
        .Select(m => new MembershipGrant(m.CustomerId, m.CellId, m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version, m.Enabled)).ToArray();
}
internal sealed class RecoveryContextResolver(FoundationHostOptions options, FoundationStore store)
{
    public CustomerContextResolver Resolver { get; } = new(options.Identity, new RecoveryMembershipSource(store));
}
public sealed record RecoveryReconcileCommand(string CaseSha256, string Outcome, System.Text.Json.JsonElement Evidence);
public sealed record ScopeOnboardingCommand(string ProjectId, string EnvironmentId, string InitialAdministrator, string Purpose, string? InitialApprover = null);
public sealed record CustomerLifecycleCommand(string Action, string Purpose);
public sealed record ArtifactRetentionCommand(DateTimeOffset CutoffUtc, string Purpose);
public sealed record ReviewedMembershipCommand(string ActorKey, string[] Roles, bool Enabled, string Purpose);
public sealed record ReviewedSupportCommand(string ActorKey, DateTimeOffset ValidUntilUtc, string Purpose);
public sealed record RevokeAccessCommand(string ActorKey, string Purpose);
public sealed record FeatureConfigurationCommand(FoundationFeatureConfiguration Configuration, long ExpectedGeneration, string Purpose);
public sealed record FeatureRollbackCommand(long TargetGeneration, long ExpectedGeneration, string Purpose);
public sealed record PagedScopeExportCommand(int PageSize = 100, int LifetimeMinutes = 15);
public sealed record PagedScopeExportReadCommand(string Cursor, string SnapshotSha256);

internal static class FoundationAdministration
{
    public static void Map(WebApplication app, FoundationStore store)
    {
        var root = FoundationApplication.Root;
        static FoundationApplication.Capability Cap(string name) => new(name);
        static CustomerContext C(HttpContext h) => FoundationApplication.Context(h);
        app.MapGet(root + "/recovery/jobs", (HttpContext h) =>
        {
            var c = C(h); return Results.Json(store.ListRecoveryCases(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId), FoundationApplication.Json);
        }).WithMetadata(Cap(FoundationCapabilities.RecoveryRead));
        app.MapGet(root + "/recovery/jobs/{jobId}", (HttpContext h, string jobId) =>
        {
            var c = C(h); return Results.Json(store.GetRecoveryCase(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId, jobId), FoundationApplication.Json);
        }).WithMetadata(Cap(FoundationCapabilities.RecoveryRead));
        app.MapPost(root + "/recovery/jobs/{jobId}/reconcile", async (HttpContext h, string jobId) =>
        {
            var b = await FoundationApplication.Body<RecoveryReconcileCommand>(h); var c = C(h);
            return Results.Json(store.ReconcileRecoveryCase(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId,
                jobId, b.CaseSha256, b.Outcome, b.Evidence.GetRawText()), FoundationApplication.Json);
        }).WithMetadata(Cap(FoundationCapabilities.RecoveryReconcile));
        app.MapGet(root + "/operations/health", (HttpContext h) =>
        {
            var c = C(h); return Results.Json(store.OperationalStatus(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId), FoundationApplication.Json);
        }).WithMetadata(Cap(FoundationCapabilities.CellHealthRead));
        app.MapGet(root + "/operations/metrics", (HttpContext h) =>
        {
            var c = C(h);
            var cell = store.OperationalStatus(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId);
            var workers = store.WorkerHealth(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId);
            var capacity = store.CapacityStatus(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId);
            return Results.Json(new { schema = "platform-studio.cell-metrics.v1", cell, workers, capacity,
                process = h.RequestServices.GetRequiredService<FoundationCellTelemetry>().Snapshot(),
                externalExecutionAuthorized = false }, FoundationApplication.Json);
        }).WithMetadata(Cap(FoundationCapabilities.CellHealthRead));
        app.MapGet(root + "/operations/workers", (HttpContext h) =>
        {
            var c = C(h); return Results.Json(store.WorkerHealth(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId), FoundationApplication.Json);
        }).WithMetadata(Cap(FoundationCapabilities.CellHealthRead));
        app.MapGet(root + "/administration/configuration", (HttpContext h) =>
        {
            var c = C(h); return Results.Json(store.ReadConfiguration(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId), FoundationApplication.Json);
        }).WithMetadata(Cap(FoundationCapabilities.LifecycleRead));
        app.MapPost(root + "/administration/configuration/plan", async (HttpContext h) =>
        {
            var b = await FoundationApplication.Body<FeatureConfigurationCommand>(h); var c = C(h);
            return Results.Json(store.PrepareConfigurationChange(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId,
                b.Configuration, b.ExpectedGeneration, b.Purpose, h.RequestServices.GetRequiredService<EntitlementVerifier>(),
                FoundationWorkflowService.RuntimeVersion), FoundationApplication.Json, statusCode: 201);
        }).WithMetadata(Cap(FoundationCapabilities.LifecycleManage));
        app.MapPost(root + "/administration/configuration/rollback/plan", async (HttpContext h) =>
        {
            var b = await FoundationApplication.Body<FeatureRollbackCommand>(h); var c = C(h);
            return Results.Json(store.PrepareConfigurationRollback(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId,
                b.TargetGeneration, b.ExpectedGeneration, b.Purpose, h.RequestServices.GetRequiredService<EntitlementVerifier>(),
                FoundationWorkflowService.RuntimeVersion), FoundationApplication.Json, statusCode: 201);
        }).WithMetadata(Cap(FoundationCapabilities.LifecycleManage));
        app.MapGet(root + "/administration/state", (HttpContext h) =>
        {
            var c = C(h); return Results.Json(store.CustomerLifecycle(c.CustomerId, c.ActorKey), FoundationApplication.Json);
        }).WithMetadata(Cap(FoundationCapabilities.LifecycleRead));
        app.MapGet(root + "/administration/scopes", (HttpContext h) =>
        {
            var c = C(h); return Results.Json(store.ListProjectScopes(c.CustomerId, c.ActorKey), FoundationApplication.Json);
        }).WithMetadata(Cap(FoundationCapabilities.LifecycleRead));
        app.MapPost(root + "/administration/scopes/plan", async (HttpContext h) =>
        {
            var c = C(h); RequireBootstrap(h, c); var b = await FoundationApplication.Body<ScopeOnboardingCommand>(h);
            FoundationApplication.ValidateMembershipActor(h.RequestServices.GetRequiredService<FoundationHostOptions>().Identity, b.InitialAdministrator);
            if (b.InitialApprover is not null) FoundationApplication.ValidateMembershipActor(h.RequestServices.GetRequiredService<FoundationHostOptions>().Identity, b.InitialApprover);
            return Results.Json(store.PrepareScopeOnboarding(c.CustomerId, c.ActorKey, b.ProjectId, b.EnvironmentId,
                b.InitialAdministrator, b.Purpose, b.InitialApprover), FoundationApplication.Json, statusCode: 201);
        }).WithMetadata(Cap(FoundationCapabilities.LifecycleManage));
        app.MapPost(root + "/administration/lifecycle/plan", async (HttpContext h) =>
        {
            var c = C(h); RequireBootstrap(h, c); var b = await FoundationApplication.Body<CustomerLifecycleCommand>(h);
            return Results.Json(store.PrepareLifecycleChange(c.CustomerId, c.ActorKey, b.Action, b.Purpose), FoundationApplication.Json, statusCode: 201);
        }).WithMetadata(Cap(FoundationCapabilities.LifecycleManage));
        app.MapGet(root + "/administration/plans/{id}", (HttpContext h, string id) =>
        {
            var c = C(h); return Results.Json(store.GetAdministrativePlan(c.CustomerId, c.ActorKey, id, c.ProjectId, c.EnvironmentId), FoundationApplication.Json);
        }).WithMetadata(Cap(FoundationCapabilities.LifecycleRead));
        app.MapPost(root + "/administration/plans/{id}/approve", async (HttpContext h, string id) =>
        {
            var c = C(h); _ = store.GetAdministrativePlan(c.CustomerId, c.ActorKey, id, c.ProjectId, c.EnvironmentId);
            var b = await FoundationApplication.Body<ExactPlanCommand>(h);
            return Results.Json(store.ApproveAdministrativePlan(c.CustomerId, c.ActorKey, id, b.Digest), FoundationApplication.Json);
        }).WithMetadata(Cap(FoundationCapabilities.LifecycleApprove));
        app.MapPost(root + "/administration/plans/{id}/execute", async (HttpContext h, string id) =>
        {
            var c = C(h); var plan = store.GetAdministrativePlan(c.CustomerId, c.ActorKey, id, c.ProjectId, c.EnvironmentId);
            var b = await FoundationApplication.Body<ExactPlanCommand>(h);
            object result = plan.Kind switch
            {
                "scope.onboard" => store.ExecuteScopeOnboarding(c.CustomerId, c.ActorKey, id, b.Digest),
                "cell.lifecycle" => store.ExecuteLifecycleChange(c.CustomerId, c.ActorKey, id, b.Digest),
                "artifact.retention" => store.ExecuteArtifactRetention(c.CustomerId, c.ActorKey, id, b.Digest),
                "membership.change" => store.ExecuteMembershipChange(c.CustomerId, c.ActorKey, id, b.Digest),
                "support.access" => store.ExecuteSupportAccess(c.CustomerId, c.ActorKey, id, b.Digest),
                "config.update" => store.ExecuteConfigurationChange(c.CustomerId, c.ActorKey, id, b.Digest,
                    h.RequestServices.GetRequiredService<EntitlementVerifier>(), FoundationWorkflowService.RuntimeVersion),
                _ => throw new FoundationStoreException("AdministrativeOperationRejected", 400)
            };
            return Results.Json(result, FoundationApplication.Json);
        }).WithMetadata(Cap(FoundationCapabilities.LifecycleManage));
        app.MapPost(root + "/administration/exports", (HttpContext h) =>
        {
            var c = C(h); return Results.Json(store.ExportScope(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId), FoundationApplication.Json, statusCode: 201);
        }).WithMetadata(Cap(FoundationCapabilities.LifecycleExport));
        app.MapGet(root + "/administration/exports/{id}", (HttpContext h, string id) =>
        {
            var c = C(h); return Results.Json(store.GetScopeExport(c.CustomerId, c.ActorKey, id, c.ProjectId, c.EnvironmentId), FoundationApplication.Json);
        }).WithMetadata(Cap(FoundationCapabilities.LifecycleExport));
        app.MapPost(root + "/administration/export-snapshots", async (HttpContext h) =>
        {
            var b = await FoundationApplication.Body<PagedScopeExportCommand>(h); var c = C(h);
            return Results.Json(store.CreatePagedScopeExport(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId,
                b.PageSize, TimeSpan.FromMinutes(b.LifetimeMinutes), h.RequestAborted), FoundationApplication.Json, statusCode: 201);
        }).WithMetadata(Cap(FoundationCapabilities.LifecycleExport));
        // Cursors belong in POST bodies, outside URL/access logs. They never replace current auditor authorization.
        app.MapPost(root + "/administration/export-snapshots/{id}/page", async (HttpContext h, string id) =>
        {
            var b = await FoundationApplication.Body<PagedScopeExportReadCommand>(h); var c = C(h);
            return Results.Json(store.ReadPagedScopeExportPage(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId,
                id, b.Cursor, b.SnapshotSha256), FoundationApplication.Json);
        }).WithMetadata(Cap(FoundationCapabilities.LifecycleExport));
        app.MapPost(root + "/administration/retention/plan", async (HttpContext h) =>
        {
            var c = C(h); var b = await FoundationApplication.Body<ArtifactRetentionCommand>(h);
            return Results.Json(store.PrepareArtifactRetention(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId,
                b.CutoffUtc, b.Purpose), FoundationApplication.Json, statusCode: 201);
        }).WithMetadata(Cap(FoundationCapabilities.LifecycleManage));
        app.MapPost(root + "/administration/memberships/plan", async (HttpContext h) =>
        {
            var c = C(h); var b = await FoundationApplication.Body<ReviewedMembershipCommand>(h);
            FoundationApplication.ValidateMembershipActor(h.RequestServices.GetRequiredService<FoundationHostOptions>().Identity, b.ActorKey);
            return Results.Json(store.PrepareMembershipChange(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId, b.ActorKey,
                b.Roles, b.Enabled, b.Purpose), FoundationApplication.Json, statusCode: 201);
        }).WithMetadata(Cap(FoundationCapabilities.LifecycleManage));
        app.MapPost(root + "/administration/support/plan", async (HttpContext h) =>
        {
            var c = C(h); var b = await FoundationApplication.Body<ReviewedSupportCommand>(h);
            FoundationApplication.ValidateMembershipActor(h.RequestServices.GetRequiredService<FoundationHostOptions>().Identity, b.ActorKey);
            return Results.Json(store.PrepareSupportAccess(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId, b.ActorKey,
                b.ValidUntilUtc, b.Purpose), FoundationApplication.Json, statusCode: 201);
        }).WithMetadata(Cap(FoundationCapabilities.LifecycleManage));
        app.MapPost(root + "/administration/access/revoke", async (HttpContext h) =>
        {
            var c = C(h); var b = await FoundationApplication.Body<RevokeAccessCommand>(h);
            FoundationApplication.ValidateMembershipActor(h.RequestServices.GetRequiredService<FoundationHostOptions>().Identity, b.ActorKey);
            return Results.Json(store.RevokeScopedAccess(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId, b.ActorKey, b.Purpose), FoundationApplication.Json);
        }).WithMetadata(Cap(FoundationCapabilities.LifecycleManage));
    }
    static void RequireBootstrap(HttpContext h, CustomerContext c)
    {
        var options = h.RequestServices.GetRequiredService<FoundationHostOptions>();
        if (c.ProjectId != options.ProjectId || c.EnvironmentId != options.EnvironmentId) throw new FoundationAuthorizationException("administrative_scope");
    }
}
