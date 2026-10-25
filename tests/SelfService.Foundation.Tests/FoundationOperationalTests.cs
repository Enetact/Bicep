using System.Net;
using System.Net.Http;
using System.Net.WebSockets;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationOperationalTests
{
    [Fact]
    public async Task Separate_customer_cells_in_same_directory_do_not_resolve_foreign_job_ids()
    {
        using var issuer = new IdentityTestIssuer();
        await using var a = await FoundationApiTests.ApiCell.Start(issuer: issuer);
        await using var b = await FoundationApiTests.ApiCell.Start(issuer: issuer, customerId: FoundationApiTests.ApiCell.OtherCustomer, cellId: "00000000-0000-0000-0000-000000000004");
        var plan = await a.Plan(); await a.Approve(plan); var job = await a.Enqueue(plan);
        var response = await b.Send("viewer", HttpMethod.Get, "/jobs/" + job.Id);
        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("{\"error\":\"NotFound\"}", await response.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await b.Send("viewer", HttpMethod.Get, "/jobs/" + job.Id, root: a.Root)).StatusCode);
        Assert.Empty(b.Transport.Requests);
    }

    [Fact]
    public async Task Saturating_one_cell_with_AHP_connections_cannot_exhaust_another_cells_request_slots()
    {
        await using var a = await FoundationApiTests.ApiCell.Start();
        await using var b = await FoundationApiTests.ApiCell.Start(customerId: FoundationApiTests.ApiCell.OtherCustomer, cellId: "00000000-0000-0000-0000-000000000004");
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(20));
        var sockets = new List<WebSocket>();
        try
        {
            for (var i = 0; i < 16; i++)
            {
                var client = a.App.GetTestServer().CreateWebSocketClient(); client.SubProtocols.Add("platform-studio");
                client.ConfigureRequest = r => r.Headers.Authorization = a.Request("viewer", HttpMethod.Get, "/agent/ahp").Headers.Authorization!.ToString();
                sockets.Add(await client.ConnectAsync(new Uri("ws://localhost" + a.Root + "/agent/ahp"), timeout.Token));
            }
            var otherMetrics = b.App.Services.GetRequiredService<SelfService.Foundation.Host.FoundationCellTelemetry>().Snapshot();
            Assert.Equal(HttpStatusCode.TooManyRequests, (await a.Send("viewer", HttpMethod.Get, "/jobs")).StatusCode);
            var pressureMetrics = a.App.Services.GetRequiredService<SelfService.Foundation.Host.FoundationCellTelemetry>().Snapshot();
            Assert.True(pressureMetrics.ClientRejected >= 1); Assert.True(pressureMetrics.InFlight >= 16);
            Assert.Equal(otherMetrics.Completed, b.App.Services.GetRequiredService<SelfService.Foundation.Host.FoundationCellTelemetry>().Snapshot().Completed);
            Assert.Equal(HttpStatusCode.OK, (await b.Send("viewer", HttpMethod.Get, "/jobs")).StatusCode);
        }
        finally { foreach (var socket in sockets) { await socket.CloseOutputAsync(WebSocketCloseStatus.NormalClosure, "Done", timeout.Token); socket.Dispose(); } }
    }

    [Fact]
    public async Task Operator_health_is_aggregate_only_and_available_during_suspension()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        await f.Member("operator", [FoundationRoles.PlatformOperator]);
        var store = f.App.Services.GetRequiredService<FoundationStore>();
        store.SetCustomerEnabled(f.CustomerId, FoundationApiTests.ApiCell.Actor("admin"), false);
        var response = await f.Send("operator", HttpMethod.Get, "/operations/health", tokenRoles: [FoundationRoles.PlatformOperator]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var text = await response.Content.ReadAsStringAsync();
        using var doc = JsonDocument.Parse(text); Assert.False(doc.RootElement.GetProperty("enabled").GetBoolean());
        foreach (var value in new[] { "actorKey", "payloadJson", "authProfileId", "configurationJson", "credential", "customer-repo" })
            Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("operator", HttpMethod.Get, "/audit", tokenRoles: [FoundationRoles.PlatformOperator])).StatusCode);
    }

    [Fact]
    public void Artifact_admission_rejects_logical_count_overflow_without_partial_write()
    {
        using var f = new StoreFixture();
        f.Execute($"""
            WITH RECURSIVE numbers(n) AS (SELECT 1 UNION ALL SELECT n+1 FROM numbers WHERE n<{FoundationStore.MaximumArtifactsPerCell})
            INSERT INTO artifacts SELECT '{f.Customer}','seed-'||n,'default','dev','empty.txt',X'',
            'e3b0c44298fc1c149afbf4c8996fb92427ae41e4649b934ca495991b7852b855','0' FROM numbers;
            """);
        var audit = f.Scalar("SELECT count(*) FROM audit");
        var error = Assert.Throws<FoundationStoreException>(() => f.Store.StoreArtifact(f.Customer, "requester", "default", "dev", "overflow.txt", "one"u8.ToArray()));
        Assert.Equal("ArtifactCapacityExceeded", error.Code); Assert.Equal(429, error.Status);
        Assert.Equal(FoundationStore.MaximumArtifactsPerCell, f.Scalar("SELECT count(*) FROM artifacts"));
        Assert.Equal(audit, f.Scalar("SELECT count(*) FROM audit"));
    }
}
