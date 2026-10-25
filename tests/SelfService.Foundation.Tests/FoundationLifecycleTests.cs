using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationLifecycleTests
{
    const string Purpose = "Reviewed customer lifecycle test";

    [Fact]
    public void Scope_onboarding_requires_distinct_review_and_grants_only_the_new_exact_scope()
    {
        using var f = new StoreFixture(); var plan = f.Store.PrepareScopeOnboarding(f.Customer, "admin", "finance", "qa", "new-admin", Purpose);
        Assert.Empty(f.Store.GetMemberships(f.Customer, "new-admin"));
        Assert.Equal("AdministrativeApprovalRequired", Assert.Throws<FoundationStoreException>(() =>
            f.Store.ExecuteScopeOnboarding(f.Customer, "admin", plan.Id, plan.Digest)).Code);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", plan.Id, plan.Digest);
        var scope = f.Store.ExecuteScopeOnboarding(f.Customer, "admin", plan.Id, plan.Digest);
        Assert.Equal("finance", scope.ProjectId); Assert.Equal("qa", scope.EnvironmentId);
        var membership = Assert.Single(f.Store.GetMemberships(f.Customer, "new-admin"));
        Assert.Equal("finance", membership.ProjectId); Assert.Equal("qa", membership.EnvironmentId); Assert.Equal(["CustomerAdmin"], membership.Roles);
        Assert.Contains(f.Store.ListProjectScopes(f.Customer, "admin"), item => item.ProjectId == "finance" && item.EnvironmentId == "qa");
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.SetMembership(f.Customer, "new-admin", "outsider", "default", "dev", ["Operator"])).Code);
    }

    [Fact]
    public void Scope_onboarding_rejects_self_elevation_changed_grants_wrong_digest_and_replay()
    {
        using var f = new StoreFixture();
        Assert.Equal("SelfMembershipChangeDenied", Assert.Throws<FoundationStoreException>(() => f.Store.PrepareScopeOnboarding(f.Customer, "admin", "new", "dev", "admin", Purpose)).Code);
        var plan = f.Store.PrepareScopeOnboarding(f.Customer, "admin", "new", "dev", "approver", Purpose);
        Assert.Equal("SelfApprovalDenied", Assert.Throws<FoundationStoreException>(() => f.Store.ApproveAdministrativePlan(f.Customer, "approver", plan.Id, plan.Digest)).Code);
        var allowed = f.Store.PrepareScopeOnboarding(f.Customer, "admin", "other", "dev", "new-admin", Purpose);
        Assert.Equal("DigestMismatch", Assert.Throws<FoundationStoreException>(() => f.Store.ApproveAdministrativePlan(f.Customer, "approver", allowed.Id, new string('0', 64))).Code);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", allowed.Id, allowed.Digest);
        f.Store.SetMembership(f.Customer, "admin", "approver", "default", "dev", ["Approver"]);
        Assert.Equal("ApproverGrantChanged", Assert.Throws<FoundationStoreException>(() => f.Store.ExecuteScopeOnboarding(f.Customer, "admin", allowed.Id, allowed.Digest)).Code);
        var fresh = f.Store.PrepareScopeOnboarding(f.Customer, "admin", "last", "dev", "new-admin", Purpose);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", fresh.Id, fresh.Digest);
        f.Store.ExecuteScopeOnboarding(f.Customer, "admin", fresh.Id, fresh.Digest);
        Assert.Equal("AdministrativeApprovalRequired", Assert.Throws<FoundationStoreException>(() => f.Store.ExecuteScopeOnboarding(f.Customer, "admin", fresh.Id, fresh.Digest)).Code);
    }

    [Fact]
    public void Suspension_fences_queued_work_and_resume_never_replays_old_jobs()
    {
        using var f = new StoreFixture(); var job = f.Queue(); var claim = f.Store.ClaimNext(f.Customer, "worker", TimeSpan.FromMinutes(1))!;
        var suspended = Change(f, "Suspend"); Assert.Equal("Suspended", suspended.State); Assert.False(suspended.Enabled);
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM jobs WHERE state='Cancelled'"));
        Assert.Null(f.Store.ClaimNext(f.Customer, "new-worker", TimeSpan.FromMinutes(1)));
        Assert.False(f.Store.GetMemberships(f.Customer, "requester").Single().Enabled);
        Assert.True(f.Store.GetAdministrativeMemberships(f.Customer, "admin").Single().Enabled);
        Assert.Empty(f.Store.GetAdministrativeMemberships(f.Customer, "operator"));
        var resumed = Change(f, "Resume"); Assert.Equal("Active", resumed.State); Assert.True(resumed.Enabled);
        var held = f.Store.GetJob(f.Customer, "viewer", job.Id); Assert.Equal("Cancelled", held.State); Assert.True(held.Fence > claim.Fence);
        Assert.Null(f.Store.ClaimNext(f.Customer, "new-worker", TimeSpan.FromMinutes(1)));
    }

    [Fact]
    public void Offboarding_holds_unknown_effects_and_cannot_be_bypassed_with_legacy_resume()
    {
        using var f = new StoreFixture(); var job = f.Queue(); var claim = f.Store.ClaimNext(f.Customer, "worker", TimeSpan.FromMinutes(1))!;
        f.Store.StartDispatch(f.Customer, job.Id, "worker", claim.Fence);
        Assert.Equal("Offboarding", Change(f, "BeginOffboarding").State);
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM jobs WHERE state='OutcomeUnknown'"));
        Assert.Equal("LifecycleReviewRequired", Assert.Throws<FoundationStoreException>(() => f.Store.SetCustomerEnabled(f.Customer, "admin", true)).Code);
        var completion = f.Store.PrepareLifecycleChange(f.Customer, "admin", "CompleteOffboarding", Purpose);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", completion.Id, completion.Digest);
        Assert.Equal("ReconciliationRequired", Assert.Throws<FoundationStoreException>(() => f.Store.ExecuteLifecycleChange(f.Customer, "admin", completion.Id, completion.Digest)).Code);
        Assert.Equal("Offboarding", f.Store.CustomerLifecycle(f.Customer, "admin").State);
        Assert.Equal("Suspended", Change(f, "CancelOffboarding").State);
        Assert.Equal("Active", Change(f, "Resume").State);
        Assert.Equal("OutcomeUnknown", f.Store.GetJob(f.Customer, "viewer", job.Id).State);
        Assert.False(f.Store.GetConnection(f.Customer, "admin", "pipeline").Enabled);
    }

    [Fact]
    public void Completed_offboarding_preserves_database_and_audit_but_revokes_grants()
    {
        using var f = new StoreFixture(); Change(f, "BeginOffboarding"); var ended = Change(f, "CompleteOffboarding");
        Assert.Equal("Offboarded", ended.State); Assert.False(ended.Enabled); Assert.True(File.Exists(f.Path));
        Assert.True(f.Store.GetAdministrativeMemberships(f.Customer, "admin").Single().Enabled);
        Assert.False(f.Store.GetMemberships(f.Customer, "admin").Single().Enabled);
        Assert.Empty(f.Store.GetAdministrativeMemberships(f.Customer, "requester"));
        Assert.True(f.Scalar("SELECT count(*) FROM audit") > 0);
        Assert.Equal("InvalidLifecycleTransition", Assert.Throws<FoundationStoreException>(() => f.Store.PrepareLifecycleChange(f.Customer, "admin", "Resume", Purpose)).Code);
        Assert.NotNull(f.Store.ExportScope(f.Customer, "auditor", "default", "dev"));
    }

    [Theory]
    [InlineData("workload:tenant:object:client")]
    [InlineData("viewer")]
    public void Workloads_and_readers_cannot_manage_customer_lifecycle(string actor)
    {
        using var f = new StoreFixture(); Assert.Throws<FoundationStoreException>(() => f.Store.PrepareLifecycleChange(f.Customer, actor, "Suspend", Purpose));
        Assert.Throws<FoundationStoreException>(() => f.Store.PrepareScopeOnboarding(f.Customer, actor, "new", "qa", "new-admin", Purpose));
    }

    [Fact]
    public void Administrative_plan_expiry_and_cross_customer_requests_fail_closed()
    {
        using var f = new StoreFixture(); var plan = f.Store.PrepareLifecycleChange(f.Customer, "admin", "Suspend", Purpose);
        f.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal("AdministrativePlanExpired", Assert.Throws<FoundationStoreException>(() => f.Store.ApproveAdministrativePlan(f.Customer, "approver", plan.Id, plan.Digest)).Code);
        Assert.Equal("CustomerScopeDenied", Assert.Throws<FoundationStoreException>(() => f.Store.CustomerLifecycle("other-customer", "admin")).Code);
        Assert.Equal("NotFound", Assert.Throws<FoundationStoreException>(() => f.Store.GetAdministrativePlan(f.Customer, "admin", plan.Id, "other", "dev")).Code);
        Assert.Equal("InvalidLifecycleTransition", Assert.Throws<FoundationStoreException>(() => f.Store.PrepareLifecycleChange(f.Customer, "admin", "Resume", Purpose)).Code);
    }

    [Fact]
    public void Approval_ledger_and_plan_payload_tampering_cannot_change_reviewed_authority()
    {
        using var f = new StoreFixture(); var plan = f.Store.PrepareLifecycleChange(f.Customer, "admin", "Suspend", Purpose);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", plan.Id, plan.Digest);
        Assert.Throws<Microsoft.Data.Sqlite.SqliteException>(() => f.Execute("UPDATE administrative_approvals SET digest='edited'"));
        f.Execute("DROP TRIGGER administrative_approvals_no_update; UPDATE administrative_approvals SET digest='edited'");
        Assert.Equal("AdministrativeApprovalBindingChanged", Assert.Throws<FoundationStoreException>(() => f.Store.ExecuteLifecycleChange(f.Customer, "admin", plan.Id, plan.Digest)).Code);
        Assert.Equal("Active", f.Store.CustomerLifecycle(f.Customer, "admin").State);
        var other = f.Store.PrepareLifecycleChange(f.Customer, "admin", "Suspend", Purpose);
        f.Execute($"UPDATE administrative_plans SET payload='{{}}' WHERE id='{other.Id}'");
        Assert.Equal("DigestMismatch", Assert.Throws<FoundationStoreException>(() => f.Store.ApproveAdministrativePlan(f.Customer, "approver", other.Id, other.Digest)).Code);
    }

    [Theory]
    [InlineData("finance:unreachable")]
    [InlineData("trailing\n")]
    public void Onboarding_cannot_create_a_scope_the_signed_context_would_reject(string project)
    {
        using var f = new StoreFixture();
        Assert.Equal("InvalidScope", Assert.Throws<FoundationStoreException>(() => f.Store.PrepareScopeOnboarding(f.Customer, "admin", project, "dev", "new-admin", Purpose)).Code);
    }

    [Fact]
    public void Corrupt_audit_cannot_be_exported_as_verified_metadata()
    {
        using var f = new StoreFixture();
        f.Execute("DROP TRIGGER audit_no_update; UPDATE audit SET reason='rewritten' WHERE sequence=1");
        Assert.Equal("AuditIntegrityFailed", Assert.Throws<FoundationStoreException>(() => f.Store.ExportScope(f.Customer, "auditor", "default", "dev")).Code);
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM scope_exports"));
    }

    [Fact]
    public void Scope_export_is_metadata_only_scoped_durable_and_tamper_checked()
    {
        using var f = new StoreFixture(); var plan = f.Plan();
        var artifact = f.Store.StoreArtifact(f.Customer, "requester", "default", "dev", "private.bicep", "private customer source must not be exported"u8.ToArray());
        var export = f.Store.ExportScope(f.Customer, "auditor", "default", "dev");
        Assert.DoesNotContain("private customer source", export.ManifestJson); Assert.DoesNotContain("configurationJson", export.ManifestJson);
        Assert.DoesNotContain("\"owner\":\"example\"", export.ManifestJson); Assert.DoesNotContain("instanceCount", export.ManifestJson);
        Assert.Contains(artifact.Sha256, export.ManifestJson); Assert.Contains(plan.Digest, export.ManifestJson);
        Assert.Equal(export, f.Reopen().GetScopeExport(f.Customer, "auditor", export.Id, "default", "dev"));
        Assert.Equal(Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(export.ManifestJson))).ToLowerInvariant(), export.Sha256);
        using var json = JsonDocument.Parse(export.ManifestJson); Assert.False(json.RootElement.GetProperty("containsArtifactContents").GetBoolean());
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.ExportScope(f.Customer, "admin", "default", "dev")).Code);
        f.Execute("UPDATE scope_exports SET manifest='{}'");
        Assert.Equal("DigestMismatch", Assert.Throws<FoundationStoreException>(() => f.Store.GetScopeExport(f.Customer, "auditor", export.Id, "default", "dev")).Code);
    }

    static FoundationCustomerLifecycle Change(StoreFixture f, string action)
    {
        var plan = f.Store.PrepareLifecycleChange(f.Customer, "admin", action, Purpose);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", plan.Id, plan.Digest);
        return f.Store.ExecuteLifecycleChange(f.Customer, "admin", plan.Id, plan.Digest);
    }
}
