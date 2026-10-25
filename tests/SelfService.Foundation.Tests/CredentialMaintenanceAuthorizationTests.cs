using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class CredentialMaintenanceAuthorizationTests(IdentityTestIssuer issuer) : IClassFixture<IdentityTestIssuer>
{
    static readonly string[] Capabilities = [FoundationCapabilities.CredentialMaintenancePrepare,
        FoundationCapabilities.CredentialMaintenanceApprove, FoundationCapabilities.CredentialMaintenanceExecute,
        FoundationCapabilities.CredentialMaintenanceRead];

    static MembershipGrant Grant(string actor, string role) =>
        new("customer-a", "cell-a", actor, "project-a", "dev", [role], 17, true);

    [Theory]
    [InlineData(FoundationCapabilities.CredentialMaintenancePrepare, FoundationRoles.Operator)]
    [InlineData(FoundationCapabilities.CredentialMaintenanceApprove, FoundationRoles.Approver)]
    [InlineData(FoundationCapabilities.CredentialMaintenanceExecute, FoundationRoles.Operator)]
    [InlineData(FoundationCapabilities.CredentialMaintenanceRead, FoundationRoles.Auditor)]
    public async Task Signed_human_role_and_exact_scoped_membership_create_only_the_requested_capability(string capability, string role)
    {
        var options = issuer.Options();
        var principal = await new IdentityTokenValidator(options).ValidateAsync(issuer.Token(issuer.UserClaims(role)));
        var context = new CustomerContextResolver(options, new IdentityMemberships(Grant(principal.ActorKey, role)))
            .Require(principal, "customer-a", "project-a", "dev", capability);
        Assert.Equal(capability, context.Capability); Assert.Equal(PrincipalKind.User, context.Kind);
        Assert.Equal(17, context.RoleVersion); Assert.Equal(principal.ExpiresUtc, context.ExpiresUtc);
        Assert.Equal("customer-a", context.CustomerId); Assert.Equal("cell-a", context.CellId);
        Assert.Equal("project-a", context.ProjectId); Assert.Equal("dev", context.EnvironmentId);
    }

    public static IEnumerable<object[]> MismatchedAuthority()
    {
        foreach (var capability in Capabilities)
        {
            var role = capability == FoundationCapabilities.CredentialMaintenanceApprove ? FoundationRoles.Approver : FoundationRoles.Operator;
            yield return [capability, FoundationRoles.CustomerAdmin, role];
            yield return [capability, role, FoundationRoles.CustomerAdmin];
            yield return [capability, FoundationRoles.CustomerAdmin, FoundationRoles.CustomerAdmin];
        }
    }

    [Theory]
    [MemberData(nameof(MismatchedAuthority))]
    public async Task Neither_a_signed_claim_nor_a_database_grant_alone_supplies_maintenance_authority(string capability, string tokenRole, string persistedRole)
    {
        var options = issuer.Options();
        var principal = await new IdentityTokenValidator(options).ValidateAsync(issuer.Token(issuer.UserClaims(tokenRole)));
        var resolver = new CustomerContextResolver(options, new IdentityMemberships(Grant(principal.ActorKey, persistedRole)));
        Assert.Throws<FoundationAuthorizationException>(() => resolver.Require(principal, "customer-a", "project-a", "dev", capability));
    }

    [Fact]
    public async Task Admitted_workload_client_cannot_inherit_human_maintenance_from_persisted_roles()
    {
        var options = issuer.Options();
        var principal = await new IdentityTokenValidator(options).ValidateAsync(issuer.Token(issuer.WorkloadClaims()));
        var grant = Grant(principal.ActorKey, FoundationRoles.Operator) with
        { Roles = [FoundationRoles.Operator, FoundationRoles.Approver, FoundationRoles.Auditor, FoundationRoles.CustomerAdmin] };
        var resolver = new CustomerContextResolver(options, new IdentityMemberships(grant));
        foreach (var capability in Capabilities)
            Assert.Equal("capability_denied", Assert.Throws<FoundationAuthorizationException>(() =>
                resolver.Require(principal, "customer-a", "project-a", "dev", capability)).Code);
    }

    [Theory]
    [InlineData("customer-b", "project-a", "dev")]
    [InlineData("customer-a", "project-b", "dev")]
    [InlineData("customer-a", "project-a", "prod")]
    public async Task Valid_maintenance_identity_cannot_expand_its_customer_or_application_scope(string customer, string project, string environment)
    {
        var options = issuer.Options();
        var principal = await new IdentityTokenValidator(options).ValidateAsync(issuer.Token(issuer.UserClaims(FoundationRoles.Operator)));
        var resolver = new CustomerContextResolver(options, new IdentityMemberships(Grant(principal.ActorKey, FoundationRoles.Operator)));
        Assert.Throws<FoundationAuthorizationException>(() => resolver.Require(principal, customer, project, environment,
            FoundationCapabilities.CredentialMaintenanceExecute));
    }

    [Fact]
    public async Task Auditor_can_review_maintenance_without_preparing_approving_or_executing_it()
    {
        var options = issuer.Options();
        var principal = await new IdentityTokenValidator(options).ValidateAsync(issuer.Token(issuer.UserClaims(FoundationRoles.Auditor)));
        var resolver = new CustomerContextResolver(options, new IdentityMemberships(Grant(principal.ActorKey, FoundationRoles.Auditor)));
        Assert.Equal(FoundationCapabilities.CredentialMaintenanceRead,
            resolver.Require(principal, "customer-a", "project-a", "dev", FoundationCapabilities.CredentialMaintenanceRead).Capability);
        foreach (var capability in Capabilities.Where(c => c != FoundationCapabilities.CredentialMaintenanceRead))
            Assert.Throws<FoundationAuthorizationException>(() => resolver.Require(principal, "customer-a", "project-a", "dev", capability));
    }
}
