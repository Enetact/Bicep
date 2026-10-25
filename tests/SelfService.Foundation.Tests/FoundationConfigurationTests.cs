using System.Text.Json;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using Xunit;
using static SelfService.Foundation.Tests.EntitlementPolicyTests;

namespace SelfService.Foundation.Tests;

public sealed class FoundationConfigurationTests
{
    const string Runtime = "1.0.0";
    static FoundationFeatureConfiguration Preview(string[]? features = null, int rollout = 100) =>
        new("LocalPreview", features ?? FoundationFeatures.Available.ToArray(), rollout, "0.0.0", "2.0.0");
    static FoundationFeatureConfiguration Licensed(EntitlementFixture signer, EntitlementDocument? document = null) =>
        new("Licensed", FoundationFeatures.Available.ToArray(), 100, "0.0.0", "2.0.0", signer.Sign(document));
    static FoundationAdministrativePlan Prepare(StoreFixture f, FoundationFeatureConfiguration config, long expected,
        EntitlementVerifier verifier) => f.Store.PrepareConfigurationChange(f.Customer, "admin", "default", "dev", config, expected, "Fixture reviewed configuration", verifier, Runtime);
    static FoundationConfigurationSnapshot Apply(StoreFixture f, FoundationFeatureConfiguration config, long expected, EntitlementVerifier verifier)
    {
        var plan = Prepare(f, config, expected, verifier);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", plan.Id, plan.Digest);
        return f.Store.ExecuteConfigurationChange(f.Customer, "admin", plan.Id, plan.Digest, verifier, Runtime);
    }
    static FoundationFeatureDecision Feature(StoreFixture f, EntitlementVerifier verifier, string feature = FoundationFeatures.ParameterizedBicep, string actor = "requester") =>
        f.Store.RequireFeature(f.Customer, actor, "default", "dev", feature, verifier, Runtime);

    [Fact]
    public void Default_explicit_local_preview_preserves_existing_mock_capabilities_without_external_authority()
    {
        using var f = new StoreFixture(); var verifier = new EntitlementVerifier([], f.Clock);
        var current = f.Store.ReadConfiguration(f.Customer, "admin", "default", "dev");
        Assert.Equal(0, current.Generation); Assert.Equal("LocalPreview", current.Configuration.Mode);
        foreach (var feature in FoundationFeatures.Available)
        {
            var decision = Feature(f, verifier, feature);
            Assert.True(decision.Enabled); Assert.False(decision.ExternalExecutionAuthorized); Assert.False(decision.IdentityGrantAuthorized);
        }
        Assert.Equal("UnknownFeature", Error(() => Feature(f, verifier, "azure.deploy")));
        Assert.Throws<ArgumentNullException>(() => Feature(f, null!));
    }

    [Fact]
    public void Trusted_signed_mode_requirement_disables_default_preview_without_stranding_role_scoped_recovery()
    {
        using var f = new StoreFixture(); var required = new EntitlementVerifier([], f.Clock, requireSignedMode: true);
        Assert.True(required.RequireSignedMode);
        foreach (var feature in FoundationFeatures.Available)
        {
            var decision = f.Store.EvaluateFeature(f.Customer, "requester", "default", "dev", feature, required, Runtime);
            Assert.False(decision.Enabled); Assert.Equal("EntitlementSignedModeRequired", decision.Reason);
            Assert.False(decision.ExternalExecutionAuthorized); Assert.False(decision.IdentityGrantAuthorized);
        }
        Assert.Equal("EntitlementSignedModeRequired", Error(() => Prepare(f, Preview(), 0, required)));
        Assert.Equal(0, f.Store.ReadConfiguration(f.Customer, "admin", "default", "dev").Generation);
        foreach (var safe in FoundationFeatures.AlwaysAvailable)
            Assert.True(Feature(f, required, safe, safe == FoundationFeatures.Administration ? "admin" : "auditor").Enabled);
        Assert.Equal("RoleDenied", Error(() => Feature(f, required, FoundationFeatures.Administration)));
        Assert.Equal("RoleDenied", Error(() => Feature(f, required, FoundationFeatures.ExportRead, "viewer")));
        Assert.Equal("RoleDenied", Error(() => f.Store.PrepareConfigurationChange(f.Customer, "requester", "default", "dev",
            Preview(), 0, "Feature policy grants no administrator role", required, Runtime)));
    }

