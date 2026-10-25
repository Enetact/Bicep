namespace SelfService.Foundation;

// Pins restrict the concrete store lookup. They do not supply current database, vault or activation authority.
public sealed record FoundationCredentialBoundDispatch(string JobId, string WorkerId, long Fence, CredentialScope Scope,
    string PlanDigest, string SourceRevision, string CatalogDigest);

/// <summary>Explicit opt-in Foundation transport; each operation owns a real activation lease through its awaited HTTP work.</summary>
public sealed class FoundationCredentialBoundTransport : IPipelineTransport, IPipelineTransportCharacteristics, IDisposable
{
    readonly FoundationStore store;
    readonly CredentialVault vault;
    readonly FoundationCellActivationAuthority activationAuthority;
    readonly CredentialVaultNamespaceIdentity expectedNamespace;
    readonly FoundationCellActivationAuthorityIdentity expectedAuthority;
    readonly string databaseInstanceId, bindingSha256, runtimeVersion;
    readonly FoundationCredentialDispatchBinding dispatch;
    readonly EntitlementVerifier verifier;
    readonly TimeProvider clock;
    readonly TimeSpan timeout;
    readonly PipelineAuthenticatedTransport transport;
    readonly bool enabled;
    int disposed;
    public bool IsMock => transport.IsMock;

    public FoundationCredentialBoundTransport(FoundationStore store, CredentialVault vault,
        FoundationCellActivationAuthority activationAuthority, CredentialVaultNamespaceIdentity expectedNamespace,
        FoundationCellActivationAuthorityIdentity expectedActivationAuthority, string expectedDatabaseInstanceId,
        string expectedBindingSha256, FoundationCredentialBoundDispatch dispatch, EntitlementVerifier entitlementVerifier,
        string runtimeVersion, bool enableExternalDispatch = false, TimeProvider? clock = null, TimeSpan? operationTimeout = null)
        : this(store, vault, activationAuthority, expectedNamespace, expectedActivationAuthority, expectedDatabaseInstanceId,
            expectedBindingSha256, dispatch, entitlementVerifier, runtimeVersion, null, enableExternalDispatch, clock, operationTimeout) { }

