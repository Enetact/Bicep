using System.Security.Cryptography;
using System.Text.Json;

namespace SelfService.Foundation;

/// <summary>Authentication of explicitly selected historical encrypted bytes; never a credential lease or restore authority.</summary>
public sealed class CredentialRecoveryRehearsalResult
{
    internal CredentialRecoveryRehearsalResult(CredentialRecoverySetVerification verification,
        IEnumerable<CredentialRecoveryProfileDependency> dependencies, DateTimeOffset completedUtc)
    {
        SignedSetVerification = verification; CompletedUtc = completedUtc;
        ProfileDependencies = Array.AsReadOnly(dependencies.ToArray());
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
        {
            Schema, SignedSetSha256, InventorySha256, completedUnixMilliseconds = completedUtc.ToUnixTimeMilliseconds(),
            ProfileDependencies, AllRetainedEnvelopesAuthenticated, TemporaryInMemoryDecryptionPerformed,
            CurrentVaultConsulted, PlaintextCredentialMaterialExported, EncryptionKeyMaterialExported, RetainedEnvelopeBytesExported, PlaintextPersisted, RestorePerformed, NamespaceAdopted,
            ExternalExecutionAuthorized, KeyRetirementAuthorized, IndependentKeyBackupVerified, IndependentBackupCoverage, ProductionRecoveryQualified
        }, CredentialRules.Json)));
    }
    public string Schema => "platform-studio.credential-recovery-rehearsal.v1";
    public CredentialRecoverySetVerification SignedSetVerification { get; }
    public string SignedSetSha256 => SignedSetVerification.Sha256;
    public string InventorySha256 => SignedSetVerification.Inventory.Sha256;
    public IReadOnlyList<CredentialRecoveryProfileDependency> ProfileDependencies { get; }
    public int AuthenticatedProfileCount => ProfileDependencies.Count;
    public DateTimeOffset CompletedUtc { get; }
    public bool AllRetainedEnvelopesAuthenticated => true;
    public bool TemporaryInMemoryDecryptionPerformed => AuthenticatedProfileCount > 0;
    public bool CurrentVaultConsulted => false;
    public bool PlaintextCredentialMaterialExported => false;
    public bool EncryptionKeyMaterialExported => false;
    public bool RetainedEnvelopeBytesExported => false;
    public bool PlaintextPersisted => false;
    public bool RestorePerformed => false;
    public bool NamespaceAdopted => false;
    public bool ExternalExecutionAuthorized => false;
    public bool KeyRetirementAuthorized => false;
    public bool IndependentKeyBackupVerified => false;
    public string IndependentBackupCoverage => "NotAssessed";
    public bool ProductionRecoveryQualified => false;
    public string Sha256 { get; }
}

/// <summary>Explicit trusted composition over retained encrypted bytes and the separately configured key provider. Performs no filesystem operation.</summary>
public sealed class CredentialRecoveryRehearsal
{
    readonly CredentialVaultNamespaceIdentity expectedNamespace;
    readonly ICredentialKeyProvider keys;
    readonly TimeProvider clock;

    public CredentialRecoveryRehearsal(CredentialVaultNamespaceIdentity expectedNamespace, ICredentialKeyProvider keys, TimeProvider? timeProvider = null)
    {
        this.expectedNamespace = expectedNamespace ?? throw Failure("configuration-invalid");
        this.keys = keys ?? throw Failure("configuration-invalid"); clock = timeProvider ?? TimeProvider.System;
    }

    public async Task<CredentialRecoveryRehearsalResult> RehearseAsync(CredentialRecoverySet set,
        CredentialRecoverySetAuthority pinnedAuthority, RSA pinnedPublicKey, CancellationToken cancellationToken = default)
    {
        using var deadline = CredentialRules.OperationDeadline(TimeSpan.FromSeconds(20), cancellationToken); var operationToken = deadline.Token;
        try
        {
            operationToken.ThrowIfCancellationRequested();
            // Detached, authenticated retained bytes remain private until every awaited source-key lookup has completed.
            using var retained = CredentialRecoverySets.ValidateRetainedSet(set, pinnedAuthority, pinnedPublicKey, expectedNamespace, clock);
            operationToken.ThrowIfCancellationRequested();
            var expected = retained.Verification.Inventory.ProfileDependencies.ToDictionary(profile => profile.ProfileIdSha256, StringComparer.Ordinal);
            var authenticated = new List<CredentialRecoveryProfileDependency>(expected.Count);
            foreach (var envelope in retained.Envelopes)
            {
                operationToken.ThrowIfCancellationRequested();
                var actual = await CredentialVault.AuthenticateRecoveryEnvelopeAsync(envelope.EncryptedBytes, envelope.ProfileIdSha256,
                    expectedNamespace, keys, operationToken);
                operationToken.ThrowIfCancellationRequested();
                if (!expected.TryGetValue(envelope.ProfileIdSha256, out var metadata) ||
                    JsonSerializer.Serialize(actual, CredentialRules.Json) != JsonSerializer.Serialize(metadata, CredentialRules.Json))
                    throw Failure("metadata-mismatch");
                authenticated.Add(actual);
            }
            if (authenticated.Count != expected.Count || authenticated.Select(profile => profile.ProfileIdSha256).Distinct(StringComparer.Ordinal).Count() != expected.Count)
                throw Failure("metadata-mismatch");
            operationToken.ThrowIfCancellationRequested();
            var completedUtc = DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds());
            CredentialRecoverySets.ValidateLifetime(retained.Verification.Signature, completedUtc);
            operationToken.ThrowIfCancellationRequested();
            return new(retained.Verification, authenticated.OrderBy(profile => profile.ProfileIdSha256, StringComparer.Ordinal), completedUtc);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Credential recovery rehearsal canceled.", cancellationToken); }
        catch (OperationCanceledException) when (operationToken.IsCancellationRequested) { throw Failure("timeout"); }
        catch (OperationCanceledException) { throw Failure("unavailable"); }
        catch (CredentialException) { throw; }
        catch (Exception) { throw Failure("unavailable"); }
    }
    static CredentialException Failure(string reason) => new("credential-recovery-rehearsal-" + reason);
}
