using System.Globalization;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Foundation;

public sealed record CredentialAcquisitionResult(CredentialProfile Profile, bool Mock, bool ProviderPermissionsVerified = false);
public sealed record CredentialGitHubAppOptions(string ClientId, long InstallationId, long RepositoryId);
public sealed record CredentialEntraFederatedOptions(string TenantId, string ClientId);
public interface ICredentialGitHubAppKeyProvider
{
    ValueTask<CredentialSecret> GetPrivateKeyAsync(CredentialScope scope, CancellationToken cancellationToken);
}
public interface ICredentialEntraAssertionProvider
{
    // The external workload identity supplies an assertion for its reviewed Entra federated credential.
    ValueTask<CredentialSecret> GetAssertionAsync(CredentialScope scope, string tenantId, string clientId, CancellationToken cancellationToken);
}

// No default credential, browser cache, environment-token lookup or interactive login exists here.
public sealed class CredentialTokenHttpClient : IDisposable
{
    readonly HttpClient? http;
    readonly bool enabled;
    readonly TimeSpan operationTimeout;
    public bool IsMock { get; }
    internal bool Enabled => enabled;
    public CredentialTokenHttpClient(bool enableExternalRequests = false, TimeSpan? operationTimeout = null)
    {
        enabled = enableExternalRequests;
        this.operationTimeout = CredentialRules.OperationTimeout(operationTimeout, TimeSpan.FromSeconds(15));
        if (enabled) http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    CredentialTokenHttpClient(HttpMessageHandler handler, TimeSpan? operationTimeout)
    {
        enabled = true; IsMock = true; this.operationTimeout = CredentialRules.OperationTimeout(operationTimeout, TimeSpan.FromSeconds(15));
        http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
    }
    public static CredentialTokenHttpClient ForMockHandler(HttpMessageHandler handler, TimeSpan? operationTimeout = null) => new(handler, operationTimeout);
    internal CancellationTokenSource BeginOperation(CancellationToken cancellationToken) => CredentialRules.OperationDeadline(operationTimeout, cancellationToken);
    internal async Task<CredentialSecret> Post(Uri uri, CredentialSecret? bearer, string? json,
        IReadOnlyDictionary<string, string>? form, int expectedStatus, CancellationToken cancellationToken)
    {
        if (!enabled || http is null) throw new CredentialException("credential-token-network-disabled");
        if (uri.Scheme != "https" || !uri.IsDefaultPort || uri.UserInfo.Length != 0 || uri.Query.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Host == "api.github.com" && Regex.IsMatch(uri.AbsolutePath, "\\A/app/installations/[1-9][0-9]*/access_tokens\\z") ||
              uri.Host == "login.microsoftonline.com" && Regex.IsMatch(uri.AbsolutePath, "\\A/[a-f0-9-]{36}/oauth2/v2.0/token\\z")))
            throw new CredentialException("credential-token-endpoint-invalid");
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Post, uri);
            request.Content = form is not null ? new FormUrlEncodedContent(form) : new StringContent(json!, Encoding.UTF8, "application/json");
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue("application/json"));
            request.Headers.UserAgent.ParseAdd("PlatformStudio-CredentialBroker/1.0");
            if (bearer is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", bearer.Text());
            if (uri.Host == "api.github.com") request.Headers.Add("X-GitHub-Api-Version", PipelineRequestFactory.GitHubApiVersion);
            cancellationToken.ThrowIfCancellationRequested();
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            if ((int)response.StatusCode != expectedStatus) throw new CredentialException("credential-token-acquisition-rejected");
            if (response.Content.Headers.ContentLength > 32768) throw new CredentialException("credential-token-response-invalid");
            await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken); using var bytes = new MemoryStream();
            var buffer = new byte[4096]; int read;
            while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
            { if (bytes.Length + read > 32768) throw new CredentialException("credential-token-response-invalid"); bytes.Write(buffer, 0, read); }
            return CredentialSecret.FromUtf8(Encoding.UTF8.GetString(bytes.ToArray()));
        }
        catch (CredentialException) { throw; }
        catch (OperationCanceledException) { throw new OperationCanceledException("Credential token request interrupted.", cancellationToken); }
        catch (Exception) { throw new CredentialException("credential-token-transport-unavailable"); }
    }
    public void Dispose() => http?.Dispose();
}

