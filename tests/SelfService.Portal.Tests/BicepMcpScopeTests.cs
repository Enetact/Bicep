using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class BicepMcpScopeTests : IDisposable
{
    readonly string Root = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "synthetic-bicep-source-" + Guid.NewGuid().ToString("N")));
    string Source(string name) => Path.GetFullPath(Path.Combine(Root, name));
    string FileUri(string name) => new Uri(Source(name)).AbsoluteUri;
    JsonElement Args(string name = "main.bicep") => JsonSerializer.SerializeToElement(new { filePath = Source(name) });
    static JsonElement Result(params string[] uris) => JsonSerializer.SerializeToElement(new { isError = false, structuredContent = new { fileUris = uris } });
    void Validate(string path)
    {
        if (!path.StartsWith(Root + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) || path.Contains(".local", StringComparison.OrdinalIgnoreCase))
            throw new InvalidOperationException("synthetic-sensitive-path-and-error");
    }
    public BicepMcpScopeTests()
    {
        Directory.CreateDirectory(Root);
        foreach (var name in new[] { "main.bicep", "module.bicep" }) Write(name, "// Synthetic local source only\n");
        Write("main.bicepparam", "using './module.bicep'\n");
        foreach (var name in new[] { "data.json", "nested.json", "bicepconfig.json" }) Write(name, "{}");
    }
    void Write(string name, string text) { var file = Source(name); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, text); }
    public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, true); }
    const string Template = """{"$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#","contentVersion":"1.0.0.0","resources":[]}""";

    [Fact] public async Task ChecksArmJsonModuleEvenWhenNativeReferenceReplyOmitsIt()
    {
        Write("main.bicep", "module avm /* reviewed local artifact */ './arm.json' = { name: 'fixture' }"); Write("arm.json", Template);
        var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        await using var client = new Fake((_, _) => Task.FromResult(Result(FileUri("main.bicep"), FileUri("bicepconfig.json"))));
        await BicepMcpScope.ValidateDependencies(client, "build_bicep", Args(), path => { Validate(path); checkedPaths.Add(path); }, default);
        Assert.Contains(Source("arm.json"), checkedPaths); Assert.Single(client.Calls);
    }
    [Theory]
    [InlineData("module secret './.local/private.json' = { name: 'fixture' }")]
    [InlineData("module/* comment */secret/* comment */'./.local/private.json'={ name: 'fixture' }")]
    [InlineData("module secret '.\\u{6c}ocal/private.json' = { name: 'fixture' }")]
    [InlineData("output data object = loadJsonContent('./.local/private.json')")]
    [InlineData("output data object = sys.loadJsonContent(/* comment */'./.local/private.json')")]
    [InlineData("output data string = 'value ${loadTextContent('./.local/private.json')}'")]
    public async Task HiddenLiteralInputIsRejectedBeforeTheNativeCompilerCanReadIt(string source)
    {
        Write("main.bicep", source);
        await using var client = new Fake((_, _) => Task.FromResult(Result(FileUri("main.bicep"))));
        var error = await Assert.ThrowsAsync<PortalException>(() => BicepMcpScope.ValidateDependencies(client, "build_bicep", Args(), Validate, default));
        Assert.Empty(client.Calls); Assert.DoesNotContain("private", error.Message); Assert.DoesNotContain("synthetic-sensitive", error.Message);
    }
    [Fact] public async Task PreflightsAllLiteralBicepChildrenBeforeProbingTheEntrypoint()
    {
        Write("main.bicep", "module child './module.bicep' = { name: 'fixture' }");
        Write("module.bicep", "module hidden './.local/private.json' = { name: 'fixture' }");
        await using var client = new Fake((_, _) => Task.FromResult(Result(FileUri("main.bicep"))));
        await Assert.ThrowsAsync<PortalException>(() => BicepMcpScope.ValidateDependencies(client, "get_file_references", Args(), Validate, default)); Assert.Empty(client.Calls);
    }
    [Theory]
    [InlineData("{\"resources\":[{\"properties\":{\"templateLink\":{\"uri\":\"https://example.invalid/never-read\"}}}]}")]
    [InlineData("{\"resources\":[{\"properties\":{\"template\":{\"resources\":[{\"properties\":{\"templateLink\":{\"uri\":\"./.local/private.json\"}}}]}}}]}")]
    [InlineData("{\"definitions\":{\"unsafe\":{\"$ref\":\"./.local/private.json#/type\"}}}")]
    [InlineData("{\"linkedTemplates\":[{\"path\":\"./private.json\"}]}")]
    public async Task RejectsNestedArmExternalInputsBeforeNativeProbe(string body)
    {
        Write("main.bicep", "module arm './arm.json' = { name: 'fixture' }");
        Write("arm.json", "{\"$schema\":\"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#\",\"contentVersion\":\"1.0.0.0\"," + body[1..]);
        await using var client = new Fake((_, _) => Task.FromResult(Result(FileUri("main.bicep"))));
        await Assert.ThrowsAsync<PortalException>(() => BicepMcpScope.ValidateDependencies(client, "build_bicep", Args(), Validate, default)); Assert.Empty(client.Calls);
    }
    [Theory]
    [InlineData("module arm 'br:mcr.microsoft.com/bicep/avm/res/storage/storage-account:1.0.0' = {}")]
    [InlineData("output data object = loadJsonContent(path)")]
    [InlineData("output data object = loadJsonContent('data.json' + suffix)")]
    [InlineData("output data object = loadJsonContent('${name}.json')")]
    public async Task UnsupportedDependencyExpressionsFailClosed(string source)
    {
        Write("main.bicep", source);
        await using var client = new Fake((_, _) => Task.FromResult(Result(FileUri("main.bicep"))));
        await Assert.ThrowsAsync<PortalException>(() => BicepMcpScope.ValidateDependencies(client, "build_bicep", Args(), Validate, default)); Assert.Empty(client.Calls);
    }
    [Theory]
    [InlineData("main.bicep", "output files array = loadDirectoryFileInfo('./.local')")]
    [InlineData("main.bicep", "output files array = sys.loadDirectoryFileInfo('./public', '*.json')")]
    [InlineData("main.bicep", "output text string = '${length(sys.loadDirectoryFileInfo('./.local'))}'")]
    [InlineData("main.bicepparam", "using none\nparam value = readEnvironmentVariable('PATH')")]
    [InlineData("main.bicepparam", "using none\nparam value = sys.readEnvironmentVariable('SYNTHETIC_UNSET', 'fallback')")]
    [InlineData("main.bicep", "extension 'az@1.0.0' as az")]
    [InlineData("main.bicep", "provider 'az@1.0.0' as az")]
    [InlineData("main.bicep", "provider az with { config: 'fixture' } as configured")]
    [InlineData("main.bicep", "import 'az@1.0.0' as az")]
    [InlineData("main.bicep", "import az as configured")]
    [InlineData("main.bicep", "test fixture './.local/private.bicep' = { params: {} }")]
    [InlineData("main.bicepparam", "extends './.local/base.bicepparam'\nparam value = 'fixture'")]
    public async Task UnsupportedCompilerReadersAndDeclarationsAreBlockedBeforeNativeCalls(string file, string source)
    {
        Write(file, source);
        await using var client = new Fake((_, _) => Task.FromResult(Result(FileUri(file))));
        var error = await Assert.ThrowsAsync<PortalException>(() => BicepMcpScope.ValidateDependencies(client, "get_file_references", Args(file), Validate, default));
        Assert.Empty(client.Calls); Assert.DoesNotContain("private", error.Message); Assert.DoesNotContain("PATH", error.Message);
    }
    [Theory]
    [InlineData("{\"extensions\":{\"az\":\"./.local/private.tgz\"},\"implicitExtensions\":[\"az\"]}")]
    [InlineData("{\"extensions\":{\"az\":\"br:example.invalid/extension:1.0.0\"}}")]
    [InlineData("{\"implicitExtensions\":[\"unreviewed\"]}")]
    [InlineData("{\"providers\":{\"az\":\"./.local/private.tgz\"}}")]
    [InlineData("{\"cacheRootDirectory\":\"./.local/private\"}")]
    [InlineData("{\"moduleAliases\":{\"br\":{\"unsafe\":{\"registry\":\"example.invalid\"}}}}")]
    [InlineData("{\"experimentalFeaturesEnabled\":{\"extensibility\":true}}")]
    [InlineData("{\"analyzers\":{\"unreviewed\":{\"path\":\"./.local/private.dll\"}}}")]
    [InlineData("{\"analyzers\":{\"core\":{},\"CORE\":{}}}")]
    [InlineData("{\"analyzers\":{},\"analyzers\":{}}")]
    [InlineData("{\"formatting\":null}")]
    [InlineData("[]")]
    public async Task CompilerConfigurationCannotIntroduceUnreviewedDependenciesBeforeNativeCalls(string configuration)
    {
        Write("bicepconfig.json", configuration);
        await using var client = new Fake((_, _) => Task.FromResult(Result(FileUri("main.bicep"))));
        var error = await Assert.ThrowsAsync<PortalException>(() => BicepMcpScope.ValidateDependencies(client, "get_file_references", Args(), Validate, default));
        Assert.Empty(client.Calls); Assert.DoesNotContain("private", error.Message); Assert.DoesNotContain("example.invalid", error.Message);
    }
    [Fact] public async Task BuiltInLintAndFormattingConfigurationCanBeInspected()
    {
        Write("bicepconfig.json", """{"analyzers":{"core":{"enabled":true,"verbose":false,"rules":{"no-unused-params":{"level":"error"}}}},"formatting":{"indentKind":"Space","indentSize":2,"insertFinalNewline":true}}""");
        await using var client = new Fake((_, _) => Task.FromResult(Result(FileUri("main.bicep"), FileUri("bicepconfig.json"))));
        await BicepMcpScope.ValidateDependencies(client, "get_file_references", Args(), Validate, default); Assert.Single(client.Calls);
    }
    [Fact] public async Task ConfigurationChangedDuringNativeProbeInvalidatesAttestation()
    {
        await using var client = new Fake((_, _) => { Write("bicepconfig.json", "{\"implicitExtensions\":[\"unsafe\"]}"); return Task.FromResult(Result(FileUri("main.bicep"))); });
        await Assert.ThrowsAsync<PortalException>(() => BicepMcpScope.ValidateDependencies(client, "get_file_references", Args(), Validate, default)); Assert.Single(client.Calls);
    }
    [Fact] public async Task CommentsAndLiteralTextDoNotIntroduceDependenciesAndNestedInterpolationStillParses()
    {
        Write("main.bicep", """
            // module hidden './.local/private.json' = {}
            /* loadJsonContent('./.local/private.json') */
            var literal = '''module hidden './.local/private.json' = {}'''
            output text string = '${uniqueString('fixture')}-${literal}'
            """);
        await using var client = new Fake((_, _) => Task.FromResult(Result(FileUri("main.bicep"))));
        await BicepMcpScope.ValidateDependencies(client, "get_file_references", Args(), Validate, default); Assert.Single(client.Calls);
    }
    [Fact] public async Task RejectsJsonInputChangedDuringNativeReferenceProbe()
    {
        Write("main.bicep", "module arm './arm.json' = { name: 'fixture' }"); Write("arm.json", Template);
        await using var client = new Fake((_, _) => { Write("arm.json", Template + " "); return Task.FromResult(Result(FileUri("main.bicep"))); });
        await Assert.ThrowsAsync<PortalException>(() => BicepMcpScope.ValidateDependencies(client, "build_bicep", Args(), Validate, default)); Assert.Single(client.Calls);
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
    public async Task InstalledNativeJsonModulesAndAllSixAvmClosuresReceiveHostPreflight()
    {
        var repository = FindRepository();
        var executable = Path.Combine(repository, ".tools", "mcp", "bicep", OperatingSystem.IsWindows() ? "Azure.Bicep.McpServer.exe" : "Azure.Bicep.McpServer");
        var fixture = Path.Combine(repository, ".local", "mcp-arm-input-tests", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(Path.Combine(fixture, "private")); Directory.CreateDirectory(Path.Combine(fixture, "transport"));
        IProviderMcpClient? native = null;
        try
        {
            var source = Path.Combine(fixture, "main.bicep"); var arm = Path.Combine(fixture, "arm.json");
            await File.WriteAllTextAsync(source, "module arm './arm.json' = { name: 'fixture' }");
            await File.WriteAllTextAsync(arm, Template); await File.WriteAllTextAsync(Path.Combine(fixture, "bicepconfig.json"), "{}");
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(90));
            var runtimeRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? Path.GetFullPath(Path.Combine(System.Runtime.InteropServices.RuntimeEnvironment.GetRuntimeDirectory(), "..", "..", ".."));
            native = await new McpProviderFactory().Connect(new("bicep", executable, [], null,
                new Dictionary<string, string> { ["DOTNET_ROOT"] = runtimeRoot }, [], Path.Combine(fixture, "transport")), timeout.Token);
            var args = JsonSerializer.SerializeToElement(new { filePath = source });
            var raw = await native.Call("get_file_references", args, timeout.Token);
            Assert.Contains(new Uri(source).AbsoluteUri, raw.GetProperty("structuredContent").GetProperty("fileUris").EnumerateArray().Select(v => v.GetString()));
            // The pinned native server may omit arm.json. The host must check it either way.
            var checkedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase); var calls = 0;
            await using var counting = new Fake(async (path, ct) => { calls++; return await native.Call("get_file_references", JsonSerializer.SerializeToElement(new { filePath = path }), ct); });
            void Policy(string path)
            {
                if (!path.StartsWith(fixture + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    path.StartsWith(Path.Combine(fixture, "private") + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
                checkedPaths.Add(path);
            }
            await BicepMcpScope.ValidateDependencies(counting, "build_bicep", args, Policy, timeout.Token); Assert.Contains(arm, checkedPaths);
            var before = calls;
            await File.WriteAllTextAsync(source, "module arm './private/arm.json' = { name: 'fixture' }");
            await File.WriteAllTextAsync(Path.Combine(fixture, "private", "arm.json"), Template);
            await Assert.ThrowsAsync<PortalException>(() => BicepMcpScope.ValidateDependencies(counting, "build_bicep", args, Policy, timeout.Token)); Assert.Equal(before, calls);
            await File.WriteAllTextAsync(source, "module arm './arm.json' = { name: 'fixture' }");
            await File.WriteAllTextAsync(arm, """{"$schema":"https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#","resources":[{"type":"Microsoft.Resources/deployments","properties":{"templateLink":{"uri":"https://example.invalid/never-read"}}}]}""");
            await Assert.ThrowsAsync<PortalException>(() => BicepMcpScope.ValidateDependencies(counting, "get_file_references", args, Policy, timeout.Token)); Assert.Equal(before, calls);
            foreach (var wrapper in BicepAvmSources.Paths)
            {
                checkedPaths.Clear();
                void RepositoryPolicy(string path)
                {
                    var relative = Path.GetRelativePath(repository, path);
                    if (Path.IsPathRooted(relative) || relative.Split(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar).Any(p => p is ".." or ".local" or "artifacts" or ".git" or ".tools")) throw new InvalidOperationException();
                    checkedPaths.Add(path);
                }
                await BicepMcpScope.ValidateDependencies(counting, "get_file_references", JsonSerializer.SerializeToElement(new { filePath = Path.Combine(repository, wrapper) }), RepositoryPolicy, timeout.Token);
                var ownJson = Path.Combine(repository, "vendor", "azure-verified-modules", BicepAvmSources.Id(wrapper)[4..], "main.json");
                Assert.Contains(ownJson, checkedPaths);
            }
        }
        finally { if (native is not null) await native.DisposeAsync(); Directory.Delete(fixture, true); }
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
