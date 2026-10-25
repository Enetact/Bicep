using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    public const int MaximumCredentialRuntimeBindings = 1024;
    internal string RuntimeDatabasePath => databasePath;

    // Trusted existing-file preflight. No CREATE, migrations, bootstrap, journal-mode PRAGMA or original-root access.
    // SQLite may still need its normal read-side locking/sidecars; this is not a physical file-immutability promise.
    public static bool ExistingCredentialRuntimeBindingRequired(string databasePath, string expectedCustomerId, string expectedCellId)
    {
        ValidateId(expectedCustomerId); ValidateId(expectedCellId);
        var path = Path.GetFullPath(databasePath); RejectLinks(path);
        if (!File.Exists(path)) throw new FoundationStoreException("ExistingCellRequired");
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder
            { DataSource = path, Mode = SqliteOpenMode.ReadOnly, Pooling = false }.ToString());
        db.Open(); ValidateSnapshot(db, expectedCellId, expectedCustomerId);
        var version = Number(db, null, "SELECT schema_version FROM cell WHERE singleton=1");
        var count = version >= 12 ? ReadCredentialRuntimeBindings(db, null).Count : 0;
        return CredentialRuntimeBindingRequired(db, null, count);
    }

    public FoundationCredentialRuntimeBindingStatus GetCredentialRuntimeBindingStatus()
    {
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true); AssertActivation(db, tx);
        var rows = ReadCredentialRuntimeBindings(db, tx); var instance = ReadInstanceIdentity(db, tx, CustomerId, CellId);
        var current = rows.SingleOrDefault(binding => binding.DatabaseInstanceId == instance.DatabaseInstanceId);
        var result = new FoundationCredentialRuntimeBindingStatus(CustomerId, CellId, instance.DatabaseInstanceId,
            CredentialRuntimeBindingRequired(db, tx, rows.Count), current, rows.Count - (current is null ? 0 : 1));
        tx.Commit(); return result;
    }

    // Only the concrete core session can invoke this after acquiring the real activation/vault metadata guards.
    // No record, actor string, callback, bool or request body can serve as proof of those guards.
    internal FoundationCredentialRuntimeBinding ProvisionCredentialRuntimeBinding(FoundationCredentialUseSession session)
    {
        ArgumentNullException.ThrowIfNull(session); session.AssertFor(this);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false); AssertActivation(db, tx);
        RequireNoRecoveryExecutionHold(db, tx);
        var rows = ReadCredentialRuntimeBindings(db, tx);
        if (CredentialRuntimeBindingRequired(db, tx, rows.Count)) throw new FoundationStoreException("CredentialRuntimeAlreadyBound", 403);
        if (Number(db, tx, "SELECT EXISTS(SELECT 1 FROM audit WHERE action='BackupRestored')") != 0 ||
            Number(db, tx, "SELECT count(*) FROM recovery_execution_holds") != 0)
            throw new FoundationStoreException("CredentialRuntimeRestoredCellDenied", 403);
        if (ScalarText(db, tx, "SELECT state FROM customer_lifecycle WHERE singleton=1") != "Active")
            throw new FoundationStoreException("CredentialRuntimeFreshActiveCellRequired", 403);
        if (session.ExpectedBindingSha256 is not null) throw new FoundationStoreException("CredentialRuntimeProvisionSessionInvalid", 403);
        if (Number(db, tx, "SELECT count(*) FROM jobs WHERE state IN ('Queued','Claimed','Dispatched')") != 0 ||
            Number(db, tx, "SELECT count(*) FROM worker_heartbeats WHERE state!='Stopped'") != 0)
            throw new FoundationStoreException("CredentialRuntimeQuiescenceRequired", 403);
        var instance = ReadInstanceIdentity(db, tx, CustomerId, CellId); var activation = session.ActivationBinding;
        ValidateCredentialRuntimeSession(this, session, instance.DatabaseInstanceId);
        var created = At(Now);
        if (created < activation.ActivatedUtc) throw new FoundationStoreException("CredentialRuntimeBindingTimeInvalid", 403);
        var binding = new FoundationCredentialRuntimeBinding(NewId(), CustomerId, CellId, instance.DatabaseInstanceId,
            CredentialRuntimeDatabasePathDigest(databasePath), session.Namespace, session.ActivationAuthority,
            activation.Generation, activation.Sha256, created, "");
        binding = binding with { Sha256 = CredentialRuntimeBindingDigest(binding) };
        Exec(db, tx, "INSERT INTO credential_runtime_bindings VALUES($c,$cell,$instance,$id,$path,$namespace,$authority,$generation,$activation,$created,$digest)",
            ("$c", CustomerId), ("$cell", CellId), ("$instance", binding.DatabaseInstanceId), ("$id", binding.Id), ("$path", binding.DatabasePathSha256),
            ("$namespace", JsonSerializer.Serialize(binding.Namespace, CredentialRecoverySets.Json)),
            ("$authority", JsonSerializer.Serialize(binding.ActivationAuthority, CredentialRecoverySets.Json)),
            ("$generation", binding.ActivationGeneration), ("$activation", binding.ActivationBindingSha256),
            ("$created", binding.CreatedAt.ToUnixTimeMilliseconds()), ("$digest", binding.Sha256));
        var scope = BootstrapScope(db, tx);
        Audit(db, tx, "cell-maintenance", scope.Project, scope.Environment, "CredentialRuntimeBound", binding.Id,
            "Succeeded", "FreshOwnerNamespaceBinding", binding.Sha256);
        _ = ReadCredentialRuntimeBindings(db, tx); session.AssertFor(this);
        try { tx.Commit(); }
        catch (Exception error) when (error is not StackOverflowException and not OutOfMemoryException)
        { throw new FoundationStoreException("CredentialRuntimeBindingUncertain", 503); }
        return binding;
    }

    internal FoundationCredentialRuntimeBinding RequireCredentialRuntimeBinding(FoundationCredentialUseSession session)
    {
        ArgumentNullException.ThrowIfNull(session); session.AssertFor(this);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true); AssertActivation(db, tx); RequireNoRecoveryExecutionHold(db, tx);
        var result = RequireCredentialRuntimeBinding(db, tx, session); tx.Commit(); return result;
    }

    FoundationCredentialRuntimeBinding RequireCredentialRuntimeBinding(SqliteConnection db, SqliteTransaction tx, FoundationCredentialUseSession session)
    {
        var instance = ReadInstanceIdentity(db, tx, CustomerId, CellId); ValidateCredentialRuntimeSession(this, session, instance.DatabaseInstanceId);
        var current = ReadCredentialRuntimeBindings(db, tx).SingleOrDefault(binding => binding.DatabaseInstanceId == instance.DatabaseInstanceId)
            ?? throw new FoundationStoreException("CredentialRuntimeBindingMissing", 403);
        if (session.ExpectedBindingSha256 is null || current.Sha256 != session.ExpectedBindingSha256 ||
            current.DatabasePathSha256 != CredentialRuntimeDatabasePathDigest(databasePath) || current.Namespace != session.Namespace ||
            current.ActivationAuthority != session.ActivationAuthority || current.ActivationGeneration != session.ActivationBinding.Generation ||
            current.ActivationBindingSha256 != session.ActivationBinding.Sha256)
            throw new FoundationStoreException("CredentialRuntimeBindingMismatch", 403);
        session.AssertFor(this); return current;
    }

    void RequireCredentialRuntimeDispatch(SqliteConnection db, SqliteTransaction tx, FoundationCredentialUseSession? session)
    {
        var rows = ReadCredentialRuntimeBindings(db, tx);
        if (session is null)
        {
            if (CredentialRuntimeBindingRequired(db, tx, rows.Count)) throw new FoundationStoreException("CredentialRuntimeBoundSessionRequired", 403);
            return; // Existing never-bound disconnected legacy path retains its previous behavior.
        }
        _ = RequireCredentialRuntimeBinding(db, tx, session); // The bound path never falls back on a never-bound cell.
    }

    static bool CredentialRuntimeBindingRequired(SqliteConnection db, SqliteTransaction? tx, int count) => count != 0 ||
        Number(db, tx, "SELECT EXISTS(SELECT 1 FROM audit WHERE action='CredentialRuntimeBound')") != 0;
}
