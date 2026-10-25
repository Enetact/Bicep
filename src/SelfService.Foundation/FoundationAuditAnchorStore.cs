using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

public sealed record FoundationAuditAnchor(int Version, string Kind, string AnchorId, string CustomerId,
    string CellId, string ProjectId, string EnvironmentId, string CreatedBy, long SignedByRoleVersion,
    long ThroughSequence, string PrefixHash, DateTimeOffset CreatedUtc, string KeyId,
    string PublicKeySha256, string Algorithm, string Signature);

public sealed partial class FoundationStore
{
    // Trusted cell-owner composition supplies the signing handle. There is deliberately no private-key HTTP/model API.
    public FoundationAuditAnchor CreateAuditAnchor(string customerId, string auditorKey, RSA signingKey,
        string keyId, string projectId = "default", string environmentId = "dev")
    {
        Scope(customerId); ValidateId(auditorKey); ValidateId(projectId); ValidateId(environmentId); RequireHuman(auditorKey);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: false);
        var roleVersion = Require(db, tx, auditorKey, projectId, environmentId, ["Auditor"]);
        var rows = AnchorRows(db, tx);
        if (!AnchorChainValid(rows) || rows.Count == 0) throw new FoundationStoreException("AuditAnchorIntegrityFailed");
        var last = rows[^1];
        FoundationAuditAnchor anchor;
        try
        {
            var unsigned = new FoundationAuditAnchor(1, "audit-prefix", NewId(), CustomerId, CellId, projectId,
                environmentId, auditorKey, roleVersion, last.Sequence, last.EventHash, At(Now), keyId,
                FoundationAuditAnchors.PublicKeyFingerprint(signingKey), FoundationAuditAnchors.Algorithm, "");
            if (!FoundationAuditAnchors.ShapeValid(unsigned, At(Now), allowUnsigned: true))
                throw new ArgumentException();
            var signature = signingKey.SignData(FoundationAuditAnchors.Payload(unsigned), HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
            anchor = unsigned with { Signature = Convert.ToBase64String(signature) };
        }
        catch (Exception e) when (e is ArgumentException or CryptographicException or ObjectDisposedException or NotSupportedException)
        { throw new FoundationStoreException("AuditAnchorSigningKeyInvalid", 400); }
        Audit(db, tx, auditorKey, projectId, environmentId, "AuditAnchorCreated", anchor.AnchorId, "Succeeded",
            "IndependentSignatureCreated", anchor.AnchorId);
        tx.Commit(); return anchor;
    }

    public bool VerifyAuditAnchor(string customerId, string auditorKey, FoundationAuditAnchor anchor,
        RSA pinnedPublicKey, string expectedKeyId, string projectId = "default", string environmentId = "dev",
        TimeSpan? maximumAge = null)
    {
        Scope(customerId); ValidateId(auditorKey); ValidateId(projectId); ValidateId(environmentId);
        using var db = Open(); using var tx = db.BeginTransaction(deferred: true);
        Require(db, tx, auditorKey, projectId, environmentId, ["Auditor"]);
        if (anchor is null || anchor.CustomerId != CustomerId || anchor.CellId != CellId ||
            anchor.ProjectId != projectId || anchor.EnvironmentId != environmentId ||
            !FoundationAuditAnchors.VerifySignature(anchor, pinnedPublicKey, expectedKeyId, At(Now), maximumAge)) return false;
        var rows = AnchorRows(db, tx);
        if (!AnchorChainValid(rows) || anchor.ThroughSequence > rows.Count) return false;
        return rows[(int)anchor.ThroughSequence - 1].EventHash == anchor.PrefixHash;
    }

    static IReadOnlyList<FoundationAuditEvent> AnchorRows(SqliteConnection db, SqliteTransaction tx)
    {
        var rows = Rows(db, tx, """
            SELECT sequence,customer_id,cell_id,event_id,actor_key,project_id,environment_id,action,target_id,outcome,reason,
              correlation,created,previous_hash,event_hash FROM audit ORDER BY sequence LIMIT 100001
            """, AuditRow);
        if (rows.Count > 100000) throw new FoundationStoreException("AuditVerificationLimit");
        return rows;
    }
    bool AnchorChainValid(IReadOnlyList<FoundationAuditEvent> rows)
    {
        var previous = ""; long sequence = 0;
        foreach (var row in rows)
        {
            if (row.CustomerId != CustomerId || row.CellId != CellId || row.Sequence != ++sequence || row.PreviousHash != previous ||
                row.EventHash != AuditHash(row.Sequence, row.CustomerId, row.CellId, row.EventId, row.ActorKey, row.ProjectId,
                    row.EnvironmentId, row.Action, row.TargetId, row.Outcome, row.Reason, row.CorrelationId,
                    row.CreatedAt.ToUnixTimeMilliseconds(), row.PreviousHash)) return false;
            previous = row.EventHash;
        }
        return true;
    }
}

