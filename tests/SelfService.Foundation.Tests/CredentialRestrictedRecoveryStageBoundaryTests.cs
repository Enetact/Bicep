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
public sealed class CredentialRestrictedRecoveryStageBoundaryTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("empty")] [InlineData("positive")] [InlineData("absence")] [InlineData("mixed")]
    public async Task Publicly_rehashed_completion_forgery_requires_actual_destination_MAC_even_without_envelopes(string shape)
    {
        using var f = new Fixture(); await f.Initialize(shape); var original = await f.Stage();
        var revised = f.Rebuild(original, badCompletionTag: true); f.Clear();
        using (f.OpenStructural(revised)) { Assert.Empty(f.Destination.Requests); }
        var before = Snapshot(f.Cell.Root);
        Assert.Equal("credential-restricted-recovery-stage-destination-completion-proof-authentication-failed",
            (await Assert.ThrowsAsync<CredentialException>(() => f.Verify(revised))).Code);
        Assert.Single(f.Destination.Requests); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData("empty")] [InlineData("positive")]
    public async Task Completion_time_and_core_cannot_be_publicly_rewritten_around_an_authentic_old_tag(string shape)
    {
        using var f = new Fixture(); await f.Initialize(shape); var original = await f.Stage(); f.Cell.Clock.Now += TimeSpan.FromSeconds(2);
        var revised = f.Rebuild(original, completed: original.CompletedUtc.AddMilliseconds(1)); f.Clear();
        using (f.OpenStructural(revised)) { Assert.Empty(f.Destination.Requests); }
        var before = Snapshot(f.Cell.Root);
        Assert.Equal("credential-restricted-recovery-stage-destination-completion-proof-authentication-failed",
            (await Assert.ThrowsAsync<CredentialException>(() => f.Verify(revised))).Code);
        Assert.Single(f.Destination.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData("general-envelope")] [InlineData("other-restricted-stage")]
    public async Task Even_a_repaired_completion_MAC_cannot_authorize_ciphertext_from_another_AAD_purpose(string donor)
    {
        using var f = new Fixture(); await f.Initialize("positive", equalMaterial: true); var receipt = await f.Stage();
        JsonObject source;
        if (donor == "general-envelope") source = Read(f.SourcePositivePath);
        else
        {
            var other = f.Replan(Path.Combine(f.Cell.Root, "other-restricted-output")); var otherReceipt = await f.Stage(plan: other);
            source = Read(Path.Combine(f.PathOf(other), "profiles", otherReceipt.Profiles.Single().Destination.ProfileIdSha256 + ".credential.json"));
            Assert.NotEqual(receipt.StageId, otherReceipt.StageId);
        }
        var target = Read(f.OutputProfile(receipt));
        foreach (var field in new[] { "nonce", "ciphertext", "tag" }) target[field] = source[field]!.DeepClone();
        var bytes = Bytes(target); File.WriteAllBytes(f.OutputProfile(receipt), bytes);
        var previous = receipt.Profiles.Single(); var d = previous.Destination;
        var destination = Dependency(d, Hash(bytes), bytes.Length);
        var state = Read(f.StatePath); var entry = state["entries"]!.AsArray().Single()!;
        entry["envelopeSha256"] = destination.EnvelopeSha256; entry["envelopeLength"] = destination.EnvelopeLength;
        Write(f.StatePath, state);
        var revised = f.Rebuild(receipt, profiles: [previous with { Destination = destination }], repairCompletionMac: true);
        f.Clear(); using (f.OpenStructural(revised)) { Assert.Empty(f.Destination.Requests); }
        var before = Snapshot(f.Cell.Root);
        Assert.Equal("credential-restricted-recovery-stage-destination-envelope-authentication-failed",
            (await Assert.ThrowsAsync<CredentialException>(() => f.Verify(revised))).Code);
        Assert.Single(f.Destination.Requests); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData("key-1", "credential-absence-proof-authentication-failed")]
    [InlineData("key-2", "credential-envelope-integrity")]
    public async Task Actual_source_authentication_failure_never_reserves_destination(string keyId, string code)
    {
        using var f = new Fixture(); await f.Initialize(); f.Cell.Keys.Values[keyId] = RandomNumberGenerator.GetBytes(32); f.Clear();
        var before = Snapshot(f.Cell.Root); Assert.Equal(code, (await Assert.ThrowsAsync<CredentialException>(() => f.Stage())).Code);
        Assert.False(Path.Exists(f.StagePath)); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData("")] [InlineData("different")]
    public async Task Exact_reviewed_plan_is_required_before_keys_or_output(string kind)
    {
        using var f = new Fixture(); await f.Initialize(); f.Clear(); var before = Snapshot(f.Cell.Root);
        await Assert.ThrowsAsync<CredentialException>(() => f.Stage(pin: kind.Length == 0 ? "" : new string('b', 64)));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData("root")] [InlineData("purpose")] [InlineData("key")] [InlineData("expiry")]
    public async Task A_new_destination_or_purpose_cannot_reuse_the_prior_review_pin(string field)
    {
        using var f = new Fixture(); await f.Initialize(); var original = f.Plan;
        var changed = f.Replan(field == "root" ? Path.Combine(f.Cell.Root, "different-output") : f.StagePath,
            field == "key" ? f.KeyIdentity with { KeyId = "different-key" } : f.KeyIdentity,
            field == "purpose" ? "Different trusted owner intent" : Fixture.Purpose, f.Cell.Clock.Now.AddMinutes(field == "expiry" ? 9 : 10));
        Assert.NotEqual(original.Sha256, changed.Sha256); f.Clear(); var before = Snapshot(f.Cell.Root);
        await Assert.ThrowsAsync<CredentialException>(() => f.Stage(plan: changed, pin: original.Sha256));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData("customer")] [InlineData("cell")] [InlineData("namespace")] [InlineData("set")]
    [InlineData("key-customer")] [InlineData("key-cell")]
    public async Task Planning_rejects_cross_scope_selection_without_key_lookup_or_filesystem_mutation(string field)
    {
        using var f = new Fixture(); await f.Initialize(); var ns = f.Identity; var authority = f.Authority; var key = f.KeyIdentity;
        if (field == "customer") ns = ns with { CustomerId = Guid.NewGuid().ToString() };
        if (field == "cell") ns = ns with { CellId = Guid.NewGuid().ToString() };
        if (field == "namespace") ns = ns with { VaultNamespaceId = Guid.NewGuid().ToString("N") };
        if (field == "set") authority = authority with { ArtifactSha256 = new string('b', 64) };
        if (field == "key-customer") key = key with { CustomerId = Guid.NewGuid().ToString() };
        if (field == "key-cell") key = key with { CellId = Guid.NewGuid().ToString() };
        f.Clear(); var before = Snapshot(f.Cell.Root);
        Assert.Throws<CredentialException>(() => CredentialRestrictedRecoveryStaging.PreparePlan(f.Selected, authority, ns, f.StagePath, key,
            Fixture.Purpose, f.Cell.Clock.Now.AddMinutes(10), f.Cell.Clock));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData("different-set")] [InlineData("signer")] [InlineData("expired")] [InlineData("rewind")]
    public async Task Selected_signed_set_and_plan_lifetime_are_checked_before_destination_keys(string field)
    {
        using var f = new Fixture(); await f.Initialize(); using var other = await f.Capture(2); using var wrongSigner = RSA.Create(2048);
        if (field == "expired") f.Cell.Clock.Now = f.Plan.ExpiresUtc;
        if (field == "rewind") f.Cell.Clock.Now = f.Plan.PreparedUtc.AddMilliseconds(-1);
        f.Clear(); var before = Snapshot(f.Cell.Root);
        await Assert.ThrowsAsync<CredentialException>(() => f.Stage(set: field == "different-set" ? other : f.Set, signer: field == "signer" ? wrongSigner : f.Signer));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [WindowsCredentialFact]
    public async Task Same_destination_key_ID_with_changed_material_is_refused_before_source_access()
    {
        using var f = new Fixture(); await f.Initialize(); f.Destination.Values[Fixture.KeyId] = RandomNumberGenerator.GetBytes(32); f.Clear();
        var before = Snapshot(f.Cell.Root);
        Assert.Equal("credential-restricted-recovery-stage-destination-key-changed", (await Assert.ThrowsAsync<CredentialException>(() => f.Stage())).Code);
        Assert.Single(f.Destination.Requests); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [WindowsCredentialFact]
    public async Task Reviewed_destination_material_is_frozen_once_across_source_callback_rotation()
    {
        using var f = new Fixture(); await f.Initialize(); var original = f.Destination.Values[Fixture.KeyId].ToArray();
        f.Cell.Keys.OnGet = () => f.Destination.Values[Fixture.KeyId] = RandomNumberGenerator.GetBytes(32); f.Clear();
        CredentialRestrictedRecoveryStageReceipt receipt;
        try { receipt = await f.Stage(); } finally { f.Cell.Keys.OnGet = null; }
        Assert.Single(f.Destination.Requests); await Assert.ThrowsAsync<CredentialException>(() => f.Verify(receipt));
        f.Destination.Values[Fixture.KeyId] = original; await f.Verify(receipt);
    }

    [Theory]
    [InlineData(true)] [InlineData(false)]
    public async Task Source_key_cancellation_never_detaches_work_or_reserves_output(bool cooperative)
    {
        using var f = new Fixture(); await f.Initialize(); var paused = new PausedKeys(f.Cell.Keys, cooperative); using var cancel = new CancellationTokenSource();
        var before = Snapshot(f.Cell.Root); var work = f.Stage(sourceKeys: paused, token: cancel.Token);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancel.Cancel();
        try { if (!cooperative) { await Task.Delay(80); Assert.False(work.IsCompleted); } Assert.False(Path.Exists(f.StagePath)); }
        finally { paused.Release.TrySetResult(); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work); Assert.Equal(cancel.Token, error.CancellationToken);
        Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [WindowsCredentialFact]
    public async Task Parent_custody_changed_by_a_source_callback_is_not_repaired_and_is_rechecked_before_reservation()
    {
        using var f = new Fixture(); await f.Initialize(); var original = Security(f.Cell.Root); string[]? changed = null;
        f.Cell.Keys.OnGet = () => { Widen(f.Cell.Root, FileSystemRights.Write); changed = Snapshot(f.Cell.Root); };
        try { await Assert.ThrowsAsync<CredentialException>(() => f.Stage()); Assert.NotNull(changed); Assert.Equal(changed, Snapshot(f.Cell.Root)); Assert.False(Path.Exists(f.StagePath)); }
        finally { f.Cell.Keys.OnGet = null; RestoreSecurity(f.Cell.Root, original); }
    }

    [Theory]
    [InlineData(".credential-recovery-stage.guard")] [InlineData(".keys.lock")] [InlineData("legacy.credential.json")]
    public async Task Protected_parent_and_leaf_names_cannot_become_a_fresh_stage(string marker)
    {
        using var f = new Fixture(); await f.Initialize(); var parent = Path.Combine(f.Cell.Root, "protected-parent"); Directory.CreateDirectory(parent);
        Directory.CreateDirectory(Path.Combine(parent, marker)); var before = Snapshot(f.Cell.Root);
        Assert.Throws<CredentialException>(() => f.Replan(Path.Combine(parent, "stage")));
        Assert.Throws<CredentialException>(() => f.Replan(Path.Combine(f.Cell.Root, marker)));
        Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData(FileSystemRights.Write)] [InlineData(FileSystemRights.ChangePermissions)]
    public async Task Foreign_parent_write_authority_is_refused_without_repair(FileSystemRights rights)
    {
        using var f = new Fixture(); await f.Initialize(); var parent = Path.Combine(f.Cell.Root, "unsafe-parent"); Directory.CreateDirectory(parent); var original = Security(parent);
        try { Widen(parent, rights); var before = Snapshot(f.Cell.Root); Assert.Throws<CredentialException>(() => f.Replan(Path.Combine(parent, "stage"))); Assert.Equal(before, Snapshot(f.Cell.Root)); }
        finally { RestoreSecurity(parent, original); }
    }

    [WindowsCredentialFact]
    public async Task Null_parent_DACL_is_refused_without_tightening_or_creating_output()
    {
        using var f = new Fixture(); await f.Initialize(); var parent = Path.Combine(f.Cell.Root, "null-parent"); Directory.CreateDirectory(parent); var original = Security(parent);
        var raw = new RawSecurityDescriptor(original, 0); raw.DiscretionaryAcl = null; var bytes = new byte[raw.BinaryLength]; raw.GetBinaryForm(bytes, 0);
        try { RestoreSecurity(parent, bytes); Assert.Throws<CredentialException>(() => f.Replan(Path.Combine(parent, "stage"))); Assert.Empty(Directory.EnumerateFileSystemEntries(parent)); Assert.Null(new RawSecurityDescriptor(Security(parent), 0).DiscretionaryAcl); }
        finally { RestoreSecurity(parent, original); }
    }

    [Theory]
    [InlineData("file")] [InlineData("directory")] [InlineData("partial")]
    public async Task Existing_output_is_never_adopted_and_refusal_precedes_keys(string kind)
    {
        using var f = new Fixture(); await f.Initialize();
        if (kind == "file") File.WriteAllText(f.StagePath, "Synthetic preserved bytes.");
        else { Directory.CreateDirectory(f.StagePath); if (kind == "partial") File.WriteAllText(Path.Combine(f.StagePath, ".credential-recovery-stage.guard"), "Existing unknown partial state."); }
        f.Clear(); var before = Snapshot(f.Cell.Root); await Assert.ThrowsAsync<CredentialException>(() => f.Stage());
        Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [WindowsCredentialFact]
    public async Task Real_competing_fresh_reservations_have_one_winner_without_overwriting_it()
    {
        using var f = new Fixture(); await f.Initialize("positive"); var barrier = new RendezvousKeys(f.Destination.Values[Fixture.KeyId]);
        var source = new ConstantKeys(f.Cell.Keys.Values["key-2"]); using var publicOne = f.Public(); using var publicTwo = f.Public();
        var first = f.Stage(sourceKeys: source, destinationKeys: barrier, signer: publicOne); var second = f.Stage(sourceKeys: source, destinationKeys: barrier, signer: publicTwo);
        var observed = Task.WhenAll(Observe(first), Observe(second));
        try
        {
            await barrier.Both.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.False(Path.Exists(f.StagePath)); barrier.Release.TrySetResult();
            var outcomes = await observed; var accepted = Assert.Single(outcomes, x => x.Receipt is not null);
            var rejected = Assert.Single(outcomes, x => x.Error is not null); Assert.IsType<CredentialException>(rejected.Error);
            Assert.NotEqual("credential-restricted-recovery-stage-outcome-unknown", ((CredentialException)rejected.Error!).Code);
            await f.Verify(accepted.Receipt!);
        }
        finally { barrier.Release.TrySetResult(); await observed; }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Post_reservation_interruption_preserves_partial_state_and_never_retries_or_cleans(bool afterProfile)
    {
        using var f = new Fixture(); await f.Initialize("positive");
        var trigger = afterProfile ? Path.Combine(f.StagePath, "profiles", f.PositiveHash + ".credential.json") : Path.Combine(f.StagePath, ".credential-recovery-stage.guard");
        Assert.Equal("credential-restricted-recovery-stage-outcome-unknown", (await Assert.ThrowsAsync<CredentialException>(() => f.Stage(clock: new FailureClock(f.Cell.Clock.Now, trigger)))).Code);
        Assert.True(File.Exists(Path.Combine(f.StagePath, ".credential-recovery-stage.guard"))); Assert.False(File.Exists(Path.Combine(f.StagePath, "completion.json")));
        f.Clear(); var before = Snapshot(f.Cell.Root); await Assert.ThrowsAsync<CredentialException>(() => f.Stage());
        Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [WindowsCredentialFact]
    public async Task Completed_restricted_stage_is_not_a_normal_vault_and_never_grants_activation()
    {
        using var f = new Fixture(); await f.Initialize(); var receipt = await f.Stage(); f.Clear(); var before = Snapshot(f.Cell.Root);
        Assert.Equal("FoundationBoundDispatchOnly", f.Plan.AccessProfile); Assert.Equal(f.Plan.AccessProfile, receipt.AccessProfile);
        Assert.True(receipt.InactiveRecoveryHold); Assert.False(receipt.NormalVaultUsable); Assert.False(receipt.PromotionAuthorized); Assert.False(receipt.ActivationPerformed);
        Assert.False(receipt.ExternalExecutionAuthorized); Assert.False(receipt.SourceHostFenced); Assert.Equal("NotAssessed", receipt.IndependentBackupCoverage);
        foreach (var root in new[] { f.StagePath, Path.Combine(f.StagePath, "profiles"), Path.Combine(f.StagePath, "rollback") })
        {
            Assert.Throws<CredentialException>(() => new CredentialVault(f.Cell.Options with { RootPath = root }, f.Destination, f.Cell.Clock));
            Assert.Throws<CredentialException>(() => CredentialVault.Provision(f.Cell.Options with { RootPath = root }, f.Destination, f.Cell.Clock));
        }
        Assert.Empty(f.Destination.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
        var text = JsonSerializer.Serialize(new { f.Plan, receipt }, Json); Assert.DoesNotContain(f.Cell.Root, text); Assert.DoesNotContain(CredentialFixture.Token, text);
    }

    static async Task<(CredentialRestrictedRecoveryStageReceipt? Receipt, Exception? Error)> Observe(Task<CredentialRestrictedRecoveryStageReceipt> work)
    { try { return (await work, null); } catch (Exception error) { return (null, error); } }
    static CredentialRecoveryProfileDependency Dependency(CredentialRecoveryProfileDependency d, string sha, long length) =>
        (CredentialRecoveryProfileDependency)typeof(CredentialRecoveryProfileDependency).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single().Invoke([
            d.ProfileIdSha256, d.ScopeSha256, d.ProjectId, d.EnvironmentId, d.Generation, d.Enabled, d.ExpiresUnixMilliseconds, d.SourceKeyId, sha, length]);
    static byte[] Bytes(object value) => JsonSerializer.SerializeToUtf8Bytes(value, Json);
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static JsonObject Read(string path) => JsonNode.Parse(File.ReadAllBytes(path))!.AsObject();
    static void Write(string path, JsonNode value) => File.WriteAllBytes(path, Bytes(value));
    static byte[] Security(string path) => new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm();
    static void RestoreSecurity(string path, byte[] bytes)
    {
        var acl = new DirectorySecurity();
        acl.SetSecurityDescriptorBinaryForm(bytes, AccessControlSections.Access | AccessControlSections.Owner | AccessControlSections.Group);
        new DirectoryInfo(path).SetAccessControl(acl);
    }
    static void Widen(string path, FileSystemRights rights) { var acl = new DirectoryInfo(path).GetAccessControl(); acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), rights, AccessControlType.Allow)); new DirectoryInfo(path).SetAccessControl(acl); }
    static string[] Snapshot(string root)
    {
        var list = new List<string>(); Walk(root); return list.Order(StringComparer.Ordinal).ToArray();
        void Walk(string path)
        {
            var directory = Directory.Exists(path); FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            var prefix = Path.GetRelativePath(root, path) + ":" + Hash(acl.GetSecurityDescriptorBinaryForm());
            if (directory) { list.Add(prefix + ":directory"); foreach (var child in Directory.EnumerateFileSystemEntries(path)) Walk(child); }
            else list.Add(prefix + ":" + Hash(File.ReadAllBytes(path)));
        }
    }
    sealed class FailureClock(DateTimeOffset now, string trigger) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => File.Exists(trigger) ? throw new InvalidOperationException("Synthetic owned post-reservation interruption.") : now; }
    sealed class PausedKeys(ICredentialKeyProvider inner, bool cooperative) : ICredentialKeyProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string c, string e, string id, CancellationToken token)
        { Entered.TrySetResult(); if (cooperative) await Release.Task.WaitAsync(token); else await Release.Task; return await inner.GetKeyAsync(c, e, id, cooperative ? token : CancellationToken.None); }
    }
    sealed class ConstantKeys(byte[] material) : ICredentialKeyProvider
    { public ValueTask<CredentialEncryptionKey> GetKeyAsync(string c, string e, string id, CancellationToken token) { token.ThrowIfCancellationRequested(); return ValueTask.FromResult(new CredentialEncryptionKey(material)); } }
    sealed class RendezvousKeys(byte[] material) : ICredentialKeyProvider
    {
        int entered; internal TaskCompletionSource Both { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string c, string e, string id, CancellationToken token)
        { if (Interlocked.Increment(ref entered) == 2) Both.TrySetResult(); await Release.Task.WaitAsync(token); return new CredentialEncryptionKey(material); }
    }
    sealed class Fixture : IDisposable
    {
        internal const string KeyId = "key-2", Purpose = "Synthetic trusted-owner restricted inactive staging";
        internal CredentialFixture Cell { get; } = new(); internal RSA Signer { get; } = RSA.Create(2048); internal CredentialTestKeys Destination { get; } = new();
        internal CredentialVaultOptions Options { get; } internal CredentialVault Source { get; }
        internal CredentialVaultNamespaceIdentity Identity => Source.DescribeNamespace();
        internal CredentialScope Positive => Cell.Scope with { AuthProfileId = "profile/restricted-stage-present" };
        internal CredentialScope Absent => Cell.Scope with { AuthProfileId = "profile/restricted-stage-absent", ProjectId = "other", EnvironmentId = "qa" };
        internal string PositiveHash => Hash(Encoding.UTF8.GetBytes(Positive.AuthProfileId));
        internal string SourcePositivePath => Path.Combine(Options.RootPath, PositiveHash + ".credential.json");
        internal string StagePath => Path.Combine(Cell.Root, "restricted-inactive-stage"); internal string StatePath => Path.Combine(StagePath, "rollback", "state.json");
        internal CredentialRestrictedRecoverySet Set { get; private set; } = null!; internal CredentialRestrictedRecoverySetAuthority Authority { get; private set; } = null!;
        internal CredentialRestrictedRecoverySetVerification Selected { get; private set; } = null!; internal CredentialRestrictedRecoveryStagePlan Plan { get; private set; } = null!;
        internal CredentialRestrictedRecoveryDestinationKeyIdentity KeyIdentity { get; private set; } = null!;
        internal Fixture()
        {
            Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32); Destination.Values[KeyId] = RandomNumberGenerator.GetBytes(32);
            Options = Cell.Options with { RootPath = Path.Combine(Cell.Root, "restricted-stage-source"), AccessProfile = CredentialVaultAccessProfile.FoundationBoundDispatchOnly,
                RollbackProtection = new(Path.Combine(Cell.Root, "restricted-stage-authority")) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 } };
            Source = CredentialVault.ProvisionStrongBoundDispatch(Options, Cell.Keys, Cell.Clock);
        }
        internal async Task Initialize(string shape = "mixed", bool equalMaterial = false)
        {
            using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token);
            if (shape is "absence" or "mixed")
            {
                var fault = typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.NonPublic | BindingFlags.Instance)!;
                fault.SetValue(Source, (Action<string>)(phase => { if (phase == "PendingPersisted") throw new IOException("Synthetic unpublished source."); }));
                try { await Assert.ThrowsAsync<IOException>(() => Source.PutAsync(Absent, secret, Cell.Clock.Now.AddHours(1))); } finally { fault.SetValue(Source, null); }
                var p = await Source.InspectRollbackStateAsync(Absent.AuthProfileId); await Source.ReconcilePendingWriteAsync(Absent.AuthProfileId, p.AuthorityRevision, p.AuthorityStateSha256, p.Pending!.PendingSha256, CredentialRollbackResolution.AbandonUnpublished);
            }
            if (shape is "positive" or "mixed") await new CredentialVault(Options with { EncryptionKeyId = "key-2" }, Cell.Keys, Cell.Clock).PutAsync(Positive, secret, Cell.Clock.Now.AddHours(1).AddTicks(1234));
            if (equalMaterial) Destination.Values[KeyId] = Cell.Keys.Values[KeyId].ToArray();
            Set = await Capture(); Authority = CredentialRestrictedRecoverySets.Authority(Set); Selected = CredentialRestrictedRecoverySets.Verify(Set, Authority, Signer, Identity, Cell.Clock);
            KeyIdentity = await CredentialRestrictedRecoveryStaging.ObserveDestinationKeyAsync(Identity.CustomerId, Identity.CellId, KeyId, Destination); Plan = Replan(StagePath);
        }
        internal Task<CredentialRestrictedRecoverySet> Capture(long generation = 1) => Source.CaptureRestrictedSignedRecoverySetAsync(Identity, Signer, "restricted-stage-signer", generation, Cell.Clock.Now, Cell.Clock.Now.AddHours(1));
        internal CredentialRestrictedRecoveryStagePlan Replan(string root, CredentialRestrictedRecoveryDestinationKeyIdentity? key = null, string purpose = Purpose, DateTimeOffset? expires = null) =>
            CredentialRestrictedRecoveryStaging.PreparePlan(Selected, Authority, Identity, root, key ?? KeyIdentity, purpose, expires ?? Cell.Clock.Now.AddMinutes(10), Cell.Clock);
        internal Task<CredentialRestrictedRecoveryStageReceipt> Stage(CredentialRestrictedRecoveryStagePlan? plan = null, string? pin = null, CredentialRestrictedRecoverySet? set = null, RSA? signer = null,
            ICredentialKeyProvider? sourceKeys = null, ICredentialKeyProvider? destinationKeys = null, TimeProvider? clock = null, CancellationToken token = default) =>
            new CredentialRestrictedRecoveryStageExecutor(sourceKeys ?? Cell.Keys, destinationKeys ?? Destination, clock ?? Cell.Clock).StageAsync(set ?? Set, plan ?? Plan, pin ?? (plan ?? Plan).Sha256, signer ?? Signer, token);
        internal Task<CredentialRestrictedRecoveryStageVerification> Verify(CredentialRestrictedRecoveryStageReceipt receipt) => new CredentialRestrictedRecoveryStageVerifier(Destination, Cell.Clock).VerifyAsync(Plan, Plan.Sha256, receipt.Sha256);
        internal string PathOf(CredentialRestrictedRecoveryStagePlan plan) => (string)typeof(CredentialRestrictedRecoveryStagePlan).GetProperty("RootPath", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(plan)!;
        internal string OutputProfile(CredentialRestrictedRecoveryStageReceipt receipt) => Path.Combine(StagePath, "profiles", receipt.Profiles.Single().Destination.ProfileIdSha256 + ".credential.json");
        internal RSA Public() { var key = RSA.Create(); key.ImportSubjectPublicKeyInfo(Signer.ExportSubjectPublicKeyInfo(), out _); return key; }
        internal void Clear() { Cell.Keys.Requests.Clear(); Destination.Requests.Clear(); }
        internal IDisposable OpenStructural(CredentialRestrictedRecoveryStageReceipt receipt) => (IDisposable)typeof(CredentialRestrictedRecoveryStageVerifier).GetMethod("OpenPrepared", BindingFlags.NonPublic | BindingFlags.Instance)!
            .Invoke(new CredentialRestrictedRecoveryStageVerifier(Destination, Cell.Clock), [Plan, Plan.Sha256, receipt.Sha256])!;
        internal CredentialRestrictedRecoveryStageReceipt Rebuild(CredentialRestrictedRecoveryStageReceipt r, bool badCompletionTag = false,
            DateTimeOffset? completed = null, IReadOnlyList<CredentialRecoveryStagedProfile>? profiles = null, bool repairCompletionMac = false)
        {
            var actualProfiles = profiles ?? r.Profiles; var finish = completed ?? r.CompletedUtc;
            var core = (string)typeof(CredentialRestrictedRecoveryStaging).GetMethod("CompletionCoreDigest", BindingFlags.NonPublic | BindingFlags.Static)!
                .Invoke(null, [Plan, r.StageId, r.StagedVaultNamespaceId, r.StagedAuthorityNamespaceId, r.BindingSha256, r.ReservedUtc, finish, actualProfiles])!;
            var state = Read(StatePath); state["completionCoreSha256"] = core; state["sha256"] = ""; var stateSha = Hash(Bytes(state)); state["sha256"] = stateSha; Write(StatePath, state);
            CredentialRestrictedRecoveryStageReceipt Make(string proofSha) => (CredentialRestrictedRecoveryStageReceipt)typeof(CredentialRestrictedRecoveryStageReceipt)
                .GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single().Invoke([r.PlanSha256, r.SourceSetSha256, r.StageId, r.StagedVaultNamespaceId,
                    r.StagedAuthorityNamespaceId, r.DestinationRootCommitmentSha256, r.DestinationKey, r.BindingSha256, stateSha, core, proofSha, r.ReservedUtc, finish, actualProfiles, r.Absences]);
            var provisional = Make(r.CompletionProofSha256); var proofPath = Path.Combine(StagePath, "rollback", "completion-proof.json"); var proof = Read(proofPath);
            if (badCompletionTag) proof["tag"] = new string('0', 64);
            if (repairCompletionMac) proof["tag"] = CompletionMac(Plan, provisional, proof, Destination.Values[KeyId]);
            var proofSha = Hash(Bytes(new { payload = CompletionPayload(Plan, provisional, proof), tag = proof["tag"]!.GetValue<string>() }));
            proof["sha256"] = proofSha; Write(proofPath, proof); var result = Make(proofSha); File.WriteAllBytes(Path.Combine(StagePath, "completion.json"), Bytes(result)); return result;
        }
        public void Dispose() { Set?.Dispose(); Signer.Dispose(); Cell.Dispose(); }
    }
    static object CompletionPayload(CredentialRestrictedRecoveryStagePlan plan, CredentialRestrictedRecoveryStageReceipt r, JsonObject p) => new
    {
        domain = "platform-studio.credential-restricted-stage-completion.v1", version = p["version"]!.GetValue<int>(), kind = p["kind"]!.GetValue<string>(),
        accessProfile = p["accessProfile"]!.GetValue<string>(), algorithm = p["algorithm"]!.GetValue<string>(),
        plan.SourceNamespace, r.Schema, r.PlanSha256, r.SourceSetSha256, r.StageId, r.StagedVaultNamespaceId, r.StagedAuthorityNamespaceId,
        r.DestinationRootCommitmentSha256, r.DestinationKey, r.BindingSha256, r.StateSha256, r.CompletionCoreSha256,
        reservedUnixMilliseconds = r.ReservedUtc.ToUnixTimeMilliseconds(), completedUnixMilliseconds = r.CompletedUtc.ToUnixTimeMilliseconds(),
        r.Profiles, r.Absences, r.InactiveRecoveryHold, r.NormalVaultUsable, r.PromotionAuthorized, r.ActivationPerformed,
        r.SourceHostFenced, r.ExternalExecutionAuthorized, r.KeyMaterialExported, r.KeyRetirementAuthorized, r.IndependentBackupCoverage, r.ProductionRecoveryQualified
    };
    static string CompletionMac(CredentialRestrictedRecoveryStagePlan plan, CredentialRestrictedRecoveryStageReceipt r, JsonObject proof, byte[] material)
    {
        var derived = HKDF.DeriveKey(HashAlgorithmName.SHA256, material, 32, Encoding.UTF8.GetBytes("platform-studio.credential-restricted-stage-completion.v1.hkdf-salt"), Encoding.UTF8.GetBytes("platform-studio.credential-restricted-stage-completion.v1.mac-key"));
        var payload = Bytes(CompletionPayload(plan, r, proof));
        try { return Convert.ToHexStringLower(HMACSHA256.HashData(derived, payload)); }
        finally { CryptographicOperations.ZeroMemory(derived); CryptographicOperations.ZeroMemory(payload); }
    }
}
