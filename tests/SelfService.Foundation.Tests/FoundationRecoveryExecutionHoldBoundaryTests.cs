using System.Net;
using System.Reflection;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationRecoveryExecutionHoldBoundaryTests
{
    const string Held = "RecoveryExecutionHeld";
    const string Purpose = "Reviewed synthetic restore-origin execution hold boundary";

    [Theory]
    [InlineData(PipelineRequestFactory.GitHub)]
    [InlineData(PipelineRequestFactory.AzureDevOps)]
    public async Task Active_owner_activated_restored_cell_denies_fresh_approved_work_and_current_credential_use(string provider)
    {
        using var f = new HeldCell(provider); await f.Cell.StoreToken();
        var authority = FoundationCellActivationAuthority.Provision(f.Path("activation"), f.Customer, f.CellId, f.Cell.Clock);
        var old = authority.Activate(f.SourcePath, f.Source.GetCellInstanceIdentity().DatabaseInstanceId, 0, null);
        var current = authority.Activate(f.RestoredPath, f.Restored.GetCellInstanceIdentity().DatabaseInstanceId, old.Generation, old.Sha256);
        using var lease = authority.AcquireLease(f.RestoredPath, current.DatabaseInstanceId);
        var guarded = new FoundationStore(f.RestoredPath, f.CellId, f.Customer, f.Cell.Clock, initializeSchema: false, activationGuard: lease.AssertCurrent);
        Assert.True(guarded.CustomerLifecycle(f.Customer, "admin").Enabled);
        guarded.SetMembership(f.Customer, "admin", "operations", "default", "dev", ["PlatformOperator"]);
        var operational = guarded.OperationalStatus(f.Customer, "operations", "default", "dev");
        Assert.True(operational.RecoveryExecutionHeld); Assert.Equal("RecoveryRequiresReconciliation", operational.ExecutionHoldReason);
        Assert.True(operational.Enabled); Assert.Equal("Active", operational.Lifecycle); Assert.True(guarded.StorageReady());
        var operationalJson = JsonSerializer.Serialize(operational);
        foreach (var privateMetadata in new[] { f.SourcePath, f.RestoredPath, f.Backup.Sha256, current.DatabaseInstanceId,
            f.Source.GetCellInstanceIdentity().DatabaseInstanceId, f.Cell.VaultRoot, CredentialFixture.Token, "BackupSha256", "SourceDatabaseInstanceId", "NamespaceCommitmentSha256" })
            Assert.DoesNotContain(privateMetadata, operationalJson);
        var prepared = f.NewPlan(guarded); var jobs = f.JobRows();
        Denied(() => guarded.EnqueuePlan(f.Customer, "operator", prepared.Id, prepared.Digest, "new-after-explicit-activation"));
        Denied(() => guarded.ClaimNext(f.Customer, "fresh-worker", TimeSpan.FromMinutes(1)));
        Denied(() => guarded.StartDispatch(f.Customer, f.Cell.Dispatch.Job.Id, "credential-worker", f.Cell.Dispatch.Job.Fence));
        var keyReads = f.Cell.Keys.Requests.Count; using var handler = new Posts();
        using (var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Cell.Vault, f.Permits(guarded), handler, f.Cell.Clock))
            Assert.Equal("pipeline-credential-authority-denied", (await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(f.Cell.Wire, default))).ReasonCode);
        Assert.Equal(keyReads, f.Cell.Keys.Requests.Count); Assert.Equal(0, handler.Calls); Assert.Equal(jobs, f.JobRows());
        lease.AssertCurrent();
        // The same authentic credential remains usable in its original, unheld source. A bad key cannot explain the denial.
        using var sourceHandler = new Posts(); using var sourceTransport = PipelineAuthenticatedTransport.ForMockHandler(f.Cell.Vault, f.Permits(f.Source), sourceHandler, f.Cell.Clock);
        Assert.Equal(200, (await sourceTransport.SendAsync(f.Cell.Wire, default)).StatusCode); Assert.Equal(1, sourceHandler.Calls);
    }

    [Theory]
    [InlineData("claim")]
    [InlineData("dispatch")]
    [InlineData("mock-dispatch")]
    [InlineData("renew")]
    [InlineData("complete")]
    public void Each_direct_worker_execution_cutoff_refuses_before_mutating_restored_job_history(string operation)
    {
        using var f = new HeldCell(); var rows = f.JobRows(); var audit = f.Number("SELECT count(*) FROM audit");
        var job = f.Cell.Dispatch.Job;
        Denied(() =>
        {
            switch (operation)
            {
                case "claim": f.Restored.ClaimNext(f.Customer, "new-worker", TimeSpan.FromMinutes(1)); break;
                case "dispatch": f.Restored.StartDispatch(f.Customer, job.Id, job.WorkerId!, job.Fence); break;
                case "mock-dispatch": f.Restored.StartMockDispatch(f.Customer, job.Id, job.WorkerId!, job.Fence, new([], f.Cell.Clock), "1.0.0"); break;
                case "renew": f.Restored.RenewLease(f.Customer, job.Id, job.WorkerId!, job.Fence, TimeSpan.FromMinutes(1)); break;
                case "complete": f.Restored.CompleteJob(f.Customer, job.Id, job.WorkerId!, job.Fence, "{\"simulated\":true}"); break;
                default: throw new InvalidOperationException();
            }
        });
        Assert.Equal(rows, f.JobRows()); Assert.Equal(audit, f.Number("SELECT count(*) FROM audit"));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Neither_readonly_job_label_nor_idempotent_old_submission_bypasses_whole_cell_hold(bool changes)
    {
        using var f = new HeldCell(); var plan = f.NewPlan(f.Restored, changes); var before = f.JobRows();
        Denied(() => f.Restored.EnqueuePlan(f.Customer, "operator", plan.Id, plan.Digest, "fresh-work"));
        Denied(() => f.Restored.EnqueuePlan(f.Customer, "operator", f.Cell.Dispatch.Plan.Id, f.Cell.Dispatch.Plan.Digest, "fixture-request"));
        Assert.Equal(before, f.JobRows()); Assert.Equal(1, f.Number("SELECT count(*) FROM jobs"));
    }

    [Theory]
    [InlineData("LocalPreview")]
    [InlineData("Licensed")]
    public async Task Fresh_roles_active_lifecycle_and_reviewed_entitlements_do_not_release_restore_hold(string mode)
    {
        using var f = new HeldCell(); using var key = RSA.Create(2048);
        foreach (var (actor, role) in new[] { ("requester", "Requester"), ("approver", "Approver"), ("operator", "Operator") })
            f.Restored.SetMembership(f.Customer, "admin", actor, "default", "dev", [role]);
        f.Restored.SetCustomerEnabled(f.Customer, "admin", false); f.Restored.SetCustomerEnabled(f.Customer, "admin", true);
        var document = new EntitlementDocument(EntitlementVerifier.DocumentSchema, "fixture-issuer", "held-cell-license", f.Customer, f.CellId,
            1, "evaluation", FoundationFeatures.Available.ToArray(), f.Cell.Clock.Now.AddMinutes(-1), f.Cell.Clock.Now.AddHours(1), "fixture-key");
        var signed = new SignedEntitlement(document, Convert.ToBase64String(key.SignData(EntitlementVerifier.CanonicalDocument(document), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
        var verifier = new EntitlementVerifier([new("fixture-issuer", "fixture-key", key.ExportSubjectPublicKeyInfoPem())], f.Cell.Clock);
        var configuration = new FoundationFeatureConfiguration(mode, FoundationFeatures.Available.ToArray(), 100, "0.0.0", "2.0.0", mode == "Licensed" ? signed : null);
        var config = f.Restored.PrepareConfigurationChange(f.Customer, "admin", "default", "dev", configuration, 0, Purpose, verifier, "1.0.0");
        f.Restored.ApproveAdministrativePlan(f.Customer, "approver", config.Id, config.Digest);
        f.Restored.ExecuteConfigurationChange(f.Customer, "admin", config.Id, config.Digest, verifier, "1.0.0");
        var plan = f.NewPlan(f.Restored); Denied(() => f.Restored.EnqueuePlan(f.Customer, "operator", plan.Id, plan.Digest, "reconfigured"));
        Assert.True(f.Restored.CustomerLifecycle(f.Customer, "admin").Enabled);
        var status = f.Restored.GetRecoveryExecutionHold(await f.Context(FoundationCapabilities.RecoveryRead, "Operator"));
        Assert.True(status.ExecutionHeld); Assert.False(status.ReleaseAvailable);
    }

    [Theory]
    [InlineData(CredentialMaintenanceOperation.Revoke)]
    [InlineData(CredentialMaintenanceOperation.Rewrap)]
    public async Task Fresh_current_instance_maintenance_approval_is_denied_before_key_or_vault_access(CredentialMaintenanceOperation operation)
    {
        using var f = new MaintenanceExecutorFixture(true); await f.Initialize();
        var snapshot = f.Store.CreateBackup(Path.Combine(f.Cell.Root, "hold-maintenance-backup.sqlite"));
        var path = Path.Combine(f.Cell.Root, "hold-maintenance-restored.sqlite");
        var restored = FoundationStore.RestoreBackup(snapshot, path, f.Cell.Clock);
        var target = await f.Vault.InspectMaintenanceAsync(f.Cell.Scope.AuthProfileId);
        var prepared = restored.PrepareCredentialMaintenance(await f.Context(FoundationCapabilities.CredentialMaintenancePrepare), operation, target, Purpose, f.Cell.Clock.Now.AddMinutes(5));
        var approved = restored.ApproveCredentialMaintenance(await f.Context(FoundationCapabilities.CredentialMaintenanceApprove), prepared.Id, prepared.Digest);
        Assert.Equal(restored.GetCellInstanceIdentity().DatabaseInstanceId, approved.DatabaseInstanceId);
        var bytes = f.VaultBytes(); var keyReads = f.Cell.Keys.Requests.Count;
        var executor = new FoundationCredentialMaintenanceExecutor(restored, f.Vault, f.Vault.DescribeNamespace(), approved.DatabaseInstanceId);
        var context = await f.Context(FoundationCapabilities.CredentialMaintenanceExecute);
        var error = await Assert.ThrowsAsync<FoundationStoreException>(() => executor.ExecuteAsync(context, approved.Id, approved.Digest));
        Assert.Equal(Held, error.Code); Assert.Equal(403, error.Status);
        Assert.Equal(bytes, f.VaultBytes()); Assert.Equal(keyReads, f.Cell.Keys.Requests.Count);
        Assert.Equal(0, Number(path, "SELECT count(*) FROM credential_maintenance_executions"));
        Assert.Null(restored.GetCredentialMaintenance(await f.Context(FoundationCapabilities.CredentialMaintenanceRead), approved.Id).Execution);
        Assert.Equal("Approved", restored.GetCredentialMaintenance(await f.Context(FoundationCapabilities.CredentialMaintenanceRead), approved.Id).State);
    }

    [Fact]
    public async Task Hold_does_not_cross_customer_boundaries_or_replace_status_authorization()
    {
        using var held = new HeldCell(); using var other = new StoreFixture("customer-b", "cell-b");
        var queued = other.Queue(); var claimed = other.Store.ClaimNext(other.Customer, "worker-b", TimeSpan.FromMinutes(1))!;
        Assert.Equal(queued.Id, other.Store.StartDispatch(other.Customer, claimed.Id, "worker-b", claimed.Fence).Job.Id);
        var context = await held.Context(FoundationCapabilities.RecoveryRead, "Operator");
        Assert.Throws<FoundationStoreException>(() => other.Store.GetRecoveryExecutionHold(context));
        Assert.Equal("Dispatched", other.Store.GetJob(other.Customer, "operator", queued.Id).State);
        other.Store.SetMembership(other.Customer, "admin", "operations", "default", "dev", ["PlatformOperator"]);
        var operational = other.Store.OperationalStatus(other.Customer, "operations", "default", "dev");
        Assert.False(operational.RecoveryExecutionHeld); Assert.Null(operational.ExecutionHoldReason); Assert.True(other.Store.StorageReady());
        var prepared = held.NewPlan(held.Restored); Denied(() => held.Restored.EnqueuePlan(held.Customer, "operator", prepared.Id, prepared.Digest, "still-held"));
    }

    [Theory]
    [InlineData(FoundationCapabilities.RecoveryRead, "Operator")]
    [InlineData(FoundationCapabilities.AuditRead, "Auditor")]
    [InlineData(FoundationCapabilities.LifecycleRead, "CustomerAdmin")]
    [InlineData(FoundationCapabilities.LifecycleRead, "Approver")]
    public async Task Authorized_hold_status_is_metadata_only_and_cannot_release_or_activate_anything(string capability, string role)
    {
        using var f = new HeldCell(); var status = f.Restored.GetRecoveryExecutionHold(await f.Context(capability, role));
        Assert.True(status.ExecutionHeld); Assert.NotNull(status.Hold);
        Assert.Equal(f.Customer, status.CustomerId); Assert.Equal(f.CellId, status.CellId);
        Assert.Equal(f.Restored.GetCellInstanceIdentity().DatabaseInstanceId, status.DatabaseInstanceId);
        Assert.Equal(f.Source.GetCellInstanceIdentity().DatabaseInstanceId, status.Hold.SourceDatabaseInstanceId);
        Assert.Equal(f.Backup.Sha256, status.Hold.BackupSha256);
        Assert.False(status.ReleaseAvailable); Assert.False(status.SourceHostFenced); Assert.False(status.CredentialActivationPerformed);
        var json = JsonSerializer.Serialize(status);
        foreach (var forbidden in new[] { f.SourcePath, f.RestoredPath, f.Cell.VaultRoot, CredentialFixture.Token, "customer-org", "customer-repo", "Ciphertext", "PrivateKey" })
            Assert.DoesNotContain(forbidden, json);
        Assert.Empty(f.Cell.Keys.Requests);
    }

    [Theory]
    [InlineData("wrong-capability")]
    [InlineData("revoked")]
    [InlineData("regranted-version")]
    [InlineData("expired")]
    public async Task Stale_or_wrong_capability_status_context_is_not_treated_as_safe_read_authority(string mutation)
    {
        using var f = new HeldCell();
        var context = await f.Context(mutation == "wrong-capability" ? FoundationCapabilities.CatalogRead : FoundationCapabilities.RecoveryRead,
            mutation == "wrong-capability" ? "Viewer" : "Operator");
        if (mutation == "revoked") f.Restored.SetMembership(f.Customer, "admin", HeldCell.Actor, "default", "dev", [], false);
        if (mutation == "regranted-version") f.Restored.SetMembership(f.Customer, "admin", HeldCell.Actor, "default", "dev", HeldCell.Roles);
        if (mutation == "expired") f.Cell.Clock.Now = f.Cell.Clock.Now.AddMinutes(11);
        Assert.Throws<FoundationStoreException>(() => f.Restored.GetRecoveryExecutionHold(context));
        Assert.Equal(1, f.Number("SELECT count(*) FROM recovery_execution_holds"));
    }

    [Fact]
    public async Task Audit_export_backups_and_inactive_reconciliation_remain_available_without_clearing_hold()
    {
        using var f = new HeldCell();
        Assert.True(f.Restored.VerifyAuditIntegrity(f.Customer, "auditor"));
        var export = f.Restored.CreatePagedScopeExport(f.Customer, "auditor", "default", "dev"); Assert.NotEmpty(export.FirstPage.CanonicalPageBase64);
        using var signer = RSA.Create(2048); var anchor = f.Restored.CreateAuditAnchor(f.Customer, "auditor", signer, "hold-evidence-key");
        Assert.NotNull(anchor); var backup = f.Restored.CreateBackup(f.Path("held-history.sqlite")); Assert.True(File.Exists(backup.DatabasePath));
        var suspend = f.Restored.PrepareLifecycleChange(f.Customer, "admin", "Suspend", Purpose);
        f.Restored.ApproveAdministrativePlan(f.Customer, "approver", suspend.Id, suspend.Digest);
        f.Restored.ExecuteLifecycleChange(f.Customer, "admin", suspend.Id, suspend.Digest);
        var item = f.Restored.GetRecoveryCase(f.Customer, "operator", "default", "dev", f.Cell.Dispatch.Job.Id);
        Assert.Equal("OutcomeUnknown", item.State);
        Assert.Equal("ReconciledNoEffect", f.Restored.ReconcileRecoveryCase(f.Customer, "operator", "default", "dev", item.Id, item.CaseSha256,
            "FailedNoEffect", "{\"reviewedSyntheticEvidence\":true}").State);
        Assert.True(f.Restored.GetRecoveryExecutionHold(await f.Context(FoundationCapabilities.RecoveryRead, "Operator", administrative: true)).ExecutionHeld);
        Assert.Empty(f.Cell.Keys.Requests);
    }

    [Fact]
    public async Task Repeated_restore_has_fresh_current_instance_hold_and_preserves_each_prior_hold_as_history()
    {
        using var f = new HeldCell(); var first = f.Restored.GetRecoveryExecutionHold(await f.Context(FoundationCapabilities.RecoveryRead, "Operator"));
        var originalBytes = HoldRows(f.RestoredPath); var backup = f.Restored.CreateBackup(f.Path("second-backup.sqlite"));
        var target = f.Path("second-restored.sqlite"); var second = FoundationStore.RestoreBackup(backup, target, f.Cell.Clock);
        var current = second.GetRecoveryExecutionHold(await f.Context(FoundationCapabilities.RecoveryRead, "Operator"));
        Assert.NotEqual(first.DatabaseInstanceId, current.DatabaseInstanceId); Assert.NotEqual(first.Hold!.Id, current.Hold!.Id);
        Assert.Equal(first.DatabaseInstanceId, current.Hold.SourceDatabaseInstanceId); Assert.Equal(backup.Sha256, current.Hold.BackupSha256);
        Assert.Equal(originalBytes, HoldRows(target).Where(row => row.Contains(first.Hold.Id, StringComparison.Ordinal)).ToArray());
        Assert.Equal(2, Number(target, "SELECT count(*) FROM recovery_execution_holds"));
        var fresh = f.NewPlan(second); Denied(() => second.EnqueuePlan(f.Customer, "operator", fresh.Id, fresh.Digest, "second-restore"));
    }

    [Fact]
    public void Raw_instance_rotation_cannot_turn_missing_current_hold_into_unheld_execution()
    {
        using var f = new HeldCell(); var rows = HoldRows(f.RestoredPath);
        Execute(f.RestoredPath, "UPDATE cell SET database_instance_id='fedcba9876543210fedcba9876543210'");
        Assert.Throws<FoundationStoreException>(() => f.Restored.ClaimNext(f.Customer, "worker", TimeSpan.FromMinutes(1)));
        Assert.Equal(rows, HoldRows(f.RestoredPath)); Assert.Equal("OutcomeUnknown", f.Restored.GetJob(f.Customer, "operator", f.Cell.Dispatch.Job.Id).State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Synthetic_legacy_v10_migration_distinguishes_prior_restore_from_fresh_cell_without_inventing_source_identity(bool previouslyRestored)
    {
        using var f = new StoreFixture(); var queued = f.Queue(); var beforeInstance = f.Store.GetCellInstanceIdentity().DatabaseInstanceId;
        FoundationTestSchemaDowngrades.DropCredentialTablesAfter(f, 10);
        f.Execute("DROP TRIGGER schema_migrations_no_delete; DELETE FROM schema_migrations WHERE version>10; CREATE TRIGGER schema_migrations_no_delete BEFORE DELETE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END; UPDATE cell SET schema_version=10;");
        var backupDigest = new string('a', 64); var restoredInstance = Guid.NewGuid().ToString("N");
        if (previouslyRestored)
        {
            // Reproduce the historical v10 restore mutation and exact audit event using the real chain writer.
            // This is a synthetic old-format fixture; no prior binary or private backup is executed.
            using var db = Open(f.Path); using var tx = db.BeginTransaction();
            using (var command = db.CreateCommand())
            {
                command.Transaction = tx; command.CommandText = "UPDATE cell SET policy_version=policy_version+1,database_instance_id=$instance; UPDATE jobs SET state='OutcomeUnknown',worker_id=NULL,lease_until=NULL,fence=fence+1,reason='RecoveryRequiresReconciliation';";
                command.Parameters.AddWithValue("$instance", restoredInstance); command.ExecuteNonQuery();
            }
            typeof(FoundationStore).GetMethod("Audit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f.Store,
                [db, tx, "cell-maintenance", "default", "dev", "BackupRestored", f.Cell, "Succeeded", "RecoveryPolicyChanged", backupDigest]);
            tx.Commit();
        }
        Assert.Equal(10, f.Scalar("SELECT schema_version FROM cell"));
        var upgraded = f.Reopen(); Assert.Equal(FoundationStore.CurrentSchemaVersion, f.Scalar("SELECT schema_version FROM cell"));
        Assert.True(upgraded.CustomerLifecycle(f.Customer, "admin").Enabled); Assert.True(upgraded.VerifyAuditIntegrity(f.Customer, "auditor"));
        Assert.Equal(previouslyRestored ? 1 : 0, f.Scalar("SELECT count(*) FROM recovery_execution_holds"));
        if (previouslyRestored)
        {
            Assert.NotEqual(beforeInstance, upgraded.GetCellInstanceIdentity().DatabaseInstanceId);
            Assert.Equal(restoredInstance, upgraded.GetCellInstanceIdentity().DatabaseInstanceId);
            Assert.Equal(1, f.Scalar("SELECT count(*) FROM recovery_execution_holds WHERE origin='LegacyRestoreOriginUnknown' AND source_database_instance_id IS NULL AND backup_sha256='" + backupDigest + "'"));
            upgraded.SetMembership(f.Customer, "admin", "operator", "default", "dev", ["Operator"]);
            upgraded.SetCustomerEnabled(f.Customer, "admin", false); upgraded.SetCustomerEnabled(f.Customer, "admin", true);
            var plan = upgraded.CreatePlan(f.Customer, "requester", f.Input()); upgraded.ApprovePlan(f.Customer, "approver", plan.Id, plan.Digest);
            Denied(() => upgraded.EnqueuePlan(f.Customer, "operator", plan.Id, plan.Digest, "legacy-restored-fresh"));
            Denied(() => upgraded.ClaimNext(f.Customer, "worker", TimeSpan.FromMinutes(1)));
            Assert.Equal("OutcomeUnknown", upgraded.GetJob(f.Customer, "operator", queued.Id).State);
        }
        else
        {
            var claim = upgraded.ClaimNext(f.Customer, "worker", TimeSpan.FromMinutes(1)); Assert.NotNull(claim); Assert.Equal(queued.Id, claim.Id);
            Assert.Equal("Dispatched", upgraded.StartDispatch(f.Customer, claim.Id, "worker", claim.Fence).Job.State);
        }
    }

    [Theory]
    [InlineData("bad-digest")]
    [InlineData("removed-current")]
    [InlineData("changed-instance")]
    public void Coherently_resigned_backup_with_corrupted_or_missing_current_hold_is_rejected_before_destination_creation(string mutation)
    {
        using var f = new HeldCell(); using var signer = RSA.Create(2048);
        var signed = f.Restored.CreateSignedBackup(f.Path("hold-signed-snapshot.sqlite"), signer, "hold-backup-key", 1,
            f.Cell.Clock.Now.AddMinutes(-1), f.Cell.Clock.Now.AddHours(1));
        var path = signed.Backup.DatabasePath;
        if (mutation == "changed-instance") Execute(path, "UPDATE cell SET database_instance_id='fedcba9876543210fedcba9876543210'");
        else
        {
            var trigger = mutation == "bad-digest" ? "recovery_execution_holds_no_update" : "recovery_execution_holds_no_delete";
            using var db = Open(path); using var tx = db.BeginTransaction(); using var query = db.CreateCommand(); query.Transaction = tx;
            query.CommandText = "SELECT sql FROM sqlite_master WHERE type='trigger' AND name=$name"; query.Parameters.AddWithValue("$name", trigger);
            var originalTrigger = Assert.IsType<string>(query.ExecuteScalar()); query.Parameters.Clear();
            query.CommandText = "DROP TRIGGER " + trigger + "; " + (mutation == "bad-digest" ? "UPDATE recovery_execution_holds SET digest='" + new string('a', 64) + "'; " : "DELETE FROM recovery_execution_holds; ") + originalTrigger;
            query.ExecuteNonQuery(); tx.Commit();
        }
        var bytes = File.ReadAllBytes(path); var digest = Convert.ToHexStringLower(SHA256.HashData(bytes));
        var manifest = signed.Manifest with { SnapshotSha256 = digest, SnapshotLength = bytes.Length };
        manifest = manifest with { Signature = Convert.ToBase64String(signer.SignData(FoundationBackupSignatures.CanonicalPayload(manifest), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) };
        signed = new(signed.Backup with { Sha256 = digest, Length = bytes.Length }, manifest);
        var authority = FoundationBackupSignatures.Authority(manifest);
        using var publicKey = RSA.Create(); publicKey.ImportSubjectPublicKeyInfo(signer.ExportSubjectPublicKeyInfo(), out _);
        _ = FoundationBackupSignatures.Verify(manifest, publicKey, authority, f.Customer, f.CellId, f.Cell.Clock);
        var directory = f.Path("refused-signed-target"); var target = System.IO.Path.Combine(directory, "cell.sqlite");
        Assert.Throws<FoundationStoreException>(() => FoundationStore.VerifySignedBackup(signed, authority, publicKey, f.Customer, f.CellId, f.Cell.Clock));
        Assert.Throws<FoundationStoreException>(() => FoundationStore.RestoreSignedBackup(signed, target, authority, publicKey, f.Customer, f.CellId, f.Cell.Clock));
        Assert.False(Directory.Exists(directory)); Assert.Equal(bytes, File.ReadAllBytes(path));
        Assert.Equal(1, f.Number("SELECT count(*) FROM recovery_execution_holds"));
    }

    static void Denied(Action action)
    { var error = Assert.Throws<FoundationStoreException>(action); Assert.Equal(Held, error.Code); Assert.Equal(403, error.Status); }
    static long Number(string path, string sql) { using var db = Open(path); using var query = db.CreateCommand(); query.CommandText = sql; return Convert.ToInt64(query.ExecuteScalar()); }
    static void Execute(string path, string sql) { using var db = Open(path); using var query = db.CreateCommand(); query.CommandText = sql; query.ExecuteNonQuery(); }
    static string[] HoldRows(string path)
    { using var db = Open(path); using var query = db.CreateCommand(); query.CommandText = "SELECT * FROM recovery_execution_holds ORDER BY created,id"; using var reader = query.ExecuteReader(); var rows = new List<string>(); while (reader.Read()) rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray())); return rows.ToArray(); }
    static SqliteConnection Open(string path) { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open(); return db; }

    sealed class Posts : HttpMessageHandler
    {
        public int Calls { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Calls++; return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("{}") }); }
    }

    sealed class HeldCell : IDisposable
    {
        public const string Actor = "user:" + IdentityTestIssuer.Tenant + ":" + IdentityTestIssuer.Subject;
        public static readonly string[] Roles = ["Operator", "Auditor", "Approver", "CustomerAdmin", "Viewer", "Requester"];
        readonly IdentityTestIssuer issuer = new();
        public CredentialFixture Cell { get; }
        public string Customer => Cell.Scope.CustomerId;
        public string CellId => Cell.Scope.CellId;
        public string SourcePath => Path("cell.db");
        public string RestoredPath => Path("restored.sqlite");
        public FoundationStore Source { get; }
        public FoundationStore Restored { get; }
        public FoundationBackup Backup { get; }
        public HeldCell(string provider = PipelineRequestFactory.GitHub)
        {
            Cell = new(provider); Source = new(SourcePath, CellId, Customer, Cell.Clock);
            Source.SetMembership(Customer, "admin", "auditor", "default", "dev", ["Auditor"]);
            Source.SetMembership(Customer, "admin", Actor, "default", "dev", Roles);
            Backup = Source.CreateBackup(Path("source-snapshot.sqlite")); Restored = FoundationStore.RestoreBackup(Backup, RestoredPath, Cell.Clock);
        }
        public string Path(string name) => System.IO.Path.Combine(Cell.Root, name);
        public long Number(string sql) => FoundationRecoveryExecutionHoldBoundaryTests.Number(RestoredPath, sql);
        public string[] JobRows()
        { using var db = Open(RestoredPath); using var query = db.CreateCommand(); query.CommandText = "SELECT id,state,worker_id,lease_until,fence,attempt,receipt,reason FROM jobs ORDER BY id"; using var reader = query.ExecuteReader(); var rows = new List<string>(); while (reader.Read()) rows.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(i => reader.IsDBNull(i) ? null : reader.GetValue(i)).ToArray())); return rows.ToArray(); }
        public FoundationPlan NewPlan(FoundationStore store, bool changes = true)
        {
            var request = Cell.Request with { RequestId = Guid.NewGuid().ToString("D") };
            var plan = store.CreatePlan(Customer, "requester", new("default", "dev", Cell.Connection.Id, "pipeline.dispatch", JsonSerializer.Serialize(request,
                new JsonSerializerOptions(JsonSerializerDefaults.Web)), changes, Cell.Clock.Now.AddMinutes(20), request.SourceRevision));
            return changes ? store.ApprovePlan(Customer, "approver", plan.Id, plan.Digest) : plan;
        }
        public FoundationCredentialDispatchPermitSource Permits(FoundationStore store) => new(store, Cell.Dispatch.Job.Id,
            Cell.Dispatch.Job.WorkerId!, Cell.Dispatch.Job.Fence, Cell.Scope, Cell.Dispatch.Plan.Digest, Cell.Dispatch.Plan.SourceRevision,
            Cell.Dispatch.Plan.CatalogDigest, new([], Cell.Clock), "1.0.0");
        public async Task<CustomerContext> Context(string capability, string role, bool administrative = false)
        {
            var options = issuer.Options(Customer, CellId); var claims = issuer.UserClaims(role);
            claims["iat"] = claims["nbf"] = Cell.Clock.Now.AddSeconds(-30).ToUnixTimeSeconds(); claims["exp"] = Cell.Clock.Now.AddMinutes(10).ToUnixTimeSeconds();
            var principal = await new IdentityTokenValidator(options, Cell.Clock).ValidateAsync(issuer.Token(claims));
            return new CustomerContextResolver(options, new Memberships(Restored, administrative), Cell.Clock).Require(principal, Customer, "default", "dev", capability);
        }
        public void Dispose() { issuer.Dispose(); Cell.Dispose(); }
        sealed class Memberships(FoundationStore store, bool administrative) : ICustomerMembershipSource
        {
            public IReadOnlyList<MembershipGrant> Find(string customerId, string actorKey) =>
                (administrative ? store.GetRecoveryMemberships(customerId, actorKey) : store.GetMemberships(customerId, actorKey)).Select(m =>
                    new MembershipGrant(m.CustomerId, m.CellId, m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version, m.Enabled)).ToArray();
        }
    }
}
