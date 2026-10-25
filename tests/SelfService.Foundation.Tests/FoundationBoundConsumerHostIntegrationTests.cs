using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationBoundConsumerHostIntegrationTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub)]
    [InlineData(PipelineRequestFactory.AzureDevOps)]
    public async Task Signed_human_HTTP_approval_and_enqueue_reach_actual_bound_runner_and_correlated_mock_receipt(string provider)
    {
        await using var f = await BoundHost.Create(provider);
        var startupReads = f.Keys.Requests.Count; Assert.Equal(0, f.FactoryCalls); var plan = await f.Plan();
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("requester", HttpMethod.Post, $"/plans/{plan.Id}/approve", new { plan.Digest })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("operator", HttpMethod.Post, $"/plans/{plan.Id}/enqueue", new { plan.Digest, idempotencyKey = "before-review" })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Send("approver", HttpMethod.Post, $"/plans/{plan.Id}/approve", new { digest = new string('0', 64) })).StatusCode);
        Assert.Equal(0, f.Number("SELECT count(*) FROM jobs")); Assert.Equal(startupReads, f.Keys.Requests.Count); Assert.Equal(0, f.FactoryCalls);
        await f.Approve(plan); var job = await f.Enqueue(plan);
        Assert.Equal(job.Id, (await f.Enqueue(plan)).Id);
        Assert.True(await f.Runner.RunOnceAsync("host-bound-worker")); Assert.False(await f.Runner.RunOnceAsync("host-bound-worker"));
        var done = await f.Job(job.Id); Assert.Equal("Succeeded", done.State);
        using var receipt = JsonDocument.Parse(done.ReceiptJson!);
        Assert.True(receipt.RootElement.GetProperty("mock").GetBoolean()); Assert.True(receipt.RootElement.GetProperty("dispatchAccepted").GetBoolean());
        Assert.False(receipt.RootElement.GetProperty("deploymentVerified").GetBoolean());
        Assert.Equal(1, f.FactoryCalls); Assert.Equal(1, f.Posts); Assert.Equal(1, f.HandlerDisposals);
        Assert.Equal("Bearer " + CredentialFixture.Token, f.Authorization); Assert.Contains(plan.Digest, f.Body);
        Assert.Contains(f.Template.Request.SourceRevision, f.Body); Assert.DoesNotContain(CredentialFixture.Token, done.ReceiptJson);
        Assert.True(f.Store.VerifyAuditIntegrity(f.Customer, BoundHost.Actor("auditor"))); Assert.Equal(1, f.Number("SELECT count(*) FROM audit WHERE action='JobCompleted'"));
        Assert.Equal(startupReads + 1, f.Keys.Requests.Count);
    }

    [Fact]
    public async Task Hosted_background_worker_uses_typed_consumer_without_a_dispatch_HTTP_endpoint()
    {
        await using var f = await BoundHost.Create(worker: true); var plan = await f.Plan(); await f.Approve(plan); var job = await f.Enqueue(plan);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(20)); FoundationJob current;
        do { current = await f.Job(job.Id); if (current.State != "Succeeded") await Task.Delay(20, watchdog.Token); } while (current.State != "Succeeded");
        Assert.Equal(1, f.Posts); Assert.Equal(1, f.FactoryCalls);
        Assert.Equal(HttpStatusCode.NotFound, (await f.Send("operator", HttpMethod.Post, "/jobs/claim", new { workerId = "not-an-api" })).StatusCode);
    }

    [Fact]
    public async Task Generic_direct_runner_on_bound_history_cannot_claim_or_mutate_an_approved_queued_job()
    {
        await using var f = await BoundHost.Create(); var plan = await f.Plan(); await f.Approve(plan); var job = await f.Enqueue(plan);
        var before = f.JobRows(); var audit = f.Number("SELECT count(*) FROM audit"); var reads = f.Keys.Requests.Count;
        var generic = new FoundationJobRunner(f.Store, new PipelineDispatcher(new DeniedPipelineTransport()));
        Assert.Equal("BoundPipelineConsumerRequired", (await Assert.ThrowsAsync<FoundationStoreException>(() => generic.RunOnceAsync("generic-worker"))).Code);
        Assert.Equal(before, f.JobRows()); Assert.Equal(audit, f.Number("SELECT count(*) FROM audit")); Assert.Equal(reads, f.Keys.Requests.Count); Assert.Equal(0, f.FactoryCalls);
        Assert.True(await f.Runner.RunOnceAsync("actual-selected-worker")); Assert.Equal("Succeeded", (await f.Job(job.Id)).State); Assert.Equal(1, f.Posts);
    }

    [Theory]
    [InlineData("binding")]
    [InlineData("namespace")]
    public async Task Stale_typed_direct_consumer_is_revalidated_before_claim_without_job_or_key_mutation(string fault)
    {
        await using var f = await BoundHost.Create(); var plan = await f.Plan(); await f.Approve(plan); _ = await f.Enqueue(plan);
        using var stale = FoundationCredentialBoundPipelineConsumer.ForMockHandlers(f.Vault, f.Authority,
            fault == "namespace" ? f.Namespace with { VaultNamespaceId = Guid.NewGuid().ToString("N") } : f.Namespace,
            f.Authority.DescribeIdentity(), f.Instance, fault == "binding" ? new string('b', 64) : f.Binding.Sha256,
            [f.Template.Scope], () => throw new InvalidOperationException("Factory must not run"));
        var runner = new FoundationJobRunner(f.Store, new PipelineDispatcher(), boundConsumer: stale);
        var rows = f.JobRows(); var audit = f.Number("SELECT count(*) FROM audit"); var reads = f.Keys.Requests.Count;
        await Assert.ThrowsAsync<FoundationStoreException>(() => runner.RunOnceAsync("stale-consumer"));
        Assert.Equal(rows, f.JobRows()); Assert.Equal(audit, f.Number("SELECT count(*) FROM audit")); Assert.Equal(reads, f.Keys.Requests.Count); Assert.Equal(0, f.Posts);
    }

    [Fact]
    public async Task Revocation_after_enqueue_is_blocked_before_handler_factory_or_credential_lookup()
    {
        await using var f = await BoundHost.Create(); var plan = await f.Plan(); await f.Approve(plan); var job = await f.Enqueue(plan);
        var reads = f.Keys.Requests.Count; f.Store.SetMembership(f.Customer, BoundHost.Actor("admin"), BoundHost.Actor("approver"), "default", "dev", [], false);
        Assert.True(await f.Runner.RunOnceAsync("revoked-before-dispatch")); Assert.Equal("Blocked", (await f.Job(job.Id)).State);
        Assert.Equal(0, f.FactoryCalls); Assert.Equal(0, f.Posts); Assert.Equal(reads, f.Keys.Requests.Count);
    }

    [Fact]
    public async Task Current_approver_revoked_during_actual_source_key_wait_emits_no_POST_and_consumes_unknown_intent()
    {
        await using var f = await BoundHost.Create(pauseKeys: true); var plan = await f.Plan(); await f.Approve(plan); var job = await f.Enqueue(plan);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30)); var running = f.Runner.RunOnceAsync("key-custody-worker", watchdog.Token);
        try
        {
            await f.Held!.Entered.Task.WaitAsync(watchdog.Token);
            f.Store.SetMembership(f.Customer, BoundHost.Actor("admin"), BoundHost.Actor("approver"), "default", "dev", [], false);
        }
        finally { f.Held!.Release.TrySetResult(); }
        Assert.True(await running); Assert.Equal("OutcomeUnknown", (await f.Job(job.Id)).State); Assert.Equal(0, f.Posts);
        Assert.Equal(1, f.FactoryCalls); Assert.Equal(1, f.HandlerDisposals); Assert.False(await f.Runner.RunOnceAsync("no-blind-replay"));
    }

    [Fact]
    public async Task HTTP_customer_isolation_cannot_enqueue_a_foreign_plan_or_reach_a_configured_factory()
    {
        await using var f = await BoundHost.Create(); var plan = await f.Plan(); await f.Approve(plan);
        var other = "/api/foundation/customers/90000000-0000-0000-0000-000000000009/projects/default/environments/dev";
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("operator", HttpMethod.Post, $"/plans/{plan.Id}/enqueue", new { plan.Digest, idempotencyKey = "foreign" }, root: other)).StatusCode);
        Assert.Equal(0, f.Number("SELECT count(*) FROM jobs")); Assert.Equal(0, f.FactoryCalls); Assert.Equal(0, f.Posts);
    }

    [Fact]
    public async Task Pipeline_mock_feature_must_still_be_authorized_before_bound_enqueue()
    {
        await using var f = await BoundHost.Create();
        var configuration = new FoundationFeatureConfiguration("LocalPreview", [FoundationFeatures.PipelinePlan], 100, "0.0.0", "2.0.0", null);
        var prepared = f.Store.PrepareConfigurationChange(f.Customer, BoundHost.Actor("admin"), "default", "dev", configuration, 0, "Synthetic mock feature restriction", new([]), FoundationWorkflowService.RuntimeVersion);
        f.Store.ApproveAdministrativePlan(f.Customer, BoundHost.Actor("approver"), prepared.Id, prepared.Digest);
        f.Store.ExecuteConfigurationChange(f.Customer, BoundHost.Actor("admin"), prepared.Id, prepared.Digest, new([]), FoundationWorkflowService.RuntimeVersion);
        var plan = await f.Plan(); await f.Approve(plan);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("operator", HttpMethod.Post, $"/plans/{plan.Id}/enqueue", new { plan.Digest, idempotencyKey = "feature-denied" })).StatusCode);
        Assert.Equal(0, f.Number("SELECT count(*) FROM jobs")); Assert.Equal(0, f.FactoryCalls); Assert.Equal(0, f.Posts);
    }

    [Fact]
    public async Task New_connection_generation_outside_selected_scope_is_refused_before_queue_mutation()
    {
        await using var f = await BoundHost.Create();
        f.Store.PutConnection(f.Customer, BoundHost.Actor("admin"), f.Template.Connection.Id, f.Template.Connection.Provider, f.Template.Dispatch.Connection.ConfigurationJson);
        var plan = await f.Plan(); await f.Approve(plan); var reads = f.Keys.Requests.Count;
        var response = await f.Send("operator", HttpMethod.Post, $"/plans/{plan.Id}/enqueue", new { plan.Digest, idempotencyKey = "unsupported-generation" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Contains("BoundPipelineConnectionNotAllowed", await response.Content.ReadAsStringAsync());
        Assert.Equal(0, f.Number("SELECT count(*) FROM jobs")); Assert.Equal(reads, f.Keys.Requests.Count); Assert.Equal(0, f.FactoryCalls); Assert.Equal(0, f.Posts);
    }

    [Fact]
    public async Task A_second_reviewed_job_can_enqueue_while_the_actual_worker_is_awaiting_a_source_key()
    {
        await using var f = await BoundHost.Create(worker: true, pauseKeys: true);
        var firstPlan = await f.Plan(); await f.Approve(firstPlan); var first = await f.Enqueue(firstPlan, "first-key-wait");
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30)); FoundationJob? second = null;
        try
        {
            await f.Held!.Entered.Task.WaitAsync(watchdog.Token); var reads = f.Keys.Requests.Count;
            var secondPlan = await f.Plan(); await f.Approve(secondPlan); second = await f.Enqueue(secondPlan, "second-during-key-wait");
            Assert.Equal("Queued", second.State); Assert.Equal(reads, f.Keys.Requests.Count);
            Assert.Equal(1, f.FactoryCalls); Assert.Equal(0, f.Posts);
        }
        finally { f.Held!.Release.TrySetResult(); }
        foreach (var job in new[] { first, second! })
            while ((await f.Job(job.Id)).State != "Succeeded") await Task.Delay(20, watchdog.Token);
        while (Volatile.Read(ref f.HandlerDisposals) != 2) await Task.Delay(20, watchdog.Token);
        Assert.Equal(2, f.Posts); Assert.Equal(2, f.FactoryCalls); Assert.Equal(2, f.HandlerDisposals);
    }

    internal sealed class BoundHost : IAsyncDisposable
    {
        public CredentialFixture Template { get; }
        public CredentialTestKeys Keys => Template.Keys;
        readonly IdentityTestIssuer issuer = new();
        public CredentialVault Vault { get; }
        readonly CredentialVault writerVault;
        public CredentialVaultNamespaceIdentity Namespace { get; }
        public FoundationCellActivationAuthority Authority { get; }
        public FoundationCredentialRuntimeBinding Binding { get; }
        public FoundationCredentialBoundPipelineConsumer Consumer { get; }
        public HeldKeys? Held { get; }
        public WebApplication App { get; private set; } = null!;
        public HttpClient Client { get; private set; } = null!;
        public FoundationStore Store => App.Services.GetRequiredService<FoundationStore>();
        public FoundationJobRunner Runner => App.Services.GetRequiredService<FoundationJobRunner>();
        public string Customer => Template.Scope.CustomerId;
        public string Cell => Template.Scope.CellId;
        public string Database { get; }
        public string AuthorityPath { get; }
        public string Instance { get; }
        public string Root => $"/api/foundation/customers/{Customer}/projects/default/environments/dev";
        public int FactoryCalls, Posts, HandlerDisposals;
        public string? Authorization, Body;
        public static string Actor(string name) => FoundationApiTests.ApiCell.Actor(name);

        BoundHost(string provider, bool pauseKeys)
        {
            Template = new(provider); Template.Clock.Now = DateTimeOffset.UtcNow;
            Database = Path.Combine(Template.Root, "host-consumer.sqlite"); AuthorityPath = Path.Combine(Template.Root, "host-activation");
            var store = new FoundationStore(Database, Cell, Customer); store.BootstrapAdministrator(Actor("admin"));
            foreach (var (name, role) in new[] { ("requester", "Requester"), ("approver", "Approver"), ("operator", "Operator"), ("viewer", "Viewer"), ("auditor", "Auditor") })
                store.SetMembership(Customer, Actor("admin"), Actor(name), "default", "dev", [role]);
            store.PutConnection(Customer, Actor("admin"), Template.Connection.Id, provider, Template.Dispatch.Connection.ConfigurationJson);
            var options = Template.Options with { RootPath = Path.Combine(Template.Root, "host-vault"), RollbackProtection = new(Path.Combine(Template.Root, "host-rollback")) };
            var original = writerVault = CredentialVault.ProvisionStrong(options, Keys);
            if (pauseKeys) Held = new(Keys);
            Vault = pauseKeys ? new CredentialVault(options, Held!) : original;
            Namespace = Vault.DescribeNamespace(); Instance = store.GetCellInstanceIdentity().DatabaseInstanceId;
            Authority = FoundationCellActivationAuthority.Provision(AuthorityPath, Customer, Cell); Authority.Activate(Database, Instance, 0, null);
            Binding = FoundationCredentialBoundTransport.BindCurrent(store, Vault, Authority, Namespace, Authority.DescribeIdentity(), Instance);
            Consumer = FoundationCredentialBoundPipelineConsumer.ForMockHandlers(Vault, Authority, Namespace, Authority.DescribeIdentity(), Instance,
                Binding.Sha256, [Template.Scope], () => { Interlocked.Increment(ref FactoryCalls); return new Handler(this); });
        }
        public static async Task<BoundHost> Create(string provider = PipelineRequestFactory.GitHub, bool worker = false, bool pauseKeys = false)
        {
            var f = new BoundHost(provider, pauseKeys);
            try
            {
                using (var token = CredentialSecret.FromUtf8(CredentialFixture.Token))
                    await f.writerVault.PutAsync(f.Template.Scope, token, DateTimeOffset.UtcNow.AddHours(1));
                var options = new FoundationHostOptions(f.issuer.Options(f.Customer, f.Cell), f.Database, Actor("admin"))
                { BoundPipelineConsumer = f.Consumer, RequireActivationAuthority = true, ActivationAuthorityDirectory = f.AuthorityPath,
                    EnableWorker = worker, PollInterval = TimeSpan.FromMilliseconds(20) };
                f.App = FoundationApplication.Build(options, configure: builder => builder.WebHost.UseTestServer());
                await f.App.StartAsync(); f.Client = f.App.GetTestClient(); f.Client.BaseAddress = new("http://localhost"); return f;
            }
            catch { await f.DisposeAsync(); throw; }
        }
        public async Task<HttpResponseMessage> Send(string actor, HttpMethod method, string path, object? body = null, string? root = null)
        {
            var role = actor switch { "admin" => "CustomerAdmin", "requester" => "Requester", "approver" => "Approver", "operator" => "Operator", "viewer" => "Viewer", _ => "Auditor" };
            var claims = issuer.UserClaims(role, FoundationApiTests.ApiCell.Subjects[actor]);
            using var request = new HttpRequestMessage(method, (root ?? Root) + path); request.Headers.Authorization = new("Bearer", issuer.Token(claims));
            if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, FoundationApplication.Json), Encoding.UTF8, "application/json");
            return await Client.SendAsync(request);
        }
        public async Task<FoundationPlan> Plan() => await Read<FoundationPlan>(await Send("requester", HttpMethod.Post, "/plans",
            new PlanCommand(Template.Connection.Id, "main", Template.Request.SourceRevision, new() { ["location"] = "eastus2" })));
        public async Task Approve(FoundationPlan plan) => _ = await Read<FoundationPlan>(await Send("approver", HttpMethod.Post, $"/plans/{plan.Id}/approve", new { plan.Digest }));
        public async Task<FoundationJob> Enqueue(FoundationPlan plan, string key = "exact-host-job") => await Read<FoundationJob>(await Send("operator", HttpMethod.Post, $"/plans/{plan.Id}/enqueue", new { plan.Digest, idempotencyKey = key }));
        public async Task<FoundationJob> Job(string id) => await Read<FoundationJob>(await Send("viewer", HttpMethod.Get, "/jobs/" + id));
        static async Task<T> Read<T>(HttpResponseMessage response)
        { var text = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}"); return JsonSerializer.Deserialize<T>(text, FoundationApplication.Json)!; }
        public long Number(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        public string[] JobRows()
        { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT id,state,fence,worker_id,lease_until,attempt FROM jobs ORDER BY id"; using var r = command.ExecuteReader(); var rows = new List<string>(); while (r.Read()) rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, r.FieldCount).Select(i => r.IsDBNull(i) ? null : r.GetValue(i)).ToArray())); return rows.ToArray(); }
        SqliteConnection Open() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); db.Open(); return db; }
        public async ValueTask DisposeAsync()
        {
            Held?.Release.TrySetResult(); Client?.Dispose();
            if (App is not null) { using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20)); await App.StopAsync(stop.Token); await App.DisposeAsync(); }
            Consumer.Dispose(); issuer.Dispose(); Template.Dispose();
        }

        sealed class Handler(BoundHost owner) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref owner.Posts); owner.Authorization = request.Headers.Authorization?.ToString(); owner.Body = await request.Content!.ReadAsStringAsync(cancellationToken);
                var c = owner.Template.Connection; var body = c.Provider == PipelineRequestFactory.GitHub
                    ? JsonSerializer.Serialize(new { workflow_run_id = 123, run_url = $"https://api.github.com/repos/{c.Organization}/{c.Repository}/actions/runs/123", html_url = $"https://github.com/{c.Organization}/{c.Repository}/actions/runs/123" })
                    : JsonSerializer.Serialize(new { id = 987, pipeline = new { id = 42 }, url = $"https://dev.azure.com/{c.Organization}/{c.Project}/_apis/pipelines/42/runs/987", resources = new { repositories = new { self = new { version = owner.Template.Request.SourceRevision, refName = "refs/heads/main" } } } });
                return new(HttpStatusCode.OK) { Content = new StringContent(body) };
            }
            protected override void Dispose(bool disposing) { if (disposing) Interlocked.Increment(ref owner.HandlerDisposals); base.Dispose(disposing); }
        }
    }

    internal sealed class HeldKeys(ICredentialKeyProvider inner) : ICredentialKeyProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken cancellationToken)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return await inner.GetKeyAsync(customer, cell, key, cancellationToken); }
    }
}
