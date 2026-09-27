using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class PipelineRegistrationTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static JsonElement Element(object o) => JsonSerializer.SerializeToElement(o, Json);
    static PortalOptions Options() {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config/workloads.json"))) dir = dir.Parent;
        return new() { RepositoryRoot = dir!.FullName };
    }
    sealed class Tokens : ITokenProvider { public Task<string> Token(BrowserSession s, string audience) { Assert.Equal("ado", audience); return Task.FromResult("synthetic-ado-token"); } }
    static JsonElement Definition(int id, string name, string yaml, string repo = "Enetact/Bicep") => Element(new {
        id, name, path = "\\", process = new { type = 2, yamlFilename = yaml }, queue = new { id = 9 },
        repository = new { id = repo, type = "GitHub", url = "https://github.com/" + repo, defaultBranch = "refs/heads/main",
            properties = new { connectedServiceId = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", secretUnrelatedProperty = "must-not-copy" } },
        variables = new { sensitive = new { value = "must-not-copy", isSecret = true } }, variableGroups = new[] { 99 }, triggers = new[] { new { triggerType = "continuousIntegration" } }
    });
    sealed class Handler : HttpMessageHandler
    {
        public PipelineCatalog Local = null!;
        public readonly List<JsonElement> Definitions = [Definition(1, "Enetact.Bicep", "azure-pipelines-self-service.yml")];
        public int Posts, Gets; public string Fault = ""; public string Commit = new('a', 40);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct)
        {
            object body; var uri = r.RequestUri!;
            if (uri.Host == "api.github.com") {
                Assert.Null(r.Headers.Authorization); Assert.Equal(HttpMethod.Get, r.Method);
                if (uri.AbsolutePath.EndsWith("/commits/main")) body = new { sha = Commit, commit = new { tree = new { sha = new string('b', 40) } } };
                else if (uri.AbsolutePath.Contains("/git/trees/")) body = new { truncated = Fault == "truncated", tree = Local.Entries.Where(e => Fault != "missing-source" || e.Yaml != "azure-pipelines-network.yml").Select(e => new { path = e.Yaml, type = "blob", mode = "100644", sha = Fault == "source-drift" ? new string('c', 40) : e.BlobSha }) };
                else body = new { @private = false };
            } else {
                Assert.Equal("dev.azure.com", uri.Host); Assert.Equal("synthetic-ado-token", r.Headers.Authorization!.Parameter);
                if (r.Method == HttpMethod.Post) {
                    Assert.EndsWith("/_apis/build/definitions", uri.AbsolutePath); Posts++;
                    var payload = await r.Content!.ReadFromJsonAsync<JsonElement>(ct);
                    Assert.Empty(payload.GetProperty("triggers").EnumerateArray()); Assert.Empty(payload.GetProperty("variables").EnumerateObject()); Assert.Empty(payload.GetProperty("variableGroups").EnumerateArray());
                    Assert.Equal("project", payload.GetProperty("jobAuthorizationScope").GetString());
                    Assert.Equal("refs/heads/main", payload.GetProperty("repository").GetProperty("defaultBranch").GetString());
                    Assert.DoesNotContain("must-not-copy", payload.GetRawText());
                    var d = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(payload.GetRawText())!;
                    d["id"] = Element(Definitions.Count + 1); var created = Element(d); Definitions.Add(created);
                    if (Fault == "lost-response" || Fault == "partial" && Posts == 2) throw new HttpRequestException("Synthetic lost response.");
                    body = created;
                } else {
                    Assert.Equal(HttpMethod.Get, r.Method); Gets++;
                    if (Fault == "denied") return new(HttpStatusCode.Forbidden);
                    if (uri.AbsolutePath.EndsWith("/definitions")) {
                        body = new { value = Definitions };
                        if (Fault == "repeat-page") { var response = new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(body) }; response.Headers.Add("x-ms-continuationtoken", "same"); return response; }
                    } else body = Definitions.Single(d => d.GetProperty("id").GetInt32() == int.Parse(uri.Segments.Last()));
                }
            }
            return new(HttpStatusCode.OK) { Content = JsonContent.Create(body) };
        }
    }
    static (PipelineRegistration Service, Handler Handler, BrowserSession Session) Setup(string fault = "") {
        var options = Options(); var handler = new Handler { Fault = fault }; var service = new PipelineRegistration(new(handler), new Tokens(), new(options), options);
        handler.Local = service.LocalCatalog(); return (service, handler, new(options));
    }
    static async Task<string> Review(PipelineRegistration service, BrowserSession s, params string[] files) =>
        Element(await service.Review(s, new(1, files), CancellationToken.None)).GetProperty("ticket").GetString()!;

    [Fact] public async Task AllRootPipelinesAreCataloguedAndExistingSeedIsReused() {
        var (service, h, s) = Setup(); using (s) {
            var inventory = await service.Inventory(s, CancellationToken.None);
            Assert.Equal(20, inventory.Pipelines.Length); Assert.Equal(19, inventory.Pipelines.Count(p => p.Status == "Missing")); Assert.Single(inventory.Pipelines, p => p.Status == "Existing"); Assert.Single(inventory.Sources); Assert.Equal(0, h.Posts);
        }
    }
    [Fact] public async Task AllMissingDefinitionsAreCreatedOnceWithoutRunningOrCopyingSecrets() {
        var (service, h, s) = Setup(); using (s) {
            var files = (await service.Inventory(s, CancellationToken.None)).Pipelines.Where(p => p.Status == "Missing").Select(p => p.Yaml).ToArray();
            var ticket = await Review(service, s, files); var result = Element(await service.Apply(s, ticket, CancellationToken.None));
            Assert.Equal("Completed", result.GetProperty("status").GetString()); Assert.Equal(19, h.Posts); Assert.False(result.GetProperty("runsQueued").GetBoolean());
            Assert.All((await service.Inventory(s, CancellationToken.None)).Pipelines, p => Assert.Equal("Existing", p.Status));
            await Assert.ThrowsAsync<PortalException>(() => service.Apply(s, ticket, CancellationToken.None)); Assert.Equal(19, h.Posts);
        }
    }
    [Theory][InlineData("source-drift")][InlineData("missing-source")]
    public async Task UnpublishedSourceCannotBeRegistered(string fault) {
        var (service, h, s) = Setup(fault); using (s) { await Assert.ThrowsAsync<PortalException>(() => Review(service, s, "azure-pipelines-network.yml")); Assert.Equal(0, h.Posts); }
    }
    [Theory][InlineData("denied")][InlineData("truncated")][InlineData("repeat-page")]
    public async Task IncompleteInventoryNeverMeansMissing(string fault) {
        var (service, h, s) = Setup(fault); using (s) { await Assert.ThrowsAsync<PortalException>(() => service.Inventory(s, CancellationToken.None)); Assert.Equal(0, h.Posts); }
    }
    [Theory][InlineData("rename")][InlineData("wrong-repository")][InlineData("duplicate")]
    public async Task ConflictingDefinitionsArePreserved(string fault) {
        var (service, h, s) = Setup(); using (s) {
            h.Definitions.Add(Definition(2, fault == "rename" ? "Another name" : "Network - AVNM allocation", "azure-pipelines-network.yml", fault == "wrong-repository" ? "Other/Repository" : "Enetact/Bicep"));
            if (fault == "duplicate") h.Definitions.Add(Definition(3, "Network - AVNM allocation", "azure-pipelines-network.yml"));
            await Assert.ThrowsAsync<PortalException>(() => Review(service, s, "azure-pipelines-network.yml")); Assert.Equal(0, h.Posts);
        }
    }
    [Theory][InlineData("commit")][InlineData("expired")][InlineData("source-connection")][InlineData("catalog")]
    public async Task StaleReviewCannotCreate(string fault) {
        var (service, h, s) = Setup(); using (s) {
            var ticket = await Review(service, s, "azure-pipelines-network.yml");
            if (fault == "commit") h.Commit = new string('c',40);
            if (fault == "expired") s.RegistrationPending[ticket] = s.RegistrationPending[ticket] with { Expires = DateTimeOffset.UtcNow.AddMinutes(-1) };
            if (fault == "source-connection") h.Definitions.Clear();
            if (fault == "catalog") s.RegistrationPending[ticket] = s.RegistrationPending[ticket] with { CatalogHash = "changed" };
            await Assert.ThrowsAsync<PortalException>(() => service.Apply(s, ticket, CancellationToken.None)); Assert.Equal(0, h.Posts);
        }
    }
    [Fact] public async Task ConcurrentMatchingCreationIsReused() {
        var (service, h, s) = Setup(); using (s) {
            var ticket = await Review(service, s, "azure-pipelines-network.yml"); h.Definitions.Add(Definition(2,"Network - AVNM allocation","azure-pipelines-network.yml"));
            var result = Element(await service.Apply(s,ticket,CancellationToken.None)); Assert.Equal("Reused",result.GetProperty("results")[0].GetProperty("status").GetString()); Assert.Equal(0,h.Posts);
        }
    }
    [Theory][InlineData("lost-response",1)][InlineData("partial",2)]
    public async Task UnknownOutcomeStopsBatchAndNeverRetries(string fault, int posts) {
        var (service, h, s) = Setup(fault); using (s) {
            var ticket = await Review(service,s,"azure-pipelines-network.yml","azure-pipelines.yml","azure-pipelines-storage-discover.yml");
            var result=Element(await service.Apply(s,ticket,CancellationToken.None)); Assert.Equal("Incomplete",result.GetProperty("status").GetString()); Assert.Equal(posts,h.Posts);
            Assert.Equal("Uncertain",result.GetProperty("results")[posts-1].GetProperty("status").GetString());
            await Assert.ThrowsAsync<PortalException>(()=>service.Apply(s,ticket,CancellationToken.None)); Assert.Equal(posts,h.Posts);
        }
    }
    [Fact] public void GitBlobUsesCanonicalLfBytes() { Assert.Equal(PipelineRegistration.GitBlob("line\n"),PipelineRegistration.GitBlob("line\r\n")); Assert.Equal("e69de29bb2d1d6434b8b29ae775ad8c2e48c5391",PipelineRegistration.GitBlob("")); }
}
