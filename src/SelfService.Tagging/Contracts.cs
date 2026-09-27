using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SelfService.Tagging;

public sealed record TagProfile
{
    public int SchemaVersion { get; init; } = 1;
    public string Id { get; init; } = "subscription-tags";
    public string TenantId { get; init; } = "";
    public string SubscriptionId { get; init; } = "";
    public bool Enabled { get; init; }
    public bool GovernanceReviewed { get; init; }
    public string GovernanceDigest { get; init; } = "";
    public string[] ExternalResourceIds { get; init; } = [];
    public Dictionary<string, string> SourceTargets { get; init; } = new(StringComparer.OrdinalIgnoreCase);
    public string[] RequiredKeys { get; init; } = ["owner", "costCenter", "workload", "environment"];
    public string[] AutomationKeys { get; init; } = [];
    public string[] ApprovedCustomKeys { get; init; } = [];
    public string[] QualifiedWriteTypes { get; init; } = [];
    public string RuleVersion { get; init; } = "tags-2026-09-27";
}
public record TagResource(string Id, string Name, string Type, string ResourceGroup, string Location,
    Dictionary<string,string> Tags, string TagState, string Fingerprint, string Ownership, string? SourceTarget, bool Supported);
public record TagCoverage(string Collector, string State, int Count, string Detail);
public record TagInventory(string Schema, string Id, string TenantId, string SubscriptionId, string Source,
    DateTimeOffset ObservedUtc, TagResource[] Resources, TagCoverage[] Coverage, string GovernanceDigest, string Digest,
    int? RunId = null, string? SourceCommit = null);
public record TagFinding(string Id, string ResourceId, string Rule, string State, string Key, string Summary);
public record TagEdit(string ResourceId, string Key, string Value, string Operation);
public record TagRequest(string Schema, string InventoryDigest, TagEdit[] Edits, int? DiscoveryRunId = null);
public record TagDelta(string ResourceId, string Key, string? Before, string Value, string Action, string Route, string? SourceTarget, string Type);
public record TagPlan(string Schema, TagRequest Request, string ProfileDigest, string GovernanceDigest, string RuleVersion,
    DateTimeOffset ExpiresUtc, TagDelta[] Changes, Dictionary<string,string> Before, string[] Blockers, string Digest);
public record TagOutcome(string ResourceId, string State, string Detail, string? RequestId = null);
public record TagReceipt(string Schema, string Id, string PlanDigest, DateTimeOffset UpdatedUtc, TagOutcome[] Results, string State);
public sealed class TagException(string message) : Exception(message);

public static class TagJson
{
    public static readonly JsonSerializerOptions Options = new(JsonSerializerDefaults.Web) { WriteIndented=true, UnmappedMemberHandling=JsonUnmappedMemberHandling.Disallow };
    public static string Write<T>(T item) => JsonSerializer.Serialize(item,Options);
    public static T Read<T>(string text)
    {
        using var doc=JsonDocument.Parse(text,new(){MaxDepth=64}); Check(doc.RootElement);
        return JsonSerializer.Deserialize<T>(text,Options) ?? throw new TagException("Empty tag document.");
    }
    static void Check(JsonElement e)
    {
        if(e.ValueKind==JsonValueKind.Object){var names=new HashSet<string>(StringComparer.OrdinalIgnoreCase);foreach(var p in e.EnumerateObject()){if(!names.Add(p.Name))throw new TagException("Duplicate/case-colliding JSON key.");Check(p.Value);}}
        if(e.ValueKind==JsonValueKind.Array)foreach(var x in e.EnumerateArray())Check(x);
    }
    public static string Hash<T>(T item)
    {
        using var stream=new MemoryStream();using(var writer=new Utf8JsonWriter(stream)){Canonical(writer,JsonSerializer.SerializeToElement(item,Options));}
        return Convert.ToHexString(SHA256.HashData(stream.ToArray())).ToLowerInvariant();
    }
    static void Canonical(Utf8JsonWriter w,JsonElement e)
    {
        if(e.ValueKind==JsonValueKind.Object){w.WriteStartObject();foreach(var p in e.EnumerateObject().OrderBy(p=>p.Name,StringComparer.Ordinal)){w.WritePropertyName(p.Name);Canonical(w,p.Value);}w.WriteEndObject();}
        else if(e.ValueKind==JsonValueKind.Array){w.WriteStartArray();foreach(var x in e.EnumerateArray())Canonical(w,x);w.WriteEndArray();}else e.WriteTo(w);
    }
}

