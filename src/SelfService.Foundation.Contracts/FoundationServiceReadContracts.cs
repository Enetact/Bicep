namespace SelfService.Foundation.Contracts;

// Wire metadata identifies the configured service cell and contract; it grants no authority.
public static class FoundationServiceProtocol
{
    public const string CellHeader = "X-Platform-Foundation-Cell";
    public const string ContractHeader = "X-Platform-Foundation-Contract";
    public const string ContractVersion = "1";
}

public sealed record FoundationServiceScope(string CustomerId, string CellId, string ProjectId, string EnvironmentId);

// These records describe the existing read responses. They are not trusted CustomerContext,
// approval, dispatch, custody or credential-use capabilities.
public sealed record FoundationJobReadResult(string Id, string CustomerId, string CellId, string PlanId,
    string ProjectId, string EnvironmentId, string OperatorKey, long OperatorVersion, string State,
    string? WorkerId, DateTimeOffset? LeaseUntil, long Fence, int Attempt, string? ReceiptJson, string? Reason,
    DateTimeOffset CreatedAt, DateTimeOffset UpdatedAt);

public sealed record FoundationArtifactReadResult(string Id, string CustomerId, string ProjectId,
    string EnvironmentId, string Name, string Sha256, byte[] Content, DateTimeOffset CreatedAt);

public sealed record FoundationArtifactReadMetadata(string Id, string CustomerId, string CellId, string ProjectId,
    string EnvironmentId, string Name, string Sha256, int Bytes, DateTimeOffset CreatedAt);
