using System.Net;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class CredentialAcquisitionTests
{
    [Fact]
    public async Task GitHub_App_JWT_requests_one_repository_and_exact_permissions_then_encrypts_installation_token()
    {
        using var f = new CredentialFixture(); using var keys = new AppKeys(); var handler = new TokenHandler(GitHubResponse(f));
        using var http = CredentialTokenHttpClient.ForMockHandler(handler);
        var source = new CredentialGitHubInstallationSource(f.Scope, f.Connection, new("Iv1.synthetic-client", 7, 77), f.Vault, keys, http, f.Clock);
        var result = await source.AcquireAndStoreAsync();
        Assert.True(result.Mock); Assert.False(result.ProviderPermissionsVerified); Assert.Equal(1, handler.Calls);
        Assert.Equal("https://api.github.com/app/installations/7/access_tokens", handler.Uri!.AbsoluteUri);
        Assert.Contains("\"repository_ids\":[77]", handler.Body); Assert.Contains("\"actions\":\"write\"", handler.Body);
        Assert.DoesNotContain("contents", handler.Body);
        Assert.Equal(PipelineRequestFactory.GitHubApiVersion, handler.ApiVersion);
        var jwt = handler.Authorization![7..].Split('.');
        static byte[] Decode(string value) => Convert.FromBase64String(value.Replace('-', '+').Replace('_', '/') + new string('=', (4 - value.Length % 4) % 4));
        Assert.True(keys.Rsa.VerifyData(Encoding.ASCII.GetBytes(jwt[0] + "." + jwt[1]), Decode(jwt[2]), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1));
        using var claims = JsonDocument.Parse(Decode(jwt[1]));
        Assert.Equal("Iv1.synthetic-client", claims.RootElement.GetProperty("iss").GetString());
        Assert.Equal(f.Clock.Now.AddMinutes(8).ToUnixTimeSeconds(), claims.RootElement.GetProperty("exp").GetInt64());
        using var lease = await f.Vault.ResolveAsync(f.Scope); Assert.Equal(result.Profile.Generation, lease.Profile.Generation);
        Assert.DoesNotContain(CredentialFixture.Token, JsonSerializer.Serialize(result));
        Assert.DoesNotContain(CredentialFixture.Token, await File.ReadAllTextAsync(Assert.Single(Directory.GetFiles(f.VaultRoot, "*.credential.json"))));
    }

    [Theory]
    [InlineData("broad_permissions")]
    [InlineData("missing_actions")]
    [InlineData("repository")]
    [InlineData("repository_name")]
    [InlineData("extra_repository")]
    [InlineData("expired")]
    [InlineData("long_lifetime")]
    [InlineData("header_token")]
    [InlineData("duplicate")]
    public async Task GitHub_response_cannot_widen_repository_permission_lifetime_or_inject_a_header(string fault)
    {
        using var f = new CredentialFixture(); using var keys = new AppKeys(); var body = JsonNode.Parse(GitHubResponse(f))!;
        switch (fault)
        {
            case "broad_permissions": body["permissions"]!["administration"] = "write"; break;
            case "missing_actions": body["permissions"]!.AsObject().Remove("actions"); break;
            case "repository": body["repositories"]![0]!["id"] = 999; break;
            case "repository_name": body["repositories"]![0]!["full_name"] = "other-org/customer-repo"; break;
            case "extra_repository": body["repositories"]!.AsArray().Add(new JsonObject { ["id"] = 999, ["full_name"] = "other/repo" }); break;
            case "expired": body["expires_at"] = f.Clock.Now.AddSeconds(-1).ToString("O"); break;
            case "long_lifetime": body["expires_at"] = f.Clock.Now.AddHours(12).ToString("O"); break;
            case "header_token": body["token"] = "bad\r\nInjected:true"; break;
        }
        var json = body.ToJsonString(); if (fault == "duplicate") json = json.Replace("\"token\":", "\"token\":\"forged\",\"Token\":", StringComparison.Ordinal);
        using var http = CredentialTokenHttpClient.ForMockHandler(new TokenHandler(json));
        var source = new CredentialGitHubInstallationSource(f.Scope, f.Connection, new("Iv1.synthetic-client", 7, 77), f.Vault, keys, http, f.Clock);
        var error = await Assert.ThrowsAsync<CredentialException>(() => source.AcquireAndStoreAsync());
        Assert.DoesNotContain(CredentialFixture.Token, error.ToString()); Assert.Empty(Directory.GetFiles(f.VaultRoot, "*.credential.json"));
    }

    [Fact]
    public async Task Default_token_client_denies_requests_before_private_material_is_requested()
    {
        using var f = new CredentialFixture(); using var keys = new AppKeys(); using var http = new CredentialTokenHttpClient();
        var source = new CredentialGitHubInstallationSource(f.Scope, f.Connection, new("Iv1.synthetic-client", 7, 77), f.Vault, keys, http, f.Clock);
        Assert.Equal("credential-token-network-disabled", (await Assert.ThrowsAsync<CredentialException>(() => source.AcquireAndStoreAsync())).Code);
        Assert.Equal(0, keys.Calls);
        using var ado = new CredentialFixture(PipelineRequestFactory.AzureDevOps); var assertions = new Assertions();
        var entra = new CredentialEntraFederatedSource(ado.Scope, new(Assertions.Tenant, Assertions.Client), ado.Vault, assertions, http, ado.Clock);
        Assert.Equal("credential-token-network-disabled", (await Assert.ThrowsAsync<CredentialException>(() => entra.AcquireAndStoreAsync())).Code);
        Assert.Equal(0, assertions.Calls);
    }

    [Fact]
    public async Task Entra_federated_assertion_uses_fixed_tenant_client_and_only_ADO_audience()
    {
        using var f = new CredentialFixture(PipelineRequestFactory.AzureDevOps); var assertions = new Assertions();
        var handler = new TokenHandler("{\"token_type\":\"Bearer\",\"expires_in\":3600,\"access_token\":\"" + CredentialFixture.Token + "\"}", 200);
        using var http = CredentialTokenHttpClient.ForMockHandler(handler);
        var source = new CredentialEntraFederatedSource(f.Scope, new(Assertions.Tenant, Assertions.Client), f.Vault, assertions, http, f.Clock);
        var result = await source.AcquireAndStoreAsync();
        Assert.True(result.Mock); Assert.False(result.ProviderPermissionsVerified); Assert.Equal(1, handler.Calls);
        Assert.Equal($"https://login.microsoftonline.com/{Assertions.Tenant}/oauth2/v2.0/token", handler.Uri!.AbsoluteUri);
        var fields = handler.Body!.Split('&').Select(p => p.Split('=', 2)).ToDictionary(p => Uri.UnescapeDataString(p[0]), p => Uri.UnescapeDataString(p[1]));
        Assert.Equal(Assertions.Client, fields["client_id"]); Assert.Equal("client_credentials", fields["grant_type"]);
        Assert.Equal("499b84ac-1321-427f-aa17-267ca6975798/.default", fields["scope"]);
        Assert.Equal("urn:ietf:params:oauth:client-assertion-type:jwt-bearer", fields["client_assertion_type"]);
        Assert.Equal(Assertions.Value, fields["client_assertion"]); Assert.Null(handler.Authorization);
        Assert.DoesNotContain("client_secret", handler.Body); Assert.DoesNotContain("password", handler.Body);
        Assert.Equal(f.Clock.Now.AddHours(1), result.Profile.ExpiresUtc);
        using var lease = await f.Vault.ResolveAsync(f.Scope); Assert.Equal(1, lease.Profile.Generation);
    }

    [Theory]
    [InlineData("token_type")]
    [InlineData("expired")]
    [InlineData("long_lifetime")]
    [InlineData("token")]
    [InlineData("duplicate")]
    [InlineData("nonjson")]
    public async Task Entra_invalid_responses_do_not_persist_or_expose_credentials(string fault)
    {
        using var f = new CredentialFixture(PipelineRequestFactory.AzureDevOps); var body = new JsonObject
        { ["token_type"] = "Bearer", ["expires_in"] = 3600, ["access_token"] = CredentialFixture.Token };
        switch (fault)
        {
            case "token_type": body["token_type"] = "Basic"; break;
            case "expired": body["expires_in"] = 0; break;
            case "long_lifetime": body["expires_in"] = 86401; break;
            case "token": body["access_token"] = "token with space"; break;
        }
        var json = body.ToJsonString(); if (fault == "duplicate") json = json.Replace("\"expires_in\":", "\"Expires_In\":100,\"expires_in\":", StringComparison.Ordinal);
        if (fault == "nonjson") json = CredentialFixture.Token;
        using var http = CredentialTokenHttpClient.ForMockHandler(new TokenHandler(json, 200));
        var source = new CredentialEntraFederatedSource(f.Scope, new(Assertions.Tenant, Assertions.Client), f.Vault, new Assertions(), http, f.Clock);
        var error = await Assert.ThrowsAsync<CredentialException>(() => source.AcquireAndStoreAsync());
        Assert.DoesNotContain(CredentialFixture.Token, error.ToString()); Assert.Empty(Directory.GetFiles(f.VaultRoot, "*.credential.json"));
    }

    [Theory]
    [InlineData(302)]
    [InlineData(401)]
    [InlineData(403)]
    [InlineData(500)]
    public async Task Credential_endpoint_errors_do_not_follow_redirects_or_echo_bodies(int status)
    {
        using var f = new CredentialFixture(); using var keys = new AppKeys(); var handler = new TokenHandler(CredentialFixture.Token, status);
        using var http = CredentialTokenHttpClient.ForMockHandler(handler);
        var source = new CredentialGitHubInstallationSource(f.Scope, f.Connection, new("Iv1.synthetic-client", 7, 77), f.Vault, keys, http, f.Clock);
        var error = await Assert.ThrowsAsync<CredentialException>(() => source.AcquireAndStoreAsync());
        Assert.DoesNotContain(CredentialFixture.Token, error.ToString()); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Foreign_connection_options_and_private_key_provider_failures_are_rejected()
    {
        using var f = new CredentialFixture(); using var keys = new AppKeys { Fail = true };
        using var http = CredentialTokenHttpClient.ForMockHandler(new TokenHandler(GitHubResponse(f)));
        Assert.Throws<CredentialException>(() => new CredentialGitHubInstallationSource(f.Scope, f.Connection with { Repository = "foreign" }, new("Iv1.synthetic-client", 7, 77), f.Vault, keys, http, f.Clock));
        var source = new CredentialGitHubInstallationSource(f.Scope, f.Connection, new("Iv1.synthetic-client", 7, 77), f.Vault, keys, http, f.Clock);
        var error = await Assert.ThrowsAsync<CredentialException>(() => source.AcquireAndStoreAsync()); Assert.DoesNotContain(CredentialFixture.Token, error.ToString());
    }

    static string GitHubResponse(CredentialFixture f) => JsonSerializer.Serialize(new
    { token = CredentialFixture.Token, expires_at = f.Clock.Now.AddMinutes(50).ToString("O"), permissions = new { actions = "write", metadata = "read" }, repositories = new[] { new { id = 77, full_name = "customer-org/customer-repo" } } });

    sealed class AppKeys : ICredentialGitHubAppKeyProvider, IDisposable
    {
        public RSA Rsa { get; } = RSA.Create(2048);
        public int Calls { get; private set; }
        public bool Fail { get; init; }
        public ValueTask<CredentialSecret> GetPrivateKeyAsync(CredentialScope scope, CancellationToken cancellationToken)
        {
            Calls++; cancellationToken.ThrowIfCancellationRequested();
            if (Fail) throw new InvalidOperationException(CredentialFixture.Token);
            // A hostile adapter cannot mutate the broker's frozen permission array.
            scope.RequiredScopes[0] = "contents:read";
            return ValueTask.FromResult(CredentialSecret.FromUtf8(Rsa.ExportPkcs8PrivateKeyPem()));
        }
        public void Dispose() => Rsa.Dispose();
    }
    sealed class Assertions : ICredentialEntraAssertionProvider
    {
        public const string Tenant = "11111111-1111-1111-1111-111111111111", Client = "33333333-3333-3333-3333-333333333333";
        public const string Value = "synthetic-federated-header.synthetic-federated-claims.synthetic-signature";
        public int Calls { get; private set; }
        public ValueTask<CredentialSecret> GetAssertionAsync(CredentialScope scope, string tenantId, string clientId, CancellationToken cancellationToken)
        {
            Calls++; Assert.Equal(Tenant, tenantId); Assert.Equal(Client, clientId); scope.RequiredScopes[0] = "https://graph.microsoft.com/.default";
            return ValueTask.FromResult(CredentialSecret.FromUtf8(Value));
        }
    }
    sealed class TokenHandler(string body, int status = 201) : HttpMessageHandler
    {
        public int Calls { get; private set; }
        public Uri? Uri { get; private set; }
        public string? Body { get; private set; }
        public string? Authorization { get; private set; }
        public string? ApiVersion { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Uri = request.RequestUri; Body = await request.Content!.ReadAsStringAsync(cancellationToken); Authorization = request.Headers.Authorization?.ToString();
            ApiVersion = request.Headers.TryGetValues("X-GitHub-Api-Version", out var versions) ? Assert.Single(versions) : null;
            var response = new HttpResponseMessage((HttpStatusCode)status) { Content = new StringContent(body) };
            if (status == 302) response.Headers.Location = new Uri("https://attacker.invalid/"); return response;
        }
    }
}
