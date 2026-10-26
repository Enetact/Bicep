using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SelfService.Foundation;

internal static class CredentialRestrictedStageProofs
{
    static readonly byte[] Salt = Encoding.UTF8.GetBytes("platform-studio.credential-restricted-stage-absence.v1.hkdf-salt");
    static readonly byte[] Info = Encoding.UTF8.GetBytes("platform-studio.credential-restricted-stage-absence.v1.mac-key");
    internal static CredentialRestrictedStageAbsenceProof Create(CredentialAbsenceScopeProof source, CredentialRestrictedRecoveryStagePlan plan,
        string stageId, string vaultId, string authorityId, string bindingSha, string coreSha, CredentialEncryptionKey destinationKey)
    {
        var proof = new CredentialRestrictedStageAbsenceProof(3, "credential-restricted-recovery-stage-absence", CredentialRestrictedRecoveryStaging.Profile, source.ProfileIdSha256,
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
    internal static CredentialAbsenceScopeDependency Projection(CredentialRestrictedStageAbsenceProof p) => new(p.ProfileIdSha256, p.ScopeSha256, p.Scope.ProjectId, p.Scope.EnvironmentId, p.DestinationKey.KeyId, p.Sha256);
    internal static void Authenticate(CredentialRestrictedStageAbsenceProof proof, CredentialEncryptionKey frozenKey)
    {
        Validate(proof); var key = frozenKey.CopyBytes(); byte[]? actual = null, supplied = null;
        try
        {
            actual = Mac(proof, key); supplied = Convert.FromHexString(proof.Tag);
            if (!CryptographicOperations.FixedTimeEquals(actual, supplied)) throw CredentialRestrictedRecoveryStaging.Failure("destination-proof-authentication-failed");
        }
        finally
        {
            CryptographicOperations.ZeroMemory(key); if (actual is not null) CryptographicOperations.ZeroMemory(actual); if (supplied is not null) CryptographicOperations.ZeroMemory(supplied);
        }
    }
    internal static void Validate(CredentialRestrictedStageAbsenceProof p, bool unsigned = false)
    {
        if (p is null || p.Version != 3 || p.Kind != "credential-restricted-recovery-stage-absence" || p.AccessProfile != CredentialRestrictedRecoveryStaging.Profile || p.Algorithm != "HKDF-SHA256/HMAC-SHA256")
            throw CredentialRestrictedRecoveryStaging.Failure("destination-proof-invalid");
        CredentialRecoverySets.RequireIdentity(p.SourceNamespace); var scope = CredentialRules.Snapshot(p.Scope);
        if (p.DestinationKey is null || p.DestinationKey.CustomerId != scope.CustomerId || p.DestinationKey.CellId != scope.CellId ||
            scope.CustomerId != p.SourceNamespace.CustomerId || scope.CellId != p.SourceNamespace.CellId ||
            CredentialRules.Hash(scope.AuthProfileId) != p.ProfileIdSha256 || CredentialRules.ScopeDigest(scope) != p.ScopeSha256 ||
            JsonSerializer.Serialize(scope, CredentialRules.Json) != JsonSerializer.Serialize(p.Scope, CredentialRules.Json) || !CredentialRules.ProfileId(p.SourceKeyId) || !CredentialRules.ProfileId(p.DestinationKey.KeyId) ||
            new[] { p.StageId, p.StagedVaultNamespaceId, p.StagedAuthorityNamespaceId }.Any(x => !CredentialRestrictedRecoveryStaging.Id(x)) ||
            new[] { p.StageId, p.StagedVaultNamespaceId, p.StagedAuthorityNamespaceId }.Distinct(StringComparer.Ordinal).Count() != 3 ||
            new[] { p.ProfileIdSha256, p.ScopeSha256, p.SourceProofSha256, p.PlanSha256, p.SourceSetSha256, p.DestinationRootCommitmentSha256,
                p.DestinationKey.CommitmentSha256, p.BindingSha256, p.CompletionCoreSha256 }.Any(x => !CredentialRestrictedRecoveryStaging.Digest(x)) ||
            !unsigned && (!CredentialRestrictedRecoveryStaging.Digest(p.Tag) || p.Sha256 != Digest(p))) throw CredentialRestrictedRecoveryStaging.Failure("destination-proof-invalid");
    }
    internal static string Digest(CredentialRestrictedStageAbsenceProof p) => CredentialRecoverySets.Hash(JsonSerializer.SerializeToUtf8Bytes(new { payload = Payload(p), p.Tag }, CredentialRules.Json));
    static object Payload(CredentialRestrictedStageAbsenceProof p) => new
    { domain = "platform-studio.credential-restricted-stage-absence.v1", p.Version, p.Kind, p.AccessProfile, p.ProfileIdSha256, p.Scope, p.ScopeSha256, p.SourceProofSha256,
        p.SourceKeyId, p.SourceNamespace, p.StageId, p.StagedVaultNamespaceId, p.StagedAuthorityNamespaceId, p.PlanSha256, p.SourceSetSha256,
        p.DestinationRootCommitmentSha256, p.DestinationKey, p.BindingSha256, p.CompletionCoreSha256, p.Algorithm };
    static byte[] Mac(CredentialRestrictedStageAbsenceProof p, ReadOnlySpan<byte> key)
    {
        Span<byte> derived = stackalloc byte[32]; var bytes = JsonSerializer.SerializeToUtf8Bytes(Payload(p), CredentialRules.Json);
        try { HKDF.DeriveKey(HashAlgorithmName.SHA256, key, derived, Salt, Info); return HMACSHA256.HashData(derived, bytes); }
        finally { CryptographicOperations.ZeroMemory(derived); CryptographicOperations.ZeroMemory(bytes); }
    }
}

// Required even for empty stages. Its private tag is not a public credential or grant.
internal static class CredentialRestrictedStageCompletionProofs
{
    static readonly byte[] Salt = Encoding.UTF8.GetBytes("platform-studio.credential-restricted-stage-completion.v1.hkdf-salt");
    static readonly byte[] Info = Encoding.UTF8.GetBytes("platform-studio.credential-restricted-stage-completion.v1.mac-key");
    internal static CredentialRestrictedStageCompletionProof Create(CredentialRestrictedRecoveryStagePlan plan,
        CredentialRestrictedRecoveryStageReceipt receipt, CredentialEncryptionKey key)
    {
        var proof = new CredentialRestrictedStageCompletionProof(3, "credential-restricted-recovery-stage-completion", CredentialRestrictedRecoveryStaging.Profile,
            "HKDF-SHA256/HMAC-SHA256", "", "");
        byte[]? mac = null;
        try
        {
            mac = Mac(plan, receipt, proof, key); proof = proof with { Tag = Convert.ToHexStringLower(mac) };
            return proof with { Sha256 = Digest(plan, receipt, proof) };
        }
        finally { if (mac is not null) CryptographicOperations.ZeroMemory(mac); }
    }
    internal static void Validate(CredentialRestrictedRecoveryStagePlan plan, CredentialRestrictedRecoveryStageReceipt receipt,
        CredentialRestrictedStageCompletionProof p)
    {
        if (p is null || p.Version != 3 || p.Kind != "credential-restricted-recovery-stage-completion" || p.AccessProfile != CredentialRestrictedRecoveryStaging.Profile ||
            p.Algorithm != "HKDF-SHA256/HMAC-SHA256" || !CredentialRestrictedRecoveryStaging.Digest(p.Tag) ||
            p.Sha256 != receipt.CompletionProofSha256 || p.Sha256 != Digest(plan, receipt, p))
            throw CredentialRestrictedRecoveryStaging.Failure("destination-completion-proof-invalid");
    }
    internal static void Authenticate(CredentialRestrictedRecoveryStagePlan plan, CredentialRestrictedRecoveryStageReceipt receipt,
        CredentialRestrictedStageCompletionProof proof, CredentialEncryptionKey key)
    {
        Validate(plan, receipt, proof); byte[]? actual = null, supplied = null;
        try
        {
            actual = Mac(plan, receipt, proof, key); supplied = Convert.FromHexString(proof.Tag);
            if (!CryptographicOperations.FixedTimeEquals(actual, supplied))
                throw CredentialRestrictedRecoveryStaging.Failure("destination-completion-proof-authentication-failed");
        }
        finally { if (actual is not null) CryptographicOperations.ZeroMemory(actual); if (supplied is not null) CryptographicOperations.ZeroMemory(supplied); }
    }
    internal static string Digest(CredentialRestrictedRecoveryStagePlan plan, CredentialRestrictedRecoveryStageReceipt receipt,
        CredentialRestrictedStageCompletionProof p) => CredentialRecoverySets.Hash(JsonSerializer.SerializeToUtf8Bytes(new { payload = Payload(plan, receipt, p), p.Tag }, CredentialRules.Json));
    static object Payload(CredentialRestrictedRecoveryStagePlan plan, CredentialRestrictedRecoveryStageReceipt r, CredentialRestrictedStageCompletionProof p) => new
    {
        domain = "platform-studio.credential-restricted-stage-completion.v1", p.Version, p.Kind, p.AccessProfile, p.Algorithm,
        plan.SourceNamespace, r.Schema, r.PlanSha256, r.SourceSetSha256, r.StageId, r.StagedVaultNamespaceId, r.StagedAuthorityNamespaceId,
        r.DestinationRootCommitmentSha256, r.DestinationKey, r.BindingSha256, r.StateSha256, r.CompletionCoreSha256,
        reservedUnixMilliseconds = r.ReservedUtc.ToUnixTimeMilliseconds(), completedUnixMilliseconds = r.CompletedUtc.ToUnixTimeMilliseconds(),
        r.Profiles, r.Absences, r.InactiveRecoveryHold, r.NormalVaultUsable, r.PromotionAuthorized, r.ActivationPerformed,
        r.SourceHostFenced, r.ExternalExecutionAuthorized, r.KeyMaterialExported, r.KeyRetirementAuthorized, r.IndependentBackupCoverage, r.ProductionRecoveryQualified
    };
    static byte[] Mac(CredentialRestrictedRecoveryStagePlan plan, CredentialRestrictedRecoveryStageReceipt receipt,
        CredentialRestrictedStageCompletionProof proof, CredentialEncryptionKey key)
    {
        Span<byte> derived = stackalloc byte[32]; var material = key.CopyBytes(); var payload = JsonSerializer.SerializeToUtf8Bytes(Payload(plan, receipt, proof), CredentialRules.Json);
        try { HKDF.DeriveKey(HashAlgorithmName.SHA256, material, derived, Salt, Info); return HMACSHA256.HashData(derived, payload); }
        finally { CryptographicOperations.ZeroMemory(derived); CryptographicOperations.ZeroMemory(material); CryptographicOperations.ZeroMemory(payload); }
    }
}
