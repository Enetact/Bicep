using System.Security.Cryptography;
using System.Text.Json;

namespace SelfService.Foundation;

/// <summary>A completed inactive stage authenticated at local custody boundaries; never an adoption or activation permit.</summary>
public sealed class CredentialRecoveryStageVerification
{
    internal CredentialRecoveryStageVerification(CredentialRecoveryStageReceipt receipt, DateTimeOffset observedUtc)
    {
        Receipt = receipt; ObservedUtc = observedUtc;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema, ReceiptSha256, PlanSha256, SourceSetSha256, observedUnixMilliseconds = observedUtc.ToUnixTimeMilliseconds(),
            OutputCiphertextsAuthenticated, AuthenticatedProfileCount, TemporaryInMemoryDecryptionPerformed, InactiveRecoveryHold, NormalVaultUsable,
            CurrentSourceVaultConsulted, PlaintextCredentialMaterialExported, EncryptionKeyMaterialExported,
            RetainedEnvelopeBytesExported, StageWritten, NamespaceAdopted, ActivationPerformed, SourceHostFenced,
            ExternalExecutionAuthorized, KeyRetirementAuthorized, IndependentKeyBackupVerified, IndependentBackupCoverage, ProductionRecoveryQualified
        }, CredentialRules.Json)));
    }
    public string Schema => "platform-studio.credential-recovery-stage-verification.v1";
    public CredentialRecoveryStageReceipt Receipt { get; }
    public string ReceiptSha256 => Receipt.Sha256;
    public string PlanSha256 => Receipt.PlanSha256;
    public string SourceSetSha256 => Receipt.SourceSetSha256;
    public DateTimeOffset ObservedUtc { get; }
    public int AuthenticatedProfileCount => Receipt.Profiles.Count;
    public bool OutputCiphertextsAuthenticated => true;
    public bool TemporaryInMemoryDecryptionPerformed => AuthenticatedProfileCount > 0;
    public bool InactiveRecoveryHold => true;
    public bool NormalVaultUsable => false;
    public bool CurrentSourceVaultConsulted => false;
    public bool PlaintextCredentialMaterialExported => false;
    public bool EncryptionKeyMaterialExported => false;
    public bool RetainedEnvelopeBytesExported => false;
    public bool StageWritten => false;
    public bool NamespaceAdopted => false;
    public bool ActivationPerformed => false;
    public bool SourceHostFenced => false;
    public bool ExternalExecutionAuthorized => false;
    public bool KeyRetirementAuthorized => false;
    public bool IndependentKeyBackupVerified => false;
    public string IndependentBackupCoverage => "NotAssessed";
    public bool ProductionRecoveryQualified => false;
    public string Sha256 { get; }
}

/// <summary>Explicit trusted-owner inspection of one exact reviewed completed stage. Resolves the destination key once.</summary>
public sealed class CredentialRecoveryStageVerifier
{
    readonly ICredentialKeyProvider destinationKeys;
    readonly TimeProvider clock;
    public CredentialRecoveryStageVerifier(ICredentialKeyProvider destinationKeys, TimeProvider? timeProvider = null)
    { this.destinationKeys = destinationKeys ?? throw Failure("configuration-invalid"); clock = timeProvider ?? TimeProvider.System; }

    public async Task<CredentialRecoveryStageVerification> VerifyAsync(CredentialRecoveryStagePlan plan,
        string reviewedPlanSha256, string expectedReceiptSha256, CancellationToken cancellationToken = default)
    {
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken); var token = deadline.Token;
        try
        {
            token.ThrowIfCancellationRequested();
            using var prepared = OpenPrepared(plan, reviewedPlanSha256, expectedReceiptSha256);
            return await prepared.VerifyAsync(token);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Credential recovery stage verification canceled.", cancellationToken); }
        catch (OperationCanceledException) when (token.IsCancellationRequested) { throw Failure("timeout"); }
        catch (OperationCanceledException) { throw Failure("unavailable"); }
        catch (CredentialException) { throw; }
        catch (Exception) { throw Failure("unavailable"); }
    }

    // No key access or caller-provided callback/lease. The concrete guard remains owned by this internal one-use session.
    internal HeldVerification OpenPrepared(CredentialRecoveryStagePlan plan, string reviewedPlanSha256, string expectedReceiptSha256)
    {
        CredentialRecoveryStageReadLease? retained = null;
        try
        {
            retained = CredentialRecoveryStaging.OpenCompletedReadLease(plan, reviewedPlanSha256, expectedReceiptSha256);
            var session = new HeldVerification(this, plan, retained); retained = null; return session;
        }
        catch (CredentialException) { throw; }
        catch (Exception) { throw Failure("unavailable"); }
        finally { retained?.Dispose(); }
    }

    internal sealed class HeldVerification : IDisposable
    {
        readonly CredentialRecoveryStageVerifier owner;
        readonly CredentialRecoveryStagePlan plan;
        readonly CredentialRecoveryStageReadLease retained;
        readonly Dictionary<string, CredentialRecoveryStagedProfile> profiles;
        readonly Dictionary<string, string> preserved;
        int attempted;
        internal CredentialRecoveryStageReceipt Receipt => retained.Receipt;

