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
        public bool BlockLogin, WrongTokenTenant, Expired, FailAdo;
        public string AccountTenant = Tenant, Username = "developer@example.test";
        public List<string[]> Commands = new(); public List<string> Directories = new();
        public async Task<JsonElement> Run(string directory, IReadOnlyList<string> args, CancellationToken ct)
        {
            Commands.Add(args.ToArray()); Directories.Add(directory);
            if (args[0] == "login")
            {
                if (BlockLogin) await Task.Delay(Timeout.Infinite, ct);
                return JsonSerializer.SerializeToElement(Array.Empty<object>());
            }
            if (args[1] == "show") return JsonSerializer.SerializeToElement(new { tenantId = AccountTenant, environmentName = "AzureCloud", user = new { name = Username, type = "user" } });
            if (FailAdo && args.Contains("499b84ac-1321-427f-aa17-267ca6975798")) throw new PortalException("Test denied", 401);
            return JsonSerializer.SerializeToElement(new { tenant = WrongTokenTenant ? OtherTenant : AccountTenant, tokenType = "Bearer", accessToken = "test-token-not-a-credential", expires_on = DateTimeOffset.UtcNow.AddMinutes(Expired ? -1 : 60).ToUnixTimeSeconds() });
        }
    }
    static async Task Complete(BrowserSession s)
    {
        Assert.True(await s.Gate.WaitAsync(TimeSpan.FromSeconds(5))); s.Gate.Release();
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
