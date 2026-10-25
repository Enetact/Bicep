using System.Net.Http.Headers;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Collections.ObjectModel;

namespace SelfService.Foundation;

public interface IPipelineTransportCharacteristics { bool IsMock { get; } }

public interface ICredentialDispatchPermitSource
{
    // The host rechecks current durable authority before returning a permit. Never expose this operation as a model tool.
    // The final check runs while custody's admission gate is held: do not reenter that same CredentialVault.
    Task<CredentialDispatchPermit?> AuthorizeAsync(PipelineWireRequest wire, CancellationToken cancellationToken);
}

[JsonConverter(typeof(CredentialDispatchPermitJsonConverter))]
public sealed class CredentialDispatchPermit
{
    readonly string wireDigest;
    readonly CredentialScope scope;
    readonly DateTimeOffset expiresUtc;
    int consumed;
    CredentialDispatchPermit(string digest, CredentialScope scope, DateTimeOffset expires) { wireDigest = digest; this.scope = scope; expiresUtc = expires; }

    public static CredentialDispatchPermit FromDurableDispatch(FoundationDispatch dispatch, PipelineWireRequest wire,
        CredentialScope scope, DateTimeOffset now)
    {
        scope = CredentialRules.Snapshot(scope);
        try
        {
            var j = dispatch.Job; var p = dispatch.Plan; var c = dispatch.Connection;
            if (j.State != "Dispatched" || j.Fence <= 0 || j.LeaseUntil is null || j.LeaseUntil <= now ||
                j.CustomerId != scope.CustomerId || j.CellId != scope.CellId || j.ProjectId != scope.ProjectId || j.EnvironmentId != scope.EnvironmentId ||
                j.PlanId != p.Id || p.CustomerId != scope.CustomerId || p.CellId != scope.CellId || p.ProjectId != scope.ProjectId || p.EnvironmentId != scope.EnvironmentId ||
                p.Operation != "pipeline.dispatch" || !p.ChangesExternalState || p.ApproverKey is null || p.ApproverKey == p.RequesterKey ||
                p.RequesterKey.StartsWith("workload:", StringComparison.Ordinal) || p.ApproverKey.StartsWith("workload:", StringComparison.Ordinal) ||
                p.RequesterVersion <= 0 || p.ApproverVersion is null or <= 0 || p.ApprovedAt is null || p.ApprovedAt > now || p.ExpiresAt <= now ||
                c.CustomerId != scope.CustomerId || c.Id != scope.ConnectionId || c.Generation != scope.ConnectionGeneration || !c.Enabled ||
                c.ProjectId != scope.ProjectId || c.EnvironmentId != scope.EnvironmentId || c.Provider != scope.Provider || p.ConnectionId != c.Id || p.ConnectionGeneration != c.Generation)
                throw new CredentialException("credential-dispatch-authority-invalid");
            using var doc = CredentialRules.Document(Encoding.UTF8.GetBytes(c.ConfigurationJson)); var config = doc.RootElement;
            string Text(string name) => config.GetProperty(name).GetString()!;
            string[] List(string name) => config.GetProperty(name).EnumerateArray().Select(e => e.GetString()!).ToArray();
            var connection = new PipelineConnection(scope.CustomerId, scope.CellId, c.Id, c.Provider, Text("organization"),
                config.GetProperty("project").ValueKind == JsonValueKind.Null ? null : Text("project"), Text("repository"), Text("pipelineOrWorkflowId"),
                List("allowedRefs"), List("allowedEnvironments"), List("allowedInputKeys"), Text("authProfileId"), c.Generation)
            {
                EnforcesSourceRevision = config.TryGetProperty("enforcesSourceRevision", out var guard) && guard.GetBoolean(),
                PipelineVersion = config.TryGetProperty("pipelineVersion", out var version) && version.ValueKind != JsonValueKind.Null ? version.GetInt32() : null
            };
            if (connection.CredentialReference != scope.AuthProfileId || PipelineRequestFactory.ConnectionDigest(connection) != scope.ConnectionDigest)
                throw new CredentialException("credential-dispatch-connection-invalid");
            var request = JsonSerializer.Deserialize<PipelineDispatchRequest>(p.PayloadJson, CredentialRules.Json)!;
            if (request.SourceRevision != p.SourceRevision || request.Environment != scope.EnvironmentId)
                throw new CredentialException("credential-dispatch-request-invalid");
            var approval = new PipelineApproval(scope.CustomerId, scope.CellId, c.Id, c.Generation, scope.ConnectionDigest,
                PipelineRequestFactory.RequestDigest(connection, request), p.Digest, p.ExpiresAt);
            var expected = PipelineRequestFactory.Create(connection, request, approval, now);
            if (Digest(expected) != Digest(wire)) throw new CredentialException("credential-dispatch-wire-invalid");
            var expiry = new[] { p.ExpiresAt, j.LeaseUntil.Value, now.AddMinutes(1) }.Min();
            return new(Digest(wire), scope, expiry);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or KeyNotFoundException or NullReferenceException or PipelineContractException)
        { throw new CredentialException("credential-dispatch-authority-invalid"); }
    }

