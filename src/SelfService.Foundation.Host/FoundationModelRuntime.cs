using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;
using SelfService.Foundation;
using SelfService.Portal;

namespace SelfService.Foundation.Host;

public sealed record FoundationModelCommand(string Goal);
public sealed record FoundationModelInvocation(string Id, string CustomerId, string CellId, string ActorKey,
    string ProjectId, string EnvironmentId, string Goal, string SkillName, string SkillContent, string SkillSha256);
public interface IFoundationModelClient
{
    bool IsMock { get; }
    Task<string> ReviewAsync(FoundationModelInvocation invocation, AgentMcpBridge bridge, CancellationToken cancellationToken);
}
public sealed record FoundationModelReview(string Id, string Advice, string ArtifactId, string AdviceSha256,
    string SkillSha256, bool Mock, IReadOnlyList<object> ToolAudit)
{
    public bool AdviceOnly => true;
    public bool ExecutionAuthorized => false;
    public bool DeploymentVerified => false;
}

// The callback receives only frozen public scope. It must return this actor's separately authorized customer credential.
[JsonConverter(typeof(FoundationModelAuthenticationJsonConverter))]
public sealed class FoundationModelAuthentication : IDisposable
{
    byte[]? cache;
    public FoundationModelAuthentication(string customerId, string cellId, string actorKey, ReadOnlySpan<byte> authentication)
    {
        CustomerId = customerId; CellId = cellId; ActorKey = actorKey;
        if (authentication.Length is <= 0 or > 65536) throw new FoundationStoreException("ModelAuthenticationInvalid", 400);
        cache = authentication.ToArray();
    }
    public string CustomerId { get; }
    public string CellId { get; }
    public string ActorKey { get; }
    internal byte[] Copy() => cache?.ToArray() ?? throw new FoundationStoreException("ModelAuthenticationInvalid", 400);
    public void Dispose() { if (cache is { } bytes) CryptographicOperations.ZeroMemory(bytes); cache = null; }
    public override string ToString() => "[REDACTED CUSTOMER MODEL AUTHENTICATION]";
}
public sealed class FoundationModelAuthenticationJsonConverter : JsonConverter<FoundationModelAuthentication>
{
    public override FoundationModelAuthentication Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new JsonException("Model authentication cannot be deserialized.");
    public override void Write(Utf8JsonWriter writer, FoundationModelAuthentication value, JsonSerializerOptions options) => throw new JsonException("Model authentication cannot be serialized.");
}

public sealed class FoundationModelRuntime(FoundationWorkflowService service, FoundationStore store, FoundationHostOptions options)
{
    readonly SemaphoreSlim active = new(1, 1);
    const string SkillRelativePath = ".agents/skills/platform-mcp-orchestration/SKILL.md";

