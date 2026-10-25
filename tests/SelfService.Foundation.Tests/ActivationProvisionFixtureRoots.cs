using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Principal;
using SelfService.Foundation;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
internal static class ActivationProvisionFixtureRoots
{
    // Test-only reservation of a fresh GUID-owned TEMP root. Never changes an existing directory's ACL.
    internal static void Create(string value)
    {
        var path = Path.GetFullPath(value); var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
        var leaf = Path.GetFileName(path);
        if (!path.StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || leaf.Length < 32 ||
            !Guid.TryParseExact(leaf[^32..], "N", out _) || Path.Exists(path)) throw new IOException("Synthetic private root boundary rejected.");
        for (var ancestor = Path.GetDirectoryName(path); ancestor is not null; ancestor = Path.GetDirectoryName(ancestor))
            if (Path.Exists(ancestor) && (File.GetAttributes(ancestor) & FileAttributes.ReparsePoint) != 0) throw new IOException("Synthetic linked parent refused.");
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        using var identity = WindowsIdentity.GetCurrent();
        var primitive = typeof(FoundationCellActivationAuthority).Assembly.GetType("SelfService.Foundation.CredentialNamespaceCustody", true)!
            .GetMethod("CreateDirectory", BindingFlags.Static | BindingFlags.NonPublic)!;
        if (primitive.Invoke(null, [path, identity.User!]) is not true) throw new IOException("Synthetic private root reservation failed.");
    }
}