    internal CredentialScope Consume(PipelineWireRequest wire, DateTimeOffset now)
    {
        var result = Inspect(wire, now);
        if (Interlocked.Exchange(ref consumed, 1) != 0)
            throw new CredentialException("credential-dispatch-permit-invalid");
        return result;
    }
    internal CredentialScope Inspect(PipelineWireRequest wire, DateTimeOffset now)
    {
        if (expiresUtc <= now || wireDigest != Digest(wire) || Volatile.Read(ref consumed) != 0)
            throw new CredentialException("credential-dispatch-permit-invalid");
        return CredentialRules.Snapshot(scope);
    }
    internal static string Digest(PipelineWireRequest wire) => CredentialRules.Hash(JsonSerializer.Serialize(new
    { wire.Method, uri = wire.Uri.AbsoluteUri, headers = wire.Headers.OrderBy(h => h.Key, StringComparer.Ordinal).ToArray(), wire.BodyJson, wire.Binding }, CredentialRules.Json));
    public override string ToString() => "[NONTRANSFERABLE DISPATCH PERMIT]";
}

public sealed class CredentialDispatchPermitJsonConverter : JsonConverter<CredentialDispatchPermit>
{
    public override CredentialDispatchPermit Read(ref Utf8JsonReader reader, Type type, JsonSerializerOptions options) => throw new JsonException("Dispatch permits cannot be deserialized.");
    public override void Write(Utf8JsonWriter writer, CredentialDispatchPermit value, JsonSerializerOptions options) => throw new JsonException("Dispatch permits cannot be serialized.");
}

public sealed class PipelineAuthenticatedTransport : IPipelineTransport, IPipelineTransportCharacteristics, IDisposable
{
    readonly CredentialVault vault;
    readonly ICredentialDispatchPermitSource permits;
    readonly HttpClient? http;
    readonly TimeProvider clock;
    readonly bool enabled;
    readonly TimeSpan operationTimeout;
    public bool IsMock { get; }

    public PipelineAuthenticatedTransport(CredentialVault vault, ICredentialDispatchPermitSource permits,
        bool enableExternalDispatch = false, TimeProvider? clock = null, TimeSpan? operationTimeout = null)
    {
        this.vault = vault; this.permits = permits; this.clock = clock ?? TimeProvider.System; enabled = enableExternalDispatch;
        this.operationTimeout = CredentialRules.OperationTimeout(operationTimeout, TimeSpan.FromSeconds(20));
        IsMock = false;
        if (enabled) http = new HttpClient(new SocketsHttpHandler
        { AllowAutoRedirect = false, UseCookies = false, UseProxy = false, PooledConnectionLifetime = TimeSpan.FromMinutes(2) }) { Timeout = Timeout.InfiniteTimeSpan };
    }
    PipelineAuthenticatedTransport(CredentialVault vault, ICredentialDispatchPermitSource permits, HttpMessageHandler mockHandler, TimeProvider? clock, TimeSpan? operationTimeout)
    {
        this.vault = vault; this.permits = permits; this.clock = clock ?? TimeProvider.System; enabled = true; IsMock = true;
        this.operationTimeout = CredentialRules.OperationTimeout(operationTimeout, TimeSpan.FromSeconds(20));
        http = new HttpClient(mockHandler) { Timeout = Timeout.InfiniteTimeSpan };
    }
    // Injected handler is an explicitly trusted test seam. It is never populated from JSON or provider metadata.
    public static PipelineAuthenticatedTransport ForMockHandler(CredentialVault vault, ICredentialDispatchPermitSource permits,
        HttpMessageHandler mockHandler, TimeProvider? clock = null, TimeSpan? operationTimeout = null) => new(vault, permits, mockHandler, clock, operationTimeout);

