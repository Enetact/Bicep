using System.Security.Cryptography;
using System.Globalization;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Foundation;

public sealed record PipelineConnection(string CustomerId, string CellId, string Id, string Provider,
    string Organization, string? Project, string Repository, string PipelineOrWorkflowId,
    string[] AllowedRefs, string[] AllowedEnvironments, string[] AllowedInputKeys,
    string CredentialReference, long Generation)
{
    // This is an admitted server-side workflow contract, not a claim inferred from a dispatch response.
    // GitHub workflows must verify studio_source_revision before any privileged step.
    public bool EnforcesSourceRevision { get; init; }
    public int? PipelineVersion { get; init; }
}

public sealed record PipelineDispatchRequest(string CustomerId, string CellId, string ConnectionId,
    long ConnectionGeneration, string RequestId, string Ref, string Environment, string SourceRevision,
    Dictionary<string, string> Inputs);

public sealed record PipelineApproval(string CustomerId, string CellId, string ConnectionId,
    long ConnectionGeneration, string ConnectionDigest, string RequestDigest, string PlanDigest,
    DateTimeOffset ExpiresUtc);

public sealed record PipelineBinding(string CustomerId, string CellId, string ConnectionId,
    long ConnectionGeneration, string RequestId, string PlanDigest, string SourceRevision,
    string CredentialReference);

public sealed record PipelineWireRequest(string Method, Uri Uri, IReadOnlyDictionary<string, string> Headers,
    string BodyJson, PipelineBinding Binding);

public sealed record PipelineWireResponse(int StatusCode, string Body);

public sealed record PipelineDispatchResult(string State, string? ExternalRunId, string? Url,
    int? StatusCode, string ReasonCode, bool Mock = true);

public sealed class PipelineContractException(string reasonCode) : Exception(reasonCode)
{
    public string ReasonCode { get; } = reasonCode;
}

