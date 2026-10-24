using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class CustomerContextTests(IdentityTestIssuer issuer) : IClassFixture<IdentityTestIssuer>
{
    static MembershipGrant Grant(string actor, string[]? roles = null, string customer = "customer-a", string cell = "cell-a",
        string project = "project-a", string environment = "dev", long version = 7, bool enabled = true) =>
        new(customer, cell, actor, project, environment, roles ?? [FoundationRoles.Requester], version, enabled);

    [Fact]
    public async Task Context_is_server_cell_bound_and_carries_exact_membership_version()
    {
        var options = issuer.Options(); var user = await new IdentityTokenValidator(options).ValidateAsync(issuer.Token());
        var memberships = new IdentityMemberships(Grant(user.ActorKey));
        var context = new CustomerContextResolver(options, memberships).Require(user, "customer-a", "project-a", "dev", FoundationCapabilities.RequestCreate);
        Assert.Equal("customer-a", context.CustomerId); Assert.Equal("cell-a", context.CellId);
        Assert.Equal(user.ActorKey, context.ActorKey); Assert.Equal(7, context.RoleVersion);
        Assert.Equal("project-a", context.ProjectId); Assert.Equal("dev", context.EnvironmentId);
        Assert.Equal(FoundationCapabilities.RequestCreate, context.Capability); Assert.Equal(PrincipalKind.User, context.Kind);
        Assert.Equal(1, memberships.Reads);
    }

    [Fact]
    public async Task Two_customers_in_same_Entra_directory_require_separate_validation_context_and_membership()
    {
        var a = issuer.Options(); var b = issuer.Options("customer-b", "cell-b");
        var principalA = await new IdentityTokenValidator(a).ValidateAsync(issuer.Token());
        var membershipB = new IdentityMemberships(Grant(principalA.ActorKey, customer: "customer-b", cell: "cell-b"));
        var resolverB = new CustomerContextResolver(b, membershipB);
        Assert.Throws<FoundationAuthorizationException>(() => resolverB.Require(principalA, "customer-b", "project-a", "dev", FoundationCapabilities.RequestCreate));
        Assert.Equal(0, membershipB.Reads);
        var principalB = await new IdentityTokenValidator(b).ValidateAsync(issuer.Token());
        var context = resolverB.Require(principalB, "customer-b", "project-a", "dev", FoundationCapabilities.RequestCreate);
        Assert.Equal(principalA.TenantId, principalB.TenantId); Assert.Equal(principalA.ActorKey, principalB.ActorKey);
        Assert.Equal("customer-b", context.CustomerId);
    }

    [Theory]
    [InlineData("customer-b", "project-a", "dev")]
    [InlineData("customer-a", "project-b", "dev")]
    [InlineData("customer-a", "project-a", "prod")]
    [InlineData("customer-a", "../project-a", "dev")]
    [InlineData("customer-a", "*", "dev")]
    [InlineData("customer-a", "project-a\n", "dev")]
    public async Task Caller_route_or_headers_cannot_expand_customer_project_or_environment(string customer, string project, string environment)
    {
        var options = issuer.Options(); var user = await new IdentityTokenValidator(options).ValidateAsync(issuer.Token());
        var memberships = new IdentityMemberships(Grant(user.ActorKey));
        Assert.Throws<FoundationAuthorizationException>(() => new CustomerContextResolver(options, memberships).Require(user, customer, project, environment, FoundationCapabilities.RequestCreate));
        if (customer != "customer-a") Assert.Equal(0, memberships.Reads);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("revoked")]
    [InlineData("version")]
    [InlineData("duplicate")]
    [InlineData("wrong_customer")]
    [InlineData("wrong_cell")]
    [InlineData("wrong_actor")]
    [InlineData("invalid_role")]
    public async Task Invalid_persisted_membership_fails_closed(string variant)
    {
        var options = issuer.Options(); var user = await new IdentityTokenValidator(options).ValidateAsync(issuer.Token());
        var grant = Grant(user.ActorKey);
        var entries = variant switch
        {
            "missing" => Array.Empty<MembershipGrant>(),
            "revoked" => [grant with { Enabled = false }],
            "version" => [grant with { Version = 0 }],
            "duplicate" => new[] { grant, grant with { Version = 8 } },
            "wrong_customer" => [grant with { CustomerId = "customer-b" }],
            "wrong_cell" => [grant with { CellId = "cell-b" }],
            "wrong_actor" => [grant with { ActorKey = $"user:{IdentityTestIssuer.OtherTenant}:{IdentityTestIssuer.Subject}" }],
            _ => [grant with { Roles = ["Owner"] }]
        };
        Assert.Throws<FoundationAuthorizationException>(() => new CustomerContextResolver(options, new IdentityMemberships(entries))
            .Require(user, "customer-a", "project-a", "dev", FoundationCapabilities.RequestCreate));
    }

    [Fact]
    public async Task Signed_roles_and_persisted_roles_are_intersected()
    {
        var options = issuer.Options(); var claims = issuer.UserClaims();
        claims["roles"] = new[] { FoundationRoles.Requester, FoundationRoles.Approver };
        var user = await new IdentityTokenValidator(options).ValidateAsync(issuer.Token(claims));
        var resolver = new CustomerContextResolver(options, new IdentityMemberships(Grant(user.ActorKey)));
        Assert.Throws<FoundationAuthorizationException>(() => resolver.Require(user, "customer-a", "project-a", "dev", FoundationCapabilities.PlanApprove));
        var approver = await new IdentityTokenValidator(options).ValidateAsync(issuer.Token(issuer.UserClaims(FoundationRoles.Approver)));
        Assert.Throws<FoundationAuthorizationException>(() => resolver.Require(approver, "customer-a", "project-a", "dev", FoundationCapabilities.RequestCreate));
    }

    [Theory]
    [InlineData(FoundationCapabilities.PlanApprove)]
    [InlineData(FoundationCapabilities.JobDispatch)]
    [InlineData(FoundationCapabilities.AuditRead)]
    public async Task Customer_administrator_does_not_implicitly_gain_other_operating_roles(string capability)
    {
        var options = issuer.Options(); var admin = await new IdentityTokenValidator(options).ValidateAsync(issuer.Token(issuer.UserClaims(FoundationRoles.CustomerAdmin)));
        var resolver = new CustomerContextResolver(options, new IdentityMemberships(Grant(admin.ActorKey, [FoundationRoles.CustomerAdmin])));
        Assert.Throws<FoundationAuthorizationException>(() => resolver.Require(admin, "customer-a", "project-a", "dev", capability));
        Assert.Equal(FoundationCapabilities.MembershipManage, resolver.Require(admin, "customer-a", "project-a", "dev", FoundationCapabilities.MembershipManage).Capability);
    }

    [Fact]
    public async Task Expired_principal_is_rejected_before_membership_lookup()
    {
        var options = issuer.Options(); var user = await new IdentityTokenValidator(options).ValidateAsync(issuer.Token());
        var source = new IdentityMemberships(Grant(user.ActorKey));
        var resolver = new CustomerContextResolver(options, source, new IdentityFixedClock(user.ExpiresUtc.AddSeconds(1)));
        var error = Assert.Throws<FoundationAuthorizationException>(() => resolver.Require(user, "customer-a", "project-a", "dev", FoundationCapabilities.RequestCreate));
        Assert.Equal(401, error.StatusCode); Assert.Equal(0, source.Reads);
    }

    [Fact]
    public async Task Membership_revocation_is_checked_on_every_resolution()
    {
        var options = issuer.Options(); var user = await new IdentityTokenValidator(options).ValidateAsync(issuer.Token());
        var grant = Grant(user.ActorKey); var source = new IdentityMemberships(grant);
        var resolver = new CustomerContextResolver(options, source);
        resolver.Require(user, "customer-a", "project-a", "dev", FoundationCapabilities.RequestCreate);
        source.Values = [grant with { Enabled = false, Version = 8 }];
        Assert.Throws<FoundationAuthorizationException>(() => resolver.Require(user, "customer-a", "project-a", "dev", FoundationCapabilities.RequestCreate));
        Assert.Equal(2, source.Reads);
    }

    [Fact]
    public async Task Workload_identity_cannot_be_a_human_requester_or_approver()
    {
        var options = issuer.Options(); var worker = await new IdentityTokenValidator(options).ValidateAsync(issuer.Token(issuer.WorkloadClaims()));
        var source = new IdentityMemberships(Grant(worker.ActorKey, [FoundationRoles.Operator, FoundationRoles.Requester, FoundationRoles.Approver, FoundationRoles.CustomerAdmin]));
        var resolver = new CustomerContextResolver(options, source);
        Assert.Equal(PrincipalKind.Workload, resolver.Require(worker, "customer-a", "project-a", "dev", FoundationCapabilities.JobDispatch).Kind);
        foreach (var capability in new[] { FoundationCapabilities.PlanApprove, FoundationCapabilities.RequestCreate, FoundationCapabilities.MembershipManage })
            Assert.Throws<FoundationAuthorizationException>(() => resolver.Require(worker, "customer-a", "project-a", "dev", capability));
    }

    [Fact]
    public async Task Privileged_approval_requires_a_distinct_human_actor()
    {
        var options = issuer.Options(); var user = await new IdentityTokenValidator(options).ValidateAsync(issuer.Token(issuer.UserClaims(FoundationRoles.Approver)));
        var resolver = new CustomerContextResolver(options, new IdentityMemberships(Grant(user.ActorKey, [FoundationRoles.Approver])));
        var context = resolver.Require(user, "customer-a", "project-a", "dev", FoundationCapabilities.PlanApprove);
        Assert.Throws<FoundationAuthorizationException>(() => AuthorizationPolicy.RequireSeparation(context, user.ActorKey));
        AuthorizationPolicy.RequireSeparation(context, $"user:{IdentityTestIssuer.Tenant}:{IdentityTestIssuer.OtherSubject}");
    }

    [Fact]
    public void Unknown_capabilities_and_platform_operator_data_access_are_denied()
    {
        Assert.False(AuthorizationPolicy.Allows("arbitrary.execute", FoundationRoles.All, PrincipalKind.User));
        Assert.False(AuthorizationPolicy.Allows(null!, FoundationRoles.All, PrincipalKind.User));
        Assert.False(AuthorizationPolicy.Allows(FoundationCapabilities.CatalogRead, FoundationRoles.All, (PrincipalKind)99));
        Assert.False(AuthorizationPolicy.Allows(FoundationCapabilities.EvidenceRead, [FoundationRoles.PlatformOperator], PrincipalKind.User));
        Assert.True(AuthorizationPolicy.Allows(FoundationCapabilities.CellHealthRead, [FoundationRoles.PlatformOperator], PrincipalKind.User));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Support_delegate_without_reviewed_purpose_and_expiry_metadata_is_denied(bool combinedRole)
    {
        var options = issuer.Options(); var claims = issuer.UserClaims(FoundationRoles.SupportDelegate);
        var roles = combinedRole ? new[] { FoundationRoles.SupportDelegate, FoundationRoles.Requester } : [FoundationRoles.SupportDelegate];
        claims["roles"] = roles;
        var principal = await new IdentityTokenValidator(options).ValidateAsync(issuer.Token(claims));
        var resolver = new CustomerContextResolver(options, new IdentityMemberships(Grant(principal.ActorKey, roles)));
        Assert.Throws<FoundationAuthorizationException>(() => resolver.Require(principal, "customer-a", "project-a", "dev", FoundationCapabilities.CatalogRead));
        Assert.True(AuthorizationPolicy.Allows(FoundationCapabilities.EvidenceRead, [FoundationRoles.SupportDelegate], PrincipalKind.User));
        Assert.False(AuthorizationPolicy.Allows(FoundationCapabilities.RequestCreate, [FoundationRoles.SupportDelegate], PrincipalKind.User));
    }
}
