using System.Diagnostics;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationCredentialRuntimeBindingStoreTests
{
    [Theory]
    [InlineData("weak")]
    [InlineData("strong")]
    [InlineData("scoped")]
    public void Explicit_fresh_binding_is_atomic_immutable_redacted_metadata_and_does_not_resolve_any_key(string mode)
    {
        using var f = new BindingFixture(mode); var before = f.Number("SELECT count(*) FROM audit");
        Assert.False(f.Store.GetCredentialRuntimeBindingStatus().BindingRequired);
        var binding = f.Bind(); var status = f.Store.GetCredentialRuntimeBindingStatus();
        Assert.True(status.BindingRequired); Assert.Equal(binding, status.CurrentBinding); Assert.Equal(0, status.HistoricalBindingCount);
        Assert.Equal(f.Instance, binding.DatabaseInstanceId); Assert.Equal(f.Vault.DescribeNamespace(), binding.Namespace);
        Assert.Equal(f.Authority.DescribeIdentity(), binding.ActivationAuthority);
        Assert.Equal(1, binding.ActivationGeneration); Assert.Equal(before + 1, f.Number("SELECT count(*) FROM audit"));
        Assert.Equal(1, f.Number("SELECT count(*) FROM audit WHERE action='CredentialRuntimeBound'")); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(binding, f.Reopen().GetCredentialRuntimeBindingStatus().CurrentBinding);
        var json = JsonSerializer.Serialize(status);
        foreach (var value in new[] { f.Cell.Root, f.Database, f.Cell.VaultRoot, CredentialFixture.Token, "Ciphertext", "PrivateKey", "AuthProfileId", "DatabasePath\"" })
            Assert.DoesNotContain(value, json);
        Assert.False(status.AutomaticBindingAvailable); Assert.False(status.RebindingAvailable); Assert.False(binding.ExternalExecutionAuthorized);
        Assert.False(binding.RecoveryHoldReleased); Assert.Equal(64, binding.DatabasePathSha256.Length);
    }

    [Fact]
    public void Ordinary_store_opening_and_a_fresh_schema_never_provision_a_binding()
    {
        using var f = new BindingFixture();
        for (var i = 0; i < 3; i++) Assert.Null(f.Reopen().GetCredentialRuntimeBindingStatus().CurrentBinding);
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_runtime_bindings")); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(0, f.Number("SELECT count(*) FROM audit WHERE action='CredentialRuntimeBound'"));
    }

    [Theory]
    [InlineData("UPDATE credential_runtime_bindings SET activation_generation=activation_generation+1")]
    [InlineData("DELETE FROM credential_runtime_bindings")]
    public void SQLite_guards_preserve_all_immutable_binding_bytes(string sql)
    {
        using var f = new BindingFixture(); var binding = f.Bind(); var rows = f.Rows();
        Assert.Throws<SqliteException>(() => f.Sql(sql)); Assert.Equal(rows, f.Rows()); Assert.Equal(binding, f.Store.GetCredentialRuntimeBindingStatus().CurrentBinding);
    }

    [Fact]
    public void Reprovisioning_cannot_change_an_existing_binding_or_its_audit_history()
    {
        using var f = new BindingFixture(); var binding = f.Bind(); var rows = f.Rows(); var audit = f.Number("SELECT count(*) FROM audit");
        Assert.Equal("CredentialRuntimeAlreadyBound", Assert.Throws<FoundationStoreException>(() => f.Bind()).Code);
        Assert.Equal(rows, f.Rows()); Assert.Equal(audit, f.Number("SELECT count(*) FROM audit")); Assert.Equal(binding, f.Store.GetCredentialRuntimeBindingStatus().CurrentBinding);
    }

    [Theory]
    [InlineData("Queued")]
    [InlineData("Claimed")]
    [InlineData("Dispatched")]
    public void Initial_policy_cutover_requires_no_pending_jobs(string state)
    {
        using var f = new BindingFixture(); f.Work(state); var audit = f.Number("SELECT count(*) FROM audit");
        Assert.Equal("CredentialRuntimeQuiescenceRequired", Assert.Throws<FoundationStoreException>(() => f.Bind()).Code);
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_runtime_bindings")); Assert.Equal(audit, f.Number("SELECT count(*) FROM audit"));
        Assert.Equal(1, f.Number("SELECT count(*) FROM jobs WHERE state='" + state + "'")); Assert.Empty(f.Cell.Keys.Requests);
    }

    [Theory]
    [InlineData("Disabled")]
    [InlineData("MockLoopback")]
    [InlineData("ClaimHold")]
    public void Initial_policy_cutover_requires_stopped_worker_registrations(string mode)
    {
        using var f = new BindingFixture(); using var process = Process.GetCurrentProcess();
        var worker = f.Store.RegisterWorker(f.Customer, "synthetic-owner-cutover", process.Id, process.StartTime.ToUniversalTime(), mode);
        Assert.Equal("CredentialRuntimeQuiescenceRequired", Assert.Throws<FoundationStoreException>(() => f.Bind()).Code);
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_runtime_bindings")); f.Store.StopWorker(f.Customer, worker);
        Assert.NotNull(f.Bind()); Assert.Empty(f.Cell.Keys.Requests);
    }

    [Fact]
    public void Suspended_unbound_cell_cannot_be_provisioned_as_a_fresh_active_cutover()
    {
        using var f = new BindingFixture(); f.Store.SetCustomerEnabled(f.Customer, "admin", false);
        Assert.Equal("CredentialRuntimeFreshActiveCellRequired", Assert.Throws<FoundationStoreException>(() => f.Bind()).Code);
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_runtime_bindings")); Assert.Empty(f.Cell.Keys.Requests);
    }

    [Fact]
    public void Audit_append_failure_rolls_back_binding_and_preserves_namespace_and_activation_bytes()
    {
        using var f = new BindingFixture(); var before = f.Number("SELECT count(*) FROM audit"); var files = f.NamespaceFiles();
        f.Sql("CREATE TRIGGER synthetic_binding_audit_failure BEFORE INSERT ON audit BEGIN SELECT RAISE(ABORT,'synthetic audit unavailable'); END;");
        try { Assert.Throws<SqliteException>(() => f.Bind()); }
        finally { f.Sql("DROP TRIGGER synthetic_binding_audit_failure;"); }
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_runtime_bindings")); Assert.Equal(before, f.Number("SELECT count(*) FROM audit"));
        Assert.Equal(files, f.NamespaceFiles()); Assert.Empty(f.Cell.Keys.Requests); Assert.NotNull(f.Bind());
    }

    [Fact]
    public void Restored_snapshot_retains_source_binding_as_history_and_cannot_bind_the_new_instance()
    {
        using var f = new BindingFixture(); var binding = f.Bind(); var rows = f.Rows(); var sourceInstance = f.Instance;
        var backup = f.Store.CreateBackup(Path.Combine(f.Cell.Root, "bound-history.sqlite"));
        var target = Path.Combine(f.Cell.Root, "restored-bound.sqlite"); var restored = FoundationStore.RestoreBackup(backup, target, f.Cell.Clock);
        var status = restored.GetCredentialRuntimeBindingStatus();
        Assert.True(status.BindingRequired); Assert.Null(status.CurrentBinding); Assert.Equal(1, status.HistoricalBindingCount);
        Assert.NotEqual(sourceInstance, status.DatabaseInstanceId); Assert.Equal(rows, BindingFixture.Rows(target));
        var old = f.Authority.ReadCurrent()!;
        f.Authority.Activate(target, status.DatabaseInstanceId, old.Generation, old.Sha256);
        Assert.Throws<FoundationStoreException>(() => FoundationCredentialBoundTransport.BindCurrent(restored, f.Vault, f.Authority,
            f.Vault.DescribeNamespace(), f.Authority.DescribeIdentity(), status.DatabaseInstanceId));
        Assert.Equal(rows, BindingFixture.Rows(target)); Assert.Equal(binding.DatabaseInstanceId, sourceInstance); Assert.Empty(f.Cell.Keys.Requests);
        var repeated = restored.CreateBackup(Path.Combine(f.Cell.Root, "repeated-bound.sqlite"));
        var twice = FoundationStore.RestoreBackup(repeated, Path.Combine(f.Cell.Root, "twice-bound.sqlite"), f.Cell.Clock);
        Assert.True(twice.GetCredentialRuntimeBindingStatus().BindingRequired); Assert.Null(twice.GetCredentialRuntimeBindingStatus().CurrentBinding);
        Assert.Equal(rows, BindingFixture.Rows(Path.Combine(f.Cell.Root, "twice-bound.sqlite")));
    }

    [Fact]
    public void Removing_current_row_cannot_remove_required_policy_anchored_in_immutable_audit()
    {
        using var f = new BindingFixture(); f.Bind(); f.WithoutTrigger("credential_runtime_bindings_no_delete", "DELETE FROM credential_runtime_bindings");
        var status = f.Store.GetCredentialRuntimeBindingStatus(); Assert.True(status.BindingRequired); Assert.Null(status.CurrentBinding);
        Assert.Equal("CredentialRuntimeAlreadyBound", Assert.Throws<FoundationStoreException>(() => f.Bind()).Code); Assert.Empty(f.Cell.Keys.Requests);
    }

    [Theory]
    [InlineData("digest='aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa'")]
    [InlineData("database_path_sha256='bbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbbb'")]
    [InlineData("namespace_json='{}'")]
    public void Tampered_binding_metadata_is_not_repaired_or_used(string mutation)
    {
        using var f = new BindingFixture(); f.Bind(); f.WithoutTrigger("credential_runtime_bindings_no_update", "UPDATE credential_runtime_bindings SET " + mutation);
        var rows = f.Rows(); Assert.Equal("CredentialRuntimeBindingIntegrityFailed", Assert.Throws<FoundationStoreException>(() => f.Store.GetCredentialRuntimeBindingStatus()).Code);
        Assert.Throws<FoundationStoreException>(() => f.Reopen()); Assert.Equal(rows, f.Rows()); Assert.Empty(f.Cell.Keys.Requests);
    }

    [Theory]
    [InlineData("table")]
    [InlineData("trigger")]
    public void Repinned_schema12_backup_missing_binding_schema_is_refused_before_creating_destination(string kind)
    {
        using var f = new BindingFixture(); f.Bind(); var backup = f.Store.CreateBackup(Path.Combine(f.Cell.Root, "schema-backup.sqlite"));
        BindingFixture.Sql(backup.DatabasePath, kind == "table" ? "DROP TABLE credential_runtime_bindings" : "DROP TRIGGER credential_runtime_bindings_no_update");
        var bytes = File.ReadAllBytes(backup.DatabasePath); backup = backup with { Sha256 = Convert.ToHexStringLower(SHA256.HashData(bytes)), Length = bytes.Length };
        var directory = Path.Combine(f.Cell.Root, "refused-target"); var destination = Path.Combine(directory, "cell.sqlite");
        Assert.Throws<FoundationStoreException>(() => FoundationStore.RestoreBackup(backup, destination, f.Cell.Clock));
        Assert.False(Directory.Exists(directory)); Assert.Equal(bytes, File.ReadAllBytes(backup.DatabasePath));
    }

    internal sealed class BindingFixture : IDisposable
    {
        internal CredentialFixture Cell { get; } = new();
        internal FoundationStore Store { get; }
        internal CredentialVault Vault { get; }
        internal FoundationCellActivationAuthority Authority { get; }
        internal string Database { get; }
        internal string Customer => Cell.Scope.CustomerId;
        internal string Instance => Store.GetCellInstanceIdentity().DatabaseInstanceId;
        internal BindingFixture(string mode = "weak")
        {
            Database = Path.Combine(Cell.Root, "binding-runtime.sqlite"); Store = new(Database, Cell.Scope.CellId, Customer, Cell.Clock);
            Store.BootstrapAdministrator("admin");
            if (mode == "weak") Vault = Cell.Vault;
            else
            {
                var options = Cell.Options with { RootPath = Path.Combine(Cell.Root, "binding-vault"),
                    RollbackProtection = new(Path.Combine(Cell.Root, "binding-rollback")) { Format = mode == "scoped" ? CredentialRollbackFormat.AuthenticatedScopeV2 : CredentialRollbackFormat.LegacyV1 } };
                Vault = mode == "scoped" ? CredentialVault.ProvisionStrongScoped(options, Cell.Keys, Cell.Clock) : CredentialVault.ProvisionStrong(options, Cell.Keys, Cell.Clock);
            }
            Authority = FoundationCellActivationAuthority.Provision(Path.Combine(Cell.Root, "binding-activation"), Customer, Cell.Scope.CellId, Cell.Clock);
            Authority.Activate(Database, Instance, 0, null);
        }
        internal FoundationCredentialRuntimeBinding Bind() => FoundationCredentialBoundTransport.BindCurrent(Store, Vault, Authority,
            Vault.DescribeNamespace(), Authority.DescribeIdentity(), Instance);
        internal FoundationStore Reopen() => new(Database, Cell.Scope.CellId, Customer, Cell.Clock);
        internal void Work(string state)
        {
            foreach (var (actor, role) in new[] { ("requester", "Requester"), ("approver", "Approver"), ("operator", "Operator") })
                Store.SetMembership(Customer, "admin", actor, "default", "dev", [role]);
            Store.PutConnection(Customer, "admin", Cell.Connection.Id, Cell.Connection.Provider, Cell.Dispatch.Connection.ConfigurationJson);
            var plan = Store.CreatePlan(Customer, "requester", new("default", "dev", Cell.Connection.Id, "pipeline.dispatch", Cell.Dispatch.Plan.PayloadJson,
                true, Cell.Clock.Now.AddMinutes(20), Cell.Request.SourceRevision));
            Store.ApprovePlan(Customer, "approver", plan.Id, plan.Digest); var queued = Store.EnqueuePlan(Customer, "operator", plan.Id, plan.Digest, "binding-cutover");
            if (state != "Queued") { var claim = Store.ClaimNext(Customer, "binding-worker", TimeSpan.FromMinutes(1))!; if (state == "Dispatched") Store.StartDispatch(Customer, queued.Id, "binding-worker", claim.Fence); }
        }
        internal long Number(string sql) { using var db = Open(Database); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        internal void Sql(string sql) => Sql(Database, sql);
        internal static void Sql(string path, string sql) { using var db = Open(path); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        internal void WithoutTrigger(string name, string sql)
        {
            using var db = Open(Database); using var command = db.CreateCommand(); command.CommandText = "SELECT sql FROM sqlite_master WHERE name=$name"; command.Parameters.AddWithValue("$name", name);
            var original = (string)command.ExecuteScalar()!; command.Parameters.Clear(); command.CommandText = "DROP TRIGGER " + name + ";" + sql + ";" + original; command.ExecuteNonQuery();
        }
        internal string[] Rows() => Rows(Database);
        internal static string[] Rows(string path)
        {
            using var db = Open(path); using var command = db.CreateCommand(); command.CommandText = "SELECT * FROM credential_runtime_bindings ORDER BY id";
            using var reader = command.ExecuteReader(); var result = new List<string>();
            while (reader.Read()) result.Add(JsonSerializer.Serialize(Enumerable.Range(0, reader.FieldCount).Select(reader.GetValue).ToArray())); return result.ToArray();
        }
        internal string[] NamespaceFiles() => Directory.EnumerateFiles(Cell.Root, "*", SearchOption.AllDirectories)
            .Where(path => !path.EndsWith(".sqlite", StringComparison.OrdinalIgnoreCase) && !path.EndsWith(".db", StringComparison.OrdinalIgnoreCase))
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(Cell.Root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))).ToArray();
        static SqliteConnection Open(string path) { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = path, Pooling = false }.ToString()); db.Open(); return db; }
        public void Dispose() => Cell.Dispose();
    }
}
