using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed partial class FoundationStore
{
    static string CredentialRuntimeDatabasePathDigest(string path) => Hash(JsonSerializer.Serialize(new
    { schema = "platform-studio.foundation-credential-runtime-database-path.v1", path = OperatingSystem.IsWindows() ? Path.GetFullPath(path).ToUpperInvariant() : Path.GetFullPath(path) }, CredentialRecoverySets.Json));

    static string CredentialRuntimeBindingDigest(FoundationCredentialRuntimeBinding binding) => Hash(JsonSerializer.Serialize(new
    {
        schema = binding.Schema, binding.Id, binding.CustomerId, binding.CellId, binding.DatabaseInstanceId, binding.DatabasePathSha256,
        binding.Namespace, binding.ActivationAuthority, binding.ActivationGeneration, binding.ActivationBindingSha256,
        created = binding.CreatedAt.ToUnixTimeMilliseconds()
    }, CredentialRecoverySets.Json));

    static void ValidateCredentialRuntimeSession(FoundationStore store, FoundationCredentialUseSession session, string instance)
    {
        session.AssertFor(store); var activation = session.ActivationBinding;
        if (activation.CustomerId != store.CustomerId || activation.CellId != store.CellId || activation.DatabaseInstanceId != instance ||
            !string.Equals(activation.DatabasePath, store.databasePath, OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal) ||
            session.Namespace.CustomerId != store.CustomerId || session.Namespace.CellId != store.CellId ||
            session.ActivationAuthority.CustomerId != store.CustomerId || session.ActivationAuthority.CellId != store.CellId ||
            session.ActivationAuthority.AuthorityId != activation.AuthorityId)
            throw new FoundationStoreException("CredentialRuntimeSessionMismatch", 403);
    }

    static List<FoundationCredentialRuntimeBinding> ReadCredentialRuntimeBindings(SqliteConnection db, SqliteTransaction? tx)
    {
        try
        {
            var cell = Rows(db, tx, "SELECT customer_id,cell_id FROM cell WHERE singleton=1", r => (Customer: r.GetString(0), Cell: r.GetString(1))).Single();
            var rows = Rows(db, tx, """
                SELECT id,customer_id,cell_id,database_instance_id,database_path_sha256,namespace_json,activation_authority_json,
                  activation_generation,activation_binding_sha256,created,digest FROM credential_runtime_bindings ORDER BY created,id LIMIT $limit
                """, r =>
            {
                var namespaceJson = r.GetString(5); var authorityJson = r.GetString(6);
                if (namespaceJson.Length > 4096 || authorityJson.Length > 4096) throw new FoundationStoreException("CredentialRuntimeBindingIntegrityFailed");
                var ns = JsonSerializer.Deserialize<CredentialVaultNamespaceIdentity>(namespaceJson, CredentialRecoverySets.Json)!;
                var authority = JsonSerializer.Deserialize<FoundationCellActivationAuthorityIdentity>(authorityJson, CredentialRecoverySets.Json)!;
                if (JsonSerializer.Serialize(ns, CredentialRecoverySets.Json) != namespaceJson || JsonSerializer.Serialize(authority, CredentialRecoverySets.Json) != authorityJson)
                    throw new FoundationStoreException("CredentialRuntimeBindingIntegrityFailed");
                return new FoundationCredentialRuntimeBinding(r.GetString(0), r.GetString(1), r.GetString(2), r.GetString(3), r.GetString(4), ns, authority,
                    r.GetInt64(7), r.GetString(8), At(r.GetInt64(9)), r.GetString(10));
            }, ("$limit", MaximumCredentialRuntimeBindings + 1));
            if (rows.Count > MaximumCredentialRuntimeBindings) throw new FoundationStoreException("CredentialRuntimeBindingCapacityExceeded");
            foreach (var row in rows)
            {
                var ns = row.Namespace; var authority = row.ActivationAuthority;
                if (!ValidDatabaseInstanceId(row.Id) || row.CustomerId != cell.Customer || row.CellId != cell.Cell ||
                    !ValidDatabaseInstanceId(row.DatabaseInstanceId) || !MaintenanceHash(row.DatabasePathSha256) || ns is null || authority is null ||
                    ns.CustomerId != cell.Customer || ns.CellId != cell.Cell || !ValidDatabaseInstanceId(ns.VaultNamespaceId) || !MaintenanceHash(ns.NamespaceCommitmentSha256) ||
                    (ns.AuthorityNamespaceId is null ? ns.AuthorityBindingSha256 is not null : !ValidDatabaseInstanceId(ns.AuthorityNamespaceId) ||
                        ns.AuthorityNamespaceId == ns.VaultNamespaceId || !MaintenanceHash(ns.AuthorityBindingSha256)) ||
                    authority.CustomerId != cell.Customer || authority.CellId != cell.Cell || !ValidDatabaseInstanceId(authority.AuthorityId) ||
                    !MaintenanceHash(authority.CustodyCommitmentSha256) || row.ActivationGeneration is < 1 or > FoundationCellActivationAuthority.MaximumGenerations ||
                    !MaintenanceHash(row.ActivationBindingSha256) || row.CreatedAt.ToUnixTimeMilliseconds() <= 0 ||
                    !MaintenanceHash(row.Sha256) || row.Sha256 != CredentialRuntimeBindingDigest(row) ||
                    Number(db, tx, "SELECT count(*) FROM audit WHERE customer_id=$c AND cell_id=$cell AND action='CredentialRuntimeBound' AND target_id=$id AND outcome='Succeeded' AND reason='FreshOwnerNamespaceBinding' AND correlation=$digest AND created>=$created",
                        ("$c", cell.Customer), ("$cell", cell.Cell), ("$id", row.Id), ("$digest", row.Sha256), ("$created", row.CreatedAt.ToUnixTimeMilliseconds())) != 1)
                    throw new FoundationStoreException("CredentialRuntimeBindingIntegrityFailed");
            }
            return rows;
        }
        catch (FoundationStoreException) { throw; }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or OverflowException or InvalidCastException or NullReferenceException)
        { throw new FoundationStoreException("CredentialRuntimeBindingIntegrityFailed"); }
    }
}
