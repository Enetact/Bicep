using System.Net;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using Microsoft.Data.Sqlite;
using System.Threading.RateLimiting;
using SelfService.Portal;
using SelfService.Foundation;

namespace SelfService.Foundation.Host;

public static class FoundationApplication
{
    public static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
        Converters = { new JsonStringEnumConverter() }
    };
    internal const string Root = "/api/foundation/customers/{customerId}/projects/{projectId}/environments/{environmentId}";
    internal sealed record Capability(string Name);

    public static WebApplication Build(FoundationHostOptions options, string[]? args = null,
        Action<WebApplicationBuilder>? configure = null)
    {
        if (options.ListenPort is < 1024 or > 65535 || options.PollInterval < TimeSpan.FromMilliseconds(10) || options.PollInterval > TimeSpan.FromMinutes(1) ||
            !CustomerContextResolver.ValidId(options.ProjectId) || !CustomerContextResolver.ValidId(options.EnvironmentId))
            throw new ArgumentException("Invalid cell host configuration.");
        if (options.DisconnectedTransport is IPipelineTransportCharacteristics { IsMock: false })
            throw new ArgumentException("A live transport cannot be supplied to a disconnected host.");
        var entitlements = new EntitlementVerifier(options.EntitlementTrustKeys, requireSignedMode: options.RequireSignedEntitlement);
        var builder = WebApplication.CreateBuilder(args ?? []);
        builder.WebHost.UseUrls($"http://127.0.0.1:{options.ListenPort}");
        builder.WebHost.ConfigureKestrel(k => k.Limits.MaxRequestBodySize = 131072);
        builder.Logging.ClearProviders(); // Request bodies, bearer tokens and identity claims are never console logged.
        builder.Services.AddSingleton(options);
        builder.Services.AddSingleton<FoundationCellTelemetry>();
        builder.Services.AddSingleton(entitlements);
        builder.Services.AddSingleton(new IdentityTokenValidator(options.Identity));
        builder.Services.AddSingleton(new FoundationTokenService(options));
        var store = new FoundationStore(options.DatabasePath, options.Identity.CellId, options.Identity.CustomerId);
        store.BootstrapAdministrator(options.BootstrapActorKey, options.ProjectId, options.EnvironmentId, options.BootstrapApproverKey);
        builder.Services.AddSingleton(store);
        builder.Services.AddSingleton<ICustomerMembershipSource, StoreMembershipSource>();
        builder.Services.AddSingleton(sp => new CustomerContextResolver(options.Identity, sp.GetRequiredService<ICustomerMembershipSource>()));
        builder.Services.AddSingleton(new PipelineDispatcher(options.DisconnectedTransport ?? new DeniedPipelineTransport()));
        builder.Services.AddSingleton<FoundationJobRunner>();
        builder.Services.AddSingleton<FoundationWorkflowService>();
        builder.Services.AddSingleton<FoundationModelRuntime>();
        builder.Services.AddSingleton<AdministrativeContextResolver>();
        builder.Services.AddSingleton<OperationalContextResolver>();
        builder.Services.AddRateLimiter(limits =>
        {
            limits.RejectionStatusCode = 429;
            limits.GlobalLimiter = PartitionedRateLimiter.CreateChained(
                PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetConcurrencyLimiter("cell",
                    _ => new ConcurrencyLimiterOptions { PermitLimit = 16, QueueLimit = 0 })),
                PartitionedRateLimiter.Create<HttpContext, string>(_ => RateLimitPartition.GetTokenBucketLimiter("cell",
                    _ => new TokenBucketRateLimiterOptions { TokenLimit = 120, TokensPerPeriod = 20,
                        ReplenishmentPeriod = TimeSpan.FromSeconds(1), AutoReplenishment = true, QueueLimit = 0 })));
        });
        if (options.EnableWorker) builder.Services.AddHostedService<FoundationWorker>();
        configure?.Invoke(builder);
        var app = builder.Build();
        app.Use(async (http, next) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            using var observation = http.RequestServices.GetRequiredService<FoundationCellTelemetry>().Start();
            var unhandled = false;
            try { await next(http); }
            catch { unhandled = true; throw; }
            finally { observation.Complete(http.Response.StatusCode, http.RequestAborted.IsCancellationRequested, unhandled); }
        });
        app.UseWebSockets(new WebSocketOptions { KeepAliveInterval = TimeSpan.FromSeconds(30) });
        app.UseRouting();
        app.UseRateLimiter();
        app.Use(async (http, next) =>
        {
            http.Response.Headers.CacheControl = "no-store";
            http.Response.Headers["X-Content-Type-Options"] = "nosniff";
            try
            {
                if (http.Connection.RemoteIpAddress is { } ip && !IPAddress.IsLoopback(ip) ||
                    http.Request.Host.Host is not ("127.0.0.1" or "localhost" or "[::1]"))
                    throw new FoundationAuthorizationException("loopback_required");
                if (http.Request.Path == "/health" && HttpMethods.IsGet(http.Request.Method)) { await next(http); return; }
                if (http.Request.Headers.ContainsKey("Origin") ||
                    http.Request.Headers.TryGetValue("Sec-Fetch-Site", out var site) && site != "none")
                    throw new FoundationAuthorizationException("api_client_required");
                var header = http.Request.Headers.Authorization;
                if (header.Count != 1 || !header[0]!.StartsWith("Bearer ", StringComparison.Ordinal) || header[0]!.Contains(','))
                    throw new FoundationAuthorizationException("bearer_required", 401);
                var principal = await http.RequestServices.GetRequiredService<FoundationTokenService>().ValidateAsync(header[0]![7..], http.RequestAborted);
                http.Items[typeof(FoundationPrincipal)] = principal;
                var capability = http.GetEndpoint()?.Metadata.GetMetadata<Capability>()
                    ?? throw new FoundationAuthorizationException("route_denied", 404);
                var resolver = capability.Name.StartsWith("lifecycle.", StringComparison.Ordinal)
                    ? http.RequestServices.GetRequiredService<AdministrativeContextResolver>().Resolver
                    : capability.Name == FoundationCapabilities.CellHealthRead ? http.RequestServices.GetRequiredService<OperationalContextResolver>().Resolver
                    : http.RequestServices.GetRequiredService<CustomerContextResolver>();
                http.Items[typeof(CustomerContext)] = resolver.Require(principal, Route(http, "customerId"),
                    Route(http, "projectId"), Route(http, "environmentId"), capability.Name);
                if (Context(http).SupportValidUntilUtc is not null) store.RecordSupportAccess(Context(http));
                await next(http);
            }
            catch (FoundationAuthorizationException e) { await Error(http, store, e.Code, e.StatusCode); }
            catch (FoundationStoreException e) { await Error(http, store, e.Code, e.Status); }
            catch (PipelineContractException e) { await Error(http, store, e.ReasonCode, 400); }
            catch (PortalException e) { await Error(http, store, "agent_policy_rejected", e.Status); }
            catch (BadHttpRequestException e) { await Error(http, store, "invalid_request", e.StatusCode); }
            catch (Exception e) when (e is JsonException or ArgumentException or FormatException or InvalidOperationException)
            { await Error(http, store, "invalid_request", 400); }
            catch (OperationCanceledException) when (http.RequestAborted.IsCancellationRequested) { }
            catch (Exception) { await Error(http, store, "service_unavailable", 503); }
        });
        app.MapGet("/health", () =>
        {
            bool ready;
            try { ready = store.StorageReady(); } catch (SqliteException) { ready = false; }
            return Results.Json(new { service = "platform-foundation", ready, externalDispatch = false }, statusCode: ready ? 200 : 503);
        });

        app.MapPut(Root + "/memberships", async (HttpContext h) =>
        {
            var c = Context(h); var b = await Body<MembershipCommand>(h);
            ValidateMembershipActor(options.Identity, b.ActorKey);
            if (b.Roles is null) throw new FoundationStoreException("InvalidRoles", 400);
            var existing = store.GetMemberships(c.CustomerId, b.ActorKey).SingleOrDefault(m => m.ProjectId == c.ProjectId && m.EnvironmentId == c.EnvironmentId);
            string[] privileged = [FoundationRoles.Approver, FoundationRoles.Operator, FoundationRoles.CustomerAdmin, FoundationRoles.Auditor, FoundationRoles.PlatformOperator];
            if (b.Enabled && b.Roles.Any(role => privileged.Contains(role, StringComparer.Ordinal) &&
                (existing is null || !existing.Enabled || !existing.Roles.Contains(role, StringComparer.Ordinal))))
                throw new FoundationStoreException("PrivilegedRoleReviewRequired", 403);
            return Results.Json(store.SetMembership(c.CustomerId, c.ActorKey, b.ActorKey, c.ProjectId, c.EnvironmentId, b.Roles, b.Enabled), Json);
        }).WithMetadata(new Capability(FoundationCapabilities.MembershipManage));
        app.MapPut(Root + "/connections/{connectionId}", async (HttpContext h, string connectionId) =>
        {
            var c = Context(h); var b = await Body<ConnectionCommand>(h);
            var candidate = Connection(c, connectionId, b, 1);
            PipelineRequestFactory.ValidateConnection(candidate);
            if (!candidate.AllowedEnvironments.Contains(c.EnvironmentId, StringComparer.Ordinal)) throw new FoundationAuthorizationException("connection_scope");
            return Results.Json(store.PutConnection(c.CustomerId, c.ActorKey, connectionId, b.Provider,
                JsonSerializer.Serialize(b, Json), c.ProjectId, c.EnvironmentId, b.Enabled), Json);
        }).WithMetadata(new Capability(FoundationCapabilities.ConnectionManage));
        app.MapPost(Root + "/plans", async (HttpContext h) =>
        {
            return Results.Json(Workflow(h).CreatePlan(Principal(h), Scope(h), await Body<PlanCommand>(h)), Json, statusCode: 201);
        }).WithMetadata(new Capability(FoundationCapabilities.RequestCreate));
        app.MapGet(Root + "/plans/{planId}", (HttpContext h, string planId) =>
        {
            return Results.Json(Workflow(h).ReadPlan(Principal(h), Scope(h), planId), Json);
        }).WithMetadata(new Capability(FoundationCapabilities.EvidenceRead));
        app.MapPost(Root + "/plans/{planId}/approve", async (HttpContext h, string planId) =>
        {
            var b = await Body<ExactPlanCommand>(h);
            return Results.Json(Workflow(h).Approve(Principal(h), Scope(h), planId, b.Digest), Json);
        }).WithMetadata(new Capability(FoundationCapabilities.PlanApprove));
        app.MapPost(Root + "/plans/{planId}/enqueue", async (HttpContext h, string planId) =>
        {
            var b = await Body<EnqueueCommand>(h);
            return Results.Json(Workflow(h).Enqueue(Principal(h), Scope(h), planId, b.Digest, b.IdempotencyKey), Json, statusCode: 202);
        }).WithMetadata(new Capability(FoundationCapabilities.JobDispatch));
        app.MapGet(Root + "/jobs", (HttpContext h) =>
        {
            var c = Context(h); return Results.Json(store.ListJobs(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId), Json);
        }).WithMetadata(new Capability(FoundationCapabilities.EvidenceRead));
        app.MapGet(Root + "/jobs/{jobId}", (HttpContext h, string jobId) =>
        {
            var c = Context(h); var j = store.GetJob(c.CustomerId, c.ActorKey, jobId, c.ProjectId, c.EnvironmentId); SameScope(c, j.ProjectId, j.EnvironmentId);
            return Results.Json(j, Json);
        }).WithMetadata(new Capability(FoundationCapabilities.EvidenceRead));
        app.MapPost(Root + "/jobs/{jobId}/cancel", (HttpContext h, string jobId) =>
        {
            var c = Context(h); var j = store.GetJob(c.CustomerId, c.ActorKey, jobId, c.ProjectId, c.EnvironmentId); SameScope(c, j.ProjectId, j.EnvironmentId);
            return Results.Json(store.CancelJob(c.CustomerId, c.ActorKey, jobId), Json);
        }).WithMetadata(new Capability(FoundationCapabilities.JobCancel));
        app.MapPost(Root + "/jobs/{jobId}/reconcile", async (HttpContext h, string jobId) =>
        {
            var c = Context(h); var b = await Body<ReconcileCommand>(h); var j = store.GetJob(c.CustomerId, c.ActorKey, jobId, c.ProjectId, c.EnvironmentId);
            SameScope(c, j.ProjectId, j.EnvironmentId);
            return Results.Json(store.ReconcileJob(c.CustomerId, c.ActorKey, jobId, b.Outcome, b.Evidence.GetRawText()), Json);
        }).WithMetadata(new Capability(FoundationCapabilities.JobReconcile));
        app.MapGet(Root + "/audit", (HttpContext h) =>
        {
            var c = Context(h); return Results.Json(new { integrityValid = store.VerifyAuditIntegrity(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId),
                events = store.ListAudit(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId) }, Json);
        }).WithMetadata(new Capability(FoundationCapabilities.AuditRead));
        app.MapPost(Root + "/artifacts", async (HttpContext h) =>
        {
            var c = Context(h); var b = await Body<ArtifactCommand>(h);
            return Results.Json(store.StoreArtifact(c.CustomerId, c.ActorKey, c.ProjectId, c.EnvironmentId, b.Name, Encoding.UTF8.GetBytes(b.Content)), Json, statusCode: 201);
        }).WithMetadata(new Capability(FoundationCapabilities.RequestCreate));
        app.MapGet(Root + "/artifacts/{artifactId}", (HttpContext h, string artifactId) =>
        {
            var c = Context(h); var a = store.GetArtifact(c.CustomerId, c.ActorKey, artifactId, c.ProjectId, c.EnvironmentId); SameScope(c, a.ProjectId, a.EnvironmentId);
            return Results.Json(a, Json);
        }).WithMetadata(new Capability(FoundationCapabilities.EvidenceRead));
        app.MapGet(Root + "/artifacts/{artifactId}/content", (HttpContext h, string artifactId) =>
        {
            var c = Context(h); var a = store.GetArtifact(c.CustomerId, c.ActorKey, artifactId, c.ProjectId, c.EnvironmentId);
            return Results.File(a.Content, a.Name.EndsWith(".zip", StringComparison.Ordinal) ? "application/zip" : "application/octet-stream", a.Name);
        }).WithMetadata(new Capability(FoundationCapabilities.EvidenceRead));
        app.MapPost(Root + "/source/bicep", async (HttpContext h) =>
            Results.Json(Workflow(h).GenerateBicep(Principal(h), Scope(h), await Body<FoundationBicepCommand>(h)), Json, statusCode: 201))
            .WithMetadata(new Capability(FoundationCapabilities.RequestCreate));
        app.MapGet(Root + "/agent/ahp", (HttpContext h) => FoundationAgentChannel.Handle(h, Scope(h)))
            .WithMetadata(new Capability(FoundationCapabilities.CatalogRead));
        app.MapGet(Root + "/agent/tools", async (HttpContext h) =>
        {
            var token = h.Request.Headers.Authorization[0]![7..];
            await using var bridge = await FoundationAgentChannel.CreateBridge(Workflow(h), Principal(h), Scope(h),
                ct => h.RequestServices.GetRequiredService<FoundationTokenService>().ValidateAsync(token, ct), h.RequestAborted);
            return Results.Json(new { tools = bridge.Tools, approvalTool = false, externalDispatch = false }, Json);
        }).WithMetadata(new Capability(FoundationCapabilities.CatalogRead));
        app.MapPost(Root + "/agent/tool", async (HttpContext h) =>
        {
            var body = await Body<FoundationToolCommand>(h); var token = h.Request.Headers.Authorization[0]![7..];
            await using var bridge = await FoundationAgentChannel.CreateBridge(Workflow(h), Principal(h), Scope(h),
                ct => h.RequestServices.GetRequiredService<FoundationTokenService>().ValidateAsync(token, ct), h.RequestAborted);
            return Results.Json(await bridge.Call(JsonSerializer.SerializeToElement(new { tool = body.Name, arguments = body.Arguments,
                @namespace = AgentPolicy.ToolNamespace }), h.RequestAborted), Json);
        }).WithMetadata(new Capability(FoundationCapabilities.CatalogRead));
        app.MapPost(Root + "/agent/review", async (HttpContext h) =>
        {
            var command = await Body<FoundationModelCommand>(h);
            var bearer = h.Request.Headers.Authorization[0]![7..];
            var model = h.RequestServices.GetRequiredService<FoundationModelRuntime>();
            return Results.Json(await model.ReviewAsync(Principal(h), Scope(h), command, h.RequestAborted,
                ct => h.RequestServices.GetRequiredService<FoundationTokenService>().ValidateAsync(bearer, ct)), Json);
        }).WithMetadata(new Capability(FoundationCapabilities.RequestCreate));
        FoundationAdministration.Map(app, store);
        return app;
    }

    static string Route(HttpContext h, string key) => h.Request.RouteValues[key]?.ToString() ?? "";
    internal static CustomerContext Context(HttpContext h) => (CustomerContext)h.Items[typeof(CustomerContext)]!;
    static FoundationPrincipal Principal(HttpContext h) => (FoundationPrincipal)h.Items[typeof(FoundationPrincipal)]!;
    static FoundationScope Scope(HttpContext h) => new(Route(h, "customerId"), Route(h, "projectId"), Route(h, "environmentId"));
    static FoundationWorkflowService Workflow(HttpContext h) => h.RequestServices.GetRequiredService<FoundationWorkflowService>();
    internal static void ValidateMembershipActor(CellIdentityOptions identity, string actor)
    {
        var parts = actor?.Split(':') ?? [];
        if (parts.Length is not (3 or 4) || parts[0] is not ("user" or "workload") ||
            parts[0] == "user" && parts.Length != 3 || parts[0] == "workload" && parts.Length != 4 ||
            parts.Skip(1).Any(p => !Guid.TryParseExact(p, "D", out var id) || id == Guid.Empty || id.ToString("D") != p) ||
            !identity.TrustedIssuersByTenant.ContainsKey(parts[1]) ||
            parts[0] == "workload" && !identity.AllowedClients.Any(c => c.Kind == PrincipalKind.Workload &&
                c.TenantId == parts[1] && c.ServicePrincipalObjectId == parts[2] && c.ClientId == parts[3]))
            throw new FoundationStoreException("InvalidMembershipIdentity", 400);
    }
    static void SameScope(CustomerContext c, string project, string environment)
    { if (c.ProjectId != project || c.EnvironmentId != environment) throw new FoundationAuthorizationException("resource_scope"); }

    internal static PipelineConnection LoadConnection(FoundationConnection saved, string cellId) =>
        Connection(saved.CustomerId, cellId, saved.Id, JsonSerializer.Deserialize<ConnectionCommand>(saved.ConfigurationJson, Json)!, saved.Generation);
    static PipelineConnection Connection(CustomerContext c, string id, ConnectionCommand b, long generation) => Connection(c.CustomerId, c.CellId, id, b, generation);
    static PipelineConnection Connection(string customer, string cell, string id, ConnectionCommand b, long generation) =>
        new(customer, cell, id, b.Provider, b.Organization, b.Project, b.Repository, b.PipelineOrWorkflowId,
            b.AllowedRefs, b.AllowedEnvironments, b.AllowedInputKeys, b.AuthProfileId, generation)
        { EnforcesSourceRevision = b.EnforcesSourceRevision, PipelineVersion = b.PipelineVersion };

    internal static async Task<T> Body<T>(HttpContext h)
    {
        if (h.Request.ContentType?.Split(';')[0].Trim() != "application/json") throw new JsonException();
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await h.Request.Body.ReadAsync(buffer, h.RequestAborted)) > 0)
        { if (output.Length + count > 131072) throw new BadHttpRequestException("Body too large", 413); output.Write(buffer, 0, count); }
        using var doc = JsonDocument.Parse(output.ToArray(), new JsonDocumentOptions { MaxDepth = 16 });
        Unique(doc.RootElement);
        var body = doc.RootElement.Deserialize<T>(Json) ?? throw new JsonException();
        // A slow request body cannot keep an expired token or revoked grant alive until an effect.
        var token = h.Request.Headers.Authorization[0]![7..];
        var principal = await h.RequestServices.GetRequiredService<FoundationTokenService>().ValidateAsync(token, h.RequestAborted);
        var capability = h.GetEndpoint()!.Metadata.GetMetadata<Capability>()!;
        var resolver = capability.Name.StartsWith("lifecycle.", StringComparison.Ordinal)
            ? h.RequestServices.GetRequiredService<AdministrativeContextResolver>().Resolver
            : capability.Name == FoundationCapabilities.CellHealthRead ? h.RequestServices.GetRequiredService<OperationalContextResolver>().Resolver
            : h.RequestServices.GetRequiredService<CustomerContextResolver>();
        h.Items[typeof(FoundationPrincipal)] = principal;
        h.Items[typeof(CustomerContext)] = resolver.Require(principal, Route(h, "customerId"), Route(h, "projectId"), Route(h, "environmentId"), capability.Name);
        if (Context(h).SupportValidUntilUtc is not null) h.RequestServices.GetRequiredService<FoundationStore>().RecordSupportAccess(Context(h));
        return body;
    }
    internal static void Unique(JsonElement e)
    {
        if (e.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in e.EnumerateObject()) { if (!names.Add(p.Name)) throw new JsonException(); Unique(p.Value); }
        }
        else if (e.ValueKind == JsonValueKind.Array) foreach (var value in e.EnumerateArray()) Unique(value);
    }
    static async Task Error(HttpContext h, FoundationStore store, string code, int status)
    {
        if (h.Response.HasStarted) throw new InvalidOperationException("Response already started.");
        try { store.RecordSecurityEvent("RequestDenied", Guid.NewGuid().ToString("N"),
            h.Items[typeof(CustomerContext)] is CustomerContext c ? c.ActorKey : "unauthenticated"); }
        catch (SqliteException) { status = 503; code = "audit_unavailable"; }
        catch (FoundationStoreException) { status = 503; code = "audit_unavailable"; }
        h.Response.StatusCode = status;
        await h.Response.WriteAsJsonAsync(new { error = code }, Json, h.RequestAborted);
    }
}