    public async Task<FoundationModelReview> ReviewAsync(FoundationPrincipal principal, FoundationScope scope, FoundationModelCommand command,
        CancellationToken cancellationToken = default, Func<CancellationToken, Task<FoundationPrincipal>>? refresh = null)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var initial = service.Require(principal, scope, FoundationCapabilities.RequestCreate);
        service.RequireFeature(initial, FoundationFeatures.ModelReview);
        if (principal.Kind != PrincipalKind.User) throw new FoundationAuthorizationException("model_human_required");
        var client = options.ModelClient ?? throw new FoundationStoreException("ModelRuntimeDisabled", 503);
        ValidateText(command?.Goal, 16000, "ModelGoalRejected");
        if (!await active.WaitAsync(0, cancellationToken)) throw new FoundationStoreException("ModelReviewBusy", 409);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            var skill = ReadPinnedSkill(options.RepositoryRoot);
            var invocation = new FoundationModelInvocation(Guid.NewGuid().ToString("D"), initial.CustomerId, initial.CellId,
                initial.ActorKey, initial.ProjectId, initial.EnvironmentId, command!.Goal, "platform-mcp-orchestration", skill.Content, skill.Hash);
            async Task<FoundationPrincipal> Current(CancellationToken ct)
            {
                ct.ThrowIfCancellationRequested(); var current = refresh is null ? principal : await refresh(ct);
                if (current.ActorKey != principal.ActorKey || current.ClientId != principal.ClientId || current.Kind != principal.Kind ||
                    !current.Roles.SetEquals(principal.Roles)) throw new FoundationAuthorizationException("model_identity_changed");
                var context = service.Require(current, scope, FoundationCapabilities.RequestCreate);
                service.RequireFeature(context, FoundationFeatures.ModelReview);
                if (context.RoleVersion != initial.RoleVersion) throw new FoundationAuthorizationException("model_grant_changed");
                return current;
            }
            _ = await Current(timeout.Token);
            await using var bridge = await FoundationAgentChannel.CreateBridge(service, principal, scope, Current, timeout.Token);
            string advice;
            try { advice = await client.ReviewAsync(invocation, bridge, timeout.Token).WaitAsync(timeout.Token); }
            catch (OperationCanceledException) { throw new FoundationStoreException("ModelReviewInterrupted", 408); }
            catch (Exception) { throw new FoundationStoreException("ModelRuntimeFailed", 502); }
            var final = await Current(timeout.Token);
            if (!bridge.EvidenceRead) throw new FoundationStoreException("ModelEvidenceNotRead", 502);
            ValidateText(advice, 32768, "ModelAdviceRejected");
            if (Regex.IsMatch(advice, "(?i)\"(?:deploymentVerified|executionAuthorized|identityGrantAuthorized|approved|changesApplied)\"\\s*:\\s*true") ||
                Regex.IsMatch(advice, "(?i)<\\s*(?:script|iframe|object|embed)\\b|javascript:|data:text/html"))
                throw new FoundationStoreException("ModelAdviceRejected", 502);
            var artifact = store.StoreArtifact(initial.CustomerId, final.ActorKey, initial.ProjectId, initial.EnvironmentId,
                "model-review-" + invocation.Id + ".md", Encoding.UTF8.GetBytes(advice));
            return new(invocation.Id, advice, artifact.Id, artifact.Sha256, skill.Hash, client.IsMock, bridge.Audit);
        }
        finally { active.Release(); }
    }

    static void ValidateText(string? value, int maximumBytes, string code)
    {
        if (string.IsNullOrWhiteSpace(value) || Encoding.UTF8.GetByteCount(value) > maximumBytes || value.Any(c => char.IsControl(c) && c is not ('\n' or '\r' or '\t')))
            throw new FoundationStoreException(code, 400);
        try { _ = FoundationStore.CanonicalJson(JsonSerializer.Serialize(new { content = value })); }
        catch (FoundationStoreException) { throw new FoundationStoreException(code, 400); }
    }
    static (string Content, string Hash) ReadPinnedSkill(string? repositoryRoot)
    {
        if (string.IsNullOrWhiteSpace(repositoryRoot) || !Path.IsPathFullyQualified(repositoryRoot)) throw new FoundationStoreException("ModelSkillUnavailable", 503);
        var root = Path.GetFullPath(repositoryRoot); var path = Path.Combine(root, SkillRelativePath.Replace('/', Path.DirectorySeparatorChar));
        var manifest = Path.Combine(root, "MANIFEST.sha256"); ModelPaths.RejectLinks(path); ModelPaths.RejectLinks(manifest);
        if (!File.Exists(path) || !File.Exists(manifest) || new FileInfo(path).Length is <= 0 or > 131072 || new FileInfo(manifest).Length > 1048576)
            throw new FoundationStoreException("ModelSkillUnavailable", 503);
        var content = File.ReadAllText(path).Replace("\r\n", "\n", StringComparison.Ordinal);
        var hash = ModelPaths.Hash(content);
        var pins = File.ReadLines(manifest).Where(line => line.EndsWith("  " + SkillRelativePath, StringComparison.Ordinal)).ToArray();
        if (pins.Length != 1 || pins[0] != hash + "  " + SkillRelativePath) throw new FoundationStoreException("ModelSkillChanged", 409);
        return (content, hash);
    }
}

public sealed class CodexFoundationModelClient : IFoundationModelClient
{
    readonly string baseRoot, customerId, cellId, executable;
    readonly Func<FoundationModelInvocation, CancellationToken, Task<FoundationModelAuthentication?>>? authentication;
    public bool IsMock => false;
    public CodexFoundationModelClient(string runtimeBaseRoot, string customerId, string cellId, string codexExecutable,
        Func<FoundationModelInvocation, CancellationToken, Task<FoundationModelAuthentication?>>? authentication = null)
    {
        if (!Path.IsPathFullyQualified(runtimeBaseRoot) || !CustomerContextResolver.ValidId(customerId) || !CustomerContextResolver.ValidId(cellId) ||
            !Path.IsPathFullyQualified(codexExecutable) || !Path.GetFileName(codexExecutable).Equals("codex.exe", StringComparison.OrdinalIgnoreCase))
            throw new FoundationStoreException("ModelRuntimeConfigurationInvalid", 400);
        baseRoot = Path.GetFullPath(runtimeBaseRoot); ModelPaths.RejectLinks(baseRoot);
        this.customerId = customerId; this.cellId = cellId; executable = codexExecutable; this.authentication = authentication;
    }

