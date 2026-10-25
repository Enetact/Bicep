using System.Net;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using SelfService.Portal;
using Xunit;
using BoundHost = SelfService.Foundation.Tests.FoundationBoundConsumerHostIntegrationTests.BoundHost;

namespace SelfService.Foundation.Tests;

public sealed class FoundationBoundTransactionalHostTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub)]
    [InlineData(PipelineRequestFactory.AzureDevOps)]
    public async Task Signed_AHP_enqueue_and_separate_HTTP_approval_use_transactional_bound_admission_end_to_end(string provider)
    {
        await using var f = await BoundHost.Create(provider); var plan = await f.Plan();
        using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(30));
        var websocket = f.App.GetTestServer().CreateWebSocketClient(); websocket.SubProtocols.Add("platform-studio");
        websocket.ConfigureRequest = request =>
        {
            using var signed = f.Request("operator", HttpMethod.Get, "/agent/ahp"); request.Headers.Authorization = signed.Headers.Authorization!.ToString();
        };
        using var socket = await websocket.ConnectAsync(new Uri("ws://localhost" + f.Root + "/agent/ahp"), watchdog.Token);
        _ = await Rpc(socket, 1, "initialize", new { channel = AgentHostChannel.Root, protocolVersions = new[] { AgentHostChannel.Version }, clientId = "transactional-bound-host" }, watchdog.Token);
        var channel = "ahp-session:/" + Guid.NewGuid().ToString("D");
        _ = await Rpc(socket, 2, "createSession", new { channel, provider = "platform-studio" }, watchdog.Token);
        var reference = new FoundationPlanReference(plan.Id, plan.Digest, "transactional-ahp-once");
        var rejected = await Rpc(socket, 3, "dispatchAction", new { channel, action = new { type = "platform/foundationEnqueue", payload = reference } }, watchdog.Token);
        Assert.True(rejected.TryGetProperty("error", out _) || !rejected.GetProperty("result").GetProperty("success").GetBoolean());
        Assert.Equal(0, f.Number("SELECT count(*) FROM jobs")); Assert.Equal(0, f.FactoryCalls); var keyReads = f.Keys.Requests.Count;
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("requester", HttpMethod.Post, $"/plans/{plan.Id}/approve", new { plan.Digest })).StatusCode);
        await f.Approve(plan);
        var accepted = await Rpc(socket, 4, "dispatchAction", new { channel, action = new { type = "platform/foundationEnqueue", payload = reference } }, watchdog.Token);
        Assert.True(accepted.GetProperty("result").GetProperty("success").GetBoolean());
        var queued = Assert.Single(f.Store.ListJobs(f.Customer, BoundHost.Actor("viewer"))); Assert.Equal("Queued", queued.State);
        Assert.Equal(keyReads, f.Keys.Requests.Count); Assert.Equal(0, f.FactoryCalls);
        var repeated = await Rpc(socket, 5, "dispatchAction", new { channel, action = new { type = "platform/foundationEnqueue", payload = reference } }, watchdog.Token);
        Assert.True(repeated.GetProperty("result").GetProperty("success").GetBoolean()); Assert.Equal(1, f.Number("SELECT count(*) FROM jobs"));
        Assert.True(await f.Runner.RunOnceAsync("transactional-ahp-worker", watchdog.Token));
        Assert.Equal("Succeeded", (await f.Job(queued.Id)).State); Assert.Equal(1, f.FactoryCalls); Assert.Equal(1, f.Posts); Assert.Equal(1, f.HandlerDisposals);
        Assert.Equal(keyReads + 1, f.Keys.Requests.Count); Assert.Equal("Bearer " + CredentialFixture.Token, f.Authorization);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", watchdog.Token);
    }

    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task Bare_enqueue_refuses_before_existing_idempotent_return_or_new_job_intent(bool duplicate)
    {
        await using var f = await BoundHost.Create(); var plan = await f.Plan(); await f.Approve(plan);
        if (duplicate) _ = await f.Enqueue(plan);
        var rows = f.JobRows(); var audit = f.Number("SELECT count(*) FROM audit"); var outbox = f.Number("SELECT count(*) FROM outbox"); var reads = f.Keys.Requests.Count;
        Denied(() => f.Store.EnqueuePlan(f.Customer, BoundHost.Actor("operator"), plan.Id, plan.Digest, "exact-host-job"));
        Assert.Equal(rows, f.JobRows()); Assert.Equal(audit, f.Number("SELECT count(*) FROM audit")); Assert.Equal(outbox, f.Number("SELECT count(*) FROM outbox"));
        Assert.Equal(reads, f.Keys.Requests.Count); Assert.Equal(0, f.FactoryCalls);
        _ = await f.Enqueue(plan); Assert.True(await f.Runner.RunOnceAsync("after-bare-enqueue-refusal")); Assert.Equal(1, f.Posts);
    }

    [Fact]
    public async Task Bare_claim_cannot_expire_or_requeue_existing_bound_dispatch_before_refusing()
    {
        using var f = await BoundTransportFixture.Create(); Sql(f.DatabasePath, "UPDATE jobs SET lease_until=0 WHERE state='Dispatched'");
        var rows = Rows(f.DatabasePath, "jobs"); var audit = Rows(f.DatabasePath, "audit"); var reads = f.Cell.Keys.Requests.Count;
        Denied(() => f.Store.ClaimNext(f.Cell.Scope.CustomerId, "bare-expiry-worker", TimeSpan.FromMinutes(1)));
        Assert.Equal(rows, Rows(f.DatabasePath, "jobs")); Assert.Equal(audit, Rows(f.DatabasePath, "audit")); Assert.Equal(reads, f.Cell.Keys.Requests.Count);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Bare_dispatch_or_mock_dispatch_cannot_cross_actual_claimed_intent(bool mock)
    {
        await using var f = await BoundHost.Create(); var plan = await f.Plan(); await f.Approve(plan); var queued = await f.Enqueue(plan);
        using var run = f.Consumer.OpenRun(f.Store); var claim = run.ClaimNext(f.Customer, "selected-claim-worker", TimeSpan.FromMinutes(2))!;
        Assert.Equal(queued.Id, claim.Id); var rows = f.JobRows(); var audit = f.Number("SELECT count(*) FROM audit"); var reads = f.Keys.Requests.Count;
        if (mock) Denied(() => f.Store.StartMockDispatch(f.Customer, claim.Id, "selected-claim-worker", claim.Fence, new([]), "1.0.0"));
        else Denied(() => f.Store.StartDispatch(f.Customer, claim.Id, "selected-claim-worker", claim.Fence));
        Assert.Equal(rows, f.JobRows()); Assert.Equal(audit, f.Number("SELECT count(*) FROM audit")); Assert.Equal(reads, f.Keys.Requests.Count); Assert.Equal(0, f.FactoryCalls);
        var dispatch = run.StartMockDispatch(f.Customer, claim.Id, "selected-claim-worker", claim.Fence, new([]), "1.0.0");
        Assert.Equal("Dispatched", dispatch.Job.State); Assert.Equal(0, f.FactoryCalls); Assert.Equal(reads, f.Keys.Requests.Count);
    }

    [Theory]
    [InlineData("MockLoopback")]
    [InlineData("ClaimHold")]
    public async Task Bare_changing_registration_refuses_before_stale_registry_cleanup(string mode)
    {
        await using var f = await BoundHost.Create();
        _ = f.Store.RegisterWorker(f.Customer, "old-disabled-worker", Environment.ProcessId, DateTimeOffset.UtcNow, "Disabled");
        Sql(f.Database, "UPDATE worker_heartbeats SET heartbeat=0");
        var workers = Rows(f.Database, "worker_heartbeats"); var audit = f.Number("SELECT count(*) FROM audit"); var reads = f.Keys.Requests.Count;
        Denied(() => f.Store.RegisterWorker(f.Customer, "bare-changing-worker", Environment.ProcessId, DateTimeOffset.UtcNow, mode));
        Assert.Equal(workers, Rows(f.Database, "worker_heartbeats")); Assert.Equal(audit, f.Number("SELECT count(*) FROM audit")); Assert.Equal(reads, f.Keys.Requests.Count);
        Assert.Equal(0, f.FactoryCalls);
    }

    static void Denied(Action action)
    {
        var error = Assert.Throws<FoundationStoreException>(action); Assert.Equal(403, error.Status);
        Assert.Equal("BoundPipelineConsumerRequired", error.Code);
    }
    static void Sql(string path, string sql) { using var db = Open(path); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    static string[] Rows(string path, string table)
    {
        using var db = Open(path); using var command = db.CreateCommand(); command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid";
        using var reader = command.ExecuteReader(); var rows = new List<string>();
        while (reader.Read()) rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray()));
        return rows.ToArray();
    }
    static SqliteConnection Open(string path) { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open(); return db; }
    static async Task<JsonElement> Rpc(WebSocket socket, int id, string method, object parameters, CancellationToken token)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { jsonrpc = "2.0", id, method, @params = parameters }, FoundationApplication.Json);
        await socket.SendAsync(bytes, WebSocketMessageType.Text, true, token); using var body = new MemoryStream(); var part = new byte[8192]; WebSocketReceiveResult result;
        do { result = await socket.ReceiveAsync(part, token); Assert.Equal(WebSocketMessageType.Text, result.MessageType); body.Write(part, 0, result.Count); Assert.InRange(body.Length, 1, 131072); } while (!result.EndOfMessage);
        using var document = JsonDocument.Parse(body.ToArray()); return document.RootElement.Clone();
    }
}
