using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Portal;

public record Product(string Id, string Type, string Name, string DiscoverName, string DeployName, string DiscoverYaml, string DeployYaml,
    string Summary, string Requirements, string Costs, Target[] Targets);
public record Target(string Workload, string Environment, string Subscription, string SubscriptionId, string Network, bool Enabled);
public record Skill(string Id, string Description, string Content);
public record RunRequest(string Product, string Environment, string Region, string Operation, int? DiscoveryRunId);

public sealed class Catalog(PortalOptions options)
{
    public string Root => options.RepositoryRoot;
    static JsonElement Read(string file) => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(file));
    public string[] Regions => Read(Path.Combine(Root, "config/platform.json")).GetProperty("approvedRegions").EnumerateArray().Select(x => x.GetString()!).ToArray();
    // These two implemented adapters also define the current dedicated YAML menus.
    public Product[] Products => new[] { Product("blobcopy", "blob-transfer", "Blob copy"), Product("eventflow", "logic-app-event-grid", "Event flow") };
    Product Product(string id, string type, string name)
    {
        var discover = $"azure-pipelines-{id}-discover.yml";
        var deploy = $"azure-pipelines-{id}-deploy.yml";
        var text = File.ReadAllText(Path.Combine(Root, deploy));
        var settings = Read(Path.Combine(Root, "self-service/pipeline-settings.json"));
        var targets = Directory.GetFiles(Path.Combine(Root, "self-service/targets"), "*.json").Select(Read)
            .Where(t => t.GetProperty("workload").GetString() == id)
            .Select(t => new Target(id, t.GetProperty("environmentName").GetString()!, t.GetProperty("subscriptionAlias").GetString()!,
                t.GetProperty("subscriptionId").GetString()!, t.GetProperty("networkProfile").GetString()!, t.GetProperty("enabled").GetBoolean()))
            .OrderBy(t => t.Environment).ToArray();
        return new(id, type, name, settings.GetProperty("workloadDiscoveryPipelineNames").GetProperty(type).GetString()!, $"Deploy - {name}", discover, deploy,
            MenuText(text, "workloadSummary"), MenuText(text, "requirements"), MenuText(text, "costAssumptions"), targets);
    }
    // Parse only the generator's JSON-quoted reference defaults, never executable YAML.
    static string MenuText(string yaml, string name)
    {
        var match = Regex.Match(yaml, $@"(?m)^  - name: {Regex.Escape(name)}\r?\n(?:(?!  - name:)[^\n]*\n)*?    default: (""[^\r\n]*"")\r?$", RegexOptions.None, TimeSpan.FromSeconds(1));
        return match.Success ? JsonSerializer.Deserialize<string>(match.Groups[1].Value)! : "See the generated pipeline menu for current details.";
    }
    public Skill[] Skills => Directory.GetDirectories(Path.Combine(Root, ".agents/skills")).Order().Select(dir =>
    {
        var text = File.ReadAllText(Path.Combine(dir, "SKILL.md"));
        var desc = Regex.Match(text, "(?m)^description: (.+)$").Groups[1].Value.Trim();
        return new Skill(Path.GetFileName(dir), desc, text);
    }).ToArray();
    public (Product Product, Target Target) Validate(RunRequest request)
    {
        var p = Products.SingleOrDefault(p => p.Id == request.Product) ?? throw new PortalException("Unknown workload.");
        var t = p.Targets.SingleOrDefault(t => t.Environment == request.Environment) ?? throw new PortalException("Unregistered environment.");
        if (!Regions.Contains(request.Region)) throw new PortalException("Unapproved region.");
        if (request.Operation is not ("discover" or "preview" or "deploy")) throw new PortalException("Unknown operation.");
        if (request.Operation == "deploy" && !t.Enabled) throw new PortalException("This target is disabled. Platform onboarding is required.", 409);
        if (request.Operation != "discover" && request.DiscoveryRunId is not > 0) throw new PortalException("Select a successful discovery run.");
        return (p, t);
    }
    public object Payload(RunRequest r)
    {
        var (p, t) = Validate(r);
        var resources = new Dictionary<string, object> { ["repositories"] = new { self = new { refName = "refs/heads/main" } } };
        object parameters;
        if (r.Operation == "discover") parameters = new { workload = t.Workload, environment = t.Environment, subscription = t.Subscription, network = t.Network };
        else
        {
            resources["pipelines"] = new { discovery = new { version = r.DiscoveryRunId!.Value.ToString(System.Globalization.CultureInfo.InvariantCulture) } };
            parameters = new { workloadName = t.Workload, environment = t.Environment, region = r.Region, executionMode = r.Operation == "preview" ? "Preview only" : "Preview and deploy" };
        }
        return new { resources, templateParameters = parameters };
    }
}
