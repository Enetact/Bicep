using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
internal sealed class RestrictedRecoveryReviewFixture : IDisposable
{
    internal const string RequesterSubject = "dddddddd-dddd-dddd-dddd-dddddddddddd";
    internal const string Requester = "user:" + IdentityTestIssuer.Tenant + ":" + RequesterSubject;
    internal const string Executor = MaintenanceExecutorFixture.Operator, Approver = MaintenanceExecutorFixture.Approver;
    readonly IdentityTestIssuer issuer = new();
    internal RestrictedStageFixture Stage { get; }
    internal CredentialFixture Cell => Stage.Cell;
    internal CredentialClock Clock => Cell.Clock;
    internal CredentialTestKeys DestinationKeys => Stage.Destination;
    internal FoundationStore Store { get; }
    internal string Database => Path.Combine(Cell.Root, "restricted-review.db");
    internal string Customer => Cell.Scope.CustomerId;
    internal string CellId => Cell.Scope.CellId;
    internal string Instance { get; }
    internal string StageRoot => Stage.StageRoot;
    internal string Guard => Stage.GuardPath;
    internal CredentialRestrictedRecoveryStagePlan Plan => Stage.Plan;
    internal CredentialRestrictedRecoveryStageReceipt Receipt => Stage.Receipt;
    internal RestrictedRecoveryReviewFixture(string provider = PipelineRequestFactory.GitHub, string mode = "mixed")
    {
        Stage = new(provider, mode); Store = new(Database, CellId, Customer, Clock);
        Store.BootstrapAdministrator("admin");
        Store.SetMembership(Customer, "admin", "approver", "default", "dev", ["Approver"]);
        var onboarding = Store.PrepareScopeOnboarding(Customer, "admin", "other", "qa", "other-admin", "Synthetic restricted review scope");
        Store.ApproveAdministrativePlan(Customer, "approver", onboarding.Id, onboarding.Digest);
        Store.ExecuteScopeOnboarding(Customer, "admin", onboarding.Id, onboarding.Digest);
        Grant(Requester, ["Operator"]); Grant(Executor, ["Operator"]); Grant(Approver, ["Approver"]); Grant(MaintenanceExecutorFixture.Auditor, ["Auditor"]);
        Instance = Store.GetCellInstanceIdentity().DatabaseInstanceId;
    }
    internal Task Initialize() => Stage.Initialize();
    internal string Child(string name) => Path.Combine(Cell.Root, name);
    internal void Grant(string actor, string[] roles, FoundationCredentialRecoveryScope? scope = null, bool enabled = true)
    {
        var scopes = scope is null ? new[] { new FoundationCredentialRecoveryScope("default", "dev"), new FoundationCredentialRecoveryScope("other", "qa") } : [scope];
        foreach (var current in scopes) Store.SetMembership(Customer, current.ProjectId == "default" ? "admin" : "other-admin", actor, current.ProjectId, current.EnvironmentId, roles, enabled);
    }
    internal async Task<IReadOnlyList<CustomerContext>> Contexts(string capability, string? actor = null, TimeSpan? lifetime = null,
        FoundationStore? store = null, string? signedRole = null)
    {
        var role = signedRole ?? (capability == FoundationCapabilities.CredentialRecoveryApprove ? "Approver" : capability == FoundationCapabilities.CredentialRecoveryRead ? "Auditor" : "Operator");
        var subject = actor ?? (capability == FoundationCapabilities.CredentialRecoveryPrepare ? RequesterSubject : capability == FoundationCapabilities.CredentialRecoveryApprove ? IdentityTestIssuer.OtherSubject : capability == FoundationCapabilities.CredentialRecoveryRead ? IdentityTestIssuer.WorkerSubject : IdentityTestIssuer.Subject);
        var prefix = "user:" + IdentityTestIssuer.Tenant + ":"; if (subject.StartsWith(prefix, StringComparison.Ordinal)) subject = subject[prefix.Length..];
        var options = issuer.Options(Customer, CellId); var claims = issuer.UserClaims(role, subject);
        claims["iat"] = claims["nbf"] = Clock.Now.AddSeconds(-30).ToUnixTimeSeconds(); claims["exp"] = Clock.Now.Add(lifetime ?? TimeSpan.FromMinutes(10)).ToUnixTimeSeconds();
        var principal = await new IdentityTokenValidator(options, Clock).ValidateAsync(issuer.Token(claims));
        var resolver = new CustomerContextResolver(options, new Memberships(store ?? Store), Clock);
        var scopes = Plan.AffectedScopes.Count > 0 ? Plan.AffectedScopes : new[] { new FoundationCredentialRecoveryScope("default", "dev") };
        return scopes.Select(scope => resolver.Require(principal, Customer, scope.ProjectId, scope.EnvironmentId, capability)).ToArray();
    }
    internal FoundationCredentialRestrictedRecoveryStageReviewCoordinator Coordinator(ICredentialKeyProvider? keys = null, TimeSpan? timeout = null,
        FoundationStore? store = null, TimeProvider? verifierClock = null, string? instance = null) =>
        new(store ?? Store, new(keys ?? DestinationKeys, verifierClock ?? Clock), instance ?? Instance, timeout);
    internal async Task<FoundationCredentialRestrictedRecoveryStageReview> Prepare(FoundationCredentialRestrictedRecoveryStageReviewCoordinator? coordinator = null) =>
        await (coordinator ?? Coordinator()).PrepareAsync(await Contexts(FoundationCapabilities.CredentialRecoveryPrepare), Plan, Plan.Sha256, Receipt.Sha256,
            "Synthetic exact restricted stage review", Clock.Now.AddMinutes(5));
    internal async Task<FoundationCredentialRestrictedRecoveryStageReview> Approved()
    { var review = await Prepare(); return Store.ApproveCredentialRestrictedRecoveryStageReview(await Contexts(FoundationCapabilities.CredentialRecoveryApprove), review.Id, review.Digest); }
    internal async Task<FoundationCredentialRestrictedRecoveryStageReview> Execute(FoundationCredentialRestrictedRecoveryStageReview review,
        FoundationCredentialRestrictedRecoveryStageReviewCoordinator? coordinator = null, IReadOnlyList<CustomerContext>? contexts = null, CancellationToken token = default) =>
        await (coordinator ?? Coordinator()).ExecuteAsync(contexts ?? await Contexts(FoundationCapabilities.CredentialRecoveryVerify), review.Id, review.Digest,
            Plan, Plan.Sha256, Receipt.Sha256, token);
    internal async Task<FoundationCredentialRestrictedRecoveryStageReview> Read(string id, FoundationStore? store = null) =>
        (store ?? Store).GetCredentialRestrictedRecoveryStageReview(await Contexts(FoundationCapabilities.CredentialRecoveryRead, store: store), id);
    internal void ClearKeys() { Cell.Keys.Requests.Clear(); DestinationKeys.Requests.Clear(); }
    internal long Admissions => Number("SELECT count(*) FROM credential_restricted_recovery_stage_review_executions");
    internal long Count(string action) => Number("SELECT count(*) FROM audit WHERE action='" + action + "'");
    internal long Number(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
    internal void Sql(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
    internal SqliteConnection Open() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false, DefaultTimeout = 1 }.ToString()); db.Open(); return db; }
    internal string[] StageAndSourceSnapshot() => new[] { StageRoot, Stage.Options.RootPath, Stage.SourceAuthority }.Where(Directory.Exists)
        .SelectMany(root => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root)).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(Cell.Root, path) + ":" + Hash((Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorBinaryForm()) + ":" +
            (Directory.Exists(path) ? "directory" : Hash(File.ReadAllBytes(path)))).ToArray();
    internal void PrivateFile(string path, byte[] bytes)
    {
        using var identity = WindowsIdentity.GetCurrent(); var security = new FileSecurity(); security.SetOwner(identity.User!); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        using var file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.Read, 4096, FileOptions.WriteThrough, security); file.Write(bytes); file.Flush(true);
    }
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    public void Dispose() { issuer.Dispose(); Stage.Dispose(); }
    sealed class Memberships(FoundationStore store) : ICustomerMembershipSource
    { public IReadOnlyList<MembershipGrant> Find(string customer, string actor) => store.GetCredentialMaintenanceMemberships(customer, actor).Select(m => new MembershipGrant(m.CustomerId, m.CellId, m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version, m.Enabled)).ToArray(); }
}
