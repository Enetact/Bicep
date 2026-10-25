using System.Reflection;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationCredentialMaintenanceStoreTests
{
    [Theory]
    [InlineData(CredentialMaintenanceOperation.Revoke)]
    [InlineData(CredentialMaintenanceOperation.Rewrap)]
    public async Task Exact_independent_approval_and_one_use_admission_record_only_public_result(CredentialMaintenanceOperation operation)
    {
        using var f = new LedgerFixture(); var target = await f.Target(operation); var plan = await f.Prepare(operation, target);
        Assert.Equal("Prepared", plan.State); Assert.Null(plan.Execution);
        await f.Approve(plan); var result = await f.Executor().ExecuteAsync(await f.Context("operator", FoundationCapabilities.CredentialMaintenanceExecute), plan.Id, plan.Digest);
        Assert.Equal("Succeeded", result.Execution!.State); Assert.Equal(2, result.Execution.Result!.AfterGeneration);
        Assert.Equal(plan.TargetSha256, result.Execution.Result.TargetSha256); Assert.NotNull(result.Execution.ResultSha256);
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM credential_maintenance_executions"));
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM audit WHERE action='CredentialMaintenanceAdmitted'"));
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM audit WHERE action='CredentialMaintenanceCompleted'"));
        var text = JsonSerializer.Serialize(result); Assert.DoesNotContain(CredentialFixture.Token, text); Assert.DoesNotContain(f.Cell.Root, text);
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Executor().ExecuteAsync(f.ExecuteContext!, plan.Id, plan.Digest));
        Assert.Throws<SqliteException>(() => f.Sql("UPDATE credential_maintenance_intents SET target_sha256='modified'"));
        Assert.Throws<SqliteException>(() => f.Sql("UPDATE credential_maintenance_approvals SET digest='modified'"));
        Assert.Throws<SqliteException>(() => f.Sql("UPDATE credential_maintenance_executions SET result_sha256='modified'"));
    }

    [WindowsCredentialFact]
    public async Task Product_capability_expiry_and_fresh_role_version_are_rechecked_before_prepare()
    {
        using var f = new LedgerFixture(); var target = await f.Target(); var read = await f.Context("operator", FoundationCapabilities.CredentialMaintenanceRead);
        Assert.Equal("MaintenanceContextDenied", Assert.Throws<FoundationStoreException>(() => f.Store.PrepareCredentialMaintenance(read, CredentialMaintenanceOperation.Revoke,
            target, "Synthetic exact local maintenance", f.Cell.Clock.Now.AddMinutes(10))).Code);
        var context = await f.Context("operator", FoundationCapabilities.CredentialMaintenancePrepare);
        f.Store.SetMembership(f.Cell.Scope.CustomerId, "admin", LedgerFixture.Operator, "default", "dev", ["Operator", "Approver"]);
        Assert.Equal("MaintenanceContextGrantChanged", Assert.Throws<FoundationStoreException>(() => f.Store.PrepareCredentialMaintenance(context,
            CredentialMaintenanceOperation.Revoke, target, "Synthetic exact local maintenance", f.Cell.Clock.Now.AddMinutes(10))).Code);
        context = await f.Context("operator", FoundationCapabilities.CredentialMaintenancePrepare); f.Cell.Clock.Now = f.Cell.Clock.Now.AddHours(3);
        Assert.Equal("MaintenanceContextExpired", Assert.Throws<FoundationStoreException>(() => f.Store.PrepareCredentialMaintenance(context,
            CredentialMaintenanceOperation.Revoke, target, "Synthetic exact local maintenance", f.Cell.Clock.Now.AddMinutes(10))).Code);
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM credential_maintenance_intents"));
    }

    [Theory]
    [InlineData("SupportDelegate")]
    [InlineData("UnknownLegacyRole")]
    public async Task Mixed_support_or_unknown_roles_cannot_be_hidden_by_maintenance_projection_or_a_cached_context(string disqualifyingRole)
    {
        using var f = new LedgerFixture(); var target = await f.Target(); var context = await f.Context("operator", FoundationCapabilities.CredentialMaintenancePrepare);
        f.Sql("UPDATE memberships SET roles='[\"Operator\",\"" + disqualifyingRole + "\"]' WHERE actor_key='" + LedgerFixture.Operator + "'");
        Assert.Equal("MaintenanceMembershipInvalid", (await Assert.ThrowsAsync<FoundationStoreException>(() =>
            f.Context("operator", FoundationCapabilities.CredentialMaintenancePrepare))).Code);
        Assert.Equal("MaintenanceMembershipInvalid", Assert.Throws<FoundationStoreException>(() => f.Store.PrepareCredentialMaintenance(context,
            CredentialMaintenanceOperation.Revoke, target, "Synthetic exact local maintenance", f.Cell.Clock.Now.AddMinutes(10))).Code);
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM credential_maintenance_intents"));
    }

    [WindowsCredentialFact]
    public async Task Support_only_sibling_scope_does_not_remove_a_standing_operator_scope()
    {
        using var f = new LedgerFixture();
        f.Sql("INSERT INTO scopes SELECT customer_id,'sibling','dev',created FROM scopes WHERE project_id='default' AND environment_id='dev'; " +
            "INSERT INTO memberships SELECT customer_id,actor_key,'sibling','dev','[\"SupportDelegate\"]',1 FROM memberships WHERE actor_key='" + LedgerFixture.Operator + "'");
        var memberships = f.Store.GetCredentialMaintenanceMemberships(f.Cell.Scope.CustomerId, LedgerFixture.Operator);
        Assert.Equal("default", Assert.Single(memberships).ProjectId);
        var context = await f.Context("operator", FoundationCapabilities.CredentialMaintenancePrepare);
        Assert.Equal("default", context.ProjectId);
        Assert.Equal("Prepared", (await f.Prepare()).State);
    }

    [WindowsCredentialFact]
    public async Task Nonrepresentable_expiry_metadata_cannot_enter_an_intent()
    {
        using var f = new LedgerFixture(); var target = (await f.Target()) with { ExpiresUnixMilliseconds = long.MaxValue };
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Prepare(target: target)); Assert.Equal(0, f.Scalar("SELECT count(*) FROM credential_maintenance_intents"));
    }

    [WindowsCredentialFact]
    public async Task Approval_is_distinct_from_both_requester_and_executor()
    {
        using var f = new LedgerFixture(); var plan = await f.Prepare();
        var self = await f.Context("operator", FoundationCapabilities.CredentialMaintenanceApprove);
        Assert.Equal("SelfApprovalDenied", Assert.Throws<FoundationStoreException>(() => f.Store.ApproveCredentialMaintenance(
            self, plan.Id, plan.Digest)).Code);
        await f.Approve(plan);
        f.Store.SetMembership(f.Cell.Scope.CustomerId, "admin", LedgerFixture.Approver, "default", "dev", ["Approver", "Operator"]);
        var conflicting = await f.Context("approver", FoundationCapabilities.CredentialMaintenanceExecute);
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Executor().ExecuteAsync(conflicting, plan.Id, plan.Digest));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM credential_maintenance_executions"));
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("requester")]
    [InlineData("approver")]
    [InlineData("connection")]
    [InlineData("instance")]
    [InlineData("quarantined")]
    [InlineData("profile")]
    public async Task Stale_authority_and_target_snapshots_cannot_create_an_admission(string changed)
    {
        using var f = new LedgerFixture(); var plan = await f.Prepare(); await f.Approve(plan);
        switch (changed)
        {
            case "policy": f.Sql("UPDATE cell SET policy_version=policy_version+1"); break;
            case "requester": f.Store.SetMembership(f.Cell.Scope.CustomerId, "admin", LedgerFixture.Operator, "default", "dev", ["Operator", "Approver"]); break;
            case "approver": f.Store.SetMembership(f.Cell.Scope.CustomerId, "admin", LedgerFixture.Approver, "default", "dev", ["Approver"]); break;
            case "connection": f.Sql("UPDATE connections SET generation=generation+1"); break;
            case "instance": f.Sql("UPDATE cell SET database_instance_id=lower(hex(randomblob(16)))"); break;
            case "quarantined": f.Sql("UPDATE credential_maintenance_intents SET state='Quarantined'"); break;
            default: await f.Cell.Vault.RevokeAsync(f.Cell.Scope.AuthProfileId, 1); break;
        }
        var before = File.ReadAllBytes(f.Profile);
        var executor = await f.Context("operator", FoundationCapabilities.CredentialMaintenanceExecute);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Executor().ExecuteAsync(executor, plan.Id, plan.Digest));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM credential_maintenance_executions")); Assert.Equal(before, File.ReadAllBytes(f.Profile));
    }

    [Theory]
    [InlineData("Suspended")]
    [InlineData("Offboarding")]
    public async Task Inactive_deny_only_revoke_is_reviewed_without_reactivating_the_cell(string state)
    {
        using var f = new LedgerFixture(); f.Sql("UPDATE cell SET enabled=0,policy_version=policy_version+1; UPDATE customer_lifecycle SET state='" + state + "'");
        var plan = await f.Prepare(); await f.Approve(plan);
        var result = await f.Executor().ExecuteAsync(await f.Context("operator", FoundationCapabilities.CredentialMaintenanceExecute), plan.Id, plan.Digest);
        Assert.Equal("Succeeded", result.Execution!.State); Assert.False((await f.Cell.Vault.GetProfileAsync(f.Cell.Scope.AuthProfileId)).Enabled);
        Assert.Equal(0, f.Scalar("SELECT enabled FROM cell"));
        var target = await f.Target(CredentialMaintenanceOperation.Rewrap);
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Prepare(CredentialMaintenanceOperation.Rewrap, target));
        f.Sql("UPDATE customer_lifecycle SET state='Offboarded'"); await Assert.ThrowsAsync<FoundationStoreException>(() => f.Prepare());
    }

    [WindowsCredentialFact]
    public async Task Required_admission_audit_failure_rolls_back_claim_and_prevents_vault_mutation()
    {
        using var f = new LedgerFixture(); var plan = await f.Prepare(); await f.Approve(plan); var before = File.ReadAllBytes(f.Profile);
        f.Sql("CREATE TRIGGER synthetic_fail_admit BEFORE INSERT ON audit WHEN NEW.action='CredentialMaintenanceAdmitted' BEGIN SELECT RAISE(ABORT,'synthetic-audit-fault'); END");
        var executor = await f.Context("operator", FoundationCapabilities.CredentialMaintenanceExecute);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Executor().ExecuteAsync(executor, plan.Id, plan.Digest));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM credential_maintenance_executions")); Assert.Equal(before, File.ReadAllBytes(f.Profile));
        f.Sql("DROP TRIGGER synthetic_fail_admit");
        Assert.Equal("Succeeded", (await f.Executor().ExecuteAsync(await f.Context("operator", FoundationCapabilities.CredentialMaintenanceExecute), plan.Id, plan.Digest)).Execution!.State);
    }

    [WindowsCredentialFact]
    public async Task Completion_audit_failure_keeps_consumed_unknown_without_replaying_the_completed_local_effect()
    {
        using var f = new LedgerFixture(); var plan = await f.Prepare(); await f.Approve(plan);
        f.Sql("CREATE TRIGGER synthetic_fail_complete BEFORE INSERT ON audit WHEN NEW.action='CredentialMaintenanceCompleted' BEGIN SELECT RAISE(ABORT,'synthetic-audit-fault'); END");
        var result = await f.Executor().ExecuteAsync(await f.Context("operator", FoundationCapabilities.CredentialMaintenanceExecute), plan.Id, plan.Digest);
        Assert.Equal("OutcomeUnknown", result.Execution!.State); Assert.Null(result.Execution.Result); Assert.Null(result.Execution.CompletedAt);
        Assert.Equal(2, (await f.Cell.Vault.GetProfileAsync(f.Cell.Scope.AuthProfileId)).Generation);
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Executor().ExecuteAsync(f.ExecuteContext!, plan.Id, plan.Digest));
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM credential_maintenance_executions"));
    }

    [Theory]
    [InlineData("intent")]
    [InlineData("result")]
    [InlineData("execution-instance")]
    [InlineData("unknown-result")]
    public async Task Scoped_reads_reject_corrupted_historical_intents_and_receipts(string changed)
    {
        using var f = new LedgerFixture(); var plan = await f.Prepare(); await f.Approve(plan);
        if (changed != "intent") await f.Executor().ExecuteAsync(await f.Context("operator", FoundationCapabilities.CredentialMaintenanceExecute), plan.Id, plan.Digest);
        switch (changed)
        {
            case "intent": f.Sql("DROP TRIGGER credential_maintenance_intents_immutable; UPDATE credential_maintenance_intents SET target_sha256='" + new string('f', 64) + "'"); break;
            case "result": f.Sql("DROP TRIGGER credential_maintenance_success_immutable; UPDATE credential_maintenance_executions SET result_sha256='" + new string('f', 64) + "'"); break;
            case "execution-instance": f.Sql("DROP TRIGGER credential_maintenance_execution_immutable; DROP TRIGGER credential_maintenance_success_immutable; UPDATE credential_maintenance_executions SET database_instance_id=lower(hex(randomblob(16)))"); break;
            default: f.Sql("DROP TRIGGER credential_maintenance_success_immutable; UPDATE credential_maintenance_executions SET state='OutcomeUnknown'"); break;
        }
        var reader = await f.Context("auditor", FoundationCapabilities.CredentialMaintenanceRead);
        Assert.Throws<FoundationStoreException>(() => f.Store.GetCredentialMaintenance(reader, plan.Id));
    }

    [Theory]
    [InlineData("source")]
    [InlineData("destination")]
    [InlineData("purpose")]
    public async Task Secret_shaped_identifiers_and_purpose_are_rejected_without_persisting_the_marker(string field)
    {
        using var f = new LedgerFixture(); var marker = "ghp_" + new string('Z', 32); var target = await f.Target(); var purpose = "Synthetic reviewed local maintenance";
        if (field == "source") target = target with { SourceKeyId = marker }; else if (field == "destination") target = target with { DestinationKeyId = marker }; else purpose = marker;
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Prepare(CredentialMaintenanceOperation.Revoke, target, purpose));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM credential_maintenance_intents")); Assert.Equal(0, f.Scalar("SELECT count(*) FROM credential_maintenance_executions"));
    }

    [WindowsCredentialFact]
    public async Task Pending_quota_excludes_consumed_history_but_refuses_unconsumed_overflow()
    {
        using var f = new LedgerFixture(); var target = await f.Target(); FoundationCredentialMaintenanceIntent? first = null;
        for (var i = 0; i < FoundationStore.MaximumPendingCredentialMaintenanceIntents; i++) { var intent = await f.Prepare(target: target); first ??= intent; }
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Prepare(target: target));
        await f.Approve(first!); await f.Executor().ExecuteAsync(await f.Context("operator", FoundationCapabilities.CredentialMaintenanceExecute), first!.Id, first.Digest);
        await f.Prepare(target: await f.Target()); Assert.Equal(65, f.Scalar("SELECT count(*) FROM credential_maintenance_intents"));
    }

    sealed class LedgerFixture : IDisposable
    {
        internal const string Operator = "user:" + IdentityTestIssuer.Tenant + ":" + IdentityTestIssuer.Subject;
        internal const string Approver = "user:" + IdentityTestIssuer.Tenant + ":" + IdentityTestIssuer.OtherSubject;
        const string Auditor = "user:" + IdentityTestIssuer.Tenant + ":dddddddd-dddd-dddd-dddd-dddddddddddd";
        public CredentialFixture Cell { get; } = new();
        readonly IdentityTestIssuer issuer = new();
        CredentialVault executionVault;
        public FoundationStore Store { get; }
        public CustomerContext? ExecuteContext { get; private set; }
        public string Profile => Assert.Single(Directory.GetFiles(Cell.VaultRoot, "*.credential.json"));
        public LedgerFixture()
        {
            executionVault = Cell.Vault;
            Store = new(Path.Combine(Cell.Root, "cell.db"), Cell.Scope.CellId, Cell.Scope.CustomerId, Cell.Clock);
            Store.SetMembership(Cell.Scope.CustomerId, "admin", Operator, "default", "dev", ["Operator", "Approver"]);
            Store.SetMembership(Cell.Scope.CustomerId, "admin", Approver, "default", "dev", ["Approver"]);
            Store.SetMembership(Cell.Scope.CustomerId, "admin", Auditor, "default", "dev", ["Auditor"]);
        }
        public async Task<FoundationCredentialMaintenanceTarget> Target(CredentialMaintenanceOperation operation = CredentialMaintenanceOperation.Revoke)
        {
            if (!Directory.GetFiles(Cell.VaultRoot, "*.credential.json").Any()) await Cell.StoreToken();
            if (operation == CredentialMaintenanceOperation.Rewrap)
            { Cell.Keys.Values.TryAdd("key-2", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)); executionVault = new(Cell.Options with { EncryptionKeyId = "key-2" }, Cell.Keys, Cell.Clock); return await executionVault.InspectMaintenanceAsync(Cell.Scope.AuthProfileId); }
            executionVault = Cell.Vault;
            return await Cell.Vault.InspectMaintenanceAsync(Cell.Scope.AuthProfileId);
        }
        public async Task<FoundationCredentialMaintenanceIntent> Prepare(CredentialMaintenanceOperation operation = CredentialMaintenanceOperation.Revoke,
            FoundationCredentialMaintenanceTarget? target = null, string purpose = "Synthetic reviewed local maintenance") => Store.PrepareCredentialMaintenance(
                await Context("operator", FoundationCapabilities.CredentialMaintenancePrepare), operation, target ?? await Target(operation), purpose, Cell.Clock.Now.AddMinutes(10));
        public async Task Approve(FoundationCredentialMaintenanceIntent intent) => Store.ApproveCredentialMaintenance(await Context("approver", FoundationCapabilities.CredentialMaintenanceApprove), intent.Id, intent.Digest);
        public FoundationCredentialMaintenanceExecutor Executor() => new(Store, executionVault, executionVault.DescribeNamespace(), Store.GetCellInstanceIdentity().DatabaseInstanceId);
        public async Task<CustomerContext> Context(string actor, string capability)
        {
            var role = capability == FoundationCapabilities.CredentialMaintenanceApprove ? "Approver" : actor == "auditor" ? "Auditor" : "Operator";
            var subject = actor == "operator" ? IdentityTestIssuer.Subject : actor == "approver" ? IdentityTestIssuer.OtherSubject : "dddddddd-dddd-dddd-dddd-dddddddddddd";
            var options = issuer.Options(Cell.Scope.CustomerId, Cell.Scope.CellId); var claims = issuer.UserClaims(role, subject);
            claims["nbf"] = Cell.Clock.Now.AddSeconds(-10).ToUnixTimeSeconds(); claims["iat"] = Cell.Clock.Now.AddSeconds(-10).ToUnixTimeSeconds(); claims["exp"] = Cell.Clock.Now.AddHours(2).ToUnixTimeSeconds();
            var principal = await new IdentityTokenValidator(options, Cell.Clock).ValidateAsync(issuer.Token(claims));
            var result = new CustomerContextResolver(options, new Members(Store), Cell.Clock).Require(principal, Cell.Scope.CustomerId, "default", "dev", capability);
            if (capability == FoundationCapabilities.CredentialMaintenanceExecute) ExecuteContext = result; return result;
        }
        public void Sql(string sql) { using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Cell.Root, "cell.db"), Pooling = false }.ToString()); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public long Scalar(string sql) { using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Cell.Root, "cell.db"), Pooling = false }.ToString()); db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        public void Dispose() { issuer.Dispose(); Cell.Dispose(); }
        sealed class Members(FoundationStore store) : ICustomerMembershipSource
        { public IReadOnlyList<MembershipGrant> Find(string customer, string actor) => store.GetCredentialMaintenanceMemberships(customer, actor).Select(m => new MembershipGrant(m.CustomerId, m.CellId, m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version, m.Enabled)).ToArray(); }
    }
}