    public Task<PipelineWireResponse> SendAsync(PipelineWireRequest wire, CancellationToken cancellationToken) =>
        SendCoreAsync(wire, permits, null, cancellationToken);

    internal Task<PipelineWireResponse> SendBoundAsync(PipelineWireRequest wire, FoundationCredentialUseSession session,
        CancellationToken cancellationToken) => SendCoreAsync(wire, session, session, cancellationToken);

    async Task<PipelineWireResponse> SendCoreAsync(PipelineWireRequest wire, ICredentialDispatchPermitSource permitSource,
        FoundationCredentialUseSession? boundSession, CancellationToken cancellationToken)
    {
        if (!enabled || http is null) throw new PipelineTransportDisabledException();
        cancellationToken.ThrowIfCancellationRequested();
        var callerCancellation = cancellationToken;
        using var deadline = CredentialRules.OperationDeadline(operationTimeout, cancellationToken); cancellationToken = deadline.Token;
        var admitted = false;
        try
        {
            if (wire is null || wire.Headers is null) throw new CredentialException("credential-dispatch-wire-invalid");
            wire = wire with { Headers = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(wire.Headers, StringComparer.Ordinal)) };
            ValidateWire(wire);
            var permit = await permitSource.AuthorizeAsync(wire, cancellationToken) ?? throw new CredentialException("credential-dispatch-not-authorized");
            var scope = permit.Inspect(wire, clock.GetUtcNow());
            if (scope.CustomerId != wire.Binding.CustomerId || scope.CellId != wire.Binding.CellId ||
                scope.ConnectionId != wire.Binding.ConnectionId || scope.ConnectionGeneration != wire.Binding.ConnectionGeneration ||
                scope.AuthProfileId != wire.Binding.CredentialReference ||
                scope.Provider == PipelineRequestFactory.GitHub != (wire.Uri.Host == "api.github.com"))
                throw new CredentialException("credential-dispatch-scope-invalid");
            using var lease = await vault.ResolveForDispatchAsync(scope, cancellationToken);
            cancellationToken.ThrowIfCancellationRequested();
            // Key custody may be asynchronous. Recheck current durable authority and the original deadline after it.
            var current = await permitSource.AuthorizeAsync(wire, cancellationToken) ?? throw new CredentialException("credential-dispatch-not-authorized");
            if (CredentialRules.ScopeDigest(current.Inspect(wire, clock.GetUtcNow())) != CredentialRules.ScopeDigest(scope))
                throw new CredentialException("credential-dispatch-scope-invalid");
            if (lease.Profile.ExpiresUtc <= clock.GetUtcNow().AddSeconds(15))
                throw new CredentialException("credential-not-current-or-authorized");
            _ = permit.Consume(wire, clock.GetUtcNow());
            if (!ReferenceEquals(current, permit)) _ = current.Consume(wire, clock.GetUtcNow());
            using var request = new HttpRequestMessage(HttpMethod.Post, wire.Uri);
            request.Content = new StringContent(wire.BodyJson, Encoding.UTF8, "application/json");
            foreach (var header in wire.Headers) request.Headers.TryAddWithoutValidation(header.Key, header.Value);
            request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", lease.AccessToken());
            Task<HttpResponseMessage> pendingResponse;
            try
            {
                cancellationToken.ThrowIfCancellationRequested();
                // Admission is the SendAsync invocation, not remote receipt or completion. Rotation/revocation cannot
                // commit between the authenticated vault read and this point. Never hold custody's gate over network I/O.
                admitted = true;
                boundSession?.MarkHttpAdmitted();
                pendingResponse = http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
            }
            finally { lease.ReleaseAdmissionGuard(); }
            using var response = await pendingResponse;
            // Never follow an authorization-bearing redirect, nor expose provider error bodies that may reflect a token.
            if ((int)response.StatusCode is >= 300 and < 400) return new(502, "");
            if (!response.IsSuccessStatusCode) return new((int)response.StatusCode, "");
            if (response.Content.Headers.ContentLength > 262144) return new((int)response.StatusCode, "");
            await using var source = await response.Content.ReadAsStreamAsync(cancellationToken);
            using var result = new MemoryStream(); var buffer = new byte[8192]; int read;
            while ((read = await source.ReadAsync(buffer, cancellationToken)) != 0)
            { if (result.Length + read > 262144) return new((int)response.StatusCode, ""); result.Write(buffer, 0, read); }
            var body = Encoding.UTF8.GetString(result.ToArray());
            if (body.Contains(lease.AccessToken(), StringComparison.Ordinal)) return new((int)response.StatusCode, "");
            return new((int)response.StatusCode, SafeBody(body, scope.Provider));
        }
        catch (OperationCanceledException) when (callerCancellation.IsCancellationRequested)
        { throw new OperationCanceledException("Pipeline credential operation interrupted.", callerCancellation); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (admitted) throw new TimeoutException("pipeline-authenticated-transport-uncertain");
            throw new PipelineContractException("pipeline-credential-admission-timeout");
        }
        catch (OperationCanceledException)
        { throw new OperationCanceledException("Pipeline credential operation interrupted.", callerCancellation); }
        catch (CredentialException) { throw new PipelineContractException("pipeline-credential-authority-denied"); }
        catch (HttpRequestException) { throw new HttpRequestException("pipeline-authenticated-transport-uncertain"); }
        catch (IOException) { throw new IOException("pipeline-authenticated-transport-uncertain"); }
    }
    static string SafeBody(string body, string provider)
    {
        try
        {
            using var doc = CredentialRules.Document(Encoding.UTF8.GetBytes(body)); var root = doc.RootElement;
            Dictionary<string, object> Select(JsonElement value, params string[] names)
            {
                var selected = new Dictionary<string, object>(StringComparer.Ordinal);
                foreach (var name in names) if (value.TryGetProperty(name, out var member)) selected[name] = member.Clone();
                return selected;
            }
            if (provider == PipelineRequestFactory.GitHub)
                return JsonSerializer.Serialize(Select(root, "workflow_run_id", "run_url", "html_url", "head_sha"), CredentialRules.Json);
            var result = Select(root, "id", "url");
            if (root.TryGetProperty("pipeline", out var pipeline)) result["pipeline"] = Select(pipeline, "id");
            if (root.TryGetProperty("resources", out var resources) && resources.TryGetProperty("repositories", out var repositories) && repositories.TryGetProperty("self", out var self))
            {
                var selected = Select(self, "version", "refName");
                if (self.TryGetProperty("repository", out var repository)) selected["repository"] = Select(repository, "id");
                result["resources"] = new { repositories = new { self = selected } };
            }
            return JsonSerializer.Serialize(result, CredentialRules.Json);
        }
        catch (Exception e) when (e is JsonException or InvalidOperationException or ArgumentException) { return ""; }
    }
    static void ValidateWire(PipelineWireRequest wire)
    {
        if (wire is null || wire.Uri is null || !wire.Uri.IsAbsoluteUri || wire.Method != "POST" || wire.Uri.Scheme != "https" ||
            !wire.Uri.IsDefaultPort || wire.Uri.UserInfo.Length != 0 || wire.Uri.Fragment.Length != 0 ||
            wire.Uri.Host is not ("api.github.com" or "dev.azure.com") || wire.BodyJson is null || Encoding.UTF8.GetByteCount(wire.BodyJson) > 131072 ||
            wire.Headers is null || wire.Headers.Count > 3 || wire.Headers.Any(h => h.Key is not ("Accept" or "X-GitHub-Api-Version" or "User-Agent") ||
                h.Value is null || h.Value.Length > 128 || h.Value.Any(char.IsControl)))
            throw new CredentialException("credential-dispatch-wire-invalid");
    }
    public void Dispose() => http?.Dispose();
}
