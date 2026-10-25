using System.Security.Cryptography;
using System.Text.Json;

namespace SelfService.Foundation;

public sealed class CredentialScopedRecoveryRehearsalResult
{
    internal CredentialScopedRecoveryRehearsalResult(CredentialScopedRecoverySetVerification verification,
        IEnumerable<CredentialRecoveryProfileDependency> profiles, IEnumerable<CredentialAbsenceScopeDependency> absences, DateTimeOffset completedUtc)
    {
        SignedSetVerification = verification; CompletedUtc = completedUtc;
        ProfileDependencies = Array.AsReadOnly(profiles.ToArray()); AbsenceScopes = Array.AsReadOnly(absences.ToArray());
        CurrentRequiredKeyIds = Array.AsReadOnly(verification.Inventory.CurrentRequiredKeyIds.ToArray());
        Sha256 = CredentialRecoverySets.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema, SignedSetSha256, InventorySha256, completedUnixMilliseconds = CompletedUtc.ToUnixTimeMilliseconds(),
            ProfileDependencies, AbsenceScopes, CurrentRequiredKeyIds, AllRetainedEnvelopesAuthenticated, AllRetainedAbsenceProofsAuthenticated,
            EncryptedProfileAuthenticationPerformed, AbsenceProofAuthenticationPerformed, TemporaryInMemoryDecryptionPerformed,
            CurrentVaultConsulted, PlaintextCredentialMaterialExported, EncryptionKeyMaterialExported, RetainedEnvelopeBytesExported,
            PlaintextPersisted, RestorePerformed, NamespaceAdopted, CustomerReviewAuthorized, ExternalExecutionAuthorized, KeyRetirementAuthorized,
            IndependentKeyBackupVerified, IndependentBackupCoverage, FullRecoverySupported, ProductionRecoveryQualified
        }, CredentialRules.Json));
    }
    public string Schema => "platform-studio.credential-scoped-recovery-rehearsal.v1";
    public CredentialScopedRecoverySetVerification SignedSetVerification { get; }
    public string SignedSetSha256 => SignedSetVerification.Sha256;
    public string InventorySha256 => SignedSetVerification.Inventory.Sha256;
    public DateTimeOffset CompletedUtc { get; }
    public IReadOnlyList<CredentialRecoveryProfileDependency> ProfileDependencies { get; }
    public IReadOnlyList<CredentialAbsenceScopeDependency> AbsenceScopes { get; }
    public IReadOnlyList<string> CurrentRequiredKeyIds { get; }
    public int AuthenticatedProfileCount => ProfileDependencies.Count;
    public int AuthenticatedAbsenceCount => AbsenceScopes.Count;
    public bool AllRetainedEnvelopesAuthenticated => true;
    public bool AllRetainedAbsenceProofsAuthenticated => true;
    public bool EncryptedProfileAuthenticationPerformed => AuthenticatedProfileCount > 0;
    public bool AbsenceProofAuthenticationPerformed => AuthenticatedAbsenceCount > 0;
    public bool TemporaryInMemoryDecryptionPerformed => AuthenticatedProfileCount > 0;
    public bool CurrentVaultConsulted => false;
    public bool PlaintextCredentialMaterialExported => false;
    public bool EncryptionKeyMaterialExported => false;
    public bool RetainedEnvelopeBytesExported => false;
    public bool PlaintextPersisted => false;
    public bool RestorePerformed => false;
    public bool NamespaceAdopted => false;
    public bool CustomerReviewAuthorized => false;
    public bool ExternalExecutionAuthorized => false;
    public bool KeyRetirementAuthorized => false;
    public bool IndependentKeyBackupVerified => false;
    public string IndependentBackupCoverage => "NotAssessed";
    public bool FullRecoverySupported => false;
    public bool ProductionRecoveryQualified => false;
    public string Sha256 { get; }
}

