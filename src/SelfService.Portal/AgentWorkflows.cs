using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SelfService.Tagging;
using SelfService.Operations;

namespace SelfService.Portal;

public record AgentWorkflow(string Id, string Name, string SkillId, string? Audience, string Scope, string Limits);
public record AgentRequest(string Workflow, string? SubscriptionId, string? ResourceGroup, string? Product, string? Environment, string? Region, int? PreviewRunId, string? NetworkReportId = null, string? EvidenceId = null, string[]? ResourceIds = null, string? DraftGoal = null, string? IacPhase = null, string? IacArtifact = null);
public sealed class AgentSession : IAsyncDisposable
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public CodexAgentRuntime? Runtime { get; set; }
    public CancellationTokenSource? Run { get; set; }
    public string State { get; set; } = "Not connected";
    public void CancelRun() { try { Run?.Cancel(); } catch (ObjectDisposedException) { /* The completed run owns disposal. */ } }
    public async ValueTask DisposeAsync() { CancelRun(); var runtime = Runtime; Runtime = null; State = "Not connected"; if (runtime is not null) await runtime.DisposeAsync(); }
}
public sealed class AgentWorkflows(PortalOptions options, Catalog catalog, AzureDiscovery azure, AdoGateway ado, TaggingService? tagging = null, McpProviderBroker? mcp = null)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static AgentWorkflow[] Definitions => WorkflowReviews.Definitions;
    public static AgentWorkflow Find(string id) => Definitions.SingleOrDefault(w => w.Id == id) ?? throw new PortalException("Unknown agent workflow.");
    public static bool Authorized(BrowserSession s, AgentWorkflow w) => w.Audience is null || s.Connected.ContainsKey(w.Audience);
    public static bool Authorized(BrowserSession s, AgentWorkflow w, long identityEpoch) => identityEpoch == Interlocked.Read(ref s.MicrosoftIdentityEpoch) && Authorized(s, w);
    public async Task<object> Status(BrowserSession s, CancellationToken ct)
    {
        object provider = new { ready = false, authenticated = false, state = "Connect Codex", model = AgentPolicy.Model, effort = AgentPolicy.Effort, speed = "Standard" };
        if (s.Agent.Runtime is { } runtime && s.Agent.State != "Running")
        {
            if (await s.Agent.Gate.WaitAsync(0, ct))
            {
                try { provider = await runtime.Status(ct); }
                catch (PortalException e) { provider = new { ready = false, state = e.Message }; }
                catch (ObjectDisposedException) { provider = new { ready = false, state = "Connect Codex" }; }
                finally { s.Agent.Gate.Release(); }
            }
        }
        var ready = JsonSerializer.SerializeToElement(provider).GetProperty("ready").GetBoolean();
        if (ready && s.Agent.State == "Waiting for Codex") s.Agent.State = "Ready";
        return new { provider, policy = AgentPolicy.Descriptor, state = s.Agent.State, mcp = mcp?.Status(s, false), installed = CodexAgentRuntime.FindExecutable(options.CodexPath) is not null,
            workflows = Definitions.Select(w => new { w.Id, w.Name, w.SkillId, w.Audience, w.Scope, w.Limits,
                ready = ready && Authorized(s, w) && s.Agent.State != "Running", evidenceReady = Authorized(s, w),
                requires = w.Audience is null ? "Codex" : $"Codex + {w.Audience}" }) };
    }
    public async Task<object> Connect(BrowserSession s, CancellationToken ct)
    {
        if (!await s.Agent.Gate.WaitAsync(0, ct)) throw new PortalException("An agent action is already in progress.", 409);
        try
        {
            s.Agent.Runtime ??= await CodexAgentRuntime.Start(options, ct);
            var url = await s.Agent.Runtime.Login(ct); s.Agent.State = "Waiting for Codex";
            return new { url };
        }
        finally { s.Agent.Gate.Release(); }
    }
    public async Task<object> Run(BrowserSession s, AgentRequest request, CancellationToken cancellation, string hostTransport = "http")
    {
        var workflow = Find(request.Workflow);
        var identityEpoch = Interlocked.Read(ref s.MicrosoftIdentityEpoch);
        if (!Authorized(s, workflow, identityEpoch)) throw new PortalException($"Connect {workflow.Audience} before running this workflow.", 401);
        if (!await s.Agent.Gate.WaitAsync(0, cancellation)) throw new PortalException("An agent action is already in progress.", 409);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5)); s.Agent.Run = timeout;
        var id = Guid.NewGuid().ToString("N"); var started = DateTimeOffset.UtcNow;
        var folder = Path.Combine(options.RepositoryRoot, "artifacts/portal-agents", id); Directory.CreateDirectory(folder);
        AgentMcpBridge? bridge = null;
        CodexAgentRuntime? inferenceRuntime = null;
        try
        {
            if (!Authorized(s, workflow, identityEpoch)) throw new PortalException("Microsoft identity changed before review. Start a new workflow.", 401);
            var runtime = s.Agent.Runtime ?? throw new PortalException("Connect Codex first.", 401);
            var readiness = JsonSerializer.SerializeToElement(await runtime.Status(timeout.Token));
            if (!readiness.GetProperty("ready").GetBoolean()) throw new PortalException("Complete Codex browser sign-in first.", 401);
            s.Agent.State = "Running";
            object evidence; TagInventory? tagEvidence = null; TagProfile? tagProfile = null; BicepDraftContext? draftContext = null; OperationsReviewContext? operationsContext = null;
            if (workflow.Scope == "mcp")
            {
                if (mcp is null || !s.Mcp.Connections.Values.Any(c => c.State == "ready" && c.Identity == McpProviderBroker.Identity(s, c.Id))) throw new PortalException("Connect at least one MCP provider before orchestration.", 409);
                if (string.IsNullOrWhiteSpace(request.DraftGoal) || request.DraftGoal.Length > 8000) throw new PortalException("Describe the orchestration task in one to 8000 characters.");
                evidence = new { goal = request.DraftGoal, repositoryRoot = options.RepositoryRoot, providers = mcp.Status(s, false), existingPlans = McpProviderBroker.Plans(s), executionBoundary = "Reads can run through broker. Changes require exact human approval; no approval tool is available to the model." };
            }
            else if (workflow.Scope == "operations")
            {
                operationsContext = OperationsService.ReviewContext(s, request.EvidenceId ?? "", workflow.Id);
                evidence = operationsContext;
            }
            else if (workflow.Scope == "composition")
            {
                draftContext = BicepDrafts.Context(catalog.Root, catalog, s, request);
                evidence = draftContext;
            }
            else if (workflow.Scope == "tag-evidence")
            {
                if (tagging is null) throw new PortalException("Tagging adapter unavailable.",409);
                var full = tagging.Evidence(s, request.EvidenceId ?? ""); tagProfile = tagging.ProfileFor(s,full.SubscriptionId);
                if (request.ResourceIds is not { Length: > 0 and <= 50 } || request.ResourceIds.Distinct().Count()!=request.ResourceIds.Length || request.ResourceIds.Any(id=>!full.Resources.Any(r=>r.Id==id))) throw new PortalException("Select 1–50 observed resources for the model review.");
                tagEvidence = full with { Resources=full.Resources.Where(r=>request.ResourceIds.Contains(r.Id)).Select(r=>r with { Tags=r.Tags.Where(p=>tagProfile.RequiredKeys.Concat(tagProfile.ApprovedCustomKeys).Contains(p.Key,StringComparer.OrdinalIgnoreCase)).ToDictionary(p=>p.Key,p=>p.Value) }).ToArray() };
                evidence = new { inventory=tagEvidence, findings=TagRules.Analyze(tagEvidence,tagProfile), rules=new { tagProfile.RequiredKeys, tagProfile.ApprovedCustomKeys, protectedKeys=TagRules.Protected, customPrefix="custom." }, projection="Explicit selected resources and approved tag keys only; the digest identifies the original saved inventory." };
            }
            else if (workflow.Scope == "resource-group")
            {
                if (request.NetworkReportId is not null)
                {
                    if (!s.NetworkReports.TryGetValue(request.NetworkReportId, out var snapshot) ||
                        snapshot.GetProperty("generatedUtc").GetDateTimeOffset() < DateTimeOffset.UtcNow.AddMinutes(-15))
                        throw new PortalException("Network snapshot is missing, belongs to another browser or is older than 15 minutes. Run discovery again.", 409);
                    evidence = snapshot;
                }
                else
                {
                if (string.IsNullOrWhiteSpace(request.ResourceGroup) || string.IsNullOrWhiteSpace(request.SubscriptionId)) throw new PortalException("Select a subscription and resource group before analysis.");
                // Prove the group exists and is visible; a 404 is not an empty group.
                var groups = await azure.ResourceGroups(s, request.SubscriptionId, timeout.Token);
                if (groups.Status is not ("Succeeded" or "SucceededEmpty") || !groups.Resources.Any(r => JsonSerializer.SerializeToElement(r, Json).GetProperty("name").GetString() == request.ResourceGroup))
                    throw new PortalException("Resource group visibility could not be verified. Refresh the resource-group list.", 409);
                evidence = await azure.Discover(s, new(workflow.SkillId, request.SubscriptionId, request.ResourceGroup), timeout.Token);
                }
            }
            else
            {
                var product = catalog.Products.SingleOrDefault(p => p.Id == request.Product) ?? throw new PortalException("Select a registered workload.");
                var target = product.Targets.SingleOrDefault(t => t.Environment == request.Environment) ?? throw new PortalException("Select a registered environment.");
                if (request.Region is null || !catalog.Regions.Contains(request.Region)) throw new PortalException("Select an approved region.");
                if (workflow.Scope == "iac")
                {
                    if(request.PreviewRunId is not >0 || request.IacPhase is not("before" or "planned" or "after"))throw new PortalException("Choose an IaC run and evidence phase.");
                    if(request.IacArtifact is not("preview" or "after") || (request.IacPhase=="after" && request.IacArtifact!="after"))throw new PortalException("Select the matching Preview or Deploy evidence artifact.");
                    evidence=await ado.Iac(s,product.Id,request.PreviewRunId.Value,target.Environment,request.IacArtifact=="after",timeout.Token);
                    var iac=JsonSerializer.SerializeToElement(evidence,Json);
                    if(iac.GetProperty("provenance").GetProperty("region").GetString()!=request.Region)throw new PortalException("IaC report does not match the selected region.");
                }
                else if (workflow.Scope == "preview")
                {
                    if (request.PreviewRunId is not > 0) throw new PortalException("Select a saved Preview run ID.");
                    evidence = await ado.Preview(s, product.Id, request.PreviewRunId.Value, timeout.Token);
                    var p = JsonSerializer.SerializeToElement(evidence, Json);
                    if (p.GetProperty("environment").GetString() != target.Environment || p.GetProperty("region").GetString() != request.Region)
                        throw new PortalException("Preview does not match the selected environment and region.");
                }
                else evidence = new { kind = "proposed-workload", product, target, region = request.Region,
                    topology = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(catalog.Root, "config/portal-topologies.json"))).GetProperty("products").GetProperty(product.Id), deploymentAuthorized = false };
            }
            var skill = catalog.Skills.Single(x => x.Id == workflow.SkillId);
            var skillHash = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(skill.Content))).ToLowerInvariant();
            var diagramGraph = workflow.Scope == "resource-group" ? EvidenceDiagram.Build(JsonSerializer.SerializeToElement(evidence, Json)) : workflow.Scope == "iac" ? IacAnalysis.Graph(JsonSerializer.SerializeToElement(evidence,Json),request.IacPhase!) : null;
            var frozen = JsonSerializer.Serialize(new { workflow, request, collectedUtc = DateTimeOffset.UtcNow, evidence, diagramGraph, selectedPhase = request.IacPhase, deploymentAuthorized = false }, Json);
            if (Encoding.UTF8.GetByteCount(frozen) > 256 * 1024) throw new PortalException("Evidence exceeds the 256 KiB model budget. Select a smaller resource group; no silent truncation is allowed.", 413);
            await File.WriteAllTextAsync(Path.Combine(folder, "evidence.json"), frozen, timeout.Token);
            var skillPath = Path.Combine(runtime.Home, "SKILL.md");
            await File.WriteAllTextAsync(skillPath, skill.Content, timeout.Token);
            bridge = await AgentMcpBridge.Create(frozen, skill.Content + "\n\nADAPTER COVERAGE: " + workflow.Limits + (workflow.Scope == "iac" ? " Compare before, predicted and observed-after facts separately. Inspect qualification blockers, child probe receipts and propertyCoverage. Current observed values must never replace unresolved desired expressions; missing properties are unknown, not defaults. Suggest explicit source parameters or staged qualification where required. Nested property discovery is not proof of full template expansion. Draw only the selectedPhase diagramGraph. " + EvidenceDiagram.Instructions.Replace("For visualize or network", "For IaC") : ""), () => Authorized(s, workflow, identityEpoch) && !timeout.IsCancellationRequested, timeout.Token);
            if (workflow.Scope == "mcp") bridge.EnableProviders(mcp!, s);
            runtime.ToolHandler = bridge.Call;
            inferenceRuntime = runtime;
            var review = await runtime.Review(skillPath, skill.Name, bridge.Tools, timeout.Token, tagEvidence is not null || draftContext is not null || operationsContext is not null, workflow.Scope == "mcp");
            await File.WriteAllTextAsync(Path.Combine(folder, "model-output.md"), review, timeout.Token);
            if (!bridge.EvidenceRead) throw new PortalException("Agent did not read both required evidence tools. Review rejected.", 502);
            if (!Authorized(s, workflow, identityEpoch)) throw new PortalException("Microsoft identity changed during review. Start a new workflow.", 401);
            BicepDraftResult? draft = draftContext is null ? null : BicepDrafts.Generate(catalog.Root, draftContext, review);
            if (draft is not null)
                foreach (var (path, content) in draft.Files)
                {
                    var file = Path.Combine(folder, "source-draft", path);
                    Directory.CreateDirectory(Path.GetDirectoryName(file)!);
                    await File.WriteAllTextAsync(file, content, timeout.Token);
                }
            WorkflowReview? structuredReview = tagEvidence is null ? null : WorkflowReviews.Tags(review,tagEvidence,tagProfile!);
            OperationsAdvice? operationsAdvice = operationsContext is null ? null : OperationsRules.Advice(review, operationsContext.Assessment, operationsContext.Evidence);
            var diagram = diagramGraph is null ? null : EvidenceDiagram.Validate(review, diagramGraph);
            if (diagram is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(folder, "diagram.json"), JsonSerializer.Serialize(diagram, Json), timeout.Token);
                if (diagram.Source is not null) await File.WriteAllTextAsync(Path.Combine(folder, "diagram.mmd"), diagram.Source, timeout.Token);
            }
            var receipt = new { id, status = "Completed", workflow = workflow.Id, startedUtc = started, completedUtc = DateTimeOffset.UtcNow,
                model = AgentPolicy.Model, reasoning = AgentPolicy.Effort, serviceTier = AgentPolicy.Tier, hostTransport, skillId = skill.Id, skillHash,
                evidenceSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(frozen))).ToLowerInvariant(),
                outputValidation = operationsAdvice is not null ? "Accepted operations advice; no execution authority" : draft is not null ? "Validated composition contract; unqualified source draft" : structuredReview is null ? "Advisory prose" : "Accepted structured advice", diagramStatus = diagram?.Status, diagramSha256 = diagram?.Source is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(diagram.Source))).ToLowerInvariant(),
                toolCalls = bridge.Audit, providerPlans = workflow.Scope == "mcp" ? McpProviderBroker.Plans(s) : [], deploymentAuthorized = false, limitations = workflow.Limits };
            await File.WriteAllTextAsync(Path.Combine(folder, "review.md"), review, timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(folder, "receipt.json"), JsonSerializer.Serialize(receipt, Json), timeout.Token);
            if(structuredReview is not null) await File.WriteAllTextAsync(Path.Combine(folder,"review.json"),TagJson.Write(structuredReview),timeout.Token);
            if(operationsAdvice is not null) await File.WriteAllTextAsync(Path.Combine(folder,"operations-advice.json"),OperationsJson.Write(operationsAdvice),timeout.Token);
            if (!Authorized(s, workflow, identityEpoch)) throw new PortalException("Microsoft identity changed during review. Start a new workflow.", 401);
            s.Agent.State = "Completed";
            return new { receipt, review, structuredReview, operationsAdvice, diagram, draft, path = $"artifacts/portal-agents/{id}" };
        }
        catch (Exception e)
        {
            s.Agent.State = e is OperationCanceledException ? "Cancelled or timed out" : "Failed";
            await File.WriteAllTextAsync(Path.Combine(folder, "receipt.json"), JsonSerializer.Serialize(new { id, status = s.Agent.State, workflow = workflow.Id,
                startedUtc = started, completedUtc = DateTimeOffset.UtcNow, model = AgentPolicy.Model, reasoning = AgentPolicy.Effort, serviceTier = AgentPolicy.Tier, hostTransport,
                toolCalls = bridge?.Audit, inferenceAttempted = inferenceRuntime?.InferenceAttempted == true,
                failure = e is PortalException pe ? pe.Message : e is OperationCanceledException ? "Cancelled or timed out" : "Workflow could not complete",
                deploymentAuthorized = false }, Json), CancellationToken.None);
            // Stopping the owned child prevents a cancelled HTTP request from leaving inference running locally.
            if (inferenceRuntime?.TurnInProgress == true || e is OperationCanceledException)
            { if (s.Agent.Runtime is not null) await s.Agent.Runtime.DisposeAsync(); s.Agent.Runtime = null; }
            if (e is OperationCanceledException) throw new PortalException("Agent review cancelled or timed out. Reconnect Codex to retry; no success is inferred.", 408);
            throw;
        }
        finally { if (bridge is not null) await bridge.DisposeAsync(); s.Agent.Run = null; s.Agent.Gate.Release(); }
    }
}
