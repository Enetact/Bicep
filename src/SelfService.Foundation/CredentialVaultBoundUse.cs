namespace SelfService.Foundation;

public sealed partial class CredentialVault
{
    // This is not a public lease/callback seam. Concrete Foundation composition holds activation first;
    // the ordinary dispatch reader may already hold this vault's admission gate when it invokes this check.
    internal void AssertBoundNamespace(CredentialVaultNamespaceIdentity expected)
    {
        _ = rollback?.ReadState();
        if (NamespaceIdentity() != expected) throw new CredentialException("credential-bound-namespace-changed");
    }

    internal IDisposable HoldBoundNamespace(CredentialVaultNamespaceIdentity expected)
    {
        var vaultGuard = custody.OpenGuard();
        FileStream? authorityGuard = null;
        try
        {
            authorityGuard = rollback?.OpenGuard();
            AssertBoundNamespace(expected);
            return new BoundNamespaceGuards(vaultGuard, authorityGuard);
        }
        catch { authorityGuard?.Dispose(); vaultGuard.Dispose(); throw; }
    }

    sealed class BoundNamespaceGuards(FileStream vaultGuard, FileStream? authorityGuard) : IDisposable
    {
        public void Dispose() { authorityGuard?.Dispose(); vaultGuard.Dispose(); }
    }
}
