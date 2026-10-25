using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class AzureDiscoveryTests
{
    static PortalOptions Options()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config/workloads.json"))) dir = dir.Parent;
        return new() { RepositoryRoot = dir!.FullName };
    }
    static string Subscription => new Catalog(Options()).Products[0].Targets[0].SubscriptionId;
    static SkillDiscoveryRequest Request(string skill = "azure--azure-storage") => new(skill, Subscription);
    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value, new JsonSerializerOptions(JsonSerializerDefaults.Web));
    [Fact] public async Task GroupScopeIsAppliedToEveryAzureCollection()
    {
        var (service, http, session) = Service();
        await service.Discover(session, Request("azure--azure-resource-visualizer") with { ResourceGroup = "rg-selected" }, CancellationToken.None);
        Assert.Equal(6, http.Requests.Count);
        Assert.All(http.Requests, r => Assert.Contains("/resourceGroups/rg-selected/", r));
    }
    [Fact] public async Task InvalidGroupCannotInjectAnArmRoute()
    {
        var (service, http, session) = Service();
        await Assert.ThrowsAsync<PortalException>(() => service.Discover(session, Request() with { ResourceGroup = "../other" }, CancellationToken.None));
        Assert.Empty(http.Requests);
    }
    static (AzureDiscovery Service, Handler Http, BrowserSession Session) Service(string scenario = "empty", string? diagnosticBody = null, bool chunked = false)
    {
        var o = Options(); var handler = new Handler(scenario, diagnosticBody, chunked);
        return (new(new HttpClient(handler), new Tokens(), o, new(o)), handler, new(o));
    }
    sealed class Tokens : ITokenProvider { public Task<string> Token(BrowserSession session, string audience) { Assert.Equal("azure", audience); return Task.FromResult("TEST-AZURE-TOKEN"); } }
    [Fact] public void EntireUpstreamCatalogIncludesNestedAndCostSkills()
    {
        var skills = new Catalog(Options()).Skills;
        Assert.Equal(53, skills.Length); Assert.Equal(42, skills.Count(s => s.Origin == "Microsoft Azure Skills"));
        Assert.Contains(skills, s => s.Name == "azure-app-onboard-deploy"); Assert.Contains(skills, s => s.Id == "azure--azure-cost--cost-analysis");
        Assert.All(skills.Where(s => s.Origin == "Microsoft Azure Skills"), s => { Assert.Equal("No pipeline associated yet", s.PipelineStatus); Assert.NotEqual("none", s.DiscoveryProfile); Assert.NotEmpty(s.Content); });
    }
    [Theory][InlineData("missing")][InlineData("platform-request-design")]
    public async Task UnknownOrLocalSkillCannotTriggerAzure(string skill)
    {
        var (service, http, session) = Service(); await Assert.ThrowsAsync<PortalException>(() => service.Discover(session, Request(skill), CancellationToken.None)); Assert.Empty(http.Requests);
    }
    [Fact] public async Task UnregisteredSubscriptionFailsBeforeAnyHttp()
    {
        var (service, http, session) = Service(); await Assert.ThrowsAsync<PortalException>(() => service.Discover(session, Request() with { SubscriptionId = Guid.NewGuid().ToString() }, CancellationToken.None)); Assert.Empty(http.Requests);
    }
    [Fact] public async Task EmptyIsExplicitAndNeverAuthorizesCreation()
    {
        var (service, http, session) = Service(); var report = Json(await service.Discover(session, Request(), CancellationToken.None));
        Assert.Equal("Collected", report.GetProperty("status").GetString()); Assert.False(report.GetProperty("deploymentAuthorized").GetBoolean()); Assert.False(report.GetProperty("allocationAuthorized").GetBoolean());
        Assert.Equal("SucceededEmpty", report.GetProperty("collections")[0].GetProperty("status").GetString()); Assert.Single(http.Requests);
    }
    [Theory][InlineData("denied")][InlineData("notFound")][InlineData("invalidJson")][InlineData("invalidSchema")][InlineData("outOfScope")]
    public async Task FailedReadNeverBecomesSuccessfulEmpty(string scenario)
    {
        var (service, _, session) = Service(scenario); var report = Json(await service.Discover(session, Request(), CancellationToken.None));
        Assert.Equal("Partial", report.GetProperty("status").GetString()); Assert.NotEqual("SucceededEmpty", report.GetProperty("collections")[0].GetProperty("status").GetString()); Assert.DoesNotContain("SECRET", report.GetRawText());
    }
    [Fact] public async Task PaginationFiltersRelevantTypesAndStripsArbitraryProperties()
    {
        var (service, http, session) = Service("pages"); var report = Json(await service.Discover(session, Request(), CancellationToken.None));
        var c = report.GetProperty("collections")[0]; Assert.Equal(2, c.GetProperty("pages").GetInt32()); Assert.Equal(2, c.GetProperty("observed").GetInt32()); Assert.Single(c.GetProperty("resources").EnumerateArray()); Assert.DoesNotContain("SECRET", report.GetRawText()); Assert.Equal(2, http.Requests.Count);
    }
    [Theory][InlineData("externalNext")][InlineData("otherScopeNext")][InlineData("otherCollectionNext")][InlineData("loop")]
    public async Task UnsafeContinuationNeverReceivesToken(string scenario)
    {
        var (service, http, session) = Service(scenario); var report = Json(await service.Discover(session, Request(), CancellationToken.None));
        Assert.Equal("Partial", report.GetProperty("status").GetString()); Assert.Single(http.Requests);
    }
    [Fact] public async Task LaterPageFailureRetainsFactsWithPartialCoverage()
    {
        var (service, _, session) = Service("laterFailure"); var report = Json(await service.Discover(session, Request(), CancellationToken.None));
        var c = report.GetProperty("collections")[0]; Assert.Equal("Partial", c.GetProperty("status").GetString()); Assert.Single(c.GetProperty("resources").EnumerateArray());
    }
    [Fact] public async Task NetworkAdapterUsesOnlyFixedGetRoutesAndMarksMissingSubnetsUnknown()
    {
        var (service, http, session) = Service("network"); var report = Json(await service.Discover(session, Request("azure--azure-enterprise-infra-planner"), CancellationToken.None));
        Assert.Equal(6, http.Requests.Count); Assert.Equal(6, report.GetProperty("collections").GetArrayLength());
        var vnet = report.GetProperty("collections")[1].GetProperty("resources")[0]; Assert.StartsWith("Unknown", vnet.GetProperty("subnetsEvidence").GetString());
        Assert.DoesNotContain("SECRET", report.GetRawText());
    }
    [Theory][InlineData("dnsFallbackEmpty", 0)][InlineData("dnsFallbackNonempty", 1)]
    public async Task DnsFallbackRecoversMetadataButNeverHidesProviderFailure(string scenario, int count)
    {
        var (service, _, session) = Service(scenario);
        var report = Json(await service.Discover(session, Request("azure--azure-resource-visualizer"), CancellationToken.None));
        Assert.Equal("Partial", report.GetProperty("status").GetString());
        var collections = report.GetProperty("collections").EnumerateArray().ToArray();
        Assert.Equal("Failed", collections.Single(c => c.GetProperty("name").GetString() == "Private DNS zones").GetProperty("status").GetString());
        var fallback = collections.Single(c => c.GetProperty("name").GetString()!.Contains("fallback"));
        Assert.Equal(count, fallback.GetProperty("resources").GetArrayLength());
        Assert.Contains("identities only", fallback.GetProperty("issue").GetString());
        Assert.DoesNotContain("SECRET", report.GetRawText());
    }
    [Theory]
    [InlineData("BadRequest")][InlineData("SubscriptionNotFound")][InlineData("NoRegisteredProviderFound")][InlineData("MissingSubscriptionRegistration")]
    public async Task DnsFailureRetainsOnlyCanonicalArmDiagnosticCode(string code)
    {
        var body = JsonSerializer.Serialize(new { error = new { code, message = "SECRET private upstream message", details = new[] { new { code = "SECRET", target = "SECRET" } } }, secret = "SECRET" });
        var (service, http, session) = Service("dnsDiagnostic", body);
        var report = Json(await service.Discover(session, Request("azure--azure-resource-visualizer"), CancellationToken.None));
        var dns = report.GetProperty("collections").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Private DNS zones");
        Assert.Equal("Partial", report.GetProperty("status").GetString());
        Assert.Equal("Failed", dns.GetProperty("status").GetString());
        Assert.Equal(0, dns.GetProperty("observed").GetInt32());
        Assert.Contains($"HTTP 400 (ARM {code})", dns.GetProperty("issue").GetString());
        Assert.Contains("not an empty result", dns.GetProperty("issue").GetString());
        Assert.DoesNotContain("SECRET", report.GetRawText());
        Assert.Equal(6, http.Requests.Count);
    }
    public static IEnumerable<object[]> UnsafeDiagnosticBodies()
    {
        yield return ["SECRET not JSON"];
        yield return ["[]"];
        yield return ["{\"code\":\"BadRequest\",\"message\":\"SECRET\"}"];
        yield return ["{\"error\":{\"code\":123,\"message\":\"SECRET\"}}"];
        yield return ["{\"error\":{\"code\":\"BadRequest\",\"code\":\"SubscriptionNotFound\"}}"];
        yield return ["{\"error\":{\"code\":\"BadRequest\"},\"error\":{\"code\":\"SubscriptionNotFound\"}}"];
        yield return ["{\"error\":{\"code\":\"BadRequest\",\"Code\":\"SubscriptionNotFound\"}}"];
        yield return ["{\"error\":{\"code\":\"BadRequest\"},\"Error\":{\"code\":\"SubscriptionNotFound\"}}"];
        yield return ["{\"error\":{\"code\":\"SECRET-CustomProviderCode\",\"message\":\"SECRET\"}}"];
        yield return ["{\"error\":{\"code\":\"Bearer SECRET\"}}"];
        yield return ["{\"error\":{\"code\":\"BadRequest\\nSECRET\"}}"];
        yield return [JsonSerializer.Serialize(new { error = new { code = new string('S', 129), message = "SECRET" } })];
        yield return ["{\"error\":{\"code\":\"BadRequest\",\"details\":" + new string('[', 9) + "\"SECRET\"" + new string(']', 9) + "}}"];
    }
    [Theory][MemberData(nameof(UnsafeDiagnosticBodies))]
    public async Task MalformedAmbiguousAndUnreviewedArmDiagnosticsCannotEscape(string body)
    {
        var (service, _, session) = Service("dnsDiagnostic", body);
        var report = Json(await service.Discover(session, Request("azure--azure-resource-visualizer"), CancellationToken.None));
        var dns = report.GetProperty("collections").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Private DNS zones");
        Assert.Equal("Partial", report.GetProperty("status").GetString());
        Assert.Equal("Failed", dns.GetProperty("status").GetString());
        Assert.Equal("HTTP 400; collection unavailable. This is not an empty result.", dns.GetProperty("issue").GetString());
        Assert.DoesNotContain("SECRET", report.GetRawText());
    }
    [Theory][InlineData(false)][InlineData(true)]
    public async Task OversizedArmDiagnosticsAreBoundedAndNeverExposed(bool chunked)
    {
        var body = JsonSerializer.Serialize(new { error = new { code = "BadRequest", message = "SECRET" + new string('S', 32 * 1024) } });
        var (service, http, session) = Service("dnsDiagnostic", body, chunked);
        var report = Json(await service.Discover(session, Request("azure--azure-resource-visualizer"), CancellationToken.None));
        var dns = report.GetProperty("collections").EnumerateArray().Single(c => c.GetProperty("name").GetString() == "Private DNS zones");
        Assert.Equal("Partial", report.GetProperty("status").GetString());
        Assert.Equal("Failed", dns.GetProperty("status").GetString());
        Assert.DoesNotContain("ARM BadRequest", dns.GetProperty("issue").GetString());
        Assert.DoesNotContain("SECRET", report.GetRawText());
        Assert.InRange(http.DiagnosticBytesRead, chunked ? 16 * 1024 + 1 : 0, chunked ? 18 * 1024 : 0);
    }
    sealed class DiagnosticContent(string body, Action<int> observed) : HttpContent
    {
        readonly byte[] bytes = System.Text.Encoding.UTF8.GetBytes(body);
        protected override bool TryComputeLength(out long length) { length = 0; return false; }
        protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) => stream.WriteAsync(bytes).AsTask();
        protected override Task<Stream> CreateContentReadStreamAsync() => Task.FromResult<Stream>(new DiagnosticStream(bytes, observed));
    }
    sealed class DiagnosticStream(byte[] bytes, Action<int> observed) : MemoryStream(bytes)
    {
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken ct = default)
        {
            var read = await base.ReadAsync(buffer, ct); observed(read); return read;
        }
    }
    sealed class Handler(string scenario, string? diagnosticBody = null, bool chunked = false) : HttpMessageHandler
    {
        public List<string> Requests = [];
        public int DiagnosticBytesRead;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Assert.Equal(HttpMethod.Get, request.Method); Assert.Equal("management.azure.com", request.RequestUri!.Host); Assert.Equal("TEST-AZURE-TOKEN", request.Headers.Authorization!.Parameter);
            var url = request.RequestUri.AbsoluteUri; Requests.Add(url); object value;
            if (scenario is "denied" or "notFound" || scenario == "laterFailure" && Requests.Count == 2)
                return Task.FromResult(new HttpResponseMessage(scenario == "notFound" ? HttpStatusCode.NotFound : HttpStatusCode.Forbidden) { Content = new StringContent("SECRET upstream diagnostics") });
            if (scenario == "invalidJson") return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = new StringContent("SECRET invalid JSON") });
            object Resource(string type) => new { id = $"/subscriptions/{Subscription}/resourceGroups/example/providers/{type}/example", type, name = "example", location = "eastus2", tags = new { password = "SECRET" }, properties = new { connectionString = "SECRET" } };
            if (scenario == "dnsDiagnostic" && request.RequestUri.AbsolutePath.EndsWith("/privateDnsZones"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest) { Content = chunked ? new DiagnosticContent(diagnosticBody!, read => DiagnosticBytesRead += read) : new StringContent(diagnosticBody!) });
            if (scenario.StartsWith("dnsFallback") && request.RequestUri.AbsolutePath.EndsWith("/privateDnsZones"))
                return Task.FromResult(new HttpResponseMessage(HttpStatusCode.BadRequest));
            if (scenario.StartsWith("dnsFallback")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(new { value = scenario == "dnsFallbackNonempty" && request.RequestUri.AbsolutePath.EndsWith("/resources") ? new[] { Resource("Microsoft.Network/privateDnsZones") } : Array.Empty<object>() }) });
            if (scenario == "invalidSchema") value = new { error = "SECRET" };
            else if (scenario == "outOfScope") value = new { value = new[] { new { id = "/subscriptions/other/resourceGroups/foreign", type = "Microsoft.Storage/storageAccounts" } } };
            else if (scenario == "network") value = new { value = request.RequestUri.AbsolutePath.EndsWith("virtualNetworks") ? new[] { Resource("Microsoft.Network/virtualNetworks") } : Array.Empty<object>() };
            else if (scenario is "pages" or "laterFailure") value = Requests.Count == 1 ? new { value = new[] { Resource("Microsoft.Storage/storageAccounts") }, nextLink = url + "&page=2" } : (object)new { value = new[] { Resource("Microsoft.Compute/virtualMachines") } };
            else if (scenario.EndsWith("Next") || scenario == "loop") value = new { value = new[] { Resource("Microsoft.Storage/storageAccounts") }, nextLink = scenario switch { "externalNext" => "https://evil.example/leak", "otherScopeNext" => url.Replace(Subscription, Guid.NewGuid().ToString()), "otherCollectionNext" => url.Replace("/resources?", "/listKeys?"), _ => url } };
            else value = new { value = Array.Empty<object>() };
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK) { Content = JsonContent.Create(value) });
        }
    }
}
