using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class BicepMcpScopeTests
{
    static readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "synthetic-bicep-source"));
    static string Source(string name) => Path.Combine(Root, name);
    static string FileUri(string name) => new Uri(Source(name)).AbsoluteUri;
    static JsonElement Args(string name = "main.bicep") => JsonSerializer.SerializeToElement(new { filePath = Source(name) });
    static JsonElement Result(params string[] uris) => JsonSerializer.SerializeToElement(new { isError = false, structuredContent = new { fileUris = uris } });
    static void Validate(string path)
    {
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || path.Contains(".local", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("synthetic-sensitive-path-and-error");
    }

    [Theory]
    [InlineData("build_bicep")][InlineData("build_bicepparam")][InlineData("format_bicep_file")]
    [InlineData("get_deployment_snapshot")][InlineData("get_file_references")][InlineData("get_bicep_file_diagnostics")]
    public async Task ChecksSourceAuxiliaryConfigAndTransitiveInputsWithoutExecutingRequestedTool(string tool)
    {
        var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var client = new Fake((path, _) => Task.FromResult(path == Source("module.bicep")
            ? Result(FileUri("module.bicep"), FileUri("main.bicep"), FileUri("nested.json"))
            : Result(FileUri("main.bicep"), FileUri("module.bicep"), FileUri("data.json"), FileUri("bicepconfig.json"))));
        await BicepMcpScope.ValidateDependencies(client, tool, Args(), path => { Validate(path); checkedPaths.Add(path); }, default);
        Assert.Equal(2, client.Calls.Count);
        Assert.All(client.Calls, call => Assert.Equal("get_file_references", call.Tool));
        Assert.Equal(5, checkedPaths.Count);
    }

    [Fact]
    public async Task AppliesSamePolicyToEveryReferenceBeforeFollowingModules()
    {
        await using var client = new Fake((_, _) => Task.FromResult(Result(FileUri("main.bicep"), FileUri("module.bicep"), FileUri(".local/private.json"))));
        var exception = await Assert.ThrowsAsync<PortalException>(() => BicepMcpScope.ValidateDependencies(client, "build_bicep", Args(), Validate, default));
        Assert.Single(client.Calls);
        Assert.DoesNotContain("private", exception.Message);
        Assert.DoesNotContain("synthetic-sensitive", exception.Message);
    }

    [Fact]
    public async Task ChecksTransitiveReferencesInParametersAndRejectsPrivateInput()
    {
        await using var client = new Fake((path, _) => Task.FromResult(path == Source("main.bicepparam")
            ? Result(FileUri("main.bicepparam"), FileUri("module.bicep"))
            : Result(FileUri("module.bicep"), FileUri(".local/credential"))));
        await Assert.ThrowsAsync<PortalException>(() => BicepMcpScope.ValidateDependencies(client, "get_deployment_snapshot", Args("main.bicepparam"), Validate, default));
        Assert.Equal(2, client.Calls.Count);
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("{\"structuredContent\":{\"fileUris\":[]}}")]
    [InlineData("{\"structuredContent\":{\"fileUris\":[null]}}")]
    [InlineData("{\"structuredContent\":{\"fileUris\":[\"relative.bicep\"]}}")]
    [InlineData("{\"structuredContent\":{\"fileUris\":[\"https://example.invalid/input.bicep\"]}}")]
    [InlineData("{\"structuredContent\":{\"fileUris\":[\"file://host/share/input.bicep\"]}}")]
    [InlineData("{\"structuredContent\":{\"fileUris\":\"malformed\"}}")]
    [InlineData("{\"isError\":true,\"content\":[{\"type\":\"text\",\"text\":\"synthetic-sensitive-provider-error\"}]}")]
    public async Task RejectsUnverifiableRepliesWithoutDisclosingRawData(string json)
    {
        await using var client = new Fake((_, _) => Task.FromResult(JsonDocument.Parse(json).RootElement.Clone()));
        var error = await Assert.ThrowsAsync<PortalException>(() => BicepMcpScope.ValidateDependencies(client, "build_bicep", Args(), Validate, default));
        Assert.DoesNotContain("synthetic-sensitive", error.ToString());
    }

    [Fact]
    public async Task RequiresRequestedSourceInAttestedResult()
    {
        await using var client = new Fake((_, _) => Task.FromResult(Result(FileUri("data.json"))));
        await Assert.ThrowsAsync<PortalException>(() => BicepMcpScope.ValidateDependencies(client, "build_bicep", Args(), Validate, default));
    }

    [Fact]
    public async Task SupportsNativeJsonTextFallback()
    {
        await using var client = new Fake((_, _) => Task.FromResult(JsonSerializer.SerializeToElement(new
        { content = new[] { new { type = "text", text = JsonSerializer.Serialize(new { fileUris = new[] { FileUri("main.bicep") } }) } } })));
        await BicepMcpScope.ValidateDependencies(client, "build_bicep", Args(), Validate, default);
        Assert.Single(client.Calls);
    }

    [Theory]
    [InlineData("get_bicep_best_practices")][InlineData("decompile_arm_template_file")][InlineData("decompile_arm_parameters_file")]
    public async Task DoesNotProbeToolsWithoutBicepCompilerInputs(string tool)
    {
        await using var client = new Fake((_, _) => throw new InvalidOperationException());
        await BicepMcpScope.ValidateDependencies(client, tool, Args(), _ => throw new InvalidOperationException(), default);
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task RejectsInvalidEntrypointsBeforeAnyProviderCall()
    {
        await using var client = new Fake((_, _) => throw new InvalidOperationException());
        foreach (var args in new[] { JsonSerializer.SerializeToElement(new { filePath = "relative.bicep" }), Args("data.json"), JsonSerializer.SerializeToElement(new { }) })
            await Assert.ThrowsAsync<PortalException>(() => BicepMcpScope.ValidateDependencies(client, "build_bicep", args, Validate, default));
        Assert.Empty(client.Calls);
    }

    [Fact]
    public async Task PreservesCallerCancellationAndSanitizesProviderFailure()
    {
        using var cancellation = new CancellationTokenSource(); cancellation.Cancel();
        await using var client = new Fake((_, _) => throw new InvalidOperationException("synthetic-sensitive-provider-error"));
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => BicepMcpScope.ValidateDependencies(client, "build_bicep", Args(), Validate, cancellation.Token));
        var error = await Assert.ThrowsAsync<PortalException>(() => BicepMcpScope.ValidateDependencies(client, "build_bicep", Args(), Validate, default));
        Assert.DoesNotContain("synthetic-sensitive", error.ToString());
    }

    [NativeBicepScopeFact]
    public async Task InstalledBicepReportsSyntheticAuxiliaryInputsAndBlocksPrivateDependency()
    {
        var repository = FindRepository();
        var executable = Path.Combine(repository, ".tools", "mcp", "bicep", OperatingSystem.IsWindows() ? "Azure.Bicep.McpServer.exe" : "Azure.Bicep.McpServer");
        var fixture = Path.Combine(repository, ".local", "mcp-scope-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fixture, "private"));
        Directory.CreateDirectory(Path.Combine(fixture, "transport"));
        IProviderMcpClient? client = null;
        try
        {
            await File.WriteAllTextAsync(Path.Combine(fixture, "bicepconfig.json"), "{}");
            await File.WriteAllTextAsync(Path.Combine(fixture, "data.txt"), "synthetic-public-fixture");
            await File.WriteAllTextAsync(Path.Combine(fixture, "private", "synthetic.txt"), "synthetic-private-fixture");
            await File.WriteAllTextAsync(Path.Combine(fixture, "allowed.bicep"), "output fixture string = loadTextContent('./data.txt')");
            await File.WriteAllTextAsync(Path.Combine(fixture, "blocked.bicep"), "output fixture string = loadTextContent('./private/synthetic.txt')");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var runtimeRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") ??
                Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
            client = await new McpProviderFactory().Connect(new("bicep", executable, [], null,
                new Dictionary<string, string> { ["DOTNET_ROOT"] = runtimeRoot }, [], Path.Combine(fixture, "transport")), timeout.Token);
            var privateObserved = false;
            void ValidateFixture(string path)
            {
                if (!path.StartsWith(fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
                if (path.StartsWith(Path.Combine(fixture, "private") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase))
                { privateObserved = true; throw new InvalidOperationException(); }
            }
            await BicepMcpScope.ValidateDependencies(client, "build_bicep", JsonSerializer.SerializeToElement(new { filePath = Path.Combine(fixture, "allowed.bicep") }), ValidateFixture, timeout.Token);
            await Assert.ThrowsAsync<PortalException>(() => BicepMcpScope.ValidateDependencies(client, "build_bicep",
                JsonSerializer.SerializeToElement(new { filePath = Path.Combine(fixture, "blocked.bicep") }), ValidateFixture, timeout.Token));
            Assert.True(privateObserved, "The native compiler must expose the synthetic private auxiliary input to the path policy.");
        }
        finally
        {
            if (client is not null) await client.DisposeAsync();
            Directory.Delete(fixture, true);
        }
    }

    static string FindRepository()
    {
        for (var folder = new DirectoryInfo(AppContext.BaseDirectory); folder is not null; folder = folder.Parent)
            if (File.Exists(Path.Combine(folder.FullName, "config", "workloads.json"))) return folder.FullName;
        throw new InvalidOperationException("Synthetic native probe could not locate the repository.");
    }

    sealed class Fake(Func<string, CancellationToken, Task<JsonElement>> call) : IProviderMcpClient
    {
        public List<(string Tool, JsonElement Arguments)> Calls { get; } = [];
        public Task<ProviderTool[]> ListTools(CancellationToken cancellationToken) => Task.FromResult(Array.Empty<ProviderTool>());
        public Task<JsonElement> Call(string tool, JsonElement arguments, CancellationToken cancellationToken)
        { Calls.Add((tool, arguments.Clone())); return call(arguments.GetProperty("filePath").GetString()!, cancellationToken); }
        public ValueTask DisposeAsync() => ValueTask.CompletedTask;
    }
}

public sealed class NativeBicepScopeFactAttribute : FactAttribute
{
    public NativeBicepScopeFactAttribute()
    {
        if (Environment.GetEnvironmentVariable("PLATFORM_TEST_BICEP") != "1")
            Skip = "Opt-in installed Bicep MCP dependency probe using only new synthetic local files. Set PLATFORM_TEST_BICEP=1.";
    }
}
