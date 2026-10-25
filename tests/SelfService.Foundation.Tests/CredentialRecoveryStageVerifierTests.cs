using System.Collections;
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
public sealed class CredentialRecoveryStageVerifierTests
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false, PipelineRequestFactory.GitHub)] [InlineData(true, PipelineRequestFactory.GitHub)]
    [InlineData(false, PipelineRequestFactory.AzureDevOps)] [InlineData(true, PipelineRequestFactory.AzureDevOps)]
    public async Task Actual_output_authenticates_after_original_vault_authority_and_signed_set_are_unavailable(bool strong, string provider)
    {
        using var f = new StageFixture(strong, provider); await f.Initialize(); Directory.Move(f.Options.RootPath, Path.Combine(f.Cell.Root, "moved-original-vault"));
        if (strong) Directory.Move(f.Options.RollbackProtection!.AuthorityDirectory, Path.Combine(f.Cell.Root, "moved-original-authority"));
        f.Cell.Keys.Fail = true; f.ClearRequests(); var before = f.Snapshot(); var result = await f.Verify();
        Assert.Equal(1, result.AuthenticatedProfileCount); Assert.Equal(f.Plan.Sha256, result.PlanSha256); Assert.Equal(f.Receipt.Sha256, result.ReceiptSha256);
        Assert.Single(f.DestinationKeys.Requests); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot()); AssertPublic(result, f);
    }

    [WindowsCredentialFact]
    public async Task Fresh_native_destination_DPAPI_provider_authenticates_without_changing_key_or_stage_custody()
    {
        using var f = new StageFixture(); var native = f.Cell.ProvisionNativeKeys("synthetic-stage-destination-keys");
        using (await native.ProvisionKeyAsync(f.Customer, f.CellId, f.KeyId)) { }
        f.DestinationProvider = native; await f.Initialize(); var before = f.Snapshot(); var result = await f.Verify();
        Assert.Equal(f.KeyId, Assert.Single(result.Receipt.Profiles).Destination.SourceKeyId); Assert.Equal(before, f.Snapshot()); AssertPublic(result, f);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Historical_stage_remains_verifiable_after_plan_set_and_profile_expiry_or_revocation(bool strong)
    {
        using var f = new StageFixture(strong); await f.Store(); var revoked = f.Cell.Scope with { AuthProfileId = "profile/staged-revoked" }; await f.Store(revoked);
        await f.Source.RevokeAsync(revoked.AuthProfileId, 1); await f.CaptureAndStage(); f.Cell.Clock.Now = f.Cell.Clock.Now.AddDays(3);
        f.ClearRequests(); var before = f.Snapshot(); var result = await f.Verify(); Assert.Equal(2, result.AuthenticatedProfileCount);
        Assert.Single(result.Receipt.Profiles, p => !p.Destination.Enabled); Assert.All(result.Receipt.Profiles, p => Assert.True(p.Destination.ExpiresUnixMilliseconds < result.ObservedUtc.ToUnixTimeMilliseconds()));
        Assert.Single(f.DestinationKeys.Requests); Assert.Equal(before, f.Snapshot()); AssertPublic(result, f);
    }

    [WindowsCredentialFact]
    public async Task Full_profile_commitment_preserves_original_expiry_offset_and_submillisecond_ticks()
    {
        using var f = new StageFixture(); await f.Store(); var profile = await f.Source.GetProfileAsync(f.Cell.Scope.AuthProfileId);
        var exact = profile with { ExpiresUtc = profile.ExpiresUtc.ToOffset(TimeSpan.FromMinutes(330)).AddTicks(1234) }; f.ReplaceSourceProfile(exact); await f.CaptureAndStage();
        var before = f.Snapshot(); var result = await f.Verify(); var actual = f.ReadStagedProfile();
        Assert.Equal(exact.ExpiresUtc.Offset, actual.ExpiresUtc.Offset); Assert.Equal(exact.ExpiresUtc.Ticks, actual.ExpiresUtc.Ticks);
        Assert.Equal(exact.Generation, actual.Generation); Assert.Equal(exact.Kind, actual.Kind); Assert.Equal(exact.Enabled, actual.Enabled);
        Assert.Equal(Assert.Single(f.Plan.PreservedProfiles).ProfileSha256, Assert.Single(result.Receipt.Profiles).PreservedProfileSha256); Assert.Equal(before, f.Snapshot()); AssertPublic(result, f);
    }

    [Theory]
    [InlineData("plan")] [InlineData("receipt")]
    public async Task Exact_reviewed_plan_and_receipt_are_required_before_any_destination_lookup(string pin)
    {
        using var f = new StageFixture(); await f.Initialize(); f.ClearRequests(); var before = f.Snapshot();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Verifier().VerifyAsync(f.Plan, pin == "plan" ? new string('a', 64) : f.Plan.Sha256, pin == "receipt" ? new string('b', 64) : f.Receipt.Sha256));
        Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(before, f.Snapshot()); AssertOpaque(error, f);
    }

    [Theory]
    [InlineData("guard")] [InlineData("marker")] [InlineData("completion")]
    public async Task Missing_held_stage_parts_are_refused_without_recreation_or_key_access(string missing)
    {
        using var f = new StageFixture(); await f.Initialize(); File.Delete(missing switch { "guard" => f.GuardPath, "marker" => Path.Combine(f.StageRoot, ".credential-recovery-stage.json"), _ => Path.Combine(f.StageRoot, "completion.json") });
        var before = f.Snapshot(); f.ClearRequests(); var error = await Assert.ThrowsAsync<CredentialException>(() => f.Verify()); Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(before, f.Snapshot()); AssertOpaque(error, f);
    }

    [Theory]
    [InlineData("missing")] [InlineData("wrong-material")] [InlineData("exception")] [InlineData("credential-exception")] [InlineData("unrelated-cancellation")]
    public async Task Destination_key_failures_are_opaque_and_change_no_stage_or_source_bytes(string attack)
    {
        using var f = new StageFixture(); await f.Initialize();
        if (attack == "missing") f.DestinationKeys.Values.Remove(f.KeyId); else if (attack == "wrong-material") f.DestinationKeys.Values[f.KeyId] = RandomNumberGenerator.GetBytes(32);
        else f.DestinationKeys.OnGet = () => throw attack switch { "credential-exception" => new CredentialException(CredentialFixture.Token), "unrelated-cancellation" => new OperationCanceledException(CredentialFixture.Token + f.Cell.Root), _ => new InvalidOperationException(CredentialFixture.Token + f.Cell.Root) };
        var before = f.Snapshot(); var error = await Assert.ThrowsAsync<CredentialException>(() => f.Verify()); AssertOpaque(error, f); Assert.DoesNotContain(CredentialFixture.Token, error.Code); Assert.DoesNotContain(f.Cell.Root, error.Code); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task One_destination_lookup_is_frozen_for_all_profiles_when_provider_changes_after_first_call()
    {
        using var f = new StageFixture(); await f.Store(); await f.Store(f.Cell.Scope with { AuthProfileId = "profile/another-staged-profile" }); await f.CaptureAndStage();
        var provider = new OnceKeys(f.DestinationKeys, f.DestinationKeys.Values[f.KeyId]); var before = f.Snapshot(); var result = await f.Verifier(provider).VerifyAsync(f.Plan, f.Plan.Sha256, f.Receipt.Sha256);
        Assert.Equal(1, provider.Calls); Assert.Equal(2, result.AuthenticatedProfileCount); Assert.Equal(before, f.Snapshot()); AssertPublic(result, f);
    }

    [Theory]
    [InlineData("file")] [InlineData("directory")] [InlineData("partial")] [InlineData("active-marker")] [InlineData("removed-profile")]
    public async Task Unknown_partial_or_active_entries_refuse_whole_output_before_keys(string attack)
    {
        using var f = new StageFixture(); await f.Initialize();
        if (attack == "directory") Directory.CreateDirectory(Path.Combine(f.StageRoot, "unexpected-directory")); else if (attack == "removed-profile") File.Delete(f.ProfilePath());
        else f.PrivateFile(Path.Combine(f.StageRoot, attack == "partial" ? ".write-synthetic" : attack == "active-marker" ? ".credential-namespace.json" : "unexpected.txt"), "synthetic retained bytes");
        f.ClearRequests(); var before = f.Snapshot(); await Assert.ThrowsAsync<CredentialException>(() => f.Verify()); Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("root")] [InlineData("guard")] [InlineData("profile")] [InlineData("completion")] [InlineData("rollback")]
    public async Task Widened_native_custody_refuses_without_repair_or_key_lookup(string member)
    {
        using var f = new StageFixture(); await f.Initialize(); var path = member switch { "root" => f.StageRoot, "guard" => f.GuardPath, "profile" => f.ProfilePath(), "completion" => Path.Combine(f.StageRoot, "completion.json"), _ => Path.Combine(f.StageRoot, "rollback") };
        AddWorldRead(path); f.ClearRequests(); var before = f.Snapshot(); await Assert.ThrowsAsync<CredentialException>(() => f.Verify()); Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("unknown-member")] [InlineData("profile-bytes")] [InlineData("acl")]
    public async Task Post_provider_custody_check_refuses_late_changes_under_retained_guard(string attack)
    {
        using var f = new StageFixture(); await f.Initialize(); var paused = new PausedKeys(f.DestinationKeys); var operation = f.Verifier(paused).VerifyAsync(f.Plan, f.Plan.Sha256, f.Receipt.Sha256);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Throws<IOException>(() => new FileStream(f.GuardPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            if (attack == "unknown-member") f.PrivateFile(Path.Combine(f.StageRoot, "unexpected-after-provider-await.txt"), "synthetic late entry");
            else if (attack == "acl") AddWorldRead(Path.Combine(f.StageRoot, "rollback", "state.json"));
            else { var json = JsonNode.Parse(File.ReadAllBytes(f.ProfilePath()))!; json["tag"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)); File.WriteAllBytes(f.ProfilePath(), JsonSerializer.SerializeToUtf8Bytes(json, Web)); }
            var changed = f.Snapshot(); paused.Release.TrySetResult(); var error = await Assert.ThrowsAsync<CredentialException>(() => operation); AssertOpaque(error, f); Assert.Equal(changed, f.Snapshot());
        }
        finally { paused.Release.TrySetResult(); }
        using var released = new FileStream(f.GuardPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [WindowsCredentialFact]
    public async Task Rewritten_matching_receipt_state_and_byte_hashes_cannot_replace_actual_AES_authentication()
    {
        using var f = new StageFixture(); await f.Initialize(); f.RewriteOutput(invalidTag: true); f.AssertStructuralStage(); f.ClearRequests(); var before = f.Snapshot();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Verify()); Assert.Equal("credential-envelope-integrity", error.Code); Assert.Single(f.DestinationKeys.Requests); Assert.Equal(before, f.Snapshot()); AssertOpaque(error, f);
    }

    [WindowsCredentialFact]
    public async Task Validly_reencrypted_submillisecond_change_is_refused_by_exact_reviewed_profile_hash()
    {
        using var f = new StageFixture(); await f.Store(); var profile = await f.Source.GetProfileAsync(f.Cell.Scope.AuthProfileId);
        f.ReplaceSourceProfile(profile with { ExpiresUtc = profile.ExpiresUtc.ToOffset(TimeSpan.FromHours(2)).AddTicks(1234) }); await f.CaptureAndStage();
        f.RewriteOutput(invalidTag: false); f.ClearRequests(); var before = f.Snapshot(); var error = await Assert.ThrowsAsync<CredentialException>(() => f.Verify());
        Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(before, f.Snapshot()); AssertOpaque(error, f);
    }

    [Theory]
    [InlineData("source-key")] [InlineData("source-envelope")]
    public async Task Reviewed_receipt_cannot_relabel_signed_inventory_source_dependencies(string lie)
    {
        using var f = new StageFixture(); await f.Initialize(); var pair = Assert.Single(f.Receipt.Profiles); var source = CloneDependency(pair.Source,
            key: lie == "source-key" ? "unrelated-source-key" : null, envelope: lie == "source-envelope" ? new string('a', 64) : null);
        f.Receipt = CloneReceipt(f.Receipt, [new(source, pair.Destination, pair.PreservedProfileSha256)]); f.WriteReceipt(); f.ClearRequests(); var before = f.Snapshot();
        await Assert.ThrowsAsync<CredentialException>(() => f.Verify()); Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Committed_absence_is_retained_in_plan_receipt_and_verified_inactive_state()
    {
        using var f = new StageFixture(true); await f.Store(); var absent = f.Cell.Scope with { AuthProfileId = "profile/abandoned-before-publication" };
        typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Source, (Action<string>)(phase => { if (phase == "PendingPersisted") throw new IOException("synthetic abandoned write"); }));
        await Assert.ThrowsAsync<IOException>(() => f.Store(absent)); typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Source, null);
        var pending = await f.Source.InspectRollbackStateAsync(absent.AuthProfileId);
        await f.Source.ReconcilePendingWriteAsync(absent.AuthProfileId, pending.AuthorityRevision, pending.AuthorityStateSha256, pending.Pending!.PendingSha256, CredentialRollbackResolution.AbandonUnpublished);
        await f.CaptureAndStage(); var before = f.Snapshot(); var result = await f.Verify(); Assert.Equal(new[] { Hash(Encoding.UTF8.GetBytes(absent.AuthProfileId)) }, f.Plan.AbsentProfileIds);
        Assert.Equal(f.Plan.AbsentProfileIds, result.Receipt.AbsentProfileIds); Assert.Equal(1, result.AuthenticatedProfileCount); Assert.Equal(before, f.Snapshot()); AssertPublic(result, f);
    }

    [WindowsCredentialFact]
    public async Task Caller_cancellation_before_or_during_lookup_releases_guard_without_writes()
    {
        using var f = new StageFixture(); await f.Initialize(); f.ClearRequests(); var before = f.Snapshot(); using var early = new CancellationTokenSource(); early.Cancel();
        var first = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Verifier().VerifyAsync(f.Plan, f.Plan.Sha256, f.Receipt.Sha256, early.Token)); Assert.Equal(early.Token, first.CancellationToken); Assert.Empty(f.DestinationKeys.Requests);
        using var caller = new CancellationTokenSource(); var paused = new PausedKeys(f.DestinationKeys); var operation = f.Verifier(paused).VerifyAsync(f.Plan, f.Plan.Sha256, f.Receipt.Sha256, caller.Token);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
        try { var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); Assert.Equal(caller.Token, error.CancellationToken); AssertOpaque(error, f); }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(before, f.Snapshot()); using var released = new FileStream(f.GuardPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [WindowsCredentialFact]
    public async Task Default_cooperative_deadline_is_opaque_and_preserves_completed_stage()
    {
        using var f = new StageFixture(); await f.Initialize(); var before = f.Snapshot(); var paused = new PausedKeys(f.DestinationKeys);
        var operation = f.Verifier(paused).VerifyAsync(f.Plan, f.Plan.Sha256, f.Receipt.Sha256); await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { var error = await Assert.ThrowsAsync<CredentialException>(() => operation.WaitAsync(TimeSpan.FromSeconds(30))); Assert.Equal("credential-recovery-stage-verification-timeout", error.Code); AssertOpaque(error, f); }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(before, f.Snapshot()); using var released = new FileStream(f.GuardPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [WindowsCredentialFact]
    public async Task Uncooperative_lookup_retains_guard_until_actual_completion_then_refuses_canceled_success()
    {
        using var f = new StageFixture(); await f.Initialize(); var before = f.Snapshot(); var paused = new PausedKeys(f.DestinationKeys, cooperative: false); using var caller = new CancellationTokenSource();
        var operation = f.Verifier(paused).VerifyAsync(f.Plan, f.Plan.Sha256, f.Receipt.Sha256, caller.Token); await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel();
        try { await Task.Delay(100); Assert.False(operation.IsCompleted); Assert.Throws<IOException>(() => new FileStream(f.GuardPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None)); Assert.Equal(before, f.Snapshot()); }
        finally { paused.Release.TrySetResult(); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); Assert.Equal(caller.Token, error.CancellationToken); AssertOpaque(error, f);
        Assert.Equal(before, f.Snapshot()); using var released = new FileStream(f.GuardPath, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [WindowsCredentialFact]
    public async Task Future_completion_is_refused_before_keys_and_empty_result_is_immutable_inactive_metadata()
    {
        using var f = new StageFixture(); await f.CaptureAndStage(); var now = f.Cell.Clock.Now; f.Cell.Clock.Now = f.Receipt.CompletedUtc.AddMilliseconds(-1); f.ClearRequests(); var before = f.Snapshot();
        await Assert.ThrowsAsync<CredentialException>(() => f.Verify()); Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(before, f.Snapshot());
        f.Cell.Clock.Now = now; var result = await f.Verify(); Assert.Equal(0, result.AuthenticatedProfileCount); Assert.False(result.TemporaryInMemoryDecryptionPerformed);
        Assert.Throws<NotSupportedException>(() => ((IList)result.Receipt.Profiles).Clear()); Assert.All(typeof(CredentialRecoveryStageVerification).GetProperties(), p => Assert.Null(p.SetMethod)); Assert.Empty(typeof(CredentialRecoveryStageVerification).GetConstructors()); AssertPublic(result, f);
    }

    [Theory]
    [InlineData("vault-to-source-authority")] [InlineData("authority-to-source-vault")]
    [InlineData("stage-to-source-vault")] [InlineData("stage-to-source-authority")]
    public async Task Self_consistent_rehashed_stage_IDs_cannot_alias_either_original_source_namespace(string alias)
    {
        using var f = new StageFixture(true); await f.Initialize(); f.RewriteAlias(alias); f.ClearRequests(); var before = f.Snapshot();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Verify()); Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(before, f.Snapshot()); AssertOpaque(error, f);
    }

    // Helpers are restricted to fresh synthetic fixtures; no production or active namespace is modified.
    static void AssertPublic(CredentialRecoveryStageVerification result, StageFixture f)
    {
        Assert.True(result.OutputCiphertextsAuthenticated); Assert.True(result.InactiveRecoveryHold); Assert.False(result.NormalVaultUsable); Assert.False(result.CurrentSourceVaultConsulted);
        Assert.False(result.PlaintextCredentialMaterialExported); Assert.False(result.EncryptionKeyMaterialExported); Assert.False(result.RetainedEnvelopeBytesExported);
        Assert.False(result.StageWritten); Assert.False(result.NamespaceAdopted); Assert.False(result.ActivationPerformed); Assert.False(result.SourceHostFenced);
        Assert.False(result.ExternalExecutionAuthorized); Assert.False(result.KeyRetirementAuthorized); Assert.False(result.IndependentKeyBackupVerified); Assert.False(result.ProductionRecoveryQualified);
        Assert.Equal("NotAssessed", result.IndependentBackupCoverage); var json = JsonSerializer.Serialize(result);
        foreach (var excluded in new[] { CredentialFixture.Token, f.Cell.Root, JsonSerializer.Serialize(f.Cell.Root).Trim('"'), f.Cell.Scope.AuthProfileId, f.Cell.Scope.Audience, "ownerSid", "\"ciphertext\"", "\"nonce\"", "requiredScopes", "actions:write" })
            Assert.DoesNotContain(excluded, json, StringComparison.OrdinalIgnoreCase);
    }
    static void AssertOpaque(Exception error, StageFixture f) { Assert.DoesNotContain(CredentialFixture.Token, error.ToString()); Assert.DoesNotContain(f.Cell.Root, error.ToString()); }
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static void AddWorldRead(string path)
    {
        var directory = Directory.Exists(path); FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        if (directory) new DirectoryInfo(path).SetAccessControl((DirectorySecurity)acl); else new FileInfo(path).SetAccessControl((FileSecurity)acl);
    }
    static CredentialRecoveryProfileDependency CloneDependency(CredentialRecoveryProfileDependency p, string? key = null, string? envelope = null, long? length = null) =>
        (CredentialRecoveryProfileDependency)typeof(CredentialRecoveryProfileDependency).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([
            p.ProfileIdSha256, p.ScopeSha256, p.ProjectId, p.EnvironmentId, p.Generation, p.Enabled, p.ExpiresUnixMilliseconds, key ?? p.SourceKeyId, envelope ?? p.EnvelopeSha256, length ?? p.EnvelopeLength]);
    static CredentialRecoveryStageReceipt CloneReceipt(CredentialRecoveryStageReceipt r, IEnumerable<CredentialRecoveryStagedProfile> profiles, string? state = null) =>
        (CredentialRecoveryStageReceipt)typeof(CredentialRecoveryStageReceipt).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([
            r.PlanSha256, r.SourceSetSha256, r.StageId, r.StagedVaultNamespaceId, r.StagedAuthorityNamespaceId, r.DestinationRootCommitmentSha256, r.DestinationKey,
            r.BindingSha256, state ?? r.StateSha256, r.ReservedUtc, r.CompletedUtc, profiles, r.AbsentProfileIds]);
    sealed class PausedKeys(ICredentialKeyProvider inner, bool cooperative = true) : ICredentialKeyProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken cancellationToken)
        { Entered.TrySetResult(); if (cooperative) await Release.Task.WaitAsync(cancellationToken); else await Release.Task; return await inner.GetKeyAsync(customer, cell, key, cooperative ? cancellationToken : CancellationToken.None); }
    }
    sealed class OnceKeys(ICredentialKeyProvider original, byte[] first) : ICredentialKeyProvider
    {
        public int Calls { get; private set; }
        public ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken cancellationToken)
        { cancellationToken.ThrowIfCancellationRequested(); Calls++; return Calls == 1 ? ValueTask.FromResult(new CredentialEncryptionKey(first)) : original.GetKeyAsync(customer, cell, "unavailable-second-lookup", cancellationToken); }
    }
    sealed class StageFixture : IDisposable
    {
        public CredentialFixture Cell { get; }
        public CredentialVaultOptions Options { get; }
        public CredentialVault Source { get; }
        public CredentialVaultNamespaceIdentity SourceNamespace { get; }
        public CredentialTestKeys DestinationKeys { get; } = new();
        public ICredentialKeyProvider DestinationProvider { get; set; }
        public RSA Signer { get; } = RSA.Create(2048);
        public CredentialRecoveryStagePlan Plan { get; private set; } = null!;
        public CredentialRecoveryStageReceipt Receipt { get; set; } = null!;
        public string Customer => Cell.Scope.CustomerId;
        public string CellId => Cell.Scope.CellId;
        public string KeyId => "existing-stage-destination";
        public string StageRoot => Path.Combine(Cell.Root, "inactive-credential-recovery");
        public string GuardPath => Path.Combine(StageRoot, ".credential-recovery-stage.guard");
        public StageFixture(bool strong = false, string provider = PipelineRequestFactory.GitHub)
        {
            Cell = new(provider); Cell.Clock.Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
            Options = strong ? Cell.Options with { RootPath = Path.Combine(Cell.Root, "stage-source-vault"), RollbackProtection = new(Path.Combine(Cell.Root, "stage-source-authority")) } : Cell.Options;
            Source = strong ? CredentialVault.ProvisionStrong(Options, Cell.Keys, Cell.Clock) : Cell.Vault; SourceNamespace = Source.DescribeNamespace();
            DestinationKeys.Values.Clear(); DestinationKeys.Values[KeyId] = RandomNumberGenerator.GetBytes(32); DestinationProvider = DestinationKeys;
        }
        public async Task Initialize() { await Store(); await CaptureAndStage(); }
        public async Task Store(CredentialScope? scope = null) { using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token); await Source.PutAsync(scope ?? Cell.Scope, secret, Cell.Clock.Now.AddHours(1)); }
        public async Task CaptureAndStage()
        {
            using var set = await Source.CaptureSignedRecoverySetAsync(SourceNamespace, Signer, "reviewed-stage-signer", 1, Cell.Clock.Now, Cell.Clock.Now.AddDays(1));
            using var publicKey = RSA.Create(); publicKey.ImportSubjectPublicKeyInfo(Signer.ExportSubjectPublicKeyInfo(), out _); var authority = CredentialRecoverySets.Authority(set);
            var verified = CredentialRecoverySets.Verify(set, authority, publicKey, SourceNamespace, Cell.Clock);
            var destination = await CredentialRecoveryStaging.ObserveDestinationKeyAsync(Customer, CellId, KeyId, DestinationProvider);
            Plan = CredentialRecoveryStaging.PreparePlan(verified, authority, SourceNamespace, StageRoot, destination, "Synthetic inactive recovery verification", Cell.Clock.Now.AddMinutes(20), Cell.Clock);
            Receipt = await new CredentialRecoveryStageExecutor(Cell.Keys, DestinationProvider, Cell.Clock).StageAsync(set, Plan, Plan.Sha256, publicKey);
        }
        public CredentialRecoveryStageVerifier Verifier(ICredentialKeyProvider? keys = null) => new(keys ?? DestinationProvider, Cell.Clock);
        public Task<CredentialRecoveryStageVerification> Verify() => Verifier().VerifyAsync(Plan, Plan.Sha256, Receipt.Sha256);
        public void ClearRequests() { Cell.Keys.Requests.Clear(); DestinationKeys.Requests.Clear(); }
        public string ProfilePath() => Directory.GetFiles(Path.Combine(StageRoot, "profiles"), "*.credential.json").Order(StringComparer.Ordinal).First();
        public CredentialProfile ReadStagedProfile() => JsonNode.Parse(File.ReadAllBytes(ProfilePath()))!["profile"]!.Deserialize<CredentialProfile>(Web)!;
        public void ReplaceSourceProfile(CredentialProfile profile) => File.WriteAllBytes(Path.Combine(Options.RootPath, Hash(Encoding.UTF8.GetBytes(profile.Scope.AuthProfileId)) + ".credential.json"), Encrypt(profile, "key-1", Cell.Keys.Values["key-1"]));
        public void RewriteOutput(bool invalidTag)
        {
            var path = ProfilePath(); var json = JsonNode.Parse(File.ReadAllBytes(path))!; byte[] candidate;
            if (invalidTag) { json["tag"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)); candidate = JsonSerializer.SerializeToUtf8Bytes(json, Web); }
            else { var profile = json["profile"]!.Deserialize<CredentialProfile>(Web)!; candidate = Encrypt(profile with { ExpiresUtc = profile.ExpiresUtc.AddTicks(1) }, KeyId, DestinationKeys.Values[KeyId]); }
            File.WriteAllBytes(path, candidate); var id = Path.GetFileName(path)[..64]; var pair = Receipt.Profiles.Single(p => p.Destination.ProfileIdSha256 == id);
            var destination = CloneDependency(pair.Destination, envelope: Hash(candidate), length: candidate.Length);
            var statePath = Path.Combine(StageRoot, "rollback", "state.json"); var state = JsonNode.Parse(File.ReadAllBytes(statePath))!;
            var entry = state["entries"]!.AsArray().Single(e => e!["profileIdSha256"]!.GetValue<string>() == id)!;
            entry["envelopeSha256"] = destination.EnvelopeSha256; entry["envelopeLength"] = destination.EnvelopeLength;
            var stateType = typeof(CredentialRecoveryStaging).GetNestedType("StageState", BindingFlags.NonPublic)!;
            var stateObject = JsonSerializer.Deserialize(state.ToJsonString(Web), stateType, Web)!;
            var stateHash = (string)typeof(CredentialRecoveryStaging).GetMethod("StateDigest", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [stateObject])!;
            state["sha256"] = stateHash; File.WriteAllBytes(statePath, JsonSerializer.SerializeToUtf8Bytes(state, Web));
            Receipt = CloneReceipt(Receipt, Receipt.Profiles.Select(p => p.Destination.ProfileIdSha256 == id ? new(p.Source, destination, p.PreservedProfileSha256) : p), stateHash); WriteReceipt();
        }
        public void WriteReceipt() => File.WriteAllBytes(Path.Combine(StageRoot, "completion.json"), JsonSerializer.SerializeToUtf8Bytes(Receipt, Web));
        public void RewriteAlias(string alias)
        {
            var stage = alias == "stage-to-source-vault" ? SourceNamespace.VaultNamespaceId : alias == "stage-to-source-authority" ? SourceNamespace.AuthorityNamespaceId! : Receipt.StageId;
            var vault = alias == "vault-to-source-authority" ? SourceNamespace.AuthorityNamespaceId! : Receipt.StagedVaultNamespaceId;
            var authority = alias == "authority-to-source-vault" ? SourceNamespace.VaultNamespaceId : Receipt.StagedAuthorityNamespaceId;
            string Rewrite(string relative, string nestedType, string digest)
            {
                var path = Path.Combine(StageRoot, relative); var node = JsonNode.Parse(File.ReadAllBytes(path))!;
                node["stageId"] = stage; node["stagedVaultNamespaceId"] = vault; node["stagedAuthorityNamespaceId"] = authority;
                var type = typeof(CredentialRecoveryStaging).GetNestedType(nestedType, BindingFlags.NonPublic)!;
                var record = JsonSerializer.Deserialize(node.ToJsonString(Web), type, Web)!;
                var sha = (string)typeof(CredentialRecoveryStaging).GetMethod(digest, BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [record])!;
                node["sha256"] = sha; File.WriteAllBytes(path, JsonSerializer.SerializeToUtf8Bytes(node, Web)); return sha;
            }
            _ = Rewrite(".credential-recovery-stage.json", "StageReservation", "ReservationDigest");
            var binding = Rewrite(Path.Combine("rollback", "binding.json"), "StageBinding", "BindingDigest"); var state = Rewrite(Path.Combine("rollback", "state.json"), "StageState", "StateDigest");
            var r = Receipt;
            Receipt = (CredentialRecoveryStageReceipt)typeof(CredentialRecoveryStageReceipt).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([
                r.PlanSha256, r.SourceSetSha256, stage, vault, authority, r.DestinationRootCommitmentSha256, r.DestinationKey,
                binding, state, r.ReservedUtc, r.CompletedUtc, r.Profiles, r.AbsentProfileIds]); WriteReceipt();
        }
        public void AssertStructuralStage()
        { using var lease = (IDisposable)typeof(CredentialRecoveryStaging).GetMethod("OpenCompletedReadLease", BindingFlags.Static | BindingFlags.NonPublic)!.Invoke(null, [Plan, Plan.Sha256, Receipt.Sha256])!; }
        static byte[] Encrypt(CredentialProfile profile, string keyId, byte[] key)
        {
            var clear = Encoding.UTF8.GetBytes(CredentialFixture.Token); var nonce = RandomNumberGenerator.GetBytes(12); var cipher = new byte[clear.Length]; var tag = new byte[16];
            try { using var aes = new AesGcm(key, 16); aes.Encrypt(nonce, clear, cipher, tag, JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, keyId, profile }, Web));
                return JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, keyId, profile, nonce = Convert.ToBase64String(nonce), ciphertext = Convert.ToBase64String(cipher), tag = Convert.ToBase64String(tag) }, Web); }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        public void PrivateFile(string path, string value)
        {
            using var identity = WindowsIdentity.GetCurrent(); var security = new FileSecurity(); security.SetOwner(identity.User!); security.SetAccessRuleProtection(true, false);
            security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            using var file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.Read, 4096, FileOptions.WriteThrough, security); file.Write(Encoding.UTF8.GetBytes(value)); file.Flush(true);
        }
        public string[] Snapshot() { var result = new List<string>(); Walk(Cell.Root, result); return result.Order(StringComparer.Ordinal).ToArray(); }
        void Walk(string path, List<string> result)
        {
            var directory = Directory.Exists(path); FileSystemSecurity security = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            var prefix = Path.GetRelativePath(Cell.Root, path) + ":" + Hash(security.GetSecurityDescriptorBinaryForm());
            if (directory) { result.Add(prefix + ":directory"); foreach (var child in Directory.EnumerateFileSystemEntries(path)) Walk(child, result); }
            else { var length = new FileInfo(path).Length; var bytes = (Path.GetFileName(path) is ".vault.lock" or ".rollback.lock" or ".keys.lock" or ".credential-recovery-stage.guard") && length == 0 ? [] : File.ReadAllBytes(path); result.Add(prefix + ":" + length + ":" + Hash(bytes)); }
        }
        public void Dispose() { Signer.Dispose(); Cell.Dispose(); }
    }
}
