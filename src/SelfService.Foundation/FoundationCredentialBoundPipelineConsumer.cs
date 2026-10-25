using System.Runtime.CompilerServices;
using System.Text.Json;
using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

/// <summary>Explicit trusted mock-only composition for the supported Foundation in-process runner.</summary>
public sealed class FoundationCredentialBoundPipelineConsumer : IDisposable
{
    public const int MaximumScopes = 256;
    readonly CredentialVault vault;
    readonly FoundationCellActivationAuthority authority;
    readonly CredentialVaultNamespaceIdentity expectedNamespace;
    readonly FoundationCellActivationAuthorityIdentity expectedAuthority;
    readonly string instance, bindingSha256;
    readonly CredentialScope[] allowedScopes;
    readonly Func<HttpMessageHandler> mockHandlers;
    readonly ConditionalWeakTable<HttpMessageHandler, object> ownedHandlers = new();
    readonly object handlerGate = new();
    readonly TimeSpan timeout;
    int disposed;

    public TimeProvider Clock { get; }
    public int ScopeCount => allowedScopes.Length;

    FoundationCredentialBoundPipelineConsumer(CredentialVault vault, FoundationCellActivationAuthority authority,
        CredentialVaultNamespaceIdentity expectedNamespace, FoundationCellActivationAuthorityIdentity expectedAuthority,
        string instance, string bindingSha256, IReadOnlyList<CredentialScope> allowedScopes,
        Func<HttpMessageHandler> mockHandlers, TimeProvider? clock, TimeSpan? timeout)
    {
        var configuredCount = allowedScopes?.Count ?? -1;
        if (vault is null || authority is null || expectedNamespace is null || expectedAuthority is null || mockHandlers is null ||
            allowedScopes is null || configuredCount is < 1 or > MaximumScopes ||
            !CredentialRecoverySets.NamespaceShape(instance) || !CredentialRecoverySets.DigestShape(bindingSha256) ||
            !CredentialRecoverySets.NamespaceShape(expectedAuthority.AuthorityId) || !CredentialRecoverySets.DigestShape(expectedAuthority.CustodyCommitmentSha256) ||
            expectedNamespace.CustomerId != expectedAuthority.CustomerId || expectedNamespace.CellId != expectedAuthority.CellId)
            throw ConfigurationFailure();
        var snapshots = new List<CredentialScope>(configuredCount); var selectors = new HashSet<string>(StringComparer.Ordinal);
        try
        {
            for (var i = 0; i < configuredCount; i++)
            {
                var scope = CredentialRules.Snapshot(allowedScopes[i]);
                if (scope.CustomerId != expectedNamespace.CustomerId || scope.CellId != expectedNamespace.CellId ||
                    !selectors.Add(Selector(scope))) throw ConfigurationFailure();
                snapshots.Add(scope);
            }
        }
        catch (Exception error) when (error is CredentialException or ArgumentException or IndexOutOfRangeException or InvalidOperationException)
        { throw ConfigurationFailure(); }
        this.vault = vault; this.authority = authority; this.expectedNamespace = expectedNamespace; this.expectedAuthority = expectedAuthority;
        this.instance = instance; this.bindingSha256 = bindingSha256; this.allowedScopes = snapshots.ToArray(); this.mockHandlers = mockHandlers;
        Clock = clock ?? TimeProvider.System; this.timeout = CredentialRules.OperationTimeout(timeout, TimeSpan.FromSeconds(20));
    }

    // A handler factory is a trusted synthetic HTTP seam, never a scope resolver or evidence of held custody.
    // Each returned handler transfers exclusively to one returned transport; reuse is rejected by reference identity.
    public static FoundationCredentialBoundPipelineConsumer ForMockHandlers(CredentialVault vault,
        FoundationCellActivationAuthority activationAuthority, CredentialVaultNamespaceIdentity expectedNamespace,
        FoundationCellActivationAuthorityIdentity expectedActivationAuthority, string expectedDatabaseInstanceId,
        string expectedBindingSha256, IReadOnlyList<CredentialScope> allowedScopes, Func<HttpMessageHandler> mockHandlerFactory,
        TimeProvider? clock = null, TimeSpan? operationTimeout = null) =>
        new(vault, activationAuthority, expectedNamespace, expectedActivationAuthority, expectedDatabaseInstanceId,
            expectedBindingSha256, allowedScopes, mockHandlerFactory, clock, operationTimeout);

    public void ValidateStartup(FoundationStore store)
    {
        using var run = OpenRun(store);
        run.AssertCurrent();
    }