    [Fact]
    public void Mandatory_signed_mode_accepts_valid_license_and_prevents_preview_switch_or_baseline_rollback_after_expiry()
    {
        using var f = new StoreFixture(); using var signer = new EntitlementFixture(f.Clock);
        var required = new EntitlementVerifier([new("issuer", "key-1", signer.Key.ExportSubjectPublicKeyInfoPem())], f.Clock, requireSignedMode: true);
        Apply(f, Licensed(signer, signer.Document() with { ExpiresUtc = f.Clock.GetUtcNow().AddMinutes(1) }), 0, required);
        Assert.True(Feature(f, required).Enabled);
        Assert.Equal("EntitlementSignedModeRequired", Error(() => Prepare(f, Preview(), 1, required)));
        Assert.Equal("EntitlementSignedModeRequired", Error(() => f.Store.PrepareConfigurationRollback(f.Customer, "admin", "default", "dev", 0, 1,
            "Attempt unsigned baseline rollback", required, Runtime)));
        f.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("EntitlementExpired", Error(() => Feature(f, required)));
        Assert.Equal("EntitlementSignedModeRequired", Error(() => Prepare(f, Preview(), 1, required)));
        Assert.Equal("EntitlementSignedModeRequired", Error(() => f.Store.PrepareConfigurationRollback(f.Customer, "admin", "default", "dev", 0, 1,
            "Expiry cannot restore unsigned preview", required, Runtime)));
        Assert.True(Feature(f, required, FoundationFeatures.Administration, "admin").Enabled);
        Assert.True(Feature(f, required, FoundationFeatures.AuditRead, "auditor").Enabled);
        Assert.Equal(1, f.Store.ReadConfiguration(f.Customer, "auditor", "default", "dev").Generation);
    }

    [Fact]
    public void Activating_host_signed_requirement_invalidates_an_already_approved_preview_change_at_execution()
    {
        using var f = new StoreFixture(); var previewPolicy = new EntitlementVerifier([], f.Clock);
        var required = new EntitlementVerifier([], f.Clock, requireSignedMode: true);
        var plan = Prepare(f, Preview(), 0, previewPolicy);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", plan.Id, plan.Digest);
        Assert.Equal("EntitlementSignedModeRequired", Error(() => f.Store.ExecuteConfigurationChange(f.Customer, "admin", plan.Id, plan.Digest, required, Runtime)));
        Assert.Equal(0, f.Store.ReadConfiguration(f.Customer, "admin", "default", "dev").Generation);
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM audit WHERE action='ConfigurationChanged'"));
        // Legacy trusted library preparation cannot bypass the mandatory disconnected admission path.
        var job = f.Queue(); var claim = f.Store.ClaimNext(f.Customer, "required-mode-worker", TimeSpan.FromMinutes(1))!;
        Assert.Equal("DispatchFeatureUnavailable", Error(() => f.Store.StartMockDispatch(f.Customer, job.Id, "required-mode-worker", claim.Fence, required, Runtime)));
        Assert.Equal("Blocked", f.Store.GetJob(f.Customer, "operator", job.Id).State);
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM audit WHERE action='DispatchIntent'"));
    }

