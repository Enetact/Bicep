using System.Collections.Frozen;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;

namespace SelfService.Foundation;

public sealed record OidcTenantAuthority(string TenantId, string Issuer, Uri MetadataUri, Uri JwksUri)
{
    public static OidcTenantAuthority PublicEntra(string tenantId, bool commonSigningKeys = true)
    {
        var tenant = OidcDocuments.Tenant(tenantId);
        return new(tenant, $"https://login.microsoftonline.com/{tenant}/v2.0",
            new($"https://login.microsoftonline.com/{tenant}/v2.0/.well-known/openid-configuration"),
            new($"https://login.microsoftonline.com/{(commonSigningKeys ? "common" : tenant)}/discovery/v2.0/keys"));
    }
}

public sealed record OidcValidationOptions(CellIdentityOptions Identity, IReadOnlyList<OidcTenantAuthority> Authorities)
{
    public TimeSpan KeyLifetime { get; init; } = TimeSpan.FromHours(24);
    public TimeSpan RefreshAfter { get; init; } = TimeSpan.FromHours(12);
    public TimeSpan MinimumRefreshInterval { get; init; } = TimeSpan.FromMinutes(5);
    public TimeSpan FailureBackoff { get; init; } = TimeSpan.FromMinutes(1);
    public TimeSpan DocumentTimeout { get; init; } = TimeSpan.FromSeconds(10);
}

// Public status deliberately omits signing key material, tokens and provider response bodies.
public sealed record OidcKeySnapshotStatus(string TenantId, DateTimeOffset? ObtainedUtc,
    DateTimeOffset? ExpiresUtc, int KeyCount, DateTimeOffset? NextRefreshAllowedUtc);

/// <summary>
/// Explicit public-Entra v2 discovery mode. Static bootstrap keys validate configuration only;
/// they are never a fallback after discovery failure, key removal or snapshot expiry.
/// </summary>
public sealed class OidcIdentityValidator
{
    readonly CellIdentityOptions identity;
    readonly OidcValidationOptions options;
    readonly IOidcDocumentTransport transport;
    readonly TimeProvider clock;
    readonly FrozenDictionary<string, TenantState> tenants;

    sealed class TenantState(OidcTenantAuthority authority)
    {
        public readonly OidcTenantAuthority Authority = authority;
        public readonly SemaphoreSlim Gate = new(1, 1);
        public Snapshot? Current;
        public long NextAttemptTicks;
    }
    sealed record Snapshot(IdentityTokenValidator Validator, FrozenSet<string> KeyIds,
        DateTimeOffset ObtainedUtc, DateTimeOffset RefreshUtc, DateTimeOffset ExpiresUtc);

    public OidcIdentityValidator(OidcValidationOptions options, IOidcDocumentTransport? transport = null,
        TimeProvider? clock = null)
    {
        ArgumentNullException.ThrowIfNull(options);
        _ = new IdentityTokenValidator(options.Identity);
        if (options.KeyLifetime < TimeSpan.FromMinutes(1) || options.KeyLifetime > TimeSpan.FromHours(24) ||
            options.RefreshAfter < TimeSpan.FromSeconds(1) || options.RefreshAfter >= options.KeyLifetime ||
            options.MinimumRefreshInterval < TimeSpan.FromSeconds(1) || options.MinimumRefreshInterval > TimeSpan.FromHours(1) ||
            options.FailureBackoff < TimeSpan.FromSeconds(1) || options.FailureBackoff > TimeSpan.FromHours(1) ||
            options.DocumentTimeout < TimeSpan.FromSeconds(1) || options.DocumentTimeout > TimeSpan.FromMinutes(1) ||
            options.Authorities is null || options.Authorities.Count is 0 or > 32)
            throw new ArgumentException("Invalid OIDC discovery configuration.");
        var policy = options.Identity;
        // Detach mutable caller-owned collections. Actual snapshot validators copy public RSA material themselves.
        identity = policy with
        {
            TrustedIssuersByTenant = policy.TrustedIssuersByTenant.ToDictionary(p => OidcDocuments.Tenant(p.Key), p => p.Value.ToArray(), StringComparer.Ordinal),
            AllowedClients = policy.AllowedClients.Select(c => new ApprovedCallingClient(OidcDocuments.Tenant(c.TenantId),
                OidcDocuments.Tenant(c.ClientId), c.Kind, c.ServicePrincipalObjectId is null ? null : OidcDocuments.Tenant(c.ServicePrincipalObjectId))).ToArray(),
            SigningKeys = policy.SigningKeys.ToArray()
        };
        var states = new Dictionary<string, TenantState>(StringComparer.Ordinal);
        foreach (var item in options.Authorities)
        {
            var authority = OidcDocuments.Authority(item);
            if (!identity.TrustedIssuersByTenant.TryGetValue(authority.TenantId, out var issuers) ||
                !issuers.Contains(authority.Issuer, StringComparer.Ordinal) ||
                !states.TryAdd(authority.TenantId, new(authority)))
                throw new ArgumentException("Invalid OIDC discovery configuration.");
        }
        if (states.Count != identity.TrustedIssuersByTenant.Count)
            throw new ArgumentException("Every approved tenant needs an explicit OIDC authority.");
        this.options = options with { Identity = identity, Authorities = states.Values.Select(s => s.Authority).ToArray() };
        this.transport = transport ?? new DeniedOidcDocumentTransport();
        this.clock = clock ?? TimeProvider.System;
        tenants = states.ToFrozenDictionary(StringComparer.Ordinal);
    }

