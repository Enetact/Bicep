using System.Collections.Concurrent;
using System.Diagnostics;
using System.Runtime.InteropServices;
using SelfService.Foundation;
using Xunit;
using Xunit.Abstractions;

namespace SelfService.Foundation.Tests;

public sealed class FoundationCapacityTests(ITestOutputHelper output)
{
    [Fact]
    public void Queue_capacity_rejects_129th_job_without_partial_job_outbox_or_audit_commit()
    {
        using var f = new StoreFixture(); var elapsed = Stopwatch.StartNew();
        for (var index = 0; index < FoundationStore.MaximumPendingJobs; index++) f.Queue("capacity-" + index);
        var overflow = f.Plan(); f.Store.ApprovePlan(f.Customer, "approver", overflow.Id, overflow.Digest);
        var auditBefore = f.Scalar("SELECT count(*) FROM audit");
        var error = Assert.Throws<FoundationStoreException>(() => f.Store.EnqueuePlan(f.Customer, "operator", overflow.Id, overflow.Digest, "capacity-overflow"));
        Assert.Equal("QueueCapacityExceeded", error.Code); Assert.Equal(429, error.Status);
        Assert.Equal(FoundationStore.MaximumPendingJobs, f.Store.ListJobs(f.Customer, "operator", limit: 1000).Count);
        Assert.Equal(FoundationStore.MaximumPendingJobs, f.Store.PendingOutbox(f.Customer, limit: 1000).Count);
        Assert.Equal(auditBefore, f.Scalar("SELECT count(*) FROM audit"));
        var queued = f.Store.ListAudit(f.Customer, "auditor", limit: 1000).Where(e => e.Action == "JobQueued").ToArray();
        Assert.Equal(FoundationStore.MaximumPendingJobs, queued.Length);
        Assert.All(queued, e => { Assert.Equal(f.Customer, e.CustomerId); Assert.Equal(f.Cell, e.CellId); });
        Assert.DoesNotContain(queued, e => e.CorrelationId == overflow.Id);
        Assert.True(f.Store.VerifyAuditIntegrity(f.Customer, "auditor"));
        WriteQualification("128 accepted jobs and one rejected overflow", elapsed.Elapsed);
    }

    [Fact]
    public async Task Four_database_clients_claim_and_complete_16_mock_jobs_once_each()
    {
        using var f = new StoreFixture(); const int count = 16;
        var expected = Enumerable.Range(0, count).Select(index => f.Queue("parallel-" + index).Id).ToHashSet(StringComparer.Ordinal);
        var clients = Enumerable.Range(0, 4).Select(_ => f.Reopen()).ToArray();
        var claims = new ConcurrentDictionary<string, int>(StringComparer.Ordinal); var elapsed = Stopwatch.StartNew();
        await Task.WhenAll(clients.Select((client, index) => Task.Run(() =>
        {
            var worker = "parallel-worker-" + index;
            while (client.ClaimNext(f.Customer, worker, TimeSpan.FromMinutes(1)) is { } job)
            {
                claims.AddOrUpdate(job.Id, 1, (_, previous) => previous + 1);
                client.StartDispatch(f.Customer, job.Id, worker, job.Fence);
                client.CompleteJob(f.Customer, job.Id, worker, job.Fence, "{\"simulated\":true,\"dispatchAccepted\":true,\"deploymentVerified\":false}");
            }
        })));
        Assert.Equal(count, claims.Count); Assert.All(claims, pair => Assert.Equal(1, pair.Value));
        Assert.True(expected.SetEquals(claims.Keys));
        var jobs = f.Reopen().ListJobs(f.Customer, "operator"); Assert.Equal(count, jobs.Count);
        Assert.All(jobs, job => { Assert.Equal("Succeeded", job.State); Assert.Equal(1, job.Attempt); Assert.Equal(1, job.Fence); });
        Assert.Equal(count, f.Store.PendingOutbox(f.Customer).Count);
        Assert.True(f.Store.VerifyAuditIntegrity(f.Customer, "auditor"));
        WriteQualification("16 jobs, four SQLite clients, zero external calls", elapsed.Elapsed);
    }

    void WriteQualification(string scenario, TimeSpan elapsed) => output.WriteLine(
        "Local qualification only: {0}; elapsed={1:F3}s; architecture={2}; logicalProcessors={3}; OS={4}; runtime={5}. This is not an SLO or production throughput result.",
        scenario, elapsed.TotalSeconds, RuntimeInformation.ProcessArchitecture, Environment.ProcessorCount,
        RuntimeInformation.OSDescription, RuntimeInformation.FrameworkDescription);
}
