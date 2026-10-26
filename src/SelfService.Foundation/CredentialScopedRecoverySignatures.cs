using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Foundation;

public static partial class CredentialScopedRecoverySets
{
    internal static byte[] CanonicalPayload(CredentialScopedRecoverySetSignature s)
    {
        RequireSignature(s, allowUnsigned: true);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            s.Version, s.Kind, s.SetId, s.CustomerId, s.CellId, s.SourceNamespace, s.Generation,
            capturedUnixMilliseconds = s.CapturedUtc.ToUnixTimeMilliseconds(), issuedUnixMilliseconds = s.IssuedUtc.ToUnixTimeMilliseconds(),
            notBeforeUnixMilliseconds = s.NotBeforeUtc.ToUnixTimeMilliseconds(), expiresUnixMilliseconds = s.ExpiresUtc.ToUnixTimeMilliseconds(),
            s.InventorySha256, s.ProfileCount, s.AbsenceProofCount, s.ContentSha256, s.ContentLength, s.KeyId, s.PublicKeySha256, s.Algorithm
        }, Json);
    }
    internal static void ValidateLifetime(CredentialScopedRecoverySetSignature signature, TimeProvider clock) => ValidateLifetime(signature, clock.GetUtcNow());
    internal static void ValidateLifetime(CredentialScopedRecoverySetSignature signature, DateTimeOffset now)
    {
        if (signature.IssuedUtc > now || signature.CapturedUtc > now) throw Failure("issued-in-future");
        if (signature.NotBeforeUtc > now) throw Failure("not-yet-valid");
        if (signature.ExpiresUtc <= now) throw Failure("expired");
    }
    internal static void RequireSignature(CredentialScopedRecoverySetSignature s, bool allowUnsigned = false)
    {
        if (s is null || s.Version != 2 || s.Kind != "credential-scoped-recovery-set") throw Failure("signature-invalid");
        RequireSignatureMetadata(s, allowUnsigned);
    }
    // Common bounded metadata only. Public version entrypoints always check their own closed domain first.
    internal static void RequireSignatureMetadata(CredentialScopedRecoverySetSignature s, bool allowUnsigned)
    {
        if (s is null) throw Failure("signature-invalid"); CredentialRecoverySets.RequireIdentity(s.SourceNamespace);
        if (s.SourceNamespace.AuthorityNamespaceId is null || !CredentialRecoverySets.NamespaceShape(s.SetId) ||
            s.CustomerId != s.SourceNamespace.CustomerId || s.CellId != s.SourceNamespace.CellId || s.Generation is < 1 or > CredentialRecoveryInventory.MaximumCounter ||
            !CredentialRecoverySets.Utc(s.CapturedUtc) || !CredentialRecoverySets.Utc(s.IssuedUtc) || !CredentialRecoverySets.Utc(s.NotBeforeUtc) || !CredentialRecoverySets.Utc(s.ExpiresUtc) ||
            s.CapturedUtc > s.IssuedUtc || s.IssuedUtc >= s.ExpiresUtc || s.NotBeforeUtc >= s.ExpiresUtc || s.ExpiresUtc - s.IssuedUtc > TimeSpan.FromDays(366) ||
            !Hash(s.InventorySha256) || !Hash(s.ContentSha256) || s.ContentLength is < 2 or > MaximumContentBytes || s.ProfileCount is < 0 or > MaximumProfiles ||
            s.AbsenceProofCount is < 0 or > MaximumProfiles || s.ProfileCount + s.AbsenceProofCount > MaximumProfiles || s.KeyId is null ||
            !Regex.IsMatch(s.KeyId, "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z", RegexOptions.CultureInvariant) || !Hash(s.PublicKeySha256) || s.Algorithm != "RSA-PSS-SHA256" ||
            !(allowUnsigned && s.Signature == "" || s.Signature is { Length: >= 344 and <= 1368 } && Regex.IsMatch(s.Signature, "\\A[A-Za-z0-9+/]+={0,2}\\z", RegexOptions.CultureInvariant))) throw Failure("signature-invalid");
        if (!(allowUnsigned && s.Signature == ""))
        {
            byte[]? bytes = null;
            try { bytes = Convert.FromBase64String(s.Signature); if (Convert.ToBase64String(bytes) != s.Signature) throw Failure("signature-invalid"); }
            catch (FormatException) { throw Failure("signature-invalid"); }
            finally { if (bytes is not null) CryptographicOperations.ZeroMemory(bytes); }
        }
        try { _ = FoundationStore.CanonicalJson(JsonSerializer.Serialize(new { s.KeyId }, Json)); }
        catch (FoundationStoreException) { throw Failure("signature-invalid"); }
    }
    static void VerifySignature(CredentialScopedRecoverySetSignature signature, RSA suppliedKey, TimeProvider clock)
    {
        try
        {
            using var key = FoundationBackupSignatures.PublicCopy(suppliedKey);
            if (FoundationAuditAnchors.PublicKeyFingerprint(key) != signature.PublicKeySha256) throw Failure("signer-mismatch");
            var bytes = Convert.FromBase64String(signature.Signature);
            try
            {
                if (bytes.Length != key.KeySize / 8 || !key.VerifyData(CanonicalPayload(signature), bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) throw Failure("signature-invalid");
            }
            finally { CryptographicOperations.ZeroMemory(bytes); }
        }
        catch (CredentialException) { throw; }
        catch (Exception error) when (error is CryptographicException or ArgumentException or ObjectDisposedException or NotSupportedException) { throw Failure("signature-invalid"); }
        ValidateLifetime(signature, clock);
    }
}