        internal HeldVerification(CredentialRecoveryStageVerifier owner, CredentialRecoveryStagePlan plan, CredentialRecoveryStageReadLease retained)
        {
            this.owner = owner; this.plan = plan; this.retained = retained;
            CredentialRecoveryStaging.ValidateCompletedReceiptTime(plan, retained.Receipt, owner.clock.GetUtcNow());
            profiles = retained.Receipt.Profiles.ToDictionary(profile => profile.Destination.ProfileIdSha256, StringComparer.Ordinal);
            preserved = plan.PreservedProfiles.ToDictionary(profile => profile.ProfileIdSha256, profile => profile.ProfileSha256, StringComparer.Ordinal);
            var original = plan.SourceInventory.ProfileDependencies.ToDictionary(profile => profile.ProfileIdSha256, StringComparer.Ordinal);
            if (profiles.Count != preserved.Count || profiles.Count != original.Count || profiles.Count != retained.Envelopes.Count ||
                !retained.Receipt.AbsentProfileIds.SequenceEqual(plan.AbsentProfileIds, StringComparer.Ordinal)) throw Failure("metadata-mismatch");
            foreach (var pair in profiles)
                if (!preserved.TryGetValue(pair.Key, out var expectedProfileSha256) || pair.Value.PreservedProfileSha256 != expectedProfileSha256 ||
                    !original.TryGetValue(pair.Key, out var source) ||
                    JsonSerializer.Serialize(pair.Value.Source, CredentialRules.Json) != JsonSerializer.Serialize(source, CredentialRules.Json) ||
                    pair.Value.Destination.SourceKeyId != plan.DestinationKey.KeyId || !SamePreservedFields(source, pair.Value.Destination))
                    throw Failure("metadata-mismatch");
        }
        internal void AssertCurrent()
        { retained.AssertCurrent(); CredentialRecoveryStaging.ValidateCompletedReceiptTime(plan, Receipt, owner.clock.GetUtcNow()); }

        internal async Task<CredentialRecoveryStageVerification> VerifyAsync(CancellationToken token)
        {
            if (Interlocked.Exchange(ref attempted, 1) != 0) throw Failure("session-already-consumed");
            token.ThrowIfCancellationRequested(); AssertCurrent();
            using var key = await CredentialRecoveryStaging.ResolvePinnedDestinationKeyAsync(plan.DestinationKey, owner.destinationKeys, token);
            token.ThrowIfCancellationRequested(); retained.AssertCurrent();
            var seen = new HashSet<string>(StringComparer.Ordinal);
            foreach (var envelope in retained.Envelopes)
            {
                token.ThrowIfCancellationRequested();
                var actual = await CredentialVault.AuthenticateStageEnvelopeAsync(envelope.EncryptedBytes, envelope.ProfileIdSha256, plan, key, token);
                token.ThrowIfCancellationRequested();
                if (!seen.Add(envelope.ProfileIdSha256) || !profiles.TryGetValue(envelope.ProfileIdSha256, out var expected) ||
                    !preserved.TryGetValue(envelope.ProfileIdSha256, out var exactProfileSha256) ||
                    actual.PreservedProfileSha256 != exactProfileSha256 || expected.PreservedProfileSha256 != exactProfileSha256 ||
                    JsonSerializer.Serialize(actual.Dependency, CredentialRules.Json) != JsonSerializer.Serialize(expected.Destination, CredentialRules.Json) ||
                    expected.Destination.SourceKeyId != plan.DestinationKey.KeyId || !SamePreservedFields(expected.Source, actual.Dependency))
                    throw Failure("metadata-mismatch");
            }
            if (seen.Count != profiles.Count || !retained.Receipt.AbsentProfileIds.SequenceEqual(plan.AbsentProfileIds, StringComparer.Ordinal)) throw Failure("metadata-mismatch");
            retained.AssertCurrent(); token.ThrowIfCancellationRequested();
            var observedUtc = DateTimeOffset.FromUnixTimeMilliseconds(owner.clock.GetUtcNow().ToUnixTimeMilliseconds());
            CredentialRecoveryStaging.ValidateCompletedReceiptTime(plan, retained.Receipt, observedUtc);
            token.ThrowIfCancellationRequested(); return new(retained.Receipt, observedUtc);
        }
        public void Dispose() => retained.Dispose();
    }

    static bool SamePreservedFields(CredentialRecoveryProfileDependency source, CredentialRecoveryProfileDependency destination) =>
        source.ProfileIdSha256 == destination.ProfileIdSha256 && source.ScopeSha256 == destination.ScopeSha256 &&
        source.ProjectId == destination.ProjectId && source.EnvironmentId == destination.EnvironmentId && source.Generation == destination.Generation &&
        source.Enabled == destination.Enabled && source.ExpiresUnixMilliseconds == destination.ExpiresUnixMilliseconds;
    static CredentialException Failure(string reason) => new("credential-recovery-stage-verification-" + reason);
}
