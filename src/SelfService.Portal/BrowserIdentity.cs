using System.Collections.Concurrent;
using System.Security.Cryptography;
using Microsoft.Identity.Client;

namespace SelfService.Portal;

// One public-client token cache per browser session. Tokens never leave this process.
public sealed class BrowserSession(PortalOptions options) : IDisposable
{
    public string Csrf { get; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
    public DateTimeOffset Created { get; } = DateTimeOffset.UtcNow;
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public IPublicClientApplication? Client { get; } = options.AuthenticationConfigured ? PublicClientApplicationBuilder.Create(options.ClientId)
        .WithAuthority($"https://login.microsoftonline.com/{options.TenantId}").WithRedirectUri("http://localhost").Build() : null;
    public IAccount? Account { get; set; }
    public ConcurrentDictionary<string, bool> Connected { get; } = new();
    public string State { get; set; } = "Not connected";
    public string? Error { get; set; }
    public CancellationTokenSource? Login { get; set; }
    public Dictionary<string, PendingRequest> Pending { get; } = new();
    public AgentSession Agent { get; } = new();
    public void Dispose() { Login?.Cancel(); _ = Agent.DisposeAsync(); /* MSAL cache becomes unreachable when session expires. */ }
}
public record PendingRequest(RunRequest Request, DateTimeOffset Expires);
public sealed class Sessions(PortalOptions options) : IDisposable
{
    readonly ConcurrentDictionary<string, BrowserSession> sessions = new();
    public void Dispose() { foreach (var s in sessions.Values) s.Dispose(); }
    public BrowserSession Get(HttpContext context)
    {
        foreach (var pair in sessions.Where(p => p.Value.Created < DateTimeOffset.UtcNow.AddHours(-8)))
            if (sessions.TryRemove(pair.Key, out var old)) old.Dispose();
        if (context.Request.Cookies.TryGetValue("portal-session", out var id) && sessions.TryGetValue(id, out var found)) return found;
        if (sessions.Count >= 64) throw new PortalException("Too many local sessions. Restart the portal.", 429);
        id = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        var session = new BrowserSession(options); sessions[id] = session;
        context.Response.Cookies.Append("portal-session", id, new CookieOptions { HttpOnly = true, SameSite = SameSiteMode.Strict, Path = "/", MaxAge = TimeSpan.FromHours(8), IsEssential = true });
        return session;
    }
}
public interface ITokenProvider { Task<string> Token(BrowserSession session, string audience); }
public sealed class BrowserIdentity : ITokenProvider
{
    public static string[] Scopes(string audience) => audience switch
    {
        "ado" => ["499b84ac-1321-427f-aa17-267ca6975798/.default"],
        "azure" => ["https://management.azure.com/.default"],
        _ => throw new PortalException("Unknown sign-in service.")
    };
    public void Begin(BrowserSession s, string audience)
    {
        var scopes = Scopes(audience);
        if (s.Client is null) throw new PortalException("Configure the portal Entra tenant and client ID first.", 409);
        if (!s.Gate.Wait(0)) throw new PortalException("Another session action is in progress.", 409);
        s.Login = new CancellationTokenSource(TimeSpan.FromMinutes(5)); s.State = "Waiting for Microsoft"; s.Error = null;
        _ = CompleteAsync(s, scopes, audience);
    }
    static async Task CompleteAsync(BrowserSession s, string[] scopes, string audience)
    {
        try
        {
            var builder = s.Client!.AcquireTokenInteractive(scopes).WithUseEmbeddedWebView(false).WithAccount(s.Account)
                .WithSystemWebViewOptions(new SystemWebViewOptions {
                    HtmlMessageSuccess = ResultPage("Sign-in received", "Return to Platform Studio. Your connection is being completed."),
                    HtmlMessageError = ResultPage("Sign-in not completed", "Return to Platform Studio to retry or cancel.") });
            var result = await builder.ExecuteAsync(s.Login!.Token);
            if (s.Account is not null && s.Account.HomeAccountId.Identifier != result.Account.HomeAccountId.Identifier)
            {
                await s.Client.RemoveAsync(result.Account); throw new PortalException("Use the same Microsoft account for both connections.");
            }
            s.Account = result.Account; s.Connected[audience] = true; s.State = "Connected";
        }
        catch (OperationCanceledException) { s.State = "Cancelled"; }
        catch (MsalException e) { s.State = "Sign-in failed"; s.Error = $"Microsoft sign-in error: {e.ErrorCode}. Check registration, consent and tenant policy."; }
        catch (PortalException e) { s.State = "Sign-in failed"; s.Error = e.Message; }
        catch { s.State = "Sign-in failed"; s.Error = "Unable to complete browser sign-in. Check local browser and registration settings."; }
        finally { s.Login?.Dispose(); s.Login = null; s.Gate.Release(); }
    }
    static string ResultPage(string title, string text) => $"<!doctype html><html lang='en'><meta charset='utf-8'><title>{title}</title><body style='background:#061322;color:#fff;font:18px Segoe UI;padding:12vw'><p style='color:#50e6ff'>PLATFORM STUDIO</p><h1>{title}</h1><p>{text}</p><p>You may close this tab.</p></body></html>";
    public async Task<string> Token(BrowserSession s, string audience)
    {
        if (s.Client is null || s.Account is null || !s.Connected.ContainsKey(audience)) throw new PortalException($"Connect {audience} first.", 401);
        try { return (await s.Client.AcquireTokenSilent(Scopes(audience), s.Account).ExecuteAsync()).AccessToken; }
        catch (MsalException) { s.Connected.TryRemove(audience, out _); throw new PortalException("Session needs consent or renewal. Connect again.", 401); }
    }
    public async Task SignOut(BrowserSession s)
    {
        if (!await s.Gate.WaitAsync(0)) { s.Login?.Cancel(); throw new PortalException("Sign-in cancellation requested. Retry disconnect when it finishes.", 409); }
        try
        {
            if (s.Client is not null) foreach (var account in await s.Client.GetAccountsAsync()) await s.Client.RemoveAsync(account);
            s.Account = null; s.Connected.Clear(); lock (s.Pending) s.Pending.Clear(); s.State = "Not connected"; s.Error = null;
        }
        finally { s.Gate.Release(); }
    }
}
