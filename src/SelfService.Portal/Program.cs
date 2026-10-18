using System.Security.Cryptography;
using System.Text.Json;
using SelfService.Portal;

if (args.FirstOrDefault() is "--checkpoint-credentials" or "--get-resume-proof")
{
    Environment.ExitCode = PortalCredentialCheckpoint.RunCommand(args); return;
}

var builder = WebApplication.CreateBuilder(new WebApplicationOptions { Args = args,
    ContentRootPath = Directory.Exists(Path.Combine(AppContext.BaseDirectory, "wwwroot")) ? AppContext.BaseDirectory : null });
builder.Configuration.AddJsonFile(Path.Combine(builder.Environment.ContentRootPath, "portal.local.json"), optional: true).AddEnvironmentVariables("STUDIO_");
var options = builder.Configuration.GetSection("Portal").Get<PortalOptions>() ?? new();
if (string.IsNullOrWhiteSpace(options.RepositoryRoot))
{
    var bundled = Path.Combine(AppContext.BaseDirectory, "repository");
    if (File.Exists(Path.Combine(bundled, "config/workloads.json"))) options.RepositoryRoot = bundled;
    else
    {
        var dir = new DirectoryInfo(builder.Environment.ContentRootPath);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config/workloads.json"))) dir = dir.Parent;
        options.RepositoryRoot = dir?.FullName ?? bundled;
    }
}
options.RepositoryRoot = Path.GetFullPath(options.RepositoryRoot); options.Validate();
builder.WebHost.ConfigureKestrel(k => { k.ListenLocalhost(options.Port); k.Limits.MaxRequestBodySize = 46 * 1024 * 1024; });
builder.Logging.ClearProviders(); builder.Logging.AddSimpleConsole();
builder.Logging.AddFilter("Microsoft.AspNetCore", LogLevel.Warning).AddFilter("System.Net.Http", LogLevel.None);
builder.Services.AddSingleton(options).AddSingleton<Catalog>().AddSingleton<Sessions>().AddSingleton<BrowserIdentity>().AddSingleton<AnalysisRunner>();
builder.Services.AddSingleton<NamingPlan>();
builder.Services.AddSingleton<IAzureCliRunner, AzureCliRunner>().AddSingleton<AzureCliIdentity>();
builder.Services.AddSingleton<IGitHubCliRunner, GitHubCliRunner>();
builder.Services.AddHttpClient<GitHubIdentity>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(30))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddTransient<IGitHubIdentity>(s => s.GetRequiredService<GitHubIdentity>());
builder.Services.AddSingleton<ITokenProvider>(s => s.GetRequiredService<BrowserIdentity>());
builder.Services.AddTransient<AgentWorkflows>();
builder.Services.AddSingleton<IProviderMcpFactory, McpProviderFactory>();
builder.Services.AddTransient<IProviderConnectionResolver, McpProviderCredentials>();
builder.Services.AddTransient<McpProviderBroker>();
builder.Services.AddTransient<OperationsService>();
builder.Services.AddHttpClient<PrivateDnsReader>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(45))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddTransient<NetworkPipeline>();
builder.Services.AddHttpClient<TaggingService>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(45))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<PipelineRegistration>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(45))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<AdoGateway>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(45))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<AzureDiscovery>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(45))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<NetworkDiscovery>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(45))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
var app = builder.Build();
app.UseWebSockets();
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    context.Response.Headers.XContentTypeOptions = "nosniff"; context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Host.Value != $"localhost:{options.Port}") { context.Response.StatusCode = 403; return; }
    IDisposable? resumeAction = null;
    try
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site") throw new PortalException("Cross-site request rejected.", 403);
            var s = context.RequestServices.GetRequiredService<Sessions>().Get(context); context.Items["session"] = s;
            if (context.Request.Method != "GET" && (context.Request.Headers.Origin != options.Origin || context.Request.Headers["X-Portal-CSRF"] != s.Csrf))
                throw new PortalException("Session or request origin is invalid. Reload the portal.", 403);
            if (context.Request.Method != "GET" && context.Request.Path != "/api/auth/resume") resumeAction = s.ResumeGate.BeginAction();
        }
        await next(context);
    }
    catch (PortalException e) { context.Response.StatusCode = e.Status; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (SelfService.Tagging.TagException e) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (SelfService.Operations.OperationsException e) { context.Response.StatusCode = 409; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (Exception) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = "Operation could not complete. For a queue request, check ADO runs before retrying; it may have been accepted." }); }
    finally { resumeAction?.Dispose(); }
});
BrowserSession Session(HttpContext c) => (BrowserSession)c.Items["session"]!;
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/api/bootstrap", (HttpContext c, Catalog catalog) => new { csrf = Session(c).Csrf, configured = options.AuthenticationConfigured,
    naming = new { draftCalculations = true, deploymentSubmission = false, previewInstances = catalog.NamingInstances },
    packagedCatalog = options.RepositoryRoot == Path.Combine(AppContext.BaseDirectory, "repository"),
    organization = options.Organization, project = options.Project, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    products = catalog.Products, topologies = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(catalog.Root, "config/portal-topologies.json"))), regions = catalog.Regions, skills = catalog.Skills.Select(s => new { s.Id, s.Name, s.Description, s.Origin, s.PipelineStatus, s.DiscoveryProfile, s.SourceUrl }),
    discoveryScopes = catalog.Products.SelectMany(p => p.Targets).DistinctBy(t => t.SubscriptionId).Select(t => new { id = t.SubscriptionId, name = t.Subscription }) });