public static class PipelineRequestFactory
{
    public const string GitHub = "github-actions";
    public const string AzureDevOps = "azure-devops";
    public const string GitHubApiVersion = "2026-03-10";
    public static IReadOnlyList<string> ReservedInputs { get; } = Array.AsReadOnly(new[]
        { "studio_request_id", "studio_plan_digest", "studio_source_revision", "studio_environment" });
    static readonly Regex Atom = new("\\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\\z", RegexOptions.CultureInvariant);
    static readonly Regex Digest = new("\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant);
    static readonly Regex Revision = new("\\A(?:[a-f0-9]{40}|[a-f0-9]{64})\\z", RegexOptions.CultureInvariant);
    static readonly Regex Secret = new("(?i)(password|secret|token|authorization|connection.?string|api.?key|account.?key|private.?key|(^|_)pat($|_))", RegexOptions.CultureInvariant);
    static readonly Regex SecretValue = new("(?i)(bearer\\s+|gh[pousr]_[A-Za-z0-9]|github_pat_|AccountKey=|SharedAccessSignature=|-----BEGIN .*PRIVATE KEY|eyJ[A-Za-z0-9_-]{8,}\\.[A-Za-z0-9_-]+\\.)", RegexOptions.CultureInvariant);
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    public static string ConnectionDigest(PipelineConnection connection)
    {
        connection = Snapshot(connection);
        ValidateConnection(connection);
        return Hash(JsonSerializer.Serialize(new
        {
            connection.CustomerId, connection.CellId, connection.Id, connection.Provider,
            connection.Organization, connection.Project, connection.Repository, connection.PipelineOrWorkflowId,
            allowedRefs = connection.AllowedRefs.Order(StringComparer.Ordinal).ToArray(),
            allowedEnvironments = connection.AllowedEnvironments.Order(StringComparer.Ordinal).ToArray(),
            allowedInputKeys = connection.AllowedInputKeys.Order(StringComparer.Ordinal).ToArray(),
            connection.CredentialReference, connection.Generation, connection.EnforcesSourceRevision, connection.PipelineVersion
        }, Json));
    }

    public static string RequestDigest(PipelineConnection connection, PipelineDispatchRequest request)
    {
        connection = Snapshot(connection); request = Snapshot(request);
        ValidateRequest(connection, request);
        return Hash(JsonSerializer.Serialize(new
        {
            connectionDigest = ConnectionDigest(connection), request.CustomerId, request.CellId,
            request.ConnectionId, request.ConnectionGeneration, request.RequestId, request.Ref,
            request.Environment, request.SourceRevision,
            inputs = new SortedDictionary<string, string>(request.Inputs, StringComparer.Ordinal)
        }, Json));
    }

    public static PipelineWireRequest Create(PipelineConnection connection, PipelineDispatchRequest request,
        PipelineApproval approval, DateTimeOffset now)
    {
        connection = Snapshot(connection); request = Snapshot(request);
        var connectionHash = ConnectionDigest(connection);
        var requestHash = RequestDigest(connection, request);
        var planDigest = approval?.PlanDigest ?? "";
        if (approval is null || approval.CustomerId != connection.CustomerId || approval.CellId != connection.CellId ||
            approval.ConnectionId != connection.Id || approval.ConnectionGeneration != connection.Generation ||
            approval.ConnectionDigest != connectionHash || approval.RequestDigest != requestHash ||
            !Digest.IsMatch(planDigest) || approval.ExpiresUtc <= now || approval.ExpiresUtc > now.AddDays(1))
            throw Invalid("pipeline-approval-binding-invalid");
        var inputs = new SortedDictionary<string, string>(request.Inputs, StringComparer.Ordinal)
        {
            [ReservedInputs[0]] = request.RequestId,
            [ReservedInputs[1]] = planDigest,
            [ReservedInputs[2]] = request.SourceRevision,
            [ReservedInputs[3]] = request.Environment
        };
        Uri uri; string body;
        var headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
        if (connection.Provider == GitHub)
        {
            var reference = request.Ref.StartsWith("refs/heads/", StringComparison.Ordinal) ? request.Ref[11..] :
                request.Ref.StartsWith("refs/tags/", StringComparison.Ordinal) ? request.Ref[10..] : request.Ref;
            uri = new($"https://api.github.com/repos/{Segment(connection.Organization)}/{Segment(connection.Repository)}/actions/workflows/{Segment(connection.PipelineOrWorkflowId)}/dispatches");
            body = JsonSerializer.Serialize(new { @ref = reference, inputs }, Json);
            headers["Accept"] = "application/vnd.github+json";
            headers["X-GitHub-Api-Version"] = GitHubApiVersion;
            headers["User-Agent"] = "PlatformStudio-Foundation/1.0";
        }
        else
        {
            var reference = request.Ref.StartsWith("refs/", StringComparison.Ordinal) ? request.Ref : "refs/heads/" + request.Ref;
            var version = connection.PipelineVersion is { } pinned ? $"pipelineVersion={pinned}&" : "";
            uri = new($"https://dev.azure.com/{Segment(connection.Organization)}/{Segment(connection.Project!)}/_apis/pipelines/{connection.PipelineOrWorkflowId}/runs?{version}api-version=7.1");
            body = JsonSerializer.Serialize(new
            {
                resources = new { repositories = new { self = new { refName = reference, version = request.SourceRevision } } },
                templateParameters = inputs
            }, Json);
            headers["Accept"] = "application/json";
        }
        return new("POST", uri, headers, body,
            new(connection.CustomerId, connection.CellId, connection.Id, connection.Generation,
                request.RequestId, planDigest, request.SourceRevision, connection.CredentialReference));
    }

    public static void ValidateConnection(PipelineConnection connection)
    {
        if (connection is null || !GuidId(connection.CustomerId) || !GuidId(connection.CellId) ||
            !SafeAtom(connection.Id) || connection.Generation <= 0 || connection.Provider is not (GitHub or AzureDevOps) ||
            !SafeAtom(connection.Organization) || !SafeAtom(connection.Repository) ||
            string.IsNullOrWhiteSpace(connection.CredentialReference) || connection.CredentialReference.Length > 240 ||
            connection.CredentialReference.Any(c => !(char.IsAsciiLetterOrDigit(c) || ":/._-".Contains(c))) ||
            connection.CredentialReference.Contains("..", StringComparison.Ordinal) || SecretValue.IsMatch(connection.CredentialReference))
            throw Invalid("pipeline-connection-invalid");
        ValidateList(connection.AllowedRefs, 100, ValidRef);
        ValidateList(connection.AllowedEnvironments, 100, SafeAtom);
        ValidateList(connection.AllowedInputKeys, 100, SafeAtom);
        if (ReservedInputs.Any(k => !connection.AllowedInputKeys.Contains(k, StringComparer.Ordinal)))
            throw Invalid("pipeline-correlation-contract-required");
        if (connection.AllowedInputKeys.Distinct(StringComparer.OrdinalIgnoreCase).Count() != connection.AllowedInputKeys.Length ||
            connection.AllowedInputKeys.Any(k => !ReservedInputs.Contains(k, StringComparer.Ordinal) && Secret.IsMatch(k)))
            throw Invalid("pipeline-secret-input-forbidden");
        if (connection.Provider == GitHub)
        {
            if (connection.Project is not null || connection.PipelineVersion is not null || !connection.EnforcesSourceRevision ||
                !PositiveNumber(connection.PipelineOrWorkflowId, long.MaxValue) &&
                !Regex.IsMatch(connection.PipelineOrWorkflowId ?? "", "\\A[A-Za-z0-9][A-Za-z0-9_.-]{0,119}\\.ya?ml\\z", RegexOptions.CultureInvariant))
                throw Invalid("pipeline-workflow-contract-invalid");
        }
        else if (string.IsNullOrWhiteSpace(connection.Project) || connection.Project.Length > 128 ||
            connection.Project.Any(c => char.IsControl(c) || "/\\?#%".Contains(c)) ||
            !PositiveNumber(connection.PipelineOrWorkflowId, int.MaxValue) ||
            connection.PipelineVersion is <= 0)
            throw Invalid("pipeline-project-contract-invalid");
    }

    static void ValidateRequest(PipelineConnection connection, PipelineDispatchRequest request)
    {
        ValidateConnection(connection);
        if (request is null || request.CustomerId != connection.CustomerId || request.CellId != connection.CellId ||
            request.ConnectionId != connection.Id || request.ConnectionGeneration != connection.Generation ||
            !GuidId(request.RequestId) || !connection.AllowedRefs.Contains(request.Ref, StringComparer.Ordinal) ||
            !connection.AllowedEnvironments.Contains(request.Environment, StringComparer.Ordinal) ||
            !Revision.IsMatch(request.SourceRevision ?? "") || request.Inputs is null || request.Inputs.Count > 21)
            throw Invalid("pipeline-request-scope-invalid");
        foreach (var input in request.Inputs)
            if (!SafeAtom(input.Key) || !connection.AllowedInputKeys.Contains(input.Key, StringComparer.Ordinal) ||
                ReservedInputs.Contains(input.Key, StringComparer.OrdinalIgnoreCase) || Secret.IsMatch(input.Key) ||
                input.Value is null || Encoding.UTF8.GetByteCount(input.Value) > 8192 ||
                input.Value.Any(c => c == '\0') || SecretValue.IsMatch(input.Value))
                throw Invalid("pipeline-input-invalid");
    }

    static bool GuidId(string value) => Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty;
    static bool PositiveNumber(string value, long maximum) => value is not null &&
        Regex.IsMatch(value, "\\A[1-9][0-9]*\\z", RegexOptions.CultureInvariant) &&
        long.TryParse(value, NumberStyles.None, CultureInfo.InvariantCulture, out var number) && number <= maximum;
    static bool SafeAtom(string value) => value is not null && Atom.IsMatch(value) && !value.Contains("..", StringComparison.Ordinal);
    static bool ValidRef(string value) => !string.IsNullOrWhiteSpace(value) && value.Length <= 240 &&
        !value.Any(c => char.IsControl(c) || char.IsWhiteSpace(c) || "\\?%#:".Contains(c)) &&
        !value.Contains("..", StringComparison.Ordinal) && !value.Contains("//", StringComparison.Ordinal) &&
        !value.Contains("@{", StringComparison.Ordinal) && !value.EndsWith("/", StringComparison.Ordinal) && !value.EndsWith(".", StringComparison.Ordinal);
    static void ValidateList(string[] values, int maximum, Func<string, bool> valid)
    {
        if (values is null || values.Length is 0 || values.Length > maximum ||
            values.Any(v => !valid(v)) || values.Distinct(StringComparer.Ordinal).Count() != values.Length)
            throw Invalid("pipeline-allowlist-invalid");
    }
    static string Segment(string value) => Uri.EscapeDataString(value);
    static PipelineConnection Snapshot(PipelineConnection value)
        => value is null ? throw Invalid("pipeline-connection-invalid") : value with
        {
            AllowedRefs = value.AllowedRefs?.ToArray() ?? [],
            AllowedEnvironments = value.AllowedEnvironments?.ToArray() ?? [],
            AllowedInputKeys = value.AllowedInputKeys?.ToArray() ?? []
        };
    static PipelineDispatchRequest Snapshot(PipelineDispatchRequest value)
    {
        if (value is null || value.Inputs is null) throw Invalid("pipeline-request-scope-invalid");
        try { return value with { Inputs = new Dictionary<string, string>(value.Inputs, StringComparer.Ordinal) }; }
        catch (Exception e) when (e is ArgumentException or InvalidOperationException)
        { throw Invalid("pipeline-input-invalid"); }
    }
    static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    static PipelineContractException Invalid(string reason) => new(reason);
}
