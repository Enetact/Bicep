using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace SelfService.Foundation;

public sealed class EntraOnboardingException() : Exception("The public identity onboarding contract is invalid.");
public sealed record EntraWorkerClient(string ClientId, string ServicePrincipalObjectId);
public sealed record EntraCellOnboardingRequest(string CustomerId, string CellId, string TenantId, string ApiClientId,
    string ScopeDefinitionId, Dictionary<string, string> RoleDefinitionIds, string[] HumanClientIds,
    EntraWorkerClient[] WorkerClients, string DisplayName = "Platform Studio customer cell");

// Detached public metadata, never an identity grant, Graph PATCH request or live acceptance result.
public sealed class EntraCellOnboardingProposal
{
    internal EntraCellOnboardingProposal(string json)
    {
        CanonicalJson = json;
        Sha256 = Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(json)));
    }
    public string CanonicalJson { get; }
    public string Sha256 { get; }
    public bool IdentityGrantAuthorized => false;
    public bool ExecutionAuthorized => false;
    public bool ProviderPermissionsVerified => false;
    public bool RuntimeAcceptanceVerified => false;
    public JsonElement ApplicationDefinition => Part("applicationDefinitionFragment");
    public JsonElement HostIdentity => Part("hostIdentity");
    JsonElement Part(string name) { using var doc = JsonDocument.Parse(CanonicalJson); return doc.RootElement.GetProperty(name).Clone(); }
    public override string ToString() => "Unqualified Entra onboarding proposal " + Sha256;
}

