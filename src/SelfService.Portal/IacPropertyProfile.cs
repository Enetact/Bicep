using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Portal;

// Compiled from the pipeline's exact source profile; portable builds need no external profile lookup.
public static class IacPropertyProfile
{
    static readonly HashSet<string> Paths = Load();
    static HashSet<string> Load()
    {
        using var stream=typeof(IacPropertyProfile).Assembly.GetManifestResourceStream("IacProfiles.json")!;
        using var doc=JsonDocument.Parse(stream);
        var patterns=doc.RootElement.GetProperty("scalars").EnumerateArray().Concat(doc.RootElement.GetProperty("types").EnumerateObject().SelectMany(t=>t.Value.GetProperty("paths").EnumerateArray())).Select(p=>p.GetString()!).ToArray();
        var result=patterns.ToHashSet(StringComparer.Ordinal);
        foreach(var p in patterns) {var parts=p.Split('.');for(var i=0;i<parts.Length;i++)if(parts[i]=="*")result.Add(string.Join('.',parts.Take(i))+".$count");}
        return result;
    }
    public static bool Allows(string path)=>path.Length<=512 && Paths.Contains(Regex.Replace(path,@"\.\d+(?=\.|$)",".*",RegexOptions.CultureInvariant,TimeSpan.FromSeconds(1)));
    public static object[] Coverage(JsonElement resource)
    {
        var rows=EvidenceDiagram.Array(resource,"propertyCoverage").ToArray();
        if(rows.Length>160)throw new PortalException("Too many IaC property coverage records.",413);
        return rows.Select(p=>{
            var path=p.GetProperty("path").GetString()!;var status=p.GetProperty("status").GetString();
            if(!Allows(path)||status is not("Observed" or "NotReturned" or "UnresolvedExpression" or "LimitExceeded"))throw new PortalException("Invalid IaC property coverage.",409);
            return (object)new{path,status};
        }).ToArray();
    }
}
