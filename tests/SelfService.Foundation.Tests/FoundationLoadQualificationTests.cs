using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Runtime.InteropServices;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using Xunit;
using Xunit.Abstractions;

namespace SelfService.Foundation.Tests;

// Conservative, versioned local regression targets, declared before measurement. These are not production SLOs.
public sealed class FoundationLoadQualificationTests(ITestOutputHelper output)
{
    const string TargetsVersion = "platform-studio.local-foundation-targets.v1";
    const double EnqueueP95LimitMs = 5000, ApiP95LimitMs = 3000, ApiMaximumLimitMs = 10000, ScenarioLimitMs = 60000;
    const int Clients = 4, Attempts = 160, ApiReads = 40;

    [Fact]
    public async Task Concurrent_enqueue_flood_is_bounded_at_128_with_atomic_backpressure_and_measured_latency()
    {
        using var fixture = new StoreFixture();
        var plans = Enumerable.Range(0, Attempts).Select(_ => fixture.Plan()).ToArray();
        foreach (var plan in plans) fixture.Store.ApprovePlan(fixture.Customer, "approver", plan.Id, plan.Digest);
        var auditBefore = fixture.Scalar("SELECT count(*) FROM audit");
        var clients = Enumerable.Range(0, Clients).Select(_ => fixture.Reopen()).ToArray();
        var elapsed = Stopwatch.StartNew();
        var flood = await Flood(clients, plans, fixture.Customer, "operator");
        elapsed.Stop();
        Emit("concurrent-enqueue-backpressure", elapsed.Elapsed.TotalMilliseconds, flood.Latencies,
            new { clients = Clients, attempts = Attempts, accepted = flood.Accepted, rejected429 = flood.Rejected,
                queueLimit = FoundationStore.MaximumPendingJobs, arrivalPacingMilliseconds = 0 },
            new { p95Milliseconds = EnqueueP95LimitMs, elapsedMilliseconds = ScenarioLimitMs });
        Assert.Equal(FoundationStore.MaximumPendingJobs, flood.Accepted); Assert.Equal(32, flood.Rejected);
        Assert.Equal(Attempts, flood.Latencies.Length);
        Assert.Equal(128, fixture.Scalar("SELECT count(*) FROM jobs"));
        Assert.Equal(128, fixture.Scalar("SELECT count(*) FROM outbox"));
        Assert.Equal(auditBefore + 128, fixture.Scalar("SELECT count(*) FROM audit"));
        Assert.Equal(128, fixture.Scalar("SELECT count(*) FROM audit WHERE action='JobQueued'"));
        Assert.True(fixture.Store.VerifyAuditIntegrity(fixture.Customer, "auditor"));
        Assert.True(Percentile(flood.Latencies, .95) <= EnqueueP95LimitMs, "Versioned local enqueue p95 target failed.");
        Assert.True(elapsed.Elapsed.TotalMilliseconds <= ScenarioLimitMs, "Versioned local enqueue scenario target failed.");
    }

