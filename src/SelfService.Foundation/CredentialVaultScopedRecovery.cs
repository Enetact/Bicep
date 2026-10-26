using System.Security.Cryptography;

namespace SelfService.Foundation;

public sealed partial class CredentialVault
{
    public async Task<CredentialScopedRecoveryInventory> InspectScopedRecoveryDependenciesAsync(
        CredentialVaultNamespaceIdentity expectedNamespace, CancellationToken cancellationToken = default)
    {
        RequireScopedRecoveryNamespace(expectedNamespace);
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken); var token = deadline.Token;
        try
        {
            token.ThrowIfCancellationRequested(); using var source = await OpenScopedRecoverySourceAsync(expectedNamespace, token);
            await source.AuthenticateAsync(token); await source.AssertCurrentAsync(token);
            var observed = ScopedRecoveryNow(); token.ThrowIfCancellationRequested();
            return source.Inventory(observed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Scoped credential recovery inventory canceled.", cancellationToken); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw ScopedRecoveryFailure("timeout"); }
        catch (OperationCanceledException) { throw ScopedRecoveryFailure("unavailable"); }
        catch (CredentialException) { throw; }
        catch (Exception) { throw ScopedRecoveryFailure("unavailable"); }
    }

    public async Task<CredentialScopedRecoverySet> CaptureScopedSignedRecoverySetAsync(CredentialVaultNamespaceIdentity expectedNamespace,
        RSA signer, string signerKeyId, long generation, DateTimeOffset notBeforeUtc, DateTimeOffset expiresUtc,
        CancellationToken cancellationToken = default)
    {
        RequireScopedRecoveryNamespace(expectedNamespace);
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken); var token = deadline.Token;
        CredentialScopedRecoverySet? result = null;
        try
        {
            token.ThrowIfCancellationRequested(); using var source = await OpenScopedRecoverySourceAsync(expectedNamespace, token);
            // Scope/current namespace/global pending checks precede signer use; validate and freeze the signer before source-key access.
            using var publicKey = FoundationStore.ValidateBackupSigner(signer); var issued = ScopedRecoveryNow();
            CredentialScopedRecoverySets.RequireSignature(new(2, "credential-scoped-recovery-set", Guid.NewGuid().ToString("N"),
                options.CustomerId, options.CellId, expectedNamespace, generation, issued, issued, notBeforeUtc, expiresUtc,
                new string('a', 64), 0, 0, new string('b', 64), 2, signerKeyId,
                FoundationAuditAnchors.PublicKeyFingerprint(publicKey), "RSA-PSS-SHA256", ""), allowUnsigned: true);
            await source.AssertCurrentAsync(token); await source.AuthenticateAsync(token); await source.AssertCurrentAsync(token);
            var observed = ScopedRecoveryNow(); token.ThrowIfCancellationRequested(); var inventory = source.Inventory(observed);
            result = CredentialScopedRecoverySets.Create(inventory, source.Files, signer, publicKey, signerKeyId, generation, observed, notBeforeUtc, expiresUtc);
            await source.AssertCurrentAsync(token);
            var completed = ScopedRecoveryNow(); CredentialScopedRecoverySets.ValidateLifetime(result.Signature, completed); token.ThrowIfCancellationRequested();
            var captured = result; result = null; return captured;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Scoped credential recovery capture canceled.", cancellationToken); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw ScopedRecoveryFailure("timeout"); }
        catch (OperationCanceledException) { throw ScopedRecoveryFailure("unavailable"); }
        catch (CredentialException) { throw; }
        catch (Exception) { throw ScopedRecoveryFailure("unavailable"); }
        finally { result?.Dispose(); }
    }

    void RequireScopedRecoveryNamespace(CredentialVaultNamespaceIdentity expected)
    {
        RequireRecoverySourceProfile("scoped");
        if (!ScopedRollbackFormat) throw ScopedRecoveryFailure("scoped-format-required");
        CredentialRecoverySets.RequireIdentity(expected);
        if (expected.CustomerId != options.CustomerId || expected.CellId != options.CellId || expected.AuthorityNamespaceId is null)
            throw ScopedRecoveryFailure("namespace-mismatch");
    }
    DateTimeOffset ScopedRecoveryNow() => DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());

