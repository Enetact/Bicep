using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationBoundNamespaceCoreTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub)]
    [InlineData(PipelineRequestFactory.AzureDevOps)]
    public async Task Restricted_source_uses_actual_bound_dispatch_and_current_scoped_rollback(string provider)
    {
        using var f = await BoundNamespaceCoreFixture.Create(provider);
        using var handler = new BoundCaptureHandler(); using var transport = f.Transport(handler);
        f.Cell.Keys.Requests.Clear();
        var response = await transport.SendAsync(f.Wire, default);
        Assert.Equal(200, response.StatusCode); Assert.Equal(1, handler.Calls); Assert.True(handler.ContentDisposed);
        Assert.Equal("Bearer " + CredentialFixture.Token, handler.Authorization);
        Assert.Single(f.Cell.Keys.Requests);
        var state = await f.Vault.InspectRollbackStateAsync(f.Cell.Scope.AuthProfileId);
        Assert.Equal(1, state.CommittedGeneration); Assert.Null(state.Pending);
        Assert.Equal(f.Namespace, f.Store.GetCredentialRuntimeBindingStatus().CurrentBinding!.Namespace);
    }

    [Fact]
    public async Task Direct_reads_refuse_before_keys_while_explicit_metadata_remains_available()
    {
        using var f = await BoundNamespaceCoreFixture.Create(); f.Cell.Keys.Requests.Clear();
        Assert.Equal("credential-bound-dispatch-session-required", (await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ResolveAsync(f.Cell.Scope))).Code);
        Assert.Equal("credential-bound-dispatch-session-required", (await Assert.ThrowsAsync<CredentialException>(() => f.Vault.RevalidateAsync(f.Cell.Scope, 1))).Code);
        Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(f.Namespace, f.Vault.DescribeNamespace());
        var status = await f.Vault.InspectRollbackStateAsync(f.Cell.Scope.AuthProfileId);
        Assert.Equal(1, status.CommittedGeneration); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(1, (await f.Vault.GetProfileAsync(f.Cell.Scope.AuthProfileId)).Generation);
    }

    [Fact]
    public async Task Generic_transport_refuses_before_a_valid_permit_callback()
    {
        using var f = await BoundNamespaceCoreFixture.Create(); var permits = new CountingPermit(f);
        using var handler = new BoundCaptureHandler();
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, permits, handler, f.Cell.Clock);
        f.Cell.Keys.Requests.Clear();
        var error = await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(f.Wire, default));
        Assert.Equal("pipeline-credential-authority-denied", error.ReasonCode);
        Assert.Equal(0, permits.Calls); Assert.Equal(0, handler.Calls); Assert.Empty(f.Cell.Keys.Requests);
    }

    [Fact]
    public async Task Immutable_metadata_sharing_failure_is_opaque_and_cannot_request_a_source_key()
    {
        using var f = await BoundNamespaceCoreFixture.Create(); f.Cell.Keys.Requests.Clear();
        using var blocker = new FileStream(Path.Combine(f.Options.RootPath, ".credential-namespace.json"), FileMode.Open, FileAccess.Read, FileShare.None);
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ResolveAsync(f.Cell.Scope));
        Assert.Equal("credential-namespace-custody-unavailable", error.Code);
        Assert.DoesNotContain(f.Cell.Root, error.ToString(), StringComparison.OrdinalIgnoreCase);
        Assert.Empty(f.Cell.Keys.Requests);
    }

    [Theory]
    [InlineData("weak")]
    [InlineData("strong")]
    [InlineData("scoped")]
    public void Existing_factories_cannot_implicitly_provision_restricted_namespaces(string factory)
    {
        using var cell = new CredentialFixture(); var options = BoundNamespaceCoreFixture.OptionsFor(cell);
        var error = Assert.Throws<CredentialException>(() => factory switch
        {
            "weak" => CredentialVault.Provision(options, cell.Keys, cell.Clock),
            "strong" => CredentialVault.ProvisionStrong(options, cell.Keys, cell.Clock),
            _ => CredentialVault.ProvisionStrongScoped(options, cell.Keys, cell.Clock)
        });
        Assert.Equal("credential-bound-dispatch-explicit-provision-required", error.Code);
        Assert.False(Path.Exists(options.RootPath)); Assert.False(Path.Exists(options.RollbackProtection!.AuthorityDirectory));
        Assert.Empty(cell.Keys.Requests);
    }

    [Fact]
    public void Explicit_factory_requires_both_restricted_profile_and_scoped_rollback_before_filesystem_changes()
    {
        using var cell = new CredentialFixture(); var options = BoundNamespaceCoreFixture.OptionsFor(cell);
        foreach (var invalid in new[] { options with { AccessProfile = CredentialVaultAccessProfile.General },
            options with { RollbackProtection = new(options.RollbackProtection!.AuthorityDirectory) } })
        {
            Assert.Equal("credential-bound-dispatch-profile-invalid", Assert.Throws<CredentialException>(() =>
                CredentialVault.ProvisionStrongBoundDispatch(invalid, cell.Keys, cell.Clock)).Code);
            Assert.False(Path.Exists(options.RootPath)); Assert.False(Path.Exists(options.RollbackProtection!.AuthorityDirectory));
        }
        Assert.Empty(cell.Keys.Requests);
    }

    [Fact]
    public async Task General_option_serialization_and_existing_unrestricted_source_remain_compatible()
    {
        using var cell = new CredentialFixture(); var json = JsonSerializer.Serialize(cell.Options, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        Assert.DoesNotContain("accessProfile", json, StringComparison.Ordinal);
        Assert.Equal(json, JsonSerializer.Serialize(cell.Options with { AccessProfile = CredentialVaultAccessProfile.General }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        await cell.StoreToken(); using var lease = await cell.Vault.ResolveAsync(cell.Scope);
        Assert.Equal(1, lease.Profile.Generation);
    }

    [Fact]
    public async Task Nonexporting_explicit_owner_revocation_remains_available_without_relaxing_direct_reads()
    {
        using var f = await BoundNamespaceCoreFixture.Create();
        var before = await f.Vault.InspectMaintenanceAsync(f.Cell.Scope.AuthProfileId);
        var revoked = await f.Vault.RevokeAsync(f.Cell.Scope.AuthProfileId, 1);
        Assert.False(revoked.Enabled); Assert.Equal(2, revoked.Generation);
        var after = await f.Vault.InspectMaintenanceAsync(f.Cell.Scope.AuthProfileId);
        Assert.Equal(before.ProfileScopeSha256, after.ProfileScopeSha256); Assert.False(after.Enabled);
        f.Cell.Keys.Requests.Clear();
        Assert.Equal("credential-bound-dispatch-session-required", (await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ResolveAsync(f.Cell.Scope))).Code);
        Assert.Empty(f.Cell.Keys.Requests);
    }

    sealed class CountingPermit(BoundNamespaceCoreFixture fixture) : ICredentialDispatchPermitSource
    {
        internal int Calls { get; private set; }
        public Task<CredentialDispatchPermit?> AuthorizeAsync(PipelineWireRequest wire, CancellationToken cancellationToken)
        {
            Calls++; return Task.FromResult<CredentialDispatchPermit?>(CredentialDispatchPermit.FromDurableDispatch(fixture.Dispatch, wire, fixture.Cell.Scope, fixture.Cell.Clock.Now));
        }
    }
}

internal sealed class BoundNamespaceCoreFixture : IDisposable
{
    internal CredentialFixture Cell { get; }
    internal FoundationStore Store { get; }
    internal CredentialVaultOptions Options { get; }
    internal CredentialVault Vault { get; }
    internal FoundationCellActivationAuthority Authority { get; }
    internal FoundationCellActivationAuthorityIdentity AuthorityIdentity { get; }
    internal CredentialVaultNamespaceIdentity Namespace { get; }
    internal string Instance { get; }
    internal FoundationCredentialRuntimeBinding Binding { get; }
    internal FoundationDispatch Dispatch { get; private set; } = null!;
    internal PipelineWireRequest Wire { get; private set; } = null!;
    internal EntitlementVerifier Verifier { get; }
    internal FoundationCredentialBoundDispatch DispatchPins => new(Dispatch.Job.Id, Dispatch.Job.WorkerId!, Dispatch.Job.Fence, Cell.Scope,
        Dispatch.Plan.Digest, Dispatch.Plan.SourceRevision, Dispatch.Plan.CatalogDigest);

    internal static CredentialVaultOptions OptionsFor(CredentialFixture cell) => cell.Options with
    {
        RootPath = Path.Combine(cell.Root, "restricted-vault"), AccessProfile = CredentialVaultAccessProfile.FoundationBoundDispatchOnly,
        RollbackProtection = new(Path.Combine(cell.Root, "restricted-rollback")) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 }
    };

    BoundNamespaceCoreFixture(string provider)
    {
        Cell = new(provider); var database = Path.Combine(Cell.Root, "restricted-cell.db");
        Store = new(database, Cell.Scope.CellId, Cell.Scope.CustomerId, Cell.Clock); Store.BootstrapAdministrator("admin");
        foreach (var (actor, role) in new[] { ("requester", "Requester"), ("approver", "Approver"), ("operator", "Operator") })
            Store.SetMembership(Cell.Scope.CustomerId, "admin", actor, "default", "dev", [role]);
        Store.PutConnection(Cell.Scope.CustomerId, "admin", Cell.Connection.Id, provider, Cell.Dispatch.Connection.ConfigurationJson);
        Options = OptionsFor(Cell); Vault = CredentialVault.ProvisionStrongBoundDispatch(Options, Cell.Keys, Cell.Clock); Namespace = Vault.DescribeNamespace();
        Instance = Store.GetCellInstanceIdentity().DatabaseInstanceId;
        Authority = FoundationCellActivationAuthority.Provision(Path.Combine(Cell.Root, "restricted-activation"), Cell.Scope.CustomerId, Cell.Scope.CellId, Cell.Clock);
        _ = Authority.Activate(database, Instance, 0, null); AuthorityIdentity = Authority.DescribeIdentity();
        Binding = FoundationCredentialBoundTransport.BindCurrent(Store, Vault, Authority, Namespace, AuthorityIdentity, Instance);
        Verifier = new([], Cell.Clock);
    }

    internal static async Task<BoundNamespaceCoreFixture> Create(string provider = PipelineRequestFactory.GitHub)
    {
        var f = new BoundNamespaceCoreFixture(provider);
        try
        {
            using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token);
            await f.Vault.PutAsync(f.Cell.Scope, secret, f.Cell.Clock.Now.AddHours(1));
            var plan = f.Store.CreatePlan(f.Cell.Scope.CustomerId, "requester", new("default", "dev", f.Cell.Connection.Id,
                "pipeline.dispatch", JsonSerializer.Serialize(f.Cell.Request, new JsonSerializerOptions(JsonSerializerDefaults.Web)), true,
                f.Cell.Clock.Now.AddMinutes(20), f.Cell.Request.SourceRevision));
            f.Store.ApprovePlan(f.Cell.Scope.CustomerId, "approver", plan.Id, plan.Digest);
            using var admission = new BoundFixtureAdmission(f.Store, f.Vault, f.Authority, f.Cell.Scope, f.Cell.Clock);
            var job = admission.Run.EnqueuePlan(f.Cell.Scope.CustomerId, "operator", plan.Id, plan.Digest, "restricted-request");
            var claim = admission.Run.ClaimNext(f.Cell.Scope.CustomerId, "restricted-worker", TimeSpan.FromMinutes(2))!;
            f.Dispatch = admission.Run.StartMockDispatch(f.Cell.Scope.CustomerId, job.Id, "restricted-worker", claim.Fence, f.Verifier, BoundTransportFixture.Runtime);
            f.Wire = PipelineRequestFactory.Create(f.Cell.Connection, f.Cell.Request, new(f.Cell.Scope.CustomerId, f.Cell.Scope.CellId,
                f.Cell.Connection.Id, 1, PipelineRequestFactory.ConnectionDigest(f.Cell.Connection), PipelineRequestFactory.RequestDigest(f.Cell.Connection, f.Cell.Request),
                plan.Digest, plan.ExpiresAt), f.Cell.Clock.Now);
            return f;
        }
        catch { f.Dispose(); throw; }
    }

    internal FoundationCredentialBoundTransport Transport(HttpMessageHandler handler) => FoundationCredentialBoundTransport.ForMockHandler(Store,
        Vault, Authority, Namespace, AuthorityIdentity, Instance, Binding.Sha256, DispatchPins, Verifier, BoundTransportFixture.Runtime, handler, Cell.Clock);
    public void Dispose() => Cell.Dispose();
}
