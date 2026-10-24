using System.Security.Cryptography;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.IdentityModel.Tokens;
using SelfService.Foundation;

namespace SelfService.Foundation.Host;

public sealed record FoundationHostOptions(CellIdentityOptions Identity, string DatabasePath,
    string BootstrapActorKey, string ProjectId = "default", string EnvironmentId = "dev")
{
    public bool EnableWorker { get; init; }
    public IPipelineTransport? DisconnectedTransport { get; init; }
    public TimeSpan PollInterval { get; init; } = TimeSpan.FromSeconds(1);
    public string? RepositoryRoot { get; init; }
    public OidcValidationOptions? Oidc { get; init; }
    public IOidcDocumentTransport? OidcTransport { get; init; }
    public IFoundationModelClient? ModelClient { get; init; }
    public string? BootstrapApproverKey { get; init; }
    public int ListenPort { get; init; } = 5107;
    public EntitlementTrustKey[] EntitlementTrustKeys { get; init; } = [];
    public bool RequireSignedEntitlement { get; init; }
}

public static class FoundationHostConfiguration
{
    // The local file contains public identity metadata and opaque references, never access tokens or private keys.
    public static FoundationHostOptions Load(string? configPath = null)
    {
        var path = configPath ?? Environment.GetEnvironmentVariable("PLATFORM_FOUNDATION_CONFIG");
        if (string.IsNullOrWhiteSpace(path)) throw new InvalidOperationException("PLATFORM_FOUNDATION_CONFIG must name a cell configuration file.");
        var file = JsonSerializer.Deserialize<CellConfiguration>(File.ReadAllText(path), FoundationApplication.Json)
            ?? throw new InvalidOperationException("Cell configuration is required.");
        var keys = file.PublicKeys.Select(k =>
        {
            var pem = k.Pem?.Trim() ?? "";
            if (!PemEncoding.TryFind(pem, out var block) || block.Location.GetOffsetAndLength(pem.Length) != (0, pem.Length) ||
                pem[block.Label] is not ("PUBLIC KEY" or "RSA PUBLIC KEY"))
                throw new ArgumentException("Only public RSA keys may appear in cell metadata.");
            using var rsa = RSA.Create(); rsa.ImportFromPem(pem);
            return (SecurityKey)new RsaSecurityKey(rsa.ExportParameters(false)) { KeyId = k.KeyId };
        }).ToArray();
        var identity = new CellIdentityOptions(file.CellId, file.CustomerId, file.Audience,
            file.TrustedIssuersByTenant, file.AllowedClients, keys);
        _ = new IdentityTokenValidator(identity);
        if (file.ListenPort is < 1024 or > 65535) throw new ArgumentException("A nonprivileged loopback port is required.");
        if (!CustomerContextResolver.ValidId(file.ProjectId) || !CustomerContextResolver.ValidId(file.EnvironmentId) ||
            !Regex.IsMatch(file.BootstrapActorKey ?? "", "\\Auser:[a-f0-9-]{36}:[a-f0-9-]{36}\\z", RegexOptions.CultureInvariant) ||
            !Guid.TryParseExact(file.BootstrapActorKey!.Split(':')[1], "D", out _) ||
            !Guid.TryParseExact(file.BootstrapActorKey.Split(':')[2], "D", out var bootstrapSubject) || bootstrapSubject == Guid.Empty ||
            !file.TrustedIssuersByTenant.ContainsKey(file.BootstrapActorKey.Split(':')[1]) ||
            !file.AllowedClients.Any(c => c.TenantId == file.BootstrapActorKey.Split(':')[1] && c.Kind == PrincipalKind.User))
            throw new ArgumentException("An exact customer scope and trusted human bootstrap identity are required.");
        var authorities = file.EnableEntraDiscovery ? file.TrustedIssuersByTenant.Keys.Select(t => OidcTenantAuthority.PublicEntra(t)).ToArray() : [];
        if (file.BootstrapApproverKey is not null)
        {
            FoundationApplication.ValidateMembershipActor(identity, file.BootstrapApproverKey);
            if (!file.BootstrapApproverKey.StartsWith("user:", StringComparison.Ordinal) || file.BootstrapApproverKey == file.BootstrapActorKey ||
                !file.AllowedClients.Any(c => c.Kind == PrincipalKind.User && c.TenantId == file.BootstrapApproverKey.Split(':')[1]))
                throw new ArgumentException("An independent human bootstrap reviewer is required.");
        }
        return new(identity, Path.GetFullPath(file.DatabasePath, Path.GetDirectoryName(Path.GetFullPath(path))!),
            file.BootstrapActorKey, file.ProjectId, file.EnvironmentId)
        {
            EnableWorker = file.MockEndpoint is not null,
            RepositoryRoot = file.RepositoryRoot is null ? null : Path.GetFullPath(file.RepositoryRoot, Path.GetDirectoryName(Path.GetFullPath(path))!),
            Oidc = file.EnableEntraDiscovery ? new(identity, authorities) : null,
            OidcTransport = file.EnableEntraDiscovery ? new EntraOidcHttpsTransport(authorities) : null,
            BootstrapApproverKey = file.BootstrapApproverKey,
            ListenPort = file.ListenPort,
            EntitlementTrustKeys = file.EntitlementTrustKeys ?? [],
            RequireSignedEntitlement = file.RequireSignedEntitlement,
            DisconnectedTransport = file.MockEndpoint is null ? null : new PipelineLoopbackTransport(new Uri(file.MockEndpoint))
        };
    }

    public sealed record PublicKey(string KeyId, string Pem);
    public sealed record CellConfiguration(string CellId, string CustomerId, string Audience, string DatabasePath,
        string BootstrapActorKey, string ProjectId, string EnvironmentId,
        Dictionary<string, string[]> TrustedIssuersByTenant, ApprovedCallingClient[] AllowedClients,
        PublicKey[] PublicKeys, string? MockEndpoint = null, string? RepositoryRoot = null, bool EnableEntraDiscovery = false, string? BootstrapApproverKey = null, int ListenPort = 5107,
        EntitlementTrustKey[]? EntitlementTrustKeys = null, bool RequireSignedEntitlement = false);
}

internal sealed class StoreMembershipSource(FoundationStore store) : ICustomerMembershipSource
{
    public IReadOnlyList<MembershipGrant> Find(string customerId, string actorKey) =>
        store.GetMemberships(customerId, actorKey).Select(m => new MembershipGrant(m.CustomerId, m.CellId,
            m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version, m.Enabled, m.ValidUntilUtc, m.Purpose, m.ApprovalId)).ToArray();
}

public sealed record MembershipCommand(string ActorKey, string[] Roles, bool Enabled = true);
public sealed record ConnectionCommand(string Provider, string Organization, string? Project, string Repository,
    string PipelineOrWorkflowId, string[] AllowedRefs, string[] AllowedEnvironments, string[] AllowedInputKeys,
    string AuthProfileId, bool EnforcesSourceRevision = false, int? PipelineVersion = null, bool Enabled = true);
public sealed record PlanCommand(string ConnectionId, string Ref, string SourceRevision,
    Dictionary<string, string> Inputs, int LifetimeMinutes = 30);
public sealed record ExactPlanCommand(string Digest);
public sealed record EnqueueCommand(string Digest, string IdempotencyKey);
public sealed record ReconcileCommand(string Outcome, JsonElement Evidence);
public sealed record ArtifactCommand(string Name, string Content);
public sealed record FoundationToolCommand(string Name, JsonElement Arguments);
