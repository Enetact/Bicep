using System.Text;
using System.Net.Http.Headers;
using SelfService.Tagging;

try
{
    if(args.Length<1)throw new TagException("Use discover, analyze, preview, apply or verify with --root and --output.");
    var verb=args[0];var values=new Dictionary<string,string>();for(int i=1;i<args.Length;i+=2){if(i+1>=args.Length||!args[i].StartsWith("--")||!values.TryAdd(args[i],args[i+1]))throw new TagException("Invalid CLI arguments.");}
    string Get(string key)=>values.GetValueOrDefault("--"+key)??throw new TagException("Missing option: "+key);
    var root=Path.GetFullPath(Get("root"));var output=Path.GetFullPath(Get("output"));Directory.CreateDirectory(output);
    var p=TagJson.Read<TagProfile>(File.ReadAllText(Path.Combine(root,"config/tag-governance.json")));
    using var http=new HttpClient(new HttpClientHandler{AllowAutoRedirect=false});
    var azure=new AzureTags(http,ct=>Task.FromResult(Environment.GetEnvironmentVariable("TAG_ARM_TOKEN")??throw new TagException("Pipeline Azure authentication required.")));
    using var timeout=new CancellationTokenSource(TimeSpan.FromMinutes(15));var ct=timeout.Token;
    async Task Save<T>(string name,T value){var path=Path.Combine(output,name);var temporary=path+".pending";await File.WriteAllTextAsync(temporary,TagJson.Write(value),CancellationToken.None);File.Move(temporary,path,true);}
    async Task Summary(TagReceipt receipt)=>await File.WriteAllTextAsync(Path.Combine(output,"README.md"),$"# Tag {verb} receipt\n\nState: {receipt.State}. Plan: `{receipt.PlanDigest}`.\n\n"+string.Join("\n",receipt.Results.Select(r=>$"- {r.ResourceId}: {r.State}. {r.Detail}")),CancellationToken.None);
    string Cell(string? x){var safe=System.Net.WebUtility.HtmlEncode(x??"(missing)").Replace("|","&#124;").Replace("\r"," ").Replace("\n"," ");return System.Text.RegularExpressions.Regex.Replace(safe,@"([\\`*_{}\[\]()!~])",@"\$1");}
    if(verb=="discover"){
        var x=await azure.Discover(p,"ADO service connection",ct);
        x=TagRules.Seal(x with{RunId=int.TryParse(Environment.GetEnvironmentVariable("BUILD_BUILDID"),out var run)?run:null,SourceCommit=Environment.GetEnvironmentVariable("BUILD_SOURCEVERSION")});
        await Save("inventory.json",x);await Save("analysis.json",TagRules.Analyze(x,p));
        await Save("manifest.json",new{schema="platform.tag-manifest/v1",x.Digest,x.TenantId,x.SubscriptionId,x.RunId,x.SourceCommit,profileDigest=TagRules.ProfileHash(p)});
        await File.WriteAllTextAsync(Path.Combine(output,"README.md"),$"# Tag discovery\n\nObserved {x.Resources.Length} visible ARM objects. Governance digest: `{x.GovernanceDigest}`. No tag changes.\n\n"+string.Join("\n",x.Coverage.Select(c=>$"- {c.Collector}: {c.State} ({c.Count}). {c.Detail}")),ct);
        if(x.Coverage.Any(c=>c.State is not("Succeeded" or "SucceededEmpty")))throw new TagException("Discovery incomplete; evidence retained, Apply blocked.");
    }else if(verb is "preview" or "analyze"){
        var x=TagJson.Read<TagInventory>(File.ReadAllText(Get("inventory")));TagRules.ValidateInventory(x,p);
        if(verb=="analyze"){await Save("analysis.json",TagRules.Analyze(x,p));return 0;}
        var encoded=Environment.GetEnvironmentVariable("TAG_REQUEST_BASE64")??throw new TagException("No request supplied.");if(encoded.Length>32768)throw new TagException("Request size exceeds limit.");
        var b64=encoded.Replace('-','+').Replace('_','/');b64=b64.PadRight((b64.Length+3)/4*4,'=');var request=TagJson.Read<TagRequest>(Encoding.UTF8.GetString(Convert.FromBase64String(b64)));
        if(request.DiscoveryRunId!=x.RunId||x.RunId is not>0||x.SourceCommit!=Environment.GetEnvironmentVariable("BUILD_SOURCEVERSION"))throw new TagException("Discovery run/source binding differs.");
        var plan=await azure.Preview(x,request,p,ct);await Save("plan.json",plan);
        var readme=new StringBuilder("# Tag change Preview\n\nThis is a deterministic tag delta, not ARM What-If. No tag writes occurred.\n\n| Resource | Key | Before | After | Action | Route |\n|---|---|---|---|---|---|\n");
        foreach(var d in plan.Changes)readme.AppendLine($"| {Cell(d.ResourceId)} | {Cell(d.Key)} | {Cell(d.Before)} | {Cell(d.Value)} | {d.Action} | {d.Route} |");
        readme.AppendLine($"\nPlan digest: `{plan.Digest}`. Expires: {plan.ExpiresUtc:O}.\n");foreach(var block in plan.Blockers)readme.AppendLine("- BLOCKED: "+Cell(block));
        await File.WriteAllTextAsync(Path.Combine(output,"README.md"),readme.ToString(),ct);
        if(plan.Blockers.Length>0||plan.Changes.Any(c=>c.Route!="External"))throw new TagException("Preview has blockers/source-owned changes. No Apply allowed.");
    }else if(verb=="apply"){
        var plan=TagJson.Read<TagPlan>(File.ReadAllText(Get("plan")));
        if(Environment.GetEnvironmentVariable("BUILD_SOURCEBRANCH")!="refs/heads/main"||Environment.GetEnvironmentVariable("TAG_APPROVED_STAGE")!="Apply")throw new TagException("Apply requires the protected main deployment stage.");
        var receipt=await azure.Apply(plan,p,async r=>{await Save("receipt.json",r);await Summary(r);},ct);
        if(receipt.State!="Verified")throw new TagException("Partial/unknown result; reconcile before retrying.");
    }else if(verb=="verify"){
        var plan=TagJson.Read<TagPlan>(File.ReadAllText(Get("plan")));if(plan.Digest!=TagRules.PlanHash(plan)||plan.ProfileDigest!=TagRules.ProfileHash(p))throw new TagException("Verification plan provenance differs.");await azure.VerifySubscription(p,ct);var rows=new List<TagOutcome>();
        foreach(var g in plan.Changes.GroupBy(x=>x.ResourceId)){
            try{var current=await azure.ReadTags(TagRules.Id(g.Key,p.SubscriptionId),ct);rows.Add(new(g.Key,g.All(x=>current.GetValueOrDefault(x.Key)==x.Value)?"Verified":"Conflict","Independent post-stage requested-key check; Apply receipt records unrelated-key preservation."));}
            catch(TagException){rows.Add(new(g.Key,"Unknown","Current tags unreadable."));}
        }
        var r=new TagReceipt("platform.tag-receipt/v1",Guid.NewGuid().ToString("N"),plan.Digest,DateTimeOffset.UtcNow,rows.ToArray(),rows.All(x=>x.State=="Verified")?"Verified":"Incomplete");await Save("verification.json",r);await Summary(r);if(r.State!="Verified")throw new TagException("Verification incomplete.");
    }else throw new TagException("Unknown tagging verb.");
    Console.WriteLine("Tagging evidence saved. No raw tag values written to console.");return 0;
}
catch(Exception e)when(e is TagException or System.Text.Json.JsonException or FormatException or HttpRequestException or OperationCanceledException){Console.Error.WriteLine(e is TagException?e.Message:"Tagging operation failed; inspect sanitized receipts. No retry inferred.");return 1;}
