using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class NetworkPipelineTests
{
    sealed class Tokens : ITokenProvider { public Task<string> Token(BrowserSession s, string audience) => Task.FromResult("fixture-only"); }
    sealed class Handler(string fault = "") : HttpMessageHandler
    {
        public int Posts { get; private set; }
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            object result;
            if (r.Method == HttpMethod.Post)
            {
                Posts++;
                var body = await r.Content!.ReadFromJsonAsync<JsonElement>(ct);
                Assert.Equal("Plan only", body.GetProperty("templateParameters").GetProperty("operation").GetString());
                Assert.Equal("refs/heads/main", body.GetProperty("resources").GetProperty("repositories").GetProperty("self").GetProperty("refName").GetString());
                if (fault == "lost-response") throw new HttpRequestException("Synthetic lost response after acceptance.");
                result = new { id = 42 };
            }
            else if (r.RequestUri!.AbsolutePath.EndsWith("/definitions")) result = new { value = new[] { new { id = 5, name = "Network - AVNM allocation" } } };
            else result = new { id = 5, process = new { yamlFilename = fault == "wrong-yaml" ? "another.yml" : "azure-pipelines-network.yml" } };
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(result) };
        }
    }
    static (NetworkPipeline Service, BrowserSession Session, Handler Handler, string Config) Setup(string fault = "")
    {
        var root = new DirectoryInfo(AppContext.BaseDirectory);
        while (root is not null && !File.Exists(Path.Combine(root.FullName, "config/workloads.json"))) root = root.Parent;
        var dir = Path.Combine(root!.FullName, "artifacts/network-queue-tests", Guid.NewGuid().ToString("N"));
        foreach (var folder in new[] { "config", "self-service/targets" }) Directory.CreateDirectory(Path.Combine(dir, folder));
        foreach (var file in Directory.GetFiles(root.FullName, "azure-pipelines-*-deploy.yml")) File.Copy(file, Path.Combine(dir, Path.GetFileName(file)));
        File.Copy(Path.Combine(root.FullName, "config/workloads.json"), Path.Combine(dir, "config/workloads.json"));
        File.Copy(Path.Combine(root.FullName, "self-service/pipeline-settings.json"), Path.Combine(dir, "self-service/pipeline-settings.json"));
        foreach (var file in Directory.GetFiles(Path.Combine(root.FullName, "self-service/targets"), "*.json")) File.Copy(file, Path.Combine(dir, "self-service/targets", Path.GetFileName(file)));
        var config = Path.Combine(dir, "config/network-allocation.json");
        File.WriteAllText(config, """{"profiles":{"avnm-private-web":{"poolId":"fixture-pool","enabled":false,"exclusiveLockReviewed":false,"externalPrefixesReconciled":false}}}""");
        var o = new PortalOptions { RepositoryRoot = dir }; var catalog = new Catalog(o); var handler = new Handler(fault);
        return (new(new(new(handler), new Tokens(), o, catalog), catalog, o), new(o), handler, config);
    }
    static async Task<string> Review(NetworkPipeline service, BrowserSession session) =>
        JsonSerializer.SerializeToElement(await service.Review(session, new("httpapi", "dev", "Plan only"))).GetProperty("ticket").GetString()!;

    [Fact] public async Task ReviewedQueueIsSingleUseAndMainOnly()
    {
        var (service, s, h, _) = Setup(); using (s) { var ticket = await Review(service, s); await service.Queue(s, ticket);
            await Assert.ThrowsAsync<PortalException>(() => service.Queue(s, ticket)); Assert.Equal(1, h.Posts); }
    }
    [Fact] public async Task LostQueueResponseIsNotRetried()
    {
        var (service, s, h, _) = Setup("lost-response"); using (s) { var ticket = await Review(service, s);
            await Assert.ThrowsAsync<HttpRequestException>(() => service.Queue(s, ticket));
            await Assert.ThrowsAsync<PortalException>(() => service.Queue(s, ticket)); Assert.Equal(1, h.Posts); }
    }
    [Theory][InlineData("drift")][InlineData("expired")]
    public async Task ChangedOrExpiredReviewCannotQueue(string fault)
    {
        var (service, s, h, config) = Setup(); using (s) { var ticket = await Review(service, s);
            if (fault == "drift") File.AppendAllText(config, " "); else s.NetworkPending[ticket] = s.NetworkPending[ticket] with { Expires = DateTimeOffset.UtcNow.AddSeconds(-1) };
            await Assert.ThrowsAsync<PortalException>(() => service.Queue(s, ticket)); Assert.Equal(0, h.Posts); }
    }
    [Fact] public async Task WrongDefinitionCannotProduceReviewTicket()
    {
        var (service, s, h, _) = Setup("wrong-yaml"); using (s) { await Assert.ThrowsAsync<PortalException>(() => Review(service, s)); Assert.Empty(s.NetworkPending); Assert.Equal(0, h.Posts); }
    }
}
