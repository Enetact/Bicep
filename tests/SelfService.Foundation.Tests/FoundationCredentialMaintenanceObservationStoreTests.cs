using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationCredentialMaintenanceObservationStoreTests
{
    [Theory]
    [InlineData(CredentialMaintenanceOperation.Revoke)]
    [InlineData(CredentialMaintenanceOperation.Rewrap)]
    public async Task Independently_reviewed_current_observation_preserves_unknown_execution_and_original_approval(CredentialMaintenanceOperation operation)
    {
        using var f = new ObservationStoreFixture(); var original = await f.Unknown(operation); var history = f.OriginalHistory(original.Id);
        var request = await f.Prepare(original); var approved = await f.Approve(request);
        var observed = await f.Record(approved); Assert.Equal("ObservedPublished", observed.Receipt!.Status);
        Assert.Equal(request.ObservationSha256, observed.Receipt.ObservationSha256); Assert.Equal(request.Digest, observed.Receipt.RequestDigest);
        Assert.Equal(history, f.OriginalHistory(original.Id)); Assert.Equal("OutcomeUnknown", f.Store.GetCredentialMaintenance(await f.Context("current", FoundationCapabilities.CredentialMaintenanceRead), original.Id).Execution!.State);
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM credential_maintenance_observation_receipts"));
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM audit WHERE action='CredentialMaintenanceObservedPublished'"));
        Assert.Throws<SqliteException>(() => f.Sql("UPDATE credential_maintenance_observation_receipts SET status='Succeeded'"));
        Assert.Throws<SqliteException>(() => f.Sql("DELETE FROM credential_maintenance_observation_approvals"));
        Assert.Throws<SqliteException>(() => f.Sql("UPDATE credential_maintenance_observation_requests SET state='Quarantined'"));
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Record(approved));
        var json = JsonSerializer.Serialize(observed); Assert.DoesNotContain(CredentialFixture.Token, json); Assert.DoesNotContain(f.Cell.Root, json);
    }

    [WindowsCredentialFact]
    public async Task Read_only_metadata_shape_accepts_terminal_safe_counters_without_relaxing_existing_write_targets()
    {
        using var f = new ObservationStoreFixture(); await f.Unknown(); var current = await f.Vault.InspectMaintenanceAsync(f.Cell.Scope.AuthProfileId);
        var terminal = current with { ProfileGeneration = 9007199254740991, AuthorityNamespaceId = Guid.NewGuid().ToString("N"),
            AuthorityBindingSha256 = new string('a', 64), AuthorityStateSha256 = new string('b', 64), AuthorityRevision = 9007199254740991 };
        var validate = typeof(FoundationStore).GetMethod("ValidateMaintenanceTarget", BindingFlags.Static | BindingFlags.NonPublic)!;
        Assert.NotNull(validate.Invoke(null, [terminal, f.Cell.Scope.CustomerId, f.Cell.Scope.CellId, true]));
        var originalWriteLimit = Assert.Throws<TargetInvocationException>(() => validate.Invoke(null, [terminal, f.Cell.Scope.CustomerId, f.Cell.Scope.CellId, false]));
        Assert.IsType<FoundationStoreException>(originalWriteLimit.InnerException);
        foreach (var invalid in new[] { terminal with { ProfileGeneration = terminal.ProfileGeneration + 1 }, terminal with { AuthorityRevision = terminal.AuthorityRevision + 1 } })
            Assert.IsType<FoundationStoreException>(Assert.Throws<TargetInvocationException>(() => validate.Invoke(null, [invalid, f.Cell.Scope.CustomerId, f.Cell.Scope.CellId, true])).InnerException);
    }

    [WindowsCredentialFact]
    public async Task Historical_quarantined_expired_original_and_revoked_original_actors_do_not_supply_or_prevent_fresh_review_authority()
    {
        using var f = new ObservationStoreFixture(); var original = await f.Unknown();
        f.Store.SetMembership(f.Cell.Scope.CustomerId, "admin", f.Actor("operator"), "default", "dev", []);
        f.Store.SetMembership(f.Cell.Scope.CustomerId, "admin", f.Actor("approver"), "default", "dev", []);
        f.Sql("UPDATE credential_maintenance_intents SET state='Quarantined'; UPDATE cell SET policy_version=policy_version+1,database_instance_id=lower(hex(randomblob(16))); UPDATE connections SET generation=generation+1,enabled=0");
        f.Cell.Clock.Now = f.Cell.Clock.Now.AddHours(2); var history = f.OriginalHistory(original.Id);
        var request = await f.Prepare(original); Assert.NotEqual(original.DatabaseInstanceId, request.DatabaseInstanceId); Assert.NotEqual(original.PolicyVersion, request.PolicyVersion);
        var recorded = await f.Record(await f.Approve(request)); Assert.Equal("ObservedPublished", recorded.Receipt!.Status);
        Assert.Equal(history, f.OriginalHistory(original.Id)); Assert.True(f.Store.VerifyAuditIntegrity(f.Cell.Scope.CustomerId, "auditor"));
    }

    [Theory]
    [InlineData("Suspended")]
    [InlineData("Offboarding")]
    public async Task Current_observation_is_safe_scoped_evidence_in_inactive_cells_without_reactivation(string state)
    {
        using var f = new ObservationStoreFixture(); var original = await f.Unknown(CredentialMaintenanceOperation.Rewrap);
        f.Sql("UPDATE cell SET enabled=0,policy_version=policy_version+1; UPDATE customer_lifecycle SET state='" + state + "'");
        var recorded = await f.Record(await f.Approve(await f.Prepare(original))); Assert.Equal("ObservedPublished", recorded.Receipt!.Status);
        Assert.Equal(0, f.Scalar("SELECT enabled FROM cell"));
        f.Sql("UPDATE customer_lifecycle SET state='Offboarded'"); await Assert.ThrowsAsync<FoundationStoreException>(() => f.Prepare(original));
        var history = f.Store.GetCredentialMaintenanceObservation(await f.Context("current", FoundationCapabilities.CredentialMaintenanceRead), recorded.Id);
        Assert.Equal(recorded.Receipt.Digest, history.Receipt!.Digest);
    }

    [Theory]
    [InlineData("policy")]
    [InlineData("instance")]
    [InlineData("requester")]
    [InlineData("approver")]
    [InlineData("recorder")]
    [InlineData("expired")]
    [InlineData("quarantined")]
    public async Task Final_record_rechecks_only_current_review_authority_and_does_not_change_vault_or_old_history(string changed)
    {
        using var f = new ObservationStoreFixture(); var original = await f.Unknown(); var request = await f.Approve(await f.Prepare(original));
        var context = await f.Context("current", FoundationCapabilities.CredentialMaintenanceExecute); var bytes = File.ReadAllBytes(f.Profile); var history = f.OriginalHistory(original.Id);
        switch (changed)
        {
            case "policy": f.Sql("UPDATE cell SET policy_version=policy_version+1"); break;
            case "instance": f.Sql("UPDATE cell SET database_instance_id=lower(hex(randomblob(16)))"); break;
            case "requester": f.Store.SetMembership(f.Cell.Scope.CustomerId, "admin", f.Actor("current"), "default", "dev", ["Operator", "Approver"]); break;
            case "approver": f.Store.SetMembership(f.Cell.Scope.CustomerId, "admin", f.Actor("reviewer"), "default", "dev", ["Approver"]); break;
            case "recorder": f.Store.SetMembership(f.Cell.Scope.CustomerId, "admin", f.Actor("current"), "default", "dev", []); break;
            case "expired": f.Cell.Clock.Now = f.Cell.Clock.Now.AddMinutes(16); break;
            default: f.Sql("UPDATE credential_maintenance_observation_requests SET state='Quarantined'"); break;
        }
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Record(request, context));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM credential_maintenance_observation_receipts")); Assert.Equal(bytes, File.ReadAllBytes(f.Profile));
        Assert.Equal(history, f.OriginalHistory(original.Id));
    }

    [WindowsCredentialFact]
    public async Task Current_operator_approver_and_recorder_separation_are_required_for_this_exact_digest()
    {
        using var f = new ObservationStoreFixture(); var original = await f.Unknown(); var request = await f.Prepare(original);
        var self = await f.Context("current", FoundationCapabilities.CredentialMaintenanceApprove);
        Assert.Equal("SelfApprovalDenied", Assert.Throws<FoundationStoreException>(() => f.Store.ApproveCredentialMaintenanceObservation(
            self, request.Id, request.Digest)).Code);
        await f.Approve(request); var sameApprover = await f.Context("reviewer", FoundationCapabilities.CredentialMaintenanceExecute);
        Assert.Equal("ApprovalSeparationRequired", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Record(request, sameApprover))).Code);
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Record(request with { Digest = new string('0', 64) }));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM credential_maintenance_observation_receipts"));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("enabled")]
    [InlineData("expiry")]
    [InlineData("source-key")]
    [InlineData("namespace")]
    [InlineData("envelope")]
    public async Task Fabricated_current_snapshots_cannot_match_the_original_admitted_candidate(string changed)
    {
        using var f = new ObservationStoreFixture(); var original = await f.Unknown(); var target = await f.Vault.InspectMaintenanceAsync(f.Cell.Scope.AuthProfileId);
        target = changed switch
        {
            "generation" => target with { ProfileGeneration = target.ProfileGeneration + 1 },
            "enabled" => target with { Enabled = !target.Enabled },
            "expiry" => target with { ExpiresUnixMilliseconds = target.ExpiresUnixMilliseconds + 1 },
            "source-key" => target with { SourceKeyId = "other-key" },
            "namespace" => target with { NamespaceCommitmentSha256 = new string('0', 64) },
            _ => target with { EnvelopeSha256 = new string('0', 64) }
        };
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Prepare(original, target)); Assert.Equal(0, f.Scalar("SELECT count(*) FROM credential_maintenance_observation_requests"));
    }

    [WindowsCredentialFact]
    public async Task Admitted_but_unpublished_original_is_still_unknown_and_cannot_be_attested_published()
    {
        using var f = new ObservationStoreFixture(); var original = await f.Unknown(afterMutation: false);
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Prepare(original)); Assert.Equal(0, f.Scalar("SELECT count(*) FROM credential_maintenance_observation_requests"));
    }

    [WindowsCredentialFact]
    public async Task Audit_failure_rolls_back_receipt_while_a_postcommit_failure_is_explicitly_uncertain_and_readable()
    {
        using var f = new ObservationStoreFixture(); var original = await f.Unknown(); var request = await f.Approve(await f.Prepare(original)); var history = f.OriginalHistory(original.Id);
        f.Sql("CREATE TRIGGER synthetic_observation_audit_failure BEFORE INSERT ON audit WHEN NEW.action='CredentialMaintenanceObservedPublished' BEGIN SELECT RAISE(ABORT,'synthetic audit failure'); END");
        await Assert.ThrowsAsync<SqliteException>(() => f.Record(request)); Assert.Equal(0, f.Scalar("SELECT count(*) FROM credential_maintenance_observation_receipts"));
        f.Sql("DROP TRIGGER synthetic_observation_audit_failure");
        typeof(FoundationStore).GetProperty("ObservationCommitFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Store, (Action)(() => throw new IOException("synthetic after-commit interruption")));
        Assert.Equal("MaintenanceObservationRecordUncertain", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Record(request))).Code);
        var recorded = f.Store.GetCredentialMaintenanceObservation(await f.Context("current", FoundationCapabilities.CredentialMaintenanceRead), request.Id);
        Assert.Equal("ObservedPublished", recorded.Receipt!.Status); Assert.Equal(history, f.OriginalHistory(original.Id));
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM audit WHERE action='CredentialMaintenanceObservedPublished'"));
        Assert.Equal("MaintenanceObservationAlreadyRecorded", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Record(request))).Code);
    }

    [Theory]
    [InlineData("request")]
    [InlineData("receipt")]
    [InlineData("approval")]
    [InlineData("original")]
    public async Task Public_historical_reads_reject_tampered_new_or_original_evidence(string changed)
    {
        using var f = new ObservationStoreFixture(); var original = await f.Unknown(); var request = await f.Record(await f.Approve(await f.Prepare(original)));
        switch (changed)
        {
            case "request": f.Sql("DROP TRIGGER credential_maintenance_observation_requests_immutable; DROP TRIGGER credential_maintenance_observation_recorded_immutable; UPDATE credential_maintenance_observation_requests SET observation_sha256='modified'"); break;
            case "receipt": f.Sql("DROP TRIGGER credential_maintenance_observation_receipts_no_update; UPDATE credential_maintenance_observation_receipts SET digest='modified'"); break;
            case "approval": f.Sql("DROP TRIGGER credential_maintenance_observation_approvals_no_update; UPDATE credential_maintenance_observation_approvals SET actor_version=0"); break;
            default: f.Sql("DROP TRIGGER credential_maintenance_execution_immutable; UPDATE credential_maintenance_executions SET expected_after_sha256='modified'"); break;
        }
        var read = await f.Context("current", FoundationCapabilities.CredentialMaintenanceRead);
        Assert.Throws<FoundationStoreException>(() => f.Store.GetCredentialMaintenanceObservation(read, request.Id));
    }

    [WindowsCredentialFact]
    public async Task Pending_quota_is_bounded_and_recorded_history_does_not_consume_a_pending_slot()
    {
        using var f = new ObservationStoreFixture(); var original = await f.Unknown(); var snapshot = await f.Vault.InspectMaintenanceAsync(f.Cell.Scope.AuthProfileId);
        FoundationCredentialMaintenanceObservationRequest? first = null;
        for (var i = 0; i < FoundationStore.MaximumPendingCredentialMaintenanceObservations; i++)
        { var request = await f.Prepare(original, snapshot); first ??= request; }
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Prepare(original, snapshot));
        await f.Record(await f.Approve(first!)); await f.Prepare(original, snapshot);
        Assert.Equal(65, f.Scalar("SELECT count(*) FROM credential_maintenance_observation_requests"));
    }

    sealed class ObservationStoreFixture : IDisposable
    {
        readonly IdentityTestIssuer issuer = new();
        readonly Dictionary<string, string> subjects = new(StringComparer.Ordinal)
        { ["operator"] = IdentityTestIssuer.Subject, ["approver"] = IdentityTestIssuer.OtherSubject,
            ["current"] = "dddddddd-dddd-dddd-dddd-dddddddddddd", ["reviewer"] = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee" };
        public CredentialFixture Cell { get; } = new();
        public FoundationStore Store { get; }
        public CredentialVault Vault { get; private set; }
        public string Profile => Assert.Single(Directory.GetFiles(Cell.VaultRoot, "*.credential.json"));
        public ObservationStoreFixture()
        {
            Vault = Cell.Vault; Store = new(Path.Combine(Cell.Root, "cell.db"), Cell.Scope.CellId, Cell.Scope.CustomerId, Cell.Clock);
            foreach (var actor in subjects.Keys) Store.SetMembership(Cell.Scope.CustomerId, "admin", Actor(actor), "default", "dev", ["Operator", "Approver", "Auditor"]);
            Store.SetMembership(Cell.Scope.CustomerId, "admin", "auditor", "default", "dev", ["Auditor"]);
        }
        public string Actor(string name) => "user:" + IdentityTestIssuer.Tenant + ":" + subjects[name];
        public async Task<FoundationCredentialMaintenanceIntent> Unknown(CredentialMaintenanceOperation operation = CredentialMaintenanceOperation.Revoke, bool afterMutation = true)
        {
            await Cell.StoreToken();
            if (operation == CredentialMaintenanceOperation.Rewrap)
            { Cell.Keys.Values.TryAdd("key-2", System.Security.Cryptography.RandomNumberGenerator.GetBytes(32)); Vault = new(Cell.Options with { EncryptionKeyId = "key-2" }, Cell.Keys, Cell.Clock); }
            var target = await Vault.InspectMaintenanceAsync(Cell.Scope.AuthProfileId);
            var intent = Store.PrepareCredentialMaintenance(await Context("operator", FoundationCapabilities.CredentialMaintenancePrepare), operation, target,
                "Synthetic original maintenance", Cell.Clock.Now.AddMinutes(10));
            Store.ApproveCredentialMaintenance(await Context("approver", FoundationCapabilities.CredentialMaintenanceApprove), intent.Id, intent.Digest);
            var executor = new FoundationCredentialMaintenanceExecutor(Store, Vault, Vault.DescribeNamespace(), Store.GetCellInstanceIdentity().DatabaseInstanceId);
            typeof(FoundationCredentialMaintenanceExecutor).GetProperty("MaintenanceFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(executor,
                (Action<string>)(stage => { if (stage == (afterMutation ? "AfterMutation" : "AfterAdmission")) throw new IOException("synthetic original interruption"); }));
            var unknown = await executor.ExecuteAsync(await Context("operator", FoundationCapabilities.CredentialMaintenanceExecute), intent.Id, intent.Digest);
            Assert.Equal("OutcomeUnknown", unknown.Execution!.State); return unknown;
        }
        public async Task<FoundationCredentialMaintenanceObservationRequest> Prepare(FoundationCredentialMaintenanceIntent original, FoundationCredentialMaintenanceTarget? observation = null) =>
            Store.PrepareCredentialMaintenanceObservation(await Context("current", FoundationCapabilities.CredentialMaintenancePrepare), original.Id,
                observation ?? await Vault.InspectMaintenanceAsync(Cell.Scope.AuthProfileId), "Synthetic current published observation", Cell.Clock.Now.AddMinutes(10));
        public async Task<FoundationCredentialMaintenanceObservationRequest> Approve(FoundationCredentialMaintenanceObservationRequest request) =>
            Store.ApproveCredentialMaintenanceObservation(await Context("reviewer", FoundationCapabilities.CredentialMaintenanceApprove), request.Id, request.Digest);
        public async Task<FoundationCredentialMaintenanceObservationRequest> Record(FoundationCredentialMaintenanceObservationRequest request, CustomerContext? context = null)
        {
            context ??= await Context("current", FoundationCapabilities.CredentialMaintenanceExecute);
            var observation = await Vault.InspectMaintenanceAsync(Cell.Scope.AuthProfileId);
            try { return (FoundationCredentialMaintenanceObservationRequest)typeof(FoundationStore).GetMethod("RecordCredentialMaintenanceObservation", BindingFlags.Instance | BindingFlags.NonPublic)!
                    .Invoke(Store, [context, request.Id, request.Digest, observation])!; }
            catch (TargetInvocationException e) when (e.InnerException is not null) { ExceptionDispatchInfo.Capture(e.InnerException).Throw(); throw; }
        }
        public async Task<CustomerContext> Context(string actor, string capability)
        {
            var role = capability == FoundationCapabilities.CredentialMaintenanceApprove ? "Approver" : "Operator";
            var claims = issuer.UserClaims(role, subjects[actor]); claims["nbf"] = Cell.Clock.Now.AddSeconds(-10).ToUnixTimeSeconds();
            claims["iat"] = Cell.Clock.Now.AddSeconds(-10).ToUnixTimeSeconds(); claims["exp"] = Cell.Clock.Now.AddHours(2).ToUnixTimeSeconds();
            var options = issuer.Options(Cell.Scope.CustomerId, Cell.Scope.CellId);
            var principal = await new IdentityTokenValidator(options, Cell.Clock).ValidateAsync(issuer.Token(claims));
            return new CustomerContextResolver(options, new Members(Store), Cell.Clock).Require(principal, Cell.Scope.CustomerId, "default", "dev", capability);
        }
        public string OriginalHistory(string id) => Text("SELECT json_object('i',i.target,'state',i.state,'approver',a.actor_key,'version',a.actor_version,'approval',a.digest,'admission',e.admission_id,'after',e.expected_after_sha256,'execution',e.state,'reason',e.reason,'result',e.result_json,'updated',e.updated) FROM credential_maintenance_intents i JOIN credential_maintenance_approvals a ON a.customer_id=i.customer_id AND a.intent_id=i.id JOIN credential_maintenance_executions e ON e.customer_id=i.customer_id AND e.intent_id=i.id WHERE i.id='" + id + "'");
        public void Sql(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public string Text(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToString(command.ExecuteScalar())!; }
        public long Scalar(string sql) => Convert.ToInt64(Text(sql));
        SqliteConnection Open() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Cell.Root, "cell.db"), Pooling = false }.ToString()); db.Open(); return db; }
        public void Dispose() { issuer.Dispose(); Cell.Dispose(); }
        sealed class Members(FoundationStore store) : ICustomerMembershipSource
        { public IReadOnlyList<MembershipGrant> Find(string customer, string actor) => store.GetCredentialMaintenanceMemberships(customer, actor).Select(m => new MembershipGrant(m.CustomerId, m.CellId, m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version, m.Enabled)).ToArray(); }
    }
}
