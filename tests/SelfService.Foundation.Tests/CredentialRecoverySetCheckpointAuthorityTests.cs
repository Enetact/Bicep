using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialRecoverySetCheckpointAuthorityTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [WindowsCredentialFact]
    public async Task Exact_reviewed_set_is_rehearsed_and_committed_without_copying_private_artifact_or_changing_source()
    {
        using var f = new Fixture(); await f.Cell.StoreToken(); using var set = await f.Capture(7); var authority = f.Provision();
        var identity = authority.Identity; Assert.Null(authority.ReadCurrent()); var before = f.SourceSnapshot(); f.Cell.Keys.Requests.Clear();
        var head = await f.Accept(authority, set);
        Assert.Equal(1, head.Sequence); Assert.Equal(7, head.SourceGeneration); Assert.Equal("", head.PreviousSha256);
        Assert.Equal(identity.AuthorityId, head.AuthorityId); Assert.Equal(identity.CommitmentSha256, head.AuthorityCommitmentSha256);
        Assert.Equal(CredentialRecoverySets.Authority(set), head.SetAuthority); Assert.Equal(f.Namespace, head.SourceNamespace);
        Assert.Equal(set.Signature.InventorySha256, head.InventorySha256); Assert.Equal(64, head.RehearsalSha256.Length);
        Assert.Single(f.Cell.Keys.Requests); Assert.Equal("key-1", f.Cell.Keys.Requests[0].KeyId); Assert.Equal(head, f.Reopen().ReadCurrent());
        Assert.Equal(CheckpointDigest(head), head.Sha256); Assert.Equal(before, f.SourceSnapshot());
        Assert.Equal(new[] { CredentialRecoverySetCheckpointAuthority.GuardName, CredentialRecoverySetCheckpointAuthority.MarkerName, "checkpoint-0001.json" },
            Directory.GetFiles(f.AuthorityPath).Select(p => Path.GetFileName(p)!).Order(StringComparer.Ordinal));
        AssertPrivate(f.AuthorityPath, true); foreach (var path in Directory.GetFiles(f.AuthorityPath)) AssertPrivate(path, false);
        var json = JsonSerializer.Serialize(new { identity, head }, Json); using var owner = WindowsIdentity.GetCurrent();
        Assert.DoesNotContain(f.Cell.Root, json); Assert.DoesNotContain(owner.User!.Value, json); Assert.DoesNotContain(CredentialFixture.Token, json);
        Assert.DoesNotContain("ciphertext", json, StringComparison.OrdinalIgnoreCase); Assert.DoesNotContain("signature", json, StringComparison.OrdinalIgnoreCase);
        Assert.Equal("NotAssessed", head.IndependentBackupCoverage); Assert.False(head.KeyBackupVerified); Assert.False(head.PromotionAuthorized);
        Assert.False(head.ActivationPerformed); Assert.False(head.ExternalExecutionAuthorized);
    }

    [WindowsCredentialFact]
    public async Task Set_generations_increase_independently_of_local_sequence_and_old_history_is_never_rewritten()
    {
        using var f = new Fixture(); await f.Cell.StoreToken(); var authority = f.Provision(); using var first = await f.Capture(3); using var next = await f.Capture(12);
        var head = await f.Accept(authority, first); var original = File.ReadAllBytes(Path.Combine(f.AuthorityPath, "checkpoint-0001.json"));
        var latest = await f.Accept(authority, next, head); Assert.Equal(2, latest.Sequence); Assert.Equal(12, latest.SourceGeneration); Assert.Equal(head.Sha256, latest.PreviousSha256);
        Assert.Equal(original, File.ReadAllBytes(Path.Combine(f.AuthorityPath, "checkpoint-0001.json"))); var before = f.AuthoritySnapshot(); f.Cell.Keys.Requests.Clear();
        Assert.Equal("CredentialRecoveryCheckpointGenerationChanged", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(authority, next, head))).Code);
        Assert.Equal("CredentialRecoveryCheckpointSetGenerationNotIncreasing", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(authority, first, latest))).Code);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.AuthoritySnapshot());
    }

    [WindowsCredentialFact]
    public async Task Retained_shared_read_leases_pin_exact_head_and_block_all_cooperating_advances_until_released()
    {
        using var f = new Fixture(); await f.Cell.StoreToken(); var authority = f.Provision(); using var first = await f.Capture(1); using var next = await f.Capture(2);
        var head = await f.Accept(authority, first); var one = authority.AcquireReadLease(head.Sequence, head.Sha256); var two = f.Reopen().AcquireReadLease(head.Sequence, head.Sha256);
        try
        {
            one.AssertCurrent(); two.AssertCurrent(); Assert.Equal(head, one.Checkpoint); Assert.Equal(authority.Identity, one.Identity);
            Assert.Equal("CredentialRecoveryCheckpointBusy", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(authority, next, head))).Code);
            one.Dispose(); one.Dispose(); Assert.Equal("CredentialRecoveryCheckpointLeaseDisposed", Assert.Throws<FoundationStoreException>(() => one.AssertCurrent()).Code);
            Assert.Equal("CredentialRecoveryCheckpointBusy", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(authority, next, head))).Code);
        }
        finally { one.Dispose(); two.Dispose(); }
        Assert.Equal(2, (await f.Accept(authority, next, head)).Sequence);
        Assert.Equal("CredentialRecoveryCheckpointGenerationChanged", Assert.Throws<FoundationStoreException>(() => authority.AcquireReadLease(head.Sequence, head.Sha256)).Code);
    }

    [WindowsCredentialFact]
    public async Task Wrong_review_namespace_or_signer_is_refused_before_source_key_access_without_mutation()
    {
        using var f = new Fixture(); await f.Cell.StoreToken(); var authority = f.Provision(); using var set = await f.Capture(1); var before = f.AuthoritySnapshot(); f.Cell.Keys.Requests.Clear();
        Assert.Equal("CredentialRecoveryCheckpointReviewedArtifactMismatch", (await Assert.ThrowsAsync<FoundationStoreException>(() =>
            authority.AcceptNextAsync(set, new string('0', 64), f.Signer, f.Cell.Keys, 0, null))).Code);
        using var otherSigner = RSA.Create(2048);
        Assert.Equal("CredentialRecoveryCheckpointSignerBindingMismatch", (await Assert.ThrowsAsync<FoundationStoreException>(() =>
            authority.AcceptNextAsync(set, set.Sha256, otherSigner, f.Cell.Keys, 0, null))).Code);
        var otherVault = CredentialVault.Provision(f.Cell.Options with { RootPath = Path.Combine(f.Cell.Root, "other-checkpoint-source") }, f.Cell.Keys, f.Cell.Clock);
        using var other = await otherVault.CaptureSignedRecoverySetAsync(otherVault.DescribeNamespace(), f.Signer, f.KeyId, 2, f.Cell.Clock.Now, f.Cell.Clock.Now.AddHours(1));
        Assert.Equal("CredentialRecoveryCheckpointNamespaceMismatch", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(authority, other))).Code);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.AuthoritySnapshot());
    }

    [WindowsCredentialFact]
    public async Task Wrong_source_key_fails_real_retained_AES_and_releases_guard_without_partial_checkpoint()
    {
        using var f = new Fixture(); await f.Cell.StoreToken(); var authority = f.Provision(); using var set = await f.Capture(1); var before = f.AuthoritySnapshot();
        var wrong = new CredentialTestKeys();
        var error = await Assert.ThrowsAsync<FoundationStoreException>(() => authority.AcceptNextAsync(set, set.Sha256, f.Signer, wrong, 0, null));
        Assert.Equal("CredentialRecoveryCheckpointCandidateInvalid", error.Code); Assert.DoesNotContain(f.Cell.Root, error.ToString()); Assert.DoesNotContain(CredentialFixture.Token, error.ToString());
        Assert.Single(wrong.Requests); Assert.Equal(before, f.AuthoritySnapshot()); Assert.Null(authority.ReadCurrent());
        Assert.Equal(1, (await f.Accept(authority, set)).Sequence);
    }

    [WindowsCredentialFact]
    public async Task Acceptance_uses_actual_retained_bytes_when_source_vault_has_been_removed_and_write_key_is_absent()
    {
        using var f = new Fixture(); await f.Cell.StoreToken(); using var set = await f.Capture(1); var authority = f.Provision();
        var source = Path.GetFullPath(f.Cell.VaultRoot); var expected = Path.GetFullPath(f.Cell.Root) + Path.DirectorySeparatorChar;
        Assert.StartsWith(expected, source, StringComparison.OrdinalIgnoreCase); Assert.Equal("vault", Path.GetFileName(source)); Directory.Delete(source, true);
        f.Cell.Keys.Requests.Clear(); var head = await f.Accept(authority, set);
        Assert.Equal(set.Sha256, head.SetAuthority.ArtifactSha256); Assert.False(Directory.Exists(source)); Assert.Single(f.Cell.Keys.Requests);
        Assert.Equal("key-1", f.Cell.Keys.Requests[0].KeyId); Assert.Single(f.Cell.Keys.Values);
    }

    [WindowsCredentialFact]
    public async Task Cancellation_during_source_key_wait_keeps_root_unchanged_and_allows_next_explicit_admission()
    {
        using var f = new Fixture(); await f.Cell.StoreToken(); using var set = await f.Capture(1); var authority = f.Provision(); var before = f.AuthoritySnapshot();
        using var canceled = new CancellationTokenSource(); var paused = new PausedKeys(f.Cell.Keys);
        var pending = authority.AcceptNextAsync(set, set.Sha256, f.Signer, paused, 0, null, canceled.Token); await paused.Started.Task.WaitAsync(TimeSpan.FromSeconds(10));
        Assert.Equal("CredentialRecoveryCheckpointBusy", Assert.Throws<FoundationStoreException>(() => f.Reopen()).Code);
        canceled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        Assert.Equal(before, f.AuthoritySnapshot()); Assert.Null(authority.ReadCurrent()); Assert.Equal(1, (await f.Accept(authority, set)).Sequence);
    }

    [WindowsCredentialFact]
    public async Task Detached_input_and_frozen_public_key_survive_original_set_and_RSA_disposal_during_source_await()
    {
        using var f = new Fixture(); await f.Cell.StoreToken(); using var set = await f.Capture(1); var selected = CredentialRecoverySets.Authority(set); var authority = f.Provision();
        var paused = new PausedKeys(f.Cell.Keys); var pending = authority.AcceptNextAsync(set, set.Sha256, f.Signer, paused, 0, null);
        await paused.Started.Task.WaitAsync(TimeSpan.FromSeconds(10)); set.Dispose(); f.Signer.Dispose(); paused.Release.TrySetResult();
        Assert.Equal(selected, (await pending).SetAuthority);
    }

    [WindowsCredentialFact]
    public async Task Actual_concurrent_CAS_has_one_winner_and_never_replays_a_losing_admission()
    {
        using var f = new Fixture(); await f.Cell.StoreToken(); using var set = await f.Capture(4); var authority = f.Provision();
        async Task<bool> Attempt() => await Task.Run(async () => { try { await f.Accept(authority, set); return true; } catch (FoundationStoreException) { return false; } });
        var outcomes = await Task.WhenAll(Attempt(), Attempt()); Assert.Single(outcomes, v => v); Assert.Single(outcomes, v => !v);
        Assert.Equal(1, authority.ReadCurrent()!.Sequence); Assert.Single(Directory.GetFiles(f.AuthorityPath, "checkpoint-*.json"));
    }

    [WindowsCredentialFact]
    public async Task Revoked_expired_profiles_are_authenticated_but_signature_expiry_during_await_cannot_append()
    {
        using var f = new Fixture(); await f.Cell.StoreToken(); await f.Cell.Vault.RevokeAsync(f.Cell.Scope.AuthProfileId, 1); f.Cell.Clock.Now = f.Cell.Clock.Now.AddHours(2);
        using var set = await f.Capture(1); var authority = f.Provision(); var before = f.AuthoritySnapshot(); var originalNow = f.Cell.Clock.Now;
        f.Cell.Keys.OnGet = () => f.Cell.Clock.Now = set.Signature.ExpiresUtc;
        Assert.Equal("CredentialRecoveryCheckpointCandidateInvalid", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Accept(authority, set))).Code);
        Assert.Equal(before, f.AuthoritySnapshot()); f.Cell.Keys.OnGet = null; f.Cell.Clock.Now = originalNow;
        var head = await f.Accept(authority, set); f.Cell.Clock.Now = set.Signature.ExpiresUtc.AddMinutes(1);
        Assert.Equal(head, authority.ReadCurrent()); Assert.False(head.PromotionAuthorized); Assert.False(head.KeyBackupVerified);
    }

    [WindowsCredentialFact]
    public async Task Actual_fresh_CurrentUser_DPAPI_key_can_rehearse_without_modifying_or_exporting_its_namespace()
    {
        using var f = new Fixture(); var baseRoot = Path.Combine(f.Cell.Root, "checkpoint-native-keys"); Directory.CreateDirectory(baseRoot);
        var keys = CredentialWindowsUserKeyProvider.ProvisionNamespace(baseRoot, f.Cell.Scope.CustomerId, f.Cell.Scope.CellId);
        using (await keys.ProvisionKeyAsync(f.Cell.Scope.CustomerId, f.Cell.Scope.CellId, "native-source")) { }
        var vault = CredentialVault.Provision(f.Cell.Options with { RootPath = Path.Combine(f.Cell.Root, "checkpoint-native-vault"), EncryptionKeyId = "native-source" }, keys, f.Cell.Clock);
        using (var secret = CredentialSecret.FromUtf8(CredentialFixture.Token)) await vault.PutAsync(f.Cell.Scope, secret, f.Cell.Clock.Now.AddHours(1));
        var ns = vault.DescribeNamespace(); using var set = await vault.CaptureSignedRecoverySetAsync(ns, f.Signer, f.KeyId, 1, f.Cell.Clock.Now, f.Cell.Clock.Now.AddHours(1));
        var authority = CredentialRecoverySetCheckpointAuthority.Provision(f.AuthorityPath, ns, f.KeyId, f.Pin, f.Cell.Clock);
        var keyFiles = Directory.GetFiles(baseRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(p => p + ":" + Hash(File.ReadAllBytes(p))).ToArray();
        var head = await authority.AcceptNextAsync(set, set.Sha256, f.Signer, keys, 0, null);
        Assert.Equal(set.Sha256, head.SetAuthority.ArtifactSha256); Assert.False(head.KeyBackupVerified);
        Assert.Equal(keyFiles, Directory.GetFiles(baseRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(p => p + ":" + Hash(File.ReadAllBytes(p))).ToArray());
        Assert.Single(Directory.GetFiles(baseRoot, "*.dpapi.json", SearchOption.AllDirectories));
    }

    static string CheckpointDigest(CredentialRecoverySetCheckpoint c) => Hash(JsonSerializer.SerializeToUtf8Bytes(new
    { schema = 1, kind = "credential-recovery-checkpoint", c.AuthorityId, c.AuthorityCommitmentSha256, c.Sequence, c.PreviousSha256,
        acceptedUnixMilliseconds = c.AcceptedUtc.ToUnixTimeMilliseconds(), c.SourceNamespace, c.SetAuthority, c.InventorySha256, c.RehearsalSha256,
        capturedUnixMilliseconds = c.CapturedUtc.ToUnixTimeMilliseconds(), issuedUnixMilliseconds = c.IssuedUtc.ToUnixTimeMilliseconds(),
        notBeforeUnixMilliseconds = c.NotBeforeUtc.ToUnixTimeMilliseconds(), expiresUnixMilliseconds = c.ExpiresUtc.ToUnixTimeMilliseconds() }, Json));
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static void AssertPrivate(string path, bool directory)
    {
        using var owner = WindowsIdentity.GetCurrent(); var security = directory ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        Assert.True(security.AreAccessRulesProtected); Assert.Equal(owner.User, security.GetOwner(typeof(SecurityIdentifier)));
        Assert.All(security.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Where(r => r.AccessControlType == AccessControlType.Allow), r => Assert.Equal(owner.User!.Value, r.IdentityReference.Value));
    }
    sealed class PausedKeys(ICredentialKeyProvider inner) : ICredentialKeyProvider
    {
        internal TaskCompletionSource Started { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        internal TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken cancellationToken)
        { Started.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return await inner.GetKeyAsync(customerId, cellId, keyId, cancellationToken); }
    }
    sealed class Fixture : IDisposable
    {
        internal CredentialFixture Cell { get; } = new();
        internal RSA Signer { get; } = RSA.Create(2048);
        internal string KeyId => "credential-checkpoint-signer";
        internal string Pin => FoundationAuditAnchors.PublicKeyFingerprint(Signer);
        internal CredentialVaultNamespaceIdentity Namespace => Cell.Vault.DescribeNamespace();
        internal string AuthorityPath => Path.Combine(Cell.Root, "credential-checkpoints");
        internal Fixture() => Cell.Clock.Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
        internal CredentialRecoverySetCheckpointAuthority Provision() => CredentialRecoverySetCheckpointAuthority.Provision(AuthorityPath, Namespace, KeyId, Pin, Cell.Clock);
        internal CredentialRecoverySetCheckpointAuthority Reopen() => new(AuthorityPath, Namespace, KeyId, Pin, Cell.Clock);
        internal Task<CredentialRecoverySet> Capture(long generation) => Cell.Vault.CaptureSignedRecoverySetAsync(Namespace, Signer, KeyId, generation, Cell.Clock.Now, Cell.Clock.Now.AddHours(1));
        internal Task<CredentialRecoverySetCheckpoint> Accept(CredentialRecoverySetCheckpointAuthority authority, CredentialRecoverySet set, CredentialRecoverySetCheckpoint? previous = null) =>
            authority.AcceptNextAsync(set, set.Sha256, Signer, Cell.Keys, previous?.Sequence ?? 0, previous?.Sha256);
        internal string[] SourceSnapshot() => Directory.GetFiles(Cell.VaultRoot, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal).Select(p => p + ":" + Hash(File.ReadAllBytes(p))).ToArray();
        internal string[] AuthoritySnapshot() => Directory.GetFileSystemEntries(AuthorityPath).Order(StringComparer.Ordinal).Select(p => Path.GetFileName(p) + ":" + Hash(File.ReadAllBytes(p))).ToArray();
        public void Dispose() { Signer.Dispose(); Cell.Dispose(); }
    }
}
