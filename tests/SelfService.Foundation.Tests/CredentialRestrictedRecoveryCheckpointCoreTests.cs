using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialRestrictedRecoveryCheckpointCoreTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub)]
    [InlineData(PipelineRequestFactory.AzureDevOps)]
    public async Task Accepted_checkpoint_commits_actual_retained_AES_and_absence_MAC_rehearsal_after_source_roots_removed(string provider)
    {
        using var f = new RestrictedCheckpointFixture(provider); await f.Initialize(); using var selected = await f.Capture(7);
        var pin = CredentialRestrictedRecoverySets.Authority(selected);
        var expected = await new CredentialRestrictedRecoveryRehearsal(f.Namespace, f.Source.Cell.Keys, f.Source.Cell.Clock).RehearseAsync(selected, pin, f.Source.Signer);
        f.Source.RemoveSources(); f.Source.Cell.Keys.Requests.Clear();
        var head = await f.Authority.AcceptNextAsync(selected, selected.Sha256, f.Source.Signer, f.Source.Cell.Keys, 0, null);
        Assert.Equal(1, head.Sequence); Assert.Equal(7, head.SourceGeneration); Assert.Equal(pin, head.SetAuthority);
        Assert.Equal(expected.InventorySha256, head.InventorySha256); Assert.Equal(expected.Sha256, head.RehearsalSha256);
        Assert.Equal("FoundationBoundDispatchOnly", head.AccessProfile); Assert.Equal(f.Namespace, head.SourceNamespace);
        Assert.Equal(new[] { "key-1", "key-2" }, f.Source.Cell.Keys.Requests.Select(r => r.KeyId).Order(StringComparer.Ordinal));
        Assert.Equal(head, f.Authority.ReadCurrent()); Assert.False(head.PromotionAuthorized); Assert.False(head.KeyBackupVerified);
        Assert.False(head.ActivationPerformed); Assert.False(head.ExternalExecutionAuthorized); Assert.Equal("NotAssessed", head.IndependentBackupCoverage);
    }

    [WindowsCredentialFact]
    public async Task Exact_head_CAS_and_strict_source_generation_have_no_source_key_or_append_effect_on_refusal()
    {
        using var f = new RestrictedCheckpointFixture(); await f.Initialize(); using var first = await f.Capture(5);
        var one = await f.Authority.AcceptNextAsync(first, first.Sha256, f.Source.Signer, f.Source.Cell.Keys, 0, null);
        using var sameGeneration = await f.Capture(5); using var next = await f.Capture(8); f.Source.Cell.Keys.Requests.Clear();
        Assert.Equal("CredentialRestrictedRecoveryCheckpointSetGenerationNotIncreasing", (await Assert.ThrowsAsync<FoundationStoreException>(() =>
            f.Authority.AcceptNextAsync(sameGeneration, sameGeneration.Sha256, f.Source.Signer, f.Source.Cell.Keys, 1, one.Sha256))).Code);
        Assert.Equal("CredentialRestrictedRecoveryCheckpointGenerationChanged", (await Assert.ThrowsAsync<FoundationStoreException>(() =>
            f.Authority.AcceptNextAsync(next, next.Sha256, f.Source.Signer, f.Source.Cell.Keys, 0, null))).Code);
        Assert.Empty(f.Source.Cell.Keys.Requests); Assert.Single(Directory.GetFiles(f.Root, "checkpoint-*.json"));
        var two = await f.Authority.AcceptNextAsync(next, next.Sha256, f.Source.Signer, f.Source.Cell.Keys, 1, one.Sha256);
        Assert.Equal(2, two.Sequence); Assert.Equal(one.Sha256, two.PreviousSha256); Assert.Equal(two, f.Authority.ReadCurrent());
    }

    [WindowsCredentialFact]
    public async Task Concrete_shared_readers_keep_append_excluded_until_each_actual_guard_is_released()
    {
        using var f = new RestrictedCheckpointFixture(); await f.Initialize(); var one = await f.Accept(); using var next = await f.Capture(2);
        var first = f.Authority.AcquireReadLease(1, one.Sha256); var second = f.Authority.AcquireReadLease(1, one.Sha256);
        try
        {
            first.AssertCurrent(); second.AssertCurrent(); Assert.Equal(f.Authority.Identity, first.Identity);
            Assert.Equal("CredentialRestrictedRecoveryCheckpointBusy", (await Assert.ThrowsAsync<FoundationStoreException>(() =>
                f.Authority.AcceptNextAsync(next, next.Sha256, f.Source.Signer, f.Source.Cell.Keys, 1, one.Sha256))).Code);
            first.Dispose(); second.AssertCurrent();
            Assert.Equal("CredentialRestrictedRecoveryCheckpointBusy", (await Assert.ThrowsAsync<FoundationStoreException>(() =>
                f.Authority.AcceptNextAsync(next, next.Sha256, f.Source.Signer, f.Source.Cell.Keys, 1, one.Sha256))).Code);
        }
        finally { first.Dispose(); second.Dispose(); }
        Assert.Equal(2, (await f.Authority.AcceptNextAsync(next, next.Sha256, f.Source.Signer, f.Source.Cell.Keys, 1, one.Sha256)).Sequence);
    }

    [WindowsCredentialFact]
    public async Task Accepted_time_callback_cannot_add_unknown_members_then_append_without_a_fresh_whole_chain_read()
    {
        using var f = new RestrictedCheckpointFixture(); await f.Initialize();
        var clock = new PhaseClock(f.Source.Cell.Clock.Now, 4, () =>
        {
            var file = new FileInfo(Path.Combine(f.Root, "unexpected.json")); File.WriteAllText(file.FullName, "synthetic preserved unknown entry");
            var security = file.GetAccessControl(); security.SetAccessRuleProtection(true, true); file.SetAccessControl(security);
        });
        var authority = f.Reopen(clock); clock.Arm();
        Assert.Equal("CredentialRestrictedRecoveryCheckpointInvalid", (await Assert.ThrowsAsync<FoundationStoreException>(() =>
            authority.AcceptNextAsync(f.Source.Set, f.Source.Set.Sha256, f.Source.Signer, f.Source.Cell.Keys, 0, null))).Code);
        Assert.True(clock.Triggered); Assert.Empty(Directory.GetFiles(f.Root, "checkpoint-*.json"));
        Assert.Equal("synthetic preserved unknown entry", File.ReadAllText(Path.Combine(f.Root, "unexpected.json")));
    }

    [WindowsCredentialFact]
    public async Task Cancellation_at_actual_postappend_observation_reports_uncertainty_and_retains_a_readable_consumed_head()
    {
        using var f = new RestrictedCheckpointFixture(); await f.Initialize(); using var cancellation = new CancellationTokenSource();
        var clock = new PostAppendClock(f.Source.Cell.Clock.Now, Path.Combine(f.Root, "checkpoint-0001.json"), cancellation.Cancel);
        var authority = f.Reopen(clock);
        var error = await Assert.ThrowsAsync<FoundationStoreException>(() => authority.AcceptNextAsync(f.Source.Set, f.Source.Set.Sha256,
            f.Source.Signer, f.Source.Cell.Keys, 0, null, cancellation.Token));
        Assert.True(clock.Triggered); Assert.Equal("CredentialRestrictedRecoveryCheckpointAppendUncertain", error.Code); Assert.Equal(503, error.Status);
        var reopened = f.Reopen(f.Source.Cell.Clock); Assert.Equal(1, reopened.ReadCurrent()!.Sequence); f.Source.Cell.Keys.Requests.Clear();
        Assert.Equal("CredentialRestrictedRecoveryCheckpointGenerationChanged", (await Assert.ThrowsAsync<FoundationStoreException>(() =>
            reopened.AcceptNextAsync(f.Source.Set, f.Source.Set.Sha256, f.Source.Signer, f.Source.Cell.Keys, 0, null))).Code);
        Assert.Empty(f.Source.Cell.Keys.Requests);
    }

    [WindowsCredentialFact]
    public async Task Final_selected_set_expiry_is_uncertain_after_append_while_historical_current_read_stays_available()
    {
        using var f = new RestrictedCheckpointFixture(); await f.Initialize();
        var clock = new ExpiringAppendClock(f.Source.Cell.Clock.Now, f.Source.Set.Signature.ExpiresUtc, Path.Combine(f.Root, "checkpoint-0001.json"));
        var authority = f.Reopen(clock);
        var error = await Assert.ThrowsAsync<FoundationStoreException>(() => authority.AcceptNextAsync(f.Source.Set, f.Source.Set.Sha256,
            f.Source.Signer, f.Source.Cell.Keys, 0, null));
        Assert.True(clock.Triggered); Assert.Equal("CredentialRestrictedRecoveryCheckpointAppendUncertain", error.Code);
        f.Source.Cell.Clock.Now = f.Source.Set.Signature.ExpiresUtc;
        var historical = f.Reopen(f.Source.Cell.Clock).ReadCurrent(); Assert.NotNull(historical);
        Assert.Equal(f.Source.Set.Sha256, historical.SetAuthority.ArtifactSha256); Assert.Equal(1, historical.Sequence);
    }

    sealed class PhaseClock(DateTimeOffset now, int phase, Action effect) : TimeProvider
    {
        int reads; bool armed;
        public bool Triggered { get; private set; }
        public void Arm() { armed = true; reads = 0; }
        public override DateTimeOffset GetUtcNow() { if (armed && ++reads == phase) { Triggered = true; effect(); } return now; }
    }
    sealed class PostAppendClock(DateTimeOffset now, string checkpoint, Action effect) : TimeProvider
    {
        public bool Triggered { get; private set; }
        public override DateTimeOffset GetUtcNow() { if (!Triggered && File.Exists(checkpoint)) { Triggered = true; effect(); } return now; }
    }
    sealed class ExpiringAppendClock(DateTimeOffset now, DateTimeOffset expires, string checkpoint) : TimeProvider
    {
        public bool Triggered { get; private set; }
        public override DateTimeOffset GetUtcNow() { if (File.Exists(checkpoint)) { Triggered = true; return expires; } return now; }
    }
}

