namespace SelfService.Foundation;

public sealed class FoundationCredentialRestrictedRecoveryJointTarget
{
    internal FoundationCredentialRestrictedRecoveryJointTarget(CredentialRestrictedRecoverySetCheckpointAuthorityIdentity identity,
        CredentialRestrictedRecoverySetCheckpoint checkpoint, FoundationCredentialRestrictedRecoveryStageTarget stageTarget)
    { CheckpointAuthority = identity; Checkpoint = checkpoint; StageTarget = stageTarget; }
    public string Schema => "platform-studio.credential-restricted-recovery-joint-target.v1";
    public string AccessProfile => "FoundationBoundDispatchOnly";
    public CredentialRestrictedRecoverySetCheckpointAuthorityIdentity CheckpointAuthority { get; }
    public CredentialRestrictedRecoverySetCheckpoint Checkpoint { get; }
    public FoundationCredentialRestrictedRecoveryStageTarget StageTarget { get; }
}

public sealed record FoundationCredentialRestrictedRecoveryJointResult(string ReviewDigest, string TargetSha256,
    string StageVerificationSha256, string CheckpointAuthorityCommitmentSha256, long CheckpointSequence, string CheckpointSha256,
    string PlanSha256, string ReceiptSha256, string SourceSetSha256, string CompletionProofSha256, DateTimeOffset ObservedAt)
{
    public string Schema => "platform-studio.credential-restricted-recovery-joint-result.v1";
    public string AccessProfile => "FoundationBoundDispatchOnly";
    public string Status => "CurrentCheckpointHeldRestrictedStageVerified";
    public bool DestinationCompletionProofAuthenticated => true;
    public bool CheckpointReadLeaseRetainedDuringVerification => true;
    public bool InactiveRecoveryHold => true;
    public bool PromotionAuthorized => false;
    public bool LiveAdoptionAuthorized => false;
    public bool ActivationPerformed => false;
    public bool ExternalExecutionAuthorized => false;
    public bool IndependentKeyBackupVerified => false;
    public string IndependentBackupCoverage => "NotAssessed";
}

public sealed record FoundationCredentialRestrictedRecoveryJointVerification(string ReviewId, string ReviewDigest, string CustomerId, string CellId,
    FoundationCredentialRestrictedRecoveryJointTarget Target, string TargetSha256, string Digest, string AdmissionId,
    string ExecutorKey, long ExecutorVersion, string DatabaseInstanceId, string State, string Reason,
    FoundationCredentialRestrictedRecoveryJointResult? Result, string? ResultSha256, DateTimeOffset AdmittedAt, DateTimeOffset? CompletedAt,
    string ReviewState)
{
    public string Schema => "platform-studio.credential-restricted-recovery-joint-verification.v1";
    public string AccessProfile => "FoundationBoundDispatchOnly";
    public bool Quarantined => ReviewState == "Quarantined";
}

internal sealed record CredentialRestrictedRecoveryJointAdmission(CredentialRestrictedRecoveryStageReviewAdmission ReviewAdmission,
    FoundationCredentialRestrictedRecoveryJointTarget Target, string TargetSha256, string Digest)
{
    internal string DatabaseInstanceId => ReviewAdmission.DatabaseInstanceId;
    internal string AdmissionId => ReviewAdmission.AdmissionId;
}
