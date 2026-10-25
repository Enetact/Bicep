using System.Text;
using System.Text.Json;

namespace SelfService.Portal;

// Each response is a complete frozen lookup, never a truncated context dump.
// Coverage is committed only after the bridge attests successful MCP delivery.
public sealed class BicepEvidenceView
{
    const int MaxResponseBytes = 12_000, MaxSettingBytes = 8_192, ResourcePageSize = 3, NetworkChunkSize = 4_000;
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static readonly HashSet<string> InlineSettings = ["workload", "environmentName", "location"];
    readonly record struct Request(string Kind, string? Key = null, int Page = 0);
    sealed record Frozen(string Json, string Digest, string Response);
    readonly object gate = new();
    readonly string evidenceDigest, catalogDigest, summary;
    readonly Dictionary<string, Frozen> modules = new(StringComparer.Ordinal), settings = new(StringComparer.Ordinal);
    readonly string[] resourcePages, networkPages;
    readonly string[][] pageResourceIds;
    readonly HashSet<Request> delivered = [];
    readonly Dictionary<string, string> modulesRead = new(StringComparer.Ordinal), settingsRead = new(StringComparer.Ordinal);
    readonly HashSet<string> resourcesRead = new(StringComparer.Ordinal);
    readonly HashSet<int> networkRead = [];
    bool baseRead;

    public BicepEvidenceView(BicepDraftContext context)
    {
        // Round-trip once, before retaining anything, so dictionaries, arrays and
        // JsonElements owned by a caller cannot change an in-flight model view.
        var frozen = JsonSerializer.Deserialize<BicepDraftContext>(JsonSerializer.Serialize(context, Json), Json)
            ?? throw InvalidRequest();
        evidenceDigest = frozen.EvidenceDigest; catalogDigest = frozen.CatalogDigest;
        foreach (var module in frozen.Modules)
        {
            var text = JsonSerializer.Serialize(module, Json); var digest = BicepDrafts.Hash(text);
            if (!modules.TryAdd(module.Id, new(text, digest, Serialize(new { schema = "platform.bicep-evidence-module/v1", evidenceDigest, catalogDigest, module, contractSha256 = digest }))))
                throw InvalidRequest();
        }
        foreach (var (key, value) in frozen.Settings)
        {
            var text = JsonSerializer.Serialize(value, Json); var digest = BicepDrafts.Hash(text);
            settings.Add(key, new(text, digest, Serialize(new { schema = "platform.bicep-evidence-setting/v1", evidenceDigest, catalogDigest, key, value, settingSha256 = digest })));
        }
        var resources = frozen.Resources;
        resourcePages = new string[Math.Max(1, (resources.Length + ResourcePageSize - 1) / ResourcePageSize)];
        pageResourceIds = new string[resourcePages.Length][];
        for (var page = 0; page < resourcePages.Length; page++)
        {
            var selected = resources.Skip(page * ResourcePageSize).Take(ResourcePageSize).ToArray();
            pageResourceIds[page] = selected.Select(r => r.Id).ToArray();
            resourcePages[page] = Serialize(new { schema = "platform.bicep-evidence-resources/v1", evidenceDigest, catalogDigest, section = "resources", page,
                pageSize = ResourcePageSize, totalResources = resources.Length, totalPages = resourcePages.Length,
                nextPage = page + 1 < resourcePages.Length ? (int?)(page + 1) : null, resources = selected });
        }
        if (frozen.NestedEvidence is { ValueKind: not (JsonValueKind.Null or JsonValueKind.Undefined) } network)
        {
            var text = JsonSerializer.Serialize(network, Json); var digest = BicepDrafts.Hash(text);
            networkPages = new string[Math.Max(1, (text.Length + NetworkChunkSize - 1) / NetworkChunkSize)];
            for (var page = 0; page < networkPages.Length; page++)
                networkPages[page] = Serialize(new { schema = "platform.bicep-evidence-network/v1", evidenceDigest, catalogDigest, section = "network", page,
                    totalPages = networkPages.Length, nextPage = page + 1 < networkPages.Length ? (int?)(page + 1) : null,
                    contentSha256 = digest, chunk = text.Substring(page * NetworkChunkSize, Math.Min(NetworkChunkSize, text.Length - page * NetworkChunkSize)) });
        }
        else networkPages = [];
        summary = Serialize(new { schema = frozen.Schema, viewSchema = "platform.bicep-evidence-index/v1", evidenceDigest, catalogDigest, goal = frozen.Goal,
            scope = new { frozen.SubscriptionId, frozen.Product, frozen.Environment, frozen.Region }, discoveryStatus = frozen.DiscoveryStatus,
            settings = frozen.Settings.Where(p => InlineSettings.Contains(p.Key)).ToDictionary(p => p.Key, p => p.Value),
            settingsIndex = frozen.Settings.Select(p => new { key = p.Key, type = p.Value.ValueKind.ToString(), inline = InlineSettings.Contains(p.Key) }).ToArray(),
            modules = frozen.Modules.Select(m => new { m.Id, m.Path, m.SourceSha256,
                version = m.Origin is { ValueKind: JsonValueKind.Object } origin && origin.TryGetProperty("version", out var version) ? version.GetString() : null,
                contractSha256 = modules[m.Id].Digest }).ToArray(),
            resources = new { count = resources.Length, pageSize = ResourcePageSize, totalPages = resourcePages.Length, section = "resources" },
            network = new { available = networkPages.Length > 0, totalPages = networkPages.Length, section = "network", chunkCharacters = NetworkChunkSize } });
        _ = Bound(summary);
    }