    [Fact]
    public void Changes_require_exact_separate_review_and_persist_a_generation_across_restart()
    {
        using var f = new StoreFixture(); var verifier = new EntitlementVerifier([], f.Clock);
        var plan = Prepare(f, Preview([FoundationFeatures.SourceCatalog]), 0, verifier);
        Assert.Equal("config.update", plan.Kind);
        Assert.Equal("AdministrativeApprovalRequired", Error(() => f.Store.ExecuteConfigurationChange(f.Customer, "admin", plan.Id, plan.Digest, verifier, Runtime)));
        Assert.Equal("SelfApprovalDenied", Error(() => f.Store.ApproveAdministrativePlan(f.Customer, "admin", plan.Id, plan.Digest)));
        Assert.Equal("DigestMismatch", Error(() => f.Store.ApproveAdministrativePlan(f.Customer, "approver", plan.Id, new string('0', 64))));
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", plan.Id, plan.Digest);
        var result = f.Store.ExecuteConfigurationChange(f.Customer, "admin", plan.Id, plan.Digest, verifier, Runtime);
        Assert.Equal(1, result.Generation); Assert.Equal(plan.Digest, result.Digest);
        Assert.Equal(result.Digest, f.Reopen().ReadConfiguration(f.Customer, "auditor", "default", "dev").Digest);
        Assert.Equal("FeatureDisabled", Error(() => Feature(f, verifier)));
        Assert.True(Feature(f, verifier, FoundationFeatures.SourceCatalog).Enabled);
        Assert.Equal("AdministrativeApprovalRequired", Error(() => f.Store.ExecuteConfigurationChange(f.Customer, "admin", plan.Id, plan.Digest, verifier, Runtime)));
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM audit WHERE action='ConfigurationChanged'"));
    }

    [Fact]
    public void Cross_customer_scope_and_unauthorized_actors_cannot_inspect_or_change_configuration()
    {
        using var f = new StoreFixture(); var verifier = new EntitlementVerifier([], f.Clock);
        Assert.Equal("CustomerScopeDenied", Error(() => f.Store.ReadConfiguration("other", "admin", "default", "dev")));
        Assert.Equal("RoleDenied", Error(() => f.Store.ReadConfiguration(f.Customer, "admin", "other", "dev")));
        Assert.Equal("RoleDenied", Error(() => f.Store.ReadConfiguration(f.Customer, "viewer", "default", "dev")));
        Assert.Equal("RoleDenied", Error(() => f.Store.PrepareConfigurationChange(f.Customer, "requester", "default", "dev", Preview(), 0, "Not administrator", verifier, Runtime)));
        Assert.Equal("RoleDenied", Error(() => f.Store.RequireFeature(f.Customer, "requester", "other", "dev", FoundationFeatures.SourceCatalog, verifier, Runtime)));
        Assert.Equal("RoleDenied", Error(() => Feature(f, verifier, FoundationFeatures.Administration)));
    }

