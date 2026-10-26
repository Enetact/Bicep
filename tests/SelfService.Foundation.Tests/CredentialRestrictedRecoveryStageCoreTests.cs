using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialRestrictedRecoveryStageCoreTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub, false)]
    [InlineData(PipelineRequestFactory.GitHub, true)]
    [InlineData(PipelineRequestFactory.AzureDevOps, false)]
    [InlineData(PipelineRequestFactory.AzureDevOps, true)]
    public async Task Original_full_profile_expiry_offset_fractional_ticks_generation_and_revocation_survive_new_AAD(string provider, bool revoked)
    {
        using var f = new RestrictedStageFixture(provider, "positive"); await f.Prepare(revoked);
        using var original = JsonDocument.Parse(File.ReadAllBytes(f.SourcePositivePath));
        var sourceProfile = original.RootElement.GetProperty("profile").GetRawText(); var receipt = await f.Stage();
        using var staged = JsonDocument.Parse(File.ReadAllBytes(f.ProfilePath)); var destinationProfile = staged.RootElement.GetProperty("profile").GetRawText();
        Assert.Equal(sourceProfile, destinationProfile); Assert.Equal(revoked ? 2 : 1, receipt.Profiles[0].Destination.Generation);
        Assert.Equal(!revoked, receipt.Profiles[0].Destination.Enabled); Assert.Equal(3, staged.RootElement.GetProperty("version").GetInt32());
        Assert.Equal(receipt.BindingSha256, staged.RootElement.GetProperty("bindingSha256").GetString());
        f.RemoveSources(); f.Cell.Keys.Values.Clear(); f.Cell.Clock.Now = f.Cell.Clock.Now.AddHours(2);
        var result = await f.Verify(receipt); Assert.True(result.DestinationCompletionProofAuthenticated); Assert.True(result.OutputCiphertextsAuthenticated);
        Assert.False(result.NormalVaultUsable); Assert.False(result.PromotionAuthorized);
    }

    [WindowsCredentialFact]
    public async Task Restricted_destination_identity_has_a_distinct_material_domain_and_exact_plan_is_public_metadata_only()
    {
        using var f = new RestrictedStageFixture(mode: "empty"); await f.Prepare();
        var ordinary = await CredentialRecoveryStaging.ObserveDestinationKeyAsync(f.Cell.Scope.CustomerId, f.Cell.Scope.CellId, "restricted-destination", f.Destination);
        Assert.NotEqual(ordinary.CommitmentSha256, f.Plan.DestinationKey.CommitmentSha256);
        Assert.Equal(ordinary.KeyId, f.Plan.DestinationKey.KeyId); Assert.False(Directory.Exists(f.StageRoot));
        var json = JsonSerializer.Serialize(f.Plan, RestrictedStageFixture.Json);
        Assert.Contains("FoundationBoundDispatchOnly", json); Assert.DoesNotContain(f.StageRoot, json);
        Assert.DoesNotContain(f.SourceAuthority, json); Assert.DoesNotContain(CredentialFixture.Token, json);
        var before = f.Plan.AffectedScopes.ToArray(); var returned = Assert.IsAssignableFrom<IList<FoundationCredentialRecoveryScope>>(f.Plan.AffectedScopes);
        Assert.Throws<NotSupportedException>(() => returned.Add(new("changed", "scope"))); Assert.Equal(before, f.Plan.AffectedScopes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Final_observation_clock_cannot_cancel_or_change_written_bytes_then_return_success(bool cancel)
    {
        using var f = new RestrictedStageFixture(mode: "positive"); await f.Prepare(); using var cancellation = new CancellationTokenSource();
        var clock = new CompletionClock(f.Cell.Clock.Now, f.StageRoot, () =>
        { if (cancel) cancellation.Cancel(); else File.AppendAllText(f.ProfilePath, " "); });
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Stage(clock: clock, token: cancellation.Token));
        Assert.True(clock.Triggered); Assert.Equal("credential-restricted-recovery-stage-outcome-unknown", error.Code);
        Assert.True(File.Exists(Path.Combine(f.StageRoot, "completion.json"))); Assert.True(File.Exists(f.GuardPath));
        if (cancel) Assert.True(cancellation.IsCancellationRequested); else Assert.EndsWith(" ", File.ReadAllText(f.ProfilePath));
    }
    sealed class CompletionClock(DateTimeOffset now, string root, Action effect) : TimeProvider
    {
        public bool Triggered { get; private set; }
        public override DateTimeOffset GetUtcNow()
        { if (!Triggered && File.Exists(Path.Combine(root, "completion.json"))) { Triggered = true; effect(); } return now; }
    }

    [WindowsCredentialFact]
    public async Task Final_parent_custody_change_preserves_completed_output_but_never_returns_success()
    {
        using var f = new RestrictedStageFixture(mode: "positive"); await f.Prepare();
        var parent = new DirectoryInfo(f.Cell.Root); var original = parent.GetAccessControl();
        var clock = new CompletionClock(f.Cell.Clock.Now, f.StageRoot, () =>
        {
            var changed = parent.GetAccessControl(); changed.AddAccessRule(new FileSystemAccessRule(
                new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.Modify, AccessControlType.Allow)); parent.SetAccessControl(changed);
        });
        try
        {
            var error = await Assert.ThrowsAsync<CredentialException>(() => f.Stage(clock: clock));
            Assert.True(clock.Triggered); Assert.Equal("credential-restricted-recovery-stage-outcome-unknown", error.Code);
            Assert.True(File.Exists(Path.Combine(f.StageRoot, "completion.json")));
        }
        finally { parent.SetAccessControl(original); }
    }
}

