using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationStoreTests
{
    [Fact]
    public void Reopen_preserves_cell_grants_plan_and_artifact_without_regranting_bootstrap()
    {
        using var f = new StoreFixture(); var plan = f.Plan();
        var artifact = f.Store.StoreArtifact(f.Customer, "requester", "default", "dev", "main.bicep", "resource example"u8.ToArray());
        var reopened = f.Reopen(); reopened.BootstrapAdministrator("admin");
        Assert.Equal(plan.Digest, reopened.GetPlan(f.Customer, "requester", plan.Id).Digest);
        Assert.Equal(artifact.Sha256, reopened.GetArtifact(f.Customer, "requester", artifact.Id).Sha256);
        Assert.Equal(1, reopened.GetMemberships(f.Customer, "admin").Single().Version);
        Assert.Equal("BootstrapAlreadyCompleted", Assert.Throws<FoundationStoreException>(() => reopened.BootstrapAdministrator("intruder")).Code);
        f.Store.SetMembership(f.Customer, "admin", "second-admin", "default", "dev", ["CustomerAdmin"]);
        f.Store.SetMembership(f.Customer, "second-admin", "admin", "default", "dev", [], false);
        reopened.BootstrapAdministrator("admin");
        Assert.False(reopened.GetMemberships(f.Customer, "admin").Single().Enabled);
    }

    [Fact]
    public void Database_customer_binding_and_all_scope_selectors_fail_closed()
    {
        using var f = new StoreFixture(); var plan = f.Plan();
        Assert.Equal("CellBindingMismatch", Assert.Throws<FoundationStoreException>(() => new FoundationStore(f.Path, "cell-b", "customer-b")).Code);
        Action[] attacks = [() => f.Store.GetPlan("customer-b", "requester", plan.Id),
            () => f.Store.GetMemberships("customer-b", "requester"),
            () => f.Store.GetConnection("customer-b", "admin", "pipeline"),
            () => f.Store.ListJobs("customer-b", "admin"), () => f.Store.PendingOutbox("customer-b"),
            () => f.Store.ListAudit("customer-b", "admin"), () => f.Store.ClaimNext("customer-b", "worker", TimeSpan.FromMinutes(1))];
        foreach (var attack in attacks) Assert.Equal("CustomerScopeDenied", Assert.Throws<FoundationStoreException>(attack).Code);
    }

    [Fact]
    public void Same_Entra_actor_in_two_customers_does_not_inherit_roles()
    {
        const string actor = "user:11111111-1111-1111-1111-111111111111:22222222-2222-2222-2222-222222222222";
        using var a = new StoreFixture(); using var b = new StoreFixture("customer-b", "cell-b");
        a.Store.SetMembership(a.Customer, "admin", actor, "default", "dev", ["Requester"]);
        Assert.Single(a.Store.GetMemberships(a.Customer, actor)); Assert.Empty(b.Store.GetMemberships(b.Customer, actor));
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => b.Store.CreatePlan(b.Customer, actor, b.Input())).Code);
    }

    [Fact]
    public void Exact_project_and_environment_grants_and_self_elevation_are_enforced()
    {
        using var f = new StoreFixture();
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.CreatePlan(f.Customer, "requester", f.Input() with { EnvironmentId = "prod" })).Code);
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.CreatePlan(f.Customer, "requester", f.Input() with { ProjectId = "other" })).Code);
        Assert.Equal("SelfMembershipChangeDenied", Assert.Throws<FoundationStoreException>(() => f.Store.SetMembership(f.Customer, "requester", "requester", "default", "dev", ["CustomerAdmin"])).Code);
        Assert.Equal("InvalidRoles", Assert.Throws<FoundationStoreException>(() => f.Store.SetMembership(f.Customer, "admin", "support", "default", "dev", ["SupportDelegate"])).Code);
    }

    [Fact]
    public void Human_request_and_approval_boundary_cannot_be_bypassed_by_workload_membership()
    {
        using var f = new StoreFixture(); const string workload = "workload:11111111-1111-1111-1111-111111111111:22222222-2222-2222-2222-222222222222:33333333-3333-3333-3333-333333333333";
        f.Store.SetMembership(f.Customer, "admin", workload, "default", "dev", ["Requester", "Approver"]);
        Assert.Equal("HumanPrincipalRequired", Assert.Throws<FoundationStoreException>(() => f.Store.CreatePlan(f.Customer, workload, f.Input())).Code);
        var plan = f.Plan();
        Assert.Equal("HumanPrincipalRequired", Assert.Throws<FoundationStoreException>(() => f.Store.ApprovePlan(f.Customer, workload, plan.Id, plan.Digest)).Code);
        Assert.Equal("InvalidIdentifier", Assert.Throws<FoundationStoreException>(() => f.Store.SetMembership(f.Customer, "admin", "injected\n", "default", "dev", ["Viewer"])).Code);
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.GetPlan(f.Customer, "admin", plan.Id)).Code);
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.ListAudit(f.Customer, "admin")).Code);
    }

    [Fact]
    public void Scoped_reads_return_the_same_not_found_for_a_foreign_project_as_an_unknown_resource()
    {
        using var f = new StoreFixture(); var plan = f.Plan(); var job = f.Queue();
        var artifact = f.Store.StoreArtifact(f.Customer, "requester", "default", "dev", "main.bicep", "mock"u8.ToArray());
        Action[] attempts = [() => f.Store.GetPlan(f.Customer, "viewer", plan.Id, "other", "dev"),
            () => f.Store.GetJob(f.Customer, "viewer", job.Id, "other", "dev"),
            () => f.Store.GetArtifact(f.Customer, "viewer", artifact.Id, "other", "dev"),
            () => f.Store.GetConnection(f.Customer, "viewer", "pipeline", "other", "dev"),
            () => f.Store.GetPlan(f.Customer, "viewer", "unknown", "other", "dev")];
        foreach (var attempt in attempts) { var error = Assert.Throws<FoundationStoreException>(attempt); Assert.Equal("NotFound", error.Code); Assert.Equal(404, error.Status); }
    }

    [Fact]
    public void Approval_binds_exact_immutable_digest_and_cannot_be_self_approved()
    {
        using var f = new StoreFixture();
        f.Store.SetMembership(f.Customer, "admin", "requester", "default", "dev", ["Requester", "Approver"]);
        var plan = f.Plan();
        Assert.Equal("SelfApprovalDenied", Assert.Throws<FoundationStoreException>(() => f.Store.ApprovePlan(f.Customer, "requester", plan.Id, plan.Digest)).Code);
        Assert.Equal("DigestMismatch", Assert.Throws<FoundationStoreException>(() => f.Store.ApprovePlan(f.Customer, "approver", plan.Id, new string('0', 64))).Code);
        f.Store.ApprovePlan(f.Customer, "approver", plan.Id, plan.Digest);
        Assert.Equal("ApprovalAlreadyRecorded", Assert.Throws<FoundationStoreException>(() => f.Store.ApprovePlan(f.Customer, "approver", plan.Id, plan.Digest)).Code);
    }

    [Theory]
    [InlineData("{\"a\":1,\"A\":2}", "AmbiguousPayload")]
    [InlineData("{\"password\":\"example\"}", "CredentialPayloadDenied")]
    [InlineData("{\"nested\":{\"accessToken\":\"example\"}}", "CredentialPayloadDenied")]
    [InlineData("{\"value\":\"Bearer fake-authorization-value\"}", "CredentialPayloadDenied")]
    [InlineData("{\"value\":\"ghp_abcdefghijklmnopqrstuvwxyz0123456789\"}", "CredentialPayloadDenied")]
    [InlineData("[]", "ObjectPayloadRequired")]
    [InlineData("{", "InvalidJson")]
    public void Payload_boundary_rejects_ambiguous_or_credential_shaped_content(string payload, string code) =>
        Assert.Equal(code, Assert.Throws<FoundationStoreException>(() => FoundationStore.CanonicalJson(payload)).Code);

    [Fact]
    public void Canonical_payload_and_limits_are_deterministic()
    {
        Assert.Equal("{\"a\":{\"b\":true},\"z\":1}", FoundationStore.CanonicalJson("{\"z\":1, \"a\":{\"b\":true}}"));
        Assert.Equal("PayloadTooLarge", Assert.Throws<FoundationStoreException>(() => FoundationStore.CanonicalJson("{\"x\":\"" + new string('x', 70000) + "\"}")).Code);
        using var f = new StoreFixture();
        Assert.Equal("InvalidPlan", Assert.Throws<FoundationStoreException>(() => f.Store.CreatePlan(f.Customer, "requester", f.Input() with { ExpiresAt = f.Clock.GetUtcNow().AddHours(2) })).Code);
    }

    [Theory]
    [InlineData("UPDATE plans SET payload='{\"changed\":true}'", "DigestMismatch")]
    [InlineData("UPDATE connections SET configuration='{\"repository\":\"other\"}'", "DigestMismatch")]
    [InlineData("UPDATE approvals SET digest='wrong'", "ApprovalDigestMismatch")]
    public void Dispatch_revalidates_persisted_plan_connection_and_approval_digests(string mutation, string code)
    {
        using var f = new StoreFixture(); var job = f.Queue(); var claimed = f.Store.ClaimNext(f.Customer, "worker", TimeSpan.FromMinutes(1))!;
        f.Execute(mutation);
        Assert.Equal(code, Assert.Throws<FoundationStoreException>(() => f.Store.StartDispatch(f.Customer, job.Id, "worker", claimed.Fence)).Code);
        Assert.Equal("Blocked", f.Store.GetJob(f.Customer, "operator", job.Id).State);
    }
}

