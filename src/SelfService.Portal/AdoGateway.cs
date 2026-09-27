using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;

namespace SelfService.Portal;

public sealed class AdoGateway(HttpClient http, ITokenProvider identity, PortalOptions options, Catalog catalog)
{
    public async Task<JsonElement> Send(BrowserSession session, string audience, string url, object? body = null)
    {
        using var request = new HttpRequestMessage(body is null ? HttpMethod.Get : HttpMethod.Post, url);
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", await identity.Token(session, audience));
        if (body is not null) request.Content = JsonContent.Create(body);
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead);
        if (!response.IsSuccessStatusCode) throw new PortalException($"{audience} returned HTTP {(int)response.StatusCode}. Check access, consent and pipeline configuration.", 502);
        // Upstream bodies can contain secrets and diagnostics: never echo them on errors.
        if (response.Content.Headers.ContentLength > 4 * 1024 * 1024) throw new PortalException("Service response exceeds the local limit.", 502);
        using var stream = await response.Content.ReadAsStreamAsync();
        using var bounded = new MemoryStream(); var buffer = new byte[8192]; int read;
        while ((read = await stream.ReadAsync(buffer)) > 0) { if (bounded.Length + read > 4 * 1024 * 1024) throw new PortalException("Service response exceeds the local limit.", 502); bounded.Write(buffer, 0, read); }
        return JsonSerializer.Deserialize<JsonElement>(bounded.ToArray());
    }
    string Api(string path) => $"{options.AdoBase}/_apis/{path}{(path.Contains('?') ? '&' : '?')}api-version=7.1";
    public async Task<JsonElement> Definition(BrowserSession s, Product p, bool discover)
    {
        var name = discover ? p.DiscoverName : p.DeployName;
        var list = await Send(s, "ado", Api($"build/definitions?name={Uri.EscapeDataString(name)}&$top=100"));
        var matches = list.GetProperty("value").EnumerateArray().Where(d => d.GetProperty("name").GetString() == name).ToArray();
        if (matches.Length != 1) throw new PortalException($"Register exactly one '{name}' pipeline in the configured project.", 409);
        var definition = await Send(s, "ado", Api($"build/definitions/{matches[0].GetProperty("id").GetInt32()}"));
        var expected = discover ? p.DiscoverYaml : p.DeployYaml;
        if (!definition.TryGetProperty("process", out var process) || !process.TryGetProperty("yamlFilename", out var path) || path.GetString()?.TrimStart('/') != expected)
            throw new PortalException($"Pipeline '{name}' must use {expected}.", 409);
        return definition;
    }
    public async Task<object[]> DiscoveryRuns(BrowserSession s, string product)
    {
        var p = catalog.Products.SingleOrDefault(p => p.Id == product) ?? throw new PortalException("Unknown workload.");
        var d = await Definition(s, p, true);
        var list = await Send(s, "ado", Api($"build/builds?definitions={d.GetProperty("id").GetInt32()}&branchName=refs%2Fheads%2Fmain&statusFilter=completed&resultFilter=succeeded&queryOrder=finishTimeDescending&$top=50"));
        return list.GetProperty("value").EnumerateArray().Where(b => b.TryGetProperty("finishTime", out var finish) && finish.GetDateTimeOffset() >= DateTimeOffset.UtcNow.AddDays(-7))
            .Select(b => (object)new { id = b.GetProperty("id").GetInt32(), name = b.GetProperty("buildNumber").GetString(), finished = b.GetProperty("finishTime").GetString() }).ToArray();
    }
    public async Task<int> ValidateHandoff(BrowserSession s, RunRequest request)
    {
        var (p, _) = catalog.Validate(request);
        var d = await Definition(s, p, request.Operation == "discover");
        if (request.Operation != "discover")
        {
            var discovery = await Definition(s, p, true);
            var b = await Send(s, "ado", Api($"build/builds/{request.DiscoveryRunId}"));
            var repo = d.GetProperty("repository").GetProperty("id").GetString();
            if (b.GetProperty("definition").GetProperty("id").GetInt32() != discovery.GetProperty("id").GetInt32() ||
                b.GetProperty("repository").GetProperty("id").GetString() != repo ||
                b.GetProperty("sourceBranch").GetString() != "refs/heads/main" || b.GetProperty("status").GetString() != "completed" ||
                b.GetProperty("result").GetString() != "succeeded" || !b.TryGetProperty("finishTime", out var finish) ||
                finish.GetDateTimeOffset() < DateTimeOffset.UtcNow.AddDays(-7) || finish.GetDateTimeOffset() > DateTimeOffset.UtcNow.AddMinutes(5))
                throw new PortalException("Discovery must be successful, from matching repository/main and at most seven days old.", 409);
            var run = await Send(s, "ado", Api($"pipelines/{discovery.GetProperty("id").GetInt32()}/runs/{request.DiscoveryRunId}"));
            if (!run.TryGetProperty("templateParameters", out var parms) ||
                !parms.TryGetProperty("workload", out var workload) || workload.GetString() != request.Product ||
                !parms.TryGetProperty("environment", out var env) || env.GetString() != request.Environment)
                throw new PortalException("Discovery selection is missing or differs from the selected workload/environment.", 409);
        }
        return d.GetProperty("id").GetInt32();
    }
    public async Task<object> Queue(BrowserSession s, RunRequest r)
    {
        var id = await ValidateHandoff(s, r);
        // No retries: a lost response may still mean ADO accepted the run.
        var result = await Send(s, "ado", Api($"pipelines/{id}/runs"), catalog.Payload(r));
        var runId = result.GetProperty("id").GetInt32();
        return new { id = runId, pipelineId = id, url = $"{options.AdoBase}/_build/results?buildId={runId}" };
    }
    public async Task<object> Status(BrowserSession s, string product, int runId)
    {
        var p = catalog.Products.SingleOrDefault(p => p.Id == product) ?? throw new PortalException("Unknown workload.");
        var discover = await Definition(s, p, true); var deploy = await Definition(s, p, false);
        var b = await Send(s, "ado", Api($"build/builds/{runId}")); var id = b.GetProperty("definition").GetProperty("id").GetInt32();
        if (id != discover.GetProperty("id").GetInt32() && id != deploy.GetProperty("id").GetInt32()) throw new PortalException("Run is outside this workload.", 403);
        return new { id = runId, state = b.GetProperty("status").GetString(), result = b.TryGetProperty("result", out var result) ? result.GetString() : null,
            url = $"{options.AdoBase}/_build/results?buildId={runId}" };
    }
    public async Task<object[]> Subscriptions(BrowserSession s)
    {
        var result = await Send(s, "azure", "https://management.azure.com/subscriptions?api-version=2022-12-01");
        var allowed = catalog.Products.SelectMany(p => p.Targets).Select(t => t.SubscriptionId).ToHashSet(StringComparer.OrdinalIgnoreCase);
        return result.GetProperty("value").EnumerateArray().Where(v => allowed.Contains(v.GetProperty("subscriptionId").GetString()!))
            .Select(v => (object)new { id = v.GetProperty("subscriptionId").GetString(), name = v.GetProperty("displayName").GetString(), state = v.GetProperty("state").GetString() }).ToArray();
    }
}
