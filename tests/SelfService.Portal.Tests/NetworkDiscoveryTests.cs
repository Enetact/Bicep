using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class NetworkDiscoveryTests
{
    const string Tenant = "aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa", Sub = "11111111-1111-1111-1111-111111111111", Other = "22222222-2222-2222-2222-222222222222";
    static PortalOptions Options() { var d=new DirectoryInfo(AppContext.BaseDirectory); while(d is not null && !File.Exists(Path.Combine(d.FullName,"config/workloads.json")))d=d.Parent;return new(){RepositoryRoot=d!.FullName,TenantId=Tenant}; }
    sealed class Tokens : ITokenProvider { public Task<string> Token(BrowserSession session,string audience) => Task.FromResult("TEST-TOKEN"); }
    sealed class Handler(string mode="ok") : HttpMessageHandler
    {
        public List<string> Requests {get;}=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct)
        {
            Assert.Equal(HttpMethod.Get,r.Method); Assert.Equal("management.azure.com",r.RequestUri!.Host); Requests.Add(r.RequestUri.PathAndQuery);
            var path=r.RequestUri.AbsolutePath;
            object value;
            if(path=="/subscriptions")
            {
                if(mode=="denied")return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden));
                value=new {value=new[]{new {subscriptionId=Sub,displayName="Visible",tenantId=Tenant,state="Enabled"},new{subscriptionId=Other,displayName="Delegated external",tenantId="bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb",state="Enabled"}},nextLink=mode=="unsafe"?"https://evil.test/steal":null};
            }
            else if(path.EndsWith("/descendants")) value=new {value=new[]{new {id="/subscriptions/"+Sub},new{id="/subscriptions/"+Other}}};
            else if(path.EndsWith("/managementGroups")) value=new {value=new[]{new{id="/providers/Microsoft.Management/managementGroups/test",name="test",properties=new{tenantId=Tenant,displayName="Test group"}}}};
            else { if(mode=="resourceDenied" && path.Contains("virtualNetworks")) return Task.FromResult(new HttpResponseMessage(HttpStatusCode.Forbidden)); value=new {value=System.Array.Empty<object>()}; }
            return Task.FromResult(new HttpResponseMessage(HttpStatusCode.OK){Content=JsonContent.Create(value)});
        }
    }
    static JsonElement Json(object o)=>JsonSerializer.SerializeToElement(o,new JsonSerializerOptions(JsonSerializerDefaults.Web));
    [Fact] public async Task TenantScanExcludesForeignSubscriptionAndRetainsEmptyCollections()
    {
        var o=Options();var h=new Handler();var service=new NetworkDiscovery(new(h),new Tokens(),o,new(o));using var s=new BrowserSession(o);
        var result=Json(await service.Discover(s,new("tenant"),CancellationToken.None));
        Assert.Single(result.GetProperty("reports").EnumerateArray());Assert.False(result.GetProperty("allocationAuthorized").GetBoolean());
        Assert.DoesNotContain(h.Requests,p=>p.Contains("/subscriptions/"+Other+"/"));Assert.Contains("excluded",result.GetProperty("issues")[0].GetString());Assert.Single(s.NetworkReports);
    }
    [Fact] public async Task ManagementGroupReportsUnavailableDescendants()
    {
        var o=Options();var service=new NetworkDiscovery(new(new Handler()),new Tokens(),o,new(o));using var s=new BrowserSession(o);
        var result=Json(await service.Discover(s,new("managementGroup",ManagementGroupId:"test"),CancellationToken.None));
        Assert.Equal("Partial",result.GetProperty("status").GetString());Assert.Contains(result.GetProperty("issues").EnumerateArray(),i=>i.GetString()!.Contains("Descendant"));
    }
    [Theory][InlineData("denied")][InlineData("unsafe")]
    public async Task IncompleteScopeEnumerationBlocksScan(string mode)
    {
        var o=Options();var h=new Handler(mode);var service=new NetworkDiscovery(new(h),new Tokens(),o,new(o));using var s=new BrowserSession(o);
        await Assert.ThrowsAsync<PortalException>(()=>service.Discover(s,new("tenant"),CancellationToken.None));Assert.DoesNotContain(h.Requests,p=>p.Contains("/resources?"));
    }
    [Fact] public async Task ArbitrarySubscriptionAndManagementGroupCannotEscapeResolvedScope()
    {
        var o=Options();var service=new NetworkDiscovery(new(new Handler()),new Tokens(),o,new(o));using var s=new BrowserSession(o);
        await Assert.ThrowsAsync<PortalException>(()=>service.Discover(s,new("selected",[Other]),CancellationToken.None));
        await Assert.ThrowsAsync<PortalException>(()=>service.Discover(s,new("managementGroup",ManagementGroupId:"../other"),CancellationToken.None));
    }
    [Fact] public async Task FailedResourceReadIsPartialNotEmpty()
    {
        var o=Options();var service=new NetworkDiscovery(new(new Handler("resourceDenied")),new Tokens(),o,new(o));using var s=new BrowserSession(o);
        var result=Json(await service.Discover(s,new("selected",[Sub]),CancellationToken.None));Assert.Equal("Partial",result.GetProperty("reports")[0].GetProperty("status").GetString());
    }
}