app.MapGet("/api/auth", (HttpContext c, AzureCliIdentity cli) => { var s = Session(c); return new { state = s.State, error = s.Error, account = s.Cli?.Username ?? s.Account?.Username, tenantId = s.AzureTenantId, mode = s.Cli is not null ? "local-cli" : s.Account is not null ? "registered-app" : null, localCliAvailable = cli.Available, connected = s.Connected.Keys.ToArray() }; });
app.MapPost("/api/auth/resume", async (HttpContext c, PortalResumeRequest request, AzureCliIdentity cli, AgentWorkflows agents, GitHubIdentity github, McpProviderBroker broker) =>
{
    var s = Session(c);
    if (!s.ResumeGate.TryBeginResume()) throw new PortalException("Another session action is active. Wait before restoring restart credentials.", 409);
    try
    {
        var cache = new PortalCredentialCheckpoint(options.RepositoryRoot, options.Port);
        using var handoff = cache.Open(request.Proof, s.Csrf, s.Cli is not null || s.Account is not null || s.Login is not null || s.Agent.Runtime is not null || s.GitHub.UserId is not null || s.GitHub.Login is not null);
        var restored = new List<string>(); var errors = new List<string>();
        if (handoff.Data.AzureFiles.Count != 0)
        {
            try { await cli.Resume(s, handoff.Data.AzureFiles); handoff.AzureRestored(); restored.AddRange(s.Connected.Keys); if (s.Error is not null) errors.Add(s.Error); }
            catch { errors.Add("Microsoft cached sign-in could not be verified. Reconnect Microsoft in Connections."); }
        }
        if (handoff.Data.CodexAuth is { } auth)
        {
            try { await agents.Resume(s, auth, c.RequestAborted); handoff.CodexRestored(); restored.Add("codex"); }
            catch { errors.Add("Codex cached sign-in could not be verified. Reconnect Codex in Agent workflows."); }
        }
        if (handoff.Data.AttachGitHub)
        {
            try { if (s.GitHub.UserId is null) await github.Resume(s, c.RequestAborted); restored.Add("github"); }
            catch { errors.Add("GitHub CLI sign-in could not be verified. Use Connections to reconnect."); }
            finally { handoff.GitHubAttempted(); }
        }
        // Catalog discovery recreates provider clients; no model turn, tool call or saved plan is run.
        foreach (var provider in new[] { "bicep", "learn", "azure", "ado", "github" })
        {
            if (provider == "azure" && !s.Connected.ContainsKey("azure") || provider == "ado" && !s.Connected.ContainsKey("ado") || provider == "github" && s.GitHub.UserId is null) continue;
            try { await broker.Connect(s, provider, c.RequestAborted); if (!s.Mcp.Connections.TryGetValue(provider, out var connection) || connection.State != "ready") errors.Add($"{provider} tool catalog needs reconnection in Connections."); }
            catch { errors.Add($"{provider} tool catalog needs reconnection in Connections."); }
        }
        cache.RecordResult(s, errors); handoff.Complete();
        return Results.Ok(new { restored, errors, message = "Fresh session created. Workflow evidence, plans and approvals were cleared." });
    }
    finally { s.ResumeGate.EndResume(); }
});
app.MapPost("/api/local-auth/{audience}", (HttpContext c, string audience, LocalAuthRequest request, AzureCliIdentity cli) => { cli.Begin(Session(c), audience, request.TenantId); return Results.Accepted(); });
app.MapPost("/api/auth/{audience}", (HttpContext c, string audience, BrowserIdentity identity) => { identity.Begin(Session(c), audience); return Results.Accepted(); });
app.MapPost("/api/cancel-login", (HttpContext c) => { try { Session(c).Login?.Cancel(); } catch (ObjectDisposedException) { } return Results.Ok(); });
app.MapPost("/api/disconnect", async (HttpContext c, BrowserIdentity identity) => { var s = Session(c); await identity.SignOut(s); if (OperatingSystem.IsWindows()) new PortalCredentialCheckpoint(options.RepositoryRoot, options.Port).Forget(s.Csrf, "microsoft"); return Results.Ok(); });
app.MapGet("/api/github/status", (HttpContext c, GitHubIdentity identity) => identity.Status(Session(c)));
app.MapPost("/api/github/connect", (HttpContext c, GitHubIdentity identity) => { identity.Begin(Session(c), false); return Results.Ok(); });
app.MapPost("/api/github/attach", (HttpContext c, GitHubIdentity identity) => { identity.Begin(Session(c), true); return Results.Ok(); });
app.MapPost("/api/github/disconnect", (HttpContext c, GitHubIdentity identity) => { identity.Disconnect(Session(c)); return Results.Ok(); });
app.MapGet("/api/skills/{id}", (string id, Catalog catalog) => catalog.Skills.SingleOrDefault(s => s.Id == id) ?? throw new PortalException("Unknown skill.", 404));
app.MapGet("/api/bicep/modules", () => BicepDrafts.Catalog(options.RepositoryRoot));
app.MapGet("/api/recovery/policies", (Catalog catalog) => catalog.RecoveryPolicies);
app.MapGet("/api/subscriptions", async (HttpContext c, AdoGateway ado) => await ado.Subscriptions(Session(c)));
app.MapGet("/api/discovery/{product}", async (HttpContext c, string product, AdoGateway ado) => await ado.DiscoveryRuns(Session(c), product));
app.MapPost("/api/review", async (HttpContext c, RunRequest request, Catalog catalog, AdoGateway ado) =>
{
    var s = Session(c); await ado.ValidateHandoff(s, request); var payload = catalog.Payload(request);
    lock (s.Pending)
    {
        foreach (var key in s.Pending.Where(p => p.Value.Expires < DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) s.Pending.Remove(key);
        if (s.Pending.Count >= 8) throw new PortalException("Too many pending reviews. Wait five minutes.", 429);
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(24)); s.Pending[ticket] = new(request, DateTimeOffset.UtcNow.AddMinutes(5));
        return new { ticket, payload, warning = request.Operation == "discover" ? "Read-only inventory. Uses an ADO agent." : "Preview uses Azure What-If and temporary metadata. Deploy can create billable resources. ADO guards still apply." };
    }
});
app.MapPost("/api/queue/{ticket}", async (HttpContext c, string ticket, AdoGateway ado) =>
{
    var s = Session(c); PendingRequest pending;
    lock (s.Pending) { if (!s.Pending.Remove(ticket, out pending!) || pending.Expires < DateTimeOffset.UtcNow) throw new PortalException("Review expired or already submitted. Check existing ADO runs before reviewing again.", 409); }
    return await ado.Queue(s, pending.Request);
});
app.MapGet("/api/runs/{product}/{id:int}", async (HttpContext c, string product, int id, AdoGateway ado) => await ado.Status(Session(c), product, id));
app.MapGet("/api/preview/{product}/{id:int}", async (HttpContext c, string product, int id, AdoGateway ado) => await ado.Preview(Session(c), product, id, c.RequestAborted));
app.MapGet("/api/iac/{product}/{id:int}/{environment}/{phase}", async (HttpContext c,string product,int id,string environment,string phase,AdoGateway ado) => { if(phase is not ("preview" or "after"))throw new PortalException("Select preview or after evidence."); return await ado.Iac(Session(c),product,id,environment,phase=="after",c.RequestAborted); });
app.MapPost("/api/analysis", async (AnalysisUpload upload, AnalysisRunner runner, HttpContext c) => await runner.Run(upload, c.RequestAborted));
app.MapPost("/api/naming/draft", async (JsonElement request, NamingPlan naming, HttpContext c) => await naming.Generate(request, c.RequestAborted));
app.MapPost("/api/skill-discovery", async (SkillDiscoveryRequest request, AzureDiscovery discovery, HttpContext c) => await discovery.Discover(Session(c), request, c.RequestAborted));
app.MapGet("/api/network/scopes", async (NetworkDiscovery network, HttpContext c) => await network.Scopes(Session(c), c.RequestAborted));
app.MapGet("/api/tags/config", (TaggingService tags) => tags.Configuration());
app.MapGet("/api/tags/scopes", async (TaggingService tags, HttpContext c) => await tags.Scopes(Session(c), c.RequestAborted));
app.MapPost("/api/tags/discover", async (TagScopeRequest r, TaggingService tags, HttpContext c) => await tags.Discover(Session(c), r, c.RequestAborted));
app.MapGet("/api/tags/evidence/{id}", (string id, TaggingService tags, HttpContext c) => tags.Read(Session(c), id));
app.MapGet("/api/tags/discovery/{runId:int}", async (int runId, TaggingService tags, HttpContext c) => await tags.Load(Session(c), runId, c.RequestAborted));
app.MapGet("/api/tags/results/{runId:int}/{kind}", async (int runId, string kind, TaggingService tags, HttpContext c) => await tags.Results(Session(c), runId, kind, c.RequestAborted));
app.MapPost("/api/tags/drafts", (TagDraftRequest r, TaggingService tags, HttpContext c) => tags.Draft(Session(c), r));
app.MapPost("/api/tags/pipeline/review", async (TagQueueRequest r, TaggingService tags, HttpContext c) => await tags.Review(Session(c), r));
app.MapPost("/api/tags/pipeline/queue/{ticket}", async (string ticket, TaggingService tags, HttpContext c) => await tags.Queue(Session(c), ticket));
app.MapGet("/api/network/allocation", (NetworkPipeline network) => network.Status());
app.MapGet("/api/pipeline-setup/catalog", (PipelineRegistration setup) => setup.LocalCatalog());
app.MapGet("/api/pipeline-setup/inventory", async (PipelineRegistration setup, HttpContext c) => await setup.Inventory(Session(c), c.RequestAborted));
app.MapPost("/api/pipeline-setup/review", async (RegistrationSelection selection, PipelineRegistration setup, HttpContext c) => await setup.Review(Session(c), selection, c.RequestAborted));
app.MapPost("/api/pipeline-setup/apply/{ticket}", async (string ticket, PipelineRegistration setup, HttpContext c) => await setup.Apply(Session(c), ticket, c.RequestAborted));
app.MapPost("/api/network/review", async (NetworkAllocationRequest request, NetworkPipeline network, HttpContext c) => await network.Review(Session(c), request));
app.MapPost("/api/network/queue/{ticket}", async (string ticket, NetworkPipeline network, HttpContext c) => await network.Queue(Session(c), ticket));
app.MapPost("/api/network/discover", async (NetworkScopeRequest request, NetworkDiscovery network, HttpContext c) =>
{
    var s = Session(c); if (!await s.NetworkGate.WaitAsync(0)) throw new PortalException("A network scan is already active in this browser.", 409);
    try { return await network.Discover(s, request, c.RequestAborted); } finally { s.NetworkGate.Release(); }
});
app.MapGet("/api/operations/catalog", () => SelfService.Operations.OperationsRegistry.Catalog);
app.MapGet("/api/operations/scopes", (HttpContext c, OperationsService service) => service.Scopes(Session(c), c.RequestAborted));
app.MapGet("/api/operations/evidence", (HttpContext c, OperationsService service) => service.List(Session(c)));
app.MapGet("/api/operations/evidence/{id}", (HttpContext c, string id) => OperationsService.Evidence(Session(c), id, false));
app.MapPost("/api/operations/collect", (HttpContext c, OperationsCollectRequest request, OperationsService service) => service.Collect(Session(c), request, c.RequestAborted));
app.MapPost("/api/operations/assess", (HttpContext c, OperationsAssessRequest request, OperationsService service) => service.Assess(Session(c), request, c.RequestAborted));
app.MapPost("/api/operations/proposals", (HttpContext c, OperationsDraftRequest request, OperationsService service) => service.Draft(Session(c), request, c.RequestAborted));
app.MapPost("/api/operations/proposals/{id}/preview", (HttpContext c, string id) => OperationsService.Preview(Session(c), id));
app.MapGet("/api/agent/status", async (HttpContext c, AgentWorkflows agents) => await agents.Status(Session(c), c.RequestAborted));
app.MapGet("/api/mcp/status", (HttpContext c, McpProviderBroker broker) => broker.Status(Session(c)));
app.MapGet("/api/mcp/tools", (HttpContext c, McpProviderBroker broker, string? provider, string? search, int? offset, int? limit) => broker.Catalog(Session(c), provider, search, offset ?? 0, limit ?? 20));
app.MapPost("/api/mcp/{provider}/connect", (HttpContext c, string provider, McpProviderBroker broker) => broker.Connect(Session(c), provider, c.RequestAborted));
app.MapPost("/api/mcp/{provider}/disconnect", (HttpContext c, string provider, McpProviderBroker broker) => broker.Disconnect(Session(c), provider, c.RequestAborted));
app.MapGet("/api/mcp/plans", (HttpContext c) => new { plans = McpProviderBroker.Plans(Session(c)) });
app.MapGet("/api/mcp/plans/{id}", (HttpContext c, string id, McpProviderBroker broker) => McpProviderBroker.View(broker.ReadPlan(Session(c), id)));
app.MapPost("/api/mcp/plans", (HttpContext c, McpPlanRequest request, McpProviderBroker broker) => McpProviderBroker.View(broker.Plan(Session(c), request)));
app.MapPost("/api/mcp/plans/{id}/approve", (HttpContext c, string id, McpPlanConsent consent, McpProviderBroker broker) => broker.Approve(Session(c), id, consent.Digest));
app.MapPost("/api/mcp/plans/{id}/run", (HttpContext c, string id, McpPlanConsent consent, McpProviderBroker broker) => broker.Run(Session(c), id, consent.Digest, c.RequestAborted));
app.MapGet("/api/agent/ahp", async (HttpContext c, AgentWorkflows agents, McpProviderBroker broker) => await AgentHostChannel.Handle(c, Session(c), agents, options, broker));
app.MapPost("/api/agent/connect", async (HttpContext c, AgentWorkflows agents) => await agents.Connect(Session(c), c.RequestAborted));
app.MapPost("/api/agent/cancel", (HttpContext c) => { Session(c).Agent.Run?.Cancel(); return Results.Ok(); });
app.MapPost("/api/agent/disconnect", async (HttpContext c) => { var s = Session(c); await s.Agent.DisposeAsync(); if (OperatingSystem.IsWindows()) new PortalCredentialCheckpoint(options.RepositoryRoot, options.Port).Forget(s.Csrf, "codex"); return Results.Ok(); });
app.MapPost("/api/agent/cancel-login", async (HttpContext c) => { if (Session(c).Agent.Runtime is { } r) await r.CancelLogin(c.RequestAborted); return Results.Ok(); });
app.MapPost("/api/agent/run", async (HttpContext c, AgentRequest request, AgentWorkflows agents) => await agents.Run(Session(c), request, c.RequestAborted));
app.MapGet("/api/agent/resource-groups/{subscription}", async (HttpContext c, string subscription, AzureDiscovery azure) => await azure.ResourceGroups(Session(c), subscription, c.RequestAborted));
app.Run();
public partial class Program { }
public record LocalAuthRequest(string? TenantId);
