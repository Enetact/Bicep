using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SelfService.Foundation;

public sealed class CredentialException(string code) : Exception("Customer credential operation failed.")
{
    public string Code { get; } = code;
}

public sealed record CredentialScope(string CustomerId, string CellId, string ProjectId, string EnvironmentId,
    string Provider, string ConnectionId, long ConnectionGeneration, string ConnectionDigest,
    string AuthProfileId, string Audience, string[] RequiredScopes);
public sealed record CredentialProfile(CredentialScope Scope, string Kind, DateTimeOffset ExpiresUtc,
    long Generation, bool Enabled);
public sealed record CredentialVaultOptions(string RootPath, string CustomerId, string CellId, string EncryptionKeyId);

// Key custody belongs to the customer host/KMS. There is deliberately no plaintext file/default key implementation.
public interface ICredentialKeyProvider
{
    ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken cancellationToken);
}

[JsonConverter(typeof(CredentialSecretJsonConverter))]
public sealed class CredentialSecret : IDisposable
{
    byte[]? value;
    CredentialSecret(byte[] value) => this.value = value;
    public static CredentialSecret FromUtf8(string value)
    {
        if (string.IsNullOrEmpty(value) || Encoding.UTF8.GetByteCount(value) > 32768) throw new CredentialException("credential-material-invalid");
        return new(Encoding.UTF8.GetBytes(value));
    }
    internal byte[] CopyBytes() => value?.ToArray() ?? throw new CredentialException("credential-disposed");
    internal string Text() => value is null ? throw new CredentialException("credential-disposed") : Encoding.UTF8.GetString(value);
    public override string ToString() => "[REDACTED CREDENTIAL]";
    public void Dispose() { if (value is { } bytes) CryptographicOperations.ZeroMemory(bytes); value = null; }
}

public sealed class CredentialSecretJsonConverter : JsonConverter<CredentialSecret>
{
    public override CredentialSecret Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new JsonException("Credential material cannot be deserialized.");
    public override void Write(Utf8JsonWriter writer, CredentialSecret value, JsonSerializerOptions options) => throw new JsonException("Credential material cannot be serialized.");
}

[JsonConverter(typeof(CredentialEncryptionKeyJsonConverter))]
public sealed class CredentialEncryptionKey : IDisposable
{
    byte[]? value;
    public CredentialEncryptionKey(ReadOnlySpan<byte> value)
    {
        if (value.Length != 32) throw new CredentialException("credential-key-invalid");
        this.value = value.ToArray();
    }
    internal byte[] CopyBytes() => value?.ToArray() ?? throw new CredentialException("credential-key-disposed");
    public override string ToString() => "[REDACTED ENCRYPTION KEY]";
    public void Dispose() { if (value is { } bytes) CryptographicOperations.ZeroMemory(bytes); value = null; }
}

public sealed class CredentialEncryptionKeyJsonConverter : JsonConverter<CredentialEncryptionKey>
{
    public override CredentialEncryptionKey Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new JsonException("Encryption keys cannot be deserialized.");
    public override void Write(Utf8JsonWriter writer, CredentialEncryptionKey value, JsonSerializerOptions options) => throw new JsonException("Encryption keys cannot be serialized.");
}

[JsonConverter(typeof(CredentialLeaseJsonConverter))]
public sealed class CredentialLease : IDisposable
{
    readonly CredentialSecret material;
    internal CredentialLease(CredentialProfile profile, CredentialSecret material) { Profile = profile; this.material = material; }
    public CredentialProfile Profile { get; }
    internal string AccessToken() => material.Text();
    public override string ToString() => "[REDACTED CREDENTIAL LEASE]";
    public void Dispose() => material.Dispose();
}

public sealed class CredentialLeaseJsonConverter : JsonConverter<CredentialLease>
{
    public override CredentialLease Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new JsonException("Credential leases cannot be deserialized.");
    public override void Write(Utf8JsonWriter writer, CredentialLease value, JsonSerializerOptions options) => throw new JsonException("Credential leases cannot be serialized.");
}

