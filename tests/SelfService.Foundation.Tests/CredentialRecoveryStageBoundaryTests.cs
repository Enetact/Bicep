using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialRecoveryStageBoundaryTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false, false)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Fresh_output_preserves_original_full_profile_including_fractional_expiry_and_never_opens_as_a_live_vault(bool strong, bool revoked)
    {
        using var f = new StageFixture(strong); var expires = f.Cell.Clock.Now.AddHours(1).AddTicks(1234);
        await f.Put(expires); if (revoked) await f.Vault.RevokeAsync(f.Cell.Scope.AuthProfileId, 1);
        await f.Select(); var before = f.SourceSnapshot(); var original = Profile(f.SourceFile);
        var receipt = await f.Stage();
        Assert.Equal(before, f.SourceSnapshot()); Assert.Equal(f.Plan.Sha256, receipt.PlanSha256); Assert.Equal(f.Set.Sha256, receipt.SourceSetSha256);
        Assert.Equal(f.Plan.DestinationRootCommitmentSha256, receipt.DestinationRootCommitmentSha256); Assert.Equal(f.Destination, receipt.DestinationKey);
        var staged = Assert.Single(receipt.Profiles); var destination = Path.Combine(f.StagePath, "profiles", staged.Destination.ProfileIdSha256 + ".credential.json");
        Assert.Equal(original.ToJsonString(), Profile(destination).ToJsonString());
        Assert.Equal(expires, Profile(destination)["expiresUtc"]!.GetValue<DateTimeOffset>());
        Assert.Equal(!revoked, staged.Destination.Enabled); Assert.Equal(revoked ? 2 : 1, staged.Destination.Generation);
        Assert.Equal("key-1", staged.Source.SourceKeyId); Assert.Equal("key-2", staged.Destination.SourceKeyId);
        Assert.NotEqual(staged.Source.EnvelopeSha256, staged.Destination.EnvelopeSha256);
        Assert.Equal(Assert.Single(f.Plan.PreservedProfiles).ProfileSha256, staged.PreservedProfileSha256);
        Assert.Equal(Hash(JsonSerializer.SerializeToUtf8Bytes(Profile(destination).Deserialize<CredentialProfile>(Json), Json)), staged.PreservedProfileSha256);
        AssertPrivate(f.StagePath, true);
        foreach (var path in Directory.EnumerateFileSystemEntries(f.StagePath, "*", SearchOption.AllDirectories)) AssertPrivate(path, Directory.Exists(path));
        Assert.False(File.Exists(Path.Combine(f.StagePath, ".credential-namespace.json")));
        Assert.Equal("credential-recovery-stage-binding", Read(Path.Combine(f.StagePath, "rollback", "binding.json"))["kind"]!.GetValue<string>());
        Assert.Equal("credential-recovery-stage-state", Read(Path.Combine(f.StagePath, "rollback", "state.json"))["kind"]!.GetValue<string>());
        AssertConservative(receipt);
        var publicJson = JsonSerializer.Serialize(new { plan = f.Plan, receipt }, Json);
        foreach (var forbidden in new[] { CredentialFixture.Token, f.Cell.Root, f.Cell.Scope.AuthProfileId, Convert.ToBase64String(f.DestKeys.Values["key-2"]) })
            Assert.DoesNotContain(forbidden, publicJson, StringComparison.OrdinalIgnoreCase);
        Assert.Throws<NotSupportedException>(() => ((IList<CredentialRecoveryStagedProfile>)receipt.Profiles).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<CredentialRecoveryPreservedProfile>)f.Plan.PreservedProfiles).Clear());
        var outputBefore = Snapshot(f.StagePath); f.DestKeys.Requests.Clear();
        foreach (var root in new[] { f.StagePath, Path.Combine(f.StagePath, "profiles"), Path.Combine(f.StagePath, "rollback") })
        {
            var options = f.Cell.Options with { RootPath = root, EncryptionKeyId = "key-2" };
            Assert.Throws<CredentialException>(() => new CredentialVault(options, f.DestKeys, f.Cell.Clock));
            Assert.Throws<CredentialException>(() => CredentialVault.Provision(options, f.DestKeys, f.Cell.Clock));
        }
        Assert.Empty(f.DestKeys.Requests); Assert.Equal(outputBefore, Snapshot(f.StagePath));
    }

    [WindowsCredentialFact]
    public async Task Expired_source_stays_expired_and_staging_does_not_renew_a_credential()
    {
        using var f = new StageFixture(); var expires = f.Cell.Clock.Now.AddMinutes(1).AddTicks(4567);
        await f.Put(expires); f.Cell.Clock.Now = f.Cell.Clock.Now.AddMinutes(2); await f.Select();
        var receipt = await f.Stage(); var profile = Assert.Single(receipt.Profiles);
        Assert.True(profile.Destination.Enabled); Assert.True(profile.Destination.ExpiresUnixMilliseconds < receipt.CompletedUtc.ToUnixTimeMilliseconds());
        Assert.Equal(expires, Profile(Assert.Single(Directory.GetFiles(Path.Combine(f.StagePath, "profiles"))))["expiresUtc"]!.GetValue<DateTimeOffset>());
    }

    [WindowsCredentialFact]
    public async Task Source_generation_zero_absence_is_preserved_without_inventing_an_envelope()
    {
        using var f = new StageFixture(strong: true); var absentScope = f.Cell.Scope with { AuthProfileId = "profile/never-published" };
        Fault(f.Vault, phase => { if (phase == "PendingPersisted") throw new IOException("Synthetic interrupted publication."); });
        try { using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token); await Assert.ThrowsAsync<IOException>(() => f.Vault.PutAsync(absentScope, secret, f.Cell.Clock.Now.AddHours(1))); }
        finally { Fault(f.Vault, null); }
        var pending = await f.Vault.InspectRollbackStateAsync(absentScope.AuthProfileId);
        await f.Vault.ReconcilePendingWriteAsync(absentScope.AuthProfileId, pending.AuthorityRevision, pending.AuthorityStateSha256,
            pending.Pending!.PendingSha256, CredentialRollbackResolution.AbandonUnpublished);
        await f.Put(); await f.Select(); var before = f.SourceSnapshot(); var receipt = await f.Stage();
        var absentHash = Hash(Encoding.UTF8.GetBytes(absentScope.AuthProfileId)); Assert.Equal(absentHash, Assert.Single(receipt.AbsentProfileIds));
        Assert.Equal(f.Plan.AbsentProfileIds, receipt.AbsentProfileIds); Assert.Single(receipt.Profiles);
        Assert.False(File.Exists(Path.Combine(f.StagePath, "profiles", absentHash + ".credential.json")));
        var state = Read(Path.Combine(f.StagePath, "rollback", "state.json"));
        var absence = state["entries"]!.AsArray().Single(n => n!["profileIdSha256"]!.GetValue<string>() == absentHash)!;
        Assert.Equal(0, absence["generation"]!.GetValue<long>()); Assert.Equal("", absence["envelopeSha256"]!.GetValue<string>()); Assert.Equal(0, absence["envelopeLength"]!.GetValue<long>());
        Assert.Equal(before, f.SourceSnapshot());
    }

    [Theory]
    [InlineData("empty")] [InlineData("malformed")] [InlineData("different")]
    public async Task Exact_separately_reviewed_plan_hash_is_required_before_any_key_or_output(string attack)
    {
        using var f = new StageFixture(); await f.Put(); await f.Select(); var before = Snapshot(f.Cell.Root);
        f.ClearRequests(); var pin = attack switch { "empty" => "", "malformed" => new string('A', 64), _ => new string('9', 64) };
        Assert.Equal("credential-recovery-stage-plan-mismatch", (await Assert.ThrowsAsync<CredentialException>(() => f.Stage(pin))).Code);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.DestKeys.Requests); Assert.False(Path.Exists(f.StagePath)); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData("path")] [InlineData("purpose")] [InlineData("key")] [InlineData("expiry")]
    public async Task Replanning_a_changed_destination_or_purpose_does_not_reuse_the_old_approval_pin(string field)
    {
        using var f = new StageFixture(); await f.Put(); await f.Select(); var original = f.Plan;
        var key = f.Destination; if (field == "key") key = key with { KeyId = "key-3" };
        var plan = CredentialRecoveryStaging.PreparePlan(f.Selected, f.Authority, f.Identity,
            field == "path" ? Path.Combine(f.Cell.Root, "other-stage") : f.StagePath, key,
            field == "purpose" ? "Different reviewed recovery purpose" : StageFixture.Purpose,
            f.Cell.Clock.Now.AddMinutes(field == "expiry" ? 9 : 10), f.Cell.Clock);
        Assert.NotEqual(original.Sha256, plan.Sha256); f.ClearRequests(); var before = Snapshot(f.Cell.Root);
        await Assert.ThrowsAsync<CredentialException>(() => new CredentialRecoveryStageExecutor(f.Cell.Keys, f.DestKeys, f.Cell.Clock).StageAsync(f.Set, plan, original.Sha256, f.Signer));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.DestKeys.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData("customer")] [InlineData("cell")] [InlineData("namespace")] [InlineData("authority")] [InlineData("key-customer")] [InlineData("key-cell")]
    public async Task Planning_refuses_cross_scope_source_selection_and_destination_key_identity(string field)
    {
        using var f = new StageFixture(strong: true); await f.Put(); await f.Select(); var identity = f.Identity; var authority = f.Authority; var key = f.Destination;
        if (field == "customer") identity = identity with { CustomerId = Guid.NewGuid().ToString() };
        if (field == "cell") identity = identity with { CellId = Guid.NewGuid().ToString() };
        if (field == "namespace") identity = identity with { VaultNamespaceId = new string('8', 32) };
        if (field == "authority") authority = authority with { ArtifactSha256 = new string('8', 64) };
        if (field == "key-customer") key = key with { CustomerId = Guid.NewGuid().ToString() };
        if (field == "key-cell") key = key with { CellId = Guid.NewGuid().ToString() };
        var before = Snapshot(f.Cell.Root); f.ClearRequests();
        Assert.Throws<CredentialException>(() => CredentialRecoveryStaging.PreparePlan(f.Selected, authority, identity, f.StagePath, key, StageFixture.Purpose, f.Cell.Clock.Now.AddMinutes(10), f.Cell.Clock));
        Assert.Equal(before, Snapshot(f.Cell.Root)); Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.DestKeys.Requests);
    }

    [Theory]
    [InlineData("expired")] [InlineData("rewind")] [InlineData("different-set")] [InlineData("wrong-signer")]
    public async Task Plan_and_exact_selected_set_must_still_be_current_before_key_access(string attack)
    {
        using var f = new StageFixture(); await f.Put(); await f.Select(); using var other = await f.Capture(2); using var wrongSigner = RSA.Create(2048);
        if (attack == "expired") f.Cell.Clock.Now = f.Plan.ExpiresUtc;
        if (attack == "rewind") f.Cell.Clock.Now = f.Plan.PreparedUtc.AddMilliseconds(-1);
        var before = Snapshot(f.Cell.Root); f.ClearRequests();
        await Assert.ThrowsAsync<CredentialException>(() => new CredentialRecoveryStageExecutor(f.Cell.Keys, f.DestKeys, f.Cell.Clock)
            .StageAsync(attack == "different-set" ? other : f.Set, f.Plan, f.Plan.Sha256, attack == "wrong-signer" ? wrongSigner : f.Signer));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.DestKeys.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [WindowsCredentialFact]
    public async Task Destination_material_rotation_under_the_same_key_id_is_refused_without_reserving_output()
    {
        using var f = new StageFixture(); await f.Put(); await f.Select(); f.DestKeys.Values["key-2"] = RandomNumberGenerator.GetBytes(32);
        var before = Snapshot(f.Cell.Root); f.ClearRequests();
        Assert.Equal("credential-recovery-stage-destination-key-changed", (await Assert.ThrowsAsync<CredentialException>(() => f.Stage())).Code);
        Assert.Single(f.DestKeys.Requests); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, Snapshot(f.Cell.Root)); Assert.False(Path.Exists(f.StagePath));
    }

    [WindowsCredentialFact]
    public async Task Destination_handle_is_fetched_once_and_source_callback_cannot_retarget_it_after_the_reviewed_fetch()
    {
        using var f = new StageFixture(); await f.Put(); await f.Select(); var originalKey = f.DestKeys.Values["key-2"].ToArray(); f.ClearRequests();
        f.Cell.Keys.OnGet = () => f.DestKeys.Values["key-2"] = RandomNumberGenerator.GetBytes(32);
        CredentialRecoveryStageReceipt receipt; try { receipt = await f.Stage(); } finally { f.Cell.Keys.OnGet = null; }
        Assert.Single(f.DestKeys.Requests); Assert.NotEmpty(f.Cell.Keys.Requests); Assert.Equal(f.Destination, receipt.DestinationKey);
        Assert.NotEqual(originalKey, f.DestKeys.Values["key-2"]);
        // Independent AES assertion uses the original key and the exact preserved public-profile AAD, never a live vault constructor.
        var envelope = Read(Assert.Single(Directory.GetFiles(Path.Combine(f.StagePath, "profiles"))));
        using var aes = new AesGcm(originalKey, 16); var ciphertext = Convert.FromBase64String(envelope["ciphertext"]!.GetValue<string>()); var plaintext = new byte[ciphertext.Length];
        try
        {
            aes.Decrypt(Convert.FromBase64String(envelope["nonce"]!.GetValue<string>()), ciphertext,
                Convert.FromBase64String(envelope["tag"]!.GetValue<string>()), plaintext, Aad(envelope));
            Assert.Equal(CredentialFixture.Token, Encoding.UTF8.GetString(plaintext));
        }
        finally { CryptographicOperations.ZeroMemory(originalKey); CryptographicOperations.ZeroMemory(plaintext); CryptographicOperations.ZeroMemory(ciphertext); }
    }

    [WindowsCredentialFact]
    public async Task Source_authentication_failure_is_whole_operation_failure_without_a_reserved_root()
    {
        using var f = new StageFixture(); await f.Put(); await f.Select(); f.Cell.Keys.Values["key-1"] = RandomNumberGenerator.GetBytes(32);
        var before = Snapshot(f.Cell.Root); await Assert.ThrowsAsync<CredentialException>(() => f.Stage());
        Assert.Equal(before, Snapshot(f.Cell.Root)); Assert.False(Path.Exists(f.StagePath));
    }

    [Theory]
    [InlineData("empty-directory")] [InlineData("file")] [InlineData("partial-hold")]
    public async Task Destination_appearing_after_planning_is_never_adopted_overwritten_or_repaired(string shape)
    {
        using var f = new StageFixture(); await f.Put(); await f.Select();
        if (shape == "file") File.WriteAllText(f.StagePath, "Existing customer bytes.");
        else { Directory.CreateDirectory(f.StagePath); if (shape == "partial-hold") File.WriteAllText(Path.Combine(f.StagePath, ".credential-recovery-stage.guard"), "Preserved unknown owner state."); }
        var before = Snapshot(f.Cell.Root); f.ClearRequests(); await Assert.ThrowsAsync<CredentialException>(() => f.Stage());
        Assert.Equal(before, Snapshot(f.Cell.Root)); Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.DestKeys.Requests);
    }

    [Theory]
    [InlineData(".credential-namespace.json")] [InlineData(".vault.lock")] [InlineData(".keys.lock")] [InlineData(".rollback.lock")]
    [InlineData("authority.json")] [InlineData(".activation.guard")] [InlineData(".backup-checkpoint.guard")]
    [InlineData(".installation-owner.json")] [InlineData(".installation.guard")] [InlineData(".package-owner.json")]
    [InlineData("cell-binding.json")] [InlineData(".foundation-owner-initialization.json")]
    [InlineData("legacy.credential.json")] [InlineData("legacy.dpapi.json")]
    [InlineData(".CREDENTIAL-RECOVERY-STAGE.GUARD")] [InlineData(".credential-recovery-stage.json")]
    public async Task Protected_ancestor_and_leaf_names_are_refused_without_creating_entries(string marker)
    {
        using var f = new StageFixture(); await f.Put(); await f.Select(); var parent = Path.Combine(f.Cell.Root, "protected-parent"); Directory.CreateDirectory(parent);
        // Marker-only partial namespaces are protected even when the marker is a directory or invalid JSON.
        Directory.CreateDirectory(Path.Combine(parent, marker)); var before = Snapshot(f.Cell.Root);
        Assert.Throws<CredentialException>(() => f.Replan(Path.Combine(parent, "new-stage")));
        Assert.Throws<CredentialException>(() => f.Replan(Path.Combine(f.Cell.Root, marker)));
        Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData(FileSystemRights.Write)] [InlineData(FileSystemRights.DeleteSubdirectoriesAndFiles)] [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.ChangePermissions)] [InlineData(FileSystemRights.TakeOwnership)]
    public async Task Immediate_parent_foreign_write_custody_is_refused_and_not_repaired(FileSystemRights rights)
    {
        using var f = new StageFixture(); await f.Put(); await f.Select(); var parent = Path.Combine(f.Cell.Root, "unsafe-parent"); Directory.CreateDirectory(parent);
        var original = SecurityBytes(parent); var acl = new DirectoryInfo(parent).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), rights, AccessControlType.Allow)); new DirectoryInfo(parent).SetAccessControl(acl);
        try
        {
            var before = Snapshot(f.Cell.Root); Assert.Throws<CredentialException>(() => f.Replan(Path.Combine(parent, "new-stage"))); Assert.Equal(before, Snapshot(f.Cell.Root));
        }
        finally { RestoreAccess(parent, original); }
    }

    [WindowsCredentialFact]
    public async Task Raw_null_parent_DACL_is_refused_without_tightening_or_creating_a_child()
    {
        using var f = new StageFixture(); await f.Put(); await f.Select(); var parent = Path.Combine(f.Cell.Root, "null-parent"); Directory.CreateDirectory(parent); var original = SecurityBytes(parent);
        var raw = new RawSecurityDescriptor(original, 0); raw.DiscretionaryAcl = null; var bytes = new byte[raw.BinaryLength]; raw.GetBinaryForm(bytes, 0); RestoreAccess(parent, bytes);
        try
        {
            Assert.Null(new RawSecurityDescriptor(SecurityBytes(parent), 0).DiscretionaryAcl);
            Assert.Throws<CredentialException>(() => f.Replan(Path.Combine(parent, "new-stage")));
            Assert.Empty(Directory.EnumerateFileSystemEntries(parent)); Assert.Null(new RawSecurityDescriptor(SecurityBytes(parent), 0).DiscretionaryAcl);
        }
        finally { RestoreAccess(parent, original); }
        Assert.Equal(AccessState(original), AccessState(SecurityBytes(parent)));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Linked_or_dangling_destination_ancestor_is_refused_without_touching_the_target(bool dangling)
    {
        using var f = new StageFixture(); await f.Put(); await f.Select(); var target = Path.Combine(f.Cell.Root, "junction-target"); var link = Path.Combine(f.Cell.Root, "junction-link");
        Directory.CreateDirectory(target); File.WriteAllText(Path.Combine(target, "preserve.txt"), "Original target."); var targetBefore = Snapshot(target);
        await CreateJunction(link, target, f.Cell.Root);
        try
        {
            if (dangling) { File.Delete(Path.Combine(target, "preserve.txt")); Directory.Delete(target); }
            Assert.Throws<CredentialException>(() => f.Replan(Path.Combine(link, "new-stage")));
            Assert.True((File.GetAttributes(link) & FileAttributes.ReparsePoint) != 0);
            if (!dangling) Assert.Equal(targetBefore, Snapshot(target)); else Assert.False(Directory.Exists(target));
        }
        finally { Directory.Delete(link); }
    }

    [WindowsCredentialFact]
    public async Task A_dependency_that_changes_parent_custody_is_rechecked_before_fresh_reservation()
    {
        using var f = new StageFixture(); await f.Put(); await f.Select(); var original = SecurityBytes(f.Cell.Root); f.ClearRequests();
        f.Cell.Keys.OnGet = () =>
        {
            var acl = new DirectoryInfo(f.Cell.Root).GetAccessControl(); acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Write, AccessControlType.Allow));
            new DirectoryInfo(f.Cell.Root).SetAccessControl(acl);
        };
        try { await Assert.ThrowsAsync<CredentialException>(() => f.Stage()); Assert.False(Path.Exists(f.StagePath)); }
        finally { f.Cell.Keys.OnGet = null; RestoreAccess(f.Cell.Root, original); }
    }

    [WindowsCredentialFact]
    public async Task Exact_fresh_reservation_race_has_one_winner_and_never_overwrites_the_winner()
    {
        using var f = new StageFixture(); await f.Put(); await f.Select(); var gate = new Barrier(2);
        var source = new RaceKeys(f.Cell.Keys.Values, gate); var destination = new RaceKeys(f.DestKeys.Values);
        async Task<(CredentialRecoveryStageReceipt? Receipt, Exception? Error)> Attempt()
        {
            try { return (await new CredentialRecoveryStageExecutor(source, destination, f.Cell.Clock).StageAsync(f.Set, f.Plan, f.Plan.Sha256, f.Signer), null); }
            catch (Exception error) { return (null, error); }
        }
        try
        {
            var results = await Task.WhenAll(Task.Run(Attempt), Task.Run(Attempt));
            var receipt = Assert.Single(results, r => r.Receipt is not null).Receipt!; Assert.IsType<CredentialException>(Assert.Single(results, r => r.Error is not null).Error);
            Assert.Equal(receipt.Sha256, Read(Path.Combine(f.StagePath, "completion.json"))["sha256"]!.GetValue<string>());
            var before = Snapshot(f.StagePath); await Assert.ThrowsAsync<CredentialException>(() => f.Stage()); Assert.Equal(before, Snapshot(f.StagePath));
        }
        finally { gate.Dispose(); }
    }

    [WindowsCredentialFact]
    public async Task Post_reservation_failure_retains_private_partial_unknown_state_and_never_becomes_a_retry()
    {
        using var f = new StageFixture(); await f.Put(); await f.Select(); var before = f.SourceSnapshot(); var clock = new FailAfterReservationClock(f.StagePath, f.Cell.Clock.Now);
        var error = await Assert.ThrowsAsync<CredentialException>(() => new CredentialRecoveryStageExecutor(f.Cell.Keys, f.DestKeys, clock).StageAsync(f.Set, f.Plan, f.Plan.Sha256, f.Signer));
        Assert.Equal("credential-recovery-stage-outcome-unknown", error.Code); Assert.True(clock.ObservedReservedRoot); Assert.True(Directory.Exists(f.StagePath)); AssertPrivate(f.StagePath, true);
        Assert.Equal(before, f.SourceSnapshot()); var partial = Snapshot(f.StagePath);
        await Assert.ThrowsAsync<CredentialException>(() => f.Stage()); Assert.Equal(partial, Snapshot(f.StagePath));
    }

    static JsonNode Read(string path) => JsonNode.Parse(File.ReadAllBytes(path))!;
    static JsonNode Profile(string path) => Read(path)["profile"]!;
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static byte[] Aad(JsonNode envelope)
    {
        var profile = envelope["profile"]!.Deserialize<CredentialProfile>(Json)!;
        // Same public format as CredentialVault's existing AAD; no runtime helper or key export is used by this assertion.
        return JsonSerializer.SerializeToUtf8Bytes(new { schema = envelope["schema"]!.GetValue<int>(), keyId = envelope["keyId"]!.GetValue<string>(), profile }, Json);
    }
    static byte[] SecurityBytes(string path) => (Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorBinaryForm();
    static void RestoreAccess(string path, byte[] bytes) { var acl = new DirectorySecurity(); acl.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access); new DirectoryInfo(path).SetAccessControl(acl); }
    static (string?, string?, bool, string) AccessState(byte[] bytes)
    {
        var raw = new RawSecurityDescriptor(bytes, 0); var acl = new byte[raw.DiscretionaryAcl!.BinaryLength]; raw.DiscretionaryAcl.GetBinaryForm(acl, 0);
        return (raw.Owner?.Value, raw.Group?.Value, (raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, Convert.ToHexString(acl));
    }
    static string[] Snapshot(string root, string? excluded = null) => new[] { root }.Concat(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories))
        .Where(path => excluded is null || (path != excluded && !path.StartsWith(excluded + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)))
        .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(root, path) + ":" + Hash(SecurityBytes(path)) + ":" + (Directory.Exists(path) ? "directory" : Hash(File.ReadAllBytes(path)))).ToArray();
    static void AssertPrivate(string path, bool directory)
    {
        using var identity = WindowsIdentity.GetCurrent(); var acl = directory ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected); Assert.Equal(identity.User, acl.GetOwner(typeof(SecurityIdentifier)));
        Assert.All(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule =>
        { Assert.Equal(identity.User!.Value, rule.IdentityReference.Value); Assert.Equal(AccessControlType.Allow, rule.AccessControlType); Assert.True((rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl); });
    }
    static void AssertConservative(CredentialRecoveryStageReceipt receipt)
    {
        Assert.True(receipt.InactiveRecoveryHold); Assert.False(receipt.NormalVaultUsable); Assert.False(receipt.ActivationPerformed); Assert.False(receipt.SourceHostFenced);
        Assert.False(receipt.ExternalExecutionAuthorized); Assert.False(receipt.KeyMaterialExported); Assert.False(receipt.KeyRetirementAuthorized); Assert.False(receipt.ProductionRecoveryQualified); Assert.Equal("NotAssessed", receipt.IndependentBackupCoverage);
    }
    static void Fault(CredentialVault vault, Action<string>? callback) => typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vault, callback);
    static async Task CreateJunction(string link, string target, string root)
    {
        var start = new ProcessStartInfo(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32", "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear(); foreach (var name in new[] { "SystemRoot", "WINDIR" }) if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; _ = process.SafeHandle; var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(deadline.Token); Assert.Equal(0, process.ExitCode); _ = await output; _ = await errors; }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }
    sealed class FailAfterReservationClock(string path, DateTimeOffset now) : TimeProvider
    {
        public bool ObservedReservedRoot { get; private set; }
        public override DateTimeOffset GetUtcNow()
        { if (Directory.Exists(path)) { ObservedReservedRoot = true; throw new IOException("Synthetic late clock failure."); } return now; }
    }
    sealed class RaceKeys(Dictionary<string, byte[]> values, Barrier? gate = null) : ICredentialKeyProvider
    {
        public ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested(); if (gate is not null && !gate.SignalAndWait(TimeSpan.FromSeconds(10), cancellationToken)) throw new IOException("Synthetic race timeout.");
            return ValueTask.FromResult(new CredentialEncryptionKey(values[keyId]));
        }
    }
    sealed class StageFixture : IDisposable
    {
        public const string Purpose = "Synthetic reviewed isolated recovery staging";
        public CredentialFixture Cell { get; } = new(); public RSA Signer { get; } = RSA.Create(2048); public CredentialTestKeys DestKeys { get; } = new();
        public CredentialVaultOptions Options { get; } public CredentialVault Vault { get; } public CredentialVaultNamespaceIdentity Identity { get; }
        public string StagePath => Path.Combine(Cell.Root, "fresh-recovery-stage"); public string SourceFile => Assert.Single(Directory.GetFiles(Options.RootPath, "*.credential.json"));
        public CredentialRecoverySet Set { get; private set; } = null!; public CredentialRecoverySetAuthority Authority { get; private set; } = null!;
        public CredentialRecoverySetVerification Selected { get; private set; } = null!; public CredentialRecoveryDestinationKeyIdentity Destination { get; private set; } = null!;
        public CredentialRecoveryStagePlan Plan { get; private set; } = null!;
        public StageFixture(bool strong = false)
        {
            Options = strong ? Cell.Options with { RootPath = Path.Combine(Cell.Root, "source-vault"), RollbackProtection = new(Path.Combine(Cell.Root, "source-authority")) } : Cell.Options;
            Vault = strong ? CredentialVault.ProvisionStrong(Options, Cell.Keys, Cell.Clock) : Cell.Vault; Identity = Vault.DescribeNamespace(); DestKeys.Values.Clear(); DestKeys.Values["key-2"] = RandomNumberGenerator.GetBytes(32);
        }
        public async Task Put(DateTimeOffset? expires = null) { using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token); await Vault.PutAsync(Cell.Scope, secret, expires ?? Cell.Clock.Now.AddHours(1)); }
        public Task<CredentialRecoverySet> Capture(long generation = 1) => Vault.CaptureSignedRecoverySetAsync(Identity, Signer, "synthetic-stage-source-signer", generation, Cell.Clock.Now, Cell.Clock.Now.AddHours(1));
        public async Task Select()
        {
            Set = await Capture(); Authority = CredentialRecoverySets.Authority(Set); Selected = CredentialRecoverySets.Verify(Set, Authority, Signer, Identity, Cell.Clock);
            Destination = await CredentialRecoveryStaging.ObserveDestinationKeyAsync(Cell.Scope.CustomerId, Cell.Scope.CellId, "key-2", DestKeys);
            Plan = Replan(StagePath); Assert.False(Path.Exists(StagePath));
        }
        public CredentialRecoveryStagePlan Replan(string path) => CredentialRecoveryStaging.PreparePlan(Selected, Authority, Identity, path, Destination, Purpose, Cell.Clock.Now.AddMinutes(10), Cell.Clock);
        public Task<CredentialRecoveryStageReceipt> Stage(string? pin = null) => new CredentialRecoveryStageExecutor(Cell.Keys, DestKeys, Cell.Clock).StageAsync(Set, Plan, pin ?? Plan.Sha256, Signer);
        public void ClearRequests() { Cell.Keys.Requests.Clear(); DestKeys.Requests.Clear(); }
        public string[] SourceSnapshot() => Snapshot(Cell.Root, StagePath);
        public void Dispose() { Set?.Dispose(); Signer.Dispose(); Cell.Dispose(); }
    }
}
