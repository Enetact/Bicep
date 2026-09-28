using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Portal;

public record Product(string Id, string Type, string Name, string DiscoverName, string DeployName, string DiscoverYaml, string DeployYaml,
    string Summary, string Requirements, string Costs, Target[] Targets);
public record Target(string Workload, string Environment, string Subscription, string SubscriptionId, string Network, bool Enabled);
public record Skill(string Id, string Name, string Description, string Content, string Origin, string PipelineStatus, string DiscoveryProfile, string? SourceUrl);
public record RunRequest(string Product, string Environment, string Region, string Operation, int? DiscoveryRunId);

public sealed class Catalog(PortalOptions options)
{
    public string Root => options.RepositoryRoot;
    // Display metadata only. Execution cannot be enabled by editing this catalog.
    public JsonElement RecoveryPolicies
    {
        get
        {
            var c = Read(Path.Combine(Root, "config/recovery-capabilities.json"));
            if (c.GetProperty("schemaVersion").GetInt32() != 1 || c.GetProperty("executionEnabled").GetBoolean())
                throw new InvalidOperationException("Recovery catalog cannot enable execution.");
            var ids = c.GetProperty("policies").EnumerateArray().Select(p => p.GetProperty("id").GetString()!).ToArray();
            var expected = ProductTypes.Concat(new[] { "tags-external", "tags-source", "network-ipam", "pipeline-registration", "discovery", "agent-review", "bicep-draft" }).ToArray();
            if (ids.Length != expected.Length || !ids.Order().SequenceEqual(expected.Order()))
                throw new InvalidOperationException("Recovery catalog must cover exactly the registered workflows.");
            return c;
        }
    }
    static JsonElement Read(string file) => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(file));
    public string[] Regions => Read(Path.Combine(Root, "config/platform.json")).GetProperty("approvedRegions").EnumerateArray().Select(x => x.GetString()!).ToArray();
    // Explicit source allowlist: configuration describes products but cannot supply executable paths.
    static readonly string[] ProductTypes = ["blob-transfer", "logic-app-event-grid", "private-storage", "key-vault", "observability", "http-functions", "service-bus-worker"];
    public Product[] Products => ProductTypes.Select(type =>
    {
        var d = Read(Path.Combine(Root, "config/workloads.json")).GetProperty("workloads").GetProperty(type);
        var slug = d.GetProperty("menuSlug").GetString()!;
        if (!Regex.IsMatch(slug, "^[a-z0-9]{3,10}$")) throw new InvalidOperationException("Invalid registered product slug.");
        return Product(slug, type, d.GetProperty("displayName").GetString()!);
    }).ToArray();
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
    public Skill[] Skills => LocalSkills.Concat(AzureSkills).ToArray();
    IEnumerable<Skill> LocalSkills => Directory.GetDirectories(Path.Combine(Root, ".agents/skills")).Order().Select(dir =>
    {
        var text = File.ReadAllText(Path.Combine(dir, "SKILL.md"));
        var desc = Regex.Match(text, "(?m)^description: (.+)$").Groups[1].Value.Trim();
        var id = Path.GetFileName(dir);
        return new Skill(id, id, desc, text, "Project", "Local guidance; no direct pipeline", "none", null);
    }).ToArray();
    IEnumerable<Skill> AzureSkills
    {
        get
        {
            var root = Path.Combine(Root, "vendor/azure-skills");
            var bundle = Read(Path.Combine(root, "bundle.json"));
            foreach (var s in bundle.GetProperty("skills").EnumerateArray())
            {
                var relative = s.GetProperty("path").GetString()!;
                if (relative.Split('/').Any(p => p is ".." or "." or "") || Path.IsPathRooted(relative) || relative.Contains('\\')) throw new InvalidOperationException("Invalid bundled skill path.");
                yield return new Skill(s.GetProperty("id").GetString()!, s.GetProperty("name").GetString()!, s.GetProperty("description").GetString()!,
                    File.ReadAllText(Path.Combine(root, relative)), "Microsoft Azure Skills", "No pipeline associated yet", s.GetProperty("discoveryProfile").GetString()!,
                    $"https://github.com/microsoft/azure-skills/blob/{bundle.GetProperty("commit").GetString()}/{relative}");
            }
        }
    }
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