public static class EntraCellOnboarding
{
    public const string Schema = "platform-studio.entra-cell-onboarding.v1";
    public const string DelegatedScope = "platform.access", WorkloadRole = "Worker.Execute";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 8, RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true,
        UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        Converters = { new JsonStringEnumConverter() }
    };

    public static EntraCellOnboardingProposal Parse(string publicRequestJson)
    {
        try
        {
            if (publicRequestJson is null || Encoding.UTF8.GetByteCount(publicRequestJson) > 65536) throw new EntraOnboardingException();
            using var doc = JsonDocument.Parse(publicRequestJson, new JsonDocumentOptions { MaxDepth = 8 });
            Unique(doc.RootElement);
            return Create(doc.RootElement.Deserialize<EntraCellOnboardingRequest>(Json) ?? throw new EntraOnboardingException());
        }
        catch (Exception e) when (e is JsonException or ArgumentException or NullReferenceException)
        { throw new EntraOnboardingException(); }
    }

    public static EntraCellOnboardingProposal Create(EntraCellOnboardingRequest request)
    {
        if (request is null || !CustomerContextResolver.ValidId(request.CustomerId) || !CustomerContextResolver.ValidId(request.CellId) ||
            request.DisplayName is not { Length: > 0 and <= 120 } || request.DisplayName.Any(c => !(char.IsAsciiLetterOrDigit(c) || c is ' ' or '-' or '_' or '.')) ||
            string.IsNullOrWhiteSpace(request.DisplayName) || request.RoleDefinitionIds is null ||
            request.HumanClientIds is not { Length: > 0 and <= 16 } || request.WorkerClients is not { Length: <= 16 }) throw new EntraOnboardingException();
        var tenant = Id(request.TenantId); var api = Id(request.ApiClientId); var scope = Id(request.ScopeDefinitionId);
        var roles = FoundationRoles.All.Append(WorkloadRole).Order(StringComparer.Ordinal).ToArray();
        if (request.RoleDefinitionIds.Count != roles.Length || !request.RoleDefinitionIds.Keys.Order(StringComparer.Ordinal).SequenceEqual(roles, StringComparer.Ordinal))
            throw new EntraOnboardingException();
        var definitions = roles.ToDictionary(r => r, r => Id(request.RoleDefinitionIds[r]), StringComparer.Ordinal);
        if (definitions.Values.Append(scope).Distinct(StringComparer.Ordinal).Count() != roles.Length + 1) throw new EntraOnboardingException();
        var humans = request.HumanClientIds.Select(Id).Order(StringComparer.Ordinal).ToArray();
        var workers = request.WorkerClients.Select(w => w is null ? throw new EntraOnboardingException() :
            new EntraWorkerClient(Id(w.ClientId), Id(w.ServicePrincipalObjectId))).OrderBy(w => w.ClientId, StringComparer.Ordinal).ToArray();
        if (humans.Distinct(StringComparer.Ordinal).Count() != humans.Length ||
            workers.Select(w => w.ClientId).Distinct(StringComparer.Ordinal).Count() != workers.Length ||
            workers.Select(w => w.ServicePrincipalObjectId).Distinct(StringComparer.Ordinal).Count() != workers.Length ||
            humans.Intersect(workers.Select(w => w.ClientId), StringComparer.Ordinal).Any() ||
            humans.Contains(api, StringComparer.Ordinal) || workers.Any(w => w.ClientId == api)) throw new EntraOnboardingException();
        var callers = humans.Select(h => new ApprovedCallingClient(tenant, h, PrincipalKind.User))
            .Concat(workers.Select(w => new ApprovedCallingClient(tenant, w.ClientId, PrincipalKind.Workload, w.ServicePrincipalObjectId))).ToArray();
        var artifact = new
        {
            schema = Schema, request.CustomerId, request.CellId, tenantId = tenant, apiClientId = api,
            applicationDefinitionFragment = new
            {
                displayName = request.DisplayName.Trim(), signInAudience = "AzureADMyOrg", identifierUris = new[] { "api://" + api },
                api = new
                {
                    requestedAccessTokenVersion = 2,
                    oauth2PermissionScopes = new[] { new { id = scope, value = DelegatedScope, type = "Admin", isEnabled = true,
                        adminConsentDisplayName = "Access the assigned Platform Studio customer cell",
                        adminConsentDescription = "Access this cell as the signed-in user, subject to current scoped membership and independent approval." } }
                },
                appRoles = roles.Select(r => new { id = definitions[r], value = r, displayName = r, isEnabled = true,
                    allowedMemberTypes = new[] { r == WorkloadRole ? "Application" : "User" },
                    description = Description(r) }).ToArray(),
                optionalClaims = new { accessToken = new[] { new { name = "idtyp", essential = false, additionalProperties = Array.Empty<string>() } } }
            },
            servicePrincipalRequirements = new { appRoleAssignmentRequired = true, observed = false },
            hostIdentity = new
            {
                request.CustomerId, request.CellId, audience = api,
                trustedIssuersByTenant = new SortedDictionary<string, string[]>(StringComparer.Ordinal) { [tenant] = [$"https://login.microsoftonline.com/{tenant}/v2.0"] },
                allowedClients = callers
            },
            claimRequirements = new { delegatedScope = DelegatedScope, workloadRole = WorkloadRole, workloadIdtyp = "app" },
            proposedClientPermissions = humans.Select(h => new { clientId = h, resourceAppId = api, permissionId = scope, type = "Scope" })
                .Concat(workers.Select(w => new { clientId = w.ClientId, resourceAppId = api, permissionId = definitions[WorkloadRole], type = "Role" })).ToArray(),
            constraints = new
            {
                applicationFragmentIsNotPatchRequest = true, existingApplicationEvidenceAndMergeRequired = true,
                explicitDefinitionIdsMustBePreserved = true, currentScopedMembershipRequired = true,
                independentHumanApprovalRequired = true, supportRequiresReviewedTimeBoundGrant = true,
                signingKeysAndDiscoveryNotProvisioned = true, workloadFederationNotProvisioned = true,
                cloudAndRepositoryPermissionsNotIncluded = true
            },
            identityGrantAuthorized = false, executionAuthorized = false, providerPermissionsVerified = false,
            runtimeAcceptanceVerified = false
        };
        return new(JsonSerializer.Serialize(artifact, Json));
    }

    static string Description(string role) => role switch
    {
        WorkloadRole => "Worker API admission only; no human approvals or downstream cloud permission.",
        FoundationRoles.SupportDelegate => "Read-only support only with a current reviewed purpose-bound grant, limited to one day.",
        FoundationRoles.CustomerAdmin => "Reviewed cell administration; no implicit operational or cloud permission.",
        _ => "Role ceiling for current scoped membership; it grants no membership, approval or downstream permission by itself."
    };
    static string Id(string? value) => value is { Length: 36 } && Guid.TryParseExact(value, "D", out var id) && id != Guid.Empty
        ? id.ToString("D") : throw new EntraOnboardingException();
    static void Unique(JsonElement value)
    {
        if (value.ValueKind == JsonValueKind.Object)
        {
            var names = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (var property in value.EnumerateObject()) { if (!names.Add(property.Name)) throw new EntraOnboardingException(); Unique(property.Value); }
        }
        else if (value.ValueKind == JsonValueKind.Array) foreach (var element in value.EnumerateArray()) Unique(element);
    }
}
