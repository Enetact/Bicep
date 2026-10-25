using System.Security.Cryptography;

namespace SelfService.Foundation;

public sealed partial class CredentialVault
{
    // Explicit source-v2 inspection only. No full inventory, capture, retention or customer-review authority is produced.
    public async Task<CredentialAbsenceScopeInspection> InspectAbsenceScopeProvenanceAsync(
        CredentialVaultNamespaceIdentity expectedNamespace, CancellationToken cancellationToken = default)
    {
        if (!ScopedRollbackFormat) throw AbsenceInspectionFailure("scoped-format-required");
        if (expectedNamespace is null || expectedNamespace.CustomerId != options.CustomerId || expectedNamespace.CellId != options.CellId)
            throw AbsenceInspectionFailure("namespace-mismatch");
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken); var token = deadline.Token;
        var cachedKeys = new Dictionary<string, CredentialEncryptionKey>(StringComparer.Ordinal);
        try
        {
            token.ThrowIfCancellationRequested(); using var gate = await Gate(token); using var authorityGate = await RollbackGate(token);
            var identity = NamespaceIdentity(); if (identity != expectedNamespace) throw AbsenceInspectionFailure("namespace-mismatch");
            var authorityCustody = new CredentialNamespaceCustody(options.RollbackProtection!.AuthorityDirectory,
                "credential-rollback-scoped-authority", options.CustomerId, options.CellId);
            if (authorityCustody.NamespaceId != identity.AuthorityNamespaceId) throw AbsenceInspectionFailure("namespace-mismatch");
            var state = rollback!.ReadState();
            if (state.Profiles.Values.Any(entry => entry.Pending is not null)) throw AbsenceInspectionFailure("pending-reconciliation");
            var proofs = rollback.SnapshotAbsenceProofs(state);
            foreach (var proof in proofs) { rollback.ValidateProofBinding(proof); RequireActualAbsence(proof.ProfileIdSha256); }
            var before = await CaptureRecoveryInventory(authorityCustody, token); ValidateRecoveryCommitments(before, state);
            var authenticated = new List<CredentialAbsenceScopeDependency>(proofs.Count);
            foreach (var proof in proofs)
            {
                token.ThrowIfCancellationRequested();
                if (!cachedKeys.TryGetValue(proof.KeyId, out var frozen))
                {
                    // The original proof key is mandatory. Clone provider handles so later alias disposal cannot change the cached key.
                    using var borrowed = await Key(proof.KeyId, token); token.ThrowIfCancellationRequested();
                    await RequireUnchanged(); var bytes = borrowed.CopyBytes();
                    try { frozen = new CredentialEncryptionKey(bytes); cachedKeys.Add(proof.KeyId, frozen); }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
                var keyBytes = frozen.CopyBytes();
                try { rollback.ValidateProofBinding(proof); CredentialAbsenceScopeProofs.Authenticate(proof, keyBytes); }
                finally { CryptographicOperations.ZeroMemory(keyBytes); }
                RequireActualAbsence(proof.ProfileIdSha256);
                authenticated.Add(new(proof.ProfileIdSha256, proof.ScopeSha256, proof.Scope.ProjectId,
                    proof.Scope.EnvironmentId, proof.KeyId, proof.Sha256));
            }
            await RequireUnchanged(); token.ThrowIfCancellationRequested();
            var observed = DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
            token.ThrowIfCancellationRequested();
            return new(identity, observed, state.Revision, state.Sha256, state.Profiles.Count(pair => pair.Value.Generation > 0), authenticated);

            async Task RequireUnchanged()
            {
                var after = await CaptureRecoveryInventory(authorityCustody, token); var current = rollback.ReadState();
                ValidateRecoveryCommitments(after, current);
                if (current.Profiles.Values.Any(entry => entry.Pending is not null) || current.Revision != state.Revision || current.Sha256 != state.Sha256 ||
                    NamespaceIdentity() != identity || identity != expectedNamespace || !before.Files.SequenceEqual(after.Files))
                    throw AbsenceInspectionFailure("changed");
                foreach (var proof in proofs) { rollback.ValidateProofBinding(proof); RequireActualAbsence(proof.ProfileIdSha256); }
                token.ThrowIfCancellationRequested();
            }
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Credential absence-scope inspection canceled.", cancellationToken); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw AbsenceInspectionFailure("timeout"); }
        catch (OperationCanceledException) { throw AbsenceInspectionFailure("unavailable"); }
        catch (CredentialException) { throw; }
        catch (Exception) { throw AbsenceInspectionFailure("unavailable"); }
        finally { foreach (var key in cachedKeys.Values) key.Dispose(); }
    }
    void RequireActualAbsence(string profileHash)
    {
        var path = Path.Combine(root, profileHash + ".credential.json"); CredentialRules.RejectLinks(path);
        if (Path.Exists(path)) throw AbsenceInspectionFailure("profile-not-absent");
    }
    static CredentialException AbsenceInspectionFailure(string reason) => new("credential-absence-scope-inspection-" + reason);
}