    // Trusted configuration helper; request/model JSON cannot supply PortalOptions or a runtime root.
    public PortalOptions IsolatedOptions(FoundationModelInvocation invocation)
    {
        var actor = invocation.ActorKey ?? "";
        if (invocation.CustomerId != customerId || invocation.CellId != cellId || !Guid.TryParseExact(invocation.Id, "D", out var id) || id == Guid.Empty ||
            !Regex.IsMatch(actor, "\\Auser:[a-f0-9-]{36}:[a-f0-9-]{36}\\z") ||
            !Guid.TryParseExact(actor.Split(':')[1], "D", out var tenant) || tenant == Guid.Empty ||
            !Guid.TryParseExact(actor.Split(':')[2], "D", out var subject) || subject == Guid.Empty ||
            !CustomerContextResolver.ValidId(invocation.ProjectId) || !CustomerContextResolver.ValidId(invocation.EnvironmentId) ||
            string.IsNullOrWhiteSpace(invocation.Goal) || Encoding.UTF8.GetByteCount(invocation.Goal) > 16000 ||
            invocation.SkillContent is null || invocation.SkillName != "platform-mcp-orchestration" || ModelPaths.Hash(invocation.SkillContent) != invocation.SkillSha256)
            throw new FoundationAuthorizationException("model_runtime_binding");
        var cellRoot = Path.Combine(baseRoot, "cell-" + ModelPaths.Hash(customerId + "\0" + cellId)[..24]);
        BindRoot(cellRoot, null);
        var actorRoot = Path.Combine(cellRoot, "actor-" + ModelPaths.Hash(actor)[..24]); BindRoot(actorRoot, actor);
        return new PortalOptions { RepositoryRoot = actorRoot, AgentRuntimeRoot = actorRoot, CodexPath = executable, Organization = "customer", Project = "foundation" };
    }
    public async Task<string> ReviewAsync(FoundationModelInvocation invocation, AgentMcpBridge bridge, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (authentication is null) throw new FoundationStoreException("ModelAuthenticationNotConfigured", 503);
        var options = IsolatedOptions(invocation);
        FoundationModelAuthentication? supplied;
        try { supplied = await authentication(invocation, cancellationToken); }
        catch (OperationCanceledException) { throw; }
        catch (Exception) { throw new FoundationStoreException("ModelAuthenticationUnavailable", 503); }
        using var authorization = supplied ?? throw new FoundationStoreException("ModelAuthenticationUnavailable", 503);
        if (authorization.CustomerId != customerId || authorization.CellId != cellId || authorization.ActorKey != invocation.ActorKey)
            throw new FoundationAuthorizationException("model_authentication_binding");
        var cache = authorization.Copy();
        try
        {
            CodexAgentRuntime.ValidateRestoredAuthentication(cache);
            await using var runtime = await CodexAgentRuntime.Start(options, cancellationToken, cache);
            var path = Path.Combine(runtime.Home, "SKILL.md"); await File.WriteAllTextAsync(path, invocation.SkillContent, new UTF8Encoding(false), cancellationToken);
            await File.WriteAllTextAsync(Path.Combine(runtime.Home, "foundation-context.json"), JsonSerializer.Serialize(new
            { invocation.Id, invocation.CustomerId, invocation.CellId, invocation.ActorKey, invocation.ProjectId, invocation.EnvironmentId, invocation.SkillSha256,
                executionAuthorized = false, externalDispatch = false }, FoundationApplication.Json), cancellationToken);
            runtime.ToolHandler = bridge.Call;
            return await runtime.Review(path, invocation.SkillName, bridge.Tools, cancellationToken, foundationInstructions: true, foundationGoal: invocation.Goal);
        }
        finally { CryptographicOperations.ZeroMemory(cache); }
    }
    sealed record RuntimeBinding(int Schema, string CustomerId, string CellId, string? ActorKey);
    void BindRoot(string path, string? actor)
    {
        ModelPaths.RejectLinks(path); var existed = Directory.Exists(path); Directory.CreateDirectory(path); ModelPaths.RejectLinks(path);
        var marker = Path.Combine(path, "foundation-binding.json"); ModelPaths.RejectLinks(marker);
        if (!File.Exists(marker))
        {
            if (existed && Directory.EnumerateFileSystemEntries(path).Any()) throw new FoundationAuthorizationException("model_runtime_unowned_root");
            try
            {
                using var file = new FileStream(marker, FileMode.CreateNew, FileAccess.Write, FileShare.None);
                JsonSerializer.Serialize(file, new RuntimeBinding(1, customerId, cellId, actor), FoundationApplication.Json); file.Flush(flushToDisk: true);
            }
            catch (IOException) when (File.Exists(marker)) { }
        }
        if (new FileInfo(marker).Length > 2048) throw new FoundationAuthorizationException("model_runtime_binding");
        using var document = JsonDocument.Parse(File.ReadAllText(marker), new JsonDocumentOptions { MaxDepth = 16 });
        FoundationApplication.Unique(document.RootElement);
        var stored = document.RootElement.Deserialize<RuntimeBinding>(FoundationApplication.Json);
        if (stored is null || stored.Schema != 1 || stored.CustomerId != customerId || stored.CellId != cellId || stored.ActorKey != actor)
            throw new FoundationAuthorizationException("model_runtime_binding");
    }
}

internal static class ModelPaths
{
    internal static string Hash(string value) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(value))).ToLowerInvariant();
    internal static void RejectLinks(string path)
    {
        for (var current = Path.GetFullPath(path); current is not null; current = Path.GetDirectoryName(current))
            if (Path.Exists(current) && (File.GetAttributes(current) & FileAttributes.ReparsePoint) != 0)
                throw new FoundationAuthorizationException("model_runtime_linked_path");
    }
}
