namespace SelfService.Foundation;

// A current, independently reviewed observation never resolves the original execution's historical outcome.
public sealed record FoundationCredentialMaintenanceObservationReceipt(string Id, string RecorderKey,
    long RecorderVersion, string Status, string RequestDigest, string ObservationSha256,
    DateTimeOffset ObservedAt, string Digest);

public sealed record FoundationCredentialMaintenanceObservationRequest(string Id, string CustomerId, string CellId,
    string ProjectId, string EnvironmentId, string OriginalIntentId, string OriginalIntentDigest,
    string OriginalAdmissionId, string ExpectedAfterSha256, FoundationCredentialMaintenanceTarget Observation,
    string ObservationSha256, string RequesterKey, long RequesterVersion, long PolicyVersion,
    string DatabaseInstanceId, string PurposeSha256, DateTimeOffset ExpiresAt, string Digest, string State,
    string? ApproverKey, long? ApproverVersion, FoundationCredentialMaintenanceObservationReceipt? Receipt);
