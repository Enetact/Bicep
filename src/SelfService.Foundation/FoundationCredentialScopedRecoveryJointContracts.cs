namespace SelfService.Foundation;

public sealed class FoundationCredentialScopedRecoveryJointTarget
{
    internal FoundationCredentialScopedRecoveryJointTarget(CredentialScopedRecoverySetCheckpointAuthorityIdentity identity,
        CredentialScopedRecoverySetCheckpoint checkpoint, FoundationCredentialScopedRecoveryStageTarget stageTarget)
    { CheckpointAuthority = identity; Checkpoint = checkpoint; StageTarget = stageTarget; }
    public string Schema => "platform-studio.credential-scoped-recovery-joint-target.v1";
    public CredentialScopedRecoverySetCheckpointAuthorityIdentity CheckpointAuthority { get; }
    public CredentialScopedRecoverySetCheckpoint Checkpoint { get; }
    public FoundationCredentialScopedRecoveryStageTarget StageTarget { get; }
}

public sealed record FoundationCredentialScopedRecoveryJointResult(string ReviewDigest, string TargetSha256,
    string StageVerificationSha256, string CheckpointAuthorityCommitmentSha256, long CheckpointSequence, string CheckpointSha256,
    string PlanSha256, string ReceiptSha256, string SourceSetSha256, DateTimeOffset ObservedAt)
{
    public string Schema => "platform-studio.credential-scoped-recovery-joint-result.v1";
    public string Status => "CurrentCheckpointHeldStageVerified";
    public bool CheckpointReadLeaseRetainedDuringVerification => true;
    public bool InactiveRecoveryHold => true;
    public bool PromotionAuthorized => false;
    public bool LiveAdoptionAuthorized => false;
    public bool ActivationPerformed => false;
    public bool ExternalExecutionAuthorized => false;
    public bool IndependentKeyBackupVerified => false;
    public string IndependentBackupCoverage => "NotAssessed";
}

public sealed record FoundationCredentialScopedRecoveryJointVerification(string ReviewId, string ReviewDigest, string CustomerId, string CellId,
    FoundationCredentialScopedRecoveryJointTarget Target, string TargetSha256, string Digest, string AdmissionId,
    string ExecutorKey, long ExecutorVersion, string DatabaseInstanceId, string State, string Reason,
    FoundationCredentialScopedRecoveryJointResult? Result, string? ResultSha256, DateTimeOffset AdmittedAt, DateTimeOffset? CompletedAt,
    string ReviewState)
{
    public string Schema => "platform-studio.credential-scoped-recovery-joint-verification.v1";
    public bool Quarantined => ReviewState == "Quarantined";
}

internal sealed record CredentialScopedRecoveryJointAdmission(CredentialScopedRecoveryStageReviewAdmission ReviewAdmission,
    FoundationCredentialScopedRecoveryJointTarget Target, string TargetSha256, string Digest)
{
    internal string DatabaseInstanceId => ReviewAdmission.DatabaseInstanceId;
    internal string AdmissionId => ReviewAdmission.AdmissionId;
}
