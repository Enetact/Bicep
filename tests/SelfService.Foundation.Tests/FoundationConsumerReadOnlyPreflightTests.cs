using System.Reflection;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationConsumerReadOnlyPreflightTests
{
    [Theory]
    [InlineData(1)] [InlineData(2)] [InlineData(3)] [InlineData(4)] [InlineData(5)] [InlineData(6)]
    [InlineData(7)] [InlineData(8)] [InlineData(9)] [InlineData(10)] [InlineData(11)] [InlineData(12)] [InlineData(13)]
    [InlineData(FoundationStore.CurrentSchemaVersion)]
    public void Valid_frozen_schema_preflight_preserves_actual_bytes_version_jobs_and_audit_without_migrating(int version)
    {
        using var f = new StoreFixture(); _ = f.Queue(); Downgrade(f, version);
        var bytes = File.ReadAllBytes(f.Path); var audits = f.Scalar("SELECT count(*) FROM audit"); var jobs = f.Scalar("SELECT count(*) FROM jobs");
        Assert.False(FoundationStore.ExistingCredentialRuntimeBindingRequired(f.Path, f.Customer, f.Cell));
        Assert.Equal(bytes, File.ReadAllBytes(f.Path)); Assert.Equal(version, f.Scalar("SELECT schema_version FROM cell"));
        Assert.Equal(audits, f.Scalar("SELECT count(*) FROM audit")); Assert.Equal(jobs, f.Scalar("SELECT count(*) FROM jobs"));
        Assert.Equal(version >= 12 ? 1 : 0, f.Scalar("SELECT count(*) FROM sqlite_master WHERE name='credential_runtime_bindings'"));
    }

    [Theory]
    [InlineData("table")]
    [InlineData("history")]
    [InlineData("audit")]
    public void Malformed_existing_layout_history_or_audit_is_refused_without_repair(string fault)
    {
        using var f = new StoreFixture();
        if (fault == "table") f.Execute("DROP TABLE outbox");
        else if (fault == "history") f.Execute("DROP TRIGGER schema_migrations_no_update;UPDATE schema_migrations SET checksum='changed' WHERE version=1;CREATE TRIGGER schema_migrations_no_update BEFORE UPDATE ON schema_migrations BEGIN SELECT RAISE(ABORT,'immutable migration history'); END;");
        else f.Execute("DROP TRIGGER audit_no_update;UPDATE audit SET correlation='changed' WHERE sequence=1;CREATE TRIGGER audit_no_update BEFORE UPDATE ON audit BEGIN SELECT RAISE(ABORT,'append-only audit'); END;");
        var bytes = File.ReadAllBytes(f.Path);
        Assert.Throws<FoundationStoreException>(() => FoundationStore.ExistingCredentialRuntimeBindingRequired(f.Path, f.Customer, f.Cell));
        Assert.Equal(bytes, File.ReadAllBytes(f.Path));
    }

    [Fact]
    public void Foreign_scope_and_missing_database_do_not_create_or_adopt_anything()
    {
        using var f = new StoreFixture(); var before = File.ReadAllBytes(f.Path); var missing = f.Path + ".missing";
        Assert.Throws<FoundationStoreException>(() => FoundationStore.ExistingCredentialRuntimeBindingRequired(f.Path, "foreign-customer", f.Cell));
        Assert.Equal("ExistingCellRequired", Assert.Throws<FoundationStoreException>(() => FoundationStore.ExistingCredentialRuntimeBindingRequired(missing, f.Customer, f.Cell)).Code);
        Assert.Equal(before, File.ReadAllBytes(f.Path)); Assert.False(File.Exists(missing));
    }

    [Fact]
    public void Retained_binding_audit_in_a_legacy_layout_cannot_be_mistaken_for_never_bound_policy()
    {
        using var f = new StoreFixture(); Downgrade(f, 11);
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Path, Pooling = false }.ToString()))
        {
            db.Open(); using var tx = db.BeginTransaction();
            typeof(FoundationStore).GetMethod("Audit", BindingFlags.Instance | BindingFlags.NonPublic)!.Invoke(f.Store,
                [db, tx, "cell-maintenance", "default", "dev", "CredentialRuntimeBound", new string('a', 32), "Succeeded", "FreshOwnerNamespaceBinding", new string('b', 64)]);
            tx.Commit();
        }
        var bytes = File.ReadAllBytes(f.Path); Assert.True(FoundationStore.ExistingCredentialRuntimeBindingRequired(f.Path, f.Customer, f.Cell));
        Assert.Equal(11, f.Scalar("SELECT schema_version FROM cell")); Assert.Equal(bytes, File.ReadAllBytes(f.Path));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM sqlite_master WHERE name='credential_runtime_bindings'"));
    }

    static void Downgrade(StoreFixture f, int version) =>
        // Reuse the already qualified genuine frozen-layout fixture, including the pre-v5 cell-column removal.
        typeof(FoundationBackupSchemaAdmissionTests).GetMethod("Downgrade", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [f, version]);
}
