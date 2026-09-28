using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class TagPipelineTests
{
    sealed class Tokens : ITokenProvider { public Task<string> Token(BrowserSession s,string audience)=>Task.FromResult("fixture-only"); }
    sealed class Handler(string fault) : HttpMessageHandler
    {
        public int Posts;
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage r,CancellationToken ct){
            object result;
            if(r.Method==HttpMethod.Post){Posts++;var body=await r.Content!.ReadFromJsonAsync<JsonElement>(ct);Assert.Equal("refs/heads/main",body.GetProperty("resources").GetProperty("repositories").GetProperty("self").GetProperty("refName").GetString());if(fault=="lost")throw new HttpRequestException("Synthetic unknown outcome");result=new{id=42};}
            else if(r.RequestUri!.AbsolutePath.Contains("/projects/"))result=new{visibility=fault=="public"?"public":fault=="unknown"?"":"private"};
            else if(r.RequestUri.AbsolutePath.EndsWith("/definitions"))result=new{value=new[]{new{id=5,name="Discover - Tags"}}};
            else result=new{id=5,process=new{yamlFilename=fault=="yaml"?"wrong.yml":"azure-pipelines-tags-discover.yml"},repository=new{id=fault=="repo"?"Other/Repo":"Enetact/Bicep",type="GitHub",defaultBranch=fault=="branch"?"refs/heads/feature":"refs/heads/main"}};
            return new(HttpStatusCode.OK){Content=JsonContent.Create(result)};
        }
    }
    static (TaggingService Service,BrowserSession Session,Handler Handler) Setup(string fault=""){
        var root=new DirectoryInfo(AppContext.BaseDirectory);while(root is not null&&!File.Exists(Path.Combine(root.FullName,"config/workloads.json")))root=root.Parent;
        var options=new PortalOptions{RepositoryRoot=root!.FullName};var catalog=new Catalog(options);var h=new Handler(fault);var tokens=new Tokens();var client=new HttpClient(h);
        return(new(client,tokens,catalog,options,new(client,tokens,options,catalog)),new(options),h);
    }
    static async Task<string> Review(TaggingService service,BrowserSession session)=>JsonSerializer.SerializeToElement(await service.Review(session,new("Discover",service.Profile().SubscriptionId))).GetProperty("ticket").GetString()!;
    [Theory][InlineData("public")][InlineData("unknown")][InlineData("yaml")][InlineData("repo")][InlineData("branch")]
    public async Task UnqualifiedProjectOrSourceCannotQueue(string fault){var(service,s,h)=Setup(fault);using(s){await Assert.ThrowsAsync<PortalException>(()=>Review(service,s));Assert.Empty(s.TagPending);Assert.Equal(0,h.Posts);}}
    [Fact]public async Task ReviewIsSingleUseAndQueueIsNotRetried(){var(service,s,h)=Setup();using(s){var ticket=await Review(service,s);await service.Queue(s,ticket);await Assert.ThrowsAsync<PortalException>(()=>service.Queue(s,ticket));Assert.Equal(1,h.Posts);}}
    [Fact]public async Task UnknownQueueOutcomeConsumesTicket(){var(service,s,h)=Setup("lost");using(s){var ticket=await Review(service,s);await Assert.ThrowsAsync<HttpRequestException>(()=>service.Queue(s,ticket));await Assert.ThrowsAsync<PortalException>(()=>service.Queue(s,ticket));Assert.Equal(1,h.Posts);}}
    [Fact]public async Task ExpiredTicketCannotQueue(){var(service,s,h)=Setup();using(s){var ticket=await Review(service,s);s.TagPending[ticket]=s.TagPending[ticket] with{Expires=DateTimeOffset.UtcNow.AddMinutes(-1)};await Assert.ThrowsAsync<PortalException>(()=>service.Queue(s,ticket));Assert.Equal(0,h.Posts);}}
}
