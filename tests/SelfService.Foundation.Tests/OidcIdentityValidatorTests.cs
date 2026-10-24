using System.Collections.Concurrent;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.IdentityModel.Tokens;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class OidcIdentityValidatorTests
{
    [Fact]
    public async Task Tenant_metadata_and_issuer_bound_public_JWK_validate_genuinely_signed_access_token()
    {
        using var f = new RotationFixture();
        var principal = await f.Validator.ValidateAsync(f.Token());
        Assert.Equal("customer-a", principal.ValidationCustomerId);
        Assert.Equal("cell-a", principal.ValidationCellId);
        Assert.Equal(IdentityTestIssuer.Tenant, principal.TenantId);
        Assert.Equal($"user:{IdentityTestIssuer.Tenant}:{IdentityTestIssuer.Subject}", principal.ActorKey);
        Assert.Equal(2, f.Transport.Calls);
        Assert.Equal(new[] { f.Authority.MetadataUri, f.Authority.JwksUri }, f.Transport.Requests);
        var status = Assert.Single(f.Validator.Status());
        Assert.Equal(1, status.KeyCount); Assert.Equal(f.Clock.GetUtcNow().AddMinutes(2), status.ExpiresUtc);
        Assert.DoesNotContain("rotation-a", JsonSerializer.Serialize(status));
        Assert.DoesNotContain(Base64UrlEncoder.Encode(f.A.ExportParameters(false).Modulus!), JsonSerializer.Serialize(status));
    }

    [Fact]
    public async Task Explicit_discovery_mode_does_not_fall_back_to_static_keys_when_transport_is_denied()
    {
        using var f = new RotationFixture();
        var denied = new OidcIdentityValidator(f.Options, clock: f.Clock);
        var token = f.Issuer.Token(f.Claims()); // Correct signature for the configured static bootstrap key.
        var error = await Assert.ThrowsAsync<FoundationAuthorizationException>(() => denied.ValidateAsync(token));
        Assert.Equal(503, error.StatusCode); Assert.Equal("identity_metadata_unavailable", error.Code);
        Assert.Null(Assert.Single(denied.Status()).ObtainedUtc);
    }

    [Fact]
    public async Task Concurrent_cold_requests_share_one_atomic_tenant_snapshot()
    {
        using var f = new RotationFixture(); f.Transport.Delay = TimeSpan.FromMilliseconds(20);
        var token = f.Token();
        var principals = await Task.WhenAll(Enumerable.Range(0, 20).Select(_ => f.Validator.ValidateAsync(token)));
        Assert.All(principals, p => Assert.Equal(IdentityTestIssuer.Subject, p.SubjectId));
        Assert.Equal(2, f.Transport.Calls);
        await f.Validator.ValidateAsync(token); Assert.Equal(2, f.Transport.Calls);
    }

    [Fact]
    public async Task Unknown_kid_refreshes_only_after_minimum_interval_and_accepts_rotated_key()
    {
        using var f = new RotationFixture(); await f.Validator.ValidateAsync(f.Token());
        f.Transport.Jwks = f.Jwks(("rotation-a", f.A), ("rotation-b", f.B));
        await Denied(f.Validator, f.Token("rotation-b", f.B), 401); Assert.Equal(2, f.Transport.Calls);
        f.Clock.Advance(TimeSpan.FromSeconds(6));
        var principal = await f.Validator.ValidateAsync(f.Token("rotation-b", f.B));
        Assert.Equal(IdentityTestIssuer.Subject, principal.SubjectId); Assert.Equal(4, f.Transport.Calls);
        Assert.Equal(2, Assert.Single(f.Validator.Status()).KeyCount);
        await f.Validator.ValidateAsync(f.Token()); Assert.Equal(4, f.Transport.Calls);
    }

    [Fact]
    public async Task Successful_refresh_removes_old_key_without_a_static_or_old_snapshot_fallback()
    {
        using var f = new RotationFixture(); await f.Validator.ValidateAsync(f.Token());
        f.Transport.Jwks = f.Jwks(("rotation-b", f.B)); f.Clock.Advance(TimeSpan.FromSeconds(61));
        await f.Validator.ValidateAsync(f.Token("rotation-b", f.B));
        await Denied(f.Validator, f.Token(), 401);
        Assert.Equal(4, f.Transport.Calls); Assert.Equal(1, Assert.Single(f.Validator.Status()).KeyCount);
    }

    [Fact]
    public async Task Metadata_outage_uses_only_unexpired_snapshot_and_never_extends_its_expiry()
    {
        using var f = new RotationFixture(); await f.Validator.ValidateAsync(f.Token());
        var expires = Assert.Single(f.Validator.Status()).ExpiresUtc;
        f.Transport.Fail = true; f.Clock.Advance(TimeSpan.FromSeconds(61));
        await f.Validator.ValidateAsync(f.Token());
        Assert.Equal(expires, Assert.Single(f.Validator.Status()).ExpiresUtc); Assert.Equal(3, f.Transport.Calls);
        f.Clock.Advance(TimeSpan.FromSeconds(60));
        await Denied(f.Validator, f.Token(), 503);
        Assert.Equal(expires, Assert.Single(f.Validator.Status()).ExpiresUtc); Assert.Equal(4, f.Transport.Calls);
        await Denied(f.Validator, f.Token(), 503); Assert.Equal(4, f.Transport.Calls);
    }

    [Fact]
    public async Task Failed_replacement_is_atomic_and_cannot_refresh_ttl_or_partially_replace_keys()
    {
        using var f = new RotationFixture(); await f.Validator.ValidateAsync(f.Token());
        var previous = Assert.Single(f.Validator.Status());
        f.Transport.Jwks = f.Jwks(("rotation-b", f.B), ("rotation-b", f.A));
        f.Clock.Advance(TimeSpan.FromSeconds(61));
        await f.Validator.ValidateAsync(f.Token());
        Assert.Equal(previous.ObtainedUtc, Assert.Single(f.Validator.Status()).ObtainedUtc);
        Assert.Equal(previous.ExpiresUtc, Assert.Single(f.Validator.Status()).ExpiresUtc);
        await Denied(f.Validator, f.Token("rotation-b", f.B), 401); Assert.Equal(4, f.Transport.Calls);
        f.Clock.Advance(TimeSpan.FromSeconds(60)); await Denied(f.Validator, f.Token(), 503);
    }

    [Fact]
    public async Task Attacker_selected_unknown_kids_cannot_create_unbounded_refresh_or_negative_cache_entries()
    {
        using var f = new RotationFixture(); await f.Validator.ValidateAsync(f.Token());
        f.Clock.Advance(TimeSpan.FromSeconds(6));
        for (var index = 0; index < 50; index++) await Denied(f.Validator, f.Token("unknown-" + index), 401);
        Assert.Equal(4, f.Transport.Calls); Assert.Equal(1, Assert.Single(f.Validator.Status()).KeyCount);
    }

    [Theory]
    [InlineData("issuer")]
    [InlineData("tenant")]
    [InlineData("audience")]
    [InlineData("caller")]
    [InlineData("expired")]
    [InlineData("not-yet-valid")]
    [InlineData("scope")]
    [InlineData("token-kind")]
    [InlineData("jku")]
    [InlineData("x5u")]
    [InlineData("algorithm")]
    [InlineData("version")]
    [InlineData("actor")]
    public async Task Invalid_routing_claims_are_rejected_before_any_discovery_request(string fault)
    {
        using var f = new RotationFixture(); var claims = f.Claims(); var header = f.Header();
        switch (fault)
        {
            case "issuer": claims["iss"] = "https://attacker.invalid/issuer"; break;
            case "tenant": claims["tid"] = IdentityTestIssuer.OtherTenant; break;
            case "audience": claims["aud"] = "other-api"; break;
            case "caller": claims["azp"] = IdentityTestIssuer.OtherClient; break;
            case "expired": claims["exp"] = f.Clock.GetUtcNow().AddSeconds(-1).ToUnixTimeSeconds(); break;
            case "not-yet-valid": claims["nbf"] = f.Clock.GetUtcNow().AddSeconds(1).ToUnixTimeSeconds(); break;
            case "scope": claims["scp"] = "unrelated.scope"; break;
            case "token-kind": claims["idtyp"] = "app"; break;
            case "jku": header["jku"] = "https://attacker.invalid/keys"; break;
            case "x5u": header["x5u"] = "http://127.0.0.1/private"; break;
            case "algorithm": header["alg"] = "none"; break;
            case "version": claims["ver"] = "1.0"; break;
            case "actor": claims["act"] = new { sub = "forged" }; break;
        }
        await Denied(f.Validator, f.Issuer.Token(claims, header, f.A), 401);
        Assert.Equal(0, f.Transport.Calls);
    }

    [Theory]
    [InlineData("")]
    [InlineData("a.b")]
    [InlineData("a.b.c.d")]
    [InlineData("a.b.\n")]
    [InlineData("eyJhbGciOiJSUzI1NiJ9.e30.x")]
    public async Task Malformed_tokens_never_trigger_metadata_io(string token)
    {
        using var f = new RotationFixture(); await Denied(f.Validator, token, 401); Assert.Equal(0, f.Transport.Calls);
    }

    [Fact]
    public async Task Duplicate_case_ambiguous_token_claims_do_not_trigger_io()
    {
        using var f = new RotationFixture();
        var json = JsonSerializer.Serialize(f.Claims()).TrimEnd('}') + ",\"Tid\":\"" + IdentityTestIssuer.Tenant + "\"}";
        await Denied(f.Validator, f.Issuer.RawToken(json, JsonSerializer.Serialize(f.Header()), f.A), 401);
        Assert.Equal(0, f.Transport.Calls);
    }

    [Theory]
    [InlineData("metadata-issuer")]
    [InlineData("metadata-template-issuer")]
    [InlineData("jwks-origin")]
    [InlineData("jwks-query")]
    [InlineData("metadata-duplicate")]
    [InlineData("oversized")]
    [InlineData("array")]
    [InlineData("http-error")]
    public async Task Untrusted_or_malformed_metadata_is_not_followed_or_accepted(string fault)
    {
        using var f = new RotationFixture();
        var metadata = JsonNode.Parse(f.Transport.Metadata)!.AsObject();
        switch (fault)
        {
            case "metadata-issuer": metadata["issuer"] = IdentityTestIssuer.Issuer(IdentityTestIssuer.OtherTenant); break;
            case "metadata-template-issuer": metadata["issuer"] = "https://login.microsoftonline.com/{tenantid}/v2.0"; break;
            case "jwks-origin": metadata["jwks_uri"] = "https://attacker.invalid/keys"; break;
            case "jwks-query": metadata["jwks_uri"] = f.Authority.JwksUri.AbsoluteUri + "?appid=unreviewed"; break;
            case "http-error": f.Transport.StatusCode = 302; break;
        }
        f.Transport.Metadata = fault switch
        {
            "metadata-duplicate" => "{\"issuer\":\"one\",\"Issuer\":\"two\"}",
            "oversized" => new string('x', 262145), "array" => "[]", _ => metadata.ToJsonString()
        };
        await Denied(f.Validator, f.Token(), 503);
        Assert.Equal(1, f.Transport.Calls); Assert.Null(Assert.Single(f.Validator.Status()).ObtainedUtc);
    }

    [Theory]
    [InlineData("private")]
    [InlineData("weak")]
    [InlineData("trivial-exponent")]
    [InlineData("symmetric")]
    [InlineData("enc-use")]
    [InlineData("algorithm")]
    [InlineData("key-ops")]
    [InlineData("foreign-issuer")]
    [InlineData("missing-issuer")]
    [InlineData("duplicate-kid")]
    [InlineData("duplicate-field")]
    [InlineData("malformed-modulus")]
    [InlineData("empty")]
    public async Task Only_issuer_bound_public_RSA_signature_keys_are_admitted(string fault)
    {
        using var f = new RotationFixture(); var document = JsonNode.Parse(f.Transport.Jwks)!.AsObject();
        var key = document["keys"]!.AsArray()[0]!.AsObject();
        switch (fault)
        {
            case "private": key["d"] = "private-field-forbidden"; break;
            case "weak": using (var weak = RSA.Create(1024)) key["n"] = Base64UrlEncoder.Encode(weak.ExportParameters(false).Modulus!); break;
            case "trivial-exponent": key["e"] = "AQ"; break;
            case "symmetric": key["kty"] = "oct"; break;
            case "enc-use": key["use"] = "enc"; break;
            case "algorithm": key["alg"] = "HS256"; break;
            case "key-ops": key["key_ops"] = new JsonArray("sign"); break;
            case "foreign-issuer": key["issuer"] = IdentityTestIssuer.Issuer(IdentityTestIssuer.OtherTenant); break;
            case "missing-issuer": key.Remove("issuer"); break;
            case "duplicate-kid": document["keys"]!.AsArray().Add(key.DeepClone()); break;
            case "malformed-modulus": key["n"] = new string('!', 342); break;
            case "empty": document["keys"] = new JsonArray(); break;
        }
        f.Transport.Jwks = fault == "duplicate-field" ? f.Transport.Jwks.Replace("\"kid\":\"rotation-a\"", "\"kid\":\"rotation-a\",\"Kid\":\"rotation-a\"") : document.ToJsonString();
        await Denied(f.Validator, f.Token(), 503);
        Assert.Equal(2, f.Transport.Calls); Assert.Equal(0, Assert.Single(f.Validator.Status()).KeyCount);
    }

    [Fact]
    public async Task A_shared_kid_cannot_mix_signing_keys_between_two_admitted_tenants()
    {
        using var f = new RotationFixture();
        var authorityA = OidcTenantAuthority.PublicEntra(IdentityTestIssuer.Tenant, false);
        var authorityB = OidcTenantAuthority.PublicEntra(IdentityTestIssuer.OtherTenant, false);
        var identity = f.Options.Identity with
        {
            TrustedIssuersByTenant = new Dictionary<string, string[]> { [IdentityTestIssuer.Tenant] = [authorityA.Issuer], [IdentityTestIssuer.OtherTenant] = [authorityB.Issuer] },
            AllowedClients = [new(IdentityTestIssuer.Tenant, IdentityTestIssuer.Client, PrincipalKind.User), new(IdentityTestIssuer.OtherTenant, IdentityTestIssuer.Client, PrincipalKind.User)]
        };
        var transport = new RoutingTransport(new Dictionary<Uri, string>
        {
            [authorityA.MetadataUri] = JsonSerializer.Serialize(new { issuer = authorityA.Issuer, jwks_uri = authorityA.JwksUri.AbsoluteUri }),
            [authorityB.MetadataUri] = JsonSerializer.Serialize(new { issuer = authorityB.Issuer, jwks_uri = authorityB.JwksUri.AbsoluteUri }),
            [authorityA.JwksUri] = f.Jwks(("rotation-a", f.A)), [authorityB.JwksUri] = f.Jwks(("rotation-a", f.B))
        });
        var validator = new OidcIdentityValidator(f.Options with { Identity = identity, Authorities = [authorityA, authorityB] }, transport, f.Clock);
        Assert.Equal(IdentityTestIssuer.Tenant, (await validator.ValidateAsync(f.Token())).TenantId);
        var other = f.Claims(); other["tid"] = IdentityTestIssuer.OtherTenant; other["iss"] = authorityB.Issuer;
        await Denied(validator, f.Issuer.Token(other, f.Header(), f.A), 401);
        Assert.Equal(IdentityTestIssuer.OtherTenant, (await validator.ValidateAsync(f.Issuer.Token(other, f.Header(), f.B))).TenantId);
        Assert.Equal(4, transport.Calls); Assert.Equal(2, validator.Status().Count);
    }

    [Fact]
    public async Task Shared_key_document_skips_foreign_issuer_keys_without_importing_their_authority()
    {
        using var f = new RotationFixture();
        var doc = JsonNode.Parse(f.Jwks(("rotation-a", f.A), ("rotation-b", f.B)))!.AsObject();
        doc["keys"]!.AsArray()[1]!["issuer"] = IdentityTestIssuer.Issuer(IdentityTestIssuer.OtherTenant);
        f.Transport.Jwks = doc.ToJsonString(); await f.Validator.ValidateAsync(f.Token());
        Assert.Equal(1, Assert.Single(f.Validator.Status()).KeyCount);
        await Denied(f.Validator, f.Token("rotation-b", f.B), 401);
    }

    [Fact]
    public async Task Caller_cancellation_is_preserved_and_cannot_publish_partial_metadata()
    {
        using var f = new RotationFixture(); f.Transport.Delay = TimeSpan.FromSeconds(5);
        using var cancelled = new CancellationTokenSource(); cancelled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Validator.ValidateAsync(f.Token(), cancelled.Token));
        Assert.Equal(0, f.Transport.Calls);
        using var during = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Validator.ValidateAsync(f.Token(), during.Token));
        Assert.Equal(1, f.Transport.Calls); Assert.Null(Assert.Single(f.Validator.Status()).ObtainedUtc);
        f.Transport.Delay = TimeSpan.Zero; f.Clock.Advance(TimeSpan.FromSeconds(6));
        await f.Validator.ValidateAsync(f.Token()); Assert.Equal(3, f.Transport.Calls);
    }

    [Fact]
    public async Task Discovery_timeout_fails_closed_with_bounded_backoff()
    {
        using var f = new RotationFixture(); f.Transport.Delay = TimeSpan.FromSeconds(5);
        await Denied(f.Validator, f.Token(), 503);
        Assert.Equal(1, f.Transport.Calls);
        await Denied(f.Validator, f.Token(), 503); Assert.Equal(1, f.Transport.Calls);
    }

    [Theory]
    [InlineData("metadata-host")]
    [InlineData("metadata-common")]
    [InlineData("metadata-http")]
    [InlineData("metadata-query")]
    [InlineData("jwks-host")]
    [InlineData("jwks-userinfo")]
    [InlineData("jwks-fragment")]
    [InlineData("issuer")]
    [InlineData("duplicate-tenant")]
    [InlineData("missing-tenant")]
    [InlineData("expired-policy")]
    public void Unsafe_or_ambiguous_authority_configuration_is_rejected_before_io(string fault)
    {
        using var f = new RotationFixture(); var a = f.Authority;
        a = fault switch
        {
            "metadata-host" => a with { MetadataUri = new("https://attacker.invalid/metadata") },
            "metadata-common" => a with { MetadataUri = new("https://login.microsoftonline.com/common/v2.0/.well-known/openid-configuration") },
            "metadata-http" => a with { MetadataUri = new(a.MetadataUri.AbsoluteUri.Replace("https:", "http:")) },
            "metadata-query" => a with { MetadataUri = new(a.MetadataUri.AbsoluteUri + "?appid=other") },
            "jwks-host" => a with { JwksUri = new("https://attacker.invalid/keys") },
            "jwks-userinfo" => a with { JwksUri = new("https://user@login.microsoftonline.com/common/discovery/v2.0/keys") },
            "jwks-fragment" => a with { JwksUri = new(a.JwksUri.AbsoluteUri + "#keys") },
            "issuer" => a with { Issuer = IdentityTestIssuer.Issuer(IdentityTestIssuer.OtherTenant) }, _ => a
        };
        var options = f.Options with { Authorities = fault == "duplicate-tenant" ? [a, a] : fault == "missing-tenant" ? [] : [a] };
        if (fault == "expired-policy") options = options with { KeyLifetime = TimeSpan.FromDays(2) };
        Assert.Throws<ArgumentException>(() => new OidcIdentityValidator(options, f.Transport, f.Clock)); Assert.Equal(0, f.Transport.Calls);
    }

    static async Task Denied(OidcIdentityValidator validator, string token, int status)
    {
        var e = await Assert.ThrowsAsync<FoundationAuthorizationException>(() => validator.ValidateAsync(token));
        Assert.Equal(status, e.StatusCode); if (token.Length > 0) Assert.DoesNotContain(token, e.ToString(), StringComparison.Ordinal);
    }

    sealed class RotationFixture : IDisposable
    {
        public readonly IdentityTestIssuer Issuer = new();
        public readonly RSA A = RSA.Create(2048), B = RSA.Create(2048);
        public readonly MutableClock Clock = new(DateTimeOffset.UtcNow);
        public readonly OidcTenantAuthority Authority = OidcTenantAuthority.PublicEntra(IdentityTestIssuer.Tenant);
        public readonly FakeTransport Transport = new();
        public readonly OidcValidationOptions Options;
        public readonly OidcIdentityValidator Validator;
        public RotationFixture()
        {
            Transport.Metadata = File.ReadAllText(Path.Combine(AppContext.BaseDirectory, "Fixtures", "Identity", "entra-v2-openid-configuration.json"));
            Transport.Jwks = Jwks(("rotation-a", A));
            Options = new(Issuer.Options(), [Authority])
            {
                KeyLifetime = TimeSpan.FromMinutes(2), RefreshAfter = TimeSpan.FromMinutes(1), MinimumRefreshInterval = TimeSpan.FromSeconds(5),
                FailureBackoff = TimeSpan.FromSeconds(10), DocumentTimeout = TimeSpan.FromSeconds(1)
            };
            Validator = new(Options, Transport, Clock);
        }
        public Dictionary<string, object> Claims()
        {
            var claims = Issuer.UserClaims(); claims["nbf"] = Clock.GetUtcNow().AddMinutes(-1).ToUnixTimeSeconds();
            claims["exp"] = Clock.GetUtcNow().AddHours(1).ToUnixTimeSeconds(); return claims;
        }
        public Dictionary<string, object> Header(string kid = "rotation-a") => new() { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = kid };
        public string Token(string kid = "rotation-a", RSA? rsa = null) => Issuer.Token(Claims(), Header(kid), rsa ?? A);
        public string Jwks(params (string Kid, RSA Key)[] keys) => JsonSerializer.Serialize(new
        {
            keys = keys.Select(k => new
            {
                kid = k.Kid, kty = "RSA", use = "sig", alg = "RS256", n = Base64UrlEncoder.Encode(k.Key.ExportParameters(false).Modulus!),
                e = Base64UrlEncoder.Encode(k.Key.ExportParameters(false).Exponent!), issuer = "https://login.microsoftonline.com/{tenantid}/v2.0"
            })
        });
        public void Dispose() { Issuer.Dispose(); A.Dispose(); B.Dispose(); }
    }

    sealed class MutableClock(DateTimeOffset initial) : TimeProvider
    {
        long ticks = initial.UtcTicks;
        public override DateTimeOffset GetUtcNow() => new(Interlocked.Read(ref ticks), TimeSpan.Zero);
        public void Advance(TimeSpan duration) => Interlocked.Add(ref ticks, duration.Ticks);
    }
    sealed class FakeTransport : IOidcDocumentTransport
    {
        int calls;
        public int Calls => Volatile.Read(ref calls);
        public readonly ConcurrentQueue<Uri> Requests = new();
        public string Metadata = "", Jwks = "";
        public bool Fail; public int StatusCode = 200; public TimeSpan Delay;
        public async Task<OidcDocumentResponse> GetAsync(Uri uri, CancellationToken cancellationToken)
        {
            Interlocked.Increment(ref calls); Requests.Enqueue(uri);
            if (Delay > TimeSpan.Zero) await Task.Delay(Delay, cancellationToken);
            if (Fail) throw new HttpRequestException("Synthetic discovery outage");
            return new(StatusCode, Encoding.UTF8.GetBytes(uri.AbsolutePath.EndsWith("openid-configuration", StringComparison.Ordinal) ? Metadata : Jwks));
        }
    }
    sealed class RoutingTransport(Dictionary<Uri, string> documents) : IOidcDocumentTransport
    {
        public int Calls;
        public Task<OidcDocumentResponse> GetAsync(Uri uri, CancellationToken cancellationToken)
        { Interlocked.Increment(ref Calls); return Task.FromResult(new OidcDocumentResponse(200, Encoding.UTF8.GetBytes(documents[uri]))); }
    }
}
