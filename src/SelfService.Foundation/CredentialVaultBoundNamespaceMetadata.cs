namespace SelfService.Foundation;

public sealed partial class CredentialVault
{
    internal CredentialBoundNamespaceMetadataLease HoldBoundNamespaceMetadata(CredentialVaultNamespaceIdentity expected)
    {
        var pins = new List<CredentialNamespaceMetadataPin>(4);
        try
        {
            pins.Add(custody.HoldMetadataFile(CredentialNamespaceCustody.MarkerName));
            if (rollback is not null) pins.AddRange(rollback.HoldNamespaceMetadata());
            var lease = new CredentialBoundNamespaceMetadataLease(this, expected, pins.ToArray());
            lease.AssertCurrent(); return lease;
        }
        catch { foreach (var pin in pins) pin.Dispose(); throw; }
    }

    internal void AssertBoundNamespaceMetadata(CredentialVaultNamespaceIdentity expected)
    {
        custody.ValidateMetadataIdentity(); rollback?.ValidateNamespaceMetadata();
        var identity = new CredentialVaultNamespaceIdentity(options.CustomerId, options.CellId, custody.NamespaceId,
            custody.NamespaceCommitmentSha256, rollback?.AuthorityNamespaceId, rollback?.AuthorityBindingSha256);
        if (identity != expected) throw new CredentialException("credential-bound-namespace-changed");
    }
}

// Actual immutable document pins remain held through the consumer run. No ledger, profile, admission gate or key is read here.
internal sealed class CredentialBoundNamespaceMetadataLease : IDisposable
{
    readonly CredentialVault vault;
    readonly CredentialVaultNamespaceIdentity expected;
    readonly CredentialNamespaceMetadataPin[] pins;
    int disposed;
    internal CredentialBoundNamespaceMetadataLease(CredentialVault vault, CredentialVaultNamespaceIdentity expected,
        CredentialNamespaceMetadataPin[] pins) { this.vault = vault; this.expected = expected; this.pins = pins; }
    internal void AssertCurrent()
    {
        if (Volatile.Read(ref disposed) != 0) throw new CredentialException("credential-bound-namespace-metadata-disposed");
        foreach (var pin in pins) pin.AssertCurrent();
        vault.AssertBoundNamespaceMetadata(expected);
    }
    public void Dispose()
    {
        if (Interlocked.Exchange(ref disposed, 1) == 0)
            foreach (var pin in pins) pin.Dispose();
    }
}
