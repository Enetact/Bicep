using System.Text;
using System.Text.Json;

namespace SelfService.Foundation;

// Maps one bounded actual stored connection. This parser supplies target metadata, never current authorization.
public static class FoundationPipelineConnection
{
    sealed record StoredConnection(string Provider, string Organization, string? Project, string Repository,
        string PipelineOrWorkflowId, string[] AllowedRefs, string[] AllowedEnvironments, string[] AllowedInputKeys,
        string AuthProfileId, bool EnforcesSourceRevision = false, int? PipelineVersion = null, bool Enabled = true);

    public static PipelineConnection FromStored(FoundationConnection saved, string cellId)
    {
        try
        {
            if (saved is null || saved.ConfigurationJson is null || Encoding.UTF8.GetByteCount(saved.ConfigurationJson) > 65536)
                throw new JsonException();
            using var document = CredentialRules.Document(Encoding.UTF8.GetBytes(saved.ConfigurationJson));
            var parsed = document.RootElement.Deserialize<StoredConnection>(CredentialRecoverySets.Json) ?? throw new JsonException();
            if (parsed.Provider != saved.Provider) throw new JsonException();
            var connection = new PipelineConnection(saved.CustomerId, cellId, saved.Id, saved.Provider, parsed.Organization, parsed.Project,
                parsed.Repository, parsed.PipelineOrWorkflowId, parsed.AllowedRefs, parsed.AllowedEnvironments, parsed.AllowedInputKeys,
                parsed.AuthProfileId, saved.Generation)
            { EnforcesSourceRevision = parsed.EnforcesSourceRevision, PipelineVersion = parsed.PipelineVersion };
            _ = PipelineRequestFactory.ConnectionDigest(connection);
            return connection with { AllowedRefs = connection.AllowedRefs.ToArray(), AllowedEnvironments = connection.AllowedEnvironments.ToArray(),
                AllowedInputKeys = connection.AllowedInputKeys.ToArray() };
        }
        catch (Exception error) when (error is JsonException or InvalidOperationException or ArgumentException or NullReferenceException or PipelineContractException)
        { throw new FoundationStoreException("StoredPipelineConnectionInvalid", 403); }
    }
}
