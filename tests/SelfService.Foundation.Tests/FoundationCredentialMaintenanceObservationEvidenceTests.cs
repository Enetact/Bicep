using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationCredentialMaintenanceObservationEvidenceTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(CredentialMaintenanceOperation.Revoke)]
    [InlineData(CredentialMaintenanceOperation.Rewrap)]
    public async Task Published_observation_exports_exact_metadata_without_resolving_or_repeating_original_execution(CredentialMaintenanceOperation operation)
    {
        using var f = new ObservationEvidenceFixture(); var original = await f.Unknown(operation: operation); var request = await f.Prepare(original); await f.Approve(request);
        f.Store.StoreArtifact(f.Customer, "requester", "default", "dev", "private.bicep", "private-observation-source-marker"u8.ToArray());
        f.Sql("UPDATE jobs SET receipt='private-observation-provider-marker'");
        using var envelope = JsonDocument.Parse(await File.ReadAllBytesAsync(f.Core.ProfilePath)); var cipher = envelope.RootElement.GetProperty("ciphertext").GetString()!;
        var execution = f.ExecutionBytes(); var custody = f.Core.VaultBytes(); var recorded = await f.Record(request);
        Assert.Equal("ObservedPublished", recorded.Receipt!.Status); Assert.Equal(request.Digest, recorded.Receipt.RequestDigest);
        Assert.Equal(request.ObservationSha256, recorded.Receipt.ObservationSha256);
        Assert.Equal(execution, f.ExecutionBytes()); Assert.Equal(custody, f.Core.VaultBytes());
        Assert.Equal("OutcomeUnknown", (await f.Core.Read(original.Id)).Execution!.State);
        Assert.Equal(request.ExpectedAfterSha256, f.Core.EnvelopeHash());
        var snapshot = f.Export(); var pages = Pages(f.Store, f.Customer, snapshot); Verify(snapshot, pages);
        var events = Audits(pages).Where(e => e.GetProperty("targetId").GetString() == request.Id).ToArray();
        Assert.Contains(events, e => e.GetProperty("action").GetString() == "CredentialMaintenanceObservedPublished");
        Assert.DoesNotContain(Audits(pages), e => e.GetProperty("action").GetString() == "CredentialMaintenanceCompleted");
        var text = string.Join('\n', pages.Select(p => Encoding.UTF8.GetString(Convert.FromBase64String(p.CanonicalPageBase64)))) + JsonSerializer.Serialize(recorded, Json);
        foreach (var privateValue in new[] { CredentialFixture.Token, cipher, Convert.ToBase64String(f.Core.Cell.Keys.Values["key-1"]),
            "private-observation-source-marker", "private-observation-provider-marker", "customer-org", "customer-repo" }) Assert.DoesNotContain(privateValue, text);
        Assert.DoesNotContain("NoEffect", text); Assert.True(f.Store.VerifyAuditIntegrity(f.Customer, MaintenanceExecutorFixture.Auditor));
    }

    [Fact]
    public async Task Prior_snapshot_and_original_unknown_remain_unchanged_after_observation_receipt_is_recorded()
    {
        using var f = new ObservationEvidenceFixture(); var original = await f.Unknown(); var request = await f.Prepare(original); await f.Approve(request);
        var old = f.Export(); var execution = f.ExecutionBytes(); await f.Record(request);
        var frozen = Pages(f.Store, f.Customer, old); Verify(old, frozen);
        Assert.DoesNotContain(Audits(frozen), e => e.GetProperty("action").GetString() == "CredentialMaintenanceObservedPublished");
        var reread = f.Store.ReadPagedScopeExportPage(f.Customer, MaintenanceExecutorFixture.Auditor, "default", "dev", old.Snapshot.Id, old.FirstCursor, old.Snapshot.Sha256);
        Assert.Equal(old.FirstPage.Sha256, reread.Sha256);
        var current = f.Export(); var pages = Pages(f.Store, f.Customer, current); Verify(current, pages);
        Assert.Contains(Audits(pages), e => e.GetProperty("action").GetString() == "CredentialMaintenanceObservedPublished");
        Assert.Equal(execution, f.ExecutionBytes());
    }

    [Theory]
    [InlineData("Prepared")]
    [InlineData("Approved")]
    [InlineData("Recorded")]
    public async Task Repeated_restore_quarantines_unfinished_reviews_and_retains_receipts_and_original_unknown_bytes(string stage)
    {
        using var f = new ObservationEvidenceFixture(); var original = await f.Unknown(); var request = await f.Prepare(original);
        if (stage != "Prepared") await f.Approve(request); if (stage == "Recorded") await f.Record(request);
        var history = await f.Read(request.Id); var execution = f.ExecutionBytes(); var receipt = f.ReceiptBytes(); var approval = f.ApprovalBytes(); var custody = f.Core.VaultBytes();
        using var signer = RSA.Create(2048); var anchor = f.Store.CreateAuditAnchor(f.Customer, MaintenanceExecutorFixture.Auditor, signer, "observation-prefix");
        var sourceInstance = f.Store.GetCellInstanceIdentity(); var snapshot = f.Store.CreateBackup(f.Child("first-snapshot.sqlite")); f.Core.Cell.Keys.Requests.Clear();
        var firstPath = f.Child("first-restored.sqlite"); var first = FoundationStore.RestoreBackup(snapshot, firstPath, f.Core.Cell.Clock);
        var firstInstance = first.GetCellInstanceIdentity(); Assert.NotEqual(sourceInstance.DatabaseInstanceId, firstInstance.DatabaseInstanceId);
        Assert.Empty(f.Core.Cell.Keys.Requests); Assert.Equal(custody, f.Core.VaultBytes());
        Assert.Equal(execution, f.ExecutionBytes(firstPath)); Assert.Equal(receipt, f.ReceiptBytes(firstPath)); Assert.Equal(approval, f.ApprovalBytes(firstPath));
        var restoredRequest = first.GetCredentialMaintenanceObservation(await f.Context(FoundationCapabilities.CredentialMaintenanceRead, first), request.Id);
        Assert.Equal(stage == "Recorded" ? "Approved" : "Quarantined", restoredRequest.State); Assert.Equal(history.Receipt, restoredRequest.Receipt);
        Assert.Equal("OutcomeUnknown", first.GetCredentialMaintenance(await f.Context(FoundationCapabilities.CredentialMaintenanceRead, first), original.Id).Execution!.State);
        Assert.True(first.VerifyAuditAnchor(f.Customer, MaintenanceExecutorFixture.Auditor, anchor, signer, "observation-prefix"));
        var holds = first.ListAudit(f.Customer, MaintenanceExecutorFixture.Auditor).Where(e => e.Action == "RecoveredCredentialMaintenanceObservationHeld" && e.TargetId == request.Id).ToArray();
        if (stage == "Recorded") Assert.Empty(holds); else { var held = Assert.Single(holds); Assert.Equal("OutcomeUnknown", held.Outcome); Assert.Equal(request.Digest, held.CorrelationId); }
        var reader = await f.Context(FoundationCapabilities.CredentialMaintenanceExecute, first);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Recorder(first).RecordAsync(reader, request.Id, request.Digest));
        Assert.Equal(receipt, f.ReceiptBytes(firstPath)); Assert.Equal(execution, f.ExecutionBytes(firstPath)); Assert.Equal(custody, f.Core.VaultBytes());
        var secondSnapshot = first.CreateBackup(f.Child("second-snapshot.sqlite")); f.Core.Cell.Keys.Requests.Clear();
        var secondPath = f.Child("second-restored.sqlite"); var second = FoundationStore.RestoreBackup(secondSnapshot, secondPath, f.Core.Cell.Clock);
        Assert.NotEqual(firstInstance.DatabaseInstanceId, second.GetCellInstanceIdentity().DatabaseInstanceId);
        Assert.Empty(f.Core.Cell.Keys.Requests); Assert.Equal(custody, f.Core.VaultBytes());
        Assert.Equal(receipt, f.ReceiptBytes(secondPath)); Assert.Equal(execution, f.ExecutionBytes(secondPath));
        var afterSecond = second.GetCredentialMaintenanceObservation(await f.Context(FoundationCapabilities.CredentialMaintenanceRead, second), request.Id);
        Assert.Equal(restoredRequest.State, afterSecond.State); Assert.Equal(restoredRequest.Receipt, afterSecond.Receipt);
        Assert.Equal(holds.Length, second.ListAudit(f.Customer, MaintenanceExecutorFixture.Auditor).Count(e => e.Action == "RecoveredCredentialMaintenanceObservationHeld" && e.TargetId == request.Id));
        var export = second.CreatePagedScopeExport(f.Customer, MaintenanceExecutorFixture.Auditor, "default", "dev", 3); Verify(export, Pages(second, f.Customer, export));
        Assert.Equal(history.State, (await f.Read(request.Id)).State); Assert.Equal(sourceInstance, f.Store.GetCellInstanceIdentity());
    }

    [Fact]
    public async Task Fresh_post_restore_review_binds_current_instance_and_policy_without_upgrading_old_unknown()
    {
        using var f = new ObservationEvidenceFixture(); var original = await f.Unknown(); var old = await f.Prepare(original); await f.Approve(old);
        var snapshot = f.Store.CreateBackup(f.Child("fresh-snapshot.sqlite")); var target = f.Child("fresh-restored.sqlite"); var restored = FoundationStore.RestoreBackup(snapshot, target, f.Core.Cell.Clock);
        var execution = f.ExecutionBytes(target); var custody = f.Core.VaultBytes();
        var fresh = await f.Recorder(restored).PrepareAsync(await f.Context(FoundationCapabilities.CredentialMaintenancePrepare, restored), original.Id,
            "Fresh exact restored observation review", f.Core.Cell.Clock.Now.AddMinutes(10));
        Assert.Equal(restored.GetCellInstanceIdentity().DatabaseInstanceId, fresh.DatabaseInstanceId); Assert.NotEqual(old.DatabaseInstanceId, fresh.DatabaseInstanceId);
        Assert.Equal(old.PolicyVersion + 1, fresh.PolicyVersion); Assert.NotEqual(old.Id, fresh.Id);
        restored.ApproveCredentialMaintenanceObservation(await f.Context(FoundationCapabilities.CredentialMaintenanceApprove, restored), fresh.Id, fresh.Digest);
        var result = await f.Recorder(restored).RecordAsync(await f.Context(FoundationCapabilities.CredentialMaintenanceExecute, restored), fresh.Id, fresh.Digest);
        Assert.Equal("ObservedPublished", result.Receipt!.Status); Assert.Equal(execution, f.ExecutionBytes(target)); Assert.Equal(custody, f.Core.VaultBytes());
        Assert.Equal("OutcomeUnknown", restored.GetCredentialMaintenance(await f.Context(FoundationCapabilities.CredentialMaintenanceRead, restored), original.Id).Execution!.State);
        Assert.Equal("Quarantined", restored.GetCredentialMaintenanceObservation(await f.Context(FoundationCapabilities.CredentialMaintenanceRead, restored), old.Id).State);
    }

    [Fact]
    public async Task Copied_approval_cannot_record_even_if_quarantine_is_forged_back_and_current_candidate_matches()
    {
        using var f = new ObservationEvidenceFixture(); var original = await f.Unknown(); var request = await f.Prepare(original); await f.Approve(request);
        var snapshot = f.Store.CreateBackup(f.Child("copied-snapshot.sqlite")); var target = f.Child("copied-restored.sqlite"); var restored = FoundationStore.RestoreBackup(snapshot, target, f.Core.Cell.Clock);
        f.Sql("UPDATE credential_maintenance_observation_requests SET state='Approved'", target); // Test-only privileged corruption, not a recovery API.
        var custody = f.Core.VaultBytes(); var executor = await f.Context(FoundationCapabilities.CredentialMaintenanceExecute, restored);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Recorder(restored).RecordAsync(executor, request.Id, request.Digest));
        Assert.Equal("[]", f.ReceiptBytes(target)); Assert.Equal(custody, f.Core.VaultBytes()); Assert.Equal("OutcomeUnknown", (await f.Core.Read(original.Id)).Execution!.State);
    }

    [Fact]
    public async Task Current_role_scope_and_inactive_history_checks_apply_without_reviving_old_pages()
    {
        using var f = new ObservationEvidenceFixture(); var original = await f.Unknown(); var request = await f.Prepare(original); await f.Approve(request); await f.Record(request);
        f.Store.SetCustomerEnabled(f.Customer, "admin", false); var reader = await f.Context(FoundationCapabilities.CredentialMaintenanceRead); var snapshot = f.Export();
        Assert.Equal("ObservedPublished", f.Store.GetCredentialMaintenanceObservation(reader, request.Id).Receipt!.Status);
        Verify(snapshot, Pages(f.Store, f.Customer, snapshot));
        f.Store.SetCustomerEnabled(f.Customer, "admin", true); snapshot = f.Export();
        f.Store.SetMembership(f.Customer, "admin", MaintenanceExecutorFixture.Auditor, "default", "dev", ["Viewer"]);
        Assert.Throws<FoundationStoreException>(() => f.Store.GetCredentialMaintenanceObservation(reader, request.Id));
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.ReadPagedScopeExportPage(f.Customer, MaintenanceExecutorFixture.Auditor,
            "default", "dev", snapshot.Snapshot.Id, snapshot.FirstCursor, snapshot.Snapshot.Sha256)).Code);
        f.Store.SetMembership(f.Customer, "admin", MaintenanceExecutorFixture.Auditor, "default", "dev", ["Auditor"]);
        Assert.Throws<FoundationStoreException>(() => f.Store.GetCredentialMaintenanceObservation(reader, request.Id));
        Assert.Equal("ObservedPublished", (await f.Read(request.Id)).Receipt!.Status);
        Assert.Equal("ExportAuthorityChanged", Assert.Throws<FoundationStoreException>(() => f.Store.ReadPagedScopeExportPage(f.Customer, MaintenanceExecutorFixture.Auditor,
            "default", "dev", snapshot.Snapshot.Id, snapshot.FirstCursor, snapshot.Snapshot.Sha256)).Code);
    }

    [Fact]
    public async Task Another_application_scope_cannot_read_observation_history_or_export_its_events()
    {
        using var f = new ObservationEvidenceFixture(); var original = await f.Unknown();
        var scope = f.Store.PrepareScopeOnboarding(f.Customer, "admin", "finance", "qa", "finance-admin", "Synthetic observation evidence isolation");
        f.Store.ApproveAdministrativePlan(f.Customer, "approver", scope.Id, scope.Digest); f.Store.ExecuteScopeOnboarding(f.Customer, "admin", scope.Id, scope.Digest);
        f.Store.SetMembership(f.Customer, "finance-admin", MaintenanceExecutorFixture.Auditor, "finance", "qa", ["Auditor"]);
        var request = await f.Prepare(original); await f.Approve(request); await f.Record(request);
        var reader = await f.Context(FoundationCapabilities.CredentialMaintenanceRead, project: "finance", environment: "qa");
        Assert.Throws<FoundationStoreException>(() => f.Store.GetCredentialMaintenanceObservation(reader, request.Id));
        var export = f.Store.CreatePagedScopeExport(f.Customer, MaintenanceExecutorFixture.Auditor, "finance", "qa", 3);
        var pages = Pages(f.Store, f.Customer, export, "finance", "qa"); Verify(export, pages);
        Assert.DoesNotContain(Audits(pages), e => e.GetProperty("targetId").GetString() == request.Id);
        using var other = new StoreFixture("customer-b", "cell-b");
        Assert.Equal("NotFound", Assert.Throws<FoundationStoreException>(() => other.Store.GetPagedScopeExport(other.Customer, "auditor", "default", "dev", export.Snapshot.Id, export.Snapshot.Sha256)).Code);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("index")]
    [InlineData("guard")]
    [InlineData("history")]
    public async Task Repinned_snapshot_with_invalid_v7_observation_schema_is_refused_before_restore(string mutation)
    {
        using var f = new ObservationEvidenceFixture(); var original = await f.Unknown(); await f.Prepare(original);
        var snapshot = f.Store.CreateBackup(f.Child("schema-snapshot.sqlite"));
        if (mutation == "table") f.Sql("DROP TABLE credential_maintenance_observation_receipts", snapshot.DatabasePath);
        else if (mutation == "history") f.Sql("DROP TRIGGER schema_migrations_no_update; UPDATE schema_migrations SET checksum='invalid' WHERE version=7", snapshot.DatabasePath);
        else
        {
            var kind = mutation == "index" ? "index" : "trigger";
            var name = f.ScalarText("SELECT name FROM sqlite_master WHERE type='" + kind + "' AND name LIKE 'credential_maintenance_observation_%' ORDER BY name LIMIT 1", snapshot.DatabasePath);
            Assert.False(string.IsNullOrEmpty(name)); f.Sql("DROP " + kind + " " + name, snapshot.DatabasePath);
        }
        var bytes = await File.ReadAllBytesAsync(snapshot.DatabasePath); var altered = snapshot with { Sha256 = Hash(bytes), Length = bytes.Length };
        var custody = f.Core.VaultBytes(); var target = f.Child("invalid-restore.sqlite"); f.Core.Cell.Keys.Requests.Clear();
        Assert.ThrowsAny<Exception>(() => FoundationStore.RestoreBackup(altered, target, f.Core.Cell.Clock));
        Assert.False(File.Exists(target)); Assert.Equal(bytes, await File.ReadAllBytesAsync(snapshot.DatabasePath)); Assert.Empty(f.Core.Cell.Keys.Requests); Assert.Equal(custody, f.Core.VaultBytes());
    }

    [Fact]
    public async Task Recovery_audit_failure_leaves_no_partial_review_authority_and_preserves_all_source_state()
    {
        using var f = new ObservationEvidenceFixture(); var original = await f.Unknown(); var request = await f.Prepare(original); await f.Approve(request);
        // Permit the earlier v6 recovery event, then exhaust capacity at the v7 observation's required audit.
        var snapshot = FoundationTestAuditCapacity.FillSnapshot(f.Store.CreateBackup(f.Child("audit-snapshot.sqlite")), f.Core.Cell.Clock, availableAuditEvents: 1);
        using var signingKey = RSA.Create(2048); var signed = FoundationTestAuditCapacity.Sign(snapshot, signingKey, f.Core.Cell.Clock);
        _ = FoundationStore.VerifySignedBackup(signed, FoundationBackupSignatures.Authority(signed.Manifest), signingKey, f.Customer, f.Core.Cell.Scope.CellId, f.Core.Cell.Clock);
        var bytes = await File.ReadAllBytesAsync(snapshot.DatabasePath); var execution = f.ExecutionBytes();
        var custody = f.Core.VaultBytes(); var instance = f.Store.GetCellInstanceIdentity(); var target = f.Child("audit-failed.sqlite"); f.Core.Cell.Keys.Requests.Clear();
        Assert.Equal("AuditCapacityExceeded", Assert.Throws<FoundationStoreException>(() => FoundationStore.RestoreBackup(snapshot, target, f.Core.Cell.Clock)).Code);
        Assert.False(File.Exists(target)); Assert.False(File.Exists(target + "-wal")); Assert.False(File.Exists(target + "-shm"));
        Assert.Equal(bytes, await File.ReadAllBytesAsync(snapshot.DatabasePath)); Assert.Equal(execution, f.ExecutionBytes()); Assert.Equal(instance, f.Store.GetCellInstanceIdentity());
        Assert.Equal(custody, f.Core.VaultBytes()); Assert.Empty(f.Core.Cell.Keys.Requests); Assert.Equal("Approved", (await f.Read(request.Id)).State);
    }

    [Fact]
    public async Task Mismatching_current_bytes_never_create_a_no_effect_or_success_receipt()
    {
        using var f = new ObservationEvidenceFixture(); var original = await f.Unknown("AfterAdmission"); var execution = f.ExecutionBytes(); var custody = f.Core.VaultBytes();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Prepare(original));
        Assert.Equal("[]", f.ReceiptBytes()); Assert.Equal(execution, f.ExecutionBytes()); Assert.Equal(custody, f.Core.VaultBytes());
        Assert.Equal("OutcomeUnknown", (await f.Core.Read(original.Id)).Execution!.State);
    }

    [Fact]
    public async Task Pending_strong_authority_is_not_bypassed_by_observation_or_consulted_during_restore()
    {
        using var f = new ObservationEvidenceFixture(strong: true); await f.Core.Initialize(); var plan = await f.Core.Plan();
        typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Core.Vault,
            new Action<string>(phase => { if (phase == "EnvelopePublished") throw new IOException("synthetic pending authority"); }));
        var original = await f.Core.Execute(plan); Assert.Equal("OutcomeUnknown", original.Execution!.State);
        var execution = f.ExecutionBytes(); var custody = f.Core.VaultBytes();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Prepare(original)); Assert.Equal("[]", f.ReceiptBytes()); Assert.Equal(custody, f.Core.VaultBytes());
        var snapshot = f.Store.CreateBackup(f.Child("pending-snapshot.sqlite")); f.Core.Cell.Keys.Requests.Clear(); var target = f.Child("pending-restored.sqlite");
        var restored = FoundationStore.RestoreBackup(snapshot, target, f.Core.Cell.Clock);
        Assert.Empty(f.Core.Cell.Keys.Requests); Assert.Equal(execution, f.ExecutionBytes(target)); Assert.Equal(custody, f.Core.VaultBytes());
        Assert.Equal("OutcomeUnknown", restored.GetCredentialMaintenance(await f.Context(FoundationCapabilities.CredentialMaintenanceRead, restored), original.Id).Execution!.State);
    }

    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static JsonElement[] Audits(IEnumerable<FoundationPagedExportPage> pages) => pages.SelectMany(p => p.Items).Where(i => i.Kind == "audit").Select(i => i.Metadata).ToArray();
    static List<FoundationPagedExportPage> Pages(FoundationStore store, string customer, FoundationPagedExportStart start, string project = "default", string environment = "dev")
    {
        var pages = new List<FoundationPagedExportPage> { start.FirstPage };
        while (pages[^1].NextCursor is { } cursor) pages.Add(store.ReadPagedScopeExportPage(customer, MaintenanceExecutorFixture.Auditor, project, environment, start.Snapshot.Id, cursor, start.Snapshot.Sha256));
        Assert.Equal(start.Snapshot.PageCount, pages.Count); return pages;
    }
    static void Verify(FoundationPagedExportStart start, IReadOnlyList<FoundationPagedExportPage> pages)
    { Assert.Equal(start.Snapshot.Sha256, Hash(Convert.FromBase64String(start.Snapshot.CanonicalManifestBase64))); for (var i = 0; i < pages.Count; i++) { Assert.Equal(pages[i].Sha256, Hash(Convert.FromBase64String(pages[i].CanonicalPageBase64))); Assert.Equal(start.Snapshot.PageSha256[i], pages[i].Sha256); } }

    sealed class ObservationEvidenceFixture(bool strong = false) : IDisposable
    {
        public MaintenanceExecutorFixture Core { get; } = new(strong);
        readonly IdentityTestIssuer issuer = new();
        public FoundationStore Store => Core.Store;
        public string Customer => Core.Cell.Scope.CustomerId;
        public string Child(string name) => Path.Combine(Core.Cell.Root, name);
        public async Task<FoundationCredentialMaintenanceIntent> Unknown(string phase = "AfterMutation", CredentialMaintenanceOperation operation = CredentialMaintenanceOperation.Rewrap)
        {
            await Core.Initialize(); var plan = await Core.Plan(operation); var executor = Core.Executor();
            typeof(FoundationCredentialMaintenanceExecutor).GetProperty("MaintenanceFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(executor,
                new Action<string>(stage => { if (stage == phase) throw new IOException("synthetic unknown outcome"); }));
            var result = await executor.ExecuteAsync(await Core.Context(FoundationCapabilities.CredentialMaintenanceExecute), plan.Id, plan.Digest); Assert.Equal("OutcomeUnknown", result.Execution!.State); return result;
        }
        public FoundationCredentialMaintenanceObservationRecorder Recorder(FoundationStore? store = null)
        { store ??= Store; return new(store, Core.Vault, Core.Vault.DescribeNamespace(), store.GetCellInstanceIdentity().DatabaseInstanceId); }
        public async Task<FoundationCredentialMaintenanceObservationRequest> Prepare(FoundationCredentialMaintenanceIntent original) => await Recorder().PrepareAsync(
            await Context(FoundationCapabilities.CredentialMaintenancePrepare), original.Id, "Review exact current published evidence", Core.Cell.Clock.Now.AddMinutes(10));
        public async Task Approve(FoundationCredentialMaintenanceObservationRequest request) => Store.ApproveCredentialMaintenanceObservation(await Context(FoundationCapabilities.CredentialMaintenanceApprove), request.Id, request.Digest);
        public async Task<FoundationCredentialMaintenanceObservationRequest> Record(FoundationCredentialMaintenanceObservationRequest request) => await Recorder().RecordAsync(await Context(FoundationCapabilities.CredentialMaintenanceExecute), request.Id, request.Digest);
        public async Task<FoundationCredentialMaintenanceObservationRequest> Read(string id) => Store.GetCredentialMaintenanceObservation(await Context(FoundationCapabilities.CredentialMaintenanceRead), id);
        public async Task<CustomerContext> Context(string capability, FoundationStore? store = null, string project = "default", string environment = "dev")
        {
            store ??= Store; var role = capability == FoundationCapabilities.CredentialMaintenanceApprove ? "Approver" : capability == FoundationCapabilities.CredentialMaintenanceRead ? "Auditor" : "Operator";
            var subject = role == "Approver" ? IdentityTestIssuer.OtherSubject : role == "Auditor" ? IdentityTestIssuer.WorkerSubject : IdentityTestIssuer.Subject;
            var options = issuer.Options(Customer, Core.Cell.Scope.CellId); var claims = issuer.UserClaims(role, subject);
            claims["iat"] = claims["nbf"] = Core.Cell.Clock.Now.AddSeconds(-30).ToUnixTimeSeconds(); claims["exp"] = Core.Cell.Clock.Now.AddMinutes(10).ToUnixTimeSeconds();
            var principal = await new IdentityTokenValidator(options, Core.Cell.Clock).ValidateAsync(issuer.Token(claims));
            return new CustomerContextResolver(options, new Members(store), Core.Cell.Clock).Require(principal, Customer, project, environment, capability);
        }
        public FoundationPagedExportStart Export() => Store.CreatePagedScopeExport(Customer, MaintenanceExecutorFixture.Auditor, "default", "dev", 3);
        public string ExecutionBytes(string? path = null) => RowsJson("credential_maintenance_executions", path);
        public string ReceiptBytes(string? path = null) => RowsJson("credential_maintenance_observation_receipts", path);
        public string ApprovalBytes(string? path = null) => RowsJson("credential_maintenance_observation_approvals", path);
        string RowsJson(string table, string? path) { using var db = Open(path); using var command = db.CreateCommand(); command.CommandText = "SELECT * FROM " + table + " ORDER BY 1,2"; using var reader = command.ExecuteReader(); var rows = new List<object[]>(); while (reader.Read()) { var row = new object[reader.FieldCount]; reader.GetValues(row); rows.Add(row); } return JsonSerializer.Serialize(rows, Json); }
        public string ScalarText(string sql, string? path = null) { using var db = Open(path); using var command = db.CreateCommand(); command.CommandText = sql; return command.ExecuteScalar()?.ToString() ?? ""; }
        public void Sql(string sql, string? path = null) { using var db = Open(path); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        SqliteConnection Open(string? path) { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path ?? Child("cell.db"), Pooling = false }.ToString()); db.Open(); return db; }
        public void Dispose() { issuer.Dispose(); Core.Dispose(); }
        sealed class Members(FoundationStore store) : ICustomerMembershipSource
        { public IReadOnlyList<MembershipGrant> Find(string customerId, string actorKey) => store.GetCredentialMaintenanceMemberships(customerId, actorKey).Select(m => new MembershipGrant(m.CustomerId, m.CellId, m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version, m.Enabled)).ToArray(); }
    }
}
