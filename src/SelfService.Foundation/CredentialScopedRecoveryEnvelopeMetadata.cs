using System.Text.Json;

namespace SelfService.Foundation;

public sealed partial class CredentialVault
{
    // Header projection only: its result is never evidence that the encrypted credential authenticated.
    internal static CredentialRecoveryProfileDependency InspectScopedRecoveryEnvelopeMetadata(ReadOnlyMemory<byte> bytes,
        string profileHash, CredentialVaultNamespaceIdentity expectedNamespace)
    {
        try
        {
            CredentialRecoverySets.RequireIdentity(expectedNamespace);
            using var document = CredentialRules.Document(bytes); var envelope = document.RootElement.Deserialize<Envelope>(CredentialRules.Json) ?? throw new JsonException();
            var profile = Detached(envelope.Profile);
            if (envelope.Schema != 1 || !CredentialRules.ProfileId(envelope.KeyId) || CredentialRules.Hash(profile.Scope.AuthProfileId) != profileHash ||
                profile.Scope.CustomerId != expectedNamespace.CustomerId || profile.Scope.CellId != expectedNamespace.CellId ||
                profile.Kind != (profile.Scope.Provider == PipelineRequestFactory.GitHub ? "github-installation-token" : "entra-access-token")) throw new JsonException();
            return RecoveryDependency(profile, envelope.KeyId, profileHash, bytes.Span);
        }
        catch (Exception error) when (error is JsonException or ArgumentException or NullReferenceException or InvalidOperationException)
        { throw CredentialScopedRecoverySets.Failure("contents-invalid"); }
    }
}
