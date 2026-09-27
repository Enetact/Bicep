using System.IO.Compression;
using System.Text;
using System.Text.Json;
using SelfService.Tagging;

namespace SelfService.Portal;

public record TagScopeRequest(string SubscriptionId);
public record TagDraftRequest(string EvidenceId,TagEdit[] Edits);
public record TagQueueRequest(string Operation,string SubscriptionId,string? EvidenceId=null,TagEdit[]? Edits=null);
public record PendingTagQueue(TagQueueRequest Request,string ProfileHash,object Payload,int DefinitionId,DateTimeOffset Expires);

public sealed class TaggingService(HttpClient http,ITokenProvider identity,Catalog catalog,PortalOptions options,AdoGateway ado)
{
    public TagProfile Profile(string? subscription=null)
    {
        var p=TagJson.Read<TagProfile>(File.ReadAllText(Path.Combine(catalog.Root,"config/tag-governance.json")));
        if(subscription is null||subscription==p.SubscriptionId)return p;
        if(!Guid.TryParse(subscription,out var id))throw new TagException("Invalid subscription.");
        return p with{TenantId=options.TenantId,SubscriptionId=id.ToString(),Enabled=false,ExternalResourceIds=[],SourceTargets=[],QualifiedWriteTypes=[],GovernanceReviewed=false,GovernanceDigest=""};
    }
    AzureTags Azure(BrowserSession s)=>new(http,async ct=>await identity.Token(s,"azure"));
    public object Configuration()=>new{profile=Profile().Id,subscriptionId=Profile().SubscriptionId,enabled=Profile().Enabled,ruleVersion=Profile().RuleVersion,requiredKeys=Profile().RequiredKeys,customKeyPrefix="custom.",workflow="tagging",limits=new{edits=100,evidenceMinutes=30,requestBytes=32768},note="Visible ARM inventory. Unsupported/unknown types and unregistered ownership remain blocked. Apply requires a qualified private ADO project and protected profile."};
    public async Task<object[]> Scopes(BrowserSession s,CancellationToken ct)=>await Azure(s).Scopes(options.TenantId,ct);
    public TagInventory Evidence(BrowserSession s,string id)
    {
        if(!s.Connected.ContainsKey("azure"))throw new PortalException("Connect Azure before reading tag evidence.",401);
        if(!s.TagReports.TryGetValue(id,out var inventory)||DateTimeOffset.UtcNow-inventory.ObservedUtc>TimeSpan.FromMinutes(30))throw new PortalException("Tag evidence is missing, stale or belongs to another browser. Discover again.",409);
        TagRules.ValidateInventory(inventory,Profile(inventory.SubscriptionId));return inventory;
    }
    async Task Store(BrowserSession s,TagInventory inventory,CancellationToken ct)
    {
        if(s.TagReports.Count>=8)foreach(var old in s.TagReports.OrderBy(x=>x.Value.ObservedUtc).Take(s.TagReports.Count-7))s.TagReports.TryRemove(old.Key,out _);
        s.TagReports[inventory.Id]=inventory;var folder=Path.Combine(catalog.Root,"artifacts/portal-tags",inventory.Id);Directory.CreateDirectory(folder);
        await File.WriteAllTextAsync(Path.Combine(folder,"inventory.json"),TagJson.Write(inventory),ct);
    }
    public async Task<object> Discover(BrowserSession s,TagScopeRequest request,CancellationToken ct)
    {
        var epoch=Interlocked.Read(ref s.NetworkEpoch);using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromMinutes(15));
        var p=Profile(request.SubscriptionId);var inventory=await Azure(s).Discover(p,"Browser Azure identity",timeout.Token);
        if(epoch!=Interlocked.Read(ref s.NetworkEpoch)||!s.Connected.ContainsKey("azure"))throw new PortalException("Session disconnected during discovery.",401);
        await Store(s,inventory,timeout.Token);return View(inventory,p);
    }
    public static object View(TagInventory inventory,TagProfile p)=>new{inventory,findings=TagRules.Analyze(inventory,p),profile=p.Id,p.Enabled,coverage="Visible ARM objects; unreadable and unsupported provider children are not inferred absent."};
    public object Read(BrowserSession s,string id){var x=Evidence(s,id);return View(x,Profile(x.SubscriptionId));}
    public object Draft(BrowserSession s,TagDraftRequest request)
    {
        var x=Evidence(s,request.EvidenceId);var plan=TagRules.BuildPlan(x,new("platform.tag-change-request/v1",x.Digest,request.Edits,x.RunId),Profile(x.SubscriptionId),DateTimeOffset.UtcNow);
        var proposals=plan.Changes.Where(c=>c.Route=="SourceChange").GroupBy(c=>c.SourceTarget).Select(g=>{
            if(g.Key is null||!System.Text.RegularExpressions.Regex.IsMatch(g.Key,@"^self-service/targets/[a-z0-9-]+\.(dev|qa|uat|prod)\.json$"))throw new TagException("Source target path is not a registered target.");
            var path=Path.Combine(catalog.Root,g.Key);var target=TagJson.Read<Dictionary<string,JsonElement>>(File.ReadAllText(path));
            var custom=target.TryGetValue("parameterOverrides",out var overrides)&&overrides.TryGetProperty("customTags",out var tags)?TagJson.Read<Dictionary<string,string>>(tags.GetRawText()):new();
            var proposed=new Dictionary<string,object>();
            foreach(var key in g.GroupBy(c=>c.Key,StringComparer.OrdinalIgnoreCase)){
                if(key.Select(c=>c.Value).Distinct().Count()!=1)throw new TagException("Resources in one source target require consistent tag values.");
                var change=key.First();if(change.Action=="NoChange")continue;
                if(change.Key.Equals("owner",StringComparison.OrdinalIgnoreCase))proposed["owner"]=change.Value;
                else if(change.Key.Equals("costCenter",StringComparison.OrdinalIgnoreCase))proposed["costCenter"]=change.Value;
                else if(change.Key.StartsWith("custom.",StringComparison.Ordinal)||change.Key=="criticality")custom[change.Key]=change.Value;
                else throw new TagException("This source tag requires a dedicated platform/catalog change, not a custom tag override.");
            }
            if(custom.Count>8)throw new TagException("Source compositions support at most eight custom tags to preserve DNS tag limits.");
            proposed["customTags"]=custom;
            return new{target=g.Key,parameterOverrides=proposed,note="Reviewed source proposal only. Merge with existing overrides; affects all resources composed by this target. Follow workload Preview/Deploy. No GitHub write performed."};
        }).ToArray();
        return new{plan,sourceProposals=proposals,advisoryOnly=true};
    }
    string Api(string path)=>options.AdoBase+"/_apis/"+path+(path.Contains('?')?"&":"?")+"api-version=7.1";
    async Task PrivateProject(BrowserSession s)
    {
        var project=await ado.Send(s,"ado",$"https://dev.azure.com/{Uri.EscapeDataString(options.Organization)}/_apis/projects/{Uri.EscapeDataString(options.Project)}?api-version=7.1");
        if(AzureTags.Text(project,"visibility")!="private")throw new PortalException("Tag inventories/requests require a verified private ADO project. Use browser discovery locally.",403);
    }
    async Task<JsonElement> Definition(BrowserSession s,bool discover)
    {
        await PrivateProject(s);var name=discover?"Discover - Tags":"Tags - Preview and Apply";var yaml=discover?"azure-pipelines-tags-discover.yml":"azure-pipelines-tags.yml";
        var list=await ado.Send(s,"ado",Api("build/definitions?name="+Uri.EscapeDataString(name)));var rows=list.GetProperty("value").EnumerateArray().Where(x=>AzureTags.Text(x,"name")==name).ToArray();
        if(rows.Length!=1)throw new PortalException($"Register exactly one {name} pipeline through ADO setup.",409);
        var d=await ado.Send(s,"ado",Api($"build/definitions/{rows[0].GetProperty("id").GetInt32()}"));var repo=d.GetProperty("repository");
        if(AzureTags.Text(d.GetProperty("process"),"yamlFilename").TrimStart('/')!=yaml||AzureTags.Text(repo,"id")!="Enetact/Bicep"||AzureTags.Text(repo,"type")!="GitHub"||AzureTags.Text(repo,"defaultBranch")!="refs/heads/main")throw new PortalException("Tag pipeline source binding differs from the registered GitHub main entry.",403);return d;
    }
    public async Task<object> Load(BrowserSession s,int runId,CancellationToken ct)
    {
        var epoch=Interlocked.Read(ref s.NetworkEpoch);
        var d=await Definition(s,true);var run=await ado.Send(s,"ado",Api($"build/builds/{runId}"));
        if(run.GetProperty("definition").GetProperty("id").GetInt32()!=d.GetProperty("id").GetInt32()||AzureTags.Text(run,"sourceBranch")!="refs/heads/main"||AzureTags.Text(run,"result")!="succeeded"||AzureTags.Text(run.GetProperty("repository"),"id")!="Enetact/Bicep")throw new PortalException("Select a successful Tags discovery from the registered main pipeline.",403);
        var artifact=await ado.Send(s,"ado",Api($"pipelines/{d.GetProperty("id").GetInt32()}/runs/{runId}/artifacts?artifactName=tag-discovery&$expand=signedContent"));
        var uri=PreviewDiagram.DownloadUri(artifact.GetProperty("signedContent").GetProperty("url").GetString());
        using var response=await http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,ct);if(!response.IsSuccessStatusCode)throw new PortalException("Tag artifact download failed.",502);
        using var stream=await response.Content.ReadAsStreamAsync(ct);var bytes=await PreviewDiagram.Bounded(stream,32*1024*1024,ct);
        using var zip=new ZipArchive(new MemoryStream(bytes));var entries=zip.Entries.Where(e=>e.FullName.Replace('\\','/').TrimStart('/') is "inventory.json" or "tag-discovery/inventory.json").ToArray();
        if(entries.Length!=1||entries[0].Length>24*1024*1024)throw new PortalException("Tag artifact inventory missing, duplicated or oversized.",502);
        using var input=entries[0].Open();var content=await PreviewDiagram.Bounded(input,24*1024*1024,ct);var x=TagJson.Read<TagInventory>(Encoding.UTF8.GetString(content));
        TagRules.ValidateInventory(x,Profile());if(x.RunId!=runId||x.SourceCommit!=AzureTags.Text(run,"sourceVersion"))throw new PortalException("Tag inventory producer binding differs.",403);
        if(epoch!=Interlocked.Read(ref s.NetworkEpoch)||!s.Connected.ContainsKey("azure"))throw new PortalException("Connect Azure before loading tag evidence; session may have disconnected.",401);
        await Store(s,x,ct);return View(x,Profile());
    }
    public async Task<object> Review(BrowserSession s,TagQueueRequest request)
    {
        if(request.SubscriptionId!=Profile().SubscriptionId||request.Operation is not("Discover" or "Preview only" or "Preview and apply"))throw new TagException("Select a registered tagging operation/subscription.");
        var p=Profile();var d=await Definition(s,request.Operation=="Discover");object parameters;
        if(request.Operation=="Discover")parameters=new{profile=p.Id};else{
            var inventory=Evidence(s,request.EvidenceId??"");if(inventory.RunId is not>0)throw new TagException("Load a saved ADO discovery before pipeline Preview. Browser evidence is for local analysis.");
            var edit=new TagRequest("platform.tag-change-request/v1",inventory.Digest,request.Edits??[],inventory.RunId);var plan=TagRules.BuildPlan(inventory,edit,p,DateTimeOffset.UtcNow);
            if(plan.Blockers.Length>0||plan.Changes.Any(x=>x.Route!="External"))throw new TagException("Resolve draft blockers or use its source-change proposal.");
            if(request.Operation=="Preview and apply"&&!p.Enabled)throw new TagException("Tag Apply profile is disabled; platform onboarding required.");
            var encoded=Convert.ToBase64String(Encoding.UTF8.GetBytes(TagJson.Write(edit))).TrimEnd('=').Replace('+','-').Replace('/','_');if(encoded.Length>32768)throw new TagException("Request exceeds 32 KiB; choose fewer edits.");
            parameters=new{profile=p.Id,operation=request.Operation,discoveryRunId=inventory.RunId.Value,requestBase64=encoded};
        }
        var payload=new{resources=new{repositories=new{self=new{refName="refs/heads/main"}}},templateParameters=parameters};
        foreach(var x in s.TagPending.Where(x=>x.Value.Expires<DateTimeOffset.UtcNow))s.TagPending.TryRemove(x.Key,out _);
        if(s.TagPending.Count>=4)throw new PortalException("Too many pending tagging reviews.",429);
        var ticket=Convert.ToHexString(System.Security.Cryptography.RandomNumberGenerator.GetBytes(24));s.TagPending[ticket]=new(request,TagRules.ProfileHash(p),payload,d.GetProperty("id").GetInt32(),DateTimeOffset.UtcNow.AddMinutes(5));
        return new{ticket,payload,warning="Queues the exact reviewed request in private ADO. Requests are not secret. Apply requires separate protected approval; no tag writes occur in this portal."};
    }
    async Task<string> ResultArtifact(BrowserSession s,int definition,int run,string artifactName,string file,CancellationToken ct)
    {
        var artifact=await ado.Send(s,"ado",Api($"pipelines/{definition}/runs/{run}/artifacts?artifactName={artifactName}&$expand=signedContent"));
        var uri=PreviewDiagram.DownloadUri(artifact.GetProperty("signedContent").GetProperty("url").GetString());
        using var response=await http.GetAsync(uri,HttpCompletionOption.ResponseHeadersRead,ct);if(!response.IsSuccessStatusCode)throw new PortalException("Result artifact not available.",409);
        using var stream=await response.Content.ReadAsStreamAsync(ct);using var zip=new ZipArchive(new MemoryStream(await PreviewDiagram.Bounded(stream,16*1024*1024,ct)));
        var entries=zip.Entries.Where(e=>e.FullName.Replace('\\','/').TrimStart('/')==file||e.FullName.Replace('\\','/').TrimStart('/')==artifactName+"/"+file).ToArray();
        if(entries.Length!=1||entries[0].Length>4*1024*1024)throw new PortalException("Result artifact missing/ambiguous/oversized.",409);
        using var input=entries[0].Open();return Encoding.UTF8.GetString(await PreviewDiagram.Bounded(input,4*1024*1024,ct));
    }
    public async Task<object> Results(BrowserSession s,int runId,string kind,CancellationToken ct)
    {
        if(kind is not("preview" or "apply" or "verify"))throw new TagException("Select Preview, Apply or Verify evidence.");
        var d=await Definition(s,false);var definition=d.GetProperty("id").GetInt32();var run=await ado.Send(s,"ado",Api($"build/builds/{runId}"));
        if(run.GetProperty("definition").GetProperty("id").GetInt32()!=definition||AzureTags.Text(run,"sourceBranch")!="refs/heads/main"||AzureTags.Text(run.GetProperty("repository"),"id")!="Enetact/Bicep")throw new PortalException("Tag results producer differs.",403);
        var plan=TagJson.Read<TagPlan>(await ResultArtifact(s,definition,runId,"tag-preview","plan.json",ct));
        if(plan.Schema!="platform.tag-plan/v1"||plan.Digest!=TagRules.PlanHash(plan))throw new TagException("Result plan digest differs.");
        foreach(var c in plan.Changes)TagRules.Id(c.ResourceId,Profile().SubscriptionId);
        TagReceipt? receipt=null;
        if(kind!="preview"){
            receipt=TagJson.Read<TagReceipt>(await ResultArtifact(s,definition,runId,kind=="apply"?"tag-apply":"tag-verification",kind=="apply"?"receipt.json":"verification.json",ct));
            if(receipt.Schema!="platform.tag-receipt/v1"||receipt.PlanDigest!=plan.Digest||receipt.Results.Any(r=>!plan.Before.ContainsKey(r.ResourceId)))throw new TagException("Receipt differs from saved plan.");
        }
        return new{runId,kind,pipelineState=AzureTags.Text(run,"status"),pipelineResult=AzureTags.Text(run,"result"),plan,receipt,url=options.AdoBase+$"/_build/results?buildId={runId}",note="Historical saved evidence; no replay or current-state claim. Rediscover for a new draft."};
    }
    public async Task<object> Queue(BrowserSession s,string ticket)
    {
        if(!s.TagPending.TryRemove(ticket,out var pending)||pending.Expires<DateTimeOffset.UtcNow||pending.ProfileHash!=TagRules.ProfileHash(Profile()))throw new PortalException("Tag review changed/expired/used. Check existing runs before retrying.",409);
        if(pending.Request.Operation!="Discover")Evidence(s,pending.Request.EvidenceId??"");
        var d=await Definition(s,pending.Request.Operation=="Discover");if(d.GetProperty("id").GetInt32()!=pending.DefinitionId)throw new PortalException("Pipeline definition changed.",409);
        var run=await ado.Send(s,"ado",Api($"pipelines/{pending.DefinitionId}/runs"),pending.Payload);var id=run.GetProperty("id").GetInt32();return new{id,url=options.AdoBase+$"/_build/results?buildId={id}"};
    }
}
