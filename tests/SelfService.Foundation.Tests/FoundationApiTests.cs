using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationApiTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub)]
    [InlineData(PipelineRequestFactory.AzureDevOps)]
    public async Task SignedApi_approval_durable_job_and_customer_pipeline_complete_with_mock_receipt(string provider)
    {
        await using var f = await ApiCell.Start(provider);
        var plan = await f.Plan();
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("requester", HttpMethod.Post, $"/plans/{plan.Id}/approve", new { plan.Digest })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("operator", HttpMethod.Post, $"/plans/{plan.Id}/enqueue", new { plan.Digest, idempotencyKey = "first" })).StatusCode);
        await f.Approve(plan);
        var job = await f.Enqueue(plan);
        var duplicate = await f.Enqueue(plan);
        Assert.Equal(job.Id, duplicate.Id);
        Assert.True(await f.App.Services.GetRequiredService<FoundationJobRunner>().RunOnceAsync("test-worker"));
        Assert.False(await f.App.Services.GetRequiredService<FoundationJobRunner>().RunOnceAsync("test-worker"));
        var response = await f.Send("viewer", HttpMethod.Get, "/jobs/" + job.Id);
        var complete = await Read<FoundationJob>(response);
        Assert.Equal("Succeeded", complete.State);
        using var receipt = JsonDocument.Parse(complete.ReceiptJson!);
        Assert.True(receipt.RootElement.GetProperty("mock").GetBoolean());
        Assert.True(receipt.RootElement.GetProperty("dispatchAccepted").GetBoolean());
        Assert.False(receipt.RootElement.GetProperty("deploymentVerified").GetBoolean());
        var wire = Assert.Single(f.Transport.Requests);
        Assert.Equal(provider == PipelineRequestFactory.GitHub ? "api.github.com" : "dev.azure.com", wire.Uri.Host);
        Assert.DoesNotContain("Authorization", wire.Headers.Keys, StringComparer.OrdinalIgnoreCase);
        Assert.Equal(plan.Digest, wire.Binding.PlanDigest);
        var audit = await f.Send("auditor", HttpMethod.Get, "/audit");
        using var evidence = JsonDocument.Parse(await audit.Content.ReadAsStringAsync());
        Assert.True(evidence.RootElement.GetProperty("integrityValid").GetBoolean());
        Assert.Contains("JobCompleted", evidence.RootElement.GetProperty("events").GetRawText());
    }

    [Fact]
    public async Task Hosted_worker_processes_durable_queue_without_test_dispatch_endpoint()
    {
        await using var f = await ApiCell.Start(worker: true);
        var plan = await f.Plan(); await f.Approve(plan); var job = await f.Enqueue(plan);
        FoundationJob? finished = null;
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        while (!timeout.IsCancellationRequested)
        {
            finished = await Read<FoundationJob>(await f.Send("viewer", HttpMethod.Get, "/jobs/" + job.Id));
            if (finished.State == "Succeeded") break;
            await Task.Delay(20, timeout.Token);
        }
        Assert.Equal("Succeeded", finished!.State);
        Assert.Single(f.Transport.Requests);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Send("operator", HttpMethod.Post, "/jobs/claim", new { workerId = "evil" })).StatusCode);
    }

    [Fact]
    public async Task Revoking_approver_after_enqueue_blocks_worker_and_emits_no_provider_request()
    {
        await using var f = await ApiCell.Start();
        var plan = await f.Plan(); await f.Approve(plan); var job = await f.Enqueue(plan);
        await f.Member("approver", [FoundationRoles.Approver], enabled: false);
        await f.App.Services.GetRequiredService<FoundationJobRunner>().RunOnceAsync("revocation-worker");
        var blocked = await Read<FoundationJob>(await f.Send("viewer", HttpMethod.Get, "/jobs/" + job.Id));
        Assert.Equal("Blocked", blocked.State); Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public async Task Changing_connection_after_approval_invalidates_plan()
    {
        await using var f = await ApiCell.Start();
        var plan = await f.Plan(); await f.Approve(plan);
        await f.PutConnection(f.Spec with { Repository = "different-repo" });
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("operator", HttpMethod.Post,
            $"/plans/{plan.Id}/enqueue", new { plan.Digest, idempotencyKey = "changed" })).StatusCode);
        Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public async Task Ambiguous_provider_response_is_quarantined_without_replay()
    {
        await using var f = await ApiCell.Start(); f.Transport.StatusCode = 204;
        var plan = await f.Plan(); await f.Approve(plan); var job = await f.Enqueue(plan);
        var runner = f.App.Services.GetRequiredService<FoundationJobRunner>();
        await runner.RunOnceAsync("uncertain-worker"); await runner.RunOnceAsync("uncertain-worker");
        var unknown = await Read<FoundationJob>(await f.Send("viewer", HttpMethod.Get, "/jobs/" + job.Id));
        Assert.Equal("OutcomeUnknown", unknown.State); Assert.Single(f.Transport.Requests);
    }

    [Fact]
    public async Task Default_host_denies_dispatch_even_with_exact_approval()
    {
        await using var f = await ApiCell.Start(transport: false);
        var plan = await f.Plan(); await f.Approve(plan);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, (await f.Send("operator", HttpMethod.Post,
            $"/plans/{plan.Id}/enqueue", new { plan.Digest, idempotencyKey = "disabled" })).StatusCode);
        Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public async Task Identity_customer_project_and_token_role_are_all_required()
    {
        await using var f = await ApiCell.Start();
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client.GetAsync(f.Root + "/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("viewer", HttpMethod.Get, "/jobs", root: f.Root.Replace(ApiCell.Customer, ApiCell.OtherCustomer))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("viewer", HttpMethod.Get, "/jobs", root: f.Root.Replace("/environments/dev", "/environments/prod"))).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("operator", HttpMethod.Get, "/jobs", tokenRoles: [FoundationRoles.Auditor])).StatusCode);
        var claims = f.Issuer.UserClaims(FoundationRoles.Viewer, ApiCell.Subjects["viewer"]); claims["aud"] = "wrong-api";
        var bad = new HttpRequestMessage(HttpMethod.Get, f.Root + "/jobs"); bad.Headers.Authorization = new("Bearer", f.Issuer.Token(claims));
        Assert.Equal(HttpStatusCode.Unauthorized, (await f.Client.SendAsync(bad)).StatusCode);
        Assert.Empty(f.Transport.Requests);
    }

    [Theory]
    [InlineData("{\"actorKey\":\"forged\",\"roles\":[\"Operator\"],\"enabled\":true,\"customerId\":\"forged\"}")]
    [InlineData("{\"actorKey\":\"forged\",\"roles\":[\"Operator\"],\"Roles\":[\"CustomerAdmin\"],\"enabled\":true}")]
    [InlineData("{\"actorKey\":null,\"roles\":[\"Operator\"],\"enabled\":true}")]
    public async Task Strict_body_contract_rejects_spoofed_unknown_duplicate_and_null_fields(string body)
    {
        await using var f = await ApiCell.Start();
        var r = await f.Send("admin", HttpMethod.Put, "/memberships", raw: body);
        Assert.Equal(HttpStatusCode.BadRequest, r.StatusCode);
    }

    [Fact]
    public async Task Oversized_body_is_rejected_without_persisting_an_artifact()
    {
        await using var f = await ApiCell.Start();
        var response = await f.Send("requester", HttpMethod.Post, "/artifacts", new ArtifactCommand("large.bicep", new string('x', 131073)));
        Assert.Equal(HttpStatusCode.RequestEntityTooLarge, response.StatusCode);
        Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public async Task Browser_origin_self_elevation_and_secret_inputs_are_denied()
    {
        await using var f = await ApiCell.Start();
        var request = f.Request("viewer", HttpMethod.Get, "/jobs"); request.Headers.Add("Origin", "https://attacker.invalid");
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.SendAsync(request)).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("admin", HttpMethod.Put, "/memberships",
            new MembershipCommand(ApiCell.Actor("admin"), [FoundationRoles.CustomerAdmin, FoundationRoles.Operator]))).StatusCode);
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Send("requester", HttpMethod.Post, "/plans", new PlanCommand("customer-ci", "main", ApiCell.Revision,
            new() { ["access_token"] = "forbidden-value" }))).StatusCode);
        Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public async Task Authorized_route_in_another_project_cannot_resolve_or_audit_read_foreign_objects()
    {
        await using var f = await ApiCell.Start();
        var plan = await f.Plan();
        var artifact = await Read<FoundationArtifact>(await f.Send("requester", HttpMethod.Post, "/artifacts", new ArtifactCommand("private.bicep", "// private project source")));
        // Fixture setup represents an existing separate project grant, not a public onboarding endpoint.
        using (var db = new SqliteConnection("Data Source=" + f.DatabasePath))
        {
            db.Open(); using var command = db.CreateCommand();
            command.CommandText = "INSERT INTO memberships SELECT customer_id,actor_key,'other',environment_id,roles,enabled FROM memberships WHERE actor_key=$actor";
            command.Parameters.AddWithValue("$actor", ApiCell.Actor("viewer")); command.ExecuteNonQuery();
        }
        var foreignRoot = f.Root.Replace("/projects/default", "/projects/other");
        foreach (var path in new[] { "/plans/" + plan.Id, "/artifacts/" + artifact.Id, "/artifacts/nonexistent" })
        {
            var denied = await f.Send("viewer", HttpMethod.Get, path, root: foreignRoot);
            Assert.Equal(HttpStatusCode.NotFound, denied.StatusCode);
            Assert.Equal("{\"error\":\"NotFound\"}", await denied.Content.ReadAsStringAsync());
        }
        var audit = f.App.Services.GetRequiredService<FoundationStore>().ListAudit(ApiCell.Customer, ApiCell.Actor("auditor"));
        Assert.DoesNotContain(audit, e => e.Action == "ArtifactRead" && e.TargetId == artifact.Id);
    }

    [Fact]
    public async Task Artifact_reopens_with_same_digest_and_audit_survives_host_restart()
    {
        var directory = Path.Combine(Path.GetTempPath(), "studio-foundation-api-" + Guid.NewGuid().ToString("N"));
        using var issuer = new IdentityTestIssuer();
        string id, digest;
        await using (var f = await ApiCell.Start(directory: directory, issuer: issuer))
        {
            var artifact = await Read<FoundationArtifact>(await f.Send("requester", HttpMethod.Post, "/artifacts",
                new ArtifactCommand("main.bicepparam", "using './main.bicep'\nparam environment = 'dev'\n")));
            id = artifact.Id; digest = artifact.Sha256;
        }
        await using (var reopened = await ApiCell.Start(directory: directory, issuer: issuer))
        {
            var artifact = await Read<FoundationArtifact>(await reopened.Send("viewer", HttpMethod.Get, "/artifacts/" + id));
            Assert.Equal(digest, artifact.Sha256);
            Assert.Contains("param environment", Encoding.UTF8.GetString(artifact.Content));
            Assert.True(reopened.App.Services.GetRequiredService<FoundationStore>().VerifyAuditIntegrity(ApiCell.Customer, ApiCell.Actor("auditor")));
        }
    }

    static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync();
        Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, FoundationApplication.Json)!;
    }

    internal sealed class ApiCell : IAsyncDisposable
    {
        public const string Customer = "00000000-0000-0000-0000-000000000001", OtherCustomer = "00000000-0000-0000-0000-000000000002";
        const string Cell = "00000000-0000-0000-0000-000000000003";
        public const string Revision = "0123456789012345678901234567890123456789";
        public static readonly Dictionary<string, string> Subjects = new()
        {
            ["admin"] = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", ["requester"] = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",
            ["approver"] = "cccccccc-cccc-cccc-cccc-cccccccccccc", ["operator"] = "dddddddd-dddd-dddd-dddd-dddddddddddd",
            ["viewer"] = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee", ["auditor"] = "ffffffff-ffff-ffff-ffff-ffffffffffff"
        };
        public static string Actor(string name) => $"user:{IdentityTestIssuer.Tenant}:{Subjects[name]}";
        public required WebApplication App { get; init; }
        public required HttpClient Client { get; init; }
        public required IdentityTestIssuer Issuer { get; init; }
        public required FixtureTransport Transport { get; init; }
        public required ConnectionCommand Spec { get; init; }
        public required string DatabasePath { get; init; }
        public string CustomerId { get; init; } = Customer;
        bool ownsIssuer;
        public string Root => $"/api/foundation/customers/{CustomerId}/projects/default/environments/dev";

        public static async Task<ApiCell> Start(string provider = PipelineRequestFactory.GitHub, bool worker = false,
            bool transport = true, string? directory = null, IdentityTestIssuer? issuer = null, string? customerId = null, string? cellId = null,
            IFoundationModelClient? modelClient = null, bool requireSignedEntitlement = false,
            bool requireActivationAuthority = false, string? activationAuthorityDirectory = null)
        {
            var owns = issuer is null; issuer ??= new();
            directory ??= Path.Combine(Path.GetTempPath(), "studio-foundation-api-" + Guid.NewGuid().ToString("N"));
            var fixture = new FixtureTransport();
            var repo = new DirectoryInfo(AppContext.BaseDirectory);
            while (repo is not null && !File.Exists(Path.Combine(repo.FullName, "config", "workloads.json"))) repo = repo.Parent;
            var options = new FoundationHostOptions(issuer.Options(customerId ?? Customer, cellId ?? Cell), Path.Combine(directory, "cell.db"), Actor("admin"))
            { EnableWorker = worker, DisconnectedTransport = transport ? fixture : null, PollInterval = TimeSpan.FromMilliseconds(20), RepositoryRoot = repo?.FullName,
                ModelClient = modelClient, RequireSignedEntitlement = requireSignedEntitlement,
                RequireActivationAuthority = requireActivationAuthority, ActivationAuthorityDirectory = activationAuthorityDirectory };
            var app = FoundationApplication.Build(options, configure: builder => builder.WebHost.UseTestServer());
            await app.StartAsync(); var client = app.GetTestClient(); client.BaseAddress = new Uri("http://localhost");
            var spec = new ConnectionCommand(provider, "customer-org", provider == PipelineRequestFactory.AzureDevOps ? "Customer Project" : null,
                "customer-repo", provider == PipelineRequestFactory.GitHub ? "studio.yml" : "42", ["main"], ["dev"],
                [.. PipelineRequestFactory.ReservedInputs, "region"], "profile/customer-ci", true, provider == PipelineRequestFactory.AzureDevOps ? 7 : null);
            var cell = new ApiCell { App = app, Client = client, Issuer = issuer, Transport = fixture, Spec = spec,
                DatabasePath = options.DatabasePath, CustomerId = customerId ?? Customer, ownsIssuer = owns };
            foreach (var (name, role) in new[] { ("requester", FoundationRoles.Requester), ("approver", FoundationRoles.Approver),
                         ("operator", FoundationRoles.Operator), ("viewer", FoundationRoles.Viewer), ("auditor", FoundationRoles.Auditor) })
                await cell.Member(name, [role]);
            await cell.PutConnection(spec);
            return cell;
        }
        public HttpRequestMessage Request(string name, HttpMethod method, string path, string[]? tokenRoles = null, string? root = null)
        {
            var claims = Issuer.UserClaims(subject: Subjects[name]);
            claims["roles"] = tokenRoles ?? new[] { FoundationRoles.CustomerAdmin, FoundationRoles.Requester,
                FoundationRoles.Approver, FoundationRoles.Operator, FoundationRoles.Viewer, FoundationRoles.Auditor };
            var r = new HttpRequestMessage(method, (root ?? Root) + path); r.Headers.Authorization = new("Bearer", Issuer.Token(claims)); return r;
        }
        public async Task<HttpResponseMessage> Send(string name, HttpMethod method, string path, object? body = null,
            string? raw = null, string[]? tokenRoles = null, string? root = null)
        {
            var r = Request(name, method, path, tokenRoles, root);
            if (raw is not null) r.Content = new StringContent(raw, Encoding.UTF8, "application/json");
            else if (body is not null) r.Content = new StringContent(JsonSerializer.Serialize(body, FoundationApplication.Json), Encoding.UTF8, "application/json");
            return await Client.SendAsync(r);
        }
        public Task Member(string name, string[] roles, bool enabled = true)
        {
            // Trusted synthetic fixture bootstrap, separate from the reviewed public privilege-grant API.
            App.Services.GetRequiredService<FoundationStore>().SetMembership(CustomerId, Actor("admin"), Actor(name), "default", "dev", roles, enabled);
            return Task.CompletedTask;
        }
        public async Task PutConnection(ConnectionCommand spec) => _ = await Read<FoundationConnection>(
            await Send("admin", HttpMethod.Put, "/connections/customer-ci", spec));
        public async Task<FoundationPlan> Plan() => await Read<FoundationPlan>(await Send("requester", HttpMethod.Post, "/plans",
            new PlanCommand("customer-ci", "main", Revision, new() { ["region"] = "eastus2" })));
        public async Task Approve(FoundationPlan plan) => _ = await Read<FoundationPlan>(await Send("approver", HttpMethod.Post,
            $"/plans/{plan.Id}/approve", new { plan.Digest }));
        public async Task<FoundationJob> Enqueue(FoundationPlan plan) => await Read<FoundationJob>(await Send("operator", HttpMethod.Post,
            $"/plans/{plan.Id}/enqueue", new { plan.Digest, idempotencyKey = "first" }));
        public async ValueTask DisposeAsync() { Client.Dispose(); await App.DisposeAsync(); if (ownsIssuer) Issuer.Dispose(); }
    }

    internal sealed class FixtureTransport : IPipelineTransport
    {
        public List<PipelineWireRequest> Requests { get; } = [];
        public int StatusCode { get; set; } = 200;
        public Task<PipelineWireResponse> SendAsync(PipelineWireRequest request, CancellationToken cancellationToken)
        {
            lock (Requests) Requests.Add(request);
            var body = request.Uri.Host == "api.github.com"
                ? "{\"workflow_run_id\":123,\"run_url\":\"https://api.github.com/repos/customer-org/customer-repo/actions/runs/123\",\"html_url\":\"https://github.com/customer-org/customer-repo/actions/runs/123\"}"
                : "{\"id\":987,\"pipeline\":{\"id\":42},\"url\":\"https://dev.azure.com/customer-org/Customer%20Project/_apis/pipelines/42/runs/987\",\"resources\":{\"repositories\":{\"self\":{\"version\":\"" + ApiCell.Revision + "\",\"refName\":\"refs/heads/main\"}}}}";
            return Task.FromResult(new PipelineWireResponse(StatusCode, body));
        }
    }
}
