using System.Net;
using System.Net.Http.Headers;
using System.Text;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class OidcDocumentTransportTests
{
    static readonly OidcTenantAuthority Authority = OidcTenantAuthority.PublicEntra(IdentityTestIssuer.Tenant);

    [Fact]
    public async Task Transport_fetches_only_configured_public_document_without_credentials_or_token_payload()
    {
        using var handler = new CaptureHandler(); using var transport = new EntraOidcHttpsTransport([Authority], handler);
        var response = await transport.GetAsync(Authority.MetadataUri, default);
        Assert.Equal(200, response.StatusCode); Assert.Equal("{}", Encoding.UTF8.GetString(response.Utf8Json));
        Assert.Equal(1, handler.Calls); Assert.Equal(HttpMethod.Get, handler.Method); Assert.Null(handler.Body);
        Assert.Null(handler.Authorization); Assert.Null(handler.Cookie); Assert.Equal(Authority.MetadataUri, handler.Uri);
        Assert.Equal("application/json", handler.Accept);
    }

    [Theory]
    [InlineData("https://attacker.invalid/keys")]
    [InlineData("http://login.microsoftonline.com/common/discovery/v2.0/keys")]
    [InlineData("https://login.microsoftonline.com/common/discovery/v2.0/keys?appid=unreviewed")]
    [InlineData("https://login.microsoftonline.com/common/discovery/v2.0/keys#fragment")]
    [InlineData("https://user@login.microsoftonline.com/common/discovery/v2.0/keys")]
    [InlineData("https://login.microsoftonline.com:8443/common/discovery/v2.0/keys")]
    [InlineData("http://127.0.0.1/keys")]
    public async Task Unapproved_origins_and_URI_mutations_never_reach_HTTP_handler(string uri)
    {
        using var handler = new CaptureHandler(); using var transport = new EntraOidcHttpsTransport([Authority], handler);
        await Assert.ThrowsAsync<FoundationAuthorizationException>(() => transport.GetAsync(new(uri), default)); Assert.Equal(0, handler.Calls);
    }

    [Theory]
    [InlineData("redirect")]
    [InlineData("retarget")]
    [InlineData("location")]
    [InlineData("error")]
    [InlineData("content-type")]
    [InlineData("encoding")]
    [InlineData("length")]
    [InlineData("stream-length")]
    public async Task Redirects_retargeted_or_unbounded_documents_are_rejected_without_followup_requests(string fault)
    {
        using var handler = new CaptureHandler { Fault = fault }; using var transport = new EntraOidcHttpsTransport([Authority], handler);
        await Assert.ThrowsAsync<FoundationAuthorizationException>(() => transport.GetAsync(Authority.MetadataUri, default)); Assert.Equal(1, handler.Calls);
    }

    [Fact]
    public async Task Caller_cancellation_is_not_converted_to_a_successful_document()
    {
        using var handler = new CaptureHandler { Fault = "cancel" }; using var transport = new EntraOidcHttpsTransport([Authority], handler);
        using var cancelled = new CancellationTokenSource(TimeSpan.FromMilliseconds(20));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => transport.GetAsync(Authority.MetadataUri, cancelled.Token));
        Assert.Equal(1, handler.Calls);
    }

    sealed class CaptureHandler : HttpMessageHandler
    {
        public int Calls; public string? Fault, Body, Authorization, Cookie, Accept;
        public HttpMethod? Method; public Uri? Uri;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Calls++; Method = request.Method; Uri = request.RequestUri; Authorization = request.Headers.Authorization?.ToString();
            Cookie = request.Headers.TryGetValues("Cookie", out var values) ? string.Join(",", values) : null;
            Accept = request.Headers.Accept.Single().MediaType; Body = request.Content is null ? null : await request.Content.ReadAsStringAsync(cancellationToken);
            if (Fault == "cancel") await Task.Delay(5000, cancellationToken);
            var response = new HttpResponseMessage(Fault == "redirect" ? HttpStatusCode.Redirect : Fault == "error" ? HttpStatusCode.ServiceUnavailable : HttpStatusCode.OK)
                { RequestMessage = request, Content = new StringContent("{}", Encoding.UTF8, "application/json") };
            if (Fault == "retarget") response.RequestMessage = new HttpRequestMessage(HttpMethod.Get, "https://attacker.invalid/keys");
            if (Fault is "redirect" or "location") response.Headers.Location = new("https://attacker.invalid/keys");
            if (Fault == "content-type") response.Content.Headers.ContentType = new MediaTypeHeaderValue("text/html");
            if (Fault == "encoding") response.Content.Headers.ContentEncoding.Add("gzip");
            if (Fault == "length") response.Content.Headers.ContentLength = 262145;
            if (Fault == "stream-length") response.Content = new StreamContent(new NonSeekableStream(new byte[262145]))
                { Headers = { ContentType = new MediaTypeHeaderValue("application/json") } };
            return response;
        }
    }

    sealed class NonSeekableStream(byte[] bytes) : Stream
    {
        readonly MemoryStream inner = new(bytes);
        public override bool CanRead => true;
        public override bool CanSeek => false;
        public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException();
        public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
        public override int Read(byte[] buffer, int offset, int count) => inner.Read(buffer, offset, count);
        public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) => inner.ReadAsync(buffer, cancellationToken);
        public override void Flush() { }
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        protected override void Dispose(bool disposing) { if (disposing) inner.Dispose(); base.Dispose(disposing); }
    }
}
