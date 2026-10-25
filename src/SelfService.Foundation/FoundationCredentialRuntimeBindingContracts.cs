namespace SelfService.Foundation;

// Public custody metadata only. Paths, credentials and key material are never part of this projection.
public sealed record FoundationCredentialRuntimeBinding(string Id, string CustomerId, string CellId,
    string DatabaseInstanceId, string DatabasePathSha256, CredentialVaultNamespaceIdentity Namespace,
    FoundationCellActivationAuthorityIdentity ActivationAuthority, long ActivationGeneration,
    string ActivationBindingSha256, DateTimeOffset CreatedAt, string Sha256)
{
    public string Schema => "platform-studio.foundation-credential-runtime-binding.v1";
    public bool RebindingAvailable => false;
    public bool RecoveryHoldReleased => false;
    public bool ExternalExecutionAuthorized => false;
}

public sealed record FoundationCredentialRuntimeBindingStatus(string CustomerId, string CellId, string DatabaseInstanceId,
    bool BindingRequired, FoundationCredentialRuntimeBinding? CurrentBinding, int HistoricalBindingCount)
{
    public string Schema => "platform-studio.foundation-credential-runtime-binding-status.v1";
    public bool AutomaticBindingAvailable => false;
    public bool RebindingAvailable => false;
    public bool RecoveryHoldReleased => false;
    public bool ExternalExecutionAuthorized => false;
}
