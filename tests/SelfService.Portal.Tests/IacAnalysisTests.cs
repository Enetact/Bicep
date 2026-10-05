using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public class IacAnalysisTests
{
    const string Subscription="11111111-1111-4111-8111-111111111111";
    const string Id="/subscriptions/"+Subscription+"/resourceGroups/rg-test/providers/Microsoft.Storage/storageAccounts/data";
    static readonly Product Product=new("storage","private-storage","Storage","Discover","Deploy","discover.yml","deploy.yml","","","",
        [new("storage","dev","selected",Subscription,"private",false)]);
    static JsonElement Json(object value)=>JsonSerializer.SerializeToElement(value);
    static string Hash(byte[] bytes)=>Convert.ToHexString(SHA256.HashData(bytes)).ToLowerInvariant();
    static byte[] Archive(string fault="",bool after=false)
    {
        var resource=new {id=fault=="scope"?Id.Replace("rg-test","rg-other"):Id,type="Microsoft.Storage/storageAccounts",status="Observed",apiVersion="2023-05-01",
            config=fault=="property"?new Dictionary<string,object>{{"password","SECRET"}}:fault=="nested"?new Dictionary<string,object>{{"properties.networkAcls.virtualNetworkRules.$count",1},{"properties.networkAcls.virtualNetworkRules.0.id",Id+"/fixture"}}:new Dictionary<string,object>{{"properties.publicNetworkAccess","Disabled"}},password="SECRET"};
        var observed=new{inventoryStatus="Listed",resources=new[]{resource},coverage="inventory",startedUtc="2026-10-04T00:00:00Z",completedUtc="2026-10-04T00:00:01Z",issues=Array.Empty<string>(),audit=new[]{new{tool="group_resource_list",status="Succeeded"}}};
        var provenance=new{product="storage",environment="dev",runId=fault=="run"?"45":"44",sourceCommit=fault=="commit"?"wrong":"abc",pipelineId=fault=="pipeline"?"2":"1",repositoryId=fault=="repo"?"wrong":"repo",region="eastus2",tenantId="tenant",inputSha256=new string('a',64),templateSha256=new string('b',64)};
        var planned=new{status="Succeeded",coverage="prediction",resources=new[]{new{id=Id,type="Microsoft.Storage/storageAccounts",action="Modify",certainty="definite",status="Predicted",config=new Dictionary<string,object>{{"properties.publicNetworkAccess","Disabled"}}}}};
        var report=new{schema="platform.iac-analysis/v1",scope=new{subscriptionId=Subscription,resourceGroup="rg-test"},provenance,
            collector=new{name="Microsoft Azure MCP",version=fault=="collector"?"other":"2.0.5",readOnly=true},
            before=observed,planned,after=after||fault=="false-after"?observed:null,comparison=Array.Empty<object>(),updatedUtc="2026-10-04T00:00:01Z",limitations=new[]{"inventory"},deploymentAuthorized=false,password="SECRET"};
        var reportNode=JsonSerializer.SerializeToNode(report)!;
        if(fault is "nested" or "nested-blocker") {
            reportNode["qualification"]=JsonSerializer.SerializeToNode(new{profile="nested-arm/v1",status="Blocked",profileSha256=new string('a',64),beforeFingerprint=new string('b',64),blockers=new[]{new{code=fault=="nested-blocker"?"ForgedCode":"DesiredStateUnresolved",resourceId=Id}},password="SECRET"});
            reportNode["before"]!["probes"]=JsonSerializer.SerializeToNode(new[]{new{id=Id+"/blobServices/default",parentId=Id,apiVersion="2023-05-01",profile="nested-arm/v1",method="GET",status="Denied",pages=1,observed=0,observedUtc="2026-10-04T00:00:00Z",password="SECRET"}});
        }
        var files=new Dictionary<string,byte[]>{{"analysis.json",JsonSerializer.SerializeToUtf8Bytes(reportNode)},{"README.md",Encoding.UTF8.GetBytes("Evidence")},{"before.bicep",Encoding.UTF8.GetBytes("// existing inventory")}};
        var receipt=JsonSerializer.SerializeToUtf8Bytes(new{schema="platform.iac-receipt/v1",provenance,files=files.ToDictionary(f=>f.Key,f=>Hash(f.Value)),createdUtc="2026-10-04T00:00:01Z",executionAuthorized=false});
        if(fault=="tamper")files["analysis.json"]=Encoding.UTF8.GetBytes("{}");
        using var bytes=new MemoryStream();
        using(var zip=new ZipArchive(bytes,ZipArchiveMode.Create,true)){
            var prefix=after?"deployment-result/iac/":"deployment-preview/azure/iac/";
            void Add(string name,byte[] content){using var stream=zip.CreateEntry(prefix+name).Open();stream.Write(content);}
            foreach(var f in files)Add(f.Key,f.Value);
            if(fault!="missing")Add("receipt.json",receipt);
            if(fault=="duplicate")Add("analysis.json",files["analysis.json"]);
        }return bytes.ToArray();
    }
    static Task<JsonElement> Read(string fault="",bool after=false)=>IacAnalysis.Read(Archive(fault,after),Product,44,"abc",1,"repo","rg-test",after,CancellationToken.None);
    [Theory]
    [InlineData("scope")][InlineData("property")][InlineData("run")][InlineData("commit")][InlineData("pipeline")][InlineData("repo")]
    [InlineData("collector")][InlineData("false-after")][InlineData("tamper")][InlineData("missing")][InlineData("duplicate")]
    [InlineData("nested-blocker")]
    public async Task RejectsWrongProducerTamperingUnsafeProjectionAndFalseAfter(string fault)=>await Assert.ThrowsAsync<PortalException>(()=>Read(fault));
    [Fact] public async Task ProjectsBoundEvidenceAndExcludesUnexpectedFields()
    {
        var report=await Read();Assert.DoesNotContain("SECRET",report.ToString());Assert.False(report.GetProperty("deploymentAuthorized").GetBoolean());
        Assert.Equal(JsonValueKind.Null,report.GetProperty("after").ValueKind);Assert.Equal("abc",report.GetProperty("provenance").GetProperty("sourceCommit").GetString());
        Assert.Single(IacAnalysis.Graph(report,"planned").Nodes);Assert.Throws<PortalException>(()=>IacAnalysis.Graph(report,"after"));
    }
    [Fact] public async Task AfterArtifactHasDistinctObservedPhase()
    {
        var report=await Read(after:true);var graph=IacAnalysis.Graph(report,"after");Assert.Equal("Observed",graph.Nodes.Single().Status);
        Assert.Empty(graph.Edges);Assert.Equal("Predicted Modify",IacAnalysis.Graph(report,"planned").Nodes.Single().Status);
    }
    [Fact] public async Task AgentDiagramCannotInventConnectionsOrDirectives()
    {
        var graph=IacAnalysis.Graph(await Read(),"planned");
        Assert.Equal("Rejected",EvidenceDiagram.Validate("```mermaid\ngraph TB\nR001[\"data\"]\nR001 -->|made up| R002\n```",graph).Status);
        Assert.Equal("Rejected",EvidenceDiagram.Validate("```mermaid\ngraph TB\nR001[\"data\"]\nclick R001 href \"https://evil.test\"\n```",graph).Status);
        Assert.Equal("Validated",EvidenceDiagram.Validate("```mermaid\ngraph TB\nR001[\"data\"]\n```",graph).Status);
    }
    [Fact] public void RegisteredWorkflowUsesActualVisualizerAndAdoEvidence()
    {
        var w=AgentWorkflows.Find("iac");Assert.Equal("azure--azure-resource-visualizer",w.SkillId);Assert.Equal("ado",w.Audience);Assert.Equal("iac",w.Scope);
        var session=new BrowserSession(new PortalOptions());Assert.False(AgentWorkflows.Authorized(session,w));
    }
    [Fact] public async Task NestedProfileSurvivesArtifactProjection()
    {
        var report=await Read("nested");var config=report.GetProperty("before").GetProperty("resources")[0].GetProperty("config");
        Assert.Equal(1,config.GetProperty("properties.networkAcls.virtualNetworkRules.$count").GetInt32());
        Assert.Equal(Id+"/fixture",config.GetProperty("properties.networkAcls.virtualNetworkRules.0.id").GetString());
        Assert.Equal("Blocked",report.GetProperty("qualification").GetProperty("status").GetString());
        Assert.Equal("Denied",report.GetProperty("probes")[0].GetProperty("status").GetString());
        Assert.DoesNotContain("SECRET",report.ToString());
    }
    [Theory]
    [InlineData("properties.delegations.0.properties.serviceName",true)]
    [InlineData("properties.subnets.$count",true)]
    [InlineData("properties.serviceEndpoints.1.locations.0",true)]
    [InlineData("properties.privateDnsZoneConfigs.0.properties.privateDnsZoneId",true)]
    [InlineData("properties.siteConfig.appSettings.0.value",false)]
    [InlineData("properties.subnets.0.properties.password",false)]
    public void SharedEmbeddedProfileAcceptsOnlyReviewedNestedPaths(string path,bool allowed)=>Assert.Equal(allowed,IacPropertyProfile.Allows(path));
    [Fact] public void InvalidCoverageCannotEnterAgentEvidence()
    {
        Assert.Throws<PortalException>(()=>IacPropertyProfile.Coverage(Json(new{propertyCoverage=new[]{new{path="password",status="Observed"}}})));
        Assert.Throws<PortalException>(()=>IacPropertyProfile.Coverage(Json(new{propertyCoverage=new[]{new{path="location",status="VerifiedEverything"}}})));
    }
}
