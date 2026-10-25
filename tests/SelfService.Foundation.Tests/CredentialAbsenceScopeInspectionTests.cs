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
public sealed class CredentialAbsenceScopeInspectionTests
{
    [WindowsCredentialFact]
    public async Task Authenticated_absences_return_only_frozen_scope_commitments_and_original_key_dependencies()
    {
        using var f = new Fixture(); await f.Abandon(f.Scope("one", "first-project", "dev")); await f.Abandon(f.Scope("two", "second-project", "qa")); await f.Put();
        var before = f.Snapshot(); f.ClearKeys(); var report = await f.Vault.InspectAbsenceScopeProvenanceAsync(f.Identity);
        Assert.Equal(f.Identity, report.Namespace); Assert.Equal(2, report.AuthenticatedAbsenceCount); Assert.Equal(1, report.PositiveEnvelopeCount);
        Assert.Equal(new[] { "key-1" }, report.AbsenceRequiredKeyIds); Assert.Equal(new[] { "first-project/dev", "second-project/qa" }, report.AbsenceScopes.Select(p => p.ProjectId + "/" + p.EnvironmentId).Order(StringComparer.Ordinal));
        Assert.Single(f.Cell.Keys.Requests); Assert.All(f.Cell.Keys.Requests, r => Assert.Equal("key-1", r.KeyId)); Assert.Equal(before, f.Snapshot()); AssertPublic(report, f);
        Assert.Throws<NotSupportedException>(() => ((IList<CredentialAbsenceScopeDependency>)report.AbsenceScopes).Clear());
        Assert.Throws<NotSupportedException>(() => ((IList<string>)report.AbsenceRequiredKeyIds).Clear());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Empty_or_positive_only_roots_resolve_no_keys_and_never_claim_positive_envelope_authentication(bool positive)
    {
        using var f = new Fixture(); if (positive) await f.Put(); f.Cell.Keys.Values.Clear(); f.ClearKeys(); var before = f.Snapshot();
        var report = await f.Vault.InspectAbsenceScopeProvenanceAsync(f.Identity);
        Assert.Equal(positive ? 1 : 0, report.PositiveEnvelopeCount); Assert.Empty(report.AbsenceScopes); Assert.Empty(report.AbsenceRequiredKeyIds);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot()); AssertPublic(report, f);
    }

    [Theory]
    [InlineData("customer")] [InlineData("cell")] [InlineData("vault")] [InlineData("commitment")]
    [InlineData("authority")] [InlineData("binding")]
    public async Task Exact_namespace_is_required_before_original_proof_key_access(string field)
    {
        using var f = new Fixture(); await f.Abandon(f.Scope("one")); var expected = f.Identity;
        var wrong = field switch
        {
            "customer" => expected with { CustomerId = Guid.NewGuid().ToString() }, "cell" => expected with { CellId = Guid.NewGuid().ToString() },
            "vault" => expected with { VaultNamespaceId = Guid.NewGuid().ToString("N") }, "commitment" => expected with { NamespaceCommitmentSha256 = new string('a', 64) },
            "authority" => expected with { AuthorityNamespaceId = Guid.NewGuid().ToString("N") }, _ => expected with { AuthorityBindingSha256 = new string('b', 64) }
        };
        var before = f.Snapshot(); f.ClearKeys(); var error = await Assert.ThrowsAsync<CredentialException>(() => f.Vault.InspectAbsenceScopeProvenanceAsync(wrong));
        Assert.Equal("credential-absence-scope-inspection-namespace-mismatch", error.Code); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Any_pending_profile_blocks_all_absence_key_work_without_reconciliation_or_writes()
    {
        using var f = new Fixture(); await f.Abandon(f.Scope("one")); await f.LeavePending(f.Scope("pending-other")); var before = f.Snapshot(); f.ClearKeys();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Vault.InspectAbsenceScopeProvenanceAsync(f.Identity));
        Assert.Equal("credential-absence-scope-inspection-pending-reconciliation", error.Code); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Historical_proof_key_is_required_without_fallback_to_available_current_write_key()
    {
        using var f = new Fixture(); await f.Abandon(f.Scope("old-key")); f.Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32); var current = f.Open("key-2");
        f.Cell.Keys.Values.Remove("key-1"); var before = f.Snapshot(); f.ClearKeys();
        await Assert.ThrowsAsync<CredentialException>(() => current.InspectAbsenceScopeProvenanceAsync(f.Identity));
        Assert.Single(f.Cell.Keys.Requests); Assert.Equal("key-1", f.Cell.Keys.Requests.Single().KeyId); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Unavailable_current_write_and_positive_keys_do_not_hide_available_historical_absence_scope()
    {
        using var f = new Fixture(); await f.Abandon(f.Scope("old-key")); f.Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32); var current = f.Open("key-2"); await f.Put(vault: current);
        f.Cell.Keys.Values.Remove("key-2"); var before = f.Snapshot(); f.ClearKeys(); var report = await current.InspectAbsenceScopeProvenanceAsync(f.Identity);
        Assert.Single(f.Cell.Keys.Requests); Assert.Equal("key-1", f.Cell.Keys.Requests.Single().KeyId); Assert.Equal(new[] { "key-1" }, report.AbsenceRequiredKeyIds);
        Assert.Equal(1, report.PositiveEnvelopeCount); Assert.Equal(before, f.Snapshot()); AssertPublic(report, f);
    }