public static class FoundationAuditAnchors
{
    public const string Algorithm = "RSA-PSS-SHA256";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 4, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    };

    public static string PublicKeyFingerprint(RSA key)
    {
        using var copy = PublicCopy(key);
        return Convert.ToHexString(SHA256.HashData(copy.ExportSubjectPublicKeyInfo())).ToLowerInvariant();
    }

    public static bool VerifySignature(FoundationAuditAnchor anchor, RSA pinnedPublicKey, string expectedKeyId,
        DateTimeOffset now, TimeSpan? maximumAge = null)
    {
        try
        {
            if (maximumAge is { } age && age <= TimeSpan.Zero || !ShapeValid(anchor, now) || anchor.KeyId != expectedKeyId ||
                maximumAge is { } limit && now - anchor.CreatedUtc > limit) return false;
            using var key = PublicCopy(pinnedPublicKey);
            if (anchor.PublicKeySha256 != PublicKeyFingerprint(key)) return false;
            var bytes = Convert.FromBase64String(anchor.Signature);
            if (bytes.Length != key.KeySize / 8 || Convert.ToBase64String(bytes) != anchor.Signature) return false;
            return key.VerifyData(Payload(anchor), bytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss);
        }
        catch (Exception e) when (e is ArgumentException or FormatException or CryptographicException or ObjectDisposedException or NullReferenceException or NotSupportedException)
        { return false; }
    }

    internal static bool ShapeValid(FoundationAuditAnchor? anchor, DateTimeOffset now, bool allowUnsigned = false) =>
        anchor is not null && anchor.Version == 1 && anchor.Kind == "audit-prefix" &&
        Guid.TryParseExact(anchor.AnchorId, "N", out var id) && id != Guid.Empty && id.ToString("N") == anchor.AnchorId &&
        CustomerContextResolver.ValidId(anchor.CustomerId) && CustomerContextResolver.ValidId(anchor.CellId) &&
        CustomerContextResolver.ValidId(anchor.ProjectId) && CustomerContextResolver.ValidId(anchor.EnvironmentId) &&
        anchor.CreatedBy is not null && Regex.IsMatch(anchor.CreatedBy, "\\A[A-Za-z0-9][A-Za-z0-9._:-]{0,255}\\z") &&
        anchor.SignedByRoleVersion > 0 && anchor.ThroughSequence is > 0 and <= 100000 &&
        anchor.PrefixHash is not null && Regex.IsMatch(anchor.PrefixHash, "\\A[a-f0-9]{64}\\z") &&
        anchor.PublicKeySha256 is not null && Regex.IsMatch(anchor.PublicKeySha256, "\\A[a-f0-9]{64}\\z") &&
        anchor.CreatedUtc.Offset == TimeSpan.Zero && anchor.CreatedUtc.UtcTicks % TimeSpan.TicksPerMillisecond == 0 &&
        anchor.CreatedUtc <= now.AddMinutes(5) && anchor.KeyId is not null &&
        Regex.IsMatch(anchor.KeyId, "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z") && anchor.Algorithm == Algorithm &&
        (allowUnsigned && anchor.Signature == "" || anchor.Signature is not null && anchor.Signature.Length is >= 344 and <= 1368 &&
            Regex.IsMatch(anchor.Signature, "\\A[A-Za-z0-9+/]+={0,2}\\z"));

    internal static byte[] Payload(FoundationAuditAnchor anchor) => JsonSerializer.SerializeToUtf8Bytes(new
    {
        anchor.Version, anchor.Kind, anchor.AnchorId, anchor.CustomerId, anchor.CellId, anchor.ProjectId,
        anchor.EnvironmentId, anchor.CreatedBy, anchor.SignedByRoleVersion, anchor.ThroughSequence,
        anchor.PrefixHash, createdUtcMilliseconds = anchor.CreatedUtc.ToUnixTimeMilliseconds(),
        anchor.KeyId, anchor.PublicKeySha256, anchor.Algorithm
    }, Json);

    static RSA PublicCopy(RSA source)
    {
        ArgumentNullException.ThrowIfNull(source);
        var p = source.ExportParameters(false);
        if (p.Modulus is null || p.Modulus.Length is < 256 or > 1024 || p.Modulus[0] == 0 ||
            p.Modulus.Length == 256 && p.Modulus[0] < 128 || (p.Modulus[^1] & 1) == 0 ||
            p.Exponent is null || p.Exponent.Length is < 1 or > 8 || (p.Exponent[^1] & 1) == 0 ||
            p.Exponent.Take(p.Exponent.Length - 1).All(b => b == 0) && p.Exponent[^1] < 3)
            throw new ArgumentException("A valid 2048-8192 bit RSA public key is required.");
        var key = RSA.Create();
        try { key.ImportParameters(new RSAParameters { Modulus = p.Modulus.ToArray(), Exponent = p.Exponent.ToArray() }); return key; }
        catch { key.Dispose(); throw; }
    }
}

