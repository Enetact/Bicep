using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationRetentionTests
{
    const string Purpose = "Reviewed local artifact retention";

    [Fact]
    public void Reviewed_retention_removes_only_selected_old_content_and_preserves_tombstone_audit()
    {
        using var f = new StoreFixture();
        var old = Artifact(f, "old.bicep"); f.Clock.Advance(TimeSpan.FromDays(31)); var recent = Artifact(f, "recent.bicep");
        var cutoff = f.Clock.GetUtcNow().AddDays(-30);
        var preview = f.Store.PrepareArtifactRetention(f.Customer, "admin", "default", "dev", cutoff, Purpose);
        Assert.Equal(old.Id, Assert.Single(preview.Candidates).Id);
        Assert.Equal("AdministrativeApprovalRequired", Assert.Throws<FoundationStoreException>(() =>
            f.Store.ExecuteArtifactRetention(f.Customer, "admin", preview.Plan.Id, preview.Plan.Digest)).Code);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", preview.Plan.Id, preview.Plan.Digest);
        var auditBefore = f.Scalar("SELECT count(*) FROM audit");
        var receipt = f.Store.ExecuteArtifactRetention(f.Customer, "admin", preview.Plan.Id, preview.Plan.Digest);
        Assert.Equal(1, receipt.ArtifactsArchived); Assert.True(receipt.AuditHistoryPreserved); Assert.False(receipt.SecureErasureVerified);
        Assert.Equal("NotFound", Assert.Throws<FoundationStoreException>(() => f.Store.GetArtifact(f.Customer, "viewer", old.Id)).Code);
        Assert.Equal(recent.Sha256, f.Store.GetArtifact(f.Customer, "viewer", recent.Id).Sha256);
        var archived = Assert.Single(f.Store.ListArchivedArtifacts(f.Customer, "auditor", "default", "dev"));
        Assert.Equal(old.Sha256, archived.Sha256); Assert.True(f.Scalar("SELECT count(*) FROM audit") > auditBefore);
        Assert.True(f.Store.VerifyAuditIntegrity(f.Customer, "auditor"));
        Assert.Equal("AdministrativeApprovalRequired", Assert.Throws<FoundationStoreException>(() => f.Store.ExecuteArtifactRetention(f.Customer, "admin", preview.Plan.Id, preview.Plan.Digest)).Code);
    }

    [Fact]
    public void Retention_does_not_run_below_minimum_age_or_while_work_might_use_source()
    {
        using var f = new StoreFixture(); _ = Artifact(f, "old.bicep");
        Assert.Equal("MinimumRetentionNotMet", Assert.Throws<FoundationStoreException>(() =>
            f.Store.PrepareArtifactRetention(f.Customer, "admin", "default", "dev", f.Clock.GetUtcNow(), Purpose)).Code);
        f.Clock.Advance(TimeSpan.FromDays(31)); var plan = f.Plan();
        Assert.Equal("RetentionBlockedByWork", Assert.Throws<FoundationStoreException>(() =>
            f.Store.PrepareArtifactRetention(f.Customer, "admin", "default", "dev", f.Clock.GetUtcNow().AddDays(-30), Purpose)).Code);
        f.Store.ApprovePlan(f.Customer, "approver", plan.Id, plan.Digest); f.Store.EnqueuePlan(f.Customer, "operator", plan.Id, plan.Digest, "pending");
        f.Clock.Advance(TimeSpan.FromHours(1));
        Assert.Equal("RetentionBlockedByWork", Assert.Throws<FoundationStoreException>(() =>
            f.Store.PrepareArtifactRetention(f.Customer, "admin", "default", "dev", f.Clock.GetUtcNow().AddDays(-30), Purpose)).Code);
    }

    [Fact]
    public void New_work_after_approval_blocks_execution_and_does_not_remove_any_artifact()
    {
        using var f = new StoreFixture(); var artifact = Artifact(f, "old.bicep"); f.Clock.Advance(TimeSpan.FromDays(31));
        var preview = Prepare(f); f.Store.ApproveAdministrativePlan(f.Customer, "approver", preview.Plan.Id, preview.Plan.Digest);
        _ = f.Plan();
        Assert.Equal("RetentionBlockedByWork", Assert.Throws<FoundationStoreException>(() =>
            f.Store.ExecuteArtifactRetention(f.Customer, "admin", preview.Plan.Id, preview.Plan.Digest)).Code);
        Assert.Equal(artifact.Sha256, f.Store.GetArtifact(f.Customer, "viewer", artifact.Id).Sha256);
        Assert.Empty(f.Store.ListArchivedArtifacts(f.Customer, "auditor", "default", "dev"));
    }

    [Fact]
    public void Candidate_tampering_rolls_back_all_content_and_tombstone_changes()
    {
        using var f = new StoreFixture(); var a = Artifact(f, "a.bicep"); var b = Artifact(f, "b.bicep");
        f.Clock.Advance(TimeSpan.FromDays(31)); var preview = Prepare(f);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", preview.Plan.Id, preview.Plan.Digest);
        f.Execute($"UPDATE artifacts SET content=x'00' WHERE id='{b.Id}'");
        Assert.Equal("RetentionSnapshotChanged", Assert.Throws<FoundationStoreException>(() =>
            f.Store.ExecuteArtifactRetention(f.Customer, "admin", preview.Plan.Id, preview.Plan.Digest)).Code);
        Assert.Equal(2, f.Scalar("SELECT count(*) FROM artifacts")); Assert.Equal(0, f.Scalar("SELECT count(*) FROM artifact_tombstones"));
        Assert.Equal(a.Sha256, f.Store.GetArtifact(f.Customer, "viewer", a.Id).Sha256);
    }

    [Fact]
    public void Offboarded_customer_retains_reviewed_administration_and_audit_for_local_retention()
    {
        using var f = new StoreFixture(); _ = Artifact(f, "old.bicep"); f.Clock.Advance(TimeSpan.FromDays(31));
        foreach (var action in new[] { "BeginOffboarding", "CompleteOffboarding" })
        {
            var lifecycle = f.Store.PrepareLifecycleChange(f.Customer, "admin", action, Purpose);
            f.Store.ApproveAdministrativePlan(f.Customer, "approver", lifecycle.Id, lifecycle.Digest);
            f.Store.ExecuteLifecycleChange(f.Customer, "admin", lifecycle.Id, lifecycle.Digest);
        }
        var plan = Prepare(f); f.Store.ApproveAdministrativePlan(f.Customer, "approver", plan.Plan.Id, plan.Plan.Digest);
        Assert.Equal(1, f.Store.ExecuteArtifactRetention(f.Customer, "admin", plan.Plan.Id, plan.Plan.Digest).ArtifactsArchived);
        Assert.NotNull(f.Store.ExportScope(f.Customer, "auditor", "default", "dev"));
        Assert.Equal("CustomerSuspended", Assert.Throws<FoundationStoreException>(() => f.Store.CreatePlan(f.Customer, "requester", f.Input())).Code);
    }

    [Fact]
    public void Revoked_approver_and_cross_customer_selectors_cannot_authorize_retention()
    {
        using var f = new StoreFixture(); _ = Artifact(f, "old.bicep"); f.Clock.Advance(TimeSpan.FromDays(31)); var preview = Prepare(f);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", preview.Plan.Id, preview.Plan.Digest);
        f.Store.SetMembership(f.Customer, "admin", "approver", "default", "dev", ["Approver"], false);
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.ExecuteArtifactRetention(f.Customer, "admin", preview.Plan.Id, preview.Plan.Digest)).Code);
        Assert.Equal("CustomerScopeDenied", Assert.Throws<FoundationStoreException>(() => f.Store.ListArchivedArtifacts("other", "auditor", "default", "dev")).Code);
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.ListArchivedArtifacts(f.Customer, "auditor", "other", "dev")).Code);
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.PrepareArtifactRetention(f.Customer, "requester", "default", "dev", f.Clock.GetUtcNow().AddDays(-30), Purpose)).Code);
    }

    static FoundationArtifact Artifact(StoreFixture f, string name) =>
        f.Store.StoreArtifact(f.Customer, "requester", "default", "dev", name, "// local retained source"u8.ToArray());
    static FoundationArtifactRetentionPlan Prepare(StoreFixture f) =>
        f.Store.PrepareArtifactRetention(f.Customer, "admin", "default", "dev", f.Clock.GetUtcNow().AddDays(-30), Purpose);
}