public sealed class CredentialScopedRecoveryRehearsal
{
    readonly CredentialVaultNamespaceIdentity expectedNamespace;
    readonly ICredentialKeyProvider keys;
    readonly TimeProvider clock;
    public CredentialScopedRecoveryRehearsal(CredentialVaultNamespaceIdentity expectedNamespace, ICredentialKeyProvider keys, TimeProvider? timeProvider = null)
    {
        this.expectedNamespace = expectedNamespace ?? throw Failure("configuration-invalid");
        this.keys = keys ?? throw Failure("configuration-invalid"); clock = timeProvider ?? TimeProvider.System;
    }
    public async Task<CredentialScopedRecoveryRehearsalResult> RehearseAsync(CredentialScopedRecoverySet set,
        CredentialScopedRecoverySetAuthority pinnedAuthority, RSA pinnedPublicKey, CancellationToken cancellationToken = default)
    {
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken); var token = deadline.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            using var retained = CredentialScopedRecoverySets.ValidateRetainedSet(set, pinnedAuthority, pinnedPublicKey, expectedNamespace, clock);
            using var cache = new FrozenKeys(expectedNamespace, keys);
            var expected = retained.Verification.Inventory;
            var profiles = new List<CredentialRecoveryProfileDependency>(expected.ProfileDependencies.Count);
            foreach (var envelope in retained.Envelopes)
            {
                token.ThrowIfCancellationRequested();
                var actual = await CredentialVault.AuthenticateRecoveryEnvelopeAsync(envelope.EncryptedBytes, envelope.ProfileIdSha256, expectedNamespace, cache, token);
                token.ThrowIfCancellationRequested();
                var signed = expected.ProfileDependencies.SingleOrDefault(p => p.ProfileIdSha256 == actual.ProfileIdSha256);
                if (signed is null || JsonSerializer.Serialize(actual, CredentialRules.Json) != JsonSerializer.Serialize(signed, CredentialRules.Json)) throw Failure("metadata-mismatch");
                profiles.Add(actual);
            }
            var absences = new List<CredentialAbsenceScopeDependency>(expected.AbsenceScopes.Count);
            foreach (var proof in retained.AbsenceProofs)
            {
                token.ThrowIfCancellationRequested(); using var key = await cache.GetKeyAsync(expectedNamespace.CustomerId, expectedNamespace.CellId, proof.KeyId, token);
                token.ThrowIfCancellationRequested(); var bytes = key.CopyBytes();
                try { CredentialAbsenceScopeProofs.Authenticate(proof, bytes); }
                finally { CryptographicOperations.ZeroMemory(bytes); }
                var actual = new CredentialAbsenceScopeDependency(proof.ProfileIdSha256, proof.ScopeSha256, proof.Scope.ProjectId, proof.Scope.EnvironmentId, proof.KeyId, proof.Sha256);
                if (expected.AbsenceScopes.SingleOrDefault(p => p.ProfileIdSha256 == actual.ProfileIdSha256) != actual) throw Failure("metadata-mismatch");
                absences.Add(actual);
            }
            if (profiles.Count != expected.ProfileDependencies.Count || absences.Count != expected.AbsenceScopes.Count ||
                !cache.KeyIds.SequenceEqual(expected.CurrentRequiredKeyIds, StringComparer.Ordinal)) throw Failure("metadata-mismatch");
            var completed = DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
            CredentialScopedRecoverySets.ValidateLifetime(retained.Verification.Signature, completed); token.ThrowIfCancellationRequested();
            return new(retained.Verification, profiles.OrderBy(p => p.ProfileIdSha256, StringComparer.Ordinal), absences.OrderBy(p => p.ProfileIdSha256, StringComparer.Ordinal), completed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Scoped credential recovery rehearsal canceled.", cancellationToken); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw Failure("timeout"); }
        catch (OperationCanceledException) { throw Failure("unavailable"); }
        catch (CredentialException) { throw; }
        catch (Exception) { throw Failure("unavailable"); }
    }
    sealed class FrozenKeys(CredentialVaultNamespaceIdentity identity, ICredentialKeyProvider source) : ICredentialKeyProvider, IDisposable
    {
        readonly Dictionary<string, CredentialEncryptionKey> cached = new(StringComparer.Ordinal);
        internal IEnumerable<string> KeyIds => cached.Keys.Order(StringComparer.Ordinal);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken cancellationToken)
        {
            if (customerId != identity.CustomerId || cellId != identity.CellId || !CredentialRules.ProfileId(keyId)) throw Failure("key-binding-invalid");
            cancellationToken.ThrowIfCancellationRequested();
            if (!cached.TryGetValue(keyId, out var frozen))
            {
                CredentialEncryptionKey borrowed;
                try { borrowed = await source.GetKeyAsync(customerId, cellId, keyId, cancellationToken) ?? throw Failure("key-unavailable"); }
                catch (OperationCanceledException) { throw; }
                catch (Exception) { throw Failure("key-unavailable"); }
                using (borrowed)
                {
                    cancellationToken.ThrowIfCancellationRequested(); var bytes = borrowed.CopyBytes();
                    try { frozen = new CredentialEncryptionKey(bytes); cached.Add(keyId, frozen); }
                    finally { CryptographicOperations.ZeroMemory(bytes); }
                }
            }
            var clone = frozen.CopyBytes();
            try { return new CredentialEncryptionKey(clone); }
            finally { CryptographicOperations.ZeroMemory(clone); }
        }
        public void Dispose() { foreach (var key in cached.Values) key.Dispose(); }
    }
    static CredentialException Failure(string suffix) => new("credential-scoped-recovery-rehearsal-" + suffix);
}
