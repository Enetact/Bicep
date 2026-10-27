using System.Net;
using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SelfService.Foundation.Contracts;

namespace SelfService.Foundation.Client;

public sealed record FoundationReadClientOptions(Uri ServiceUri, FoundationServiceScope Scope)
{
    public TimeSpan RequestTimeout { get; init; } = TimeSpan.FromSeconds(15);
    public int MaximumJsonBytes { get; init; } = 2 * 1024 * 1024;
    public int MaximumArtifactBytes { get; init; } = 1024 * 1024;
}

public sealed class FoundationReadException(string code, int? statusCode = null)
    : Exception("Foundation service read failed: " + code)
{
    public string Code { get; } = code;
    public int? StatusCode { get; } = statusCode;
}

/// <summary>A fixed-cell, read-only client. The caller supplies a Foundation API access token
/// for each request; the destination service independently validates identity and scope.
/// This client neither stores tokens nor grants execution or recovery authority.</summary>
public sealed class FoundationReadClient : IDisposable
{
    static readonly Regex ScopeId = new("\\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\\z", RegexOptions.CultureInvariant);
    static readonly Regex ObjectId = new("\\A[A-Za-z0-9][A-Za-z0-9._:-]{0,255}\\z", RegexOptions.CultureInvariant);
    static readonly Regex Digest = new("\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 16, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    };
    readonly HttpClient http;
    readonly FoundationReadClientOptions options;
    readonly string root;
    bool disposed;

    public FoundationReadClient(FoundationReadClientOptions options) : this(Validate(options), null) { }

    // Only trusted local tests can replace the transport. Production owns a redirect/cookie-free handler.
    internal FoundationReadClient(FoundationReadClientOptions options, HttpMessageHandler? testHandler)
    {
        this.options = Validate(options);
        http = new HttpClient(testHandler ?? new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            AutomaticDecompression = DecompressionMethods.None,
            ConnectTimeout = options.RequestTimeout, PooledConnectionLifetime = TimeSpan.FromMinutes(5),
            MaxResponseHeadersLength = 32
        }, disposeHandler: true) { BaseAddress = options.ServiceUri, Timeout = Timeout.InfiniteTimeSpan };
        var scope = options.Scope;
        root = "api/foundation/customers/" + Uri.EscapeDataString(scope.CustomerId) + "/projects/" +
            Uri.EscapeDataString(scope.ProjectId) + "/environments/" + Uri.EscapeDataString(scope.EnvironmentId);
    }

    public async Task<FoundationJobReadResult> GetJobAsync(string jobId, string foundationApiAccessToken,
        CancellationToken cancellationToken = default)
    {
        RequireId(jobId);
        var job = Parse<FoundationJobReadResult>(await ReadAsync("/jobs/" + Uri.EscapeDataString(jobId),
            foundationApiAccessToken, options.MaximumJsonBytes, json: true, cancellationToken));
        var scope = options.Scope;
        if (job.Id != jobId || job.CustomerId != scope.CustomerId || job.CellId != scope.CellId ||
            job.ProjectId != scope.ProjectId || job.EnvironmentId != scope.EnvironmentId ||
            !ValidObjectId(job.PlanId) || string.IsNullOrWhiteSpace(job.OperatorKey) || job.OperatorVersion <= 0 ||
            job.Fence < 0 || job.Attempt < 0 || job.CreatedAt == default || job.UpdatedAt < job.CreatedAt ||
            job.State is not ("Queued" or "Claimed" or "Dispatched" or "Succeeded" or "Failed" or "Blocked" or "Cancelled" or
                "OutcomeUnknown" or "ReconciledSucceeded" or "ReconciledNoEffect"))
            throw new FoundationReadException("incompatible-response");
        return job;
    }

