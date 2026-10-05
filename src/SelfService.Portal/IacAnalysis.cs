using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Portal;

// Artifact reader only. Pipeline credentials and raw template parameters never reach the browser/model.
public static class IacAnalysis
{
    static string Text(JsonElement e,string key) => e.GetProperty(key).GetString() ?? "";
    static readonly string[] Actions = ["Create","Modify","Delete","NoChange","Ignore","Deploy","Unsupported","Detach","Unknown"];
    static readonly string[] Observations = ["Observed","Absent","NotAssessed","Denied","Unknown"];
    static object? CleanQualification(JsonElement report,Func<string,bool> scoped) {
        if(!report.TryGetProperty("qualification",out var q))return null;
        if(Text(q,"profile")!="nested-arm/v1"||Text(q,"status") is not("Blocked" or "QualifiedProjection"))throw new PortalException("Unknown IaC qualification profile.",409);
        foreach(var field in new[]{"profileSha256","beforeFingerprint"})if(!Regex.IsMatch(Text(q,field),"^[a-f0-9]{64}$",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)))throw new PortalException("Invalid nested evidence digest.",409);
        var blockers=EvidenceDiagram.Array(q,"blockers").ToArray();if(blockers.Length>3000)throw new PortalException("Too many coverage blockers.",413);
        var codes=new[]{"WhatIfIncomplete","LinkedTemplatesNotExpanded","DesiredStateUnresolved","BeforeStateUnknown","BeforePropertyUnknown","InventoryIncomplete","ChildProbeIncomplete","PropertyProjectionIncomplete","AfterStateUnverified"};
        return new {profile=Text(q,"profile"),status=Text(q,"status"),profileSha256=Text(q,"profileSha256"),beforeFingerprint=Text(q,"beforeFingerprint"),
            blockers=blockers.Select(b=>{var id=EvidenceDiagram.Text(b,"resourceId");var code=Text(b,"code");if(!codes.Contains(code)||(id is not null&&!scoped(id)))throw new PortalException("Invalid IaC qualification blocker.",409);return new{code,resourceId=id};}).ToArray(),
            authority="Profile-only evidence. Current observations cannot resolve desired deployment expressions or authorize deployment."};
    }
    static object[] CleanProbes(JsonElement report,Func<string,bool> scoped) {
        var result=new List<object>();
        foreach(var phase in new[]{"before","after"})foreach(var p in EvidenceDiagram.Array(report.GetProperty(phase),"probes")) {
            if(result.Count>=1000)throw new PortalException("Too many child probe receipts.",413);
            var id=Text(p,"id");var parent=Text(p,"parentId");var status=Text(p,"status");var apiVersion=Text(p,"apiVersion");
            if(!scoped(id)||!scoped(parent)||!id.StartsWith(parent+"/",StringComparison.OrdinalIgnoreCase)||Text(p,"method")!="GET"||Text(p,"profile")!="nested-arm/v1"||
                status is not("Unknown" or "NotAvailable" or "Denied" or "LimitExceeded" or "Partial" or "Succeeded")||!Regex.IsMatch(apiVersion,"^\\d{4}-\\d{2}-\\d{2}(-preview)?$",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)))throw new PortalException("Invalid IaC child probe.",409);
            result.Add(new{phase,id,parentId=parent,status,apiVersion,method="GET",observedUtc=EvidenceDiagram.Text(p,"observedUtc"),pages=p.GetProperty("pages").GetInt32(),observed=p.GetProperty("observed").GetInt32()});
        }
        return result.ToArray();
    }
    static object? CleanState(JsonElement state,bool planned) {
        if(state.ValueKind==JsonValueKind.Null)return null;
        var status=Text(state,planned?"status":"inventoryStatus");
        if(planned ? status is not("Succeeded" or "Incomplete") : status is not("Listed" or "GroupAbsent" or "Partial" or "Unknown"))throw new PortalException("Invalid IaC coverage status.",409);
        return new {status=planned?status:null,inventoryStatus=planned?null:status,
            startedUtc=EvidenceDiagram.Text(state,"startedUtc"),completedUtc=EvidenceDiagram.Text(state,"completedUtc"),
            coverage=planned?"Azure What-If prediction; omitted properties and uncertain changes remain unknown.":"Scoped Microsoft Azure MCP inventory and direct ARM GETs. Fixed profile child probes supplement inventory. Unsupported child types and omitted properties remain unknown.",
            issues=EvidenceDiagram.Array(state,"issues").Any()?new[]{"Collection has incomplete evidence. Inspect the pipeline report."}:Array.Empty<string>(),
            audit=EvidenceDiagram.Array(state,"audit").Select(a=>new{tool=Text(a,"tool"),status=Text(a,"status")}),
            resources=state.GetProperty("resources").EnumerateArray().Select(r=>new {id=Text(r,"id"),name=ResourceIdentity.Parse(Text(r,"id")).Name,fullName=ResourceIdentity.Parse(Text(r,"id")).FullName,type=Text(r,"type"),status=Text(r,"status"),
                apiVersion=EvidenceDiagram.Text(r,"apiVersion"),observedUtc=EvidenceDiagram.Text(r,"observedUtc"),propertyCoverage=IacPropertyProfile.Coverage(r),action=EvidenceDiagram.Text(r,"action"),certainty=EvidenceDiagram.Text(r,"certainty"),config=r.GetProperty("config"),unresolvedExpressions=r.TryGetProperty("unresolvedExpressions",out var u)?u.GetInt32():0})};
    }
    public static async Task<JsonElement> Read(byte[] bytes,Product product,int runId,string commit,int definitionId,string repositoryId,
        string resourceGroup,bool after,CancellationToken ct)
    {
        try {
            using var zip=new ZipArchive(new MemoryStream(bytes),ZipArchiveMode.Read);
            if(zip.Entries.Count>1000)throw new PortalException("IaC archive exceeds the entry limit.",413);
            var artifact=after?"deployment-result":"deployment-preview";var prefix=after?"iac/":"azure/iac/";
            async Task<byte[]> File(string name) {
                var entries=zip.Entries.Where(e=>e.FullName==prefix+name||e.FullName==artifact+"/"+prefix+name).ToArray();
                if(entries.Length!=1)throw new PortalException("IaC evidence is unavailable or ambiguous. Older runs need a new Preview; no state is inferred.",409);
                using var stream=entries[0].Open();return await PreviewDiagram.Bounded(stream,4*1024*1024,ct);
            }
            var receipt=JsonSerializer.Deserialize<JsonElement>(await File("receipt.json"));
            if(Text(receipt,"schema")!="platform.iac-receipt/v1"||receipt.GetProperty("executionAuthorized").GetBoolean())throw new PortalException("Invalid IaC receipt.",409);
            var files=new Dictionary<string,byte[]>(StringComparer.Ordinal);
            foreach(var entry in receipt.GetProperty("files").EnumerateObject()) {
                if(entry.Name is not ("analysis.json" or "before.bicep" or "after.bicep" or "README.md") || files.ContainsKey(entry.Name))throw new PortalException("Unexpected IaC receipt file.",409);
                var content=await File(entry.Name);
                if(Convert.ToHexString(SHA256.HashData(content)).ToLowerInvariant()!=entry.Value.GetString())throw new PortalException("IaC artifact integrity mismatch.",409);
                files.Add(entry.Name,content);
            }
            if(!files.ContainsKey("analysis.json")||!files.ContainsKey("README.md"))throw new PortalException("Incomplete IaC receipt.",409);
            var report=JsonSerializer.Deserialize<JsonElement>(files["analysis.json"]);
            if(Text(report,"schema")!="platform.iac-analysis/v1"||report.GetProperty("deploymentAuthorized").GetBoolean())throw new PortalException("Invalid IaC report.",409);
            var provenance=report.GetProperty("provenance");var scope=report.GetProperty("scope");
            string[] provenanceKeys=["product","environment","runId","sourceCommit","pipelineId","repositoryId","region","tenantId","inputSha256","templateSha256"];
            if(provenance.EnumerateObject().Any(p=>!provenanceKeys.Contains(p.Name)) ||
                receipt.EnumerateObject().Any(p=>!new[]{"schema","provenance","files","createdUtc","executionAuthorized"}.Contains(p.Name)))throw new PortalException("Unexpected IaC provenance fields.",409);
            foreach(var key in new[]{"inputSha256","templateSha256"})
                if(!Regex.IsMatch(Text(provenance,key),"^[a-f0-9]{64}$",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)))throw new PortalException("Invalid IaC input digest.",409);
            var subscription=Text(scope,"subscriptionId");var environment=Text(provenance,"environment");
            if(!product.Targets.Any(t=>t.Environment==environment&&t.SubscriptionId.Equals(subscription,StringComparison.OrdinalIgnoreCase)) ||
                Text(scope,"resourceGroup")!=resourceGroup || Text(provenance,"product")!=product.Id ||
                provenance.GetProperty("runId").ToString()!=runId.ToString() || Text(provenance,"sourceCommit")!=commit ||
                provenance.GetProperty("pipelineId").ToString()!=definitionId.ToString() || Text(provenance,"repositoryId")!=repositoryId ||
                !JsonElement.DeepEquals(provenance,receipt.GetProperty("provenance")))throw new PortalException("IaC evidence target, source or producer mismatch.",409);
            var scopeId=$"/subscriptions/{subscription}/resourceGroups/{resourceGroup}";
            var declaredIds=EvidenceDiagram.Array(report.GetProperty("planned"),"resources").Select(r=>Text(r,"id")).ToHashSet(StringComparer.OrdinalIgnoreCase);
            bool Scoped(string id)=>id.Length<=2048&&!id.Any(c=>c is '?' or '#' or '%' or '\\' or '\r' or '\n')&&!id.Contains("..",StringComparison.Ordinal)&&
                id.StartsWith($"/subscriptions/{subscription}/",StringComparison.OrdinalIgnoreCase)&&
                (id.Equals(scopeId,StringComparison.OrdinalIgnoreCase)||id.StartsWith(scopeId+"/providers/",StringComparison.OrdinalIgnoreCase)||declaredIds.Contains(id));
            var phaseIds=new Dictionary<string,HashSet<string>>();
            foreach(var phase in new[]{"before","planned","after"}) {
                var state=report.GetProperty(phase);if(state.ValueKind==JsonValueKind.Null)continue;
                var resources=state.GetProperty("resources");if(resources.GetArrayLength()>500)throw new PortalException("IaC evidence exceeds 500 resources.",413);
                var ids=new HashSet<string>(StringComparer.OrdinalIgnoreCase);phaseIds.Add(phase,ids);
                foreach(var r in resources.EnumerateArray()) {
                    var id=Text(r,"id");if(!Scoped(id)||!ids.Add(id))throw new PortalException("IaC resource scope or uniqueness mismatch.",409);
                    if(!Regex.IsMatch(Text(r,"type"),"^[A-Za-z0-9.]+/[A-Za-z0-9/]+$",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)))throw new PortalException("Invalid resource type.",409);
                    if(phase=="planned") {if(!Actions.Contains(Text(r,"action"))||Text(r,"status") is not("Predicted" or "Unresolved"))throw new PortalException("Invalid predicted action.",409);}
                    else if(!Observations.Contains(Text(r,"status")))throw new PortalException("Invalid observed state.",409);
                    if(r.GetProperty("config").EnumerateObject().Count()>4096)throw new PortalException("IaC projected property limit exceeded.",413);
                    foreach(var property in r.GetProperty("config").EnumerateObject())
                        if(!IacPropertyProfile.Allows(property.Name)||property.Value.ValueKind is not(JsonValueKind.String or JsonValueKind.Number or JsonValueKind.True or JsonValueKind.False)||property.Value.ToString().Length>2048)
                            throw new PortalException("IaC property projection is not allowlisted.",409);
                }
            }
            if(!after&&phaseIds.ContainsKey("after"))throw new PortalException("Preview cannot claim an after observation.",409);
            var comparison=report.GetProperty("comparison").EnumerateArray().ToArray();
            if(comparison.Length>500)throw new PortalException("Too many IaC comparisons.",413);
            foreach(var c in comparison) {
                if(!phaseIds.TryGetValue("planned",out var plannedIds)||!plannedIds.Contains(Text(c,"id"))||!Actions.Contains(Text(c,"action"))||
                    Text(c,"status") is not("Unknown" or "Observed absent" or "Mismatch" or "Projected properties match" or "Partial projected match" or "Observed existence only"))throw new PortalException("Invalid IaC comparison.",409);
                foreach(var field in new[]{"checkedPaths","mismatchPaths","unknownPaths"})
                    if(EvidenceDiagram.Array(c,field).Any(p=>!IacPropertyProfile.Allows(p.GetString()??"")))throw new PortalException("Unrecognized comparison property.",409);
            }
            var collector=report.GetProperty("collector");
            if(Text(collector,"name")!="Microsoft Azure MCP"||Text(collector,"version")!="2.0.5"||!collector.GetProperty("readOnly").GetBoolean())throw new PortalException("Unknown IaC collector.",409);
            // Return only the bounded public contract, not arbitrary producer-added fields.
            var publicProvenance=new{product=product.Id,environment,runId,sourceCommit=commit,pipelineId=definitionId,repositoryId,
                region=Text(provenance,"region"),tenantId=EvidenceDiagram.Text(provenance,"tenantId"),inputSha256=Text(provenance,"inputSha256"),templateSha256=Text(provenance,"templateSha256")};
            return JsonSerializer.SerializeToElement(new {kind="portal-iac-analysis",artifactPhase=after?"after":"preview",provenance=publicProvenance,scope=new{subscriptionId=subscription,resourceGroup},
                collector=new{name="Microsoft Azure MCP",version="2.0.5",credential="AzureCliCredential",readOnly=true},
                qualification=CleanQualification(report,Scoped),probes=CleanProbes(report,Scoped),
                before=CleanState(report.GetProperty("before"),false),planned=CleanState(report.GetProperty("planned"),true),after=CleanState(report.GetProperty("after"),false),
                comparison=comparison.Select(c=>new{id=Text(c,"id"),action=Text(c,"action"),status=Text(c,"status"),checkedPaths=EvidenceDiagram.Array(c,"checkedPaths").ToArray(),mismatchPaths=EvidenceDiagram.Array(c,"mismatchPaths").ToArray(),unknownPaths=EvidenceDiagram.Array(c,"unknownPaths").ToArray()}),
                updatedUtc=Text(report,"updatedUtc"),limitations=new[]{"Existing-only Bicep is not a restore template.","Projected matches are not full configuration, connectivity or application acceptance."},deploymentAuthorized=false,
                evidenceSha256=Convert.ToHexString(SHA256.HashData(files["analysis.json"])).ToLowerInvariant(),
                receipt,
                beforeBicep=files.TryGetValue("before.bicep",out var beforeSource)?Encoding.UTF8.GetString(beforeSource):null,
                afterBicep=files.TryGetValue("after.bicep",out var afterSource)?Encoding.UTF8.GetString(afterSource):null});
        }catch(Exception e)when(e is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException or ArgumentException) {
            throw new PortalException("IaC evidence is invalid or incomplete. Inspect the retained pipeline artifact.",409);
        }
    }
    public static EvidenceGraph Graph(JsonElement report,string phase) {
        if(phase is not("before" or "planned" or "after"))throw new PortalException("Select before, planned or after.");
        var state=report.GetProperty(phase);
        if(state.ValueKind==JsonValueKind.Null)throw new PortalException("This phase has not been collected. Run Preview or Deploy first.",409);
        var resources=state.GetProperty("resources").EnumerateArray().ToArray();
        if(resources.Length>200)throw new PortalException("Agent diagrams are limited to 200 resources; use the full deterministic portal view.",413);
        var ids=resources.OrderBy(r=>Text(r,"id"),StringComparer.OrdinalIgnoreCase).Select((r,i)=>(id:Text(r,"id"),alias:$"R{i+1:D3}")).ToDictionary(x=>x.id,x=>x.alias,StringComparer.OrdinalIgnoreCase);
        var nodes=resources.Select(r=>new EvidenceNode(ids[Text(r,"id")],Text(r,"id"),Text(r,"id").Split('/').Last(),Text(r,"type"),
            phase=="planned"?$"Predicted {Text(r,"action")}":Text(r,"status"),1,phase=="planned"?"What-If prediction; not live":"Observed collection; omitted properties unknown")).ToArray();
        var edges=new List<EvidenceEdge>();
        foreach(var r in resources)foreach(var p in r.GetProperty("config").EnumerateObject()) {
            if((p.Name.EndsWith(".id",StringComparison.Ordinal)||p.Name=="properties.virtualNetworkSubnetId")&&p.Value.ValueKind==JsonValueKind.String&&ids.TryGetValue(p.Value.GetString()!,out var to))
                edges.Add(new(ids[Text(r,"id")],to,phase=="planned"?"predicted configuration reference":"observed configuration reference"));
        }
        return new(nodes,edges.ToArray(),$"{phase} phase only. {Text(state,"coverage")} Other phases, unobserved links and full runtime readiness cannot be inferred.");
    }
}
