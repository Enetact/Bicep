using Microsoft.Data.Sqlite;

namespace SelfService.Foundation;

/// <summary>Trusted, existing-store authorization for one exact dispatched job. Not an HTTP/model authority.</summary>
public sealed class FoundationCredentialDispatchPermitSource : ICredentialDispatchPermitSource
{
    readonly FoundationStore store;
    readonly FoundationCredentialDispatchBinding binding;
    readonly EntitlementVerifier verifier;
    readonly string runtimeVersion;

    public FoundationCredentialDispatchPermitSource(FoundationStore store, string jobId, string workerId, long fence,
        CredentialScope scope, string expectedPlanDigest, string expectedSourceRevision, string expectedCatalogDigest,
        EntitlementVerifier entitlementVerifier, string runtimeVersion)
    {
        ArgumentNullException.ThrowIfNull(store); ArgumentNullException.ThrowIfNull(entitlementVerifier);
        var detached = CredentialRules.Snapshot(scope);
        if (detached.CustomerId != store.CustomerId || detached.CellId != store.CellId || fence <= 0 ||
            !CustomerContextResolver.ValidId(jobId) || !CustomerContextResolver.ValidId(workerId) ||
            expectedPlanDigest is not { Length: 64 } || expectedPlanDigest.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c)) ||
            string.IsNullOrWhiteSpace(expectedSourceRevision) || expectedSourceRevision.Length > 128 || expectedSourceRevision.Any(char.IsControl) ||
            expectedCatalogDigest is null || expectedCatalogDigest.Length != 0 &&
                (expectedCatalogDigest.Length != 64 || expectedCatalogDigest.Any(c => !char.IsAsciiHexDigit(c) || char.IsUpper(c))) ||
            string.IsNullOrWhiteSpace(runtimeVersion) || runtimeVersion.Length > 128)
            throw new CredentialException("credential-durable-dispatch-binding-invalid");
        this.store = store; verifier = entitlementVerifier; this.runtimeVersion = runtimeVersion;
        binding = new(store.GetCellInstanceIdentity().DatabaseInstanceId, jobId, workerId, fence, detached,
            expectedPlanDigest, expectedSourceRevision, expectedCatalogDigest);
    }

    public Task<CredentialDispatchPermit?> AuthorizeAsync(PipelineWireRequest wire, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        try
        {
            return Task.FromResult<CredentialDispatchPermit?>(store.AuthorizeCurrentCredentialDispatch(binding, wire,
                verifier, runtimeVersion, cancellationToken));
        }
        catch (FoundationStoreException) { throw new CredentialException("credential-durable-dispatch-not-current"); }
        catch (SqliteException) { throw new CredentialException("credential-durable-dispatch-storage-unavailable"); }
    }
    public override string ToString() => "[STORE-BOUND DISPATCH AUTHORIZER]";
}