    public async Task<FoundationPrincipal> ValidateAsync(string token, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var (tenant, kid) = Route(token);
        var state = tenants[tenant];
        var snapshot = await Keys(state, kid, cancellationToken);
        var result = await snapshot.Validator.ValidateAsync(token, cancellationToken);
        // Do not allow a validation started before expiry to finish using an expired snapshot.
        if (snapshot.ExpiresUtc <= clock.GetUtcNow()) throw Unavailable();
        return result;
    }

    public IReadOnlyList<OidcKeySnapshotStatus> Status() => tenants.Values.OrderBy(t => t.Authority.TenantId, StringComparer.Ordinal)
        .Select(t =>
        {
            var s = Volatile.Read(ref t.Current); var ticks = Interlocked.Read(ref t.NextAttemptTicks);
            return new OidcKeySnapshotStatus(t.Authority.TenantId, s?.ObtainedUtc, s?.ExpiresUtc, s?.KeyIds.Count ?? 0,
                ticks == 0 ? null : new DateTimeOffset(ticks, TimeSpan.Zero));
        }).ToArray();

    async Task<Snapshot> Keys(TenantState state, string kid, CancellationToken cancellationToken)
    {
        var now = clock.GetUtcNow(); var snapshot = Volatile.Read(ref state.Current);
        if (Usable(snapshot, kid, now) && now < snapshot!.RefreshUtc) return snapshot;
        // A cold/rotation fetch already in progress owns the next-attempt marker. Share that result
        // instead of rejecting concurrent callers merely because the fetch has not published yet.
        if (Interlocked.Read(ref state.NextAttemptTicks) > now.UtcTicks && state.Gate.CurrentCount != 0)
            return Existing(snapshot, kid, now);
        await state.Gate.WaitAsync(cancellationToken);
        try
        {
            now = clock.GetUtcNow(); snapshot = Volatile.Read(ref state.Current);
            if (Usable(snapshot, kid, now) && now < snapshot!.RefreshUtc) return snapshot;
            if (Interlocked.Read(ref state.NextAttemptTicks) > now.UtcTicks) return Existing(snapshot, kid, now);
            Interlocked.Exchange(ref state.NextAttemptTicks, now.Add(options.MinimumRefreshInterval).UtcTicks);
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            timeout.CancelAfter(options.DocumentTimeout);
            try
            {
                using var metadata = OidcDocuments.Parse(await Fetch(state.Authority.MetadataUri, timeout.Token));
                var root = metadata.RootElement;
                if (OidcDocuments.String(root, "issuer") != state.Authority.Issuer ||
                    OidcDocuments.String(root, "jwks_uri") != state.Authority.JwksUri.AbsoluteUri)
                    throw new FormatException();
                using var jwks = OidcDocuments.Parse(await Fetch(state.Authority.JwksUri, timeout.Token));
                var publicKeys = OidcDocuments.Keys(jwks.RootElement, state.Authority);
                var tenantPolicy = identity with
                {
                    TrustedIssuersByTenant = new Dictionary<string, string[]> { [state.Authority.TenantId] = [state.Authority.Issuer] },
                    AllowedClients = identity.AllowedClients.Where(c => c.TenantId == state.Authority.TenantId).ToArray(),
                    SigningKeys = publicKeys
                };
                now = clock.GetUtcNow();
                timeout.Token.ThrowIfCancellationRequested();
                var replacement = new Snapshot(new IdentityTokenValidator(tenantPolicy, clock),
                    publicKeys.Select(k => k.KeyId).ToFrozenSet(StringComparer.Ordinal), now,
                    now.Add(options.RefreshAfter), now.Add(options.KeyLifetime));
                Volatile.Write(ref state.Current, replacement);
                return Existing(replacement, kid, now);
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { throw; }
            catch (Exception e) when (e is OperationCanceledException or HttpRequestException or IOException or JsonException or
                FormatException or ArgumentException or InvalidOperationException or CryptographicException or FoundationAuthorizationException or TimeoutException)
            {
                var backoff = options.FailureBackoff > options.MinimumRefreshInterval ? options.FailureBackoff : options.MinimumRefreshInterval;
                Interlocked.Exchange(ref state.NextAttemptTicks, clock.GetUtcNow().Add(backoff).UtcTicks);
                return Existing(Volatile.Read(ref state.Current), kid, clock.GetUtcNow());
            }
        }
        finally { state.Gate.Release(); }
    }

    async Task<byte[]> Fetch(Uri uri, CancellationToken cancellationToken)
    {
        var response = await transport.GetAsync(uri, cancellationToken);
        if (response.StatusCode != 200 || response.Utf8Json is null || response.Utf8Json.Length > OidcDocuments.MaximumDocumentBytes)
            throw new FormatException();
        return response.Utf8Json;
    }
    static bool Usable(Snapshot? snapshot, string kid, DateTimeOffset now) =>
        snapshot is not null && snapshot.ExpiresUtc > now && snapshot.KeyIds.Contains(kid);
    static Snapshot Existing(Snapshot? snapshot, string kid, DateTimeOffset now)
    {
        if (snapshot is null || snapshot.ExpiresUtc <= now) throw Unavailable();
        if (!snapshot.KeyIds.Contains(kid)) throw Invalid();
        return snapshot;
    }

    // This parses only enough untrusted claims to select an already admitted tenant and throttle metadata I/O.
    // It grants no authority; the existing strict validator verifies the signature and complete claims afterward.
    (string Tenant, string Kid) Route(string token)
    {
        try
        {
            if (string.IsNullOrWhiteSpace(token) || Encoding.UTF8.GetByteCount(token) > 16384) throw Invalid();
            var parts = token.Split('.');
            if (parts.Length != 3 || parts.Any(s => s.Length == 0 || s.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')))) throw Invalid();
            using var header = OidcDocuments.Parse(Base64UrlEncoder.DecodeBytes(parts[0]));
            using var payload = OidcDocuments.Parse(Base64UrlEncoder.DecodeBytes(parts[1]));
            var h = header.RootElement; var p = payload.RootElement;
            if (OidcDocuments.String(h, "alg") != "RS256" || OidcDocuments.String(h, "typ") is not ("JWT" or "at+jwt") ||
                h.TryGetProperty("jku", out _) || h.TryGetProperty("x5u", out _) || h.TryGetProperty("crit", out _) || h.TryGetProperty("b64", out _)) throw Invalid();
            var tenant = OidcDocuments.Tenant(OidcDocuments.String(p, "tid"));
            var subject = OidcDocuments.Tenant(OidcDocuments.String(p, "oid"));
            var kid = OidcDocuments.String(h, "kid");
            if (kid.Length > 128 || kid.Any(char.IsControl) || !tenants.TryGetValue(tenant, out var state) ||
                OidcDocuments.String(p, "iss") != state.Authority.Issuer || OidcDocuments.String(p, "aud") != identity.Audience ||
                OidcDocuments.String(p, "ver") != "2.0" || p.TryGetProperty("appid", out _) || p.TryGetProperty("act", out _)) throw Invalid();
            var client = OidcDocuments.Tenant(OidcDocuments.String(p, "azp"));
            if (!p.TryGetProperty("exp", out var exp) || !exp.TryGetInt64(out var seconds) ||
                !p.TryGetProperty("nbf", out var nbf) || !nbf.TryGetInt64(out var begins) ||
                seconds <= begins || DateTimeOffset.FromUnixTimeSeconds(seconds) <= clock.GetUtcNow() ||
                DateTimeOffset.FromUnixTimeSeconds(begins) > clock.GetUtcNow()) throw Invalid();
            if (!p.TryGetProperty("roles", out var roles) || roles.ValueKind != JsonValueKind.Array || roles.GetArrayLength() is 0 or > 32) throw Invalid();
            var roleNames = roles.EnumerateArray().Select(r => r.ValueKind == JsonValueKind.String ? r.GetString()! : throw Invalid()).ToArray();
            if (roleNames.Any(r => string.IsNullOrWhiteSpace(r) || r.Length > 80) ||
                roleNames.Distinct(StringComparer.Ordinal).Count() != roleNames.Length) throw Invalid();
            PrincipalKind kind;
            if (p.TryGetProperty("scp", out var scope))
            {
                if (scope.ValueKind != JsonValueKind.String || !scope.GetString()!.Split(' ', StringSplitOptions.RemoveEmptyEntries).Contains(identity.RequiredDelegatedScope, StringComparer.Ordinal) ||
                    p.TryGetProperty("idtyp", out var userType) && userType.GetString() != "user") throw Invalid();
                kind = PrincipalKind.User;
            }
            else
            {
                if (OidcDocuments.String(p, "idtyp") != "app" || !roleNames.Contains(identity.RequiredWorkloadRole, StringComparer.Ordinal)) throw Invalid();
                kind = PrincipalKind.Workload;
            }
            if (!identity.AllowedClients.Any(c => c.TenantId == tenant && c.ClientId == client && c.Kind == kind &&
                (kind == PrincipalKind.User || c.ServicePrincipalObjectId == subject))) throw Invalid();
            return (tenant, kid);
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException or InvalidOperationException or FoundationAuthorizationException)
        { throw Invalid(); }
    }
    static FoundationAuthorizationException Invalid() => new("token_invalid", 401);
    static FoundationAuthorizationException Unavailable() => new("identity_metadata_unavailable", 503);
}

internal static class OidcDocuments
{
    public const int MaximumDocumentBytes = 262144;
    public static string Tenant(string value) => value is not null && value.Length == 36 && Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
        ? id.ToString("D") : throw new ArgumentException("Invalid OIDC tenant configuration.");
    public static OidcTenantAuthority Authority(OidcTenantAuthority value)
    {
        if (value is null) throw new ArgumentException("Invalid OIDC authority.");
        var tenant = Tenant(value.TenantId); var prefix = "https://login.microsoftonline.com/";
        var expectedIssuer = prefix + tenant + "/v2.0";
        var expectedMetadata = expectedIssuer + "/.well-known/openid-configuration";
        if (value.Issuer != expectedIssuer || !Exact(value.MetadataUri, expectedMetadata) ||
            !(Exact(value.JwksUri, prefix + tenant + "/discovery/v2.0/keys") || Exact(value.JwksUri, prefix + "common/discovery/v2.0/keys")))
            throw new ArgumentException("Only explicit public Entra v2 tenant authorities are supported.");
        return new(tenant, expectedIssuer, new(expectedMetadata), new(value.JwksUri.AbsoluteUri));
    }
    static bool Exact(Uri? value, string expected) => value is not null && value.IsAbsoluteUri &&
        value.Scheme == "https" && value.UserInfo.Length == 0 && value.Query.Length == 0 && value.Fragment.Length == 0 &&
        value.Port == 443 && value.OriginalString == expected;
    public static JsonDocument Parse(byte[] bytes)
    {
        if (bytes.Length is 0 or > MaximumDocumentBytes) throw new FormatException();
        var doc = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 8 });
        try { if (doc.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException(); Unique(doc.RootElement); return doc; }
        catch { doc.Dispose(); throw; }
    }
    static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject()) { if (!seen.Add(property.Name)) throw new FormatException(); Unique(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var item in element.EnumerateArray()) Unique(item);
    }
    public static string String(JsonElement element, string name) => element.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String &&
        !string.IsNullOrEmpty(p.GetString()) ? p.GetString()! : throw new FormatException();
    public static IReadOnlyList<SecurityKey> Keys(JsonElement root, OidcTenantAuthority authority)
    {
        if (!root.TryGetProperty("keys", out var array) || array.ValueKind != JsonValueKind.Array || array.GetArrayLength() is 0 or > 64) throw new FormatException();
        var keys = new List<SecurityKey>(); var kids = new HashSet<string>(StringComparer.Ordinal);
        foreach (var item in array.EnumerateArray())
        {
            var kid = String(item, "kid");
            if (kid.Length > 128 || kid.Any(char.IsControl) || !kids.Add(kid) || String(item, "kty") != "RSA" || String(item, "use") != "sig" ||
                new[] { "d", "p", "q", "dp", "dq", "qi", "oth", "k", "jku", "x5u" }.Any(name => item.TryGetProperty(name, out _)) ||
                item.TryGetProperty("alg", out var alg) && alg.GetString() != "RS256") throw new FormatException();
            if (item.TryGetProperty("key_ops", out var operations) && (operations.ValueKind != JsonValueKind.Array ||
                operations.GetArrayLength() != 1 || operations[0].GetString() != "verify")) throw new FormatException();
            var issuer = String(item, "issuer");
            if (issuer == "https://login.microsoftonline.com/{tenantid}/v2.0") issuer = authority.Issuer;
            if (issuer != authority.Issuer) continue;
            var n = String(item, "n"); var e = String(item, "e");
            if (n.Length is < 342 or > 1366 || e.Length is 0 or > 11 ||
                n.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-')) ||
                e.Any(c => !char.IsAsciiLetterOrDigit(c) && c is not ('_' or '-'))) throw new FormatException();
            keys.Add(new RsaSecurityKey(new RSAParameters { Modulus = Base64UrlEncoder.DecodeBytes(n), Exponent = Base64UrlEncoder.DecodeBytes(e) }) { KeyId = kid });
        }
        if (keys.Count is 0 or > 16) throw new FormatException();
        return keys;
    }
}
