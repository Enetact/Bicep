using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using SelfService.Foundation;

namespace SelfService.Foundation.Tests;

// Local synthetic issuer only. Nothing in this fixture accesses Entra or a network endpoint.
public sealed class IdentityTestIssuer : IDisposable
{
    public const string Tenant = "11111111-1111-1111-1111-111111111111";
    public const string OtherTenant = "22222222-2222-2222-2222-222222222222";
    public const string Subject = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
    public const string OtherSubject = "bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
    public const string WorkerSubject = "cccccccc-cccc-cccc-cccc-cccccccccccc";
    public const string Client = "33333333-3333-3333-3333-333333333333";
    public const string OtherClient = "44444444-4444-4444-4444-444444444444";
    public const string WorkerClient = "55555555-5555-5555-5555-555555555555";
    public const string Audience = "99999999-9999-9999-9999-999999999999";
    public const string KeyId = "synthetic-offline-identity-v1";
    public static string Issuer(string tenant = Tenant) => $"https://login.microsoftonline.com/{tenant}/v2.0";
    public static string V1Issuer => $"https://sts.windows.net/{Tenant}/";
    readonly RSA rsa = RSA.Create(2048);

    public CellIdentityOptions Options(string customer = "customer-a", string cell = "cell-a") => new(cell, customer, Audience,
        new Dictionary<string, string[]> { [Tenant] = [Issuer(), V1Issuer] },
        new ApprovedCallingClient[] { new(Tenant, Client, PrincipalKind.User), new(Tenant, WorkerClient, PrincipalKind.Workload, WorkerSubject) },
        new SecurityKey[] { new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = KeyId } });

    public Dictionary<string, object> UserClaims(string role = FoundationRoles.Requester, string subject = Subject)
    {
        var now = DateTimeOffset.UtcNow;
        return new(StringComparer.Ordinal)
        {
            ["aud"] = Audience, ["iss"] = Issuer(), ["iat"] = now.AddSeconds(-30).ToUnixTimeSeconds(),
            ["nbf"] = now.AddSeconds(-30).ToUnixTimeSeconds(), ["exp"] = now.AddMinutes(10).ToUnixTimeSeconds(),
            ["tid"] = Tenant, ["oid"] = subject, ["azp"] = Client, ["ver"] = "2.0", ["idtyp"] = "user",
            ["scp"] = "platform.access", ["roles"] = new[] { role },
            ["preferred_username"] = "synthetic-user@example.invalid", ["name"] = "Synthetic identity test payload"
        };
    }

    public Dictionary<string, object> WorkloadClaims()
    {
        var claims = UserClaims(); claims.Remove("scp"); claims["idtyp"] = "app";
        claims["oid"] = WorkerSubject; claims["azp"] = WorkerClient; claims["roles"] = new[] { "Worker.Execute" };
        return claims;
    }

    public string Token(Dictionary<string, object>? claims = null, Dictionary<string, object>? header = null,
        RSA? signingKey = null) => RawToken(JsonSerializer.Serialize(claims ?? UserClaims()),
            JsonSerializer.Serialize(header ?? new() { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = KeyId }), signingKey);

    public string RawToken(string payloadJson, string? headerJson = null, RSA? signingKey = null)
    {
        headerJson ??= JsonSerializer.Serialize(new { alg = "RS256", typ = "JWT", kid = KeyId });
        var header = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(headerJson));
        var payload = Base64UrlEncoder.Encode(Encoding.UTF8.GetBytes(payloadJson));
        var body = header + "." + payload;
        return body + "." + Base64UrlEncoder.Encode((signingKey ?? rsa).SignData(Encoding.ASCII.GetBytes(body),
            HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
    }
    public void Dispose() => rsa.Dispose();
}

internal sealed class IdentityMemberships(params MembershipGrant[] values) : ICustomerMembershipSource
{
    public int Reads { get; private set; }
    public MembershipGrant[] Values { get; set; } = values;
    public IReadOnlyList<MembershipGrant> Find(string customerId, string actorKey) { Reads++; return Values; }
}

internal sealed class IdentityFixedClock(DateTimeOffset now) : TimeProvider
{
    public override DateTimeOffset GetUtcNow() => now;
}
