using System.Net;
using System.Net.Http;
using System.Text.Json;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationAccessApiTests
{
    [Fact]
    public async Task New_privileged_role_grants_require_an_exact_independently_reviewed_plan()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        var target = FoundationApiTests.ApiCell.Actor("viewer");
        var direct = await f.Send("admin", HttpMethod.Put, "/memberships", new MembershipCommand(target, [FoundationRoles.Operator]));
        Assert.Equal(HttpStatusCode.Forbidden, direct.StatusCode);
        var plan = (await Read(await f.Send("admin", HttpMethod.Post, "/administration/memberships/plan",
            new ReviewedMembershipCommand(target, [FoundationRoles.Operator], true, "Synthetic reviewed operator assignment"))))
            .Deserialize<FoundationAdministrativePlan>(FoundationApplication.Json)!;
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("admin", HttpMethod.Post, $"/administration/plans/{plan.Id}/approve", new { plan.Digest })).StatusCode);
        _ = await Read(await f.Send("approver", HttpMethod.Post, $"/administration/plans/{plan.Id}/approve", new { plan.Digest }));
        _ = await Read(await f.Send("admin", HttpMethod.Post, $"/administration/plans/{plan.Id}/execute", new { plan.Digest }));
        Assert.Equal(HttpStatusCode.OK, (await f.Send("viewer", HttpMethod.Get, "/jobs", tokenRoles: [FoundationRoles.Operator])).StatusCode);
        Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public async Task Reviewed_support_is_scoped_read_only_audited_and_urgently_revocable()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        await f.Member("viewer", []);
        var created = await Read(await f.Send("requester", HttpMethod.Post, "/artifacts", new ArtifactCommand("source.txt", "Synthetic source")));
        var artifactId = created.GetProperty("id").GetString();
        var target = FoundationApiTests.ApiCell.Actor("viewer");
        var plan = (await Read(await f.Send("admin", HttpMethod.Post, "/administration/support/plan",
            new ReviewedSupportCommand(target, DateTimeOffset.UtcNow.AddMinutes(10), "Review synthetic source for support"))))
            .Deserialize<FoundationAdministrativePlan>(FoundationApplication.Json)!;
        _ = await Read(await f.Send("approver", HttpMethod.Post, $"/administration/plans/{plan.Id}/approve", new { plan.Digest }));
        _ = await Read(await f.Send("admin", HttpMethod.Post, $"/administration/plans/{plan.Id}/execute", new { plan.Digest }));
        var allowed = await f.Send("viewer", HttpMethod.Get, $"/artifacts/{artifactId}/content", tokenRoles: [FoundationRoles.SupportDelegate]);
        Assert.Equal(HttpStatusCode.OK, allowed.StatusCode);
        Assert.Equal("Synthetic source", await allowed.Content.ReadAsStringAsync());
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("viewer", HttpMethod.Post, "/plans",
            new PlanCommand("customer-ci", "main", FoundationApiTests.ApiCell.Revision, new()), tokenRoles: [FoundationRoles.SupportDelegate])).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("viewer", HttpMethod.Get, "/audit", tokenRoles: [FoundationRoles.SupportDelegate])).StatusCode);
        var audit = await Read(await f.Send("auditor", HttpMethod.Get, "/audit"));
        Assert.Contains("SupportEvidenceAccess", audit.GetRawText());
        _ = await Read(await f.Send("admin", HttpMethod.Post, "/administration/access/revoke", new RevokeAccessCommand(target, "End synthetic support")));
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("viewer", HttpMethod.Get, $"/artifacts/{artifactId}/content", tokenRoles: [FoundationRoles.SupportDelegate])).StatusCode);
        Assert.Empty(f.Transport.Requests);
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("workload:11111111-1111-1111-1111-111111111111:cccccccc-cccc-cccc-cccc-cccccccccccc:99999999-9999-9999-9999-999999999999")]
    [InlineData("user:22222222-2222-2222-2222-222222222222:eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee")]
    public async Task Unreachable_or_unadmitted_membership_identities_are_rejected(string actor)
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        Assert.Equal(HttpStatusCode.BadRequest, (await f.Send("admin", HttpMethod.Put, "/memberships", new MembershipCommand(actor, [FoundationRoles.Viewer]))).StatusCode);
    }

    static async Task<JsonElement> Read(HttpResponseMessage response)
    {
        var text = await response.Content.ReadAsStringAsync(); Assert.True(response.IsSuccessStatusCode, $"{response.StatusCode}: {text}");
        using var doc = JsonDocument.Parse(text); return doc.RootElement.Clone();
    }
}
