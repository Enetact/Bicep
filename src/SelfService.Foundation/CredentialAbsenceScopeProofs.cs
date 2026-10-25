using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SelfService.Foundation;

// Private scope metadata, not a public report, bearer credential, or authority to retry a write.
internal sealed record CredentialAbsenceScopeProof(int Version, string Kind, string CustomerId, string CellId,
    string VaultNamespaceId, string VaultNamespaceCommitmentSha256, string AuthorityNamespaceId, string AuthorityNamespaceCommitmentSha256,
    string BindingSha256, string ProfileIdSha256, CredentialScope Scope, string ScopeSha256, string KeyId,
    string FirstTransactionId, string FirstCandidateSha256, long FirstCandidateLength, long FirstGeneration, string Algorithm, string Tag, string Sha256);

internal static class CredentialAbsenceScopeProofs
{
    const string Kind = "credential-absence-scope-proof", Algorithm = "HKDF-SHA256/HMAC-SHA256";
    static readonly byte[] Salt = Encoding.UTF8.GetBytes("platform-studio.credential-absence-scope-proof.v2.hkdf-salt");
    static readonly byte[] Info = Encoding.UTF8.GetBytes("platform-studio.credential-absence-scope-proof.v2.mac-key");

    internal static CredentialAbsenceScopeProof Create(string customerId, string cellId, string vaultId, string vaultCommitment,
        string authorityId, string authorityCommitment, string bindingSha256, string profileIdSha256, CredentialScope scope, string keyId,
        string transactionId, byte[] candidate, ReadOnlySpan<byte> frozenKey)
    {
        scope = CredentialRules.Snapshot(scope);
        var unsigned = new CredentialAbsenceScopeProof(2, Kind, customerId, cellId, vaultId, vaultCommitment, authorityId, authorityCommitment,
            bindingSha256, profileIdSha256, scope, CredentialRules.ScopeDigest(scope), keyId, transactionId,
            CredentialRecoverySets.Hash(candidate), candidate.Length, 1, Algorithm, "", "");
        Validate(unsigned, unsignedProof: true);
        var mac = Mac(unsigned, frozenKey);
        try
        {
            var tagged = unsigned with { Tag = Convert.ToHexStringLower(mac) };
            return tagged with { Sha256 = Digest(tagged) };
        }
        finally { CryptographicOperations.ZeroMemory(mac); }
    }

    internal static void Authenticate(CredentialAbsenceScopeProof proof, ReadOnlySpan<byte> frozenKey)
    {
        Validate(proof); byte[]? expected = null, actual = null;
        try
        {
            actual = Mac(proof, frozenKey); expected = Convert.FromHexString(proof.Tag);
            if (!CryptographicOperations.FixedTimeEquals(actual, expected)) throw Failure("authentication-failed");
        }
        finally
        {
            if (expected is not null) CryptographicOperations.ZeroMemory(expected);
            if (actual is not null) CryptographicOperations.ZeroMemory(actual);
        }
    }
    internal static void Validate(CredentialAbsenceScopeProof proof, bool unsignedProof = false)
    {
        try
        {
            if (proof is null || proof.Version != 2 || proof.Kind != Kind || proof.Algorithm != Algorithm || proof.FirstGeneration != 1 ||
                !Guid.TryParseExact(proof.CustomerId, "D", out var customer) || customer == Guid.Empty || !Guid.TryParseExact(proof.CellId, "D", out var cell) || cell == Guid.Empty ||
                !CredentialRecoverySets.NamespaceShape(proof.VaultNamespaceId) || !CredentialRecoverySets.NamespaceShape(proof.AuthorityNamespaceId) ||
                proof.VaultNamespaceId == proof.AuthorityNamespaceId || !CredentialRecoverySets.NamespaceShape(proof.FirstTransactionId) ||
                !Hash(proof.VaultNamespaceCommitmentSha256) || !Hash(proof.AuthorityNamespaceCommitmentSha256) || !Hash(proof.BindingSha256) ||
                !Hash(proof.ProfileIdSha256) || !Hash(proof.ScopeSha256) || !Hash(proof.FirstCandidateSha256) || proof.FirstCandidateLength is < 1 or > 131072 ||
                !CredentialRules.ProfileId(proof.KeyId) || !unsignedProof && (!Hash(proof.Tag) || !Hash(proof.Sha256) || Digest(proof) != proof.Sha256))
                throw Failure("invalid");
            var scope = CredentialRules.Snapshot(proof.Scope);
            if (scope.CustomerId != proof.CustomerId || scope.CellId != proof.CellId || CredentialRules.Hash(scope.AuthProfileId) != proof.ProfileIdSha256 ||
                CredentialRules.ScopeDigest(scope) != proof.ScopeSha256 || JsonSerializer.Serialize(scope, CredentialRules.Json) != JsonSerializer.Serialize(proof.Scope, CredentialRules.Json))
                throw Failure("invalid");
            _ = FoundationStore.CanonicalJson(JsonSerializer.Serialize(new { scope.ProjectId, scope.EnvironmentId, proof.KeyId }, CredentialRules.Json));
        }
        catch (Exception error) when (error is ArgumentException or InvalidOperationException or NullReferenceException or FoundationStoreException)
        { throw Failure("invalid"); }
    }
    internal static string Digest(CredentialAbsenceScopeProof proof) => CredentialRecoverySets.Hash(JsonSerializer.SerializeToUtf8Bytes(new
    { payload = Payload(proof), proof.Tag }, CredentialRules.Json));
    static object Payload(CredentialAbsenceScopeProof p) => new
    {
        domain = "platform-studio.credential-absence-scope-proof.v2", p.Version, p.Kind, p.CustomerId, p.CellId,
        p.VaultNamespaceId, p.VaultNamespaceCommitmentSha256, p.AuthorityNamespaceId, p.AuthorityNamespaceCommitmentSha256,
        p.BindingSha256, p.ProfileIdSha256, p.Scope, p.ScopeSha256, p.KeyId, p.FirstTransactionId, p.FirstCandidateSha256, p.FirstCandidateLength, p.FirstGeneration, p.Algorithm
    };
    static byte[] Mac(CredentialAbsenceScopeProof proof, ReadOnlySpan<byte> frozenKey)
    {
        if (frozenKey.Length != 32) throw Failure("key-invalid");
        Span<byte> derived = stackalloc byte[32]; var payload = JsonSerializer.SerializeToUtf8Bytes(Payload(proof), CredentialRules.Json);
        try
        {
            HKDF.DeriveKey(HashAlgorithmName.SHA256, frozenKey, derived, Salt, Info);
            return HMACSHA256.HashData(derived, payload);
        }
        finally { CryptographicOperations.ZeroMemory(derived); CryptographicOperations.ZeroMemory(payload); }
    }
    static bool Hash(string value) => CredentialRecoverySets.DigestShape(value);
    static CredentialException Failure(string suffix) => new("credential-absence-proof-" + suffix);
}
