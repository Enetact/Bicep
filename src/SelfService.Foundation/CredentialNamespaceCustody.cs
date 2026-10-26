using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Foundation;

// Every construction/provision entry fails through LocalPath before any Windows API on another OS.
#pragma warning disable CA1416
// Local Windows owner custody only. Checksums bind metadata; they do not defeat a malicious OS owner.
internal sealed class CredentialNamespaceCustody
{
    internal const string MarkerName = ".credential-namespace.json";
    readonly string root, kind, customer, cell, sid, namespaceId;
    internal string Root => root;
    internal string NamespaceId => namespaceId;
    internal bool IsBoundDispatchOnly => kind == "credential-vault-scoped-bound-dispatch";
    internal string NamespaceCommitmentSha256 => CheckRoot().Sha256;
    string GuardName => Guard(kind);
    static string Guard(string kind) => kind switch
    {
        "credential-vault" or "credential-vault-fenced" or "credential-vault-scoped-fenced" or "credential-vault-scoped-bound-dispatch" => ".vault.lock",
        "windows-user-keys" => ".keys.lock",
        "credential-rollback-authority" or "credential-rollback-scoped-authority" or "credential-rollback-scoped-bound-authority" => ".rollback.lock",
        _ => throw Failure("credential-namespace-binding-invalid")
    };
    internal sealed record Marker(int Schema, string Kind, string CustomerId, string CellId, string RootPath,
        string OwnerSid, string NamespaceId, string Sha256);

    internal CredentialNamespaceCustody(string path, string kind, string customer, string cell)
    {
        this.root = LocalPath(path); this.kind = kind; this.customer = customer; this.cell = cell;
        sid = CurrentSid().Value; var marker = CheckRoot(); namespaceId = marker.NamespaceId;
        ValidateFile(Path.Combine(root, GuardName));
        if (new FileInfo(Path.Combine(root, GuardName)).Length != 0) throw Failure("credential-namespace-guard-invalid");
    }

    internal static void Provision(string path, string kind, string customer, string cell)
    {
        path = PreflightNew(path, kind, customer, cell);
        var sid = CurrentSid();
        if (!CreateDirectory(path, sid)) throw Failure("credential-namespace-already-exists-or-reservation-failed");
        // Never delete or adopt an existing/partial namespace, including after an interrupted provision.
        var unsigned = new Marker(1, kind, customer, cell, path, sid.Value, Guid.NewGuid().ToString("N"), "");
        var marker = unsigned with { Sha256 = Digest(unsigned) };
        using (var file = CreateFile(Path.Combine(path, Guard(kind)))) file.Flush(true);
        using (var file = CreateFile(Path.Combine(path, MarkerName)))
        { JsonSerializer.Serialize(file, marker, CredentialRules.Json); file.Flush(true); }
        _ = new CredentialNamespaceCustody(path, kind, customer, cell);
    }
    internal static string PreflightNew(string path, string kind, string customer, string cell)
    {
        path = LocalPath(path); _ = Guard(kind);
        if (
            !Guid.TryParseExact(customer, "D", out var c) || c == Guid.Empty || !Guid.TryParseExact(cell, "D", out var e) || e == Guid.Empty)
            throw Failure("credential-namespace-binding-invalid");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw Failure("credential-namespace-parent-required");
        RejectProtectedAncestors(path);
        if (Path.Exists(path)) throw Failure("credential-namespace-already-exists-or-reservation-failed");
        return path;
    }

    internal FileStream OpenGuard()
    {
        CheckRoot(); var path = Path.Combine(root, GuardName); ValidateFile(path);
        FileStream? guard = null;
        try
        {
            guard = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            CheckRoot(); ValidateFile(path);
            if (guard.Length != 0) throw Failure("credential-namespace-guard-invalid");
            return guard;
        }
        catch { guard?.Dispose(); throw; }
    }

