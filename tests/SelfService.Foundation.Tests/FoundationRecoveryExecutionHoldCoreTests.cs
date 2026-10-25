using System.Reflection;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationRecoveryExecutionHoldCoreTests
{
    [Fact]
    public async Task Fresh_cell_is_unheld_and_backup_creation_does_not_turn_it_into_a_restored_cell()
    {
        using var f = new Fixture(); var context = await f.Reader(f.Core.Store);
        var initial = f.Core.Store.GetRecoveryExecutionHold(context); Assert.False(initial.ExecutionHeld); Assert.Null(initial.Hold);
        var operational = f.Core.Store.OperationalStatus(f.Core.Customer, "operations", "default", "dev");
        Assert.False(operational.RecoveryExecutionHeld); Assert.Null(operational.ExecutionHoldReason);
        Assert.Equal(11, f.Core.Store.SchemaStatus().Version); Assert.Equal(0, f.Core.Scalar("SELECT count(*) FROM recovery_execution_holds"));
        _ = f.Backup(f.Core.Store, "fresh-backup"); Assert.False(f.Core.Store.GetRecoveryExecutionHold(context).ExecutionHeld);
        Assert.Null(f.Core.Store.ClaimNext(f.Core.Customer, "worker", TimeSpan.FromMinutes(1))); var job = f.Core.Queue();
        Assert.Equal(job.Id, f.Core.Store.ClaimNext(f.Core.Customer, "worker", TimeSpan.FromMinutes(1))!.Id);
    }

    [Fact]
    public async Task Every_restore_binds_immutable_hold_to_actual_source_new_instance_and_exact_backup()
    {
        using var f = new Fixture(); var source = f.Core.Store.GetCellInstanceIdentity().DatabaseInstanceId;
        var backup = f.Backup(f.Core.Store, "source-backup"); var restored = f.Restore(backup, "held-database");
        var status = restored.GetRecoveryExecutionHold(await f.Reader(restored));
        Assert.True(status.ExecutionHeld); var hold = Assert.IsType<FoundationRecoveryExecutionHold>(status.Hold);
        Assert.Equal(source, hold.SourceDatabaseInstanceId); Assert.Equal("RestoredSnapshot", hold.Origin);
        Assert.NotEqual(source, status.DatabaseInstanceId); Assert.Equal(restored.GetCellInstanceIdentity().DatabaseInstanceId, hold.DatabaseInstanceId);
        Assert.Equal(backup.Sha256, hold.BackupSha256); Assert.Equal("RestoreRecoveryRequiresReconciliation", hold.Reason);
        Assert.False(status.ReleaseAvailable); Assert.False(status.SourceHostFenced); Assert.False(status.CredentialActivationPerformed);
        Assert.False(status.ExternalExecutionAuthorized); Assert.True(restored.CustomerLifecycle(f.Core.Customer, "admin").Enabled);
        var operational = restored.OperationalStatus(f.Core.Customer, "operations", "default", "dev");
        Assert.True(operational.Enabled); Assert.True(operational.RecoveryExecutionHeld); Assert.Equal("RecoveryRequiresReconciliation", operational.ExecutionHoldReason);
        Assert.True(restored.VerifyAuditIntegrity(f.Core.Customer, "auditor"));
        Assert.DoesNotContain(f.Core.Path, JsonSerializer.Serialize(status));
        Assert.Equal("RecoveryExecutionHeld", Assert.Throws<FoundationStoreException>(() => restored.ClaimNext(f.Core.Customer, "worker", TimeSpan.FromMinutes(1))).Code);
    }

    [Theory]
    [InlineData("null")] [InlineData("workload")] [InlineData("support")]
    public async Task Hold_status_requires_current_human_context_without_delegated_support(string change)
    {
        using var f = new Fixture(); CustomerContext? context = await f.Reader(f.Core.Store);
        if (change == "null") context = null;
        else context = (CustomerContext)typeof(CustomerContext).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([
            context.CustomerId, context.CellId, context.ActorKey, context.ProjectId, context.EnvironmentId, context.Capability, context.RoleVersion,
            change == "workload" ? PrincipalKind.Workload : context.Kind, context.ExpiresUtc, null, null, change == "support" ? "synthetic-support" : null]);
        Assert.Throws<FoundationStoreException>(() => f.Core.Store.GetRecoveryExecutionHold(context!));
        Assert.Equal(0, f.Core.Scalar("SELECT count(*) FROM recovery_execution_holds"));
    }

    [Theory]
    [InlineData(1)] [InlineData(4)] [InlineData(5)]
    public async Task Retained_legacy_source_identity_is_reported_only_when_that_column_existed_in_the_verified_snapshot(int version)
    {
        using var f = new Fixture(); var original = f.Core.Store.GetCellInstanceIdentity().DatabaseInstanceId;
        // Reuse the existing frozen-schema fixture; no historical binary or real backup is involved.
        typeof(FoundationBackupSchemaAdmissionTests).GetMethod("Downgrade", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [f.Core, version]);
        var backup = f.Backup(f.Core.Store, "legacy-source"); var sourceBytes = File.ReadAllBytes(backup.DatabasePath);
        var restored = f.Restore(backup, "legacy-restored"); var status = restored.GetRecoveryExecutionHold(await f.Reader(restored));
        Assert.True(status.ExecutionHeld); Assert.NotNull(status.Hold);
        Assert.Equal(version < 5 ? null : original, status.Hold.SourceDatabaseInstanceId);
        Assert.Equal(version < 5 ? "LegacyRestoreOriginUnknown" : "RestoredSnapshot", status.Hold.Origin);
        Assert.NotEqual(original, status.DatabaseInstanceId); Assert.Equal(sourceBytes, File.ReadAllBytes(backup.DatabasePath));
        Assert.Equal("RecoveryExecutionHeld", Assert.Throws<FoundationStoreException>(() => restored.ClaimNext(f.Core.Customer, "worker", TimeSpan.FromMinutes(1))).Code);
    }

    [Theory]
    [InlineData("UPDATE recovery_execution_holds SET reason='clear'")]
    [InlineData("DELETE FROM recovery_execution_holds")]
    public void Existing_hold_cannot_be_updated_or_deleted_through_SQL(string mutation)
    {
        using var f = new Fixture(); var restored = f.Restore(f.Backup(f.Core.Store, "original"), "held"); var before = f.Rows(restored);
        Assert.Throws<SqliteException>(() => f.Execute(restored, mutation)); Assert.Equal(before, f.Rows(restored));
    }

    [Fact]
    public void Genuine_fresh_v10_upgrade_preserves_ten_frozen_checksums_and_does_not_grant_an_implicit_runtime_migration()
    {
        using var f = new Fixture(); var originalInstance = f.Core.Store.GetCellInstanceIdentity().DatabaseInstanceId; Downgrade(f.Core);
        var history = History(f.Core.Path); var bytes = File.ReadAllBytes(f.Core.Path);
        Assert.Equal("CurrentSchemaRequired", Assert.Throws<FoundationStoreException>(() => new FoundationStore(f.Core.Path, f.Core.Cell, f.Core.Customer, initializeSchema: false)).Code);
        Assert.Equal(bytes, File.ReadAllBytes(f.Core.Path)); var upgraded = f.Core.Reopen();
        Assert.Equal(history, History(f.Core.Path).Take(10)); Assert.Equal(11, upgraded.SchemaStatus().Version);
        Assert.Equal(originalInstance, upgraded.GetCellInstanceIdentity().DatabaseInstanceId); Assert.Equal(0, f.Core.Scalar("SELECT count(*) FROM recovery_execution_holds"));
        Assert.Equal(1, f.Core.Scalar("SELECT count(*) FROM audit WHERE action='SchemaMigrated' AND reason='SchemaV10ToV11'"));
        var plan = upgraded.CreatePlan(f.Core.Customer, "requester", f.Core.Input()); upgraded.ApprovePlan(f.Core.Customer, "approver", plan.Id, plan.Digest);
        Assert.NotNull(upgraded.EnqueuePlan(f.Core.Customer, "operator", plan.Id, plan.Digest, "after-explicit-upgrade"));
    }

    [Fact]
    public void Missing_current_hold_table_is_not_recreated_or_treated_as_an_unheld_cell()
    {
        using var f = new Fixture(); f.Core.Execute("DROP TABLE recovery_execution_holds");
        Assert.Equal("MigrationSchemaMismatch", Assert.Throws<FoundationStoreException>(() => f.Core.Reopen()).Code);
        Assert.Equal("MigrationSchemaMismatch", Assert.Throws<FoundationStoreException>(() => new FoundationStore(f.Core.Path, f.Core.Cell, f.Core.Customer, initializeSchema: false)).Code);
        Assert.Equal(0, f.Core.Scalar("SELECT count(*) FROM sqlite_master WHERE name='recovery_execution_holds'"));
    }

    [Fact]
    public void Migration_audit_failure_rolls_back_v11_objects_history_and_cell_version()
    {
        using var f = new Fixture(); Downgrade(f.Core); var history = History(f.Core.Path); var audit = f.Core.Scalar("SELECT count(*) FROM audit");
        f.Core.Execute("CREATE TRIGGER synthetic_hold_migration_audit BEFORE INSERT ON audit WHEN NEW.reason='SchemaV10ToV11' BEGIN SELECT RAISE(ABORT,'synthetic audit refusal'); END;");
        Assert.Throws<SqliteException>(() => f.Core.Reopen()); Assert.Equal(10, f.Core.Scalar("SELECT schema_version FROM cell"));
        Assert.Equal(history, History(f.Core.Path)); Assert.Equal(audit, f.Core.Scalar("SELECT count(*) FROM audit"));
        Assert.Equal(0, f.Core.Scalar("SELECT count(*) FROM sqlite_master WHERE name LIKE 'recovery_execution_holds%'"));
    }

    [Fact]
    public void Tampered_v11_history_checksum_is_refused_without_repair()
    {
        using var f = new Fixture(); f.Core.Execute("DROP TRIGGER schema_migrations_no_update;UPDATE schema_migrations SET checksum='changed' WHERE version=11;CREATE TRIGGER schema_migrations_no_update BEFORE UPDATE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END;");
        Assert.Equal("MigrationHistoryMismatch", Assert.Throws<FoundationStoreException>(() => f.Core.Reopen()).Code);
        Assert.Equal(1, f.Core.Scalar("SELECT count(*) FROM schema_migrations WHERE version=11 AND checksum='changed'"));
    }

    [Fact]
    public async Task Bounded_history_overflow_preserves_complete_source_and_leaves_no_failed_restore_destination()
    {
        using var f = new Fixture(); var held = f.Restore(f.Backup(f.Core.Store, "original"), "held");
        var first = held.GetRecoveryExecutionHold(await f.Reader(held)).Hold!;
        var hasher = typeof(FoundationStore).GetMethod("RecoveryExecutionHoldDigest", BindingFlags.Static | BindingFlags.NonPublic)!;
        using (var db = f.Open(held)) using (var tx = db.BeginTransaction())
        {
            using var command = db.CreateCommand(); command.Transaction = tx;
            command.CommandText = "INSERT INTO recovery_execution_holds VALUES($c,$cell,$instance,$id,$source,$origin,$backup,$policy,$created,$reason,$digest)";
            foreach (var name in new[] { "$c", "$cell", "$instance", "$id", "$source", "$origin", "$backup", "$reason", "$digest" }) command.Parameters.Add(name, SqliteType.Text);
            command.Parameters.Add("$policy", SqliteType.Integer); command.Parameters.Add("$created", SqliteType.Integer); command.Prepare();
            for (var index = 1; index < FoundationStore.MaximumRecoveryExecutionHolds; index++)
            {
                var row = first with { Id = Guid.NewGuid().ToString("N"), DatabaseInstanceId = Guid.NewGuid().ToString("N"), Sha256 = "" };
                row = row with { Sha256 = (string)hasher.Invoke(null, [row])! };
                command.Parameters["$c"].Value = row.CustomerId; command.Parameters["$cell"].Value = row.CellId; command.Parameters["$instance"].Value = row.DatabaseInstanceId;
                command.Parameters["$id"].Value = row.Id; command.Parameters["$source"].Value = row.SourceDatabaseInstanceId!; command.Parameters["$origin"].Value = row.Origin;
                command.Parameters["$backup"].Value = row.BackupSha256; command.Parameters["$policy"].Value = row.PolicyVersion; command.Parameters["$created"].Value = row.CreatedAt.ToUnixTimeMilliseconds();
                command.Parameters["$reason"].Value = row.Reason; command.Parameters["$digest"].Value = row.Sha256; Assert.Equal(1, command.ExecuteNonQuery());
            }
            tx.Commit();
        }
        var snapshot = f.Backup(held, "full-history"); var bytes = File.ReadAllBytes(snapshot.DatabasePath); var target = f.Owned("overflow-target");
        Assert.Equal("RecoveryExecutionHoldCapacityExceeded", Assert.Throws<FoundationStoreException>(() => FoundationStore.RestoreBackup(snapshot, target, f.Core.Clock)).Code);
        Assert.False(File.Exists(target)); Assert.Equal(bytes, File.ReadAllBytes(snapshot.DatabasePath));
        Assert.True(held.GetRecoveryExecutionHold(await f.Reader(held)).ExecutionHeld);
    }

    static void Downgrade(StoreFixture f)
    {
        FoundationTestSchemaDowngrades.DropCredentialTablesAfter(f, 10);
        f.Execute("DROP TRIGGER schema_migrations_no_delete;DELETE FROM schema_migrations WHERE version>10;UPDATE cell SET schema_version=10;CREATE TRIGGER schema_migrations_no_delete BEFORE DELETE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END;");
    }
    static string[] History(string path)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open();
        using var command = db.CreateCommand(); command.CommandText = "SELECT checksum FROM schema_migrations ORDER BY version"; using var rows = command.ExecuteReader(); var result = new List<string>();
        while (rows.Read()) result.Add(rows.GetString(0)); return result.ToArray();
    }
    sealed class Fixture : IDisposable
    {
        public StoreFixture Core { get; } = new(); readonly List<string> owned = []; readonly Dictionary<FoundationStore, string> paths = [];
        readonly IdentityTestIssuer issuer = new(); const string Actor = "user:" + IdentityTestIssuer.Tenant + ":" + IdentityTestIssuer.Subject;
        public Fixture()
        {
            Core.Store.SetMembership(Core.Customer, "admin", Actor, "default", "dev", ["Auditor"]);
            Core.Store.SetMembership(Core.Customer, "admin", "operations", "default", "dev", ["PlatformOperator"]); paths[Core.Store] = Core.Path;
        }
        public string Owned(string name) { var path = Core.Path + ".hold-" + name; owned.Add(path); return path; }
        public FoundationBackup Backup(FoundationStore store, string name) => store.CreateBackup(Owned(name));
        public FoundationStore Restore(FoundationBackup backup, string name) { var path = Owned(name); var result = FoundationStore.RestoreBackup(backup, path, Core.Clock); paths[result] = path; return result; }
        public SqliteConnection Open(FoundationStore store) { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = paths[store], Pooling = false }.ToString()); db.Open(); return db; }
        public void Execute(FoundationStore store, string sql) { using var db = Open(store); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public string[] Rows(FoundationStore store) { using var db = Open(store); using var command = db.CreateCommand(); command.CommandText = "SELECT digest FROM recovery_execution_holds ORDER BY id"; using var reader = command.ExecuteReader(); var rows = new List<string>(); while (reader.Read()) rows.Add(reader.GetString(0)); return rows.ToArray(); }
        public async Task<CustomerContext> Reader(FoundationStore store)
        {
            var options = issuer.Options(Core.Customer, Core.Cell); var claims = issuer.UserClaims("Auditor");
            claims["iat"] = claims["nbf"] = Core.Clock.GetUtcNow().AddSeconds(-30).ToUnixTimeSeconds(); claims["exp"] = Core.Clock.GetUtcNow().AddMinutes(10).ToUnixTimeSeconds();
            var principal = await new IdentityTokenValidator(options, Core.Clock).ValidateAsync(issuer.Token(claims));
            return new CustomerContextResolver(options, new Memberships(store), Core.Clock).Require(principal, Core.Customer, "default", "dev", FoundationCapabilities.AuditRead);
        }
        public void Dispose() { issuer.Dispose(); foreach (var path in owned) foreach (var suffix in new[] { "", "-wal", "-shm", "-journal" }) if (File.Exists(path + suffix)) File.Delete(path + suffix); Core.Dispose(); }
        sealed class Memberships(FoundationStore store) : ICustomerMembershipSource
        { public IReadOnlyList<MembershipGrant> Find(string customer, string actor) => store.GetCredentialMaintenanceMemberships(customer, actor).Select(m => new MembershipGrant(m.CustomerId, m.CellId, m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version, m.Enabled)).ToArray(); }
    }
}
