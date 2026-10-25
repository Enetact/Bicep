using SelfService.Foundation;

namespace SelfService.Foundation.Tests;

// Fixture setup uses the same concrete finite-scope run as the supported Host.
// SQL-only preparation must never resolve a credential key or construct an HTTP handler.
internal sealed class BoundFixtureAdmission : IDisposable
{
    readonly FoundationCredentialBoundPipelineConsumer consumer;
    internal FoundationCredentialBoundPipelineRun Run { get; }

    internal BoundFixtureAdmission(FoundationStore store, CredentialVault vault, FoundationCellActivationAuthority authority,
        CredentialScope scope, TimeProvider? clock = null)
    {
        var binding = store.GetCredentialRuntimeBindingStatus().CurrentBinding
            ?? throw new InvalidOperationException("A genuine current binding is required for bound fixture admission.");
        consumer = FoundationCredentialBoundPipelineConsumer.ForMockHandlers(vault, authority, binding.Namespace,
            authority.DescribeIdentity(), binding.DatabaseInstanceId, binding.Sha256, [scope],
            () => throw new InvalidOperationException("SQL fixture admission must not create an HTTP handler."), clock);
        try { Run = consumer.OpenRun(store); }
        catch { consumer.Dispose(); throw; }
    }

    public void Dispose() { Run.Dispose(); consumer.Dispose(); }
}
