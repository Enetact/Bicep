using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Portal;

public record PipelineEntry(string Yaml, string Name, string Category, string BlobSha);
public record PipelineCatalog(string Repository, string Branch, string Hash, PipelineEntry[] Entries);
public record RegistrationSelection(int SourcePipelineId, string[] Pipelines);
public record RegistrationSeed(int Id, string Name, string ConnectionId, int QueueId);
public record RegistrationRow(string Yaml, string Name, string Category, string Status, int? ExistingId, string Detail);
public record RegistrationInventory(string Repository, string Branch, string Commit, string CatalogHash, RegistrationSeed[] Sources, RegistrationRow[] Pipelines);
public record RegistrationReview(RegistrationSelection Selection, string CatalogHash, string Commit, RegistrationSeed Seed, DateTimeOffset Expires);
public record RegistrationOutcome(string Yaml, string Name, string Status, int? Id, string Detail);

// Only fixed ADO definition GET/POST and anonymous GitHub GET routes. No queue, update,
// delete, permissions, service-endpoint creation or GitHub write operation exists here.
public sealed class PipelineRegistration(HttpClient http, ITokenProvider identity, Catalog catalog, PortalOptions options)
{
    static readonly SemaphoreSlim ApplyGate = new(1, 1);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static string? Text(JsonElement e, string key) => EvidenceDiagram.Text(e, key);
    static JsonElement Part(JsonElement e, string key) => EvidenceDiagram.Part(e, key);
    string Api(string route) => $"{options.AdoBase}/_apis/{route}{(route.Contains('?') ? '&' : '?')}api-version=7.1";
    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static string GitBlob(string text)
    {
        // Git's blob identifier, not a credential/security hash. Match the repo's LF rule.
        var bytes = Encoding.UTF8.GetBytes(text.Replace("\r\n", "\n"));
        return Convert.ToHexString(SHA1.HashData([.. Encoding.ASCII.GetBytes($"blob {bytes.Length}\0"), .. bytes])).ToLowerInvariant();
    }
    public PipelineCatalog LocalCatalog()
    {
        var configText = File.ReadAllText(Path.Combine(catalog.Root, "config/pipeline-registration.json"));
        var config = JsonSerializer.Deserialize<JsonElement>(configText);
        var repository = Text(config, "repository") ?? ""; var branch = Text(config, "branch");
        if (config.GetProperty("schemaVersion").GetInt32() != 1 || !Regex.IsMatch(repository, "^[A-Za-z0-9_-]+/[A-Za-z0-9_.-]+$") || branch != "main")
            throw new PortalException("Registration requires a reviewed owner/repository and main branch configuration.", 409);
        var definitions = catalog.Products.SelectMany(p => new[] {
            (p.DiscoverYaml, p.DiscoverName, "Workload discovery"), (p.DeployYaml, p.DeployName, "Workload deployment") }).ToList();
        var settings = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(catalog.Root, "self-service/pipeline-settings.json")));
        foreach (var extra in config.GetProperty("additionalPipelines").EnumerateArray())
            definitions.Add((Text(extra, "yaml")!, Text(extra, "name") ?? settings.GetProperty(extra.GetProperty("nameFrom").GetString()!).GetString()!, Text(extra, "category")!));
        var entries = definitions.Select(d => {
            if (!Regex.IsMatch(d.Item1 ?? "", "^azure-pipelines(?:-[a-z0-9-]+)?\\.yml$") || string.IsNullOrWhiteSpace(d.Item2) || d.Item2.Length > 150 || d.Item2.IndexOfAny(['/', '\\', '*', '?']) >= 0)
                throw new PortalException("Invalid pipeline registration catalog entry.", 409);
            var source = File.ReadAllText(Path.Combine(catalog.Root, d.Item1!));
            // This is a contract for our reviewed, literal YAML roots, not a general YAML parser.
            if (!Regex.IsMatch(source, "(?m)^trigger: none\\s*$") || !Regex.IsMatch(source, "(?m)^pr: none\\s*$") ||
                Regex.Matches(source, "(?m)^\\s*(?:trigger|pr):[^\\r\\n]*$").Any(m => !Regex.IsMatch(m.Value.Trim(), "^(?:trigger|pr): none$")) ||
                Regex.IsMatch(source, "(?m)^\\s*schedules:"))
                throw new PortalException($"{d.Item1}: registration requires literal disabled CI/PR/resource triggers and no schedules.", 409);
            return new PipelineEntry(d.Item1!, d.Item2, d.Item3, GitBlob(source));
        }).ToArray();
        var roots = Directory.GetFiles(catalog.Root, "azure-pipelines*.yml").Select(Path.GetFileName).Order().ToArray();
        if (entries.Select(e => e.Yaml).Distinct().Count() != entries.Length || entries.Select(e => e.Name).Distinct(StringComparer.OrdinalIgnoreCase).Count() != entries.Length ||
            !roots.SequenceEqual(entries.Select(e => e.Yaml).Order())) throw new PortalException("Registration catalog must cover every root pipeline exactly once, with unique names.", 409);
        return new(repository, branch!, Hash(configText + JsonSerializer.Serialize(entries)), entries);
    }
    async Task<(JsonElement Body, string? Next)> Send(string url, BrowserSession? session, object? body, CancellationToken ct)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
        if (session is not null) request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await identity.Token(session, "ado"));
        else { request.Headers.UserAgent.ParseAdd("PlatformStudio-Registration/1.0"); request.Headers.Add("X-GitHub-Api-Version", "2022-11-28"); }
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new PortalException($"{(session is null ? "Public GitHub read" : "ADO registration request")} returned HTTP {(int)response.StatusCode}. Check access, published source and rate limits. No automatic retry.", 502);
        using var stream = await response.Content.ReadAsStreamAsync(ct); using var bytes = new MemoryStream();
        var buffer = new byte[8192]; int n;
        while ((n = await stream.ReadAsync(buffer, ct)) > 0) { if (bytes.Length + n > 8 * 1024 * 1024) throw new PortalException("Registration response exceeded its limit; inventory is incomplete.", 502); bytes.Write(buffer, 0, n); }
        return (JsonSerializer.Deserialize<JsonElement>(bytes.ToArray()), response.Headers.TryGetValues("x-ms-continuationtoken", out var values) ? values.Single() : null);
    }
    async Task<JsonElement[]> Definitions(BrowserSession session, CancellationToken ct)
    {
        var result = new List<JsonElement>(); var seen = new HashSet<string>(); string? next = null;
        do {
            var response = await Send(Api("build/definitions?includeAllProperties=true&$top=100" + (next is null ? "" : "&continuationToken=" + Uri.EscapeDataString(next))), session, null, ct);
            if (Part(response.Body, "value").ValueKind != JsonValueKind.Array) throw new PortalException("ADO inventory schema was incomplete.", 502);
            result.AddRange(response.Body.GetProperty("value").EnumerateArray()); next = response.Next;
            if (result.Count > 500 || next is not null && (!seen.Add(next) || seen.Count >= 10)) throw new PortalException("ADO definition inventory limit reached; no missing pipelines inferred.", 409);
        } while (!string.IsNullOrEmpty(next));
        // Older ADO responses can omit process/repository even with includeAllProperties.
        for (var i = 0; i < result.Count; i++)
            if (Part(result[i], "process").ValueKind != JsonValueKind.Object || Part(result[i], "repository").ValueKind != JsonValueKind.Object)
                result[i] = (await Send(Api($"build/definitions/{result[i].GetProperty("id").GetInt32()}"), session, null, ct)).Body;
        return result.ToArray();
    }
    static bool RepositoryMatches(JsonElement d, PipelineCatalog local)
    {
        var r = Part(d, "repository"); var url = Text(r, "url")?.TrimEnd('/');
        return string.Equals(Text(r, "type"), "GitHub", StringComparison.OrdinalIgnoreCase) &&
            string.Equals(Text(r, "id"), local.Repository, StringComparison.OrdinalIgnoreCase) &&
            (string.Equals(url, "https://github.com/" + local.Repository, StringComparison.OrdinalIgnoreCase) || string.Equals(url, "https://github.com/" + local.Repository + ".git", StringComparison.OrdinalIgnoreCase));
    }
    static RegistrationSeed? Seed(JsonElement d, PipelineCatalog local)
    {
        if (!RepositoryMatches(d, local) || !Guid.TryParse(Text(Part(Part(d, "repository"), "properties"), "connectedServiceId"), out var connection) ||
            !Part(Part(d, "queue"), "id").TryGetInt32Safe(out var queue) || queue <= 0) return null;
        return new(d.GetProperty("id").GetInt32(), Text(d, "name")!, connection.ToString(), queue);
    }
    public static RegistrationRow Classify(PipelineEntry e, PipelineCatalog local, JsonElement[] definitions, IReadOnlyDictionary<string, string> blobs)
    {
        var names = definitions.Where(d => string.Equals(Text(d, "name"), e.Name, StringComparison.OrdinalIgnoreCase)).ToArray();
        var paths = definitions.Where(d => RepositoryMatches(d, local) && Text(Part(d, "process"), "yamlFilename")?.TrimStart('/') == e.Yaml).ToArray();
        if (names.Length > 1 || paths.Length > 1) return new(e.Yaml, e.Name, e.Category, "Conflict", null, "Duplicate name or YAML binding; reconcile in ADO. Nothing will be renamed or deleted.");
        if (names.Length == 1) {
            var d = names[0];
            var match = paths.Length == 1 && paths[0].GetProperty("id").GetInt32() == d.GetProperty("id").GetInt32() &&
                Text(d, "name") == e.Name && Text(d, "path") == "\\" && Text(Part(d, "repository"), "defaultBranch") is "main" or "refs/heads/main";
            return new(e.Yaml, e.Name, e.Category, match ? "Existing" : "Conflict", d.GetProperty("id").GetInt32(), match ? "Existing matching definition preserved. Permissions/checks are not certified." : "Name is bound to another repository, path, folder or branch; left unchanged.");
        }
        if (paths.Length == 1) return new(e.Yaml, e.Name, e.Category, "Conflict", paths[0].GetProperty("id").GetInt32(), "This YAML already has a different ADO name. Review catalog naming; no duplicate created.");
        if (!blobs.TryGetValue(e.Yaml, out var sha) || sha != e.BlobSha) return new(e.Yaml, e.Name, e.Category, "Source not published", null, "Push/merge this exact YAML to GitHub main, or update the local catalog/package.");
        return new(e.Yaml, e.Name, e.Category, "Missing", null, "Ready to register; registration does not run this pipeline.");
    }
    async Task<(string Commit, Dictionary<string, string> Blobs)> Published(PipelineCatalog local, CancellationToken ct)
    {
        var api = "https://api.github.com/repos/" + local.Repository;
        var repo = (await Send(api, null, null, ct)).Body;
        if (repo.GetProperty("private").GetBoolean()) throw new PortalException("This adapter supports public GitHub repositories only.", 409);
        var commit = (await Send(api + "/commits/main", null, null, ct)).Body;
        var sha = Text(commit, "sha")!; var treeSha = Text(Part(Part(commit, "commit"), "tree"), "sha")!;
        if (!Regex.IsMatch(sha ?? "", "^[0-9a-f]{40}$") || !Regex.IsMatch(treeSha ?? "", "^[0-9a-f]{40}$")) throw new PortalException("GitHub commit could not be qualified.", 502);
        var tree = (await Send(api + "/git/trees/" + treeSha, null, null, ct)).Body; // Root entries only, no recursive truncation.
        if (tree.GetProperty("truncated").GetBoolean()) throw new PortalException("GitHub tree is incomplete.", 502);
        return (sha!, tree.GetProperty("tree").EnumerateArray().Where(x => Text(x, "type") == "blob" && Text(x, "mode") == "100644").ToDictionary(x => Text(x, "path")!, x => Text(x, "sha")!));
    }
    public async Task<RegistrationInventory> Inventory(BrowserSession session, CancellationToken ct)
    {
        var local = LocalCatalog(); var definitions = await Definitions(session, ct); var published = await Published(local, ct);
        return new(local.Repository, local.Branch, published.Commit, local.Hash,
            definitions.Select(d => Seed(d, local)).OfType<RegistrationSeed>().ToArray(),
            local.Entries.Select(e => Classify(e, local, definitions, published.Blobs)).ToArray());
    }
    public static object Payload(PipelineEntry e, PipelineCatalog local, RegistrationSeed seed) => new {
        name = e.Name, path = "\\", type = "build", quality = "definition", queueStatus = "enabled", jobAuthorizationScope = "project",
        description = "Registered by Platform Studio. Manual runs only; deployment permissions and approvals are configured separately.",
        process = new { type = 2, yamlFilename = e.Yaml }, queue = new { id = seed.QueueId },
        repository = new { id = local.Repository, name = local.Repository, type = "GitHub", url = "https://github.com/" + local.Repository,
            defaultBranch = "refs/heads/main", clean = "true", properties = new { connectedServiceId = seed.ConnectionId, defaultBranch = "refs/heads/main", apiUrl = "https://api.github.com/repos/" + local.Repository, reportBuildStatus = "false" } },
        triggers = Array.Empty<object>(), variables = new { }, variableGroups = Array.Empty<object>()
    };
    public async Task<object> Review(BrowserSession session, RegistrationSelection selection, CancellationToken ct)
    {
        if (selection.Pipelines is not { Length: > 0 and <= 32 } || selection.Pipelines.Distinct().Count() != selection.Pipelines.Length) throw new PortalException("Select missing pipelines once each.");
        var inventory = await Inventory(session, ct); var local = LocalCatalog();
        var seed = inventory.Sources.SingleOrDefault(s => s.Id == selection.SourcePipelineId) ?? throw new PortalException("Select an existing GitHub-connected pipeline for this repository.", 409);
        if (local.Hash != inventory.CatalogHash || selection.Pipelines.Any(p => inventory.Pipelines.SingleOrDefault(r => r.Yaml == p)?.Status != "Missing")) throw new PortalException("Selection changed or contains existing, unpublished or conflicting pipelines. Refresh inventory.", 409);
        var ticket = Convert.ToHexString(RandomNumberGenerator.GetBytes(24));
        lock (session.RegistrationPending) {
            foreach (var key in session.RegistrationPending.Where(p => p.Value.Expires < DateTimeOffset.UtcNow).Select(p => p.Key).ToArray()) session.RegistrationPending.Remove(key);
            if (session.RegistrationPending.Count >= 4) throw new PortalException("Too many pending registration reviews.", 429);
            session.RegistrationPending[ticket] = new(selection, local.Hash, inventory.Commit, seed, DateTimeOffset.UtcNow.AddMinutes(5));
        }
        return new { ticket, organization = options.Organization, project = options.Project, inventory.Repository, inventory.Commit, seed,
            payloads = local.Entries.Where(e => selection.Pipelines.Contains(e.Yaml)).OrderBy(e => e.Category == "Workload discovery" ? 0 : 1).Select(e => Payload(e, local, seed)),
            warning = "Creates only these ADO definitions. No runs, source edits, secrets, permissions or approval changes. Existing project permissions will apply to new definitions. GitHub main must remain at the reviewed commit until registration starts." };
    }
    public async Task<object> Apply(BrowserSession session, string ticket, CancellationToken ct)
    {
        if (!await ApplyGate.WaitAsync(0, ct)) throw new PortalException("Another registration is active. Refresh inventory when it completes.", 409);
        try {
            RegistrationReview reviewed;
            lock (session.RegistrationPending) {
                if (!session.RegistrationPending.Remove(ticket, out reviewed!) || reviewed.Expires < DateTimeOffset.UtcNow) throw new PortalException("Registration review expired or already consumed. Refresh inventory before retrying.", 409);
            }
            var inventory = await Inventory(session, ct); var local = LocalCatalog();
            if (local.Hash != reviewed.CatalogHash || inventory.CatalogHash != reviewed.CatalogHash || inventory.Commit != reviewed.Commit || !inventory.Sources.Contains(reviewed.Seed))
                throw new PortalException("Source, main commit or source connection/queue changed. Review again.", 409);
            var chosen = reviewed.Selection.Pipelines.Select(p => inventory.Pipelines.Single(r => r.Yaml == p)).ToArray();
            if (chosen.Any(r => r.Status is not ("Missing" or "Existing"))) throw new PortalException("A selected definition now conflicts; no registration performed.", 409);
            var id = Guid.NewGuid().ToString("N"); var folder = Path.Combine(catalog.Root, "artifacts/pipeline-registration", id); Directory.CreateDirectory(folder);
            var results = new List<RegistrationOutcome>();
            async Task Save() => await File.WriteAllTextAsync(Path.Combine(folder, "receipt.json"), JsonSerializer.Serialize(new { id, organization = options.Organization, project = options.Project, local.Repository, reviewed.Commit, reviewed.CatalogHash, reviewed.Selection, results, runsQueued = false, sourceWritten = false, permissionsChanged = false }, Json), CancellationToken.None);
            await Save();
            foreach (var entry in local.Entries.Where(e => reviewed.Selection.Pipelines.Contains(e.Yaml)).OrderBy(e => e.Category == "Workload discovery" ? 0 : 1)) {
                // Recheck each name/path immediately before mutation, including other administrators' work.
                var current = Classify(entry, local, await Definitions(session, ct), new Dictionary<string, string> { [entry.Yaml] = entry.BlobSha });
                if (current.Status == "Existing") { results.Add(new(entry.Yaml, entry.Name, "Reused", current.ExistingId, "Created concurrently; matching definition preserved.")); await Save(); continue; }
                if (current.Status != "Missing") { results.Add(new(entry.Yaml, entry.Name, "Blocked", current.ExistingId, current.Detail)); await Save(); break; }
                results.Add(new(entry.Yaml, entry.Name, "Pending", null, "Request outcome not yet confirmed. Reconcile before retrying.")); await Save();
                try {
                    var created = (await Send(Api("build/definitions"), session, Payload(entry, local, reviewed.Seed), ct)).Body;
                    var createdId = created.GetProperty("id").GetInt32();
                    var verified = (await Send(Api($"build/definitions/{createdId}"), session, null, ct)).Body;
                    var classification = Classify(entry, local, [verified], new Dictionary<string, string>());
                    if (classification.Status != "Existing") throw new PortalException("Created definition could not be verified.", 502);
                    results[^1] = new(entry.Yaml, entry.Name, "Created", createdId, "Definition read back successfully. No run queued; resource authorization/checks may still be required."); await Save();
                } catch (Exception e) when (e is PortalException or HttpRequestException or OperationCanceledException or JsonException or InvalidOperationException) {
                    results[^1] = new(entry.Yaml, entry.Name, "Uncertain", null, "Registration/read-back was not confirmed. Stop and refresh ADO inventory; do not blindly retry. No rollback or further creation attempted."); await Save(); break;
                }
            }
            return new { id, status = results.Count == chosen.Length && results.All(r => r.Status is "Created" or "Reused") ? "Completed" : "Incomplete", results, path = $"artifacts/pipeline-registration/{id}/receipt.json", runsQueued = false };
        } finally { ApplyGate.Release(); }
    }
}

static class RegistrationJson
{
    public static bool TryGetInt32Safe(this JsonElement e, out int value) { value = 0; return e.ValueKind == JsonValueKind.Number && e.TryGetInt32(out value); }
}