    [WindowsCredentialFact]
    public async Task Each_distinct_original_key_is_resolved_once_and_no_write_key_is_requested()
    {
        using var f = new Fixture(); await f.Abandon(f.Scope("one")); await f.Abandon(f.Scope("two")); f.Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32); await f.Abandon(f.Scope("three"), f.Open("key-2"));
        var before = f.Snapshot(); f.ClearKeys(); var report = await f.Open("key-not-provisioned").InspectAbsenceScopeProvenanceAsync(f.Identity);
        Assert.Equal(3, report.AuthenticatedAbsenceCount); Assert.Equal(new[] { "key-1", "key-2" }, report.AbsenceRequiredKeyIds);
        Assert.Equal(new[] { "key-1", "key-2" }, f.Cell.Keys.Requests.Select(r => r.KeyId).Order(StringComparer.Ordinal)); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("unknown-file")] [InlineData("directory")] [InlineData("partial-vault")]
    [InlineData("partial-state")] [InlineData("present-absence")]
    public async Task Unknown_partial_or_nonabsent_entries_fail_whole_inspection_before_keys(string kind)
    {
        using var f = new Fixture(); var scope = f.Scope("one"); await f.Abandon(scope);
        var path = kind switch
        {
            "unknown-file" => Path.Combine(f.Options.RootPath, "unreviewed.json"), "directory" => Path.Combine(f.Options.RootPath, "unexpected-directory"),
            "partial-vault" => Path.Combine(f.Options.RootPath, ".write-" + Guid.NewGuid().ToString("N")),
            "partial-state" => Path.Combine(f.AuthorityPath, ".state-write-" + Guid.NewGuid().ToString("N")),
            _ => Path.Combine(f.Options.RootPath, Hash(System.Text.Encoding.UTF8.GetBytes(scope.AuthProfileId)) + ".credential.json")
        };
        if (kind == "directory") Directory.CreateDirectory(path); else f.PrivateFile(path, "{}"u8.ToArray()); var before = f.Snapshot(); f.ClearKeys();
        await Assert.ThrowsAsync<CredentialException>(() => f.Vault.InspectAbsenceScopeProvenanceAsync(f.Identity));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("vault-marker")] [InlineData("authority-marker")] [InlineData("state-bytes")]
    [InlineData("unknown-file")] [InlineData("positive-bytes")]
    public async Task Provider_await_mutation_is_detected_without_partial_report_or_repairs(string changed)
    {
        using var f = new Fixture(); await f.Abandon(f.Scope("one")); await f.Put(); var paused = new PausedKeys(f.Cell.Keys); var vault = f.Open(keys: paused);
        var operation = vault.InspectAbsenceScopeProvenanceAsync(f.Identity); string[] afterMutation;
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10));
            var path = changed switch
            {
                "vault-marker" => Path.Combine(f.Options.RootPath, ".credential-namespace.json"), "authority-marker" => Path.Combine(f.AuthorityPath, ".credential-namespace.json"),
                "state-bytes" => Path.Combine(f.AuthorityPath, "state.json"), "positive-bytes" => Directory.GetFiles(f.Options.RootPath, "*.credential.json").Single(),
                _ => Path.Combine(f.Options.RootPath, "unexpected-after-key.json")
            };
            if (changed == "unknown-file") f.PrivateFile(path, "{}"u8.ToArray()); else File.AppendAllText(path, " "); // Even semantically equivalent byte replacement is detected.
            afterMutation = f.Snapshot(); paused.Release.TrySetResult(); await Assert.ThrowsAsync<CredentialException>(() => operation);
        }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(afterMutation, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Both_real_custody_guards_and_cooperating_mutation_remain_blocked_during_key_await()
    {
        using var f = new Fixture(); await f.Abandon(f.Scope("one")); await f.Put(); var before = f.Snapshot(); var paused = new PausedKeys(f.Cell.Keys);
        var operation = f.Open(keys: paused).InspectAbsenceScopeProvenanceAsync(f.Identity);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); Assert.Throws<IOException>(() => new FileStream(Path.Combine(f.Options.RootPath, ".vault.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            Assert.Throws<IOException>(() => new FileStream(Path.Combine(f.AuthorityPath, ".rollback.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            var mutation = f.Vault.RevokeAsync(f.Cell.Scope.AuthProfileId, 1); await Task.Delay(75); Assert.False(mutation.IsCompleted); Assert.Equal(before, f.Snapshot());
            paused.Release.TrySetResult(); var report = await operation; Assert.Equal(1, report.AuthenticatedAbsenceCount); Assert.Equal(2, (await mutation).Generation);
        }
        finally { paused.Release.TrySetResult(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Widened_custody_during_key_await_is_rejected_without_ACL_repair(bool authority)
    {
        using var f = new Fixture(); await f.Abandon(f.Scope("one")); var paused = new PausedKeys(f.Cell.Keys);
        var directory = new DirectoryInfo(authority ? f.AuthorityPath : f.Options.RootPath); var original = directory.GetAccessControl();
        var operation = f.Open(keys: paused).InspectAbsenceScopeProvenanceAsync(f.Identity);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); var widened = directory.GetAccessControl();
            widened.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow)); directory.SetAccessControl(widened);
            var changed = f.Snapshot(); paused.Release.TrySetResult(); await Assert.ThrowsAsync<CredentialException>(() => operation); Assert.Equal(changed, f.Snapshot());
        }
        finally { paused.Release.TrySetResult(); directory.SetAccessControl(original); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Cancellation_does_not_detach_a_key_provider_or_release_guards_before_actual_return(bool uncooperative)
    {
        using var f = new Fixture(); await f.Abandon(f.Scope("one")); var before = f.Snapshot(); var paused = new PausedKeys(f.Cell.Keys, cooperative: !uncooperative); using var caller = new CancellationTokenSource();
        var operation = f.Open(keys: paused).InspectAbsenceScopeProvenanceAsync(f.Identity, caller.Token);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(10)); caller.Cancel();
            if (uncooperative) { await Task.Delay(50); Assert.False(operation.IsCompleted); Assert.Throws<IOException>(() => new FileStream(Path.Combine(f.AuthorityPath, ".rollback.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None)); }
            paused.Release.TrySetResult(); var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); Assert.Equal(caller.Token, error.CancellationToken);
        }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(before, f.Snapshot()); using var guard = new FileStream(Path.Combine(f.Options.RootPath, ".vault.lock"), FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [WindowsCredentialFact]
    public async Task Caller_cancellation_before_start_never_resolves_any_source_key()
    {
        using var f = new Fixture(); await f.Abandon(f.Scope("one")); f.ClearKeys(); var before = f.Snapshot(); using var canceled = new CancellationTokenSource(); canceled.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Vault.InspectAbsenceScopeProvenanceAsync(f.Identity, canceled.Token)); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Cancellation_during_final_observation_time_sampling_cannot_publish_a_successful_report()
    {
        using var f = new Fixture(); await f.Abandon(f.Scope("one")); using var caller = new CancellationTokenSource();
        var clock = new CancelOnObservationClock(f.Cell.Clock.Now, caller.Cancel); f.Cell.Keys.OnGet = clock.Arm;
        var vault = new CredentialVault(f.Options, f.Cell.Keys, clock); var before = f.Snapshot();
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => vault.InspectAbsenceScopeProvenanceAsync(f.Identity, caller.Token));
        Assert.True(clock.Observed); Assert.Equal(caller.Token, error.CancellationToken); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Provider_errors_are_opaque_and_never_report_a_partial_success(bool unrelatedCancellation)
    {
        using var f = new Fixture(); await f.Abandon(f.Scope("one")); var before = f.Snapshot();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Open(keys: new FailedKeys(f.Cell.Root, unrelatedCancellation)).InspectAbsenceScopeProvenanceAsync(f.Identity));
        if (unrelatedCancellation) Assert.Equal("credential-absence-scope-inspection-unavailable", error.Code);
        Assert.DoesNotContain(f.Cell.Root, error.ToString()); Assert.DoesNotContain(CredentialFixture.Token, error.ToString()); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Legacy_roots_are_not_relabelled_or_upgraded_by_absence_inspection()
    {
        using var f = new Fixture(); var before = f.Snapshot(); f.ClearKeys();
        Assert.Equal("credential-absence-scope-inspection-scoped-format-required", (await Assert.ThrowsAsync<CredentialException>(() => f.Cell.Vault.InspectAbsenceScopeProvenanceAsync(f.Cell.Vault.DescribeNamespace()))).Code);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task V1_full_inventory_and_signed_capture_refuse_v2_before_keys_or_signer_access()
    {
        using var f = new Fixture(); await f.Abandon(f.Scope("one")); f.ClearKeys(); var before = f.Snapshot(); var signer = RSA.Create(2048); signer.Dispose();
        Assert.Equal("credential-recovery-inventory-scoped-format-unsupported", (await Assert.ThrowsAsync<CredentialException>(() => f.Vault.InspectRecoveryDependenciesAsync(f.Identity))).Code);
        Assert.Equal("credential-recovery-set-scoped-format-unsupported", (await Assert.ThrowsAsync<CredentialException>(() => f.Vault.CaptureSignedRecoverySetAsync(f.Identity, signer, "unused-signer", 1, f.Cell.Clock.Now, f.Cell.Clock.Now.AddHours(1)))).Code);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot());
    }

    static void AssertPublic(CredentialAbsenceScopeInspection report, Fixture f)
    {
        Assert.Equal(report.AuthenticatedAbsenceCount > 0, report.AbsenceScopeAuthenticationPerformed); Assert.False(report.PositiveEnvelopeAuthenticationPerformed); Assert.False(report.PositiveEnvelopeKeyDependenciesIncluded);
        Assert.False(report.CompleteRecoveryInventory); Assert.False(report.FullRecoverySupported); Assert.False(report.CustomerReviewAuthorized); Assert.False(report.ExternalExecutionAuthorized);
        Assert.False(report.KeyRetirementAuthorized); Assert.False(report.IndependentKeyBackupVerified); Assert.False(report.SourceHostFenced); Assert.False(report.ProductionRecoveryQualified); Assert.Equal("NotAssessed", report.IndependentBackupCoverage);
        var text = JsonSerializer.Serialize(report, new JsonSerializerOptions(JsonSerializerDefaults.Web)); Assert.DoesNotContain(f.Cell.Root, text); Assert.DoesNotContain(CredentialFixture.Token, text);
        Assert.DoesNotContain("profile/absence-", text); foreach (var name in new[] { "\"tag\":", "\"mac\":", "\"scope\":", "\"authProfileId\":", "\"requiredScopes\":" }) Assert.DoesNotContain(name, text, StringComparison.OrdinalIgnoreCase);
        Assert.Matches("^[a-f0-9]{64}$", report.Sha256);
    }
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    sealed class CancelOnObservationClock(DateTimeOffset now, Action cancel) : TimeProvider
    {
        bool armed; public bool Observed { get; private set; }
        public void Arm() => armed = true;
        public override DateTimeOffset GetUtcNow() { if (armed) { Observed = true; cancel(); } return now; }
    }
    sealed class PausedKeys(ICredentialKeyProvider inner, bool cooperative = true) : ICredentialKeyProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously); public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken token)
        { Entered.TrySetResult(); if (cooperative) await Release.Task.WaitAsync(token); else await Release.Task; return await inner.GetKeyAsync(customer, cell, key, cooperative ? token : CancellationToken.None); }
    }
    sealed class FailedKeys(string path, bool canceled) : ICredentialKeyProvider
    { public ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken token) => canceled ? throw new OperationCanceledException("Synthetic unrelated private provider " + path) : throw new IOException("Synthetic private provider " + path + CredentialFixture.Token); }
    sealed class Fixture : IDisposable
    {
        public CredentialFixture Cell { get; } = new();
        public CredentialVaultOptions Options { get; }
        public CredentialVault Vault { get; }
        public CredentialVaultNamespaceIdentity Identity { get; }
        public string AuthorityPath => Options.RollbackProtection!.AuthorityDirectory;
        public Fixture()
        {
            Cell.Clock.Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z"); Options = Cell.Options with { RootPath = Path.Combine(Cell.Root, "scoped-source-vault"),
                RollbackProtection = new(Path.Combine(Cell.Root, "scoped-source-authority")) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 } };
            Vault = CredentialVault.ProvisionStrongScoped(Options, Cell.Keys, Cell.Clock); Identity = Vault.DescribeNamespace();
        }
        public CredentialScope Scope(string suffix, string project = "default", string environment = "dev") => Cell.Scope with { AuthProfileId = "profile/absence-" + suffix, ProjectId = project, EnvironmentId = environment };
        public CredentialVault Open(string keyId = "key-1", ICredentialKeyProvider? keys = null) => new(Options with { EncryptionKeyId = keyId }, keys ?? Cell.Keys, Cell.Clock);
        public async Task Put(CredentialScope? scope = null, CredentialVault? vault = null) { using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token); await (vault ?? Vault).PutAsync(scope ?? Cell.Scope, secret, Cell.Clock.Now.AddHours(1)); }
        public async Task LeavePending(CredentialScope scope, CredentialVault? vault = null)
        {
            vault ??= Vault; var fault = typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.Instance | BindingFlags.NonPublic)!;
            fault.SetValue(vault, (Action<string>)(phase => { if (phase == "PendingPersisted") throw new IOException("Synthetic pending interruption."); }));
            try { await Assert.ThrowsAsync<IOException>(() => Put(scope, vault)); } finally { fault.SetValue(vault, null); }
        }
        public async Task Abandon(CredentialScope scope, CredentialVault? vault = null)
        {
            vault ??= Vault; await LeavePending(scope, vault); var pending = await vault.InspectRollbackStateAsync(scope.AuthProfileId);
            await vault.ReconcilePendingWriteAsync(scope.AuthProfileId, pending.AuthorityRevision, pending.AuthorityStateSha256, pending.Pending!.PendingSha256, CredentialRollbackResolution.AbandonUnpublished);
        }
        public void ClearKeys() => Cell.Keys.Requests.Clear();
        public void PrivateFile(string path, byte[] bytes)
        {
            using var current = WindowsIdentity.GetCurrent(); var acl = new FileSecurity(); acl.SetOwner(current.User!); acl.SetAccessRuleProtection(true, false); acl.AddAccessRule(new FileSystemAccessRule(current.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            using var file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.Read, 4096, FileOptions.WriteThrough, acl); file.Write(bytes); file.Flush(true);
        }
        public string[] Snapshot() => Directory.GetFileSystemEntries(Cell.Root, "*", SearchOption.AllDirectories).Prepend(Cell.Root).Order(StringComparer.Ordinal)
            .Select(p => Path.GetRelativePath(Cell.Root, p) + ":" + (Directory.Exists(p) ? "directory" : new FileInfo(p).Length == 0 ? Hash([]) : Hash(File.ReadAllBytes(p))) + ":" +
                (Directory.Exists(p) ? (FileSystemSecurity)new DirectoryInfo(p).GetAccessControl() : new FileInfo(p).GetAccessControl()).GetSecurityDescriptorSddlForm(AccessControlSections.Owner | AccessControlSections.Access)).ToArray();
        public void Dispose() => Cell.Dispose();
    }
}
