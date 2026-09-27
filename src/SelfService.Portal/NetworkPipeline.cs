using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SelfService.Portal;

public record NetworkAllocationRequest(string Workload, string Environment, string Operation, string Profile = "avnm-private-web");
public record PendingNetworkAllocation(NetworkAllocationRequest Request, string ConfigHash, DateTimeOffset Expires);
public sealed class NetworkPipeline(AdoGateway ado, Catalog catalog, PortalOptions options)
{
    const string Name = "Network - AVNM allocation", Yaml = "azure-pipelines-network.yml";
    string Api(string path) => $"{options.AdoBase}/_apis/{path}{(path.Contains('?') ? '&' : '?')}api-version=7.1";
    public static object Payload(NetworkAllocationRequest r) => new { resources = new { repositories = new { self = new { refName = "refs/heads/main" } } },
        templateParameters = new { workload = r.Workload, environment = r.Environment, operation = r.Operation, profile = r.Profile } };
    public (string Hash, JsonElement Profile) Configuration()
    {
        var text = File.ReadAllText(Path.Combine(catalog.Root, "config/network-allocation.json"));
        return (Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))), JsonSerializer.Deserialize<JsonElement>(text).GetProperty("profiles").GetProperty("avnm-private-web"));
    }
    public object Status()
    {
        var p = Configuration().Profile;
        return new { profile = "avnm-private-web", definition = Name, yaml = Yaml, configured = !string.IsNullOrWhiteSpace(EvidenceDiagram.Text(p,"poolId")), enabled = p.GetProperty("enabled").GetBoolean(),
            note = "Requires an existing reviewed AVNM pool, pool-wide ADO exclusive lock and approval checks. Plan/Reconcile read only; Reserve consumes address space. Network creation is a separate approved stage. Workload targets stay independent." };
    }
    public static void ValidateProfile(JsonElement p, NetworkAllocationRequest r)
    {
        if (r.Profile != "avnm-private-web" || r.Operation is not ("Plan only" or "Reserve only" or "Reserve and create network" or "Reconcile")) throw new PortalException("Select a registered network operation.");
        if (string.IsNullOrWhiteSpace(EvidenceDiagram.Text(p,"poolId"))) throw new PortalException("Configure the existing AVNM pool in config/network-allocation.json before queueing.",409);
        if (r.Operation.StartsWith("Reserve",StringComparison.Ordinal) && (!p.GetProperty("enabled").GetBoolean() || !p.GetProperty("exclusiveLockReviewed").GetBoolean() || !p.GetProperty("externalPrefixesReconciled").GetBoolean()))
            throw new PortalException("Network reservation is not enabled or its platform reviews are incomplete.",409);
    }
    async Task<int> Validate(BrowserSession s, NetworkAllocationRequest r)
    {
        var product = catalog.Products.FirstOrDefault(p=>p.Id==r.Workload && p.Id!="observe");
        if (product is null || !product.Targets.Any(t=>t.Environment==r.Environment)) throw new PortalException("Select a registered network workload and environment.");
        ValidateProfile(Configuration().Profile,r);
        var found = await ado.Send(s,"ado",Api($"build/definitions?name={Uri.EscapeDataString(Name)}&$top=100"));
        var matches = found.GetProperty("value").EnumerateArray().Where(d=>EvidenceDiagram.Text(d,"name")==Name).ToArray();
        if(matches.Length!=1)throw new PortalException($"Register exactly one '{Name}' pipeline using {Yaml}.",409);
        var definition=await ado.Send(s,"ado",Api($"build/definitions/{matches[0].GetProperty("id").GetInt32()}"));
        if(EvidenceDiagram.Text(EvidenceDiagram.Part(definition,"process"),"yamlFilename")?.TrimStart('/')!=Yaml)throw new PortalException("Network pipeline YAML path does not match the registered entry.",409);
        return definition.GetProperty("id").GetInt32();
    }
    public async Task<object> Review(BrowserSession s,NetworkAllocationRequest r)
    {
        var hash=Configuration().Hash; await Validate(s,r);
        if(Configuration().Hash!=hash)throw new PortalException("Network profile changed during review. Retry.",409);
        foreach(var item in s.NetworkPending.Where(p=>p.Value.Expires<DateTimeOffset.UtcNow))s.NetworkPending.TryRemove(item.Key,out _);
        if(s.NetworkPending.Count>=8)throw new PortalException("Too many pending network reviews.",429);
        var ticket=Convert.ToHexString(RandomNumberGenerator.GetBytes(24));s.NetworkPending[ticket]=new(r,hash,DateTimeOffset.UtcNow.AddMinutes(5));
        return new{ticket,payload=Payload(r),warning=r.Operation.StartsWith("Reserve",StringComparison.Ordinal)?"This queues a real allocation run. Reservation consumes pool space; network creation can incur Azure charges. ADO approvals remain required.":"This queues a read-only allocation Plan/Reconcile run in ADO."};
    }
    public async Task<object> Queue(BrowserSession s,string ticket)
    {
        if(!s.NetworkPending.TryRemove(ticket,out var pending)||pending.Expires<DateTimeOffset.UtcNow)throw new PortalException("Network review expired or already sent. Check ADO before retrying.",409);
        if(Configuration().Hash!=pending.ConfigHash)throw new PortalException("Reviewed network configuration changed.",409);
        var id=await Validate(s,pending.Request);
        if(Configuration().Hash!=pending.ConfigHash)throw new PortalException("Reviewed network configuration changed.",409);
        var result=await ado.Send(s,"ado",Api($"pipelines/{id}/runs"),Payload(pending.Request)); // Never retry queue POST.
        var runId=result.GetProperty("id").GetInt32();return new{id=runId,url=$"{options.AdoBase}/_build/results?buildId={runId}"};
    }
}
