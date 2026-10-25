using System.Runtime.Versioning;
using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationActivationProvisionCustodyTests
{
    [WindowsCredentialFact]
    public async Task Native_competing_provisioners_admit_one_private_complete_authority_without_adoption()
    {
        using var f = new ProvisionFixture(); var parentAcl = SecurityBytes(f.Root);
        for (var iteration = 0; iteration < 3; iteration++)
        {
            var path = f.Child("authority-race-" + iteration); var go = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
            var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
            {
                await go.Task;
                try { return (Authority: FoundationCellActivationAuthority.Provision(path, f.Customer, f.Cell), Error: (string?)null); }
                catch (FoundationStoreException e) { return (Authority: (FoundationCellActivationAuthority?)null, Error: e.Code); }
            })).ToArray();
            go.TrySetResult(); var results = await Task.WhenAll(tasks); var winner = Assert.Single(results, r => r.Authority is not null);
            Assert.All(results.Where(r => r.Authority is null), r => Assert.Equal("ActivationAuthorityAlreadyExists", r.Error));
            Assert.Null(winner.Authority!.ReadCurrent()); AssertPrivate(path, directory: true);
            Assert.Equal([".activation.guard", "authority.json"], Directory.GetFiles(path).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal).ToArray());
            foreach (var file in Directory.GetFiles(path)) AssertPrivate(file, directory: false);
            var bytes = f.Snapshot(path); Assert.Equal("ActivationAuthorityAlreadyExists", Error(() => FoundationCellActivationAuthority.Provision(path, f.Customer, f.Cell)));
            Assert.Equal(bytes, f.Snapshot(path)); Assert.Null(new FoundationCellActivationAuthority(path, f.Customer, f.Cell).ReadCurrent());
        }
        Assert.Equal(parentAcl, SecurityBytes(f.Root));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unrelated")]
    [InlineData("guard-only")]
    [InlineData("marker-only")]
    [InlineData("interrupted-write")]
    public void Existing_empty_or_partial_directories_are_preserved_without_acl_repair_or_provisioning_retry(string state)
    {
        using var f = new ProvisionFixture(); var root = f.Child("existing-authority"); Directory.CreateDirectory(root);
        var name = state switch { "unrelated" => "unrelated.txt", "guard-only" => ".activation.guard", "marker-only" => "authority.json", _ => ".write-" + Guid.NewGuid().ToString("N") + ".tmp" };
        if (state != "empty") File.WriteAllText(Path.Combine(root, name), state == "guard-only" ? "" : "preserved synthetic partial state");
        AddAccess(root, FileSystemRights.ReadAndExecute); var before = f.Snapshot(root); var acl = SecurityBytes(root);
        Assert.Equal("ActivationAuthorityAlreadyExists", Error(() => FoundationCellActivationAuthority.Provision(root, f.Customer, f.Cell)));
        Assert.Equal(before, f.Snapshot(root)); Assert.Equal(acl, SecurityBytes(root));
    }

    public static IEnumerable<object[]> Sentinels()
    {
        foreach (var name in new[] { ".activation.guard", "authority.json", ".installation-owner.json", ".installation.guard", ".package-owner.json",
            ".credential-namespace.json", ".vault.lock", ".keys.lock", ".rollback.lock", ".backup-checkpoint.guard", "cell-binding.json", ".foundation-owner-initialization.json",
            ".credential-recovery-stage.guard", ".credential-recovery-stage.json", ".credential-recovery-checkpoint.guard", ".credential-recovery-checkpoint.json",
            "synthetic.credential.json", "synthetic.dpapi.json" })
            foreach (var directory in new[] { false, true }) yield return [name, directory];
    }
    [Theory]
    [MemberData(nameof(Sentinels))]
    public void Complete_or_partial_operational_namespace_ancestors_are_refused_without_changing_bytes_or_acls(string sentinel, bool directory)
    {
        using var f = new ProvisionFixture(); var root = f.Child("protected-namespace"); Directory.CreateDirectory(root);
        var parent = Path.Combine(root, "existing-deeper-parent"); Directory.CreateDirectory(parent); var marker = Path.Combine(root, sentinel);
        if (directory) { Directory.CreateDirectory(marker); File.WriteAllText(Path.Combine(marker, "preserved.txt"), "synthetic marker directory"); }
        else File.WriteAllText(marker, "synthetic marker bytes");
        var before = f.Snapshot(root); var parentAcl = SecurityBytes(parent); var target = Path.Combine(parent, "new-authority");
        Assert.Equal("ActivationProtectedNamespaceDenied", Error(() => FoundationCellActivationAuthority.Provision(target, f.Customer, f.Cell)));
        Assert.False(Path.Exists(target)); Assert.Equal(before, f.Snapshot(root)); Assert.Equal(parentAcl, SecurityBytes(parent));
    }

    [Theory]
    [InlineData(".credential-namespace.json")]
    [InlineData(".rollback.lock")]
    [InlineData("authority.json")]
    [InlineData(".foundation-owner-initialization.json")]
    [InlineData("synthetic.CREDENTIAL.JSON")]
    [InlineData("synthetic.dpapi.json")]
    public void Fresh_authority_cannot_claim_a_protected_operational_sentinel_name(string name)
    {
        using var f = new ProvisionFixture(); var before = f.Snapshot(f.Root);
        Assert.Equal("ActivationProtectedNamespaceDenied", Error(() => FoundationCellActivationAuthority.Provision(f.Child(name), f.Customer, f.Cell)));
        Assert.Equal(before, f.Snapshot(f.Root));
    }

    [Theory]
    [InlineData(FileSystemRights.WriteData)]
    [InlineData(FileSystemRights.AppendData)]
    [InlineData(FileSystemRights.DeleteSubdirectoriesAndFiles)]
    [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.ChangePermissions)]
    [InlineData(FileSystemRights.TakeOwnership)]
    [InlineData(FileSystemRights.WriteAttributes)]
    [InlineData(FileSystemRights.WriteExtendedAttributes)]
    public void Foreign_effective_parent_write_and_delete_rights_are_refused_without_acl_repair(FileSystemRights rights)
    {
        using var f = new ProvisionFixture(); var parent = f.Child("unsafe-parent"); Directory.CreateDirectory(parent);
        File.WriteAllText(Path.Combine(parent, "preserved.txt"), "synthetic preserved parent"); AddAccess(parent, rights);
        var before = f.Snapshot(parent); var acl = SecurityBytes(parent); var target = Path.Combine(parent, "authority");
        Assert.Equal("ActivationParentCustodyDenied", Error(() => FoundationCellActivationAuthority.Provision(target, f.Customer, f.Cell)));
        Assert.False(Path.Exists(target)); Assert.Equal(before, f.Snapshot(parent)); Assert.Equal(acl, SecurityBytes(parent));
    }

    [Theory]
    [InlineData("read-only")]
    [InlineData("inherit-only")]
    [InlineData("deny-only")]
    public void Safe_parent_rules_do_not_broaden_the_private_at_birth_authority_or_files(string kind)
    {
        using var f = new ProvisionFixture(); var parent = f.Child("safe-parent"); Directory.CreateDirectory(parent);
        var acl = new DirectoryInfo(parent).GetAccessControl(); var world = kind == "deny-only"
            ? new SecurityIdentifier("S-1-5-21-1-2-3-999999") : new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        acl.AddAccessRule(new FileSystemAccessRule(world, kind == "read-only" ? FileSystemRights.ReadAndExecute : FileSystemRights.Write,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, kind == "inherit-only" ? PropagationFlags.InheritOnly : PropagationFlags.None,
            kind == "deny-only" ? AccessControlType.Deny : AccessControlType.Allow));
        new DirectoryInfo(parent).SetAccessControl(acl); var before = SecurityBytes(parent); var target = Path.Combine(parent, "authority");
        var authority = FoundationCellActivationAuthority.Provision(target, f.Customer, f.Cell); Assert.Null(authority.ReadCurrent());
        Assert.Equal(before, SecurityBytes(parent)); AssertPrivate(target, directory: true);
        foreach (var file in Directory.GetFiles(target)) AssertPrivate(file, directory: false);
    }

    [WindowsCredentialFact]
    public void Native_null_parent_dacl_is_refused_without_creating_state_and_the_synthetic_acl_is_restored()
    {
        using var f = new ProvisionFixture(); var parent = f.Child("null-dacl-parent"); Directory.CreateDirectory(parent);
        var original = SecurityBytes(parent); var target = Path.Combine(parent, "authority");
        try
        {
            var raw = new RawSecurityDescriptor(original, 0); raw.DiscretionaryAcl = null; var bytes = new byte[raw.BinaryLength]; raw.GetBinaryForm(bytes, 0);
            SetAccessBytes(parent, bytes); Assert.Null(new RawSecurityDescriptor(SecurityBytes(parent), 0).DiscretionaryAcl);
            Assert.Equal("ActivationParentCustodyDenied", Error(() => FoundationCellActivationAuthority.Provision(target, f.Customer, f.Cell)));
            Assert.False(Path.Exists(target)); Assert.Empty(Directory.GetFileSystemEntries(parent)); Assert.Null(new RawSecurityDescriptor(SecurityBytes(parent), 0).DiscretionaryAcl);
        }
        finally { SetAccessBytes(parent, original); }
        Assert.Equal(AccessState(original), AccessState(SecurityBytes(parent)));
    }

    [WindowsCredentialFact]
    public void Missing_parent_is_not_created_and_existing_file_is_not_replaced()
    {
        using var f = new ProvisionFixture(); var missing = f.Child("missing"); var before = f.Snapshot(f.Root);
        Assert.Equal("ActivationAuthorityParentMissing", Error(() => FoundationCellActivationAuthority.Provision(Path.Combine(missing, "authority"), f.Customer, f.Cell)));
        Assert.False(Path.Exists(missing)); Assert.Equal(before, f.Snapshot(f.Root));
        var file = f.Child("existing-authority-file"); File.WriteAllText(file, "preserve original file"); var bytes = File.ReadAllBytes(file); var acl = SecurityBytes(file);
        Assert.Equal("ActivationAuthorityAlreadyExists", Error(() => FoundationCellActivationAuthority.Provision(file, f.Customer, f.Cell)));
        Assert.Equal(bytes, File.ReadAllBytes(file)); Assert.Equal(acl, SecurityBytes(file));
    }

    [WindowsCredentialFact]
    public void Protected_namespace_detection_does_not_open_even_an_exclusively_held_marker()
    {
        using var f = new ProvisionFixture(); var parent = f.Child("held-namespace"); Directory.CreateDirectory(parent);
        var marker = Path.Combine(parent, ".rollback.lock"); File.WriteAllText(marker, "synthetic unreadable sentinel"); var before = f.Snapshot(parent);
        using (var held = new FileStream(marker, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Assert.Equal("ActivationProtectedNamespaceDenied", Error(() => FoundationCellActivationAuthority.Provision(Path.Combine(parent, "authority"), f.Customer, f.Cell)));
        Assert.Equal(before, f.Snapshot(parent));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Direct_or_ancestor_junctions_including_dangling_ones_cannot_create_authority_state(bool direct, bool dangling)
    {
        using var f = new ProvisionFixture(); var target = f.Child("junction-target"); Directory.CreateDirectory(target);
        if (!dangling) File.WriteAllText(Path.Combine(target, "preserved.txt"), "synthetic original target");
        var link = f.Child("junction-link"); await CreateJunction(link, target, f.Root);
        try
        {
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            if (dangling) Directory.Delete(target, false);
            var before = dangling ? Array.Empty<string>() : f.Snapshot(target); var path = direct ? link : Path.Combine(link, "new-authority");
            Assert.Equal("ActivationLinkedPathDenied", Error(() => FoundationCellActivationAuthority.Provision(path, f.Customer, f.Cell)));
            if (dangling) Assert.False(Directory.Exists(target)); else Assert.Equal(before, f.Snapshot(target));
        }
        finally { Directory.Delete(link, false); }
    }

    static async Task CreateJunction(string link, string target, string root)
    {
        var executable = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32", "cmd.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear(); foreach (var name in new[] { "SystemRoot", "WINDIR" }) if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(arg);
        using var process = Process.Start(start)!; _ = process.SafeHandle; var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(deadline.Token); Assert.Equal(0, process.ExitCode); _ = await output; _ = await errors; }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }

    [WindowsCredentialFact]
    public void Existing_activation_and_lease_history_contract_keeps_private_new_records_and_exact_cas()
    {
        using var f = new ActivationFixture(); var authority = f.Provision(); var first = authority.Activate(f.Cell.Path, f.Instance, 0, null);
        using (var lease = authority.AcquireLease(f.Cell.Path, f.Instance))
        { lease.AssertCurrent(); Assert.Equal("ActivationAuthorityBusy", Error(() => authority.Activate(f.Cell.Path, f.Instance, first.Generation, first.Sha256))); }
        Assert.Equal("ActivationBindingAlreadyCurrent", Error(() => authority.Activate(f.Cell.Path, f.Instance, first.Generation, first.Sha256)));
        var recovered = f.Recover(); var second = authority.Activate(recovered.Path, recovered.Id, first.Generation, first.Sha256);
        Assert.Equal(2, second.Generation); Assert.Equal(first.Sha256, second.PreviousSha256);
        Assert.Equal(second, new FoundationCellActivationAuthority(f.AuthorityPath, f.Cell.Customer, f.Cell.Cell, f.Cell.Clock).ReadCurrent());
        foreach (var file in Directory.GetFiles(f.AuthorityPath)) AssertPrivate(file, directory: false);
    }

    static string Error(Action action) => Assert.Throws<FoundationStoreException>(action).Code;
    static void AddAccess(string path, FileSystemRights rights)
    { var acl = new DirectoryInfo(path).GetAccessControl(); acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), rights, AccessControlType.Allow)); new DirectoryInfo(path).SetAccessControl(acl); }
    static byte[] SecurityBytes(string path) => (Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorBinaryForm();
    static void SetAccessBytes(string path, byte[] bytes)
    { var acl = new DirectorySecurity(); acl.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access); new DirectoryInfo(path).SetAccessControl(acl); }
    static (string?, string?, bool, string) AccessState(byte[] bytes)
    { var raw = new RawSecurityDescriptor(bytes, 0); var acl = new byte[raw.DiscretionaryAcl!.BinaryLength]; raw.DiscretionaryAcl.GetBinaryForm(acl, 0); return (raw.Owner?.Value, raw.Group?.Value, (raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, Convert.ToHexString(acl)); }
    static void AssertPrivate(string path, bool directory)
    {
        using var identity = WindowsIdentity.GetCurrent(); var acl = directory ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected); Assert.Equal(identity.User, acl.GetOwner(typeof(SecurityIdentifier)));
        Assert.All(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule =>
        { Assert.Equal(identity.User!.Value, rule.IdentityReference.Value); Assert.Equal(AccessControlType.Allow, rule.AccessControlType); Assert.True((rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl); });
    }

    sealed class ProvisionFixture : IDisposable
    {
        readonly string parent = Path.Combine(Path.GetTempPath(), "PlatformStudio-ActivationProvisionTests");
        public string Root { get; }
        public string Customer => "synthetic-customer";
        public string Cell => "synthetic-cell";
        public ProvisionFixture()
        {
            Directory.CreateDirectory(parent); Root = Path.Combine(parent, Guid.NewGuid().ToString("N"));
            ActivationProvisionFixtureRoots.Create(Root);
        }
        public string Child(string name) => Path.Combine(Root, name);
        public string[] Snapshot(string path) => new[] { path }.Concat(Directory.EnumerateFileSystemEntries(path, "*", SearchOption.AllDirectories)).Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(path, p) + ":" + Convert.ToHexStringLower(SHA256.HashData(SecurityBytes(p))) + ":" + (Directory.Exists(p) ? "directory" : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p))))).ToArray();
        public void Dispose()
        {
            if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                !Guid.TryParseExact(Path.GetFileName(Root), "N", out _)) throw new InvalidOperationException("Synthetic cleanup boundary rejected.");
            Delete(Root);
        }
        static void Delete(string path)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            { if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new InvalidOperationException("Linked cleanup refused."); if (Directory.Exists(entry)) Delete(entry); else File.Delete(entry); }
            Directory.Delete(path, false);
        }
    }
}
