using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Runtime.Versioning;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationWorkerReceiptTests
{
    [Theory]
    [InlineData("authority.json")]
    [InlineData(".activation.guard")]
    [InlineData(".installation-owner.json")]
    [InlineData(".installation.guard")]
    [InlineData(".package-owner.json")]
    [InlineData(".credential-namespace.json")]
    [InlineData("cell-binding.json")]
    [InlineData(".keys.lock")]
    [InlineData(".vault.lock")]
    [InlineData(".rollback.lock")]
    [InlineData(".backup-checkpoint.guard")]
    [InlineData(".credential-recovery-stage.guard")]
    [InlineData(".credential-recovery-stage.json")]
    [InlineData(".credential-recovery-checkpoint.guard")]
    [InlineData(".credential-recovery-checkpoint.json")]
    [InlineData(".foundation-owner-initialization.json")]
    [InlineData("synthetic.credential.json")]
    [InlineData("synthetic.dpapi.json")]
    public async Task Protected_namespace_refusal_preserves_exact_bytes_and_precedes_all_cell_access(string marker)
    {
        using var f = new ReceiptCell(); var protectedRoot = Path.Combine(f.Root, "protected"); var nested = Path.Combine(protectedRoot, "nested");
        Directory.CreateDirectory(nested); File.WriteAllText(Path.Combine(protectedRoot, marker), "synthetic owner metadata");
        File.WriteAllBytes(Path.Combine(nested, "content.bin"), [1, 2, 3, 4]);
        var contents = Contents(protectedRoot); var database = f.DatabaseBytes(); var audit = f.AuditCount();
        var receipt = Path.Combine(nested, "receipt.json");
        using (var preflight = f.Start(receipt, validateOnly: true))
        { Assert.Equal(2, await preflight.Exit()); Assert.Contains("WorkerReceiptProtectedNamespace", preflight.Stderr); }
        using var worker = f.Start(receipt);
        Assert.Equal(2, await worker.Exit()); Assert.Contains("WorkerReceiptProtectedNamespace", worker.Stderr);
        Assert.Equal(contents, Contents(protectedRoot)); Assert.Equal(database, f.DatabaseBytes()); Assert.Equal(audit, f.AuditCount());
        Assert.Equal(0, f.Registrations()); Assert.False(File.Exists(receipt)); Assert.False(File.Exists(receipt + ".reserve"));
    }

    [Theory]
    [InlineData("authority.json")]
    [InlineData(".installation-owner.json")]
    [InlineData(".package-owner.json")]
    [InlineData(".credential-namespace.json")]
    [InlineData("cell-binding.json")]
    [InlineData("synthetic.credential.json")]
    [InlineData("synthetic.dpapi.json")]
    public async Task Fresh_reserved_metadata_names_cannot_be_claimed_as_worker_receipts(string name)
    {
        using var f = new ReceiptCell(); var before = f.DatabaseBytes(); var path = Path.Combine(f.Root, name);
        using var worker = f.Start(path); Assert.Equal(2, await worker.Exit()); Assert.Contains("WorkerReceiptNameRejected", worker.Stderr);
        Assert.False(File.Exists(path)); Assert.Equal(before, f.DatabaseBytes()); Assert.Equal(0, f.Registrations());
    }

    [Theory]
    [InlineData("")]
    [InlineData("-wal")]
    [InlineData("-shm")]
    [InlineData("-journal")]
    public async Task Database_and_auxiliary_aliases_are_refused_without_cell_mutation(string suffix)
    {
        using var f = new ReceiptCell("cell.json"); var before = f.DatabaseBytes(); var path = f.Database + suffix;
        using var worker = f.Start(path); Assert.Equal(2, await worker.Exit()); Assert.Equal(before, f.DatabaseBytes()); Assert.Equal(0, f.Registrations());
        if (suffix.Length > 0) Assert.False(File.Exists(path));
    }

    [Fact]
    public async Task Reservation_alias_to_configured_database_is_refused_before_opening_it()
    {
        using var f = new ReceiptCell("safe.json.reserve"); var before = f.DatabaseBytes();
        using var worker = f.Start(Path.Combine(f.Root, "safe.json"));
        Assert.Equal(2, await worker.Exit()); Assert.Contains("WorkerReceiptProtectedNamespace", worker.Stderr);
        Assert.Equal(before, f.DatabaseBytes()); Assert.Equal(0, f.Registrations());
    }

    [Theory]
    [InlineData("..\\escape.json")]
    [InlineData(".\\receipt.json")]
    [InlineData("C:relative.json")]
    [InlineData("\\rooted.json")]
    [InlineData("\\\\localhost\\C$\\receipt.json")]
    [InlineData("safe.json.")]
    [InlineData("safe.json ")]
    [InlineData("safe.json:stream")]
    [InlineData("CON.json")]
    [InlineData("nested/file.json")]
    public async Task Noncanonical_and_network_paths_are_rejected_before_cell_access(string receipt)
    {
        using var f = new ReceiptCell(); var before = f.DatabaseBytes();
        using var worker = f.Start(receipt); Assert.Equal(2, await worker.Exit()); Assert.Equal(before, f.DatabaseBytes()); Assert.Equal(0, f.Registrations());
        Assert.DoesNotContain(f.Root, worker.Stderr);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_receipt_or_reservation_bytes_and_ACLs_are_never_overwritten_or_repaired(bool reservation)
    {
        using var f = new ReceiptCell(); var path = Path.Combine(f.Root, "occupied.json"); var existing = reservation ? path + ".reserve" : path;
        File.WriteAllText(existing, "synthetic existing owned data"); var before = File.ReadAllBytes(existing); var database = f.DatabaseBytes();
        var acl = OperatingSystem.IsWindows() ? new FileInfo(existing).GetAccessControl().GetSecurityDescriptorBinaryForm() : [];
        using var worker = f.Start(path); Assert.Equal(2, await worker.Exit()); Assert.Equal(before, File.ReadAllBytes(existing));
        if (OperatingSystem.IsWindows()) Assert.Equal(acl, new FileInfo(existing).GetAccessControl().GetSecurityDescriptorBinaryForm());
        Assert.Equal(database, f.DatabaseBytes()); Assert.Equal(0, f.Registrations());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_or_ancestor_junctions_are_rejected_without_writing_the_target(bool direct)
    {
        using var f = new ReceiptCell(); var target = Path.Combine(f.Root, "junction-target"); Directory.CreateDirectory(target);
        File.WriteAllText(Path.Combine(target, "preserved.txt"), "synthetic target data"); var link = Path.Combine(f.Root, direct ? "link.json" : "linked-parent");
        await f.Junction(link, target); var receipt = direct ? link : Path.Combine(link, "receipt.json"); var before = f.DatabaseBytes(); var contents = Contents(target);
        using var worker = f.Start(receipt); Assert.Equal(2, await worker.Exit()); Assert.Contains("WorkerReceiptLinkedPathRejected", worker.Stderr);
        Assert.Equal(contents, Contents(target)); Assert.Equal(before, f.DatabaseBytes()); Assert.Equal(0, f.Registrations());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Safe_absolute_or_relative_JSON_receipts_are_complete_private_and_keep_disabled_work_queued(bool relative)
    {
        using var f = new ReceiptCell(); f.Queue(); var receipt = Path.Combine(f.Root, "ordinary.json");
        using var worker = f.Start(relative ? "ordinary.json" : receipt); await worker.Ready(receipt);
        AssertPrivate(receipt); AssertPrivate(receipt + ".reserve");
        using (var doc = JsonDocument.Parse(File.ReadAllBytes(receipt)))
        { Assert.Equal(worker.Id, doc.RootElement.GetProperty("processId").GetInt32()); Assert.Equal("Disabled", doc.RootElement.GetProperty("mode").GetString()); }
        Assert.Equal(0, await worker.Exit()); Assert.False(File.Exists(receipt + ".reserve"));
        Assert.Empty(Directory.GetFiles(f.Root, "*.write-*")); Assert.Equal(1, f.Registrations()); Assert.Equal(1, f.Scalar("SELECT count(*) FROM jobs WHERE state='Queued'"));
        Assert.Equal(0, f.Scalar("SELECT count(*) FROM audit WHERE action='JobClaimed'"));
        AssertPrivate(receipt);
    }

    [Fact]
    public async Task Two_owned_processes_racing_for_one_receipt_admit_one_registration_and_preserve_the_losing_cell()
    {
        using var first = new ReceiptCell(); using var second = new ReceiptCell(); var receipt = Path.Combine(first.Root, "shared.json");
        var firstBefore = first.DatabaseBytes(); var secondBefore = second.DatabaseBytes();
        using var a = first.Start(receipt); using var b = second.Start(receipt);
        var exits = await Task.WhenAll(a.Exit(), b.Exit()); Assert.Equal([0, 2], exits.Order().ToArray());
        Assert.Equal(1, first.Registrations() + second.Registrations()); Assert.False(File.Exists(receipt + ".reserve")); AssertPrivate(receipt);
        if (exits[0] == 2) Assert.Equal(firstBefore, first.DatabaseBytes()); else Assert.Equal(secondBefore, second.DatabaseBytes());
        using var doc = JsonDocument.Parse(File.ReadAllBytes(receipt)); Assert.Equal(exits[0] == 0 ? a.Id : b.Id, doc.RootElement.GetProperty("processId").GetInt32());
    }

    [Theory]
    [InlineData("Disabled")]
    [InlineData("ClaimHold")]
    [InlineData("MockLoopback")]
    public async Task Read_only_configuration_preflight_validates_private_receipt_policy_without_creating_files_or_opening_a_cell(string mode)
    {
        using var f = new ReceiptCell(); var before = f.DatabaseBytes(); var audit = f.AuditCount(); var receipt = Path.Combine(f.Root, "validated.json");
        using var worker = f.Start(receipt, validateOnly: true, mode: mode, qualificationOnly: mode != "Disabled", missingDatabase: true);
        Assert.Equal(0, await worker.Exit()); Assert.Empty(worker.Stderr);
        using var json = JsonDocument.Parse(worker.Stdout); var result = json.RootElement;
        Assert.Equal(5, result.EnumerateObject().Count()); Assert.Equal("platform-studio.foundation-worker-preflight.v1", result.GetProperty("schema").GetString());
        Assert.True(result.GetProperty("receiptPathValidated").GetBoolean()); Assert.False(result.GetProperty("workerStartupPerformed").GetBoolean());
        Assert.False(result.GetProperty("externalDispatch").GetBoolean()); Assert.Equal(worker.ConfigurationSha256, result.GetProperty("configurationSha256").GetString());
        Assert.DoesNotContain(f.Root, worker.Stdout); Assert.False(File.Exists(receipt)); Assert.False(File.Exists(receipt + ".reserve"));
        Assert.False(File.Exists(Path.Combine(f.Root, "never-created.db"))); Assert.Equal(before, f.DatabaseBytes()); Assert.Equal(audit, f.AuditCount()); Assert.Equal(0, f.Registrations());
    }

    [Theory]
    [InlineData("ClaimHold", true)]
    [InlineData("MockLoopback", true)]
    [InlineData("Disabled", false)]
    public async Task Read_only_preflight_requires_qualified_synthetic_modes_and_a_receipt(string mode, bool receiptPresent)
    {
        using var f = new ReceiptCell(); var before = f.DatabaseBytes(); var receipt = Path.Combine(f.Root, "rejected.json");
        using var worker = f.Start(receiptPresent ? receipt : null, validateOnly: true, mode: mode);
        Assert.Equal(2, await worker.Exit()); Assert.Equal(before, f.DatabaseBytes()); Assert.Equal(0, f.Registrations());
        Assert.Contains(receiptPresent ? "SyntheticWorkerOptInRequired" : "WorkerReceiptRequired", worker.Stderr);
        Assert.False(File.Exists(receipt)); Assert.False(File.Exists(receipt + ".reserve"));
    }

    [Theory]
    [InlineData("authority.json")]
    [InlineData(".installation-owner.json")]
    [InlineData(".package-owner.json")]
    [InlineData(".credential-namespace.json")]
    [InlineData(".vault.lock")]
    [InlineData(".keys.lock")]
    [InlineData(".rollback.lock")]
    [InlineData(".backup-checkpoint.guard")]
    [InlineData(".credential-recovery-stage.guard")]
    [InlineData(".credential-recovery-stage.json")]
    [InlineData(".credential-recovery-checkpoint.guard")]
    [InlineData(".credential-recovery-checkpoint.json")]
    [InlineData(".foundation-owner-initialization.json")]
    public async Task Real_launcher_refuses_protected_namespace_before_creating_logs_or_touching_cell_state(string marker)
    {
        using var f = new ReceiptCell(); var metadata = Path.Combine(f.Root, "protected-launcher"); Directory.CreateDirectory(metadata);
        File.WriteAllText(Path.Combine(metadata, marker), "synthetic preserved metadata"); var contents = Contents(metadata);
        var database = f.DatabaseBytes(); var audit = f.AuditCount(); var receipt = Path.Combine(metadata, "receipt.json");
        using var launcher = f.Launch(receipt); Assert.NotEqual(0, await launcher.Exit());
        Assert.Equal(contents, Contents(metadata)); Assert.Equal(database, f.DatabaseBytes()); Assert.Equal(audit, f.AuditCount());
        Assert.Equal(0, f.Registrations()); Assert.Empty(Directory.GetFiles(metadata, "*.log")); Assert.False(File.Exists(receipt));
    }

    [Theory]
    [InlineData("malformed")]
    [InlineData("unknown-field")]
    [InlineData("duplicate-field")]
    [InlineData("noncanonical-path")]
    [InlineData("public-endpoint")]
    [InlineData("missing-worker-dll")]
    public async Task Real_launcher_rejects_unvalidated_configuration_before_creating_any_sibling_logs(string fault)
    {
        using var f = new ReceiptCell(); var receipt = Path.Combine(f.Root, "invalid-launcher.json"); var before = f.DatabaseBytes(); var audit = f.AuditCount();
        using var launcher = f.Launch(receipt, fault); Assert.NotEqual(0, await launcher.Exit());
        Assert.Empty(Directory.GetFiles(f.Root, "*.log")); Assert.False(File.Exists(receipt)); Assert.False(File.Exists(receipt + ".reserve"));
        Assert.Equal(before, f.DatabaseBytes()); Assert.Equal(audit, f.AuditCount()); Assert.Equal(0, f.Registrations());
    }

    [Fact]
    public async Task Real_launcher_returns_private_logs_and_receipt_and_releases_configuration_handle_after_startup()
    {
        using var f = new ReceiptCell(); f.Queue(); var receipt = Path.Combine(f.Root, "launched.json");
        using var launcher = f.Launch(receipt); Assert.Equal(0, await launcher.Exit()); Assert.Empty(launcher.Stderr);
        try
        {
            using var result = JsonDocument.Parse(launcher.Stdout); Assert.Equal("Registered", result.RootElement.GetProperty("state").GetString());
            Assert.False(result.RootElement.GetProperty("externalDispatch").GetBoolean()); AssertPrivate(receipt);
            foreach (var channel in new[] { "stdoutPath", "stderrPath" })
            {
                var path = result.RootElement.GetProperty(channel).GetString()!;
                Assert.StartsWith(f.Root + Path.DirectorySeparatorChar, path, StringComparison.OrdinalIgnoreCase); AssertPrivate(path);
            }
            var configurationHash = launcher.ConfigurationSha256;
            using var exclusive = new FileStream(launcher.Configuration, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
            Assert.Equal(configurationHash, Convert.ToHexStringLower(SHA256.HashData(exclusive)));
        }
        finally { await f.WaitForNaturalExit(receipt); }
        Assert.Equal(1, f.Scalar("SELECT count(*) FROM worker_heartbeats WHERE state='Stopped'")); Assert.Equal(1, f.Scalar("SELECT count(*) FROM jobs WHERE state='Queued'"));
        Assert.False(File.Exists(receipt + ".reserve")); Assert.Empty(Directory.GetFiles(f.Root, "*.write-*"));
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
    public async Task Other_SID_parent_write_or_delete_rights_are_refused_without_ACL_repair_or_cell_access(FileSystemRights rights)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new ReceiptCell(); var parent = Path.Combine(f.Root, "unsafe-parent"); Directory.CreateDirectory(parent);
        File.WriteAllText(Path.Combine(parent, "preserved.txt"), "synthetic preserved parent state");
        var acl = new DirectoryInfo(parent).GetAccessControl(); acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"), rights, AccessControlType.Allow));
        new DirectoryInfo(parent).SetAccessControl(acl);
        var beforeAcl = new DirectoryInfo(parent).GetAccessControl().GetSecurityDescriptorBinaryForm(); var contents = Contents(parent); var database = f.DatabaseBytes(); var audit = f.AuditCount();
        var receipt = Path.Combine(parent, "receipt.json");
        using (var preflight = f.Start(receipt, validateOnly: true))
        { Assert.Equal(2, await preflight.Exit()); Assert.Contains("WorkerReceiptParentCustodyRejected", preflight.Stderr); }
        using (var worker = f.Start(receipt))
        { Assert.Equal(2, await worker.Exit()); Assert.Contains("WorkerReceiptParentCustodyRejected", worker.Stderr); }
        if (rights == FileSystemRights.WriteData)
        { using var launcher = f.Launch(receipt); Assert.NotEqual(0, await launcher.Exit()); }
        Assert.Equal(beforeAcl, new DirectoryInfo(parent).GetAccessControl().GetSecurityDescriptorBinaryForm()); Assert.Equal(contents, Contents(parent));
        Assert.Equal(database, f.DatabaseBytes()); Assert.Equal(audit, f.AuditCount()); Assert.Equal(0, f.Registrations());
    }

    [Fact]
    public async Task Inherit_only_other_SID_rules_do_not_block_a_parent_and_created_receipts_still_have_private_ACLs()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new ReceiptCell(); var parent = Path.Combine(f.Root, "inherit-only-parent"); Directory.CreateDirectory(parent);
        var acl = new DirectoryInfo(parent).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"), FileSystemRights.Write,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        new DirectoryInfo(parent).SetAccessControl(acl); var before = new DirectoryInfo(parent).GetAccessControl().GetSecurityDescriptorBinaryForm();
        var receipt = Path.Combine(parent, "receipt.json");
        using (var preflight = f.Start(receipt, validateOnly: true)) Assert.Equal(0, await preflight.Exit());
        using (var worker = f.Start(receipt)) { Assert.Equal(0, await worker.Exit()); AssertPrivate(receipt); }
        Assert.Equal(before, new DirectoryInfo(parent).GetAccessControl().GetSecurityDescriptorBinaryForm());
    }

    [Fact]
    public async Task Native_NULL_DACL_parent_is_refused_for_validation_startup_and_launcher_without_creating_state()
    {
        using var f = new ReceiptCell(); var parent = Path.Combine(f.Root, "null-dacl-parent"); Directory.CreateDirectory(parent);
        File.WriteAllText(Path.Combine(parent, "preserved.txt"), "synthetic preserved state");
        var original = new DirectoryInfo(parent).GetAccessControl().GetSecurityDescriptorBinaryForm(); var before = Contents(parent); var database = f.DatabaseBytes();
        var receipt = Path.Combine(parent, "receipt.json");
        try
        {
            var raw = new RawSecurityDescriptor(original, 0); raw.DiscretionaryAcl = null; var bytes = new byte[raw.BinaryLength]; raw.GetBinaryForm(bytes, 0);
            var untrusted = new DirectorySecurity(); untrusted.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access); new DirectoryInfo(parent).SetAccessControl(untrusted);
            Assert.Null(new RawSecurityDescriptor(new DirectoryInfo(parent).GetAccessControl().GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl);
            using (var preflight = f.Start(receipt, validateOnly: true)) { Assert.Equal(2, await preflight.Exit()); Assert.Contains("WorkerReceiptParentCustodyRejected", preflight.Stderr); }
            using (var worker = f.Start(receipt)) { Assert.Equal(2, await worker.Exit()); Assert.Contains("WorkerReceiptParentCustodyRejected", worker.Stderr); }
            using (var launcher = f.Launch(receipt)) Assert.NotEqual(0, await launcher.Exit());
            Assert.Null(new RawSecurityDescriptor(new DirectoryInfo(parent).GetAccessControl().GetSecurityDescriptorBinaryForm(), 0).DiscretionaryAcl);
            Assert.Equal(database, f.DatabaseBytes()); Assert.Equal(0, f.Registrations());
        }
        finally
        {
            var restore = new DirectorySecurity(); restore.SetSecurityDescriptorBinaryForm(original, AccessControlSections.Access); new DirectoryInfo(parent).SetAccessControl(restore);
        }
        var restored = new RawSecurityDescriptor(new DirectoryInfo(parent).GetAccessControl().GetSecurityDescriptorBinaryForm(), 0); var expected = new RawSecurityDescriptor(original, 0);
        Assert.Equal(expected.Owner, restored.Owner); Assert.Equal(expected.Group, restored.Group);
        Assert.Equal(expected.ControlFlags & ControlFlags.DiscretionaryAclProtected, restored.ControlFlags & ControlFlags.DiscretionaryAclProtected);
        var restoredAcl = new byte[restored.DiscretionaryAcl!.BinaryLength]; restored.DiscretionaryAcl.GetBinaryForm(restoredAcl, 0);
        var expectedAcl = new byte[expected.DiscretionaryAcl!.BinaryLength]; expected.DiscretionaryAcl.GetBinaryForm(expectedAcl, 0);
        Assert.Equal(expectedAcl, restoredAcl); Assert.Equal(before, Contents(parent));
    }

    [Theory]
    [InlineData("authority.json")]
    [InlineData(".credential-namespace.json")]
    [InlineData("synthetic.credential.json")]
    public async Task Directory_sentinels_preserve_incomplete_protected_namespaces_for_both_preflight_and_startup(string sentinel)
    {
        using var f = new ReceiptCell(); var parent = Path.Combine(f.Root, "directory-sentinel"); Directory.CreateDirectory(parent);
        Directory.CreateDirectory(Path.Combine(parent, sentinel)); var contents = Contents(parent); var before = f.DatabaseBytes();
        var receipt = Path.Combine(parent, "receipt.json");
        using (var preflight = f.Start(receipt, validateOnly: true)) { Assert.Equal(2, await preflight.Exit()); Assert.Contains("WorkerReceiptProtectedNamespace", preflight.Stderr); }
        using (var worker = f.Start(receipt)) { Assert.Equal(2, await worker.Exit()); Assert.Contains("WorkerReceiptProtectedNamespace", worker.Stderr); }
        using (var launcher = f.Launch(receipt)) Assert.NotEqual(0, await launcher.Exit());
        Assert.Equal(contents, Contents(parent)); Assert.Equal(before, f.DatabaseBytes()); Assert.Equal(0, f.Registrations());
    }

    static string[] Contents(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + (File.Exists(path) ? Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))) : "directory")).ToArray();
    static void AssertPrivate(string path)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent(); var acl = new FileInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected); Assert.Equal(identity.User, acl.GetOwner(typeof(SecurityIdentifier)));
        foreach (FileSystemAccessRule rule in acl.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow) Assert.Equal(identity.User, rule.IdentityReference);
    }

    sealed class ReceiptCell : IDisposable
    {
        readonly List<string> junctions = [];
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "PlatformStudio-WorkerReceiptTests", Guid.NewGuid().ToString("N"));
        public string Database { get; }
        public string Customer { get; } = Guid.NewGuid().ToString("D"); public string Cell { get; } = Guid.NewGuid().ToString("D");
        public ReceiptCell(string database = "cell.sqlite")
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Windows receipt fixture required.");
            using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User!; var acl = new DirectorySecurity();
            acl.SetOwner(sid); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(Root).Create(acl);
            Database = Path.Combine(Root, database); new FoundationStore(Database, Cell, Customer).BootstrapAdministrator("admin");
        }
        public void Queue()
        {
            var store = new FoundationStore(Database, Cell, Customer, initializeSchema: false);
            foreach (var pair in new[] { ("requester", "Requester"), ("approver", "Approver"), ("operator", "Operator") })
                store.SetMembership(Customer, "admin", pair.Item1, "default", "dev", [pair.Item2]);
            store.PutConnection(Customer, "admin", "pipeline", "github", "{\"synthetic\":true}");
            var plan = store.CreatePlan(Customer, "requester", new("default", "dev", "pipeline", "pipeline.dispatch", "{\"synthetic\":true}", true,
                DateTimeOffset.UtcNow.AddMinutes(20), new string('a', 40)));
            store.ApprovePlan(Customer, "approver", plan.Id, plan.Digest); store.EnqueuePlan(Customer, "operator", plan.Id, plan.Digest, "receipt-fixture");
        }
        public byte[] DatabaseBytes() => File.ReadAllBytes(Database);
        public long AuditCount() => Scalar("SELECT count(*) FROM audit");
        public long Registrations() => Scalar("SELECT count(*) FROM worker_heartbeats");
        public long Scalar(string sql)
        {
            using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString());
            db.Open(); using var command = db.CreateCommand(); command.CommandText = sql; return (long)command.ExecuteScalar()!;
        }
        public OwnedWorker Start(string? receipt, bool validateOnly = false, string mode = "Disabled", bool qualificationOnly = false, bool missingDatabase = false)
        {
            var config = WriteConfiguration(receipt, mode, qualificationOnly, missingDatabase);
            var info = StartInfo(Dotnet());
            info.ArgumentList.Add(WorkerAssembly());
            info.ArgumentList.Add(validateOnly ? "--validate-config" : "--config"); info.ArgumentList.Add(config); return new(Process.Start(info)!, config);
        }
        string WriteConfiguration(string? receipt, string mode = "Disabled", bool qualificationOnly = false, bool missingDatabase = false)
        {
            var config = Path.Combine(Root, Guid.NewGuid().ToString("N") + ".json");
            File.WriteAllText(config, JsonSerializer.Serialize(new { customerId = Customer, cellId = Cell, databasePath = missingDatabase ? Path.Combine(Root, "never-created.db") : Database,
                mode, qualificationOnly, mockEndpoint = mode == "MockLoopback" ? "http://127.0.0.1:50977/" : null,
                runSeconds = 2, heartbeatIntervalMilliseconds = 250, receiptPath = receipt }, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            return config;
        }
        public OwnedWorker Launch(string receipt, string? fault = null)
        {
            var config = WriteConfiguration(receipt); var text = File.ReadAllText(config);
            if (fault == "malformed") text = "{";
            if (fault is "unknown-field" or "duplicate-field") text = text[..^1] + (fault == "unknown-field" ? ",\"unreviewed\":true}" : ",\"receiptPath\":" + JsonSerializer.Serialize(receipt) + "}");
            if (fault is "noncanonical-path" or "public-endpoint")
            {
                var body = System.Text.Json.Nodes.JsonNode.Parse(text)!;
                if (fault == "noncanonical-path") body["receiptPath"] = ".\\invalid-launcher.json";
                else { body["mode"] = "MockLoopback"; body["qualificationOnly"] = true; body["mockEndpoint"] = "https://example.invalid/"; }
                text = body.ToJsonString();
            }
            File.WriteAllText(config, text);
            var pwsh = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Select(part => Path.Combine(part, "pwsh.exe")).First(File.Exists);
            var info = StartInfo(pwsh); info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-Command");
            static string Quote(string value) => "'" + value.Replace("'", "''", StringComparison.Ordinal) + "'";
            info.ArgumentList.Add("$ErrorActionPreference = 'Stop'; $result = & " + Quote(Path.Combine(RepositoryRoot(), "scripts", "Run-FoundationWorker.ps1")) + " -ConfigurationPath " + Quote(config) +
                " -DotNetPath " + Quote(Dotnet()) + (fault == "missing-worker-dll" ? "" : " -WorkerDllPath " + Quote(WorkerAssembly())) + "; $result | ConvertTo-Json -Compress");
            return new(Process.Start(info)!, config);
        }
        static string Dotnet() => Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "C:\\IT\\Package\\dotnet", "dotnet.exe");
        static string WorkerAssembly() => Path.Combine(new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.FullName,
            "SelfService.Foundation.Worker", "release", "SelfService.Foundation.Worker.dll");
        static string RepositoryRoot()
        {
            for (var directory = new DirectoryInfo(AppContext.BaseDirectory); directory is not null; directory = directory.Parent)
                if (File.Exists(Path.Combine(directory.FullName, "scripts", "Run-FoundationWorker.ps1"))) return directory.FullName;
            throw new IOException("Fixture repository root missing.");
        }
        public async Task WaitForNaturalExit(string receipt)
        {
            using var doc = JsonDocument.Parse(File.ReadAllBytes(receipt)); var metadata = doc.RootElement;
            Process process;
            try { process = Process.GetProcessById(metadata.GetProperty("processId").GetInt32()); }
            catch (ArgumentException) { return; } // This particular owned child already exited.
            using (process)
            {
                _ = process.SafeHandle; // Retain the opened kernel object before checking its identity; never kill by a PID.
                if (process.HasExited) return;
                Assert.Equal(metadata.GetProperty("processStartedUtc").GetDateTimeOffset().UtcTicks, new DateTimeOffset(process.StartTime.ToUniversalTime()).UtcTicks);
                Assert.Equal(metadata.GetProperty("executablePath").GetString(), process.MainModule!.FileName, ignoreCase: true);
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10)); await process.WaitForExitAsync(timeout.Token);
            }
        }
        public async Task Junction(string link, string target)
        {
            Assert.StartsWith(Root + Path.DirectorySeparatorChar, link, StringComparison.OrdinalIgnoreCase);
            Assert.StartsWith(Root + Path.DirectorySeparatorChar, target, StringComparison.OrdinalIgnoreCase);
            var info = StartInfo(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32", "cmd.exe"));
            foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(arg);
            using var process = Process.Start(info)!; var output = process.StandardOutput.ReadToEndAsync(); var error = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
            try { await process.WaitForExitAsync(timeout.Token); } catch { if (!process.HasExited) { process.Kill(); process.WaitForExit(5000); } throw; }
            await Task.WhenAll(output, error); Assert.Equal(0, process.ExitCode); junctions.Add(link);
        }
        ProcessStartInfo StartInfo(string executable)
        {
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Root };
            info.Environment.Clear(); foreach (var name in new[] { "SystemRoot", "windir", "ComSpec" }) if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
            info.Environment["DOTNET_ROOT"] = "C:\\IT\\Package\\dotnet"; info.Environment["PATH"] = "C:\\IT\\Package\\dotnet;" + Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32");
            info.Environment["TEMP"] = Root; info.Environment["TMP"] = Root; info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            info.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false"; return info;
        }
        public void Dispose()
        {
            var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PlatformStudio-WorkerReceiptTests")) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(expected, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture ownership boundary rejected.");
            foreach (var junction in junctions) Directory.Delete(junction, false);
            Delete(Root);
        }
        static void Delete(string root)
        {
            foreach (var path in Directory.EnumerateFileSystemEntries(root))
            { Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint)); if (Directory.Exists(path)) Delete(path); else File.Delete(path); }
            Directory.Delete(root, false);
        }
    }
    sealed class OwnedWorker(Process process, string configuration) : IDisposable
    {
        readonly Task<string> output = process.StandardOutput.ReadToEndAsync(); readonly Task<string> errors = process.StandardError.ReadToEndAsync();
        public int Id => process.Id; public string Stderr => errors.IsCompletedSuccessfully ? errors.Result : "";
        public string Stdout => output.IsCompletedSuccessfully ? output.Result : "";
        public string Configuration => configuration;
        public string ConfigurationSha256 => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(configuration)));
        public async Task<int> Exit()
        { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30)); await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(output, errors); return process.ExitCode; }
        public async Task Ready(string receipt)
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            while (!File.Exists(receipt)) { if (process.HasExited) { await Exit(); Assert.Fail("Worker exited before a complete receipt: " + Stderr); } await Task.Delay(20, timeout.Token); }
            using var document = JsonDocument.Parse(File.ReadAllBytes(receipt)); Assert.Equal(Id, document.RootElement.GetProperty("processId").GetInt32());
        }
        public void Dispose()
        { if (!process.HasExited) { process.Kill(); Assert.True(process.WaitForExit(5000)); } process.Dispose(); }
    }
}
