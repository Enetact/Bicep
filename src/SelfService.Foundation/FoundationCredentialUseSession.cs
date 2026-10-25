using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

// No public constructor, factory, callback or serialized proof can supply a held runtime session.
internal sealed class FoundationCredentialUseSession : ICredentialDispatchPermitSource, IDisposable
{
    readonly FoundationStore store;
    readonly CredentialVault vault;
    readonly FoundationCellActivationAuthority authority;
    readonly FoundationCellActivationLease activationLease;
    readonly FoundationCredentialDispatchBinding? dispatch;
    readonly EntitlementVerifier? verifier;
    readonly string? runtimeVersion;
    readonly CredentialBoundNamespaceMetadataLease? metadataLease;
    int disposed;
    int admitted;

    internal CredentialVaultNamespaceIdentity Namespace { get; }
    internal FoundationCellActivationAuthorityIdentity ActivationAuthority { get; }
    internal FoundationCellActivationBinding ActivationBinding => activationLease.Binding;
    internal string? ExpectedBindingSha256 { get; }
    internal bool HttpAdmitted => Volatile.Read(ref admitted) != 0;

    FoundationCredentialUseSession(FoundationStore store, CredentialVault vault, FoundationCellActivationAuthority authority,
        FoundationCellActivationLease lease, CredentialVaultNamespaceIdentity expectedNamespace,
        FoundationCellActivationAuthorityIdentity expectedAuthority, string? expectedBindingSha256,
        FoundationCredentialDispatchBinding? dispatch, EntitlementVerifier? verifier, string? runtimeVersion,
        CredentialBoundNamespaceMetadataLease? metadataLease = null)
    {
        this.store = store; this.vault = vault; this.authority = authority; activationLease = lease;
        Namespace = expectedNamespace; ActivationAuthority = expectedAuthority; ExpectedBindingSha256 = expectedBindingSha256;
        this.dispatch = dispatch; this.verifier = verifier; this.runtimeVersion = runtimeVersion;
        this.metadataLease = metadataLease;
    }

    internal static FoundationCredentialUseSession Open(FoundationStore store, CredentialVault vault,
        FoundationCellActivationAuthority authority, CredentialVaultNamespaceIdentity expectedNamespace,
        FoundationCellActivationAuthorityIdentity expectedAuthority, string expectedDatabaseInstanceId, string? expectedBindingSha256,
        FoundationCredentialDispatchBinding? dispatch = null, EntitlementVerifier? verifier = null, string? runtimeVersion = null)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(vault); ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(expectedNamespace); ArgumentNullException.ThrowIfNull(expectedAuthority);
        // AcquireLease validates a local existing database under the authority's own shared guard before any store/key use.
        var lease = authority.AcquireLease(store.RuntimeDatabasePath, expectedDatabaseInstanceId);
        try
        {
            var session = new FoundationCredentialUseSession(store, vault, authority, lease, expectedNamespace,
                expectedAuthority, expectedBindingSha256, dispatch, verifier, runtimeVersion);
            using (vault.HoldBoundNamespace(expectedNamespace)) session.AssertFor(store);
            return session;
        }
        catch { lease.Dispose(); throw; }
    }

    // Consumer custody is namespace identity only. It cannot authorize credentials via AuthorizeAsync and never reads admission state.
    internal static FoundationCredentialUseSession OpenForConsumer(FoundationStore store, CredentialVault vault,
        FoundationCellActivationAuthority authority, CredentialVaultNamespaceIdentity expectedNamespace,
        FoundationCellActivationAuthorityIdentity expectedAuthority, string expectedDatabaseInstanceId, string expectedBindingSha256)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(vault); ArgumentNullException.ThrowIfNull(authority);
        ArgumentNullException.ThrowIfNull(expectedNamespace); ArgumentNullException.ThrowIfNull(expectedAuthority);
        var lease = authority.AcquireLease(store.RuntimeDatabasePath, expectedDatabaseInstanceId);
        CredentialBoundNamespaceMetadataLease? metadata = null;
        try
        {
            metadata = vault.HoldBoundNamespaceMetadata(expectedNamespace);
            var session = new FoundationCredentialUseSession(store, vault, authority, lease, expectedNamespace, expectedAuthority,
                expectedBindingSha256, null, null, null, metadata);
            session.AssertFor(store); return session;
        }
        catch { metadata?.Dispose(); lease.Dispose(); throw; }
    }

    internal void AssertFor(FoundationStore expectedStore)
    {
        if (Volatile.Read(ref disposed) != 0 || !ReferenceEquals(store, expectedStore) ||
            Namespace.CustomerId != store.CustomerId || Namespace.CellId != store.CellId ||
            ActivationAuthority.CustomerId != store.CustomerId || ActivationAuthority.CellId != store.CellId)
            throw new CredentialException("credential-bound-session-invalid");
        activationLease.AssertCurrent();
        if (authority.ReadHeldIdentity() != ActivationAuthority)
            throw new CredentialException("credential-bound-activation-authority-changed");
        if (metadataLease is not null) metadataLease.AssertCurrent();
        else vault.AssertBoundNamespace(Namespace);
    }

    internal void MarkHttpAdmitted() => Interlocked.Exchange(ref admitted, 1);

    public Task<CredentialDispatchPermit?> AuthorizeAsync(PipelineWireRequest wire, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (dispatch is null || verifier is null || runtimeVersion is null)
            throw new CredentialException("credential-bound-dispatch-required");
        try
        {
            AssertFor(store);
            return Task.FromResult<CredentialDispatchPermit?>(store.AuthorizeCurrentCredentialDispatch(dispatch, wire,
                verifier, runtimeVersion, cancellationToken, this));
        }
        catch (FoundationStoreException) { throw new CredentialException("credential-bound-dispatch-not-current"); }
        catch (SqliteException) { throw new CredentialException("credential-bound-dispatch-storage-unavailable"); }
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            try { metadataLease?.Dispose(); }
            finally { activationLease.Dispose(); }
    }
    public override string ToString() => "[CONCRETE ACTIVATION-HELD CREDENTIAL USE SESSION]";
}