    // Retain this concrete scope from BEFORE claim through the awaited operation and its durable job outcome.
    // Startup probes use a short scope; they do not cache authority or establish a lifetime guard.
    public FoundationCredentialBoundPipelineRun OpenRun(FoundationStore store)
    {
        EnsureAvailable();
        FoundationCredentialUseSession? session = null;
        try
        {
            session = FoundationCredentialUseSession.OpenForConsumer(store, vault, authority, expectedNamespace, expectedAuthority,
                instance, bindingSha256);
            _ = store.RequireCredentialRuntimeBinding(session);
            EnsureAvailable();
            return new(this, store, session);
        }
        catch (FoundationStoreException) { session?.Dispose(); throw; }
        catch (Exception error) when (error is CredentialException or SqliteException or IOException or UnauthorizedAccessException)
        { session?.Dispose(); throw new FoundationStoreException("BoundPipelineConsumerUnavailable", 403); }
        catch { session?.Dispose(); throw; }
    }

    internal FoundationCredentialBoundTransport CreateTransport(FoundationStore store, FoundationCredentialUseSession runSession,
        FoundationDispatch dispatch, PipelineConnection connection, string workerId, long fence, EntitlementVerifier verifier, string runtimeVersion)
    {
        EnsureAvailable();
        if (dispatch is null || dispatch.Job is null || dispatch.Plan is null || dispatch.Connection is null || connection is null || verifier is null ||
            dispatch.Job.WorkerId != workerId || dispatch.Job.Fence != fence ||
            dispatch.Job.State != "Dispatched" || fence <= 0 || dispatch.Job.CustomerId != expectedNamespace.CustomerId ||
            dispatch.Job.CellId != expectedNamespace.CellId || dispatch.Connection.CustomerId != expectedNamespace.CustomerId ||
            connection.CustomerId != expectedNamespace.CustomerId || connection.CellId != expectedNamespace.CellId ||
            connection.Id != dispatch.Connection.Id || connection.Generation != dispatch.Connection.Generation || connection.Provider != dispatch.Connection.Provider)
            throw new PipelineContractException("pipeline-bound-consumer-dispatch-invalid");
        var scope = SelectScope(connection, dispatch.Job.ProjectId, dispatch.Job.EnvironmentId);
        var digest = scope.ConnectionDigest;
        PipelineWireRequest wire;
        try
        {
            using var document = CredentialRules.Document(System.Text.Encoding.UTF8.GetBytes(dispatch.Plan.PayloadJson));
            var request = document.RootElement.Deserialize<PipelineDispatchRequest>(CredentialRules.Json)
                ?? throw new JsonException();
            if (request.SourceRevision != dispatch.Plan.SourceRevision || request.Environment != dispatch.Job.EnvironmentId)
                throw new PipelineContractException("pipeline-bound-consumer-dispatch-invalid");
            var approval = new PipelineApproval(expectedNamespace.CustomerId, expectedNamespace.CellId, connection.Id, connection.Generation,
                digest, PipelineRequestFactory.RequestDigest(connection, request), dispatch.Plan.Digest, dispatch.Plan.ExpiresAt);
            wire = PipelineRequestFactory.Create(connection, request, approval, Clock.GetUtcNow());
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException)
        { throw new PipelineContractException("pipeline-bound-consumer-dispatch-invalid"); }
        var pins = new FoundationCredentialDispatchBinding(instance, dispatch.Job.Id, workerId, fence, scope,
            dispatch.Plan.Digest, dispatch.Plan.SourceRevision, dispatch.Plan.CatalogDigest);
        AuthorizePreparation(store, runSession, pins, wire, verifier, runtimeVersion);
        HttpMessageHandler? handler = null; var ownsHandler = false;
        try
        {
            try { handler = mockHandlers(); }
            catch { throw new PipelineContractException("pipeline-bound-consumer-handler-unavailable"); }
            if (handler is null) throw new PipelineContractException("pipeline-bound-consumer-handler-unavailable");
            lock (handlerGate)
            {
                if (ownedHandlers.TryGetValue(handler, out _))
                    throw new PipelineContractException("pipeline-bound-consumer-handler-reused");
                ownedHandlers.Add(handler, new object()); ownsHandler = true;
            }
            EnsureAvailable();
            // The synthetic factory cannot keep an earlier durable authorization alive through its own execution.
            AuthorizePreparation(store, runSession, pins, wire, verifier, runtimeVersion);
            var transport = FoundationCredentialBoundTransport.ForMockHandler(store, vault, authority, expectedNamespace, expectedAuthority,
                instance, bindingSha256, new(pins.JobId, pins.WorkerId, pins.Fence, scope, pins.PlanDigest, pins.SourceRevision, pins.CatalogDigest),
                verifier, runtimeVersion, new OwnedMockHandler(handler), Clock, timeout);
            ownsHandler = false; return transport;
        }
        finally
        {
            // A reused handler belongs to its first transport and must never be disposed by the rejected second caller.
            if (ownsHandler)
                try { handler?.Dispose(); }
                catch { throw new PipelineContractException("pipeline-bound-consumer-handler-unavailable"); }
        }
    }