// Actual disposable synthetic custody, reusable by the independent verifier integration tests.
[SupportedOSPlatform("windows")]
internal sealed class RestrictedStageFixture : IDisposable
{
    internal static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    internal CredentialFixture Cell { get; }
    internal CredentialTestKeys Destination { get; } = new();
    internal RSA Signer { get; } = RSA.Create(2048);
    internal CredentialVaultOptions Options { get; }
    internal CredentialVault Source { get; }
    internal string SourceAuthority => Options.RollbackProtection!.AuthorityDirectory;
    internal string StageRoot => Path.Combine(Cell.Root, "restricted-inactive-stage");
    internal string GuardPath => Path.Combine(StageRoot, ".credential-recovery-stage.guard");
    internal CredentialScope Positive => Cell.Scope with { AuthProfileId = "profile/restricted-stage-positive" };
    internal CredentialScope Absent => Cell.Scope with { AuthProfileId = "profile/restricted-stage-absent", ProjectId = "other", EnvironmentId = "qa" };
    internal string SourcePositivePath => Path.Combine(Options.RootPath, Hash(Positive.AuthProfileId) + ".credential.json");
    internal string ProfilePath => Path.Combine(StageRoot, "profiles", Hash(Positive.AuthProfileId) + ".credential.json");
    internal CredentialRestrictedRecoverySet Set { get; private set; } = null!;
    internal CredentialRestrictedRecoveryStagePlan Plan { get; private set; } = null!;
    internal CredentialRestrictedRecoveryStageReceipt Receipt { get; private set; } = null!;
    readonly string mode;
    internal RestrictedStageFixture(string provider = PipelineRequestFactory.GitHub, string mode = "mixed")
    {
        if (mode is not ("mixed" or "positive" or "absence" or "empty")) throw new ArgumentException(nameof(mode));
        this.mode = mode; Cell = new(provider); Cell.Clock.Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
        Destination.Values["restricted-destination"] = RandomNumberGenerator.GetBytes(32);
        Options = Cell.Options with { AccessProfile = CredentialVaultAccessProfile.FoundationBoundDispatchOnly,
            RootPath = Path.Combine(Cell.Root, "restricted-stage-source-vault"),
            RollbackProtection = new(Path.Combine(Cell.Root, "restricted-stage-source-authority")) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 } };
        Source = CredentialVault.ProvisionStrongBoundDispatch(Options, Cell.Keys, Cell.Clock);
    }
    internal async Task Prepare(bool revoked = false)
    {
        if (mode is "mixed" or "absence")
        {
            var fault = typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.NonPublic | BindingFlags.Instance)!;
            fault.SetValue(Source, (Action<string>)(phase => { if (phase == "PendingPersisted") throw new IOException("Synthetic interrupted source."); }));
            try { using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token); await Assert.ThrowsAsync<IOException>(() => Source.PutAsync(Absent, secret, Cell.Clock.Now.AddHours(1))); }
            finally { fault.SetValue(Source, null); }
            var pending = await Source.InspectRollbackStateAsync(Absent.AuthProfileId);
            await Source.ReconcilePendingWriteAsync(Absent.AuthProfileId, pending.AuthorityRevision, pending.AuthorityStateSha256, pending.Pending!.PendingSha256, CredentialRollbackResolution.AbandonUnpublished);
        }
        if (mode is "mixed" or "positive")
        {
            Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32); var writer = new CredentialVault(Options with { EncryptionKeyId = "key-2" }, Cell.Keys, Cell.Clock);
            using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token);
            await writer.PutAsync(Positive, secret, Cell.Clock.Now.AddHours(1).AddTicks(1234).ToOffset(TimeSpan.FromHours(5.5)));
            if (revoked) await writer.RevokeAsync(Positive.AuthProfileId, 1);
        }
        var ns = Source.DescribeNamespace(); Set = await Source.CaptureRestrictedSignedRecoverySetAsync(ns, Signer, "restricted-stage-signer", 1, Cell.Clock.Now, Cell.Clock.Now.AddHours(1));
        var authority = CredentialRestrictedRecoverySets.Authority(Set); var verified = CredentialRestrictedRecoverySets.Verify(Set, authority, Signer, ns, Cell.Clock);
        var destination = await CredentialRestrictedRecoveryStaging.ObserveDestinationKeyAsync(ns.CustomerId, ns.CellId, "restricted-destination", Destination);
        Plan = CredentialRestrictedRecoveryStaging.PreparePlan(verified, authority, ns, StageRoot, destination, "Synthetic inactive restricted stage", Cell.Clock.Now.AddMinutes(20), Cell.Clock);
    }
    internal async Task<CredentialRestrictedRecoveryStageReceipt> Initialize(bool revoked = false) { await Prepare(revoked); return await Stage(); }
    internal async Task<CredentialRestrictedRecoveryStageReceipt> Stage(ICredentialKeyProvider? sourceKeys = null, TimeProvider? clock = null, CancellationToken token = default)
    {
        Receipt = await new CredentialRestrictedRecoveryStageExecutor(sourceKeys ?? Cell.Keys, Destination, clock ?? Cell.Clock).StageAsync(Set, Plan, Plan.Sha256, Signer, token); return Receipt;
    }
    internal Task<CredentialRestrictedRecoveryStageVerification> Verify(ICredentialKeyProvider? destinationKeys = null, CancellationToken token = default) => Verify(Receipt, destinationKeys, token);
    internal Task<CredentialRestrictedRecoveryStageVerification> Verify(CredentialRestrictedRecoveryStageReceipt receipt, ICredentialKeyProvider? destinationKeys = null, CancellationToken token = default) =>
        new CredentialRestrictedRecoveryStageVerifier(destinationKeys ?? Destination, Cell.Clock).VerifyAsync(Plan, Plan.Sha256, receipt.Sha256, token);
    internal string[] Snapshot() => Directory.GetFiles(StageRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(p => Path.GetRelativePath(StageRoot, p) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
    internal void RemoveSources()
    {
        Directory.Move(Options.RootPath, Path.Combine(Cell.Root, "removed-restricted-source-vault"));
        Directory.Move(SourceAuthority, Path.Combine(Cell.Root, "removed-restricted-source-authority"));
    }
    internal static string Hash(string value) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(value)));
    public void Dispose() { Set?.Dispose(); Signer.Dispose(); Cell.Dispose(); }
}
