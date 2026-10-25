using System.Net;
using System.Text;

namespace SelfService.Foundation;

// Explicitly opt-in fixture transport. It never targets public DNS/IPs and never sends credentials.
// Production service credentials/live dispatch require a separately reviewed transport capability.
public sealed class PipelineLoopbackTransport : IPipelineTransport, IDisposable
{
    readonly Uri origin;
    readonly HttpClient http;
    public PipelineLoopbackTransport(Uri fixtureOrigin)
    {
        if (fixtureOrigin is null || !fixtureOrigin.IsAbsoluteUri || fixtureOrigin.Scheme != "http" ||
            !IPAddress.TryParse(fixtureOrigin.DnsSafeHost, out var address) || !IPAddress.IsLoopback(address) ||
            fixtureOrigin.Port < 1024 || fixtureOrigin.AbsolutePath != "/" || fixtureOrigin.UserInfo.Length != 0 ||
            fixtureOrigin.Query.Length != 0 || fixtureOrigin.Fragment.Length != 0)
            throw new PipelineContractException("pipeline-fixture-origin-invalid");
        origin = fixtureOrigin;
        http = new HttpClient(new SocketsHttpHandler { AllowAutoRedirect = false, UseCookies = false, UseProxy = false })
        { Timeout = TimeSpan.FromSeconds(15) };
    }
    public async Task<PipelineWireResponse> SendAsync(PipelineWireRequest wire, CancellationToken cancellationToken)
    {
        if (wire.Method != "POST" || wire.Uri.Scheme != "https" || wire.Uri.UserInfo.Length != 0 ||
            !wire.Uri.IsDefaultPort || wire.Uri.Fragment.Length != 0 || wire.Uri.Host is not ("api.github.com" or "dev.azure.com"))
            throw new PipelineContractException("pipeline-fixture-upstream-invalid");
        var target = new Uri(origin, wire.Uri.PathAndQuery);
        if (target.Scheme != origin.Scheme || target.Host != origin.Host || target.Port != origin.Port)
            throw new PipelineContractException("pipeline-fixture-target-invalid");
        using var request = new HttpRequestMessage(HttpMethod.Post, target);
        request.Content = new StringContent(wire.BodyJson, Encoding.UTF8, "application/json");
        foreach (var header in wire.Headers)
        {
            if (header.Key is not ("Accept" or "X-GitHub-Api-Version" or "User-Agent"))
                throw new PipelineContractException("pipeline-fixture-header-invalid");
            request.Headers.TryAddWithoutValidation(header.Key, header.Value);
        }
        request.Headers.Add("X-Platform-Studio-Mock", "true");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        if (response.Content.Headers.ContentLength > 262144) return new((int)response.StatusCode, "");
        using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var bytes = new MemoryStream();
        var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, cancellationToken)) != 0)
        {
            if (bytes.Length + read > 262144) return new((int)response.StatusCode, "");
            bytes.Write(buffer, 0, read);
        }
        return new((int)response.StatusCode, Encoding.UTF8.GetString(bytes.ToArray()));
    }
    public void Dispose() => http.Dispose();
}
