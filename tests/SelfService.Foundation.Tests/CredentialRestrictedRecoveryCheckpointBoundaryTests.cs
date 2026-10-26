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
public sealed class CredentialRestrictedRecoveryCheckpointBoundaryTests
{
    [WindowsCredentialFact]
    public async Task Final_checkpoint_clock_rewind_before_later_plan_preparation_is_unknown_even_when_the_head_is_historically_valid()
    {
        using var f = new RestrictedStageFixture(); await f.Prepare(); var completion = System.IO.Path.Combine(f.StageRoot, "completion.json");
        CredentialRestrictedRecoveryStagePlan? later = null;
        var clock = new CompletedStageClock(f.Cell.Clock, completion, () => f.Cell.Clock.Now = later!.PreparedUtc.AddMilliseconds(-1));
        var ns = f.Source.DescribeNamespace();
        var authority = CredentialRestrictedRecoverySetCheckpointAuthority.Provision(System.IO.Path.Combine(f.Cell.Root, "rewind-checkpoint"), ns,
            "restricted-stage-signer", FoundationAuditAnchors.PublicKeyFingerprint(f.Signer), clock);
        var head = await authority.AcceptNextAsync(f.Set, f.Set.Sha256, f.Signer, f.Cell.Keys, 0, null);
        f.Cell.Clock.Now += TimeSpan.FromMinutes(10);
        var selected = CredentialRestrictedRecoverySets.Verify(f.Set, CredentialRestrictedRecoverySets.Authority(f.Set), f.Signer, ns, f.Cell.Clock);
        later = CredentialRestrictedRecoveryStaging.PreparePlan(selected, CredentialRestrictedRecoverySets.Authority(f.Set), ns,
            f.StageRoot, f.Plan.DestinationKey, "Synthetic later prepared plan", f.Cell.Clock.Now.AddMinutes(10), f.Cell.Clock);
        var wrapper = new CredentialRestrictedRecoveryCheckpointStageExecutor(authority, authority.DescribeIdentity(),
            new CredentialRestrictedRecoveryStageExecutor(f.Cell.Keys, f.Destination, f.Cell.Clock));
        f.Destination.Requests.Clear();
        Assert.Equal("credential-restricted-recovery-checkpoint-stage-outcome-unknown", (await Assert.ThrowsAsync<CredentialException>(() =>
            wrapper.StageLatestAsync(f.Set, later, later.Sha256, f.Signer, head.Sequence, head.Sha256))).Code);
        Assert.True(clock.Fired); Assert.True(File.Exists(completion)); Assert.Single(f.Destination.Requests);
        Assert.True(f.Cell.Clock.Now < later.PreparedUtc); Assert.Equal(head, authority.ReadCurrent());
        using var exclusive = new FileStream(f.GuardPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Final_checkpoint_callback_runs_under_actual_completed_stage_guard_and_cannot_hide_late_output_changes(bool tamper)
    {
        using var f = new RestrictedStageFixture(); await f.Prepare(); var completion = System.IO.Path.Combine(f.StageRoot, "completion.json");
        var guardHeld = false;
        var clock = new CompletedStageClock(f.Cell.Clock, completion, () =>
        {
            try { using var contender = new FileStream(f.GuardPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
            catch (IOException) { guardHeld = true; }
            if (tamper) File.AppendAllText(completion, " ");
        });
        var ns = f.Source.DescribeNamespace(); var pin = FoundationAuditAnchors.PublicKeyFingerprint(f.Signer);
        var authority = CredentialRestrictedRecoverySetCheckpointAuthority.Provision(System.IO.Path.Combine(f.Cell.Root, "latest-restricted-checkpoint"), ns, "restricted-stage-signer", pin, clock);
        var head = await authority.AcceptNextAsync(f.Set, f.Set.Sha256, f.Signer, f.Cell.Keys, 0, null);
        var wrapper = new CredentialRestrictedRecoveryCheckpointStageExecutor(authority, authority.DescribeIdentity(),
            new CredentialRestrictedRecoveryStageExecutor(f.Cell.Keys, f.Destination, f.Cell.Clock));
        f.Destination.Requests.Clear();
        if (tamper)
        {
            Assert.Equal("credential-restricted-recovery-checkpoint-stage-outcome-unknown", (await Assert.ThrowsAsync<CredentialException>(() =>
                wrapper.StageLatestAsync(f.Set, f.Plan, f.Plan.Sha256, f.Signer, head.Sequence, head.Sha256))).Code);
            Assert.EndsWith(" ", File.ReadAllText(completion)); var before = f.Snapshot();
            f.Cell.Keys.Requests.Clear(); f.Destination.Requests.Clear();
            await Assert.ThrowsAsync<CredentialException>(() => wrapper.StageLatestAsync(f.Set, f.Plan, f.Plan.Sha256, f.Signer, head.Sequence, head.Sha256));
            Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests); Assert.Equal(before, f.Snapshot());
        }
        else
        {
            var result = await wrapper.StageLatestAsync(f.Set, f.Plan, f.Plan.Sha256, f.Signer, head.Sequence, head.Sha256);
            Assert.True(result.CheckpointReadLeaseRetainedDuringStaging); Assert.Single(f.Destination.Requests);
        }
        Assert.True(clock.Fired); Assert.True(guardHeld); Assert.Equal(head, authority.ReadCurrent());
        using var exclusive = new FileStream(f.GuardPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("header", "missing")] [InlineData("header", "null")] [InlineData("header", "general")]
    [InlineData("record", "missing")] [InlineData("record", "null")] [InlineData("record", "general")]
    [InlineData("head", "missing")] [InlineData("head", "null")] [InlineData("head", "general")]
    [InlineData("set", "missing")] [InlineData("set", "null")] [InlineData("set", "general")]
    public async Task Every_actual_wire_profile_is_mandatory_even_after_public_checksum_recomputation(string location, string mutation)
    {
        using var f = new CandidateFixture(); await f.Put(); using var set = await f.Capture(); var authority = f.Checkpoint.Provision();
        await f.Accept(authority, set); var path = System.IO.Path.Combine(f.Checkpoint.Path, location == "header" ? Marker : "checkpoint-0001.json");
        var document = JsonNode.Parse(File.ReadAllText(path))!.AsObject();
        var node = location switch { "header" or "record" => document, "head" => document["checkpoint"]!.AsObject(), _ => document["checkpoint"]!["setAuthority"]!.AsObject() };
        if (mutation == "missing") node.Remove("accessProfile"); else node["accessProfile"] = mutation == "null" ? null : JsonValue.Create("General");
        var owner = typeof(CredentialRestrictedRecoverySetCheckpointAuthority);
        if (location == "header")
        {
            var type = owner.GetNestedType("Header", BindingFlags.NonPublic)!; var parsed = JsonSerializer.Deserialize(document.ToJsonString(Json), type, Json)!;
            document["sha256"] = (string)owner.GetMethod("HeaderDigest", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [parsed])!;
        }
        else if (location != "record")
        {
            var parsed = document["checkpoint"]!.Deserialize<CredentialRestrictedRecoverySetCheckpoint>(Json)!;
            document["checkpoint"]!["sha256"] = PublicCheckpointDigest(parsed);
        }
        File.WriteAllText(path, document.ToJsonString(Json)); f.Cell.Keys.Requests.Clear(); var before = Snapshot(f.Checkpoint.Path);
        Assert.Throws<FoundationStoreException>(() => authority.ReadCurrent()); Assert.Throws<FoundationStoreException>(() => f.Checkpoint.Reopen());
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, Snapshot(f.Checkpoint.Path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void General_scoped_and_restricted_roots_cannot_adopt_each_other_with_identical_sentinels(bool restrictedRoot)
    {
        using var f = new AuthorityRoot();
        if (restrictedRoot)
        {
            _ = f.Provision(); var before = Snapshot(f.Path);
            Assert.Throws<FoundationStoreException>(() => new CredentialScopedRecoverySetCheckpointAuthority(f.Path, f.Namespace, AuthorityRoot.KeyId, f.Pin, f.Clock));
            Assert.Equal(before, Snapshot(f.Path));
        }
        else
        {
            _ = CredentialScopedRecoverySetCheckpointAuthority.Provision(f.Path, f.Namespace, AuthorityRoot.KeyId, f.Pin, f.Clock); var before = Snapshot(f.Path);
            Assert.Throws<FoundationStoreException>(() => f.Reopen()); Assert.Equal(before, Snapshot(f.Path));
        }
    }

    [Theory]
    [InlineData(1)] [InlineData(2)]
    public async Task A_clock_callback_after_source_keys_cannot_append_into_changed_authority_custody(int cutoff)
    {
        using var f = new CandidateFixture(); await f.Put(); using var set = await f.Capture();
        var marker = System.IO.Path.Combine(f.Checkpoint.Path, Marker); var callback = new AfterKeyClock(f.Cell.Clock, f.Cell.Keys, cutoff, () => File.WriteAllText(marker, "{}"));
        var authority = f.Checkpoint.Provision(callback); f.Cell.Keys.Requests.Clear(); callback.Armed = true; var source = Snapshot(f.Cell.Root);
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(authority, set));
        Assert.True(callback.Fired); Assert.Equal("{}", File.ReadAllText(marker));
        Assert.Empty(Directory.GetFiles(f.Checkpoint.Path, "checkpoint-*.json")); Assert.Equal(source, Snapshot(f.Cell.Root));
    }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    const string Marker = ".credential-recovery-checkpoint.json", Guard = ".credential-recovery-checkpoint.guard";

    [WindowsCredentialFact]
    public void Fresh_native_authority_is_private_at_birth_and_never_adopts_or_repairs_an_existing_path()
    {
        using var f = new AuthorityRoot(); var parent = Security(f.Root); var authority = f.Provision();
        Assert.Null(authority.ReadCurrent()); AssertPrivate(f.Path, true);
        Assert.Equal(new[] { Guard, Marker }.Order(StringComparer.Ordinal), Directory.GetFiles(f.Path).Select(p => System.IO.Path.GetFileName(p)!).Order(StringComparer.Ordinal));
        foreach (var file in Directory.GetFiles(f.Path)) AssertPrivate(file, false);
        Assert.Equal(f.Namespace, authority.Identity.SourceNamespace); Assert.Equal(f.Pin, authority.Identity.PublicKeySha256);
        var bytes = Snapshot(f.Path); Assert.Throws<FoundationStoreException>(() => f.Provision()); Assert.Equal(bytes, Snapshot(f.Path)); Assert.Equal(parent, Security(f.Root));
    }

    [Theory]
    [InlineData("empty")] [InlineData("unrelated")] [InlineData("guard-only")] [InlineData("marker-only")] [InlineData("record-only")] [InlineData("partial-write")]
    public void Existing_empty_partial_and_unrelated_roots_are_preserved_without_adoption(string shape)
    {
        using var f = new AuthorityRoot(); Directory.CreateDirectory(f.Path);
        if (shape != "empty") File.WriteAllText(System.IO.Path.Combine(f.Path, shape switch { "guard-only" => Guard, "marker-only" => Marker, "record-only" => "checkpoint-0001.json", "partial-write" => ".write-interrupted", _ => "original.txt" }), "Original partial bytes.");
        var before = Snapshot(f.Path); Assert.Throws<FoundationStoreException>(() => f.Provision()); Assert.Throws<FoundationStoreException>(() => f.Reopen()); Assert.Equal(before, Snapshot(f.Path));
    }

    [WindowsCredentialFact]
    public void Provision_failure_after_private_reservation_retains_its_partial_guard_and_cannot_retry()
    {
        using var f = new AuthorityRoot(); Assert.ThrowsAny<Exception>(() => f.Provision(new ThrowingClock()));
        Assert.True(Directory.Exists(f.Path)); AssertPrivate(f.Path, true); Assert.True(File.Exists(System.IO.Path.Combine(f.Path, Guard)));
        var before = Snapshot(f.Path); Assert.Throws<FoundationStoreException>(() => f.Provision()); Assert.Throws<FoundationStoreException>(() => f.Reopen()); Assert.Equal(before, Snapshot(f.Path));
    }

    public static IEnumerable<object[]> ProtectedNames()
    {
        foreach (var name in new[] { Guard, Marker, ".credential-namespace.json", ".vault.lock", ".keys.lock", ".rollback.lock", "authority.json", ".backup-checkpoint.guard",
            ".activation.guard", ".installation-owner.json", ".installation.guard", ".package-owner.json", "cell-binding.json", ".foundation-owner-initialization.json",
            ".credential-recovery-stage.guard", ".credential-recovery-stage.json", "legacy.credential.json", "legacy.dpapi.json" })
            foreach (var directory in new[] { false, true }) yield return [name, directory];
    }
    [Theory]
    [MemberData(nameof(ProtectedNames))]
    public void Ancestor_marker_files_or_directories_even_invalid_partial_namespaces_are_not_overwritten(string marker, bool directory)
    {
        using var f = new AuthorityRoot(); var parent = f.Child("protected-parent"); Directory.CreateDirectory(parent); var existing = System.IO.Path.Combine(parent, marker);
        if (directory) Directory.CreateDirectory(existing); else File.WriteAllText(existing, "Preserved invalid marker.");
        var before = Snapshot(parent); var target = System.IO.Path.Combine(parent, "new-checkpoint"); Assert.Throws<FoundationStoreException>(() => f.ProvisionAt(target));
        Assert.False(System.IO.Path.Exists(target)); Assert.Equal(before, Snapshot(parent));
    }

    [Theory]
    [InlineData(".CREDENTIAL-RECOVERY-CHECKPOINT.GUARD")] [InlineData(".credential-recovery-checkpoint.json")]
    [InlineData(".VAULT.LOCK")] [InlineData("AUTHORITY.JSON")] [InlineData("legacy.CREDENTIAL.JSON")] [InlineData("legacy.DPAPI.JSON")]
    public void Protected_leaf_aliases_cannot_turn_a_clean_parent_into_an_operational_namespace(string leaf)
    {
        using var f = new AuthorityRoot(); var before = Snapshot(f.Root); Assert.Throws<FoundationStoreException>(() => f.ProvisionAt(f.Child(leaf))); Assert.Equal(before, Snapshot(f.Root));
    }

    [Theory]
    [InlineData(FileSystemRights.Write)] [InlineData(FileSystemRights.DeleteSubdirectoriesAndFiles)] [InlineData(FileSystemRights.Delete)]
    [InlineData(FileSystemRights.ChangePermissions)] [InlineData(FileSystemRights.TakeOwnership)]
    public void Foreign_effective_parent_writes_are_refused_without_parent_repair(FileSystemRights rights)
    {
        using var f = new AuthorityRoot(); var parent = f.Child("unsafe-parent"); Directory.CreateDirectory(parent); AddAccess(parent, rights); var before = Snapshot(parent);
        Assert.Throws<FoundationStoreException>(() => f.ProvisionAt(System.IO.Path.Combine(parent, "checkpoint"))); Assert.Equal(before, Snapshot(parent));
    }

    [WindowsCredentialFact]
    public void Existing_authority_operations_reject_a_new_null_parent_DACL_without_repairing_it()
    {
        using var f = new AuthorityRoot(); var authority = f.Provision(); var original = Security(f.Root); var raw = new RawSecurityDescriptor(original, 0); raw.DiscretionaryAcl = null;
        var bytes = new byte[raw.BinaryLength]; raw.GetBinaryForm(bytes, 0); SetAccess(f.Root, bytes);
        try
        {
            Assert.Null(new RawSecurityDescriptor(Security(f.Root), 0).DiscretionaryAcl);
            Assert.Throws<FoundationStoreException>(() => authority.ReadCurrent()); Assert.Throws<FoundationStoreException>(() => f.Reopen());
            Assert.Null(new RawSecurityDescriptor(Security(f.Root), 0).DiscretionaryAcl);
        }
        finally { SetAccess(f.Root, original); }
        Assert.Equal(AccessState(original), AccessState(Security(f.Root)));
    }

    [Theory]
    [InlineData("root")] [InlineData("marker")] [InlineData("guard")]
    public void Read_only_access_still_refuses_foreign_read_permissions_on_private_custody(string where)
    {
        using var f = new AuthorityRoot(); var authority = f.Provision(); AddAccess(where == "root" ? f.Path : System.IO.Path.Combine(f.Path, where == "marker" ? Marker : Guard), FileSystemRights.Read);
        var before = Snapshot(f.Path); Assert.Throws<FoundationStoreException>(() => authority.ReadCurrent()); Assert.Throws<FoundationStoreException>(() => f.Reopen()); Assert.Equal(before, Snapshot(f.Path));
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Direct_and_ancestor_junctions_including_dangling_entries_preserve_their_original_targets(bool direct, bool dangling)
    {
        using var f = new AuthorityRoot(); var target = f.Child("junction-target"); Directory.CreateDirectory(target); var link = f.Child("junction");
        if (!dangling) File.WriteAllText(System.IO.Path.Combine(target, "original.txt"), "Untouched target."); await CreateJunction(link, target, f.Root);
        try { if (dangling) Directory.Delete(target); var before = dangling ? [] : Snapshot(target); Assert.Throws<FoundationStoreException>(() => f.ProvisionAt(direct ? link : System.IO.Path.Combine(link, "checkpoint"))); if (dangling) Assert.False(Directory.Exists(target)); else Assert.Equal(before, Snapshot(target)); }
        finally { Directory.Delete(link); }
    }

    [Theory]
    [InlineData("customer")] [InlineData("cell")] [InlineData("vault")] [InlineData("namespace-commitment")] [InlineData("authority-namespace")] [InlineData("signer-id")] [InlineData("signer-pin")]
    public void Existing_root_identity_cannot_rebind_source_namespace_or_signer(string changed)
    {
        using var f = new AuthorityRoot(); f.Provision(); var ns = f.Namespace;
        ns = changed switch { "customer" => ns with { CustomerId = Guid.NewGuid().ToString() }, "cell" => ns with { CellId = Guid.NewGuid().ToString() },
            "vault" => ns with { VaultNamespaceId = Guid.NewGuid().ToString("N") }, "namespace-commitment" => ns with { NamespaceCommitmentSha256 = new string('9', 64) },
            "authority-namespace" => ns with { AuthorityNamespaceId = Guid.NewGuid().ToString("N"), AuthorityBindingSha256 = new string('9', 64) }, _ => ns };
        var before = Snapshot(f.Path); Assert.Throws<FoundationStoreException>(() => new CredentialRestrictedRecoverySetCheckpointAuthority(f.Path, ns,
            changed == "signer-id" ? "other-signer" : AuthorityRoot.KeyId, changed == "signer-pin" ? new string('9', 64) : f.Pin, f.Clock)); Assert.Equal(before, Snapshot(f.Path));
    }

    [WindowsCredentialFact]
    public void Copied_identity_metadata_does_not_adopt_another_absolute_root()
    {
        using var f = new AuthorityRoot(); f.Provision(); var copy = f.Child("copy"); Directory.CreateDirectory(copy); Protect(copy, true);
        foreach (var source in Directory.GetFiles(f.Path)) { var target = System.IO.Path.Combine(copy, System.IO.Path.GetFileName(source)); File.Copy(source, target); Protect(target, false); }
        var before = Snapshot(copy); Assert.Throws<FoundationStoreException>(() => f.ReopenAt(copy)); Assert.Equal(before, Snapshot(copy));
    }

    [Theory]
    [InlineData("unknown")] [InlineData("duplicate-case")] [InlineData("root-path")] [InlineData("owner")] [InlineData("schema")] [InlineData("truncated")] [InlineData("oversize")]
    public void Header_corruption_is_refused_without_rewriting_any_custody_bytes(string mutation)
    {
        using var f = new AuthorityRoot(); var authority = f.Provision(); var path = System.IO.Path.Combine(f.Path, Marker); var node = JsonNode.Parse(File.ReadAllText(path))!;
        if (mutation == "unknown") node["unexpected"] = true; if (mutation == "root-path") node["rootPath"] = f.Child("other"); if (mutation == "owner") node["ownerSid"] = "S-1-5-18"; if (mutation == "schema") node["schema"] = 2;
        File.WriteAllText(path, mutation switch { "truncated" => "{", "oversize" => new string('x', CredentialRestrictedRecoverySetCheckpointAuthority.MaximumMetadataBytes + 1), "duplicate-case" => File.ReadAllText(path).Replace("\"schema\":", "\"SCHEMA\":1,\"schema\":", StringComparison.Ordinal), _ => node.ToJsonString(Json) });
        var before = Snapshot(f.Path); Assert.Throws<FoundationStoreException>(() => authority.ReadCurrent()); Assert.Equal(before, Snapshot(f.Path));
    }

    [WindowsCredentialFact]
    public async Task Real_retained_AES_authentication_precedes_append_and_receipts_remain_non_grants()
    {
        using var f = new CandidateFixture(); await f.Put(); using var set = await f.Capture(); var authority = f.Checkpoint.Provision(); var source = Snapshot(f.Cell.Root); f.Cell.Keys.Requests.Clear();
        var head = await f.Accept(authority, set); Assert.Single(f.Cell.Keys.Requests); Assert.Equal(CredentialRestrictedRecoverySets.Authority(set), head.SetAuthority);
        Assert.Equal(set.Signature.InventorySha256, head.InventorySha256); Assert.Equal(1, head.Sequence); Assert.Equal("", head.PreviousSha256); Assert.Equal(head, authority.ReadCurrent());
        Assert.False(head.KeyBackupVerified); Assert.False(head.PromotionAuthorized); Assert.False(head.ActivationPerformed); Assert.False(head.ExternalExecutionAuthorized); Assert.Equal("NotAssessed", head.IndependentBackupCoverage);
        Assert.Equal(source, Snapshot(f.Cell.Root)); var text = JsonSerializer.Serialize(head, Json); Assert.DoesNotContain(CredentialFixture.Token, text); Assert.DoesNotContain(f.Cell.Root, text); Assert.DoesNotContain(f.Cell.Scope.AuthProfileId, text);
    }

    [Theory]
    [InlineData(".credential-recovery-checkpoint.guard")] [InlineData(".CREDENTIAL-RECOVERY-CHECKPOINT.JSON")]
    public async Task Existing_staging_entry_point_cannot_turn_a_clean_parent_into_a_checkpoint_namespace(string leaf)
    {
        using var f = new CandidateFixture(); await f.Put(); using var set = await f.Capture();
        var selected = CredentialRestrictedRecoverySets.Verify(set, CredentialRestrictedRecoverySets.Authority(set), f.Signer, f.Checkpoint.Namespace, f.Cell.Clock);
        var key = await CredentialRestrictedRecoveryStaging.ObserveDestinationKeyAsync(f.Cell.Scope.CustomerId, f.Cell.Scope.CellId, "key-1", f.Cell.Keys);
        var before = Snapshot(f.Cell.Root); var path = System.IO.Path.Combine(f.Cell.Root, leaf);
        Assert.Throws<CredentialException>(() => CredentialRestrictedRecoveryStaging.PreparePlan(selected, CredentialRestrictedRecoverySets.Authority(set), f.Checkpoint.Namespace,
            path, key, "Synthetic protected leaf refusal", f.Cell.Clock.Now.AddMinutes(10), f.Cell.Clock));
        Assert.False(System.IO.Path.Exists(path)); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData("reviewed-pin")] [InlineData("wrong-public-key")] [InlineData("signer-id")] [InlineData("namespace")] [InlineData("wrong-AES-key")] [InlineData("key-failure")] [InlineData("expired")] [InlineData("cancel-before")] [InlineData("cancel-at-key")]
    public async Task Rejected_candidate_key_and_cancellation_paths_leave_head_audit_and_source_unchanged(string rejection)
    {
        using var f = new CandidateFixture(); await f.Put(); using var other = new CandidateFixture(); await other.Put();
        using var set = rejection == "namespace" ? await other.Capture() : await f.Capture(keyId: rejection == "signer-id" ? "other-signer" : AuthorityRoot.KeyId);
        var authority = f.Checkpoint.Provision(); using var key = rejection == "wrong-public-key" ? RSA.Create(2048) : f.Public(); using var canceled = new CancellationTokenSource();
        if (rejection == "wrong-AES-key") f.Cell.Keys.Values["key-1"] = RandomNumberGenerator.GetBytes(32);
        if (rejection == "key-failure") f.Cell.Keys.Fail = true; if (rejection == "expired") f.Cell.Clock.Now = set.Signature.ExpiresUtc;
        if (rejection == "cancel-before") canceled.Cancel(); if (rejection == "cancel-at-key") f.Cell.Keys.OnGet = canceled.Cancel;
        var before = Snapshot(f.Checkpoint.Path); var source = Snapshot(f.Cell.Root); f.Cell.Keys.Requests.Clear();
        try { await Assert.ThrowsAnyAsync<Exception>(() => authority.AcceptNextAsync(set, rejection == "reviewed-pin" ? new string('1', 64) : set.Sha256, key, f.Cell.Keys, 0, null, canceled.Token)); }
        finally { f.Cell.Keys.OnGet = null; }
        Assert.Null(authority.ReadCurrent()); Assert.Equal(before, Snapshot(f.Checkpoint.Path)); Assert.Equal(source, Snapshot(f.Cell.Root));
        if (rejection is "reviewed-pin" or "wrong-public-key" or "signer-id" or "namespace" or "expired" or "cancel-before") Assert.Empty(f.Cell.Keys.Requests);
    }

    [Theory]
    [InlineData(1)] [InlineData(10)]
    public async Task Old_or_reused_signed_source_generations_cannot_advance_a_correctly_selected_head(long generation)
    {
        using var f = new CandidateFixture(); await f.Put(); using var first = await f.Capture(10); using var candidate = await f.Capture(generation); var authority = f.Checkpoint.Provision(); var head = await f.Accept(authority, first);
        var before = Snapshot(f.Checkpoint.Path); f.Cell.Keys.Requests.Clear(); await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(authority, candidate, head));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(head, authority.ReadCurrent()); Assert.Equal(before, Snapshot(f.Checkpoint.Path));
    }

    [WindowsCredentialFact]
    public async Task Shared_read_lease_pins_one_head_blocks_append_and_old_selection_cannot_be_reacquired()
    {
        using var f = new CandidateFixture(); await f.Put(); using var first = await f.Capture(); using var next = await f.Capture(2); var authority = f.Checkpoint.Provision(); var head = await f.Accept(authority, first);
        var lease = authority.AcquireReadLease(head.Sequence, head.Sha256); using (var second = authority.AcquireReadLease(head.Sequence, head.Sha256)) { lease.AssertCurrent(); second.AssertCurrent(); }
        f.Cell.Keys.Requests.Clear(); await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(authority, next, head)); Assert.Empty(f.Cell.Keys.Requests);
        lease.Dispose(); Assert.Throws<FoundationStoreException>(lease.AssertCurrent); var latest = await f.Accept(authority, next, head);
        Assert.Throws<FoundationStoreException>(() => authority.AcquireReadLease(head.Sequence, head.Sha256)); Assert.Equal(2, latest.Sequence); Assert.Equal(head.Sha256, latest.PreviousSha256);
    }

    [WindowsCredentialFact]
    public async Task Exclusive_admission_guard_spans_source_await_and_frozen_RSA_and_artifact_survive_caller_replacement()
    {
        using var f = new CandidateFixture(); await f.Put(); var set = await f.Capture(); var selected = CredentialRestrictedRecoverySets.Authority(set); var authority = f.Checkpoint.Provision(); using var key = f.Public(); using var replacement = RSA.Create(2048);
        var blocked = new BlockedKeys(f.Cell.Keys); var accepting = authority.AcceptNextAsync(set, set.Sha256, key, blocked, 0, null);
        await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            Assert.Throws<FoundationStoreException>(() => authority.ReadCurrent()); Assert.Throws<FoundationStoreException>(() => f.Checkpoint.Reopen());
            key.ImportSubjectPublicKeyInfo(replacement.ExportSubjectPublicKeyInfo(), out _); set.Dispose(); blocked.Release.TrySetResult();
            var head = await accepting; Assert.Equal(selected, head.SetAuthority); Assert.Equal(f.Checkpoint.Pin, head.SetAuthority.PublicKeySha256);
        }
        finally { blocked.Release.TrySetResult(); await accepting.WaitAsync(TimeSpan.FromSeconds(5)); }
    }

    [WindowsCredentialFact]
    public async Task Source_provider_that_changes_parent_custody_cannot_publish_a_checkpoint()
    {
        using var f = new CandidateFixture(); await f.Put(); using var set = await f.Capture(); var authority = f.Checkpoint.Provision(); var original = Security(f.Checkpoint.Root); var before = Snapshot(f.Checkpoint.Path);
        f.Cell.Keys.OnGet = () => AddAccess(f.Checkpoint.Root, FileSystemRights.Write);
        try { await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(authority, set)); Assert.Equal(before, Snapshot(f.Checkpoint.Path)); }
        finally { f.Cell.Keys.OnGet = null; SetAccess(f.Checkpoint.Root, original); }
        Assert.Null(authority.ReadCurrent());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Lifetime_rewind_or_expiry_during_source_authentication_refuses_before_append(bool expiry)
    {
        using var f = new CandidateFixture(); await f.Put(); using var set = await f.Capture(); var authority = f.Checkpoint.Provision(); var before = Snapshot(f.Checkpoint.Path);
        f.Cell.Keys.OnGet = () => f.Cell.Clock.Now = expiry ? set.Signature.ExpiresUtc : set.Signature.IssuedUtc.AddMilliseconds(-1);
        try { await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(authority, set)); Assert.Equal(before, Snapshot(f.Checkpoint.Path)); }
        finally { f.Cell.Keys.OnGet = null; f.Cell.Clock.Now = set.Signature.IssuedUtc; }
        Assert.Null(authority.ReadCurrent());
    }

    [WindowsCredentialFact]
    public async Task Exact_CAS_race_appends_one_winner_and_never_rewrites_the_accepted_record()
    {
        using var f = new CandidateFixture(); await f.Put(); using var one = await f.Capture(); using var two = await f.Capture(2); using var three = await f.Capture(3); var authority = f.Checkpoint.Provision(); var first = await f.Accept(authority, one);
        var start = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task<CredentialRestrictedRecoverySetCheckpoint?> Attempt(CredentialRestrictedRecoverySet set) { await start.Task; using var key = f.Public(); try { return await authority.AcceptNextAsync(set, set.Sha256, key, f.Cell.Keys, first.Sequence, first.Sha256); } catch (FoundationStoreException) { return null; } }
        var a = Task.Run(() => Attempt(two)); var b = Task.Run(() => Attempt(three)); start.SetResult(); var outcomes = await Task.WhenAll(a, b); var winner = Assert.Single(outcomes, value => value is not null)!;
        Assert.Equal(winner, authority.ReadCurrent()); Assert.Equal(2, winner.Sequence); var before = Snapshot(f.Checkpoint.Path); f.Cell.Keys.Requests.Clear();
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(authority, two, first)); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, Snapshot(f.Checkpoint.Path));
    }

