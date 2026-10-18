using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;

namespace SelfService.Portal;

public interface IAzureCliRunner
{
    bool Available { get; }
    Task<JsonElement> Run(string directory, IReadOnlyList<string> arguments, CancellationToken ct);
}

// Only server-authored commands reach this runner. Never invoke a shell or return CLI output/errors to the browser.
public sealed class AzureCliRunner(PortalOptions options) : IAzureCliRunner
{
    public static (string Executable, string[] Prefix)? Resolve(string configured)
    {
        var candidates = string.IsNullOrWhiteSpace(configured)
            ? (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(p => !string.IsNullOrWhiteSpace(p))
                .SelectMany(p => new[] { Path.Combine(p.Trim('"'), "az.exe"), Path.Combine(p.Trim('"'), "az.cmd") })
            : new[] { configured };
        foreach (var candidate in candidates)
        {
            if (!Path.IsPathFullyQualified(candidate) || !File.Exists(candidate)) continue;
            if (Path.GetFileName(candidate).Equals("az.exe", StringComparison.OrdinalIgnoreCase)) return (Path.GetFullPath(candidate), []);
            // Microsoft Windows MSI/ZIP distributions use ../python.exe. Bypass cmd.exe to preserve argument boundaries.
            if (!Path.GetFileName(candidate).Equals("az.cmd", StringComparison.OrdinalIgnoreCase)) continue;
            var python = Path.GetFullPath(Path.Combine(Path.GetDirectoryName(candidate)!, "..", "python.exe"));
            if (File.Exists(python) && File.ReadAllText(candidate).Contains("azure.cli", StringComparison.Ordinal)) return (python, ["-IBm", "azure.cli"]);
        }
        return null;
    }
    public bool Available => Resolve(options.AzureCliPath) is not null;
    public async Task<JsonElement> Run(string directory, IReadOnlyList<string> arguments, CancellationToken ct)
    {
        var executable = Resolve(options.AzureCliPath) ?? throw new PortalException("Install Microsoft Azure CLI, restart the portal to refresh PATH, and retry local authentication.", 409);
        var start = new ProcessStartInfo(executable.Executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
        foreach (var arg in executable.Prefix.Concat(arguments)) start.ArgumentList.Add(arg);
        // Do not inherit credentials/ADO PATs or cloud overrides from the launching terminal.
        foreach (var key in start.Environment.Keys.Where(k => k.StartsWith("AZURE_", StringComparison.OrdinalIgnoreCase) || k.StartsWith("ARM_", StringComparison.OrdinalIgnoreCase)).ToArray()) start.Environment.Remove(key);
        start.Environment["AZURE_CONFIG_DIR"] = directory;
        start.Environment["AZURE_CORE_ENABLE_BROKER_ON_WINDOWS"] = "false";
        start.Environment["AZURE_CORE_LOGIN_EXPERIENCE_V2"] = "off";
        start.Environment["AZURE_CORE_COLLECT_TELEMETRY"] = "false";
        start.Environment["AZURE_LOGGING_ENABLE_LOG_FILE"] = "false";
        start.Environment["AZURE_EXTENSION_USE_DYNAMIC_INSTALL"] = "no";
        using var process = new Process { StartInfo = start };
        var started = false;
        var receipt = Path.Combine(directory, "process.json");
        using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct); timeout.CancelAfter(TimeSpan.FromMinutes(5));
        try
        {
            process.Start(); started = true;
            using var cancel = timeout.Token.Register(() => { try { if (!process.HasExited) process.Kill(entireProcessTree: true); } catch (InvalidOperationException) { } });
            await File.WriteAllTextAsync(receipt, JsonSerializer.Serialize(new { pid = process.Id, parentPid = Environment.ProcessId, executable = executable.Executable, startedUtc = process.StartTime.ToUniversalTime() }), timeout.Token);
            async Task<string> Drain(StreamReader stream) { try { return await Read(stream, timeout.Token); } catch { await timeout.CancelAsync(); throw; } }
            var stdout = Drain(process.StandardOutput); var stderr = Drain(process.StandardError);
            await Task.WhenAll(stdout, stderr, process.WaitForExitAsync(timeout.Token));
            timeout.Token.ThrowIfCancellationRequested();
            if (process.ExitCode != 0) throw new PortalException("Azure CLI authentication did not complete. Retry sign-in; your tenant may require consent, MFA or an approved client. CLI diagnostic output is withheld because it can contain credentials.", 401);
            using var document = JsonDocument.Parse(await stdout); return document.RootElement.Clone();
        }
        finally
        {
            if (started) { try { if (!process.HasExited) { process.Kill(entireProcessTree: true); await process.WaitForExitAsync(CancellationToken.None); } } catch (InvalidOperationException) { } }
            if (File.Exists(receipt)) File.Delete(receipt);
        }
    }
    static async Task<string> Read(StreamReader reader, CancellationToken ct)
    {
        var result = new StringBuilder(); var buffer = new char[4096]; int count;
        while ((count = await reader.ReadAsync(buffer, ct)) > 0)
        {
            if (result.Length + count > 2 * 1024 * 1024) throw new PortalException("Azure CLI response exceeded the local authentication limit.", 413);
            result.Append(buffer, 0, count);
        }
        return result.ToString();
    }
}

public sealed class LocalCliSession : IAsyncDisposable
{
    public string DirectoryPath { get; }
    public SemaphoreSlim Gate { get; } = new(1, 1);
    public CancellationTokenSource Lifetime { get; } = new();
    public string? TenantId { get; set; }
    public string? Username { get; set; }
    public string? AccountId { get; set; }
    public Dictionary<string, (string Value, DateTimeOffset Expires)> Tokens { get; } = new();
    public LocalCliSession(string repository)
    {
        var root = Path.GetFullPath(Path.Combine(repository, ".local/portal/azure-cli"));
        for (var parent = new DirectoryInfo(root); parent is not null; parent = parent.Parent)
            if (parent.Exists && parent.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new PortalException("Local authentication cache cannot use a linked directory.", 409);
        DirectoryPath = Path.Combine(root, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(DirectoryPath);
        if (OperatingSystem.IsWindows())
        {
            var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
            foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
                acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(DirectoryPath).SetAccessControl(acl);
        }
        else File.SetUnixFileMode(DirectoryPath, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        using var parentProcess = Process.GetCurrentProcess();
        File.WriteAllText(Path.Combine(DirectoryPath, "owner.json"), JsonSerializer.Serialize(new { parentPid = Environment.ProcessId, parentStartedUtc = parentProcess.StartTime.ToUniversalTime() }));
    }
    public async ValueTask DisposeAsync()
    {
        await Lifetime.CancelAsync(); await Gate.WaitAsync();
        try { Tokens.Clear(); Username = TenantId = AccountId = null; RemoveOwnedDirectory(DirectoryPath); }
        finally { Gate.Release(); }
    }
    static void RemoveOwnedDirectory(string path)
    {
        if (!Directory.Exists(path)) return;
        var dir = new DirectoryInfo(path);
        if (dir.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked authentication cache refused.");
        foreach (var item in dir.EnumerateFileSystemInfos())
        {
            if (item.Attributes.HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked authentication cache entry refused.");
            if (item is DirectoryInfo) RemoveOwnedDirectory(item.FullName); else item.Delete();
        }
        dir.Delete();
    }
}

public sealed class AzureCliIdentity(PortalOptions options, IAzureCliRunner runner)
{
    public bool Available => runner.Available;
    static string Resource(string audience) => audience switch { "azure" => "https://management.azure.com/", "ado" => "499b84ac-1321-427f-aa17-267ca6975798", _ => throw new PortalException("Unknown sign-in service.") };
    // The restart handoff supplies only this portal's protected, verified cache.
    // Restoring files is not authentication: attach audiences only after fresh CLI
    // account/token checks. Browser state and token dictionaries are never restored.
    public async Task Resume(BrowserSession s, IReadOnlyDictionary<string, byte[]> files)
    {
        if (!await s.Gate.WaitAsync(0)) throw new PortalException("Another session action is in progress.", 409);
        LocalCliSession? cli = null;
        var cliHeld = false;
        try
        {
            if (s.Cli is not null || s.Account is not null || !s.Connected.IsEmpty || s.AzureTenantId is not null)
                throw new PortalException("Disconnect Microsoft before restoring a saved sign-in.", 409);
            if (!Available) throw new PortalException("Install Microsoft Azure CLI and restart the portal, then retry local authentication.", 409);
            var names = new[] { "azureProfile.json", "msal_token_cache.bin", "msal_http_cache.bin" };
            if (files is null || files.Count is < 2 or > 3 || !files.ContainsKey(names[0]) || !files.ContainsKey(names[1]) ||
                files.Keys.Any(k => !names.Contains(k, StringComparer.Ordinal)) ||
                files.Values.Any(v => v is null || v.Length is <= 0 or > 8 * 1024 * 1024) || files.Values.Sum(v => (long)v.Length) > 16 * 1024 * 1024)
                throw new PortalException("Saved Microsoft authentication files are incomplete or outside the supported limits.", 409);
            var profile = files["azureProfile.json"].AsMemory();
            if (profile.Span.StartsWith(new byte[] { 0xef, 0xbb, 0xbf })) profile = profile[3..];
            using var document = JsonDocument.Parse(profile);
            var defaults = document.RootElement.GetProperty("subscriptions").EnumerateArray()
                .Where(a => a.TryGetProperty("isDefault", out var selected) && selected.ValueKind == JsonValueKind.True).ToArray();
            if (defaults.Length != 1) throw new PortalException("Saved Microsoft authentication has no unambiguous selected account.", 401);
            var expected = AccountValue(defaults[0]);
            if (!string.IsNullOrWhiteSpace(options.TenantId) && (!Guid.TryParse(options.TenantId, out var configuredTenant) || configuredTenant.ToString() != expected.Tenant))
                throw new PortalException("Saved Microsoft authentication does not match the configured tenant. Reconnect Microsoft.", 401);
            cli = new LocalCliSession(options.RepositoryRoot);
            s.ClearMicrosoftEvidence(); s.Cli = cli; s.State = "Checking saved Microsoft sign-in"; s.Error = null;
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(cli.Lifetime.Token); timeout.CancelAfter(TimeSpan.FromSeconds(90));
            await cli.Gate.WaitAsync(timeout.Token); cliHeld = true;
            foreach (var (name, bytes) in files) await File.WriteAllBytesAsync(Path.Combine(cli.DirectoryPath, name), bytes, timeout.Token);
            var account = await Account(cli, timeout.Token);
            if (account.Tenant != expected.Tenant || account.Id != expected.Id) throw new PortalException("Saved Microsoft account or tenant changed. Reconnect Microsoft.", 401);
            cli.TenantId = account.Tenant; cli.Username = account.Name; cli.AccountId = account.Id;
            var restored = new List<string>();
            foreach (var audience in new[] { "azure", "ado" })
            {
                try { _ = await Acquire(cli, audience, timeout.Token); restored.Add(audience); }
                catch (OperationCanceledException) { throw; }
                catch { /* One audience may need consent; independently verify the other. */ }
            }
            var final = await Account(cli, timeout.Token);
            if (final.Tenant != expected.Tenant || final.Id != expected.Id || restored.Count == 0)
                throw new PortalException("Saved Microsoft sign-in could not be restored. Reconnect Microsoft.", 401);
            timeout.Token.ThrowIfCancellationRequested();
            s.AzureTenantId = account.Tenant;
            foreach (var audience in restored) s.Connected[audience] = true;
            s.State = "Connected";
            s.Error = restored.Count == 2 ? null : restored.Contains("azure")
                ? "Azure is restored. Connect Azure DevOps to renew its access."
                : "Azure DevOps is restored. Connect Azure to renew its access.";
        }
        catch (Exception error)
        {
            if (cli is not null)
            {
                s.Cli = null; s.AzureTenantId = null; s.Connected.Clear();
                if (cliHeld) { cli.Gate.Release(); cliHeld = false; }
                try { await cli.DisposeAsync(); }
                catch { s.State = "Renewal required"; s.Error = "Saved Microsoft authentication cleanup failed. Stop the portal before retrying."; throw new PortalException(s.Error, 409); }
                s.State = "Renewal required"; s.Error = "Saved Microsoft sign-in needs renewal. Reconnect Microsoft.";
            }
            if (cli is null && error is PortalException portal) throw portal;
            throw new PortalException("Saved Microsoft sign-in could not be restored. Reconnect Microsoft.", 401);
        }
        finally { if (cliHeld) cli!.Gate.Release(); s.Gate.Release(); }
    }
    public void Begin(BrowserSession s, string audience, string? tenant)
    {
        _ = Resource(audience);
        if (!string.IsNullOrWhiteSpace(tenant) && !Guid.TryParse(tenant, out _)) throw new PortalException("Tenant ID must be a GUID or empty.");
        if (!Available) throw new PortalException("Install Microsoft Azure CLI and restart the portal, then retry local authentication.", 409);
        if (s.Account is not null) throw new PortalException("Disconnect Microsoft before switching authentication modes.", 409);
        if (s.Cli?.TenantId is { } active && !string.IsNullOrWhiteSpace(tenant) && !active.Equals(tenant, StringComparison.OrdinalIgnoreCase)) throw new PortalException("Disconnect Microsoft before changing tenant.", 409);
        if (!s.Gate.Wait(0)) throw new PortalException("Another session action is in progress.", 409);
        try
        {
            s.Cli ??= new LocalCliSession(options.RepositoryRoot);
            s.Login = new CancellationTokenSource(TimeSpan.FromMinutes(5)); s.State = "Waiting for Microsoft"; s.Error = null;
            _ = Complete(s, audience, string.IsNullOrWhiteSpace(tenant) ? null : Guid.Parse(tenant).ToString());
        }
        catch { s.Gate.Release(); throw; }
    }
    async Task Complete(BrowserSession s, string audience, string? tenant)
    {
        var cli = s.Cli!;
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(s.Login!.Token, cli.Lifetime.Token);
        try
        {
            await cli.Gate.WaitAsync(ct.Token);
            try
            {
                if (cli.TenantId is null)
                {
                    var args = new List<string> { "login", "--allow-no-subscriptions", "--output", "json", "--only-show-errors" };
                    if (tenant is not null) { args.Add("--tenant"); args.Add(tenant); }
                    await runner.Run(cli.DirectoryPath, args, ct.Token);
                    var account = await Account(cli, ct.Token);
                    if (tenant is not null && tenant != account.Tenant) throw new PortalException("Microsoft returned another tenant. Disconnect and retry.", 401);
                    cli.TenantId = account.Tenant; cli.Username = account.Name; cli.AccountId = account.Id;
                    s.AzureTenantId = account.Tenant;
                }
                _ = await Acquire(cli, audience, ct.Token);
                ct.Token.ThrowIfCancellationRequested(); s.Connected[audience] = true; s.State = "Connected";
            }
            finally { cli.Gate.Release(); }
        }
        catch (OperationCanceledException) { s.State = "Cancelled"; }
        catch (PortalException e) { s.State = "Sign-in failed"; s.Error = e.Message; }
        catch { s.State = "Sign-in failed"; s.Error = "Local Azure CLI sign-in failed. Check the CLI installation and tenant policy, then retry."; }
        finally
        {
            if (s.Connected.IsEmpty)
            {
                try { await cli.DisposeAsync(); s.Cli = null; s.AzureTenantId = null; }
                catch { s.Error = "Local authentication cache cleanup failed. Stop the portal with Stop-Portal.ps1 before retrying."; }
            }
            s.Login?.Dispose(); s.Login = null; s.Gate.Release();
        }
    }
    async Task<(string Tenant, string Name, string Id)> Account(LocalCliSession cli, CancellationToken ct)
    {
        var value = await runner.Run(cli.DirectoryPath, ["account", "show", "--output", "json", "--only-show-errors"], ct);
        return AccountValue(value);
    }
    static (string Tenant, string Name, string Id) AccountValue(JsonElement value)
    {
        var tenant = value.GetProperty("tenantId").GetString(); var user = value.GetProperty("user");
        if (!Guid.TryParse(tenant, out var parsed) || value.GetProperty("environmentName").GetString() != "AzureCloud" || user.GetProperty("type").GetString() != "user")
            throw new PortalException("Use an interactive Microsoft user in Azure public cloud for local authentication.", 401);
        var name = user.GetProperty("name").GetString();
        if (string.IsNullOrWhiteSpace(name) || name.Length > 320) throw new PortalException("Azure CLI did not return a valid account identity.", 401);
        return (parsed.ToString(), name, name.ToLowerInvariant());
    }
    async Task<string> Acquire(LocalCliSession cli, string audience, CancellationToken ct)
    {
        var resource = Resource(audience);
        if (cli.Tokens.TryGetValue(audience, out var cached) && cached.Expires > DateTimeOffset.UtcNow.AddMinutes(2)) return cached.Value;
        var account = await Account(cli, ct);
        if (cli.TenantId != account.Tenant || cli.AccountId != account.Id) throw new PortalException("The local CLI account or tenant changed. Disconnect Microsoft and sign in again.", 401);
        var result = await runner.Run(cli.DirectoryPath, ["account", "get-access-token", "--tenant", cli.TenantId!, "--resource", resource, "--output", "json", "--only-show-errors"], ct);
        var token = result.GetProperty("accessToken").GetString();
        var expiryText = result.GetProperty("expires_on").ToString();
        if (result.GetProperty("tenant").GetString() != cli.TenantId || !string.Equals(result.GetProperty("tokenType").GetString(), "Bearer", StringComparison.OrdinalIgnoreCase) ||
            !long.TryParse(expiryText, out var unix) || unix <= DateTimeOffset.UtcNow.ToUnixTimeSeconds() + 120 || unix > DateTimeOffset.UtcNow.AddDays(2).ToUnixTimeSeconds() || string.IsNullOrWhiteSpace(token))
            throw new PortalException("Azure CLI returned an unqualified token. Update Azure CLI (2.54+ required) and reconnect.", 401);
        cli.Tokens[audience] = (token, DateTimeOffset.FromUnixTimeSeconds(unix)); return token;
    }
    public async Task<string> Token(BrowserSession s, string audience)
    {
        _ = Resource(audience);
        var cli = s.Cli;
        if (cli is null || !s.Connected.ContainsKey(audience)) throw new PortalException($"Connect {audience} with local authentication first.", 401);
        using var ct = CancellationTokenSource.CreateLinkedTokenSource(cli.Lifetime.Token); ct.CancelAfter(TimeSpan.FromSeconds(45));
        await cli.Gate.WaitAsync(ct.Token);
        try { return await Acquire(cli, audience, ct.Token); }
        catch { cli.Tokens.Clear(); s.ClearMicrosoftEvidence(); s.State = "Renewal required"; s.Error = "Reconnect Microsoft; if renewal fails, disconnect and sign in again."; throw new PortalException("Local authentication needs renewal. Reconnect Microsoft; if it fails, disconnect and sign in again.", 401); }
        finally { cli.Gate.Release(); }
    }
}
