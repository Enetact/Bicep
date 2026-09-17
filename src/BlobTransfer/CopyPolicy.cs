using System.Security.Cryptography;
using System.Text;

namespace BlobTransfer;

public sealed record CopyLimits(long MaxBytes);

public static class CopyPolicy
{
    // Names and full URLs never appear in application logs. Metadata stores only this fingerprint.
    public static string Fingerprint(Uri source, string etag) =>
        Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(source.AbsoluteUri + "\n" + etag))).ToLowerInvariant();

    public static void ValidateLength(long size, long maximum)
    {
        if (maximum <= 0 || size < 0 || size > maximum)
            throw new InvalidOperationException("Copy rejected: invalid or excessive content length.");
    }

    public static bool IsCompletedDuplicate(IDictionary<string, string> metadata, string fingerprint, long existingLength, long sourceLength) =>
        metadata.TryGetValue("sourcefingerprint", out var saved) &&
        string.Equals(saved, fingerprint, StringComparison.Ordinal) && existingLength == sourceLength;
}
