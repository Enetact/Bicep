using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.JsonWebTokens;
using Microsoft.IdentityModel.Tokens;

namespace SelfService.Foundation;

public sealed record ApprovedCallingClient(string TenantId, string ClientId, PrincipalKind Kind,
    string? ServicePrincipalObjectId = null);

public sealed record CellIdentityOptions(string CellId, string CustomerId, string Audience,
    IReadOnlyDictionary<string, string[]> TrustedIssuersByTenant,
    IReadOnlyList<ApprovedCallingClient> AllowedClients, IReadOnlyList<SecurityKey> SigningKeys,
    string RequiredDelegatedScope = "platform.access", string RequiredWorkloadRole = "Worker.Execute");

public sealed class FoundationPrincipal
{
    internal FoundationPrincipal(string tenantId, string subjectId, string clientId, PrincipalKind kind,
        IEnumerable<string> roles, DateTimeOffset expiresUtc, string validationCustomerId, string validationCellId)
    {
        TenantId = tenantId; SubjectId = subjectId; ClientId = clientId; Kind = kind;
        Roles = roles.ToFrozenSet(StringComparer.Ordinal); ExpiresUtc = expiresUtc;
        ValidationCustomerId = validationCustomerId; ValidationCellId = validationCellId;
        ActorKey = kind == PrincipalKind.User ? $"user:{tenantId}:{subjectId}" : $"workload:{tenantId}:{subjectId}:{clientId}";
    }
    public string TenantId { get; }
    public string SubjectId { get; }
    public string ClientId { get; }
    public PrincipalKind Kind { get; }
    public IReadOnlySet<string> Roles { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public string ActorKey { get; }
    public string ValidationCustomerId { get; }
    public string ValidationCellId { get; }
}

// Explicit trusted keys/issuers are supplied by the host; validation never discovers keys from a token URL.
public sealed class IdentityTokenValidator
{
    const int MaxTokenBytes = 16384;
    readonly string audience, delegatedScope, workloadRole, customerId, cellId;
    readonly FrozenDictionary<string, FrozenSet<string>> issuers;
    readonly ApprovedCallingClient[] clients;
    readonly SecurityKey[] keys;
    readonly TimeProvider clock;
    readonly JsonWebTokenHandler handler = new() { MapInboundClaims = false, MaximumTokenSizeInBytes = MaxTokenBytes };

    public IdentityTokenValidator(CellIdentityOptions options, TimeProvider? clock = null)
    {
        if (!CustomerContextResolver.ValidId(options.CustomerId) || !CustomerContextResolver.ValidId(options.CellId) ||
            string.IsNullOrWhiteSpace(options.Audience) || options.Audience.Length > 512 ||
            options.Audience.Any(char.IsWhiteSpace) || string.IsNullOrWhiteSpace(options.RequiredDelegatedScope) ||
            options.RequiredDelegatedScope.Any(char.IsWhiteSpace) || string.IsNullOrWhiteSpace(options.RequiredWorkloadRole))
            throw new ArgumentException("Invalid trusted identity configuration.");
        audience = options.Audience; delegatedScope = options.RequiredDelegatedScope; workloadRole = options.RequiredWorkloadRole;
        customerId = options.CustomerId; cellId = options.CellId;
        this.clock = clock ?? TimeProvider.System;
        try
        {
            if (options.TrustedIssuersByTenant.Count is 0 or > 32 || options.AllowedClients.Count is 0 or > 128 ||
                options.SigningKeys.Count is 0 or > 16) throw new ArgumentException();
            var entries = new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal);
            foreach (var entry in options.TrustedIssuersByTenant)
            {
                var tenant = GuidValue(entry.Key);
                if (entry.Value is null || entry.Value.Length is 0 or > 4 || entry.Value.Any(i =>
                    !Uri.TryCreate(i, UriKind.Absolute, out var u) || u.Scheme != "https" ||
                    !string.IsNullOrEmpty(u.UserInfo) || !string.IsNullOrEmpty(u.Query) || !string.IsNullOrEmpty(u.Fragment)))
                    throw new ArgumentException();
                entries.Add(tenant, entry.Value.ToFrozenSet(StringComparer.Ordinal));
            }
            issuers = entries.ToFrozenDictionary(StringComparer.Ordinal);
            clients = options.AllowedClients.Select(c => new ApprovedCallingClient(GuidValue(c.TenantId),
                GuidValue(c.ClientId), c.Kind, c.ServicePrincipalObjectId is null ? null : GuidValue(c.ServicePrincipalObjectId))).ToArray();
            if (clients.Any(c => !issuers.ContainsKey(c.TenantId) || !Enum.IsDefined(c.Kind) ||
                c.Kind == PrincipalKind.Workload && c.ServicePrincipalObjectId is null ||
                c.Kind == PrincipalKind.User && c.ServicePrincipalObjectId is not null) ||
                clients.Distinct().Count() != clients.Length) throw new ArgumentException();
            keys = options.SigningKeys.Select(CopyPublicRsaKey).ToArray();
            if (keys.Select(k => k.KeyId).Distinct(StringComparer.Ordinal).Count() != keys.Length) throw new ArgumentException();
        }
        catch (Exception e) when (e is ArgumentException or FormatException or CryptographicException or FoundationAuthorizationException or NullReferenceException)
        { throw new ArgumentException("Invalid trusted identity configuration."); }
    }

