using System.Text.Json;

namespace SelfService.Foundation;

public sealed class CredentialScopedRecoveryStageVerification
{
    internal CredentialScopedRecoveryStageVerification(CredentialScopedRecoveryStageReceipt receipt, DateTimeOffset observed)
    {
        Receipt = receipt; ObservedUtc = observed;
        Sha256 = CredentialRecoverySets.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        { Schema, ReceiptSha256, PlanSha256, SourceSetSha256, CompletionCoreSha256, observedUnixMilliseconds = ObservedUtc.ToUnixTimeMilliseconds(),
            AuthenticatedProfileCount, AuthenticatedAbsenceCount, OutputCiphertextsAuthenticated, DestinationAbsenceProofsAuthenticated,
            EncryptedProfileAuthenticationPerformed, DestinationAbsenceProofAuthenticationPerformed, TemporaryInMemoryDecryptionPerformed,
            InactiveRecoveryHold, NormalVaultUsable, PromotionAuthorized, ActivationPerformed, SourceHostFenced, ExternalExecutionAuthorized,
            KeyRetirementAuthorized, IndependentKeyBackupVerified, IndependentBackupCoverage, ProductionRecoveryQualified }, CredentialRules.Json));
    }
    public string Schema => "platform-studio.credential-scoped-recovery-stage-verification.v1";
    public CredentialScopedRecoveryStageReceipt Receipt { get; }
    public string ReceiptSha256 => Receipt.Sha256;
    public string PlanSha256 => Receipt.PlanSha256;
    public string SourceSetSha256 => Receipt.SourceSetSha256;
    public string CompletionCoreSha256 => Receipt.CompletionCoreSha256;
    public DateTimeOffset ObservedUtc { get; }
    public int AuthenticatedProfileCount => Receipt.Profiles.Count;
    public int AuthenticatedAbsenceCount => Receipt.Absences.Count;
    public bool OutputCiphertextsAuthenticated => true;
    public bool DestinationAbsenceProofsAuthenticated => true;
    public bool EncryptedProfileAuthenticationPerformed => AuthenticatedProfileCount > 0;
    public bool DestinationAbsenceProofAuthenticationPerformed => AuthenticatedAbsenceCount > 0;
    public bool TemporaryInMemoryDecryptionPerformed => AuthenticatedProfileCount > 0;
    public bool InactiveRecoveryHold => true;
    public bool NormalVaultUsable => false;
    public bool PromotionAuthorized => false;
    public bool ActivationPerformed => false;
    public bool SourceHostFenced => false;
    public bool ExternalExecutionAuthorized => false;
    public bool KeyRetirementAuthorized => false;
    public bool IndependentKeyBackupVerified => false;
    public string IndependentBackupCoverage => "NotAssessed";
    public bool ProductionRecoveryQualified => false;
    public string Sha256 { get; }
}

public sealed class CredentialScopedRecoveryStageVerifier
{
    readonly ICredentialKeyProvider destinationKeys; readonly TimeProvider clock;
    public CredentialScopedRecoveryStageVerifier(ICredentialKeyProvider destinationKeys, TimeProvider? timeProvider = null)
    { this.destinationKeys = destinationKeys ?? throw CredentialScopedRecoveryStaging.Failure("configuration-invalid"); clock = timeProvider ?? TimeProvider.System; }
    public async Task<CredentialScopedRecoveryStageVerification> VerifyAsync(CredentialScopedRecoveryStagePlan plan, string reviewedPlanSha256,
        string expectedReceiptSha256, CancellationToken cancellationToken = default)
    {
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken); var token = deadline.Token;
        try { token.ThrowIfCancellationRequested(); using var held = OpenPrepared(plan, reviewedPlanSha256, expectedReceiptSha256); return await held.VerifyAsync(token); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException("Scoped stage verification canceled.", cancellationToken); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw CredentialScopedRecoveryStaging.Failure("timeout"); }
        catch (OperationCanceledException) { throw CredentialScopedRecoveryStaging.Failure("destination-key-unavailable"); }
        catch (CredentialException) { throw; }
        catch (Exception) { throw CredentialScopedRecoveryStaging.Failure("verification-unavailable"); }
    }
    internal HeldVerification OpenPrepared(CredentialScopedRecoveryStagePlan plan, string reviewedPlanSha256, string expectedReceiptSha256)
    {
        CredentialScopedRecoveryStageReadLease? retained = null;
        try
        {
            retained = CredentialScopedRecoveryStaging.OpenCompletedReadLease(plan, reviewedPlanSha256, expectedReceiptSha256);
            CredentialScopedRecoveryStaging.ValidateCompletedReceiptTime(plan, retained.Receipt, clock.GetUtcNow());
            var session = new HeldVerification(this, plan, retained); retained = null; return session;
        }
        finally { retained?.Dispose(); }
    }
    internal sealed class HeldVerification(CredentialScopedRecoveryStageVerifier owner, CredentialScopedRecoveryStagePlan plan,
        CredentialScopedRecoveryStageReadLease retained) : IDisposable
    {
        int used;
        internal CredentialScopedRecoveryStageReceipt Receipt => retained.Receipt;
        internal void AssertCurrent() { retained.AssertCurrent(); CredentialScopedRecoveryStaging.ValidateCompletedReceiptTime(plan, Receipt, owner.clock.GetUtcNow()); }
        internal async Task<CredentialScopedRecoveryStageVerification> VerifyAsync(CancellationToken token)
        {
            if (Interlocked.Exchange(ref used, 1) != 0) throw CredentialScopedRecoveryStaging.Failure("session-already-consumed");
            token.ThrowIfCancellationRequested(); AssertCurrent();
            using var key = await CredentialRecoveryStaging.ResolvePinnedDestinationKeyAsync(plan.DestinationKey, owner.destinationKeys, token);
            token.ThrowIfCancellationRequested(); AssertCurrent();
            foreach (var envelope in retained.Envelopes)
            {
                token.ThrowIfCancellationRequested(); var actual = await CredentialVault.AuthenticateScopedStageEnvelopeAsync(envelope.EncryptedBytes, envelope.ProfileIdSha256, plan, key, token);
                var expected = Receipt.Profiles.Single(p => p.Destination.ProfileIdSha256 == envelope.ProfileIdSha256);
                if (actual.PreservedProfileSha256 != expected.PreservedProfileSha256 ||
                    JsonSerializer.Serialize(actual.Dependency, CredentialRules.Json) != JsonSerializer.Serialize(expected.Destination, CredentialRules.Json)) throw CredentialScopedRecoveryStaging.Failure("destination-profile-mismatch");
            }
            foreach (var proof in retained.AbsenceProofs)
            {
                token.ThrowIfCancellationRequested(); CredentialScopedStageProofs.Authenticate(proof, key);
                if (CredentialScopedStageProofs.Projection(proof) != Receipt.Absences.Single(p => p.Destination.ProfileIdSha256 == proof.ProfileIdSha256).Destination)
                    throw CredentialScopedRecoveryStaging.Failure("destination-proof-mismatch");
            }
            if (retained.Envelopes.Count != Receipt.Profiles.Count || retained.AbsenceProofs.Count != Receipt.Absences.Count) throw CredentialScopedRecoveryStaging.Failure("count-mismatch");
            AssertCurrent(); var observed = CredentialScopedRecoveryStaging.At(owner.clock.GetUtcNow());
            CredentialScopedRecoveryStaging.ValidateCompletedReceiptTime(plan, Receipt, observed); token.ThrowIfCancellationRequested(); return new(Receipt, observed);
        }
        public void Dispose() => retained.Dispose();
    }
}
