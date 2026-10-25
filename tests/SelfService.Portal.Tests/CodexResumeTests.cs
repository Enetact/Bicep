using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class CodexResumeTests
{
    static byte[] Authentication => Encoding.UTF8.GetBytes("""
        {"auth_mode":"chatgpt","OPENAI_API_KEY":null,"tokens":{"id_token":"synthetic-id","access_token":"synthetic-access","refresh_token":"synthetic-refresh","account_id":"synthetic-account"},"last_refresh":"2026-10-17T00:00:00Z"}
        """);
    static string Fixture()
    {
        var directory = new DirectoryInfo(AppContext.BaseDirectory);
        while (directory is not null && !File.Exists(Path.Combine(directory.FullName, "config/workloads.json"))) directory = directory.Parent;
        var path = Path.GetFullPath(Path.Combine(directory!.FullName, "artifacts/codex-resume-tests", Guid.NewGuid().ToString("N")));
        Directory.CreateDirectory(path); return path;
    }
    static JsonElement Json(object value) => JsonSerializer.SerializeToElement(value);

    [Fact] public void RestoredHomeContainsOnlyExactCacheAndRebuiltIsolationPolicy()
    {
        var root = Fixture();
        try
        {
            Directory.CreateDirectory(Path.Combine(root, ".codex"));
            File.WriteAllText(Path.Combine(root, ".codex", "config.toml"), "synthetic_ambient_plugins = true");
            File.WriteAllText(Path.Combine(root, ".codex", "auth.json"), "synthetic-desktop-cache-must-not-be-copied");
            var authentication = Authentication;
            var home = CodexAgentRuntime.PrepareHome(new() { RepositoryRoot = root }, authentication);
            Assert.StartsWith(Path.Combine(root, ".local", "portal", "agents") + Path.DirectorySeparatorChar, home);
            Assert.Equal(new[] { "auth.json", "config.toml" }, Directory.GetFiles(home).Select(Path.GetFileName).Order());
            Assert.Equal(authentication, File.ReadAllBytes(Path.Combine(home, "auth.json")));
            Assert.Equal(AgentPolicy.BootstrapConfiguration, File.ReadAllText(Path.Combine(home, "config.toml")));
            Assert.DoesNotContain("synthetic_ambient", File.ReadAllText(Path.Combine(home, "config.toml")));
            Assert.Contains("cli_auth_credentials_store = \"file\"", AgentPolicy.BootstrapConfiguration);
            Assert.Contains("project_doc_max_bytes = 0", AgentPolicy.BootstrapConfiguration);
            Assert.False(Directory.Exists(Path.Combine(home, "plugins")));
            Assert.False(Directory.Exists(Path.Combine(home, "hooks")));
            if (OperatingSystem.IsWindows())
            {
                var acl = new DirectoryInfo(home).GetAccessControl();
                Assert.True(acl.AreAccessRulesProtected);
                var rules = acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().ToArray();
                var allowed = new[] { WindowsIdentity.GetCurrent().User!.Value, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null).Value };
                var identities = new List<string>();
                foreach (var rule in rules)
                {
                    identities.Add(rule.IdentityReference.Value);
                    Assert.False(rule.IsInherited); Assert.Equal(AccessControlType.Allow, rule.AccessControlType);
                }
                Assert.Equal(allowed.Order(), identities.Order());
            }
            else Assert.Equal(UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute, File.GetUnixFileMode(home));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData("{}")]
    [InlineData("[]")]
    [InlineData("not-json")]
    [InlineData("{\"auth_mode\":\"apikey\",\"tokens\":{\"id_token\":\"id\",\"access_token\":\"access\",\"refresh_token\":\"refresh\"}}")]
    [InlineData("{\"OPENAI_API_KEY\":\"synthetic-key\",\"tokens\":{\"id_token\":\"id\",\"access_token\":\"access\",\"refresh_token\":\"refresh\"}}")]
    [InlineData("{\"tokens\":{\"id_token\":\"id\",\"access_token\":\"access\"}}")]
    [InlineData("{\"tokens\":{\"id_token\":\"id\",\"access_token\":\"access\",\"refresh_token\":\" \"}}")]
    [InlineData("{\"tokens\":{\"id_token\":\"id\",\"access_token\":\"access\",\"refresh_token\":true}}")]
    [InlineData("{\"auth_mode\":\"chatgpt\",\"auth_mode\":\"apikey\",\"tokens\":{\"id_token\":\"id\",\"access_token\":\"access\",\"refresh_token\":\"refresh\"}}")]
    [InlineData("{\"tokens\":{\"id_token\":\"id\",\"access_token\":\"access\",\"access_token\":\"duplicate\",\"refresh_token\":\"refresh\"}}")]
    public void InvalidCredentialHandoffCreatesNoRuntimeHome(string text)
    {
        var root = Fixture();
        try
        {
            var error = Assert.Throws<PortalException>(() => CodexAgentRuntime.PrepareHome(new() { RepositoryRoot = root }, Encoding.UTF8.GetBytes(text)));
            Assert.Equal(409, error.Status); Assert.DoesNotContain(text, error.Message);
            Assert.False(Directory.Exists(Path.Combine(root, ".local")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Fact] public void CredentialHandoffIsBoundedAndDoesNotRequireOptionalNativeMetadata()
    {
        CodexAgentRuntime.ValidateRestoredAuthentication(Encoding.UTF8.GetBytes("""{"tokens":{"id_token":"id","access_token":"access","refresh_token":"refresh"}}"""));
        Assert.Throws<PortalException>(() => CodexAgentRuntime.ValidateRestoredAuthentication([]));
        Assert.Throws<PortalException>(() => CodexAgentRuntime.ValidateRestoredAuthentication(new byte[64 * 1024 + 1]));
        Assert.Throws<PortalException>(() => CodexAgentRuntime.ValidateRestoredAuthentication([0xff, 0xfe]));
    }

    [Fact] public async Task ProcessStartupFailureDeletesRestoredPlaintextCache()
    {
        var root = Fixture();
        try
        {
            var executable = Path.Combine(root, "codex.exe");
            File.WriteAllText(executable, "Synthetic invalid executable; cannot run a provider.");
            await Assert.ThrowsAnyAsync<Exception>(() => CodexAgentRuntime.Start(new() { RepositoryRoot = root, CodexPath = executable }, CancellationToken.None, Authentication));
            var homes = Directory.GetDirectories(Path.Combine(root, ".local", "portal", "agents"));
            Assert.Single(homes);
            Assert.False(File.Exists(Path.Combine(homes[0], "auth.json")));
            Assert.False(File.Exists(Path.Combine(homes[0], "process.json")));
        }
        finally { Directory.Delete(root, true); }
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task ResumeOnlyReadsAccountAndRequiredModelWithoutLoginOrInference(bool authenticated, bool ready)
    {
        var calls = new List<string>();
        Task<JsonElement> Protocol(string method, object parameters, CancellationToken ct)
        {
            calls.Add(method);
            if (method == "account/read")
            {
                Assert.False(Json(parameters).GetProperty("refreshToken").GetBoolean());
                return Task.FromResult(Json(new { account = authenticated ? new { type = "chatgpt" } : null }));
            }
            Assert.Equal("model/list", method);
            return Task.FromResult(Json(new { data = ready ? new[] { new { model = AgentPolicy.Model, supportedReasoningEfforts = new[] { new { reasoningEffort = AgentPolicy.Effort } } } } : [], nextCursor = (string?)null }));
        }
        if (authenticated && ready)
        {
            var result = Json(await CodexAgentRuntime.ReadRestoredStatus(Protocol, "synthetic", CancellationToken.None));
            Assert.True(result.GetProperty("authenticated").GetBoolean()); Assert.True(result.GetProperty("ready").GetBoolean());
        }
        else await Assert.ThrowsAsync<PortalException>(() => CodexAgentRuntime.ReadRestoredStatus(Protocol, "synthetic", CancellationToken.None));
        Assert.Equal(authenticated ? new[] { "account/read", "model/list" } : new[] { "account/read" }, calls);
    }

    [Fact] public async Task ResumeRejectsBusyOrExistingRunBeforeStartingAProcess()
    {
        using var session = new BrowserSession(new());
        var workflows = new AgentWorkflows(new(), null!, null!, null!);
        await session.Agent.Gate.WaitAsync();
        try { await Assert.ThrowsAsync<PortalException>(() => workflows.Resume(session, Authentication, CancellationToken.None)); }
        finally { session.Agent.Gate.Release(); }
        using var run = new CancellationTokenSource(); session.Agent.Run = run;
        await Assert.ThrowsAsync<PortalException>(() => workflows.Resume(session, Authentication, CancellationToken.None));
        Assert.Null(session.Agent.Runtime); Assert.Equal("Not connected", session.Agent.State);
        session.Agent.Run = null;
    }
}
