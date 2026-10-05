using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class GitHubIdentityTests
{
    sealed class Runner : IGitHubCliRunner
    {
        public bool Available { get; set; } = true;
        public bool Fail, Wait;
        public List<GitHubCommand> Commands { get; } = [];
        public async Task<string> Run(GitHubCommand command, Action<string>? code, CancellationToken ct) {
            Commands.Add(command);
            if (Fail) throw new PortalException("Synthetic credential unavailable.", 401);
            if (command == GitHubCommand.Login) { code?.Invoke("ABCD-EFGH"); if (Wait) await Task.Delay(Timeout.Infinite, ct); }
            return command == GitHubCommand.Token ? "synthetic-github-credential" : "";
        }
    }
    sealed class Handler : HttpMessageHandler
    {
        public long Id = 123; public HttpStatusCode Status = HttpStatusCode.OK; public int Calls;
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage r, CancellationToken ct) {
            Calls++; Assert.Equal("https://api.github.com/user", r.RequestUri!.AbsoluteUri); Assert.Equal(HttpMethod.Get, r.Method);
            Assert.Equal("synthetic-github-credential", r.Headers.Authorization!.Parameter);
            return Task.FromResult(new HttpResponseMessage(Status) { Content = JsonContent.Create(new { id = Id, login = "octocat" }) });
        }
    }
    static JsonElement Status(GitHubIdentity identity, BrowserSession session) => JsonSerializer.SerializeToElement(identity.Status(session));
    static async Task Finish(GitHubIdentity identity, BrowserSession session) {
        for (var i = 0; i < 100 && Status(identity, session).GetProperty("waiting").GetBoolean(); i++) await Task.Delay(10);
        Assert.False(Status(identity, session).GetProperty("waiting").GetBoolean());
    }
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task BrowserAndExistingFlowsVerifyIdentityWithoutExposingCredential(bool existing) {
        var runner = new Runner(); var handler = new Handler(); var identity = new GitHubIdentity(new(handler), runner);
        using var session = new BrowserSession(new());
        identity.Begin(session, existing); await Finish(identity, session);
        var status = Status(identity, session);
        Assert.True(status.GetProperty("connected").GetBoolean()); Assert.Equal("octocat", status.GetProperty("user").GetString());
        Assert.DoesNotContain("credential", status.GetRawText()); Assert.Equal(existing ? 1 : 2, runner.Commands.Count);
        Assert.Equal("synthetic-github-credential", await identity.Token(session, CancellationToken.None));
        session.RegistrationPending["test"] = new(new(1, []), "", "", new(1, "", "", 1), DateTimeOffset.UtcNow);
        identity.Disconnect(session); Assert.Empty(session.RegistrationPending); Assert.Null(await identity.Token(session, CancellationToken.None));
    }
    [Theory] [InlineData(false)] [InlineData(true)]
    public async Task AccountSwitchOrExpiredCredentialInvalidatesConnectionAndReview(bool failure) {
        var runner = new Runner(); var handler = new Handler(); var identity = new GitHubIdentity(new(handler), runner);
        using var session = new BrowserSession(new()); identity.Begin(session, true); await Finish(identity, session);
        session.RegistrationPending["test"] = new(new(1, []), "", "", new(1, "", "", 1), DateTimeOffset.UtcNow);
        if (failure) runner.Fail = true; else handler.Id = 999;
        await Assert.ThrowsAsync<PortalException>(() => identity.Token(session, CancellationToken.None));
        Assert.False(Status(identity, session).GetProperty("connected").GetBoolean()); Assert.Empty(session.RegistrationPending);
    }
    [Fact] public async Task CancellationDoesNotAttachOrVerifyAccountAndCannotOverlap() {
        var runner = new Runner { Wait = true }; var handler = new Handler(); var identity = new GitHubIdentity(new(handler), runner);
        using var session = new BrowserSession(new()); using var other = new BrowserSession(new());
        identity.Begin(session, false); Assert.Equal("ABCD-EFGH", Status(identity, session).GetProperty("code").GetString());
        Assert.Throws<PortalException>(() => identity.Begin(other, true));
        await Assert.ThrowsAsync<PortalException>(() => identity.Token(session, CancellationToken.None));
        identity.Disconnect(session); await Finish(identity, session);
        Assert.False(Status(identity, session).GetProperty("connected").GetBoolean()); Assert.Equal(0, handler.Calls);
        Assert.Equal(JsonValueKind.Null, Status(identity, session).GetProperty("code").ValueKind);
    }
    [Fact] public async Task UnauthenticatedOrDeniedIdentityNeverBecomesConnected() {
        var runner = new Runner(); var handler = new Handler { Status = HttpStatusCode.Unauthorized }; var identity = new GitHubIdentity(new(handler), runner);
        using var session = new BrowserSession(new());
        Assert.Null(await identity.Token(session, CancellationToken.None)); Assert.Equal(0, handler.Calls);
        identity.Begin(session, true); await Finish(identity, session);
        Assert.False(Status(identity, session).GetProperty("connected").GetBoolean()); Assert.Equal("Sign-in failed", Status(identity, session).GetProperty("state").GetString());
    }
    [Fact] public void CliCommandsAreFixedAndDoNotInheritInjectedCredentials() {
        Environment.SetEnvironmentVariable("GH_TOKEN", "synthetic-injected-token");
        try {
            var start = GitHubCliRunner.StartInfo("C:/gh.exe", GitHubCommand.Login);
            Assert.False(start.UseShellExecute); Assert.True(start.CreateNoWindow); Assert.False(start.Environment.ContainsKey("GH_TOKEN"));
            Assert.Equal(new[] { "auth", "login", "--hostname", "github.com", "--web", "--git-protocol", "https", "--skip-ssh-key", "--clipboard=false" }, start.ArgumentList);
            Assert.Equal(new[] { "auth", "token", "--hostname", "github.com" }, GitHubCliRunner.StartInfo("C:/gh.exe", GitHubCommand.Token).ArgumentList);
        } finally { Environment.SetEnvironmentVariable("GH_TOKEN", null); }
    }
    [Theory]
    [InlineData("! First copy your one-time code: ABCD-EFGH\nOpen browser", "ABCD-EFGH")]
    [InlineData("arbitrary token ABCD-EFGH", null)]
    [InlineData("one-time code: ABCD-EFGHI", null)]
    public void OnlyDeviceCodeIsProjectedFromOutput(string text, string? expected) => Assert.Equal(expected, GitHubCliRunner.DeviceCode(text));
}
