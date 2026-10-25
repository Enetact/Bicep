using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using System.Text.Json.Serialization;
using System.Text.RegularExpressions;

namespace SelfService.Foundation;

public sealed record FoundationCellActivationBinding(string CustomerId, string CellId, string AuthorityId,
    string DatabasePath, string DatabaseInstanceId, long Generation, DateTimeOffset ActivatedUtc, string PreviousSha256, string Sha256);

/// <summary>Single-machine cooperative activation authority. Protect this directory independently from cell databases and snapshots.</summary>
public sealed partial class FoundationCellActivationAuthority
{
    public const int MaximumGenerations = 1024;
    public const int MaximumMetadataBytes = 16384;
    readonly string root, customerId, cellId, authorityId;
    readonly TimeProvider clock;
    const string MarkerName = "authority.json", GuardName = ".activation.guard";
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web)
    {
        MaxDepth = 5, PropertyNameCaseInsensitive = false, UnmappedMemberHandling = JsonUnmappedMemberHandling.Disallow,
        RespectRequiredConstructorParameters = true, RespectNullableAnnotations = true
    };
    sealed record Marker(int Schema, string Kind, string AuthorityId, string CustomerId, string CellId, DateTimeOffset CreatedUtc, string Sha256);
    sealed record ActivationFile(int Schema, string Kind, FoundationCellActivationBinding Binding);

    public FoundationCellActivationAuthority(string authorityDirectory, string customerId, string cellId, TimeProvider? timeProvider = null)
    {
        ValidateScope(customerId, cellId); root = LocalPath(authorityDirectory); this.customerId = customerId; this.cellId = cellId;
        clock = timeProvider ?? TimeProvider.System;
        NoLinks(root);
        if (!Directory.Exists(root)) throw Failure("ActivationAuthorityMissing", 403);
        ValidateProtectedDirectory(root);
        using var guard = OpenGuard(exclusive: false);
        authorityId = ReadMarker().AuthorityId;
        _ = ReadState();
    }

    // Provisioning is an explicit trusted-owner operation, never a startup fallback or a customer/model action.
    public static FoundationCellActivationAuthority Provision(string authorityDirectory, string customerId, string cellId, TimeProvider? timeProvider = null)
    {
        ValidateScope(customerId, cellId);
        if (!OperatingSystem.IsWindows()) throw Failure("ActivationProvisionWindowsRequired", 403);
        var path = LocalPath(authorityDirectory); ProvisionNoLinks(path);
        ValidateProvisionParent(path); RejectProvisionNamespace(path);
        if (Path.Exists(path)) throw Failure("ActivationAuthorityAlreadyExists");
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User ?? throw Failure("ActivationOwnerUnavailable", 403);
        // Native mkdir supplies the private owner/DACL at CREATE_NEW; an existing empty root is never adopted or repaired.
        if (!CredentialNamespaceCustody.CreateDirectory(path, sid))
            throw Failure(Path.Exists(path) ? "ActivationAuthorityAlreadyExists" : "ActivationAuthorityReservationRejected");
        ProvisionNoLinks(path); ValidateProtectedDirectory(path);
        // Later failures retain this fresh partial authority. No parent, existing namespace or partial root is removed.
        var guardPath = Path.Combine(path, GuardName);
        using (var guard = CredentialNamespaceCustody.CreateFile(guardPath))
        {
            guard.Flush(flushToDisk: true);
            var marker = new Marker(1, "cell-activation-authority", Guid.NewGuid().ToString("N"), customerId, cellId,
                DateTimeOffset.FromUnixTimeMilliseconds((timeProvider ?? TimeProvider.System).GetUtcNow().ToUnixTimeMilliseconds()), "");
            marker = marker with { Sha256 = MarkerDigest(marker) };
            WriteNew(path, MarkerName, marker);
        }
        return new(path, customerId, cellId, timeProvider);
    }

    static readonly string[] ProvisionSentinels = [".activation.guard", "authority.json", ".installation-owner.json", ".installation.guard",
        ".package-owner.json", ".credential-namespace.json", ".vault.lock", ".keys.lock", ".rollback.lock", ".backup-checkpoint.guard", "cell-binding.json", ".foundation-owner-initialization.json",
        ".credential-recovery-stage.guard", ".credential-recovery-stage.json", ".credential-recovery-checkpoint.guard", ".credential-recovery-checkpoint.json"];
    static bool ProvisionEnvelopeName(string name) => name.EndsWith(".credential.json", StringComparison.OrdinalIgnoreCase) || name.EndsWith(".dpapi.json", StringComparison.OrdinalIgnoreCase);
    static void RejectProvisionNamespace(string path)
    {
        if (ProvisionSentinels.Contains(Path.GetFileName(path), StringComparer.OrdinalIgnoreCase) || ProvisionEnvelopeName(Path.GetFileName(path)))
            throw Failure("ActivationProtectedNamespaceDenied", 403);
        var depth = 0;
        for (var parent = Path.GetDirectoryName(path); parent is not null; parent = Path.GetDirectoryName(parent))
        {
            if (++depth > 128) throw Failure("ActivationAncestorInspectionLimit", 403);
            var entries = 0;
            try
            {
                // Inspect names, not marker/credential contents; links and directory sentinels count too.
                foreach (var entry in Directory.EnumerateFileSystemEntries(parent))
                {
                    if (++entries > 65536) throw Failure("ActivationAncestorInspectionLimit", 403);
                    var name = Path.GetFileName(entry);
                    if (ProvisionSentinels.Contains(name, StringComparer.OrdinalIgnoreCase) || ProvisionEnvelopeName(name))
                        throw Failure("ActivationProtectedNamespaceDenied", 403);
                }
            }
            catch (Exception error) when (error is IOException or UnauthorizedAccessException)
            { throw Failure("ActivationNamespaceInspectionDenied", 403); }
        }
    }
    static void ProvisionNoLinks(string value)
    {
        for (string? path = Path.GetFullPath(value); path is not null; path = Path.GetDirectoryName(path))
            try { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw Failure("ActivationLinkedPathDenied", 400); }
            catch (FileNotFoundException) { } catch (DirectoryNotFoundException) { }
    }
    [SupportedOSPlatform("windows")]
    static void ValidateProvisionParent(string path)
    {
        var parent = Path.GetDirectoryName(path);
        if (parent is null || !Directory.Exists(parent)) throw Failure("ActivationAuthorityParentMissing", 403);
        var security = new DirectoryInfo(parent).GetAccessControl(); var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        if (descriptor.DiscretionaryAcl is null || (descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0)
            throw Failure("ActivationParentCustodyDenied", 403);
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User?.Value ?? throw Failure("ActivationOwnerUnavailable", 403);
        var trusted = new HashSet<string>(StringComparer.Ordinal) { sid, "S-1-5-18", "S-1-5-32-544" };
        if (security.GetOwner(typeof(SecurityIdentifier)) is not { } owner || !trusted.Contains(owner.Value))
            throw Failure("ActivationParentCustodyDenied", 403);
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & writes) != 0 && !trusted.Contains(rule.IdentityReference.Value))
                throw Failure("ActivationParentCustodyDenied", 403);
    }

    public FoundationCellActivationBinding? ReadCurrent()
    {
        using var guard = OpenGuard(exclusive: false); CheckAuthority(); return ReadState();
    }

    public FoundationCellActivationBinding Activate(string databasePath, string databaseInstanceId, long expectedGeneration, string? expectedBindingSha256)
    {
        var database = DatabasePath(databasePath); ValidateInstance(databaseInstanceId);
        if (expectedGeneration < 0 || expectedGeneration >= MaximumGenerations ||
            expectedGeneration == 0 && expectedBindingSha256 is not null || expectedGeneration != 0 && !DigestShape(expectedBindingSha256))
            throw Failure("ActivationExpectedStateInvalid", 400);
        using var guard = OpenGuard(exclusive: true); CheckAuthority();
        var current = ReadState();
        if ((current?.Generation ?? 0) != expectedGeneration || !string.Equals(current?.Sha256, expectedBindingSha256, StringComparison.Ordinal))
            throw Failure("ActivationGenerationChanged");
        ValidateActualInstance(database, databaseInstanceId);
        if (current?.DatabasePath == database && current.DatabaseInstanceId == databaseInstanceId)
            throw Failure("ActivationBindingAlreadyCurrent");
        var binding = new FoundationCellActivationBinding(customerId, cellId, authorityId, database, databaseInstanceId,
            expectedGeneration + 1, DateTimeOffset.FromUnixTimeMilliseconds(clock.GetUtcNow().ToUnixTimeMilliseconds()), current?.Sha256 ?? "", "");
        binding = binding with { Sha256 = BindingDigest(binding) };
        WriteNew(root, RecordName(binding.Generation), new ActivationFile(1, "cell-activation", binding));
        return binding;
    }

    public FoundationCellActivationLease AcquireLease(string databasePath) => AcquireLeaseCore(databasePath, null);

    public FoundationCellActivationLease AcquireLease(string databasePath, string databaseInstanceId)
    {
        ValidateInstance(databaseInstanceId); return AcquireLeaseCore(databasePath, databaseInstanceId);
    }

    FoundationCellActivationLease AcquireLeaseCore(string databasePath, string? expectedInstance)
    {
        // Lexical local-path validation precedes File.Exists and every SQLite operation.
        var database = DatabasePath(databasePath);
        var guard = OpenGuard(exclusive: false);
        try
        {
            var actualInstance = FoundationStore.ReadCellInstanceIdentity(database, customerId, cellId).DatabaseInstanceId;
            if (expectedInstance is not null && actualInstance != expectedInstance) throw Failure("ActivationInstanceMismatch", 403);
            CheckAuthority(); var current = ReadState() ?? throw Failure("ActivationBindingMissing", 403);
            if (current.DatabasePath != database || current.DatabaseInstanceId != actualInstance) throw Failure("ActivationBindingDenied", 403);
            return new(this, guard, current);
        }
        catch { guard.Dispose(); throw; }
    }

    internal void AssertLease(FoundationCellActivationBinding lease, string databasePath, string databaseInstanceId)
    {
        var database = DatabasePath(databasePath); ValidateInstance(databaseInstanceId); CheckAuthority();
        var current = ReadState();
        if (current is null || current.Generation != lease.Generation || current.Sha256 != lease.Sha256 ||
            current.DatabasePath != database || current.DatabaseInstanceId != databaseInstanceId)
            throw Failure("ActivationLeaseStale", 403);
    }

    FileStream OpenGuard(bool exclusive)
    {
        try
        {
            NoLinks(root); ValidateProtectedDirectory(root);
            var path = Path.Combine(root, GuardName); NoLinks(path); ValidateProtectedFile(path);
            var guard = new FileStream(path, FileMode.Open, exclusive ? FileAccess.ReadWrite : FileAccess.Read,
                exclusive ? FileShare.None : FileShare.Read);
            if (guard.Length != 0) { guard.Dispose(); throw Failure("ActivationAuthorityInvalid"); }
            return guard;
        }
        catch (FileNotFoundException) { throw Failure("ActivationAuthorityMissing", 403); }
        catch (DirectoryNotFoundException) { throw Failure("ActivationAuthorityMissing", 403); }
        catch (IOException) { throw Failure("ActivationAuthorityBusy"); }
        catch (UnauthorizedAccessException) { throw Failure("ActivationAuthorityAccessDenied", 403); }
    }

    void CheckAuthority()
    {
        if (ReadMarker().AuthorityId != authorityId) throw Failure("ActivationAuthorityChanged", 403);
    }
    Marker ReadMarker()
    {
        var marker = Read<Marker>(Path.Combine(root, MarkerName));
        if (marker.Schema != 1 || marker.Kind != "cell-activation-authority" || !InstanceShape(marker.AuthorityId) ||
            marker.CustomerId != customerId || marker.CellId != cellId || !UtcMilliseconds(marker.CreatedUtc) ||
            marker.CreatedUtc > clock.GetUtcNow() || !FixedDigest(marker.Sha256, MarkerDigest(marker))) throw Failure("ActivationAuthorityInvalid");
        return marker;
    }
    FoundationCellActivationBinding? ReadState()
    {
        NoLinks(root); var records = new List<string>();
        foreach (var entry in Directory.EnumerateFileSystemEntries(root))
        {
            NoLinks(entry);
            if (!File.Exists(entry)) throw Failure("ActivationAuthorityInvalid");
            var name = Path.GetFileName(entry);
            if (name == MarkerName || name == GuardName) continue;
            if (!Regex.IsMatch(name, "\\Aactivation-[0-9]{4}\\.json\\z", RegexOptions.CultureInvariant) || records.Count >= MaximumGenerations)
                throw Failure("ActivationAuthorityInvalid");
            records.Add(entry);
        }
        FoundationCellActivationBinding? current = null;
        foreach (var path in records.Order(StringComparer.Ordinal))
        {
            var file = Read<ActivationFile>(path); var binding = file.Binding;
            if (binding is null || file.Schema != 1 || file.Kind != "cell-activation" || binding.CustomerId != customerId || binding.CellId != cellId ||
                binding.AuthorityId != authorityId || !InstanceShape(binding.DatabaseInstanceId) || binding.Generation != (current?.Generation ?? 0) + 1 ||
                binding.Generation > MaximumGenerations || Path.GetFileName(path) != RecordName(binding.Generation) ||
                binding.DatabasePath != CanonicalDatabasePath(binding.DatabasePath) || binding.PreviousSha256 != (current?.Sha256 ?? "") ||
                !UtcMilliseconds(binding.ActivatedUtc) || binding.ActivatedUtc > clock.GetUtcNow() ||
                current is not null && binding.ActivatedUtc < current.ActivatedUtc || !FixedDigest(binding.Sha256, BindingDigest(binding)))
                throw Failure("ActivationAuthorityInvalid");
            current = binding;
        }
        return current;
    }

    string DatabasePath(string path)
    {
        var database = CanonicalDatabasePath(path); NoLinks(database);
        if (!File.Exists(database) || Directory.Exists(database) || database.StartsWith(root + Path.DirectorySeparatorChar, PathComparison))
            throw Failure("ActivationDatabasePathInvalid", 400);
        return database;
    }
    void ValidateActualInstance(string database, string instance)
    {
        if (FoundationStore.ReadCellInstanceIdentity(database, customerId, cellId).DatabaseInstanceId != instance)
            throw Failure("ActivationInstanceMismatch", 403);
    }
    static string CanonicalDatabasePath(string path) => LocalPath(path);
    static string LocalPath(string value)
    {
        if (string.IsNullOrWhiteSpace(value) || value.Length > 4096 || value.Any(char.IsControl) || !Path.IsPathFullyQualified(value))
            throw Failure("ActivationPathInvalid", 400);
        if (OperatingSystem.IsWindows() && value.Split('\\', '/').Skip(1).Any(segment => segment.EndsWith('.') || segment.EndsWith(' ') || segment.Contains(':')))
            throw Failure("ActivationPathInvalid", 400);
        var path = Path.TrimEndingDirectorySeparator(Path.GetFullPath(value));
        if (path.Length <= (Path.GetPathRoot(path)?.Length ?? 0)) throw Failure("ActivationPathInvalid", 400);
        if (OperatingSystem.IsWindows())
        {
            var drive = Path.GetPathRoot(path)!;
            if (!Regex.IsMatch(drive, "\\A[A-Za-z]:\\\\\\z", RegexOptions.CultureInvariant) || new DriveInfo(drive).DriveType == DriveType.Network ||
                path[drive.Length..].Split(Path.DirectorySeparatorChar).Any(segment => segment.EndsWith('.') || segment.EndsWith(' ') || segment.Contains(':')))
                throw Failure("ActivationPathInvalid", 400);
            path = path.ToUpperInvariant();
        }
        return path;
    }

    static T Read<T>(string path)
    {
        try
        {
            NoLinks(path); ValidateProtectedFile(path);
            using var file = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            if (file.Length is < 2 or > MaximumMetadataBytes) throw Failure("ActivationAuthorityInvalid");
            using var document = JsonDocument.Parse(file, new JsonDocumentOptions { MaxDepth = 5 });
            Unique(document.RootElement);
            return document.RootElement.Deserialize<T>(Json) ?? throw Failure("ActivationAuthorityInvalid");
        }
        catch (Exception ex) when (ex is JsonException or ArgumentException or InvalidOperationException or IOException or UnauthorizedAccessException)
        { throw Failure("ActivationAuthorityInvalid"); }
    }
    static void WriteNew<T>(string directory, string name, T value)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(value, Json);
        if (bytes.Length > MaximumMetadataBytes) throw Failure("ActivationMetadataCapacityExceeded", 429);
        var temporary = Path.Combine(directory, ".write-" + Guid.NewGuid().ToString("N") + ".tmp");
        var final = Path.Combine(directory, name); NoLinks(final);
        try
        {
            using (var output = OperatingSystem.IsWindows() ? CredentialNamespaceCustody.CreateFile(temporary) :
                new FileStream(temporary, FileMode.CreateNew, FileAccess.Write, FileShare.None, 4096, FileOptions.WriteThrough))
            { output.Write(bytes); output.Flush(flushToDisk: true); }
            if (!OperatingSystem.IsWindows()) ProtectFile(temporary);
            NoLinks(directory); NoLinks(final); File.Move(temporary, final, overwrite: false);
            ValidateProtectedFile(final);
        }
        finally { if (File.Exists(temporary)) File.Delete(temporary); }
    }
    static string RecordName(long generation) => $"activation-{generation:D4}.json";
    static string MarkerDigest(Marker value) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
        { value.Schema, value.Kind, value.AuthorityId, value.CustomerId, value.CellId, created = value.CreatedUtc.ToUnixTimeMilliseconds() }, Json));
    static string BindingDigest(FoundationCellActivationBinding value) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
        { schema = 1, kind = "cell-activation", value.CustomerId, value.CellId, value.AuthorityId, value.DatabasePath, value.DatabaseInstanceId,
            value.Generation, activated = value.ActivatedUtc.ToUnixTimeMilliseconds(), value.PreviousSha256 }, Json));
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static bool DigestShape(string? value) => value is not null && Regex.IsMatch(value, "\\A[a-f0-9]{64}\\z", RegexOptions.CultureInvariant);
    static bool FixedDigest(string? first, string second) => DigestShape(first) && CryptographicOperations.FixedTimeEquals(
        System.Text.Encoding.ASCII.GetBytes(first!), System.Text.Encoding.ASCII.GetBytes(second));
    static bool InstanceShape(string? value) => value is not null && Regex.IsMatch(value, "\\A[a-f0-9]{32}\\z", RegexOptions.CultureInvariant) && value.Any(c => c != '0');
    static void ValidateInstance(string value) { if (!InstanceShape(value)) throw Failure("ActivationInstanceInvalid", 400); }
    static void ValidateScope(string customer, string cell)
    { if (!CustomerContextResolver.ValidId(customer) || !CustomerContextResolver.ValidId(cell)) throw Failure("ActivationScopeInvalid", 400); }
    static bool UtcMilliseconds(DateTimeOffset value) => value.Offset == TimeSpan.Zero && value.UtcTicks % TimeSpan.TicksPerMillisecond == 0;
    static StringComparison PathComparison => OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
    static FoundationStoreException Failure(string code, int status = 409) => new(code, status);
    static void Unique(JsonElement value)
    {
        if (value.ValueKind != JsonValueKind.Object) return;
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var property in value.EnumerateObject())
        { if (!seen.Add(property.Name)) throw Failure("ActivationAuthorityInvalid"); Unique(property.Value); }
    }
    static void NoLinks(string value)
    {
        for (string? path = Path.GetFullPath(value); path is not null; path = Path.GetDirectoryName(path))
            if ((File.Exists(path) || Directory.Exists(path)) && (File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0)
                throw Failure("ActivationLinkedPathDenied", 400);
    }

    static void ProtectDirectory(string path)
    {
        if (OperatingSystem.IsWindows()) ProtectWindowsDirectory(path);
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite | UnixFileMode.UserExecute);
        ValidateProtectedDirectory(path);
    }
    static void ProtectFile(string path)
    {
        if (OperatingSystem.IsWindows()) ProtectWindowsFile(path);
        else File.SetUnixFileMode(path, UnixFileMode.UserRead | UnixFileMode.UserWrite);
    }
    static void ValidateProtectedDirectory(string path)
    {
        if (!Directory.Exists(path)) throw Failure("ActivationAuthorityMissing", 403);
        if (OperatingSystem.IsWindows()) ValidateWindowsSecurity(new DirectoryInfo(path).GetAccessControl(), directory: true);
        else if ((File.GetUnixFileMode(path) & (UnixFileMode.GroupRead | UnixFileMode.GroupWrite | UnixFileMode.GroupExecute |
            UnixFileMode.OtherRead | UnixFileMode.OtherWrite | UnixFileMode.OtherExecute)) != 0) throw Failure("ActivationAuthorityPermissionsDenied", 403);
    }
    static void ValidateProtectedFile(string path)
    {
        if (OperatingSystem.IsWindows()) ValidateWindowsSecurity(new FileInfo(path).GetAccessControl(), directory: false);
        else if (File.Exists(path) && (File.GetUnixFileMode(path) & (UnixFileMode.GroupWrite | UnixFileMode.OtherWrite)) != 0)
            throw Failure("ActivationAuthorityPermissionsDenied", 403);
    }
    [SupportedOSPlatform("windows")]
    static void ProtectWindowsDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User ?? throw Failure("ActivationAuthorityAccessDenied", 403);
        var security = new DirectorySecurity(); security.SetOwner(sid); security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(path).SetAccessControl(security);
    }
    [SupportedOSPlatform("windows")]
    static void ProtectWindowsFile(string path)
    {
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User ?? throw Failure("ActivationAuthorityAccessDenied", 403);
        var security = new FileSecurity(); security.SetOwner(sid); security.SetAccessRuleProtection(isProtected: true, preserveInheritance: false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        new FileInfo(path).SetAccessControl(security);
    }
    [SupportedOSPlatform("windows")]
    static void ValidateWindowsSecurity(FileSystemSecurity security, bool directory)
    {
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User ?? throw Failure("ActivationAuthorityAccessDenied", 403);
        if (!sid.Equals(security.GetOwner(typeof(SecurityIdentifier))) || directory && !security.AreAccessRulesProtected)
            throw Failure("ActivationAuthorityPermissionsDenied", 403);
        var allowed = false;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(includeExplicit: true, includeInherited: true, typeof(SecurityIdentifier)))
        {
            if (rule.AccessControlType != AccessControlType.Allow) continue;
            if (!sid.Equals(rule.IdentityReference)) throw Failure("ActivationAuthorityPermissionsDenied", 403);
            if ((rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl) allowed = true;
        }
        if (!allowed) throw Failure("ActivationAuthorityPermissionsDenied", 403);
    }
}

