using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class PortalTests
{
    static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config/workloads.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new Exception("Run tests in the repository checkout.");
    }
    static PortalOptions Options() => new() { RepositoryRoot = Root() };
    static RunRequest Request(string op = "preview") => new("blobcopy", "dev", "eastus2", op, 91);
    [Fact] public void CatalogReflectsActualTargetsAndMenuCosts()
    {
        var catalog = new Catalog(Options()); Assert.Equal(7, catalog.Products.Length);
        Assert.Equal(28, catalog.Products.Sum(p => p.Targets.Length)); Assert.All(catalog.Products, p => { Assert.NotEmpty(p.Summary); Assert.NotEmpty(p.Costs); Assert.DoesNotContain("See the generated", p.Requirements); Assert.All(p.Targets,t => Assert.False(t.Enabled)); });
        Assert.Contains(catalog.Skills, s => s.Id == "platform-discovery-audit");
    }
    [Theory]
    [InlineData("storage")] [InlineData("keyvault")] [InlineData("observe")] [InlineData("httpapi")] [InlineData("busworker")]
    public void NewProductsHaveDedicatedMenusAndPreviewPayloads(string id)
    {
        var c = new Catalog(Options()); var p = c.Products.Single(p => p.Id == id);
        Assert.Equal($"azure-pipelines-{id}-discover.yml",p.DiscoverYaml);
        Assert.Equal($"azure-pipelines-{id}-deploy.yml",p.DeployYaml);
        Assert.Contains("not zero/free",p.Costs);
        var payload=JsonSerializer.Serialize(c.Payload(new(id,"dev","eastus2","preview",91)));
        Assert.Contains("Preview only",payload);Assert.Contains("refs/heads/main",payload);
        Assert.Throws<PortalException>(()=>c.Validate(new(id,"dev","eastus2","deploy",91)));
    }
    [Theory]
    [InlineData("bad", "dev", "eastus2", "preview", 91)]
    [InlineData("blobcopy", "missing", "eastus2", "preview", 91)]
    [InlineData("blobcopy", "dev", "elsewhere", "preview", 91)]
    [InlineData("blobcopy", "dev", "eastus2", "delete", 91)]
    [InlineData("blobcopy", "dev", "eastus2", "deploy", 91)]
    [InlineData("blobcopy", "dev", "eastus2", "preview", 0)]
    public void RequestCannotEscapeRegisteredSelections(string product, string env, string region, string op, int run) =>
        Assert.Throws<PortalException>(() => new Catalog(Options()).Validate(new(product, env, region, op, run)));
    [Fact] public void DiscoveryDoesNotNeedManifestOrEnablement()
    {
        var json = JsonSerializer.SerializeToElement(new Catalog(Options()).Payload(Request("discover") with { DiscoveryRunId = null }));
        Assert.Equal("azure-subscription-a", json.GetProperty("templateParameters").GetProperty("subscription").GetString());
        Assert.False(json.GetProperty("resources").TryGetProperty("pipelines", out _));
    }
    [Fact] public void PreviewPinsExactDiscoveryAndMainAndHasNoArbitraryVariables()
    {
        var json = JsonSerializer.SerializeToElement(new Catalog(Options()).Payload(Request()));
        Assert.Equal("91", json.GetProperty("resources").GetProperty("pipelines").GetProperty("discovery").GetProperty("version").GetString());
        Assert.Equal("refs/heads/main", json.GetProperty("resources").GetProperty("repositories").GetProperty("self").GetProperty("refName").GetString());
        Assert.Equal("Preview only", json.GetProperty("templateParameters").GetProperty("executionMode").GetString());
        Assert.False(json.TryGetProperty("variables", out _));
    }
    [Fact] public async Task UnconfiguredIdentityCannotStartLoginOrIssueToken()
    {
        using var session = new BrowserSession(Options()); var identity = new BrowserIdentity();
        Assert.Throws<PortalException>(() => identity.Begin(session, "ado"));
        await Assert.ThrowsAsync<PortalException>(() => identity.Token(session, "ado"));
        Assert.Throws<PortalException>(() => BrowserIdentity.Scopes("https://untrusted.example"));
    }
    [Theory]
    [InlineData("wrongYaml")][InlineData("duplicate")][InlineData("failed")][InlineData("old")]
    [InlineData("future")][InlineData("branch")][InlineData("repository")][InlineData("definition")]
    [InlineData("environment")][InlineData("workload")][InlineData("missingParameters")]
    public async Task InvalidDiscoveryNeverQueues(string fault)
    {
        var (ado, handler, session) = Gateway(fault);
        await Assert.ThrowsAsync<PortalException>(() => ado.Queue(session, Request())); Assert.Equal(0, handler.Posts);
    }
    [Fact] public async Task ValidHandoffQueuesOnlyExpectedPipelineAndReturnsSafeUrl()
    {
        var (ado, handler, session) = Gateway(); var result = JsonSerializer.SerializeToElement(await ado.Queue(session, Request()));
        Assert.Equal(1, handler.Posts); Assert.Equal("Preview only", handler.PostBody!.Value.GetProperty("templateParameters").GetProperty("executionMode").GetString());
        Assert.Equal("https://dev.azure.com/enetactgames/Enetact/_build/results?buildId=92", result.GetProperty("url").GetString());
        Assert.All(handler.Urls, u => Assert.StartsWith("https://dev.azure.com/enetactgames/Enetact/_apis/", u));
    }
    [Fact] public async Task UpstreamFailureIsSanitizedAndNeverRetried()
    {
        var (ado, handler, session) = Gateway("postFailure"); var error = await Assert.ThrowsAsync<PortalException>(() => ado.Queue(session, Request()));
        Assert.Equal(1, handler.Posts); Assert.DoesNotContain("SECRET", error.Message);
    }
    [Fact] public async Task AnalysisRejectsInvalidInputsBeforeLaunchingProcess()
    {
        await Assert.ThrowsAsync<PortalException>(() => new AnalysisRunner(Options()).Run(new("not base64", "oops"), CancellationToken.None));
    }
    static (AdoGateway, FakeAdo, BrowserSession) Gateway(string fault = "")
    {
        var options = Options(); var handler = new FakeAdo(fault);
        return (new(new HttpClient(handler), new TestTokens(), options, new(options)), handler, new(options));
    }
    sealed class TestTokens : ITokenProvider { public Task<string> Token(BrowserSession s, string audience) => Task.FromResult("TEST-ONLY"); }
    sealed class FakeAdo(string fault) : HttpMessageHandler
    {
        public int Posts; public JsonElement? PostBody; public List<string> Urls = [];
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            var url = request.RequestUri!.ToString(); Urls.Add(url); Assert.Equal("TEST-ONLY", request.Headers.Authorization?.Parameter);
            object value;
            if (request.Method == HttpMethod.Post)
            {
                Posts++; PostBody = await request.Content!.ReadFromJsonAsync<JsonElement>(cancellationToken);
                if (fault == "postFailure") return new(HttpStatusCode.Forbidden) { Content = new StringContent("SECRET server detail") };
                Assert.Contains("pipelines/12/runs", url); value = new { id = 92 };
            }
            else if (url.Contains("definitions?"))
            {
                var discover = url.Contains("Discover"); var definition = new { id = discover ? 11 : 12, name = discover ? "Discover - Blob copy" : "Deploy - Blob copy" };
                value = new { value = fault == "duplicate" ? new[] { definition, definition } : new[] { definition } };
            }
            else if (url.Contains("definitions/")) value = new { id = url.Contains("/11?") ? 11 : 12, repository = new { id = "repo-1" }, process = new { yamlFilename = fault == "wrongYaml" ? "azure-pipelines.yml" : url.Contains("/11?") ? "azure-pipelines-blobcopy-discover.yml" : "azure-pipelines-blobcopy-deploy.yml" } };
            else if (url.Contains("build/builds/91")) value = new { definition = new { id = fault == "definition" ? 22 : 11 }, repository = new { id = fault == "repository" ? "other" : "repo-1" }, sourceBranch = fault == "branch" ? "refs/heads/feature" : "refs/heads/main", status = "completed", result = fault == "failed" ? "failed" : "succeeded", finishTime = DateTimeOffset.UtcNow.AddDays(fault == "old" ? -8 : fault == "future" ? 1 : -1) };
            else if (url.Contains("pipelines/11/runs/91")) value = fault == "missingParameters" ? new { id = 91 } : new { templateParameters = new { workload = fault == "workload" ? "eventflow" : "blobcopy", environment = fault == "environment" ? "prod" : "dev" } };
            else throw new Exception("Unexpected upstream call: " + url);
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(value) };
        }
    }
}
