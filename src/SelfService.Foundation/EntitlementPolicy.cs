using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SelfService.Foundation;

/// <summary>Technical feature availability only. This policy never grants an identity, role, or external effect.</summary>
public static class FoundationFeatures
{
    public const string SourceCatalog = "source.catalog";
    public const string ParameterizedBicep = "source.bicep";
    public const string ModelReview = "model.review";
    public const string PipelinePlan = "pipeline.plan";
    public const string PipelineMockDispatch = "pipeline.mock-dispatch";
    public const string Administration = "administration.safe";
    public const string AuditRead = "audit.read";
    public const string ExportRead = "export.read";
    public static IReadOnlySet<string> Available { get; } = new[]
        { SourceCatalog, ParameterizedBicep, ModelReview, PipelinePlan, PipelineMockDispatch }.ToFrozenSet(StringComparer.Ordinal);
    public static IReadOnlySet<string> AlwaysAvailable { get; } = new[]
        { Administration, AuditRead, ExportRead }.ToFrozenSet(StringComparer.Ordinal);
    public static bool IsKnown(string feature) => feature is not null && (Available.Contains(feature) || AlwaysAvailable.Contains(feature));
}

public sealed record EntitlementDocument(string Schema, string Issuer, string LicenseId, string CustomerId, string CellId,
    long Sequence, string Edition, string[] Features, DateTimeOffset NotBefore, DateTimeOffset ExpiresUtc, string KeyId);
public sealed record SignedEntitlement(EntitlementDocument Document, string Signature);
public sealed record EntitlementTrustKey(string Issuer, string KeyId, string SubjectPublicKeyInfoPem);

/// <summary>Verified, detached claims. The feature set and trust store cannot be mutated by a caller.</summary>
public sealed class ValidatedEntitlement
{
    internal ValidatedEntitlement(EntitlementDocument document, string digest)
    {
        Issuer = document.Issuer; LicenseId = document.LicenseId; CustomerId = document.CustomerId; CellId = document.CellId;
        Sequence = document.Sequence; Edition = document.Edition; NotBefore = document.NotBefore; ExpiresUtc = document.ExpiresUtc;
        KeyId = document.KeyId; Features = document.Features.ToFrozenSet(StringComparer.Ordinal); Digest = digest;
    }
    public string Issuer { get; }
    public string LicenseId { get; }
    public string CustomerId { get; }
    public string CellId { get; }
    public long Sequence { get; }
    public string Edition { get; }
    public IReadOnlySet<string> Features { get; }
    public DateTimeOffset NotBefore { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public string KeyId { get; }
    public string Digest { get; }
    public bool ExternalExecutionAuthorized => false;
    public bool IdentityGrantAuthorized => false;
}

/// <summary>Offline RSA-PSS/SHA-256 verification against explicitly configured issuer public keys. No trust on first use.</summary>
public sealed class EntitlementVerifier
{
    public const string DocumentSchema = "platform-studio.entitlement.v1";
    readonly FrozenDictionary<(string Issuer, string KeyId), byte[]> publicKeys;
    readonly TimeProvider clock;
    static readonly Regex Id = new("\\A[A-Za-z0-9][A-Za-z0-9._:-]{0,127}\\z", RegexOptions.CultureInvariant);
    static readonly JsonSerializerOptions StrictJson = new(JsonSerializerDefaults.Web)
    {
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true,
        PropertyNameCaseInsensitive = false,
        MaxDepth = 12
    };

    public bool RequireSignedMode { get; }

    public EntitlementVerifier(IEnumerable<EntitlementTrustKey> trustedKeys, TimeProvider? timeProvider = null, bool requireSignedMode = false)
    {
        ArgumentNullException.ThrowIfNull(trustedKeys);
        clock = timeProvider ?? TimeProvider.System;
        RequireSignedMode = requireSignedMode;
        var keys = new Dictionary<(string, string), byte[]>();
        foreach (var key in trustedKeys)
        {
            if (key is null || !ValidId(key.Issuer) || !ValidId(key.KeyId) || string.IsNullOrEmpty(key.SubjectPublicKeyInfoPem) ||
                key.SubjectPublicKeyInfoPem.Length > 16384 || key.SubjectPublicKeyInfoPem.Contains("PRIVATE", StringComparison.Ordinal) ||
                !key.SubjectPublicKeyInfoPem.TrimStart().StartsWith("-----BEGIN PUBLIC KEY-----", StringComparison.Ordinal) || keys.Count >= 32)
                throw new FoundationStoreException("InvalidEntitlementTrustKey", 400);
            try
            {
                using var rsa = RSA.Create(); rsa.ImportFromPem(key.SubjectPublicKeyInfoPem);
                if (rsa.KeySize is < 2048 or > 8192 || !keys.TryAdd((key.Issuer, key.KeyId), rsa.ExportSubjectPublicKeyInfo()))
                    throw new FoundationStoreException("InvalidEntitlementTrustKey", 400);
            }
            catch (Exception ex) when (ex is CryptographicException or ArgumentException)
            { throw new FoundationStoreException("InvalidEntitlementTrustKey", 400); }
        }
        publicKeys = keys.ToFrozenDictionary();
    }

    public ValidatedEntitlement Verify(SignedEntitlement envelope, string customerId, string cellId)
    {
        var verified = VerifySignature(envelope, customerId, cellId);
        var now = clock.GetUtcNow();
        if (now < verified.NotBefore) throw new FoundationStoreException("EntitlementNotYetValid", 403);
        if (now >= verified.ExpiresUtc) throw new FoundationStoreException("EntitlementExpired", 403);
        return verified;
    }

