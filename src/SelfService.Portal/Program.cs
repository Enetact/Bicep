using System.Security.Cryptography;
using System.Text.Json;
using SelfService.Portal;

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
builder.Services.AddSingleton<ITokenProvider>(s => s.GetRequiredService<BrowserIdentity>());
builder.Services.AddTransient<AgentWorkflows>();
builder.Services.AddHttpClient<AdoGateway>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(45))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
builder.Services.AddHttpClient<AzureDiscovery>().ConfigureHttpClient(c => c.Timeout = TimeSpan.FromSeconds(45))
    .ConfigurePrimaryHttpMessageHandler(() => new HttpClientHandler { AllowAutoRedirect = false });
var app = builder.Build();
app.UseWebSockets();
app.Use(async (context, next) =>
{
    context.Response.Headers.ContentSecurityPolicy = "default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self'; object-src 'none'; base-uri 'none'; frame-ancestors 'none'; form-action 'self'";
    context.Response.Headers.XContentTypeOptions = "nosniff"; context.Response.Headers["Referrer-Policy"] = "no-referrer";
    context.Response.Headers.CacheControl = "no-store";
    if (context.Request.Host.Value != $"localhost:{options.Port}") { context.Response.StatusCode = 403; return; }
    try
    {
        if (context.Request.Path.StartsWithSegments("/api"))
        {
            if (context.Request.Headers["Sec-Fetch-Site"] == "cross-site") throw new PortalException("Cross-site request rejected.", 403);
            var s = context.RequestServices.GetRequiredService<Sessions>().Get(context); context.Items["session"] = s;
            if (context.Request.Method != "GET" && (context.Request.Headers.Origin != options.Origin || context.Request.Headers["X-Portal-CSRF"] != s.Csrf))
                throw new PortalException("Session or request origin is invalid. Reload the portal.", 403);
        }
        await next(context);
    }
    catch (PortalException e) { context.Response.StatusCode = e.Status; await context.Response.WriteAsJsonAsync(new { error = e.Message }); }
    catch (Exception) { context.Response.StatusCode = 500; await context.Response.WriteAsJsonAsync(new { error = "Operation could not complete. For a queue request, check ADO runs before retrying; it may have been accepted." }); }
});
BrowserSession Session(HttpContext c) => (BrowserSession)c.Items["session"]!;
app.UseDefaultFiles(); app.UseStaticFiles();
app.MapGet("/api/bootstrap", (HttpContext c, Catalog catalog) => new { csrf = Session(c).Csrf, configured = options.AuthenticationConfigured,
    packagedCatalog = options.RepositoryRoot == Path.Combine(AppContext.BaseDirectory, "repository"),
    organization = options.Organization, project = options.Project, architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString(),
    products = catalog.Products, topologies = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(catalog.Root, "config/portal-topologies.json"))), regions = catalog.Regions, skills = catalog.Skills.Select(s => new { s.Id, s.Name, s.Description, s.Origin, s.PipelineStatus, s.DiscoveryProfile, s.SourceUrl }),
    discoveryScopes = catalog.Products.SelectMany(p => p.Targets).DistinctBy(t => t.SubscriptionId).Select(t => new { id = t.SubscriptionId, name = t.Subscription }) });
app.MapGet("/api/auth", (HttpContext c) => { var s = Session(c); return new { state = s.State, error = s.Error, account = s.Account?.Username, connected = s.Connected.Keys.ToArray() }; });
app.MapPost("/api/auth/{audience}", (HttpContext c, string audience, BrowserIdentity identity) => { identity.Begin(Session(c), audience); return Results.Accepted(); });
app.MapPost("/api/cancel-login", (HttpContext c) => { try { Session(c).Login?.Cancel(); } catch (ObjectDisposedException) { } return Results.Ok(); });
app.MapPost("/api/disconnect", async (HttpContext c, BrowserIdentity identity) => { var s = Session(c); await identity.SignOut(s); await s.Agent.DisposeAsync(); return Results.Ok(); });
app.MapGet("/api/skills/{id}", (string id, Catalog catalog) => catalog.Skills.SingleOrDefault(s => s.Id == id) ?? throw new PortalException("Unknown skill.", 404));
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
app.MapPost("/api/analysis", async (AnalysisUpload upload, AnalysisRunner runner, HttpContext c) => await runner.Run(upload, c.RequestAborted));
app.MapPost("/api/skill-discovery", async (SkillDiscoveryRequest request, AzureDiscovery discovery, HttpContext c) => await discovery.Discover(Session(c), request, c.RequestAborted));
app.MapGet("/api/agent/status", async (HttpContext c, AgentWorkflows agents) => await agents.Status(Session(c), c.RequestAborted));
app.MapGet("/api/agent/ahp", async (HttpContext c, AgentWorkflows agents) => await AgentHostChannel.Handle(c, Session(c), agents, options));
app.MapPost("/api/agent/connect", async (HttpContext c, AgentWorkflows agents) => await agents.Connect(Session(c), c.RequestAborted));
app.MapPost("/api/agent/cancel", (HttpContext c) => { Session(c).Agent.Run?.Cancel(); return Results.Ok(); });
app.MapPost("/api/agent/disconnect", async (HttpContext c) => { await Session(c).Agent.DisposeAsync(); return Results.Ok(); });
app.MapPost("/api/agent/cancel-login", async (HttpContext c) => { if (Session(c).Agent.Runtime is { } r) await r.CancelLogin(c.RequestAborted); return Results.Ok(); });
app.MapPost("/api/agent/run", async (HttpContext c, AgentRequest request, AgentWorkflows agents) => await agents.Run(Session(c), request, c.RequestAborted));
app.MapGet("/api/agent/resource-groups/{subscription}", async (HttpContext c, string subscription, AzureDiscovery azure) => await azure.ResourceGroups(Session(c), subscription, c.RequestAborted));
app.Run();
public partial class Program { }
