using System.Security.Cryptography;
using System.Text.Json;

namespace SelfService.Foundation;

public sealed class CredentialRestrictedRecoveryRehearsalResult
{
    internal CredentialRestrictedRecoveryRehearsalResult(CredentialRestrictedRecoverySetVerification verification,
        IEnumerable<CredentialRecoveryProfileDependency> profiles, IEnumerable<CredentialAbsenceScopeDependency> absences, DateTimeOffset completedUtc)
    {
        SignedSetVerification = verification; CompletedUtc = completedUtc;
        ProfileDependencies = Array.AsReadOnly(profiles.ToArray()); AbsenceScopes = Array.AsReadOnly(absences.ToArray());
        CurrentRequiredKeyIds = Array.AsReadOnly(verification.Inventory.CurrentRequiredKeyIds.ToArray());
        Sha256 = CredentialRecoverySets.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema, AccessProfile, SignedSetSha256, InventorySha256, completedUnixMilliseconds = CompletedUtc.ToUnixTimeMilliseconds(),
            ProfileDependencies, AbsenceScopes, CurrentRequiredKeyIds, AllRetainedEnvelopesAuthenticated, AllRetainedAbsenceProofsAuthenticated,
            EncryptedProfileAuthenticationPerformed, AbsenceProofAuthenticationPerformed, TemporaryInMemoryDecryptionPerformed,
            CurrentVaultConsulted, PlaintextCredentialMaterialExported, EncryptionKeyMaterialExported, RetainedEnvelopeBytesExported,
            PlaintextPersisted, RestorePerformed, NamespaceAdopted, CustomerReviewAuthorized, ExternalExecutionAuthorized, KeyRetirementAuthorized,
            IndependentKeyBackupVerified, IndependentBackupCoverage, FullRecoverySupported, ProductionRecoveryQualified
        }, CredentialRules.Json));
    }
    public string Schema => "platform-studio.credential-restricted-recovery-rehearsal.v1";
    public string AccessProfile => "FoundationBoundDispatchOnly";
    public CredentialRestrictedRecoverySetVerification SignedSetVerification { get; }
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

public sealed class CredentialRestrictedRecoveryRehearsal
{
    readonly CredentialVaultNamespaceIdentity expectedNamespace;
    readonly ICredentialKeyProvider keys;
    readonly TimeProvider clock;
    public CredentialRestrictedRecoveryRehearsal(CredentialVaultNamespaceIdentity expectedNamespace, ICredentialKeyProvider keys, TimeProvider? timeProvider = null)
    {
        this.expectedNamespace = expectedNamespace ?? throw Failure("configuration-invalid");
        this.keys = keys ?? throw Failure("configuration-invalid"); clock = timeProvider ?? TimeProvider.System;
    }
    public async Task<CredentialRestrictedRecoveryRehearsalResult> RehearseAsync(CredentialRestrictedRecoverySet set,
        CredentialRestrictedRecoverySetAuthority pinnedAuthority, RSA pinnedPublicKey, CancellationToken cancellationToken = default)
    {
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken); var token = deadline.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            using var retained = CredentialRestrictedRecoverySets.ValidateRetainedSet(set, pinnedAuthority, pinnedPublicKey, expectedNamespace, clock);
            var authenticated = await CredentialScopedRecoveryRehearsal.AuthenticateRetainedAsync(expectedNamespace, keys, retained.Verification.Inventory.ScopedInventory, retained.Envelopes, retained.AbsenceProofs, token);
            var completed = DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
            CredentialRestrictedRecoverySets.ValidateLifetime(retained.Verification.Signature, completed); token.ThrowIfCancellationRequested();
            return new(retained.Verification, authenticated.Profiles, authenticated.Absences, completed);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Scoped credential recovery rehearsal canceled.", cancellationToken); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw Failure("timeout"); }
        catch (OperationCanceledException) { throw Failure("unavailable"); }
        catch (CredentialException) { throw; }
        catch (Exception) { throw Failure("unavailable"); }
    }
    static CredentialException Failure(string suffix) => new("credential-restricted-recovery-rehearsal-" + suffix);
}