    async Task<ScopedRecoverySource> OpenScopedRecoverySourceAsync(CredentialVaultNamespaceIdentity expected, CancellationToken token)
    {
        FileStream? vaultGuard = null, authorityGuard = null; ScopedRecoverySource? source = null;
        try
        {
            vaultGuard = await Gate(token); authorityGuard = await RollbackGate(token); var identity = NamespaceIdentity();
            if (identity != expected) throw ScopedRecoveryFailure("namespace-mismatch");
            var authorityCustody = new CredentialNamespaceCustody(options.RollbackProtection!.AuthorityDirectory,
                RequiresBoundDispatchSession ? "credential-rollback-scoped-bound-authority" : "credential-rollback-scoped-authority",
                options.CustomerId, options.CellId);
            if (authorityCustody.NamespaceId != identity.AuthorityNamespaceId) throw ScopedRecoveryFailure("namespace-mismatch");
            var state = rollback!.ReadState();
            if (state.Profiles.Values.Any(entry => entry.Pending is not null)) throw ScopedRecoveryFailure("pending-reconciliation");
            var proofs = rollback.SnapshotAbsenceProofs(state);
            foreach (var proof in proofs) { rollback.ValidateProofBinding(proof); RequireActualAbsence(proof.ProfileIdSha256); }
            var before = await CaptureRecoveryInventory(authorityCustody, token); ValidateRecoveryCommitments(before, state);
            source = new(this, identity, authorityCustody, state, proofs, before, vaultGuard, authorityGuard);
            vaultGuard = null; authorityGuard = null;
            await source.ReadFilesAsync(token); await source.AssertCurrentAsync(token); var retained = source; source = null; return retained;
        }
        finally { source?.Dispose(); authorityGuard?.Dispose(); vaultGuard?.Dispose(); }
    }