    [Fact]
    public async Task Other_customer_signed_API_remains_isolated_while_first_cell_has_slow_provider_and_queue_flood()
    {
        using var issuer = new IdentityTestIssuer();
        await using var a = await FoundationApiTests.ApiCell.Start(issuer: issuer);
        await using var b = await FoundationApiTests.ApiCell.Start(issuer: issuer, customerId: FoundationApiTests.ApiCell.OtherCustomer,
            cellId: "00000000-0000-0000-0000-000000000004");
        var aStore = a.App.Services.GetRequiredService<FoundationStore>();
        var bStore = b.App.Services.GetRequiredService<FoundationStore>();
        var heldPlan = await a.Plan(); await a.Approve(heldPlan); var heldJob = await a.Enqueue(heldPlan);
        var bPlan = await b.Plan(); await b.Approve(bPlan);
        var plans = PreparePipelinePlans(aStore, 159);
        var clients = Enumerable.Range(0, Clients).Select(_ => new FoundationStore(a.DatabasePath, aStore.CellId,
            aStore.CustomerId, initializeSchema: false)).ToArray();
        using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var slow = new HeldFixtureTransport(a.Transport);
        var runner = new FoundationJobRunner(aStore, new PipelineDispatcher(slow));
        var dispatch = runner.RunOnceAsync("load-held-worker", stop.Token);
        try
        {
            await slow.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.Equal("Dispatched", aStore.GetJob(a.CustomerId, FoundationApiTests.ApiCell.Actor("operator"), heldJob.Id).State);
            using (var warm = await b.Send("viewer", HttpMethod.Get, "/jobs")) Assert.Equal(HttpStatusCode.OK, warm.StatusCode);
            var aAuditBefore = Scalar(a.DatabasePath, "SELECT count(*) FROM audit");
            var latencies = new List<double>(); var overlappedReads = 0;
            var elapsed = Stopwatch.StartNew();
            var flood = Flood(clients, plans, a.CustomerId, FoundationApiTests.ApiCell.Actor("operator"), pacingMilliseconds: 2);
            for (var index = 0; index < ApiReads; index++)
            {
                var overlap = !flood.IsCompleted;
                var request = Stopwatch.StartNew();
                using var response = await b.Send("viewer", HttpMethod.Get, "/jobs");
                var body = await response.Content.ReadAsStringAsync(); request.Stop(); latencies.Add(request.Elapsed.TotalMilliseconds);
                Assert.Equal(HttpStatusCode.OK, response.StatusCode);
                Assert.Empty(JsonSerializer.Deserialize<FoundationJob[]>(body, FoundationApplication.Json)!);
                if (overlap) overlappedReads++;
                Assert.False(dispatch.IsCompleted); // The slow provider remains held throughout the measured reads.
            }
            var pressure = await flood; elapsed.Stop();
            Emit("isolated-signed-api-under-pressure", elapsed.Elapsed.TotalMilliseconds, latencies.ToArray(),
                new { cells = 2, signedApiReads = ApiReads, statuses200 = ApiReads, overlappedReads,
                    pressureClients = Clients, pressureAttempts = plans.Length, accepted = pressure.Accepted, rejected429 = pressure.Rejected,
                    heldProviderRequests = slow.Calls, arrivalPacingMilliseconds = 2 },
                new { p95Milliseconds = ApiP95LimitMs, maximumMilliseconds = ApiMaximumLimitMs, elapsedMilliseconds = ScenarioLimitMs });
            Assert.True(overlappedReads > 0, "No measured API read overlapped the other customer's queue flood.");
            Assert.Equal(127, pressure.Accepted); Assert.Equal(32, pressure.Rejected);
            Assert.Equal(128, Scalar(a.DatabasePath, "SELECT count(*) FROM jobs WHERE state IN ('Queued','Claimed','Dispatched')"));
            Assert.Equal(aAuditBefore + 127, Scalar(a.DatabasePath, "SELECT count(*) FROM audit"));
            Assert.Empty(a.Transport.Requests); Assert.Empty(b.Transport.Requests);
            using (var foreignId = await b.Send("viewer", HttpMethod.Get, "/jobs/" + heldJob.Id)) Assert.Equal(HttpStatusCode.NotFound, foreignId.StatusCode);
            using (var forgedScope = await b.Send("operator", HttpMethod.Post, $"/plans/{heldPlan.Id}/enqueue",
                new { heldPlan.Digest, idempotencyKey = "cross-cell-denied" }, root: a.Root)) Assert.Equal(HttpStatusCode.Forbidden, forgedScope.StatusCode);
            var independentJob = await b.Enqueue(bPlan); // A's full queue does not consume B's admission capacity.
            Assert.Equal(b.CustomerId, independentJob.CustomerId); Assert.Equal("Queued", independentJob.State);
            Assert.Equal(1, Scalar(b.DatabasePath, "SELECT count(*) FROM jobs"));
            Assert.True(aStore.VerifyAuditIntegrity(a.CustomerId, FoundationApiTests.ApiCell.Actor("auditor")));
            Assert.True(bStore.VerifyAuditIntegrity(b.CustomerId, FoundationApiTests.ApiCell.Actor("auditor")));
            Assert.True(Percentile(latencies, .95) <= ApiP95LimitMs, "Versioned local signed API p95 target failed.");
            Assert.True(latencies.Max() <= ApiMaximumLimitMs, "Versioned local signed API maximum target failed.");
            Assert.True(elapsed.Elapsed.TotalMilliseconds <= ScenarioLimitMs, "Versioned local isolation scenario target failed.");
            slow.Release.TrySetResult(); await dispatch;
            Assert.Equal("Succeeded", aStore.GetJob(a.CustomerId, FoundationApiTests.ApiCell.Actor("operator"), heldJob.Id).State);
            Assert.Single(a.Transport.Requests); Assert.Empty(b.Transport.Requests);
        }
        finally { slow.Release.TrySetResult(); await dispatch; }
    }