internal sealed class StoreClock : TimeProvider
{
    DateTimeOffset current = new(2026, 10, 24, 18, 0, 0, TimeSpan.Zero);
    public override DateTimeOffset GetUtcNow() => current;
    public void Advance(TimeSpan elapsed) => current += elapsed;
}

internal sealed class StoreFixture : IDisposable
{
    readonly string directory = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PlatformStudio-FoundationTests", Guid.NewGuid().ToString("N"));
    public string Path { get; }
    public string Customer { get; }
    public string Cell { get; }
    public StoreClock Clock { get; } = new();
    public FoundationStore Store { get; }
    public StoreFixture(string customer = "customer-a", string cell = "cell-a")
    {
        Customer = customer; Cell = cell; Path = System.IO.Path.Combine(directory, "cell.sqlite"); Store = Reopen();
        Store.BootstrapAdministrator("admin");
        foreach (var pair in new[] { ("requester", "Requester"), ("approver", "Approver"), ("operator", "Operator"), ("auditor", "Auditor"), ("viewer", "Viewer") })
            Store.SetMembership(Customer, "admin", pair.Item1, "default", "dev", [pair.Item2]);
        Store.PutConnection(Customer, "admin", "pipeline", "github", "{\"owner\":\"example\",\"repository\":\"workloads\",\"ref\":\"refs/heads/main\"}");
    }
    public FoundationStore Reopen() => new(Path, Cell, Customer, Clock);
    public FoundationPlanInput Input(bool changes = true) => new("default", "dev", "pipeline", "pipeline-dispatch",
        "{\"parameters\":{\"location\":\"eastus2\",\"instanceCount\":2},\"simulated\":true}", changes,
        Clock.GetUtcNow().AddMinutes(20), new string('a', 40), new string('b', 64));
    public FoundationPlan Plan(bool changes = true) => Store.CreatePlan(Customer, "requester", Input(changes));
    public FoundationJob Queue(string key = "request-1", bool changes = true)
    {
        var plan = Plan(changes); if (changes) Store.ApprovePlan(Customer, "approver", plan.Id, plan.Digest);
        return Store.EnqueuePlan(Customer, "operator", plan.Id, plan.Digest, key);
    }
    public void Execute(string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString()); db.Open();
        using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery();
    }
    public long Scalar(string sql)
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path, Pooling = false }.ToString()); db.Open();
        using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar());
    }
    public void Dispose()
    {
        foreach (var suffix in new[] { "", "-wal", "-shm" }) if (File.Exists(Path + suffix)) File.Delete(Path + suffix);
        if (Directory.Exists(directory)) Directory.Delete(directory, false);
    }
}
