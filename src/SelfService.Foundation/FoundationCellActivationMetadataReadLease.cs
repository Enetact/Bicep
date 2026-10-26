using System.Runtime.Versioning;

namespace SelfService.Foundation;

public sealed partial class FoundationCellActivationAuthority
{
    // Metadata selection only: this never opens a database or proves that a selected database is active.
    [SupportedOSPlatform("windows")]
    internal FoundationCellActivationMetadataReadLease AcquireMetadataReadLease(FoundationCellActivationAuthorityIdentity expectedIdentity,
        long expectedGeneration, string? expectedBindingSha256)
    {
        if (expectedIdentity is null || expectedGeneration is < 0 or > MaximumGenerations ||
            (expectedGeneration == 0 ? expectedBindingSha256 is not null : !DigestShape(expectedBindingSha256)))
            throw Failure("ActivationMetadataSelectionInvalid", 400);
        var observed = clock.GetUtcNow();
        var guard = OpenGuard(exclusive: false);
        try
        {
            AssertMetadataSelection(expectedIdentity, expectedGeneration, expectedBindingSha256, observed);
            return new(this, guard, expectedIdentity, expectedGeneration, expectedBindingSha256, observed);
        }
        catch { guard.Dispose(); throw; }
    }
    [SupportedOSPlatform("windows")]
    internal void AssertMetadataSelection(FoundationCellActivationAuthorityIdentity identity, long generation, string? bindingSha256, DateTimeOffset observedUtc)
    {
        if (observedUtc.Offset != TimeSpan.Zero) throw Failure("ActivationMetadataTimeInvalid", 403);
        NoLinks(root); ValidateProvisionParent(root);
        if (ReadHeldIdentityAt(observedUtc) != identity) throw Failure("ActivationMetadataIdentityChanged", 403);
        var current = ReadStateAt(observedUtc);
        if ((current?.Generation ?? 0) != generation || current?.Sha256 != bindingSha256)
            throw Failure("ActivationMetadataHeadChanged", 403);
    }
    internal DateTimeOffset ObserveMetadataTime() => clock.GetUtcNow();
}

[SupportedOSPlatform("windows")]
internal sealed class FoundationCellActivationMetadataReadLease : IDisposable
{
    readonly FoundationCellActivationAuthority owner;
    readonly DateTimeOffset openedAt;
    readonly object gate = new();
    FileStream? guard;
    internal FoundationCellActivationAuthorityIdentity Identity { get; }
    internal long Generation { get; }
    internal string? BindingSha256 { get; }
    internal FoundationCellActivationMetadataReadLease(FoundationCellActivationAuthority owner, FileStream guard,
        FoundationCellActivationAuthorityIdentity identity, long generation, string? bindingSha256, DateTimeOffset openedAt)
    { this.owner = owner; this.guard = guard; Identity = identity; Generation = generation; BindingSha256 = bindingSha256; this.openedAt = openedAt; }
    internal void AssertCurrent() { lock (gate) { RequireHeld(); owner.AssertMetadataSelection(Identity, Generation, BindingSha256, owner.ObserveMetadataTime()); } }
    internal void AssertCurrentAt(DateTimeOffset observedUtc) { lock (gate) { RequireHeld(); owner.AssertMetadataSelection(Identity, Generation, BindingSha256, observedUtc); } }
    internal void AssertSnapshotCurrent() => AssertCurrentAt(openedAt);
    void RequireHeld() { if (guard is null) throw new FoundationStoreException("ActivationMetadataLeaseDisposed", 403); }
    public void Dispose() { lock (gate) { guard?.Dispose(); guard = null; } }
}
