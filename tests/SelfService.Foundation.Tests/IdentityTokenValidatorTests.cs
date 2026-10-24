using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.IdentityModel.Tokens;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class IdentityTokenValidatorTests(IdentityTestIssuer issuer) : IClassFixture<IdentityTestIssuer>
{
    [Theory]
    [InlineData("2.0")]
    [InlineData("1.0")]
    public async Task Signed_Entra_shaped_access_token_validates_exact_issuer_subject_and_caller(string version)
    {
        var claims = issuer.UserClaims();
        if (version == "1.0") { claims["ver"] = version; claims["iss"] = IdentityTestIssuer.V1Issuer; claims.Remove("azp"); claims["appid"] = IdentityTestIssuer.Client; }
        var actual = await new IdentityTokenValidator(issuer.Options()).ValidateAsync(issuer.Token(claims));
        Assert.Equal(IdentityTestIssuer.Tenant, actual.TenantId); Assert.Equal(IdentityTestIssuer.Subject, actual.SubjectId);
        Assert.Equal(IdentityTestIssuer.Client, actual.ClientId); Assert.Equal(PrincipalKind.User, actual.Kind);
        Assert.Equal($"user:{IdentityTestIssuer.Tenant}:{IdentityTestIssuer.Subject}", actual.ActorKey);
        Assert.Equal("customer-a", actual.ValidationCustomerId); Assert.Equal("cell-a", actual.ValidationCellId);
        Assert.Contains(FoundationRoles.Requester, actual.Roles);
    }

    [Theory]
    [InlineData("wrong_audience")]
    [InlineData("wrong_issuer")]
    [InlineData("wrong_tenant")]
    [InlineData("bad_subject")]
    [InlineData("padded_subject")]
    [InlineData("zero_tenant")]
    [InlineData("missing_caller")]
    [InlineData("unapproved_caller")]
    [InlineData("missing_version")]
    [InlineData("unknown_version")]
    [InlineData("both_caller_claims")]
    [InlineData("unverified_actor")]
    [InlineData("missing_scope")]
    [InlineData("wrong_scope")]
    [InlineData("empty_roles")]
    [InlineData("object_roles")]
    [InlineData("string_roles")]
    [InlineData("duplicate_roles")]
    [InlineData("expired")]
    [InlineData("future")]
    [InlineData("missing_nbf")]
    [InlineData("string_exp")]
    [InlineData("string_nbf")]
    [InlineData("missing_exp")]
    [InlineData("id_token")]
    [InlineData("app_with_delegated_scope")]
    public async Task Invalid_identity_contract_is_rejected_without_echoing_token(string variant)
    {
        var claims = issuer.UserClaims();
        switch (variant)
        {
            case "wrong_audience": claims["aud"] = "api://another-cell"; break;
            case "wrong_issuer": claims["iss"] = "https://attacker.invalid/"; break;
            case "wrong_tenant": claims["tid"] = IdentityTestIssuer.OtherTenant; break;
            case "bad_subject": claims["oid"] = "synthetic-user@example.invalid"; break;
            case "padded_subject": claims["oid"] = " " + IdentityTestIssuer.Subject + " "; break;
            case "zero_tenant": claims["tid"] = Guid.Empty.ToString(); break;
            case "missing_caller": claims.Remove("azp"); break;
            case "unapproved_caller": claims["azp"] = IdentityTestIssuer.OtherClient; break;
            case "missing_version": claims.Remove("ver"); break;
            case "unknown_version": claims["ver"] = "3.0"; break;
            case "both_caller_claims": claims["appid"] = IdentityTestIssuer.Client; break;
            case "unverified_actor": claims["act"] = new { sub = IdentityTestIssuer.OtherSubject }; break;
            case "missing_scope": claims.Remove("scp"); break;
            case "wrong_scope": claims["scp"] = "User.Read"; break;
            case "empty_roles": claims["roles"] = Array.Empty<string>(); break;
            case "object_roles": claims["roles"] = new { role = FoundationRoles.Requester }; break;
            case "string_roles": claims["roles"] = FoundationRoles.Requester; break;
            case "duplicate_roles": claims["roles"] = new[] { FoundationRoles.Requester, FoundationRoles.Requester }; break;
            case "expired": claims["exp"] = DateTimeOffset.UtcNow.AddMinutes(-1).ToUnixTimeSeconds(); claims["nbf"] = DateTimeOffset.UtcNow.AddMinutes(-10).ToUnixTimeSeconds(); break;
            case "future": claims["nbf"] = DateTimeOffset.UtcNow.AddMinutes(1).ToUnixTimeSeconds(); break;
            case "missing_nbf": claims.Remove("nbf"); break;
            case "string_exp": claims["exp"] = claims["exp"].ToString()!; break;
            case "string_nbf": claims["nbf"] = claims["nbf"].ToString()!; break;
            case "missing_exp": claims.Remove("exp"); break;
            case "id_token": claims.Remove("scp"); claims.Remove("idtyp"); break;
            case "app_with_delegated_scope": claims["idtyp"] = "app"; break;
        }
        var token = issuer.Token(claims);
        var error = await Assert.ThrowsAsync<FoundationAuthorizationException>(() => new IdentityTokenValidator(issuer.Options()).ValidateAsync(token));
        Assert.Equal(401, error.StatusCode); Assert.Equal("token_invalid", error.Code);
        Assert.DoesNotContain(token, error.ToString(), StringComparison.Ordinal);
        Assert.DoesNotContain("synthetic-user", error.ToString(), StringComparison.Ordinal);
    }

    [Theory]
    [InlineData("alg")]
    [InlineData("typ")]
    [InlineData("kid")]
    [InlineData("jku")]
    [InlineData("x5u")]
    [InlineData("crit")]
    [InlineData("b64")]
    public async Task Unreviewed_header_or_external_key_source_is_rejected(string variant)
    {
        var header = new Dictionary<string, object> { ["alg"] = "RS256", ["typ"] = "JWT", ["kid"] = IdentityTestIssuer.KeyId };
        header[variant] = variant switch { "alg" => "HS256", "typ" => "ID", "kid" => "unknown", _ => "https://attacker.invalid/keys" };
        await Assert.ThrowsAsync<FoundationAuthorizationException>(() => new IdentityTokenValidator(issuer.Options()).ValidateAsync(issuer.Token(header: header)));
    }

    [Fact]
    public async Task Wrong_signature_is_rejected_even_when_every_claim_matches()
    {
        using var other = RSA.Create(2048);
        var validator = new IdentityTokenValidator(issuer.Options());
        await Assert.ThrowsAsync<FoundationAuthorizationException>(() => validator.ValidateAsync(issuer.Token(signingKey: other)));
        var token = issuer.Token(); var parts = token.Split('.'); parts[2] = (parts[2][0] == 'A' ? 'B' : 'A') + parts[2][1..];
        await Assert.ThrowsAsync<FoundationAuthorizationException>(() => validator.ValidateAsync(string.Join('.', parts)));
    }

    [Fact]
    public async Task Duplicate_and_case_ambiguous_claims_are_rejected()
    {
        var json = JsonSerializer.Serialize(issuer.UserClaims());
        foreach (var property in new[] { "tid", "TID" })
        {
            var duplicate = json[..^1] + $",\"{property}\":\"{IdentityTestIssuer.Tenant}\"}}";
            await Assert.ThrowsAsync<FoundationAuthorizationException>(() => new IdentityTokenValidator(issuer.Options()).ValidateAsync(issuer.RawToken(duplicate)));
        }
    }

    [Fact]
    public async Task Oversized_and_unsigned_tokens_fail_closed()
    {
        var validator = new IdentityTokenValidator(issuer.Options());
        var claims = issuer.UserClaims(); claims["untrusted"] = new string('x', 17000);
        await Assert.ThrowsAsync<FoundationAuthorizationException>(() => validator.ValidateAsync(issuer.Token(claims)));
        await Assert.ThrowsAsync<FoundationAuthorizationException>(() => validator.ValidateAsync("eyJhbGciOiJub25lIn0.e30."));
        await Assert.ThrowsAsync<FoundationAuthorizationException>(() => validator.ValidateAsync("not-a-token"));
    }

    [Fact]
    public async Task Tenant_issuer_pair_must_match_even_when_both_directories_are_trusted()
    {
        var original = issuer.Options();
        var options = original with
        {
            TrustedIssuersByTenant = new Dictionary<string, string[]> { [IdentityTestIssuer.Tenant] = [IdentityTestIssuer.Issuer()],
                [IdentityTestIssuer.OtherTenant] = [IdentityTestIssuer.Issuer(IdentityTestIssuer.OtherTenant)] },
            AllowedClients = [.. original.AllowedClients, new(IdentityTestIssuer.OtherTenant, IdentityTestIssuer.Client, PrincipalKind.User)]
        };
        var claims = issuer.UserClaims(); claims["tid"] = IdentityTestIssuer.OtherTenant;
        await Assert.ThrowsAsync<FoundationAuthorizationException>(() => new IdentityTokenValidator(options).ValidateAsync(issuer.Token(claims)));
    }

    [Fact]
    public async Task Trusted_configuration_is_frozen_after_construction()
    {
        var options = issuer.Options(); var validator = new IdentityTokenValidator(options);
        ((Dictionary<string, string[]>)options.TrustedIssuersByTenant)[IdentityTestIssuer.Tenant][0] = "https://attacker.invalid/";
        ((ApprovedCallingClient[])options.AllowedClients)[0] = new(IdentityTestIssuer.Tenant, IdentityTestIssuer.OtherClient, PrincipalKind.User);
        options.SigningKeys[0].KeyId = "changed-after-construction";
        var actual = await validator.ValidateAsync(issuer.Token());
        Assert.Equal(IdentityTestIssuer.Client, actual.ClientId);
    }

    [Fact]
    public void Symmetric_keys_and_unbound_workload_clients_are_rejected_as_configuration()
    {
        var options = issuer.Options();
        Assert.Throws<ArgumentException>(() => new IdentityTokenValidator(options with { SigningKeys = [new SymmetricSecurityKey(new byte[32]) { KeyId = "unreviewed" }] }));
        Assert.Throws<ArgumentException>(() => new IdentityTokenValidator(options with { AllowedClients = [new(IdentityTestIssuer.Tenant, IdentityTestIssuer.WorkerClient, PrincipalKind.Workload)] }));
    }

    [Theory]
    [InlineData("trivial_exponent")]
    [InlineData("even_exponent")]
    [InlineData("short_modulus_bits")]
    public void Invalid_RSA_public_parameters_are_rejected_before_serving_requests(string variant)
    {
        var options = issuer.Options(); var parameters = ((RsaSecurityKey)options.SigningKeys[0]).Parameters;
        if (variant == "trivial_exponent") parameters.Exponent = [1];
        else if (variant == "even_exponent") parameters.Exponent = [2];
        else { parameters.Modulus = parameters.Modulus!.ToArray(); parameters.Modulus[0] = 1; }
        Assert.Throws<ArgumentException>(() => new IdentityTokenValidator(options with
        { SigningKeys = [new RsaSecurityKey(parameters) { KeyId = IdentityTestIssuer.KeyId }] }));
    }

    [Fact]
    public async Task Cancellation_is_preserved_without_identity_failure_relabeling()
    {
        using var cts = new CancellationTokenSource(); cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => new IdentityTokenValidator(issuer.Options()).ValidateAsync(issuer.Token(), cts.Token));
    }

    [Fact]
    public async Task Workload_access_requires_explicit_type_role_client_and_service_principal_binding()
    {
        var validator = new IdentityTokenValidator(issuer.Options());
        var actual = await validator.ValidateAsync(issuer.Token(issuer.WorkloadClaims()));
        Assert.Equal(PrincipalKind.Workload, actual.Kind);
        Assert.Equal($"workload:{IdentityTestIssuer.Tenant}:{IdentityTestIssuer.WorkerSubject}:{IdentityTestIssuer.WorkerClient}", actual.ActorKey);
        foreach (var variant in new[] { "wrong_subject", "wrong_client", "missing_idtyp", "missing_role" })
        {
            var claims = issuer.WorkloadClaims();
            switch (variant)
            {
                case "wrong_subject": claims["oid"] = IdentityTestIssuer.OtherSubject; break;
                case "wrong_client": claims["azp"] = IdentityTestIssuer.Client; break;
                case "missing_idtyp": claims.Remove("idtyp"); break;
                case "missing_role": claims["roles"] = new[] { FoundationRoles.Operator }; break;
            }
            await Assert.ThrowsAsync<FoundationAuthorizationException>(() => validator.ValidateAsync(issuer.Token(claims)));
        }
    }
}
