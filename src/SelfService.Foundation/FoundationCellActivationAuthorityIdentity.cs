using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;

namespace SelfService.Foundation;

// Public commitments contain neither the protected directory path nor its owner SID.
public sealed record FoundationCellActivationAuthorityIdentity(string CustomerId, string CellId, string AuthorityId,
    string CustodyCommitmentSha256);

public sealed partial class FoundationCellActivationAuthority
{
    public FoundationCellActivationAuthorityIdentity DescribeIdentity()
    {
        using var guard = OpenGuard(exclusive: false);
        return ReadHeldIdentity();
    }

    // Only concrete activation-held composition uses this non-reentrant read. The v1 on-disk marker is unchanged.
    internal FoundationCellActivationAuthorityIdentity ReadHeldIdentity() => ReadHeldIdentityCore(null);
    internal FoundationCellActivationAuthorityIdentity ReadHeldIdentityAt(DateTimeOffset observedUtc) => ReadHeldIdentityCore(observedUtc);
    FoundationCellActivationAuthorityIdentity ReadHeldIdentityCore(DateTimeOffset? observedUtc)
    {
        if (!OperatingSystem.IsWindows()) throw Failure("ActivationCustodyWindowsRequired", 403);
        try
        {
            if (observedUtc is { } at) { if (ReadMarkerAt(at).AuthorityId != authorityId) throw Failure("ActivationAuthorityChanged", 403); }
            else CheckAuthority();
            ValidateProtectedDirectory(root);
            var marker = observedUtc is { } observed ? ReadMarkerAt(observed) : ReadMarker();
            var owner = new DirectoryInfo(root).GetAccessControl().GetOwner(typeof(SecurityIdentifier)) as SecurityIdentifier
                ?? throw Failure("ActivationAuthorityAccessDenied", 403);
            var digest = Hash(JsonSerializer.SerializeToUtf8Bytes(new
            {
                schema = "platform-studio.activation-authority-custody.v1", rootPath = root, ownerSid = owner.Value,
                marker.CustomerId, marker.CellId, marker.AuthorityId, markerSha256 = marker.Sha256
            }, Json));
            return new(customerId, cellId, authorityId, digest);
        }
        catch (Exception error) when (error is IOException or UnauthorizedAccessException)
        { throw Failure("ActivationCustodyUnavailable", 403); }
    }
}
