using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class McpGuidanceTests
{
    static readonly string[] Providers = ["ado", "azure", "bicep", "github", "learn"];
    static string Root()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config/workloads.json"))) dir = dir.Parent;
        return dir?.FullName ?? throw new InvalidOperationException("Run tests in the repository checkout.");
    }
    static string PackRoot => Path.Combine(Root(), ".agents", "skills", "platform-mcp-orchestration");
    static JsonElement ReadJson(string file) => JsonSerializer.Deserialize<JsonElement>(File.ReadAllText(file));
    static string Normalize(string text) => text.Replace("\r\n", "\n").TrimStart('\uFEFF');
    static string WithoutLinkDestinations(string text) => Regex.Replace(text, @"(\[[^\]]+\])\([^)]+\)", "$1");
    static string Sha256(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    static Skill SelectedSkill(Catalog catalog) => catalog.Skills.Single(s => s.Id == AgentWorkflows.Find("mcp-orchestration").SkillId);
    static async Task<string> ReadSkill(AgentMcpBridge bridge)
    {
        var response = JsonSerializer.SerializeToElement(await bridge.Call(JsonSerializer.SerializeToElement(new
        {
            tool = "platform_skill", arguments = new { }, @namespace = AgentPolicy.ToolNamespace
        }), CancellationToken.None));
        Assert.True(response.GetProperty("success").GetBoolean());
        return Assert.Single(response.GetProperty("contentItems").EnumerateArray()).GetProperty("text").GetString()!;
    }

    [Fact] public void SelectedCatalogSkillIncludesEveryNormativeProviderInstructionAndJsonExample()
    {
        var manifest = ReadJson(Path.Combine(PackRoot, "guidance.json"));
        Assert.Equal(1, manifest.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Providers, manifest.GetProperty("providers").EnumerateArray().Select(p => p.GetString()!).OrderBy(p => p, StringComparer.Ordinal));
        var sources = manifest.GetProperty("sources").EnumerateArray().ToArray();
        var paths = sources.Select(s => s.GetProperty("path").GetString()!).ToArray();
        Assert.Equal(paths.Length, paths.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.Contains("instruction.md", paths);
        foreach (var provider in Providers)
        {
            Assert.Contains($"providers/{provider}/SKILL.md", paths);
            Assert.Contains($"providers/{provider}/instruction.md", paths);
        }
        var skill = SelectedSkill(new Catalog(new() { RepositoryRoot = Root() }));
        Assert.Equal(File.ReadAllText(Path.Combine(PackRoot, "SKILL.md")), skill.Content);
        var locked = ReadJson(Path.Combine(PackRoot, "guidance.lock.json"));
        var jsonBlocks = Regex.Matches(Normalize(skill.Content), @"(?m)^```json\s*\n([\s\S]*?)\n```[ \t]*$")
            .Select(m => JsonSerializer.Deserialize<JsonElement>(m.Groups[1].Value)).ToArray();
        var exampleCount = 0;
        foreach (var source in sources)
        {
            var relative = source.GetProperty("path").GetString()!;
            var content = Normalize(File.ReadAllText(Path.Combine(PackRoot, relative)));
            var anchor = $"<a id=\"{source.GetProperty("id").GetString()}\"></a>";
            Assert.Equal(1, skill.Content.Split(anchor, StringSplitOptions.None).Length - 1);
            var lockedSource = Assert.Single(locked.GetProperty("sources").EnumerateArray(), s => s.GetProperty("path").GetString() == relative);
            Assert.Equal(Sha256(content), lockedSource.GetProperty("sha256").GetString());
            Assert.Equal(Encoding.UTF8.GetByteCount(content), lockedSource.GetProperty("bytes").GetInt32());
            if (source.GetProperty("kind").GetString() == "json")
            {
                var expected = JsonSerializer.Deserialize<JsonElement>(content);
                Assert.Single(jsonBlocks, actual => JsonElement.DeepEquals(expected, actual));
                exampleCount++;
                continue;
            }
            // Frontmatter becomes the generated entrypoint's metadata; local links become compiled anchors.
            // Whole paragraphs must survive compilation, including instructions containing rewritten links.
            content = Regex.Replace(content, @"\A---\n[\s\S]*?\n---\n", "");
            var paragraphs = Regex.Split(WithoutLinkDestinations(content.Trim()), @"\n[ \t]*\n")
                .Where(p => p.Length >= 16 && !p.StartsWith('#')).ToArray();
            Assert.True(paragraphs.Length > 0, $"{relative} needs substantive guidance to verify.");
            foreach (var paragraph in paragraphs) Assert.Contains(paragraph, WithoutLinkDestinations(Normalize(skill.Content)));
        }
        Assert.True(exampleCount >= 3, "Public read, scoped read and GitHub change examples must reach the selected skill.");
        Assert.Equal(1, locked.GetProperty("schemaVersion").GetInt32());
        Assert.Equal(Sha256(skill.Content), locked.GetProperty("outputSha256").GetString());
        Assert.Equal(paths.OrderBy(p => p, StringComparer.Ordinal), locked.GetProperty("sources").EnumerateArray()
            .Select(s => s.GetProperty("path").GetString()!).OrderBy(p => p, StringComparer.Ordinal));
    }

    [Fact] public async Task ActualMcpSkillCallbackReturnsTheEntireSelectedGeneratedGuidance()
    {
        var skill = SelectedSkill(new Catalog(new() { RepositoryRoot = Root() }));
        await using var bridge = await AgentMcpBridge.Create("Synthetic task evidence", skill.Content, () => true, CancellationToken.None);
        Assert.Equal(skill.Content, await ReadSkill(bridge));
        Assert.False(bridge.EvidenceRead, "Reading guidance alone must not satisfy the evidence requirement.");
        Assert.Single(bridge.Audit);
    }

    [Fact] public async Task FrozenCatalogGuidanceCannotChangeWhenItsSourceFileIsReplaced()
    {
        var root = Path.Combine(Path.GetTempPath(), "mcp-guidance-catalog-tests-" + Guid.NewGuid().ToString("N"));
        var skillDirectory = Path.Combine(root, ".agents", "skills", "platform-mcp-orchestration");
        var vendorDirectory = Path.Combine(root, "vendor", "azure-skills");
        try
        {
            Directory.CreateDirectory(skillDirectory); Directory.CreateDirectory(vendorDirectory);
            var skillFile = Path.Combine(skillDirectory, "SKILL.md");
            File.Copy(Path.Combine(PackRoot, "SKILL.md"), skillFile);
            File.WriteAllText(Path.Combine(vendorDirectory, "bundle.json"), "{\"commit\":\"synthetic\",\"skills\":[]}");
            var catalog = new Catalog(new() { RepositoryRoot = root });
            var frozen = SelectedSkill(catalog);
            await using var bridge = await AgentMcpBridge.Create("Synthetic task evidence", frozen.Content, () => true, CancellationToken.None);
            var changed = frozen.Content + "\nSOURCE-REPLACED-AFTER-FREEZE\n";
            File.WriteAllText(skillFile, changed);
            Assert.Equal(changed, SelectedSkill(catalog).Content);
            Assert.NotEqual(Sha256(changed), Sha256(frozen.Content));
            Assert.Equal(frozen.Content, await ReadSkill(bridge));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
