using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SelfService.Portal;

public record AgentWorkflow(string Id, string Name, string SkillId, string? Audience, string Scope, string Limits);
public record AgentRequest(string Workflow, string? SubscriptionId, string? ResourceGroup, string? Product, string? Environment, string? Region, int? PreviewRunId, string? NetworkReportId = null);
public sealed class AgentSession : IAsyncDisposable
{
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public CodexAgentRuntime? Runtime { get; set; }
    public CancellationTokenSource? Run { get; set; }
    public string State { get; set; } = "Not connected";
    public async ValueTask DisposeAsync() { Run?.Cancel(); var runtime = Runtime; Runtime = null; State = "Not connected"; if (runtime is not null) await runtime.DisposeAsync(); }
}
public sealed class AgentWorkflows(PortalOptions options, Catalog catalog, AzureDiscovery azure, AdoGateway ado)
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    public static readonly AgentWorkflow[] Definitions = [
        new("visualize", "Azure resource visualizer", "azure--azure-resource-visualizer", "azure", "resource-group", "Selected group or browser-owned network snapshot. Mermaid is validated against evidence before image-only rendering. Data plane, identities and reachability remain unverified."),
        new("network", "Private network evidence review", "azure--azure-resource-visualizer", "azure", "resource-group", "Review configured VNets, subnets, peerings, NSGs, routes, DNS links and endpoints from selected scope. Partial visibility remains unknown. No free-IP or allocation approval."),
        new("workload", "Workload configuration advisor", "platform-request-design", null, "workload", "Explain the registered catalog, resource composition and onboarding. This adapter does not create or resolve a typed request or queue a pipeline."),
        new("preview", "Saved Preview change review", "platform-change-review", "ado", "preview", "Review the saved, bounded Preview resource-action projection. Full plan digests, ownership, ZIP contents and deployment gate outcome remain outside this projection.")
    ];
    public static AgentWorkflow Find(string id) => Definitions.SingleOrDefault(w => w.Id == id) ?? throw new PortalException("Unknown agent workflow.");
    public static bool Authorized(BrowserSession s, AgentWorkflow w) => w.Audience is null || s.Connected.ContainsKey(w.Audience);
    public async Task<object> Status(BrowserSession s, CancellationToken ct)
    {
        object provider = new { ready = false, state = "Connect Codex", model = AgentPolicy.Model, effort = AgentPolicy.Effort, speed = "Standard" };
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
        return new { provider, state = s.Agent.State, installed = CodexAgentRuntime.FindExecutable(options.CodexPath) is not null,
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
    public async Task<object> Run(BrowserSession s, AgentRequest request, CancellationToken cancellation)
    {
        var workflow = Find(request.Workflow);
        if (!Authorized(s, workflow)) throw new PortalException($"Connect {workflow.Audience} before running this workflow.", 401);
        if (!await s.Agent.Gate.WaitAsync(0, cancellation)) throw new PortalException("An agent action is already in progress.", 409);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellation);
        timeout.CancelAfter(TimeSpan.FromMinutes(5)); s.Agent.Run = timeout;
        var id = Guid.NewGuid().ToString("N"); var started = DateTimeOffset.UtcNow;
        var folder = Path.Combine(options.RepositoryRoot, "artifacts/portal-agents", id); Directory.CreateDirectory(folder);
        AgentMcpBridge? bridge = null;
        var inferenceStarted = false;
        try
        {
            var runtime = s.Agent.Runtime ?? throw new PortalException("Connect Codex first.", 401);
            var readiness = JsonSerializer.SerializeToElement(await runtime.Status(timeout.Token));
            if (!readiness.GetProperty("ready").GetBoolean()) throw new PortalException("Complete Codex browser sign-in first.", 401);
            s.Agent.State = "Running";
            object evidence;
            if (workflow.Scope == "resource-group")
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
                if (workflow.Scope == "preview")
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
            var diagramGraph = workflow.Scope == "resource-group" ? EvidenceDiagram.Build(JsonSerializer.SerializeToElement(evidence, Json)) : null;
            var frozen = JsonSerializer.Serialize(new { workflow, request, collectedUtc = DateTimeOffset.UtcNow, evidence, diagramGraph, deploymentAuthorized = false }, Json);
            if (Encoding.UTF8.GetByteCount(frozen) > 256 * 1024) throw new PortalException("Evidence exceeds the 256 KiB model budget. Select a smaller resource group; no silent truncation is allowed.", 413);
            await File.WriteAllTextAsync(Path.Combine(folder, "evidence.json"), frozen, timeout.Token);
            var skillPath = Path.Combine(runtime.Home, "SKILL.md");
            await File.WriteAllTextAsync(skillPath, skill.Content, timeout.Token);
            bridge = await AgentMcpBridge.Create(frozen, skill.Content + "\n\nADAPTER COVERAGE: " + workflow.Limits, () => Authorized(s, workflow) && !timeout.IsCancellationRequested, timeout.Token);
            runtime.ToolHandler = bridge.Call;
            inferenceStarted = true;
            var review = await runtime.Review(skillPath, skill.Name, bridge.Tools, timeout.Token);
            if (!bridge.EvidenceRead) throw new PortalException("Agent did not read both required evidence tools. Review rejected.", 502);
            var diagram = diagramGraph is null ? null : EvidenceDiagram.Validate(review, diagramGraph);
            if (diagram is not null)
            {
                await File.WriteAllTextAsync(Path.Combine(folder, "diagram.json"), JsonSerializer.Serialize(diagram, Json), timeout.Token);
                if (diagram.Source is not null) await File.WriteAllTextAsync(Path.Combine(folder, "diagram.mmd"), diagram.Source, timeout.Token);
            }
            var receipt = new { id, status = "Completed", workflow = workflow.Id, startedUtc = started, completedUtc = DateTimeOffset.UtcNow,
                model = AgentPolicy.Model, reasoning = AgentPolicy.Effort, serviceTier = AgentPolicy.Tier, skillId = skill.Id, skillHash,
                evidenceSha256 = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(frozen))).ToLowerInvariant(),
                diagramStatus = diagram?.Status, diagramSha256 = diagram?.Source is null ? null : Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(diagram.Source))).ToLowerInvariant(),
                toolCalls = bridge.Audit, deploymentAuthorized = false, limitations = workflow.Limits };
            await File.WriteAllTextAsync(Path.Combine(folder, "review.md"), review, timeout.Token);
            await File.WriteAllTextAsync(Path.Combine(folder, "receipt.json"), JsonSerializer.Serialize(receipt, Json), timeout.Token);
            s.Agent.State = "Completed";
            return new { receipt, review, diagram, path = $"artifacts/portal-agents/{id}" };
        }
        catch (Exception e)
        {
            s.Agent.State = e is OperationCanceledException ? "Cancelled or timed out" : "Failed";
            await File.WriteAllTextAsync(Path.Combine(folder, "receipt.json"), JsonSerializer.Serialize(new { id, status = s.Agent.State, workflow = workflow.Id,
                startedUtc = started, completedUtc = DateTimeOffset.UtcNow, model = AgentPolicy.Model, reasoning = AgentPolicy.Effort, serviceTier = AgentPolicy.Tier,
                toolCalls = bridge?.Audit, deploymentAuthorized = false }, Json), CancellationToken.None);
            // Stopping the owned child prevents a cancelled HTTP request from leaving inference running locally.
            if (inferenceStarted || e is OperationCanceledException)
            { if (s.Agent.Runtime is not null) await s.Agent.Runtime.DisposeAsync(); s.Agent.Runtime = null; }
            if (e is OperationCanceledException) throw new PortalException("Agent review cancelled or timed out. Reconnect Codex to retry; no success is inferred.", 408);
            throw;
        }
        finally { if (bridge is not null) await bridge.DisposeAsync(); s.Agent.Run = null; s.Agent.Gate.Release(); }
    }
}
