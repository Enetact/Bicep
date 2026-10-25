using System.Security.Cryptography;
using System.Text.Json;

namespace SelfService.Foundation;

internal sealed class PreparedCredentialRecoveryStageEnvelope(byte[] bytes, CredentialRecoveryStagedProfile profile) : IDisposable
{
    internal byte[] Bytes { get; } = bytes;
    internal CredentialRecoveryStagedProfile Profile { get; } = profile;
    public void Dispose() => CryptographicOperations.ZeroMemory(Bytes);
}

public sealed partial class CredentialVault
{
    // Pure signed-header projection only. Publication still requires AES authentication of the original retained bytes.
    internal static string RecoveryProfileSha256(ReadOnlyMemory<byte> bytes)
    {
        try
        {
            using var document = CredentialRules.Document(bytes);
            var envelope = document.RootElement.Deserialize<Envelope>(CredentialRules.Json) ?? throw new JsonException();
            return FullProfileHash(Detached(envelope.Profile));
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException or InvalidOperationException)
        { throw new CredentialException("credential-envelope-integrity"); }
    }

    internal static async Task<PreparedCredentialRecoveryStageEnvelope> PrepareRecoveryStageEnvelopeAsync(ReadOnlyMemory<byte> retained,
        string profileHash, CredentialRecoveryStagePlan plan, ICredentialKeyProvider sourceKeys, CredentialEncryptionKey frozenDestinationKey,
        CancellationToken cancellationToken)
    {
        var authenticated = await AuthenticateEnvelopeBytes(retained, profileHash, plan.SourceNamespace.CustomerId, plan.SourceNamespace.CellId, sourceKeys, cancellationToken);
        using var secret = authenticated.Secret; cancellationToken.ThrowIfCancellationRequested();
        var profile = authenticated.Envelope.Profile; var fullHash = FullProfileHash(profile);
        if (plan.PreservedProfiles.SingleOrDefault(p => p.ProfileIdSha256 == profileHash)?.ProfileSha256 != fullHash)
            throw CredentialRecoveryStaging.Failure("preserved-profile-mismatch");
        var original = RecoveryDependency(profile, authenticated.Envelope.KeyId, profileHash, retained.Span);
        var keyBytes = frozenDestinationKey.CopyBytes(); byte[]? candidate = null;
        try
        {
            // The authenticated original object retains expiry offset/ticks and the original generation/enabled/scope/kind.
            candidate = EncryptEnvelopeCandidate(profile, secret, plan.DestinationKey.KeyId, keyBytes);
            cancellationToken.ThrowIfCancellationRequested();
            var destination = RecoveryDependency(profile, plan.DestinationKey.KeyId, profileHash, candidate);
            var prepared = new PreparedCredentialRecoveryStageEnvelope(candidate, new(original, destination, fullHash)); candidate = null;
            return prepared;
        }
        finally { CryptographicOperations.ZeroMemory(keyBytes); if (candidate is not null) CryptographicOperations.ZeroMemory(candidate); }
    }

    internal static async Task<CredentialRecoveryStageAuthentication> AuthenticateStageEnvelopeAsync(ReadOnlyMemory<byte> bytes,
        string profileHash, CredentialRecoveryStagePlan plan, CredentialEncryptionKey frozenDestinationKey, CancellationToken cancellationToken)
    {
        var provider = new FrozenStageKeyProvider(plan.DestinationKey, frozenDestinationKey);
        var authenticated = await AuthenticateEnvelopeBytes(bytes, profileHash, plan.SourceNamespace.CustomerId, plan.SourceNamespace.CellId, provider, cancellationToken);
        using var secret = authenticated.Secret; cancellationToken.ThrowIfCancellationRequested();
        if (authenticated.Envelope.KeyId != plan.DestinationKey.KeyId) throw CredentialRecoveryStaging.Failure("destination-key-mismatch");
        return new(RecoveryDependency(authenticated.Envelope.Profile, authenticated.Envelope.KeyId, profileHash, bytes.Span), FullProfileHash(authenticated.Envelope.Profile));
    }
    static string FullProfileHash(CredentialProfile profile) => CredentialRecoverySets.Hash(JsonSerializer.SerializeToUtf8Bytes(profile, CredentialRules.Json));
    static CredentialRecoveryProfileDependency RecoveryDependency(CredentialProfile profile, string keyId, string profileHash, ReadOnlySpan<byte> envelope)
    {
        var dependency = new CredentialRecoveryProfileDependency(profileHash, CredentialRules.ScopeDigest(profile.Scope), profile.Scope.ProjectId,
            profile.Scope.EnvironmentId, profile.Generation, profile.Enabled, profile.ExpiresUtc.ToUnixTimeMilliseconds(), keyId, CredentialRecoverySets.Hash(envelope), envelope.Length);
        CredentialRecoveryInventory.ValidateProfile(dependency); return dependency;
    }
    sealed class FrozenStageKeyProvider(CredentialRecoveryDestinationKeyIdentity identity, CredentialEncryptionKey key) : ICredentialKeyProvider
    {
        public ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (customerId != identity.CustomerId || cellId != identity.CellId || keyId != identity.KeyId) throw CredentialRecoveryStaging.Failure("destination-key-mismatch");
            var bytes = key.CopyBytes();
            try { return ValueTask.FromResult(new CredentialEncryptionKey(bytes)); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
    }
}
