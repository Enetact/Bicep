using System.Diagnostics;
using System.IO.Compression;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class AvmModuleTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web) { WriteIndented = true };
    static string Root { get { var d = new DirectoryInfo(AppContext.BaseDirectory); while (d is not null && !File.Exists(Path.Combine(d.FullName, "config/workloads.json"))) d = d.Parent; return d!.FullName; } }
    static JsonElement J(object value) => JsonSerializer.SerializeToElement(value, Json);
    static JsonElement Parse(string text) => JsonDocument.Parse(text).RootElement.Clone();
    static BicepModuleContract[] Modules() => BicepDrafts.Catalog(Root).Modules.Where(m => m.Id.StartsWith("avm-", StringComparison.Ordinal)).ToArray();
    static BicepDraftContext Context()
    {
        var modules = BicepDrafts.Catalog(Root).Modules.Where(m => m.DraftSelectable).ToArray();
        return new("platform.bicep-context/v1", "avm-evidence-fixture", BicepDrafts.Hash(JsonSerializer.Serialize(modules, Json)),
            "11111111-1111-1111-1111-111111111111", "storage", "dev", "eastus2", "Reviewed AVM source", "Partial", new(), [], modules);
    }
    static string Proposal(BicepModuleContract module, Dictionary<string, BicepBinding>? bindings = null) => JsonSerializer.Serialize(
        new BicepProposal("platform.bicep-proposal/v1", "avm-evidence-fixture", "Reviewed AVM source fixture",
            [new("component", module.Id, "Synthetic source fixture", bindings ?? new())], ["Ownership and live qualification required"]), Json);
    static string Fixture()
    {
        var path = Path.Combine(Root, "artifacts/avm-adapter-tests", Guid.NewGuid().ToString("N"));
        path = Path.GetFullPath(path); Directory.CreateDirectory(path); return path;
    }
    static string Copy(BicepModuleContract module)
    {
        var root = Fixture();
        foreach (var path in module.Sources!.Keys)
        {
            var file = Path.Combine(root, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!);
            File.Copy(Path.Combine(Root, path), file);
        }
        return root;
    }

    [Fact] public void ExactlySixReviewedAvmModulesExtendTheLegacyCatalog()
    {
        var all = BicepDrafts.Catalog(Root); var avm = Modules();
        Assert.Equal(23, all.Modules.Length); Assert.Equal(18, all.Modules.Count(m => m.DraftSelectable));
        Assert.Equal(BicepAvmSources.Paths.Order(), avm.Select(m => m.Path).Order());
        Assert.All(avm, module => { Assert.Equal(BicepAvmSources.Id(module.Path), module.Id); Assert.Equal(5, module.Sources!.Count); Assert.NotNull(module.Origin); });
    }

    [Fact] public void ArchiveIncludesTheExactPinnedClosureLicenseAndProvenance()
    {
        var context = Context();
        foreach (var module in Modules())
        {
            var result = BicepDrafts.Generate(Root, context, Proposal(module));
            using var stream = new MemoryStream(Convert.FromBase64String(result.ArchiveBase64));
            using var archive = new ZipArchive(stream, ZipArchiveMode.Read);
            foreach (var (path, hash) in module.Sources!)
            {
                var entry = archive.GetEntry(path); Assert.NotNull(entry);
                using var entryStream = entry.Open();
                Assert.Equal(hash, Convert.ToHexString(SHA256.HashData(entryStream)).ToLowerInvariant());
                Assert.Contains(path, result.Files["draft-receipt.json"]);
                Assert.Contains(hash, result.Files["draft-receipt.json"]);
            }
            Assert.DoesNotContain("br/public:", result.Files["main.bicep"]);
            Assert.Contains("deploymentAuthorized\": false", result.Files["draft-receipt.json"]);
            Assert.Contains("origin", result.Files["draft-receipt.json"]);
        }
    }

    [Fact] public void TamperedAndMissingDependencyFilesAreRejected()
    {
        var module = Modules()[0]; var root = Copy(module);
        var dependency = module.Sources!.Keys.Single(p => p.EndsWith("/main.json", StringComparison.Ordinal));
        File.AppendAllText(Path.Combine(root, dependency), " ");
        Assert.Throws<PortalException>(() => BicepAvmSources.Read(root, module));
        root = Copy(module);
        var missing = module.Sources.Keys.Single(p => p.EndsWith("/manifest.json", StringComparison.Ordinal));
        File.Delete(Path.Combine(root, missing));
        Assert.Throws<PortalException>(() => BicepAvmSources.Read(root, module));
    }

    [Theory]
    [InlineData("../outside.json")]
    [InlineData("vendor/azure-verified-modules/network-route-table/main.json")]
    [InlineData("vendor\\azure-verified-modules\\identity-user-assigned-identity\\main.json")]
    public void SourceMetadataCannotChooseAnUnreviewedPath(string path)
    {
        var module = Modules().Single(m => m.Id == "avm-identity-user-assigned-identity");
        var sources = new Dictionary<string, string>(module.Sources!);
        var own = sources.Keys.Single(p => p.EndsWith("/main.json", StringComparison.Ordinal));
        var hash = sources[own]; sources.Remove(own); sources[path] = hash;
        Assert.Throws<PortalException>(() => BicepAvmSources.Read(Root, module with { Sources = sources }));
    }

    [Fact] public void SourceClosureRejectsDirectoryLinks()
    {
        var module = Modules()[0]; var root = Fixture();
        var own = "vendor/azure-verified-modules/" + module.Id[4..];
        var target = Path.Combine(root, "link-payload"); Directory.CreateDirectory(target);
        foreach (var path in module.Sources!.Keys)
        {
            var destination = path.StartsWith(own + "/", StringComparison.Ordinal) ? Path.Combine(target, Path.GetFileName(path)) : Path.Combine(root, path);
            Directory.CreateDirectory(Path.GetDirectoryName(destination)!); File.Copy(Path.Combine(Root, path), destination);
        }
        var link = Path.GetFullPath(Path.Combine(root, own)); Directory.CreateDirectory(Path.GetDirectoryName(link)!);
        if (OperatingSystem.IsWindows())
        {
            var start = new ProcessStartInfo("cmd.exe") { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true };
            foreach (var argument in new[] { "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
            using var process = Process.Start(start)!;
            var output = process.StandardOutput.ReadToEnd(); var error = process.StandardError.ReadToEnd(); process.WaitForExit();
            Assert.True(process.ExitCode == 0, "Local junction fixture creation failed: " + output + error);
        }
        else Directory.CreateSymbolicLink(link, target);
        Assert.Throws<PortalException>(() => BicepAvmSources.Read(root, module));
    }

    [Fact] public void EveryChildCollectionEmitsExplicitTypesAndConstraints()
    {
        var context = Context(); var count = 0;
        foreach (var module in Modules())
        {
            var children = module.Parameters.Where(p => BicepParameterSchema.Expand(p.Value, module.Definitions).TryGetProperty("items", out _))
                .ToDictionary(p => p.Key, _ => new BicepBinding("Input"));
            if (children.Count == 0) continue;
            var result = BicepDrafts.Generate(Root, context, Proposal(module, children));
            Assert.Contains("type t_p_component_", result.Files["main.bicep"]);
            Assert.Contains("@sealed()", result.Files["main.bicep"]);
            Assert.Contains("type t_p_component_", result.Files["stack.bicep"]);
            foreach (var child in children.Keys) Assert.Contains("p_component_" + child, result.RequiredInputs);
            var directory = Path.Combine(Root, "artifacts/bicep-draft-tests", module.Id, "typed-children");
            foreach (var (path, content) in result.Files)
            {
                var file = Path.Combine(directory, path); Directory.CreateDirectory(Path.GetDirectoryName(file)!); File.WriteAllText(file, content, new UTF8Encoding(false));
            }
            count++;
        }
        Assert.True(count >= 3, "Federation, NSG rules and route children must retain typed interfaces.");
    }

    static JsonElement ChildSchema => BicepParameterSchema.Expand(Parse("""
        {"type":"array","maxLength":2,"items":{"type":"object","additionalProperties":false,"properties":{
          "name":{"type":"string","minLength":1},
          "properties":{"type":"object","additionalProperties":false,"properties":{
            "priority":{"type":"int","minValue":100,"maxValue":4096},
            "access":{"type":"string","allowedValues":["Allow","Deny"]},
            "description":{"type":"string","nullable":true,"maxLength":140}
          }}
        }}}
        """));
    [Fact] public void RecursiveChildValidationAcceptsReviewedDataWithoutInterpretingIt()
    {
        BicepParameterSchema.Validate(Parse("""[{"name":"O'Brien ${fake.expression}","properties":{"priority":100,"access":"Deny"}}]"""), ChildSchema);
        BicepParameterSchema.Validate(Parse("""[{"name":"rule","properties":{"priority":100,"access":"Allow","description":null}}]"""), ChildSchema);
    }
    [Theory]
    [InlineData("[{\"name\":\"rule\",\"properties\":{\"priority\":\"100\",\"access\":\"Allow\"}}]")]
    [InlineData("[{\"name\":\"rule\",\"properties\":{\"priority\":99,\"access\":\"Allow\"}}]")]
    [InlineData("[{\"name\":\"rule\",\"properties\":{\"priority\":100,\"access\":\"Bypass\"}}]")]
    [InlineData("[{\"name\":\"rule\",\"properties\":{\"priority\":100,\"access\":\"Allow\",\"invented\":true}}]")]
    [InlineData("[{\"name\":\"rule\",\"properties\":{\"priority\":100}}]")]
    [InlineData("[{\"name\":\"rule\",\"name\":\"duplicate\",\"properties\":{\"priority\":100,\"access\":\"Allow\"}}]")]
    [InlineData("[{}, {}, {}]")]
    public void InvalidChildValuesAreRejected(string value) => Assert.Throws<PortalException>(() => BicepParameterSchema.Validate(Parse(value), ChildSchema));

    [Theory]
    [InlineData("{\"type\":\"secureString\"}")]
    [InlineData("{\"type\":\"object\",\"properties\":{\"secret\":{\"type\":\"secureObject\"}}}")]
    [InlineData("{\"type\":\"object\",\"additionalProperties\":{\"type\":\"string\"}}")]
    [InlineData("{\"type\":\"string\",\"anyOf\":[]}")]
    public void SecureAndUnsupportedSchemasStayOutsideTheAdapter(string schema) => Assert.Throws<PortalException>(() => BicepParameterSchema.Expand(Parse(schema)));

    [Fact] public void UnknownAndCyclicReferencesAreRejected()
    {
        var reference = Parse("""{"$ref":"#/definitions/recursive"}""");
        Assert.Throws<PortalException>(() => BicepParameterSchema.Expand(reference));
        Assert.Throws<PortalException>(() => BicepParameterSchema.Expand(reference, new() { ["recursive"] = reference }));
        var valid = BicepParameterSchema.Expand(Parse("""{"$ref":"#/definitions/child","maxLength":10}"""), new() { ["child"] = Parse("""{"type":"string","minLength":1}""") });
        Assert.Equal(10, valid.GetProperty("maxLength").GetInt32());
        Assert.Equal(1, valid.GetProperty("minLength").GetInt32());
    }

    [Fact] public void ArrayAllowedValuesConstrainEachFederationAudience()
    {
        var module = Modules().Single(m => m.Id == "avm-identity-user-assigned-identity");
        var schema = BicepParameterSchema.Expand(module.Parameters["federatedIdentityCredentials"], module.Definitions);
        BicepParameterSchema.Validate(Parse("""[{"name":"github-federation","issuer":"https://token.actions.githubusercontent.com","subject":"repo:example/repo:environment:reviewed","audiences":["api://AzureADTokenExchange"]}]"""), schema);
        Assert.Throws<PortalException>(() => BicepParameterSchema.Validate(Parse("""[{"name":"github-federation","issuer":"https://token.actions.githubusercontent.com","subject":"reviewed","audiences":["invented-audience"]}]"""), schema));
        Assert.Throws<PortalException>(() => BicepParameterSchema.Validate(Parse("""[{"name":"github-federation","issuer":"https://token.actions.githubusercontent.com","subject":"reviewed","audiences":["api://AzureADTokenExchange","api://AzureADTokenExchange"]}]"""), schema));
        var types = new List<string>(); _ = BicepParameterSchema.Type(schema, "federationFixture", types);
        Assert.Contains("('api://AzureADTokenExchange')[]", string.Join('\n', types));
    }

    [Fact] public void ActualNsgChildSettingsAreValidatedBeforeSourceEmission()
    {
        var module = Modules().Single(m => m.Id == "avm-network-network-security-group"); var context = Context();
        var binding = new Dictionary<string, BicepBinding> { ["securityRules"] = new("Setting", "reviewedChildren") };
        context.Settings["reviewedChildren"] = Parse("""[{"name":"deny","properties":{"priority":100,"access":"Deny","direction":"Inbound","protocol":"*","sourceAddressPrefix":"*","sourcePortRange":"*","destinationAddressPrefix":"*","destinationPortRange":"*"}}]""");
        var valid = BicepDrafts.Generate(Root, context, Proposal(module, binding));
        Assert.Contains("deny", valid.Files["resolved-values.json"]);
        context.Settings["reviewedChildren"] = Parse("""[{"name":"deny","properties":{"priority":100,"access":"Deny","invented":true}}]""");
        Assert.Throws<PortalException>(() => BicepDrafts.Generate(Root, context, Proposal(module, binding)));
    }
}
