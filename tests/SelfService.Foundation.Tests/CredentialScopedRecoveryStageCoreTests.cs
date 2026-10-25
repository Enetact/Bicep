using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialScopedRecoveryStageCoreTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [WindowsCredentialFact]
    public async Task Mixed_stage_rekeys_absence_proofs_and_verifies_with_only_destination_keys_after_source_roots_and_keys_removed()
    {
        using var f = new Fixture(); await f.Initialize(); var originalProof = f.SourceProof(); var receipt = await f.Stage();
        Assert.Single(receipt.Profiles); var absent = Assert.Single(receipt.Absences); Assert.Equal(originalProof["sha256"]!.GetValue<string>(), absent.Source.ProofSha256);
        Assert.Equal("key-1", absent.Source.SourceKeyId); Assert.Equal("stage-destination", absent.Destination.SourceKeyId); Assert.NotEqual(absent.Source.ProofSha256, absent.Destination.ProofSha256);
        Assert.Equal(new[] { new FoundationCredentialRecoveryScope("default", "dev"), new FoundationCredentialRecoveryScope("other", "qa") }, f.Plan.AffectedScopes);
        var staged = f.State()["entries"]!.AsArray().Single(e => e!["generation"]!.GetValue<int>() == 0)!["absenceProof"]!;
        Assert.NotEqual(originalProof["tag"]!.GetValue<string>(), staged["tag"]!.GetValue<string>()); Assert.Equal(receipt.CompletionCoreSha256, staged["completionCoreSha256"]!.GetValue<string>());
        f.RemoveSources(); f.Cell.Keys.Values.Clear(); f.Cell.Keys.Requests.Clear(); f.Destination.Requests.Clear(); f.Cell.Clock.Now = f.Cell.Clock.Now.AddHours(2);
        var result = await f.Verify(receipt);
        Assert.Equal(1, result.AuthenticatedProfileCount); Assert.Equal(1, result.AuthenticatedAbsenceCount); Assert.True(result.OutputCiphertextsAuthenticated); Assert.True(result.DestinationAbsenceProofsAuthenticated);
        Assert.Single(f.Destination.Requests); Assert.Empty(f.Cell.Keys.Requests); Assert.True(result.InactiveRecoveryHold); Assert.False(result.NormalVaultUsable); Assert.False(result.PromotionAuthorized);
        Assert.False(result.ActivationPerformed); Assert.False(result.ExternalExecutionAuthorized); Assert.Equal("NotAssessed", result.IndependentBackupCoverage);
        var publicJson = JsonSerializer.Serialize(result, Json); Assert.DoesNotContain(f.Cell.Root, publicJson); Assert.DoesNotContain(f.Absent.AuthProfileId, publicJson); Assert.DoesNotContain("\"tag\"", publicJson);
    }

    [WindowsCredentialFact]
    public async Task Positive_profile_preserves_exact_expiry_offset_fractional_ticks_generation_and_revocation()
    {
        using var f = new Fixture(); await f.Initialize(revoked: true); var original = JsonNode.Parse(File.ReadAllText(f.SourcePositivePath))!["profile"]!.ToJsonString(Json);
        var receipt = await f.Stage(); var staged = JsonNode.Parse(File.ReadAllText(Path.Combine(f.StagePath, "profiles", receipt.Profiles[0].Destination.ProfileIdSha256 + ".credential.json")))!["profile"]!.ToJsonString(Json);
        Assert.Equal(original, staged); Assert.Equal(2, receipt.Profiles[0].Destination.Generation); Assert.False(receipt.Profiles[0].Destination.Enabled); await f.Verify(receipt);
    }

    [WindowsCredentialFact]
    public async Task Coherently_rehashed_destination_MAC_and_exact_revised_receipt_pass_structure_but_fail_actual_authentication()
    {
        using var f = new Fixture(); await f.Initialize(); var receipt = await f.Stage(); var revised = f.ReplaceDestinationTag(receipt, new string('0', 64));
        f.Cell.Keys.Values.Clear(); f.Cell.Keys.Requests.Clear(); f.Destination.Requests.Clear();
        using (f.OpenStructural(revised)) { Assert.Empty(f.Destination.Requests); }
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Verify(revised));
        Assert.Equal("credential-scoped-recovery-stage-destination-proof-authentication-failed", error.Code); Assert.Single(f.Destination.Requests); Assert.Empty(f.Cell.Keys.Requests);
        Assert.True(File.Exists(Path.Combine(f.StagePath, "completion.json"))); Assert.Equal(revised.Sha256, JsonNode.Parse(File.ReadAllText(Path.Combine(f.StagePath, "completion.json")))!["sha256"]!.GetValue<string>());
    }

    [WindowsCredentialFact]
    public async Task Original_source_MAC_is_never_a_destination_MAC_even_when_both_key_materials_are_equal()
    {
        using var f = new Fixture(); await f.Initialize(equalMaterial: true); var sourceTag = f.SourceProof()["tag"]!.GetValue<string>();
        var receipt = await f.Stage(); var revised = f.ReplaceDestinationTag(receipt, sourceTag); using (f.OpenStructural(revised)) { }
        Assert.Equal("credential-scoped-recovery-stage-destination-proof-authentication-failed", (await Assert.ThrowsAsync<CredentialException>(() => f.Verify(revised))).Code);
    }

    [WindowsCredentialFact]
    public async Task Bad_original_absence_key_fails_before_fresh_output_reservation_even_when_positive_AES_is_valid()
    {
        using var f = new Fixture(); await f.Initialize(); f.Cell.Keys.Values["key-1"] = RandomNumberGenerator.GetBytes(32);
        Assert.Equal("credential-absence-proof-authentication-failed", (await Assert.ThrowsAsync<CredentialException>(() => f.Stage())).Code); Assert.False(Directory.Exists(f.StagePath));
    }

    [WindowsCredentialFact]
    public async Task Destination_material_is_frozen_once_across_source_awaits_and_cannot_be_retargeted_by_provider_rotation()
    {
        using var f = new Fixture(); await f.Initialize(); var original = f.Destination.Values["stage-destination"].ToArray();
        f.Cell.Keys.OnGet = () => f.Destination.Values["stage-destination"] = RandomNumberGenerator.GetBytes(32); f.Destination.Requests.Clear();
        var receipt = await f.Stage(); Assert.Single(f.Destination.Requests); f.Cell.Keys.OnGet = null;
        await Assert.ThrowsAsync<CredentialException>(() => f.Verify(receipt)); f.Destination.Values["stage-destination"] = original; await f.Verify(receipt);
    }

    [WindowsCredentialFact]
    public async Task Uncooperative_source_cancellation_retains_private_work_until_dependency_returns_and_never_reserves_output()
    {
        using var f = new Fixture(); await f.Initialize(); using var cancellation = new CancellationTokenSource(); var paused = new PausedKeys(f.Cell.Keys);
        var work = new CredentialScopedRecoveryStageExecutor(paused, f.Destination, f.Cell.Clock).StageAsync(f.Set, f.Plan, f.Plan.Sha256, f.Signer, cancellation.Token);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); cancellation.Cancel(); await Task.Delay(80); Assert.False(work.IsCompleted); Assert.False(Directory.Exists(f.StagePath));
        paused.Release.TrySetResult(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => work); Assert.False(Directory.Exists(f.StagePath));
    }

    [WindowsCredentialFact]
    public async Task Failure_after_reservation_retains_private_generic_guard_and_refuses_automatic_retry_or_cleanup()
    {
        using var f = new Fixture(); await f.Initialize(); var clock = new ReservedFailureClock(f.Cell.Clock.Now, f.StagePath);
        Assert.Equal("credential-scoped-recovery-stage-outcome-unknown", (await Assert.ThrowsAsync<CredentialException>(() =>
            new CredentialScopedRecoveryStageExecutor(f.Cell.Keys, f.Destination, clock).StageAsync(f.Set, f.Plan, f.Plan.Sha256, f.Signer))).Code);
        Assert.True(File.Exists(Path.Combine(f.StagePath, ".credential-recovery-stage.guard"))); Assert.False(File.Exists(Path.Combine(f.StagePath, "completion.json")));
        var before = f.StageSnapshot(); await Assert.ThrowsAsync<CredentialException>(() => f.Stage()); Assert.Equal(before, f.StageSnapshot());
    }

    [WindowsCredentialFact]
    public async Task Existing_empty_output_is_never_adopted_or_changed_and_refusal_precedes_all_key_access()
    {
        using var f = new Fixture(); await f.Initialize(); Directory.CreateDirectory(f.StagePath); f.Cell.Keys.Requests.Clear(); f.Destination.Requests.Clear();
        await Assert.ThrowsAsync<CredentialException>(() => f.Stage()); Assert.Empty(Directory.GetFileSystemEntries(f.StagePath)); Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests);
    }

    [WindowsCredentialFact]
    public async Task Completed_stage_tamper_during_destination_key_await_refuses_before_authentication_without_repair()
    {
        using var f = new Fixture(); await f.Initialize(); var receipt = await f.Stage(); f.Cell.Keys.Requests.Clear();
        var path = Path.Combine(f.StagePath, "rollback", "state.json"); f.Destination.OnGet = () => File.AppendAllText(path, " ");
        await Assert.ThrowsAsync<CredentialException>(() => f.Verify(receipt)); Assert.Empty(f.Cell.Keys.Requests); Assert.EndsWith(" ", File.ReadAllText(path)); f.Destination.OnGet = null;
    }

    sealed class PausedKeys(ICredentialKeyProvider source) : ICredentialKeyProvider
    {
        internal TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string c, string e, string id, CancellationToken token)
        { Entered.TrySetResult(); await Release.Task; return await source.GetKeyAsync(c, e, id, CancellationToken.None); }
    }
    sealed class ReservedFailureClock(DateTimeOffset now, string root) : TimeProvider
    { public override DateTimeOffset GetUtcNow() => File.Exists(Path.Combine(root, ".credential-recovery-stage.guard")) ? throw new InvalidOperationException("Synthetic post-reservation interruption.") : now; }
    sealed class Fixture : IDisposable
    {
        internal CredentialFixture Cell { get; } = new(); internal RSA Signer { get; } = RSA.Create(2048); internal CredentialTestKeys Destination { get; } = new();
        internal string SourceAuthority => Path.Combine(Cell.Root, "scoped-stage-source-authority");
        internal string StagePath => Path.Combine(Cell.Root, "scoped-held-output");
        internal CredentialScope Positive => Cell.Scope with { AuthProfileId = "profile/stage-positive" };
        internal CredentialScope Absent => Cell.Scope with { AuthProfileId = "profile/stage-absent", ProjectId = "other", EnvironmentId = "qa" };
        internal string Hash(string id) => Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(id)));
        internal string SourcePositivePath => Path.Combine(Options.RootPath, Hash(Positive.AuthProfileId) + ".credential.json");
        internal CredentialVaultOptions Options { get; }
        internal CredentialVault Vault { get; }
        internal CredentialScopedRecoverySet Set { get; private set; } = null!;
        internal CredentialScopedRecoveryStagePlan Plan { get; private set; } = null!;
        internal Fixture()
        {
            Cell.Clock.Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z"); Destination.Values["stage-destination"] = RandomNumberGenerator.GetBytes(32);
            Options = Cell.Options with { RootPath = Path.Combine(Cell.Root, "scoped-stage-source-vault"), RollbackProtection = new(SourceAuthority) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 } };
            Vault = CredentialVault.ProvisionStrongScoped(Options, Cell.Keys, Cell.Clock);
        }
        internal async Task Initialize(bool revoked = false, bool equalMaterial = false)
        {
            typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Vault,
                (Action<string>)(phase => { if (phase == "PendingPersisted") throw new CredentialException("synthetic-source-interruption"); }));
            using (var material = CredentialSecret.FromUtf8(CredentialFixture.Token)) await Assert.ThrowsAsync<CredentialException>(() => Vault.PutAsync(Absent, material, Cell.Clock.Now.AddHours(1)));
            var status = await Vault.InspectRollbackStateAsync(Absent.AuthProfileId); await Vault.ReconcilePendingWriteAsync(Absent.AuthProfileId, status.AuthorityRevision, status.AuthorityStateSha256, status.Pending!.PendingSha256, CredentialRollbackResolution.AbandonUnpublished);
            typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Vault, null);
            Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32); var positive = new CredentialVault(Options with { EncryptionKeyId = "key-2" }, Cell.Keys, Cell.Clock);
            using (var material = CredentialSecret.FromUtf8(CredentialFixture.Token)) await positive.PutAsync(Positive, material, Cell.Clock.Now.AddHours(1).AddTicks(1234).ToOffset(TimeSpan.FromHours(5.5)));
            if (revoked) await positive.RevokeAsync(Positive.AuthProfileId, 1);
            var ns = Vault.DescribeNamespace(); Set = await Vault.CaptureScopedSignedRecoverySetAsync(ns, Signer, "scoped-stage-signer", 1, Cell.Clock.Now, Cell.Clock.Now.AddHours(1));
            var authority = CredentialScopedRecoverySets.Authority(Set); var verified = CredentialScopedRecoverySets.Verify(Set, authority, Signer, ns, Cell.Clock);
            if (equalMaterial) Destination.Values["stage-destination"] = Cell.Keys.Values["key-1"].ToArray();
            var key = await CredentialRecoveryStaging.ObserveDestinationKeyAsync(ns.CustomerId, ns.CellId, "stage-destination", Destination);
            Plan = CredentialScopedRecoveryStaging.PreparePlan(verified, authority, ns, StagePath, key, "Synthetic inactive scoped stage", Cell.Clock.Now.AddMinutes(20), Cell.Clock);
        }
        internal Task<CredentialScopedRecoveryStageReceipt> Stage() => new CredentialScopedRecoveryStageExecutor(Cell.Keys, Destination, Cell.Clock).StageAsync(Set, Plan, Plan.Sha256, Signer);
        internal Task<CredentialScopedRecoveryStageVerification> Verify(CredentialScopedRecoveryStageReceipt receipt) => new CredentialScopedRecoveryStageVerifier(Destination, Cell.Clock).VerifyAsync(Plan, Plan.Sha256, receipt.Sha256);
        internal JsonObject State() => JsonNode.Parse(File.ReadAllText(Path.Combine(StagePath, "rollback", "state.json")))!.AsObject();
        internal JsonObject SourceProof() => JsonNode.Parse(File.ReadAllText(Path.Combine(SourceAuthority, "state.json")))!["profiles"]![Hash(Absent.AuthProfileId)]!["absenceProof"]!.AsObject();
        internal IDisposable OpenStructural(CredentialScopedRecoveryStageReceipt receipt) => (IDisposable)typeof(CredentialScopedRecoveryStageVerifier)
            .GetMethod("OpenPrepared", BindingFlags.NonPublic | BindingFlags.Instance)!.Invoke(new CredentialScopedRecoveryStageVerifier(Destination, Cell.Clock), [Plan, Plan.Sha256, receipt.Sha256])!;
        internal CredentialScopedRecoveryStageReceipt ReplaceDestinationTag(CredentialScopedRecoveryStageReceipt r, string tag)
        {
            var state = State(); var proof = state["entries"]!.AsArray().Single(e => e!["generation"]!.GetValue<int>() == 0)!["absenceProof"]!.AsObject(); proof["tag"] = tag;
            var assembly = typeof(CredentialVault).Assembly; var proofType = assembly.GetType("SelfService.Foundation.CredentialScopedStageAbsenceProof")!;
            var parsedProof = JsonSerializer.Deserialize(proof.ToJsonString(Json), proofType, Json)!;
            proof["sha256"] = (string)assembly.GetType("SelfService.Foundation.CredentialScopedStageProofs")!.GetMethod("Digest", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [parsedProof])!;
            var stageType = typeof(CredentialScopedRecoveryStaging); var stateType = stageType.GetNestedType("State", BindingFlags.NonPublic)!;
            var parsedState = JsonSerializer.Deserialize(state.ToJsonString(Json), stateType, Json)!;
            var stateSha = (string)stageType.GetMethod("StateDigest", BindingFlags.NonPublic | BindingFlags.Static)!.Invoke(null, [parsedState])!; state["sha256"] = stateSha;
            File.WriteAllText(Path.Combine(StagePath, "rollback", "state.json"), state.ToJsonString(Json));
            var absences = r.Absences.Select(x => x.Destination.ProfileIdSha256 == Hash(Absent.AuthProfileId) ? new CredentialScopedRecoveryStagedAbsence(x.Source, x.Destination with { ProofSha256 = proof["sha256"]!.GetValue<string>() }) : x).ToArray();
            var changed = (CredentialScopedRecoveryStageReceipt)typeof(CredentialScopedRecoveryStageReceipt).GetConstructors(BindingFlags.NonPublic | BindingFlags.Instance).Single()
                .Invoke([r.PlanSha256, r.SourceSetSha256, r.StageId, r.StagedVaultNamespaceId, r.StagedAuthorityNamespaceId, r.DestinationRootCommitmentSha256,
                    r.DestinationKey, r.BindingSha256, stateSha, r.CompletionCoreSha256, r.ReservedUtc, r.CompletedUtc, r.Profiles, absences]);
            File.WriteAllText(Path.Combine(StagePath, "completion.json"), JsonSerializer.Serialize(changed, Json)); return changed;
        }
        internal string[] StageSnapshot() => Directory.GetFiles(StagePath, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(StagePath, p) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
        internal void RemoveSources()
        {
            foreach (var path in new[] { Options.RootPath, SourceAuthority })
            {
                var full = Path.GetFullPath(path); if (!full.StartsWith(Path.GetFullPath(Cell.Root) + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase) ||
                    Path.GetFileName(full) is not ("scoped-stage-source-vault" or "scoped-stage-source-authority")) throw new InvalidOperationException("Synthetic scoped-source deletion boundary rejected.");
                Directory.Delete(full, recursive: true);
            }
        }
        public void Dispose() { Set?.Dispose(); Signer.Dispose(); Cell.Dispose(); }
    }
}