    FoundationCredentialBoundTransport(FoundationStore store, CredentialVault vault,
        FoundationCellActivationAuthority activationAuthority, CredentialVaultNamespaceIdentity expectedNamespace,
        FoundationCellActivationAuthorityIdentity expectedAuthority, string databaseInstanceId, string bindingSha256,
        FoundationCredentialBoundDispatch dispatch, EntitlementVerifier verifier, string runtimeVersion,
        HttpMessageHandler? mockHandler, bool enabled, TimeProvider? clock, TimeSpan? operationTimeout)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(vault); ArgumentNullException.ThrowIfNull(activationAuthority);
        ArgumentNullException.ThrowIfNull(expectedNamespace); ArgumentNullException.ThrowIfNull(expectedAuthority);
        ArgumentNullException.ThrowIfNull(dispatch); ArgumentNullException.ThrowIfNull(verifier);
        var scope = CredentialRules.Snapshot(dispatch.Scope);
        if (expectedNamespace.CustomerId != store.CustomerId || expectedNamespace.CellId != store.CellId ||
            expectedAuthority.CustomerId != store.CustomerId || expectedAuthority.CellId != store.CellId ||
            scope.CustomerId != store.CustomerId || scope.CellId != store.CellId ||
            !CustomerContextResolver.ValidId(dispatch.JobId) || !CustomerContextResolver.ValidId(dispatch.WorkerId) || dispatch.Fence <= 0 ||
            !CredentialRecoverySets.NamespaceShape(databaseInstanceId) || !CredentialRecoverySets.DigestShape(bindingSha256) ||
            !CredentialRecoverySets.DigestShape(dispatch.PlanDigest) || !CredentialRecoverySets.DigestShape(expectedAuthority.CustodyCommitmentSha256) ||
            string.IsNullOrWhiteSpace(dispatch.SourceRevision) || dispatch.SourceRevision.Length > 128 || dispatch.SourceRevision.Any(char.IsControl) ||
            dispatch.CatalogDigest is null || dispatch.CatalogDigest.Length != 0 && !CredentialRecoverySets.DigestShape(dispatch.CatalogDigest) ||
            string.IsNullOrWhiteSpace(runtimeVersion) || runtimeVersion.Length > 128 || runtimeVersion.Any(char.IsControl))
            throw new CredentialException("credential-bound-transport-configuration-invalid");
        this.store = store; this.vault = vault; this.activationAuthority = activationAuthority; this.expectedNamespace = expectedNamespace;
        this.expectedAuthority = expectedAuthority; this.databaseInstanceId = databaseInstanceId; this.bindingSha256 = bindingSha256;
        this.dispatch = new(databaseInstanceId, dispatch.JobId, dispatch.WorkerId, dispatch.Fence, scope,
            dispatch.PlanDigest, dispatch.SourceRevision, dispatch.CatalogDigest);
        this.verifier = verifier; this.runtimeVersion = runtimeVersion; this.clock = clock ?? TimeProvider.System;
        timeout = CredentialRules.OperationTimeout(operationTimeout, TimeSpan.FromSeconds(20)); this.enabled = enabled;
        transport = mockHandler is null
            ? new(vault, new BoundOnlyPermits(), enabled, this.clock, timeout)
            : PipelineAuthenticatedTransport.ForMockHandler(vault, new BoundOnlyPermits(), mockHandler, this.clock, timeout);
    }

    // This trusted synthetic seam neither changes host registration nor selects an external provider by configuration.
    public static FoundationCredentialBoundTransport ForMockHandler(FoundationStore store, CredentialVault vault,
        FoundationCellActivationAuthority activationAuthority, CredentialVaultNamespaceIdentity expectedNamespace,
        FoundationCellActivationAuthorityIdentity expectedActivationAuthority, string expectedDatabaseInstanceId,
        string expectedBindingSha256, FoundationCredentialBoundDispatch dispatch, EntitlementVerifier entitlementVerifier,
        string runtimeVersion, HttpMessageHandler mockHandler, TimeProvider? clock = null, TimeSpan? operationTimeout = null)
    {
        ArgumentNullException.ThrowIfNull(mockHandler);
        return new(store, vault, activationAuthority, expectedNamespace, expectedActivationAuthority, expectedDatabaseInstanceId,
            expectedBindingSha256, dispatch, entitlementVerifier, runtimeVersion, mockHandler, true, clock, operationTimeout);
    }

    // Explicit trusted-owner cutover only. No normal constructor/startup, DTO or missing-root fallback calls this operation.
    public static FoundationCredentialRuntimeBinding BindCurrent(FoundationStore store, CredentialVault vault,
        FoundationCellActivationAuthority activationAuthority, CredentialVaultNamespaceIdentity expectedNamespace,
        FoundationCellActivationAuthorityIdentity expectedActivationAuthority, string expectedDatabaseInstanceId)
    {
        using var session = FoundationCredentialUseSession.Open(store, vault, activationAuthority, expectedNamespace,
            expectedActivationAuthority, expectedDatabaseInstanceId, null);
        using var namespaceGuards = vault.HoldBoundNamespace(expectedNamespace);
        session.AssertFor(store);
        return store.ProvisionCredentialRuntimeBinding(session);
    }

    public async Task<PipelineWireResponse> SendAsync(PipelineWireRequest wire, CancellationToken cancellationToken)
    {
        ObjectDisposedException.ThrowIf(Volatile.Read(ref disposed) != 0, this);
        if (!enabled) throw new PipelineTransportDisabledException();
        cancellationToken.ThrowIfCancellationRequested();
        using var deadline = CredentialRules.OperationDeadline(timeout, cancellationToken);
        FoundationCredentialUseSession? session = null;
        try
        {
            session = FoundationCredentialUseSession.Open(store, vault, activationAuthority, expectedNamespace, expectedAuthority,
                databaseInstanceId, bindingSha256, dispatch, verifier, runtimeVersion);
            deadline.Token.ThrowIfCancellationRequested();
            store.RequireCredentialRuntimeBinding(session);
            var response = await transport.SendBoundAsync(wire, session, deadline.Token);
            // Inner Send has disposed its owned request/response/body before returning. This actual session still owns activation.
            deadline.Token.ThrowIfCancellationRequested();
            var current = await session.AuthorizeAsync(wire, deadline.Token)
                ?? throw new CredentialException("credential-bound-dispatch-not-current");
            _ = current.Inspect(wire, clock.GetUtcNow());
            session.AssertFor(store); deadline.Token.ThrowIfCancellationRequested();
            return response;
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        { throw new OperationCanceledException("Pipeline bound credential operation interrupted.", cancellationToken); }
        catch (OperationCanceledException) when (deadline.IsCancellationRequested)
        {
            if (session?.HttpAdmitted == true) throw new TimeoutException("pipeline-credential-bound-operation-uncertain");
            throw new PipelineContractException("pipeline-credential-admission-timeout");
        }
        catch (TimeoutException) when (session?.HttpAdmitted == true)
        { throw new TimeoutException("pipeline-credential-bound-operation-uncertain"); }
        catch (Exception error) when (session?.HttpAdmitted == true && error is not StackOverflowException and not OutOfMemoryException)
        { throw new HttpRequestException("pipeline-credential-bound-operation-uncertain"); }
        catch (Exception error) when (error is CredentialException or FoundationStoreException or Microsoft.Data.Sqlite.SqliteException or IOException or UnauthorizedAccessException)
        { throw new PipelineContractException("pipeline-credential-authority-denied"); }
        finally { session?.Dispose(); }
    }

    // Disposing a transport may cancel its HttpClient; only the Send finally can release that operation's actual lease.
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0) transport.Dispose();
    }

    sealed class BoundOnlyPermits : ICredentialDispatchPermitSource
    {
        public Task<CredentialDispatchPermit?> AuthorizeAsync(PipelineWireRequest wire, CancellationToken cancellationToken) =>
            throw new CredentialException("credential-bound-session-required");
    }
}
