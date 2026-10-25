using System.Text.Json;
using System.Text.Json.Nodes;
using System.Security.Cryptography;
using Microsoft.IdentityModel.Tokens;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class EntraCellOnboardingTests
{
    [Fact]
    public void Definitions_match_runtime_contract_without_granting_identity_or_downstream_rights()
    {
        var proposal = EntraCellOnboarding.Create(Request());
        var definition = proposal.ApplicationDefinition;
        Assert.Equal("AzureADMyOrg", definition.GetProperty("signInAudience").GetString());
        Assert.Equal(2, definition.GetProperty("api").GetProperty("requestedAccessTokenVersion").GetInt32());
        var scope = definition.GetProperty("api").GetProperty("oauth2PermissionScopes")[0];
        Assert.Equal("Admin", scope.GetProperty("type").GetString()); Assert.Equal("platform.access", scope.GetProperty("value").GetString());
        var roles = definition.GetProperty("appRoles").EnumerateArray().ToArray(); Assert.Equal(9, roles.Length);
        Assert.Equal(FoundationRoles.All.Order(), roles.Where(r => r.GetProperty("value").GetString() != "Worker.Execute").Select(r => r.GetProperty("value").GetString()).Order());
        Assert.All(roles, r => Assert.Equal(r.GetProperty("value").GetString() == "Worker.Execute" ? "Application" : "User", r.GetProperty("allowedMemberTypes")[0].GetString()));
        Assert.Equal("idtyp", definition.GetProperty("optionalClaims").GetProperty("accessToken")[0].GetProperty("name").GetString());
        Assert.False(definition.TryGetProperty("passwordCredentials", out _)); Assert.False(definition.TryGetProperty("requiredResourceAccess", out _));
        Assert.False(proposal.IdentityGrantAuthorized); Assert.False(proposal.ExecutionAuthorized);
        Assert.False(proposal.ProviderPermissionsVerified); Assert.False(proposal.RuntimeAcceptanceVerified);
        using var doc = JsonDocument.Parse(proposal.CanonicalJson);
        Assert.True(doc.RootElement.GetProperty("constraints").GetProperty("currentScopedMembershipRequired").GetBoolean());
        Assert.True(doc.RootElement.GetProperty("constraints").GetProperty("supportRequiresReviewedTimeBoundGrant").GetBoolean());
    }

    [Fact]
    public async Task Generated_identity_metadata_accepts_matching_signed_payloads_and_rejects_wrong_cell_audience_or_workload_subject()
    {
        using var issuer = new IdentityTestIssuer(); var request = Request();
        var proposal = EntraCellOnboarding.Create(request); var validator = new IdentityTokenValidator(Options(proposal, issuer));
        var humanClaims = issuer.UserClaims(); humanClaims.Remove("idtyp");
        var human = await validator.ValidateAsync(issuer.Token(humanClaims)); Assert.Equal(PrincipalKind.User, human.Kind);
        var worker = await validator.ValidateAsync(issuer.Token(issuer.WorkloadClaims())); Assert.Equal(PrincipalKind.Workload, worker.Kind);
        Assert.False(AuthorizationPolicy.Allows(FoundationCapabilities.PlanApprove, worker.Roles, worker.Kind));
        var wrong = issuer.WorkloadClaims(); wrong["oid"] = IdentityTestIssuer.OtherSubject;
        await Assert.ThrowsAsync<FoundationAuthorizationException>(() => validator.ValidateAsync(issuer.Token(wrong)));
        var otherCell = EntraCellOnboarding.Create(request with { CustomerId = "customer-b", CellId = "cell-b", ApiClientId = "88888888-8888-8888-8888-888888888888" });
        await Assert.ThrowsAsync<FoundationAuthorizationException>(() => new IdentityTokenValidator(Options(otherCell, issuer)).ValidateAsync(issuer.Token()));
        var noMembership = new CustomerContextResolver(Options(proposal, issuer), new IdentityMemberships());
        Assert.Throws<FoundationAuthorizationException>(() => noMembership.Require(human, request.CustomerId, "default", "dev", FoundationCapabilities.RequestCreate));
        Assert.Throws<FoundationAuthorizationException>(() => noMembership.Require(human, "customer-b", "default", "dev", FoundationCapabilities.RequestCreate));
        var support = await validator.ValidateAsync(issuer.Token(issuer.UserClaims(FoundationRoles.SupportDelegate)));
        var unreviewedSupport = new CustomerContextResolver(Options(proposal, issuer), new IdentityMemberships(
            new MembershipGrant(request.CustomerId, request.CellId, support.ActorKey, "default", "dev", [FoundationRoles.SupportDelegate], 1, true)));
        Assert.Throws<FoundationAuthorizationException>(() => unreviewedSupport.Require(support, request.CustomerId, "default", "dev", FoundationCapabilities.EvidenceRead));
        var machineReviewer = new CustomerContextResolver(Options(proposal, issuer), new IdentityMemberships(
            new MembershipGrant(request.CustomerId, request.CellId, worker.ActorKey, "default", "dev", [FoundationRoles.Approver], 1, true)));
        Assert.Throws<FoundationAuthorizationException>(() => machineReviewer.Require(worker, request.CustomerId, "default", "dev", FoundationCapabilities.PlanApprove));
    }

    [Fact]
    public void Host_identity_fragment_merges_into_actual_public_configuration_without_unknown_fields_or_creating_state()
    {
        using var issuer = new IdentityTestIssuer(); var proposal = EntraCellOnboarding.Create(Request());
        using var rsa = RSA.Create(((RsaSecurityKey)issuer.Options().SigningKeys[0]).Parameters);
        var directory = Path.Combine(Path.GetTempPath(), "foundation-entra-contract-" + Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(directory); var path = Path.Combine(directory, "public-cell.json");
        try
        {
            var node = JsonNode.Parse(proposal.HostIdentity.GetRawText())!.AsObject();
            node["databasePath"] = "cell.sqlite"; node["projectId"] = "default"; node["environmentId"] = "dev";
            node["bootstrapActorKey"] = $"user:{IdentityTestIssuer.Tenant}:{IdentityTestIssuer.Subject}";
            node["publicKeys"] = JsonSerializer.SerializeToNode(new[] { new { keyId = IdentityTestIssuer.KeyId, pem = rsa.ExportSubjectPublicKeyInfoPem() } });
            File.WriteAllText(path, node.ToJsonString()); var options = FoundationHostConfiguration.Load(path);
            Assert.Equal(IdentityTestIssuer.Audience, options.Identity.Audience);
            Assert.Equal(2, options.Identity.AllowedClients.Count); Assert.False(options.EnableWorker);
            Assert.False(File.Exists(options.DatabasePath));
        }
        finally { File.Delete(path); Directory.Delete(directory, false); }
    }

    [Fact]
    public void Canonical_proposal_is_detached_and_stable_when_input_collections_are_reordered_or_mutated()
    {
        var request = Request(); var original = EntraCellOnboarding.Create(request);
        var reverse = request with { RoleDefinitionIds = request.RoleDefinitionIds.Reverse().ToDictionary(p => p.Key, p => p.Value) };
        Assert.Equal(original.CanonicalJson, EntraCellOnboarding.Create(reverse).CanonicalJson);
        request.RoleDefinitionIds[FoundationRoles.Requester] = Guid.NewGuid().ToString("D");
        request.HumanClientIds[0] = Guid.NewGuid().ToString("D"); request.WorkerClients[0] = new(Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"));
        Assert.NotEqual(original.Sha256, EntraCellOnboarding.Create(request).Sha256);
        Assert.Equal(IdentityTestIssuer.Client, original.HostIdentity.GetProperty("allowedClients")[0].GetProperty("clientId").GetString());
    }

    [Theory]
    [InlineData("zero-tenant")]
    [InlineData("missing-role")]
    [InlineData("extra-role")]
    [InlineData("case-role")]
    [InlineData("case-insensitive-map")]
    [InlineData("duplicate-definition")]
    [InlineData("duplicate-human")]
    [InlineData("same-human-worker")]
    [InlineData("same-worker-subject")]
    [InlineData("api-as-client")]
    [InlineData("bad-display")]
    [InlineData("null-worker")]
    public void Ambiguous_or_overbroad_identity_contracts_fail_closed(string variant)
    {
        var request = Request();
        request = variant switch
        {
            "zero-tenant" => request with { TenantId = Guid.Empty.ToString("D") },
            "duplicate-human" => request with { HumanClientIds = [IdentityTestIssuer.Client, IdentityTestIssuer.Client] },
            "same-human-worker" => request with { WorkerClients = [new(IdentityTestIssuer.Client, IdentityTestIssuer.WorkerSubject)] },
            "same-worker-subject" => request with { WorkerClients = [.. request.WorkerClients, new(IdentityTestIssuer.OtherClient, IdentityTestIssuer.WorkerSubject)] },
            "api-as-client" => request with { HumanClientIds = [IdentityTestIssuer.Audience] },
            "bad-display" => request with { DisplayName = "synthetic\ninvalid" },
            "null-worker" => request with { WorkerClients = [null!] },
            _ => request
        };
        if (variant == "missing-role") request.RoleDefinitionIds.Remove(FoundationRoles.Viewer);
        if (variant == "extra-role") request.RoleDefinitionIds.Add("Owner", Guid.NewGuid().ToString("D"));
        if (variant == "case-role") { var id = request.RoleDefinitionIds[FoundationRoles.Viewer]; request.RoleDefinitionIds.Remove(FoundationRoles.Viewer); request.RoleDefinitionIds.Add("viewer", id); }
        if (variant == "case-insensitive-map")
        {
            var map = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            foreach (var pair in request.RoleDefinitionIds) map.Add(pair.Key.ToLowerInvariant(), pair.Value);
            request = request with { RoleDefinitionIds = map };
        }
        if (variant == "duplicate-definition") request.RoleDefinitionIds[FoundationRoles.Viewer] = request.ScopeDefinitionId;
        Assert.Throws<EntraOnboardingException>(() => EntraCellOnboarding.Create(request));
    }

    [Fact]
    public void Public_json_is_bounded_unique_and_rejects_credentials_without_echoing_material()
    {
        var json = JsonSerializer.Serialize(Request(), new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.Equal(EntraCellOnboarding.Create(Request()).Sha256, EntraCellOnboarding.Parse(json).Sha256);
        var duplicate = json[..^1] + ",\"TenantId\":\"" + IdentityTestIssuer.OtherTenant + "\"}";
        Assert.Throws<EntraOnboardingException>(() => EntraCellOnboarding.Parse(duplicate));
        var node = JsonNode.Parse(json)!.AsObject(); node["clientSecret"] = "synthetic-must-never-echo";
        var error = Assert.Throws<EntraOnboardingException>(() => EntraCellOnboarding.Parse(node.ToJsonString()));
        Assert.DoesNotContain("synthetic-must-never-echo", error.ToString());
        Assert.Throws<EntraOnboardingException>(() => EntraCellOnboarding.Parse(new string(' ', 65537)));
        Assert.Throws<EntraOnboardingException>(() => EntraCellOnboarding.Parse("{"));
    }

    static CellIdentityOptions Options(EntraCellOnboardingProposal proposal, IdentityTestIssuer issuer)
    {
        var json = new JsonSerializerOptions(JsonSerializerDefaults.Web) { Converters = { new System.Text.Json.Serialization.JsonStringEnumConverter() } };
        var identity = proposal.HostIdentity;
        return new(identity.GetProperty("cellId").GetString()!, identity.GetProperty("customerId").GetString()!, identity.GetProperty("audience").GetString()!,
            identity.GetProperty("trustedIssuersByTenant").Deserialize<Dictionary<string, string[]>>(json)!,
            identity.GetProperty("allowedClients").Deserialize<ApprovedCallingClient[]>(json)!, issuer.Options().SigningKeys);
    }
    internal static EntraCellOnboardingRequest Request()
    {
        var roles = FoundationRoles.All.Append("Worker.Execute").Order(StringComparer.Ordinal).Select((role, index) => new { role, id = $"00000000-0000-0000-0000-{index + 10:D12}" })
            .ToDictionary(p => p.role, p => p.id, StringComparer.Ordinal);
        return new("customer-a", "cell-a", IdentityTestIssuer.Tenant, IdentityTestIssuer.Audience, "00000000-0000-0000-0000-000000000001", roles,
            [IdentityTestIssuer.Client], [new(IdentityTestIssuer.WorkerClient, IdentityTestIssuer.WorkerSubject)]);
    }
}
