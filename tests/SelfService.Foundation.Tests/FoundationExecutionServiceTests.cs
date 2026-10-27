using System.Reflection.Metadata;
using System.Reflection.PortableExecutable;
using System.Text.Json;
using Microsoft.Extensions.DependencyInjection;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationExecutionServiceTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub)]
    [InlineData(PipelineRequestFactory.AzureDevOps)]
    public async Task Shared_executor_completes_the_signed_approved_job_with_the_existing_mock_receipt(string provider)
    {
        await using var f = await FoundationApiTests.ApiCell.Start(provider);
        var plan = await f.Plan(); await f.Approve(plan); var job = await f.Enqueue(plan);
        var store = f.App.Services.GetRequiredService<FoundationStore>();
        var execution = new FoundationPipelineExecutionService(store, new PipelineDispatcher(f.Transport));

        Assert.True(await execution.RunOnceAsync("shared-executor"));
        var result = store.GetJob(f.CustomerId, FoundationApiTests.ApiCell.Actor("viewer"), job.Id, "default", "dev");
        Assert.Equal("Succeeded", result.State); Assert.Single(f.Transport.Requests);
        using var receipt = JsonDocument.Parse(result.ReceiptJson!);
        Assert.True(receipt.RootElement.GetProperty("mock").GetBoolean());
        Assert.True(receipt.RootElement.GetProperty("dispatchAccepted").GetBoolean());
        Assert.False(receipt.RootElement.GetProperty("deploymentVerified").GetBoolean());
        Assert.False(await f.App.Services.GetRequiredService<FoundationJobRunner>().RunOnceAsync("compatibility-adapter"));
        Assert.Single(f.Transport.Requests);
    }

    [Fact]
    public async Task Lost_correlation_remains_unknown_and_is_not_replayed_by_a_different_host_adapter()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        var plan = await f.Plan(); await f.Approve(plan); var job = await f.Enqueue(plan);
        f.Transport.StatusCode = 202;
        var store = f.App.Services.GetRequiredService<FoundationStore>();
        var execution = new FoundationPipelineExecutionService(store, new PipelineDispatcher(f.Transport));

        Assert.True(await execution.RunOnceAsync("shared-executor"));
        Assert.Equal("OutcomeUnknown", store.GetJob(f.CustomerId, FoundationApiTests.ApiCell.Actor("viewer"), job.Id, "default", "dev").State);
        Assert.False(await f.App.Services.GetRequiredService<FoundationJobRunner>().RunOnceAsync("compatibility-adapter"));
        Assert.Single(f.Transport.Requests);
    }

    [Fact]
    public async Task Shared_executor_rechecks_revoked_approval_before_provider_access()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        var plan = await f.Plan(); await f.Approve(plan); var job = await f.Enqueue(plan);
        await f.Member("approver", [], enabled: false);
        var store = f.App.Services.GetRequiredService<FoundationStore>();
        var execution = new FoundationPipelineExecutionService(store, new PipelineDispatcher(f.Transport));

        Assert.True(await execution.RunOnceAsync("shared-executor"));
        Assert.Equal("Blocked", store.GetJob(f.CustomerId, FoundationApiTests.ApiCell.Actor("viewer"), job.Id, "default", "dev").State);
        Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public async Task Canceled_shared_execution_does_not_claim_or_change_the_queued_job()
    {
        await using var f = await FoundationApiTests.ApiCell.Start();
        var plan = await f.Plan(); await f.Approve(plan); var job = await f.Enqueue(plan);
        var store = f.App.Services.GetRequiredService<FoundationStore>();
        var execution = new FoundationPipelineExecutionService(store, new PipelineDispatcher(f.Transport));
        using var canceled = new CancellationTokenSource(); canceled.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => execution.RunOnceAsync("shared-executor", canceled.Token));
        var result = store.GetJob(f.CustomerId, FoundationApiTests.ApiCell.Actor("viewer"), job.Id, "default", "dev");
        Assert.Equal("Queued", result.State); Assert.Equal(0, result.Attempt); Assert.Empty(f.Transport.Requests);
    }

    [Fact]
    public void Shared_activation_shutdown_retains_actual_custody_until_the_last_owned_use_finishes()
    {
        var root = Path.Combine(Path.GetTempPath(), "foundation-shared-activation-" + Guid.NewGuid().ToString("N"));
        if (OperatingSystem.IsWindows()) ActivationProvisionFixtureRoots.Create(root); else Directory.CreateDirectory(root);
        const string customer = FoundationApiTests.ApiCell.Customer, cell = "00000000-0000-0000-0000-000000000003";
        try
        {
            var sourcePath = Path.Combine(root, "source", "cell.db"); var targetPath = Path.Combine(root, "target", "cell.db");
            var source = new FoundationStore(sourcePath, cell, customer); var target = new FoundationStore(targetPath, cell, customer);
            var authorityPath = Path.Combine(root, "authority");
            var authority = FoundationCellActivationAuthority.Provision(authorityPath, customer, cell);
            var selected = authority.Activate(sourcePath, source.GetCellInstanceIdentity().DatabaseInstanceId, 0, null);
            using var activation = new FoundationExecutionActivation(true, authorityPath, customer, cell, sourcePath);
            using var first = activation.RetainUse(); using var last = activation.RetainUse();

            activation.Dispose(); activation.Dispose();
            Assert.Equal("ActivationRuntimeStopping", Assert.Throws<FoundationStoreException>(() => activation.RetainUse()).Code);
            Assert.Equal("ActivationAuthorityBusy", Assert.Throws<FoundationStoreException>(() => authority.Activate(targetPath,
                target.GetCellInstanceIdentity().DatabaseInstanceId, selected.Generation, selected.Sha256)).Code);
            first.Dispose(); first.Dispose();
            Assert.Equal("ActivationAuthorityBusy", Assert.Throws<FoundationStoreException>(() => authority.Activate(targetPath,
                target.GetCellInstanceIdentity().DatabaseInstanceId, selected.Generation, selected.Sha256)).Code);
            last.Dispose(); last.Dispose();
            var switched = authority.Activate(targetPath, target.GetCellInstanceIdentity().DatabaseInstanceId, selected.Generation, selected.Sha256);
            Assert.Equal(selected.Generation + 1, switched.Generation);
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, recursive: true); }
    }

    [Theory]
    [InlineData(false, "unused-authority")]
    [InlineData(true, null)]
    public void Shared_activation_rejects_incoherent_configuration_without_provisioning(bool required, string? authority)
    {
        using var f = new StoreFixture();
        Assert.Throws<ArgumentException>(() => new FoundationExecutionActivation(required, authority, f.Customer, f.Cell, f.Path));
    }

    [Fact]
    public void Built_independent_worker_has_no_web_host_portal_or_aspnet_runtime_dependency()
    {
        var artifactBin = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!;
        var candidates = new[]
        {
            Path.Combine(artifactBin.FullName, "SelfService.Foundation.Worker", "release", "SelfService.Foundation.Worker.dll"),
            Path.Combine(artifactBin.FullName, "SelfService.Foundation.Worker", "debug", "SelfService.Foundation.Worker.dll")
        };
        var worker = candidates.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("The worker must be built with this isolated test execution.");
        using var input = File.OpenRead(worker); using var assembly = new PEReader(input);
        var metadata = assembly.GetMetadataReader();
        var references = metadata.AssemblyReferences.Select(handle => metadata.GetString(metadata.GetAssemblyReference(handle).Name)).ToArray();
        Assert.Contains("SelfService.Foundation", references);
        Assert.DoesNotContain("SelfService.Foundation.Host", references); Assert.DoesNotContain("SelfService.Portal", references);
        var runtime = File.ReadAllText(Path.ChangeExtension(worker, ".runtimeconfig.json"));
        Assert.DoesNotContain("Microsoft.AspNetCore.App", runtime);
        using var dependencies = JsonDocument.Parse(File.ReadAllText(Path.ChangeExtension(worker, ".deps.json")));
        var closure = dependencies.RootElement.GetProperty("libraries").EnumerateObject().Select(entry => entry.Name.Split('/')[0]).ToArray();
        Assert.DoesNotContain("SelfService.Foundation.Host", closure); Assert.DoesNotContain("SelfService.Portal", closure);
        Assert.DoesNotContain("ModelContextProtocol", closure); Assert.DoesNotContain("Microsoft.Identity.Client", closure);
    }
}
