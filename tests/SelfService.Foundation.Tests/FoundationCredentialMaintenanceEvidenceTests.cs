using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationCredentialMaintenanceEvidenceTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(CredentialMaintenanceOperation.Revoke)]
    [InlineData(CredentialMaintenanceOperation.Rewrap)]
    public async Task Exact_local_result_uses_existing_audit_exports_without_private_payloads(CredentialMaintenanceOperation operation)
    {
        using var f = new EvidenceFixture(); await f.Seed(); var intent = await f.Prepare(operation); await f.Approve(intent);
        f.Store.StoreArtifact(f.Customer, "requester", "default", "dev", "private.bicep", "private-maintenance-source-marker"u8.ToArray());
        f.Sql("UPDATE jobs SET receipt='private-maintenance-provider-receipt-marker'");
        var envelope = JsonDocument.Parse(await File.ReadAllBytesAsync(f.Envelope));
        var ciphertext = envelope.RootElement.GetProperty("ciphertext").GetString()!; envelope.Dispose();
        var completed = await f.Executor().ExecuteAsync(await f.Operator(), intent.Id, intent.Digest);
        Assert.Equal("Succeeded", completed.Execution!.State); Assert.NotNull(completed.Execution.ResultSha256);
        Assert.Equal(2, (await f.Vault.GetProfileAsync(f.Credentials.Scope.AuthProfileId)).Generation);
        var start = f.Export(); var pages = ReadAll(f.Store, f.Customer, start); VerifyCanonical(start, pages);
        var events = AuditItems(pages).Where(e => e.GetProperty("targetId").GetString() == intent.Id).ToArray();
        Assert.Contains(events, e => Action(e) == "CredentialMaintenancePrepared" && Outcome(e) == "Prepared");
        Assert.Contains(events, e => Action(e) == "CredentialMaintenanceApproved" && Outcome(e) == "Approved");
        Assert.Contains(events, e => Action(e) == "CredentialMaintenanceAdmitted" && Outcome(e) == "OutcomeUnknown");
        Assert.Contains(events, e => Action(e) == "CredentialMaintenanceCompleted" && Outcome(e) == "Succeeded");
        var publicBytes = string.Join('\n', pages.Select(p => Encoding.UTF8.GetString(Convert.FromBase64String(p.CanonicalPageBase64))))
            + JsonSerializer.Serialize(completed, Json);
        foreach (var secret in new[] { CredentialFixture.Token, ciphertext, Convert.ToBase64String(f.Credentials.Keys.Values["key-1"]),
            "private-maintenance-source-marker", "private-maintenance-provider-receipt-marker", "customer-org", "customer-repo" })
            Assert.DoesNotContain(secret, publicBytes);
        Assert.All(AuditItems(pages), e => { Assert.Equal("default", e.GetProperty("projectId").GetString()); Assert.Equal("dev", e.GetProperty("environmentId").GetString()); });
        Assert.True(f.Store.VerifyAuditIntegrity(f.Customer, EvidenceFixture.AuditorActor));
    }

    [Fact]
    public async Task An_immutable_export_does_not_reinterpret_admission_unknown_as_later_success()
    {
        using var f = new EvidenceFixture(); await f.Seed(); var intent = await f.Prepare(); await f.Approve(intent);
        var earlier = f.Export(); var firstDigest = earlier.FirstPage.Sha256;
        var completed = await f.Executor().ExecuteAsync(await f.Operator(), intent.Id, intent.Digest);
        Assert.Equal("Succeeded", completed.Execution!.State);
        var frozen = ReadAll(f.Store, f.Customer, earlier); VerifyCanonical(earlier, frozen);
        Assert.DoesNotContain(AuditItems(frozen), e => Action(e) == "CredentialMaintenanceCompleted");
        var reread = f.Store.ReadPagedScopeExportPage(f.Customer, EvidenceFixture.AuditorActor, "default", "dev", earlier.Snapshot.Id,
            earlier.FirstCursor, earlier.Snapshot.Sha256);
        Assert.Equal(firstDigest, reread.Sha256);
        var later = f.Export(); var laterPages = ReadAll(f.Store, f.Customer, later); VerifyCanonical(later, laterPages);
        Assert.Contains(AuditItems(laterPages), e => Action(e) == "CredentialMaintenanceCompleted" && e.GetProperty("targetId").GetString() == intent.Id);
        Assert.True(later.Snapshot.AuditSequence > earlier.Snapshot.AuditSequence);
    }

    [Theory]
    [InlineData("Prepared")]
    [InlineData("Approved")]
    [InlineData("UnknownBeforeMutation")]
    [InlineData("UnknownAfterMutation")]
    [InlineData("Succeeded")]
    public async Task Restored_intents_are_history_and_never_reusable_maintenance_authority(string stage)
    {
        using var f = new EvidenceFixture(); await f.Seed(); var intent = await f.Prepare();
        if (stage != "Prepared") await f.Approve(intent);
        if (stage.StartsWith("Unknown", StringComparison.Ordinal))
        {
            var executor = f.Executor(); Fault(executor, stage == "UnknownBeforeMutation" ? "AfterAdmission" : "AfterMutation");
            var unknown = await executor.ExecuteAsync(await f.Operator(), intent.Id, intent.Digest); Assert.Equal("OutcomeUnknown", unknown.Execution!.State);
        }
        if (stage == "Succeeded") Assert.Equal("Succeeded", (await f.Executor().ExecuteAsync(await f.Operator(), intent.Id, intent.Digest)).Execution!.State);
        var original = f.Store.GetCredentialMaintenance(await f.Auditor(), intent.Id);
        var execution = JsonSerializer.Serialize(original.Execution, Json); var approval = f.ApprovalBytes(intent.Id);
        var originalInstance = f.Store.GetCellInstanceIdentity(); var originalPolicy = f.Number("SELECT policy_version FROM cell");
        var bytes = await File.ReadAllBytesAsync(f.Envelope); using var signer = RSA.Create(2048);
        var anchor = f.Store.CreateAuditAnchor(f.Customer, EvidenceFixture.AuditorActor, signer, "maintenance-evidence-key");
        var backup = f.Store.CreateBackup(f.Child("snapshot.sqlite")); f.Credentials.Keys.Requests.Clear();
        var restored = FoundationStore.RestoreBackup(backup, f.Child("restored.sqlite"), f.Credentials.Clock);
        Assert.Empty(f.Credentials.Keys.Requests); Assert.Equal(bytes, await File.ReadAllBytesAsync(f.Envelope));
        Assert.NotEqual(originalInstance.DatabaseInstanceId, restored.GetCellInstanceIdentity().DatabaseInstanceId);
        Assert.Equal(originalPolicy + 1, f.Number("SELECT policy_version FROM cell", f.Child("restored.sqlite")));
        var history = restored.GetCredentialMaintenance(await f.Auditor(restored), intent.Id);
        Assert.Equal(stage == "Succeeded" ? "Approved" : "Quarantined", history.State);
        Assert.Equal(execution, JsonSerializer.Serialize(history.Execution, Json));
        Assert.Equal(approval, f.ApprovalBytes(intent.Id, f.Child("restored.sqlite")));
        Assert.Equal(original.DatabaseInstanceId, history.DatabaseInstanceId);
        Assert.True(restored.VerifyAuditIntegrity(f.Customer, EvidenceFixture.AuditorActor));
        Assert.True(restored.VerifyAuditAnchor(f.Customer, EvidenceFixture.AuditorActor, anchor, signer, "maintenance-evidence-key"));
        var recovery = restored.ListAudit(f.Customer, EvidenceFixture.AuditorActor).Where(e => e.Action == "RecoveredCredentialMaintenanceHeld" && e.TargetId == intent.Id).ToArray();
        if (stage == "Succeeded") Assert.Empty(recovery);
        else { var audit = Assert.Single(recovery); Assert.Equal("OutcomeUnknown", audit.Outcome); Assert.Equal("RecoveryRequiresReconciliation", audit.Reason); Assert.Equal(intent.Digest, audit.CorrelationId); }
        // Prepared/Approved/UnknownBeforeMutation still have exactly the original generation in the real vault.
        // Matching custody does not revive copied approval or turn OutcomeUnknown into retry authority.
        if (stage is "Prepared" or "Approved" or "UnknownBeforeMutation") Assert.Equal(intent.Target.ProfileGeneration, (await f.Vault.GetProfileAsync(f.Credentials.Scope.AuthProfileId)).Generation);
        var resumed = f.Executor(restored); var admissions = f.Number("SELECT count(*) FROM credential_maintenance_executions", f.Child("restored.sqlite"));
        var restoredOperator = await f.Operator(restored);
        await Assert.ThrowsAnyAsync<Exception>(() => resumed.ExecuteAsync(restoredOperator, intent.Id, intent.Digest));
        Assert.Equal(admissions, f.Number("SELECT count(*) FROM credential_maintenance_executions", f.Child("restored.sqlite")));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(f.Envelope));
        var start = restored.CreatePagedScopeExport(f.Customer, EvidenceFixture.AuditorActor, "default", "dev", 3);
        VerifyCanonical(start, ReadAll(restored, f.Customer, start));
        Assert.Equal(original.State, f.Store.GetCredentialMaintenance(await f.Auditor(), intent.Id).State);
    }

    [Fact]
    public async Task Unknown_history_requires_current_scoped_reader_and_old_pages_do_not_survive_regrant()
    {
        using var f = new EvidenceFixture(); await f.Seed(); var intent = await f.Prepare(); await f.Approve(intent);
        var executor = f.Executor(); Fault(executor, "AfterAdmission"); await executor.ExecuteAsync(await f.Operator(), intent.Id, intent.Digest);
        var reader = await f.Auditor(); var snapshot = f.Export();
        Assert.Equal("OutcomeUnknown", f.Store.GetCredentialMaintenance(reader, intent.Id).Execution!.State);
        f.Store.SetMembership(f.Customer, "admin", EvidenceFixture.AuditorActor, "default", "dev", ["Viewer"]);
        Assert.Throws<FoundationStoreException>(() => f.Store.GetCredentialMaintenance(reader, intent.Id));
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.ReadPagedScopeExportPage(f.Customer, EvidenceFixture.AuditorActor,
            "default", "dev", snapshot.Snapshot.Id, snapshot.FirstCursor, snapshot.Snapshot.Sha256)).Code);
        f.Store.SetMembership(f.Customer, "admin", EvidenceFixture.AuditorActor, "default", "dev", ["Auditor"]);
        Assert.Throws<FoundationStoreException>(() => f.Store.GetCredentialMaintenance(reader, intent.Id));
        Assert.Equal("OutcomeUnknown", f.Store.GetCredentialMaintenance(await f.Auditor(), intent.Id).Execution!.State);
        Assert.Equal("ExportAuthorityChanged", Assert.Throws<FoundationStoreException>(() => f.Store.ReadPagedScopeExportPage(f.Customer,
            EvidenceFixture.AuditorActor, "default", "dev", snapshot.Snapshot.Id, snapshot.FirstCursor, snapshot.Snapshot.Sha256)).Code);
    }

    [Fact]
    public async Task Suspended_cell_keeps_unknown_history_available_to_current_auditor_without_retry_authority()
    {
        using var f = new EvidenceFixture(); await f.Seed(); var intent = await f.Prepare(); await f.Approve(intent);
        var executor = f.Executor(); Fault(executor, "AfterAdmission"); await executor.ExecuteAsync(await f.Operator(), intent.Id, intent.Digest);
        f.Store.SetCustomerEnabled(f.Customer, "admin", false);
        Assert.Equal("OutcomeUnknown", f.Store.GetCredentialMaintenance(await f.Auditor(), intent.Id).Execution!.State);
        var snapshot = f.Export(); var pages = ReadAll(f.Store, f.Customer, snapshot); VerifyCanonical(snapshot, pages);
        Assert.Contains(AuditItems(pages), e => Action(e) == "CredentialMaintenanceUnknown" && Outcome(e) == "OutcomeUnknown");
        var before = await File.ReadAllBytesAsync(f.Envelope); var current = await f.Operator();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Executor().ExecuteAsync(current, intent.Id, intent.Digest));
        Assert.Equal(before, await File.ReadAllBytesAsync(f.Envelope));
    }

    [Fact]
    public async Task Database_instance_fence_denies_copied_approval_even_if_mutable_quarantine_state_is_forged_back()
    {
        using var f = new EvidenceFixture(); await f.Seed(); var intent = await f.Prepare(); await f.Approve(intent);
        var snapshot = f.Store.CreateBackup(f.Child("instance-snapshot.sqlite")); var target = f.Child("instance-restored.sqlite");
        var restored = FoundationStore.RestoreBackup(snapshot, target, f.Credentials.Clock);
        Assert.Equal("Quarantined", restored.GetCredentialMaintenance(await f.Auditor(restored), intent.Id).State);
        // Synthetic privileged SQL alteration is not a shipped recovery/force-success operation.
        // A still-matching real vault and copied approval must also fail the independent database-instance pin.
        f.Sql("UPDATE credential_maintenance_intents SET state='Approved'", target);
        Assert.Equal(intent.Target.ProfileGeneration, (await f.Vault.GetProfileAsync(f.Credentials.Scope.AuthProfileId)).Generation);
        var current = await f.Operator(restored); var before = await File.ReadAllBytesAsync(f.Envelope);
        var error = await Assert.ThrowsAsync<FoundationStoreException>(() => f.Executor(restored).ExecuteAsync(current, intent.Id, intent.Digest));
        Assert.Equal("MaintenanceInstanceChanged", error.Code);
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_maintenance_executions", target));
        Assert.Equal(before, await File.ReadAllBytesAsync(f.Envelope));
    }

    [Fact]
    public async Task Another_scope_cannot_read_or_export_the_maintenance_record()
    {
        using var f = new EvidenceFixture(); await f.Seed();
        var scope = f.Store.PrepareScopeOnboarding(f.Customer, "admin", "finance", "qa", "finance-admin", "Evidence fixture scope isolation");
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", scope.Id, scope.Digest); f.Store.ExecuteScopeOnboarding(f.Customer, "admin", scope.Id, scope.Digest);
        f.Store.SetMembership(f.Customer, "finance-admin", EvidenceFixture.AuditorActor, "finance", "qa", ["Auditor"]);
        var intent = await f.Prepare(); await f.Approve(intent); await f.Executor().ExecuteAsync(await f.Operator(), intent.Id, intent.Digest);
        var foreignReader = await f.Context(FoundationCapabilities.CredentialMaintenanceRead, "Auditor", IdentityTestIssuer.WorkerSubject, f.Store, "finance", "qa");
        Assert.Equal("MaintenanceScopeMismatch", Assert.Throws<FoundationStoreException>(() => f.Store.GetCredentialMaintenance(foreignReader, intent.Id)).Code);
        var foreign = f.Store.CreatePagedScopeExport(f.Customer, EvidenceFixture.AuditorActor, "finance", "qa", 3);
        var pages = ReadAll(f.Store, f.Customer, foreign, "finance", "qa"); VerifyCanonical(foreign, pages);
        Assert.DoesNotContain(AuditItems(pages), e => e.GetProperty("targetId").GetString() == intent.Id);
        using var other = new StoreFixture("customer-b", "cell-b");
        Assert.Equal("NotFound", Assert.Throws<FoundationStoreException>(() => other.Store.GetPagedScopeExport(other.Customer, "auditor", "default", "dev",
            foreign.Snapshot.Id, foreign.Snapshot.Sha256)).Code);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("guard")]
    [InlineData("history")]
    public async Task Repinned_v6_snapshot_with_missing_maintenance_schema_is_refused_before_restore(string mutation)
    {
        using var f = new EvidenceFixture(); await f.Seed(); await f.Prepare();
        var snapshot = f.Store.CreateBackup(f.Child("schema-snapshot.sqlite"));
        f.Sql(mutation switch { "table" => "DROP TABLE credential_maintenance_executions", "guard" => "DROP TRIGGER credential_maintenance_approvals_no_update",
            _ => "DROP TRIGGER schema_migrations_no_update; UPDATE schema_migrations SET checksum='invalid' WHERE version=6" }, snapshot.DatabasePath);
        var bytes = await File.ReadAllBytesAsync(snapshot.DatabasePath); var altered = snapshot with { Length = bytes.Length, Sha256 = Hash(bytes) };
        var target = f.Child("must-remain-absent.sqlite"); var envelope = await File.ReadAllBytesAsync(f.Envelope); f.Credentials.Keys.Requests.Clear();
        Assert.ThrowsAny<Exception>(() => FoundationStore.RestoreBackup(altered, target, f.Credentials.Clock));
        Assert.False(File.Exists(target)); Assert.Empty(f.Credentials.Keys.Requests); Assert.Equal(envelope, await File.ReadAllBytesAsync(f.Envelope));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(snapshot.DatabasePath));
    }

    [Fact]
    public async Task Recovery_audit_failure_preserves_source_custody_and_leaves_no_partially_restored_authority()
    {
        using var f = new EvidenceFixture(); await f.Seed(); var intent = await f.Prepare(); await f.Approve(intent);
        var snapshot = FoundationTestAuditCapacity.FillSnapshot(f.Store.CreateBackup(f.Child("failed-recovery-snapshot.sqlite")), f.Credentials.Clock);
        using var signingKey = RSA.Create(2048); var signed = FoundationTestAuditCapacity.Sign(snapshot, signingKey, f.Credentials.Clock);
        _ = FoundationStore.VerifySignedBackup(signed, FoundationBackupSignatures.Authority(signed.Manifest), signingKey, f.Customer, f.Credentials.Scope.CellId, f.Credentials.Clock);
        var backupBytes = await File.ReadAllBytesAsync(snapshot.DatabasePath);
        var bytes = await File.ReadAllBytesAsync(f.Envelope); var original = f.Store.GetCellInstanceIdentity(); var target = f.Child("failed-restore.sqlite");
        f.Credentials.Keys.Requests.Clear(); Assert.Equal("AuditCapacityExceeded", Assert.Throws<FoundationStoreException>(() => FoundationStore.RestoreBackup(snapshot, target, f.Credentials.Clock)).Code);
        Assert.False(File.Exists(target)); Assert.False(File.Exists(target + "-wal")); Assert.False(File.Exists(target + "-shm"));
        Assert.Equal(backupBytes, await File.ReadAllBytesAsync(snapshot.DatabasePath)); Assert.Equal(bytes, await File.ReadAllBytesAsync(f.Envelope));
        Assert.Empty(f.Credentials.Keys.Requests); Assert.Equal(original, f.Store.GetCellInstanceIdentity());
        Assert.Equal("Approved", f.Store.GetCredentialMaintenance(await f.Auditor(), intent.Id).State);
    }

    static void Fault(FoundationCredentialMaintenanceExecutor executor, string stage) =>
        typeof(FoundationCredentialMaintenanceExecutor).GetProperty("MaintenanceFault", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(executor, new Action<string>(phase => { if (phase == stage) throw new IOException("synthetic interruption"); }));
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static string? Action(JsonElement e) => e.GetProperty("action").GetString();
    static string? Outcome(JsonElement e) => e.GetProperty("outcome").GetString();
    static JsonElement[] AuditItems(IEnumerable<FoundationPagedExportPage> pages) => pages.SelectMany(p => p.Items).Where(i => i.Kind == "audit").Select(i => i.Metadata).ToArray();
    static List<FoundationPagedExportPage> ReadAll(FoundationStore store, string customer, FoundationPagedExportStart start, string project = "default", string environment = "dev")
    {
        var pages = new List<FoundationPagedExportPage> { start.FirstPage };
        while (pages[^1].NextCursor is { } cursor) pages.Add(store.ReadPagedScopeExportPage(customer, EvidenceFixture.AuditorActor, project, environment,
            start.Snapshot.Id, cursor, start.Snapshot.Sha256));
        Assert.Equal(start.Snapshot.PageCount, pages.Count); return pages;
    }
    static void VerifyCanonical(FoundationPagedExportStart start, IReadOnlyList<FoundationPagedExportPage> pages)
    {
        Assert.Equal(start.Snapshot.Sha256, Hash(Convert.FromBase64String(start.Snapshot.CanonicalManifestBase64)));
        for (var index = 0; index < pages.Count; index++)
        { Assert.Equal(pages[index].Sha256, Hash(Convert.FromBase64String(pages[index].CanonicalPageBase64))); Assert.Equal(start.Snapshot.PageSha256[index], pages[index].Sha256); }
    }

    sealed class EvidenceFixture : IDisposable
    {
        public const string OperatorActor = "user:" + IdentityTestIssuer.Tenant + ":" + IdentityTestIssuer.Subject;
        public const string ApproverActor = "user:" + IdentityTestIssuer.Tenant + ":" + IdentityTestIssuer.OtherSubject;
        public const string AuditorActor = "user:" + IdentityTestIssuer.Tenant + ":" + IdentityTestIssuer.WorkerSubject;
        public CredentialFixture Credentials { get; } = new();
        readonly IdentityTestIssuer issuer = new();
        public FoundationStore Store { get; }
        public CredentialVault Vault { get; private set; }
        public string Customer => Credentials.Scope.CustomerId;
        public string Database => Child("cell.db");
        public string Envelope => Assert.Single(Directory.GetFiles(Credentials.VaultRoot, "*.credential.json"));
        public EvidenceFixture()
        {
            Store = new(Database, Credentials.Scope.CellId, Customer, Credentials.Clock, initializeSchema: false); Vault = Credentials.Vault;
            foreach (var (actor, role) in new[] { (OperatorActor, "Operator"), (ApproverActor, "Approver"), (AuditorActor, "Auditor") })
                Store.SetMembership(Customer, "admin", actor, "default", "dev", [role]);
        }
        public async Task Seed() => await Credentials.StoreToken();
        public async Task<FoundationCredentialMaintenanceIntent> Prepare(CredentialMaintenanceOperation operation = CredentialMaintenanceOperation.Revoke)
        {
            if (operation == CredentialMaintenanceOperation.Rewrap)
            { Credentials.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32); Vault = new(Credentials.Options with { EncryptionKeyId = "key-2" }, Credentials.Keys, Credentials.Clock); }
            return Store.PrepareCredentialMaintenance(await Context(FoundationCapabilities.CredentialMaintenancePrepare, "Operator", IdentityTestIssuer.Subject), operation,
                await Vault.InspectMaintenanceAsync(Credentials.Scope.AuthProfileId), "Review synthetic profile maintenance evidence", Credentials.Clock.Now.AddMinutes(10));
        }
        public async Task Approve(FoundationCredentialMaintenanceIntent intent) => Store.ApproveCredentialMaintenance(
            await Context(FoundationCapabilities.CredentialMaintenanceApprove, "Approver", IdentityTestIssuer.OtherSubject), intent.Id, intent.Digest);
        public Task<CustomerContext> Operator(FoundationStore? store = null) => Context(FoundationCapabilities.CredentialMaintenanceExecute, "Operator", IdentityTestIssuer.Subject, store);
        public Task<CustomerContext> Auditor(FoundationStore? store = null) => Context(FoundationCapabilities.CredentialMaintenanceRead, "Auditor", IdentityTestIssuer.WorkerSubject, store);
        public async Task<CustomerContext> Context(string capability, string role, string subject, FoundationStore? store = null, string project = "default", string environment = "dev")
        {
            store ??= Store; var options = issuer.Options(Customer, Credentials.Scope.CellId); var claims = issuer.UserClaims(role, subject);
            claims["iat"] = claims["nbf"] = Credentials.Clock.Now.AddSeconds(-30).ToUnixTimeSeconds(); claims["exp"] = Credentials.Clock.Now.AddMinutes(10).ToUnixTimeSeconds();
            var principal = await new IdentityTokenValidator(options, Credentials.Clock).ValidateAsync(issuer.Token(claims));
            return new CustomerContextResolver(options, new Members(store), Credentials.Clock).Require(principal, Customer, project, environment, capability);
        }
        public FoundationCredentialMaintenanceExecutor Executor(FoundationStore? store = null)
        { store ??= Store; return new(store, Vault, Vault.DescribeNamespace(), store.GetCellInstanceIdentity().DatabaseInstanceId); }
        public FoundationPagedExportStart Export() => Store.CreatePagedScopeExport(Customer, AuditorActor, "default", "dev", 3);
        public string Child(string name) => Path.Combine(Credentials.Root, name);
        public void Sql(string sql, string? path = null) { using var db = Open(path); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public long Number(string sql, string? path = null) { using var db = Open(path); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        public string ApprovalBytes(string id, string? path = null)
        { using var db = Open(path); using var command = db.CreateCommand(); command.CommandText = "SELECT json_array(customer_id,intent_id,actor_key,actor_version,digest,created) FROM credential_maintenance_approvals WHERE intent_id=$id"; command.Parameters.AddWithValue("$id", id); return command.ExecuteScalar()?.ToString() ?? "absent"; }
        SqliteConnection Open(string? path) { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path ?? Database, Pooling = false }.ToString()); db.Open(); return db; }
        public void Dispose() { issuer.Dispose(); Credentials.Dispose(); }
        sealed class Members(FoundationStore store) : ICustomerMembershipSource
        { public IReadOnlyList<MembershipGrant> Find(string customerId, string actorKey) => store.GetCredentialMaintenanceMemberships(customerId, actorKey)
            .Select(m => new MembershipGrant(m.CustomerId, m.CellId, m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version, m.Enabled)).ToArray(); }
    }
}