    public bool BaseRead { get { lock (gate) return baseRead; } }
    public object Coverage
    {
        get
        {
            lock (gate) return new { schema = "platform.bicep-evidence-coverage/v1", evidenceDigest, catalogDigest, baseRead,
                modules = modulesRead.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { id = p.Key, contractSha256 = p.Value }).ToArray(),
                settings = settingsRead.OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { key = p.Key, settingSha256 = p.Value }).ToArray(),
                resources = resourcesRead.Order(StringComparer.Ordinal).Select(id => new { idSha256 = BicepDrafts.Hash(id) }).ToArray(),
                networkPages = networkRead.Order().ToArray() };
        }
    }

    public string Read(string? moduleId = null, string? setting = null, string? section = null, int? page = null)
    {
        var arguments = new Dictionary<string, object>();
        if (moduleId is not null) arguments["moduleId"] = moduleId;
        if (setting is not null) arguments["setting"] = setting;
        if (section is not null) arguments["section"] = section;
        if (page is not null) arguments["page"] = page.Value;
        var request = Parse(JsonSerializer.SerializeToElement(arguments, Json));
        var response = request.Kind switch { "base" => summary, "module" => modules[request.Key!].Response,
            "setting" => Encoding.UTF8.GetByteCount(settings[request.Key!].Json) <= MaxSettingBytes ? settings[request.Key!].Response : throw TooLarge(),
            "resources" => resourcePages[request.Page], "network" => networkPages[request.Page], _ => throw InvalidRequest() };
        response = Bound(response);
        lock (gate) delivered.Add(request);
        return response;
    }

    public void ValidateRequest(JsonElement args) => _ = Parse(args);

    public void MarkRead(JsonElement args)
    {
        var request = Parse(args);
        lock (gate)
        {
            if (!delivered.Contains(request)) throw new PortalException("Evidence delivery was not completed.", 409);
            switch (request.Kind)
            {
                case "base": baseRead = true; break;
                case "module":
                    var module = modules[request.Key!];
                    if (BicepDrafts.Hash(module.Json) != module.Digest) throw new PortalException("Evidence delivery could not be verified.", 409);
                    modulesRead[request.Key!] = module.Digest; break;
                case "setting": settingsRead[request.Key!] = settings[request.Key!].Digest; break;
                case "resources": foreach (var id in pageResourceIds[request.Page]) resourcesRead.Add(id); break;
                case "network": networkRead.Add(request.Page); break;
            }
        }
    }

    public void ValidateProposal(string modelJson)
    {
        BicepProposal proposal;
        try { proposal = JsonSerializer.Deserialize<BicepProposal>(modelJson, Json) ?? throw InvalidRequest(); }
        catch (JsonException) { throw new PortalException("Invalid Bicep proposal contract."); }
        if (proposal.Modules is not { Length: > 0 and <= 16 }) throw new PortalException("Invalid Bicep proposal contract.");
        lock (gate)
        {
            if (!baseRead) throw new PortalException("Read the Bicep evidence index before proposing source.", 409);
            foreach (var node in proposal.Modules)
            {
                if (node is null || node.ModuleId is null || !modules.TryGetValue(node.ModuleId, out var module) ||
                    !modulesRead.TryGetValue(node.ModuleId, out var digest) || digest != module.Digest || BicepDrafts.Hash(module.Json) != digest)
                    throw new PortalException("Read every selected module contract before proposing source.", 409);
                if (node.Bindings is null) throw new PortalException("Invalid Bicep proposal contract.");
                foreach (var binding in node.Bindings.Values)
                {
                    if (binding is null) throw new PortalException("Invalid Bicep proposal contract.");
                    if (binding.Kind == "Setting" && (binding.Reference is null || !settings.ContainsKey(binding.Reference) ||
                        !InlineSettings.Contains(binding.Reference) && (!settingsRead.TryGetValue(binding.Reference, out var settingDigest) || settingDigest != settings[binding.Reference].Digest)))
                        throw new PortalException("Read each referenced setting before proposing source.", 409);
                    if (binding.Kind == "Resource" && (binding.Reference is null || !resourcesRead.Contains(binding.Reference)))
                        throw new PortalException("Read each referenced observed resource page before proposing source.", 409);
                }
            }
        }
    }

    Request Parse(JsonElement args)
    {
        if (args.ValueKind != JsonValueKind.Object) throw InvalidRequest();
        string? module = null, setting = null, section = null; int? page = null;
        var names = new HashSet<string>(StringComparer.Ordinal);
        foreach (var property in args.EnumerateObject())
        {
            if (!names.Add(property.Name)) throw InvalidRequest();
            if (property.Name == "page")
            {
                if (property.Value.ValueKind != JsonValueKind.Number || !property.Value.TryGetInt32(out var value) || value < 0) throw InvalidRequest();
                page = value; continue;
            }
            if (property.Name is not ("moduleId" or "setting" or "section") || property.Value.ValueKind != JsonValueKind.String || string.IsNullOrWhiteSpace(property.Value.GetString())) throw InvalidRequest();
            var text = property.Value.GetString();
            switch (property.Name) { case "moduleId": module = text; break; case "setting": setting = text; break; case "section": section = text; break; }
        }
        if ((module is null ? 0 : 1) + (setting is null ? 0 : 1) + (section is null ? 0 : 1) > 1 || page is not null && section is null) throw InvalidRequest();
        if (module is not null) return modules.ContainsKey(module) ? new("module", module) : throw InvalidRequest();
        if (setting is not null) return settings.ContainsKey(setting) ? new("setting", setting) : throw InvalidRequest();
        if (section is null) return new("base");
        var selected = page ?? 0;
        return section switch { "resources" when selected < resourcePages.Length => new("resources", Page: selected),
            "network" when selected < networkPages.Length => new("network", Page: selected), _ => throw InvalidRequest() };
    }
    static string Serialize(object value) => JsonSerializer.Serialize(value, Json);
    static string Bound(string value) => Encoding.UTF8.GetByteCount(value) <= MaxResponseBytes ? value : throw TooLarge();
    static PortalException TooLarge() => new("This evidence item exceeds the supported response size. Use a narrower reviewed input.", 413);
    static PortalException InvalidRequest() => new("Invalid Bicep evidence lookup.");
}
