using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationAccessTests
{
    const string Purpose = "Customer-approved support investigation";
    const string SupportActor = "user:11111111-1111-1111-1111-111111111111:bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";

    [Fact]
    public void Bootstrap_distinct_reviewer_is_seeded_once_and_never_regranted_after_revocation()
    {
        using var f = new StoreFixture(); var path = f.Path + ".bootstrap";
        try
        {
            var store = new FoundationStore(path, f.Cell, f.Customer, f.Clock);
            store.BootstrapAdministrator("initial-admin", initialApproverKey: "initial-reviewer");
            Assert.Equal(["Approver"], store.GetAdministrativeMemberships(f.Customer, "initial-reviewer").Single().Roles);
            store.RevokeScopedAccess(f.Customer, "initial-admin", "default", "dev", "initial-reviewer", Purpose);
            new FoundationStore(path, f.Cell, f.Customer, f.Clock).BootstrapAdministrator("initial-admin", initialApproverKey: "initial-reviewer");
            Assert.False(store.GetAdministrativeMemberships(f.Customer, "initial-reviewer").Single().Enabled);
            Assert.Equal("BootstrapReviewerMismatch", Assert.Throws<FoundationStoreException>(() => store.BootstrapAdministrator("initial-admin", initialApproverKey: "replacement")).Code);
            Assert.Equal("BootstrapReviewerMismatch", Assert.Throws<FoundationStoreException>(() => store.BootstrapAdministrator("initial-admin")).Code);
            Assert.Equal("SelfApprovalDenied", Assert.Throws<FoundationStoreException>(() => store.BootstrapAdministrator("initial-admin", initialApproverKey: "initial-admin")).Code);
        }
        finally { foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); }
    }

    [Fact]
    public void Legacy_cell_cannot_silently_add_a_bootstrap_reviewer()
    {
        using var f = new StoreFixture();
        Assert.Equal("BootstrapReviewerMismatch", Assert.Throws<FoundationStoreException>(() =>
            f.Store.BootstrapAdministrator("admin", initialApproverKey: "new-bootstrap-reviewer")).Code);
        Assert.Empty(f.Store.GetMemberships(f.Customer, "new-bootstrap-reviewer"));
    }

    [Fact]
    public void Reviewed_scope_onboarding_seeds_a_distinct_scoped_reviewer_for_future_access_plans()
    {
        using var f = new StoreFixture(); var plan = f.Store.PrepareScopeOnboarding(f.Customer, "admin", "finance", "qa", "scope-admin", Purpose, "scope-reviewer");
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", plan.Id, plan.Digest);
        f.Store.ExecuteScopeOnboarding(f.Customer, "admin", plan.Id, plan.Digest);
        var grant = f.Store.PrepareMembershipChange(f.Customer, "scope-admin", "finance", "qa", "scoped-operator", ["Operator"], true, Purpose);
        f.Store.ApproveAdministrativePlan(f.Customer, "scope-reviewer", grant.Id, grant.Digest);
        f.Store.ExecuteMembershipChange(f.Customer, "scope-admin", grant.Id, grant.Digest);
        Assert.Equal("finance", f.Store.GetMemberships(f.Customer, "scoped-operator").Single().ProjectId);
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.PrepareMembershipChange(f.Customer, "scope-admin", "default", "dev", "other", ["Operator"], true, Purpose)).Code);
    }

    [Fact]
    public void Membership_grants_require_exact_independent_approval_and_reject_self_elevation_or_replay()
    {
        using var f = new StoreFixture(); var plan = f.Store.PrepareMembershipChange(f.Customer, "admin", "default", "dev", "new-operator", ["Operator"], true, Purpose);
        Assert.Empty(f.Store.GetMemberships(f.Customer, "new-operator"));
        Assert.Equal("AdministrativeApprovalRequired", Assert.Throws<FoundationStoreException>(() => f.Store.ExecuteMembershipChange(f.Customer, "admin", plan.Id, plan.Digest)).Code);
        Assert.Equal("SelfApprovalDenied", Assert.Throws<FoundationStoreException>(() => f.Store.ApproveAdministrativePlan(f.Customer, "new-operator", plan.Id, plan.Digest)).Code);
        Assert.Equal("SelfMembershipChangeDenied", Assert.Throws<FoundationStoreException>(() => f.Store.PrepareMembershipChange(f.Customer, "admin", "default", "dev", "admin", ["CustomerAdmin", "Operator"], true, Purpose)).Code);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", plan.Id, plan.Digest);
        var membership = f.Store.ExecuteMembershipChange(f.Customer, "admin", plan.Id, plan.Digest);
        Assert.True(membership.Enabled); Assert.Equal(["Operator"], membership.Roles);
        Assert.Equal("AdministrativeApprovalRequired", Assert.Throws<FoundationStoreException>(() => f.Store.ExecuteMembershipChange(f.Customer, "admin", plan.Id, plan.Digest)).Code);
    }

    [Fact]
    public void Changed_target_or_reviewer_grants_invalidate_pending_membership_changes()
    {
        using var f = new StoreFixture(); var plan = f.Store.PrepareMembershipChange(f.Customer, "admin", "default", "dev", "target", ["Operator"], true, Purpose);
        f.Store.SetMembership(f.Customer, "admin", "target", "default", "dev", ["Viewer"]);
        Assert.Equal("TargetGrantChanged", Assert.Throws<FoundationStoreException>(() => f.Store.ApproveAdministrativePlan(f.Customer, "approver", plan.Id, plan.Digest)).Code);
        var fresh = f.Store.PrepareMembershipChange(f.Customer, "admin", "default", "dev", "target", ["Operator"], true, Purpose);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", fresh.Id, fresh.Digest);
        f.Store.SetMembership(f.Customer, "admin", "approver", "default", "dev", ["Approver"]);
        Assert.Equal("ApproverGrantChanged", Assert.Throws<FoundationStoreException>(() => f.Store.ExecuteMembershipChange(f.Customer, "admin", fresh.Id, fresh.Digest)).Code);
        Assert.Equal(["Viewer"], f.Store.GetMemberships(f.Customer, "target").Single().Roles);
    }

    [Fact]
    public void Urgent_revocation_is_deny_only_and_last_administrator_is_protected()
    {
        using var f = new StoreFixture(); var response = f.Store.RevokeScopedAccess(f.Customer, "admin", "default", "dev", "operator", Purpose);
        Assert.True(response.StandingMembershipDisabled); Assert.False(response.SupportAccessDisabled);
        Assert.False(f.Store.GetMemberships(f.Customer, "operator").Single().Enabled);
        Assert.Equal("LastAdministratorProtected", Assert.Throws<FoundationStoreException>(() => f.Store.RevokeScopedAccess(f.Customer, "admin", "default", "dev", "admin", Purpose)).Code);
        Assert.Equal("InvalidRoles", Assert.Throws<FoundationStoreException>(() => f.Store.PrepareMembershipChange(f.Customer, "admin", "default", "dev", "target", ["SupportDelegate"], true, Purpose)).Code);
    }

    [Fact]
    public void Support_grant_is_virtual_read_only_exact_scoped_audited_and_not_a_standing_viewer_membership()
    {
        using var f = new StoreFixture(); var artifact = f.Store.StoreArtifact(f.Customer, "requester", "default", "dev", "main.bicep", "// private scoped source"u8.ToArray());
        var grant = GrantSupport(f); var membership = Assert.Single(f.Store.GetMemberships(f.Customer, SupportActor));
        Assert.Equal(["SupportDelegate"], membership.Roles); Assert.Equal(grant.ValidUntilUtc, membership.ValidUntilUtc);
        Assert.Equal(grant.Purpose, membership.Purpose); Assert.Equal(grant.ApprovalId, membership.ApprovalId);
        Assert.Equal(0, f.Scalar($"SELECT count(*) FROM memberships WHERE actor_key='{SupportActor}'"));
        Assert.Equal(artifact.Sha256, f.Store.GetArtifact(f.Customer, SupportActor, artifact.Id).Sha256);
        Assert.Empty(f.Store.GetAdministrativeMemberships(f.Customer, SupportActor));
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.ListAudit(f.Customer, SupportActor)).Code);
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.CreatePlan(f.Customer, SupportActor, f.Input())).Code);
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.SetMembership(f.Customer, SupportActor, "someone", "default", "dev", ["Viewer"])).Code);
        Assert.Contains(f.Store.ListAudit(f.Customer, "auditor"), e => e.Action == "SupportEvidenceAccess" && e.ActorKey == SupportActor && e.CorrelationId == grant.ApprovalId);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    [InlineData(1441)]
    public void Support_access_requires_future_lifetime_no_longer_than_one_day(int minutes)
    {
        using var f = new StoreFixture();
        Assert.Equal("InvalidSupportLifetime", Assert.Throws<FoundationStoreException>(() => f.Store.PrepareSupportAccess(f.Customer, "admin", "default", "dev", SupportActor,
            f.Clock.GetUtcNow().AddMinutes(minutes), Purpose)).Code);
    }

    [Fact]
    public void Support_rejects_missing_purpose_workloads_existing_privileges_and_target_self_review()
    {
        using var f = new StoreFixture();
        Assert.Equal("AdministrativePurposeRequired", Assert.Throws<FoundationStoreException>(() => f.Store.PrepareSupportAccess(f.Customer, "admin", "default", "dev", SupportActor, f.Clock.GetUtcNow().AddHours(1), "")).Code);
        Assert.Equal("HumanPrincipalRequired", Assert.Throws<FoundationStoreException>(() => f.Store.PrepareSupportAccess(f.Customer, "admin", "default", "dev", "workload:tenant:object:client", f.Clock.GetUtcNow().AddHours(1), Purpose)).Code);
        Assert.Equal("SupportMustBeReadOnly", Assert.Throws<FoundationStoreException>(() => f.Store.PrepareSupportAccess(f.Customer, "admin", "default", "dev", "operator", f.Clock.GetUtcNow().AddHours(1), Purpose)).Code);
        var plan = f.Store.PrepareSupportAccess(f.Customer, "admin", "default", "dev", SupportActor, f.Clock.GetUtcNow().AddHours(1), Purpose);
        Assert.Equal("SelfApprovalDenied", Assert.Throws<FoundationStoreException>(() => f.Store.ApproveAdministrativePlan(f.Customer, SupportActor, plan.Id, plan.Digest)).Code);
    }

    [Fact]
    public void Support_expires_on_read_without_background_cleanup_and_cannot_survive_urgent_revocation()
    {
        using var f = new StoreFixture(); var artifact = f.Store.StoreArtifact(f.Customer, "requester", "default", "dev", "main.bicep", "// source"u8.ToArray());
        _ = GrantSupport(f, TimeSpan.FromMinutes(10)); f.Clock.Advance(TimeSpan.FromMinutes(11));
        Assert.Empty(f.Store.GetMemberships(f.Customer, SupportActor));
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.GetArtifact(f.Customer, SupportActor, artifact.Id)).Code);
        var renewed = GrantSupport(f); Assert.True(renewed.Enabled);
        var revoked = f.Store.RevokeScopedAccess(f.Customer, "admin", "default", "dev", SupportActor, Purpose);
        Assert.True(revoked.SupportAccessDisabled); Assert.Empty(f.Store.GetMemberships(f.Customer, SupportActor));
        Assert.False(f.Store.SupportAccessStatus(f.Customer, "admin", "default", "dev", SupportActor)!.Enabled);
    }

    [Fact]
    public void Support_approval_role_revocation_or_policy_change_never_resumes_old_access()
    {
        using var f = new StoreFixture(); _ = GrantSupport(f);
        f.Store.SetMembership(f.Customer, "admin", "approver", "default", "dev", ["Approver"]);
        Assert.Empty(f.Store.GetMemberships(f.Customer, SupportActor));
        _ = GrantSupport(f); f.Store.SetCustomerEnabled(f.Customer, "admin", false); f.Store.SetCustomerEnabled(f.Customer, "admin", true);
        Assert.Empty(f.Store.GetMemberships(f.Customer, SupportActor));
        Assert.False(f.Store.SupportAccessStatus(f.Customer, "admin", "default", "dev", SupportActor)!.Enabled);
    }

    [Fact]
    public void Editing_a_revoked_lease_version_cannot_reactivate_an_old_approval()
    {
        using var f = new StoreFixture(); _ = GrantSupport(f);
        f.Store.SetMembership(f.Customer, "admin", SupportActor, "default", "dev", [], false);
        f.Execute("UPDATE support_grants SET actor_version=(SELECT version FROM actors WHERE actor_key=support_grants.actor_key)");
        Assert.DoesNotContain(f.Store.GetMemberships(f.Customer, SupportActor), m => m.Enabled);
        Assert.False(f.Store.SupportAccessStatus(f.Customer, "admin", "default", "dev", SupportActor)!.Enabled);
    }

    [Fact]
    public void Support_metadata_tampering_and_cross_customer_identifiers_are_denied()
    {
        using var f = new StoreFixture(); _ = GrantSupport(f);
        f.Execute("UPDATE support_grants SET purpose='rewritten approved purpose'");
        Assert.Empty(f.Store.GetMemberships(f.Customer, SupportActor));
        Assert.Equal("CustomerScopeDenied", Assert.Throws<FoundationStoreException>(() => f.Store.GetMemberships("other-customer", SupportActor)).Code);
        using var other = new StoreFixture("other-customer", "other-cell");
        Assert.Empty(other.Store.GetMemberships(other.Customer, SupportActor));
    }

    [Fact]
    public void V2_upgrade_preserves_the_frozen_v2_checksum_and_adds_support_schema_atomically()
    {
        using var f = new StoreFixture(); var v2 = f.Store.SchemaStatus().AppliedMigrations[1];
        DowngradeToV2(f); var upgraded = f.Reopen();
        Assert.Equal(FoundationStore.CurrentSchemaVersion, upgraded.SchemaStatus().Version); Assert.Equal(v2, upgraded.SchemaStatus().AppliedMigrations[1]);
        Assert.Contains(upgraded.ListAudit(f.Customer, "auditor"), e => e.Reason == "SchemaV2ToV3");
        Assert.True(upgraded.VerifyAuditIntegrity(f.Customer, "auditor"));
    }

    [Fact]
    public void V3_migration_conflict_rolls_back_to_v2_without_partial_history_or_audit()
    {
        using var f = new StoreFixture(); DowngradeToV2(f);
        f.Execute("CREATE TABLE support_grants(conflicting TEXT)"); var audit = f.Scalar("SELECT count(*) FROM audit");
        Assert.Throws<SqliteException>(() => f.Reopen());
        Assert.Equal(2, f.Scalar("SELECT schema_version FROM cell")); Assert.Equal(2, f.Scalar("SELECT count(*) FROM schema_migrations"));
        Assert.Equal(audit, f.Scalar("SELECT count(*) FROM audit"));
    }

    internal static FoundationSupportAccess GrantSupport(StoreFixture f, TimeSpan? duration = null, string target = SupportActor)
    {
        var plan = f.Store.PrepareSupportAccess(f.Customer, "admin", "default", "dev", target,
            f.Clock.GetUtcNow().Add(duration ?? TimeSpan.FromHours(1)), Purpose);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", plan.Id, plan.Digest);
        return f.Store.ExecuteSupportAccess(f.Customer, "admin", plan.Id, plan.Digest);
    }

    static void DowngradeToV2(StoreFixture f)
    {
        FoundationTestSchemaDowngrades.DropCredentialTablesAfter(f, 2);
        f.Execute("""
        DROP TRIGGER cell_instance_insert_guard; DROP TRIGGER cell_instance_update_guard; ALTER TABLE cell DROP COLUMN database_instance_id;
        DROP TABLE worker_heartbeats; DROP TABLE support_grants; DROP TRIGGER schema_migrations_no_delete; DELETE FROM schema_migrations WHERE version>=3;
        CREATE TRIGGER schema_migrations_no_delete BEFORE DELETE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END;
        UPDATE cell SET schema_version=2;
        """);
    }
}
