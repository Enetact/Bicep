using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace SelfService.Foundation;

public enum MachineDuty
{
    ApiRuntime, JobWorker, AuditAppender, DatabaseMigrator, DiscoveryConnector,
    AzureDeployment, Bootstrap, AzureDevOpsIntegration, GitHubIntegration, ModelBridge, ReleaseSigner
}

public enum MachineIdentityMode { ManagedIdentity, WorkloadIdentityFederation, GitHubApp, CustomerDelegated, LocalSynthetic }
public enum PermissionDomain { PlatformCapability, AzureAction, AzureDataAction, AzureDevOpsPermission, GitHubPermission }
public enum PermissionApproval { ReadPolicy, ExactHumanPlan, PrivilegedBootstrap }
public sealed record ProposedPermission(PermissionDomain Domain, string Name);

// Untrusted proposal input. It contains references/permissions, never credentials or an authorization result.
public sealed record IdentityPermissionManifest(string Version, string Adapter, string Operation,
    string CustomerId, string CellId, string ProjectId, string EnvironmentId, MachineDuty Duty,
    MachineIdentityMode IdentityMode, string ServiceAudience, string[] ExactScopes, ProposedPermission[] Permissions,
    string Rationale, bool RequiresPrivilegedBootstrap, PermissionApproval Approval,
    string[] ValidationTests, string[] RevocationTests);

// A reviewed, server-owned policy for ONE adapter operation, not a model-selected permission profile.
public sealed record IdentityPermissionPolicy(string Version, string Adapter, string Operation, MachineDuty Duty,
    MachineIdentityMode[] AllowedIdentityModes, string ServiceAudience, string[] ExactScopes,
    ProposedPermission[] ExactPermissions, bool RequiresPrivilegedBootstrap, PermissionApproval Approval);

public sealed class IdentityPermissionProposal
{
    internal IdentityPermissionProposal(string canonicalJson, string policyVersion)
    {
        CanonicalJson = canonicalJson; PolicyVersion = policyVersion;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(canonicalJson)));
    }
    public string CanonicalJson { get; }
    public string Sha256 { get; }
    public string PolicyVersion { get; }
    public bool ExecutionAuthorized => false;
    public bool IdentityGrantAuthorized => false;
    public bool ProviderPermissionsVerified => false;
}

public static class IdentityPermissionManifestValidator
{
    public static IdentityPermissionProposal Validate(IdentityPermissionManifest manifest,
        IdentityPermissionPolicy policy, CustomerContext context)
    {
        if (manifest is null || policy is null || context is null) throw Denied();
        if (!Id(manifest.Version) || !Id(policy.Version) || manifest.Version != policy.Version || !Id(manifest.Adapter) || !Id(manifest.Operation) ||
            manifest.Adapter != policy.Adapter || manifest.Operation != policy.Operation || manifest.Duty != policy.Duty ||
            manifest.CustomerId != context.CustomerId || manifest.CellId != context.CellId ||
            manifest.ProjectId != context.ProjectId || manifest.EnvironmentId != context.EnvironmentId ||
            !Enum.IsDefined(manifest.Duty) || !Enum.IsDefined(manifest.IdentityMode) || !Enum.IsDefined(manifest.Approval) ||
            policy.AllowedIdentityModes is null || !policy.AllowedIdentityModes.Contains(manifest.IdentityMode) ||
            !Text(manifest.ServiceAudience, 512) || manifest.ServiceAudience != policy.ServiceAudience ||
            manifest.RequiresPrivilegedBootstrap != policy.RequiresPrivilegedBootstrap || manifest.Approval != policy.Approval ||
            !Text(manifest.Rationale, 2000) || !TestNames(manifest.ValidationTests) || !TestNames(manifest.RevocationTests)) throw Denied();

        if (!Scopes(manifest.ExactScopes) || !Scopes(policy.ExactScopes) ||
            !manifest.ExactScopes.Order(StringComparer.Ordinal).SequenceEqual(policy.ExactScopes.Order(StringComparer.Ordinal), StringComparer.Ordinal) ||
            !Permissions(manifest.Permissions) || !Permissions(policy.ExactPermissions) ||
            !manifest.Permissions.OrderBy(Key, StringComparer.Ordinal).SequenceEqual(policy.ExactPermissions.OrderBy(Key, StringComparer.Ordinal))) throw Denied();

        var external = manifest.Permissions.Any(p => p.Domain != PermissionDomain.PlatformCapability);
        if (external && manifest.IdentityMode == MachineIdentityMode.LocalSynthetic ||
            (manifest.Duty == MachineDuty.Bootstrap) != manifest.RequiresPrivilegedBootstrap ||
            manifest.RequiresPrivilegedBootstrap && manifest.Approval != PermissionApproval.PrivilegedBootstrap ||
            !DutyMatches(manifest)) throw Denied();

        // Stable, detached snapshot: later mutation of input arrays cannot change a reviewed proposal/hash.
        var canonical = JsonSerializer.Serialize(manifest with
        {
            ExactScopes = manifest.ExactScopes.Order(StringComparer.Ordinal).ToArray(),
            Permissions = manifest.Permissions.OrderBy(Key, StringComparer.Ordinal).ToArray(),
            ValidationTests = manifest.ValidationTests.Order(StringComparer.Ordinal).ToArray(),
            RevocationTests = manifest.RevocationTests.Order(StringComparer.Ordinal).ToArray()
        });
        return new(canonical, policy.Version);
    }