    public async Task<FoundationArtifactReadResult> GetArtifactAsync(string artifactId, string foundationApiAccessToken,
        CancellationToken cancellationToken = default)
    {
        RequireId(artifactId);
        var artifact = Parse<FoundationArtifactReadResult>(await ReadAsync("/artifacts/" + Uri.EscapeDataString(artifactId),
            foundationApiAccessToken, options.MaximumJsonBytes, json: true, cancellationToken));
        var scope = options.Scope;
        if (artifact.Id != artifactId || artifact.CustomerId != scope.CustomerId || artifact.ProjectId != scope.ProjectId ||
            artifact.EnvironmentId != scope.EnvironmentId || artifact.CreatedAt == default || artifact.Content is null ||
            artifact.Content.Length > options.MaximumArtifactBytes || artifact.Name is null ||
            !Regex.IsMatch(artifact.Name, "\\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\\z", RegexOptions.CultureInvariant) ||
            !ValidDigest(artifact.Sha256)) throw new FoundationReadException("incompatible-response");
        RequireDigest(artifact.Content, artifact.Sha256);
        return artifact;
    }

    // The current API returns bytes with metadata. This projection verifies them before discarding them.
    public async Task<FoundationArtifactReadMetadata> GetArtifactMetadataAsync(string artifactId, string foundationApiAccessToken,
        CancellationToken cancellationToken = default)
    {
        var artifact = await GetArtifactAsync(artifactId, foundationApiAccessToken, cancellationToken);
        return new(artifact.Id, artifact.CustomerId, options.Scope.CellId, artifact.ProjectId, artifact.EnvironmentId,
            artifact.Name, artifact.Sha256, artifact.Content.Length, artifact.CreatedAt);
    }

    public async Task<byte[]> GetArtifactContentAsync(string artifactId, string expectedSha256,
        string foundationApiAccessToken, CancellationToken cancellationToken = default)
    {
        RequireId(artifactId);
        if (!ValidDigest(expectedSha256)) throw new ArgumentException("An exact artifact SHA-256 is required.", nameof(expectedSha256));
        var bytes = await ReadAsync("/artifacts/" + Uri.EscapeDataString(artifactId) + "/content", foundationApiAccessToken,
            options.MaximumArtifactBytes, json: false, cancellationToken);
        RequireDigest(bytes, expectedSha256); return bytes;
    }

