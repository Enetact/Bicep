using System.IO.Compression;
using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public class PreviewDiagramTests
{
    static PortalOptions Options()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir != null && !File.Exists(Path.Combine(dir.FullName, "config/workloads.json"))) dir = dir.Parent;
        return new() { RepositoryRoot = dir!.FullName };
    }
    static Product Product => new Catalog(Options()).Products.Single(p => p.Id == "storage");
    static byte[] Archive(string fault = "")
    {
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true))
        {
            void Add(string path, object value) { using var writer = new StreamWriter(zip.CreateEntry("deployment-preview/" + path).Open()); writer.Write(JsonSerializer.Serialize(value)); }
            var t = Product.Targets.Single(t => t.Environment == "dev");
            Add("target.json", new { workload = fault == "workload" ? "eventflow" : "storage", environmentName = "dev", subscriptionId = t.SubscriptionId });
            Add("preview-inputs.json", new { schemaVersion = 1, kind = "workload-preview-inputs", runId = fault == "run" ? "45" : "44", sourceCommit = fault == "commit" ? "other" : "abc", createdUtc = "2026-09-27T00:00:00Z" });
            Add("status.json", new { status = fault == "blocked" ? "Blocked" : "Preview succeeded; no workload deployment performed", error = "DO NOT RETURN SECRET" });
            Add("effective.parameters.json", new { parameters = new { location = new { value = "eastus2" }, secret = new { value = "DO NOT RETURN SECRET" } } });
            if (fault != "missing") Add("azure/stack-what-if.json", new { properties = new { provisioningState = "Succeeded", changes = new { resourceChanges = new[] {
                new { id = $"/subscriptions/{(fault == "scope" ? "other" : t.SubscriptionId)}/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/data", changeType = "create", changeCertainty = "definite", resourceConfigurationChanges = new { after = new { password = "DO NOT RETURN SECRET" } } }
            } } } });
            if (fault == "duplicate") Add("target.json", new { });
        }
        return bytes.ToArray();
    }
    [Theory]
    [InlineData("workload")][InlineData("run")][InlineData("commit")][InlineData("scope")][InlineData("missing")][InlineData("duplicate")]
    public async Task InvalidArtifactIsRejected(string fault) => await Assert.ThrowsAsync<PortalException>(() => PreviewDiagram.Read(Archive(fault), Product, 44, "abc", CancellationToken.None));
    [Theory][InlineData("")][InlineData("blocked")]
    public async Task ProjectsOnlyResourceActionsAndKeepsBlockedEvidence(string fault)
    {
        var result = JsonSerializer.SerializeToElement(await PreviewDiagram.Read(Archive(fault), Product, 44, "abc", CancellationToken.None));
        Assert.Equal(fault != "blocked", result.GetProperty("previewSucceeded").GetBoolean());
        Assert.False(result.GetProperty("deploymentAuthorized").GetBoolean());
        Assert.Equal("Create", result.GetProperty("resources")[0].GetProperty("action").GetString());
        Assert.Equal("eastus2", result.GetProperty("region").GetString());
        Assert.DoesNotContain("SECRET", result.ToString());
    }
    [Theory]
    [InlineData("http://x.vsblob.vsassets.io/a")][InlineData("https://evil.test/a")][InlineData("https://x.vsblob.vsassets.io.evil.test/a")]
    [InlineData("https://user@x.vsblob.vsassets.io/a")][InlineData("https://x.vsblob.vsassets.io:444/a")][InlineData("https://localhost/a")]
    public void UnsafeDownloadHostIsRejected(string url) => Assert.Throws<PortalException>(() => PreviewDiagram.DownloadUri(url));
    [Fact] public async Task BoundedReaderRejectsOversizedStream() => await Assert.ThrowsAsync<PortalException>(() => PreviewDiagram.Bounded(new MemoryStream(new byte[11]), 10, CancellationToken.None));
    [Theory][InlineData("")][InlineData("wrongDefinition")][InlineData("wrongBranch")][InlineData("wrongRepo")][InlineData("redirect")]
    public async Task GatewayBindsRunAndNeverSendsAdoTokenToStorage(string fault)
    {
        var options = Options(); var handler = new Fake(fault); var gateway = new AdoGateway(new HttpClient(handler), new Tokens(), options, new Catalog(options));
        if (fault == "") await gateway.Preview(new(options), "storage", 44, CancellationToken.None);
        else await Assert.ThrowsAsync<PortalException>(() => gateway.Preview(new(options), "storage", 44, CancellationToken.None));
        Assert.Equal(fault is "" or "redirect" ? 1 : 0, handler.Downloads);
    }
    sealed class Tokens : ITokenProvider { public Task<string> Token(BrowserSession s, string audience) => Task.FromResult("TEST-TOKEN"); }
    sealed class Fake(string fault) : HttpMessageHandler
    {
        public int Downloads;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get, request.Method);
            var uri = request.RequestUri!; object body;
            if (uri.Host == "test.vsblob.vsassets.io")
            {
                Downloads++; Assert.Null(request.Headers.Authorization);
                return Task.FromResult(new HttpResponseMessage(fault == "redirect" ? HttpStatusCode.Redirect : HttpStatusCode.OK) { Content = new ByteArrayContent(Archive()) });
            }
            Assert.Equal("TEST-TOKEN", request.Headers.Authorization?.Parameter);
            if (uri.AbsolutePath.EndsWith("/definitions")) body = new { value = new[] { new { id = 12, name = Product.DeployName } } };
            else if (uri.AbsolutePath.EndsWith("/definitions/12")) body = new { id = 12, repository = new { id = "repo" }, process = new { yamlFilename = Product.DeployYaml } };
            else if (uri.AbsolutePath.EndsWith("/builds/44")) body = new { definition = new { id = fault == "wrongDefinition" ? 99 : 12 }, repository = new { id = fault == "wrongRepo" ? "other" : "repo" }, sourceBranch = fault == "wrongBranch" ? "refs/heads/other" : "refs/heads/main", sourceVersion = "abc" };
            else if (uri.AbsolutePath.EndsWith("/artifacts")) body = new { name = "deployment-preview", signedContent = new { url = "https://test.vsblob.vsassets.io/signed" } };
            else throw new Exception("Unexpected request");
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) });
        }
    }
}
