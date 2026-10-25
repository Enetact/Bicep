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
public sealed class FoundationOwnerProcessTests
{
    [Fact]
    public async Task Fresh_initialization_is_private_explicit_and_never_reopens_an_existing_destination()
    {
        using var f = new OwnerFixture(); var destination = Path.Combine(f.Root, "new-cell");
        var created = await f.Run(new { operation = "initialize", customerId = f.Customer, cellId = f.Cell, destinationDirectory = destination,
            administratorActorKey = OwnerFixture.Admin, reviewerActorKey = OwnerFixture.Reviewer, projectId = "default", environmentId = "dev" });
        Assert.Equal(0, created.Exit); Assert.Equal(FoundationStore.CurrentSchemaVersion, created.Result.GetProperty("schema").GetProperty("version").GetInt32());
        var database = Path.Combine(destination, "cell.sqlite"); var before = FileHash(database);
        AssertOwnerOnly(created.OutputPath, file: true); AssertOwnerOnly(destination, file: false);
        AssertOwnerOnly(Path.Combine(destination, ".foundation-owner-initialization.json"), file: true);
        var duplicate = await f.Run(new { operation = "initialize", customerId = f.Customer, cellId = f.Cell, destinationDirectory = destination,
            administratorActorKey = OwnerFixture.Admin, reviewerActorKey = OwnerFixture.Reviewer, projectId = "default", environmentId = "dev" });
        Assert.Equal(2, duplicate.Exit); Assert.Equal("OwnerDestinationAlreadyExists", duplicate.Code); Assert.Equal(before, FileHash(database));
        var inspect = await f.Run(new { operation = "inspect", customerId = f.Customer, cellId = f.Cell, databasePath = database });
        Assert.Equal(0, inspect.Exit); Assert.Equal("CurrentSchema", inspect.Result.GetProperty("state").GetString());
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Existing_empty_directory_or_zero_byte_database_is_not_claimed_or_initialized(bool zeroByte)
    {
        using var f = new OwnerFixture(); var destination = Path.Combine(f.Root, "existing"); Directory.CreateDirectory(destination);
        var database = Path.Combine(destination, "cell.sqlite"); if (zeroByte) File.WriteAllBytes(database, []);
        var result = await f.Run(new { operation = "initialize", customerId = f.Customer, cellId = f.Cell, destinationDirectory = destination,
            administratorActorKey = OwnerFixture.Admin, reviewerActorKey = OwnerFixture.Reviewer, projectId = "default", environmentId = "dev" });
        Assert.Equal(2, result.Exit); Assert.Equal("OwnerDestinationAlreadyExists", result.Code);
        Assert.Equal(zeroByte ? 1 : 0, Directory.GetFiles(destination).Length); if (zeroByte) Assert.Equal(0, new FileInfo(database).Length);
    }

    [Theory]
    [InlineData(1, "LegacySchemaRequiresStagedUpgrade")]
    [InlineData(4, "LegacySchemaRequiresStagedUpgrade")]
    [InlineData(99, "UnsupportedSchema")]
    public async Task Inspection_reports_legacy_or_unsupported_schema_without_migration(int version, string state)
    {
        using var f = new OwnerFixture(); var database = Path.Combine(f.Root, "legacy.sqlite");
        using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = database, Pooling = false }.ToString()))
        { db.Open(); using var command = db.CreateCommand(); command.CommandText = $"CREATE TABLE cell(singleton INTEGER PRIMARY KEY,schema_version INTEGER,customer_id TEXT,cell_id TEXT);INSERT INTO cell VALUES(1,{version},'customer-owner','cell-owner');"; command.ExecuteNonQuery(); }
        var before = FileHash(database);
        var result = await f.Run(new { operation = "inspect", customerId = f.Customer, cellId = f.Cell, databasePath = database });
        Assert.Equal(0, result.Exit); Assert.Equal(state, result.Result.GetProperty("state").GetString()); Assert.Equal(before, FileHash(database));
    }

    [Fact]
    public async Task Authority_provision_status_and_exact_CAS_activation_respect_live_guards_and_stale_heads()
    {
        using var f = new OwnerFixture(); var store = f.Source(); var authorityPath = Path.Combine(f.Root, "authority");
        var provision = await f.Run(new { operation = "authority.provision", customerId = f.Customer, cellId = f.Cell, authorityDirectory = authorityPath });
        Assert.Equal(0, provision.Exit); Assert.Equal(JsonValueKind.Null, provision.Result.GetProperty("binding").ValueKind);
        var instance = store.GetCellInstanceIdentity().DatabaseInstanceId;
        var activate = await f.Run(new { operation = "authority.activate", customerId = f.Customer, cellId = f.Cell, authorityDirectory = authorityPath,
            databasePath = f.Database, databaseInstanceId = instance, expectedGeneration = 0, expectedBindingSha256 = (string?)null });
        Assert.Equal(0, activate.Exit); var authority = new FoundationCellActivationAuthority(authorityPath, f.Customer, f.Cell);
        var current = authority.ReadCurrent()!;
        using (authority.AcquireLease(f.Database))
        {
            var busy = await f.Run(new { operation = "authority.activate", customerId = f.Customer, cellId = f.Cell, authorityDirectory = authorityPath,
                databasePath = f.Database, databaseInstanceId = instance, expectedGeneration = current.Generation, expectedBindingSha256 = current.Sha256 });
            Assert.Equal(2, busy.Exit); Assert.Equal("ActivationAuthorityBusy", busy.Code);
        }
        var stale = await f.Run(new { operation = "authority.activate", customerId = f.Customer, cellId = f.Cell, authorityDirectory = authorityPath,
            databasePath = f.Database, databaseInstanceId = instance, expectedGeneration = 0, expectedBindingSha256 = (string?)null });
        Assert.Equal("ActivationGenerationChanged", stale.Code);
        var status = await f.Run(new { operation = "authority.status", customerId = f.Customer, cellId = f.Cell, authorityDirectory = authorityPath });
        Assert.Equal(0, status.Exit); Assert.Equal(current.Sha256, status.Result.GetProperty("binding").GetProperty("sha256").GetString());
    }

    [Fact]
    public async Task Result_cannot_enter_an_authority_namespace_or_SQLite_sidecar_and_existing_state_is_unchanged()
    {
        using var f = new OwnerFixture(); var store = f.Source(); var root = Path.Combine(f.Root, "authority");
        var authority = FoundationCellActivationAuthority.Provision(root, f.Customer, f.Cell);
        var current = authority.Activate(f.Database, store.GetCellInstanceIdentity().DatabaseInstanceId, 0, null);
        var files = Directory.GetFiles(root).Order().ToArray(); var forbidden = Path.Combine(root, "audit.owner-result.json");
        var result = await f.Run(new { operation = "inspect", customerId = f.Customer, cellId = f.Cell, databasePath = f.Database }, output: forbidden);
        Assert.Equal("OwnerProtectedResultNamespace", result.Code); Assert.False(File.Exists(forbidden));
        Assert.Equal(files, Directory.GetFiles(root).Order().ToArray()); Assert.Equal(current, authority.ReadCurrent());
        var sidecar = f.Database + "-journal";
        result = await f.Run(new { operation = "inspect", customerId = f.Customer, cellId = f.Cell, databasePath = f.Database }, output: sidecar);
        Assert.Equal(2, result.Exit); Assert.False(File.Exists(sidecar)); Assert.Equal(current, authority.ReadCurrent());
        var child = Path.Combine(root, "child");
        result = await f.Run(new { operation = "initialize", customerId = f.Customer, cellId = f.Cell, destinationDirectory = child,
            administratorActorKey = OwnerFixture.Admin, reviewerActorKey = OwnerFixture.Reviewer, projectId = "default", environmentId = "dev" });
        Assert.Equal(2, result.Exit); Assert.False(Directory.Exists(child)); Assert.Equal(current, authority.ReadCurrent());
        result = await f.Run(new { operation = "authority.provision", customerId = f.Customer, cellId = f.Cell, authorityDirectory = child });
        Assert.Equal(2, result.Exit); Assert.False(Directory.Exists(child)); Assert.Equal(files, Directory.GetFiles(root).Order().ToArray());
    }

    [Fact]
    public async Task Signed_backup_verification_and_staged_restore_use_explicit_public_pins_and_never_activate()
    {
        using var f = new OwnerFixture(); using var key = RSA.Create(2048); var store = f.Source(); var job = f.Queue(store);
        var identity = store.GetCellInstanceIdentity(); var packet = f.Sign(store, key); var directory = Path.Combine(f.Root, "restored"); var target = Path.Combine(directory, "cell.sqlite");
        var verified = await f.Run(f.BackupRequest("backup.verify", packet, key));
        Assert.Equal(0, verified.Exit); Assert.True(verified.Result.GetProperty("snapshotBytesVerified").GetBoolean());
        Assert.True(verified.Result.GetProperty("sqliteAuditVerificationPerformed").GetBoolean());
        var restored = await f.Run(f.BackupRequest("backup.restore", packet, key, directory));
        Assert.Equal(0, restored.Exit); Assert.False(restored.Result.GetProperty("activationPerformed").GetBoolean());
        AssertOwnerOnly(directory, file: false); AssertOwnerOnly(target, file: true, protectedRule: false);
        var reopened = new FoundationStore(target, f.Cell, f.Customer, initializeSchema: false);
        Assert.NotEqual(identity.DatabaseInstanceId, reopened.GetCellInstanceIdentity().DatabaseInstanceId);
        Assert.Equal("OutcomeUnknown", reopened.GetJob(f.Customer, OwnerFixture.Operator, job.Id).State);
        Assert.Equal(identity, store.GetCellInstanceIdentity()); Assert.Equal("Queued", store.GetJob(f.Customer, OwnerFixture.Operator, job.Id).State);
        var occupied = Path.Combine(f.Root, "occupied.sqlite"); File.WriteAllBytes(occupied, []);
        var rejected = await f.Run(f.BackupRequest("backup.restore", packet, key, occupied));
        Assert.Equal("OwnerDestinationAlreadyExists", rejected.Code); Assert.Equal(0, new FileInfo(occupied).Length);
        var authorityPath = Path.Combine(f.Root, "authority"); var authority = FoundationCellActivationAuthority.Provision(authorityPath, f.Customer, f.Cell);
        var files = Directory.GetFiles(authorityPath).Order().ToArray();
        rejected = await f.Run(f.BackupRequest("backup.restore", packet, key, Path.Combine(authorityPath, "unrelated.sqlite")));
        Assert.Equal(2, rejected.Exit); Assert.Null(authority.ReadCurrent()); Assert.Equal(files, Directory.GetFiles(authorityPath).Order().ToArray());
    }

    public static IEnumerable<object[]> NamespaceSentinels()
    {
        foreach (var marker in new[] { ".installation-owner.json", ".installation.guard", ".package-owner.json", ".credential-namespace.json",
            ".vault.lock", ".keys.lock", "cell-binding.json", ".rollback.lock", ".backup-checkpoint.guard", ".foundation-owner-initialization.json",
            ".credential-recovery-stage.guard", ".credential-recovery-stage.json", ".credential-recovery-checkpoint.guard", ".credential-recovery-checkpoint.json", "synthetic.credential.json", "synthetic.dpapi.json" })
            foreach (var directory in new[] { false, true }) yield return [marker, directory];
    }
    [Theory]
    [MemberData(nameof(NamespaceSentinels))]
    public async Task Result_and_mutation_destinations_preserve_protected_file_and_directory_namespaces(string marker, bool directory)
    {
        using var f = new OwnerFixture(); using var key = RSA.Create(2048); var packet = f.Sign(f.Source(), key);
        var software = Path.Combine(f.Root, "immutable-software"); var nested = Path.Combine(software, "nested");
        Directory.CreateDirectory(nested);
        if (directory) Directory.CreateDirectory(Path.Combine(software, marker)); else File.WriteAllText(Path.Combine(software, marker), "synthetic-owned-marker");
        File.WriteAllBytes(Path.Combine(nested, "software.bin"), [1, 2, 3, 4]);
        string[] Contents() => Directory.EnumerateFileSystemEntries(software, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(software, path) + ":" + (File.Exists(path) ? FileHash(path) : "directory")).ToArray();
        var before = Contents(); var receipt = Path.Combine(nested, "unsafe.owner-result.json");
        var result = await f.Run(new { operation = "inspect", customerId = f.Customer, cellId = f.Cell, databasePath = f.Database }, output: receipt);
        Assert.Equal("OwnerProtectedResultNamespace", result.Code); Assert.False(File.Exists(receipt));
        result = await f.Run(new { operation = "initialize", customerId = f.Customer, cellId = f.Cell, destinationDirectory = Path.Combine(nested, "new-cell"),
            administratorActorKey = OwnerFixture.Admin, reviewerActorKey = OwnerFixture.Reviewer, projectId = "default", environmentId = "dev" });
        Assert.Equal("OwnerProtectedResultNamespace", result.Code);
        result = await f.Run(new { operation = "authority.provision", customerId = f.Customer, cellId = f.Cell, authorityDirectory = Path.Combine(nested, "new-authority") });
        Assert.Equal("OwnerProtectedResultNamespace", result.Code);
        result = await f.Run(f.BackupRequest("backup.restore", packet, key, Path.Combine(nested, "new-restoration")));
        Assert.Equal("OwnerProtectedResultNamespace", result.Code); Assert.Equal(before, Contents());
    }

    [Theory]
    [InlineData("wrong-key")]
    [InlineData("wrong-latest")]
    [InlineData("wrong-customer")]
    [InlineData("tampered-snapshot")]
    public async Task Rejected_restore_authority_never_creates_the_destination(string variant)
    {
        using var f = new OwnerFixture(); using var key = RSA.Create(2048); using var wrong = RSA.Create(2048);
        var packet = f.Sign(f.Source(), key); var destination = Path.Combine(f.Root, "must-not-create");
        var request = f.BackupRequest("backup.restore", packet, variant == "wrong-key" ? wrong : key, destination);
        if (variant == "wrong-latest") request["restoreAuthority"] = FoundationBackupSignatures.Authority(packet.Manifest) with { Generation = 2 };
        if (variant == "wrong-customer") request["customerId"] = "other-customer";
        if (variant == "tampered-snapshot")
        { var bytes = File.ReadAllBytes(packet.Backup.DatabasePath); bytes[0] ^= 1; File.WriteAllBytes(packet.Backup.DatabasePath, bytes); }
        var result = await f.Run(request); Assert.Equal(2, result.Exit); Assert.False(Directory.Exists(destination));
    }

    [Fact]
    public async Task Identity_proposal_matches_scope_and_is_advice_only_with_stable_canonical_hash()
    {
        using var f = new OwnerFixture(); var contract = new EntraCellOnboardingRequest(f.Customer, f.Cell, IdentityTestIssuer.Tenant,
            Guid.NewGuid().ToString("D"), Guid.NewGuid().ToString("D"),
            FoundationRoles.All.Append(EntraCellOnboarding.WorkloadRole).ToDictionary(r => r, _ => Guid.NewGuid().ToString("D")),
            [IdentityTestIssuer.Client], [new(IdentityTestIssuer.WorkerClient, IdentityTestIssuer.WorkerSubject)]);
        var result = await f.Run(new { operation = "identity.propose", customerId = f.Customer, cellId = f.Cell, identityContract = contract });
        Assert.Equal(0, result.Exit); Assert.False(result.Result.GetProperty("identityGrantAuthorized").GetBoolean());
        Assert.Equal(EntraCellOnboarding.Create(contract).Sha256, result.Result.GetProperty("sha256").GetString());
        var wrong = await f.Run(new { operation = "identity.propose", customerId = "other-customer", cellId = f.Cell, identityContract = contract });
        Assert.Equal("OwnerIdentityBindingRejected", wrong.Code); Assert.False(File.Exists(f.Database));
    }

    [Theory]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    [InlineData("migrate")]
    public async Task Strict_requests_reject_untrusted_fields_and_do_not_echo_secret_material(string variant)
    {
        using var f = new OwnerFixture();
        var raw = variant switch
        {
            "unknown" => "{\"operation\":\"inspect\",\"customerId\":\"customer-owner\",\"cellId\":\"cell-owner\",\"databasePath\":\"unused\",\"password\":\"do-not-echo-this-fixture\"}",
            "duplicate" => "{\"operation\":\"inspect\",\"operation\":\"initialize\"}",
            "oversized" => "{\"operation\":\"" + new string('x', 66000) + "\"}",
            _ => "{\"operation\":\"migrate\"}"
        };
        var result = await f.Run(null, raw: raw); Assert.Equal(2, result.Exit);
        Assert.DoesNotContain("do-not-echo", result.Stdout); Assert.Empty(result.Stderr); Assert.False(File.Exists(f.Database));
    }

    public static IEnumerable<object[]> UnsafeRequestCustody()
    {
        foreach (var subject in new[] { "request-file", "request-parent", "result-parent" })
            foreach (var rights in new[] { FileSystemRights.WriteData, FileSystemRights.AppendData, FileSystemRights.DeleteSubdirectoriesAndFiles,
                FileSystemRights.Delete, FileSystemRights.ChangePermissions, FileSystemRights.TakeOwnership, FileSystemRights.WriteAttributes, FileSystemRights.WriteExtendedAttributes })
                yield return [subject, rights];
    }
    [Theory]
    [MemberData(nameof(UnsafeRequestCustody))]
    public async Task Unsafe_request_file_or_immediate_parent_cannot_activate_or_create_result_and_preserves_existing_custody(string subject, FileSystemRights rights)
    {
        using var f = new OwnerFixture(); var source = f.Source(); var authorityPath = Path.Combine(f.Root, "authority");
        var authority = FoundationCellActivationAuthority.Provision(authorityPath, f.Customer, f.Cell);
        var binding = authority.Activate(f.Database, source.GetCellInstanceIdentity().DatabaseInstanceId, 0, null);
        var otherPath = Path.Combine(f.Root, "other.sqlite"); var other = new FoundationStore(otherPath, f.Cell, f.Customer);
        var requestParent = Path.Combine(f.Root, "requests"); var resultParent = Path.Combine(f.Root, "results");
        Directory.CreateDirectory(requestParent); Directory.CreateDirectory(resultParent);
        var input = Path.Combine(requestParent, "activate.json"); var output = Path.Combine(resultParent, "refused.owner-result.json");
        WriteRequest(input, new { operation = "authority.activate", customerId = f.Customer, cellId = f.Cell, authorityDirectory = authorityPath,
            databasePath = otherPath, databaseInstanceId = other.GetCellInstanceIdentity().DatabaseInstanceId,
            expectedGeneration = binding.Generation, expectedBindingSha256 = binding.Sha256 });
        var target = subject == "request-file" ? input : subject == "request-parent" ? requestParent : resultParent;
        AddAccess(target, rights); var acl = SecurityBytes(target); var bytes = File.ReadAllBytes(input); var originalDb = FileHash(f.Database); var otherDb = FileHash(otherPath);
        var metadata = Contents(authorityPath);
        var rejected = await f.Run(null, existingRequest: input, output: output);
        Assert.Equal(2, rejected.Exit);
        Assert.Equal(subject switch { "request-file" => "OwnerRequestFileCustodyRejected", "request-parent" => "OwnerRequestParentCustodyRejected", _ => "OwnerResultParentCustodyRejected" }, rejected.Code);
        Assert.False(File.Exists(output)); Assert.Equal(acl, SecurityBytes(target)); Assert.Equal(bytes, File.ReadAllBytes(input));
        Assert.Equal(originalDb, FileHash(f.Database)); Assert.Equal(otherDb, FileHash(otherPath)); Assert.Equal(metadata, Contents(authorityPath)); Assert.Equal(binding, authority.ReadCurrent());
    }

    [Theory]
    [InlineData("request-file")]
    [InlineData("request-parent")]
    [InlineData("result-parent")]
    public async Task Public_read_only_custody_is_allowed_without_broadening_private_result_permissions(string subject)
    {
        using var f = new OwnerFixture(); f.Source(); var requests = Path.Combine(f.Root, "requests"); var results = Path.Combine(f.Root, "results");
        Directory.CreateDirectory(requests); Directory.CreateDirectory(results); var input = Path.Combine(requests, "inspect.json"); var output = Path.Combine(results, "readable.owner-result.json");
        WriteRequest(input, new { operation = "inspect", customerId = f.Customer, cellId = f.Cell, databasePath = f.Database });
        var target = subject == "request-file" ? input : subject == "request-parent" ? requests : results;
        AddAccess(target, FileSystemRights.ReadAndExecute); var before = SecurityBytes(target); var database = FileHash(f.Database);
        var result = await f.Run(null, existingRequest: input, output: output);
        Assert.Equal(0, result.Exit); Assert.Equal("CurrentSchema", result.Result.GetProperty("state").GetString()); AssertOwnerOnly(output, file: true);
        Assert.Equal(before, SecurityBytes(target)); Assert.Equal(database, FileHash(f.Database));
    }

    [Theory]
    [InlineData("request-file")]
    [InlineData("request-parent")]
    [InlineData("result-parent")]
    public async Task Native_NULL_DACL_custody_is_refused_before_any_result_or_authority_effect_and_test_ACL_is_restored(string subject)
    {
        using var f = new OwnerFixture(); var source = f.Source(); var authorityPath = Path.Combine(f.Root, "authority");
        var authority = FoundationCellActivationAuthority.Provision(authorityPath, f.Customer, f.Cell);
        var binding = authority.Activate(f.Database, source.GetCellInstanceIdentity().DatabaseInstanceId, 0, null);
        var otherPath = Path.Combine(f.Root, "other.sqlite"); var other = new FoundationStore(otherPath, f.Cell, f.Customer);
        var requests = Path.Combine(f.Root, "requests"); var results = Path.Combine(f.Root, "results"); Directory.CreateDirectory(requests); Directory.CreateDirectory(results);
        var input = Path.Combine(requests, "null-dacl.json"); var output = Path.Combine(results, "null-dacl.owner-result.json");
        WriteRequest(input, new { operation = "authority.activate", customerId = f.Customer, cellId = f.Cell, authorityDirectory = authorityPath,
            databasePath = otherPath, databaseInstanceId = other.GetCellInstanceIdentity().DatabaseInstanceId,
            expectedGeneration = binding.Generation, expectedBindingSha256 = binding.Sha256 });
        var target = subject == "request-file" ? input : subject == "request-parent" ? requests : results;
        var original = SecurityBytes(target); var bytes = File.ReadAllBytes(input); var database = FileHash(f.Database); var metadata = Contents(authorityPath);
        try
        {
            var descriptor = new RawSecurityDescriptor(original, 0); descriptor.DiscretionaryAcl = null;
            var untrusted = new byte[descriptor.BinaryLength]; descriptor.GetBinaryForm(untrusted, 0); SetAccessBytes(target, untrusted);
            var observed = new RawSecurityDescriptor(SecurityBytes(target), 0); Assert.Null(observed.DiscretionaryAcl);
            var rejected = await f.Run(null, existingRequest: input, output: output);
            Assert.Equal(2, rejected.Exit); Assert.Equal(subject switch { "request-file" => "OwnerRequestFileCustodyRejected", "request-parent" => "OwnerRequestParentCustodyRejected", _ => "OwnerResultParentCustodyRejected" }, rejected.Code);
            Assert.False(File.Exists(output)); Assert.Null(new RawSecurityDescriptor(SecurityBytes(target), 0).DiscretionaryAcl); Assert.Equal(database, FileHash(f.Database));
            Assert.Equal(metadata, Contents(authorityPath)); Assert.Equal(binding, authority.ReadCurrent());
        }
        finally { SetAccessBytes(target, original); }
        Assert.Equal(AccessState(original), AccessState(SecurityBytes(target))); Assert.Equal(bytes, File.ReadAllBytes(input));
    }

    [Fact]
    public async Task Inherit_only_foreign_parent_rules_do_not_apply_to_parent_and_are_not_copied_into_private_results()
    {
        using var f = new OwnerFixture(); f.Source(); var results = Path.Combine(f.Root, "results"); Directory.CreateDirectory(results);
        var acl = new DirectoryInfo(results).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"), FileSystemRights.Write,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.InheritOnly, AccessControlType.Allow));
        new DirectoryInfo(results).SetAccessControl(acl); var before = SecurityBytes(results); var output = Path.Combine(results, "private.owner-result.json");
        var result = await f.Run(new { operation = "inspect", customerId = f.Customer, cellId = f.Cell, databasePath = f.Database }, output: output);
        Assert.Equal(0, result.Exit); AssertOwnerOnly(output, file: true); Assert.Equal(before, SecurityBytes(results));
    }

    [Fact]
    public async Task Trusted_request_bytes_remain_held_against_normal_write_and_replacement_until_the_operation_finishes()
    {
        using var f = new OwnerFixture(); f.Source(); var input = Path.Combine(f.Root, "held.json"); var output = Path.Combine(f.Root, "held.owner-result.json");
        WriteRequest(input, new { operation = "inspect", customerId = f.Customer, cellId = f.Cell, databasePath = f.Database }); var bytes = File.ReadAllBytes(input);
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = f.Database, Pooling = false }.ToString());
        db.Open(); using var command = db.CreateCommand(); command.CommandText = "PRAGMA journal_mode=DELETE"; command.ExecuteScalar();
        command.CommandText = "BEGIN EXCLUSIVE"; command.ExecuteNonQuery();
        var running = f.Run(null, existingRequest: input, output: output);
        try
        {
            var wait = Stopwatch.StartNew();
            while (!File.Exists(output) && !running.IsCompleted && wait.Elapsed < TimeSpan.FromSeconds(3)) await Task.Delay(20);
            Assert.True(File.Exists(output)); Assert.False(running.IsCompleted);
            Assert.Throws<IOException>(() => { using var writer = new FileStream(input, FileMode.Open, FileAccess.Write, FileShare.None); });
            Assert.Throws<IOException>(() => File.Move(input, Path.Combine(f.Root, "unreviewed-replacement.json")));
        }
        finally { command.CommandText = "ROLLBACK"; command.ExecuteNonQuery(); }
        Assert.Equal(0, (await running).Exit); Assert.Equal(bytes, File.ReadAllBytes(input));
        using var released = new FileStream(input, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
        Assert.Equal(bytes.Length, released.Length);
    }

    static void WriteRequest(string path, object request) => File.WriteAllText(path, JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
    static byte[] SecurityBytes(string path) => (Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorBinaryForm();
    static (string? Owner, string? Group, bool Protected, string Acl) AccessState(byte[] bytes)
    {
        var descriptor = new RawSecurityDescriptor(bytes, 0); var acl = descriptor.DiscretionaryAcl!; var access = new byte[acl.BinaryLength]; acl.GetBinaryForm(access, 0);
        // Windows can normalize AUTO_INHERITED flags during SetAccessControl; compare the restored owner and actual ACE bytes.
        return (descriptor.Owner?.Value, descriptor.Group?.Value, (descriptor.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, Convert.ToHexString(access));
    }
    static void SetAccessBytes(string path, byte[] bytes)
    {
        FileSystemSecurity acl = Directory.Exists(path) ? new DirectorySecurity() : new FileSecurity();
        acl.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access); // Do not request SACL/SeSecurityPrivilege changes.
        if (acl is DirectorySecurity directory) new DirectoryInfo(path).SetAccessControl(directory); else new FileInfo(path).SetAccessControl((FileSecurity)acl);
    }
    static void AddAccess(string path, FileSystemRights rights)
    {
        var acl = Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier("S-1-1-0"), rights, AccessControlType.Allow));
        if (acl is DirectorySecurity directory) new DirectoryInfo(path).SetAccessControl(directory); else new FileInfo(path).SetAccessControl((FileSecurity)acl);
    }
    static string[] Contents(string directory) => Directory.GetFileSystemEntries(directory, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(directory, path) + ":" + (File.Exists(path) ? FileHash(path) : "directory")).ToArray();

    static string FileHash(string path) => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)));
    static void AssertOwnerOnly(string path, bool file, bool protectedRule = true)
    {
        if (!OperatingSystem.IsWindows()) return;
        using var identity = WindowsIdentity.GetCurrent();
        FileSystemSecurity security = file ? new FileInfo(path).GetAccessControl() : new DirectoryInfo(path).GetAccessControl();
        if (protectedRule) Assert.True(security.AreAccessRulesProtected); Assert.Equal(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
        foreach (FileSystemAccessRule rule in security.GetAccessRules(true, true, typeof(SecurityIdentifier)))
            if (rule.AccessControlType == AccessControlType.Allow) Assert.Equal(identity.User, rule.IdentityReference);
    }

    sealed record OwnerResult(int Exit, string Code, string Stdout, string Stderr, string OutputPath, JsonElement Result);
    sealed class OwnerFixture : IDisposable
    {
        public const string Admin = "user:11111111-1111-1111-1111-111111111111:aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa";
        public const string Reviewer = "user:11111111-1111-1111-1111-111111111111:cccccccc-cccc-cccc-cccc-cccccccccccc";
        public const string Requester = "user:11111111-1111-1111-1111-111111111111:bbbbbbbb-bbbb-bbbb-bbbb-bbbbbbbbbbbb";
        public const string Operator = "user:11111111-1111-1111-1111-111111111111:dddddddd-dddd-dddd-dddd-dddddddddddd";
        readonly string parent = Path.Combine(Path.GetTempPath(), "PlatformStudio-OwnerTests");
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "PlatformStudio-OwnerTests", Guid.NewGuid().ToString("N"));
        public string Customer => "customer-owner"; public string Cell => "cell-owner"; public string Database => Path.Combine(Root, "source.sqlite");
        public OwnerFixture()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Windows owner fixture required.");
            using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User!; var acl = new DirectorySecurity();
            acl.SetOwner(sid); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(Root).Create(acl);
        }
        public FoundationStore Source() { var store = new FoundationStore(Database, Cell, Customer); store.BootstrapAdministrator(Admin, initialApproverKey: Reviewer); return store; }
        public FoundationJob Queue(FoundationStore store)
        {
            store.SetMembership(Customer, Admin, Requester, "default", "dev", [FoundationRoles.Requester]);
            store.SetMembership(Customer, Admin, Operator, "default", "dev", [FoundationRoles.Operator]);
            store.PutConnection(Customer, Admin, "pipeline", "github", "{\"repository\":\"synthetic\"}");
            var plan = store.CreatePlan(Customer, Requester, new("default", "dev", "pipeline", "pipeline.dispatch", "{\"simulated\":true}", true, DateTimeOffset.UtcNow.AddMinutes(20), new string('a', 40)));
            store.ApprovePlan(Customer, Reviewer, plan.Id, plan.Digest); return store.EnqueuePlan(Customer, Operator, plan.Id, plan.Digest, "fixture");
        }
        public FoundationSignedBackup Sign(FoundationStore store, RSA key)
        {
            var now = DateTimeOffset.FromUnixTimeMilliseconds(DateTimeOffset.UtcNow.ToUnixTimeMilliseconds());
            var packet = store.CreateSignedBackup(Path.Combine(Root, "snapshot.sqlite"), key, "fixture-key", 1, now.AddMinutes(-1), now.AddHours(2));
            File.WriteAllText(Path.Combine(Root, "manifest.json"), JsonSerializer.Serialize(packet.Manifest, new JsonSerializerOptions(JsonSerializerDefaults.Web))); return packet;
        }
        public Dictionary<string, object?> BackupRequest(string operation, FoundationSignedBackup packet, RSA key, string? destination = null) => new()
        {
            ["operation"] = operation, ["customerId"] = Customer, ["cellId"] = Cell, ["snapshotPath"] = packet.Backup.DatabasePath,
            ["manifestPath"] = Path.Combine(Root, "manifest.json"), ["pinnedPublicKeyPem"] = key.ExportSubjectPublicKeyInfoPem(),
            ["pinnedPublicKeySha256"] = FoundationAuditAnchors.PublicKeyFingerprint(key), ["restoreAuthority"] = FoundationBackupSignatures.Authority(packet.Manifest),
            ["destinationDirectory"] = destination
        };
        public async Task<OwnerResult> Run(object? request, string? raw = null, string? output = null, string? existingRequest = null)
        {
            var id = Guid.NewGuid().ToString("N"); var input = existingRequest ?? Path.Combine(Root, id + ".json"); output ??= Path.Combine(Root, id + ".owner-result.json");
            if (existingRequest is null) File.WriteAllText(input, raw ?? JsonSerializer.Serialize(request, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
            var executable = Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "C:\\IT\\Package\\dotnet", "dotnet.exe");
            var artifactBin = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!;
            var dll = Path.Combine(artifactBin.FullName, "SelfService.Foundation.Owner", "release", "SelfService.Foundation.Owner.dll");
            var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = Root };
            info.Environment.Clear(); foreach (var name in new[] { "SystemRoot", "windir", "ComSpec" }) if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
            info.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(executable)!; info.Environment["PATH"] = Path.GetDirectoryName(executable) + ";" + Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32");
            info.Environment["TEMP"] = Root; info.Environment["TMP"] = Root; info.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"; info.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
            foreach (var argument in new[] { dll, "--request", input, "--output", output }) info.ArgumentList.Add(argument);
            using var process = Process.Start(info)!; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            try { await process.WaitForExitAsync(timeout.Token); } catch { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } throw; }
            var text = await stdout; var errors = await stderr;
            using var status = JsonDocument.Parse(text); Assert.False(status.RootElement.GetProperty("externalExecutionAuthorized").GetBoolean());
            Assert.DoesNotContain(Root, text); Assert.DoesNotContain(Customer, text); Assert.Empty(errors);
            var result = default(JsonElement);
            if (File.Exists(output)) { using var body = JsonDocument.Parse(File.ReadAllText(output)); if (body.RootElement.TryGetProperty("result", out var data)) result = data.Clone(); }
            return new(process.ExitCode, status.RootElement.GetProperty("code").GetString()!, text, errors, output, result);
        }
        public void Dispose()
        {
            if (!Path.GetFullPath(Root).StartsWith(Path.GetFullPath(parent) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)) throw new IOException("Fixture boundary rejected.");
            DeleteOwned(Root);
        }
        void DeleteOwned(string directory)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(directory))
            { if (File.GetAttributes(entry).HasFlag(FileAttributes.ReparsePoint)) throw new IOException("Linked fixture path rejected."); if (Directory.Exists(entry)) DeleteOwned(entry); else File.Delete(entry); }
            Directory.Delete(directory, false);
        }
    }
}