/// <summary>
/// Append-only by this interface, not WORM storage. Keep the signing key and latest trusted anchor
/// under independent custody; an administrator who controls both can replace the evidence.
/// </summary>
public sealed class FoundationAuditAnchorFiles
{
    readonly string root, customerId, cellId;
    readonly TimeProvider clock;
    public FoundationAuditAnchorFiles(string trustedRoot, string customerId, string cellId, TimeProvider? clock = null)
    {
        if (!Path.IsPathFullyQualified(trustedRoot) || !CustomerContextResolver.ValidId(customerId) || !CustomerContextResolver.ValidId(cellId))
            throw new ArgumentException("An explicit trusted cell anchor directory is required.");
        root = Path.GetFullPath(trustedRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
        if (root.Length <= (Path.GetPathRoot(root)?.Length ?? 0)) throw new ArgumentException("An anchor subdirectory is required.");
        this.customerId = customerId; this.cellId = cellId; this.clock = clock ?? TimeProvider.System;
        NoLinks(root); Directory.CreateDirectory(root); NoLinks(root);
    }
    public string WriteNew(FoundationAuditAnchor anchor, RSA pinnedPublicKey, string expectedKeyId)
    {
        Bound(anchor, pinnedPublicKey, expectedKeyId);
        var destination = AnchorPath(anchor.AnchorId); var temporary = Path.Combine(root, ".pending-" + Guid.NewGuid().ToString("N"));
        var bytes = JsonSerializer.SerializeToUtf8Bytes(anchor, FoundationAuditAnchors.Json);
        try
        {
            NoLinks(root); NoLinks(destination); NoLinks(temporary);
            using (var file = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { file.Write(bytes); file.Flush(flushToDisk: true); }
            NoLinks(root); NoLinks(destination);
            File.Move(temporary, destination, overwrite: false); return destination;
        }
        finally { if (File.Exists(temporary)) { NoLinks(temporary); File.Delete(temporary); } }
    }
    public FoundationAuditAnchor ReadVerified(string anchorId, RSA pinnedPublicKey, string expectedKeyId)
    {
        var path = AnchorPath(anchorId); NoLinks(root); NoLinks(path);
        using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (file.Length is 0 or > 32768) throw new FoundationStoreException("AuditAnchorFileInvalid", 400);
        var bytes = new byte[(int)file.Length]; file.ReadExactly(bytes);
        try
        {
            using var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 4 });
            if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new JsonException();
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in doc.RootElement.EnumerateObject()) if (!names.Add(property.Name)) throw new JsonException();
            var anchor = doc.RootElement.Deserialize<FoundationAuditAnchor>(FoundationAuditAnchors.Json) ?? throw new JsonException();
            Bound(anchor, pinnedPublicKey, expectedKeyId); return anchor;
        }
        catch (Exception e) when (e is JsonException or ArgumentException or InvalidOperationException)
        { throw new FoundationStoreException("AuditAnchorFileInvalid", 400); }
    }
    string AnchorPath(string anchorId)
    {
        if (!Guid.TryParseExact(anchorId, "N", out var id) || id == Guid.Empty || id.ToString("N") != anchorId)
            throw new FoundationStoreException("AuditAnchorIdInvalid", 400);
        return Path.Combine(root, anchorId + ".anchor.json");
    }
    void Bound(FoundationAuditAnchor anchor, RSA key, string expectedKeyId)
    {
        if (anchor is null || anchor.CustomerId != customerId || anchor.CellId != cellId ||
            !FoundationAuditAnchors.VerifySignature(anchor, key, expectedKeyId, clock.GetUtcNow()))
            throw new FoundationStoreException("AuditAnchorSignatureInvalid", 403);
    }
    static void NoLinks(string value)
    {
        string? path = Path.GetFullPath(value);
        while (path is not null)
        {
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw new FoundationStoreException("AuditAnchorLinkedPathDenied", 400);
            path = Path.GetDirectoryName(path);
        }
    }
}
