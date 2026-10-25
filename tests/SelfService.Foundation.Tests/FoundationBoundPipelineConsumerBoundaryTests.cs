using System.Net;
using System.Net.WebSockets;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using SelfService.Portal;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationBoundPipelineConsumerBoundaryTests
{
    [Theory]
    [InlineData("worker", "BoundPipelineConsumerRequired", 403)]
    [InlineData("legacy", "BoundPipelineConsumerRequired", 403)]
    [InlineData("mixed", "BoundPipelineConsumerConflict", 400)]
    [InlineData("activation-flag", "BoundPipelineConsumerActivationRequired", 403)]
    [InlineData("activation-directory", "BoundPipelineConsumerActivationRequired", 403)]
    public async Task Invalid_bound_execution_configuration_refuses_before_bootstrap_or_any_credential_factory(string mode, string code, int status)
    {
        await using var f = new SelectedCell(seedActors: false); var options = f.Options();
        options = mode switch
        {
            "worker" => options with { BoundPipelineConsumer = null, EnableWorker = true },
            "legacy" => options with { BoundPipelineConsumer = null, DisconnectedTransport = new LegacyMock() },
            "mixed" => options with { DisconnectedTransport = new LegacyMock() },
            "activation-flag" => options with { RequireActivationAuthority = false, ActivationAuthorityDirectory = null },
            _ => options with { ActivationAuthorityDirectory = null }
        };
        var before = f.LogicalRows();
        var error = Assert.Throws<FoundationStoreException>(() => FoundationApplication.Build(options, configure: b => b.WebHost.UseTestServer()));
        Assert.Equal(code, error.Code); Assert.Equal(status, error.Status); Assert.Equal(before, f.LogicalRows());
        Assert.Equal(0, f.Number("SELECT count(*) FROM actors")); Assert.Empty(f.Template.Keys.Requests); Assert.Equal(0, f.Probe.Factories);
        f.AssertExclusiveAvailable();
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("cell")]
    [InlineData("path")]
    [InlineData("instance")]
    [InlineData("binding")]
    [InlineData("namespace")]
    [InlineData("copied-consumer-authority")]
    [InlineData("copied-host-authority")]
    public async Task Cross_bound_or_copied_actual_startup_custody_is_refused_before_bootstrap(string mismatch)
    {
        await using var f = new SelectedCell(seedActors: false); var options = f.Options(); string? copiedPath = null;
        switch (mismatch)
        {
            case "customer": options = options with { Identity = f.Issuer.Options("10000000-0000-0000-0000-000000000099", f.Cell) }; break;
            case "cell": options = options with { Identity = f.Issuer.Options(f.Customer, "20000000-0000-0000-0000-000000000099") }; break;
            case "path":
                var other = f.Path("other.sqlite"); _ = new FoundationStore(other, f.Cell, f.Customer);
                options = options with { DatabasePath = other }; break;
            case "instance": options = options with { BoundPipelineConsumer = f.Consumer(instance: Guid.NewGuid().ToString("N")) }; break;
            case "binding": options = options with { BoundPipelineConsumer = f.Consumer(bindingSha: new string('a', 64)) }; break;
            case "namespace":
                var alternate = CredentialVault.Provision(f.Template.Options with { RootPath = f.Path("alternate-vault") }, f.Template.Keys);
                options = options with { BoundPipelineConsumer = f.Consumer(vault: alternate, ns: alternate.DescribeNamespace()) }; break;
            default:
                copiedPath = f.CopyAuthority();
                var copied = new FoundationCellActivationAuthority(copiedPath, f.Customer, f.Cell);
                Assert.Equal(f.Activation, copied.ReadCurrent()); Assert.Equal(f.AuthorityIdentity.AuthorityId, copied.DescribeIdentity().AuthorityId);
                Assert.NotEqual(f.AuthorityIdentity.CustodyCommitmentSha256, copied.DescribeIdentity().CustodyCommitmentSha256);
                options = mismatch == "copied-host-authority"
                    ? options with { ActivationAuthorityDirectory = copiedPath }
                    : options with { BoundPipelineConsumer = f.Consumer(authority: copied, authorityIdentity: copied.DescribeIdentity()) }; break;
        }
        var before = f.LogicalRows();
        var error = Assert.Throws<FoundationStoreException>(() => FoundationApplication.Build(options, configure: b => b.WebHost.UseTestServer()));
        Assert.InRange(error.Status, 400, 409); Assert.Equal(before, f.LogicalRows()); Assert.Equal(0, f.Number("SELECT count(*) FROM actors"));
        Assert.Empty(f.Template.Keys.Requests); Assert.Equal(0, f.Probe.Factories); f.AssertExclusiveAvailable();
        if (copiedPath is not null) using (var lease = new FileStream(System.IO.Path.Combine(copiedPath, ".activation.guard"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Assert.Equal(0, lease.Length);
    }

    [Fact]
    public async Task Selected_host_startup_is_key_free_and_does_not_create_a_handler_or_rebind_existing_metadata()
    {
        await using var f = new SelectedCell(); var before = f.LogicalRows(); var bindings = f.Rows("SELECT * FROM credential_runtime_bindings");
        await f.Start(); Assert.Equal(before, f.LogicalRows()); Assert.Equal(bindings, f.Rows("SELECT * FROM credential_runtime_bindings"));
        Assert.Empty(f.Template.Keys.Requests); Assert.Equal(0, f.Probe.Factories);
        Assert.Equal(HttpStatusCode.OK, (await f.Send("viewer", HttpMethod.Get, "/jobs")).StatusCode);
        Assert.True(f.Store.GetCredentialRuntimeBindingStatus().BindingRequired);
    }

    [Fact]
    public async Task Bound_readonly_host_remains_available_but_its_legacy_runner_and_enqueue_cannot_claim_work()
    {
        await using var f = new SelectedCell(); var queued = f.QueueDirect();
        await f.Start(f.Options() with { BoundPipelineConsumer = null, EnableWorker = false, DisconnectedTransport = null });
        Assert.Equal(HttpStatusCode.OK, (await f.Send("viewer", HttpMethod.Get, "/jobs")).StatusCode);
        var before = f.LogicalRows();
        var denied = await Assert.ThrowsAsync<FoundationStoreException>(() => f.Runner.RunOnceAsync("readonly-runner"));
        Assert.Equal("BoundPipelineConsumerRequired", denied.Code); Assert.Equal(before, f.LogicalRows());
        var plan = await f.Plan(); var response = await f.Send("operator", HttpMethod.Post, $"/plans/{plan.Id}/enqueue", new { plan.Digest, idempotencyKey = "readonly" });
        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode); Assert.Equal("Queued", f.Store.GetJob(f.Customer, SelectedCell.Actor("viewer"), queued.Id).State);
        Assert.Empty(f.Template.Keys.Requests); Assert.Equal(0, f.Probe.Factories);
    }

    [Fact]
    public async Task Never_bound_legacy_host_keeps_its_explicit_disconnected_approval_and_dispatch_behavior()
    {
        await using var f = new SelectedCell(bind: false); var legacy = new LegacyMock();
        await f.Start(f.Options() with { BoundPipelineConsumer = null, RequireActivationAuthority = false, ActivationAuthorityDirectory = null, DisconnectedTransport = legacy });
        var plan = await f.Plan(); await f.Approve(plan); var job = await f.Enqueue(plan);
        Assert.True(await f.Runner.RunOnceAsync("legacy-worker")); Assert.Equal("Succeeded", f.ReadJob(job.Id).State);
        Assert.Equal(1, legacy.Calls); Assert.Equal(0, f.Probe.Factories); Assert.Empty(f.Template.Keys.Requests);
        Assert.False(f.Store.GetCredentialRuntimeBindingStatus().BindingRequired);
    }

    [Fact]
    public async Task Real_AHP_enqueue_requires_exact_separate_human_HTTP_approval_before_bound_mock_dispatch()
    {
        await using var f = new SelectedCell(); await f.Initialize(); await f.Start();
        // The requester deliberately also has current approval authority. Refusing its own plan
        // must therefore prove human separation, rather than merely a missing Approver role.
        f.Store.SetMembership(f.Customer, SelectedCell.Actor("admin"), SelectedCell.Actor("requester"), "default", "dev", ["Requester", "Approver"]);
        var plan = await f.Plan(); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var client = f.App!.GetTestServer().CreateWebSocketClient(); client.SubProtocols.Add("platform-studio");
        client.ConfigureRequest = r => r.Headers.Authorization = f.Request("operator", HttpMethod.Get, "/agent/ahp").Headers.Authorization!.ToString();
        using var socket = await client.ConnectAsync(new Uri("ws://localhost" + f.Root + "/agent/ahp"), stop.Token);
        _ = await Rpc(socket, 1, "initialize", new { channel = AgentHostChannel.Root, protocolVersions = new[] { AgentHostChannel.Version }, clientId = "bound-independent" }, stop.Token);
        var channel = "ahp-session:/" + Guid.NewGuid().ToString("D"); _ = await Rpc(socket, 2, "createSession", new { channel, provider = "platform-studio" }, stop.Token);
        var approveTool = await Rpc(socket, 3, "dispatchAction", new { channel, action = new { type = "platform/foundationApprove", payload = new { plan.Id, plan.Digest } } }, stop.Token);
        Assert.True(approveTool.TryGetProperty("error", out _));
        var refused = await Rpc(socket, 4, "dispatchAction", new { channel, action = new { type = "platform/foundationEnqueue", payload = new FoundationPlanReference(plan.Id, plan.Digest, "ahp-once") } }, stop.Token);
        Assert.True(refused.TryGetProperty("error", out _) || !refused.GetProperty("result").GetProperty("success").GetBoolean());
        Assert.Equal(0, f.Number("SELECT count(*) FROM jobs")); Assert.Equal(0, f.Probe.Factories);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("requester", HttpMethod.Post, $"/plans/{plan.Id}/approve", new { plan.Digest })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Send("approver", HttpMethod.Post, $"/plans/{plan.Id}/approve", new { digest = new string('a', 64) })).StatusCode);
        await f.Approve(plan);
        var queued = await Rpc(socket, 5, "dispatchAction", new { channel, action = new { type = "platform/foundationEnqueue", payload = new FoundationPlanReference(plan.Id, plan.Digest, "ahp-once") } }, stop.Token);
        Assert.True(queued.GetProperty("result").GetProperty("success").GetBoolean());
        var job = f.Store.ListJobs(f.Customer, SelectedCell.Actor("viewer")).Single(); Assert.Equal("Queued", job.State);
        Assert.True(await f.Runner.RunOnceAsync("bound-ahp")); var completed = f.ReadJob(job.Id);
        Assert.Equal("Succeeded", completed.State); Assert.Equal(1, f.Probe.Posts); Assert.Equal(1, f.Probe.Factories);
        Assert.Equal("Bearer " + CredentialFixture.Token, f.Probe.Authorization);
        using var receipt = JsonDocument.Parse(completed.ReceiptJson!); Assert.True(receipt.RootElement.GetProperty("mock").GetBoolean());
        Assert.False(receipt.RootElement.GetProperty("deploymentVerified").GetBoolean());
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", stop.Token);
    }

    [Theory]
    [InlineData("valid", "Succeeded")]
    [InlineData("accepted", "OutcomeUnknown")]
    [InlineData("redirect", "OutcomeUnknown")]
    [InlineData("invalid", "OutcomeUnknown")]
    public async Task Selected_consumer_provider_response_is_correlated_without_inventing_deployment_success(string response, string state)
    {
        await using var f = new SelectedCell(); f.Probe.Response = response; await f.Initialize(); await f.Start();
        var plan = await f.Plan(); await f.Approve(plan); var queued = await f.Enqueue(plan);
        Assert.True(await f.Runner.RunOnceAsync("response-worker")); var job = f.ReadJob(queued.Id); Assert.Equal(state, job.State);
        Assert.Equal(1, f.Probe.Posts); Assert.Equal(1, f.Probe.Factories);
        Assert.DoesNotContain(CredentialFixture.Token, JsonSerializer.Serialize(job));
        if (job.ReceiptJson is not null) Assert.Contains("\"deploymentVerified\":false", job.ReceiptJson);
    }

    [Theory]
    [InlineData("disposed")]
    [InlineData("activation")]
    [InlineData("missing-binding")]
    public async Task Direct_runner_rechecks_actual_consumer_before_claim_and_preserves_queued_job_on_refusal(string change)
    {
        await using var f = new SelectedCell(); var queued = f.QueueDirect();
        var runner = new FoundationJobRunner(f.Store, new PipelineDispatcher(), new([]), f.Selected);
        if (change == "disposed") f.Selected.Dispose();
        if (change == "activation") { var next = f.SwitchTarget(); _ = f.Authority.Activate(next.Path, next.Instance, f.Activation.Generation, f.Activation.Sha256); }
        if (change == "missing-binding") f.RemoveBindingRows();
        var before = f.Rows("SELECT * FROM jobs ORDER BY id"); var audit = f.Number("SELECT count(*) FROM audit");
        await Assert.ThrowsAsync<FoundationStoreException>(() => runner.RunOnceAsync("stale-runner"));
        Assert.Equal(before, f.Rows("SELECT * FROM jobs ORDER BY id")); Assert.Equal(audit, f.Number("SELECT count(*) FROM audit"));
        Assert.Equal("Queued", f.ReadJob(queued.Id).State); Assert.Empty(f.Template.Keys.Requests); Assert.Equal(0, f.Probe.Factories);
    }

    [Fact]
    public async Task Direct_runner_real_activation_scope_is_held_before_a_blocked_SQL_claim()
    {
        await using var f = new SelectedCell(); await f.Initialize(); var queued = f.QueueDirect();
        var runner = new FoundationJobRunner(f.Store, new PipelineDispatcher(), new([]), f.Selected);
        using var writer = Open(f.DatabasePath); using var transaction = writer.BeginTransaction(deferred: false);
        var pending = Task.Run(() => runner.RunOnceAsync("blocked-claim"));
        try { await f.WaitForExclusiveConflict(); f.AssertBusy(); Assert.False(pending.IsCompleted); Assert.Equal("Queued", f.ReadJob(queued.Id).State); }
        finally { transaction.Rollback(); }
        Assert.True(await pending); Assert.Equal("Succeeded", f.ReadJob(queued.Id).State); f.AssertExclusiveAvailable();
    }

    [Fact]
    public async Task Direct_runner_keeps_outer_activation_scope_through_a_blocked_durable_result_commit()
    {
        await using var f = new SelectedCell(); f.Probe.Phase = "handler"; await f.Initialize(); var queued = f.QueueDirect();
        var runner = new FoundationJobRunner(f.Store, new PipelineDispatcher(), new([]), f.Selected);
        var pending = Task.Run(() => runner.RunOnceAsync("blocked-result")); using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        await f.Probe.Entered.Task.WaitAsync(timeout.Token);
        using var writer = Open(f.DatabasePath); using var transaction = writer.BeginTransaction(deferred: false);
        try
        {
            f.Probe.Release.TrySetResult(); await f.Probe.BodyDisposed.Task.WaitAsync(timeout.Token);
            await Task.Delay(150, timeout.Token); Assert.False(pending.IsCompleted); Assert.Equal("Dispatched", f.ReadJob(queued.Id).State); f.AssertBusy();
        }
        finally { transaction.Rollback(); f.Probe.Release.TrySetResult(); }
        Assert.True(await pending); Assert.Equal("Succeeded", f.ReadJob(queued.Id).State); f.AssertExclusiveAvailable();
    }

    [Fact]
    public async Task Updated_connection_outside_configured_scope_is_rejected_before_HTTP_or_AHP_enqueue_mutation()
    {
        await using var f = new SelectedCell(); await f.Start();
        f.Store.PutConnection(f.Customer, SelectedCell.Actor("admin"), f.Template.Connection.Id, f.Template.Connection.Provider, f.Template.Dispatch.Connection.ConfigurationJson);
        var plan = await f.Plan(); await f.Approve(plan); var before = f.Rows("SELECT * FROM jobs ORDER BY id");
        var response = await f.Send("operator", HttpMethod.Post, $"/plans/{plan.Id}/enqueue", new { plan.Digest, idempotencyKey = "new-generation" });
        Assert.Equal(HttpStatusCode.Forbidden, response.StatusCode); Assert.Equal(before, f.Rows("SELECT * FROM jobs ORDER BY id"));
        var tool = await f.Send("operator", HttpMethod.Post, "/agent/tool", new FoundationToolCommand("platform_foundation_enqueue",
            JsonSerializer.SerializeToElement(new FoundationPlanReference(plan.Id, plan.Digest, "tool-new-generation"), FoundationApplication.Json)));
        using var toolBody = JsonDocument.Parse(await tool.Content.ReadAsStringAsync());
        Assert.False(toolBody.RootElement.GetProperty("success").GetBoolean()); Assert.Equal(before, f.Rows("SELECT * FROM jobs ORDER BY id"));
        Assert.Empty(f.Template.Keys.Requests); Assert.Equal(0, f.Probe.Factories); Assert.Equal(0, f.Probe.Posts);
    }

    [Fact]
    public async Task Owner_selected_restored_instance_cannot_use_historical_binding_to_start_the_consumer_or_bootstrap()
    {
        await using var f = new SelectedCell(seedActors: false);
        var backup = f.Store.CreateBackup(f.Path("restore-source.sqlite")); var path = f.Path("restored-selected.sqlite");
        var restored = FoundationStore.RestoreBackup(backup, path); var instance = restored.GetCellInstanceIdentity().DatabaseInstanceId;
        _ = f.Authority.Activate(path, instance, f.Activation.Generation, f.Activation.Sha256);
        var options = f.Options() with { DatabasePath = path, BoundPipelineConsumer = f.Consumer(instance: instance) };
        var before = f.Rows("SELECT * FROM actors ORDER BY actor_key", path);
        var audits = f.Rows("SELECT * FROM audit ORDER BY sequence", path);
        var error = Assert.Throws<FoundationStoreException>(() => FoundationApplication.Build(options, configure: b => b.WebHost.UseTestServer()));
        Assert.Equal("RecoveryExecutionHeld", error.Code); Assert.Equal(before, f.Rows("SELECT * FROM actors ORDER BY actor_key", path));
        Assert.Equal(audits, f.Rows("SELECT * FROM audit ORDER BY sequence", path)); Assert.Empty(f.Template.Keys.Requests); Assert.Equal(0, f.Probe.Factories);
        Assert.True(restored.GetCredentialRuntimeBindingStatus().BindingRequired); Assert.Null(restored.GetCredentialRuntimeBindingStatus().CurrentBinding);
        f.AssertExclusiveAvailable();
    }

    [Fact]
    public async Task Authorized_enqueues_remain_available_while_the_real_host_worker_polls_every_twenty_milliseconds()
    {
        await using var f = new SelectedCell(); await f.Initialize();
        await f.Start(f.Options() with { EnableWorker = true });
        var jobs = new List<FoundationJob>();
        for (var i = 0; i < 8; i++)
        {
            var plan = await f.Plan(); await f.Approve(plan);
            jobs.Add(await f.Enqueue(plan)); // A single request per approval: no retry can hide a custody refusal.
        }
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        while (jobs.Any(job => f.ReadJob(job.Id).State != "Succeeded"))
            await Task.Delay(20, watchdog.Token);
        Assert.Equal(8, f.Probe.Posts); Assert.Equal(8, f.Probe.Factories);
        Assert.Equal(8, f.Number("SELECT count(*) FROM audit WHERE action='JobCompleted'"));
        Assert.True(f.Store.VerifyAuditIntegrity(f.Customer, SelectedCell.Actor("auditor")));
    }

    [Fact]
    public async Task Exact_approved_AHP_enqueue_is_key_free_while_another_owned_dispatch_holds_the_real_source_key_admission_gate()
    {
        await using var f = new SelectedCell(); f.Probe.Phase = "key"; await f.Initialize();
        await f.Start(f.Options() with { EnableWorker = true });
        var first = await f.Plan(); await f.Approve(first); var firstJob = await f.Enqueue(first);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await f.Probe.Entered.Task.WaitAsync(watchdog.Token);
            Assert.Equal("Dispatched", f.ReadJob(firstJob.Id).State); Assert.Equal(0, f.Probe.Posts); f.AssertBusy();
            var second = await f.Plan(); await f.Approve(second);
            var factories = f.Probe.Factories; var reads = f.Template.Keys.Requests.Count;
            var client = f.App!.GetTestServer().CreateWebSocketClient(); client.SubProtocols.Add("platform-studio");
            client.ConfigureRequest = request => request.Headers.Authorization = f.Request("operator", HttpMethod.Get, "/agent/ahp").Headers.Authorization!.ToString();
            using var socket = await client.ConnectAsync(new Uri("ws://localhost" + f.Root + "/agent/ahp"), watchdog.Token);
            _ = await Rpc(socket, 1, "initialize", new { channel = AgentHostChannel.Root, protocolVersions = new[] { AgentHostChannel.Version }, clientId = "bound-keywait-independent" }, watchdog.Token);
            var channel = "ahp-session:/" + Guid.NewGuid().ToString("D"); _ = await Rpc(socket, 2, "createSession", new { channel, provider = "platform-studio" }, watchdog.Token);
            var queued = await Rpc(socket, 3, "dispatchAction", new { channel, action = new { type = "platform/foundationEnqueue", payload = new FoundationPlanReference(second.Id, second.Digest, "keywait-ahp-once") } }, watchdog.Token);
            Assert.True(queued.GetProperty("result").GetProperty("success").GetBoolean());
            var secondJob = f.Store.ListJobs(f.Customer, SelectedCell.Actor("viewer")).Single(job => job.Id != firstJob.Id);
            Assert.Equal("Queued", f.ReadJob(secondJob.Id).State); Assert.Equal("Dispatched", f.ReadJob(firstJob.Id).State);
            Assert.Equal(factories, f.Probe.Factories); Assert.Equal(reads, f.Template.Keys.Requests.Count);
            Assert.Equal(0, f.Probe.Posts); Assert.False(f.Probe.Release.Task.IsCompleted); f.AssertBusy();
            await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", watchdog.Token);
            f.Probe.Release.TrySetResult();
            while (f.ReadJob(firstJob.Id).State != "Succeeded" || f.ReadJob(secondJob.Id).State != "Succeeded")
                await Task.Delay(20, watchdog.Token);
            Assert.Equal(2, f.Probe.Posts); Assert.Equal(2, f.Probe.Factories);
        }
        finally { f.Probe.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData("key")]
    [InlineData("handler")]
    [InlineData("body")]
    [InlineData("body-disposal")]
    public async Task Host_stop_and_disposal_cannot_detach_uncooperative_owned_work_or_release_actual_operation_guard(string phase)
    {
        await using var f = new SelectedCell(); f.Probe.Phase = phase; await f.Initialize();
        await f.Start(f.Options() with { EnableWorker = true });
        var runner = f.Runner;
        var worker = f.App!.Services.GetServices<IHostedService>().OfType<BackgroundService>().Single();
        var plan = await f.Plan(); await f.Approve(plan); var queued = await f.Enqueue(plan);
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        try
        {
            await f.Probe.Entered.Task.WaitAsync(watchdog.Token);
            using var stopTimeout = new CancellationTokenSource(TimeSpan.FromMilliseconds(200));
            try { await f.App.StopAsync(stopTimeout.Token); } catch (OperationCanceledException) when (stopTimeout.IsCancellationRequested) { }
            await f.App.DisposeAsync(); f.MarkAppDisposed();
            Assert.False(worker.ExecuteTask!.IsCompleted); f.AssertBusy();
            var jobs = f.Rows("SELECT * FROM jobs ORDER BY id"); var audit = f.Number("SELECT count(*) FROM audit"); var factories = f.Probe.Factories;
            Assert.Equal("ActivationRuntimeStopping", (await Assert.ThrowsAsync<FoundationStoreException>(() => runner.RunOnceAsync("after-host-disposal"))).Code);
            Assert.Equal(jobs, f.Rows("SELECT * FROM jobs ORDER BY id")); Assert.Equal(audit, f.Number("SELECT count(*) FROM audit")); Assert.Equal(factories, f.Probe.Factories);
        }
        finally { f.Probe.Release.TrySetResult(); }
        try { await worker.ExecuteTask!.WaitAsync(watchdog.Token); } catch (OperationCanceledException) when (!watchdog.IsCancellationRequested) { }
        Assert.Equal("OutcomeUnknown", f.ReadJob(queued.Id).State); Assert.Equal(phase == "key" ? 0 : 1, f.Probe.Posts);
        f.AssertExclusiveAvailable();
    }

    static SqliteConnection Open(string path) { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open(); return db; }
    static async Task<JsonElement> Rpc(WebSocket socket, int id, string method, object parameters, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, method, @params = parameters }, FoundationApplication.Json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token); using var buffer = new MemoryStream(); var part = new byte[8192]; WebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(part, token); Assert.Equal(WebSocketMessageType.Text, result.MessageType); buffer.Write(part, 0, result.Count); Assert.InRange(buffer.Length, 1, 131072); } while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(buffer.ToArray()); return document.RootElement.Clone();
    }

    sealed class Probe
    {
        int factories, posts;
        public int Factories => Volatile.Read(ref factories); public int Posts => Volatile.Read(ref posts);
        public string? Authorization { get; private set; }
        public string Phase { get; set; } = ""; public string Response { get; set; } = "valid";
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource BodyDisposed { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public HttpMessageHandler Handler() { Interlocked.Increment(ref factories); return new HandlerImpl(this); }
        public ICredentialKeyProvider Keys(ICredentialKeyProvider inner) => new HeldKeys(this, inner);
        sealed class HeldKeys(Probe probe, ICredentialKeyProvider inner) : ICredentialKeyProvider
        {
            public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken cancellationToken)
            { if (probe.Phase == "key") { probe.Entered.TrySetResult(); await probe.Release.Task; } return await inner.GetKeyAsync(customerId, cellId, keyId, cancellationToken); }
        }
        sealed class HandlerImpl(Probe probe) : HttpMessageHandler
        {
            protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
            {
                Interlocked.Increment(ref probe.posts); probe.Authorization = request.Headers.Authorization?.ToString();
                if (probe.Phase == "handler") { probe.Entered.TrySetResult(); await probe.Release.Task; }
                var status = probe.Response == "accepted" ? HttpStatusCode.Accepted : probe.Response == "redirect" ? HttpStatusCode.Redirect : HttpStatusCode.OK;
                return new(status) { Content = new StreamContent(new Body(probe, probe.Response == "invalid" ? "{}" : ValidBody)) };
            }
        }
        sealed class Body(Probe probe, string text) : Stream
        {
            readonly byte[] bytes = Encoding.UTF8.GetBytes(text); int position;
            public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
            public override long Length => throw new NotSupportedException(); public override long Position { get => position; set => throw new NotSupportedException(); }
            public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
            { if (position == 0 && probe.Phase == "body") { probe.Entered.TrySetResult(); await probe.Release.Task; } var count = Math.Min(buffer.Length, bytes.Length - position); bytes.AsMemory(position, count).CopyTo(buffer); position += count; return count; }
            public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();
            protected override void Dispose(bool disposing) { if (disposing) probe.BodyDisposed.TrySetResult(); base.Dispose(disposing); }
            public override async ValueTask DisposeAsync()
            { if (probe.Phase == "body-disposal") { probe.Entered.TrySetResult(); await probe.Release.Task; } Dispose(); GC.SuppressFinalize(this); }
            public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException(); public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
            public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        }
    }

    const string ValidBody = "{\"workflow_run_id\":123,\"run_url\":\"https://api.github.com/repos/customer-org/customer-repo/actions/runs/123\",\"html_url\":\"https://github.com/customer-org/customer-repo/actions/runs/123\"}";
    sealed class LegacyMock : IPipelineTransport, IPipelineTransportCharacteristics
    { public bool IsMock => true; public int Calls { get; private set; } public Task<PipelineWireResponse> SendAsync(PipelineWireRequest request, CancellationToken cancellationToken) { Calls++; return Task.FromResult(new PipelineWireResponse(200, ValidBody)); } }

    sealed class SelectedCell : IAsyncDisposable
    {
        public CredentialFixture Template { get; } = new();
        public IdentityTestIssuer Issuer { get; } = new(); public Probe Probe { get; } = new();
        readonly List<FoundationCredentialBoundPipelineConsumer> consumers = []; bool appDisposed;
        public string Customer => Template.Scope.CustomerId; public string Cell => Template.Scope.CellId;
        public string DatabasePath => Path("selected.sqlite"); public string AuthorityPath => Path("selected-authority");
        public FoundationStore Store { get; }
        public FoundationCellActivationAuthority Authority { get; }
        public FoundationCellActivationBinding Activation { get; }
        public FoundationCellActivationAuthorityIdentity AuthorityIdentity { get; }
        public CredentialVaultNamespaceIdentity Namespace { get; }
        public FoundationCredentialRuntimeBinding? Binding { get; }
        public FoundationCredentialBoundPipelineConsumer Selected { get; }
        public WebApplication? App { get; private set; } public HttpClient? Client { get; private set; }
        public string Root => $"/api/foundation/customers/{Customer}/projects/default/environments/dev";
        public FoundationJobRunner Runner => App!.Services.GetRequiredService<FoundationJobRunner>();
        public static string Actor(string name) => FoundationApiTests.ApiCell.Actor(name);
        public SelectedCell(bool bind = true, bool seedActors = true)
        {
            Template.Clock.Now = DateTimeOffset.UtcNow;
            Store = new(DatabasePath, Cell, Customer);
            if (seedActors)
            {
                Store.BootstrapAdministrator(Actor("admin"));
                foreach (var (actor, role) in new[] { ("requester", "Requester"), ("approver", "Approver"), ("operator", "Operator"), ("viewer", "Viewer"), ("auditor", "Auditor") })
                    Store.SetMembership(Customer, Actor("admin"), Actor(actor), "default", "dev", [role]);
                Store.PutConnection(Customer, Actor("admin"), Template.Connection.Id, Template.Connection.Provider, Template.Dispatch.Connection.ConfigurationJson);
            }
            Namespace = Template.Vault.DescribeNamespace(); Authority = FoundationCellActivationAuthority.Provision(AuthorityPath, Customer, Cell);
            Activation = Authority.Activate(DatabasePath, Store.GetCellInstanceIdentity().DatabaseInstanceId, 0, null); AuthorityIdentity = Authority.DescribeIdentity();
            if (bind) Binding = FoundationCredentialBoundTransport.BindCurrent(Store, Template.Vault, Authority, Namespace, AuthorityIdentity, Store.GetCellInstanceIdentity().DatabaseInstanceId);
            Selected = Consumer();
        }
        public FoundationCredentialBoundPipelineConsumer Consumer(CredentialVault? vault = null, CredentialVaultNamespaceIdentity? ns = null,
            FoundationCellActivationAuthority? authority = null, FoundationCellActivationAuthorityIdentity? authorityIdentity = null, string? instance = null, string? bindingSha = null)
        {
            vault ??= new CredentialVault(Template.Options, Probe.Keys(Template.Keys));
            var consumer = FoundationCredentialBoundPipelineConsumer.ForMockHandlers(vault, authority ?? Authority, ns ?? Namespace,
                authorityIdentity ?? AuthorityIdentity, instance ?? Store.GetCellInstanceIdentity().DatabaseInstanceId, bindingSha ?? Binding?.Sha256 ?? new string('0', 64),
                [Template.Scope], Probe.Handler, operationTimeout: TimeSpan.FromSeconds(30)); consumers.Add(consumer); return consumer;
        }
        public FoundationHostOptions Options() => new(Issuer.Options(Customer, Cell), DatabasePath, Actor("admin"))
        { BoundPipelineConsumer = Selected, RequireActivationAuthority = true, ActivationAuthorityDirectory = AuthorityPath, PollInterval = TimeSpan.FromMilliseconds(20) };
        public async Task Start(FoundationHostOptions? options = null)
        { App = FoundationApplication.Build(options ?? Options(), configure: b => b.WebHost.UseTestServer()); await App.StartAsync(); Client = App.GetTestClient(); Client.BaseAddress = new("http://localhost"); }
        public Task Initialize() => Template.StoreToken();
        public string Path(string name) => System.IO.Path.Combine(Template.Root, name);
        public HttpRequestMessage Request(string actor, HttpMethod method, string path)
        {
            var claims = Issuer.UserClaims(subject: FoundationApiTests.ApiCell.Subjects[actor]);
            claims["roles"] = new[] { FoundationRoles.CustomerAdmin, FoundationRoles.Requester, FoundationRoles.Approver, FoundationRoles.Operator, FoundationRoles.Viewer, FoundationRoles.Auditor };
            var request = new HttpRequestMessage(method, Root + path); request.Headers.Authorization = new("Bearer", Issuer.Token(claims)); return request;
        }
        public async Task<HttpResponseMessage> Send(string actor, HttpMethod method, string path, object? body = null)
        { using var request = Request(actor, method, path); if (body is not null) request.Content = new StringContent(JsonSerializer.Serialize(body, FoundationApplication.Json), Encoding.UTF8, "application/json"); return await Client!.SendAsync(request); }
        public async Task<FoundationPlan> Plan() => await Read<FoundationPlan>(await Send("requester", HttpMethod.Post, "/plans",
            new PlanCommand(Template.Connection.Id, "main", Template.Request.SourceRevision, new() { ["location"] = "eastus2" })));
        public async Task Approve(FoundationPlan plan) => _ = await Read<FoundationPlan>(await Send("approver", HttpMethod.Post, $"/plans/{plan.Id}/approve", new { plan.Digest }));
        public async Task<FoundationJob> Enqueue(FoundationPlan plan) => await Read<FoundationJob>(await Send("operator", HttpMethod.Post, $"/plans/{plan.Id}/enqueue", new { plan.Digest, idempotencyKey = Guid.NewGuid().ToString("N") }));
        public FoundationJob QueueDirect()
        {
            var request = Template.Request with { RequestId = Guid.NewGuid().ToString("D") };
            var plan = Store.CreatePlan(Customer, Actor("requester"), new("default", "dev", Template.Connection.Id, "pipeline.dispatch", JsonSerializer.Serialize(request, FoundationApplication.Json), true,
                DateTimeOffset.UtcNow.AddMinutes(10), request.SourceRevision)); Store.ApprovePlan(Customer, Actor("approver"), plan.Id, plan.Digest);
            using var run = Selected.OpenRun(Store);
            return run.EnqueuePlan(Customer, Actor("operator"), plan.Id, plan.Digest, Guid.NewGuid().ToString("N"));
        }
        public FoundationJob ReadJob(string id) => Store.GetJob(Customer, Actor("viewer"), id);
        public string[] LogicalRows() => Rows("SELECT 'actor',actor_key,roles,version,enabled FROM actors JOIN memberships USING(customer_id,actor_key) UNION ALL SELECT 'audit',action,target_id,sequence,created FROM audit ORDER BY 1,2,3");
        public long Number(string sql) { using var db = Open(DatabasePath); using var query = db.CreateCommand(); query.CommandText = sql; return Convert.ToInt64(query.ExecuteScalar()); }
        public string[] Rows(string sql, string? databasePath = null)
        { using var db = Open(databasePath ?? DatabasePath); using var query = db.CreateCommand(); query.CommandText = sql; using var reader = query.ExecuteReader(); var rows = new List<string>(); while (reader.Read()) rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray())); return rows.ToArray(); }
        public string CopyAuthority()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
            var path = Path("copied-authority-" + Guid.NewGuid().ToString("N")); ActivationProvisionFixtureRoots.Create(path);
            foreach (var file in Directory.GetFiles(AuthorityPath)) File.Copy(file, System.IO.Path.Combine(path, System.IO.Path.GetFileName(file))); return path;
        }
        public void RemoveBindingRows()
        {
            using var db = Open(DatabasePath); using var transaction = db.BeginTransaction(); using var query = db.CreateCommand(); query.Transaction = transaction;
            query.CommandText = "SELECT name,sql FROM sqlite_master WHERE type='trigger' AND tbl_name='credential_runtime_bindings'";
            using var reader = query.ExecuteReader(); var triggers = new List<(string Name, string Sql)>(); while (reader.Read()) triggers.Add((reader.GetString(0), reader.GetString(1))); reader.Close();
            query.CommandText = string.Join(';', triggers.Select(t => "DROP TRIGGER " + t.Name)) + ";DELETE FROM credential_runtime_bindings;" + string.Join(';', triggers.Select(t => t.Sql)); query.ExecuteNonQuery(); transaction.Commit();
        }
        public (string Path, string Instance) SwitchTarget()
        { var path = Path("switch.sqlite"); var target = new FoundationStore(path, Cell, Customer); return (path, target.GetCellInstanceIdentity().DatabaseInstanceId); }
        public void AssertBusy() { var target = SwitchTarget(); Assert.Equal("ActivationAuthorityBusy", Assert.Throws<FoundationStoreException>(() => Authority.Activate(target.Path, target.Instance, Activation.Generation, Activation.Sha256)).Code); }
        public void AssertExclusiveAvailable() { using var guard = new FileStream(System.IO.Path.Combine(AuthorityPath, ".activation.guard"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); Assert.Equal(0, guard.Length); }
        public async Task WaitForExclusiveConflict()
        {
            using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (true)
            {
                try { using var guard = new FileStream(System.IO.Path.Combine(AuthorityPath, ".activation.guard"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
                catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return; }
                await Task.Delay(20, stop.Token);
            }
        }
        public void MarkAppDisposed() => appDisposed = true;
        public async ValueTask DisposeAsync()
        { Probe.Release.TrySetResult(); Client?.Dispose(); if (App is not null && !appDisposed) { await App.StopAsync(); await App.DisposeAsync(); } foreach (var consumer in consumers) consumer.Dispose(); Issuer.Dispose(); Template.Dispose(); }
        static async Task<T> Read<T>(HttpResponseMessage response)
        { var text = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}"); return JsonSerializer.Deserialize<T>(text, FoundationApplication.Json)!; }
    }
}
