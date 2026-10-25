using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class AspireMcpCredentialsTests
{
    sealed class Tokens : ITokenProvider
    {
        public Task<string> Token(BrowserSession session, string audience) => throw new InvalidOperationException("Aspire must never request cloud credentials.");
    }
    sealed class GitHub : IGitHubIdentity
    {
        public Task<string?> Token(BrowserSession session, CancellationToken ct) => throw new InvalidOperationException("Aspire must never request GitHub credentials.");
    }
    [Fact] public async Task MissingOwnedDashboardFailsClosedWithoutUsingCloudCredentials()
    {
        var root = Path.Combine(Path.GetTempPath(), "aspire-mcp-tests-" + Guid.NewGuid().ToString("N"));
        var options = new PortalOptions { RepositoryRoot = root }; using var session = new BrowserSession(options);
        try
        {
            var resolver = new McpProviderCredentials(options, new Tokens(), new GitHub());
            var error = await Assert.ThrowsAsync<PortalException>(() => resolver.Resolve(session, "aspire", CancellationToken.None));
            Assert.Equal(409, error.Status); Assert.DoesNotContain(root, error.Message);
            Assert.DoesNotContain("aspire", PortalCredentialCheckpoint.ReconnectableProviders(session, root));
            Assert.Empty(Directory.GetFiles(root, "*.rsp", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
    [Theory]
    [InlineData("https://example.invalid/")]
    [InlineData("http://localhost:18888/")]
    [InlineData("http://127.0.0.1:18888/login?t=synthetic-private-key")]
    [InlineData("http://127.0.0.1:18888/#synthetic-private-key")]
    public async Task DashboardEndpointCannotBeSelectedOutsideLiteralLoopbackOrigin(string endpoint)
    {
        if (!OperatingSystem.IsWindows()) return;
        var root = Path.Combine(Path.GetTempPath(), "aspire-mcp-tests-" + Guid.NewGuid().ToString("N"));
        var options = new PortalOptions { RepositoryRoot = root }; using var session = new BrowserSession(options);
        try
        {
            var state = Path.Combine(root, ".local", "observability"); PortalCredentialCheckpoint.PrivateDirectory(state);
            await File.WriteAllTextAsync(Path.Combine(state, "dashboard.json"), JsonSerializer.Serialize(new { schemaVersion = 1, cliVersion = "13.6.0", dashboardUrl = endpoint, apiKey = "synthetic-private-key" }));
            await File.WriteAllTextAsync(Path.Combine(state, "process.json"), JsonSerializer.Serialize(new { schema = "platform.observability-process/v1" }));
            var resolver = new McpProviderCredentials(options, new Tokens(), new GitHub());
            var error = await Assert.ThrowsAsync<PortalException>(() => resolver.Resolve(session, "aspire", CancellationToken.None));
            Assert.Equal(409, error.Status); Assert.DoesNotContain(endpoint, error.Message); Assert.DoesNotContain("synthetic-private-key", error.Message);
            Assert.Empty(Directory.GetFiles(root, "*.rsp", SearchOption.AllDirectories));
        }
        finally { if (Directory.Exists(root)) Directory.Delete(root, true); }
    }
}
