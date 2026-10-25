using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialScopedRecoverySourceTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Combined_source_authenticates_both_kinds_and_resolves_each_actual_key_once(bool capture)
    {
        using var f = new Fixture(); await f.Abandon(f.Scope("absent-one")); await f.Abandon(f.Scope("absent-two", "other-project")); await f.Put(); await f.Put(f.Scope("present-other"));
        var before = f.Snapshot(); f.Recording.Requests.Clear(); var inventory = await f.Observe(capture);
        Assert.Equal(2, inventory.ProfileDependencies.Count); Assert.Equal(2, inventory.AbsenceScopes.Count); Assert.Equal(new[] { "key-1" }, inventory.CurrentRequiredKeyIds);
        Assert.Single(f.Recording.Requests); Assert.Equal("key-1", f.Recording.Requests.Single()); Assert.Equal(before, f.Snapshot()); AssertPublic(inventory, f);
    }

    [Theory]
    [InlineData(false, 0)] [InlineData(true, 0)] [InlineData(false, 1)] [InlineData(true, 1)] [InlineData(false, 2)] [InlineData(true, 2)]
    public async Task Empty_positive_only_and_proof_only_sets_are_explicit_complete_metadata_without_keys_being_invented(bool capture, int kind)
    {
        using var f = new Fixture(); if (kind == 1) await f.Put(); else if (kind == 2) await f.Abandon(f.Scope("proof-only"));
        var before = f.Snapshot(); f.Recording.Requests.Clear(); var inventory = await f.Observe(capture);
        Assert.Equal(kind == 1 ? 1 : 0, inventory.ProfileDependencies.Count); Assert.Equal(kind == 2 ? 1 : 0, inventory.AbsenceScopes.Count);
        Assert.Equal(kind == 0 ? 0 : 1, f.Recording.Requests.Count); Assert.Equal(before, f.Snapshot()); AssertPublic(inventory, f);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Actual_union_includes_historical_absence_and_positive_keys_without_current_write_key_fallback(bool capture)
    {
        using var f = new Fixture(); await f.Abandon(f.Scope("old-proof")); f.Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32); await f.Put(vault: f.Open("key-2"));
        var before = f.Snapshot(); f.Recording.Requests.Clear(); var inventory = await f.Observe(capture, f.Open("unprovisioned-current-key"));
        Assert.Equal(new[] { "key-1", "key-2" }, inventory.CurrentRequiredKeyIds); Assert.Equal(new[] { "key-1", "key-2" }, f.Recording.Requests.Order(StringComparer.Ordinal));
        Assert.Equal("key-1", inventory.AbsenceScopes.Single().SourceKeyId); Assert.Equal("key-2", inventory.ProfileDependencies.Single().SourceKeyId); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData(false, "key-1")] [InlineData(true, "key-1")] [InlineData(false, "key-2")] [InlineData(true, "key-2")]
    public async Task Missing_either_real_dependency_refuses_whole_result_without_source_changes(bool capture, string missing)
    {
        using var f = new Fixture(); await f.Abandon(f.Scope("historical")); f.Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32); await f.Put(vault: f.Open("key-2"));
        f.Cell.Keys.Values.Remove(missing); var before = f.Snapshot(); await Assert.ThrowsAsync<CredentialException>(() => f.Observe(capture)); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Native_current_user_keys_capture_without_modifying_or_exporting_their_fresh_namespace()
    {
        using var f = new Fixture(native: true); await f.ProvisionNative(); await f.Abandon(f.Scope("native-proof")); await f.Put(); var before = f.Snapshot(); f.Recording.Requests.Clear();
        using var set = await f.Capture(); Assert.Single(f.Recording.Requests); using var key = f.Public();
        var verified = CredentialScopedRecoverySets.Verify(set, CredentialScopedRecoverySets.Authority(set), key, f.Identity, f.Cell.Clock);
        Assert.Single(verified.Inventory.ProfileDependencies); Assert.Single(verified.Inventory.AbsenceScopes); Assert.False(verified.EncryptedProfileAuthenticationPerformed); Assert.False(verified.AbsenceProofAuthenticationPerformed);
        Assert.Equal(before, f.Snapshot()); Assert.Single(Directory.GetFiles(f.Cell.Root, "*.dpapi.json", SearchOption.AllDirectories)); AssertPublic(verified.Inventory, f);
    }

    [WindowsCredentialFact]
    public async Task Expired_and_revoked_values_remain_authenticated_dependencies_without_being_renewed()
    {
        using var f = new Fixture(); await f.Put(); await f.Vault.RevokeAsync(f.Cell.Scope.AuthProfileId, 1); await f.Abandon(f.Scope("historical-proof")); f.Cell.Clock.Now = f.Cell.Clock.Now.AddHours(2);
        var before = f.Snapshot(); using var set = await f.Capture(); using var key = f.Public(); var verified = CredentialScopedRecoverySets.Verify(set, CredentialScopedRecoverySets.Authority(set), key, f.Identity, f.Cell.Clock);
        var profile = Assert.Single(verified.Inventory.ProfileDependencies); Assert.False(profile.Enabled); Assert.Equal(2, profile.Generation); Assert.True(profile.ExpiresUnixMilliseconds < f.Cell.Clock.Now.ToUnixTimeMilliseconds()); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Pending_anywhere_is_refused_before_providers_or_signer_access(bool capture)
    {
        using var f = new Fixture(); await f.Abandon(f.Scope("one")); await f.LeavePending(f.Scope("unrelated-pending")); var before = f.Snapshot(); f.Recording.Requests.Clear(); var signer = RSA.Create(2048); signer.Dispose();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Observe(capture, signer: signer)); Assert.Equal("credential-scoped-recovery-source-pending-reconciliation", error.Code);
        Assert.Empty(f.Recording.Requests); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("customer")] [InlineData("cell")] [InlineData("namespace")] [InlineData("commitment")] [InlineData("authority")]
    public async Task Wrong_full_namespace_is_refused_before_source_keys_or_signing(string changed)
    {
        using var f = new Fixture(); await f.Put(); var expected = f.Identity;
        expected = changed switch { "customer" => expected with { CustomerId = Guid.NewGuid().ToString() }, "cell" => expected with { CellId = Guid.NewGuid().ToString() },
            "namespace" => expected with { VaultNamespaceId = Guid.NewGuid().ToString("N") }, "commitment" => expected with { NamespaceCommitmentSha256 = new string('a', 64) }, _ => expected with { AuthorityNamespaceId = Guid.NewGuid().ToString("N") } };
        var before = f.Snapshot(); f.Recording.Requests.Clear(); var signer = RSA.Create(2048); signer.Dispose();
        Assert.Equal("credential-scoped-recovery-source-namespace-mismatch", (await Assert.ThrowsAsync<CredentialException>(() => f.Observe(true, expected: expected, signer: signer))).Code);
        Assert.Empty(f.Recording.Requests); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Invalid_public_only_signer_is_rejected_before_any_source_key_lookup()
    {
        using var f = new Fixture(); await f.Put(); await f.Abandon(f.Scope("one")); var before = f.Snapshot(); f.Recording.Requests.Clear(); using var publicOnly = f.Public();
        await Assert.ThrowsAsync<CredentialException>(() => f.Observe(true, signer: publicOnly)); Assert.Empty(f.Recording.Requests); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData(false, "state")] [InlineData(true, "state")] [InlineData(false, "positive")] [InlineData(true, "positive")]
    [InlineData(false, "unknown")] [InlineData(true, "unknown")] [InlineData(false, "marker")] [InlineData(true, "marker")]
    public async Task Provider_mutations_fail_closed_and_are_not_repaired(bool capture, string changed)
    {
        using var f = new Fixture(); await f.Put(); await f.Abandon(f.Scope("one")); var paused = new PausedKeys(f.Recording); var operation = f.Observe(capture, f.Open(keys: paused)); string[] after;
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var path = changed switch
            { "state" => Path.Combine(f.AuthorityPath, "state.json"), "positive" => Directory.GetFiles(f.Options.RootPath, "*.credential.json").Single(), "marker" => Path.Combine(f.Options.RootPath, ".credential-namespace.json"), _ => Path.Combine(f.Options.RootPath, "unexpected.json") };
            if (changed == "unknown") f.PrivateFile(path, "{}"u8.ToArray()); else File.AppendAllText(path, " "); after = f.Snapshot(); paused.Release.TrySetResult(); await Assert.ThrowsAsync<CredentialException>(() => operation);
        }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(after, f.Snapshot());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Custody_widening_after_external_key_await_is_refused_without_ACL_repair(bool capture)
    {
        using var f = new Fixture(); await f.Put(); var directory = new DirectoryInfo(f.AuthorityPath); var original = directory.GetAccessControl(); var paused = new PausedKeys(f.Recording); var operation = f.Observe(capture, f.Open(keys: paused));
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var widened = directory.GetAccessControl(); widened.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow)); directory.SetAccessControl(widened);
            var after = f.Snapshot(); paused.Release.TrySetResult(); await Assert.ThrowsAsync<CredentialException>(() => operation); Assert.Equal(after, f.Snapshot());
        }
        finally { paused.Release.TrySetResult(); directory.SetAccessControl(original); }
    }

    [Theory]
    [InlineData(false, false)] [InlineData(false, true)] [InlineData(true, false)] [InlineData(true, true)]
    public async Task Cancellation_never_detaches_a_provider_or_releases_guards_before_actual_completion(bool capture, bool uncooperative)
    {
        using var f = new Fixture(); await f.Put(); await f.Abandon(f.Scope("one")); var before = f.Snapshot(); var paused = new PausedKeys(f.Recording, !uncooperative); using var caller = new CancellationTokenSource();
        var operation = f.Observe(capture, f.Open(keys: paused), token: caller.Token);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); caller.Cancel();
            if (uncooperative) { await Task.Delay(50); Assert.False(operation.IsCompleted); Assert.Throws<IOException>(() => new FileStream(Path.Combine(f.AuthorityPath, ".rollback.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)); }
            paused.Release.TrySetResult(); var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); Assert.Equal(caller.Token, error.CancellationToken);
        }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Real_cooperating_mutation_and_both_custody_guards_remain_blocked_during_key_await(bool capture)
    {
        using var f = new Fixture(); await f.Put(); await f.Abandon(f.Scope("one")); var before = f.Snapshot(); var paused = new PausedKeys(f.Recording); var operation = f.Observe(capture, f.Open(keys: paused));
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); foreach (var guard in new[] { Path.Combine(f.Options.RootPath, ".vault.lock"), Path.Combine(f.AuthorityPath, ".rollback.lock") }) Assert.Throws<IOException>(() => new FileStream(guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            var mutation = f.Vault.RevokeAsync(f.Cell.Scope.AuthProfileId, 1); await Task.Delay(75); Assert.False(mutation.IsCompleted); Assert.Equal(before, f.Snapshot());
            paused.Release.TrySetResult(); var inventory = await operation; Assert.Equal(1, inventory.ProfileDependencies.Single().Generation); Assert.Equal(2, (await mutation).Generation);
        }
        finally { paused.Release.TrySetResult(); }
    }

    [WindowsCredentialFact]
    public async Task Signer_side_effect_after_final_signature_is_detected_and_source_is_not_repaired()
    {
        using var f = new Fixture(); await f.Put(); await f.Abandon(f.Scope("one")); string[]? changed = null;
        using var signer = new CallbackSigner(f.Signer, count => { if (count == 2) { File.AppendAllText(Path.Combine(f.AuthorityPath, "state.json"), " "); changed = f.Snapshot(); } });
        await Assert.ThrowsAsync<CredentialException>(() => f.Observe(true, signer: signer)); Assert.NotNull(changed); Assert.Equal(changed, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Validity_expiring_during_source_key_wait_prevents_a_signed_result()
    {
        using var f = new Fixture(); await f.Put(); var before = f.Snapshot(); var expires = f.Cell.Clock.Now.AddMinutes(1); var paused = new PausedKeys(f.Recording);
        var operation = f.Open(keys: paused).CaptureScopedSignedRecoverySetAsync(f.Identity, f.Signer, "scoped-source-signer", 1, f.Cell.Clock.Now, expires);
        try { await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); f.Cell.Clock.Now = expires; paused.Release.TrySetResult(); await Assert.ThrowsAsync<CredentialException>(() => operation); }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Legacy_sources_remain_unsupported_by_new_typed_source_protocol(bool capture)
    {
        using var f = new Fixture(); var before = f.Snapshot(); f.Recording.Requests.Clear();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Observe(capture, f.Cell.Vault, f.Cell.Vault.DescribeNamespace()));
        Assert.Equal("credential-scoped-recovery-source-scoped-format-required", error.Code); Assert.Empty(f.Recording.Requests); Assert.Equal(before, f.Snapshot());
    }

    static void AssertPublic(CredentialScopedRecoveryInventory inventory, Fixture f)
    {
        Assert.Equal("NotAssessed", inventory.IndependentBackupCoverage); Assert.False(inventory.KeyRetirementAuthorized); Assert.Matches("^[a-f0-9]{64}$", inventory.Sha256);
        var text = JsonSerializer.Serialize(inventory, new JsonSerializerOptions(JsonSerializerDefaults.Web));
        foreach (var value in new[] { f.Cell.Root, CredentialFixture.Token, f.Cell.Scope.AuthProfileId, "profile/scoped-source-", "\"scope\":", "\"tag\":", "\"authProfileId\":" }) Assert.DoesNotContain(value, text, StringComparison.OrdinalIgnoreCase);
    }
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    sealed class PausedKeys(ICredentialKeyProvider inner, bool cooperative = true) : ICredentialKeyProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken token)
        { Entered.TrySetResult(); if (cooperative) await Release.Task.WaitAsync(token); else await Release.Task; return await inner.GetKeyAsync(customer, cell, key, cooperative ? token : CancellationToken.None); }
    }
    sealed class RecordedKeys(ICredentialKeyProvider inner) : ICredentialKeyProvider
    {
        public List<string> Requests { get; } = [];
        public ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken token) { Requests.Add(key); return inner.GetKeyAsync(customer, cell, key, token); }
    }
    sealed class CallbackSigner(RSA inner, Action<int> signed) : RSA
    {
        int calls;
        public override int KeySize { get => inner.KeySize; set => throw new NotSupportedException(); }
        public override RSAParameters ExportParameters(bool includePrivateParameters) => includePrivateParameters ? throw new NotSupportedException() : inner.ExportParameters(false);
        public override void ImportParameters(RSAParameters parameters) => throw new NotSupportedException();
        public override byte[] Encrypt(byte[] data, RSAEncryptionPadding padding) => throw new NotSupportedException();
        public override byte[] Decrypt(byte[] data, RSAEncryptionPadding padding) => throw new NotSupportedException();
        public override bool VerifyHash(byte[] hash, byte[] signature, HashAlgorithmName algorithm, RSASignaturePadding padding) => inner.VerifyHash(hash, signature, algorithm, padding);
        public override byte[] SignHash(byte[] hash, HashAlgorithmName algorithm, RSASignaturePadding padding) { var signature = inner.SignHash(hash, algorithm, padding); signed(++calls); return signature; }
    }
    sealed class Fixture : IDisposable
    {
        public CredentialFixture Cell { get; } = new(); public RSA Signer { get; } = RSA.Create(2048);
        public CredentialVaultOptions Options { get; } public CredentialVault Vault { get; } public CredentialVaultNamespaceIdentity Identity { get; }
        public RecordedKeys Recording { get; } public CredentialWindowsUserKeyProvider? Native { get; }
        public string AuthorityPath => Options.RollbackProtection!.AuthorityDirectory;
        public Fixture(bool native = false)
        {
            Cell.Clock.Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z"); if (native) Native = Cell.ProvisionNativeKeys("scoped-capture-native-keys"); Recording = new(Native is null ? Cell.Keys : Native);
            Options = Cell.Options with { RootPath = Path.Combine(Cell.Root, "scoped-capture-vault"), RollbackProtection = new(Path.Combine(Cell.Root, "scoped-capture-authority")) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 } };
            Vault = CredentialVault.ProvisionStrongScoped(Options, Recording, Cell.Clock); Identity = Vault.DescribeNamespace();
        }
        public async Task ProvisionNative() { using (await Native!.ProvisionKeyAsync(Cell.Scope.CustomerId, Cell.Scope.CellId, "key-1")) { } }
        public CredentialScope Scope(string suffix, string project = "default") => Cell.Scope with { AuthProfileId = "profile/scoped-source-" + suffix, ProjectId = project };
        public CredentialVault Open(string keyId = "key-1", ICredentialKeyProvider? keys = null) => new(Options with { EncryptionKeyId = keyId }, keys ?? Recording, Cell.Clock);
        public async Task Put(CredentialScope? scope = null, CredentialVault? vault = null) { using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token); await (vault ?? Vault).PutAsync(scope ?? Cell.Scope, secret, Cell.Clock.Now.AddHours(1)); }
        public async Task LeavePending(CredentialScope scope)
        {
            var property = typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.NonPublic | BindingFlags.Instance)!; property.SetValue(Vault, (Action<string>)(phase => { if (phase == "PendingPersisted") throw new IOException("Synthetic pending interruption."); }));
            try { await Assert.ThrowsAsync<IOException>(() => Put(scope)); } finally { property.SetValue(Vault, null); }
        }
        public async Task Abandon(CredentialScope scope)
        { await LeavePending(scope); var pending = await Vault.InspectRollbackStateAsync(scope.AuthProfileId); await Vault.ReconcilePendingWriteAsync(scope.AuthProfileId, pending.AuthorityRevision, pending.AuthorityStateSha256, pending.Pending!.PendingSha256, CredentialRollbackResolution.AbandonUnpublished); }
        public Task<CredentialScopedRecoverySet> Capture(CredentialVault? vault = null, CredentialVaultNamespaceIdentity? expected = null, RSA? signer = null, CancellationToken token = default) =>
            (vault ?? Vault).CaptureScopedSignedRecoverySetAsync(expected ?? Identity, signer ?? Signer, "scoped-source-signer", 1, Cell.Clock.Now, Cell.Clock.Now.AddHours(1), token);
        public async Task<CredentialScopedRecoveryInventory> Observe(bool capture, CredentialVault? vault = null, CredentialVaultNamespaceIdentity? expected = null, RSA? signer = null, CancellationToken token = default)
        {
            if (!capture) return await (vault ?? Vault).InspectScopedRecoveryDependenciesAsync(expected ?? Identity, token);
            using var set = await Capture(vault, expected, signer, token); using var key = Public(); return CredentialScopedRecoverySets.Verify(set, CredentialScopedRecoverySets.Authority(set), key, expected ?? Identity, Cell.Clock).Inventory;
        }
        public RSA Public() { var key = RSA.Create(); key.ImportSubjectPublicKeyInfo(Signer.ExportSubjectPublicKeyInfo(), out _); return key; }
        public void PrivateFile(string path, byte[] bytes)
        {
            using var current = WindowsIdentity.GetCurrent(); var acl = new FileSecurity(); acl.SetOwner(current.User!); acl.SetAccessRuleProtection(true, false); acl.AddAccessRule(new FileSystemAccessRule(current.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            using var file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.Read, 4096, FileOptions.WriteThrough, acl); file.Write(bytes); file.Flush(true);
        }
        public string[] Snapshot() => Directory.GetFileSystemEntries(Cell.Root, "*", SearchOption.AllDirectories).Prepend(Cell.Root).Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(Cell.Root, p) + ":" + (Directory.Exists(p) ? "directory" : new FileInfo(p).Length == 0 ? Hash([]) : Hash(File.ReadAllBytes(p))) + ":" +
                (Directory.Exists(p) ? (FileSystemSecurity)new DirectoryInfo(p).GetAccessControl() : new FileInfo(p).GetAccessControl()).GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access)).ToArray();
        public void Dispose() { Signer.Dispose(); Cell.Dispose(); }
    }
}