    static void AuthorizePreparation(FoundationStore store, FoundationCredentialUseSession session,
        FoundationCredentialDispatchBinding pins, PipelineWireRequest wire, EntitlementVerifier verifier, string runtimeVersion)
    {
        try
        {
            session.AssertFor(store);
            _ = store.AuthorizeCurrentCredentialDispatch(pins, wire, verifier, runtimeVersion, default, session);
        }
        catch (Exception error) when (error is FoundationStoreException or CredentialException or SqliteException or IOException or UnauthorizedAccessException)
        { throw new PipelineContractException("pipeline-credential-authority-denied"); }
    }

    void EnsureAvailable()
    {
        if (Volatile.Read(ref disposed) != 0) throw new FoundationStoreException("BoundPipelineConsumerDisposed", 403);
    }
    internal void RequireConnection(PipelineConnection connection, string projectId, string environmentId)
    {
        EnsureAvailable();
        try { _ = SelectScope(connection, projectId, environmentId); }
        catch (Exception error) when (error is PipelineContractException or ArgumentException or InvalidOperationException)
        { throw new FoundationStoreException("BoundPipelineConnectionNotAllowed", 403); }
    }
    CredentialScope SelectScope(PipelineConnection connection, string projectId, string environmentId)
    {
        if (connection is null || connection.CustomerId != expectedNamespace.CustomerId || connection.CellId != expectedNamespace.CellId)
            throw new PipelineContractException("pipeline-bound-consumer-scope-not-allowed");
        var digest = PipelineRequestFactory.ConnectionDigest(connection);
        return allowedScopes.SingleOrDefault(candidate => candidate.ProjectId == projectId && candidate.EnvironmentId == environmentId &&
            candidate.Provider == connection.Provider && candidate.ConnectionId == connection.Id && candidate.ConnectionGeneration == connection.Generation &&
            candidate.ConnectionDigest == digest && candidate.AuthProfileId == connection.CredentialReference)
            ?? throw new PipelineContractException("pipeline-bound-consumer-scope-not-allowed");
    }
    static string Selector(CredentialScope scope) => JsonSerializer.Serialize(new
    { scope.CustomerId, scope.CellId, scope.ProjectId, scope.EnvironmentId, scope.Provider, scope.ConnectionId, scope.ConnectionGeneration, scope.AuthProfileId }, CredentialRules.Json);
    static CredentialException ConfigurationFailure() => new("credential-bound-consumer-configuration-invalid");
    public void Dispose() => Interlocked.Exchange(ref disposed, 1);
    public override string ToString() => "[MOCK-ONLY BOUND FOUNDATION PIPELINE CONSUMER]";

    sealed class OwnedMockHandler(HttpMessageHandler inner) : DelegatingHandler(inner)
    {
        protected override void Dispose(bool disposing)
        {
            try { base.Dispose(disposing); }
            catch { throw new PipelineContractException("pipeline-bound-consumer-handler-unavailable"); }
        }
    }
}

/// <summary>Concrete activation-held scope for one runner iteration; never constructible from a request or status DTO.</summary>
public sealed class FoundationCredentialBoundPipelineRun : IDisposable
{
    readonly FoundationCredentialBoundPipelineConsumer owner;
    readonly FoundationStore store;
    readonly FoundationCredentialUseSession session;
    int disposed;
    internal FoundationCredentialBoundPipelineRun(FoundationCredentialBoundPipelineConsumer owner, FoundationStore store,
        FoundationCredentialUseSession session) { this.owner = owner; this.store = store; this.session = session; }

    public void AssertCurrent()
    {
        if (Volatile.Read(ref disposed) != 0) throw new FoundationStoreException("BoundPipelineRunDisposed", 403);
        try { session.AssertFor(store); _ = store.RequireCredentialRuntimeBinding(session); }
        catch (FoundationStoreException) { throw; }
        catch (Exception error) when (error is CredentialException or SqliteException or IOException or UnauthorizedAccessException)
        { throw new FoundationStoreException("BoundPipelineConsumerUnavailable", 403); }
    }

    public FoundationCredentialBoundTransport CreateTransport(FoundationDispatch dispatch, PipelineConnection connection,
        string workerId, long fence, EntitlementVerifier entitlementVerifier, string runtimeVersion)
    {
        AssertCurrent();
        return owner.CreateTransport(store, session, dispatch, connection, workerId, fence, entitlementVerifier, runtimeVersion);
    }

    // Eligibility only: callers load the actual scoped connection and keep this run through the store's final enqueue transaction.
    public void RequireConnection(PipelineConnection connection, string projectId, string environmentId)
    {
        AssertCurrent(); owner.RequireConnection(connection, projectId, environmentId); AssertCurrent();
    }

    public void Dispose() { if (Interlocked.Exchange(ref disposed, 1) == 0) session.Dispose(); }
}
