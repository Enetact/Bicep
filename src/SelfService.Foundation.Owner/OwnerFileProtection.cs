using System.Runtime.InteropServices;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Principal;

namespace SelfService.Foundation.Owner;

[SupportedOSPlatform("windows")]
internal static class OwnerFileProtection
{
    public static FileStream CreateResult(string path)
    {
        AssertParent(path, "OwnerResultParentCustodyRejected");
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User ?? throw new UnauthorizedAccessException();
        var security = new FileSecurity(); security.SetOwner(sid); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, AccessControlType.Allow));
        return new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.Read, 4096, FileOptions.WriteThrough, security);
    }

    public static FileStream OpenRequest(string path)
    {
        AssertParent(path, "OwnerRequestParentCustodyRejected"); AssertRequestFile(path);
        var input = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read);
        try
        {
            AssertParent(path, "OwnerRequestParentCustodyRejected"); AssertRequestFile(path);
            return input; // Retained through command completion; ordinary writers and replacement are excluded.
        }
        catch { input.Dispose(); throw; }
    }

    public static void AssertParent(string path, string code)
    {
        var parent = Path.GetDirectoryName(path) ?? throw new FoundationStoreException(code, 403);
        AssertTrusted(new DirectoryInfo(parent).GetAccessControl(), code);
    }

    static void AssertRequestFile(string path) => AssertTrusted(new FileInfo(path).GetAccessControl(), "OwnerRequestFileCustodyRejected");
    static void AssertTrusted(FileSystemSecurity security, string code)
    {
        var descriptor = new RawSecurityDescriptor(security.GetSecurityDescriptorBinaryForm(), 0);
        // NULL/absent DACL grants access; it must never be mistaken for an empty ACL that grants none.
        if (descriptor.DiscretionaryAcl is null || (descriptor.ControlFlags & ControlFlags.DiscretionaryAclPresent) == 0)
            throw new FoundationStoreException(code, 403);
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User?.Value ?? throw new FoundationStoreException(code, 403);
        var trusted = new HashSet<string>(StringComparer.Ordinal) { sid, "S-1-5-18", "S-1-5-32-544" };
        if (security.GetOwner(typeof(SecurityIdentifier)) is not { } owner || !trusted.Contains(owner.Value))
            throw new FoundationStoreException(code, 403);
        const FileSystemRights writes = FileSystemRights.Write | FileSystemRights.DeleteSubdirectoriesAndFiles |
            FileSystemRights.Delete | FileSystemRights.ChangePermissions | FileSystemRights.TakeOwnership;
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow && (rule.PropagationFlags & PropagationFlags.InheritOnly) == 0 &&
                (rule.FileSystemRights & writes) != 0 && !trusted.Contains(rule.IdentityReference.Value))
                throw new FoundationStoreException(code, 403);
    }

    public static bool CreateDirectory(string path)
    {
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User ?? throw new UnauthorizedAccessException();
        var security = new DirectorySecurity(); security.SetOwner(sid); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        var descriptor = GCHandle.Alloc(security.GetSecurityDescriptorBinaryForm(), GCHandleType.Pinned);
        try
        {
            var attributes = new SecurityAttributes { Length = Marshal.SizeOf<SecurityAttributes>(), Descriptor = descriptor.AddrOfPinnedObject(), Inherit = false };
            return CreateDirectoryW(path, ref attributes); // Native failure on ANY existing destination, including an empty directory.
        }
        finally { descriptor.Free(); }
    }

    [StructLayout(LayoutKind.Sequential)]
    struct SecurityAttributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [DllImport("kernel32.dll", CharSet = CharSet.Unicode, SetLastError = true)]
    [return: MarshalAs(UnmanagedType.Bool)]
    static extern bool CreateDirectoryW(string path, ref SecurityAttributes attributes);
}
