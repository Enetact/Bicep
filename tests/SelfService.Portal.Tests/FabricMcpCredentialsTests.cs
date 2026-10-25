using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class FabricMcpCredentialsTests
{
    sealed class Tokens : ITokenProvider
    {
        public List<string> Audiences { get; } = [];
        public Task<string> Token(BrowserSession session, string audience) { Audiences.Add(audience); return Task.FromResult("synthetic-fabric-bearer"); }
    }
    sealed class GitHub : IGitHubIdentity
    {
        public Task<string?> Token(BrowserSession session, CancellationToken ct) => throw new InvalidOperationException("Fabric must not use GitHub credentials.");
    }
    [Fact] public async Task OfficialEndpointReceivesOnlyExplicitFabricHostCredential()
    {
        var root = Path.Combine(Path.GetTempPath(), "fabric-mcp-tests-" + Guid.NewGuid().ToString("N"));
        var options = new PortalOptions { RepositoryRoot = root }; var tokens = new Tokens();
        using var session = new BrowserSession(options);
        try
        {
            var resolver = new McpProviderCredentials(options, tokens, new GitHub());
            session.Connected["azure"] = true; session.Connected["ado"] = true;
            await Assert.ThrowsAsync<PortalException>(() => resolver.Resolve(session, "fabric", CancellationToken.None));
            Assert.Empty(tokens.Audiences);
            session.Connected["fabric"] = true;
            var spec = await resolver.Resolve(session, "fabric", CancellationToken.None);
            Assert.Equal("https://api.fabric.microsoft.com/v1/mcp/core", spec.Url);
            Assert.Null(spec.Command); Assert.Empty(spec.Args); Assert.Empty(spec.Environment);
            Assert.Equal("Bearer synthetic-fabric-bearer", spec.Headers["Authorization"]);
            Assert.Single(spec.Headers); Assert.Equal(new[] { "fabric" }, tokens.Audiences);
            Assert.DoesNotContain("synthetic-fabric-bearer", spec.ToString());
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