    static FoundationPlan[] PreparePipelinePlans(FoundationStore store, int count)
    {
        var plans = new List<FoundationPlan>();
        for (var index = 0; index < count; index++)
        {
            var request = new PipelineDispatchRequest(store.CustomerId, store.CellId, "customer-ci", 1,
                Guid.NewGuid().ToString("D"), "main", "dev", FoundationApiTests.ApiCell.Revision, new() { ["region"] = "eastus2" });
            var plan = store.CreatePlan(store.CustomerId, FoundationApiTests.ApiCell.Actor("requester"), new("default", "dev", "customer-ci",
                "pipeline.dispatch", JsonSerializer.Serialize(request, FoundationApplication.Json), true, DateTimeOffset.UtcNow.AddMinutes(20), request.SourceRevision));
            store.ApprovePlan(store.CustomerId, FoundationApiTests.ApiCell.Actor("approver"), plan.Id, plan.Digest); plans.Add(plan);
        }
        return plans.ToArray();
    }

    sealed record FloodResult(int Accepted, int Rejected, double[] Latencies);
    static async Task<FloodResult> Flood(FoundationStore[] clients, FoundationPlan[] plans, string customer, string actor, int pacingMilliseconds = 0)
    {
        var accepted = 0; var rejected = 0; var samples = new ConcurrentBag<double>();
        using var ready = new CountdownEvent(clients.Length); using var start = new ManualResetEventSlim(false);
        var tasks = clients.Select((client, clientIndex) => Task.Run(() =>
        {
            ready.Signal(); Assert.True(start.Wait(TimeSpan.FromSeconds(15)));
            for (var index = clientIndex; index < plans.Length; index += clients.Length)
            {
                var plan = plans[index]; var elapsed = Stopwatch.StartNew();
                try { client.EnqueuePlan(customer, actor, plan.Id, plan.Digest, "load-" + index); Interlocked.Increment(ref accepted); }
                catch (FoundationStoreException error) when (error.Code == "QueueCapacityExceeded" && error.Status == 429)
                { Interlocked.Increment(ref rejected); }
                finally { elapsed.Stop(); samples.Add(elapsed.Elapsed.TotalMilliseconds); }
                if (pacingMilliseconds > 0) Thread.Sleep(pacingMilliseconds);
            }
        })).ToArray();
        Assert.True(ready.Wait(TimeSpan.FromSeconds(15))); start.Set(); await Task.WhenAll(tasks);
        return new(accepted, rejected, samples.ToArray());
    }

    void Emit(string scenario, double elapsedMilliseconds, double[] samples, object workload, object targets) => output.WriteLine(
        "PLATFORM_FOUNDATION_LOAD " + JsonSerializer.Serialize(new { schema = "platform-studio.foundation-load-measurement.v1",
            targetsVersion = TargetsVersion, scenario, elapsedMilliseconds, samplesMilliseconds = samples,
            workload, targets, runtime = RuntimeInformation.FrameworkDescription,
            processArchitecture = RuntimeInformation.ProcessArchitecture.ToString(), logicalProcessors = Environment.ProcessorCount,
            localQualificationOnly = true, productionSloVerified = false, externalDispatch = false }));

    static double Percentile(IEnumerable<double> values, double percentile)
    { var sorted = values.Order().ToArray(); return sorted[Math.Max(0, (int)Math.Ceiling(sorted.Length * percentile) - 1)]; }
    static long Scalar(string path, string sql)
    {
        using var db = new Microsoft.Data.Sqlite.SqliteConnection(new Microsoft.Data.Sqlite.SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar());
    }
    sealed class HeldFixtureTransport(IPipelineTransport inner) : IPipelineTransport
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public int Calls { get; private set; }
        public async Task<PipelineWireResponse> SendAsync(PipelineWireRequest request, CancellationToken cancellationToken)
        { Calls++; Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return await inner.SendAsync(request, cancellationToken); }
    }
}
