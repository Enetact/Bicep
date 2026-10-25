using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Diagnostics;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialNamespaceCustodyTests
{
    const string Marker = ".credential-namespace.json";

    [WindowsCredentialFact]
    public void Constructors_are_existing_only_and_fresh_provision_is_atomic_private_and_non_adopting()
    {
        using var f = new CredentialFixture(); var options = f.Options with { RootPath = Path.Combine(f.Root, "new-vault") };
        Assert.Throws<CredentialException>(() => new CredentialVault(options, f.Keys, f.Clock)); Assert.False(Directory.Exists(options.RootPath));
        var vault = CredentialVault.Provision(options, f.Keys, f.Clock); Assert.Empty(f.Keys.Requests);
        var before = Snapshot(options.RootPath); var acl = Acl(options.RootPath);
        AssertPrivate(options.RootPath, true); AssertPrivate(Path.Combine(options.RootPath, Marker), false); AssertPrivate(Path.Combine(options.RootPath, ".vault.lock"), false);
        _ = new CredentialVault(options, f.Keys, f.Clock);
        Assert.Throws<CredentialException>(() => CredentialVault.Provision(options, f.Keys, f.Clock));
        Assert.Equal(acl, Acl(options.RootPath)); AssertSnapshot(before, options.RootPath);
        var keysBase = Path.Combine(f.Root, "missing-keys-parent");
        Assert.Throws<CredentialException>(() => new CredentialWindowsUserKeyProvider(keysBase, f.Scope.CustomerId, f.Scope.CellId));
        Assert.Throws<CredentialException>(() => CredentialWindowsUserKeyProvider.ProvisionNamespace(keysBase, f.Scope.CustomerId, f.Scope.CellId));
        Assert.False(Directory.Exists(keysBase));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unrelated")]
    [InlineData("legacy")]
    public void Existing_unmarked_directories_are_preserved_including_their_original_permissions(string kind)
    {
        using var f = new CredentialFixture(); var root = Path.Combine(f.Root, "unowned"); Directory.CreateDirectory(root);
        if (kind != "empty") File.WriteAllText(Path.Combine(root, kind == "legacy" ? "cell-binding.json" : "preserve.txt"), "synthetic preserved state");
        var before = Snapshot(root); var acl = Acl(root); var options = f.Options with { RootPath = root };
        Assert.Throws<CredentialException>(() => new CredentialVault(options, f.Keys, f.Clock));
        Assert.Throws<CredentialException>(() => CredentialVault.Provision(options, f.Keys, f.Clock));
        AssertSnapshot(before, root); Assert.Equal(acl, Acl(root)); Assert.Empty(f.Keys.Requests);
    }

    [WindowsCredentialFact]
    public async Task Simultaneous_namespace_provisioners_admit_one_and_preserve_the_winning_binding()
    {
        using var f = new CredentialFixture(); var options = f.Options with { RootPath = Path.Combine(f.Root, "competition") };
        bool Attempt() { try { CredentialVault.Provision(options, f.Keys, f.Clock); return true; } catch (CredentialException) { return false; } }
        Assert.Single(await Task.WhenAll(Task.Run(Attempt), Task.Run(Attempt)), success => success);
        var before = Snapshot(options.RootPath); _ = new CredentialVault(options, f.Keys, f.Clock); AssertSnapshot(before, options.RootPath);
    }

    [WindowsCredentialFact]
    public void Provision_preserves_requested_lower_case_path_spelling_in_the_directory_and_binding()
    {
        using var f = new CredentialFixture(); var options = f.Options with { RootPath = Path.Combine(f.Root, "lower-case-vault") };
        CredentialVault.Provision(options, f.Keys, f.Clock);
        var actual = Directory.GetDirectories(f.Root).Single(p => Path.GetFileName(p) == "lower-case-vault");
        Assert.Equal(options.RootPath, actual);
        var marker = JsonNode.Parse(File.ReadAllText(Path.Combine(actual, Marker)))!;
        Assert.Equal(Path.GetFullPath(options.RootPath), marker["rootPath"]!.GetValue<string>());
        _ = new CredentialVault(options, f.Keys, f.Clock);
    }

    public static IEnumerable<object[]> ProtectedAncestors()
    {
        foreach (var marker in new[] { ".activation.guard", "authority.json", ".installation-owner.json", ".installation.guard", ".package-owner.json",
            Marker, ".vault.lock", ".keys.lock", ".rollback.lock", ".backup-checkpoint.guard", "cell-binding.json", ".foundation-owner-initialization.json",
            ".credential-recovery-stage.guard", ".credential-recovery-stage.json", ".credential-recovery-checkpoint.guard", ".credential-recovery-checkpoint.json", "legacy.credential.json", "legacy.dpapi.json" })
            foreach (var directory in new[] { false, true }) yield return [marker, directory];
    }

    [Theory]
    [MemberData(nameof(ProtectedAncestors))]
    public void Both_provisioners_refuse_protected_ancestor_files_or_directories_without_adding_or_changing_state(string marker, bool directory)
    {
        using var f = new CredentialFixture(); var parent = Path.Combine(f.Root, "protected"); Directory.CreateDirectory(parent);
        var sentinel = Path.Combine(parent, marker);
        if (directory) Directory.CreateDirectory(sentinel); else File.WriteAllText(sentinel, "synthetic preserved namespace sentinel");
        var inventory = Directory.GetFileSystemEntries(parent, "*", SearchOption.AllDirectories).Order().ToArray(); var bytes = Snapshot(parent); var acl = Acl(parent);
        var options = f.Options with { RootPath = Path.Combine(parent, "nested", "vault") };
        // An additional unmarked child proves the guard walks all ancestors, not only the immediate parent.
        Directory.CreateDirectory(Path.GetDirectoryName(options.RootPath)!);
        inventory = Directory.GetFileSystemEntries(parent, "*", SearchOption.AllDirectories).Order().ToArray();
        Assert.Equal("credential-namespace-protected-ancestor", Assert.Throws<CredentialException>(() => CredentialVault.Provision(options, f.Keys)).Code);
        Assert.Equal("credential-namespace-protected-ancestor", Assert.Throws<CredentialException>(() =>
            CredentialWindowsUserKeyProvider.ProvisionNamespace(Path.GetDirectoryName(options.RootPath)!, f.Scope.CustomerId, f.Scope.CellId)).Code);
        Assert.Equal(inventory, Directory.GetFileSystemEntries(parent, "*", SearchOption.AllDirectories).Order().ToArray());
        AssertSnapshot(bytes, parent); Assert.Equal(acl, Acl(parent)); Assert.Empty(f.Keys.Requests);
    }

    [Theory]
    [InlineData("schema")]
    [InlineData("kind")]
    [InlineData("customerId")]
    [InlineData("cellId")]
    [InlineData("rootPath")]
    [InlineData("ownerSid")]
    [InlineData("namespaceId")]
    [InlineData("emptyNamespaceId")]
    public async Task Rehashed_metadata_cannot_rebind_namespace_identity_or_replace_an_open_instances_namespace(string field)
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var path = Path.Combine(f.VaultRoot, Marker);
        var marker = JsonNode.Parse(await File.ReadAllTextAsync(path))!;
        if (field == "schema") marker[field] = 2;
        else marker[field == "emptyNamespaceId" ? "namespaceId" : field] = field switch { "kind" => "windows-user-keys", "rootPath" => f.Root.ToUpperInvariant(), "ownerSid" => "S-1-1-0",
            "emptyNamespaceId" => new string('0', 32),
            "namespaceId" => Guid.NewGuid().ToString("N"), _ => "10000000-0000-0000-0000-000000000099" };
        Rehash(marker); await File.WriteAllTextAsync(path, marker.ToJsonString()); var before = Snapshot(f.VaultRoot); f.Keys.Requests.Clear();
        await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ResolveAsync(f.Scope)); Assert.Empty(f.Keys.Requests);
        if (field != "namespaceId") Assert.Throws<CredentialException>(() => f.Reopen());
        AssertSnapshot(before, f.VaultRoot);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("marker")]
    [InlineData("guard")]
    [InlineData("profile")]
    public async Task Widened_owner_permissions_are_refused_on_reopen_and_existing_instance_reads_without_repair(string target)
    {
        using var f = new CredentialFixture(); await f.StoreToken();
        var path = target switch { "root" => f.VaultRoot, "marker" => Path.Combine(f.VaultRoot, Marker), "guard" => Path.Combine(f.VaultRoot, ".vault.lock"),
            _ => Assert.Single(Directory.GetFiles(f.VaultRoot, "*.credential.json")) };
        Widen(path); var acl = Acl(path); var before = Snapshot(f.VaultRoot); f.Keys.Requests.Clear();
        if (target != "profile") Assert.Throws<CredentialException>(() => f.Reopen());
        await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ResolveAsync(f.Scope));
        Assert.Empty(f.Keys.Requests); Assert.Equal(acl, Acl(path)); AssertSnapshot(before, f.VaultRoot);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("marker")]
    [InlineData("guard")]
    [InlineData("key")]
    public async Task Native_key_namespace_and_key_ACLs_are_validated_without_constructor_repair(string target)
    {
        using var f = new CredentialFixture(); var provider = f.ProvisionNativeKeys();
        using (await provider.ProvisionKeyAsync(f.Scope.CustomerId, f.Scope.CellId, "key-1")) { }
        var root = Assert.Single(Directory.GetDirectories(Path.Combine(f.Root, "custody")));
        var path = target switch { "root" => root, "marker" => Path.Combine(root, Marker), "guard" => Path.Combine(root, ".keys.lock"),
            _ => Assert.Single(Directory.GetFiles(root, "*.dpapi.json")) };
        Widen(path); var acl = Acl(path); var before = Snapshot(root);
        if (target != "key") Assert.Throws<CredentialException>(() => new CredentialWindowsUserKeyProvider(Path.Combine(f.Root, "custody"), f.Scope.CustomerId, f.Scope.CellId));
        await Assert.ThrowsAsync<CredentialException>(async () => { using var key = await provider.GetKeyAsync(f.Scope.CustomerId, f.Scope.CellId, "key-1", default); });
        Assert.Equal(acl, Acl(path)); AssertSnapshot(before, root);
    }

    [Theory]
    [InlineData("missing-marker")]
    [InlineData("missing-guard")]
    [InlineData("nonempty-guard")]
    [InlineData("oversized-marker")]
    [InlineData("duplicate-marker")]
    public async Task Incomplete_or_malformed_namespace_is_not_repaired_or_recreated(string mutation)
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var marker = Path.Combine(f.VaultRoot, Marker); var guard = Path.Combine(f.VaultRoot, ".vault.lock");
        switch (mutation)
        {
            case "missing-marker": File.Delete(marker); break;
            case "missing-guard": File.Delete(guard); break;
            case "nonempty-guard": File.WriteAllText(guard, "synthetic invalid guard"); break;
            case "oversized-marker": File.WriteAllText(marker, new string('x', 16385)); break;
            default: File.WriteAllText(marker, File.ReadAllText(marker).Replace("\"schema\":1", "\"schema\":1,\"Schema\":1", StringComparison.Ordinal)); break;
        }
        var before = Snapshot(f.VaultRoot); Assert.Throws<CredentialException>(() => f.Reopen());
        await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ResolveAsync(f.Scope)); AssertSnapshot(before, f.VaultRoot);
    }

    [Theory]
    [InlineData("relative")]
    [InlineData("\\\\localhost\\unadmitted\\vault")]
    [InlineData("C:\\")]
    [InlineData("C:\\unsafe.\\vault")]
    [InlineData("C:\\unsafe\\..\\vault")]
    [InlineData("C:\\unsafe\\CON\\vault")]
    public void Nonlocal_and_ambiguous_paths_are_rejected_before_key_resolution_or_provision(string path)
    {
        using var f = new CredentialFixture(); f.Keys.Requests.Clear();
        Assert.Throws<CredentialException>(() => new CredentialVault(f.Options with { RootPath = path }, f.Keys));
        Assert.Throws<CredentialException>(() => CredentialVault.Provision(f.Options with { RootPath = path }, f.Keys));
        Assert.Throws<CredentialException>(() => new CredentialWindowsUserKeyProvider(path, f.Scope.CustomerId, f.Scope.CellId));
        Assert.Throws<CredentialException>(() => CredentialWindowsUserKeyProvider.ProvisionNamespace(path, f.Scope.CustomerId, f.Scope.CellId));
        Assert.Empty(f.Keys.Requests);
    }

    [WindowsCredentialFact]
    public async Task Copied_marker_and_junction_cannot_retarget_protected_custody()
    {
        using var f = new CredentialFixture(); await f.StoreToken(); var options = f.Options with { RootPath = Path.Combine(f.Root, "copy") };
        CredentialVault.Provision(options, f.Keys, f.Clock);
        File.Copy(Path.Combine(f.VaultRoot, Marker), Path.Combine(options.RootPath, Marker), overwrite: true);
        var before = Snapshot(options.RootPath); Assert.Throws<CredentialException>(() => new CredentialVault(options, f.Keys)); AssertSnapshot(before, options.RootPath);
        var linked = Path.Combine(f.Root, "linked"); CreateJunction(linked, f.VaultRoot, f.Root);
        try { Assert.Throws<CredentialException>(() => new CredentialVault(f.Options with { RootPath = linked }, f.Keys)); }
        finally { Directory.Delete(linked); }
    }

    static Dictionary<string, byte[]> Snapshot(string path) => Directory.GetFiles(path).ToDictionary(p => Path.GetFileName(p)!, File.ReadAllBytes);
    static void AssertSnapshot(Dictionary<string, byte[]> before, string path)
    { var after = Snapshot(path); Assert.Equal(before.Keys.Order(), after.Keys.Order()); foreach (var file in before) Assert.Equal(file.Value, after[file.Key]); }
    static string Acl(string path) => (Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl())
        .GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner);
    static void Widen(string path)
    {
        var security = Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        if (Directory.Exists(path)) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security); else new FileInfo(path).SetAccessControl((FileSecurity)security);
    }
    static void AssertPrivate(string path, bool directory)
    {
        var security = directory ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected); using var identity = WindowsIdentity.GetCurrent();
        Assert.Equal(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
        var rule = Assert.Single(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>());
        Assert.Equal(identity.User, rule.IdentityReference); Assert.Equal(FileSystemRights.FullControl, rule.FileSystemRights);
    }
    static void Rehash(JsonNode node)
    {
        var bytes = JsonSerializer.SerializeToUtf8Bytes(new { schema = node["schema"]!.GetValue<int>(), kind = node["kind"]!.GetValue<string>(),
            customerId = node["customerId"]!.GetValue<string>(), cellId = node["cellId"]!.GetValue<string>(), rootPath = node["rootPath"]!.GetValue<string>(),
            ownerSid = node["ownerSid"]!.GetValue<string>(), namespaceId = node["namespaceId"]!.GetValue<string>() });
        node["sha256"] = Convert.ToHexStringLower(SHA256.HashData(bytes));
    }
    static void CreateJunction(string path, string target, string workingDirectory)
    {
        var executable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p.Trim('"'), "pwsh.exe")).First(File.Exists);
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = workingDirectory };
        start.Environment.Clear();
        foreach (var name in new[] { "SystemRoot", "WINDIR", "TEMP", "TMP" })
            if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(executable)!, "Modules");
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        var script = "$ErrorActionPreference='Stop'; New-Item -ItemType Junction -Path '" + path.Replace("'", "''") + "' -Target '" + target.Replace("'", "''") + "' | Out-Null";
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
        using var process = Process.Start(start)!;
        try
        {
            var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            Assert.True(process.WaitForExit(10000)); Assert.Equal(0, process.ExitCode); Assert.Equal("", stderr.GetAwaiter().GetResult());
            Assert.Equal("", stdout.GetAwaiter().GetResult()); Assert.True(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint));
        }
        finally { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } }
    }
}
