using System.Text.Json;
using SelfService.Foundation;
using SelfService.Foundation.Contracts;
using SelfService.Portal;

namespace SelfService.Foundation.Host;

public sealed record FoundationScope(string CustomerId, string ProjectId, string EnvironmentId);
public sealed record FoundationPlanReference(string Id, string Digest, string IdempotencyKey);
public sealed record FoundationObjectReference(string Id);
public sealed record FoundationCatalogCommand(string? ModuleId = null, string? Parameter = null, int Offset = 0, int Limit = 10);
public sealed record FoundationBicepCommand(string SubscriptionId, string Region, string Goal,
    BicepNode[] Modules, Dictionary<string, JsonElement> Settings);

// HTTP, AHP and model tools share this service. Caller identities and scope never come from model arguments.
public sealed class FoundationWorkflowService(FoundationStore store, FoundationHostOptions options,
    CustomerContextResolver resolver, EntitlementVerifier? entitlements = null)
{
    readonly EntitlementVerifier verifier = entitlements ?? new(options.EntitlementTrustKeys, requireSignedMode: options.RequireSignedEntitlement);
    public const string RuntimeVersion = FoundationExecutionRuntime.RuntimeVersion;
    public FoundationFeatureDecision RequireFeature(CustomerContext c, string feature) =>
        store.RequireFeature(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId, feature, verifier, RuntimeVersion);
    public CustomerContext Require(FoundationPrincipal principal, FoundationScope scope, string capability)
    {
        var context = resolver.Require(principal, scope.CustomerId, scope.ProjectId, scope.EnvironmentId, capability);
        if (context.SupportValidUntilUtc is not null) store.RecordSupportAccess(context); return context;
    }

    public FoundationPlan CreatePlan(FoundationPrincipal principal, FoundationScope scope, PlanCommand body)
    {
        var c = Require(principal, scope, FoundationCapabilities.RequestCreate);
        RequireFeature(c, FoundationFeatures.PipelinePlan);
        if (body.LifetimeMinutes is < 1 or > 60) throw new FoundationStoreException("InvalidLifetime", 400);
        var saved = store.GetConnection(c.CustomerId, c.ActorKey, body.ConnectionId, c.ProjectId, c.EnvironmentId);
        var connection = FoundationApplication.LoadConnection(saved, c.CellId);
        var request = new PipelineDispatchRequest(c.CustomerId, c.CellId, saved.Id, saved.Generation,
            Guid.NewGuid().ToString("D"), body.Ref, c.EnvironmentId, body.SourceRevision, body.Inputs);
        _ = PipelineRequestFactory.RequestDigest(connection, request);
        return store.CreatePlan(c.CustomerId, c.ActorKey, new(c.ProjectId, c.EnvironmentId, saved.Id,
            "pipeline.dispatch", JsonSerializer.Serialize(request, FoundationApplication.Json), true,
            DateTimeOffset.UtcNow.AddMinutes(body.LifetimeMinutes), body.SourceRevision));
    }

    public FoundationPlan ReadPlan(FoundationPrincipal principal, FoundationScope scope, string id)
    {
        var c = Require(principal, scope, FoundationCapabilities.EvidenceRead);
        return store.GetPlan(c.CustomerId, c.ActorKey, id, c.ProjectId, c.EnvironmentId);
    }

    // Human HTTP route only. Neither AHP nor MCP advertises approval.
    public FoundationPlan Approve(FoundationPrincipal principal, FoundationScope scope, string id, string digest)
    {
        var c = Require(principal, scope, FoundationCapabilities.PlanApprove);
        var plan = store.GetPlan(c.CustomerId, c.ActorKey, id, c.ProjectId, c.EnvironmentId);
        AuthorizationPolicy.RequireSeparation(c, plan.RequesterKey);
        return store.ApprovePlan(c.CustomerId, c.ActorKey, id, digest);
    }

    public FoundationJob Enqueue(FoundationPrincipal principal, FoundationScope scope, string id, string digest, string key)
    {
        var c = Require(principal, scope, FoundationCapabilities.JobDispatch);
        RequireFeature(c, FoundationFeatures.PipelineMockDispatch);
        var plan = store.GetPlan(c.CustomerId, c.ActorKey, id, c.ProjectId, c.EnvironmentId);
        if (options.DisconnectedTransport is null && options.BoundPipelineConsumer is null) throw new FoundationStoreException("ExternalDispatchDisabled", 503);
        FoundationApplication.ValidateBoundConsumer(store, options);
        using var run = options.BoundPipelineConsumer?.OpenRun(store);
        if (run is not null)
        {
            var saved = store.GetConnection(c.CustomerId, c.ActorKey, plan.ConnectionId, c.ProjectId, c.EnvironmentId);
            run.RequireConnection(FoundationApplication.LoadConnection(saved, c.CellId), c.ProjectId, c.EnvironmentId);
        }
        return run is null ? store.EnqueuePlan(c.CustomerId, c.ActorKey, id, digest, key) :
            run.EnqueuePlan(c.CustomerId, c.ActorKey, id, digest, key);
    }

    public FoundationJob ReadJob(FoundationPrincipal principal, FoundationScope scope, string id)
    {
        var c = Require(principal, scope, FoundationCapabilities.EvidenceRead);
        return store.GetJob(c.CustomerId, c.ActorKey, id, c.ProjectId, c.EnvironmentId);
    }

    public FoundationJob Cancel(FoundationPrincipal principal, FoundationScope scope, string id)
    {
        var c = Require(principal, scope, FoundationCapabilities.JobCancel);
        _ = store.GetJob(c.CustomerId, c.ActorKey, id, c.ProjectId, c.EnvironmentId);
        return store.CancelJob(c.CustomerId, c.ActorKey, id);
    }

    public object ArtifactMetadata(FoundationPrincipal principal, FoundationScope scope, string id)
    {
        var c = Require(principal, scope, FoundationCapabilities.EvidenceRead);
        var a = store.GetArtifact(c.CustomerId, c.ActorKey, id, c.ProjectId, c.EnvironmentId);
        return new { a.Id, a.Name, a.Sha256, bytes = a.Content.Length, a.CreatedAt, deploymentVerified = false };
    }

    public object GenerateBicep(FoundationPrincipal principal, FoundationScope scope, FoundationBicepCommand body)
    {
        var c = Require(principal, scope, FoundationCapabilities.RequestCreate);
        RequireFeature(c, FoundationFeatures.ParameterizedBicep);
        if (options.RepositoryRoot is null || !Guid.TryParseExact(body.SubscriptionId, "D", out var subscription) || subscription == Guid.Empty ||
            body.Goal is not { Length: > 0 and <= 2000 } || body.Region is null ||
            !System.Text.RegularExpressions.Regex.IsMatch(body.Region, "\\A[a-z][a-z0-9]{1,31}\\z") ||
            body.Settings is null || body.Modules is not { Length: > 0 and <= 16 })
            throw new FoundationStoreException("InvalidBicepRequest", 400);
        // This scan prevents accidental credential-shaped source settings entering durable/model context.
        _ = FoundationStore.CanonicalJson(JsonSerializer.Serialize(new { body.Goal, body.Settings }, FoundationApplication.Json));
        var modules = BicepDrafts.Catalog(options.RepositoryRoot).Modules.Where(m => m.DraftSelectable && m.Id != "network-ipam-reservation").ToArray();
        var catalogDigest = BicepDrafts.Hash(JsonSerializer.Serialize(modules, new JsonSerializerOptions(JsonSerializerDefaults.Web) { WriteIndented = true }));
        var evidenceDigest = BicepDrafts.Hash(JsonSerializer.Serialize(new { c.CustomerId, c.CellId, c.ProjectId, c.EnvironmentId,
            catalogDigest, body.SubscriptionId, body.Region, body.Goal, body.Modules, body.Settings }, FoundationApplication.Json));
        var context = new BicepDraftContext("platform.bicep-context/v1", evidenceDigest, catalogDigest, body.SubscriptionId,
            "foundation-source-draft", c.EnvironmentId, body.Region, body.Goal, "NotCollected", body.Settings, [], modules);
        var proposal = new BicepProposal("platform.bicep-proposal/v1", evidenceDigest, "Customer-scoped unqualified source draft", body.Modules,
            ["No Azure discovery, provider authorization, deployment preview or deployment acceptance."]);
        BicepDraftResult result;
        try { result = BicepDrafts.Generate(options.RepositoryRoot, context, JsonSerializer.Serialize(proposal, FoundationApplication.Json)); }
        catch (PortalException) { throw new FoundationStoreException("BicepProposalRejected", 400); }
        var archive = store.StoreArtifact(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId, "source-draft.zip", Convert.FromBase64String(result.ArchiveBase64));
        return new { archive.Id, archive.Sha256, archive.Name, fileCount = result.Files.Count, result.RequiredInputs, result.Gaps,
            catalogDigest, evidenceDigest, discoveryStatus = "NotCollected", deploymentVerified = false, deploymentAuthorized = false };
    }

    public object Catalog(FoundationPrincipal principal, FoundationScope scope, FoundationCatalogCommand command)
    {
        var c = Require(principal, scope, FoundationCapabilities.CatalogRead);
        RequireFeature(c, FoundationFeatures.SourceCatalog);
        if (options.RepositoryRoot is null) throw new FoundationStoreException("SourceCatalogUnavailable", 503);
        if (command.Offset < 0 || command.Limit is < 1 or > 20) throw new FoundationStoreException("InvalidPage", 400);
        var modules = BicepDrafts.Catalog(options.RepositoryRoot).Modules.Where(m => m.DraftSelectable && m.Id != "network-ipam-reservation").ToArray();
        if (command.ModuleId is null)
        {
            if (command.Parameter is not null) throw new FoundationStoreException("ModuleRequired", 400);
            return new { total = modules.Length, offset = command.Offset, modules = modules.Skip(command.Offset).Take(command.Limit)
                .Select(m => new { m.Id, m.SourceSha256, parameters = m.Parameters.Keys, outputs = m.Outputs.Keys, m.ResourceTypes }), deploymentAuthorized = false };
        }
        var module = modules.SingleOrDefault(m => m.Id == command.ModuleId) ?? throw new FoundationStoreException("ModuleNotFound", 404);
        if (command.Parameter is null) return module;
        if (!module.Parameters.TryGetValue(command.Parameter, out var schema)) throw new FoundationStoreException("ParameterNotFound", 404);
        return new { module.Id, module.SourceSha256, parameter = command.Parameter, schema = BicepParameterSchema.Expand(schema, module.Definitions) };
    }

    public Task<object> DispatchTool(FoundationPrincipal principal, FoundationScope scope, string name, JsonElement arguments, CancellationToken ct)
    {
        ct.ThrowIfCancellationRequested(); FoundationApplication.Unique(arguments);
        if (arguments.ValueKind != JsonValueKind.Object) throw new JsonException();
        object result = name switch
        {
            FoundationAgentToolContracts.Catalog => Catalog(principal, scope, Parse<FoundationCatalogCommand>(arguments)),
            FoundationAgentToolContracts.Bicep => GenerateBicep(principal, scope, Parse<FoundationBicepCommand>(arguments)),
            FoundationAgentToolContracts.Plan => CreatePlan(principal, scope, Parse<PlanCommand>(arguments)),
            FoundationAgentToolContracts.Job => ReadJob(principal, scope, Parse<FoundationObjectReference>(arguments).Id),
            FoundationAgentToolContracts.Enqueue => EnqueueReference(Parse<FoundationPlanReference>(arguments)),
            FoundationAgentToolContracts.Cancel => Cancel(principal, scope, Parse<FoundationObjectReference>(arguments).Id),
            FoundationAgentToolContracts.Artifact => ArtifactMetadata(principal, scope, Parse<FoundationObjectReference>(arguments).Id),
            _ => throw new FoundationAuthorizationException("foundation_tool_denied")
        };
        return Task.FromResult(result);
        FoundationJob EnqueueReference(FoundationPlanReference r) => Enqueue(principal, scope, r.Id, r.Digest, r.IdempotencyKey);
    }
    static T Parse<T>(JsonElement e) => e.Deserialize<T>(FoundationApplication.Json) ?? throw new JsonException();
    public void RecordDenied(FoundationPrincipal principal) => store.RecordSecurityEvent("AgentActionDenied", Guid.NewGuid().ToString("N"), principal.ActorKey);
}