[SupportedOSPlatform("windows")]
internal sealed class RestrictedCheckpointFixture : IDisposable
{
    internal RestrictedStageFixture Source { get; }
    internal string Root => Path.Combine(Source.Cell.Root, "restricted-checkpoint-authority");
    internal CredentialVaultNamespaceIdentity Namespace => Source.Plan.SourceNamespace;
    internal string SignerSha256 => Convert.ToHexStringLower(SHA256.HashData(Source.Signer.ExportSubjectPublicKeyInfo()));
    internal CredentialRestrictedRecoverySetCheckpointAuthority Authority { get; private set; } = null!;
    internal RestrictedCheckpointFixture(string provider = PipelineRequestFactory.GitHub, string mode = "mixed") { Source = new(provider, mode); }
    internal async Task Initialize()
    {
        await Source.Prepare(); Authority = CredentialRestrictedRecoverySetCheckpointAuthority.Provision(Root, Namespace, "restricted-stage-signer", SignerSha256, Source.Cell.Clock);
    }
    internal CredentialRestrictedRecoverySetCheckpointAuthority Reopen(TimeProvider clock) => new(Root, Namespace, "restricted-stage-signer", SignerSha256, clock);
    internal Task<CredentialRestrictedRecoverySet> Capture(long generation) => Source.Source.CaptureRestrictedSignedRecoverySetAsync(Namespace,
        Source.Signer, "restricted-stage-signer", generation, Source.Cell.Clock.Now, Source.Cell.Clock.Now.AddHours(1));
    internal Task<CredentialRestrictedRecoverySetCheckpoint> Accept() => Authority.AcceptNextAsync(Source.Set, Source.Set.Sha256, Source.Signer, Source.Cell.Keys, 0, null);
    public void Dispose() => Source.Dispose();
}
