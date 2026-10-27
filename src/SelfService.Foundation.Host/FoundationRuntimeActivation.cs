using SelfService.Foundation;

namespace SelfService.Foundation.Host;

// DI requests shutdown; active owned runner uses retain the actual lease until they finish.
// No user/model route provisions or switches authority.
public sealed class FoundationRuntimeActivation : IDisposable
{
    internal FoundationExecutionActivation Execution { get; }
    public FoundationRuntimeActivation(FoundationHostOptions options)
    {
        Execution = new FoundationExecutionActivation(options.RequireActivationAuthority, options.ActivationAuthorityDirectory,
            options.Identity.CustomerId, options.Identity.CellId, options.DatabasePath);
    }
    public void AssertCurrent(string databasePath, string instanceId) => Execution.AssertCurrent(databasePath, instanceId);

    // This retains the already-acquired concrete host lease, not caller-supplied authority.
    // No lifetime monitor is held while the lease performs SQLite/metadata checks or disposal.
    internal IDisposable RetainUse() => Execution.RetainUse();
    public void Dispose() => Execution.Dispose();
}
