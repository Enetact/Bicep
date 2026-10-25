using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text.Json;
using System.Text.RegularExpressions;

namespace SelfService.Foundation.Worker;

// Trusted local Windows receipt ownership; not a service identity or provider authorization.
internal sealed class WorkerReceiptFile : IDisposable
{
    const int MaximumBytes = 32768;
    static readonly string[] Markers = ["authority.json", ".activation.guard", ".installation-owner.json", ".installation.guard",
        ".package-owner.json", ".credential-namespace.json", "cell-binding.json", ".keys.lock", ".vault.lock", ".rollback.lock", ".backup-checkpoint.guard", ".foundation-owner-initialization.json",
        ".credential-recovery-stage.guard", ".credential-recovery-stage.json", ".credential-recovery-checkpoint.guard", ".credential-recovery-checkpoint.json"];
    readonly string path, database, configuration;
    readonly string? authority;
    readonly FileStream reservation;

    WorkerReceiptFile(string path, string database, string configuration, string? authority, FileStream reservation)
    { this.path = path; this.database = database; this.configuration = configuration; this.authority = authority; this.reservation = reservation; }

    public static WorkerReceiptFile? Reserve(string? requestedPath, string directory, string database, string configuration, string? authority)
    {
        if (requestedPath is null) return null;
        if (!OperatingSystem.IsWindows()) throw Rejected("WorkerReceiptWindowsRequired");
        var path = Validate(requestedPath, directory, database, configuration, authority);
        var reservationPath = path + ".reserve";
        // A cooperating second worker must lose this CreateNew race before it can open/register the cell.
        var guard = CreatePrivate(reservationPath, FileOptions.DeleteOnClose | FileOptions.WriteThrough);
        try
        {
            ValidateDestination(path, database, configuration, authority);
            return new(path, database, configuration, authority, guard);
        }
        catch { guard.Dispose(); throw; }
    }

    public static string Validate(string requestedPath, string directory, string database, string configuration, string? authority)
    {
        if (!OperatingSystem.IsWindows()) throw Rejected("WorkerReceiptWindowsRequired");
        var path = CanonicalPath(requestedPath, directory);
        var name = Path.GetFileName(path);
        if (!name.EndsWith(".json", StringComparison.OrdinalIgnoreCase) || Markers.Contains(name, StringComparer.OrdinalIgnoreCase) ||
            name.EndsWith(".credential.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dpapi.json", StringComparison.OrdinalIgnoreCase))
            throw Rejected("WorkerReceiptNameRejected");
        if (!Directory.Exists(Path.GetDirectoryName(path))) throw Rejected("WorkerReceiptParentMissing");
        var reservationPath = path + ".reserve";
        ValidateDestination(path, database, configuration, authority);
        ValidateDestination(reservationPath, database, configuration, authority);
        return path;
    }

