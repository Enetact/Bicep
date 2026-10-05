using System.Net;
using System.Text.Json;
using SelfService.Operations;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class OperationsTests
{
    const string Sub = "11111111-1111-1111-1111-111111111111";
    const string Scope = "/subscriptions/" + Sub;
    const string Zone = Scope + "/resourcegroups/a/providers/microsoft.network/privatednszones/example.internal";
    const string Other = Scope + "/resourcegroups/b/providers/microsoft.network/privatednszones/example.internal";
    static ObservedResource Z(string id = Zone) => new(id, PrivateDnsRules.ZoneType, "example.internal", null, new());
    static ObservedResource R(string zone = Zone, string ttl = "300", string address = "[\"10.0.0.1\"]") => new(zone+"/a/app",PrivateDnsRules.ZoneType+"/a","app",zone,new(){{"recordType","A"},{"ttl",ttl},{"values",address},{"ownership","Unknown"}});
    static OperationsEvidence E(params ObservedResource[] resources) => OperationsJson.Seal(new("platform.operations-evidence/v1",Guid.NewGuid().ToString("N"),"tenant",[Sub],DateTimeOffset.UtcNow,DateTimeOffset.UtcNow,"test-only",[new(Scope,"Zones","Succeeded",1,resources.Length,null)],resources));
    [Fact] public void SameNameIsCandidateAndValueConflictHasNoWinner()
    {
        var findings=PrivateDnsRules.Analyze(E(Z(),Z(Other),R(),R(Other,address:"[\"10.0.0.2\"]")));
        Assert.Equal(2,findings.Count(f=>f.Rule=="DNS-001")); Assert.Equal(2,findings.Count(f=>f.Rule=="DNS-002"));
        Assert.All(findings.Where(f=>f.Rule=="DNS-002"),f=>Assert.Equal("Conflict",f.State));
        Assert.Contains(findings,f=>f.Rule=="DNS-005"&&f.Message.Contains("Unknown"));
    }
    [Fact] public void DeniedIsUnknownNotUnused()
    {
        var e=E(Z());e=OperationsJson.Seal(e with {Coverage=[new(Zone,"Records","Denied",0,0,"HTTP 403")]});
        Assert.Contains(PrivateDnsRules.Analyze(e),f=>f.Rule=="DNS-005"&&f.State=="Unknown");
    }
    [Theory][InlineData("300","300","InSync")][InlineData("300","600","DesiredChange")][InlineData("600","300","ObservedDeviation")][InlineData("600","900","Conflict")]
    public void ThreeWayComparisonSeparatesIntentAndObservation(string observed,string desired,string state)
    {
        var rows=DriftRules.Compare(E(R()),E(R(ttl:observed)),[new(Zone+"/a/app","ttl",desired)]);
        Assert.Equal(state,rows.Single(r=>r.Property=="ttl").State);
    }
    [Fact] public void MissingReferenceAndMissingResourceDoNotProveHistoricalDriftOrDeletion()
    {
        Assert.All(DriftRules.Compare(null,E(R()),[]),r=>Assert.Equal("Unknown",r.State));
        Assert.Equal("Unknown",Assert.Single(DriftRules.Compare(E(R()),E(),[])).State);
        Assert.Contains("not an accepted",OperationsRules.Assess("drift",E(R()),E(R()),[]).BaselineAuthority);
    }
    [Fact] public void DriftRejectsCrossScopeAndUnsupportedEdits()
    {
        var b=E(R());var other=OperationsJson.Seal(b with {Tenant="other"});
        Assert.Throws<OperationsException>(()=>DriftRules.Compare(b,other,[]));
        Assert.Throws<OperationsException>(()=>DriftRules.Compare(b,b,[new(Zone+"/a/app","values","10.0.0.5")]));
        Assert.Throws<OperationsException>(()=>DriftRules.Compare(b,b,[new(Zone+"/a/app","ttl","-1")]));
    }
    [Fact] public void EvidenceTamperingStalenessAndBrowserScopeFailClosed()
    {
        var e=E(R());Assert.Throws<OperationsException>(()=>OperationsJson.Verify(e with {Collector="tampered"}));
        var old=OperationsJson.Seal(e with {CompletedUtc=DateTimeOffset.UtcNow.AddHours(-1)});
        Assert.Throws<OperationsException>(()=>OperationsJson.Fresh(old,DateTimeOffset.UtcNow));
        using var first=new BrowserSession(new());using var other=new BrowserSession(new());first.Connected["azure"]=true;other.Connected["azure"]=true;first.Operations.Evidence[e.Id]=e;
        Assert.Same(e,OperationsService.Evidence(first,e.Id));Assert.Throws<PortalException>(()=>OperationsService.Evidence(other,e.Id));
        first.Connected.Clear();Assert.Throws<PortalException>(()=>OperationsService.Evidence(first,e.Id));
    }
    [Fact] public void SerializedEvidenceIsPortableAcrossWindowsAndPipelineLineEndings()
    {
        var e=E(R());var json=OperationsJson.Write(e);
        Assert.DoesNotContain("\r",json);
        OperationsJson.Verify(OperationsJson.Read<OperationsEvidence>(json.Replace("\n","\r\n")));
    }
    [Fact] public void AgentInventedTargetsOperationsAndReferencesAreRejected()
    {
        var e=E(Z());var a=OperationsRules.Assess("private-dns",e,null,[]);var f=a.Findings.Single();
        var recommendation=new OperationRecommendation("one",Zone,"Investigate",[f.Id],"Review coverage");
        var advice=new OperationsAdvice("platform.operations-advice/v1","private-dns",a.Digest,"Review",[recommendation],false);
        Assert.True(OperationsRules.Advice(OperationsJson.Write(advice),a,e).AdvisoryOnly);
        foreach(var r in new[]{recommendation with{ResourceId=Other},recommendation with{Operation="DeleteZone"},recommendation with{EvidenceRefs=["invented"]}})
            Assert.Throws<OperationsException>(()=>OperationsRules.Advice(OperationsJson.Write(advice with{Recommendations=[r]}),a,e));
        Assert.Throws<OperationsException>(()=>OperationsRules.Advice(OperationsJson.Write(advice with{AssessmentDigest="wrong"}),a,e));
    }
    [Fact] public void ProposalIsBoundedAndCannotProduceExecutionAuthority()
    {
        var e=E(Z(),Z(Other));var a=OperationsRules.Assess("private-dns",e,null,[]);
        var p=OperationsRules.Propose(e,a,"MigrationReview",[Zone],Other,"platform team","Review a consolidation candidate");
        var preview=OperationsRules.Preview(p,a,DateTimeOffset.UtcNow);Assert.False(preview.ExecutionAvailable);Assert.NotEmpty(preview.Blockers);
        Assert.Throws<OperationsException>(()=>OperationsRules.Preview(p with{Rationale="changed"},a,DateTimeOffset.UtcNow));
        Assert.Throws<OperationsException>(()=>OperationsRules.Preview(p,a,DateTimeOffset.UtcNow.AddHours(1)));
        Assert.Throws<OperationsException>(()=>OperationsRules.Propose(e,a,"MigrationReview",[Zone],Zone,"owner","reason"));
    }
    static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value);
    [Fact] public void ProjectionOmitsTxtSecretsMetadataAndProviderCredentials()
    {
        var row=Json(new{id=Zone+"/TXT/secret",name="secret",type="Microsoft.Network/privateDnsZones/TXT",etag="x",properties=new{ttl=60,txtRecords=new[]{new{value=new[]{"SECRET"}}},metadata=new{password="SECRET"}}});
        var projected=PrivateDnsReader.Project(row,Zone);Assert.DoesNotContain("SECRET",OperationsJson.Write(projected));Assert.Contains("Unsupported",projected.Properties["valueCoverage"]);
    }
    [Fact] public void RecordSetsNormalizeOrderAndDeduplicateValues()
    {
        var row=Json(new{id=Zone+"/A/app",name="app",type="Microsoft.Network/privateDnsZones/A",properties=new{ttl=60,aRecords=new[]{new{ipv4Address="10.0.0.2"},new{ipv4Address="10.0.0.1"},new{ipv4Address="10.0.0.2"}}}});
        Assert.Equal("[\"10.0.0.1\",\"10.0.0.2\"]",PrivateDnsReader.Project(row,Zone).Properties["values"]);
    }
    sealed class Handler(Func<HttpRequestMessage,int,HttpResponseMessage> respond):HttpMessageHandler
    {
        public int Calls;public List<string> Paths=[];
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request,CancellationToken ct){Paths.Add(request.RequestUri!.AbsolutePath);return Task.FromResult(respond(request,++Calls));}
    }
    static HttpResponseMessage Response(object value,HttpStatusCode code=HttpStatusCode.OK)=>new(code){Content=new StringContent(JsonSerializer.Serialize(value))};
    sealed class Tokens : ITokenProvider { public Task<string> Token(BrowserSession s,string audience)=>Task.FromResult("test-only"); }
    const string Tenant = "99999999-9999-9999-9999-999999999999";
    [Fact] public async Task ScopePickerUsesAuthenticatedTenantAndReturnedSubscriptionsWithoutCatalogBinding()
    {
        using var handler=new Handler((r,n)=>Response(new{value=new[]{
            new{subscriptionId=Sub,tenantId=Tenant,state="Enabled",displayName="Returned by Azure"},
            new{subscriptionId="22222222-2222-2222-2222-222222222222",tenantId="other",state="Enabled",displayName="Wrong tenant"},
            new{subscriptionId="33333333-3333-3333-3333-333333333333",tenantId=Tenant,state="Disabled",displayName="Disabled"}}}));
        using var s=new BrowserSession(new());s.Connected["azure"]=true;s.AzureTenantId=Tenant;
        var service=new OperationsService(new(new(handler)),new Tokens(),new());
        var result=await service.Scopes(s,CancellationToken.None);
        Assert.Equal(Tenant,result.TenantId);Assert.Equal(Sub,Assert.Single(result.Subscriptions).Id);Assert.NotEmpty(result.Exclusions);
        Assert.All(handler.Paths,p=>Assert.Equal("/subscriptions",p));
        await Assert.ThrowsAsync<PortalException>(()=>service.Collect(s,new(["44444444-4444-4444-4444-444444444444"]),CancellationToken.None));
        Assert.All(handler.Paths,p=>Assert.Equal("/subscriptions",p));
    }
    [Fact] public async Task IncompleteSubscriptionEnumerationBlocksCollection()
    {
        using var handler=new Handler((r,n)=>Response(new{},HttpStatusCode.Forbidden));using var s=new BrowserSession(new());s.Connected["azure"]=true;s.AzureTenantId=Tenant;
        var service=new OperationsService(new(new(handler)),new Tokens(),new());
        await Assert.ThrowsAsync<PortalException>(()=>service.Collect(s,new([Sub]),CancellationToken.None));Assert.Single(handler.Paths);
    }
    [Fact] public async Task CollectorFollowsOnlySamePathPaginationAndNeverForwardsTokenElsewhere()
    {
        var path=Scope+"/providers/Microsoft.Network/privateDnsZones";
        using var handler=new Handler((r,n)=>Response(new{value=Array.Empty<object>(),nextLink="https://evil.example/steal"}));
        var result=await new PrivateDnsReader(new(handler)).List("test-only",path+"?api-version=2024-06-01",Scope,"Zones",CancellationToken.None);
        Assert.Equal("Partial",result.Coverage.Status);Assert.Equal(1,handler.Calls);
    }
    [Theory][InlineData(403,"Denied")][InlineData(404,"Failed")][InlineData(429,"Failed")]
    public async Task DeniedMissingAndThrottledCollectionsAreNotEmptySuccess(int code,string status)
    {
        using var handler=new Handler((r,n)=>Response(new{},(HttpStatusCode)code));
        var result=await new PrivateDnsReader(new(handler)).List("test-only",Scope+"/providers/Microsoft.Network/privateDnsZones?api-version=2024-06-01",Scope,"Zones",CancellationToken.None);
        Assert.Equal(status,result.Coverage.Status);Assert.False(OperationsJson.Complete(result.Coverage));
    }
    [Fact] public async Task SuccessfulEmptyAndPaginationHaveExplicitCoverage()
    {
        var path=Scope+"/providers/Microsoft.Network/privateDnsZones";
        using var handler=new Handler((r,n)=>Response(new{value=Array.Empty<object>(),nextLink=n==1?"https://management.azure.com"+path+"?api-version=2024-06-01&skip=1":null}));
        var result=await new PrivateDnsReader(new(handler)).List("test-only",path+"?api-version=2024-06-01",Scope,"Zones",CancellationToken.None);
        Assert.Equal("SucceededEmpty",result.Coverage.Status);Assert.Equal(2,result.Coverage.Pages);
    }
    [Fact] public async Task FullCollectorRejectsOutOfScopeRecordsAndPreservesCoverage()
    {
        using var handler=new Handler((r,n)=>r.RequestUri!.AbsolutePath.EndsWith("/privateDnsZones")?Response(new{value=new[]{new{id=Zone.Replace(Sub,"22222222-2222-2222-2222-222222222222"),name="example.internal",type="Microsoft.Network/privateDnsZones",properties=new{}}}}):Response(new{value=Array.Empty<object>()}));
        var result=await new PrivateDnsReader(new(handler)).Collect("test-only","tenant",[Sub],CancellationToken.None);
        Assert.Empty(result.Resources);Assert.Contains(result.Coverage,c=>c.Status=="Partial");OperationsJson.Verify(result);
    }
    [Fact] public async Task CancellationDoesNotBecomeEmptyEvidence()
    {
        using var handler=new Handler((r,n)=>Response(new{value=Array.Empty<object>()}));using var cts=new CancellationTokenSource();cts.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(()=>new PrivateDnsReader(new(handler)).Collect("test-only","tenant",[Sub],cts.Token));Assert.Equal(0,handler.Calls);
    }
    [Fact] public void NewWorkflowsReuseCurrentPolicyAndHavePinnedSkills()
    {
        Assert.All(new[]{"private-dns","drift"},id=>{var w=AgentWorkflows.Find(id);Assert.Equal("operations",w.Scope);Assert.Equal("azure",w.Audience);});
        Assert.Equal("gpt-6.1-sol",AgentPolicy.Model);Assert.Equal("ultra",AgentPolicy.Effort);Assert.Equal("default",AgentPolicy.Tier);
    }
}
