using System.Text.Json;

namespace SelfService.Foundation;

internal sealed partial class CredentialRollbackAuthority
{
    // Pure retained metadata validation. Never opens source paths and never claims the MAC has been authenticated.
    internal static IReadOnlyList<CredentialAbsenceScopeProof> ValidateScopedRecoverySnapshot(CredentialNamespaceCustody.Marker vaultMarker,
        CredentialNamespaceCustody.Marker authorityMarker, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, CredentialScopedRecoveryInventory inventory)
        => ValidateScopedRecoverySnapshotCore(vaultMarker, authorityMarker, files, inventory, CredentialRecoveryContentProfile.GeneralScopedV2);

    internal static IReadOnlyList<CredentialAbsenceScopeProof> ValidateRestrictedRecoverySnapshot(CredentialNamespaceCustody.Marker vaultMarker,
        CredentialNamespaceCustody.Marker authorityMarker, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, CredentialRestrictedRecoveryInventory inventory)
    {
        inventory.ValidateIntegrity();
        return ValidateScopedRecoverySnapshotCore(vaultMarker, authorityMarker, files, inventory.ScopedInventory, CredentialRecoveryContentProfile.RestrictedScopedV3);
    }

    static IReadOnlyList<CredentialAbsenceScopeProof> ValidateScopedRecoverySnapshotCore(CredentialNamespaceCustody.Marker vaultMarker,
        CredentialNamespaceCustody.Marker authorityMarker, IReadOnlyDictionary<string, ReadOnlyMemory<byte>> files, CredentialScopedRecoveryInventory inventory,
        CredentialRecoveryContentProfile contentProfile)
    {
        T Parse<T>(string name)
        {
            if (!files.TryGetValue(name, out var bytes)) throw CredentialScopedRecoverySets.Failure("contents-invalid");
            using var document = CredentialRules.Document(bytes);
            if (typeof(T) == typeof(ScopedState))
            {
                if (!document.RootElement.TryGetProperty("profiles", out var profiles) || profiles.ValueKind != JsonValueKind.Object)
                    throw CredentialScopedRecoverySets.Failure("contents-invalid");
                if (profiles.EnumerateObject().Take(MaximumProfiles + 1).Count() > MaximumProfiles) throw CredentialScopedRecoverySets.Failure("capacity-exceeded");
            }
            return document.RootElement.Deserialize<T>(CredentialScopedRecoverySets.Json) ?? throw CredentialScopedRecoverySets.Failure("contents-invalid");
        }
        inventory.ValidateIntegrity(); var identity = inventory.Namespace;
        var kinds = contentProfile switch
        {
            CredentialRecoveryContentProfile.GeneralScopedV2 => (Vault: "credential-vault-scoped-fenced", Authority: "credential-rollback-scoped-authority"),
            CredentialRecoveryContentProfile.RestrictedScopedV3 => (Vault: "credential-vault-scoped-bound-dispatch", Authority: "credential-rollback-scoped-bound-authority"),
            _ => throw CredentialScopedRecoverySets.Failure("contents-invalid")
        };
        var binding = Parse<Binding>("vault/rollback-binding.json"); var other = Parse<Binding>("authority/authority.json");
        if (binding != other || binding.Schema != 2 || binding.Kind != "credential-rollback-scoped-binding" ||
            vaultMarker.Kind != kinds.Vault || authorityMarker.Kind != kinds.Authority ||
            vaultMarker.CustomerId != identity.CustomerId || vaultMarker.CellId != identity.CellId ||
            authorityMarker.CustomerId != identity.CustomerId || authorityMarker.CellId != identity.CellId || binding.VaultNamespaceId == binding.AuthorityNamespaceId ||
            string.Equals(binding.VaultPath, binding.AuthorityPath, StringComparison.OrdinalIgnoreCase) ||
            binding.VaultPath.StartsWith(binding.AuthorityPath + "\\", StringComparison.OrdinalIgnoreCase) || binding.AuthorityPath.StartsWith(binding.VaultPath + "\\", StringComparison.OrdinalIgnoreCase) ||
            binding.CustomerId != identity.CustomerId || binding.CellId != identity.CellId || binding.VaultNamespaceId != identity.VaultNamespaceId ||
            binding.VaultNamespaceId != vaultMarker.NamespaceId || binding.VaultPath != vaultMarker.RootPath || binding.AuthorityNamespaceId != identity.AuthorityNamespaceId ||
            binding.AuthorityNamespaceId != authorityMarker.NamespaceId || binding.AuthorityPath != authorityMarker.RootPath ||
            vaultMarker.OwnerSid != authorityMarker.OwnerSid || vaultMarker.Sha256 != identity.NamespaceCommitmentSha256 ||
            binding.Sha256 != BindingDigest(binding) || binding.Sha256 != identity.AuthorityBindingSha256) throw CredentialScopedRecoverySets.Failure("contents-invalid");
        var stored = Parse<ScopedState>("authority/state.json");
        if (stored.Schema != 2 || stored.Revision != inventory.AuthorityRevision || stored.Revision is < 0 or > MaximumCounter ||
            stored.Profiles is null || stored.Profiles.Count > MaximumProfiles || stored.Profiles.Values.Any(e => e is null)) throw CredentialScopedRecoverySets.Failure("contents-invalid");
        var state = new State(stored.Schema, stored.Revision, stored.Profiles.ToDictionary(p => p.Key,
            p => new Entry(p.Value.Generation, p.Value.EnvelopeSha256, p.Value.EnvelopeLength, p.Value.Pending) { AbsenceProof = p.Value.AbsenceProof }, StringComparer.Ordinal), stored.Sha256);
        if (state.Sha256 != ScopedStateDigest(state, binding) || state.Sha256 != inventory.AuthorityStateSha256 ||
            state.Profiles.Count != inventory.ProfileDependencies.Count + inventory.AbsenceScopes.Count) throw CredentialScopedRecoverySets.Failure("contents-invalid");
        var proofs = new List<CredentialAbsenceScopeProof>();
        foreach (var pair in state.Profiles.OrderBy(p => p.Key, StringComparer.Ordinal))
        {
            var entry = pair.Value;
            if (!CredentialRecoverySets.DigestShape(pair.Key) || entry.Pending is not null || entry.Generation is < 0 or > MaximumCounter)
                throw CredentialScopedRecoverySets.Failure("contents-invalid");
            if (entry.Generation > 0)
            {
                if (entry.AbsenceProof is not null || !CredentialRecoverySets.DigestShape(entry.EnvelopeSha256) || entry.EnvelopeLength is < 1 or > 131072)
                    throw CredentialScopedRecoverySets.Failure("contents-invalid");
                var profile = inventory.ProfileDependencies.SingleOrDefault(p => p.ProfileIdSha256 == pair.Key);
                if (profile is null || profile.Generation != entry.Generation || profile.EnvelopeSha256 != entry.EnvelopeSha256 || profile.EnvelopeLength != entry.EnvelopeLength)
                    throw CredentialScopedRecoverySets.Failure("contents-invalid");
                continue;
            }
            var proof = entry.AbsenceProof;
            if (proof is null || entry.EnvelopeSha256 != "" || entry.EnvelopeLength != 0) throw CredentialScopedRecoverySets.Failure("contents-invalid");
            CredentialAbsenceScopeProofs.Validate(proof);
            if (proof.CustomerId != identity.CustomerId || proof.CellId != identity.CellId || proof.VaultNamespaceId != vaultMarker.NamespaceId ||
                proof.VaultNamespaceCommitmentSha256 != vaultMarker.Sha256 || proof.AuthorityNamespaceId != authorityMarker.NamespaceId ||
                proof.AuthorityNamespaceCommitmentSha256 != authorityMarker.Sha256 || proof.BindingSha256 != binding.Sha256 || proof.ProfileIdSha256 != pair.Key)
                throw CredentialScopedRecoverySets.Failure("contents-invalid");
            var scope = inventory.AbsenceScopes.SingleOrDefault(p => p.ProfileIdSha256 == pair.Key);
            if (scope is null || scope.ScopeSha256 != proof.ScopeSha256 || scope.ProjectId != proof.Scope.ProjectId || scope.EnvironmentId != proof.Scope.EnvironmentId ||
                scope.SourceKeyId != proof.KeyId || scope.ProofSha256 != proof.Sha256) throw CredentialScopedRecoverySets.Failure("contents-invalid");
            proofs.Add(proof);
        }
        if (proofs.Count != inventory.AbsenceScopes.Count || state.Profiles.Count(p => p.Value.Generation > 0) != inventory.ProfileDependencies.Count)
            throw CredentialScopedRecoverySets.Failure("contents-invalid");
        return Array.AsReadOnly(proofs.ToArray());
    }
}
