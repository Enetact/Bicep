using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SelfService.Tagging;

namespace SelfService.Portal;

public record BicepModuleContract(string Id, string Path, string SourceSha256, bool DraftSelectable,
    Dictionary<string, JsonElement> Parameters, Dictionary<string, JsonElement> Outputs, string[] ResourceTypes,
    Dictionary<string, JsonElement>? Definitions = null, Dictionary<string, string>? Sources = null, JsonElement? Origin = null);
public record BicepModuleCatalog(int SchemaVersion, string Purpose, BicepModuleContract[] Modules);
public record BicepBinding(string Kind, string? Reference = null, string? Module = null, string? Output = null);
public record BicepNode(string Id, string ModuleId, string Rationale, Dictionary<string, BicepBinding> Bindings);
public record BicepProposal(string Schema, string EvidenceDigest, string Summary, BicepNode[] Modules, string[] Gaps);
public record BicepObservedResource(string Id, string Type);
public record BicepDraftContext(string Schema, string EvidenceDigest, string CatalogDigest, string SubscriptionId,
    string Product, string Environment, string Region, string Goal, string DiscoveryStatus,
    Dictionary<string, JsonElement> Settings, BicepObservedResource[] Resources, BicepModuleContract[] Modules, JsonElement? NestedEvidence = null);
public record BicepDraftResult(string Status, string Summary, string[] RequiredInputs, string[] Gaps,
    Dictionary<string, string> Files, string ArchiveBase64);

