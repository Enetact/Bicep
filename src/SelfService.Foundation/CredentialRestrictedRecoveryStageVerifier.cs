using System.Text.Json;

namespace SelfService.Foundation;

public sealed class CredentialRestrictedRecoveryStageVerification
{
    internal CredentialRestrictedRecoveryStageVerification(CredentialRestrictedRecoveryStageReceipt receipt, DateTimeOffset observed)
    {
        Receipt = receipt; ObservedUtc = observed;
        Sha256 = CredentialRecoverySets.Hash(JsonSerializer.SerializeToUtf8Bytes(new
        { Schema, AccessProfile, ReceiptSha256, PlanSha256, SourceSetSha256, CompletionCoreSha256, CompletionProofSha256, observedUnixMilliseconds = ObservedUtc.ToUnixTimeMilliseconds(),
            AuthenticatedProfileCount, AuthenticatedAbsenceCount, OutputCiphertextsAuthenticated, DestinationAbsenceProofsAuthenticated, DestinationCompletionProofAuthenticated,
            EncryptedProfileAuthenticationPerformed, DestinationAbsenceProofAuthenticationPerformed, TemporaryInMemoryDecryptionPerformed,
            InactiveRecoveryHold, NormalVaultUsable, PromotionAuthorized, ActivationPerformed, SourceHostFenced, ExternalExecutionAuthorized,
            KeyRetirementAuthorized, IndependentKeyBackupVerified, IndependentBackupCoverage, ProductionRecoveryQualified }, CredentialRules.Json));
    }
    public string Schema => "platform-studio.credential-restricted-recovery-stage-verification.v1";
    public string AccessProfile => "FoundationBoundDispatchOnly";
    public CredentialRestrictedRecoveryStageReceipt Receipt { get; }
    public string ReceiptSha256 => Receipt.Sha256;
    public string PlanSha256 => Receipt.PlanSha256;
    public string SourceSetSha256 => Receipt.SourceSetSha256;
    public string CompletionCoreSha256 => Receipt.CompletionCoreSha256;
    public string CompletionProofSha256 => Receipt.CompletionProofSha256;
    public DateTimeOffset ObservedUtc { get; }
    public int AuthenticatedProfileCount => Receipt.Profiles.Count;
    public int AuthenticatedAbsenceCount => Receipt.Absences.Count;
    public bool OutputCiphertextsAuthenticated => true;
    public bool DestinationAbsenceProofsAuthenticated => true;
    public bool DestinationCompletionProofAuthenticated => true;
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

public sealed class CredentialRestrictedRecoveryStageVerifier
{
    readonly ICredentialKeyProvider destinationKeys; readonly TimeProvider clock;
    public CredentialRestrictedRecoveryStageVerifier(ICredentialKeyProvider destinationKeys, TimeProvider? timeProvider = null)
    { this.destinationKeys = destinationKeys ?? throw CredentialRestrictedRecoveryStaging.Failure("configuration-invalid"); clock = timeProvider ?? TimeProvider.System; }
    public async Task<CredentialRestrictedRecoveryStageVerification> VerifyAsync(CredentialRestrictedRecoveryStagePlan plan, string reviewedPlanSha256,
        string expectedReceiptSha256, CancellationToken cancellationToken = default)
    {
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken); var token = deadline.Token;
        try { token.ThrowIfCancellationRequested(); using var held = OpenPrepared(plan, reviewedPlanSha256, expectedReceiptSha256); return await held.VerifyAsync(token); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw new OperationCanceledException("Restricted stage verification canceled.", cancellationToken); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw CredentialRestrictedRecoveryStaging.Failure("timeout"); }
        catch (OperationCanceledException) { throw CredentialRestrictedRecoveryStaging.Failure("destination-key-unavailable"); }
        catch (CredentialException) { throw; }
        catch (Exception) { throw CredentialRestrictedRecoveryStaging.Failure("verification-unavailable"); }
    }
    internal HeldVerification OpenPrepared(CredentialRestrictedRecoveryStagePlan plan, string reviewedPlanSha256, string expectedReceiptSha256)
    {
        CredentialRestrictedRecoveryStageReadLease? retained = null;
        try
        {
            retained = CredentialRestrictedRecoveryStaging.OpenCompletedReadLease(plan, reviewedPlanSha256, expectedReceiptSha256);
            CredentialRestrictedRecoveryStaging.ValidateCompletedReceiptTime(plan, retained.Receipt, clock.GetUtcNow());
            var session = new HeldVerification(this, plan, retained); retained = null; return session;
        }
        finally { retained?.Dispose(); }
    }
    internal sealed class HeldVerification(CredentialRestrictedRecoveryStageVerifier owner, CredentialRestrictedRecoveryStagePlan plan,
        CredentialRestrictedRecoveryStageReadLease retained) : IDisposable
    {
        int used;
        CredentialRestrictedRecoveryStageVerification? verifiedResult;
        internal CredentialRestrictedRecoveryStageReceipt Receipt => retained.Receipt;
        internal void AssertCurrent()
        {
            CredentialRestrictedRecoveryStaging.ValidateCompletedReceiptTime(plan, Receipt, owner.clock.GetUtcNow());
            retained.AssertCurrent();
        }
        // The new durable review uses this actual session after all SQL clock/auth/audit work.
        // Keep this final publication fence clock-free; no caller DTO or callback establishes custody.
        internal void AssertVerifiedCurrent(CredentialRestrictedRecoveryStageVerification result, CancellationToken token)
        {
            if (!ReferenceEquals(result, verifiedResult) || result is null || !ReferenceEquals(result.Receipt, Receipt))
                throw CredentialRestrictedRecoveryStaging.Failure("verified-session-mismatch");
            retained.AssertCurrent(); token.ThrowIfCancellationRequested();
        }
        internal async Task<CredentialRestrictedRecoveryStageVerification> VerifyAsync(CancellationToken token)
        {
            if (Interlocked.Exchange(ref used, 1) != 0) throw CredentialRestrictedRecoveryStaging.Failure("session-already-consumed");
            token.ThrowIfCancellationRequested(); AssertCurrent();
            using var key = await CredentialRestrictedRecoveryStaging.ResolvePinnedDestinationKeyAsync(plan.DestinationKey, owner.destinationKeys, token);
            token.ThrowIfCancellationRequested(); AssertCurrent();
            CredentialRestrictedStageCompletionProofs.Authenticate(plan, Receipt, retained.CompletionProof, key);
            var context = new CredentialRestrictedStageEnvelopeContext(Receipt.StageId, Receipt.StagedVaultNamespaceId, Receipt.StagedAuthorityNamespaceId, Receipt.BindingSha256);
            foreach (var envelope in retained.Envelopes)
            {
                token.ThrowIfCancellationRequested(); var actual = await CredentialVault.AuthenticateRestrictedStageEnvelopeAsync(envelope.EncryptedBytes, envelope.ProfileIdSha256, plan, context, key, token);
                var expected = Receipt.Profiles.Single(p => p.Destination.ProfileIdSha256 == envelope.ProfileIdSha256);
                if (actual.PreservedProfileSha256 != expected.PreservedProfileSha256 ||
                    JsonSerializer.Serialize(actual.Dependency, CredentialRules.Json) != JsonSerializer.Serialize(expected.Destination, CredentialRules.Json)) throw CredentialRestrictedRecoveryStaging.Failure("destination-profile-mismatch");
            }
            foreach (var proof in retained.AbsenceProofs)
            {
                token.ThrowIfCancellationRequested(); CredentialRestrictedStageProofs.Authenticate(proof, key);
                if (CredentialRestrictedStageProofs.Projection(proof) != Receipt.Absences.Single(p => p.Destination.ProfileIdSha256 == proof.ProfileIdSha256).Destination)
                    throw CredentialRestrictedRecoveryStaging.Failure("destination-proof-mismatch");
            }
            if (retained.Envelopes.Count != Receipt.Profiles.Count || retained.AbsenceProofs.Count != Receipt.Absences.Count) throw CredentialRestrictedRecoveryStaging.Failure("count-mismatch");
            var observed = CredentialRestrictedRecoveryStaging.At(owner.clock.GetUtcNow());
            CredentialRestrictedRecoveryStaging.ValidateCompletedReceiptTime(plan, Receipt, observed);
            var result = new CredentialRestrictedRecoveryStageVerification(Receipt, observed);
            retained.AssertCurrent(); token.ThrowIfCancellationRequested(); verifiedResult = result; return result;
        }
        public void Dispose() => retained.Dispose();
    }
}
