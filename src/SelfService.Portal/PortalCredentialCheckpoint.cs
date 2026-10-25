using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using Microsoft.AspNetCore.DataProtection;

namespace SelfService.Portal;

// Restart handoff only. OAuth material never becomes a cookie, URL, report or response.
// A random, single-browser capability travels in the URL fragment and is immediately removed.
public sealed class PortalCredentialCheckpoint
{
    public static readonly string[] AzureNames = ["azureProfile.json", "msal_token_cache.bin", "msal_http_cache.bin"];
    const int MaximumFile = 8 * 1024 * 1024, MaximumEnvelope = 24 * 1024 * 1024;
    readonly string folder, file;
    readonly IDataProtector protector;

    public PortalCredentialCheckpoint(string repository, int port)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Protected portal restart requires Windows.");
        if (port is < 1024 or > 65535) throw new InvalidOperationException("Invalid portal port.");
        repository = Path.GetFullPath(repository);
        if (!File.Exists(Path.Combine(repository, "config/workloads.json"))) throw new InvalidOperationException("Invalid repository.");
        folder = Path.Combine(repository, ".local/portal/credential-cache", port.ToString(System.Globalization.CultureInfo.InvariantCulture));
        PrivateDirectory(folder);
        var keys = Path.Combine(folder, "keys"); PrivateDirectory(keys);
        foreach (var entry in Directory.EnumerateFileSystemEntries(keys)) RejectLinks(entry);
        protector = DataProtectionProvider.Create(new DirectoryInfo(keys), b =>
        {
            b.SetApplicationName("PlatformStudio:" + repository.ToUpperInvariant() + ":" + port);
            if (OperatingSystem.IsWindows()) b.ProtectKeysWithDpapi(protectToLocalMachine: false);
            else throw new PlatformNotSupportedException();
        })
            .CreateProtector("portal-restart-credentials", "v1");
        file = Path.Combine(folder, "restart.bin"); RejectLinks(file);
    }

    public sealed class Payload
    {
        public int Version { get; set; } = 1;
        public DateTimeOffset Expires { get; set; } = DateTimeOffset.UtcNow.AddDays(1);
        public string Proof { get; set; } = Convert.ToHexString(RandomNumberGenerator.GetBytes(32));
        public string? BrowserBinding { get; set; }
        public Dictionary<string, byte[]> AzureFiles { get; set; } = new(StringComparer.Ordinal);
        public byte[]? CodexAuth { get; set; }
        public bool AttachGitHub { get; set; } = true;
    }

    public string Checkpoint(string? azureHome, string? codexHome)
    {
        var p = new Payload();
        try
        {
            if (azureHome is not null)
            {
                ValidateOwnedHome(azureHome, "azure-cli");
                foreach (var name in AzureNames)
                {
                    var path = Path.Combine(azureHome, name);
                    if (File.Exists(path)) p.AzureFiles.Add(name, ReadBounded(path, MaximumFile));
                }
                if (!p.AzureFiles.ContainsKey(AzureNames[0]) || !p.AzureFiles.ContainsKey(AzureNames[1]))
                    throw new InvalidOperationException("Azure authentication files are incomplete; original cache retained.");
            }
            if (codexHome is not null)
            {
                ValidateOwnedHome(codexHome, "agents");
                p.CodexAuth = ReadBounded(Path.Combine(codexHome, "auth.json"), 1024 * 1024);
            }
            if (p.AzureFiles.Values.Sum(v => v.Length) > 16 * 1024 * 1024) throw new InvalidOperationException("Authentication cache exceeds its limit.");
            // Even an empty checkpoint is a deliberate, one-time GitHub CLI attachment request.
            using var guard = Lock();
            if (File.Exists(file))
            {
                var existing = Read();
                try { if (existing.Expires > DateTimeOffset.UtcNow) throw new InvalidOperationException("An unconsumed restart checkpoint exists. Resume it before another restart."); }
                finally { Wipe(existing); }
            }
            Write(p);
            var verified = Read();
            try
            {
                if (verified.Proof != p.Proof || verified.AzureFiles.Count != p.AzureFiles.Count ||
                    verified.AzureFiles.Any(pair => !p.AzureFiles.TryGetValue(pair.Key, out var expected) || !pair.Value.AsSpan().SequenceEqual(expected)) ||
                    !(verified.CodexAuth ?? []).AsSpan().SequenceEqual(p.CodexAuth ?? []))
                    throw new CryptographicException("Protected checkpoint verification failed.");
            }
            finally { Wipe(verified); }
            return p.Proof;
        }
        finally { Wipe(p); }
    }

    public string RecoveryProof()
    {
        using var guard = Lock(); var p = Read();
        try
        {
            Validate(p);
            if (p.BrowserBinding is not null) throw new InvalidOperationException("Checkpoint was already claimed by a browser.");
            return p.Proof;
        }
        finally { Wipe(p); }
    }

    public Lease Open(string proof, string csrf, bool browserAlreadyConnected)
    {
        if (!Regex.IsMatch(proof ?? "", "^[A-Fa-f0-9]{64}$")) throw new PortalException("Restart resume link is invalid.", 403);
        var guard = Lock(); Payload? p = null;
        try
        {
            p = Read(); Validate(p);
            if (!CryptographicOperations.FixedTimeEquals(Convert.FromHexString(proof!), Convert.FromHexString(p.Proof)))
                throw new PortalException("Restart resume link is invalid.", 403);
            var binding = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csrf)));
            if (p.BrowserBinding is not null && p.BrowserBinding != binding)
                throw new PortalException("Restart credentials were already claimed by another browser.", 409);
            if (p.BrowserBinding is null && browserAlreadyConnected)
                throw new PortalException("Open the restart link in a fresh portal session.", 409);
            p.BrowserBinding = binding; Write(p);
            return new Lease(this, guard, p);
        }
        catch { guard.Dispose(); if (p is not null) Wipe(p); throw; }
    }

    public sealed class Lease : IDisposable
    {
        readonly PortalCredentialCheckpoint owner; readonly FileStream guard;
        public Payload Data { get; }
        internal Lease(PortalCredentialCheckpoint owner, FileStream guard, Payload data) { this.owner = owner; this.guard = guard; Data = data; }
        public void AzureRestored() { foreach (var b in Data.AzureFiles.Values) CryptographicOperations.ZeroMemory(b); Data.AzureFiles.Clear(); owner.Write(Data); }
        public void CodexRestored() { if (Data.CodexAuth is { } b) CryptographicOperations.ZeroMemory(b); Data.CodexAuth = null; owner.Write(Data); }
        public void GitHubAttempted() { Data.AttachGitHub = false; owner.Write(Data); }
        public void Complete() { if (Data.AzureFiles.Count == 0 && Data.CodexAuth is null) { RejectLinks(owner.file); File.Delete(owner.file); } }
        public void Dispose() { Wipe(Data); guard.Dispose(); }
    }

    public void Forget(string csrf, string provider)
    {
        using var guard = Lock(); if (!File.Exists(file)) return;
        var p = Read();
        try
        {
            var binding = Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(csrf)));
            if (p.BrowserBinding != binding) return;
            if (provider == "microsoft") { foreach (var b in p.AzureFiles.Values) CryptographicOperations.ZeroMemory(b); p.AzureFiles.Clear(); }
            if (provider == "codex") { if (p.CodexAuth is { } b) CryptographicOperations.ZeroMemory(b); p.CodexAuth = null; }
            if (p.AzureFiles.Count == 0 && p.CodexAuth is null) File.Delete(file); else Write(p);
        }
        finally { Wipe(p); }
    }

    public static string[] ReconnectableProviders(BrowserSession session, string? repository = null) => McpProviderBroker.Providers
        .Where(p => p.Id switch
        {
            "azure" or "ado" or "fabric" => session.Connected.ContainsKey(p.Id),
            "github" => session.GitHub.UserId is not null,
            "bicep" or "learn" => true,
            "aspire" => repository is not null && AspireMcpConfiguration.IsAvailable(repository),
            _ => false
        }).Select(p => p.Id).ToArray();

    public void RecordResult(BrowserSession session, IReadOnlyList<string> errors)
    {
        // Local operational evidence only: no account IDs, cookies, proofs, credentials or tool results.
        var path = Path.Combine(folder, "resume-status.json"); RejectLinks(path);
        var temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using var process = System.Diagnostics.Process.GetCurrentProcess();
            File.WriteAllText(temporary, JsonSerializer.Serialize(new
            {
                schema = "platform.portal-resume-status/v1", observedUtc = DateTimeOffset.UtcNow,
                portalPid = Environment.ProcessId, portalStartedUtc = process.StartTime.ToUniversalTime(),
                microsoft = new { azure = session.Connected.ContainsKey("azure"), ado = session.Connected.ContainsKey("ado"), fabric = session.Connected.ContainsKey("fabric") },
                codexReady = session.Agent.Runtime is not null && session.Agent.State == "Ready", githubConnected = session.GitHub.UserId is not null,
                providers = session.Mcp.Connections.Values.OrderBy(c => c.Id).Select(c => new { id = c.Id, state = c.State, toolCount = c.Tools.Length }).ToArray(),
                pendingPlans = session.Mcp.Plans.Count, errors
            }));
            RejectLinks(path); File.Move(temporary, path, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }

    FileStream Lock()
    {
        var path = Path.Combine(folder, "checkpoint.lock"); RejectLinks(path);
        try { return new FileStream(path, FileMode.OpenOrCreate, FileAccess.ReadWrite, FileShare.None); }
        catch (IOException) { throw new PortalException("Another restart credential action is in progress.", 409); }
    }
    Payload Read()
    {
        if (!File.Exists(file)) throw new PortalException("Restart credentials were already used or are unavailable.", 409);
        var encrypted = ReadBounded(file, MaximumEnvelope); byte[]? plain = null;
        try { plain = protector.Unprotect(encrypted); return JsonSerializer.Deserialize<Payload>(plain) ?? throw new CryptographicException(); }
        catch (Exception e) when (e is JsonException or CryptographicException) { throw new PortalException("Protected restart credentials cannot be opened by this Windows user or portal.", 409); }
        finally { if (plain is not null) CryptographicOperations.ZeroMemory(plain); }
    }
    void Write(Payload p)
    {
        var plain = JsonSerializer.SerializeToUtf8Bytes(p); byte[] encrypted;
        try { encrypted = protector.Protect(plain); } finally { CryptographicOperations.ZeroMemory(plain); }
        if (encrypted.Length > MaximumEnvelope) throw new InvalidOperationException("Authentication checkpoint exceeds its limit.");
        var temporary = Path.Combine(folder, Guid.NewGuid().ToString("N") + ".tmp");
        try
        {
            using (var stream = new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough)) { stream.Write(encrypted); stream.Flush(true); }
            RejectLinks(file); File.Move(temporary, file, true);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    static void Validate(Payload p)
    {
        if (p.Version != 1 || p.Expires <= DateTimeOffset.UtcNow || p.Expires > DateTimeOffset.UtcNow.AddDays(2) ||
            !Regex.IsMatch(p.Proof, "^[A-F0-9]{64}$") || p.AzureFiles.Count > 3 || p.AzureFiles.Keys.Any(k => !AzureNames.Contains(k)) ||
            p.AzureFiles.Values.Any(b => b.Length is 0 or > MaximumFile) || p.AzureFiles.Values.Sum(v => v.Length) > 16 * 1024 * 1024 || p.CodexAuth is { Length: > 1024 * 1024 })
            throw new PortalException("Restart credentials expired or have an invalid format. Connect again.", 409);
    }
    static byte[] ReadBounded(string path, int maximum)
    {
        RejectLinks(path);
        using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        if (stream.Length is 0 || stream.Length > maximum) throw new InvalidOperationException("Authentication cache file has an invalid size.");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    void ValidateOwnedHome(string home, string kind)
    {
        home = Path.GetFullPath(home); var root = Path.GetDirectoryName(Path.GetDirectoryName(folder))!;
        var expectedRoot = Path.Combine(root, kind);
        if (!string.Equals(Path.GetDirectoryName(home), expectedRoot, StringComparison.OrdinalIgnoreCase) ||
            !Regex.IsMatch(Path.GetFileName(home), "^[a-f0-9]{32}$")) throw new InvalidOperationException("Only portal-owned authentication homes can be checkpointed.");
        RejectLinks(home);
    }
    public static void PrivateDirectory(string path)
    {
        RejectLinks(path); Directory.CreateDirectory(path);
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var acl = new DirectorySecurity(); acl.SetAccessRuleProtection(true, false);
        foreach (var sid in new[] { WindowsIdentity.GetCurrent().User!, new SecurityIdentifier(WellKnownSidType.LocalSystemSid, null) })
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(acl);
    }
    public static void RejectLinks(string path)
    {
        for (FileSystemInfo? p = File.Exists(path) ? new FileInfo(path) : new DirectoryInfo(path); p is not null;
            p = p is DirectoryInfo d ? d.Parent : ((FileInfo)p).Directory)
            if (p.LinkTarget is not null || p.Exists && (p.Attributes & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked authentication paths are refused.");
    }
    static void Wipe(Payload p) { foreach (var b in p.AzureFiles.Values) CryptographicOperations.ZeroMemory(b); if (p.CodexAuth is { } auth) CryptographicOperations.ZeroMemory(auth); }

    public static int RunCommand(string[] args)
    {
        try
        {
            var allowed = new[] { "--repository", "--port", "--azure-home", "--codex-home" };
            var values = new Dictionary<string, string>(StringComparer.Ordinal);
            for (var i = 1; i < args.Length; i += 2)
                if (i + 1 >= args.Length || !allowed.Contains(args[i]) || !values.TryAdd(args[i], args[i + 1])) throw new InvalidOperationException();
            var cache = new PortalCredentialCheckpoint(values["--repository"], int.Parse(values["--port"], System.Globalization.CultureInfo.InvariantCulture));
            var proof = args[0] == "--get-resume-proof" ? cache.RecoveryProof() : cache.Checkpoint(values.GetValueOrDefault("--azure-home"), values.GetValueOrDefault("--codex-home"));
            Console.WriteLine(proof); return 0; // Only the calling lifecycle script captures this capability.
        }
        catch { Console.Error.WriteLine("Protected credential checkpoint failed. Original caches and any previous checkpoint must be retained."); return 1; }
    }
}

public sealed record PortalResumeRequest(string Proof);