    [Fact]
    public void Concurrent_prepared_updates_and_preexisting_jobs_are_fenced_by_successful_execution()
    {
        using var f = new StoreFixture(); var verifier = new EntitlementVerifier([], f.Clock);
        var queued = f.Queue(); var first = Prepare(f, Preview(), 0, verifier); var second = Prepare(f, Preview(rollout: 0), 0, verifier);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", first.Id, first.Digest);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", second.Id, second.Digest);
        f.Store.ExecuteConfigurationChange(f.Customer, "admin", first.Id, first.Digest, verifier, Runtime);
        Assert.Equal("PolicyChanged", Error(() => f.Store.ExecuteConfigurationChange(f.Customer, "admin", second.Id, second.Digest, verifier, Runtime)));
        Assert.Equal("ConfigurationGenerationChanged", Error(() => Prepare(f, Preview(), 0, verifier)));
        var claim = f.Store.ClaimNext(f.Customer, "worker", TimeSpan.FromMinutes(1))!;
        Assert.Equal("PolicyChanged", Error(() => f.Store.StartDispatch(f.Customer, queued.Id, "worker", claim.Fence)));
        Assert.Equal(1, f.Store.ReadConfiguration(f.Customer, "admin", "default", "dev").Generation);
    }

    [Fact]
    public void Revoked_approver_or_expired_plan_cannot_execute_a_reviewed_change()
    {
        using var f = new StoreFixture(); var verifier = new EntitlementVerifier([], f.Clock);
        var first = Prepare(f, Preview(), 0, verifier);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", first.Id, first.Digest);
        f.Store.SetMembership(f.Customer, "admin", "approver", "default", "dev", ["Viewer"]);
        Assert.Equal("RoleDenied", Error(() => f.Store.ExecuteConfigurationChange(f.Customer, "admin", first.Id, first.Digest, verifier, Runtime)));
        var second = Prepare(f, Preview(), 0, verifier); f.Clock.Advance(TimeSpan.FromMinutes(16));
        Assert.Equal("AdministrativePlanExpired", Error(() => f.Store.ApproveAdministrativePlan(f.Customer, "approver", second.Id, second.Digest)));
    }

    [Fact]
    public void Rollback_requires_new_review_and_advances_generation_instead_of_restoring_old_fence()
    {
        using var f = new StoreFixture(); var verifier = new EntitlementVerifier([], f.Clock);
        var one = Apply(f, Preview([FoundationFeatures.SourceCatalog]), 0, verifier);
        Apply(f, Preview(rollout: 0), 1, verifier);
        var rollback = f.Store.PrepareConfigurationRollback(f.Customer, "admin", "default", "dev", 1, 2, "Restore prior reviewed feature set", verifier, Runtime);
        Assert.Equal("AdministrativeApprovalRequired", Error(() => f.Store.ExecuteConfigurationChange(f.Customer, "admin", rollback.Id, rollback.Digest, verifier, Runtime)));
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", rollback.Id, rollback.Digest);
        var three = f.Store.ExecuteConfigurationChange(f.Customer, "admin", rollback.Id, rollback.Digest, verifier, Runtime);
        Assert.Equal(3, three.Generation); Assert.NotEqual(one.Digest, three.Digest);
        Assert.True(Feature(f, verifier, FoundationFeatures.SourceCatalog).Enabled);
        Assert.Equal("FeatureDisabled", Error(() => Feature(f, verifier)));
        Assert.Equal(3, f.Scalar("SELECT count(*) FROM administrative_plans WHERE kind='config.update' AND state='Executed'"));
    }

    [Fact]
    public void Real_signed_license_can_expire_without_stranding_safe_admin_audit_export_or_history()
    {
        using var f = new StoreFixture(); using var signer = new EntitlementFixture(f.Clock);
        Apply(f, Licensed(signer, signer.Document() with { ExpiresUtc = f.Clock.GetUtcNow().AddMinutes(1) }), 0, signer.Verifier);
        Assert.True(Feature(f, signer.Verifier).Enabled);
        f.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("EntitlementExpired", Error(() => Feature(f, signer.Verifier)));
        Assert.Equal(1, f.Store.ReadConfiguration(f.Customer, "admin", "default", "dev").Generation);
        foreach (var safe in FoundationFeatures.AlwaysAvailable)
        {
            var actor = safe == FoundationFeatures.Administration ? "admin" : "auditor";
            var result = f.Store.RequireFeature(f.Customer, actor, "default", "dev", safe, new EntitlementVerifier([]), "invalid runtime");
            Assert.True(result.Enabled); Assert.Equal("AlwaysAvailable", result.Reason); Assert.False(result.ExternalExecutionAuthorized);
        }
        Assert.True(f.Scalar("SELECT count(*) FROM audit") > 0);
    }

    [Fact]
    public void Safe_access_survives_invalid_configuration_and_suspension_but_keeps_roles()
    {
        using var f = new StoreFixture(); var verifier = new EntitlementVerifier([], f.Clock);
        Apply(f, Preview(), 0, verifier);
        f.Execute("UPDATE administrative_plans SET payload='{}' WHERE kind='config.update'; UPDATE cell SET enabled=0 WHERE singleton=1");
        Assert.True(f.Store.RequireFeature(f.Customer, "auditor", "default", "dev", FoundationFeatures.AuditRead, verifier, "bad").Enabled);
        Assert.True(f.Store.RequireFeature(f.Customer, "admin", "default", "dev", FoundationFeatures.Administration, verifier, "bad").Enabled);
        Assert.Equal("RoleDenied", Error(() => f.Store.RequireFeature(f.Customer, "viewer", "default", "dev", FoundationFeatures.AuditRead, verifier, Runtime)));
        Assert.Equal("CustomerSuspended", Error(() => Feature(f, verifier)));
    }

    [Fact]
    public void Entitlement_is_rechecked_at_execution_and_feature_use_not_only_during_preparation()
    {
        using var f = new StoreFixture(); using var signer = new EntitlementFixture(f.Clock);
        var plan = Prepare(f, Licensed(signer, signer.Document() with { ExpiresUtc = f.Clock.GetUtcNow().AddMinutes(1) }), 0, signer.Verifier);
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", plan.Id, plan.Digest); f.Clock.Advance(TimeSpan.FromMinutes(2));
        Assert.Equal("EntitlementExpired", Error(() => f.Store.ExecuteConfigurationChange(f.Customer, "admin", plan.Id, plan.Digest, signer.Verifier, Runtime)));
        Assert.Equal(0, f.Store.ReadConfiguration(f.Customer, "admin", "default", "dev").Generation);
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM audit WHERE action='ConfigurationChanged'"));
        Assert.Equal("EntitlementIssuerNotTrusted", Error(() => Prepare(f, Licensed(signer), 0, new EntitlementVerifier([], f.Clock))));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Worker_rechecks_license_expiry_without_policy_change_before_any_mock_provider_post(bool expire)
    {
        using var f = new StoreFixture("10000000-0000-0000-0000-000000000001", "20000000-0000-0000-0000-000000000001");
        using var signer = new EntitlementFixture(f.Clock);
        var signed = signer.Document() with { CustomerId = f.Customer, CellId = f.Cell, ExpiresUtc = f.Clock.GetUtcNow().AddMinutes(1) };
        Apply(f, Licensed(signer, signed), 0, signer.Verifier);
        var command = new ConnectionCommand(PipelineRequestFactory.GitHub, "customer-a", null, "platform-infra", "platform.yml",
            ["refs/heads/main"], ["dev"], ["location", .. PipelineRequestFactory.ReservedInputs], "auth-profile/customer-a", true);
        var connection = f.Store.PutConnection(f.Customer, "admin", "pipeline", command.Provider,
            JsonSerializer.Serialize(command, FoundationApplication.Json));
        var request = new PipelineDispatchRequest(f.Customer, f.Cell, connection.Id, connection.Generation, Guid.NewGuid().ToString("D"),
            "refs/heads/main", "dev", new string('a', 40), new() { ["location"] = "eastus2" });
        var plan = f.Store.CreatePlan(f.Customer, "requester", new("default", "dev", connection.Id, "pipeline.dispatch",
            JsonSerializer.Serialize(request, FoundationApplication.Json), true, f.Clock.GetUtcNow().AddMinutes(20), request.SourceRevision));
        f.Store.ApprovePlan(f.Customer, "approver", plan.Id, plan.Digest);
        var job = f.Store.EnqueuePlan(f.Customer, "operator", plan.Id, plan.Digest, "expired-license-worker");
        var policyBefore = f.Scalar("SELECT policy_version FROM cell WHERE singleton=1");
        if (expire) f.Clock.Advance(TimeSpan.FromMinutes(2));
        var transport = new EntitlementMockTransport();
        var runner = new FoundationJobRunner(f.Store, new PipelineDispatcher(transport, f.Clock), signer.Verifier);
        Assert.True(await runner.RunOnceAsync("entitlement-worker"));
        var terminal = f.Store.GetJob(f.Customer, "operator", job.Id);
        Assert.Equal(expire ? "Blocked" : "Succeeded", terminal.State);
        Assert.Equal(expire ? 0 : 1, transport.Posts);
        if (expire) Assert.Equal("DispatchFeatureUnavailable", terminal.Reason);
        if (expire) Assert.Equal(0, f.Scalar("SELECT count(*) FROM audit WHERE action='DispatchIntent'"));
        Assert.Equal(policyBefore, f.Scalar("SELECT policy_version FROM cell WHERE singleton=1"));
        Assert.False(await runner.RunOnceAsync("entitlement-worker"));
    }

    sealed class EntitlementMockTransport : IPipelineTransport, IPipelineTransportCharacteristics
    {
        public bool IsMock => true;
        public int Posts { get; private set; }
        public Task<PipelineWireResponse> SendAsync(PipelineWireRequest request, CancellationToken cancellationToken)
        {
            Posts++;
            return Task.FromResult(new PipelineWireResponse(200,
                "{\"workflow_run_id\":9011203,\"run_url\":\"https://api.github.com/repos/customer-a/platform-infra/actions/runs/9011203\",\"html_url\":\"https://github.com/customer-a/platform-infra/actions/runs/9011203\"}"));
        }
    }

    [Fact]
    public void Sequence_high_water_survives_local_preview_and_rejects_reissue_conflicts_or_rollback()
    {
        using var f = new StoreFixture(); using var signer = new EntitlementFixture(f.Clock);
        var original = signer.Document() with { Sequence = 2 };
        Apply(f, Licensed(signer, original), 0, signer.Verifier);
        // Identical document may be reused for a rollout change, including a new randomized PSS signature.
        Apply(f, Licensed(signer, original) with { RolloutPercent = 50 }, 1, signer.Verifier);
        Apply(f, Preview(), 2, signer.Verifier);
        Assert.Equal("EntitlementReplayDenied", Error(() => Prepare(f, Licensed(signer, original with { Sequence = 1 }), 3, signer.Verifier)));
        Assert.Equal("EntitlementSequenceConflict", Error(() => Prepare(f, Licensed(signer, original with { Edition = "changed" }), 3, signer.Verifier)));
        Apply(f, Licensed(signer, original with { Sequence = 3 }), 3, signer.Verifier);
        Assert.Equal("EntitlementReplayDenied", Error(() => f.Store.PrepareConfigurationRollback(f.Customer, "admin", "default", "dev", 1, 4,
            "Attempt stale entitlement rollback", signer.Verifier, Runtime)));
    }

    [Fact]
    public void Signed_customer_cell_binding_and_sequence_high_water_cannot_be_bypassed_with_another_scope()
    {
        using var f = new StoreFixture(); using var other = new StoreFixture("customer-b", "cell-b");
        using var signer = new EntitlementFixture(f.Clock);
        Assert.Equal("EntitlementBindingMismatch", Error(() => Prepare(other, Licensed(signer), 0, signer.Verifier)));
        var entitlement = signer.Document() with { Sequence = 2 };
        Apply(f, Licensed(signer, entitlement), 0, signer.Verifier);
        var onboard = f.Store.PrepareScopeOnboarding(f.Customer, "admin", "project-b", "prod", "admin-b", "Second customer project scope", "reviewer-b");
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", onboard.Id, onboard.Digest);
        f.Store.ExecuteScopeOnboarding(f.Customer, "admin", onboard.Id, onboard.Digest);
        Assert.Equal(0, f.Store.ReadConfiguration(f.Customer, "admin-b", "project-b", "prod").Generation);
        Assert.Equal("EntitlementReplayDenied", Error(() => f.Store.PrepareConfigurationChange(f.Customer, "admin-b", "project-b", "prod",
            Licensed(signer, entitlement with { Sequence = 1 }), 0, "Scope cannot reset entitlement sequence", signer.Verifier, Runtime)));
        var plan = f.Store.PrepareConfigurationChange(f.Customer, "admin-b", "project-b", "prod", Licensed(signer, entitlement),
            0, "Same signed entitlement in separate scope", signer.Verifier, Runtime);
        f.Store.ApproveAdministrativePlan(f.Customer, "reviewer-b", plan.Id, plan.Digest);
        f.Store.ExecuteConfigurationChange(f.Customer, "admin-b", plan.Id, plan.Digest, signer.Verifier, Runtime);
        Assert.Equal(1, f.Store.ReadConfiguration(f.Customer, "admin", "default", "dev").Generation);
        Assert.Equal(1, f.Store.ReadConfiguration(f.Customer, "admin-b", "project-b", "prod").Generation);
    }

    [Theory]
    [InlineData(0, false)]
    [InlineData(100, true)]
    public void Rollout_is_persisted_and_consistent_across_requester_and_operator(int percent, bool enabled)
    {
        using var f = new StoreFixture(); var verifier = new EntitlementVerifier([], f.Clock);
        Apply(f, Preview(rollout: percent), 0, verifier);
        foreach (var actor in new[] { "requester", "operator" })
            Assert.Equal(enabled, f.Store.EvaluateFeature(f.Customer, actor, "default", "dev", FoundationFeatures.ParameterizedBicep, verifier, Runtime).Enabled);
        Assert.Equal(percent, f.Reopen().ReadConfiguration(f.Customer, "admin", "default", "dev").Configuration.RolloutPercent);
    }

    [Fact]
    public void Rollout_and_runtime_window_cannot_override_signed_feature_grants_or_server_policy()
    {
        using var f = new StoreFixture(); using var signer = new EntitlementFixture(f.Clock);
        Assert.Equal("InvalidFeatureConfiguration", Error(() => Prepare(f, Preview(["azure.deploy"]), 0, signer.Verifier)));
        Assert.Equal("InvalidFeatureConfiguration", Error(() => Prepare(f, Preview([FoundationFeatures.SourceCatalog, FoundationFeatures.SourceCatalog]), 0, signer.Verifier)));
        Assert.Equal("InvalidFeatureConfiguration", Error(() => Prepare(f, Preview(rollout: 101), 0, signer.Verifier)));
        Assert.Equal("InvalidRuntimeWindow", Error(() => Prepare(f, Preview() with { MinimumRuntimeVersion = "2.0.0", MaximumRuntimeVersion = "1.0.0" }, 0, signer.Verifier)));
        Assert.Equal("RuntimeIncompatible", Error(() => Prepare(f, Preview() with { MinimumRuntimeVersion = "1.1.0" }, 0, signer.Verifier)));
        Assert.Equal("InvalidRuntimeVersion", Error(() => Prepare(f, Preview() with { MinimumRuntimeVersion = "1.0.0-preview" }, 0, signer.Verifier)));
        Assert.Equal("EntitlementFeatureDenied", Error(() => Prepare(f, Licensed(signer, signer.Document() with { Features = [FoundationFeatures.SourceCatalog] }), 0, signer.Verifier)));
        Apply(f, Preview(), 0, signer.Verifier);
        Assert.False(f.Store.EvaluateFeature(f.Customer, "requester", "default", "dev", FoundationFeatures.SourceCatalog, signer.Verifier, "3.0.0").Enabled);
    }

    [Theory]
    [InlineData("UPDATE administrative_plans SET payload='{}' WHERE kind='config.update'", "DigestMismatch")]
    [InlineData("DROP TRIGGER audit_no_delete; DELETE FROM audit WHERE action='ConfigurationChanged'", "ConfigurationHistoryIntegrityFailed")]
    public void Historical_digest_and_execution_receipt_are_revalidated_without_erasing_audit(string sql, string code)
    {
        using var f = new StoreFixture(); var verifier = new EntitlementVerifier([], f.Clock);
        Apply(f, Preview(), 0, verifier); f.Execute(sql);
        Assert.Equal(code, Error(() => f.Reopen().ReadConfiguration(f.Customer, "auditor", "default", "dev")));
        Assert.True(f.Scalar("SELECT count(*) FROM audit") > 0);
    }
}
