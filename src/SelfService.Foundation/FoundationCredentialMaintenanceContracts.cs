namespace SelfService.Foundation;

public enum CredentialMaintenanceOperation { Revoke, Rewrap }
public sealed record FoundationCredentialMaintenanceTarget(string ProfileScopeJson, string ProfileScopeSha256,
    string ProfileIdSha256, string VaultNamespaceId, string NamespaceCommitmentSha256, long ProfileGeneration,
    bool Enabled, long ExpiresUnixMilliseconds, string EnvelopeSha256, long EnvelopeLength, string SourceKeyId,
    string DestinationKeyId, string? AuthorityNamespaceId, string? AuthorityBindingSha256, long AuthorityRevision, string? AuthorityStateSha256);
public sealed record FoundationCredentialMaintenanceResult(CredentialMaintenanceOperation Operation, string TargetSha256,
    long BeforeGeneration, long AfterGeneration, string BeforeEnvelopeSha256, string AfterEnvelopeSha256,
    string NamespaceCommitmentSha256, long AuthorityRevision, string? AuthorityStateSha256);
public sealed record FoundationCredentialMaintenanceExecution(string AdmissionId, string ExecutorKey, long ExecutorVersion,
    string State, string Reason, string ExpectedAfterSha256, FoundationCredentialMaintenanceResult? Result,
    string? ResultSha256, DateTimeOffset AdmittedAt, DateTimeOffset? CompletedAt);
public sealed record FoundationCredentialMaintenanceIntent(string Id, string CustomerId, string CellId, string ProjectId,
    string EnvironmentId, CredentialMaintenanceOperation Operation, FoundationCredentialMaintenanceTarget Target, string TargetSha256,
    string RequesterKey, long RequesterVersion, string ConnectionId, long ConnectionGeneration, string ConnectionSha256,
    long PolicyVersion, string DatabaseInstanceId, string PurposeSha256, DateTimeOffset ExpiresAt, string Digest, string State,
    string? ApproverKey, long? ApproverVersion, FoundationCredentialMaintenanceExecution? Execution);

internal sealed record CredentialMaintenanceAdmission(FoundationCredentialMaintenanceIntent Intent, string AdmissionId,
    string ExecutorKey, long ExecutorVersion, string DatabaseInstanceId, string ExpectedAfterSha256);
