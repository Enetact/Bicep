using System.IO.Compression;
using System.Security.Cryptography;
using System.Text.Json;
using System.Text.Json.Nodes;
using SelfService.Portal;
using Xunit;
namespace SelfService.Portal.Tests;

public sealed class NamingArtifactTests
{
    [Theory][InlineData("")][InlineData("hash")][InlineData("source")][InlineData("scope")]
    public async Task RegeneratesReviewedSourceAndRejectsChanges(string fault)
    {
        var directory=new DirectoryInfo(AppContext.BaseDirectory);
        while(directory!=null&&!File.Exists(Path.Combine(directory.FullName,"config/workloads.json")))directory=directory.Parent;
        var root=directory!.FullName;
        var sandbox=Path.Combine(root,"artifacts/naming-artifact-tests",Guid.NewGuid().ToString("N"));
        foreach(var folder in new[]{"scripts/naming","config","self-service/instances"})Directory.CreateDirectory(Path.Combine(sandbox,folder));
        foreach(var file in Directory.GetFiles(Path.Combine(root,"scripts/naming"),"*.mjs"))File.Copy(file,Path.Combine(sandbox,"scripts/naming",Path.GetFileName(file)));
        foreach(var file in new[]{"naming-policy.json","workloads.json"})File.Copy(Path.Combine(root,"config",file),Path.Combine(sandbox,"config",file));
        var options=new PortalOptions{RepositoryRoot=sandbox};
        var product=new Catalog(new(){RepositoryRoot=root}).Products.Single(p=>p.Id=="storage");var target=product.Targets.Single(t=>t.Environment=="dev");
        var intent=JsonNode.Parse(File.ReadAllText(Path.Combine(root,"tests/naming/request.example.json")))!["intent"]!;
        intent["subscriptionId"]=target.SubscriptionId;
        var request=JsonSerializer.SerializeToElement(new{workloadType=product.Type,intent});
        var draft=await new NamingPlan(options).Generate(request,CancellationToken.None);
        var content=JsonSerializer.SerializeToUtf8Bytes(draft);
        if(fault=="source")intent["stackAcronym"]="new";
        File.WriteAllText(Path.Combine(sandbox,"self-service/instances/registry.json"),JsonSerializer.Serialize(new{instances=new[]{new{workloadType=product.Type,intent}}}));
        using var bytes=new MemoryStream();
        using(var zip=new ZipArchive(bytes,ZipArchiveMode.Create,true)){
            using(var stream=zip.CreateEntry("deployment-preview/naming-draft.json").Open())stream.Write(content);
            using var receipt=zip.CreateEntry("deployment-preview/preview-inputs.json").Open();
            JsonSerializer.Serialize(receipt,new{files=new Dictionary<string,string>{{"naming-draft.json",fault=="hash"?"bad":Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()}}});
        }
        if(fault=="scope")target=target with{SubscriptionId="33333333-3333-4333-8333-333333333333"};
        if(fault!="")await Assert.ThrowsAsync<PortalException>(()=>NamingArtifact.ResourceGroup(bytes.ToArray(),options,product,target,CancellationToken.None));
        else Assert.Equal(draft.GetProperty("parameterPatch").GetProperty("parameters").GetProperty("workloadResourceGroupName").GetProperty("value").GetString(),await NamingArtifact.ResourceGroup(bytes.ToArray(),options,product,target,CancellationToken.None));
    }
}
