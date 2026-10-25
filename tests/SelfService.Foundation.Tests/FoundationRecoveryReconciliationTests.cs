using System.Net;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using Microsoft.Extensions.DependencyInjection;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationRecoveryReconciliationTests
{
    const string Purpose = "Resolve synthetic customer offboarding";
    const string Evidence = "{\"reviewedRun\":\"synthetic-123\",\"finding\":\"Recorded by the authorized operator\"}";

    [Theory]
    [InlineData("Succeeded", "ReconciledSucceeded")]
    [InlineData("FailedNoEffect", "ReconciledNoEffect")]
    public void Offboarding_can_complete_after_evidence_without_reactivation(string outcome, string expected)
    {
        using var f = new StoreFixture(); var job = Dispatch(f); Change(f, "BeginOffboarding");
        var completion = f.Store.PrepareLifecycleChange(f.Customer, "admin", "CompleteOffboarding", Purpose);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", completion.Id, completion.Digest);
        Assert.Equal("ReconciliationRequired", Assert.Throws<FoundationStoreException>(() =>
            f.Store.ExecuteLifecycleChange(f.Customer, "admin", completion.Id, completion.Digest)).Code);
        Assert.Equal("CustomerSuspended", Assert.Throws<FoundationStoreException>(() => f.Store.GetJob(f.Customer, "operator", job.Id)).Code);
        Assert.Equal("CustomerSuspended", Assert.Throws<FoundationStoreException>(() =>
            f.Store.ReconcileJob(f.Customer, "operator", job.Id, outcome, Evidence)).Code);
        var item = Assert.Single(f.Store.ListRecoveryCases(f.Customer, "operator", "default", "dev").Items);
        Assert.Equal(job.Id, item.Id); Assert.Equal("OutcomeUnknown", item.State);
        var result = f.Store.ReconcileRecoveryCase(f.Customer, "operator", "default", "dev", job.Id, item.CaseSha256, outcome, Evidence);
        Assert.Equal(expected, result.State); Assert.NotEqual(item.CaseSha256, result.CaseSha256);
        Assert.False(f.Store.CustomerLifecycle(f.Customer, "admin").Enabled);
        Assert.Empty(f.Store.ListRecoveryCases(f.Customer, "operator", "default", "dev").Items);
        Assert.Equal(expected, f.Store.GetRecoveryCase(f.Customer, "operator", "default", "dev", job.Id).State);
        Assert.Equal("ReconciliationNotRequired", Assert.Throws<FoundationStoreException>(() =>
            f.Store.ReconcileRecoveryCase(f.Customer, "operator", "default", "dev", job.Id, item.CaseSha256, outcome, Evidence)).Code);
        Assert.Null(f.Store.ClaimNext(f.Customer, "another-worker", TimeSpan.FromMinutes(1)));
        var ended = f.Store.ExecuteLifecycleChange(f.Customer, "admin", completion.Id, completion.Digest);
        Assert.Equal("Offboarded", ended.State); Assert.False(ended.Enabled);
        Assert.Empty(f.Store.GetRecoveryMemberships(f.Customer, "operator"));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM connections WHERE enabled=1"));
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM audit WHERE action='JobReconciled' AND reason='InactiveOperatorEvidenceRecorded'"));
    }

    [Fact]
    public void Suspended_cell_can_record_reconciliation_but_active_cell_uses_ordinary_flow()
    {
        using var f = new StoreFixture(); var job = Dispatch(f);
        Assert.Equal("InactiveRecoveryRequired", Assert.Throws<FoundationStoreException>(() =>
            f.Store.ListRecoveryCases(f.Customer, "operator", "default", "dev")).Code);
        Change(f, "Suspend");
        var item = f.Store.GetRecoveryCase(f.Customer, "operator", "default", "dev", job.Id);
        f.Store.ReconcileRecoveryCase(f.Customer, "operator", "default", "dev", job.Id, item.CaseSha256, "FailedNoEffect", Evidence);
        Assert.Equal("Suspended", f.Store.CustomerLifecycle(f.Customer, "admin").State);
        Assert.Null(f.Store.ClaimNext(f.Customer, "worker", TimeSpan.FromMinutes(1)));
        Assert.True(f.Store.GetRecoveryMemberships(f.Customer, "operator").Single().Enabled);
        Assert.False(f.Store.GetMemberships(f.Customer, "operator").Single().Enabled);
    }

    [Theory]
    [InlineData("admin")]
    [InlineData("approver")]
    [InlineData("viewer")]
    [InlineData("auditor")]
    [InlineData("requester")]
    [InlineData("workload:tenant:object:client")]
    public void Recovery_requires_current_human_operator_not_an_administrative_role(string actor)
    {
        using var f = new StoreFixture(); var job = Dispatch(f); Change(f, "BeginOffboarding");
        Assert.Throws<FoundationStoreException>(() => f.Store.ListRecoveryCases(f.Customer, actor, "default", "dev"));
        Assert.Throws<FoundationStoreException>(() => f.Store.GetRecoveryCase(f.Customer, actor, "default", "dev", job.Id));
        Assert.Throws<FoundationStoreException>(() =>
            f.Store.ReconcileRecoveryCase(f.Customer, actor, "default", "dev", job.Id, new string('a', 64), "Succeeded", Evidence));
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM jobs WHERE state='OutcomeUnknown'"));
    }

    [Fact]
    public void Cross_customer_and_scope_references_and_revoked_operator_are_denied()
    {
        using var f = new StoreFixture(); var job = Dispatch(f); Change(f, "BeginOffboarding");
        var item = f.Store.GetRecoveryCase(f.Customer, "operator", "default", "dev", job.Id);
        Assert.Equal("CustomerScopeDenied", Assert.Throws<FoundationStoreException>(() =>
            f.Store.GetRecoveryCase("customer-b", "operator", "default", "dev", job.Id)).Code);
        Assert.Throws<FoundationStoreException>(() => f.Store.GetRecoveryCase(f.Customer, "operator", "other", "qa", job.Id));
        f.Store.RevokeScopedAccess(f.Customer, "admin", "default", "dev", "operator", Purpose);
        Assert.DoesNotContain(f.Store.GetRecoveryMemberships(f.Customer, "operator"), m => m.Enabled);
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() =>
            f.Store.ReconcileRecoveryCase(f.Customer, "operator", "default", "dev", job.Id, item.CaseSha256, "Succeeded", Evidence)).Code);
    }

    [Fact]
    public void Membership_in_both_projects_does_not_admit_a_foreign_recovery_case()
    {
        using var f = new StoreFixture();
        var onboarding = f.Store.PrepareScopeOnboarding(f.Customer, "admin", "other", "qa", "other-admin", Purpose);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", onboarding.Id, onboarding.Digest);
        f.Store.ExecuteScopeOnboarding(f.Customer, "admin", onboarding.Id, onboarding.Digest);
        f.Store.SetMembership(f.Customer, "other-admin", "operator", "other", "qa", ["Operator"]);
        var job = Dispatch(f); Change(f, "BeginOffboarding");
        var item = f.Store.GetRecoveryCase(f.Customer, "operator", "default", "dev", job.Id);
        Assert.Empty(f.Store.ListRecoveryCases(f.Customer, "operator", "other", "qa").Items);
        Assert.Equal("NotFound", Assert.Throws<FoundationStoreException>(() =>
            f.Store.GetRecoveryCase(f.Customer, "operator", "other", "qa", job.Id)).Code);
        Assert.Equal("NotFound", Assert.Throws<FoundationStoreException>(() =>
            f.Store.ReconcileRecoveryCase(f.Customer, "operator", "other", "qa", job.Id, item.CaseSha256, "Succeeded", Evidence)).Code);
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM jobs WHERE state='OutcomeUnknown'"));
    }

    [Fact]
    public void Changed_case_and_invalid_evidence_leave_job_and_audit_unchanged()
    {
        using var f = new StoreFixture(); var job = Dispatch(f); Change(f, "BeginOffboarding");
        var item = f.Store.GetRecoveryCase(f.Customer, "operator", "default", "dev", job.Id);
        f.Execute("UPDATE jobs SET reason='NewOperatorReviewRequired'");
        var audit = f.Scalar("SELECT count(*) FROM audit");
        Assert.Equal("DigestMismatch", Assert.Throws<FoundationStoreException>(() =>
            f.Store.ReconcileRecoveryCase(f.Customer, "operator", "default", "dev", job.Id, item.CaseSha256, "Succeeded", Evidence)).Code);
        foreach (var evidence in new[] { "{}", "[]", "{\"token\":\"forbidden\"}", "{\"x\":1,\"X\":2}" })
            Assert.Throws<FoundationStoreException>(() =>
                f.Store.ReconcileRecoveryCase(f.Customer, "operator", "default", "dev", job.Id, item.CaseSha256, "Succeeded", evidence));
        Assert.Equal(audit, f.Scalar("SELECT count(*) FROM audit"));
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM jobs WHERE state='OutcomeUnknown' AND receipt IS NULL"));
    }

    [Fact]
    public void Recovery_reconciliation_and_required_audit_commit_together()
    {
        using var f = new StoreFixture(); var job = Dispatch(f); Change(f, "BeginOffboarding");
        var item = f.Store.GetRecoveryCase(f.Customer, "operator", "default", "dev", job.Id);
        f.Execute("CREATE TRIGGER fail_recovery_audit BEFORE INSERT ON audit WHEN NEW.action='JobReconciled' BEGIN SELECT RAISE(ABORT,'synthetic-fault'); END");
        Assert.Throws<SqliteException>(() => f.Store.ReconcileRecoveryCase(f.Customer, "operator", "default", "dev", job.Id, item.CaseSha256, "Succeeded", Evidence));
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM jobs WHERE state='OutcomeUnknown' AND receipt IS NULL"));
        f.Execute("DROP TRIGGER fail_recovery_audit");
        Assert.Equal("ReconciledSucceeded", f.Store.ReconcileRecoveryCase(f.Customer, "operator", "default", "dev", job.Id, item.CaseSha256, "Succeeded", Evidence).State);
    }

    [Fact]
    public void Bounded_case_queue_projects_metadata_only_and_advances_as_cases_are_resolved()
    {
        using var f = new StoreFixture(); Dispatch(f, "first"); Dispatch(f, "second"); Dispatch(f, "third");
        f.Execute("UPDATE jobs SET receipt='{\"privateProviderBody\":\"synthetic-private-result\"}'");
        Change(f, "BeginOffboarding"); var page = f.Store.ListRecoveryCases(f.Customer, "operator", "default", "dev", 2);
        Assert.True(page.HasMore); Assert.Equal(2, page.Items.Count);
        var text = JsonSerializer.Serialize(page);
        foreach (var forbidden in new[] { "synthetic-private-result", "privateProviderBody", "PayloadJson", "ReceiptJson", "OperatorKey", "RequesterKey" })
            Assert.DoesNotContain(forbidden, text);
        var item = page.Items[0];
        f.Store.ReconcileRecoveryCase(f.Customer, "operator", "default", "dev", item.Id, item.CaseSha256, "FailedNoEffect", Evidence);
        Assert.False(f.Store.ListRecoveryCases(f.Customer, "operator", "default", "dev", 2).HasMore);
        Assert.Throws<FoundationStoreException>(() => f.Store.ListRecoveryCases(f.Customer, "operator", "default", "dev", 101));
        Assert.Throws<FoundationStoreException>(() => f.Store.ListRecoveryCases(f.Customer, "operator", "default", "dev", 0));
    }

    [Fact]
    public async Task Signed_recovery_HTTP_closes_offboarding_without_reopening_normal_or_agent_routes()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        var plan = await f.Plan(); await f.Approve(plan); var job = await f.Enqueue(plan);
        var store = f.App.Services.GetRequiredService<FoundationStore>();
        var claim = store.ClaimNext(f.CustomerId, "fixture-worker", TimeSpan.FromMinutes(1))!;
        store.StartDispatch(f.CustomerId, job.Id, "fixture-worker", claim.Fence);
        await Change(f, "BeginOffboarding");
        var page = await Read<FoundationRecoveryCases>(await f.Send("operator", HttpMethod.Get, "/recovery/jobs", tokenRoles: [FoundationRoles.Operator]));
        var item = Assert.Single(page.Items);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("operator", HttpMethod.Get, "/jobs")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("operator", HttpMethod.Get, "/agent/tools")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("operator", HttpMethod.Get, "/agent/ahp")).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("operator", HttpMethod.Post, $"/jobs/{job.Id}/reconcile", new { outcome = "Succeeded", evidence = new { run = "fixture" } })).StatusCode);
        Assert.Equal(HttpStatusCode.Conflict, (await f.Send("operator", HttpMethod.Post, $"/recovery/jobs/{job.Id}/reconcile",
            new { caseSha256 = new string('0', 64), outcome = "Succeeded", evidence = new { run = "fixture" } })).StatusCode);
        var result = await Read<FoundationRecoveryCase>(await f.Send("operator", HttpMethod.Post, $"/recovery/jobs/{job.Id}/reconcile",
            new { item.CaseSha256, outcome = "FailedNoEffect", evidence = new { run = "synthetic-123", reviewed = true } }, tokenRoles: [FoundationRoles.Operator]));
        Assert.Equal("ReconciledNoEffect", result.State);
        await Change(f, "CompleteOffboarding");
        Assert.Equal("Offboarded", store.CustomerLifecycle(f.CustomerId, FoundationApiTests.ApiCell.Actor("admin")).State);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("operator", HttpMethod.Get, "/recovery/jobs")).StatusCode);
        Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public async Task Signed_roles_scope_workloads_and_body_fields_do_not_create_recovery_authority()
    {
        await using var f = await FoundationApiTests.ApiCell.Start(); var plan = await f.Plan(); await f.Approve(plan); var job = await f.Enqueue(plan);
        var store = f.App.Services.GetRequiredService<FoundationStore>(); var claim = store.ClaimNext(f.CustomerId, "fixture-worker", TimeSpan.FromMinutes(1))!;
        store.StartDispatch(f.CustomerId, job.Id, "fixture-worker", claim.Fence);
        var workerActor = $"workload:{IdentityTestIssuer.Tenant}:{IdentityTestIssuer.WorkerSubject}:{IdentityTestIssuer.WorkerClient}";
        store.SetMembership(f.CustomerId, FoundationApiTests.ApiCell.Actor("admin"), workerActor, "default", "dev", ["Operator"]);
        await Change(f, "Suspend");
        foreach (var actor in new[] { "viewer", "admin", "auditor", "requester", "approver" })
            Assert.Equal(HttpStatusCode.Forbidden, (await f.Send(actor, HttpMethod.Get, "/recovery/jobs", tokenRoles: [FoundationRoles.Operator])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("operator", HttpMethod.Get, "/recovery/jobs", tokenRoles: [FoundationRoles.Viewer])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("operator", HttpMethod.Get, "/recovery/jobs", root: f.Root.Replace(f.CustomerId, "customer-other"))).StatusCode);
        using var workerRequest = new HttpRequestMessage(HttpMethod.Get, f.Root + "/recovery/jobs");
        workerRequest.Headers.Authorization = new("Bearer", f.Issuer.Token(f.Issuer.WorkloadClaims()));
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Client.SendAsync(workerRequest)).StatusCode);
        var item = await Read<FoundationRecoveryCase>(await f.Send("operator", HttpMethod.Get, $"/recovery/jobs/{job.Id}"));
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Send("operator", HttpMethod.Post, $"/recovery/jobs/{job.Id}/reconcile",
            new { item.CaseSha256, outcome = "Succeeded", evidence = new { run = "fixture" }, customerId = "customer-other" })).StatusCode);
        Assert.Empty(f.Transport.Requests);
    }

    static FoundationJob Dispatch(StoreFixture f, string key = "recovery-job")
    {
        var job = f.Queue(key); var claim = f.Store.ClaimNext(f.Customer, "worker", TimeSpan.FromMinutes(1))!;
        f.Store.StartDispatch(f.Customer, job.Id, "worker", claim.Fence); return job;
    }
    static FoundationCustomerLifecycle Change(StoreFixture f, string action)
    {
        var plan = f.Store.PrepareLifecycleChange(f.Customer, "admin", action, Purpose);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", plan.Id, plan.Digest);
        return f.Store.ExecuteLifecycleChange(f.Customer, "admin", plan.Id, plan.Digest);
    }
    static async Task Change(FoundationApiTests.ApiCell f, string action)
    {
        var plan = await Read<FoundationAdministrativePlan>(await f.Send("admin", HttpMethod.Post, "/administration/lifecycle/plan", new CustomerLifecycleCommand(action, Purpose)));
        _ = await Read<FoundationAdministrativePlan>(await f.Send("approver", HttpMethod.Post, $"/administration/plans/{plan.Id}/approve", new { plan.Digest }));
        _ = await Read<FoundationCustomerLifecycle>(await f.Send("admin", HttpMethod.Post, $"/administration/plans/{plan.Id}/execute", new { plan.Digest }));
    }
    static async Task<T> Read<T>(HttpResponseMessage response)
    {
        var body = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {body}");
        return JsonSerializer.Deserialize<T>(body, FoundationApplication.Json)!;
    }
}
