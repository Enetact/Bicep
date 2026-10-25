using System.Diagnostics;
using System.Net.Http.Headers;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Portal;

public enum GitHubCommand { Login, Token }
public interface IGitHubCliRunner
{
    bool Available { get; }
    Task<string> Run(GitHubCommand command, Action<string>? deviceCode, CancellationToken ct);
}
public interface IGitHubIdentity { Task<string?> Token(BrowserSession session, CancellationToken ct); }

// The CLI owns its normal local credential store. The portal never copies that store,
// runs shell commands, exposes tokens to JavaScript, or logs CLI output.
public sealed class GitHubCliRunner(PortalOptions options) : IGitHubCliRunner
{
    public string? Resolve()
    {
        if (!string.IsNullOrWhiteSpace(options.GitHubCliPath))
            return Path.IsPathFullyQualified(options.GitHubCliPath) && File.Exists(options.GitHubCliPath) && Path.GetFileName(options.GitHubCliPath).Equals("gh.exe", StringComparison.OrdinalIgnoreCase) ? options.GitHubCliPath : null;
        var candidates = new[] { Path.Combine(options.RepositoryRoot, ".tools/github-cli/bin/gh.exe"), Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.ProgramFiles), "GitHub CLI/gh.exe") }
            .Concat((Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(Path.IsPathFullyQualified).Select(p => Path.Combine(p, "gh.exe")));
        return candidates.FirstOrDefault(File.Exists);
    }
    public bool Available => Resolve() is not null;
    public static string? DeviceCode(string text) => Regex.Match(text, @"one-time code:\s*([A-Z0-9]{4}-[A-Z0-9]{4})(?![A-Z0-9])", RegexOptions.CultureInvariant).Groups[1] is { Success: true } match ? match.Value : null;
    public static ProcessStartInfo StartInfo(string executable, GitHubCommand command)
    {
        var p = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, RedirectStandardInput = true };
        foreach (var key in p.Environment.Keys.Where(k => k.StartsWith("GH_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("GITHUB_", StringComparison.OrdinalIgnoreCase) || k == "BROWSER").ToArray()) p.Environment.Remove(key);
        p.Environment["GH_PROMPT_DISABLED"] = "1"; p.Environment["GH_NO_UPDATE_NOTIFIER"] = "1"; p.Environment["NO_COLOR"] = "1";
        foreach (var arg in command switch {
            GitHubCommand.Login => new[] { "auth", "login", "--hostname", "github.com", "--web", "--git-protocol", "https", "--skip-ssh-key", "--clipboard=false", "--scopes", "workflow" },
            GitHubCommand.Token => new[] { "auth", "token", "--hostname", "github.com" },
            _ => throw new PortalException("Unsupported GitHub command.") }) p.ArgumentList.Add(arg);
        return p;
    }
    public async Task<string> Run(GitHubCommand command, Action<string>? deviceCode, CancellationToken ct)
    {
        var executable = Resolve() ?? throw new PortalException("GitHub CLI is missing. Run scripts/Install-GitHubCli.ps1, then retry.", 409);
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(command == GitHubCommand.Login ? TimeSpan.FromMinutes(10) : TimeSpan.FromSeconds(30));
        var folder = Path.Combine(options.RepositoryRoot, ".local/portal/github-cli", Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(folder);
        using var process = new Process { StartInfo = StartInfo(executable, command) };
        try {
            if (!process.Start()) throw new PortalException("GitHub CLI could not start.", 502);
            File.WriteAllText(Path.Combine(folder, "process.json"), JsonSerializer.Serialize(new { pid = process.Id, parentPid = Environment.ProcessId, executable,
                startedUtc = process.StartTime.ToUniversalTime(), parentStartedUtc = Process.GetCurrentProcess().StartTime.ToUniversalTime() }));
            process.StandardInput.Close(); // Explicit flags + non-TTY device flow have no prompts.
            async Task<string> Read(StreamReader reader, bool codes) {
                // StreamReader can wait to fill a larger buffer on a long-lived pipe.
                // Single-character reads surface the short device prompt immediately.
                var result = new StringBuilder(); var buffer = new char[1]; int n; var reported = false;
                while ((n = await reader.ReadAsync(buffer.AsMemory(), timeout.Token)) > 0) {
                    if (result.Length + n > 64 * 1024) { timeout.Cancel(); throw new PortalException("GitHub CLI output exceeded its limit.", 502); }
                    result.Append(buffer, 0, n);
                    if (codes && !reported && DeviceCode(result.ToString()) is { } code) { deviceCode?.Invoke(code); reported = true; }
                }
                return result.ToString();
            }
            var stdout = Read(process.StandardOutput, false); var stderr = Read(process.StandardError, command == GitHubCommand.Login);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
            if (process.ExitCode != 0) throw new PortalException(command == GitHubCommand.Token ? "No usable local GitHub credential. Choose Sign in to GitHub." : "GitHub sign-in failed or expired. Retry and check browser consent, network and organization policy.", 401);
            return command == GitHubCommand.Token ? stdout.Result.Trim() : "";
        } catch (OperationCanceledException) { throw; }
        catch (PortalException) { throw; }
        catch { throw new PortalException("GitHub CLI could not complete. Check the installed executable and retry.", 502); }
        finally {
            try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } } catch (InvalidOperationException) { }
            // Generated directory contains only this process receipt, never credentials.
            File.Delete(Path.Combine(folder, "process.json")); Directory.Delete(folder);
        }
    }
}

