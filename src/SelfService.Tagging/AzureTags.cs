using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SelfService.Tagging;

// Shared fixed-route ARM adapter. Only Apply uses PATCH; no resource PUT, DELETE or deployment API.
public sealed class AzureTags(HttpClient http,Func<CancellationToken,Task<string>> token)
{
    const string Arm="https://management.azure.com";
    public async Task<(JsonElement Body,string? RequestId)> Send(string path,CancellationToken ct,object? patch=null)
    {
        if(!path.StartsWith('/')||path.Contains('#')||path.Contains('\\'))throw new TagException("Invalid ARM path.");
        using var timeout=CancellationTokenSource.CreateLinkedTokenSource(ct);timeout.CancelAfter(TimeSpan.FromSeconds(45));
        for(var attempt=0;;attempt++){
            using var request=new HttpRequestMessage(patch is null?HttpMethod.Get:HttpMethod.Patch,Arm+path);
            request.Headers.Authorization=new AuthenticationHeaderValue("Bearer",await token(timeout.Token));
            if(patch is not null)request.Content=JsonContent.Create(patch);
            using var response=await http.SendAsync(request,HttpCompletionOption.ResponseHeadersRead,timeout.Token);
            if(patch is null && (int)response.StatusCode is 429 or 503 && attempt<2){await Task.Delay(TimeSpan.FromSeconds(Math.Clamp(response.Headers.RetryAfter?.Delta?.TotalSeconds??(attempt+1)*2,1,10)),timeout.Token);continue;}
            if(!response.IsSuccessStatusCode)throw new TagException($"ARM HTTP {(int)response.StatusCode}; resource state is unknown, not empty.");
            using var stream=await response.Content.ReadAsStreamAsync(timeout.Token);using var bytes=new MemoryStream();var buffer=new byte[8192];int n;
            while((n=await stream.ReadAsync(buffer,timeout.Token))>0){if(bytes.Length+n>16*1024*1024)throw new TagException("ARM response exceeded limit.");bytes.Write(buffer,0,n);}
            return(JsonSerializer.Deserialize<JsonElement>(bytes.ToArray()),response.Headers.TryGetValues("x-ms-request-id",out var values)?values.FirstOrDefault():null);
        }
    }
    public async Task<JsonElement[]> List(string path,CancellationToken ct,int limit=50000)
    {
        var all=new List<JsonElement>();var seen=new HashSet<string>();var initial=new Uri(Arm+path);string? next=path;
        for(int pages=0;next is not null;pages++){
            if(pages>=100||!seen.Add(next))throw new TagException("Collector page limit or repeated continuation; coverage incomplete.");
            var value=(await Send(next,ct)).Body;if(!value.TryGetProperty("value",out var array)||array.ValueKind!=JsonValueKind.Array)throw new TagException("Collector schema invalid.");
            all.AddRange(array.EnumerateArray());if(all.Count>limit)throw new TagException("Collector item limit; select a narrower scope.");
            next=null;if(value.TryGetProperty("nextLink",out var link)&&link.ValueKind!=JsonValueKind.Null&&!string.IsNullOrEmpty(link.GetString())){
                if(!Uri.TryCreate(link.GetString(),UriKind.Absolute,out var uri)||uri.Scheme!="https"||uri.Host!="management.azure.com"||uri.Port!=443||uri.UserInfo!=""||uri.Fragment!=""||!uri.AbsolutePath.Equals(initial.AbsolutePath,StringComparison.OrdinalIgnoreCase))throw new TagException("Unsafe continuation rejected.");next=uri.PathAndQuery;
            }
        }return all.ToArray();
    }
    public static string Text(JsonElement x,string name)=>x.TryGetProperty(name,out var v)&&v.ValueKind==JsonValueKind.String?v.GetString()!:"";
    public static Dictionary<string,string> Tags(JsonElement x)
    {
        var result=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);
        if(!x.TryGetProperty("tags",out var tags)||tags.ValueKind==JsonValueKind.Null)return result;
        if(tags.ValueKind!=JsonValueKind.Object)throw new TagException("Malformed tags, not empty.");
        foreach(var p in tags.EnumerateObject())if(p.Value.ValueKind!=JsonValueKind.String||!result.TryAdd(p.Name,p.Value.GetString()!))throw new TagException("Invalid tag value or case collision.");return result;
    }
    public async Task<object[]> Scopes(string tenant,CancellationToken ct)
    {
        if(!Guid.TryParse(tenant,out _))throw new TagException("Configure the Azure tenant first.");
        return(await List("/subscriptions?api-version=2022-12-01",ct)).Where(x=>Text(x,"tenantId").Equals(tenant,StringComparison.OrdinalIgnoreCase)&&Text(x,"state")=="Enabled").Select(x=>(object)new{id=Text(x,"subscriptionId"),name=Text(x,"displayName")}).ToArray();
    }
    public async Task VerifySubscription(TagProfile p,CancellationToken ct)
    {
        if(!Guid.TryParse(p.TenantId,out _)||!Guid.TryParse(p.SubscriptionId,out _))throw new TagException("Configure a valid tag profile scope.");
        var s=(await Send($"/subscriptions/{p.SubscriptionId}?api-version=2022-12-01",ct)).Body;
        if(!Text(s,"tenantId").Equals(p.TenantId,StringComparison.OrdinalIgnoreCase)||Text(s,"state")!="Enabled")throw new TagException("Subscription tenant/state could not be qualified.");
    }
    public async Task<string> Governance(TagProfile p,CancellationToken ct)
    {
        var root=$"/subscriptions/{p.SubscriptionId}";
        var policies=await List(root+"/providers/Microsoft.Authorization/policyAssignments?api-version=2022-06-01",ct);
        var locks=await List(root+"/providers/Microsoft.Authorization/locks?api-version=2016-09-01",ct);
        // Without atScope(), include assignments below the subscription as well as inherited ones.
        // Hash referenced rules too: changing a definition need not change its assignment.
        var definitions=new Dictionary<string,JsonElement>(StringComparer.OrdinalIgnoreCase);
        async Task Definition(string id){
            if(definitions.ContainsKey(id))return;
            if(definitions.Count>=500||!System.Text.RegularExpressions.Regex.IsMatch(id,@"^/(subscriptions/[0-9a-fA-F-]{36}/|providers/Microsoft.Management/managementGroups/[A-Za-z0-9_.()-]+/)?providers/Microsoft.Authorization/policy(Set)?Definitions/[A-Za-z0-9_.()-]+$",System.Text.RegularExpressions.RegexOptions.IgnoreCase))throw new TagException("Governance definition reference unsupported or over limit.");
            var value=(await Send(id+"?api-version=2023-04-01",ct)).Body;definitions.Add(id,value);
            if(value.TryGetProperty("properties",out var properties)&&properties.TryGetProperty("policyDefinitions",out var children))foreach(var child in children.EnumerateArray())await Definition(Text(child,"policyDefinitionId"));
        }
        foreach(var policy in policies)await Definition(Text(policy.GetProperty("properties"),"policyDefinitionId"));
        return TagJson.Hash(new{policies=policies.OrderBy(x=>Text(x,"id"),StringComparer.Ordinal).ToArray(),locks=locks.OrderBy(x=>Text(x,"id"),StringComparer.Ordinal).ToArray(),definitions});
    }
    public async Task<TagInventory> Discover(TagProfile p,string source,CancellationToken ct)
    {
        await VerifySubscription(p,ct);var resources=new List<TagResource>();var coverage=new List<TagCoverage>();
        async Task Collect(string name,string path,string? type=null){try{
            var rows=await List(path,ct);foreach(var row in rows){var id=Text(row,"id");var parts=id.Split('/');resources.Add(TagRules.Resource(id,Text(row,"name"),type??Text(row,"type"),parts.Length>4&&parts[3].Equals("resourceGroups",StringComparison.OrdinalIgnoreCase)?parts[4]:"",Text(row,"location"),Tags(row),p));}
            coverage.Add(new(name,rows.Length==0?"SucceededEmpty":"Succeeded",rows.Length,"Visible ARM objects only; provider child/data-plane coverage is not exhaustive."));
        }catch(Exception e)when(e is TagException or HttpRequestException or JsonException){coverage.Add(new(name,"Partial",resources.Count,"Collection could not complete; no empty inference."));}}
        var root=$"/subscriptions/{p.SubscriptionId}";
        await Collect("Resources",root+"/resources?api-version=2021-04-01");
        await Collect("ResourceGroups",root+"/resourcegroups?api-version=2021-04-01","microsoft.resources/resourcegroups");
        try{var tags=await ReadTags(root,ct);resources.Add(TagRules.Resource(root,p.SubscriptionId,"microsoft.resources/subscriptions","","",tags,p));coverage.Add(new("SubscriptionTags","Succeeded",1,"Independent scope tags, not inherited resource tags."));}catch(TagException){coverage.Add(new("SubscriptionTags","Failed",0,"Scope tags unreadable."));}
        string governance="";try{governance=await Governance(p,ct);coverage.Add(new("Governance","Succeeded",1,"Assignment/lock fingerprint only; semantic review is a platform prerequisite."));}catch(TagException){coverage.Add(new("Governance","Failed",0,"Policy or locks unreadable; apply cannot be qualified."));}
        return TagRules.Seal(new("platform.tag-inventory/v1",Guid.NewGuid().ToString("N"),p.TenantId,p.SubscriptionId,source,DateTimeOffset.UtcNow,resources.DistinctBy(r=>r.Id).OrderBy(r=>r.Id).ToArray(),coverage.ToArray(),governance,""));
    }
    public async Task<Dictionary<string,string>> ReadTags(string id,CancellationToken ct)
    {
        var value=(await Send(id+"/providers/Microsoft.Resources/tags/default?api-version=2021-04-01",ct)).Body;
        if(!value.TryGetProperty("properties",out var properties)||properties.ValueKind!=JsonValueKind.Object)throw new TagException("Tags response lacks properties; not empty.");return Tags(properties);
    }
    public async Task<TagPlan> Preview(TagInventory inventory,TagRequest request,TagProfile profile,CancellationToken ct)
    {
        var initial=TagRules.BuildPlan(inventory,request,profile,DateTimeOffset.UtcNow);var blockers=initial.Blockers.ToList();
        foreach(var group in request.Edits.GroupBy(x=>TagRules.Id(x.ResourceId,profile.SubscriptionId))){
            try{var current=await ReadTags(group.Key,ct);if(TagRules.Fingerprint(current)!=initial.Before[group.Key])blockers.Add(group.Key+": tags drifted; discover again.");}catch(TagException){blockers.Add(group.Key+": current tags unreadable.");}
        }
        var governance=await Governance(profile,ct);if(governance!=inventory.GovernanceDigest)blockers.Add("Governance changed; rediscover and review.");
        var result=initial with{Blockers=blockers.Distinct().ToArray()};return result with{Digest=TagRules.PlanHash(result)};
    }
    public async Task<TagReceipt> Apply(TagPlan plan,TagProfile p,Func<TagReceipt,Task> save,CancellationToken ct)
    {
        TagRules.ValidatePlan(plan,p,DateTimeOffset.UtcNow);
        if(!p.Enabled||!p.GovernanceReviewed||p.GovernanceDigest!=plan.GovernanceDigest)throw new TagException("Apply disabled or governance fingerprint not reviewed.");
        await VerifySubscription(p,ct);if(await Governance(p,ct)!=plan.GovernanceDigest)throw new TagException("Governance drift; no writes performed.");
        var groups=plan.Changes.GroupBy(c=>c.ResourceId).ToArray();
        foreach(var group in groups)if(TagRules.Fingerprint(await ReadTags(group.Key,ct))!=plan.Before[group.Key])throw new TagException("Before-state drift; no writes performed.");
        var results=new List<TagOutcome>();var id=Guid.NewGuid().ToString("N");
        TagReceipt Receipt(string state)=>new("platform.tag-receipt/v1",id,plan.Digest,DateTimeOffset.UtcNow,results.ToArray(),state);
        await save(Receipt("Running"));
        foreach(var group in groups){
            try{
                var current=await ReadTags(group.Key,ct);if(TagRules.Fingerprint(current)!=plan.Before[group.Key])throw new TagException("Tags drifted immediately before write.");
                var delta=group.Where(x=>x.Action!="NoChange").ToDictionary(x=>x.Key,x=>x.Value,StringComparer.OrdinalIgnoreCase);
                results.Add(new(group.Key,"Pending","Outcome not yet confirmed; reconcile before retrying."));await save(Receipt("Running"));
                string? requestId=null;if(delta.Count>0)requestId=(await Send(group.Key+"/providers/Microsoft.Resources/tags/default?api-version=2021-04-01",ct,new{operation="Merge",properties=new{tags=delta}})).RequestId;
                foreach(var pair in delta)current[pair.Key]=pair.Value;
                var after=await ReadTags(group.Key,ct);if(TagRules.Fingerprint(after)!=TagRules.Fingerprint(current))throw new TagException("Read-back differs; unrelated state or requested tags changed.");
                results[^1]=new(group.Key,"Verified","Requested keys and preservation of unrelated tags verified.",requestId);await save(Receipt("Running"));
            }catch(Exception e)when(e is TagException or HttpRequestException or OperationCanceledException or JsonException){
                if(results.LastOrDefault()?.ResourceId==group.Key)results[^1]=new(group.Key,"Unknown","Write/read-back not confirmed. Stop and reconcile; no retry or rollback.");else results.Add(new(group.Key,"Blocked","Revalidation failed; no write attempted for this resource."));
                await save(Receipt("Incomplete"));return Receipt("Incomplete");
            }
        }var completed=Receipt("Verified");await save(completed);return completed;
    }
}
