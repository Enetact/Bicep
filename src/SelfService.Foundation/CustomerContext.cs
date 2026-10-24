using System.Collections.Frozen;
using System.Text.RegularExpressions;

namespace SelfService.Foundation;

public sealed record MembershipGrant(string CustomerId, string CellId, string ActorKey, string ProjectId,
    string EnvironmentId, string[] Roles, long Version, bool Enabled,
    DateTimeOffset? ValidUntilUtc = null, string? Purpose = null, string? ApprovalId = null);

public interface ICustomerMembershipSource
{
    IReadOnlyList<MembershipGrant> Find(string customerId, string actorKey);
}

public sealed class CustomerContext
{
    internal CustomerContext(string customerId, string cellId, string actorKey, string projectId,
        string environmentId, string capability, long roleVersion, PrincipalKind kind, DateTimeOffset expiresUtc,
        DateTimeOffset? supportValidUntilUtc = null, string? supportPurpose = null, string? supportApprovalId = null)
    {
        CustomerId = customerId; CellId = cellId; ActorKey = actorKey; ProjectId = projectId;
        EnvironmentId = environmentId; Capability = capability; RoleVersion = roleVersion;
        Kind = kind; ExpiresUtc = expiresUtc;
        SupportValidUntilUtc = supportValidUntilUtc; SupportPurpose = supportPurpose; SupportApprovalId = supportApprovalId;
    }

    public string CustomerId { get; }
    public string CellId { get; }
    public string ActorKey { get; }
    public string ProjectId { get; }
    public string EnvironmentId { get; }
    public string Capability { get; }
    public long RoleVersion { get; }
    public PrincipalKind Kind { get; }
    public DateTimeOffset ExpiresUtc { get; }
    public DateTimeOffset? SupportValidUntilUtc { get; }
    public string? SupportPurpose { get; }
    public string? SupportApprovalId { get; }
}

public sealed class CustomerContextResolver
{
    readonly string customerId, cellId;
    readonly ICustomerMembershipSource memberships;
    readonly TimeProvider clock;
    static readonly Regex Identifier = new("\\A[a-zA-Z0-9][a-zA-Z0-9_.-]{0,127}\\z", RegexOptions.CultureInvariant);

    public CustomerContextResolver(CellIdentityOptions options, ICustomerMembershipSource memberships,
        TimeProvider? clock = null)
    {
        if (!ValidId(options.CustomerId) || !ValidId(options.CellId)) throw new ArgumentException("Invalid cell binding.");
        customerId = options.CustomerId; cellId = options.CellId;
        this.memberships = memberships ?? throw new ArgumentNullException(nameof(memberships));
        this.clock = clock ?? TimeProvider.System;
    }

    public CustomerContext Require(FoundationPrincipal principal, string requestedCustomerId,
        string projectId, string environmentId, string capability)
    {
        if (principal is null || principal.ExpiresUtc <= clock.GetUtcNow())
            throw new FoundationAuthorizationException("principal_expired", 401);
        // A caller-selected route/body/header cannot override the installation's customer binding.
        if (!ValidId(requestedCustomerId) || !string.Equals(customerId, requestedCustomerId, StringComparison.Ordinal) ||
            principal.ValidationCustomerId != customerId || principal.ValidationCellId != cellId ||
            !ValidId(projectId) || !ValidId(environmentId))
            throw new FoundationAuthorizationException("customer_scope");
        var found = memberships.Find(customerId, principal.ActorKey) ?? [];
        if (found.Any(g => g is null || g.CustomerId != customerId || g.CellId != cellId || g.ActorKey != principal.ActorKey))
            throw new FoundationAuthorizationException("membership_binding");
        var matching = found.Where(g => g.ProjectId == projectId && g.EnvironmentId == environmentId).ToArray();
        if (matching.Length != 1 || !matching[0].Enabled || matching[0].Version <= 0)
            throw new FoundationAuthorizationException("membership_required");
        var membership = matching[0];
        if (membership.Roles is null || membership.Roles.Length is 0 or > 16 ||
            membership.Roles.Any(r => r is null || !FoundationRoles.All.Contains(r)))
            throw new FoundationAuthorizationException("membership_roles");
        var support = membership.Roles.Contains(FoundationRoles.SupportDelegate, StringComparer.Ordinal);
        if (support && (principal.Kind != PrincipalKind.User || membership.Roles.Length != 1 ||
            membership.ValidUntilUtc is null || membership.ValidUntilUtc <= clock.GetUtcNow() || membership.ValidUntilUtc > clock.GetUtcNow().AddDays(1) ||
            string.IsNullOrWhiteSpace(membership.Purpose) || membership.Purpose.Length is < 8 or > 256 || membership.Purpose.Any(char.IsControl) ||
            !ValidId(membership.ApprovalId))) throw new FoundationAuthorizationException("support_grant_invalid");
        var persisted = membership.Roles.ToFrozenSet(StringComparer.Ordinal);
        var effective = principal.Kind == PrincipalKind.User
            ? persisted.Where(principal.Roles.Contains).ToArray()
            : persisted.ToArray(); // Workload.Execute + exact SP/client is checked by token validation.
        if (!AuthorizationPolicy.Allows(capability, effective, principal.Kind))
            throw new FoundationAuthorizationException("capability_denied");
        return new(customerId, cellId, principal.ActorKey, projectId, environmentId, capability,
            membership.Version, principal.Kind, support && membership.ValidUntilUtc < principal.ExpiresUtc ? membership.ValidUntilUtc.Value : principal.ExpiresUtc,
            support ? membership.ValidUntilUtc : null, support ? membership.Purpose : null, support ? membership.ApprovalId : null);
    }

    public static bool ValidId(string? value) => value is not null && Identifier.IsMatch(value);
}
