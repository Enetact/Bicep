using System.IO.Compression;
using System.Text.Json;

namespace SelfService.Portal;

// Read-only projection: never return template parameters, property values or signed download URLs.
public static class PreviewDiagram
{
    public static async Task<byte[]> Bounded(Stream stream, int limit, CancellationToken ct)
    {
        using var bytes = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer, ct)) > 0)
        {
            if (bytes.Length + read > limit) throw new PortalException("Preview artifact exceeds the local size limit.", 413);
            bytes.Write(buffer, 0, read);
        }
        return bytes.ToArray();
    }
    public static Uri DownloadUri(string? url)
    {
        if (!Uri.TryCreate(url, UriKind.Absolute, out var uri) || uri.Scheme != "https" || uri.Port != 443 ||
            uri.UserInfo.Length != 0 || uri.Fragment.Length != 0 ||
            !(uri.Host.EndsWith(".vsblob.vsassets.io", StringComparison.OrdinalIgnoreCase) || uri.Host.EndsWith(".blob.core.windows.net", StringComparison.OrdinalIgnoreCase)))
            throw new PortalException("ADO returned an unsupported artifact download host. Open the report in ADO.", 502);
        return uri;
    }
    public static async Task<object> Read(byte[] bytes, Product product, int runId, string commit, CancellationToken ct)
    {
        try
        {
            using var zip = new ZipArchive(new MemoryStream(bytes), ZipArchiveMode.Read);
            if (zip.Entries.Count > 1000) throw new PortalException("Preview archive has too many entries.", 413);
            async Task<JsonElement> ReadFile(string path)
            {
                // Permit the optional artifact root folder, but never arbitrary suffix matches or extraction.
                var entries = zip.Entries.Where(e => e.FullName == path || e.FullName == "deployment-preview/" + path).ToArray();
                if (entries.Length != 1) throw new PortalException($"Preview is incomplete: expected one {path}. Open its README in ADO.", 409);
                if (entries[0].Length > 4 * 1024 * 1024) throw new PortalException("Preview file exceeds 4 MiB.", 413);
                using var stream = entries[0].Open();
                return JsonSerializer.Deserialize<JsonElement>(await Bounded(stream, 4 * 1024 * 1024, ct));
            }
            var target = await ReadFile("target.json"); var receipt = await ReadFile("preview-inputs.json");
            var status = await ReadFile("status.json"); var raw = await ReadFile("azure/stack-what-if.json");
            var parameters = await ReadFile("effective.parameters.json");
            string Text(JsonElement e, string key) => e.GetProperty(key).GetString() ?? "";
            var environment = Text(target, "environmentName");
            var t = product.Targets.SingleOrDefault(t => t.Environment == environment && t.SubscriptionId.Equals(Text(target, "subscriptionId"), StringComparison.OrdinalIgnoreCase));
            if (t is null || Text(target, "workload") != product.Id || receipt.GetProperty("schemaVersion").GetInt32() != 1 ||
                Text(receipt, "kind") != "workload-preview-inputs" || receipt.GetProperty("runId").ToString() != runId.ToString(System.Globalization.CultureInfo.InvariantCulture) || Text(receipt, "sourceCommit") != commit)
                throw new PortalException("Preview target or provenance differs from this workload/run.", 409);
            var changes = raw.GetProperty("properties").GetProperty("changes").GetProperty("resourceChanges");
            if (changes.ValueKind != JsonValueKind.Array || changes.GetArrayLength() > 10000) throw new PortalException("Preview changes are invalid or exceed 10,000 resources.", 413);
            var rows = changes.EnumerateArray().Select(c =>
            {
                var id = Text(c, "id"); var action = Text(c, "changeType");
                if (!id.StartsWith($"/subscriptions/{t.SubscriptionId}/", StringComparison.OrdinalIgnoreCase)) throw new PortalException("Preview contains an out-of-scope resource.", 409);
                var known = new[] { "Create", "Delete", "Modify", "NoChange", "Ignore", "Deploy", "Unsupported", "Detach" };
                return new { id, action = known.FirstOrDefault(a => a.Equals(action, StringComparison.OrdinalIgnoreCase)) ?? "Unknown",
                    certainty = c.TryGetProperty("changeCertainty", out var certainty) ? certainty.GetString() : "unknown" };
            }).ToArray();
            var succeeded = Text(status, "status") == "Preview succeeded; no workload deployment performed" &&
                Text(raw.GetProperty("properties"), "provisioningState") == "Succeeded";
            return new { kind = "portal-preview-diagram", product = product.Id, environment, subscriptionId = t.SubscriptionId,
                runId, region = Text(parameters.GetProperty("parameters").GetProperty("location"), "value"), sourceCommit = commit, createdUtc = Text(receipt, "createdUtc"), status = succeeded ? "Preview succeeded" : "Blocked or incomplete preview",
                previewSucceeded = succeeded, deploymentAuthorized = false, resources = rows,
                coverage = "Saved Azure What-If resource actions only. Missing resources are unknown, not unchanged. Property details remain in ADO. This view does not authorize deployment or prove connectivity." };
        }
        catch (Exception e) when (e is InvalidDataException or JsonException or KeyNotFoundException or InvalidOperationException)
        { throw new PortalException("Preview artifact format is invalid or incomplete. Open the report in ADO.", 409); }
    }
}
