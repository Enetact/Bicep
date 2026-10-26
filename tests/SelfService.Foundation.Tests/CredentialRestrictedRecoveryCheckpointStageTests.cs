using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialRestrictedRecoveryCheckpointStageTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub, "mixed", 1, 1)]
    [InlineData(PipelineRequestFactory.AzureDevOps, "mixed", 1, 1)]
    [InlineData(PipelineRequestFactory.GitHub, "positive", 1, 0)]
    [InlineData(PipelineRequestFactory.AzureDevOps, "positive", 1, 0)]
    [InlineData(PipelineRequestFactory.GitHub, "absence", 0, 1)]
    [InlineData(PipelineRequestFactory.AzureDevOps, "absence", 0, 1)]
    [InlineData(PipelineRequestFactory.GitHub, "empty", 0, 0)]
    [InlineData(PipelineRequestFactory.AzureDevOps, "empty", 0, 0)]
    public async Task Current_restricted_checkpoint_stages_retained_bytes_with_policy_and_keyed_completion_after_sources_are_removed(string provider, string mode, int profiles, int absences)
    {
        using var f = new Fixture(provider, mode); await f.Initialize(revoked: true); f.Base.RemoveSources();
        var before = f.Snapshot(); f.ClearKeys(); using var key = f.Public();
        var result = await f.Execute(key);
        Assert.Equal(f.Head, result.Checkpoint); Assert.Equal(f.Identity, result.AuthorityIdentity); Assert.Equal(f.Plan.Sha256, result.StageReceipt.PlanSha256);
        Assert.Equal(f.Set.Sha256, result.StageReceipt.SourceSetSha256); Assert.Equal(profiles, result.StageReceipt.Profiles.Count); Assert.Equal(absences, result.StageReceipt.Absences.Count);
        Assert.Single(f.Destination.Requests); Assert.Equal(new[] { profiles > 0 ? "key-2" : null, absences > 0 ? "key-1" : null }.Where(p => p is not null).Order(StringComparer.Ordinal), f.Cell.Keys.Requests.Select(p => p.KeyId).Order(StringComparer.Ordinal));
        Assert.All(result.StageReceipt.Profiles, p => Assert.False(p.Destination.Enabled));
        Assert.Equal(before, f.Snapshot(f.StageRoot)); Assert.False(Path.Exists(f.Base.Options.RootPath)); Assert.False(Path.Exists(f.Base.SourceAuthority));
        var verified = await new CredentialRestrictedRecoveryStageVerifier(f.Destination, f.Cell.Clock).VerifyAsync(f.Plan, f.Plan.Sha256, result.StageReceipt.Sha256);
        Assert.Equal(profiles, verified.AuthenticatedProfileCount); Assert.Equal(absences, verified.AuthenticatedAbsenceCount);
        Assert.True(verified.DestinationCompletionProofAuthenticated); Assert.Equal(result.StageReceipt.CompletionProofSha256, verified.Receipt.CompletionProofSha256);
        AssertLimits(result, f);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Old_still_valid_restricted_set_and_its_plan_cannot_replace_the_latest_head(bool useCurrentHead)
    {
        using var f = new Fixture(); await f.Initialize(); using var next = await f.Capture(2); var current = await f.Accept(next);
        using var key = f.Public(); _ = CredentialRestrictedRecoverySets.Verify(f.Set, CredentialRestrictedRecoverySets.Authority(f.Set), key, f.Namespace, f.Cell.Clock);
        var before = f.Snapshot(); f.ClearKeys(); var selected = useCurrentHead ? current : f.Head;
        var task = f.Wrapper().StageLatestAsync(f.Set, f.Plan, f.Plan.Sha256, key, selected.Sequence, selected.Sha256);
        if (useCurrentHead) Assert.Equal("credential-restricted-recovery-checkpoint-stage-selection-mismatch", (await Assert.ThrowsAsync<CredentialException>(() => task)).Code);
        else Assert.Equal("CredentialRestrictedRecoveryCheckpointGenerationChanged", (await Assert.ThrowsAsync<FoundationStoreException>(() => task)).Code);
        AssertPreOutputUnchanged(f, before);
    }

    [Theory]
    [InlineData("customer")] [InlineData("cell")] [InlineData("profile")] [InlineData("source")]
    [InlineData("authority")] [InlineData("signer")] [InlineData("public-key")] [InlineData("commitment")]
    public void Full_configured_identity_is_an_independent_constraint(string field)
    {
        using var f = new Fixture(); var identity = f.Identity; var wrong = field switch
        {
            "customer" => identity with { CustomerId = Guid.NewGuid().ToString() }, "cell" => identity with { CellId = Guid.NewGuid().ToString() },
            "profile" => identity with { AccessProfile = "General" },
            "source" => identity with { SourceNamespace = identity.SourceNamespace with { VaultNamespaceId = Guid.NewGuid().ToString("N") } },
            "authority" => identity with { AuthorityId = Guid.NewGuid().ToString("N") }, "signer" => identity with { SignerKeyId = "other-signer" },
            "public-key" => identity with { PublicKeySha256 = new string('a', 64) }, _ => identity with { CommitmentSha256 = new string('b', 64) }
        };
        var before = f.Snapshot();
        Assert.Equal("credential-restricted-recovery-checkpoint-stage-identity-mismatch", Assert.Throws<CredentialException>(() => new CredentialRestrictedRecoveryCheckpointStageExecutor(f.Authority, wrong, f.Executor())).Code);
        Assert.Equal(before, f.Snapshot()); Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests);
    }

    [WindowsCredentialFact]
    public async Task Missing_checkpoint_refuses_before_caller_RSA_keys_or_destination()
    {
        using var f = new Fixture(); await f.Initialize(accept: false); var before = f.Snapshot(); f.ClearKeys(); var disposed = RSA.Create(2048); disposed.Dispose();
        Assert.Equal("CredentialRestrictedRecoveryCheckpointMissing", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Wrapper().StageLatestAsync(f.Set, f.Plan, f.Plan.Sha256, disposed, 1, new string('a', 64)))).Code);
        AssertPreOutputUnchanged(f, before);
    }

    [Theory]
    [InlineData("sequence")] [InlineData("head")] [InlineData("plan-pin")] [InlineData("signer")]
    public async Task Wrong_reviewed_inputs_refuse_before_provider_or_output_access(string input)
    {
        using var f = new Fixture(); await f.Initialize(); var before = f.Snapshot(); f.ClearKeys(); using var key = input == "signer" ? RSA.Create(2048) : f.Public();
        var operation = f.Wrapper().StageLatestAsync(f.Set, f.Plan, input == "plan-pin" ? new string('a', 64) : f.Plan.Sha256, key,
            input == "sequence" ? f.Head.Sequence + 1 : f.Head.Sequence, input == "head" ? new string('b', 64) : f.Head.Sha256);
        if (input is "sequence" or "head") Assert.Equal("CredentialRestrictedRecoveryCheckpointGenerationChanged", (await Assert.ThrowsAsync<FoundationStoreException>(() => operation)).Code);
        else Assert.Equal("credential-restricted-recovery-checkpoint-stage-" + (input == "signer" ? "signer-mismatch" : "selection-mismatch"), (await Assert.ThrowsAsync<CredentialException>(() => operation)).Code);
        AssertPreOutputUnchanged(f, before);
    }

    [Theory]
    [InlineData("source")] [InlineData("destination")]
    public async Task Concrete_checkpoint_read_lease_blocks_append_through_external_key_awaits(string dependency)
    {
        using var f = new Fixture(); await f.Initialize(); using var next = await f.Capture(2); var before = f.Snapshot(); f.ClearKeys();
        var paused = new PausedKeys(dependency == "source" ? f.Cell.Keys : f.Destination); using var key = f.Public();
        var operation = f.Execute(key, dependency == "source" ? paused : null, dependency == "destination" ? paused : null);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); Assert.False(Path.Exists(f.StageRoot));
            var requests = f.Cell.Keys.Requests.Count;
            Assert.Equal("CredentialRestrictedRecoveryCheckpointBusy", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(next))).Code);
            Assert.Equal(requests, f.Cell.Keys.Requests.Count); Assert.Equal(f.Head, f.Authority.ReadCurrent()); paused.Release.TrySetResult();
            Assert.Equal(f.Head, (await operation).Checkpoint);
        }
        finally { paused.Release.TrySetResult(); await Observe(operation); }
        Assert.Equal(before, f.Snapshot(f.StageRoot)); Assert.Equal(2, (await f.Accept(next)).Sequence);
    }

    [WindowsCredentialFact]
    public async Task Concrete_checkpoint_lease_is_still_held_during_completed_output_readback()
    {
        using var f = new Fixture(); await f.Initialize(); using var next = await f.Capture(2); var probed = false;
        var clock = new CompletedClock(f.Cell.Clock, f.StageRoot, () =>
        {
            var error = Assert.Throws<FoundationStoreException>(() => f.Accept(next).GetAwaiter().GetResult());
            Assert.Equal("CredentialRestrictedRecoveryCheckpointBusy", error.Code); probed = true;
        });
        using var key = f.Public(); var result = await f.Execute(key, clock: clock);
        Assert.True(probed); Assert.Equal(f.Head, result.Checkpoint); Assert.Equal(2, (await f.Accept(next)).Sequence);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Caller_cancellation_keeps_the_checkpoint_held_until_the_actual_dependency_returns(bool uncooperative)
    {
        using var f = new Fixture(); await f.Initialize(); using var next = await f.Capture(2); var before = f.Snapshot();
        var paused = new PausedKeys(f.Destination, !uncooperative); using var caller = new CancellationTokenSource(); using var key = f.Public();
        var operation = f.Execute(key, destination: paused, token: caller.Token);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); caller.Cancel();
            if (uncooperative)
            {
                Assert.False(operation.IsCompleted);
                Assert.Equal("CredentialRestrictedRecoveryCheckpointBusy", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(next))).Code);
            }
            paused.Release.TrySetResult(); var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); Assert.Equal(caller.Token, error.CancellationToken);
        }
        finally { paused.Release.TrySetResult(); await Observe(operation); }
        Assert.Equal(before, f.Snapshot()); Assert.False(Path.Exists(f.StageRoot)); Assert.Equal(2, (await f.Accept(next)).Sequence);
    }

    [Theory]
    [InlineData("source")] [InlineData("destination")]
    public async Task Credential_provider_errors_preserve_authority_source_and_absent_output_without_private_error_details(string dependency)
    {
        using var f = new Fixture(); await f.Initialize(); var before = f.Snapshot(); using var key = f.Public();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Execute(key, dependency == "source" ? new FailedKeys(f.Cell.Root) : null, dependency == "destination" ? new FailedKeys(f.Cell.Root) : null));
        Assert.DoesNotContain(f.Cell.Root, error.ToString()); Assert.DoesNotContain(CredentialFixture.Token, error.ToString());
        Assert.Equal(before, f.Snapshot()); Assert.False(Path.Exists(f.StageRoot)); Assert.Equal(f.Head, f.Authority.ReadCurrent());
    }

    [WindowsCredentialFact]
    public async Task Plan_expiry_during_key_await_cannot_publish_or_change_checkpoint()
    {
        using var f = new Fixture(); await f.Initialize(); var before = f.Snapshot(); var paused = new PausedKeys(f.Destination); using var key = f.Public();
        var operation = f.Execute(key, destination: paused);
        try { await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); f.Cell.Clock.Now = f.Plan.ExpiresUtc; paused.Release.TrySetResult(); await Assert.ThrowsAsync<CredentialException>(() => operation); }
        finally { paused.Release.TrySetResult(); await Observe(operation); }
        Assert.Equal(before, f.Snapshot()); Assert.False(Path.Exists(f.StageRoot)); Assert.Equal(f.Head, f.Authority.ReadCurrent());
    }

    [WindowsCredentialFact]
    public async Task Same_owner_checkpoint_tampering_is_unknown_after_completed_output_and_never_repaired()
    {
        using var f = new Fixture(); await f.Initialize(); var marker = Path.Combine(f.CheckpointRoot, CredentialRestrictedRecoverySetCheckpointAuthority.MarkerName); var changed = "{}"u8.ToArray();
        f.Destination.OnGet = () => File.WriteAllBytes(marker, changed); using var key = f.Public();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Execute(key));
        Assert.Equal("credential-restricted-recovery-checkpoint-stage-outcome-unknown", error.Code); Assert.Equal(changed, File.ReadAllBytes(marker));
        Assert.True(File.Exists(Path.Combine(f.StageRoot, "completion.json"))); Assert.True(File.Exists(Path.Combine(f.StageRoot, "rollback", "completion-proof.json")));
        var receipt = JsonSerializer.Deserialize<JsonElement>(File.ReadAllBytes(Path.Combine(f.StageRoot, "completion.json")));
        Assert.Equal("FoundationBoundDispatchOnly", receipt.GetProperty("accessProfile").GetString()); Assert.True(receipt.GetProperty("inactiveRecoveryHold").GetBoolean());
    }

    [WindowsCredentialFact]
    public async Task Cancellation_during_final_checkpoint_assertion_is_unknown_and_preserves_completed_output()
    {
        var guardHeld = false;
        using var caller = new CancellationTokenSource(); using var f = new Fixture(checkpointClock: cell => new CompletedClock(cell.Clock, Path.Combine(cell.Root, "restricted-inactive-stage"), () =>
        {
            Assert.Throws<IOException>(() => new FileStream(Path.Combine(cell.Root, "restricted-inactive-stage", ".credential-recovery-stage.guard"), FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            guardHeld = true; caller.Cancel();
        }));
        await f.Initialize(); using var key = f.Public();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Execute(key, token: caller.Token));
        Assert.True(guardHeld); Assert.Equal("credential-restricted-recovery-checkpoint-stage-outcome-unknown", error.Code); Assert.True(File.Exists(Path.Combine(f.StageRoot, "completion.json")));
        Assert.Equal(f.Head, f.Authority.ReadCurrent());
    }

    [WindowsCredentialFact]
    public async Task Stage_bytes_changed_by_the_final_checkpoint_clock_cannot_return_stale_success()
    {
        string? changedPath = null; var changed = "{}"u8.ToArray();
        using var f = new Fixture(checkpointClock: cell => new CompletedClock(cell.Clock, Path.Combine(cell.Root, "restricted-inactive-stage"), () => File.WriteAllBytes(changedPath!, changed)));
        await f.Initialize(); changedPath = f.Base.ProfilePath; using var key = f.Public();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Execute(key));
        Assert.Equal("credential-restricted-recovery-checkpoint-stage-outcome-unknown", error.Code); Assert.Equal(changed, File.ReadAllBytes(changedPath));
        Assert.True(File.Exists(Path.Combine(f.StageRoot, "completion.json"))); Assert.Equal(f.Head, f.Authority.ReadCurrent());
    }

    [WindowsCredentialFact]
    public async Task Final_checkpoint_clock_jump_past_plan_and_source_lifetimes_is_unknown_and_preserves_completed_output()
    {
        using var f = new Fixture(checkpointClock: cell => new CompletedClock(cell.Clock, Path.Combine(cell.Root, "restricted-inactive-stage"), () => cell.Clock.Now = cell.Clock.Now.AddHours(2)));
        await f.Initialize(); f.ClearKeys(); using var key = f.Public();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Execute(key));
        Assert.Equal("credential-restricted-recovery-checkpoint-stage-outcome-unknown", error.Code);
        Assert.True(f.Cell.Clock.Now >= f.Plan.ExpiresUtc); Assert.True(f.Cell.Clock.Now >= f.Plan.SourceSignature.ExpiresUtc);
        Assert.True(File.Exists(Path.Combine(f.StageRoot, "completion.json"))); Assert.True(File.Exists(Path.Combine(f.StageRoot, "rollback", "completion-proof.json")));
        Assert.Single(f.Destination.Requests); Assert.Equal(f.Head, f.Authority.ReadCurrent());
    }

    [WindowsCredentialFact]
    public async Task Cancellation_before_checkpoint_read_does_not_resolve_keys_or_create_output()
    {
        using var f = new Fixture(); await f.Initialize(); var before = f.Snapshot(); f.ClearKeys(); using var caller = new CancellationTokenSource(); caller.Cancel(); using var key = f.Public();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Execute(key, token: caller.Token));
        Assert.Equal(caller.Token, error.CancellationToken); AssertPreOutputUnchanged(f, before);
    }

    [WindowsCredentialFact]
    public async Task Existing_output_is_preserved_without_adoption_or_provider_access()
    {
        using var f = new Fixture(); await f.Initialize(); Directory.CreateDirectory(f.StageRoot); File.WriteAllText(Path.Combine(f.StageRoot, "preserved-owner.txt"), "Synthetic owner content.");
        var before = f.Snapshot(); f.ClearKeys(); using var key = f.Public(); await Assert.ThrowsAsync<CredentialException>(() => f.Execute(key));
        Assert.Equal(before, f.Snapshot()); Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests);
    }

    [WindowsCredentialFact]
    public async Task Failure_after_fresh_reservation_preserves_partial_output_and_current_checkpoint()
    {
        using var f = new Fixture(); await f.Initialize(); var before = f.Snapshot(); using var key = f.Public();
        var clock = new ReservationClock(f.Cell.Clock.GetUtcNow(), f.Plan.ExpiresUtc, f.StageRoot);
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Execute(key, clock: clock));
        Assert.Equal("credential-restricted-recovery-stage-outcome-unknown", error.Code); Assert.True(Directory.Exists(f.StageRoot));
        Assert.Equal(new[] { ".credential-recovery-stage.guard" }, Directory.GetFileSystemEntries(f.StageRoot).Select(Path.GetFileName));
        Assert.Equal(before, f.Snapshot(f.StageRoot)); Assert.Equal(f.Head, f.Authority.ReadCurrent());
    }

    [WindowsCredentialFact]
    public async Task Caller_public_key_can_be_disposed_after_first_await_without_replacing_the_configured_signer()
    {
        using var f = new Fixture(); await f.Initialize(); using var key = f.Public(); f.Destination.OnGet = key.Dispose;
        var result = await f.Execute(key); Assert.Equal(f.Head, result.Checkpoint); AssertLimits(result, f);
    }

    static void AssertPreOutputUnchanged(Fixture f, string[] before)
    { Assert.False(Path.Exists(f.StageRoot)); Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests); Assert.Equal(before, f.Snapshot()); }
    static void AssertLimits(CredentialRestrictedRecoveryCheckpointStageResult result, Fixture f)
    {
        Assert.Equal("FoundationBoundDispatchOnly", result.AccessProfile); Assert.True(result.CheckpointReadLeaseRetainedDuringStaging); Assert.True(result.InactiveRecoveryHold);
        Assert.False(result.NormalVaultUsable); Assert.False(result.PromotionAuthorized); Assert.False(result.ActivationPerformed); Assert.False(result.NamespaceAdopted); Assert.False(result.SourceHostFenced);
        Assert.False(result.ExternalExecutionAuthorized); Assert.False(result.KeyRetirementAuthorized); Assert.False(result.IndependentKeyBackupVerified); Assert.False(result.CustomerReviewAdmissionPerformed); Assert.False(result.ProductionRecoveryQualified);
        Assert.Equal("NotAssessed", result.IndependentBackupCoverage); Assert.Matches("^[a-f0-9]{64}$", result.Sha256);
        Assert.Matches("^[a-f0-9]{64}$", result.StageReceipt.CompletionCoreSha256); Assert.Matches("^[a-f0-9]{64}$", result.StageReceipt.CompletionProofSha256);
        var text = JsonSerializer.Serialize(result, RestrictedStageFixture.Json); Assert.DoesNotContain(f.Cell.Root, text); Assert.DoesNotContain(CredentialFixture.Token, text);
        Assert.DoesNotContain(f.Base.Positive.AuthProfileId, text); Assert.DoesNotContain("\"ciphertext\":", text, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("\"tag\":", text, StringComparison.OrdinalIgnoreCase);
    }
    static async Task Observe(Task task) { try { await task; } catch { } }
    sealed class CompletedClock(TimeProvider inner, string stage, Action action) : TimeProvider
    {
        bool invoked;
        public override DateTimeOffset GetUtcNow() { if (!invoked && File.Exists(Path.Combine(stage, "completion.json"))) { invoked = true; action(); } return inner.GetUtcNow(); }
    }
    sealed class ReservationClock(DateTimeOffset initial, DateTimeOffset expired, string stage) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => Directory.Exists(stage) ? expired : initial; }
    sealed class PausedKeys(ICredentialKeyProvider inner, bool cooperative = true) : ICredentialKeyProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken token)
        { Entered.TrySetResult(); if (cooperative) await Release.Task.WaitAsync(token); else await Release.Task; return await inner.GetKeyAsync(customer, cell, key, cooperative ? token : CancellationToken.None); }
    }
    sealed class FailedKeys(string path) : ICredentialKeyProvider
    { public ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken token) => throw new IOException("Synthetic provider at " + path + CredentialFixture.Token); }
    sealed class Fixture : IDisposable
    {
        public RestrictedStageFixture Base { get; }
        public CredentialFixture Cell => Base.Cell;
        public CredentialTestKeys Destination => Base.Destination;
        public CredentialVaultNamespaceIdentity Namespace { get; }
        public CredentialRestrictedRecoverySet Set => Base.Set;
        public CredentialRestrictedRecoveryStagePlan Plan => Base.Plan;
        public string StageRoot => Base.StageRoot;
        public string CheckpointRoot => Path.Combine(Cell.Root, "restricted-recovery-checkpoint");
        public CredentialRestrictedRecoverySetCheckpointAuthority Authority { get; }
        public CredentialRestrictedRecoverySetCheckpointAuthorityIdentity Identity { get; }
        public CredentialRestrictedRecoverySetCheckpoint Head { get; private set; } = null!;
        public Fixture(string provider = PipelineRequestFactory.GitHub, string mode = "mixed", Func<CredentialFixture, TimeProvider>? checkpointClock = null)
        {
            Base = new(provider, mode); Namespace = Base.Source.DescribeNamespace();
            Authority = CredentialRestrictedRecoverySetCheckpointAuthority.Provision(CheckpointRoot, Namespace, "restricted-stage-signer", Hash(Base.Signer.ExportSubjectPublicKeyInfo()), checkpointClock?.Invoke(Cell) ?? Cell.Clock);
            Identity = Authority.DescribeIdentity();
        }
        public async Task Initialize(bool revoked = false, bool accept = true) { await Base.Prepare(revoked); if (accept) Head = await Accept(Set); }
        public Task<CredentialRestrictedRecoverySet> Capture(long generation) => Base.Source.CaptureRestrictedSignedRecoverySetAsync(Namespace, Base.Signer, "restricted-stage-signer", generation, Cell.Clock.Now, Cell.Clock.Now.AddHours(1));
        public async Task<CredentialRestrictedRecoverySetCheckpoint> Accept(CredentialRestrictedRecoverySet set)
        { var current = Authority.ReadCurrent(); using var key = Public(); return await Authority.AcceptNextAsync(set, set.Sha256, key, Cell.Keys, current?.Sequence ?? 0, current?.Sha256); }
        public RSA Public() { var key = RSA.Create(); key.ImportSubjectPublicKeyInfo(Base.Signer.ExportSubjectPublicKeyInfo(), out _); return key; }
        public CredentialRestrictedRecoveryStageExecutor Executor(ICredentialKeyProvider? source = null, ICredentialKeyProvider? destination = null, TimeProvider? clock = null) => new(source ?? Cell.Keys, destination ?? Destination, clock ?? Cell.Clock);
        public CredentialRestrictedRecoveryCheckpointStageExecutor Wrapper(ICredentialKeyProvider? source = null, ICredentialKeyProvider? destination = null, TimeProvider? clock = null) => new(Authority, Identity, Executor(source, destination, clock));
        public Task<CredentialRestrictedRecoveryCheckpointStageResult> Execute(RSA key, ICredentialKeyProvider? source = null, ICredentialKeyProvider? destination = null, TimeProvider? clock = null, CancellationToken token = default) =>
            Wrapper(source, destination, clock).StageLatestAsync(Set, Plan, Plan.Sha256, key, Head.Sequence, Head.Sha256, token);
        public void ClearKeys() { Cell.Keys.Requests.Clear(); Destination.Requests.Clear(); }
        public string[] Snapshot(string? exclude = null) => Directory.EnumerateFileSystemEntries(Cell.Root, "*", SearchOption.AllDirectories).Prepend(Cell.Root)
            .Where(p => exclude is null || p != exclude && !p.StartsWith(exclude + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(Cell.Root, p) + ":" + (Directory.Exists(p) ? "directory" : Hash(File.ReadAllBytes(p))) + ":" +
                (Directory.Exists(p) ? (FileSystemSecurity)new DirectoryInfo(p).GetAccessControl() : new FileInfo(p).GetAccessControl()).GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access)).ToArray();
        static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
        public void Dispose() => Base.Dispose();
    }
}
