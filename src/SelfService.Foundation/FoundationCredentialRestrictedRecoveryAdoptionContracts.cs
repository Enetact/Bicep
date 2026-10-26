namespace SelfService.Foundation;

// Commitments to trusted-owner selections, not namespace identities, reservations or execution grants.
public sealed record FoundationCredentialRestrictedRecoveryAdoptionSelection(string VaultRootCommitmentSha256,
    string RollbackRootCommitmentSha256, FoundationCellActivationAuthorityIdentity ActivationAuthority,
    long ActivationGeneration, string? ActivationBindingSha256)
{
    public string Schema => "platform-studio.credential-restricted-recovery-adoption-selection.v1";
    public string AccessProfile => "FoundationBoundDispatchOnly";
    public string Operation => "ProvisionFreshRestrictedNamespacesForRestoredInstance";
    public bool FreshDestinationReservationPerformed => false;
}

public sealed class FoundationCredentialRestrictedRecoveryAdoptionTarget
{
    internal FoundationCredentialRestrictedRecoveryAdoptionTarget(FoundationCredentialRestrictedRecoveryJointTarget recoveryTarget,
        string databaseInstanceId, string databasePathSha256, FoundationRecoveryExecutionHold recoveryHold, bool runtimeBindingRequired,
        string runtimeBindingHistorySha256, int runtimeBindingHistoryCount, string? currentRuntimeBindingSha256,
        FoundationCredentialRuntimeBinding sourceRuntimeBinding, FoundationCredentialRestrictedRecoveryAdoptionSelection selection)
    {
        RecoveryTarget = recoveryTarget; DatabaseInstanceId = databaseInstanceId; DatabasePathSha256 = databasePathSha256;
        RecoveryHold = recoveryHold; RuntimeBindingRequired = runtimeBindingRequired; RuntimeBindingHistorySha256 = runtimeBindingHistorySha256;
        RuntimeBindingHistoryCount = runtimeBindingHistoryCount; CurrentRuntimeBindingSha256 = currentRuntimeBindingSha256;
        SourceRuntimeBinding = sourceRuntimeBinding; Selection = selection;
    }
    public string Schema => "platform-studio.credential-restricted-recovery-adoption-target.v1";
    public string AccessProfile => "FoundationBoundDispatchOnly";
    public string Operation => "ProvisionFreshRestrictedNamespacesForRestoredInstance";
    public FoundationCredentialRestrictedRecoveryJointTarget RecoveryTarget { get; }
    public string DatabaseInstanceId { get; }
    public string DatabasePathSha256 { get; }
    public FoundationRecoveryExecutionHold RecoveryHold { get; }
    public bool RuntimeBindingRequired { get; }
    public string RuntimeBindingHistorySha256 { get; }
    public int RuntimeBindingHistoryCount { get; }
    public string? CurrentRuntimeBindingSha256 { get; }
    public FoundationCredentialRuntimeBinding SourceRuntimeBinding { get; }
    public FoundationCredentialRestrictedRecoveryAdoptionSelection Selection { get; }
}

public sealed record FoundationCredentialRestrictedRecoveryAdoptionApproval(string ApproverKey, long ApproverVersion,
    DateTimeOffset ApproverExpiresAt, string IntentDigest, string StageVerificationSha256,
    DateTimeOffset VerificationObservedUtc, DateTimeOffset ApprovedAt)
{
    public string Schema => "platform-studio.credential-restricted-recovery-adoption-approval.v1";
    public string AccessProfile => "FoundationBoundDispatchOnly";
    public bool ExecutionAdmissionAvailable => false;
    public bool AdoptionAuthorized => false;
}

public sealed record FoundationCredentialRestrictedRecoveryAdoptionIntent(string Id, string CustomerId, string CellId,
    FoundationCredentialRestrictedRecoveryAdoptionTarget Target, string TargetSha256, string RequesterKey, long RequesterVersion,
    DateTimeOffset RequesterExpiresAt, long PolicyVersion, string DatabaseInstanceId, string PurposeSha256,
    DateTimeOffset ExpiresAt, DateTimeOffset PreparedAt, string PreparedVerificationSha256, DateTimeOffset PreparedVerificationObservedUtc,
    string Digest, string State, FoundationCredentialRestrictedRecoveryAdoptionApproval? Approval)
{
    public string Schema => "platform-studio.credential-restricted-recovery-adoption-intent.v1";
    public string AccessProfile => "FoundationBoundDispatchOnly";
    public string Operation => "ProvisionFreshRestrictedNamespacesForRestoredInstance";
    public bool Quarantined => State == "Quarantined";
    public bool ExecutionAdmissionAvailable => false;
    public bool AdoptionAuthorized => false;
    public bool ActivationPerformed => false;
    public bool RecoveryHoldReleased => false;
    public bool FreshDestinationReservationPerformed => false;
    public bool SourceHostFenced => false;
    public bool ExternalExecutionAuthorized => false;
    public bool IndependentKeyBackupVerified => false;
}