internal static class CredentialRules
{
    internal const string AdoAudience = "499b84ac-1321-427f-aa17-267ca6975798";
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    { MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow, RespectNullableAnnotations = true, RespectRequiredConstructorParameters = true };
    internal static CredentialScope Snapshot(CredentialScope scope)
    {
        if (scope is null || !Guid.TryParseExact(scope.CustomerId, "D", out var customer) || customer == Guid.Empty ||
            !Guid.TryParseExact(scope.CellId, "D", out var cell) || cell == Guid.Empty ||
            !CustomerContextResolver.ValidId(scope.ProjectId) || !CustomerContextResolver.ValidId(scope.EnvironmentId) ||
            !CustomerContextResolver.ValidId(scope.ConnectionId) || scope.ConnectionGeneration <= 0 ||
            !Regex.IsMatch(scope.ConnectionDigest ?? "", "\\A[a-f0-9]{64}\\z") || !ProfileId(scope.AuthProfileId) ||
            scope.RequiredScopes is null || scope.RequiredScopes.Length is < 1 or > 16 ||
            scope.RequiredScopes.Any(s => string.IsNullOrEmpty(s) || s.Length > 128 || s.Any(char.IsWhiteSpace)) ||
            scope.RequiredScopes.Distinct(StringComparer.Ordinal).Count() != scope.RequiredScopes.Length)
            throw new CredentialException("credential-scope-invalid");
        var valid = scope.Provider switch
        {
            PipelineRequestFactory.GitHub => scope.Audience == "https://api.github.com" && scope.RequiredScopes.Contains("actions:write", StringComparer.Ordinal) &&
                scope.RequiredScopes.All(s => s is "actions:write" or "contents:read"),
            PipelineRequestFactory.AzureDevOps => scope.Audience == AdoAudience && scope.RequiredScopes.SequenceEqual(new[] { AdoAudience + "/.default" }),
            _ => false
        };
        if (!valid) throw new CredentialException("credential-audience-or-scope-invalid");
        return scope with { RequiredScopes = scope.RequiredScopes.Order(StringComparer.Ordinal).ToArray() };
    }
    internal static bool ProfileId(string? value) => value is not null && Regex.IsMatch(value, "\\A[A-Za-z0-9][A-Za-z0-9:/._-]{0,239}\\z") && !value.Contains("..", StringComparison.Ordinal);
    internal static string ScopeDigest(CredentialScope scope) => Hash(JsonSerializer.Serialize(Snapshot(scope), Json));
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static bool SameIdentity(CredentialScope a, CredentialScope b) => a.CustomerId == b.CustomerId && a.CellId == b.CellId &&
        a.ProjectId == b.ProjectId && a.EnvironmentId == b.EnvironmentId && a.Provider == b.Provider && a.ConnectionId == b.ConnectionId &&
        a.AuthProfileId == b.AuthProfileId && a.Audience == b.Audience && a.RequiredScopes.SequenceEqual(b.RequiredScopes);
    internal static void Token(string value)
    {
        if (string.IsNullOrEmpty(value) || value.Length > 16384 || value.Any(c => !char.IsAsciiLetterOrDigit(c) && !"._~+/-=".Contains(c)))
            throw new CredentialException("credential-token-invalid");
    }
    internal static void RejectLinks(string path)
    {
        for (var candidate = Path.GetFullPath(path); candidate is not null; candidate = Path.GetDirectoryName(candidate))
            if (Path.Exists(candidate) && (File.GetAttributes(candidate) & FileAttributes.ReparsePoint) != 0)
                throw new CredentialException("credential-linked-path");
    }
    internal static JsonDocument Document(ReadOnlyMemory<byte> bytes)
    {
        var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
        try { Unique(doc.RootElement); return doc; } catch { doc.Dispose(); throw; }
    }
    static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var p in value.EnumerateObject()) { if (!seen.Add(p.Name)) throw new JsonException(); Unique(p.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) Unique(child);
    }
}