    static bool DutyMatches(IdentityPermissionManifest m)
    {
        var domains = m.Permissions.Select(p => p.Domain).ToArray();
        return m.Duty switch
        {
            MachineDuty.DiscoveryConnector => domains.All(d => d == PermissionDomain.AzureAction) &&
                m.IdentityMode is MachineIdentityMode.CustomerDelegated or MachineIdentityMode.ManagedIdentity or MachineIdentityMode.WorkloadIdentityFederation &&
                m.Permissions.All(p => p.Name.EndsWith("/read", StringComparison.OrdinalIgnoreCase)) &&
                m.Approval == PermissionApproval.ReadPolicy,
            MachineDuty.AzureDeployment => domains.All(d => d is PermissionDomain.AzureAction or PermissionDomain.AzureDataAction) &&
                m.IdentityMode is MachineIdentityMode.ManagedIdentity or MachineIdentityMode.WorkloadIdentityFederation &&
                m.Approval == PermissionApproval.ExactHumanPlan &&
                !m.Permissions.Any(p => p.Name.StartsWith("Microsoft.Authorization/", StringComparison.OrdinalIgnoreCase)),
            MachineDuty.Bootstrap => domains.All(d => d == PermissionDomain.AzureAction) &&
                m.IdentityMode is MachineIdentityMode.ManagedIdentity or MachineIdentityMode.WorkloadIdentityFederation,
            MachineDuty.AzureDevOpsIntegration => domains.All(d => d == PermissionDomain.AzureDevOpsPermission) &&
                m.IdentityMode is MachineIdentityMode.CustomerDelegated or MachineIdentityMode.ManagedIdentity or MachineIdentityMode.WorkloadIdentityFederation &&
                m.Approval == PermissionApproval.ExactHumanPlan,
            MachineDuty.GitHubIntegration => domains.All(d => d == PermissionDomain.GitHubPermission) &&
                m.IdentityMode == MachineIdentityMode.GitHubApp && m.Approval == PermissionApproval.ExactHumanPlan,
            _ => domains.All(d => d == PermissionDomain.PlatformCapability) && !m.RequiresPrivilegedBootstrap
        };
    }

    static bool Id(string? value) => CustomerContextResolver.ValidId(value);
    static bool Text(string? value, int max) => !string.IsNullOrWhiteSpace(value) && value.Length <= max &&
        !value.Any(char.IsControl);
    static bool TestNames(string[]? values) => values is { Length: > 0 and <= 32 } &&
        values.All(Id) && values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    static bool Scopes(string[]? values) => values is { Length: > 0 and <= 32 } && values.All(v =>
        Text(v, 512) && !v.Any(char.IsWhiteSpace) && !v.Contains('*') && !v.Contains('?') &&
        !v.Contains("%2a", StringComparison.OrdinalIgnoreCase) && !v.Contains("%3f", StringComparison.OrdinalIgnoreCase) &&
        !v.Equals("all", StringComparison.OrdinalIgnoreCase) && !v.Equals("/", StringComparison.Ordinal)) &&
        values.Distinct(StringComparer.Ordinal).Count() == values.Length;
    static bool Permissions(ProposedPermission[]? values) => values is { Length: > 0 and <= 128 } &&
        values.All(p => p is not null && Enum.IsDefined(p.Domain) && Text(p.Name, 256) && !p.Name.Any(char.IsWhiteSpace) &&
            !p.Name.Contains('*') && !p.Name.Contains('?') && !p.Name.Equals("Owner", StringComparison.OrdinalIgnoreCase) &&
            !p.Name.Equals("Contributor", StringComparison.OrdinalIgnoreCase)) && values.Distinct().Count() == values.Length;
    static string Key(ProposedPermission p) => $"{(int)p.Domain}:{p.Name}";
    static FoundationAuthorizationException Denied() => new("permission_proposal_denied");
}
