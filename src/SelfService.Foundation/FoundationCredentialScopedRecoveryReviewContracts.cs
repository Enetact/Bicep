namespace SelfService.Foundation;



public sealed class FoundationCredentialScopedRecoveryStageTarget
{
    internal FoundationCredentialScopedRecoveryStageTarget(string planSha256, string receiptSha256, string sourceSetSha256, string sourceInventorySha256,
        CredentialVaultNamespaceIdentity sourceNamespace, string rootCommitment, CredentialRecoveryDestinationKeyIdentity destinationKey,
        string stageId, string vaultId, string authorityId, string bindingSha256, string stateSha256, string completionCoreSha256,
        int profileCount, int absenceProofCount, IEnumerable<FoundationCredentialRecoveryScope> scopes)
    {
        PlanSha256 = planSha256; ReceiptSha256 = receiptSha256; SourceSetSha256 = sourceSetSha256; SourceInventorySha256 = sourceInventorySha256;
        SourceNamespace = sourceNamespace; DestinationRootCommitmentSha256 = rootCommitment; DestinationKey = destinationKey; StageId = stageId;
        StagedVaultNamespaceId = vaultId; StagedAuthorityNamespaceId = authorityId; BindingSha256 = bindingSha256; StateSha256 = stateSha256;
        CompletionCoreSha256 = completionCoreSha256; ProfileCount = profileCount; AbsenceProofCount = absenceProofCount;
        Scopes = Array.AsReadOnly(scopes.ToArray());
    }
    public string Schema => "platform-studio.credential-scoped-recovery-stage-review-target.v1";
    public string PlanSha256 { get; }
    public string ReceiptSha256 { get; }
    public string SourceSetSha256 { get; }
    public string SourceInventorySha256 { get; }
    public CredentialVaultNamespaceIdentity SourceNamespace { get; }
    public string DestinationRootCommitmentSha256 { get; }
    public CredentialRecoveryDestinationKeyIdentity DestinationKey { get; }
    public string StageId { get; }
    public string StagedVaultNamespaceId { get; }
    public string StagedAuthorityNamespaceId { get; }
    public string BindingSha256 { get; }
    public string StateSha256 { get; }
    public string CompletionCoreSha256 { get; }
    public int ProfileCount { get; }
    public int AbsenceProofCount { get; }
    public IReadOnlyList<FoundationCredentialRecoveryScope> Scopes { get; }
}

public sealed record FoundationCredentialScopedRecoveryStageReviewResult(string RequestDigest, string TargetSha256,
    string StageVerificationSha256, string PlanSha256, string ReceiptSha256, string SourceSetSha256, DateTimeOffset ObservedAt)
{
    public string Schema => "platform-studio.credential-scoped-recovery-stage-review-result.v1";
    public string Status => "HeldStageVerified";
    public bool InactiveRecoveryHold => true;
    public bool PromotionAuthorized => false;
    public bool LiveAdoptionAuthorized => false;
    public bool ActivationPerformed => false;
    public bool ExternalExecutionAuthorized => false;
    public string IndependentBackupCoverage => "NotAssessed";
}
public sealed record FoundationCredentialScopedRecoveryStageReviewExecution(string AdmissionId, string ExecutorKey, long ExecutorVersion,
    string State, string Reason, FoundationCredentialScopedRecoveryStageReviewResult? Result, string? ResultSha256,
    DateTimeOffset AdmittedAt, DateTimeOffset? CompletedAt);
public sealed record FoundationCredentialScopedRecoveryStageReview(string Id, string CustomerId, string CellId, string ProjectId, string EnvironmentId,
    FoundationCredentialScopedRecoveryStageTarget Target, string TargetSha256, string RequesterKey, long RequesterVersion, DateTimeOffset RequesterExpiresAt,
    long PolicyVersion, string DatabaseInstanceId, string PurposeSha256, DateTimeOffset ExpiresAt, DateTimeOffset PreparedAt, string Digest, string State,
    string? ApproverKey, long? ApproverVersion, DateTimeOffset? ApproverExpiresAt, DateTimeOffset? ApprovedAt,
    FoundationCredentialScopedRecoveryStageReviewExecution? Execution);

internal sealed record CredentialScopedRecoveryStageReviewAdmission(FoundationCredentialScopedRecoveryStageReview Review, string AdmissionId,
    string ExecutorKey, long ExecutorVersion, string DatabaseInstanceId);
