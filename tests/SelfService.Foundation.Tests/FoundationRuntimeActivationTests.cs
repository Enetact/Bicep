using Microsoft.Extensions.DependencyInjection;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationRuntimeActivationTests
{
    const string Customer = FoundationApiTests.ApiCell.Customer;
    const string Cell = "00000000-0000-0000-0000-000000000003";

    [Fact]
    public async Task Api_holds_activation_until_disposed_and_restored_copy_needs_an_explicit_switch()
    {
        using var fixture = new ActivationFixture(); using var issuer = new IdentityTestIssuer();
        var source = fixture.Prepare("source"); var binding = fixture.Authority.Activate(source.Path, source.Store.GetCellInstanceIdentity().DatabaseInstanceId, 0, null);
        var api = await FoundationApiTests.ApiCell.Start(directory: source.Directory, issuer: issuer,
            requireActivationAuthority: true, activationAuthorityDirectory: fixture.AuthorityPath);
        try
        {
            var store = api.App.Services.GetRequiredService<FoundationStore>(); var plan = await api.Plan(); await api.Approve(plan); var job = await api.Enqueue(plan);
            var snapshot = store.CreateBackup(System.IO.Path.Combine(fixture.Root, "snapshot.db"));
            var targetDirectory = System.IO.Path.Combine(fixture.Root, "restored"); Directory.CreateDirectory(targetDirectory);
            var targetPath = System.IO.Path.Combine(targetDirectory, "cell.db"); var restored = FoundationStore.RestoreBackup(snapshot, targetPath);
            var targetId = restored.GetCellInstanceIdentity().DatabaseInstanceId;
            Assert.NotEqual(source.Store.GetCellInstanceIdentity().DatabaseInstanceId, targetId);
            Assert.Throws<FoundationStoreException>(() => fixture.Authority.Activate(targetPath, targetId, binding.Generation, binding.Sha256));
            await Assert.ThrowsAsync<FoundationStoreException>(() => FoundationApiTests.ApiCell.Start(directory: targetDirectory, issuer: issuer,
                requireActivationAuthority: true, activationAuthorityDirectory: fixture.AuthorityPath));
            Assert.Equal("OutcomeUnknown", restored.GetJob(Customer, FoundationApiTests.ApiCell.Actor("viewer"), job.Id).State);
            await api.DisposeAsync();
            var switched = fixture.Authority.Activate(targetPath, targetId, binding.Generation, binding.Sha256);
            Assert.Equal(2, switched.Generation);
            await Assert.ThrowsAsync<FoundationStoreException>(() => FoundationApiTests.ApiCell.Start(directory: source.Directory, issuer: issuer,
                requireActivationAuthority: true, activationAuthorityDirectory: fixture.AuthorityPath));
            await using var replacement = await FoundationApiTests.ApiCell.Start(directory: targetDirectory, issuer: issuer,
                requireActivationAuthority: true, activationAuthorityDirectory: fixture.AuthorityPath);
            Assert.Equal("RecoveryExecutionHeld", Assert.Throws<FoundationStoreException>(() => restored.ClaimNext(Customer, "worker", TimeSpan.FromSeconds(5))).Code);
            using var response = await replacement.Send("viewer", HttpMethod.Get, "/jobs"); Assert.True(response.IsSuccessStatusCode);
        }
        finally { await api.DisposeAsync(); }
    }

    [Fact]
    public async Task Failed_build_releases_lease_and_inactive_startup_cannot_bootstrap_or_mutate_the_cell()
    {
        using var fixture = new ActivationFixture(); using var issuer = new IdentityTestIssuer();
        var source = fixture.Prepare("source"); var inactive = fixture.Prepare("inactive");
        var initial = fixture.Authority.Activate(source.Path, source.Store.GetCellInstanceIdentity().DatabaseInstanceId, 0, null);
        await Assert.ThrowsAsync<FoundationStoreException>(() => FoundationApiTests.ApiCell.Start(directory: inactive.Directory, issuer: issuer,
            requireActivationAuthority: true, activationAuthorityDirectory: fixture.AuthorityPath));
        Assert.Empty(inactive.Store.GetMemberships(Customer, FoundationApiTests.ApiCell.Actor("admin")));
        var options = new FoundationHostOptions(issuer.Options(Customer, Cell), source.Path, FoundationApiTests.ApiCell.Actor("admin"))
            { RequireActivationAuthority = true, ActivationAuthorityDirectory = fixture.AuthorityPath };
        Assert.Throws<InvalidOperationException>(() => FoundationApplication.Build(options, configure: _ => throw new InvalidOperationException("synthetic build failure")));
        var next = fixture.Authority.Activate(inactive.Path, inactive.Store.GetCellInstanceIdentity().DatabaseInstanceId, initial.Generation, initial.Sha256);
        Assert.Equal(2, next.Generation);
    }

    [Fact]
    public async Task Runtime_does_not_provision_authority_or_create_a_database_when_activation_is_required()
    {
        using var fixture = new ActivationFixture(); using var issuer = new IdentityTestIssuer();
        var source = fixture.Prepare("source"); var missingAuthority = System.IO.Path.Combine(fixture.Root, "never-created-authority");
        await Assert.ThrowsAnyAsync<Exception>(() => FoundationApiTests.ApiCell.Start(directory: source.Directory, issuer: issuer,
            requireActivationAuthority: true, activationAuthorityDirectory: missingAuthority));
        Assert.False(Directory.Exists(missingAuthority));
        var missingDirectory = System.IO.Path.Combine(fixture.Root, "never-created-cell");
        await Assert.ThrowsAsync<FoundationStoreException>(() => FoundationApiTests.ApiCell.Start(directory: missingDirectory, issuer: issuer,
            requireActivationAuthority: true, activationAuthorityDirectory: fixture.AuthorityPath));
        Assert.False(Directory.Exists(missingDirectory));
    }

    sealed class ActivationFixture : IDisposable
    {
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "foundation-runtime-activation-" + Guid.NewGuid().ToString("N"));
        public string AuthorityPath => System.IO.Path.Combine(Root, "authority");
        public FoundationCellActivationAuthority Authority { get; }
        public ActivationFixture()
        { if (OperatingSystem.IsWindows()) ActivationProvisionFixtureRoots.Create(Root); else Directory.CreateDirectory(Root); Authority = FoundationCellActivationAuthority.Provision(AuthorityPath, Customer, Cell); }
        public (string Directory, string Path, FoundationStore Store) Prepare(string name)
        {
            var directory = System.IO.Path.Combine(Root, name); var path = System.IO.Path.Combine(directory, "cell.db");
            return (directory, path, new FoundationStore(path, Cell, Customer));
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
