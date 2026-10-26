using System.Security.Cryptography;
using System.Text.Json;

namespace SelfService.Foundation;

public static partial class CredentialRestrictedRecoverySets
{
    static CredentialScopedRecoverySetSignature Metadata(CredentialRestrictedRecoverySetSignature s) => new(s.Version, s.Kind, s.SetId, s.CustomerId, s.CellId,
        s.SourceNamespace, s.Generation, s.CapturedUtc, s.IssuedUtc, s.NotBeforeUtc, s.ExpiresUtc, s.InventorySha256, s.ProfileCount, s.AbsenceProofCount,
        s.ContentSha256, s.ContentLength, s.KeyId, s.PublicKeySha256, s.Algorithm, s.Signature);
    internal static void RequireSignature(CredentialRestrictedRecoverySetSignature signature, bool allowUnsigned = false)
    {
        if (signature is null || signature.Version != 3 || signature.Kind != "credential-restricted-recovery-set" || signature.AccessProfile != Profile) throw Failure("signature-invalid");
        CredentialScopedRecoverySets.RequireSignatureMetadata(Metadata(signature), allowUnsigned);
    }
    internal static byte[] CanonicalPayload(CredentialRestrictedRecoverySetSignature s)
    {
        RequireSignature(s, true);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            s.Version, s.Kind, s.AccessProfile, s.SetId, s.CustomerId, s.CellId, s.SourceNamespace, s.Generation,
            capturedUnixMilliseconds = s.CapturedUtc.ToUnixTimeMilliseconds(), issuedUnixMilliseconds = s.IssuedUtc.ToUnixTimeMilliseconds(),
            notBeforeUnixMilliseconds = s.NotBeforeUtc.ToUnixTimeMilliseconds(), expiresUnixMilliseconds = s.ExpiresUtc.ToUnixTimeMilliseconds(),
            s.InventorySha256, s.ProfileCount, s.AbsenceProofCount, s.ContentSha256, s.ContentLength, s.KeyId, s.PublicKeySha256, s.Algorithm
        }, Json);
    }
    internal static void ValidateLifetime(CredentialRestrictedRecoverySetSignature signature, TimeProvider clock) => ValidateLifetime(signature, clock.GetUtcNow());
    internal static void ValidateLifetime(CredentialRestrictedRecoverySetSignature signature, DateTimeOffset now)
    { RequireSignature(signature); CredentialScopedRecoverySets.ValidateLifetime(Metadata(signature), now); }
    static void VerifySignature(CredentialRestrictedRecoverySetSignature signature, RSA supplied, TimeProvider clock)
    {
        try
        {
            using var key = FoundationBackupSignatures.PublicCopy(supplied);
            if (FoundationAuditAnchors.PublicKeyFingerprint(key) != signature.PublicKeySha256) throw Failure("signer-mismatch");
            var bytes = Convert.FromBase64String(signature.Signature);
            try { if (bytes.Length != key.KeySize / 8 || !key.VerifyData(CanonicalPayload(signature), bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw Failure("signature-invalid"); }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (Exception e) when (e is CryptographicException or ArgumentException or ObjectDisposedException or NotSupportedException) { throw Failure("signature-invalid"); }
        ValidateLifetime(signature, clock);
    }
}
