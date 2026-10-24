using System.Collections.Frozen;

namespace SelfService.Foundation;

public enum PrincipalKind { User, Workload }

public sealed class FoundationAuthorizationException(string code, int statusCode = 403)
    : Exception("Authentication or customer authorization failed.")
{
    public string Code { get; } = code;
    public int StatusCode { get; } = statusCode;
}

public static class FoundationCapabilities
{
    public const string CatalogRead = "catalog.read";
    public const string EvidenceRead = "evidence.read";
    public const string RequestCreate = "request.create";
    public const string PlanApprove = "plan.approve";
    public const string JobDispatch = "job.dispatch";
    public const string JobCancel = "job.cancel";
    public const string JobReconcile = "job.reconcile";
    public const string AuditRead = "audit.read";
    public const string MembershipManage = "membership.manage";
    public const string ConnectionManage = "connection.manage";
    public const string CellHealthRead = "cell.health.read";
    public const string LifecycleManage = "lifecycle.manage";
    public const string LifecycleApprove = "lifecycle.approve";
    public const string LifecycleRead = "lifecycle.read";
    public const string LifecycleExport = "lifecycle.export";
}

public static class FoundationRoles
{
    public const string Viewer = "Viewer";
    public const string Requester = "Requester";
    public const string Approver = "Approver";
    public const string Operator = "Operator";
    public const string CustomerAdmin = "CustomerAdmin";
    public const string Auditor = "Auditor";
    public const string PlatformOperator = "PlatformOperator";
    public const string SupportDelegate = "SupportDelegate";

    public static readonly IReadOnlySet<string> All = new[] { Viewer, Requester, Approver, Operator,
        CustomerAdmin, Auditor, PlatformOperator, SupportDelegate }.ToFrozenSet(StringComparer.Ordinal);
}

public static class AuthorizationPolicy
{
    static readonly FrozenDictionary<string, FrozenSet<string>> RolesByCapability =
        new Dictionary<string, FrozenSet<string>>(StringComparer.Ordinal)
        {
            [FoundationCapabilities.CatalogRead] = Set(FoundationRoles.Viewer, FoundationRoles.Requester,
                FoundationRoles.Approver, FoundationRoles.Operator, FoundationRoles.CustomerAdmin,
                FoundationRoles.Auditor, FoundationRoles.SupportDelegate),
            [FoundationCapabilities.EvidenceRead] = Set(FoundationRoles.Viewer, FoundationRoles.Requester,
                FoundationRoles.Approver, FoundationRoles.Operator, FoundationRoles.Auditor, FoundationRoles.SupportDelegate),
            [FoundationCapabilities.RequestCreate] = Set(FoundationRoles.Requester),
            [FoundationCapabilities.PlanApprove] = Set(FoundationRoles.Approver),
            [FoundationCapabilities.JobDispatch] = Set(FoundationRoles.Operator),
            [FoundationCapabilities.JobCancel] = Set(FoundationRoles.Operator),
            [FoundationCapabilities.JobReconcile] = Set(FoundationRoles.Operator),
            [FoundationCapabilities.AuditRead] = Set(FoundationRoles.Auditor),
            [FoundationCapabilities.MembershipManage] = Set(FoundationRoles.CustomerAdmin),
            [FoundationCapabilities.ConnectionManage] = Set(FoundationRoles.CustomerAdmin),
            [FoundationCapabilities.CellHealthRead] = Set(FoundationRoles.PlatformOperator)
            ,[FoundationCapabilities.LifecycleManage] = Set(FoundationRoles.CustomerAdmin)
            ,[FoundationCapabilities.LifecycleApprove] = Set(FoundationRoles.Approver)
            ,[FoundationCapabilities.LifecycleRead] = Set(FoundationRoles.CustomerAdmin, FoundationRoles.Approver, FoundationRoles.Auditor)
            ,[FoundationCapabilities.LifecycleExport] = Set(FoundationRoles.Auditor)
        }.ToFrozenDictionary(StringComparer.Ordinal);

    static readonly FrozenSet<string> WorkloadCapabilities = Set(FoundationCapabilities.JobDispatch,
        FoundationCapabilities.JobCancel, FoundationCapabilities.JobReconcile, FoundationCapabilities.CellHealthRead);

    static FrozenSet<string> Set(params string[] values) => values.ToFrozenSet(StringComparer.Ordinal);

    public static bool Allows(string capability, IEnumerable<string> grantedRoles, PrincipalKind kind)
    {
        if (string.IsNullOrWhiteSpace(capability) || grantedRoles is null || !Enum.IsDefined(kind) ||
            !RolesByCapability.TryGetValue(capability, out var allowed) ||
            kind == PrincipalKind.Workload && !WorkloadCapabilities.Contains(capability)) return false;
        return grantedRoles.Any(allowed.Contains);
    }

    public static void RequireSeparation(CustomerContext approver, string requesterActorKey)
    {
        if (approver.Kind != PrincipalKind.User || approver.Capability != FoundationCapabilities.PlanApprove ||
            string.Equals(approver.ActorKey, requesterActorKey, StringComparison.Ordinal))
            throw new FoundationAuthorizationException("approval_separation");
    }
}