// The model chooses typed bindings. Only this emitter writes source, using reviewed local modules and pinned AVM closures.
// It has no Azure client, pipeline gateway, shell, publication or deployment method.
public static class BicepDrafts
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static readonly Regex Identifier = new("^[a-z][a-zA-Z0-9]{0,31}$", RegexOptions.CultureInvariant);
    static readonly string[] Paths = ["compute/private-functions/main.bicep", "event-grid/event-subscription/main.bicep",
        "event-grid/topic/main.bicep", "logic-app/standard/main.bicep", "messaging/service-bus/main.bicep",
        "monitoring/log-analytics/main.bicep", "monitoring/observability/main.bicep", "network/ipam-reservation.bicep",
        "network/private-dns-zone/main.bicep", "network/private-endpoint/main.bicep", "network/workload-vnet/main.bicep",
        "security/key-vault/main.bicep", "storage/storage-account/main.bicep",
        "analytics/data-factory/main.bicep", "analytics/databricks-workspace/main.bicep", "storage/data-lake/main.bicep", "compute/private-aks/main.bicep"];
    static readonly string[] SettingKeys = ["owner", "costCenter", "existingLogAnalyticsWorkspaceId", "integrationSubnetId",
        "privateEndpointSubnetId", "privateDnsZoneIds", "deploymentPrincipalObjectId", "consumerPrincipalObjectId",
        "readerPrincipalObjectId", "apiClientId", "allowedClientApplications", "alertActionGroupIds"];
    public static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value.Replace("\r\n", "\n")))).ToLowerInvariant();
    public static BicepModuleCatalog Catalog(string root)
    {
        var catalog = TagJson.Read<BicepModuleCatalog>(File.ReadAllText(Path.Combine(root, "config/bicep-module-contracts.json")));
        if (catalog.SchemaVersion != 1 || catalog.Modules.Length != Paths.Length || catalog.Modules.Select(m => m.Path).Distinct().Count() != Paths.Length)
            throw new PortalException("Refresh the reviewed module contracts.", 409);
        foreach (var m in catalog.Modules)
        {
            if (!Paths.Any(p => "modules/" + p == m.Path) ||
                Hash(File.ReadAllText(Path.Combine(root, m.Path))) != m.SourceSha256)
                throw new PortalException("Module source differs from its reviewed contract. Regenerate and review the catalog.", 409);
        }
        BicepModuleCatalog avm;
        try { avm = TagJson.Read<BicepModuleCatalog>(File.ReadAllText(Path.Combine(root, "config/avm-module-contracts.json"))); }
        catch { throw new PortalException("Refresh the reviewed AVM module contracts.", 409); }
        if (avm.SchemaVersion != 1 || avm.Modules.Length != BicepAvmSources.Paths.Length ||
            avm.Modules.Select(m => m.Path).Distinct(StringComparer.Ordinal).Count() != BicepAvmSources.Paths.Length)
            throw new PortalException("Refresh the reviewed AVM module contracts.", 409);
        foreach (var module in avm.Modules)
        {
            _ = BicepAvmSources.Read(root, module);
            foreach (var parameter in module.Parameters.Values) _ = BicepParameterSchema.Expand(parameter, module.Definitions);
        }
        var merged = catalog.Modules.Concat(avm.Modules).ToArray();
        if (merged.Select(m => m.Id).Distinct(StringComparer.Ordinal).Count() != merged.Length) throw new PortalException("Duplicate module contract identifier.", 409);
        return catalog with { Modules = merged };
    }
    public static BicepDraftContext Context(string root, Catalog catalog, BrowserSession session, AgentRequest request)
    {
        if (request.DraftGoal is not { Length: > 0 and <= 2000 } || string.IsNullOrWhiteSpace(request.DraftGoal)) throw new PortalException("Describe the infrastructure draft in up to 2,000 characters.");
        if (request.NetworkReportId is null || !session.NetworkReports.TryGetValue(request.NetworkReportId, out var snapshot) ||
            snapshot.GetProperty("generatedUtc").GetDateTimeOffset() < DateTimeOffset.UtcNow.AddMinutes(-15) ||
            snapshot.GetProperty("generatedUtc").GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(1))
            throw new PortalException("Select your own network discovery snapshot from the last 15 minutes.", 409);
        var product = catalog.Products.SingleOrDefault(p => p.Id == request.Product) ?? throw new PortalException("Select a registered workload context.");
        var target = product.Targets.SingleOrDefault(t => t.Environment == request.Environment) ?? throw new PortalException("Select a registered environment.");
        if (request.Region is null || !catalog.Regions.Contains(request.Region)) throw new PortalException("Select an approved region.");
        var reports = snapshot.GetProperty("reports").EnumerateArray().Where(r => string.Equals(EvidenceDiagram.Text(r, "subscriptionId"), target.SubscriptionId, StringComparison.OrdinalIgnoreCase)).ToArray();
        if (reports.Length != 1) throw new PortalException("Discovery must include the selected workload subscription.");
        var resources = new Dictionary<string, BicepObservedResource>(StringComparer.OrdinalIgnoreCase);
        foreach (var collection in EvidenceDiagram.Array(reports[0], "collections"))
        foreach (var r in EvidenceDiagram.Array(collection, "resources"))
        {
            Add(r);
            // AzureDiscovery projects subnets at the top level; ARM property bags are never forwarded here.
            foreach (var subnet in EvidenceDiagram.Array(r, "subnets"))
            {
                var parentId=EvidenceDiagram.Text(r,"id");var subnetId=EvidenceDiagram.Text(subnet,"id");
                if(EvidenceDiagram.Text(r,"type")?.Equals("Microsoft.Network/virtualNetworks",StringComparison.OrdinalIgnoreCase)==true && parentId is not null && subnetId?.StartsWith(parentId+"/subnets/",StringComparison.OrdinalIgnoreCase)==true)
                    Add(JsonSerializer.SerializeToElement(new{id=subnetId,type="Microsoft.Network/virtualNetworks/subnets"}));
            }
        }
        void Add(JsonElement r)
        {
            var id = EvidenceDiagram.Text(r, "id"); var type = EvidenceDiagram.Text(r, "type");
            if (id is not null && id.StartsWith($"/subscriptions/{target.SubscriptionId}/resourceGroups/", StringComparison.OrdinalIgnoreCase) && id.Length <= 2048 && type is { Length: > 0 and <= 200 })
                resources.TryAdd(id, new(id, type));
        }
        var settings = new Dictionary<string, JsonElement> { ["workload"] = JsonSerializer.SerializeToElement(target.Workload),
            ["environmentName"] = JsonSerializer.SerializeToElement(target.Environment), ["location"] = JsonSerializer.SerializeToElement(request.Region) };
        var profile = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(root, $"self-service/targets/{product.Id}.{target.Environment}.json")));
        foreach (var key in SettingKeys)
            if (profile.GetProperty("parameterOverrides").TryGetProperty(key, out var value) && value.GetRawText().Length <= 8192 &&
                !value.GetRawText().Contains("REPLACE", StringComparison.OrdinalIgnoreCase) && !value.GetRawText().Contains("00000000-0000-0000-0000-000000000000", StringComparison.Ordinal) &&
                value.ValueKind is not (JsonValueKind.Null or JsonValueKind.Undefined) &&
                !(value.ValueKind == JsonValueKind.String && string.IsNullOrWhiteSpace(value.GetString())) &&
                !(value.ValueKind == JsonValueKind.Array && value.GetArrayLength() == 0) &&
                !(value.ValueKind == JsonValueKind.Object && !value.EnumerateObject().Any()))
                settings[key] = value.Clone();
        var modules = Catalog(root).Modules.Where(m => m.DraftSelectable && m.Id != "network-ipam-reservation").ToArray();
        var catalogDigest = Hash(JsonSerializer.Serialize(modules, Json));
        var status = EvidenceDiagram.Text(reports[0], "status") ?? "Unknown";
        var digest = Hash(JsonSerializer.Serialize(new { snapshot = snapshot.GetProperty("id"), observed = snapshot.GetProperty("generatedUtc"), report = reports[0], settings, catalogDigest, request.DraftGoal, product.Id, target.Environment, request.Region }, Json));
        return new("platform.bicep-context/v1", digest, catalogDigest, target.SubscriptionId, product.Id, target.Environment, request.Region,
            request.DraftGoal, status, settings, resources.Values.OrderBy(r => r.Id).ToArray(), modules,reports[0].Clone());
    }
    public static BicepDraftResult Generate(string root, BicepDraftContext context, string output)
    {
        var proposal = TagJson.Read<BicepProposal>(output);
        if (proposal.Schema != "platform.bicep-proposal/v1" || proposal.EvidenceDigest != context.EvidenceDigest ||
            proposal.Summary is not { Length: > 0 and <= 8000 } || proposal.Modules is not { Length: > 0 and <= 16 } ||
            proposal.Gaps is null || proposal.Gaps.Length > 30 || proposal.Gaps.Any(g => g is null || g.Length > 2000)) throw new PortalException("Invalid Bicep proposal contract.");
        var modules = Catalog(root).Modules.Where(m => m.DraftSelectable && m.Id != "network-ipam-reservation").ToArray();
        if (Hash(JsonSerializer.Serialize(modules, Json)) != context.CatalogDigest) throw new PortalException("Module contracts changed during the review.", 409);
        var nodes = new Dictionary<string, BicepNode>(StringComparer.OrdinalIgnoreCase);
        foreach (var node in proposal.Modules)
            if (node is null || node.Id is null || !Identifier.IsMatch(node.Id) || !nodes.TryAdd(node.Id, node) ||
                !modules.Any(m => m.Id == node.ModuleId) || node.Rationale is not { Length: > 0 and <= 2000 } || node.Bindings is null)
                throw new PortalException("Unknown, duplicate or invalid composition node.");
        var declarations = new List<string>(); var types = new List<string>(); var bodies = new List<string>(); var required = new List<string>(); var parameterNames = new List<string>();
        var values = new Dictionary<string, JsonElement>(); var provenance = new Dictionary<string, object>();
        var edges = nodes.Keys.ToDictionary(k => k, _ => new List<string>(), StringComparer.OrdinalIgnoreCase);
        var files = new Dictionary<string, string>();
        foreach (var node in nodes.Values)
        {
            var contract = modules.Single(m => m.Id == node.ModuleId);
            if (node.Bindings.Keys.Any(k => !contract.Parameters.ContainsKey(k))) throw new PortalException("Unknown module parameter.");
            var body = new StringBuilder($"module m_{node.Id} './{contract.Path}' = {{\n  params: {{\n");
            foreach (var (key, rawDefinition) in contract.Parameters)
            {
                var definition = BicepParameterSchema.Expand(rawDefinition, contract.Definitions);
                if (!Identifier.IsMatch(key)) throw new PortalException("Unsupported parameter identifier.");
                var supplied = node.Bindings.TryGetValue(key, out var binding);
                if (supplied && binding is null) throw new PortalException("Null binding is not a reviewed input.");
                if (!supplied && definition.TryGetProperty("defaultValue", out _)) continue;
                binding ??= new("Input");
                var name = $"p_{node.Id}_{key}"; var type = definition.GetProperty("type").GetString();
                if (binding.Kind != "Output" && (binding.Module is not null || binding.Output is not null) || binding.Kind == "Output" && binding.Reference is not null || binding.Kind == "Input" && binding.Reference is not null)
                    throw new PortalException("Unexpected binding fields.");
                if (binding.Kind == "Output")
                {
                    if (binding.Module is null || !nodes.TryGetValue(binding.Module, out var producer) || producer.Id == node.Id || binding.Module != producer.Id || binding.Output is null)
                        throw new PortalException("Unknown output dependency.");
                    var producerContract = modules.Single(m => m.Id == producer.ModuleId);
                    if (!producerContract.Outputs.TryGetValue(binding.Output, out var source) || source.GetProperty("type").GetString() != type || !Identifier.IsMatch(binding.Output)) throw new PortalException("Output type or reference mismatch.");
                    if (definition.TryGetProperty("properties", out _) || definition.TryGetProperty("items", out _)) throw new PortalException("Structured output bindings require a separately qualified adapter.");
                    edges[node.Id].Add(producer.Id); body.AppendLine($"    {key}: m_{producer.Id}.outputs.{binding.Output}");
                    provenance[name] = new { binding.Kind, binding.Module, binding.Output, status = "Proposed output; semantic compatibility requires review" }; continue;
                }
                JsonElement? value = null;
                switch (binding.Kind)
                {
                    case "Input": break;
                    case "Setting":
                        if (binding.Reference is null || !context.Settings.TryGetValue(binding.Reference, out var setting)) throw new PortalException("Unknown approved setting.");
                        value = setting; break;
                    case "Resource":
                        var resource = context.Resources.SingleOrDefault(r => r.Id == binding.Reference) ?? throw new PortalException("Unobserved resource ID.");
                        var expected = key switch { "workspaceId" => "Microsoft.OperationalInsights/workspaces", "integrationSubnetId" or "subnetId" => "Microsoft.Network/virtualNetworks/subnets", "virtualNetworkId" => "Microsoft.Network/virtualNetworks", "storageAccountId" => "Microsoft.Storage/storageAccounts", "topicId" => "Microsoft.EventGrid/topics", _ => null };
                        if (expected is null || !resource.Type.Equals(expected, StringComparison.OrdinalIgnoreCase)) throw new PortalException("Resource reference is incompatible with this input. Use an unresolved reviewed input.");
                        value = JsonSerializer.SerializeToElement(resource.Id); break;
                    default: throw new PortalException("Only Input, Setting, Resource and Output bindings are permitted.");
                }
                declarations.AddRange(BicepParameterSchema.Decorators(definition));
                declarations.Add($"param {name} {BicepParameterSchema.Type(definition, name, types)}");
                parameterNames.Add(name);
                if (value is { } resolved) { BicepParameterSchema.Validate(resolved, definition); values[name] = resolved; } else required.Add(name);
                provenance[name] = new { binding.Kind, binding.Reference, status = value is null ? "Required input" : binding.Kind == "Resource" ? "Observed ID; eligibility and reuse not authorized" : "Source setting; live validation required" };
                body.AppendLine($"    {key}: {name}");
            }
            body.AppendLine("  }\n}"); bodies.Add(body.ToString());
            if (contract.Sources is not null)
            {
                foreach (var source in BicepAvmSources.Read(root, contract))
                    if (!files.TryAdd(source.Key, source.Value) && files[source.Key] != source.Value) throw new PortalException("Conflicting module source closure.", 409);
            }
            else
            {
                var moduleSource = File.ReadAllText(Path.Combine(root, contract.Path)).Replace("\r\n", "\n");
                if (Hash(moduleSource) != contract.SourceSha256) throw new PortalException("Module source changed while emitting the draft.", 409);
                files.TryAdd(contract.Path, moduleSource);
            }
        }
        var visiting = new HashSet<string>(); var done = new HashSet<string>();
        void Visit(string id) { if (done.Contains(id)) return; if (!visiting.Add(id)) throw new PortalException("Cyclic module dependency."); foreach (var next in edges[id]) Visit(next); visiting.Remove(id); done.Add(id); }
        foreach (var id in nodes.Keys) Visit(id);
        files["main.bicep"] = "// UNQUALIFIED SOURCE DRAFT. Review ownership, parameters and dependencies before promotion.\ntargetScope = 'resourceGroup'\n\n" + string.Join('\n', types) + "\n\n" + string.Join('\n', declarations) + "\n\n" + string.Join('\n', bodies);
        files["main.bicepparam"] = "using './main.bicep'\n\n" + string.Join('\n', values.Keys.Select(k => $"param {k} = loadJsonContent('./resolved-values.json').{k}")) + "\n" + string.Join('\n', required.Select(k => $"// REQUIRED: supply param {k} after platform review.")) + "\n";
        files["stack.bicep"] = "// UNQUALIFIED subscription wrapper. Resource group ownership must be reviewed.\ntargetScope = 'subscription'\nparam workloadResourceGroupName string\nparam deploymentLocation string\nparam workloadTags object\n" + string.Join('\n', types) + "\n\n" + string.Join('\n', declarations) +
            "\nresource group 'Microsoft.Resources/resourceGroups@2024-03-01' = {\n  name: workloadResourceGroupName\n  location: deploymentLocation\n  tags: workloadTags\n}\nmodule composition './main.bicep' = {\n  scope: group\n  params: {\n" + string.Join('\n', parameterNames.Select(p => $"    {p}: {p}")) + "\n  }\n}\n";
        files["stack.bicepparam"] = files["main.bicepparam"].Replace("using './main.bicep'", "using './stack.bicep'", StringComparison.Ordinal) + "// REQUIRED: supply workloadResourceGroupName, deploymentLocation and workloadTags after ownership review.\n";
        files["resolved-values.json"] = JsonSerializer.Serialize(values, Json);
        files["proposal.json"] = JsonSerializer.Serialize(proposal, Json);
        files["parameter-provenance.json"] = JsonSerializer.Serialize(provenance, Json);
        files["bicepconfig.json"] = File.ReadAllText(Path.Combine(root, "bicepconfig.json"));
        files["README.md"] = "# Unqualified Bicep source draft\n\nGenerated from a validated module/binding proposal. This is not a registered workload, Azure What-If, deployment approval or IPAM reservation.\n\n1. Review proposal.json and parameter-provenance.json.\n2. Fill required inputs in main.bicepparam; inspect resolved-values.json. Environment-file defaults were not evaluated: only selected intent and allowlisted target overrides were supplied.\n3. Run `az bicep build --file main.bicep` and `az bicep build-params --file main.bicepparam`. Compile stack.bicep too; its parameter file additionally requires reviewed workloadResourceGroupName, deploymentLocation and workloadTags.\n4. Review resource naming, ownership, private endpoints/DNS, identities/RBAC, runtime dependencies, cost, policy and tests.\n5. Promote reviewed source into workloads/<new-type> using docs/workload-onboarding.md. Qualify the stack wrapper, adapter, catalog, configuration, tests and protected pipeline bindings before Preview/Deploy. Do not upload this archive to an existing deployment artifact.\n\nModule defaults remain their source defaults. IPAM reservation is excluded. No existing resource is adopted by selecting its ID. Required main inputs:\n\n" + string.Join('\n', required.Select(x => "- " + x)) + "\n\nAgent-reported gaps are in proposal.json and remain untrusted advice. All drafts require platform review even when there are no missing inputs.\n";
        files["draft-receipt.json"] = JsonSerializer.Serialize(new { schema = "platform.bicep-draft/v1", context.EvidenceDigest, context.CatalogDigest,
            context.SubscriptionId, context.Product, context.Environment, context.Region, context.DiscoveryStatus,
            status = "UnqualifiedSourceDraft", compilation = "Required", deploymentAuthorized = false, requiredInputs = required,
            requiredStackSettings = new[] { "workloadResourceGroupName", "deploymentLocation", "workloadTags" },
            modules = proposal.Modules.Select(n => { var module = modules.Single(m => m.Id == n.ModuleId); return new { n.Id, n.ModuleId, sourceSha256 = module.SourceSha256, sources = module.Sources, origin = module.Origin }; }),
            files = files.ToDictionary(p => p.Key, p => Hash(p.Value)) }, Json);
        using var bytes = new MemoryStream();
        using (var zip = new ZipArchive(bytes, ZipArchiveMode.Create, true)) foreach (var (path, content) in files) { using var writer = new StreamWriter(zip.CreateEntry(path).Open(), new UTF8Encoding(false)); writer.Write(content); }
        return new("UnqualifiedSourceDraft", proposal.Summary, required.ToArray(), proposal.Gaps, files, Convert.ToBase64String(bytes.ToArray()));
    }
}
