using System.Text.Json;
using SelfService.Portal;
using Xunit;

namespace SelfService.Portal.Tests;

public sealed class LocalCliIdentityTests
{
    const string Tenant = "11111111-2222-3333-4444-555555555555";
    const string OtherTenant = "aaaaaaaa-bbbb-cccc-dddd-eeeeeeeeeeee";
    static PortalOptions Options()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "config/workloads.json"))) dir = dir.Parent;
        return new() { RepositoryRoot = dir!.FullName };
    }
    sealed class FakeCli : IAzureCliRunner
    {
        public bool Available { get; set; } = true;
        public bool BlockLogin, WrongTokenTenant, Expired, FailAdo, FailAzure, FailAccountWithSensitiveError;
        public string AccountTenant = Tenant, Username = "developer@example.test", AccountCloud = "AzureCloud", AccountType = "user";
        public int SwitchAccountAtRead, AccountReads;
        public List<string[]> Commands = new(); public List<string> Directories = new();
        public async Task<JsonElement> Run(string directory, IReadOnlyList<string> args, CancellationToken ct)
        {
            Commands.Add(args.ToArray()); Directories.Add(directory);
            if (args[0] == "login")
            {
                if (BlockLogin) await Task.Delay(Timeout.Infinite, ct);
                return JsonSerializer.SerializeToElement(Array.Empty<object>());
            }
            if (args[1] == "show")
            {
                if (FailAccountWithSensitiveError) throw new PortalException("synthetic-sensitive-runner-error-token", 401);
                AccountReads++;
                return JsonSerializer.SerializeToElement(new { tenantId = AccountTenant, environmentName = AccountCloud,
                    user = new { name = SwitchAccountAtRead > 0 && AccountReads >= SwitchAccountAtRead ? "changed@example.test" : Username, type = AccountType } });
            }
            if (FailAdo && args.Contains("499b84ac-1321-427f-aa17-267ca6975798")) throw new PortalException("Test denied", 401);
            if (FailAzure && args.Contains("https://management.azure.com/")) throw new PortalException("Test denied", 401);
            return JsonSerializer.SerializeToElement(new { tenant = WrongTokenTenant ? OtherTenant : AccountTenant, tokenType = "Bearer", accessToken = "test-token-not-a-credential", expires_on = DateTimeOffset.UtcNow.AddMinutes(Expired ? -1 : 60).ToUnixTimeSeconds() });
        }
    }
    static async Task Complete(BrowserSession s)
    {
        Assert.True(await s.Gate.WaitAsync(TimeSpan.FromSeconds(5))); s.Gate.Release();
    }
    static Dictionary<string, byte[]> SavedFiles(string tenant = Tenant, string username = "developer@example.test") => new(StringComparer.Ordinal)
    {
        ["azureProfile.json"] = JsonSerializer.SerializeToUtf8Bytes(new { subscriptions = new[] { new { tenantId = tenant, environmentName = "AzureCloud", isDefault = true, user = new { name = username, type = "user" } } } }),
        ["msal_token_cache.bin"] = System.Text.Encoding.UTF8.GetBytes("synthetic-encrypted-msal-cache")
    };
    [Fact] public async Task ResumeRestoresOnlyVerifiedAudiencesIntoAFreshPrivateDirectoryWithoutLogin()
    {
        var o = Options(); var fake = new FakeCli(); var identity = new AzureCliIdentity(o, fake); using var s = new BrowserSession(o);
        var files = SavedFiles(); files["msal_http_cache.bin"] = [1, 2, 3];
        s.NetworkReports["stale"] = JsonSerializer.SerializeToElement(new { }); var epoch = s.MicrosoftIdentityEpoch;
        await identity.Resume(s, files);
        Assert.Equal("Connected", s.State); Assert.Null(s.Error); Assert.Equal(Tenant, s.AzureTenantId);
        Assert.Equal(new[] { "ado", "azure" }, s.Connected.Keys.Order()); Assert.Empty(s.NetworkReports); Assert.True(s.MicrosoftIdentityEpoch > epoch);
        Assert.Equal("developer@example.test", s.Cli!.AccountId); Assert.Null(s.Account);
        Assert.DoesNotContain(fake.Commands, c => c[0] == "login");
        Assert.Equal(2, fake.Commands.Count(c => c.Contains("get-access-token")));
        Assert.All(fake.Directories, path => Assert.Equal(s.Cli.DirectoryPath, path));
        foreach (var file in files) Assert.Equal(file.Value, await File.ReadAllBytesAsync(Path.Combine(s.Cli.DirectoryPath, file.Key)));
        Assert.True(File.Exists(Path.Combine(s.Cli.DirectoryPath, "owner.json")));
        var directory = s.Cli.DirectoryPath; await new BrowserIdentity(identity).SignOut(s); Assert.False(Directory.Exists(directory));
    }
    [Theory][InlineData(true, false, "ado")][InlineData(false, true, "azure")]
    public async Task ResumeTreatsAzureAndAdoConsentIndependently(bool azureFails, bool adoFails, string restored)
    {
        var o = Options(); var fake = new FakeCli { FailAzure = azureFails, FailAdo = adoFails }; var identity = new AzureCliIdentity(o, fake); using var s = new BrowserSession(o);
        await identity.Resume(s, SavedFiles());
        Assert.Equal(new[] { restored }, s.Connected.Keys); Assert.Equal("Connected", s.State); Assert.NotNull(s.Error);
        Assert.DoesNotContain(fake.Commands, c => c[0] == "login");
        await new BrowserIdentity(identity).SignOut(s);
    }
    [Theory]
    [InlineData("tenant")][InlineData("user")][InlineData("cloud")][InlineData("principal-type")][InlineData("expired")][InlineData("token-tenant")][InlineData("both-denied")][InlineData("final-account-change")]
    public async Task ResumeRejectsChangedIdentityAndUnqualifiedTokensAndRemovesOnlyItsFreshCache(string failure)
    {
        var o = Options(); var fake = new FakeCli(); var identity = new AzureCliIdentity(o, fake); using var s = new BrowserSession(o);
        await using var other = new LocalCliSession(o.RepositoryRoot); var keep = Path.Combine(other.DirectoryPath, "keep.txt"); await File.WriteAllTextAsync(keep, "unrelated cache");
        switch (failure)
        {
            case "tenant": fake.AccountTenant = OtherTenant; break;
            case "user": fake.Username = "another@example.test"; break;
            case "cloud": fake.AccountCloud = "AzureUSGovernment"; break;
            case "principal-type": fake.AccountType = "servicePrincipal"; break;
            case "expired": fake.Expired = true; break;
            case "token-tenant": fake.WrongTokenTenant = true; break;
            case "both-denied": fake.FailAzure = fake.FailAdo = true; break;
            case "final-account-change": fake.SwitchAccountAtRead = 4; break;
        }
        await Assert.ThrowsAsync<PortalException>(() => identity.Resume(s, SavedFiles()));
        Assert.Null(s.Cli); Assert.Null(s.AzureTenantId); Assert.Empty(s.Connected); Assert.Equal("Renewal required", s.State);
        Assert.All(fake.Directories, path => Assert.False(Directory.Exists(path))); Assert.True(File.Exists(keep));
        Assert.DoesNotContain(fake.Commands, c => c[0] == "login");
    }
    [Theory][InlineData("../auth.json")][InlineData("auth.json")][InlineData("owner.json")][InlineData("AzureProfile.json")]
    public async Task ResumeRejectsUnapprovedFileNamesBeforeCreatingACache(string name)
    {
        var o = Options(); var fake = new FakeCli(); var identity = new AzureCliIdentity(o, fake); using var s = new BrowserSession(o);
        var files = SavedFiles(); files[name] = [1];
        await Assert.ThrowsAsync<PortalException>(() => identity.Resume(s, files)); Assert.Null(s.Cli); Assert.Empty(fake.Commands);
    }
    [Theory][InlineData("missing")][InlineData("empty")][InlineData("oversize")][InlineData("malformed")][InlineData("no-default")][InlineData("ambiguous-default")]
    public async Task ResumeRejectsIncompleteOrAmbiguousCacheBeforeStartingCli(string failure)
    {
        var o = Options(); var fake = new FakeCli(); var identity = new AzureCliIdentity(o, fake); using var s = new BrowserSession(o); var files = SavedFiles();
        switch (failure)
        {
            case "missing": files.Remove("msal_token_cache.bin"); break;
            case "empty": files["msal_token_cache.bin"] = []; break;
            case "oversize": files["msal_token_cache.bin"] = new byte[8 * 1024 * 1024 + 1]; break;
            case "malformed": files["azureProfile.json"] = [1, 2, 3]; break;
            case "no-default": files["azureProfile.json"] = JsonSerializer.SerializeToUtf8Bytes(new { subscriptions = Array.Empty<object>() }); break;
            case "ambiguous-default":
                var profile = JsonSerializer.Deserialize<JsonElement>(files["azureProfile.json"]).GetProperty("subscriptions")[0];
                files["azureProfile.json"] = JsonSerializer.SerializeToUtf8Bytes(new { subscriptions = new[] { profile, profile } }); break;
        }
        await Assert.ThrowsAsync<PortalException>(() => identity.Resume(s, files)); Assert.Null(s.Cli); Assert.Empty(fake.Commands);
    }
    [Fact] public async Task ResumeHonorsConfiguredTenantAndDoesNotExposeRawRunnerFailures()
    {
        var o = Options(); o.TenantId = OtherTenant; var fake = new FakeCli(); var identity = new AzureCliIdentity(o, fake); using var s = new BrowserSession(o);
        await Assert.ThrowsAsync<PortalException>(() => identity.Resume(s, SavedFiles())); Assert.Empty(fake.Commands); Assert.Null(s.Cli);
        o.TenantId = Tenant; fake.FailAccountWithSensitiveError = true;
        var error = await Assert.ThrowsAsync<PortalException>(() => identity.Resume(s, SavedFiles()));
        Assert.DoesNotContain("synthetic-sensitive", error.Message); Assert.DoesNotContain("synthetic-sensitive", s.Error); Assert.Null(s.Cli);
        Assert.All(fake.Directories, path => Assert.False(Directory.Exists(path)));
    }
    [Fact] public async Task ResumeCannotOverwriteAnExistingMicrosoftSessionOrOverlapAnotherAction()
    {
        var o = Options(); var fake = new FakeCli(); var identity = new AzureCliIdentity(o, fake); using var s = new BrowserSession(o);
        await s.Gate.WaitAsync();
        try { await Assert.ThrowsAsync<PortalException>(() => identity.Resume(s, SavedFiles())); } finally { s.Gate.Release(); }
        Assert.Empty(fake.Commands); s.Connected["azure"] = true; s.State = "Connected";
        await Assert.ThrowsAsync<PortalException>(() => identity.Resume(s, SavedFiles())); Assert.True(s.Connected.ContainsKey("azure")); Assert.Equal("Connected", s.State);
        s.Connected.Clear();
        fake.Available = false; await Assert.ThrowsAsync<PortalException>(() => identity.Resume(s, SavedFiles())); Assert.Null(s.Cli); Assert.Empty(fake.Commands);
    }
    [Fact] public async Task ResumePreservesAnExistingCliSessionAndAcceptsBomProfilesOnlyForFreshSessions()
    {
        var o = Options(); var fake = new FakeCli(); var identity = new AzureCliIdentity(o, fake); using var s = new BrowserSession(o);
        s.Cli = new LocalCliSession(o.RepositoryRoot); var existing = s.Cli; var directory = existing.DirectoryPath;
        await Assert.ThrowsAsync<PortalException>(() => identity.Resume(s, SavedFiles())); Assert.Same(existing, s.Cli); Assert.True(Directory.Exists(directory)); Assert.Empty(fake.Commands);
        await new BrowserIdentity(identity).SignOut(s);
        var files = SavedFiles(); files["azureProfile.json"] = new byte[] { 0xef, 0xbb, 0xbf }.Concat(files["azureProfile.json"]).ToArray();
        await identity.Resume(s, files); Assert.Equal("Connected", s.State); Assert.DoesNotContain(fake.Commands, command => command[0] == "login");
        await new BrowserIdentity(identity).SignOut(s);
    }
    [Fact] public async Task LocalSignInNeedsNoRegistrationAndAdoUsesSeparateAudienceWithoutSecondLogin()
    {
        var o = Options(); var fake = new FakeCli(); var cli = new AzureCliIdentity(o, fake); var router = new BrowserIdentity(cli);
        using var s = new BrowserSession(o);
        cli.Begin(s, "azure", null); await Complete(s);
        Assert.Equal("Connected", s.State); Assert.Null(s.Account); Assert.Equal(Tenant, s.AzureTenantId);
        Assert.True(s.Connected.ContainsKey("azure")); Assert.False(s.Connected.ContainsKey("ado"));
        var directory = s.Cli!.DirectoryPath;
        Assert.StartsWith(Path.GetFullPath(Path.Combine(o.RepositoryRoot, ".local/portal/azure-cli")), directory);
        Assert.Equal("test-token-not-a-credential", await router.Token(s, "azure"));
        cli.Begin(s, "ado", null); await Complete(s);
        Assert.Single(fake.Commands, c => c[0] == "login");
        Assert.Contains(fake.Commands, c => c.Contains("499b84ac-1321-427f-aa17-267ca6975798"));
        Assert.Contains(fake.Commands, c => c.Contains("https://management.azure.com/"));
        Assert.All(fake.Directories, d => Assert.Equal(directory, d));
        await router.SignOut(s); Assert.False(Directory.Exists(directory)); Assert.Empty(s.Connected); Assert.Null(s.AzureTenantId);
        await Assert.ThrowsAsync<PortalException>(() => router.Token(s, "ado"));
    }
    [Theory][InlineData(true,false)][InlineData(false,true)]
    public async Task WrongTenantOrExpiredTokenDoesNotEstablishConnection(bool wrong, bool expired)
    {
        var o = Options(); var fake = new FakeCli { WrongTokenTenant = wrong, Expired = expired }; var cli = new AzureCliIdentity(o, fake);
        using var s = new BrowserSession(o); cli.Begin(s, "azure", null); await Complete(s);
        Assert.Equal("Sign-in failed", s.State); Assert.Empty(s.Connected); Assert.Null(s.Cli); Assert.Null(s.AzureTenantId);
        Assert.All(fake.Directories, d => Assert.False(Directory.Exists(d)));
    }
    [Fact] public async Task ExplicitTenantIsValidatedAndPassedAsOneArgument()
    {
        var o = Options(); var fake = new FakeCli(); var cli = new AzureCliIdentity(o, fake); using var s = new BrowserSession(o);
        Assert.Throws<PortalException>(() => cli.Begin(s, "azure", "tenant; unwanted command"));
        Assert.Empty(fake.Commands); cli.Begin(s, "azure", Tenant); await Complete(s);
        Assert.Contains(fake.Commands, c => c[0] == "login" && c[^2] == "--tenant" && c[^1] == Tenant);
        Assert.Throws<PortalException>(() => cli.Begin(s, "ado", OtherTenant));
        await new BrowserIdentity(cli).SignOut(s);
    }
    [Fact] public async Task ReturnedTenantMustMatchExplicitRequest()
    {
        var o=Options(); var fake=new FakeCli(); var cli=new AzureCliIdentity(o,fake); using var s=new BrowserSession(o);
        cli.Begin(s,"azure",OtherTenant); await Complete(s); Assert.Empty(s.Connected); Assert.Null(s.Cli);
    }
    [Fact] public async Task CancellationStopsPendingLoginAndRemovesOnlyOwnedCache()
    {
        var o=Options(); var fake=new FakeCli{BlockLogin=true}; var cli=new AzureCliIdentity(o,fake); using var s=new BrowserSession(o);
        await using var other=new LocalCliSession(o.RepositoryRoot);
        var otherFile=Path.Combine(other.DirectoryPath,"keep.txt"); await File.WriteAllTextAsync(otherFile,"unrelated session");
        cli.Begin(s,"azure",null); var directory=s.Cli!.DirectoryPath;
        Assert.Throws<PortalException>(()=>cli.Begin(s,"ado",null));
        s.Login!.Cancel(); await Complete(s);
        Assert.Equal("Cancelled",s.State); Assert.Empty(s.Connected); Assert.False(Directory.Exists(directory)); Assert.True(File.Exists(otherFile));
    }
    [Fact] public async Task FailedAdoConnectionPreservesWorkingAzureConnection()
    {
        var o=Options(); var fake=new FakeCli{FailAdo=true}; var cli=new AzureCliIdentity(o,fake); using var s=new BrowserSession(o);
        cli.Begin(s,"azure",null); await Complete(s); cli.Begin(s,"ado",null); await Complete(s);
        Assert.True(s.Connected.ContainsKey("azure")); Assert.False(s.Connected.ContainsKey("ado")); Assert.NotNull(s.Cli);
        await new BrowserIdentity(cli).SignOut(s);
    }
    [Fact] public async Task RenewalReusesCliCacheButAccountChangeInvalidatesEvidenceAndAllAudiences()
    {
        var o=Options(); var fake=new FakeCli(); var cli=new AzureCliIdentity(o,fake); var router=new BrowserIdentity(cli); using var s=new BrowserSession(o);
        cli.Begin(s,"azure",null); await Complete(s); cli.Begin(s,"ado",null); await Complete(s);
        s.Cli!.Tokens["azure"]=("old",DateTimeOffset.UtcNow);
        Assert.Equal("test-token-not-a-credential",await router.Token(s,"azure"));
        Assert.Equal(2,fake.Commands.Count(c=>c.Contains("get-access-token")&&c.Contains("https://management.azure.com/")));
        s.NetworkReports["old"]=JsonSerializer.SerializeToElement(new{}); var epoch=s.NetworkEpoch;
        s.Cli.Tokens["azure"]=("old",DateTimeOffset.UtcNow); fake.Username="someone-else@example.test";
        await Assert.ThrowsAsync<PortalException>(()=>router.Token(s,"azure"));
        Assert.Empty(s.Connected); Assert.Empty(s.Cli.Tokens); Assert.Empty(s.NetworkReports); Assert.True(s.NetworkEpoch>epoch);
        await router.SignOut(s);
    }
    [Fact] public async Task AuthenticationMethodsCannotBeMixedAndUnknownAudiencesNeverExecute()
    {
        var o=Options(); var fake=new FakeCli(); var cli=new AzureCliIdentity(o,fake); var router=new BrowserIdentity(cli); using var s=new BrowserSession(o);
        Assert.Throws<PortalException>(()=>cli.Begin(s,"untrusted",null)); Assert.Empty(fake.Commands);
        cli.Begin(s,"azure",null); await Complete(s);
        Assert.Throws<PortalException>(()=>router.Begin(s,"ado"));
        await Assert.ThrowsAsync<PortalException>(()=>router.Token(s,"https://untrusted.example"));
        await router.SignOut(s);
    }
    [Fact] public void MissingCliDoesNotCreateSessionOrRunCommand()
    {
        var o=Options(); var fake=new FakeCli{Available=false}; var cli=new AzureCliIdentity(o,fake); using var s=new BrowserSession(o);
        Assert.Throws<PortalException>(()=>cli.Begin(s,"azure",null)); Assert.Null(s.Cli); Assert.Empty(fake.Commands);
        Assert.Null(AzureCliRunner.Resolve("relative/az.cmd")); Assert.Null(AzureCliRunner.Resolve("C:/does-not-exist/az.cmd"));
    }
    [LocalCliFact] public async Task InstalledCliProcessRunsWithoutShellInIsolatedDirectory()
    {
        var options=Options(); var runner=new AzureCliRunner(options); await using var cache=new LocalCliSession(options.RepositoryRoot);
        using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(45));
        var result=await runner.Run(cache.DirectoryPath,["version","--output","json"],timeout.Token);
        Assert.True(Version.Parse(result.GetProperty("azure-cli").GetString()!)>=new Version(2,54));
        Assert.False(File.Exists(Path.Combine(cache.DirectoryPath,"process.json")));
        Assert.True(File.Exists(Path.Combine(cache.DirectoryPath,"owner.json")));
    }
    sealed class LocalCliFactAttribute : FactAttribute
    {
        public LocalCliFactAttribute() { if(!OperatingSystem.IsWindows()||AzureCliRunner.Resolve("") is null) Skip="Requires an installed Windows Azure CLI; unit tests use an isolated fake."; }
    }
}