    // Historical signatures may be inspected after expiry; feature decisions must use Verify instead.
    internal ValidatedEntitlement VerifySignature(SignedEntitlement envelope, string customerId, string cellId)
    {
        if (envelope?.Document is null) throw new FoundationStoreException("InvalidEntitlement", 400);
        var document = Snapshot(envelope.Document);
        if (!string.Equals(document.CustomerId, customerId, StringComparison.Ordinal) ||
            !string.Equals(document.CellId, cellId, StringComparison.Ordinal))
            throw new FoundationStoreException("EntitlementBindingMismatch", 403);
        if (!publicKeys.TryGetValue((document.Issuer, document.KeyId), out var key))
            throw new FoundationStoreException("EntitlementIssuerNotTrusted", 403);
        if (envelope.Signature is null || envelope.Signature.Length is < 300 or > 1400)
            throw new FoundationStoreException("EntitlementSignatureInvalid", 403);
        try
        {
            var signature = Convert.FromBase64String(envelope.Signature);
            if (Convert.ToBase64String(signature) != envelope.Signature) throw new FormatException();
            using var rsa = RSA.Create(); rsa.ImportSubjectPublicKeyInfo(key, out _);
            if (!rsa.VerifyData(CanonicalDocument(document), signature, HashAlgorithmName.SHA256, RSASignaturePadding.Pss))
                throw new FoundationStoreException("EntitlementSignatureInvalid", 403);
        }
        catch (Exception ex) when (ex is FormatException or CryptographicException)
        { throw new FoundationStoreException("EntitlementSignatureInvalid", 403); }
        return new(document, Convert.ToHexStringLower(SHA256.HashData(CanonicalDocument(document))));
    }

    public static SignedEntitlement Parse(string json)
    {
        if (json is null || Encoding.UTF8.GetByteCount(json) > 16384) throw new FoundationStoreException("InvalidEntitlement", 400);
        try
        {
            using var document = JsonDocument.Parse(json, new JsonDocumentOptions { MaxDepth = 12 });
            RejectDuplicateProperties(document.RootElement);
            var result = JsonSerializer.Deserialize<SignedEntitlement>(json, StrictJson);
            if (result?.Document is null) throw new JsonException();
            return result with { Document = Snapshot(result.Document) };
        }
        catch (JsonException) { throw new FoundationStoreException("InvalidEntitlement", 400); }
    }

    /// <summary>Stable bytes for the signature: fixed field order, sorted feature IDs, UTC Unix millisecond timestamps.</summary>
    public static byte[] CanonicalDocument(EntitlementDocument document)
    {
        var value = Snapshot(document);
        return JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = value.Schema, issuer = value.Issuer, licenseId = value.LicenseId, customerId = value.CustomerId,
            cellId = value.CellId, sequence = value.Sequence, edition = value.Edition, features = value.Features,
            notBefore = value.NotBefore.ToUnixTimeMilliseconds(), expiresUtc = value.ExpiresUtc.ToUnixTimeMilliseconds(), keyId = value.KeyId
        });
    }

    internal static EntitlementDocument Snapshot(EntitlementDocument document)
    {
        if (document is null || document.Schema != DocumentSchema || !ValidId(document.Issuer) || !ValidId(document.LicenseId) ||
            !ValidId(document.CustomerId) || !ValidId(document.CellId) || !ValidId(document.Edition) || !ValidId(document.KeyId) ||
            document.Sequence is < 1 or > 9007199254740991 || document.Features is null || document.Features.Length is < 1 or > 32 ||
            document.NotBefore.Offset != TimeSpan.Zero || document.ExpiresUtc.Offset != TimeSpan.Zero ||
            document.NotBefore.Ticks % TimeSpan.TicksPerMillisecond != 0 || document.ExpiresUtc.Ticks % TimeSpan.TicksPerMillisecond != 0 ||
            document.ExpiresUtc <= document.NotBefore)
            throw new FoundationStoreException("InvalidEntitlement", 400);
        var features = document.Features.ToArray();
        if (features.Any(feature => !FoundationFeatures.Available.Contains(feature)) || features.Distinct(StringComparer.OrdinalIgnoreCase).Count() != features.Length)
            throw new FoundationStoreException("InvalidEntitlementFeatures", 400);
        Array.Sort(features, StringComparer.Ordinal);
        return document with { Features = features };
    }

    static bool ValidId(string? value) => value is not null && Id.IsMatch(value);
    static void RejectDuplicateProperties(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var properties = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            { if (!properties.Add(property.Name)) throw new JsonException(); RejectDuplicateProperties(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var item in value.EnumerateArray()) RejectDuplicateProperties(item);
    }
}

public sealed record FoundationFeatureConfiguration(string Mode, string[] EnabledFeatures, int RolloutPercent,
    string MinimumRuntimeVersion, string MaximumRuntimeVersion, SignedEntitlement? Entitlement = null);
public sealed record FoundationConfigurationSnapshot(string CustomerId, string CellId, string ProjectId, string EnvironmentId,
    long Generation, string Digest, string? AdministrativePlanId, FoundationFeatureConfiguration Configuration);
public sealed record FoundationFeatureDecision(string Feature, bool Enabled, string Reason, long Generation,
    string ConfigurationDigest, string Mode)
{
    public bool ExternalExecutionAuthorized => false;
    public bool IdentityGrantAuthorized => false;
}
