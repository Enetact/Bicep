using SelfService.Foundation;

namespace SelfService.Foundation.Host;

// DI requests shutdown; active owned runner uses retain the actual lease until they finish.
// No user/model route provisions or switches authority.
public sealed class FoundationRuntimeActivation : IDisposable
{
    readonly FoundationCellActivationLease? lease;
    readonly object lifetimeGate = new();
    int activeUses;
    bool disposeRequested, leaseDisposalScheduled;
    public FoundationRuntimeActivation(FoundationHostOptions options)
    {
        if (!options.RequireActivationAuthority)
        {
            if (options.ActivationAuthorityDirectory is not null) throw new ArgumentException("Activation authority must be explicitly required when configured.");
            return;
        }
        if (string.IsNullOrWhiteSpace(options.ActivationAuthorityDirectory)) throw new ArgumentException("An existing protected activation authority is required.");
        var authority = new FoundationCellActivationAuthority(options.ActivationAuthorityDirectory, options.Identity.CustomerId, options.Identity.CellId);
        lease = authority.AcquireLease(options.DatabasePath);
    }
    public void AssertCurrent(string databasePath, string instanceId) => lease?.AssertCurrent(databasePath, instanceId);

    // This retains the already-acquired concrete host lease, not caller-supplied authority.
    // No lifetime monitor is held while the lease performs SQLite/metadata checks or disposal.
    internal IDisposable RetainUse()
    {
        lock (lifetimeGate)
        {
            if (disposeRequested) throw new FoundationStoreException("ActivationRuntimeStopping", 403);
            activeUses = checked(activeUses + 1); return new RuntimeUse(this);
        }
    }
    void ReleaseUse()
    {
        FoundationCellActivationLease? release;
        lock (lifetimeGate)
        {
            activeUses--; release = TakeDisposableLease();
        }
        release?.Dispose();
    }
    FoundationCellActivationLease? TakeDisposableLease()
    {
        if (!disposeRequested || activeUses != 0 || leaseDisposalScheduled) return null;
        leaseDisposalScheduled = true; return lease;
    }
    public void Dispose()
    {
        FoundationCellActivationLease? release;
        lock (lifetimeGate)
        {
            disposeRequested = true; release = TakeDisposableLease();
        }
        release?.Dispose();
    }
    sealed class RuntimeUse(FoundationRuntimeActivation owner) : IDisposable
    {
        FoundationRuntimeActivation? retained = owner;
        public void Dispose() => Interlocked.Exchange(ref retained, null)?.ReleaseUse();
    }
}
