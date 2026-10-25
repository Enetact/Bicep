using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SelfService.Foundation;

public sealed partial class CredentialVault
{
    // Read-only custody. The existing guard files are opened/held, never recreated or written.
    internal async Task<HeldMaintenanceObservation> HoldMaintenanceObservationAsync(string profileScopeJson,
        string expectedEnvelopeSha256, CredentialVaultNamespaceIdentity expectedNamespace, CancellationToken cancellationToken)
    {
        var gate = await Gate(cancellationToken); FileStream? authorityGate = null;
        try
        {
            authorityGate = rollback is null ? null : await RollbackGate(cancellationToken);
            if (NamespaceIdentity() != expectedNamespace) throw new CredentialException("credential-observation-namespace-changed");
            using var document = CredentialRules.Document(Encoding.UTF8.GetBytes(profileScopeJson));
            var scope = Bound(document.RootElement.Deserialize<CredentialScope>(CredentialRules.Json)
                ?? throw new CredentialException("credential-observation-scope-invalid"));
            var path = ProfilePath(scope.AuthProfileId); var beforeKeyLookup = await custody.ReadFileAsync(path, 131072, cancellationToken);
            if (HashMaintenance(beforeKeyLookup) != expectedEnvelopeSha256)
                throw new CredentialException("credential-observation-candidate-mismatch");
            var state = rollback?.ReadState(); rollback?.RequireCurrent(state!, CredentialRules.Hash(scope.AuthProfileId), beforeKeyLookup);
            // Both guards are already held; the bypass skips a nested authority lock, never authentication.
            var observed = await ReadObserved(path, cancellationToken, bypassRollback: true); using var secret = observed.Secret;
            var actual = await ObserveMaintenanceLocked(path, observed.Envelope.Profile, observed.Envelope.KeyId, observed.Bytes, cancellationToken);
            if (actual.EnvelopeSha256 != expectedEnvelopeSha256 || actual.ProfileScopeJson != profileScopeJson)
                throw new CredentialException("credential-observation-candidate-mismatch");
            return new(this, gate, authorityGate, path, observed.Envelope.Profile, observed.Envelope.KeyId, observed.Bytes, actual);
        }
        catch { try { authorityGate?.Dispose(); } finally { gate.Dispose(); } throw; }
    }

    internal sealed class HeldMaintenanceObservation(CredentialVault owner, FileStream gate, FileStream? authorityGate,
        string path, CredentialProfile profile, string keyId, byte[] authenticatedBytes,
        FoundationCredentialMaintenanceTarget observation) : IDisposable
    {
        int disposed;
        internal FoundationCredentialMaintenanceTarget Observation => observation;
        internal async Task RevalidateAsync(CancellationToken cancellationToken)
        {
            if (Volatile.Read(ref disposed) != 0) throw new CredentialException("credential-observation-session-disposed");
            if (await owner.ObserveMaintenanceLocked(path, profile, keyId, authenticatedBytes, cancellationToken) != observation)
                throw new CredentialException("credential-observation-target-changed");
        }
        public void Dispose()
        {
            if (Interlocked.Exchange(ref disposed, 1) != 0) return;
            CryptographicOperations.ZeroMemory(authenticatedBytes);
            try { authorityGate?.Dispose(); } finally { gate.Dispose(); }
        }
    }
}