    public async Task<FoundationPrincipal> ValidateAsync(string token, CancellationToken ct = default)
    {
        ct.ThrowIfCancellationRequested();
        try
        {
            if (string.IsNullOrWhiteSpace(token) || Encoding.UTF8.GetByteCount(token) > MaxTokenBytes)
                throw Denied();
            var segments = token.Split('.');
            if (segments.Length != 3 || segments.Any(string.IsNullOrEmpty)) throw Denied();
            using var header = ParseObject(Base64UrlEncoder.DecodeBytes(segments[0]));
            using var payload = ParseObject(Base64UrlEncoder.DecodeBytes(segments[1]));
            var h = header.RootElement; var p = payload.RootElement;
            if (String(h, "alg") != SecurityAlgorithms.RsaSha256 || String(h, "typ") is not ("JWT" or "at+jwt") ||
                h.TryGetProperty("jku", out _) || h.TryGetProperty("x5u", out _) || h.TryGetProperty("crit", out _) || h.TryGetProperty("b64", out _)) throw Denied();
            var kid = String(h, "kid");
            if (kid.Length is 0 or > 128 || !keys.Any(k => k.KeyId == kid)) throw Denied();
            var tenant = GuidValue(String(p, "tid"));
            var subject = GuidValue(String(p, "oid"));
            var version = String(p, "ver");
            if (version is not ("1.0" or "2.0") || !issuers.TryGetValue(tenant, out var trusted) ||
                !trusted.Contains(String(p, "iss")) || String(p, "aud") != audience) throw Denied();
            var clientClaim = version == "2.0" ? "azp" : "appid";
            var otherClientClaim = version == "2.0" ? "appid" : "azp";
            if (p.TryGetProperty(otherClientClaim, out _) || p.TryGetProperty("act", out _)) throw Denied();
            var client = GuidValue(String(p, clientClaim));
            if (!p.TryGetProperty("exp", out var expires) || expires.ValueKind != JsonValueKind.Number || !expires.TryGetInt64(out var seconds) ||
                !p.TryGetProperty("nbf", out var begins) || begins.ValueKind != JsonValueKind.Number || !begins.TryGetInt64(out _)) throw Denied();
            var parameters = new TokenValidationParameters
            {
                RequireSignedTokens = true, RequireExpirationTime = true, RequireAudience = true,
                ValidateIssuer = true, ValidateAudience = true, ValidateIssuerSigningKey = true, ValidateLifetime = true,
                ValidIssuer = String(p, "iss"), ValidAudience = audience, IgnoreTrailingSlashWhenValidatingAudience = false,
                IssuerSigningKeys = keys.Where(k => k.KeyId == kid), TryAllIssuerSigningKeys = false,
                ValidAlgorithms = [SecurityAlgorithms.RsaSha256], ValidTypes = ["JWT", "at+jwt"], ClockSkew = TimeSpan.Zero,
                IncludeTokenOnFailedValidation = false, SaveSigninToken = false, LogTokenId = false,
                LifetimeValidator = (nbf, exp, _, _) => nbf.HasValue && exp.HasValue &&
                    new DateTimeOffset(nbf.Value.ToUniversalTime()) <= clock.GetUtcNow() &&
                    new DateTimeOffset(exp.Value.ToUniversalTime()) > clock.GetUtcNow() && exp > nbf
            };
            var result = await handler.ValidateTokenAsync(token, parameters);
            ct.ThrowIfCancellationRequested();
            if (!result.IsValid) throw Denied();
            var roles = Roles(p);
            PrincipalKind kind;
            if (p.TryGetProperty("scp", out var scope))
            {
                if (scope.ValueKind != JsonValueKind.String ||
                    !(scope.GetString() ?? "").Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(delegatedScope, StringComparer.Ordinal) ||
                    p.TryGetProperty("idtyp", out var userType) && (userType.ValueKind != JsonValueKind.String || userType.GetString() != "user")) throw Denied();
                kind = PrincipalKind.User;
            }
            else
            {
                if (String(p, "idtyp") != "app" || !roles.Contains(workloadRole, StringComparer.Ordinal)) throw Denied();
                kind = PrincipalKind.Workload;
            }
            if (!clients.Any(c => c.TenantId == tenant && c.ClientId == client && c.Kind == kind &&
                (kind == PrincipalKind.User || c.ServicePrincipalObjectId == subject))) throw Denied();
            return new(tenant, subject, client, kind, roles, DateTimeOffset.FromUnixTimeSeconds(seconds), customerId, cellId);
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException or SecurityTokenException or FoundationAuthorizationException)
        { throw Denied(); }
    }

