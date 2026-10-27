namespace SelfService.Foundation;

// A concrete local authority lease shared by API and independent execution hosts.
// Shutdown requests disposal; active operations retain the real lease until they finish.
// This neither provisions authority nor accepts a remote verification result as custody.
public sealed class FoundationExecutionActivation : IDisposable
{
    readonly FoundationCellActivationLease? lease;
    readonly object lifetimeGate = new();
    int activeUses;
    bool disposeRequested, leaseDisposalScheduled;

    public FoundationExecutionActivation(bool requireActivationAuthority, string? activationAuthorityDirectory,
        string customerId, string cellId, string databasePath)
    {
        if (!requireActivationAuthority)
        {
            if (activationAuthorityDirectory is not null) throw new ArgumentException("Activation authority must be explicitly required when configured.");
            return;
        }
        if (string.IsNullOrWhiteSpace(activationAuthorityDirectory)) throw new ArgumentException("An existing protected activation authority is required.");
        var authority = new FoundationCellActivationAuthority(activationAuthorityDirectory, customerId, cellId);
        lease = authority.AcquireLease(databasePath);
    }

    public void AssertCurrent(string databasePath, string instanceId) => lease?.AssertCurrent(databasePath, instanceId);

    // Retains this already-acquired concrete lease, not caller-supplied authority.
    // The lifetime monitor is never held during SQLite/metadata checks or lease disposal.
    public IDisposable RetainUse()
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

    sealed class RuntimeUse(FoundationExecutionActivation owner) : IDisposable
    {
        FoundationExecutionActivation? retained = owner;
        public void Dispose() => Interlocked.Exchange(ref retained, null)?.ReleaseUse();
    }
}