    async Task<byte[]> ReadAsync(string suffix, string token, int maximumBytes, bool json, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(disposed, this); cancellationToken.ThrowIfCancellationRequested();
        if (string.IsNullOrEmpty(token) || token.Length > 32768 || token.Any(c => c > 126 || c <= 32))
            throw new ArgumentException("A Foundation API bearer token is required.", nameof(token));
        using var deadline = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        deadline.CancelAfter(options.RequestTimeout);
        try
        {
            using var request = new HttpRequestMessage(HttpMethod.Get, root + suffix);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token);
            request.Headers.Accept.Add(new MediaTypeWithQualityHeaderValue(json ? "application/json" : "application/octet-stream"));
            using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, deadline.Token);
            if (response.StatusCode != HttpStatusCode.OK) throw StatusFailure(response.StatusCode);
            if (!OneHeader(response, FoundationServiceProtocol.CellHeader, options.Scope.CellId) ||
                !OneHeader(response, FoundationServiceProtocol.ContractHeader, FoundationServiceProtocol.ContractVersion))
                throw new FoundationReadException("incompatible-response");
            var type = response.Content.Headers.ContentType?.MediaType;
            if (json ? type != "application/json" : type is not ("application/octet-stream" or "application/zip"))
                throw new FoundationReadException("incompatible-response");
            if (response.Content.Headers.ContentEncoding.Any()) throw new FoundationReadException("incompatible-response");
            if (response.Content.Headers.ContentLength > maximumBytes) throw new FoundationReadException("response-too-large");
            await using var input = await response.Content.ReadAsStreamAsync(deadline.Token);
            using var output = new MemoryStream(); var buffer = new byte[8192]; int read;
            while ((read = await input.ReadAsync(buffer, deadline.Token)) != 0)
            {
                if (output.Length + read > maximumBytes) throw new FoundationReadException("response-too-large");
                await output.WriteAsync(buffer.AsMemory(0, read), deadline.Token);
            }
            deadline.Token.ThrowIfCancellationRequested(); return output.ToArray();
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Foundation service read canceled.", cancellationToken); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        { throw new FoundationReadException("deadline-exceeded"); }
        catch (Exception e) when (e is HttpRequestException or IOException)
        { throw new FoundationReadException("service-unavailable"); }
    }

    static T Parse<T>(byte[] bytes)
    {
        try
        {
            using var document = JsonDocument.Parse(bytes, new JsonDocumentOptions { MaxDepth = 16 });
            Unique(document.RootElement);
            return document.RootElement.Deserialize<T>(Json) ?? throw new JsonException();
        }
        catch (Exception e) when (e is JsonException or NotSupportedException or FormatException)
        { throw new FoundationReadException("incompatible-response"); }
    }
    static void Unique(JsonElement element)
    {
        if (element.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in element.EnumerateObject()) { if (!names.Add(property.Name)) throw new JsonException(); Unique(property.Value); }
        }
        else if (element.ValueKind == JsonValueKind.Array) foreach (var value in element.EnumerateArray()) Unique(value);
    }
    static bool OneHeader(HttpResponseMessage response, string name, string expected) =>
        response.Headers.TryGetValues(name, out var values) && values.Take(2).ToArray() is [var value] && value == expected;
    static void RequireDigest(byte[] bytes, string digest)
    {
        if (Convert.ToHexStringLower(SHA256.HashData(bytes)) != digest) throw new FoundationReadException("artifact-integrity-failed");
    }
    static bool ValidDigest(string? value) => value is not null && Digest.IsMatch(value);
    static bool ValidObjectId(string? value) => value is not null && ObjectId.IsMatch(value);
    static void RequireId(string id) { if (!ValidObjectId(id)) throw new ArgumentException("An exact object identifier is required.", nameof(id)); }
    static FoundationReadException StatusFailure(HttpStatusCode status) => new((int)status switch
    {
        401 => "authentication-required", 403 => "access-denied", 404 => "not-found", 429 => "service-throttled",
        >= 300 and < 400 => "redirect-refused", >= 500 => "service-unavailable", _ => "service-error"
    }, (int)status);
    static FoundationReadClientOptions Validate(FoundationReadClientOptions value)
    {
        if (value is null || value.ServiceUri is null || !value.ServiceUri.IsAbsoluteUri || value.Scope is null ||
            value.ServiceUri.UserInfo.Length != 0 || value.ServiceUri.Query.Length != 0 || value.ServiceUri.Fragment.Length != 0 ||
            value.ServiceUri.AbsolutePath != "/" || value.ServiceUri.Scheme is not ("http" or "https") ||
            value.ServiceUri.Scheme == "http" && value.ServiceUri.Host != "localhost" &&
                (!IPAddress.TryParse(value.ServiceUri.Host.Trim('[', ']'), out var address) || !IPAddress.IsLoopback(address)) ||
            new[] { value.Scope.CustomerId, value.Scope.CellId, value.Scope.ProjectId, value.Scope.EnvironmentId }.Any(id => id is null || !ScopeId.IsMatch(id)) ||
            value.RequestTimeout < TimeSpan.FromMilliseconds(10) || value.RequestTimeout > TimeSpan.FromMinutes(1) ||
            value.MaximumJsonBytes is < 128 or > 16 * 1024 * 1024 || value.MaximumArtifactBytes is < 1 or > 16 * 1024 * 1024)
            throw new ArgumentException("Invalid Foundation read client configuration.", nameof(value));
        return value;
    }
    public void Dispose() { if (disposed) return; disposed = true; http.Dispose(); }
}
