using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationBackupCheckpointAuthorityBoundaryTests
{
    [WindowsCredentialFact]
    public void Fresh_authority_is_private_at_birth_and_cannot_adopt_an_existing_root()
    {
        using var f = new BoundaryRoot(); var beforeParent = SecurityBytes(f.Root); var authority = f.Provision();
        Assert.Null(authority.ReadCurrent()); AssertPrivate(f.AuthorityPath, true);
        Assert.Equal(new[] { ".backup-checkpoint.guard", "authority.json" }, Directory.GetFiles(f.AuthorityPath).Select(Path.GetFileName).Order(StringComparer.Ordinal));
        foreach (var file in Directory.GetFiles(f.AuthorityPath)) AssertPrivate(file, false);
        var identity = authority.DescribeIdentity(); Assert.Equal(f.Customer, identity.CustomerId); Assert.Equal(f.Cell, identity.CellId);
        Assert.Equal(f.KeyId, identity.SignerKeyId); Assert.Equal(f.KeyPin, identity.PublicKeySha256);
        var original = Snapshot(f.AuthorityPath); Assert.Throws<FoundationStoreException>(() => f.Provision());
        Assert.Equal(original, Snapshot(f.AuthorityPath)); Assert.Equal(beforeParent, SecurityBytes(f.Root));
    }

    [Theory]
    [InlineData("empty")]
    [InlineData("unrelated")]
    [InlineData("guard-only")]
    [InlineData("marker-only")]
    [InlineData("checkpoint-only")]
    [InlineData("partial-write")]
    public void Existing_empty_partial_or_unrelated_namespaces_are_preserved_without_adoption_or_repair(string state)
    {
        using var f = new BoundaryRoot(); Directory.CreateDirectory(f.AuthorityPath);
        var name = state switch { "guard-only" => ".backup-checkpoint.guard", "marker-only" => "authority.json",
            "checkpoint-only" => "checkpoint-0001.json", "partial-write" => ".write-synthetic.tmp", _ => "preserved.txt" };
        if (state != "empty") File.WriteAllText(Path.Combine(f.AuthorityPath, name), state == "guard-only" ? "" : "synthetic preserved partial state");
        var before = Snapshot(f.AuthorityPath); Assert.Throws<FoundationStoreException>(() => f.Provision());
        Assert.Throws<FoundationStoreException>(() => f.Reopen()); Assert.Equal(before, Snapshot(f.AuthorityPath));
    }

    [WindowsCredentialFact]
    public void Failure_after_fresh_reservation_preserves_private_partial_state_and_never_implicitly_retries()
    {
        using var f = new BoundaryRoot(); Assert.ThrowsAny<Exception>(() => f.Provision(new ThrowingClock()));
        Assert.True(Directory.Exists(f.AuthorityPath)); AssertPrivate(f.AuthorityPath, true);
        Assert.NotEmpty(Directory.GetFileSystemEntries(f.AuthorityPath));
        var partial = Snapshot(f.AuthorityPath); Assert.Throws<FoundationStoreException>(() => f.Provision());
        Assert.Throws<FoundationStoreException>(() => f.Reopen()); Assert.Equal(partial, Snapshot(f.AuthorityPath));
    }

    public static IEnumerable<object[]> ProtectedNamespaces()
    {
        foreach (var marker in new[] { ".activation.guard", "authority.json", ".backup-checkpoint.guard", ".installation-owner.json",
            ".installation.guard", ".package-owner.json", ".credential-namespace.json", ".vault.lock", ".keys.lock", ".rollback.lock",
            "cell-binding.json", ".foundation-owner-initialization.json", ".credential-recovery-stage.guard", ".credential-recovery-stage.json", ".credential-recovery-checkpoint.guard", ".credential-recovery-checkpoint.json", "legacy.credential.json", "legacy.dpapi.json" })
            foreach (var directory in new[] { false, true }) yield return [marker, directory];
    }

    [Theory]
    [MemberData(nameof(ProtectedNamespaces))]
    public void Protected_ancestor_files_directories_and_partial_markers_cannot_receive_new_checkpoint_authority(string marker, bool directory)
    {
        using var f = new BoundaryRoot(); var parent = f.Child("protected-parent"); Directory.CreateDirectory(parent);
        var deeper = Path.Combine(parent, "existing-deeper-parent"); Directory.CreateDirectory(deeper); var markerPath = Path.Combine(parent, marker);
        if (directory) { Directory.CreateDirectory(markerPath); File.WriteAllText(Path.Combine(markerPath, "preserved.txt"), "synthetic marker contents"); }
        else File.WriteAllText(markerPath, "synthetic marker contents");
        var before = Snapshot(parent); var target = Path.Combine(deeper, "checkpoint");
        Assert.Throws<FoundationStoreException>(() => f.ProvisionAt(target)); Assert.False(Path.Exists(target)); Assert.Equal(before, Snapshot(parent));
    }

    [Theory]
    [InlineData(".backup-checkpoint.guard")]
    [InlineData("AUTHORITY.JSON")]
    [InlineData(".CREDENTIAL-NAMESPACE.JSON")]
    [InlineData(".foundation-owner-initialization.json")]
    [InlineData(".installation-owner.json")]
    [InlineData("legacy.CREDENTIAL.JSON")]
    [InlineData("legacy.DPAPI.JSON")]
    public void Protected_fresh_leaf_aliases_do_not_turn_a_clean_parent_into_an_operational_namespace(string leaf)
    {
        using var f = new BoundaryRoot(); var before = Snapshot(f.Root); var target = f.Child(leaf);
        Assert.Throws<FoundationStoreException>(() => f.ProvisionAt(target)); Assert.False(Path.Exists(target)); Assert.Equal(before, Snapshot(f.Root));
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
    public void Foreign_effective_parent_write_rights_are_rejected_without_modifying_parent_or_creating_a_root(FileSystemRights rights)
    {
        using var f = new BoundaryRoot(); var parent = f.Child("unsafe-parent"); Directory.CreateDirectory(parent);
        AddAccess(parent, rights); File.WriteAllText(Path.Combine(parent, "preserved.txt"), "synthetic preserved parent");
        var before = Snapshot(parent); var target = Path.Combine(parent, "checkpoint");
        Assert.Throws<FoundationStoreException>(() => f.ProvisionAt(target)); Assert.False(Path.Exists(target)); Assert.Equal(before, Snapshot(parent));
    }

    [Theory]
    [InlineData("read-only")]
    [InlineData("inherit-only")]
    [InlineData("deny-only")]
    public void Nonwriting_parent_access_does_not_broaden_new_private_authority_permissions(string kind)
    {
        using var f = new BoundaryRoot(); var parent = f.Child("safe-parent"); Directory.CreateDirectory(parent);
        var acl = new DirectoryInfo(parent).GetAccessControl();
        var foreign = kind == "deny-only" ? new SecurityIdentifier("S-1-5-21-1-2-3-999999") : new SecurityIdentifier(WellKnownSidType.WorldSid, null);
        acl.AddAccessRule(new FileSystemAccessRule(foreign, kind == "read-only" ? FileSystemRights.ReadAndExecute : FileSystemRights.Write,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, kind == "inherit-only" ? PropagationFlags.InheritOnly : PropagationFlags.None,
            kind == "deny-only" ? AccessControlType.Deny : AccessControlType.Allow));
        new DirectoryInfo(parent).SetAccessControl(acl); var before = SecurityBytes(parent); var target = Path.Combine(parent, "checkpoint");
        var authority = f.ProvisionAt(target); Assert.Null(authority.ReadCurrent()); AssertPrivate(target, true);
        foreach (var file in Directory.GetFiles(target)) AssertPrivate(file, false); Assert.Equal(before, SecurityBytes(parent));
    }

    [WindowsCredentialFact]
    public void Null_parent_dacl_is_refused_and_only_the_owned_synthetic_parent_acl_is_restored()
    {
        using var f = new BoundaryRoot(); var parent = f.Child("null-parent"); Directory.CreateDirectory(parent);
        var original = SecurityBytes(parent); var target = Path.Combine(parent, "checkpoint");
        try
        {
            var raw = new RawSecurityDescriptor(original, 0); raw.DiscretionaryAcl = null; var bytes = new byte[raw.BinaryLength]; raw.GetBinaryForm(bytes, 0);
            SetAccessBytes(parent, bytes); Assert.Null(new RawSecurityDescriptor(SecurityBytes(parent), 0).DiscretionaryAcl);
            Assert.Throws<FoundationStoreException>(() => f.ProvisionAt(target)); Assert.False(Path.Exists(target)); Assert.Empty(Directory.GetFileSystemEntries(parent));
            Assert.Null(new RawSecurityDescriptor(SecurityBytes(parent), 0).DiscretionaryAcl);
        }
        finally { SetAccessBytes(parent, original); }
        Assert.Equal(AccessState(original), AccessState(SecurityBytes(parent)));
    }

    [WindowsCredentialFact]
    public void Missing_parent_and_existing_leaf_file_are_never_created_or_replaced()
    {
        using var f = new BoundaryRoot(); var missing = f.Child("missing-parent"); var before = Snapshot(f.Root);
        Assert.Throws<FoundationStoreException>(() => f.ProvisionAt(Path.Combine(missing, "checkpoint")));
        Assert.False(Path.Exists(missing)); Assert.Equal(before, Snapshot(f.Root));
        File.WriteAllText(f.AuthorityPath, "synthetic original file"); var original = File.ReadAllBytes(f.AuthorityPath); var acl = SecurityBytes(f.AuthorityPath);
        Assert.Throws<FoundationStoreException>(() => f.Provision()); Assert.Equal(original, File.ReadAllBytes(f.AuthorityPath)); Assert.Equal(acl, SecurityBytes(f.AuthorityPath));
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(true, false)]
    [InlineData(false, true)]
    [InlineData(true, true)]
    public async Task Direct_or_ancestor_junctions_including_dangling_links_preserve_their_original_targets(bool direct, bool dangling)
    {
        using var f = new BoundaryRoot(); var target = f.Child("junction-target"); Directory.CreateDirectory(target);
        if (!dangling) File.WriteAllText(Path.Combine(target, "preserved.txt"), "synthetic original target");
        var link = f.Child("junction"); await CreateJunction(link, target, f.Root);
        try
        {
            if (dangling) Directory.Delete(target, false); string[] before = dangling ? [] : Snapshot(target);
            Assert.Throws<FoundationStoreException>(() => f.ProvisionAt(direct ? link : Path.Combine(link, "checkpoint")));
            if (dangling) Assert.False(Directory.Exists(target)); else Assert.Equal(before, Snapshot(target));
        }
        finally { Directory.Delete(link, false); }
    }

    [WindowsCredentialFact]
    public async Task Native_provision_race_reserves_one_complete_authority_and_preserves_winner()
    {
        using var f = new BoundaryRoot(); var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = Enumerable.Range(0, 4).Select(_ => Task.Run(async () =>
        {
            await start.Task; try { return f.Provision(); } catch (FoundationStoreException) { return null; }
        })).ToArray(); start.SetResult(); var results = await Task.WhenAll(tasks);
        var winner = Assert.Single(results, a => a is not null)!; var before = Snapshot(f.AuthorityPath);
        Assert.Equal(winner.DescribeIdentity(), f.Reopen().DescribeIdentity()); Assert.Null(f.Reopen().ReadCurrent()); Assert.Equal(before, Snapshot(f.AuthorityPath));
    }

    [Theory]
    [InlineData("customer")]
    [InlineData("cell")]
    [InlineData("signer-key-id")]
    [InlineData("signer-fingerprint")]
    public void Existing_authority_identity_cannot_be_rebound_to_another_scope_or_signer(string changed)
    {
        using var f = new BoundaryRoot(); f.Provision(); var before = Snapshot(f.AuthorityPath);
        Assert.Throws<FoundationStoreException>(() => new FoundationBackupCheckpointAuthority(f.AuthorityPath,
            changed == "customer" ? "other-customer" : f.Customer, changed == "cell" ? "other-cell" : f.Cell,
            changed == "signer-key-id" ? "rotated-backup-key" : f.KeyId, changed == "signer-fingerprint" ? Hash("another-signer") : f.KeyPin, f.Clock));
        Assert.Equal(before, Snapshot(f.AuthorityPath));
    }

    [Theory]
    [InlineData("root")]
    [InlineData("authority.json")]
    [InlineData(".backup-checkpoint.guard")]
    public void Widened_existing_custody_is_refused_without_permissions_repair(string entry)
    {
        using var f = new BoundaryRoot(); var authority = f.Provision(); var path = entry == "root" ? f.AuthorityPath : Path.Combine(f.AuthorityPath, entry);
        AddAccess(path, FileSystemRights.ReadAndExecute); var before = Snapshot(f.AuthorityPath);
        Assert.Throws<FoundationStoreException>(() => f.Reopen()); Assert.Throws<FoundationStoreException>(() => authority.ReadCurrent());
        Assert.Equal(before, Snapshot(f.AuthorityPath));
    }

    [Theory]
    [InlineData("unknown-field")]
    [InlineData("duplicate-field")]
    [InlineData("case-alias")]
    [InlineData("missing-hash")]
    [InlineData("wrong-customer")]
    [InlineData("truncated")]
    [InlineData("oversized")]
    public void Marker_metadata_tampering_cannot_be_repaired_or_adopted_on_reopen(string mutation)
    {
        using var f = new BoundaryRoot(); var authority = f.Provision(); var path = Path.Combine(f.AuthorityPath, "authority.json");
        var original = File.ReadAllText(path); var json = JsonNode.Parse(original)!.AsObject();
        switch (mutation)
        {
            case "unknown-field": json["unreviewed"] = "synthetic"; break;
            case "case-alias": json["Schema"] = 1; break;
            case "missing-hash": json.Remove("sha256"); break;
            case "wrong-customer": json["customerId"] = "other-customer"; break;
        }
        var text = mutation switch { "duplicate-field" => "{\"schema\":1," + original.TrimStart()[1..], "truncated" => "{\"schema\":",
            "oversized" => original + new string(' ', FoundationBackupCheckpointAuthority.MaximumMetadataBytes), _ => json.ToJsonString() };
        File.WriteAllText(path, text); var before = Snapshot(f.AuthorityPath);
        Assert.Throws<FoundationStoreException>(() => f.Reopen()); Assert.Throws<FoundationStoreException>(() => authority.ReadCurrent());
        Assert.Equal(before, Snapshot(f.AuthorityPath));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Unknown_file_or_directory_blocks_the_entire_authority_without_cleanup(bool directory)
    {
        using var f = new BoundaryRoot(); var authority = f.Provision(); var path = Path.Combine(f.AuthorityPath, "unreviewed.json");
        if (directory) Directory.CreateDirectory(path); else WritePrivate(path, "synthetic unknown entry");
        var before = Snapshot(f.AuthorityPath); Assert.Throws<FoundationStoreException>(() => f.Reopen());
        Assert.Throws<FoundationStoreException>(() => authority.ReadCurrent()); Assert.Equal(before, Snapshot(f.AuthorityPath));
    }

    [WindowsCredentialFact]
    public void Copied_authority_is_not_adopted_under_a_different_absolute_root_path()
    {
        using var f = new BoundaryRoot(); f.Provision(); var copy = f.Child("copied-authority"); Directory.CreateDirectory(copy); Protect(copy, true);
        foreach (var path in Directory.GetFiles(f.AuthorityPath)) { var target = Path.Combine(copy, Path.GetFileName(path)); File.Copy(path, target); Protect(target, false); }
        var before = Snapshot(copy); Assert.Throws<FoundationStoreException>(() => f.ReopenAt(copy)); Assert.Equal(before, Snapshot(copy));
    }

    [Theory]
    [InlineData("bytes")]
    [InlineData("schema")]
    [InlineData("audit")]
    [InlineData("customer")]
    [InlineData("cell")]
    [InlineData("key-id")]
    [InlineData("manifest-pin")]
    [InlineData("wrong-public-key")]
    public void Rejected_candidates_never_append_a_head_or_change_customer_audit_or_source_bytes(string changed)
    {
        using var b = new SignedBackupFixture(); using var f = For(b); var authority = f.Provision(); var candidate = b.Create();
        if (changed == "bytes") { var bytes = File.ReadAllBytes(candidate.Backup.DatabasePath); bytes[0] ^= 1; File.WriteAllBytes(candidate.Backup.DatabasePath, bytes); }
        if (changed is "schema" or "audit")
        {
            using (var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = candidate.Backup.DatabasePath, Pooling = false }.ToString()))
            {
                db.Open(); using var command = db.CreateCommand();
                command.CommandText = "SELECT sql FROM sqlite_master WHERE name='audit_no_update'"; var guard = (string)command.ExecuteScalar()!;
                command.CommandText = changed == "schema" ? "DROP TABLE outbox" : "DROP TRIGGER audit_no_update; UPDATE audit SET outcome='SyntheticTamper' WHERE sequence=1; " + guard;
                command.ExecuteNonQuery();
            }
            var bytes = File.ReadAllBytes(candidate.Backup.DatabasePath); var hash = Convert.ToHexStringLower(SHA256.HashData(bytes));
            candidate = new(candidate.Backup with { Sha256 = hash, Length = bytes.Length }, b.Sign(candidate.Manifest with { SnapshotSha256 = hash, SnapshotLength = bytes.Length }));
        }
        if (changed is "customer" or "cell" or "key-id") candidate = candidate with { Manifest = b.Sign(candidate.Manifest with
        { CustomerId = changed == "customer" ? "other-customer" : candidate.Manifest.CustomerId,
            CellId = changed == "cell" ? "other-cell" : candidate.Manifest.CellId, KeyId = changed == "key-id" ? "rotated-key" : candidate.Manifest.KeyId }) };
        using var key = changed == "wrong-public-key" ? RSA.Create(2048) : b.PublicKey();
        var manifestPin = changed == "manifest-pin" ? Hash("not-the-reviewed-manifest") : FoundationBackupSignatures.Authority(candidate.Manifest).ManifestSha256;
        var before = Snapshot(f.AuthorityPath); var source = File.ReadAllBytes(candidate.Backup.DatabasePath); var audit = b.Cell.Store.ListAudit(b.Cell.Customer, "auditor").Count;
        Assert.Throws<FoundationStoreException>(() => authority.AcceptNext(candidate, key, manifestPin, 0, null));
        Assert.Null(authority.ReadCurrent()); Assert.Equal(before, Snapshot(f.AuthorityPath)); Assert.Equal(source, File.ReadAllBytes(candidate.Backup.DatabasePath));
        Assert.Equal(audit, b.Cell.Store.ListAudit(b.Cell.Customer, "auditor").Count);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(10)]
    public void Old_or_reused_backup_generation_cannot_advance_an_existing_checkpoint(long candidateGeneration)
    {
        using var b = new SignedBackupFixture(); using var f = For(b); var authority = f.Provision(); using var key = b.PublicKey();
        var first = Accept(authority, b.Create(10), key); var next = b.Create(candidateGeneration); var before = Snapshot(f.AuthorityPath);
        Assert.Throws<FoundationStoreException>(() => Accept(authority, next, key, first)); Assert.Equal(first, authority.ReadCurrent()); Assert.Equal(before, Snapshot(f.AuthorityPath));
    }

    [WindowsCredentialFact]
    public async Task Exact_cas_race_appends_one_next_checkpoint_and_stale_retry_never_reexecutes_admission()
    {
        using var b = new SignedBackupFixture(); using var f = For(b); var authority = f.Provision(); using var key = b.PublicKey();
        var first = Accept(authority, b.Create(), key); var candidates = new[] { b.Create(2), b.Create(3) };
        var audit = b.Cell.Store.ListAudit(b.Cell.Customer, "auditor").Count; var source = candidates.Select(c => File.ReadAllBytes(c.Backup.DatabasePath)).ToArray();
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var tasks = candidates.Select(candidate => Task.Run(async () =>
        { await start.Task; using var ownKey = b.PublicKey(); try { return Accept(authority, candidate, ownKey, first); } catch (FoundationStoreException) { return null; } })).ToArray();
        start.SetResult(); var outcomes = await Task.WhenAll(tasks); var accepted = Assert.Single(outcomes, head => head is not null)!;
        Assert.Equal(2, accepted.Sequence); Assert.Equal(first.Sha256, accepted.PreviousSha256); Assert.Equal(accepted, authority.ReadCurrent());
        var beforeRetry = Snapshot(f.AuthorityPath); Assert.Throws<FoundationStoreException>(() => Accept(authority, candidates[0], key, first));
        Assert.Equal(beforeRetry, Snapshot(f.AuthorityPath)); Assert.Equal(audit, b.Cell.Store.ListAudit(b.Cell.Customer, "auditor").Count);
        for (var i = 0; i < candidates.Length; i++) Assert.Equal(source[i], File.ReadAllBytes(candidates[i].Backup.DatabasePath));
    }

    [Theory]
    [InlineData("gap")]
    [InlineData("truncated-record")]
    [InlineData("wrong-record-hash")]
    public void Gapped_or_corrupt_checkpoint_history_is_refused_without_truncation_or_repair(string corruption)
    {
        using var b = new SignedBackupFixture(); using var f = For(b); var authority = f.Provision(); using var key = b.PublicKey();
        var first = Accept(authority, b.Create(), key); Accept(authority, b.Create(2), key, first);
        var firstPath = Path.Combine(f.AuthorityPath, "checkpoint-0001.json"); var lastPath = Path.Combine(f.AuthorityPath, "checkpoint-0002.json");
        if (corruption == "gap") File.Delete(firstPath);
        else if (corruption == "truncated-record") File.WriteAllText(lastPath, "{\"schema\":1,");
        else { var json = JsonNode.Parse(File.ReadAllText(lastPath))!; json["checkpoint"]!["sha256"] = Hash("incorrect-record"); File.WriteAllText(lastPath, json.ToJsonString()); }
        var before = Snapshot(f.AuthorityPath); Assert.Throws<FoundationStoreException>(() => authority.ReadCurrent()); Assert.Throws<FoundationStoreException>(() => f.Reopen());
        Assert.Equal(before, Snapshot(f.AuthorityPath));
    }

    [WindowsCredentialFact]
    public void Accepted_head_is_recoverable_after_caller_loses_the_return_and_original_cas_cannot_be_retried()
    {
        using var b = new SignedBackupFixture(); using var f = For(b); var authority = f.Provision(); using var key = b.PublicKey(); var candidate = b.Create();
        _ = Accept(authority, candidate, key); // Deliberately discard caller result; this is not a process/power-loss qualification.
        var observed = f.Reopen().ReadCurrent(); Assert.NotNull(observed); Assert.Equal(FoundationBackupSignatures.Authority(candidate.Manifest), observed.RestoreAuthority);
        var before = Snapshot(f.AuthorityPath); Assert.Throws<FoundationStoreException>(() => Accept(authority, candidate, key)); Assert.Equal(before, Snapshot(f.AuthorityPath));
    }

    [WindowsCredentialFact]
    public void Postappend_observation_failure_is_uncertain_and_preserves_the_accepted_head_without_automatic_retry()
    {
        using var b = new SignedBackupFixture(); using var f = For(b);
        var authority = f.Provision(new RefuseAfterAppendClock(f.Clock, Path.Combine(f.AuthorityPath, "checkpoint-0001.json")));
        using var key = b.PublicKey(); var candidate = b.Create(); var source = File.ReadAllBytes(candidate.Backup.DatabasePath);
        var audit = b.Cell.Store.ListAudit(b.Cell.Customer, "auditor").Count;
        Assert.Equal("BackupCheckpointAppendUncertain", Assert.Throws<FoundationStoreException>(() => Accept(authority, candidate, key)).Code);
        var observed = f.Reopen().ReadCurrent(); Assert.NotNull(observed); Assert.Equal(1, observed.Sequence);
        Assert.Equal(FoundationBackupSignatures.Authority(candidate.Manifest), observed.RestoreAuthority);
        var beforeRetry = Snapshot(f.AuthorityPath); Assert.Throws<FoundationStoreException>(() => Accept(f.Reopen(), candidate, key));
        Assert.Equal(beforeRetry, Snapshot(f.AuthorityPath)); Assert.Equal(source, File.ReadAllBytes(candidate.Backup.DatabasePath));
        Assert.Equal(audit, b.Cell.Store.ListAudit(b.Cell.Customer, "auditor").Count);
    }

    [WindowsCredentialFact]
    public void History_entry_and_expected_sequence_limits_refuse_incomplete_or_overflowing_admission()
    {
        using var b = new SignedBackupFixture(); using var f = For(b); var authority = f.Provision(); using var key = b.PublicKey(); var candidate = b.Create();
        var before = Snapshot(f.AuthorityPath);
        Assert.Throws<FoundationStoreException>(() => authority.AcceptNext(candidate, key, FoundationBackupSignatures.Authority(candidate.Manifest).ManifestSha256,
            FoundationBackupCheckpointAuthority.MaximumCheckpoints, Hash("expected-limit-head")));
        Assert.Equal(before, Snapshot(f.AuthorityPath));
        for (var i = 1; i <= FoundationBackupCheckpointAuthority.MaximumCheckpoints + 1; i++) WritePrivate(Path.Combine(f.AuthorityPath, "checkpoint-" + i.ToString("D4") + ".json"), "{}");
        var oversized = Snapshot(f.AuthorityPath); Assert.Throws<FoundationStoreException>(() => authority.ReadCurrent()); Assert.Equal(oversized, Snapshot(f.AuthorityPath));
    }

    static FoundationBackupCheckpoint Accept(FoundationBackupCheckpointAuthority authority, FoundationSignedBackup candidate, RSA publicKey, FoundationBackupCheckpoint? expected = null) =>
        authority.AcceptNext(candidate, publicKey, FoundationBackupSignatures.Authority(candidate.Manifest).ManifestSha256, expected?.Sequence ?? 0, expected?.Sha256);
    static BoundaryRoot For(SignedBackupFixture b) => new(b.Cell.Customer, b.Cell.Cell, FoundationAuditAnchors.PublicKeyFingerprint(b.Key), b.Cell.Clock);

    static byte[] SecurityBytes(string path) => (Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorBinaryForm();
    static void SetAccessBytes(string path, byte[] bytes)
    { var acl = new DirectorySecurity(); acl.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access); new DirectoryInfo(path).SetAccessControl(acl); }
    static (string?, string?, bool, string) AccessState(byte[] bytes)
    {
        var raw = new RawSecurityDescriptor(bytes, 0); var acl = new byte[raw.DiscretionaryAcl!.BinaryLength]; raw.DiscretionaryAcl.GetBinaryForm(acl, 0);
        return (raw.Owner?.Value, raw.Group?.Value, (raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, Convert.ToHexString(acl));
    }
    static void AddAccess(string path, FileSystemRights rights)
    {
        var acl = Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), rights, AccessControlType.Allow));
        if (Directory.Exists(path)) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)acl); else new FileInfo(path).SetAccessControl((FileSecurity)acl);
    }
    static void Protect(string path, bool directory)
    {
        using var identity = WindowsIdentity.GetCurrent(); FileSystemSecurity acl = directory ? new DirectorySecurity() : new FileSecurity();
        acl.SetOwner(identity.User!); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl,
            directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)acl); else new FileInfo(path).SetAccessControl((FileSecurity)acl);
    }
    static void AssertPrivate(string path, bool directory)
    {
        using var identity = WindowsIdentity.GetCurrent(); var acl = directory ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected); Assert.Equal(identity.User, acl.GetOwner(typeof(SecurityIdentifier)));
        Assert.All(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule =>
        { Assert.Equal(identity.User!.Value, rule.IdentityReference.Value); Assert.Equal(AccessControlType.Allow, rule.AccessControlType); Assert.True((rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl); });
    }
    static string[] Snapshot(string root) => new[] { root }.Concat(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(SecurityBytes(path))) + ":" +
            (Directory.Exists(path) ? "directory" : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))))).ToArray();
    static void WritePrivate(string path, string value) { File.WriteAllText(path, value); Protect(path, false); }
    static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    static async Task CreateJunction(string link, string target, string root)
    {
        var executable = Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32", "cmd.exe");
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear(); foreach (var name in new[] { "SystemRoot", "WINDIR" }) if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; _ = process.SafeHandle; var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(deadline.Token); Assert.Equal(0, process.ExitCode); _ = await output; _ = await errors; }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }

    sealed class BoundaryRoot : IDisposable
    {
        public string Root { get; } = Path.Combine(Path.GetTempPath(), "studio-backup-checkpoint-boundaries-" + Guid.NewGuid().ToString("N"));
        public string AuthorityPath => Child("checkpoint-authority");
        public string Customer { get; }
        public string Cell { get; }
        public string KeyId => "backup-key";
        public string KeyPin { get; }
        public TimeProvider Clock { get; }
        public BoundaryRoot(string customer = "customer-a", string cell = "cell-a", string? pin = null, TimeProvider? clock = null)
        {
            Customer = customer; Cell = cell; KeyPin = pin ?? Hash("synthetic-signer-pin"); Clock = clock ?? new StoreClock();
            ActivationProvisionFixtureRoots.Create(Root);
        }
        public string Child(string name) => Path.Combine(Root, name);
        public FoundationBackupCheckpointAuthority Provision(TimeProvider? clock = null) => ProvisionAt(AuthorityPath, clock);
        public FoundationBackupCheckpointAuthority ProvisionAt(string path, TimeProvider? clock = null) => FoundationBackupCheckpointAuthority.Provision(path, Customer, Cell, KeyId, KeyPin, clock ?? Clock);
        public FoundationBackupCheckpointAuthority Reopen() => ReopenAt(AuthorityPath);
        public FoundationBackupCheckpointAuthority ReopenAt(string path) => new(path, Customer, Cell, KeyId, KeyPin, Clock);
        public void Dispose()
        {
            var temporary = Path.TrimEndingDirectorySeparator(Path.GetFullPath(Path.GetTempPath())) + Path.DirectorySeparatorChar;
            if (!Path.GetFullPath(Root).StartsWith(temporary, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(Root)[^32..], "N", out _))
                throw new IOException("Synthetic checkpoint cleanup boundary rejected.");
            Delete(Root);
        }
        static void Delete(string path)
        {
            foreach (var entry in Directory.EnumerateFileSystemEntries(path))
            { if ((File.GetAttributes(entry) & FileAttributes.ReparsePoint) != 0) throw new IOException("Synthetic linked cleanup refused."); if (Directory.Exists(entry)) Delete(entry); else File.Delete(entry); }
            Directory.Delete(path, false);
        }
    }

    sealed class ThrowingClock : TimeProvider { public override DateTimeOffset GetUtcNow() => throw new IOException("Synthetic private clock failure."); }
    sealed class RefuseAfterAppendClock(TimeProvider source, string checkpointPath) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => File.Exists(checkpointPath)
            ? throw new IOException("Synthetic postappend observation failure.") : source.GetUtcNow();
    }
}