/// <summary>Keep alive for the entire API/worker lifetime. Disposal releases the shared activation guard.</summary>
public sealed class FoundationCellActivationLease : IDisposable
{
    readonly FoundationCellActivationAuthority owner;
    readonly object gate = new();
    FileStream? guard;
    public FoundationCellActivationBinding Binding { get; }
    internal FoundationCellActivationLease(FoundationCellActivationAuthority owner, FileStream guard, FoundationCellActivationBinding binding)
    { this.owner = owner; this.guard = guard; Binding = binding; }
    public void AssertCurrent()
    {
        lock (gate)
        {
            if (guard is null) throw new FoundationStoreException("ActivationLeaseDisposed", 403);
            var identity = FoundationStore.ReadCellInstanceIdentity(Binding.DatabasePath, Binding.CustomerId, Binding.CellId);
            owner.AssertLease(Binding, Binding.DatabasePath, identity.DatabaseInstanceId);
        }
    }
    public void AssertCurrent(string actualDatabasePath, string actualDatabaseInstanceId)
    {
        lock (gate)
        {
            if (guard is null) throw new FoundationStoreException("ActivationLeaseDisposed", 403);
            owner.AssertLease(Binding, actualDatabasePath, actualDatabaseInstanceId);
        }
    }
    public void Dispose() { lock (gate) { guard?.Dispose(); guard = null; } }
}