    [Theory]
    [InlineData("gap")] [InlineData("truncated")] [InlineData("hash")] [InlineData("extra")] [InlineData("directory")]
    [InlineData("computed-flag")] [InlineData("computed-generation")]
    public async Task History_corruption_and_false_claims_are_never_truncated_repaired_or_silently_accepted(string corruption)
    {
        using var f = new CandidateFixture(); await f.Put(); using var first = await f.Capture(); using var next = await f.Capture(2); var authority = f.Checkpoint.Provision(); var head = await f.Accept(authority, first); await f.Accept(authority, next, head);
        var path = System.IO.Path.Combine(f.Checkpoint.Path, "checkpoint-0002.json"); var node = JsonNode.Parse(File.ReadAllText(path))!;
        if (corruption == "gap") File.Delete(System.IO.Path.Combine(f.Checkpoint.Path, "checkpoint-0001.json"));
        else if (corruption == "extra") { File.WriteAllText(System.IO.Path.Combine(f.Checkpoint.Path, "unrecognized.json"), "{}"); Protect(System.IO.Path.Combine(f.Checkpoint.Path, "unrecognized.json"), false); }
        else if (corruption == "directory") Directory.CreateDirectory(System.IO.Path.Combine(f.Checkpoint.Path, "unrecognized-directory"));
        else { if (corruption == "computed-flag") node["checkpoint"]!["promotionAuthorized"] = true; if (corruption == "computed-generation") node["checkpoint"]!["sourceGeneration"] = 99; if (corruption == "hash") node["checkpoint"]!["sha256"] = new string('9', 64); File.WriteAllText(path, corruption == "truncated" ? "{" : node.ToJsonString(Json)); }
        var before = Snapshot(f.Checkpoint.Path); Assert.Throws<FoundationStoreException>(() => authority.ReadCurrent()); Assert.Throws<FoundationStoreException>(() => f.Checkpoint.Reopen()); Assert.Equal(before, Snapshot(f.Checkpoint.Path));
    }