    public void Publish(WorkerProcessReceipt receipt, JsonSerializerOptions json)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(receipt, json);
        if (bytes.Length > MaximumBytes) throw Rejected("WorkerReceiptByteLimit");
        ValidateDestination(path, database, configuration, authority);
        var temporary = path + ".write-" + Guid.NewGuid().ToString("N");
        ValidateDestination(temporary, database, configuration, authority);
        if (!OperatingSystem.IsWindows()) throw Rejected("WorkerReceiptWindowsRequired");
        try
        {
            using (var stream = CreatePrivate(temporary, FileOptions.WriteThrough))
            { stream.Write(bytes); stream.Flush(flushToDisk: true); }
            ValidateDestination(path, database, configuration, authority); RejectLinks(temporary); AssertParentCustody(temporary);
            // The final file first appears complete, keeps the protected ACL, and never replaces another file.
            File.Move(temporary, path, overwrite: false); temporary = "";
        }
        finally
        {
            if (temporary.Length > 0 && File.Exists(temporary))
            { RejectLinks(temporary); AssertParentCustody(temporary); File.Delete(temporary); }
        }
    }

    public void Dispose() => reservation.Dispose(); // DeleteOnClose removes only the exact file this instance created.

    static string CanonicalPath(string value, string directory)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl) || value.Contains('/')) throw Rejected("WorkerReceiptPathRejected");
        var absolute = Regex.IsMatch(value, "\\A[A-Za-z]:\\\\");
        if (!absolute && Path.IsPathRooted(value) || !absolute && value.Contains(':')) throw Rejected("WorkerReceiptPathRejected");
        var tail = absolute ? value[3..] : value;
        if (tail.Split('\\').Any(segment => segment.Length == 0 || segment is "." or ".." || segment.EndsWith('.') || segment.EndsWith(' ') ||
            segment.Contains(':') || Regex.IsMatch(segment.Split('.')[0], "\\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])\\z", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant)))
            throw Rejected("WorkerReceiptPathRejected");
        var path = Path.GetFullPath(value, directory); var root = Path.GetPathRoot(path);
        if (root is null || !Regex.IsMatch(root, "\\A[A-Za-z]:\\\\\\z") || new DriveInfo(root).DriveType != DriveType.Fixed)
            throw Rejected("WorkerReceiptPathRejected");
        RejectLinks(path); return path;
    }
    static void ValidateDestination(string candidate, string database, string configuration, string? authority)
    {
        RejectLinks(candidate); AssertParentCustody(candidate);
        if (new[] { "", "-wal", "-shm", "-journal" }.Any(suffix => candidate.Equals(database + suffix, StringComparison.OrdinalIgnoreCase)) ||
            candidate.Equals(configuration, StringComparison.OrdinalIgnoreCase) || authority is not null &&
                (candidate.Equals(authority, StringComparison.OrdinalIgnoreCase) || candidate.StartsWith(authority + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
            throw Rejected("WorkerReceiptProtectedNamespace");
        for (var parent = Path.GetDirectoryName(candidate); parent is not null; parent = Path.GetDirectoryName(parent))
            if (Markers.Any(marker => Path.Exists(Path.Combine(parent, marker))) ||
                Directory.EnumerateFileSystemEntries(parent, "*.credential.json", SearchOption.TopDirectoryOnly).Any() ||
                Directory.EnumerateFileSystemEntries(parent, "*.dpapi.json", SearchOption.TopDirectoryOnly).Any())
                throw Rejected("WorkerReceiptProtectedNamespace");
        if (File.Exists(candidate) || Directory.Exists(candidate)) throw Rejected("WorkerReceiptAlreadyExists");
    }
    static void AssertParentCustody(string path)
    {
        if (!OperatingSystem.IsWindows()) throw Rejected("WorkerReceiptWindowsRequired");
        var parent = Path.GetDirectoryName(path) ?? throw Rejected("WorkerReceiptParentMissing");
        using var identity = WindowsIdentity.GetCurrent();
        var sid = identity.User?.Value ?? throw Rejected("WorkerReceiptOwnerUnavailable");
        var trusted = new HashSet<string>(StringComparer.Ordinal) { sid, "S-1-5-18", "S-1-5-32-544" };
        var acl = new DirectoryInfo(parent).GetAccessControl();
        var descriptor = new RawSecurityDescriptor(acl.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null || (descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0)
            throw Rejected("WorkerReceiptParentCustodyRejected");
        if (acl.GetOwner(typeof(SecurityIdentifier)) is not { } owner || !trusted.Contains(owner.Value))
            throw Rejected("WorkerReceiptParentCustodyRejected");
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & writes) != 0 && !trusted.Contains(rule.IdentityReference.Value))
                throw Rejected("WorkerReceiptParentCustodyRejected");
    }
    static void RejectLinks(string path)
    {
        for (var current = path; current is not null; current = Path.GetDirectoryName(current))
            try { if (File.GetAttributes(current).HasFlag(FileAttributes.ReparsePoint)) throw Rejected("WorkerReceiptLinkedPathRejected"); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
    }
    [SupportedOSPlatform("windows")]
    static FileStream CreatePrivate(string path, FileOptions options)
    {
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User ?? throw new UnauthorizedAccessException();
        var acl = new FileSecurity(); acl.SetOwner(sid); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.None, 4096, options, acl);
    }
    static FoundationStoreException Rejected(string code) => new(code, 400);
}