    internal void ValidateCurrent() { CheckRoot(); ValidateFile(Path.Combine(root, GuardName)); }
    // Immutable namespace identity only. Consumer probes deliberately do not open or inspect the mutable admission gate.
    internal void ValidateMetadataIdentity() => _ = CheckRoot();
    internal CredentialNamespaceMetadataPin HoldMetadataFile(string name)
    {
        if (name is not (MarkerName or "rollback-binding.json" or "authority.json"))
            throw Failure("credential-namespace-metadata-file-invalid");
        ValidateMetadataIdentity(); var path = Path.Combine(root, name); ValidateFile(path);
        FileStream? stream = null;
        try
        {
            stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            ValidateMetadataIdentity(); ValidateFile(path);
            if (stream.Length is < 1 or > 16384) throw Failure("credential-namespace-file-size-invalid");
            var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes);
            return new(this, path, stream, bytes);
        }
        catch { stream?.Dispose(); throw; }
    }
    // Pure retained-byte validation only: original namespace paths are metadata, never reopened or adopted here.
    internal static Marker ParseRecoveryMarker(ReadOnlyMemory<byte> bytes, string expectedKind, string expectedCustomer,
        string expectedCell, string expectedNamespace, string? expectedCommitment)
    {
        using var document = CredentialRules.Document(bytes);
        var marker = document.RootElement.Deserialize<Marker>(CredentialRecoverySets.Json) ?? throw Failure("credential-recovery-set-metadata-invalid");
        if (marker.Schema != 1 || marker.Kind != expectedKind || marker.CustomerId != expectedCustomer || marker.CellId != expectedCell ||
            marker.NamespaceId != expectedNamespace || !CredentialRecoverySets.NamespaceShape(marker.NamespaceId) || marker.Sha256 != Digest(marker) ||
            expectedCommitment is not null && marker.Sha256 != expectedCommitment || marker.RootPath is null || marker.RootPath.Length > 4096 ||
            !Regex.IsMatch(marker.RootPath, "\\A[A-Za-z]:\\\\[^\\x00-\\x1f]+\\z", RegexOptions.CultureInvariant) || marker.OwnerSid is null ||
            !Regex.IsMatch(marker.OwnerSid, "\\AS-1-[0-9]+(?:-[0-9]+){1,15}\\z", RegexOptions.CultureInvariant))
            throw Failure("credential-recovery-set-metadata-invalid");
        return marker;
    }
    Marker CheckRoot()
    {
        CredentialRules.RejectLinks(root);
        if (!Directory.Exists(root)) throw Failure("credential-namespace-existing-required");
        ValidateSecurity(new DirectoryInfo(root).GetAccessControl(), directory: true);
        Marker marker;
        try
        {
            using var doc = CredentialRules.Document(ReadFile(Path.Combine(root, MarkerName), 16384));
            marker = doc.RootElement.Deserialize<Marker>(CredentialRules.Json) ?? throw new JsonException();
        }
        catch (Exception e) when (e is JsonException or FormatException or ArgumentException or InvalidOperationException)
        { throw Failure("credential-namespace-marker-invalid"); }
        if (marker.Schema != 1 || marker.Kind != kind || marker.CustomerId != customer || marker.CellId != cell ||
            marker.RootPath != root || marker.OwnerSid != sid || !Regex.IsMatch(marker.NamespaceId ?? "", "\\A[a-f0-9]{32}\\z") ||
            !Guid.TryParseExact(marker.NamespaceId, "N", out var instance) || instance == Guid.Empty ||
            marker.Sha256 != Digest(marker) || namespaceId is not null && marker.NamespaceId != namespaceId)
            throw Failure("credential-namespace-binding-invalid");
        return marker;
    }

    internal void ValidateFile(string path)
    {
        CredentialRules.RejectLinks(path);
        if (!File.Exists(path) || Directory.Exists(path)) throw Failure("credential-namespace-file-required");
        ValidateSecurity(new FileInfo(path).GetAccessControl(), directory: false);
    }
    internal FileStream CreateNew(string path)
    { CheckRoot(); CredentialRules.RejectLinks(path); return CreateFile(path); }
    internal byte[] ReadFile(string path, int maximum)
    {
        ValidateFile(path); using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        ValidateFile(path);
        if (stream.Length is < 1 || stream.Length > maximum) throw Failure("credential-namespace-file-size-invalid");
        var bytes = new byte[checked((int)stream.Length)]; stream.ReadExactly(bytes); return bytes;
    }
    internal async Task<byte[]> ReadFileAsync(string path, int maximum, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested(); ValidateFile(path);
        await using var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read, 4096, FileOptions.Asynchronous);
        ValidateFile(path);
        if (stream.Length is < 1 || stream.Length > maximum) throw Failure("credential-namespace-file-size-invalid");
        var bytes = new byte[checked((int)stream.Length)]; await stream.ReadExactlyAsync(bytes, cancellationToken); return bytes;
    }
    internal static string LocalPath(string value)
    {
        if (!OperatingSystem.IsWindows()) throw Failure("credential-namespace-windows-required");
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || !Regex.IsMatch(value, "\\A[A-Za-z]:\\\\") ||
            value.Any(char.IsControl) || value.Contains('/') || value[2..].Contains(':') || value[3..].Split('\\').Any(p =>
                p is "" or "." or ".." || p.EndsWith('.') || p.EndsWith(' ') || Regex.IsMatch(p, "\\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\\.|\\z)", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw Failure("credential-namespace-local-path-required");
        var path = Path.GetFullPath(value);
        if (path.Length <= 3 || new DriveInfo(Path.GetPathRoot(path)!).DriveType != DriveType.Fixed)
            throw Failure("credential-namespace-local-path-required");
        CredentialRules.RejectLinks(path); return path;
    }
    static void RejectProtectedAncestors(string path)
    {
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            foreach (var marker in new[] { ".activation.guard", "authority.json", ".installation-owner.json", ".installation.guard", ".package-owner.json",
                MarkerName, ".vault.lock", ".keys.lock", ".rollback.lock", ".backup-checkpoint.guard", "cell-binding.json", ".foundation-owner-initialization.json",
                ".credential-recovery-stage.guard", ".credential-recovery-stage.json", ".credential-recovery-checkpoint.guard", ".credential-recovery-checkpoint.json" })
                if (Path.Exists(Path.Combine(parent, marker))) throw Failure("credential-namespace-protected-ancestor");
            foreach (var pattern in new[] { "*.credential.json", "*.dpapi.json" })
                if (Directory.EnumerateFileSystemEntries(parent, pattern, SearchOption.TopDirectoryOnly).Any())
                    throw Failure("credential-namespace-protected-ancestor");
        }
    }
    static string Digest(Marker m) => Convert.ToHexStringLower(SHA256.HashData(JsonSerializer.SerializeToUtf8Bytes(new
    { m.Schema, m.Kind, m.CustomerId, m.CellId, m.RootPath, m.OwnerSid, m.NamespaceId }, CredentialRules.Json)));
    static SecurityIdentifier CurrentSid()
    {
        if (!OperatingSystem.IsWindows()) throw Failure("credential-namespace-windows-required");
        using var identity = WindowsIdentity.GetCurrent(); return identity.User ?? throw Failure("credential-namespace-owner-unavailable");
    }
    void ValidateSecurity(FileSystemSecurity security, bool directory)
    {
        if (security.GetOwner(typeof(SecurityIdentifier))?.Value != sid || !security.AreAccessRulesProtected)
            throw Failure("credential-namespace-owner-acl-invalid");
        var allowed = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
        {
            if (rule.IdentityReference.Value != sid || rule.AccessControlType != AccessControlType.Allow)
                throw Failure("credential-namespace-owner-acl-invalid");
            if ((rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl) allowed = true;
            if (directory && (rule.InheritanceFlags & (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)) !=
                (InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit)) throw Failure("credential-namespace-owner-acl-invalid");
        }
        if (!allowed) throw Failure("credential-namespace-owner-acl-invalid");
    }
    [SupportedOSPlatform("windows")]
    internal static FileStream CreateFile(string path)
    {
        var sid = CurrentSid(); var security = new FileSecurity(); security.SetOwner(sid); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, FileOptions.WriteThrough, security);
    }
    [SupportedOSPlatform("windows")]
    internal static bool CreateDirectory(string path, SecurityIdentifier sid)
    {
        var security = new DirectorySecurity(); security.SetOwner(sid); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
            PropagationFlags.None, AccessControlType.Allow));
        var descriptor = GCHandle.Alloc(security.GetSecurityDescriptorBinaryForm(), GCHandleType.Pinned);
        try
        {
            var attributes = new Attributes { Length = Marshal.SizeOf<Attributes>(), Descriptor = descriptor.AddrOfPinnedObject(), Inherit = false };
            return CreateDirectoryW(path, ref attributes);
        }
        finally { descriptor.Free(); }
    }
    [StructLayout(LayoutKind.Sequential)]
    struct Attributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [DefaultDllImportSearchPaths(DllImportSearchPath.System32)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateDirectoryW(string path, ref Attributes attributes);
    static CredentialException Failure(string code) => new(code);
}
