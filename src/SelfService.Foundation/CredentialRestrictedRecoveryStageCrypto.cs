using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SelfService.Foundation;

public sealed partial class CredentialVault
{
    // This format is private inactive-stage data. It is never an ordinary vault envelope.
    sealed record RestrictedStageEnvelope(int Version, string Kind, string AccessProfile, string StageId,
        string StagedVaultNamespaceId, string StagedAuthorityNamespaceId, string PlanSha256, string SourceSetSha256,
        CredentialVaultNamespaceIdentity SourceNamespace, string DestinationRootCommitmentSha256,
        CredentialRestrictedRecoveryDestinationKeyIdentity DestinationKey, string BindingSha256, string KeyId,
        CredentialProfile Profile, string Nonce, string Ciphertext, string Tag);

    internal static async Task<PreparedCredentialRecoveryStageEnvelope> PrepareRestrictedRecoveryStageEnvelopeAsync(ReadOnlyMemory<byte> retained,
        string hash, CredentialRestrictedRecoveryStagePlan plan, CredentialRestrictedStageEnvelopeContext context,
        ICredentialKeyProvider sourceKeys, CredentialEncryptionKey destinationKey, CancellationToken token)
    {
        var observed = await AuthenticateEnvelopeBytes(retained, hash, plan.SourceNamespace.CustomerId, plan.SourceNamespace.CellId, sourceKeys, token);
        using var secret = observed.Secret; token.ThrowIfCancellationRequested(); var full = FullProfileHash(observed.Envelope.Profile);
        if (plan.PreservedProfiles.SingleOrDefault(p => p.ProfileIdSha256 == hash)?.ProfileSha256 != full)
            throw CredentialRestrictedRecoveryStaging.Failure("preserved-profile-mismatch");
        var source = RecoveryDependency(observed.Envelope.Profile, observed.Envelope.KeyId, hash, retained.Span);
        var key = destinationKey.CopyBytes(); var clear = secret.CopyBytes(); byte[]? aad = null, candidate = null;
        try
        {
            var header = new RestrictedStageEnvelope(3, "credential-restricted-recovery-stage-envelope", CredentialRestrictedRecoveryStaging.Profile,
                context.StageId, context.StagedVaultNamespaceId, context.StagedAuthorityNamespaceId, plan.Sha256, plan.SourceSetAuthority.ArtifactSha256,
                plan.SourceNamespace, plan.DestinationRootCommitmentSha256, plan.DestinationKey, context.BindingSha256, plan.DestinationKey.KeyId,
                observed.Envelope.Profile, "", "", "");
            var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[clear.Length]; var tag = new byte[16]; aad = RestrictedStageAad(header);
            using (var aes = new AesGcm(key, 16)) aes.Encrypt(nonce, clear, cipher, tag, aad);
            candidate = JsonSerializer.SerializeToUtf8Bytes(header with
            { Nonce = Convert.ToBase64String(nonce), Ciphertext = Convert.ToBase64String(cipher), Tag = Convert.ToBase64String(tag) }, CredentialRules.Json);
            if (candidate.Length > 131072) throw CredentialRestrictedRecoveryStaging.Failure("capacity-exceeded");
            var result = new PreparedCredentialRecoveryStageEnvelope(candidate,
                new(source, RecoveryDependency(observed.Envelope.Profile, plan.DestinationKey.KeyId, hash, candidate), full)); candidate = null; return result;
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key); CryptographicOperations.ZeroMemory(clear);
            if (aad is not null) CryptographicOperations.ZeroMemory(aad); if (candidate is not null) CryptographicOperations.ZeroMemory(candidate);
        }
    }

    internal static Task<CredentialRecoveryStageAuthentication> AuthenticateRestrictedStageEnvelopeAsync(ReadOnlyMemory<byte> bytes,
        string hash, CredentialRestrictedRecoveryStagePlan plan, CredentialRestrictedStageEnvelopeContext context,
        CredentialEncryptionKey key, CancellationToken token)
    {
        token.ThrowIfCancellationRequested(); var envelope = ReadRestrictedStageEnvelope(bytes, hash, plan, context);
        var material = key.CopyBytes(); byte[]? clear = null, aad = null;
        try
        {
            var nonce = RestrictedStageBase64(envelope.Nonce, 12, 12); var cipher = RestrictedStageBase64(envelope.Ciphertext, 1, 32768);
            var tag = RestrictedStageBase64(envelope.Tag, 16, 16); clear = new byte[cipher.Length]; aad = RestrictedStageAad(envelope);
            using (var aes = new AesGcm(material, 16)) aes.Decrypt(nonce, cipher, tag, clear, aad);
            CredentialRules.Token(Encoding.UTF8.GetString(clear)); token.ThrowIfCancellationRequested();
            return Task.FromResult(new CredentialRecoveryStageAuthentication(
                RecoveryDependency(envelope.Profile, envelope.KeyId, hash, bytes.Span), FullProfileHash(envelope.Profile)));
        }
        catch (Exception error) when (error is CryptographicException or FormatException or ArgumentException)
        { throw CredentialRestrictedRecoveryStaging.Failure("destination-envelope-authentication-failed"); }
        finally
        {
            CryptographicOperations.ZeroMemory(material); if (clear is not null) CryptographicOperations.ZeroMemory(clear);
            if (aad is not null) CryptographicOperations.ZeroMemory(aad);
        }
    }

    // Structural projection only. Successful AES authentication is a separate mandatory verifier step.
    internal static string RestrictedStageProfileSha256(ReadOnlyMemory<byte> bytes, string hash,
        CredentialRestrictedRecoveryStagePlan plan, CredentialRestrictedStageEnvelopeContext context) =>
        FullProfileHash(ReadRestrictedStageEnvelope(bytes, hash, plan, context).Profile);

    static RestrictedStageEnvelope ReadRestrictedStageEnvelope(ReadOnlyMemory<byte> bytes, string hash,
        CredentialRestrictedRecoveryStagePlan p, CredentialRestrictedStageEnvelopeContext c)
    {
        try
        {
            if (bytes.Length is < 2 or > 131072) throw CredentialRestrictedRecoveryStaging.Failure("capacity-exceeded");
            using var doc = CredentialRules.Document(bytes); var e = doc.RootElement.Deserialize<RestrictedStageEnvelope>(CredentialRules.Json) ?? throw new JsonException();
            var profile = Detached(e.Profile);
            if (e.Version != 3 || e.Kind != "credential-restricted-recovery-stage-envelope" || e.AccessProfile != CredentialRestrictedRecoveryStaging.Profile ||
                e.StageId != c.StageId || e.StagedVaultNamespaceId != c.StagedVaultNamespaceId || e.StagedAuthorityNamespaceId != c.StagedAuthorityNamespaceId ||
                e.PlanSha256 != p.Sha256 || e.SourceSetSha256 != p.SourceSetAuthority.ArtifactSha256 || e.SourceNamespace != p.SourceNamespace ||
                e.DestinationRootCommitmentSha256 != p.DestinationRootCommitmentSha256 || e.DestinationKey != p.DestinationKey || e.BindingSha256 != c.BindingSha256 ||
                e.KeyId != p.DestinationKey.KeyId || profile.Scope.CustomerId != p.SourceNamespace.CustomerId || profile.Scope.CellId != p.SourceNamespace.CellId ||
                CredentialRules.Hash(profile.Scope.AuthProfileId) != hash || profile.Generation <= 0 ||
                profile.Kind != (profile.Scope.Provider == PipelineRequestFactory.GitHub ? "github-installation-token" : "entra-access-token")) throw new JsonException();
            _ = RestrictedStageBase64(e.Nonce, 12, 12); _ = RestrictedStageBase64(e.Ciphertext, 1, 32768); _ = RestrictedStageBase64(e.Tag, 16, 16);
            return e with { Profile = profile };
        }
        catch (Exception error) when (error is JsonException or FormatException or ArgumentException or NullReferenceException or InvalidOperationException)
        { throw CredentialRestrictedRecoveryStaging.Failure("destination-envelope-invalid"); }
    }
    static byte[] RestrictedStageBase64(string value, int minimum, int maximum)
    {
        if (value is null || value.Length > 4 * ((maximum + 2) / 3)) throw new FormatException();
        var bytes = Convert.FromBase64String(value);
        if (bytes.Length < minimum || bytes.Length > maximum || Convert.ToBase64String(bytes) != value) throw new FormatException(); return bytes;
    }
    static byte[] RestrictedStageAad(RestrictedStageEnvelope e) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        domain = "platform-studio.credential-restricted-stage-envelope.v1", e.Version, e.Kind, e.AccessProfile,
        e.StageId, e.StagedVaultNamespaceId, e.StagedAuthorityNamespaceId, e.PlanSha256, e.SourceSetSha256, e.SourceNamespace,
        e.DestinationRootCommitmentSha256, e.DestinationKey, e.BindingSha256, e.KeyId, e.Profile
    }, CredentialRules.Json);
}