public static class TagRules
{
    public const string Mask="[MASKED]";
    public static readonly string[] Protected=["managedBy","workloadType","releaseActivated","deploymentPrincipal","trustedServiceReview","runtimeCredentialReview"];
    public static readonly string[] Types=["microsoft.storage/storageaccounts","microsoft.keyvault/vaults","microsoft.network/virtualnetworks","microsoft.network/privateendpoints","microsoft.network/networksecuritygroups","microsoft.network/routetables","microsoft.network/privatednszones","microsoft.network/dnszones","microsoft.web/sites","microsoft.web/serverfarms","microsoft.insights/components","microsoft.operationalinsights/workspaces","microsoft.managedidentity/userassignedidentities","microsoft.eventgrid/topics","microsoft.servicebus/namespaces","microsoft.resources/resourcegroups","microsoft.resources/subscriptions"];
    public static bool Sensitive(string key,string value) => Regex.IsMatch(key,"secret|password|token|credential|private.?key",RegexOptions.IgnoreCase) || Regex.IsMatch(value,"(?i)(AccountKey=|SharedAccessSignature=|Bearer |-----BEGIN|[?&]sig=|eyJ[A-Za-z0-9_-]+\\.)") || Regex.IsMatch(value,@"\b[^\s@]+@[^\s@]+\.[^\s@]+\b");
    public static string Id(string id,string subscription)
    {
        if(!Guid.TryParse(subscription,out var s) || id.Length>2048 || !Regex.IsMatch(id,@"^/subscriptions/[0-9a-fA-F-]{36}(?:/[A-Za-z0-9_.() -]+)*$",RegexOptions.CultureInvariant) || !id.Split('/')[2].Equals(s.ToString(),StringComparison.OrdinalIgnoreCase) || id.Contains("/../") || id.Contains("/./"))throw new TagException("Resource ID is invalid or outside the selected subscription.");
        return id.TrimEnd('/').ToLowerInvariant();
    }
    public static string Fingerprint(Dictionary<string,string> tags) => TagJson.Hash(tags.OrderBy(p=>p.Key,StringComparer.OrdinalIgnoreCase).ToDictionary(p=>p.Key.ToLowerInvariant(),p=>p.Value));
    public static string ProfileHash(TagProfile p)=>TagJson.Hash(p);
    public static TagResource Resource(string id,string name,string type,string group,string location,Dictionary<string,string> tags,TagProfile p)
    {
        id=Id(id,p.SubscriptionId);var safe=new Dictionary<string,string>(StringComparer.OrdinalIgnoreCase);bool masked=false;
        foreach(var pair in tags){if(!safe.TryAdd(pair.Key,Sensitive(pair.Key,pair.Value)?Mask:pair.Value))throw new TagException("Tag names collide by case.");masked|=safe[pair.Key]==Mask;}
        var target=p.SourceTargets.FirstOrDefault(x=>x.Key.Equals(id,StringComparison.OrdinalIgnoreCase)).Value;
        var route=target is not null?"SourceChange":p.ExternalResourceIds.Contains(id,StringComparer.OrdinalIgnoreCase)?"External":"Unknown";
        // Markers can restrict a registered external route, but cannot grant authority.
        if(route=="External" && tags.Keys.Any(k=>Protected.Contains(k,StringComparer.OrdinalIgnoreCase)))route="Unknown";
        return new(id,name,type.ToLowerInvariant(),group,location,safe,masked?"Masked":tags.Count==0?"Untagged":"Observed",Fingerprint(tags),route,target,Types.Contains(type.ToLowerInvariant()));
    }
    public static TagFinding[] Analyze(TagInventory inventory,TagProfile profile) => inventory.Resources.SelectMany(r=>
        !r.Supported?new[]{new TagFinding(TagJson.Hash(new{r.Id,rule="support"}),r.Id,"support","Unknown","","Type not qualified for tagging; excluded from compliance denominator.")}:
        profile.RequiredKeys.Where(k=>!r.Tags.Any(p=>p.Key.Equals(k,StringComparison.OrdinalIgnoreCase)&&!string.IsNullOrWhiteSpace(p.Value)&&!p.Value.Contains("unconfigured",StringComparison.OrdinalIgnoreCase))).Select(k=>new TagFinding(TagJson.Hash(new{r.Id,key=k}),r.Id,"required-key","NeedsInput",k,"Supply an approved business value; names do not prove ownership.")).Concat(r.TagState=="Masked"?[new TagFinding(TagJson.Hash(new{r.Id,rule="sensitive"}),r.Id,"sensitive","Blocked","","Sensitive-looking tags masked; changes require separate reconciliation.")]:[])).ToArray();
    public static void ValidateKey(string type,string key,string value,TagProfile profile)
    {
        if(string.IsNullOrWhiteSpace(key)||key!=key.Trim()||key.Length>(type=="microsoft.storage/storageaccounts"?128:512)||value.Length>256||key.Any(c=>char.IsControl(c)||"<>%&\\?/".Contains(c))||value.Any(char.IsControl))throw new TagException("Invalid tag key/value length or characters.");
        if(Protected.Concat(profile.AutomationKeys).Contains(key,StringComparer.OrdinalIgnoreCase)||key.StartsWith("hidden-",StringComparison.OrdinalIgnoreCase)||Sensitive(key,value)||value==Mask)throw new TagException("Protected, automation or sensitive tag cannot be edited.");
        if(!profile.RequiredKeys.Concat(profile.ApprovedCustomKeys).Contains(key,StringComparer.OrdinalIgnoreCase)&&!Regex.IsMatch(key,@"^custom\.[A-Za-z][A-Za-z0-9_.-]{0,90}$"))throw new TagException("Use an approved key or custom.<name> for a new custom tag.");
        if(type is "microsoft.network/privatednszones" or "microsoft.network/dnszones" && !Regex.IsMatch(key,@"^[A-Za-z][A-Za-z0-9_.-]*$"))throw new TagException("DNS tags require an ASCII letter and supported characters.");
    }
    public static TagInventory Seal(TagInventory x)=>x with{Digest=TagJson.Hash(x with{Digest=""})};
    public static void ValidateInventory(TagInventory x,TagProfile p)
    {
        if(x.Schema!="platform.tag-inventory/v1"||x.SubscriptionId!=p.SubscriptionId||x.TenantId!=p.TenantId||x.Digest!=Seal(x).Digest||x.Resources.Length>50000||x.Resources.Select(r=>r.Id).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=x.Resources.Length)throw new TagException("Inventory schema, scope, digest or size is invalid.");
        foreach(var r in x.Resources){
            if(Id(r.Id,p.SubscriptionId)!=r.Id)throw new TagException("Resource IDs must be canonical.");
            var qualified=Resource(r.Id,r.Name,r.Type,r.ResourceGroup,r.Location,r.Tags,p);
            if(r.Ownership!=qualified.Ownership||r.SourceTarget!=qualified.SourceTarget||r.Supported!=qualified.Supported||r.TagState!=qualified.TagState||(r.TagState!="Masked"&&r.Fingerprint!=qualified.Fingerprint))throw new TagException("Inventory metadata differs from current ownership/support rules. Rediscover.");
        }
    }
    public static TagPlan BuildPlan(TagInventory inventory,TagRequest request,TagProfile p,DateTimeOffset now)
    {
        ValidateInventory(inventory,p);
        if(request.Schema!="platform.tag-change-request/v1"||request.InventoryDigest!=inventory.Digest||request.Edits is not {Length:>0 and <=100})throw new TagException("Invalid request or discovery binding; choose 1–100 tag edits.");
        var blockers=new List<string>();var changes=new List<TagDelta>();var before=new Dictionary<string,string>();
        if(new[]{"Resources","ResourceGroups","SubscriptionTags","Governance"}.Any(name=>inventory.Coverage.Count(c=>c.Collector==name)!=1)||inventory.Coverage.Any(c=>c.State is not("Succeeded" or "SucceededEmpty"))||string.IsNullOrWhiteSpace(inventory.GovernanceDigest))blockers.Add("Discovery coverage incomplete.");
        if(now-inventory.ObservedUtc>TimeSpan.FromMinutes(30)||inventory.ObservedUtc>now.AddMinutes(1))blockers.Add("Discovery expired or future-dated.");
        foreach(var group in request.Edits.GroupBy(x=>Id(x.ResourceId,p.SubscriptionId))){
            var r=inventory.Resources.SingleOrDefault(r=>r.Id==group.Key)??throw new TagException("Requested resource was not discovered.");
            if(!r.Supported||r.TagState=="Masked")blockers.Add($"{r.Id}: unsupported or masked tags.");
            if(r.Ownership=="Unknown")blockers.Add($"{r.Id}: ownership not registered.");
            if(r.Ownership=="External"&&!p.QualifiedWriteTypes.Contains(r.Type,StringComparer.OrdinalIgnoreCase))blockers.Add($"{r.Id}: resource type has not passed writer qualification.");
            if(group.Select(x=>x.Key).Distinct(StringComparer.OrdinalIgnoreCase).Count()!=group.Count())throw new TagException("Select each resource/key once.");
            before[r.Id]=r.Fingerprint;var resulting=new Dictionary<string,string>(r.Tags,StringComparer.OrdinalIgnoreCase);
            foreach(var e in group){ValidateKey(r.Type,e.Key,e.Value,p);if(e.Operation is not("AddIfAbsent" or "SetValue"))throw new TagException("Only AddIfAbsent and SetValue are supported.");
                var old=resulting.GetValueOrDefault(e.Key);var value=e.Operation=="AddIfAbsent"&&old is not null?old:e.Value;
                changes.Add(new(r.Id,resulting.Keys.FirstOrDefault(k=>k.Equals(e.Key,StringComparison.OrdinalIgnoreCase))??e.Key,old,value,old==value?"NoChange":old is null?"Add":"Update",r.Ownership,r.SourceTarget,r.Type));resulting[e.Key]=value;
            }
            if(resulting.Count>(r.Type is "microsoft.network/privatednszones" or "microsoft.network/dnszones"?15:50))blockers.Add($"{r.Id}: resulting tag count exceeds qualified limit.");
        }
        var plan=new TagPlan("platform.tag-plan/v1",request,ProfileHash(p),inventory.GovernanceDigest,p.RuleVersion,now.AddMinutes(30),changes.ToArray(),before,blockers.Distinct().ToArray(),"");return plan with{Digest=PlanHash(plan)};
    }
    public static string PlanHash(TagPlan plan)=>TagJson.Hash(new{plan.Schema,plan.Request,plan.ProfileDigest,plan.GovernanceDigest,plan.RuleVersion,plan.Changes,plan.Before,plan.Blockers});
    public static void ValidatePlan(TagPlan plan,TagProfile p,DateTimeOffset now)
    {
        if(plan.Schema!="platform.tag-plan/v1"||plan.Digest!=PlanHash(plan)||plan.ProfileDigest!=ProfileHash(p)||plan.ExpiresUtc<now||plan.ExpiresUtc>now.AddMinutes(31)||plan.Blockers.Length>0)throw new TagException("Plan blocked, expired, changed or unqualified.");
        if(plan.Changes.Any(c=>c.Route!="External"))throw new TagException("Source-owned resources require the workload source-change route.");
        if(plan.Request.Schema!="platform.tag-change-request/v1"||plan.Request.Edits is not{Length:>0 and <=100}||plan.Changes.Length!=plan.Request.Edits.Length||plan.Changes.Select(c=>c.ResourceId+"|"+c.Key.ToLowerInvariant()).Distinct().Count()!=plan.Changes.Length||plan.Before.Count!=plan.Changes.Select(c=>c.ResourceId).Distinct().Count())throw new TagException("Invalid plan edit correspondence.");
        foreach(var c in plan.Changes){
            var e=plan.Request.Edits.SingleOrDefault(e=>Id(e.ResourceId,p.SubscriptionId)==c.ResourceId&&e.Key.Equals(c.Key,StringComparison.OrdinalIgnoreCase));
            if(e is null||e.Operation is not("AddIfAbsent" or "SetValue")||c.Value!=(e.Operation=="AddIfAbsent"&&c.Before is not null?c.Before:e.Value)||c.Action!=(c.Before==c.Value?"NoChange":c.Before is null?"Add":"Update")||!plan.Before.ContainsKey(c.ResourceId)||c.SourceTarget is not null)throw new TagException("Plan delta differs from reviewed request.");
        }
        foreach(var c in plan.Changes){Id(c.ResourceId,p.SubscriptionId);ValidateKey(c.Type,c.Key,c.Value,p);if(!p.ExternalResourceIds.Contains(c.ResourceId,StringComparer.OrdinalIgnoreCase)||!p.QualifiedWriteTypes.Contains(c.Type,StringComparer.OrdinalIgnoreCase))throw new TagException("External resource/type not registered.");}
    }
}
