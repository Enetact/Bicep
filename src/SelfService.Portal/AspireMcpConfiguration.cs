using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Portal;

// The dashboard is a local credential-bearing service. Only the launcher-owned,
// private configuration can select its endpoint; model arguments cannot do so.
internal static class AspireMcpConfiguration
{
    public static bool IsAvailable(string repository)
    {
        try { _ = ReadOwned(repository); return true; }
        catch { return false; }
    }
    public static ProviderConnectionSpec Resolve(string repository, string directory)
    {
        string? responseFile = null;
        try
        {
            var (cli, url, key) = ReadOwned(repository);
            PortalCredentialCheckpoint.PrivateDirectory(directory);
            responseFile = Path.Combine(directory, "api-key-" + Guid.NewGuid().ToString("N") + ".rsp");
            // System.CommandLine response files are supported by the pinned official
            // CLI. The key never enters Windows command arguments or process receipts.
            using (var output = new StreamWriter(new FileStream(responseFile, FileMode.CreateNew, FileAccess.Write, FileShare.None)))
                output.Write("--api-key\n" + key + "\n");
            return new("aspire", cli, ["agent", "mcp", "--dashboard-url", url, "@" + responseFile, "--log-level", "None", "--non-interactive", "--nologo"], null,
                new() { ["ASPIRE_HOME"] = directory, ["ASPIRE_TELEMETRY_OPTOUT"] = "1" }, [], directory)
            { RedactionValues = [key], CredentialFiles = [responseFile] };
        }
        catch
        {
            if (responseFile is not null) try { File.Delete(responseFile); } catch { }
            // Never surface private config paths, values or process exceptions.
            throw new PortalException("Start the owned Aspire dashboard with scripts/Start-Observability.ps1, then connect Aspire. Its private configuration, pinned runtime and live ownership must all be valid.", 409);
        }
    }

    static (string Cli, string Url, string Key) ReadOwned(string repository)
    {
        var root = Path.GetFullPath(repository);
        var configuration = Path.Combine(root, ".local", "observability", "dashboard.json");
        var receiptPath = Path.Combine(root, ".local", "observability", "process.json");
        using var config = ReadPrivate(configuration);
        using var receipt = ReadPrivate(receiptPath);
        var settings = config.RootElement; var owner = receipt.RootElement;
        if (settings.GetProperty("schemaVersion").GetInt32() != 1 || settings.GetProperty("cliVersion").GetString() != "13.6.0" ||
            owner.GetProperty("schema").GetString() != "platform.observability-process/v1") throw new InvalidOperationException();
        var url = settings.GetProperty("dashboardUrl").GetString()!;
        if (!Uri.TryCreate(url, UriKind.Absolute, out var endpoint) || endpoint.Scheme != "http" || endpoint.Host != "127.0.0.1" ||
            endpoint.Port is < 1024 or > 65535 || endpoint.AbsolutePath != "/" || endpoint.Query.Length != 0 || endpoint.Fragment.Length != 0 || endpoint.UserInfo.Length != 0)
            throw new InvalidOperationException();
        var cli = Path.GetFullPath(settings.GetProperty("cliPath").GetString()!);
        var tools = Path.Combine(root, ".tools", "aspire-cli") + Path.DirectorySeparatorChar;
        if (!cli.StartsWith(tools, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(cli) != "aspire.exe" || !File.Exists(cli)) throw new InvalidOperationException();
        PortalCredentialCheckpoint.RejectLinks(cli);
        if (owner.GetProperty("cliVersion").GetString() != "13.6.0" || owner.GetProperty("dashboardUrl").GetString() != url ||
            !string.Equals(Path.GetFullPath(owner.GetProperty("executable").GetString()!), cli, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        using var process = Process.GetProcessById(owner.GetProperty("pid").GetInt32());
        if (process.HasExited || process.StartTime.ToUniversalTime().Ticks != owner.GetProperty("startedUtc").GetDateTimeOffset().UtcTicks ||
            !string.Equals(process.MainModule?.FileName, cli, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        var dashboard = Path.GetFullPath(owner.GetProperty("listenerExecutable").GetString()!);
        var dashboardRoot = Path.Combine(root, ".local", "observability", "aspire-home") + Path.DirectorySeparatorChar;
        if (!dashboard.StartsWith(dashboardRoot, StringComparison.OrdinalIgnoreCase) || Path.GetFileName(dashboard) != "Aspire.Dashboard.exe") throw new InvalidOperationException();
        PortalCredentialCheckpoint.RejectLinks(dashboard);
        using var listener = Process.GetProcessById(owner.GetProperty("listenerPid").GetInt32());
        if (listener.HasExited || listener.StartTime.ToUniversalTime().Ticks != owner.GetProperty("listenerStartedUtc").GetDateTimeOffset().UtcTicks ||
            !string.Equals(listener.MainModule?.FileName, dashboard, StringComparison.OrdinalIgnoreCase)) throw new InvalidOperationException();
        var key = settings.GetProperty("apiKey").GetString()!;
        // Launcher keys have no quoting or response-file control characters.
        if (!Regex.IsMatch(key, "^[A-Za-z0-9+/=_-]{22,256}$", RegexOptions.CultureInvariant)) throw new InvalidOperationException();
        return (cli, url, key);
    }

    static JsonDocument ReadPrivate(string path)
    {
        PortalCredentialCheckpoint.RejectLinks(path);
        var file = new FileInfo(path);
        if (!OperatingSystem.IsWindows() || !file.Exists || file.Length is < 1 or > 65536) throw new InvalidOperationException();
        var security = file.GetAccessControl(AccessControlSections.Access | AccessControlSections.Owner);
        var current = WindowsIdentity.GetCurrent().User!;
        var system = new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null);
        if (!current.Equals(security.GetOwner(typeof(SecurityIdentifier)))) throw new InvalidOperationException();
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && !current.Equals(rule.IdentityReference) && !system.Equals(rule.IdentityReference)) throw new InvalidOperationException();
        return JsonDocument.Parse(File.ReadAllBytes(path), new JsonDocumentOptions { MaxDepth = 8 });
    }
}
