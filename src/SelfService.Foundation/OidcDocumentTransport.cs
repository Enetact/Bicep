namespace SelfService.Foundation;

public sealed record OidcDocumentResponse(int StatusCode, byte[] Utf8Json);

public interface IOidcDocumentTransport
{
    Task<OidcDocumentResponse> GetAsync(Uri uri, CancellationToken cancellationToken);
}

public sealed class DeniedOidcDocumentTransport : IOidcDocumentTransport
{
    public Task<OidcDocumentResponse> GetAsync(Uri uri, CancellationToken cancellationToken) =>
        throw new FoundationAuthorizationException("oidc_discovery_disabled", 503);
}

/// <summary>Explicit trusted outbound transport. It retrieves public discovery documents, never credentials.</summary>
public sealed class EntraOidcHttpsTransport : IOidcDocumentTransport, IDisposable
{
    readonly HttpClient client;
    readonly HashSet<string> addresses;

    public EntraOidcHttpsTransport(IReadOnlyList<OidcTenantAuthority> authorities) : this(authorities,
        new SocketsHttpHandler
        {
            AllowAutoRedirect = false, UseCookies = false, UseProxy = false,
            ConnectTimeout = TimeSpan.FromSeconds(5), MaxConnectionsPerServer = 4,
            MaxResponseHeadersLength = 16, AutomaticDecompression = System.Net.DecompressionMethods.None
        }) { }

    // Handler injection is for trusted composition/testing, not caller-controlled request input.
    public EntraOidcHttpsTransport(IReadOnlyList<OidcTenantAuthority> authorities, HttpMessageHandler handler)
    {
        ArgumentNullException.ThrowIfNull(authorities); ArgumentNullException.ThrowIfNull(handler);
        if (authorities.Count is 0 or > 32) throw new ArgumentException("Invalid OIDC authority list.");
        addresses = authorities.Select(OidcDocuments.Authority).SelectMany(a => new[] { a.MetadataUri.AbsoluteUri, a.JwksUri.AbsoluteUri })
            .ToHashSet(StringComparer.Ordinal);
        client = new HttpClient(handler, disposeHandler: true) { Timeout = TimeSpan.FromSeconds(15) };
    }

    public async Task<OidcDocumentResponse> GetAsync(Uri uri, CancellationToken cancellationToken)
    {
        if (uri is null || !uri.IsAbsoluteUri || !addresses.Contains(uri.OriginalString))
            throw new FoundationAuthorizationException("oidc_document_origin_denied", 503);
        using var request = new HttpRequestMessage(HttpMethod.Get, uri);
        request.Headers.Accept.ParseAdd("application/json");
        request.Headers.UserAgent.ParseAdd("PlatformStudio-Foundation/1.0");
        using var response = await client.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.StatusCode != System.Net.HttpStatusCode.OK || response.RequestMessage?.RequestUri?.AbsoluteUri != uri.AbsoluteUri ||
            response.Headers.Location is not null || response.Content.Headers.ContentLength > OidcDocuments.MaximumDocumentBytes ||
            response.Content.Headers.ContentEncoding.Count != 0)
            throw new FoundationAuthorizationException("oidc_document_response_denied", 503);
        var contentType = response.Content.Headers.ContentType?.MediaType;
        if (contentType is not ("application/json" or "application/jwk-set+json"))
            throw new FoundationAuthorizationException("oidc_document_response_denied", 503);
        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var output = new MemoryStream(); var buffer = new byte[8192]; int count;
        while ((count = await stream.ReadAsync(buffer, cancellationToken)) > 0)
        {
            if (output.Length + count > OidcDocuments.MaximumDocumentBytes)
                throw new FoundationAuthorizationException("oidc_document_response_denied", 503);
            output.Write(buffer, 0, count);
        }
        return new((int)response.StatusCode, output.ToArray());
    }
    public void Dispose() => client.Dispose();
}