    [WindowsCredentialFact]
    public async Task Postappend_observation_failure_is_uncertain_and_preserves_accepted_output_for_explicit_read()
    {
        using var f = new CandidateFixture(); await f.Put(); using var set = await f.Capture(); var checkpointPath = System.IO.Path.Combine(f.Checkpoint.Path, "checkpoint-0001.json");
        var authority = f.Checkpoint.Provision(new RefuseAfterAppendClock(f.Cell.Clock, checkpointPath));
        Assert.Equal("CredentialRestrictedRecoveryCheckpointAppendUncertain", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(authority, set))).Code);
        var read = f.Checkpoint.Reopen().ReadCurrent(); Assert.NotNull(read); Assert.Equal(CredentialRestrictedRecoverySets.Authority(set), read.SetAuthority);
        var before = Snapshot(f.Checkpoint.Path); await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(f.Checkpoint.Reopen(), set)); Assert.Equal(before, Snapshot(f.Checkpoint.Path));
    }

    [WindowsCredentialFact]
    public async Task Expected_counter_and_physical_history_capacity_refuse_without_key_access_or_cleanup()
    {
        using var f = new CandidateFixture(); await f.Put(); using var set = await f.Capture(); var authority = f.Checkpoint.Provision(); using var key = f.Public(); f.Cell.Keys.Requests.Clear();
        var before = Snapshot(f.Checkpoint.Path); await Assert.ThrowsAsync<FoundationStoreException>(() => authority.AcceptNextAsync(set, set.Sha256, key, f.Cell.Keys, CredentialRestrictedRecoverySetCheckpointAuthority.MaximumCheckpoints, new string('a', 64))); Assert.Equal(before, Snapshot(f.Checkpoint.Path)); Assert.Empty(f.Cell.Keys.Requests);
        for (var i = 1; i <= CredentialRestrictedRecoverySetCheckpointAuthority.MaximumCheckpoints + 1; i++) { var path = System.IO.Path.Combine(f.Checkpoint.Path, $"checkpoint-{i:D4}.json"); File.WriteAllText(path, "{}"); Protect(path, false); }
        before = Snapshot(f.Checkpoint.Path); Assert.Throws<FoundationStoreException>(() => authority.ReadCurrent()); Assert.Equal(before, Snapshot(f.Checkpoint.Path));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public void Identical_guard_names_do_not_adopt_the_other_checkpoint_protocol(bool scopedRoot)
    {
        using var f = new AuthorityRoot();
        if (scopedRoot)
        {
            _ = f.Provision(); var before = Snapshot(f.Path);
            Assert.Throws<FoundationStoreException>(() => new CredentialRecoverySetCheckpointAuthority(f.Path, f.Namespace, AuthorityRoot.KeyId, f.Pin, f.Clock));
            Assert.Equal(before, Snapshot(f.Path));
        }
        else
        {
            _ = CredentialRecoverySetCheckpointAuthority.Provision(f.Path, f.Namespace, AuthorityRoot.KeyId, f.Pin, f.Clock); var before = Snapshot(f.Path);
            Assert.Throws<FoundationStoreException>(() => f.Reopen()); Assert.Equal(before, Snapshot(f.Path));
        }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Mixed_retained_set_requires_actual_original_proof_key_after_live_source_roots_are_moved(bool wrongProofKey)
    {
        using var f = new CandidateFixture(); await f.PutAbsence(); f.Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32);
        using (var secret = CredentialSecret.FromUtf8(CredentialFixture.Token))
            await new CredentialVault(f.Options with { EncryptionKeyId = "key-2" }, f.Cell.Keys, f.Cell.Clock).PutAsync(f.Cell.Scope, secret, f.Cell.Clock.Now.AddHours(1));
        using var set = await f.Capture(); var authority = f.Checkpoint.Provision(); var before = Snapshot(f.Checkpoint.Path);
        Directory.Move(f.Options.RootPath, System.IO.Path.Combine(f.Cell.Root, "moved-vault"));
        Directory.Move(f.Options.RollbackProtection!.AuthorityDirectory, System.IO.Path.Combine(f.Cell.Root, "moved-rollback"));
        var source = Snapshot(f.Cell.Root); f.Cell.Keys.Requests.Clear();
        if (wrongProofKey) f.Cell.Keys.Values["key-1"] = RandomNumberGenerator.GetBytes(32);
        if (wrongProofKey)
        {
            Assert.Equal("CredentialRestrictedRecoveryCheckpointCandidateInvalid", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(authority, set))).Code);
            Assert.Null(authority.ReadCurrent()); Assert.Equal(before, Snapshot(f.Checkpoint.Path));
        }
        else
        {
            var head = await f.Accept(authority, set); Assert.Equal(CredentialRestrictedRecoverySets.Authority(set), head.SetAuthority);
            Assert.Equal(1, head.Sequence); Assert.False(head.KeyBackupVerified); Assert.False(head.PromotionAuthorized);
        }
        Assert.Equal(new[] { "key-2", "key-1" }, f.Cell.Keys.Requests.Select(k => k.KeyId));
        Assert.Equal(source, Snapshot(f.Cell.Root)); Assert.False(Directory.Exists(f.Options.RootPath));
    }

    [WindowsCredentialFact]
    public async Task Cancellation_retains_exclusive_guard_until_an_uncooperative_source_provider_returns()
    {
        using var f = new CandidateFixture(); await f.PutAbsence(); using var set = await f.Capture(); var authority = f.Checkpoint.Provision();
        var before = Snapshot(f.Checkpoint.Path); using var cancellation = new CancellationTokenSource(); var blocked = new UncooperativeKeys(f.Cell.Keys);
        var operation = authority.AcceptNextAsync(set, set.Sha256, f.Signer, blocked, 0, null, cancellation.Token);
        try
        {
            await blocked.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel();
            Assert.False(operation.IsCompleted); Assert.Throws<FoundationStoreException>(() => authority.ReadCurrent());
            Assert.Throws<FoundationStoreException>(() => f.Checkpoint.Reopen()); blocked.Release.TrySetResult();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation);
        }
        finally { blocked.Release.TrySetResult(); }
        Assert.Null(authority.ReadCurrent()); Assert.Equal(before, Snapshot(f.Checkpoint.Path));
    }

    [WindowsCredentialFact]
    public async Task Coherently_resigned_retained_proof_with_invalid_MAC_is_not_admitted_as_a_checkpoint()
    {
        using var f = new CandidateFixture(); await f.PutAbsence(); using var original = await f.Capture();
        var selected = CredentialRestrictedRecoverySets.Verify(original, CredentialRestrictedRecoverySets.Authority(original), f.Signer, f.Checkpoint.Namespace, f.Cell.Clock);
        using var rewritten = ResignBadProof(f, original, selected.Inventory);
        _ = CredentialRestrictedRecoverySets.Verify(rewritten, CredentialRestrictedRecoverySets.Authority(rewritten), f.Signer, f.Checkpoint.Namespace, f.Cell.Clock);
        var authority = f.Checkpoint.Provision(); var before = Snapshot(f.Checkpoint.Path); var source = Snapshot(f.Cell.Root); f.Cell.Keys.Requests.Clear();
        Assert.Equal("CredentialRestrictedRecoveryCheckpointCandidateInvalid", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(authority, rewritten))).Code);
        Assert.Single(f.Cell.Keys.Requests); Assert.Null(authority.ReadCurrent()); Assert.Equal(before, Snapshot(f.Checkpoint.Path)); Assert.Equal(source, Snapshot(f.Cell.Root));
    }

    static CredentialRestrictedRecoverySet ResignBadProof(CandidateFixture f, CredentialRestrictedRecoverySet original, CredentialRestrictedRecoveryInventory inventory)
    {
        using var output = new MemoryStream(); original.WritePrivateArtifact(output); var artifact = JsonNode.Parse(output.ToArray())!.AsObject();
        var content = JsonNode.Parse(Convert.FromBase64String(artifact["content"]!.GetValue<string>()))!.AsObject();
        var stateFile = content["files"]!.AsArray().Single(n => n!["name"]!.GetValue<string>() == "authority/state.json")!.AsObject();
        var state = JsonNode.Parse(Convert.FromBase64String(stateFile["bytes"]!.GetValue<string>()))!.AsObject();
        var proof = state["profiles"]!.AsObject().Single().Value!["absenceProof"]!.AsObject(); proof["tag"] = new string('0', 64);
        var payload = new JsonObject { ["domain"] = "platform-studio.credential-absence-scope-proof.v2" };
        foreach (var field in new[] { "version", "kind", "customerId", "cellId", "vaultNamespaceId", "vaultNamespaceCommitmentSha256", "authorityNamespaceId", "authorityNamespaceCommitmentSha256", "bindingSha256", "profileIdSha256", "scope", "scopeSha256", "keyId", "firstTransactionId", "firstCandidateSha256", "firstCandidateLength", "firstGeneration", "algorithm" }) payload[field] = proof[field]!.DeepClone();
        proof["sha256"] = Hash(JsonSerializer.SerializeToUtf8Bytes(new { payload, tag = proof["tag"]!.GetValue<string>() }, Json));
        var ns = f.Checkpoint.Namespace;
        state["sha256"] = Hash(JsonSerializer.SerializeToUtf8Bytes(new
        {
            domain = "platform-studio.credential-rollback-scoped-state.v2", ns.CustomerId, ns.CellId, ns.VaultNamespaceId, authorityNamespaceId = ns.AuthorityNamespaceId,
            schema = state["schema"]!.GetValue<int>(), revision = state["revision"]!.GetValue<long>(),
            profiles = state["profiles"]!.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal).Select(p => new { id = p.Key, entry = p.Value }).ToArray()
        }, Json));
        var stateBytes = JsonSerializer.SerializeToUtf8Bytes(state, Json); stateFile["bytes"] = Convert.ToBase64String(stateBytes);
        stateFile["length"] = stateBytes.Length; stateFile["sha256"] = Hash(stateBytes);
        var absence = Assert.Single(inventory.AbsenceScopes) with { ProofSha256 = proof["sha256"]!.GetValue<string>() };
        var rebuilt = (CredentialRestrictedRecoveryInventory)typeof(CredentialRestrictedRecoveryInventory).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single(c => c.GetParameters().Length == 6).Invoke([
            ns, inventory.ObservedUtc, inventory.ProfileDependencies, new[] { absence }, inventory.AuthorityRevision, state["sha256"]!.GetValue<string>()]);
        content["inventory"] = new JsonObject { ["schema"] = rebuilt.Schema, ["accessProfile"] = rebuilt.AccessProfile, ["source"] = JsonSerializer.SerializeToNode(typeof(CredentialRestrictedRecoveryInventory).GetProperty("ScopedInventory", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(rebuilt), Json), ["sha256"] = rebuilt.Sha256 }; var bytes = JsonSerializer.SerializeToUtf8Bytes(content, Json);
        artifact["content"] = Convert.ToBase64String(bytes); var signature = original.Signature with { ContentSha256 = Hash(bytes), ContentLength = bytes.Length, InventorySha256 = rebuilt.Sha256 };
        var signatureBytes = JsonSerializer.SerializeToUtf8Bytes(new
        {
            signature.Version, signature.Kind, signature.AccessProfile, signature.SetId, signature.CustomerId, signature.CellId, signature.SourceNamespace, signature.Generation,
            capturedUnixMilliseconds = signature.CapturedUtc.ToUnixTimeMilliseconds(), issuedUnixMilliseconds = signature.IssuedUtc.ToUnixTimeMilliseconds(),
            notBeforeUnixMilliseconds = signature.NotBeforeUtc.ToUnixTimeMilliseconds(), expiresUnixMilliseconds = signature.ExpiresUtc.ToUnixTimeMilliseconds(),
            signature.InventorySha256, signature.ProfileCount, signature.AbsenceProofCount, signature.ContentSha256, signature.ContentLength, signature.KeyId, signature.PublicKeySha256, signature.Algorithm
        }, Json);
        artifact["signature"] = JsonSerializer.SerializeToNode(signature with { Signature = Convert.ToBase64String(f.Signer.SignData(signatureBytes, HashAlgorithmName.SHA256, RSASignaturePadding.Pss)) }, Json);
        return CredentialRestrictedRecoverySets.Parse(JsonSerializer.SerializeToUtf8Bytes(artifact, Json));
    }

    // Deliberately permissive fixture serialization can hash malformed null claims. The production
    // parser must still reject them; invoking its strict serializer here would only test fixture setup.
    static string PublicCheckpointDigest(CredentialRestrictedRecoverySetCheckpoint checkpoint) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
    {
        schema = 3, kind = "credential-restricted-recovery-checkpoint", checkpoint.AuthorityId, checkpoint.AuthorityCommitmentSha256,
        checkpoint.AccessProfile, checkpoint.Sequence, checkpoint.PreviousSha256, acceptedUnixMilliseconds = checkpoint.AcceptedUtc.ToUnixTimeMilliseconds(),
        checkpoint.SourceNamespace, checkpoint.SetAuthority, checkpoint.InventorySha256, checkpoint.RehearsalSha256,
        capturedUnixMilliseconds = checkpoint.CapturedUtc.ToUnixTimeMilliseconds(), issuedUnixMilliseconds = checkpoint.IssuedUtc.ToUnixTimeMilliseconds(),
        notBeforeUnixMilliseconds = checkpoint.NotBeforeUtc.ToUnixTimeMilliseconds(), expiresUnixMilliseconds = checkpoint.ExpiresUtc.ToUnixTimeMilliseconds()
    }, Json));
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static byte[] Security(string path) => (Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorBinaryForm();
    static string[] Snapshot(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root).Order(StringComparer.Ordinal)
        .Select(path => System.IO.Path.GetRelativePath(root, path) + ":" + Hash(Security(path)) + ":" + (Directory.Exists(path) ? "directory" : Hash(File.ReadAllBytes(path)))).ToArray();
    static void SetAccess(string path, byte[] descriptor) { var security = new DirectorySecurity(); security.SetSecurityDescriptorBinaryForm(descriptor, AccessControlSections.Access); new DirectoryInfo(path).SetAccessControl(security); }
    static (string?, string?, bool, string) AccessState(byte[] descriptor) { var raw = new RawSecurityDescriptor(descriptor, 0); var acl = new byte[raw.DiscretionaryAcl!.BinaryLength]; raw.DiscretionaryAcl.GetBinaryForm(acl, 0); return (raw.Owner?.Value, raw.Group?.Value, (raw.ControlFlags & ControlFlags.DiscretionaryAclProtected) != 0, Convert.ToHexString(acl)); }
    static void AddAccess(string path, FileSystemRights rights)
    {
        var security = Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), rights, AccessControlType.Allow));
        if (Directory.Exists(path)) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security); else new FileInfo(path).SetAccessControl((FileSecurity)security);
    }
    static void Protect(string path, bool directory)
    {
        using var user = WindowsIdentity.GetCurrent(); FileSystemSecurity security = directory ? new DirectorySecurity() : new FileSecurity(); security.SetOwner(user.User!); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(user.User!, FileSystemRights.FullControl, directory ? InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit : InheritanceFlags.None, PropagationFlags.None, AccessControlType.Allow));
        if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)security); else new FileInfo(path).SetAccessControl((FileSecurity)security);
    }
    static void AssertPrivate(string path, bool directory)
    {
        using var current = WindowsIdentity.GetCurrent(); var security = directory ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl(); Assert.True(security.AreAccessRulesProtected); Assert.Equal(current.User, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.All(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>(), rule => { Assert.Equal(current.User!.Value, rule.IdentityReference.Value); Assert.Equal(AccessControlType.Allow, rule.AccessControlType); Assert.True((rule.FileSystemRights & FileSystemRights.FullControl) == FileSystemRights.FullControl); });
    }
    static async Task CreateJunction(string link, string target, string root)
    {
        var start = new ProcessStartInfo(System.IO.Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32", "cmd.exe")) { UseShellExecute = false, CreateNoWindow = true, WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        start.Environment.Clear(); foreach (var name in new[] { "SystemRoot", "WINDIR" }) if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value; foreach (var argument in new[] { "/d", "/c", "mklink", "/J", link, target }) start.ArgumentList.Add(argument);
        using var process = Process.Start(start)!; _ = process.SafeHandle; var stdout = process.StandardOutput.ReadToEndAsync(); var stderr = process.StandardError.ReadToEndAsync(); using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(deadline.Token); Assert.Equal(0, process.ExitCode); _ = await stdout; _ = await stderr; } finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }
    sealed class ThrowingClock : TimeProvider { public override DateTimeOffset GetUtcNow() => throw new IOException("Synthetic private clock failure."); }
    sealed class CompletedStageClock(TimeProvider source, string completion, Action callback) : TimeProvider
    {
        internal bool Fired { get; private set; }
        public override DateTimeOffset GetUtcNow()
        { if (!Fired && File.Exists(completion)) { Fired = true; callback(); } return source.GetUtcNow(); }
    }
    sealed class AfterKeyClock(TimeProvider source, CredentialTestKeys keys, int cutoff, Action callback) : TimeProvider
    {
        int samples; internal bool Armed { get; set; } internal bool Fired { get; private set; }
        public override DateTimeOffset GetUtcNow()
        {
            if (Armed && keys.Requests.Count > 0 && ++samples == cutoff) { Armed = false; Fired = true; callback(); }
            return source.GetUtcNow();
        }
    }
    sealed class RefuseAfterAppendClock(TimeProvider source, string file) : TimeProvider { public override DateTimeOffset GetUtcNow() => File.Exists(file) ? throw new IOException("Synthetic postappend observation failure.") : source.GetUtcNow(); }
    sealed class BlockedKeys(ICredentialKeyProvider inner) : ICredentialKeyProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken token) { Entered.TrySetResult(); await Release.Task.WaitAsync(token); return await inner.GetKeyAsync(customerId, cellId, keyId, token); }
    }
    sealed class UncooperativeKeys(ICredentialKeyProvider inner) : ICredentialKeyProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken token)
        { Entered.TrySetResult(); await Release.Task; return await inner.GetKeyAsync(customerId, cellId, keyId, CancellationToken.None); }
    }
    sealed class AuthorityRoot : IDisposable
    {
        public const string KeyId = "synthetic-credential-checkpoint-signer";
        public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "studio-credential-checkpoint-boundary-" + Guid.NewGuid().ToString("N"));
        public string Path => Child("checkpoint-authority"); public CredentialVaultNamespaceIdentity Namespace { get; } public string Pin { get; } public TimeProvider Clock { get; }
        public AuthorityRoot(CredentialVaultNamespaceIdentity? source = null, string? pin = null, TimeProvider? clock = null)
        { Namespace = source ?? new("10000000-0000-0000-0000-000000000001", "20000000-0000-0000-0000-000000000001", Guid.NewGuid().ToString("N"), Hash("synthetic-namespace"u8.ToArray()), Guid.NewGuid().ToString("N"), Hash("synthetic-authority-binding"u8.ToArray())); Pin = pin ?? Hash("synthetic-signer"u8.ToArray()); Clock = clock ?? new CredentialClock(); ActivationProvisionFixtureRoots.Create(Root); }
        public string Child(string name) => System.IO.Path.Combine(Root, name);
        public CredentialRestrictedRecoverySetCheckpointAuthority Provision(TimeProvider? clock = null) => ProvisionAt(Path, clock);
        public CredentialRestrictedRecoverySetCheckpointAuthority ProvisionAt(string path, TimeProvider? clock = null) => CredentialRestrictedRecoverySetCheckpointAuthority.Provision(path, Namespace, KeyId, Pin, clock ?? Clock);
        public CredentialRestrictedRecoverySetCheckpointAuthority Reopen() => ReopenAt(Path);
        public CredentialRestrictedRecoverySetCheckpointAuthority ReopenAt(string path) => new(path, Namespace, KeyId, Pin, Clock);
        public void Dispose()
        {
            var prefix = System.IO.Path.TrimEndingDirectorySeparator(System.IO.Path.GetFullPath(System.IO.Path.GetTempPath())) + System.IO.Path.DirectorySeparatorChar;
            if (!System.IO.Path.GetFullPath(Root).StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(System.IO.Path.GetFileName(Root)[^32..], "N", out _)) throw new IOException("Synthetic cleanup boundary refused.");
            Delete(Root);
        }
        static void Delete(string directory) { foreach (var path in Directory.EnumerateFileSystemEntries(directory)) { if ((File.GetAttributes(path) & FileAttributes.ReparsePoint) != 0) throw new IOException("Synthetic linked cleanup refused."); if (Directory.Exists(path)) Delete(path); else File.Delete(path); } Directory.Delete(directory); }
    }
    sealed class CandidateFixture : IDisposable
    {
        public CredentialFixture Cell { get; } = new(); public RSA Signer { get; } = RSA.Create(2048); public AuthorityRoot Checkpoint { get; }
        public CredentialVaultOptions Options { get; } public CredentialVault Vault { get; }
        public CandidateFixture() { Cell.Clock.Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z"); Options = Cell.Options with { AccessProfile = CredentialVaultAccessProfile.FoundationBoundDispatchOnly, RootPath = System.IO.Path.Combine(Cell.Root, "scoped-checkpoint-source"), RollbackProtection = new(System.IO.Path.Combine(Cell.Root, "scoped-checkpoint-rollback")) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 } }; Vault = CredentialVault.ProvisionStrongBoundDispatch(Options, Cell.Keys, Cell.Clock); Checkpoint = new(Vault.DescribeNamespace(), FoundationAuditAnchors.PublicKeyFingerprint(Signer), Cell.Clock); }
        public async Task Put() { using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token); await Vault.PutAsync(Cell.Scope, secret, Cell.Clock.Now.AddHours(1)); }
        public async Task PutAbsence()
        {
            var absent = Cell.Scope with { AuthProfileId = "profile/scoped-checkpoint-absence", ProjectId = "absence-project", EnvironmentId = "prod" };
            var property = typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.Instance | BindingFlags.NonPublic)!;
            property.SetValue(Vault, (Action<string>)(phase => { if (phase == "PendingPersisted") throw new IOException("Synthetic interrupted initial scope proof."); }));
            using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token);
            try { await Assert.ThrowsAsync<IOException>(() => Vault.PutAsync(absent, secret, Cell.Clock.Now.AddHours(1))); }
            finally { property.SetValue(Vault, null); }
            var pending = await Vault.InspectRollbackStateAsync(absent.AuthProfileId);
            await Vault.ReconcilePendingWriteAsync(absent.AuthProfileId, pending.AuthorityRevision, pending.AuthorityStateSha256, pending.Pending!.PendingSha256, CredentialRollbackResolution.AbandonUnpublished);
        }
        public Task<CredentialRestrictedRecoverySet> Capture(long generation = 1, string keyId = AuthorityRoot.KeyId) => Vault.CaptureRestrictedSignedRecoverySetAsync(Checkpoint.Namespace, Signer, keyId, generation, Cell.Clock.Now, Cell.Clock.Now.AddHours(1));
        public RSA Public() { var key = RSA.Create(); key.ImportSubjectPublicKeyInfo(Signer.ExportSubjectPublicKeyInfo(), out _); return key; }
        public async Task<CredentialRestrictedRecoverySetCheckpoint> Accept(CredentialRestrictedRecoverySetCheckpointAuthority authority, CredentialRestrictedRecoverySet set, CredentialRestrictedRecoverySetCheckpoint? prior = null)
        { using var key = Public(); return await authority.AcceptNextAsync(set, set.Sha256, key, Cell.Keys, prior?.Sequence ?? 0, prior?.Sha256); }
        public void Dispose() { Checkpoint.Dispose(); Signer.Dispose(); Cell.Dispose(); }
    }
}
