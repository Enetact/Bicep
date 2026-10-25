using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SelfService.Foundation;

public sealed partial class CredentialVault
{
    internal static async Task<PreparedCredentialRecoveryStageEnvelope> PrepareScopedRecoveryStageEnvelopeAsync(ReadOnlyMemory<byte> retained,
        string hash, CredentialScopedRecoveryStagePlan plan, ICredentialKeyProvider sourceKeys, CredentialEncryptionKey destinationKey, CancellationToken token)
    {
        var observed = await AuthenticateEnvelopeBytes(retained, hash, plan.SourceNamespace.CustomerId, plan.SourceNamespace.CellId, sourceKeys, token);
        using var secret = observed.Secret; token.ThrowIfCancellationRequested(); var full = FullProfileHash(observed.Envelope.Profile);
        if (plan.PreservedProfiles.SingleOrDefault(p => p.ProfileIdSha256 == hash)?.ProfileSha256 != full) throw CredentialScopedRecoveryStaging.Failure("preserved-profile-mismatch");
        var source = RecoveryDependency(observed.Envelope.Profile, observed.Envelope.KeyId, hash, retained.Span); var bytes = destinationKey.CopyBytes();
        try
        {
            var candidate = EncryptEnvelopeCandidate(observed.Envelope.Profile, secret, plan.DestinationKey.KeyId, bytes);
            return new(candidate, new(source, RecoveryDependency(observed.Envelope.Profile, plan.DestinationKey.KeyId, hash, candidate), full));
        }
        finally { CryptographicOperations.ZeroMemory(bytes); }
    }
    internal static async Task<CredentialRecoveryStageAuthentication> AuthenticateScopedStageEnvelopeAsync(ReadOnlyMemory<byte> bytes,
        string hash, CredentialScopedRecoveryStagePlan plan, CredentialEncryptionKey key, CancellationToken token)
    {
        var observed = await AuthenticateEnvelopeBytes(bytes, hash, plan.SourceNamespace.CustomerId, plan.SourceNamespace.CellId, new FrozenStageKeyProvider(plan.DestinationKey, key), token);
        using var secret = observed.Secret; token.ThrowIfCancellationRequested();
        if (observed.Envelope.KeyId != plan.DestinationKey.KeyId) throw CredentialScopedRecoveryStaging.Failure("destination-key-mismatch");
        return new(RecoveryDependency(observed.Envelope.Profile, observed.Envelope.KeyId, hash, bytes.Span), FullProfileHash(observed.Envelope.Profile));
    }
}

