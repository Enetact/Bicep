using System.Net;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using SelfService.Portal;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationRecoveryExecutionHoldApiTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub)]
    [InlineData(PipelineRequestFactory.AzureDevOps)]
    public async Task Restored_Active_cell_refuses_fresh_HTTP_enqueue_with_exact_hold_error_and_no_new_intent(string provider)
    {
        await using var f = await HeldApi.Start(provider); var plan = await f.ApprovedPlan(); var counts = f.Counts();
        using var rejected = await f.Api.Send("operator", HttpMethod.Post, $"/plans/{plan.Id}/enqueue",
            new { plan.Digest, idempotencyKey = "fresh-held-http" }, tokenRoles: [FoundationRoles.Operator]);
        Assert.Equal(HttpStatusCode.Forbidden, rejected.StatusCode);
        Assert.Equal("{\"error\":\"RecoveryExecutionHeld\"}", await rejected.Content.ReadAsStringAsync());
        Assert.Equal(counts, f.Counts()); Assert.Empty(f.Api.Transport.Requests); Assert.Empty(f.Source.Transport.Requests);
        var history = await Read<FoundationJob>(await f.Api.Send("viewer", HttpMethod.Get, "/jobs/" + f.Historical.Id));
        Assert.Equal("OutcomeUnknown", history.State);
        Assert.Equal(plan.Digest, (await Read<FoundationPlan>(await f.Api.Send("viewer", HttpMethod.Get, "/plans/" + plan.Id))).Digest);
    }

    [Fact]
    public async Task Typed_AHP_enqueue_denies_generically_under_current_authority_and_keeps_read_actions_available()
    {
        await using var f = await HeldApi.Start(); var plan = await f.ApprovedPlan(); var counts = f.Counts();
        var client = f.Api.App.GetTestServer().CreateWebSocketClient(); client.SubProtocols.Add("platform-studio");
        client.ConfigureRequest = request => request.Headers.Authorization = f.Api.Request("operator", HttpMethod.Get, "/agent/ahp",
            tokenRoles: [FoundationRoles.Operator]).Headers.Authorization!.ToString();
        using var budget = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        using var socket = await client.ConnectAsync(new Uri("ws://localhost" + f.Api.Root + "/agent/ahp"), budget.Token);
        var initialized = await Rpc(socket, 1, "initialize", new { channel = AgentHostChannel.Root,
            protocolVersions = new[] { AgentHostChannel.Version }, clientId = "synthetic-recovery-hold" }, budget.Token);
        Assert.True(initialized.TryGetProperty("result", out _));
        var channel = "ahp-session:/" + Guid.NewGuid().ToString("D");
        _ = await Rpc(socket, 2, "createSession", new { channel, provider = "platform-studio" }, budget.Token);
        var denied = await Rpc(socket, 3, "dispatchAction", new { channel, action = new { type = "platform/foundationEnqueue",
            payload = new FoundationPlanReference(plan.Id, plan.Digest, "fresh-held-ahp") } }, budget.Token);
        var result = denied.GetProperty("result"); Assert.False(result.GetProperty("success").GetBoolean());
        Assert.Equal("Customer workflow authorization, scope or input validation rejected this call.", result.GetProperty("contentItems")[0].GetProperty("text").GetString());
        Assert.DoesNotContain("RecoveryExecutionHeld", result.GetRawText()); // The existing bridge deliberately keeps error details private.
        Assert.Equal(counts, f.Counts()); Assert.Empty(f.Api.Transport.Requests);
        var read = await Rpc(socket, 4, "dispatchAction", new { channel, action = new { type = "platform/foundationJob",
            payload = new FoundationObjectReference(f.Historical.Id) } }, budget.Token);
        Assert.True(read.GetProperty("result").GetProperty("success").GetBoolean());
        using var job = JsonDocument.Parse(read.GetProperty("result").GetProperty("contentItems")[0].GetProperty("text").GetString()!);
        Assert.Equal("OutcomeUnknown", job.RootElement.GetProperty("state").GetString());
        Assert.Equal(counts, f.Counts()); Assert.Empty(f.Api.Transport.Requests);
        await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", budget.Token);
    }

    [Fact]
    public async Task Existing_operations_health_exposes_hold_without_relabeling_storage_readiness_or_blocking_history()
    {
        await using var f = await HeldApi.Start();
        await f.Api.Member("viewer", [FoundationRoles.Viewer, FoundationRoles.PlatformOperator]);
        var status = await Read<FoundationOperationalStatus>(await f.Api.Send("viewer", HttpMethod.Get, "/operations/health",
            tokenRoles: [FoundationRoles.PlatformOperator]));
        Assert.True(status.Enabled); Assert.Equal("Active", status.Lifecycle);
        Assert.True(status.RecoveryExecutionHeld); Assert.Equal("RecoveryRequiresReconciliation", status.ExecutionHoldReason);
        using var health = await f.Api.Client.GetAsync("/health"); var storage = await Read<JsonElement>(health);
        Assert.True(storage.GetProperty("ready").GetBoolean()); Assert.False(storage.GetProperty("externalDispatch").GetBoolean());
        var history = await Read<FoundationJob>(await f.Api.Send("viewer", HttpMethod.Get, "/jobs/" + f.Historical.Id,
            tokenRoles: [FoundationRoles.Viewer])); Assert.Equal("OutcomeUnknown", history.State);
        var audit = await Read<JsonElement>(await f.Api.Send("auditor", HttpMethod.Get, "/audit", tokenRoles: [FoundationRoles.Auditor]));
        Assert.True(audit.GetProperty("integrityValid").GetBoolean()); Assert.Empty(f.Api.Transport.Requests);
        var raw = JsonSerializer.Serialize(status);
        Assert.DoesNotContain(f.Api.DatabasePath, raw); Assert.DoesNotContain("sourceDatabaseInstanceId", raw, StringComparison.OrdinalIgnoreCase);
    }

    static async Task<T> Read<T>(HttpResponseMessage response)
    {
        using (response)
        {
            var text = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
            return JsonSerializer.Deserialize<T>(text, FoundationApplication.Json)!;
        }
    }
    static async Task<JsonElement> Rpc(WebSocket socket, int id, string method, object parameters, CancellationToken cancellationToken)
    {
        await socket.SendAsync(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(new { jsonrpc = "2.0", id, method, @params = parameters }, FoundationApplication.Json)),
            WebSocketMessageType.Text, true, cancellationToken);
        using var bytes = new MemoryStream(); var buffer = new byte[8192]; WebSocketReceiveResult part;
        do
        {
            part = await socket.ReceiveAsync(buffer, cancellationToken); Assert.Equal(WebSocketMessageType.Text, part.MessageType);
            bytes.Write(buffer, 0, part.Count); Assert.True(bytes.Length <= 131072);
        } while (!part.EndOfMessage);
        using var document = JsonDocument.Parse(bytes.ToArray()); return document.RootElement.Clone();
    }

    sealed class HeldApi : IAsyncDisposable
    {
        public required FoundationApiTests.ApiCell Source { get; init; }
        public required FoundationApiTests.ApiCell Api { get; init; }
        public required FoundationJob Historical { get; init; }
        public required string OwnedRoot { get; init; }
        FoundationStore Store => Api.App.Services.GetRequiredService<FoundationStore>();
        public static async Task<HeldApi> Start(string provider = PipelineRequestFactory.GitHub)
        {
            var root = Path.Combine(Path.GetTempPath(), "studio-recovery-hold-api-" + Guid.NewGuid().ToString("N"));
            var source = await FoundationApiTests.ApiCell.Start(provider, directory: Path.Combine(root, "source"));
            FoundationApiTests.ApiCell? api = null;
            try
            {
                var sourcePlan = await source.Plan(); await source.Approve(sourcePlan); var historical = await source.Enqueue(sourcePlan);
                var sourceStore = source.App.Services.GetRequiredService<FoundationStore>();
                var backup = sourceStore.CreateBackup(Path.Combine(root, "snapshot.sqlite"));
                var restoredDirectory = Path.Combine(root, "restored"); var restored = FoundationStore.RestoreBackup(backup, Path.Combine(restoredDirectory, "cell.db"));
                Assert.NotEqual(sourceStore.GetCellInstanceIdentity().DatabaseInstanceId, restored.GetCellInstanceIdentity().DatabaseInstanceId);
                api = await FoundationApiTests.ApiCell.Start(provider, directory: restoredDirectory, issuer: source.Issuer);
                Assert.True(api.App.Services.GetRequiredService<FoundationStore>().CustomerLifecycle(FoundationApiTests.ApiCell.Customer, FoundationApiTests.ApiCell.Actor("admin")).Enabled);
                return new HeldApi { Source = source, Api = api, Historical = historical, OwnedRoot = root };
            }
            catch { if (api is not null) await api.DisposeAsync(); await source.DisposeAsync(); throw; }
        }
        public async Task<FoundationPlan> ApprovedPlan()
        {
            var plan = await Api.Plan(); await Api.Approve(plan);
            plan = await Read<FoundationPlan>(await Api.Send("viewer", HttpMethod.Get, "/plans/" + plan.Id, tokenRoles: [FoundationRoles.Viewer]));
            Assert.Equal(FoundationApiTests.ApiCell.Actor("requester"), plan.RequesterKey);
            Assert.Equal(FoundationApiTests.ApiCell.Actor("approver"), plan.ApproverKey);
            var requester = Assert.Single(Store.GetMemberships(FoundationApiTests.ApiCell.Customer, plan.RequesterKey));
            var approver = Assert.Single(Store.GetMemberships(FoundationApiTests.ApiCell.Customer, plan.ApproverKey!));
            var executor = Assert.Single(Store.GetMemberships(FoundationApiTests.ApiCell.Customer, FoundationApiTests.ApiCell.Actor("operator")));
            Assert.True(requester.Enabled && approver.Enabled && executor.Enabled); Assert.Contains(FoundationRoles.Operator, executor.Roles);
            Assert.Equal(plan.RequesterVersion, requester.Version); Assert.Equal(plan.ApproverVersion, approver.Version);
            Assert.Equal(plan.ConnectionGeneration, Store.GetConnection(FoundationApiTests.ApiCell.Customer, plan.RequesterKey, plan.ConnectionId).Generation);
            Assert.Equal(plan.PolicyVersion, Number("SELECT policy_version FROM cell WHERE singleton=1")); Assert.True(plan.ExpiresAt > DateTimeOffset.UtcNow);
            return plan;
        }
        public (long Jobs, long Outbox) Counts() => (Number("SELECT count(*) FROM jobs"), Number("SELECT count(*) FROM outbox"));
        long Number(string sql)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Api.DatabasePath, Pooling = false }.ToString()); db.Open();
            using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar());
        }
        public async ValueTask DisposeAsync()
        {
            await Api.DisposeAsync(); await Source.DisposeAsync();
            var root = Path.GetFullPath(OwnedRoot); var temp = Path.GetFullPath(Path.GetTempPath()); var name = Path.GetFileName(root);
            if (!root.StartsWith(temp, StringComparison.OrdinalIgnoreCase) ||
                !name.StartsWith("studio-recovery-hold-api-", StringComparison.Ordinal) || !Guid.TryParseExact(name[25..], "N", out _))
                throw new InvalidOperationException("Synthetic API cleanup boundary invalid.");
            if (Directory.Exists(root)) Directory.Delete(root, recursive: true);
        }
    }
}