    // Only these concrete private instances own source guards and retained bytes; callers cannot inject a lease or callback.
    sealed class ScopedRecoverySource(CredentialVault owner, CredentialVaultNamespaceIdentity identity,
        CredentialNamespaceCustody authorityCustody, CredentialRollbackAuthority.State state,
        IReadOnlyList<CredentialAbsenceScopeProof> proofs, RecoveryInventoryCapture before,
        FileStream vaultGuard, FileStream authorityGuard) : ICredentialKeyProvider, IDisposable
    {
        readonly Dictionary<string, CredentialEncryptionKey> keys = new(StringComparer.Ordinal);
        readonly List<CredentialRecoveryProfileDependency> profiles = [];
        readonly List<CredentialAbsenceScopeDependency> absences = [];
        internal SortedDictionary<string, byte[]> Files { get; } = new(StringComparer.Ordinal);
        bool authenticated;

        internal async Task ReadFilesAsync(CancellationToken token)
        {
            foreach (var pair in before.Files)
            {
                token.ThrowIfCancellationRequested(); var separator = pair.Key.IndexOf('/'); var name = pair.Key[(separator + 1)..];
                var source = pair.Key.StartsWith("vault/", StringComparison.Ordinal) ? owner.custody : authorityCustody;
                var bytes = name is ".vault.lock" or ".rollback.lock" ? [] : await source.ReadFileAsync(Path.Combine(source.Root, name),
                    name == "state.json" ? 1048576 : name.EndsWith(".credential.json", StringComparison.Ordinal) ? 131072 : 16384, token);
                Files.Add(pair.Key, bytes);
                if (bytes.Length != pair.Value.Length || CredentialRecoverySets.Hash(bytes) != pair.Value.Sha256) throw ScopedRecoveryFailure("changed");
            }
        }
        internal async Task AuthenticateAsync(CancellationToken token)
        {
            if (authenticated) throw ScopedRecoveryFailure("source-already-authenticated");
            foreach (var name in before.ProfileNames)
            {
                token.ThrowIfCancellationRequested(); var dependency = await AuthenticateRecoveryEnvelopeAsync(Files["vault/" + name], name[..64], identity, this, token);
                if (!state.Profiles.TryGetValue(dependency.ProfileIdSha256, out var committed) || committed.Generation != dependency.Generation || committed.AbsenceProof is not null ||
                    dependency.EnvelopeSha256 != committed.EnvelopeSha256 || dependency.EnvelopeLength != committed.EnvelopeLength)
                    throw ScopedRecoveryFailure("state-mismatch");
                profiles.Add(dependency);
            }
            foreach (var proof in proofs)
            {
                token.ThrowIfCancellationRequested(); using var key = await GetKeyAsync(identity.CustomerId, identity.CellId, proof.KeyId, token); var bytes = key.CopyBytes();
                try { owner.rollback!.ValidateProofBinding(proof); CredentialAbsenceScopeProofs.Authenticate(proof, bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                owner.RequireActualAbsence(proof.ProfileIdSha256);
                absences.Add(new(proof.ProfileIdSha256, proof.ScopeSha256, proof.Scope.ProjectId, proof.Scope.EnvironmentId, proof.KeyId, proof.Sha256));
            }
            if (profiles.Count + absences.Count != state.Profiles.Count || profiles.Count + absences.Count > CredentialRecoveryInventory.MaximumProfiles)
                throw ScopedRecoveryFailure("state-mismatch");
            token.ThrowIfCancellationRequested(); authenticated = true;
        }
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (customerId != identity.CustomerId || cellId != identity.CellId || !CredentialRules.ProfileId(keyId)) throw ScopedRecoveryFailure("key-binding-invalid");
            if (!keys.TryGetValue(keyId, out var cached))
            {
                using var borrowed = await owner.Key(keyId, token); token.ThrowIfCancellationRequested();
                var bytes = borrowed.CopyBytes();
                try { cached = new(bytes); keys.Add(keyId, cached); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                await AssertCurrentAsync(token);
            }
            var copy = cached.CopyBytes();
            try { return new CredentialEncryptionKey(copy); }
            finally { CryptographicOperations.ZeroMemory(copy); }
        }
        internal async Task AssertCurrentAsync(CancellationToken token)
        {
            var after = await owner.CaptureRecoveryInventory(authorityCustody, token); var current = owner.rollback!.ReadState(); ValidateRecoveryCommitments(after, current);
            if (current.Profiles.Values.Any(entry => entry.Pending is not null) || current.Revision != state.Revision || current.Sha256 != state.Sha256 ||
                owner.NamespaceIdentity() != identity || !before.Files.SequenceEqual(after.Files)) throw ScopedRecoveryFailure("changed");
            foreach (var proof in proofs) { owner.rollback.ValidateProofBinding(proof); owner.RequireActualAbsence(proof.ProfileIdSha256); }
            token.ThrowIfCancellationRequested();
        }
        internal CredentialScopedRecoveryInventory Inventory(DateTimeOffset observed)
        {
            if (!authenticated) throw ScopedRecoveryFailure("source-not-authenticated");
            if (owner.RequiresBoundDispatchSession) throw ScopedRecoveryFailure("restricted-source-projection-unsupported");
            return new(identity, observed, profiles, absences, state.Revision, state.Sha256);
        }
        internal CredentialRestrictedRecoveryInventory RestrictedInventory(DateTimeOffset observed)
        {
            if (!authenticated || !owner.RequiresBoundDispatchSession) throw RestrictedRecoveryFailure("source-not-authenticated");
            return new(identity, observed, profiles, absences, state.Revision, state.Sha256);
        }
        public void Dispose()
        {
            foreach (var key in keys.Values) key.Dispose(); foreach (var bytes in Files.Values) CryptographicOperations.ZeroMemory(bytes);
            authorityGuard.Dispose(); vaultGuard.Dispose();
        }
    }
    static CredentialException ScopedRecoveryFailure(string reason) => new("credential-scoped-recovery-source-" + reason);
}