    static SecurityKey CopyPublicRsaKey(SecurityKey source)
    {
        if (source is null || string.IsNullOrWhiteSpace(source.KeyId) || source.KeyId.Length > 128) throw new ArgumentException();
        RSAParameters parameters;
        if (source is RsaSecurityKey rsa) parameters = rsa.Rsa?.ExportParameters(false) ?? rsa.Parameters;
        else if (source is JsonWebKey jwk && jwk.Kty == "RSA")
            parameters = new() { Modulus = Base64UrlEncoder.DecodeBytes(jwk.N), Exponent = Base64UrlEncoder.DecodeBytes(jwk.E) };
        else throw new ArgumentException();
        if (parameters.Modulus is null || parameters.Modulus.Length is < 256 or > 1024 || parameters.Exponent is null || parameters.Exponent.Length is < 1 or > 8) throw new ArgumentException();
        // Key material must describe an actual >=2048-bit RSA modulus and a nontrivial odd public exponent.
        if (parameters.Modulus[0] == 0 || parameters.Modulus.Length == 256 && parameters.Modulus[0] < 128 ||
            (parameters.Modulus[^1] & 1) == 0 || (parameters.Exponent[^1] & 1) == 0 ||
            parameters.Exponent.All(b => b == 0) || parameters.Exponent.Take(parameters.Exponent.Length - 1).All(b => b == 0) && parameters.Exponent[^1] < 3)
            throw new ArgumentException();
        return new RsaSecurityKey(new RSAParameters { Modulus = parameters.Modulus.ToArray(), Exponent = parameters.Exponent.ToArray() }) { KeyId = source.KeyId };
    }

    static JsonDocument ParseObject(byte[] utf8)
    {
        var document = JsonDocument.Parse(utf8, new JsonDocumentOptions { MaxDepth = 8 });
        if (document.RootElement.ValueKind != JsonValueKind.Object) { document.Dispose(); throw Denied(); }
        var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in document.RootElement.EnumerateObject())
            if (!names.Add(property.Name)) { document.Dispose(); throw Denied(); }
        return document;
    }

    static string[] Roles(JsonElement p)
    {
        if (!p.TryGetProperty("roles", out var value) || value.ValueKind != JsonValueKind.Array || value.GetArrayLength() is 0 or > 32) throw Denied();
        var roles = value.EnumerateArray().Select(v => v.ValueKind == JsonValueKind.String ? v.GetString()! : throw Denied()).ToArray();
        if (roles.Any(r => string.IsNullOrWhiteSpace(r) || r.Length > 80) || roles.Distinct(StringComparer.Ordinal).Count() != roles.Length) throw Denied();
        return roles;
    }

    static string String(JsonElement value, string name) => value.TryGetProperty(name, out var item) &&
        item.ValueKind == JsonValueKind.String && !string.IsNullOrEmpty(item.GetString()) ? item.GetString()! : throw Denied();
    static string GuidValue(string value) => value.Length == 36 && Guid.TryParseExact(value, "D", out var guid) && guid != Guid.Empty
        ? guid.ToString("D") : throw Denied();
    static FoundationAuthorizationException Denied() => new("token_invalid", 401);
}
