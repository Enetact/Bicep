using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class IdentityPermissionManifestTests(IdentityTestIssuer issuer) : IClassFixture<IdentityTestIssuer>
{
    static IdentityPermissionManifest Fixture() => new("1.0", "github", "queue-workflow", "customer-a", "cell-a",
        "project-a", "dev", MachineDuty.GitHubIntegration, MachineIdentityMode.GitHubApp, "https://api.github.com/",
        ["repo:synthetic-customer/platform", "workflow:reviewed-deploy.yml"],
        [new(PermissionDomain.GitHubPermission, "actions:write"), new(PermissionDomain.GitHubPermission, "contents:read")],
        "Queue only the customer-owned reviewed workflow after exact plan approval.", false, PermissionApproval.ExactHumanPlan,
        ["named-workflow-only", "wrong-customer-denied"], ["installation-revoked", "connection-generation-changed"]);

    static IdentityPermissionPolicy Policy(IdentityPermissionManifest manifest) => new("1.0", manifest.Adapter,
        manifest.Operation, manifest.Duty, [manifest.IdentityMode], manifest.ServiceAudience,
        [.. manifest.ExactScopes], [.. manifest.Permissions], manifest.RequiresPrivilegedBootstrap, manifest.Approval);

    async Task<CustomerContext> Context()
    {
        var options = issuer.Options(); var principal = await new IdentityTokenValidator(options).ValidateAsync(issuer.Token());
        return new CustomerContextResolver(options, new IdentityMemberships(new MembershipGrant("customer-a", "cell-a", principal.ActorKey,
            "project-a", "dev", [FoundationRoles.Requester], 1, true)))
            .Require(principal, "customer-a", "project-a", "dev", FoundationCapabilities.RequestCreate);
    }

    [Fact]
    public async Task Matching_permission_proposal_is_detached_hashed_and_grants_no_authority()
    {
        var fixture = Fixture(); var policy = Policy(fixture); var context = await Context();
        var result = IdentityPermissionManifestValidator.Validate(fixture, policy, context);
        Assert.False(result.ExecutionAuthorized); Assert.False(result.IdentityGrantAuthorized); Assert.False(result.ProviderPermissionsVerified);
        Assert.Equal(64, result.Sha256.Length); Assert.Equal("1.0", result.PolicyVersion);
        var json = result.CanonicalJson; fixture.ExactScopes[0] = "repo:attacker/other";
        Assert.Equal(json, result.CanonicalJson);
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("cell")]
    [InlineData("project")]
    [InlineData("environment")]
    [InlineData("adapter")]
    [InlineData("operation")]
    [InlineData("scope")]
    [InlineData("permission")]
    [InlineData("identity")]
    [InlineData("approval")]
    [InlineData("validation_tests")]
    [InlineData("revocation_tests")]
    [InlineData("audience")]
    [InlineData("version")]
    public async Task Proposal_cannot_change_customer_operation_identity_permission_or_approval(string variant)
    {
        var fixture = Fixture(); var policy = Policy(fixture);
        fixture = variant switch
        {
            "customer" => fixture with { CustomerId = "customer-b" }, "cell" => fixture with { CellId = "cell-b" },
            "project" => fixture with { ProjectId = "project-b" }, "environment" => fixture with { EnvironmentId = "prod" },
            "adapter" => fixture with { Adapter = "attacker" }, "operation" => fixture with { Operation = "write-repository" },
            "scope" => fixture with { ExactScopes = ["repo:synthetic-customer/other"] },
            "permission" => fixture with { Permissions = [new(PermissionDomain.GitHubPermission, "administration:write")] },
            "identity" => fixture with { IdentityMode = MachineIdentityMode.CustomerDelegated },
            "approval" => fixture with { Approval = PermissionApproval.ReadPolicy },
            "validation_tests" => fixture with { ValidationTests = [] }, "revocation_tests" => fixture with { RevocationTests = [] },
            "audience" => fixture with { ServiceAudience = "https://attacker.invalid/" },
            _ => fixture with { Version = "2.0" }
        };
        var context = await Context();
        Assert.Throws<FoundationAuthorizationException>(() => IdentityPermissionManifestValidator.Validate(fixture, policy, context));
    }

    [Theory]
    [InlineData("wildcard_scope")]
    [InlineData("encoded_wildcard_scope")]
    [InlineData("wildcard_permission")]
    [InlineData("broad_role")]
    [InlineData("local_identity")]
    [InlineData("runtime_cloud_permission")]
    [InlineData("bootstrap_in_deployer")]
    [InlineData("discovery_secret_action")]
    [InlineData("discovery_wrong_identity")]
    public async Task Unsafe_manifest_is_rejected_even_if_accidentally_mirrored_in_policy(string variant)
    {
        var fixture = Fixture();
        fixture = variant switch
        {
            "wildcard_scope" => fixture with { ExactScopes = ["repo:synthetic-customer/*"] },
            "encoded_wildcard_scope" => fixture with { ExactScopes = ["repo:synthetic-customer/%2A"] },
            "wildcard_permission" => fixture with { Permissions = [new(PermissionDomain.GitHubPermission, "*")] },
            "broad_role" => fixture with { Permissions = [new(PermissionDomain.AzureAction, "Contributor")] },
            "local_identity" => fixture with { IdentityMode = MachineIdentityMode.LocalSynthetic },
            "runtime_cloud_permission" => fixture with { Duty = MachineDuty.ApiRuntime },
            "bootstrap_in_deployer" => fixture with { Duty = MachineDuty.AzureDeployment, IdentityMode = MachineIdentityMode.WorkloadIdentityFederation,
                Permissions = [new(PermissionDomain.AzureAction, "Microsoft.Authorization/roleAssignments/write")] },
            "discovery_wrong_identity" => fixture with { Duty = MachineDuty.DiscoveryConnector, IdentityMode = MachineIdentityMode.GitHubApp,
                Approval = PermissionApproval.ReadPolicy, Permissions = [new(PermissionDomain.AzureAction, "Microsoft.Resources/subscriptions/resourceGroups/read")] },
            _ => fixture with { Duty = MachineDuty.DiscoveryConnector, IdentityMode = MachineIdentityMode.ManagedIdentity,
                Approval = PermissionApproval.ReadPolicy, Permissions = [new(PermissionDomain.AzureAction, "Microsoft.Storage/storageAccounts/listKeys/action")] }
        };
        var context = await Context();
        Assert.Throws<FoundationAuthorizationException>(() => IdentityPermissionManifestValidator.Validate(fixture, Policy(fixture), context));
    }

    [Fact]
    public async Task Azure_deployment_and_bootstrap_are_separate_explicit_proposals()
    {
        var context = await Context();
        var deployment = Fixture() with { Adapter = "azure", Operation = "deploy-reviewed-pattern", Duty = MachineDuty.AzureDeployment,
            IdentityMode = MachineIdentityMode.WorkloadIdentityFederation, ServiceAudience = "https://management.azure.com/",
            ExactScopes = ["/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/synthetic-dev"],
            Permissions = [new(PermissionDomain.AzureAction, "Microsoft.Resources/deployments/write"),
                new(PermissionDomain.AzureAction, "Microsoft.Storage/storageAccounts/write")] };
        var result = IdentityPermissionManifestValidator.Validate(deployment, Policy(deployment), context);
        Assert.False(result.ExecutionAuthorized); Assert.False(result.ProviderPermissionsVerified);
        var bootstrap = deployment with { Operation = "reviewed-iam-bootstrap", Duty = MachineDuty.Bootstrap,
            RequiresPrivilegedBootstrap = true, Approval = PermissionApproval.PrivilegedBootstrap,
            Permissions = [new(PermissionDomain.AzureAction, "Microsoft.Authorization/roleAssignments/write")] };
        Assert.False(IdentityPermissionManifestValidator.Validate(bootstrap, Policy(bootstrap), context).IdentityGrantAuthorized);
    }
}
