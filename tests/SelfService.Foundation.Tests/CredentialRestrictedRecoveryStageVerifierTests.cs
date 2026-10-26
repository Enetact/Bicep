using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialRestrictedRecoveryStageVerifierTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub, "mixed")]
    [InlineData(PipelineRequestFactory.AzureDevOps, "mixed")]
    [InlineData(PipelineRequestFactory.GitHub, "positive")]
    [InlineData(PipelineRequestFactory.AzureDevOps, "positive")]
    [InlineData(PipelineRequestFactory.GitHub, "absence")]
    [InlineData(PipelineRequestFactory.AzureDevOps, "absence")]
    [InlineData(PipelineRequestFactory.GitHub, "empty")]
    [InlineData(PipelineRequestFactory.AzureDevOps, "empty")]
    public async Task Historical_output_authenticates_with_only_destination_key_including_empty_completion(string provider, string mode)
    {
        using var f = new RestrictedStageFixture(provider, mode); await f.Initialize(); f.RemoveSources(); f.Cell.Keys.Values.Clear(); f.Cell.Keys.Fail = true;
        f.Cell.Keys.Requests.Clear(); f.Destination.Requests.Clear(); f.Cell.Clock.Now = f.Cell.Clock.Now.AddDays(2); var before = Snapshot(f.Cell.Root);
        var result = await f.Verify();
        Assert.Equal(mode is "mixed" or "positive" ? 1 : 0, result.AuthenticatedProfileCount);
        Assert.Equal(mode is "mixed" or "absence" ? 1 : 0, result.AuthenticatedAbsenceCount);
        Assert.True(result.DestinationCompletionProofAuthenticated); Assert.Single(f.Destination.Requests); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(before, Snapshot(f.Cell.Root)); AssertPublic(result, f);
    }

    [Theory]
    [InlineData("plan")] [InlineData("receipt")]
    public async Task Exact_reviewed_selections_are_required_before_any_destination_lookup(string selection)
    {
        using var f = new RestrictedStageFixture(); await f.Initialize(); f.Destination.Requests.Clear(); var before = Snapshot(f.Cell.Root);
        await Assert.ThrowsAsync<CredentialException>(() => new CredentialRestrictedRecoveryStageVerifier(f.Destination, f.Cell.Clock).VerifyAsync(
            f.Plan, selection == "plan" ? new string('a', 64) : f.Plan.Sha256, selection == "receipt" ? new string('b', 64) : f.Receipt.Sha256));
        Assert.Empty(f.Destination.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData("guard")] [InlineData("marker")] [InlineData("receipt")] [InlineData("completion-proof")]
    public async Task Required_stage_parts_are_not_recreated_and_refuse_before_keys(string member)
    {
        using var f = new RestrictedStageFixture(); await f.Initialize(); File.Delete(Member(f, member));
        f.Destination.Requests.Clear(); var before = Snapshot(f.Cell.Root);
        await Assert.ThrowsAsync<CredentialException>(() => f.Verify()); Assert.Empty(f.Destination.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData("root")] [InlineData("guard")] [InlineData("profile")] [InlineData("receipt")] [InlineData("rollback")] [InlineData("completion-proof")]
    public async Task Widened_native_custody_is_rejected_before_keys_without_repair(string member)
    {
        using var f = new RestrictedStageFixture(); await f.Initialize(); var path = Member(f, member); var original = Security(path);
        try
        {
            AddWorldRead(path); var before = Snapshot(f.Cell.Root); f.Destination.Requests.Clear();
            await Assert.ThrowsAsync<CredentialException>(() => f.Verify()); Assert.Empty(f.Destination.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
        }
        finally { SetSecurity(path, original); }
    }

    [Theory]
    [InlineData("directory")] [InlineData("partial")] [InlineData("active-marker")] [InlineData("missing-profile")]
    public async Task Unknown_or_partial_tree_cannot_be_accepted_as_a_subset(string attack)
    {
        using var f = new RestrictedStageFixture(); await f.Initialize();
        if (attack == "directory") Directory.CreateDirectory(Path.Combine(f.StageRoot, "unexpected"));
        else if (attack == "missing-profile") File.Delete(f.ProfilePath);
        else PrivateFile(Path.Combine(f.StageRoot, attack == "partial" ? ".write-interrupted" : ".credential-namespace.json"), "Synthetic unknown retained entry.");
        f.Destination.Requests.Clear(); var before = Snapshot(f.Cell.Root);
        await Assert.ThrowsAsync<CredentialException>(() => f.Verify()); Assert.Empty(f.Destination.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData("missing")] [InlineData("wrong-material")] [InlineData("exception")] [InlineData("credential-exception")] [InlineData("unrelated-cancellation")]
    public async Task Destination_provider_failure_is_opaque_and_cannot_use_source_keys(string attack)
    {
        using var f = new RestrictedStageFixture(); await f.Initialize(); f.Cell.Keys.Requests.Clear(); f.Destination.Requests.Clear();
        if (attack == "missing") f.Destination.Values.Remove(f.Plan.DestinationKey.KeyId);
        else if (attack == "wrong-material") f.Destination.Values[f.Plan.DestinationKey.KeyId] = RandomNumberGenerator.GetBytes(32);
        else f.Destination.OnGet = () => throw attack switch
        {
            "credential-exception" => new CredentialException(CredentialFixture.Token + f.Cell.Root),
            "unrelated-cancellation" => new OperationCanceledException(CredentialFixture.Token + f.Cell.Root),
            _ => new InvalidOperationException(CredentialFixture.Token + f.Cell.Root)
        };
        var before = Snapshot(f.Cell.Root); var error = await Assert.ThrowsAsync<CredentialException>(() => f.Verify());
        AssertOpaque(error, f); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData("state")] [InlineData("profile")] [InlineData("binding")] [InlineData("receipt")] [InlineData("completion-proof")] [InlineData("unknown")]
    public async Task Whole_tree_is_rechecked_after_the_external_destination_lookup(string member)
    {
        using var f = new RestrictedStageFixture(); await f.Initialize(); string[]? changed = null;
        f.Destination.OnGet = () =>
        {
            if (member == "unknown") PrivateFile(Path.Combine(f.StageRoot, "appeared-during-key.txt"), "Synthetic late entry.");
            else File.AppendAllText(Member(f, member), " ");
            changed = Snapshot(f.Cell.Root);
        };
        f.Destination.Requests.Clear(); await Assert.ThrowsAsync<CredentialException>(() => f.Verify());
        Assert.Single(f.Destination.Requests); Assert.NotNull(changed); Assert.Equal(changed, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData("root")] [InlineData("completion-proof")]
    public async Task Late_custody_changes_are_not_repaired_or_accepted(string member)
    {
        using var f = new RestrictedStageFixture(); await f.Initialize(); var path = Member(f, member); var original = Security(path); string[]? changed = null;
        try
        {
            f.Destination.OnGet = () => { AddWorldRead(path); changed = Snapshot(f.Cell.Root); };
            await Assert.ThrowsAsync<CredentialException>(() => f.Verify()); Assert.NotNull(changed); Assert.Equal(changed, Snapshot(f.Cell.Root));
        }
        finally { f.Destination.OnGet = null; SetSecurity(path, original); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Cancellation_keeps_the_actual_stage_guard_until_destination_provider_returns(bool uncooperative)
    {
        using var f = new RestrictedStageFixture(); await f.Initialize(); var before = Snapshot(f.Cell.Root);
        using var caller = new CancellationTokenSource(); var paused = new PausedKeys(f.Destination, !uncooperative); var operation = f.Verify(paused, caller.Token);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); caller.Cancel();
            if (uncooperative) { await Task.Delay(60); Assert.False(operation.IsCompleted); Assert.Throws<IOException>(() => new FileStream(f.GuardPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)); }
            paused.Release.TrySetResult(); var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); Assert.Equal(caller.Token, error.CancellationToken);
        }
        finally { paused.Release.TrySetResult(); try { await operation; } catch { } }
        using (new FileStream(f.GuardPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [WindowsCredentialFact]
    public async Task Future_completion_is_refused_before_destination_lookup()
    {
        using var f = new RestrictedStageFixture(); await f.Initialize(); f.Cell.Clock.Now = f.Receipt.CompletedUtc.AddMilliseconds(-1); f.Destination.Requests.Clear();
        var before = Snapshot(f.Cell.Root); await Assert.ThrowsAsync<CredentialException>(() => f.Verify()); Assert.Empty(f.Destination.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Final_observation_clock_mutation_or_cancellation_cannot_publish_verification(bool cancel)
    {
        using var f = new RestrictedStageFixture(); await f.Initialize(); using var caller = new CancellationTokenSource(); string[]? changed = null;
        var clock = new FinalClock(f.Cell.Clock.Now, () =>
        {
            if (cancel) caller.Cancel(); else File.AppendAllText(Member(f, "completion-proof"), " "); changed = Snapshot(f.Cell.Root);
        });
        var verifier = new CredentialRestrictedRecoveryStageVerifier(f.Destination, clock);
        if (cancel) Assert.Equal(caller.Token, (await Assert.ThrowsAnyAsync<OperationCanceledException>(() => verifier.VerifyAsync(f.Plan, f.Plan.Sha256, f.Receipt.Sha256, caller.Token))).CancellationToken);
        else await Assert.ThrowsAsync<CredentialException>(() => verifier.VerifyAsync(f.Plan, f.Plan.Sha256, f.Receipt.Sha256));
        Assert.Equal(4, clock.Calls); Assert.NotNull(changed); Assert.Equal(changed, Snapshot(f.Cell.Root));
    }

    static string Member(RestrictedStageFixture f, string member) => member switch
    {
        "root" => f.StageRoot, "guard" => f.GuardPath, "marker" => Path.Combine(f.StageRoot, ".credential-recovery-stage.json"),
        "profile" => f.ProfilePath, "receipt" => Path.Combine(f.StageRoot, "completion.json"), "rollback" => Path.Combine(f.StageRoot, "rollback"),
        "state" => Path.Combine(f.StageRoot, "rollback", "state.json"), "binding" => Path.Combine(f.StageRoot, "rollback", "binding.json"),
        _ => Path.Combine(f.StageRoot, "rollback", "completion-proof.json")
    };
    static void AssertPublic(CredentialRestrictedRecoveryStageVerification value, RestrictedStageFixture f)
    {
        Assert.Equal("FoundationBoundDispatchOnly", value.AccessProfile); Assert.True(value.DestinationCompletionProofAuthenticated);
        Assert.True(value.InactiveRecoveryHold); Assert.False(value.NormalVaultUsable); Assert.False(value.PromotionAuthorized); Assert.False(value.ActivationPerformed);
        Assert.False(value.SourceHostFenced); Assert.False(value.ExternalExecutionAuthorized); Assert.False(value.KeyRetirementAuthorized); Assert.False(value.ProductionRecoveryQualified);
        var json = JsonSerializer.Serialize(value); foreach (var excluded in new[] { f.Cell.Root, CredentialFixture.Token, f.Positive.AuthProfileId, f.Absent.AuthProfileId, "\"Tag\"", "\"Ciphertext\"", "\"Scope\"" }) Assert.DoesNotContain(excluded, json, StringComparison.OrdinalIgnoreCase);
    }
    static void AssertOpaque(Exception error, RestrictedStageFixture f) { Assert.DoesNotContain(CredentialFixture.Token, error.ToString()); Assert.DoesNotContain(f.Cell.Root, error.ToString()); }
    static FileSystemSecurity Security(string path) => Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
    static void SetSecurity(string path, FileSystemSecurity security) { if (security is DirectorySecurity directory) new DirectoryInfo(path).SetAccessControl(directory); else new FileInfo(path).SetAccessControl((FileSecurity)security); }
    static void AddWorldRead(string path) { var security = Security(path); security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow)); SetSecurity(path, security); }
    static void PrivateFile(string path, string text)
    {
        using var identity = WindowsIdentity.GetCurrent(); var security = new FileSecurity(); security.SetOwner(identity.User!); security.SetAccessRuleProtection(true, false);
        security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
        using var file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.Read, 4096, FileOptions.WriteThrough, security);
        file.Write(System.Text.Encoding.UTF8.GetBytes(text)); file.Flush(true);
    }
    static string[] Snapshot(string root) => Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories).Prepend(root).Order(StringComparer.Ordinal)
        .Select(path => Path.GetRelativePath(root, path) + ":" + (Directory.Exists(path) ? "directory" : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))) + ":" + Security(path).GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access)).ToArray();
    sealed class PausedKeys(ICredentialKeyProvider source, bool cooperative) : ICredentialKeyProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken token)
        { Entered.TrySetResult(); if (cooperative) await Release.Task.WaitAsync(token); else await Release.Task; return await source.GetKeyAsync(customer, cell, key, cooperative ? token : CancellationToken.None); }
    }
    sealed class FinalClock(DateTimeOffset now, Action final) : TimeProvider
    { internal int Calls { get; private set; } public override DateTimeOffset GetUtcNow() { if (++Calls == 4) final(); return now; } }
}