public sealed class CredentialGitHubInstallationSource
{
    readonly CredentialScope scope;
    readonly CredentialGitHubAppOptions options;
    readonly CredentialVault vault;
    readonly ICredentialGitHubAppKeyProvider keys;
    readonly CredentialTokenHttpClient http;
    readonly TimeProvider clock;
    readonly string fullName;
    public CredentialGitHubInstallationSource(CredentialScope scope, PipelineConnection connection, CredentialGitHubAppOptions options,
        CredentialVault vault, ICredentialGitHubAppKeyProvider keys, CredentialTokenHttpClient http, TimeProvider? clock = null)
    {
        this.scope = CredentialRules.Snapshot(scope);
        if (scope.Provider != PipelineRequestFactory.GitHub || options.InstallationId <= 0 || options.RepositoryId <= 0 ||
            !Regex.IsMatch(options.ClientId ?? "", "\\A[A-Za-z0-9_.-]{1,128}\\z") || PipelineRequestFactory.ConnectionDigest(connection) != scope.ConnectionDigest ||
            connection.CustomerId != scope.CustomerId || connection.CellId != scope.CellId || connection.Id != scope.ConnectionId || connection.Generation != scope.ConnectionGeneration ||
            connection.CredentialReference != scope.AuthProfileId)
            throw new CredentialException("credential-github-source-binding-invalid");
        this.options = options; this.vault = vault; this.keys = keys; this.http = http; this.clock = clock ?? TimeProvider.System;
        fullName = connection.Organization + "/" + connection.Repository;
    }
    public async Task<CredentialAcquisitionResult> AcquireAndStoreAsync(long expectedProfileGeneration = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        vault.RequireGenericCredentialSourceUse("credential-bound-dispatch-acquisition-unsupported");
        if (!http.Enabled) throw new CredentialException("credential-token-network-disabled");
        using var deadline = http.BeginOperation(cancellationToken);
        try { return await AcquireCore(expectedProfileGeneration, deadline.Token); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Credential token acquisition interrupted.", cancellationToken); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { throw new CredentialException("credential-token-acquisition-timeout"); }
        catch (OperationCanceledException) { throw new OperationCanceledException("Credential token acquisition interrupted.", cancellationToken); }
    }
    async Task<CredentialAcquisitionResult> AcquireCore(long expectedProfileGeneration, CancellationToken cancellationToken)
    {
        using var assertion = await AppAssertion(cancellationToken);
        var permissions = new Dictionary<string, string> { ["actions"] = "write" };
        if (scope.RequiredScopes.Contains("contents:read", StringComparer.Ordinal)) permissions["contents"] = "read";
        var body = JsonSerializer.Serialize(new { repository_ids = new[] { options.RepositoryId }, permissions }, CredentialRules.Json);
        using var response = await http.Post(new($"https://api.github.com/app/installations/{options.InstallationId}/access_tokens"), assertion, body, null, 201, cancellationToken);
        try
        {
            using var doc = CredentialRules.Document(Encoding.UTF8.GetBytes(response.Text())); var root = doc.RootElement;
            var granted = root.GetProperty("permissions");
            if (granted.GetProperty("actions").GetString() != "write" ||
                granted.EnumerateObject().Any(p => p.Name == "metadata" ? p.Value.GetString() != "read" : !permissions.TryGetValue(p.Name, out var requested) || p.Value.GetString() != requested) ||
                permissions.Any(p => !granted.TryGetProperty(p.Key, out var value) || value.GetString() != p.Value))
                throw new CredentialException("credential-github-permission-response-invalid");
            var repositories = root.GetProperty("repositories").EnumerateArray().ToArray();
            if (repositories.Length != 1 || repositories[0].GetProperty("id").GetInt64() != options.RepositoryId ||
                !string.Equals(repositories[0].GetProperty("full_name").GetString(), fullName, StringComparison.OrdinalIgnoreCase) ||
                !DateTimeOffset.TryParse(root.GetProperty("expires_at").GetString(), CultureInfo.InvariantCulture, DateTimeStyles.AssumeUniversal, out var expires) ||
                expires <= clock.GetUtcNow().AddSeconds(30) || expires > clock.GetUtcNow().AddMinutes(62))
                throw new CredentialException("credential-github-repository-or-lifetime-invalid");
            using var token = CredentialSecret.FromUtf8(root.GetProperty("token").GetString()!);
            var profile = await vault.PutAsync(scope, token, expires, expectedProfileGeneration, cancellationToken);
            return new(profile, http.IsMock);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
        { throw new CredentialException("credential-github-token-response-invalid"); }
    }
    async Task<CredentialSecret> AppAssertion(CancellationToken cancellationToken)
    {
        try
        {
            using var key = await keys.GetPrivateKeyAsync(CredentialRules.Snapshot(scope), cancellationToken); using var rsa = RSA.Create(); rsa.ImportFromPem(key.Text());
            if (rsa.KeySize < 2048) throw new CredentialException("credential-github-key-invalid");
            static string Encode(byte[] value) => Convert.ToBase64String(value).TrimEnd('=').Replace('+', '-').Replace('/', '_');
            var now = clock.GetUtcNow();
            var text = Encode(JsonSerializer.SerializeToUtf8Bytes(new { alg = "RS256", typ = "JWT" })) + "." +
                Encode(JsonSerializer.SerializeToUtf8Bytes(new { iat = now.AddSeconds(-30).ToUnixTimeSeconds(), exp = now.AddMinutes(8).ToUnixTimeSeconds(), iss = options.ClientId }));
            return CredentialSecret.FromUtf8(text + "." + Encode(rsa.SignData(Encoding.ASCII.GetBytes(text), HashAlgorithmName.SHA256, RSASignaturePadding.Pkcs1)));
        }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new CredentialException("credential-github-key-unavailable"); }
    }
}

public sealed class CredentialEntraFederatedSource
{
    readonly CredentialScope scope;
    readonly CredentialEntraFederatedOptions options;
    readonly CredentialVault vault;
    readonly ICredentialEntraAssertionProvider assertions;
    readonly CredentialTokenHttpClient http;
    readonly TimeProvider clock;
    public CredentialEntraFederatedSource(CredentialScope scope, CredentialEntraFederatedOptions options,
        CredentialVault vault, ICredentialEntraAssertionProvider assertions, CredentialTokenHttpClient http, TimeProvider? clock = null)
    {
        this.scope = CredentialRules.Snapshot(scope);
        if (scope.Provider != PipelineRequestFactory.AzureDevOps || !Guid.TryParseExact(options.TenantId, "D", out var tenant) || tenant == Guid.Empty ||
            !Guid.TryParseExact(options.ClientId, "D", out var client) || client == Guid.Empty)
            throw new CredentialException("credential-entra-source-binding-invalid");
        this.options = options with { TenantId = tenant.ToString("D"), ClientId = client.ToString("D") };
        this.vault = vault; this.assertions = assertions; this.http = http; this.clock = clock ?? TimeProvider.System;
    }
    public async Task<CredentialAcquisitionResult> AcquireAndStoreAsync(long expectedProfileGeneration = 0, CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        vault.RequireGenericCredentialSourceUse("credential-bound-dispatch-acquisition-unsupported");
        if (!http.Enabled) throw new CredentialException("credential-token-network-disabled");
        using var deadline = http.BeginOperation(cancellationToken);
        try { return await AcquireCore(expectedProfileGeneration, deadline.Token); }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Credential token acquisition interrupted.", cancellationToken); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { throw new CredentialException("credential-token-acquisition-timeout"); }
        catch (OperationCanceledException) { throw new OperationCanceledException("Credential token acquisition interrupted.", cancellationToken); }
    }
    async Task<CredentialAcquisitionResult> AcquireCore(long expectedProfileGeneration, CancellationToken cancellationToken)
    {
        CredentialSecret assertion;
        try { assertion = await assertions.GetAssertionAsync(CredentialRules.Snapshot(scope), options.TenantId, options.ClientId, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new CredentialException("credential-entra-assertion-unavailable"); }
        using (assertion)
        {
            CredentialRules.Token(assertion.Text());
            var form = new Dictionary<string, string>
            {
                ["client_id"] = options.ClientId, ["scope"] = CredentialRules.AdoAudience + "/.default", ["grant_type"] = "client_credentials",
                ["client_assertion_type"] = "urn:ietf:params:oauth:client-assertion-type:jwt-bearer", ["client_assertion"] = assertion.Text()
            };
            using var response = await http.Post(new($"https://login.microsoftonline.com/{options.TenantId}/oauth2/v2.0/token"), null, null, form, 200, cancellationToken);
            try
            {
                using var doc = CredentialRules.Document(Encoding.UTF8.GetBytes(response.Text())); var root = doc.RootElement;
                if (!string.Equals(root.GetProperty("token_type").GetString(), "Bearer", StringComparison.OrdinalIgnoreCase) ||
                    !root.GetProperty("expires_in").TryGetInt32(out var seconds) || seconds is < 31 or > 86400)
                    throw new CredentialException("credential-entra-token-lifetime-invalid");
                using var token = CredentialSecret.FromUtf8(root.GetProperty("access_token").GetString()!);
                var profile = await vault.PutAsync(scope, token, clock.GetUtcNow().AddSeconds(seconds), expectedProfileGeneration, cancellationToken);
                return new(profile, http.IsMock);
            }
            catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or FormatException or ArgumentException)
            { throw new CredentialException("credential-entra-token-response-invalid"); }
        }
    }
}
