using SelfService.Foundation;

namespace SelfService.Foundation.Host;

// DI owns this lease for the host lifetime. No user/model route provisions or switches authority.
public sealed class FoundationRuntimeActivation : IDisposable
{
    readonly FoundationCellActivationLease? lease;
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
    public void Dispose() => lease?.Dispose();
}
