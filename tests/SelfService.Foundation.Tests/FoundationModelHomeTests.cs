using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using SelfService.Portal;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationModelHomeTests
{
    [Fact]
    public void Concrete_adapter_uses_customer_cell_actor_bound_roots_and_fresh_private_native_homes_without_processes()
    {
        using var f = new HomeFixture(); var client = new CodexFoundationModelClient(f.Root, "customer-a", "cell-a", f.Executable);
        var options = client.IsolatedOptions(f.Invocation());
        var first = CodexAgentRuntime.PrepareHome(options); var second = CodexAgentRuntime.PrepareHome(options);
        Assert.NotEqual(first, second); Assert.Equal(Path.GetDirectoryName(first), options.AgentRuntimeRoot);
        Assert.StartsWith(f.Root + Path.DirectorySeparatorChar, first, StringComparison.Ordinal);
        Assert.False(File.Exists(Path.Combine(first, "auth.json"))); Assert.False(File.Exists(Path.Combine(first, "process.json")));
        Assert.Contains("[features]", File.ReadAllText(Path.Combine(first, "config.toml")));
        var otherActor = client.IsolatedOptions(f.Invocation() with { ActorKey = "user:11111111-1111-1111-1111-111111111111:bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb" });
        Assert.NotEqual(options.AgentRuntimeRoot, otherActor.AgentRuntimeRoot);
        var otherCell = new CodexFoundationModelClient(f.Root, "customer-b", "cell-b", f.Executable).IsolatedOptions(f.Invocation() with { CustomerId = "customer-b", CellId = "cell-b" });
        Assert.NotEqual(options.AgentRuntimeRoot, otherCell.AgentRuntimeRoot); Assert.False(client.IsMock);
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("cell")]
    [InlineData("actor")]
    [InlineData("skill")]
    [InlineData("digest")]
    [InlineData("scope")]
    public void Caller_cannot_retarget_runtime_scope_or_supply_unpinned_skill(string mutation)
    {
        using var f = new HomeFixture(); var client = new CodexFoundationModelClient(f.Root, "customer-a", "cell-a", f.Executable); var invocation = f.Invocation();
        invocation = mutation switch
        {
            "customer" => invocation with { CustomerId = "customer-b" }, "cell" => invocation with { CellId = "cell-b" },
            "actor" => invocation with { ActorKey = "../../operator" }, "skill" => invocation with { SkillName = "arbitrary-script" },
            "scope" => invocation with { ProjectId = "../../other" }, _ => invocation with { SkillSha256 = new string('a', 64) }
        };
        Assert.Throws<FoundationAuthorizationException>(() => client.IsolatedOptions(invocation));
    }

    [Fact]
    public async Task No_default_authentication_means_no_process_no_cache_read_and_no_home_creation()
    {
        using var f = new HomeFixture(); var client = new CodexFoundationModelClient(f.Root, "customer-a", "cell-a", f.Executable);
        var error = await Assert.ThrowsAsync<FoundationStoreException>(() => client.ReviewAsync(f.Invocation(), null!, default));
        Assert.Equal("ModelAuthenticationNotConfigured", error.Code); Assert.Empty(Directory.GetDirectories(f.Root));
    }

    [Fact]
    public async Task Authentication_for_another_actor_is_rejected_before_any_process_or_auth_file()
    {
        using var f = new HomeFixture(); var invocation = f.Invocation();
        var client = new CodexFoundationModelClient(f.Root, "customer-a", "cell-a", f.Executable, (_, _) => Task.FromResult<FoundationModelAuthentication?>(
            new("customer-a", "cell-a", "user:11111111-1111-1111-1111-111111111111:bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb", Encoding.UTF8.GetBytes("synthetic-invalid-cache"))));
        await Assert.ThrowsAsync<FoundationAuthorizationException>(() => client.ReviewAsync(invocation, null!, default));
        Assert.Empty(Directory.GetFiles(f.Root, "auth.json", SearchOption.AllDirectories)); Assert.Empty(Directory.GetFiles(f.Root, "process.json", SearchOption.AllDirectories));
    }

    [Fact]
    public void Authentication_object_never_serializes_or_displays_cache_bytes()
    {
        using var auth = new FoundationModelAuthentication("customer-a", "cell-a", "actor", Encoding.UTF8.GetBytes("synthetic-private-token-cache"));
        Assert.Throws<JsonException>(() => JsonSerializer.Serialize(auth)); Assert.DoesNotContain("synthetic-private-token-cache", auth.ToString());
    }

    [Fact]
    public void Optional_runtime_root_preserves_legacy_default_and_rejects_relative_overrides()
    {
        using var f = new HomeFixture(); var legacy = CodexAgentRuntime.PrepareHome(new PortalOptions { RepositoryRoot = f.Root });
        Assert.StartsWith(Path.Combine(f.Root, ".local", "portal", "agents") + Path.DirectorySeparatorChar, legacy, StringComparison.Ordinal);
        Assert.Throws<PortalException>(() => CodexAgentRuntime.PrepareHome(new PortalOptions { RepositoryRoot = f.Root, AgentRuntimeRoot = "../caller-selected" }));
        Assert.Contains("no approval", CodexAgentRuntime.FoundationDeveloperInstructions);
    }

    sealed class HomeFixture : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "foundation-model-home-tests-" + Guid.NewGuid().ToString("N"));
        public string Executable => Path.Combine(Root, "codex.exe");
        public HomeFixture() => Directory.CreateDirectory(Root);
        public FoundationModelInvocation Invocation()
        {
            const string skill = "Reviewed synthetic skill";
            return new(Guid.NewGuid().ToString("D"), "customer-a", "cell-a", "user:11111111-1111-1111-1111-111111111111:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa",
                "default", "dev", "Review source", "platform-mcp-orchestration", skill, Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(skill))).ToLowerInvariant());
        }
        public void Dispose() { if (Directory.Exists(Root)) Directory.Delete(Root, recursive: true); }
    }
}
