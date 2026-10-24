using System.Net;
using System.Text.Json;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationTelemetryTests
{
    [Fact]
    public void Completed_outcomes_and_cumulative_latency_buckets_remain_consistent_and_double_completion_is_ignored()
    {
        var time = new MetricClock(); var metrics = new FoundationCellTelemetry(time);
        using var first = metrics.Start(); time.Advance(20); first.Complete(200); first.Complete(500);
        using var second = metrics.Start(); time.Advance(120); second.Complete(403);
        using var third = metrics.Start(); time.Advance(2000); third.Complete(200, wasAborted: true);
        using (metrics.Start()) { } // An observation abandoned on an exceptional path is counted as a server failure.
        var snapshot = metrics.Snapshot();
        Assert.Equal(4, snapshot.Started); Assert.Equal(4, snapshot.Completed); Assert.Equal(0, snapshot.InFlight);
        Assert.Equal(1, snapshot.Successful); Assert.Equal(1, snapshot.ClientRejected);
        Assert.Equal(1, snapshot.Aborted); Assert.Equal(1, snapshot.ServerFailed); Assert.Equal(0, snapshot.Other);
        Assert.Equal(2.14, snapshot.DurationSumSeconds, 6);
        Assert.Equal(1, snapshot.DurationBuckets[0].Count); Assert.Equal(2, snapshot.DurationBuckets[1].Count);
        Assert.Equal(4, snapshot.DurationBuckets[^1].Count); Assert.Null(snapshot.DurationBuckets[^1].UpperBoundSeconds);
        Assert.Equal(snapshot.DurationBuckets.Select(b => b.Count).Order().ToArray(), snapshot.DurationBuckets.Select(b => b.Count).ToArray());
        Assert.False(snapshot.Durable);
    }

    [Fact]
    public async Task Concurrent_requests_have_bounded_aggregate_state_and_independent_hosts_do_not_share_counters()
    {
        var first = new FoundationCellTelemetry(); var other = new FoundationCellTelemetry();
        var observations = Enumerable.Range(0, 256).Select(_ => first.Start()).ToArray();
        Assert.Equal(256, first.Snapshot().InFlight);
        await Parallel.ForEachAsync(observations, async (observation, _) => { observation.Complete(429); observation.Dispose(); await Task.Yield(); });
        var snapshot = first.Snapshot(); Assert.Equal(256, snapshot.ClientRejected); Assert.Equal(0, snapshot.InFlight);
        Assert.Equal(256, snapshot.PeakInFlight); Assert.Equal(9, snapshot.DurationBuckets.Length);
        Assert.Equal(0, other.Snapshot().Started); Assert.Equal(0, other.Snapshot().Completed);
        Assert.Equal(256, snapshot.DurationBuckets[^1].Count);
    }

    [Fact]
    public async Task Metrics_require_operations_role_and_expose_only_aggregates_without_customer_or_request_content()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("admin", HttpMethod.Get, "/operations/metrics")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("viewer", HttpMethod.Get, "/operations/metrics")).StatusCode);
        using (var anonymous = await f.Client.GetAsync(f.Root + "/operations/metrics")) Assert.Equal(HttpStatusCode.Unauthorized, anonymous.StatusCode);
        await f.Member("operator", [FoundationRoles.PlatformOperator]);
        const string privateMarker = "private-payload-marker-do-not-collect";
        using (var failed = await f.Send("viewer", HttpMethod.Post, "/artifacts", new ArtifactCommand("private.txt", privateMarker)))
            Assert.Equal(HttpStatusCode.Forbidden, failed.StatusCode);
        using var response = await f.Send("operator", HttpMethod.Get, "/operations/metrics", tokenRoles: [FoundationRoles.PlatformOperator]);
        var json = await response.Content.ReadAsStringAsync(); Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal("no-store", response.Headers.CacheControl!.ToString());
        using var result = JsonDocument.Parse(json); var root = result.RootElement;
        Assert.Equal("platform-studio.cell-metrics.v1", root.GetProperty("schema").GetString());
        Assert.False(root.GetProperty("externalExecutionAuthorized").GetBoolean());
        Assert.False(root.GetProperty("capacity").GetProperty("physicalDiskQuotaEnforced").GetBoolean());
        Assert.True(root.GetProperty("process").GetProperty("clientRejected").GetInt64() >= 3);
        foreach (var forbidden in new[] { privateMarker, f.CustomerId, FoundationApiTests.ApiCell.Actor("viewer"), "private.txt", "customer-org", "customer-repo", "user:", "Bearer " })
            Assert.DoesNotContain(forbidden, json, StringComparison.Ordinal);
        Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public async Task Operations_monitoring_survives_signed_feature_requirement_and_customer_suspension()
    {
        await using var f = await FoundationApiTests.ApiCell.Start(requireSignedEntitlement: true);
        await f.Member("operator", [FoundationRoles.PlatformOperator]);
        f.App.Services.GetRequiredService<FoundationStore>().SetCustomerEnabled(f.CustomerId, FoundationApiTests.ApiCell.Actor("admin"), false);
        using var response = await f.Send("operator", HttpMethod.Get, "/operations/metrics", tokenRoles: [FoundationRoles.PlatformOperator]);
        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        var result = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync());
        Assert.Equal("Suspended", result.GetProperty("cell").GetProperty("lifecycle").GetString());
        Assert.False(result.GetProperty("cell").GetProperty("enabled").GetBoolean());
        Assert.False(result.GetProperty("externalExecutionAuthorized").GetBoolean());
    }

    [Fact]
    public async Task Signed_cross_customer_and_wrong_scope_requests_cannot_read_metrics_or_change_the_other_collectors()
    {
        await using var first = await FoundationApiTests.ApiCell.Start();
        await using var second = await FoundationApiTests.ApiCell.Start(issuer: first.Issuer,
            customerId: FoundationApiTests.ApiCell.OtherCustomer, cellId: "00000000-0000-0000-0000-000000000004");
        var firstMetrics = first.App.Services.GetRequiredService<FoundationCellTelemetry>();
        var secondMetrics = second.App.Services.GetRequiredService<FoundationCellTelemetry>();
        var initial = secondMetrics.Snapshot();
        for (var i = 0; i < 8; i++) using (await first.Send("viewer", HttpMethod.Get, "/jobs")) { }
        Assert.True(firstMetrics.Snapshot().Completed >= 8); Assert.Equal(initial.Completed, secondMetrics.Snapshot().Completed);
        await first.Member("operator", [FoundationRoles.PlatformOperator]);
        using var foreign = await first.Send("operator", HttpMethod.Get, "/operations/metrics", tokenRoles: [FoundationRoles.PlatformOperator],
            root: $"/api/foundation/customers/{second.CustomerId}/projects/default/environments/dev");
        Assert.Equal(HttpStatusCode.Forbidden, foreign.StatusCode);
        using var scope = await first.Send("operator", HttpMethod.Get, "/operations/metrics", tokenRoles: [FoundationRoles.PlatformOperator],
            root: $"/api/foundation/customers/{first.CustomerId}/projects/other/environments/dev");
        Assert.Equal(HttpStatusCode.Forbidden, scope.StatusCode);
        Assert.Equal(initial.Completed, secondMetrics.Snapshot().Completed);
        Assert.Empty(first.Transport.Requests); Assert.Empty(second.Transport.Requests);
    }

    [Fact]
    public async Task Revoked_operations_membership_is_rechecked_for_every_metrics_read()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        await f.Member("operator", [FoundationRoles.PlatformOperator]);
        using (var allowed = await f.Send("operator", HttpMethod.Get, "/operations/metrics", tokenRoles: [FoundationRoles.PlatformOperator]))
            Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        await f.Member("operator", [FoundationRoles.PlatformOperator], enabled: false);
        using var denied = await f.Send("operator", HttpMethod.Get, "/operations/metrics", tokenRoles: [FoundationRoles.PlatformOperator]);
        Assert.Equal(HttpStatusCode.Forbidden, denied.StatusCode);
    }

    [Fact]
    public async Task Aborted_HTTP_body_is_counted_once_and_leaves_no_request_in_flight()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        var metrics = f.App.Services.GetRequiredService<FoundationCellTelemetry>(); var before = metrics.Snapshot();
        using var cancellation = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        using var identity = f.Request("requester", HttpMethod.Post, "/artifacts");
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var body = new WaitingStream(entered);
        var pending = f.App.GetTestServer().SendAsync(context =>
        {
            context.Request.Method = "POST"; context.Request.Path = f.Root + "/artifacts";
            context.Request.Host = new("localhost"); context.Request.ContentType = "application/json";
            context.Request.Headers.Authorization = identity.Headers.Authorization!.ToString();
            context.Request.Body = body; context.RequestAborted = cancellation.Token;
        }, cancellation.Token);
        await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancellation.Cancel();
        try { await pending.WaitAsync(TimeSpan.FromSeconds(5)); } catch (OperationCanceledException) { }
        var deadline = System.Diagnostics.Stopwatch.StartNew();
        while (metrics.Snapshot().Completed == before.Completed && deadline.Elapsed < TimeSpan.FromSeconds(5)) await Task.Delay(10);
        var after = metrics.Snapshot(); Assert.Equal(before.Completed + 1, after.Completed);
        Assert.Equal(before.Aborted + 1, after.Aborted); Assert.Equal(0, after.InFlight); Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public void Capacity_counters_are_role_scoped_and_readable_while_cell_is_suspended()
    {
        using var f = new StoreFixture();
        f.Store.SetMembership(f.Customer, "admin", "ops", "default", "dev", [FoundationRoles.PlatformOperator]);
        _ = f.Plan(); var capacity = f.Store.CapacityStatus(f.Customer, "ops", "default", "dev");
        Assert.Equal(1, capacity.PipelinePlans); Assert.True(capacity.AuditEvents > 0);
        Assert.Equal(FoundationStore.MaximumPlansPerCell, capacity.PipelinePlanLimit);
        Assert.Throws<FoundationStoreException>(() => f.Store.CapacityStatus(f.Customer, "auditor", "default", "dev"));
        f.Store.SetCustomerEnabled(f.Customer, "admin", false);
        Assert.Equal(1, f.Store.CapacityStatus(f.Customer, "ops", "default", "dev").PipelinePlans);
    }

    sealed class MetricClock : TimeProvider
    {
        long milliseconds;
        public override long TimestampFrequency => 1000;
        public override long GetTimestamp() => milliseconds;
        public void Advance(long value) => milliseconds += value;
    }

    sealed class WaitingStream(TaskCompletionSource entered) : Stream
    {
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken token = default)
        { entered.TrySetResult(); await Task.Delay(Timeout.InfiniteTimeSpan, token); return 0; }
        public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken token) => ReadAsync(buffer.AsMemory(offset, count), token).AsTask();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override void Flush() => throw new NotSupportedException(); public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException(); public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
    }
}