internal static class CredentialScopedStageProofs
{
    static readonly byte[] Salt = Encoding.UTF8.GetBytes("platform-studio.credential-scoped-stage-absence.v1.hkdf-salt");
    static readonly byte[] Info = Encoding.UTF8.GetBytes("platform-studio.credential-scoped-stage-absence.v1.mac-key");
    internal static CredentialScopedStageAbsenceProof Create(CredentialAbsenceScopeProof source, CredentialScopedRecoveryStagePlan plan,
        string stageId, string vaultId, string authorityId, string bindingSha, string coreSha, CredentialEncryptionKey destinationKey)
    {
        var proof = new CredentialScopedStageAbsenceProof(1, "credential-scoped-recovery-stage-absence", source.ProfileIdSha256,
            CredentialRules.Snapshot(source.Scope), source.ScopeSha256, source.Sha256, source.KeyId, plan.SourceNamespace, stageId, vaultId, authorityId,
            plan.Sha256, plan.SourceSetAuthority.ArtifactSha256, plan.DestinationRootCommitmentSha256, plan.DestinationKey, bindingSha, coreSha,
            "HKDF-SHA256/HMAC-SHA256", "", "");
        Validate(proof, unsigned: true); var key = destinationKey.CopyBytes(); byte[]? mac = null;
        try
        {
            mac = Mac(proof, key); proof = proof with { Tag = Convert.ToHexStringLower(mac) }; return proof with { Sha256 = Digest(proof) };
        }
        finally { CryptographicOperations.ZeroMemory(key); if (mac is not null) CryptographicOperations.ZeroMemory(mac); }
    }
    internal static CredentialAbsenceScopeDependency Projection(CredentialScopedStageAbsenceProof p) => new(p.ProfileIdSha256, p.ScopeSha256, p.Scope.ProjectId, p.Scope.EnvironmentId, p.DestinationKey.KeyId, p.Sha256);
    internal static void Authenticate(CredentialScopedStageAbsenceProof proof, CredentialEncryptionKey frozenKey)
    {
        Validate(proof); var key = frozenKey.CopyBytes(); byte[]? actual = null, supplied = null;
        try
        {
            actual = Mac(proof, key); supplied = Convert.FromHexString(proof.Tag);
            if (!CryptographicOperations.FixedTimeEquals(actual, supplied)) throw CredentialScopedRecoveryStaging.Failure("destination-proof-authentication-failed");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key); if (actual is not null) CryptographicOperations.ZeroMemory(actual); if (supplied is not null) CryptographicOperations.ZeroMemory(supplied);
        }
    }
    internal static void Validate(CredentialScopedStageAbsenceProof p, bool unsigned = false)
    {
        if (p is null || p.Version != 1 || p.Kind != "credential-scoped-recovery-stage-absence" || p.Algorithm != "HKDF-SHA256/HMAC-SHA256")
            throw CredentialScopedRecoveryStaging.Failure("destination-proof-invalid");
        CredentialRecoverySets.RequireIdentity(p.SourceNamespace); var scope = CredentialRules.Snapshot(p.Scope);
        if (p.DestinationKey is null || p.DestinationKey.CustomerId != scope.CustomerId || p.DestinationKey.CellId != scope.CellId ||
            scope.CustomerId != p.SourceNamespace.CustomerId || scope.CellId != p.SourceNamespace.CellId ||
            CredentialRules.Hash(scope.AuthProfileId) != p.ProfileIdSha256 || CredentialRules.ScopeDigest(scope) != p.ScopeSha256 ||
            JsonSerializer.Serialize(scope, CredentialRules.Json) != JsonSerializer.Serialize(p.Scope, CredentialRules.Json) || !CredentialRules.ProfileId(p.SourceKeyId) || !CredentialRules.ProfileId(p.DestinationKey.KeyId) ||
            new[] { p.StageId, p.StagedVaultNamespaceId, p.StagedAuthorityNamespaceId }.Any(x => !CredentialScopedRecoveryStaging.Id(x)) ||
            new[] { p.StageId, p.StagedVaultNamespaceId, p.StagedAuthorityNamespaceId }.Distinct(StringComparer.Ordinal).Count() != 3 ||
            new[] { p.ProfileIdSha256, p.ScopeSha256, p.SourceProofSha256, p.PlanSha256, p.SourceSetSha256, p.DestinationRootCommitmentSha256,
                p.DestinationKey.CommitmentSha256, p.BindingSha256, p.CompletionCoreSha256 }.Any(x => !CredentialScopedRecoveryStaging.Digest(x)) ||
            !unsigned && (!CredentialScopedRecoveryStaging.Digest(p.Tag) || p.Sha256 != Digest(p))) throw CredentialScopedRecoveryStaging.Failure("destination-proof-invalid");
    }
    internal static string Digest(CredentialScopedStageAbsenceProof p) => CredentialRecoverySets.Hash(JsonSerializer.SerializeToUtf8Bytes(new { payload = Payload(p), p.Tag }, CredentialRules.Json));
    static object Payload(CredentialScopedStageAbsenceProof p) => new
    { domain = "platform-studio.credential-scoped-stage-absence.v1", p.Version, p.Kind, p.ProfileIdSha256, p.Scope, p.ScopeSha256, p.SourceProofSha256,
        p.SourceKeyId, p.SourceNamespace, p.StageId, p.StagedVaultNamespaceId, p.StagedAuthorityNamespaceId, p.PlanSha256, p.SourceSetSha256,
        p.DestinationRootCommitmentSha256, p.DestinationKey, p.BindingSha256, p.CompletionCoreSha256, p.Algorithm };
    static byte[] Mac(CredentialScopedStageAbsenceProof p, ReadOnlySpan<byte> key)
    {
        Span<byte> derived = stackalloc byte[32]; var bytes = JsonSerializer.SerializeToUtf8Bytes(Payload(p), CredentialRules.Json);
        try { HKDF.DeriveKey(HashAlgorithmName.SHA256, key, derived, Salt, Info); return HMACSHA256.HashData(derived, bytes); }
        finally { CryptographicOperations.ZeroMemory(derived); CryptographicOperations.ZeroMemory(bytes); }
    }
}
