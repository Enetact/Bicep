using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;
public sealed class NamingPlanTests
{
    static string Root()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "config/workloads.json"))) directory = directory.Parent;
        return directory?.FullName ?? throw new InvalidOperationException("Repository not found.");
    }
    static JsonElement Request(string workload) => JsonSerializer.SerializeToElement(new {
        workloadType = workload,
        intent = JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(Path.Combine(Root(), "tests/naming/request.example.json"))).GetProperty("intent")
    });
    [Theory][InlineData("private-storage")][InlineData("key-vault")][InlineData("observability")][InlineData("http-functions")][InlineData("service-bus-worker")][InlineData("private-aks")][InlineData("private-data-platform")][InlineData("logic-app-event-grid")][InlineData("blob-transfer")]
    public async Task RealNodeAdapterReturnsFrozenNonExecutableDraft(string workload)
    {
        var runner = new NamingPlan(new() { RepositoryRoot = Root() });
        var result = await runner.Generate(Request(workload), CancellationToken.None);
        Assert.False(result.GetProperty("deploymentAuthorized").GetBoolean());
        Assert.Equal("standard-v2",result.GetProperty("parameterPatch").GetProperty("parameters").GetProperty("resourceNames").GetProperty("value").GetProperty("profile").GetString());
        Assert.Equal(64,result.GetProperty("draftSha256").GetString()!.Length);
    }
    [Fact] public async Task UnsupportedProductFailsWithoutFallback() => await Assert.ThrowsAsync<PortalException>(() => new NamingPlan(new() { RepositoryRoot=Root() }).Generate(Request("unknown-product"),CancellationToken.None));
    [Fact] public async Task OversizedInputRejectedBeforeExecution() => await Assert.ThrowsAsync<PortalException>(() => new NamingPlan(new() { RepositoryRoot=Root(), NodePath="not-invoked" }).Generate(JsonSerializer.SerializeToElement(new { oversized=new string('x',65537) }),CancellationToken.None));
    [Fact] public async Task MissingNodeDoesNotReportSuccess() => await Assert.ThrowsAsync<PortalException>(() => new NamingPlan(new() { RepositoryRoot=Root(), NodePath="missing-naming-node" }).Generate(Request("key-vault"),CancellationToken.None));
}
