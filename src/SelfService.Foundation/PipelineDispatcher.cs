using System.Globalization;
using System.Text.Json;

namespace SelfService.Foundation;

public interface IPipelineTransport
{
    Task<PipelineWireResponse> SendAsync(PipelineWireRequest request, CancellationToken cancellationToken);
}

public sealed class PipelineTransportDisabledException() : Exception("pipeline-external-dispatch-disabled");

public sealed class DeniedPipelineTransport : IPipelineTransport
{
    public Task<PipelineWireResponse> SendAsync(PipelineWireRequest request, CancellationToken cancellationToken)
        => throw new PipelineTransportDisabledException();
}

// No transport with external network capability is supplied or enabled by this foundation.
// Host authorization and durable exact-plan approval must happen before calling this adapter.
public sealed class PipelineDispatcher(IPipelineTransport? transport = null, TimeProvider? clock = null)
{
    readonly IPipelineTransport transport = transport ?? new DeniedPipelineTransport();
    readonly TimeProvider clock = clock ?? TimeProvider.System;
    public async Task<PipelineDispatchResult> DispatchAsync(PipelineConnection connection, PipelineDispatchRequest request,
        PipelineApproval approval, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested) return Result("Rejected", null, null, null, "pipeline-cancelled-before-dispatch");
        PipelineWireRequest wire;
        try { wire = PipelineRequestFactory.Create(connection, request, approval, clock.GetUtcNow()); }
        catch (PipelineContractException e) { return Result("Rejected", null, null, null, e.ReasonCode); }
        PipelineWireResponse response;
        try { response = await transport.SendAsync(wire, cancellationToken); }
        catch (PipelineTransportDisabledException) { return Result("Rejected", null, null, null, "pipeline-external-dispatch-disabled"); }
        catch (PipelineContractException e) { return Result("Rejected", null, null, null, e.ReasonCode); }
        catch (OperationCanceledException) { return Result("OutcomeUnknown", null, null, null, "pipeline-dispatch-interrupted"); }
        catch (HttpRequestException) { return Result("OutcomeUnknown", null, null, null, "pipeline-transport-uncertain"); }
        catch (IOException) { return Result("OutcomeUnknown", null, null, null, "pipeline-transport-uncertain"); }
        catch (TimeoutException) { return Result("OutcomeUnknown", null, null, null, "pipeline-dispatch-interrupted"); }
        // This adapter never retries a POST. An accepted response is not proof of pipeline success.
        if (response.StatusCode is 202 or 204)
            return Result("OutcomeUnknown", null, null, response.StatusCode, "pipeline-accepted-without-correlated-run");
        if (response.StatusCode != 200)
            return Result(response.StatusCode is >= 400 and < 500 && response.StatusCode != 408 ? "Rejected" : "OutcomeUnknown",
                null, null, response.StatusCode, "pipeline-http-" + response.StatusCode.ToString(CultureInfo.InvariantCulture));
        try
        {
            if (response.Body is null || System.Text.Encoding.UTF8.GetByteCount(response.Body) > 262144)
                throw new FormatException();
            using var document = JsonDocument.Parse(response.Body, new JsonDocumentOptions { MaxDepth = 16 });
            Unique(document.RootElement);
            if (document.RootElement.ValueKind != JsonValueKind.Object) throw new FormatException();
            return connection.Provider == PipelineRequestFactory.GitHub
                ? GitHub(connection, request, document.RootElement, response.StatusCode)
                : Ado(connection, request, document.RootElement, response.StatusCode);
        }
        catch (Exception e) when (e is JsonException or FormatException or InvalidOperationException or OverflowException)
        {
            return Result("OutcomeUnknown", null, null, response.StatusCode, "pipeline-run-response-unverified");
        }
    }

    PipelineDispatchResult GitHub(PipelineConnection connection, PipelineDispatchRequest request, JsonElement body, int status)
    {
        var id = PositiveId(body, "workflow_run_id");
        var api = $"https://api.github.com/repos/{Escape(connection.Organization)}/{Escape(connection.Repository)}/actions/runs/{id}";
        var html = $"https://github.com/{Escape(connection.Organization)}/{Escape(connection.Repository)}/actions/runs/{id}";
        MatchUri(body, "run_url", api, required: true);
        MatchUri(body, "html_url", html, required: true);
        if (body.TryGetProperty("head_sha", out var sha) && sha.GetString() != request.SourceRevision) throw new FormatException();
        return Result("Accepted", id, html, status, "pipeline-run-accepted-not-verified");
    }

    PipelineDispatchResult Ado(PipelineConnection connection, PipelineDispatchRequest request, JsonElement body, int status)
    {
        var id = PositiveId(body, "id");
        if (!body.TryGetProperty("pipeline", out var pipeline) || PositiveId(pipeline, "id") != connection.PipelineOrWorkflowId)
            throw new FormatException();
        var api = $"https://dev.azure.com/{Escape(connection.Organization)}/{Escape(connection.Project!)}/_apis/pipelines/{connection.PipelineOrWorkflowId}/runs/{id}";
        MatchUri(body, "url", api, required: true);
        if (body.TryGetProperty("resources", out var resources) && resources.TryGetProperty("repositories", out var repos) &&
            repos.TryGetProperty("self", out var self))
        {
            if (self.TryGetProperty("version", out var version) && version.GetString() != request.SourceRevision) throw new FormatException();
            if (self.TryGetProperty("refName", out var reference))
            {
                var expected = request.Ref.StartsWith("refs/", StringComparison.Ordinal) ? request.Ref : "refs/heads/" + request.Ref;
                if (reference.GetString() != expected) throw new FormatException();
            }
            if (self.TryGetProperty("repository", out var repository) && repository.TryGetProperty("id", out var repoId) &&
                !string.Equals(repoId.GetString(), connection.Repository, StringComparison.OrdinalIgnoreCase)) throw new FormatException();
        }
        return Result("Accepted", id,
            $"https://dev.azure.com/{Escape(connection.Organization)}/{Escape(connection.Project!)}/_build/results?buildId={id}",
            status, "pipeline-run-accepted-not-verified");
    }

    static string PositiveId(JsonElement body, string name)
    {
        if (!body.TryGetProperty(name, out var value) || !value.TryGetInt64(out var id) || id <= 0) throw new FormatException();
        return id.ToString(CultureInfo.InvariantCulture);
    }
    static void MatchUri(JsonElement body, string name, string expected, bool required)
    {
        if (!body.TryGetProperty(name, out var value)) { if (required) throw new FormatException(); return; }
        if (value.ValueKind != JsonValueKind.String || !Uri.TryCreate(value.GetString(), UriKind.Absolute, out var actual) ||
            actual.UserInfo.Length != 0 || actual.Fragment.Length != 0 || actual.Query.Length != 0 ||
            !string.Equals(actual.AbsoluteUri, expected, StringComparison.OrdinalIgnoreCase)) throw new FormatException();
    }
    static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject())
            {
                if (!seen.Add(property.Name)) throw new FormatException();
                Unique(property.Value);
            }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var child in value.EnumerateArray()) Unique(child);
    }
    static string Escape(string text) => Uri.EscapeDataString(text);
    PipelineDispatchResult Result(string state, string? id, string? url, int? status, string reason)
        => new(state, id, url, status, reason, Mock: transport is not IPipelineTransportCharacteristics { IsMock: false });
}