public sealed class GitHubSession : IDisposable
{
    public object Sync { get; } = new();
    public CancellationTokenSource? Login;
    public string State = "Not connected";
    public string? Code, Error, User;
    public long? UserId;
    public long Epoch;
    public bool Disposed;
    public void Dispose() { lock (Sync) { Disposed = true; Epoch++; Login?.Cancel(); UserId = null; User = null; Code = null; } }
}

public sealed class GitHubIdentity(HttpClient http, IGitHubCliRunner runner) : IGitHubIdentity
{
    // CLI login alters one local user's active credential; serialize interactive flows across browser sessions.
    static readonly SemaphoreSlim LoginGate = new(1, 1);
    public async Task Resume(BrowserSession s, CancellationToken ct)
    {
        if (!await LoginGate.WaitAsync(0, ct)) throw new PortalException("A GitHub sign-in is already active.", 409);
        try
        {
            long epoch;
            lock (s.GitHub.Sync)
            {
                if (s.GitHub.Disposed || s.GitHub.Login is not null || s.GitHub.UserId is not null) throw new PortalException("Use a fresh GitHub session for restart resume.", 409);
                epoch = ++s.GitHub.Epoch;
            }
            var credential = await Credential(ct);
            lock (s.GitHub.Sync)
            {
                if (s.GitHub.Disposed || s.GitHub.Epoch != epoch) throw new PortalException("GitHub connection changed during restart resume.", 409);
                ct.ThrowIfCancellationRequested(); s.GitHub.UserId = credential.Id; s.GitHub.User = credential.Login;
                s.GitHub.State = "Connected"; s.GitHub.Error = s.GitHub.Code = null;
            }
        }
        finally { LoginGate.Release(); }
    }
    public object Status(BrowserSession s) { lock (s.GitHub.Sync) return new { available = runner.Available, state = s.GitHub.State, connected = s.GitHub.UserId is not null,
        user = s.GitHub.User, code = s.GitHub.Code, error = s.GitHub.Error, waiting = s.GitHub.Login is not null, verificationUrl = "https://github.com/login/device" }; }
    public void Begin(BrowserSession s, bool useExisting)
    {
        if (!runner.Available) throw new PortalException("GitHub CLI is missing. Run scripts/Install-GitHubCli.ps1, then retry.", 409);
        lock (s.GitHub.Sync) {
            if (s.GitHub.Disposed || s.GitHub.Login is not null || !LoginGate.Wait(0)) throw new PortalException("A GitHub sign-in is already active. Complete or cancel it first.", 409);
            s.GitHub.Epoch++; s.GitHub.UserId = null; s.GitHub.User = null; s.GitHub.Error = null; s.GitHub.Code = null;
            lock (s.RegistrationPending) s.RegistrationPending.Clear();
            s.GitHub.Login = new CancellationTokenSource(TimeSpan.FromMinutes(10)); s.GitHub.State = useExisting ? "Checking local GitHub account" : "Waiting for GitHub";
            _ = Complete(s, useExisting, s.GitHub.Epoch, s.GitHub.Login);
        }
    }
    async Task<(string Token, long Id, string Login)> Credential(CancellationToken ct)
    {
        var token = await runner.Run(GitHubCommand.Token, null, ct);
        if (token.Length is < 10 or > 4096 || token.Any(char.IsWhiteSpace)) throw new PortalException("GitHub credential is invalid. Sign in again.", 401);
        using var request = new HttpRequestMessage(HttpMethod.Get, "https://api.github.com/user");
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", token); request.Headers.UserAgent.ParseAdd("PlatformStudio/1.0"); request.Headers.Add("X-GitHub-Api-Version", "2022-11-28");
        using var response = await http.SendAsync(request, HttpCompletionOption.ResponseHeadersRead, ct);
        if (!response.IsSuccessStatusCode) throw new PortalException($"GitHub account verification returned HTTP {(int)response.StatusCode}. Reconnect or check GitHub access.", 401);
        await response.Content.LoadIntoBufferAsync(64 * 1024, ct);
        var user = JsonSerializer.Deserialize<JsonElement>(await response.Content.ReadAsStringAsync(ct));
        if (!user.TryGetProperty("id", out var id) || !id.TryGetInt64(out var number) || number <= 0 || !user.TryGetProperty("login", out var login) || !Regex.IsMatch(login.GetString() ?? "", "^[A-Za-z0-9-]{1,39}$")) throw new PortalException("GitHub identity could not be verified.", 502);
        return (token, number, login.GetString()!);
    }
    async Task Complete(BrowserSession s, bool useExisting, long epoch, CancellationTokenSource login)
    {
        try {
            if (!useExisting) await runner.Run(GitHubCommand.Login, code => { lock (s.GitHub.Sync) if (s.GitHub.Epoch == epoch) s.GitHub.Code = code; }, login.Token);
            var credential = await Credential(login.Token);
            lock (s.GitHub.Sync) if (s.GitHub.Epoch == epoch && !s.GitHub.Disposed) { login.Token.ThrowIfCancellationRequested(); s.GitHub.UserId = credential.Id; s.GitHub.User = credential.Login; s.GitHub.State = "Connected"; }
        } catch (Exception e) {
            lock (s.GitHub.Sync) if (s.GitHub.Epoch == epoch) { s.GitHub.State = e is OperationCanceledException ? "Cancelled or expired" : "Sign-in failed"; s.GitHub.Error = e is PortalException p ? p.Message : e is OperationCanceledException ? null : "GitHub verification failed. Check local CLI and network access, then retry."; }
        } finally {
            lock (s.GitHub.Sync) { s.GitHub.Code = null; s.GitHub.Login = null; login.Dispose(); }
            LoginGate.Release();
        }
    }
    public void Disconnect(BrowserSession s)
    {
        if (s.Mcp.Connections.TryGetValue("github", out var connection)) connection.Lifetime.Cancel();
        lock (s.GitHub.Sync) { s.GitHub.Epoch++; s.GitHub.Login?.Cancel(); s.GitHub.UserId = null; s.GitHub.User = null; s.GitHub.Code = null; s.GitHub.Error = null; s.GitHub.State = "Not connected"; }
        lock (s.RegistrationPending) s.RegistrationPending.Clear();
    }
    public async Task<string?> Token(BrowserSession s, CancellationToken ct)
    {
        long? id; long epoch;
        lock (s.GitHub.Sync) { id = s.GitHub.UserId; epoch = s.GitHub.Epoch; if (s.GitHub.Login is not null) throw new PortalException("Complete GitHub sign-in before checking repository source.", 409); }
        if (id is null) return null; // Existing public-repository anonymous reads remain supported.
        try {
            var result = await Credential(ct);
            lock (s.GitHub.Sync) if (s.GitHub.Disposed || epoch != s.GitHub.Epoch || result.Id != id) throw new PortalException("The local GitHub account changed. Reconnect GitHub and review registration again.", 401);
            return result.Token;
        } catch {
            lock (s.GitHub.Sync) if (s.GitHub.Epoch == epoch) Disconnect(s);
            throw; // Never silently downgrade a failed authenticated request to anonymous.
        }
    }
}
