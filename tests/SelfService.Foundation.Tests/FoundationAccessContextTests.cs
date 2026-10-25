using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationAccessContextTests
{
    const string SupportActor = "user:" + IdentityTestIssuer.Tenant + ":" + IdentityTestIssuer.OtherSubject;

    [Fact]
    public async Task Signed_support_token_requires_current_reviewed_lease_and_context_expires_at_the_lease()
    {
        using var f = new StoreFixture(); using var issuer = new IdentityTestIssuer();
        var grant = FoundationAccessTests.GrantSupport(f, TimeSpan.FromMinutes(30), SupportActor);
        var options = issuer.Options(f.Customer, f.Cell); var principal = await Principal(issuer, options, f.Clock);
        var resolver = new CustomerContextResolver(options, new AccessMemberships(f.Store), f.Clock);
        var context = resolver.Require(principal, f.Customer, "default", "dev", FoundationCapabilities.EvidenceRead);
        Assert.Equal(grant.ValidUntilUtc, context.ExpiresUtc); Assert.Equal(grant.ValidUntilUtc, context.SupportValidUntilUtc);
        Assert.Equal(grant.Purpose, context.SupportPurpose); Assert.Equal(grant.ApprovalId, context.SupportApprovalId);
        f.Store.RecordSupportAccess(context);
        Assert.Throws<FoundationAuthorizationException>(() => resolver.Require(principal, f.Customer, "default", "dev", FoundationCapabilities.RequestCreate));
        Assert.Throws<FoundationAuthorizationException>(() => resolver.Require(principal, f.Customer, "default", "dev", FoundationCapabilities.PlanApprove));
        Assert.Throws<FoundationAuthorizationException>(() => resolver.Require(principal, f.Customer, "default", "dev", FoundationCapabilities.LifecycleManage));
        Assert.Throws<FoundationAuthorizationException>(() => resolver.Require(principal, f.Customer, "other", "dev", FoundationCapabilities.EvidenceRead));
        f.Clock.Advance(TimeSpan.FromMinutes(31));
        Assert.Throws<FoundationAuthorizationException>(() => resolver.Require(principal, f.Customer, "default", "dev", FoundationCapabilities.EvidenceRead));
        Assert.Equal("SupportAccessExpiredOrRevoked", Assert.Throws<FoundationStoreException>(() => f.Store.RecordSupportAccess(context)).Code);
    }

    [Fact]
    public async Task Cached_support_context_cannot_survive_revocation_and_a_different_reviewed_grant()
    {
        using var f = new StoreFixture(); using var issuer = new IdentityTestIssuer();
        _ = FoundationAccessTests.GrantSupport(f, target: SupportActor);
        var options = issuer.Options(f.Customer, f.Cell); var principal = await Principal(issuer, options, f.Clock);
        var resolver = new CustomerContextResolver(options, new AccessMemberships(f.Store), f.Clock);
        var previous = resolver.Require(principal, f.Customer, "default", "dev", FoundationCapabilities.CatalogRead);
        f.Store.RevokeScopedAccess(f.Customer, "admin", "default", "dev", SupportActor, "Urgent reviewed-context revocation");
        _ = FoundationAccessTests.GrantSupport(f, target: SupportActor);
        Assert.Equal("SupportAccessExpiredOrRevoked", Assert.Throws<FoundationStoreException>(() => f.Store.RecordSupportAccess(previous)).Code);
        var current = resolver.Require(principal, f.Customer, "default", "dev", FoundationCapabilities.CatalogRead);
        Assert.NotEqual(previous.RoleVersion, current.RoleVersion); Assert.NotEqual(previous.SupportApprovalId, current.SupportApprovalId);
        f.Store.RecordSupportAccess(current);
    }

    [Theory]
    [InlineData("missing_expiry")]
    [InlineData("expired")]
    [InlineData("unbounded")]
    [InlineData("missing_purpose")]
    [InlineData("missing_approval")]
    [InlineData("combined_roles")]
    public async Task Fake_or_incomplete_support_metadata_cannot_create_a_customer_context(string fault)
    {
        using var f = new StoreFixture(); using var issuer = new IdentityTestIssuer();
        var options = issuer.Options(f.Customer, f.Cell); var principal = await Principal(issuer, options, f.Clock);
        var grant = new MembershipGrant(f.Customer, f.Cell, principal.ActorKey, "default", "dev", [FoundationRoles.SupportDelegate], 1, true,
            f.Clock.GetUtcNow().AddMinutes(30), "Purpose-bound scoped support", "approved-plan");
        grant = fault switch
        {
            "missing_expiry" => grant with { ValidUntilUtc = null }, "expired" => grant with { ValidUntilUtc = f.Clock.GetUtcNow() },
            "unbounded" => grant with { ValidUntilUtc = f.Clock.GetUtcNow().AddDays(2) }, "missing_purpose" => grant with { Purpose = null },
            "missing_approval" => grant with { ApprovalId = null }, _ => grant with { Roles = [FoundationRoles.SupportDelegate, FoundationRoles.Requester] }
        };
        var resolver = new CustomerContextResolver(options, new IdentityMemberships(grant), f.Clock);
        Assert.Throws<FoundationAuthorizationException>(() => resolver.Require(principal, f.Customer, "default", "dev", FoundationCapabilities.EvidenceRead));
    }

    [Fact]
    public async Task Support_read_receipt_cannot_substitute_another_purpose_for_the_approved_grant()
    {
        using var f = new StoreFixture(); using var issuer = new IdentityTestIssuer();
        var support = FoundationAccessTests.GrantSupport(f, target: SupportActor);
        var options = issuer.Options(f.Customer, f.Cell); var principal = await Principal(issuer, options, f.Clock);
        var forged = new MembershipGrant(f.Customer, f.Cell, principal.ActorKey, "default", "dev", [FoundationRoles.SupportDelegate], support.Version,
            true, support.ValidUntilUtc, "A different unapproved purpose", support.ApprovalId);
        var resolver = new CustomerContextResolver(options, new IdentityMemberships(forged), f.Clock);
        var context = resolver.Require(principal, f.Customer, "default", "dev", FoundationCapabilities.EvidenceRead);
        Assert.Equal("SupportAccessExpiredOrRevoked", Assert.Throws<FoundationStoreException>(() => f.Store.RecordSupportAccess(context)).Code);
    }

    [Fact]
    public async Task Token_role_and_customer_binding_still_apply_to_reviewed_support()
    {
        using var f = new StoreFixture(); using var issuer = new IdentityTestIssuer(); _ = FoundationAccessTests.GrantSupport(f, target: SupportActor);
        var options = issuer.Options(f.Customer, f.Cell); var principal = await Principal(issuer, options, f.Clock, FoundationRoles.Viewer);
        var resolver = new CustomerContextResolver(options, new AccessMemberships(f.Store), f.Clock);
        Assert.Throws<FoundationAuthorizationException>(() => resolver.Require(principal, f.Customer, "default", "dev", FoundationCapabilities.EvidenceRead));
        var supportPrincipal = await Principal(issuer, options, f.Clock);
        Assert.Throws<FoundationAuthorizationException>(() => resolver.Require(supportPrincipal, "different-customer", "default", "dev", FoundationCapabilities.EvidenceRead));
    }

    static async Task<FoundationPrincipal> Principal(IdentityTestIssuer issuer, CellIdentityOptions options, StoreClock clock,
        string role = FoundationRoles.SupportDelegate)
    {
        var claims = issuer.UserClaims(role, IdentityTestIssuer.OtherSubject);
        claims["iat"] = clock.GetUtcNow().AddSeconds(-30).ToUnixTimeSeconds();
        claims["nbf"] = clock.GetUtcNow().AddSeconds(-30).ToUnixTimeSeconds();
        claims["exp"] = clock.GetUtcNow().AddHours(2).ToUnixTimeSeconds();
        return await new IdentityTokenValidator(options, clock).ValidateAsync(issuer.Token(claims));
    }

    sealed class AccessMemberships(FoundationStore store) : ICustomerMembershipSource
    {
        public IReadOnlyList<MembershipGrant> Find(string customerId, string actorKey) => store.GetMemberships(customerId, actorKey)
            .Select(m => new MembershipGrant(m.CustomerId, m.CellId, m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version,
                m.Enabled, m.ValidUntilUtc, m.Purpose, m.ApprovalId)).ToArray();
    }
}
