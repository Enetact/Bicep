using System.Diagnostics;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialRecoveryInventoryBoundaryTests
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData("customer")]
    [InlineData("cell")]
    [InlineData("namespace")]
    [InlineData("commitment")]
    [InlineData("authority")]
    [InlineData("authority-binding")]
    public async Task Wrong_expected_namespace_is_refused_before_any_key_read_and_preserves_every_file(string field)
    {
        using var f = new InventoryFixture(true); await f.Store(); var before = f.Snapshot();
        var expected = field switch
        {
            "customer" => f.Expected with { CustomerId = "10000000-0000-0000-0000-000000000099" },
            "cell" => f.Expected with { CellId = "20000000-0000-0000-0000-000000000099" },
            "namespace" => f.Expected with { VaultNamespaceId = Guid.NewGuid().ToString("N") },
            "commitment" => f.Expected with { NamespaceCommitmentSha256 = new string('a', 64) },
            "authority" => f.Expected with { AuthorityNamespaceId = Guid.NewGuid().ToString("N") },
            _ => f.Expected with { AuthorityBindingSha256 = new string('b', 64) }
        };
        f.Cell.Keys.Requests.Clear();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Vault.InspectRecoveryDependenciesAsync(expected));
        Assert.Equal("credential-recovery-inventory-namespace-mismatch", error.Code);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot()); AssertOpaque(error, f);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public async Task Expired_and_revoked_material_is_authenticated_with_source_keys_without_looking_up_configured_write_key(bool strong, bool revoked)
    {
        using var f = new InventoryFixture(strong); await f.Store();
        if (revoked) await f.Source.RevokeAsync(f.Cell.Scope.AuthProfileId, 1); else f.Cell.Clock.Now = f.Cell.Clock.Now.AddHours(2);
        var before = f.Snapshot(); f.Cell.Keys.Requests.Clear(); var result = await f.Vault.InspectRecoveryDependenciesAsync(f.Expected);
        Assert.Equal(["key-1"], result.CurrentRequiredKeyIds); var profile = Assert.Single(result.ProfileDependencies);
        Assert.Equal(revoked ? 2 : 1, profile.Generation); Assert.Equal(!revoked, profile.Enabled);
        Assert.Equal("key-1", profile.SourceKeyId); Assert.All(f.Cell.Keys.Requests, key => Assert.Equal("key-1", key.KeyId));
        Assert.Equal(before, f.Snapshot()); AssertReportSafe(result, f);
    }

    [Theory]
    [InlineData("PendingPersisted")]
    [InlineData("EnvelopePublished")]
    public async Task Any_strong_pending_decision_blocks_the_entire_inventory_before_keys_without_reconciling_it(string phase)
    {
        using var f = new InventoryFixture(true); await f.Store(); await f.Store(f.Cell.Scope with { AuthProfileId = "profile/another" });
        typeof(CredentialVault).GetProperty("RollbackFault", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .SetValue(f.Source, (Action<string>)(p => { if (p == phase) throw new IOException("synthetic pending state"); }));
        await Assert.ThrowsAsync<IOException>(() => f.Source.RevokeAsync("profile/another", 1));
        var before = f.Snapshot(); f.Cell.Keys.Requests.Clear();
        await Assert.ThrowsAsync<CredentialException>(() => f.Vault.InspectRecoveryDependenciesAsync(f.Expected));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot());
        Assert.NotNull((await f.Source.InspectRollbackStateAsync("profile/another")).Pending);
    }

    [WindowsCredentialFact]
    public async Task Source_lookup_holds_both_guards_and_a_real_cooperating_mutation_occurs_only_after_inventory_completion()
    {
        using var f = new InventoryFixture(true); await f.Store(); var paused = new PausedKeys(f.Cell.Keys); var vault = f.WithKeys(paused);
        var before = f.Snapshot(); var scanning = vault.InspectRecoveryDependenciesAsync(f.Expected);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var mutation = f.Source.RevokeAsync(f.Cell.Scope.AuthProfileId, 1);
        try
        {
            await Task.Delay(70); Assert.False(scanning.IsCompleted); Assert.False(mutation.IsCompleted); Assert.Equal(before, f.Snapshot());
            Assert.Throws<IOException>(() => new FileStream(f.AuthorityGuard!, FileMode.Open, FileAccess.Read, FileShare.Read));
        }
        finally { paused.Release.TrySetResult(); }
        var report = await scanning; Assert.Equal(1, Assert.Single(report.ProfileDependencies).Generation);
        Assert.False((await mutation).Enabled); Assert.Equal(2, (await f.Source.GetProfileAsync(f.Cell.Scope.AuthProfileId)).Generation);
        AssertReportSafe(report, f);
    }

    [WindowsCredentialFact]
    public async Task Inventory_does_not_open_a_database_or_wait_on_an_owned_exclusive_sqlite_transaction()
    {
        using var f = new InventoryFixture(true); await f.Store();
        var connectionString = new SqliteConnectionStringBuilder { DataSource = Path.Combine(f.Cell.Root, "cell.db"), Pooling = false }.ToString();
        using (var settings = new SqliteConnection(connectionString))
        {
            settings.Open(); using var journalMode = settings.CreateCommand(); journalMode.CommandText = "PRAGMA journal_mode=DELETE"; journalMode.ExecuteScalar();
        }
        var before = f.Snapshot();
        using (var db = new SqliteConnection(connectionString))
        {
            db.Open(); using var command = db.CreateCommand(); command.CommandText = "BEGIN EXCLUSIVE"; command.ExecuteNonQuery();
            try
            {
                var report = await f.Vault.InspectRecoveryDependenciesAsync(f.Expected).WaitAsync(TimeSpan.FromSeconds(4));
                Assert.Single(report.ProfileDependencies);
            }
            finally { command.CommandText = "ROLLBACK"; command.ExecuteNonQuery(); }
        }
        Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Caller_cancellation_of_a_cooperative_source_lookup_releases_custody_without_returning_a_partial_report()
    {
        using var f = new InventoryFixture(true); await f.Store(); var paused = new PausedKeys(f.Cell.Keys); var vault = f.WithKeys(paused);
        var before = f.Snapshot(); using var cancel = new CancellationTokenSource(); var scanning = vault.InspectRecoveryDependenciesAsync(f.Expected, cancel.Token);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanning); paused.Release.TrySetResult();
        Assert.Equal(before, f.Snapshot()); Assert.Single((await f.Vault.InspectRecoveryDependenciesAsync(f.Expected)).ProfileDependencies);
    }

    [WindowsCredentialFact]
    public async Task Default_deadline_is_cooperative_and_cannot_report_success_for_a_stalled_provider()
    {
        using var f = new InventoryFixture(true); await f.Store(); var paused = new PausedKeys(f.Cell.Keys); var before = f.Snapshot(); var watch = Stopwatch.StartNew();
        var scanning = f.WithKeys(paused).InspectRecoveryDependenciesAsync(f.Expected);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try
        {
            var error = await Assert.ThrowsAsync<CredentialException>(() => scanning.WaitAsync(TimeSpan.FromSeconds(28)));
            Assert.Equal("credential-recovery-inventory-timeout", error.Code);
        }
        finally { paused.Release.TrySetResult(); }
        Assert.InRange(watch.Elapsed, TimeSpan.FromSeconds(15), TimeSpan.FromSeconds(28)); Assert.Equal(before, f.Snapshot());
        Assert.Single((await f.Vault.InspectRecoveryDependenciesAsync(f.Expected)).ProfileDependencies);
    }

    [WindowsCredentialFact]
    public async Task Provider_ignoring_cancellation_keeps_custody_until_actual_completion_and_cannot_return_a_successful_report()
    {
        using var f = new InventoryFixture(true); await f.Store(); var paused = new PausedKeys(f.Cell.Keys, cooperative: false); var vault = f.WithKeys(paused); var before = f.Snapshot();
        using var cancel = new CancellationTokenSource(); var scanning = vault.InspectRecoveryDependenciesAsync(f.Expected, cancel.Token);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); cancel.Cancel(); await Task.Delay(70); Assert.False(scanning.IsCompleted);
        using var mutationCancel = new CancellationTokenSource(); var mutation = f.Source.RevokeAsync(f.Cell.Scope.AuthProfileId, 1, mutationCancel.Token);
        try { await Task.Delay(70); Assert.False(mutation.IsCompleted); Assert.Equal(before, f.Snapshot()); mutationCancel.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => mutation); }
        finally { paused.Release.TrySetResult(); }
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => scanning); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData(false, "unknown-file")]
    [InlineData(false, "unknown-directory")]
    [InlineData(false, "partial-write")]
    [InlineData(true, "unknown-file")]
    [InlineData(true, "unknown-directory")]
    [InlineData(true, "partial-write")]
    [InlineData(true, "authority-partial-write")]
    [InlineData(true, "authority-unknown-directory")]
    public async Task Unknown_or_partial_inventory_members_refuse_the_whole_report_before_any_source_key_read(bool strong, string attack)
    {
        using var f = new InventoryFixture(strong); await f.Store();
        var path = attack switch
        {
            "partial-write" => Path.Combine(f.Options.RootPath, ".write-" + Guid.NewGuid().ToString("N")),
            "authority-partial-write" => Path.Combine(f.Options.RollbackProtection!.AuthorityDirectory, ".state-write-" + Guid.NewGuid().ToString("N")),
            "authority-unknown-directory" => Path.Combine(f.Options.RollbackProtection!.AuthorityDirectory, "unexpected-directory"),
            _ => Path.Combine(f.Options.RootPath, attack)
        };
        if (attack.EndsWith("directory", StringComparison.Ordinal)) Directory.CreateDirectory(path); else f.PrivateFile(path, "synthetic retained partial bytes");
        var before = f.Snapshot(); f.Cell.Keys.Requests.Clear();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Vault.InspectRecoveryDependenciesAsync(f.Expected));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot()); AssertOpaque(error, f);
    }

    [Theory]
    [InlineData("root")]
    [InlineData("profile")]
    [InlineData("marker")]
    [InlineData("guard")]
    [InlineData("authority-root")]
    [InlineData("authority-state")]
    public async Task Widened_custody_acls_fail_without_repair_or_partial_inventory(string entry)
    {
        using var f = new InventoryFixture(true); await f.Store();
        var path = entry switch
        {
            "root" => f.Options.RootPath, "profile" => f.Profile(), "marker" => Path.Combine(f.Options.RootPath, ".credential-namespace.json"),
            "guard" => Path.Combine(f.Options.RootPath, ".vault.lock"), "authority-root" => f.Options.RollbackProtection!.AuthorityDirectory,
            _ => Path.Combine(f.Options.RollbackProtection!.AuthorityDirectory, "state.json")
        };
        var acl = Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        acl.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute, AccessControlType.Allow));
        if (acl is DirectorySecurity directory) new DirectoryInfo(path).SetAccessControl(directory); else new FileInfo(path).SetAccessControl((FileSecurity)acl);
        var before = f.Snapshot(); f.Cell.Keys.Requests.Clear(); await Assert.ThrowsAsync<CredentialException>(() => f.Vault.InspectRecoveryDependenciesAsync(f.Expected));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("ciphertext")]
    [InlineData("metadata")]
    public async Task A_bad_profile_refuses_the_entire_inventory_without_returning_other_valid_dependencies(string attack)
    {
        using var f = new InventoryFixture(); await f.Store(); var other = f.Cell.Scope with { AuthProfileId = "profile/second-authenticated-before-tamper" }; await f.Store(other);
        var file = f.Profile(other.AuthProfileId); var json = JsonNode.Parse(File.ReadAllText(file))!;
        if (attack == "metadata") json["profile"]!["enabled"] = false; else json["tag"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16));
        File.WriteAllText(file, json.ToJsonString()); var before = f.Snapshot();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Vault.InspectRecoveryDependenciesAsync(f.Expected)); Assert.Equal(before, f.Snapshot()); AssertOpaque(error, f);
    }

    [Theory]
    [InlineData("profile")]
    [InlineData("membership")]
    [InlineData("authority-state")]
    public async Task Changes_while_source_lookup_is_awaiting_are_revalidated_before_any_inventory_is_returned(string changed)
    {
        using var f = new InventoryFixture(true); await f.Store(); var paused = new PausedKeys(f.Cell.Keys); var scanning = f.WithKeys(paused).InspectRecoveryDependenciesAsync(f.Expected);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        if (changed == "profile") { var file = f.Profile(); var json = JsonNode.Parse(File.ReadAllText(file))!; json["tag"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)); File.WriteAllText(file, json.ToJsonString()); }
        else if (changed == "membership") f.PrivateFile(Path.Combine(f.Options.RootPath, "unexpected-after-await.txt"), "synthetic concurrent owner entry");
        else f.ChangeState(state => state["revision"] = state["revision"]!.GetValue<long>() + 1);
        var before = f.Snapshot(); paused.Release.TrySetResult();
        await Assert.ThrowsAsync<CredentialException>(() => scanning); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task More_than_256_real_encrypted_profiles_is_refused_before_source_keys_and_without_a_partial_result()
    {
        using var f = new InventoryFixture();
        for (var i = 0; i < 257; i++) await f.Store(f.Cell.Scope with { AuthProfileId = "profile/bounded-capacity-" + i });
        var before = f.Snapshot(); f.Cell.Keys.Requests.Clear(); await Assert.ThrowsAsync<CredentialException>(() => f.Vault.InspectRecoveryDependenciesAsync(f.Expected));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Authenticated_profile_counter_outside_safe_range_is_refused_without_modifying_its_valid_ciphertext()
    {
        using var f = new InventoryFixture(); await f.Store(); var profile = await f.Source.GetProfileAsync(f.Cell.Scope.AuthProfileId);
        f.AuthenticatedEnvelope(profile with { Generation = 9007199254740992 });
        Assert.Equal(9007199254740992, (await f.Source.GetProfileAsync(f.Cell.Scope.AuthProfileId)).Generation);
        var before = f.Snapshot(); await Assert.ThrowsAsync<CredentialException>(() => f.Vault.InspectRecoveryDependenciesAsync(f.Expected)); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData("revision")]
    [InlineData("profiles")]
    public async Task Strong_state_out_of_bound_counters_or_profile_set_with_valid_checksum_fail_before_keys(string attack)
    {
        using var f = new InventoryFixture(true); await f.Store();
        f.ChangeState(state =>
        {
            if (attack == "revision") state["revision"] = 9007199254740992;
            else for (var i = 0; i < 256; i++) state["profiles"]![Hash(Encoding.UTF8.GetBytes("profile/absent-" + i))] = new JsonObject
                { ["generation"] = 0, ["envelopeSha256"] = "", ["envelopeLength"] = 0, ["pending"] = null };
        });
        var before = f.Snapshot(); f.Cell.Keys.Requests.Clear(); await Assert.ThrowsAsync<CredentialException>(() => f.Vault.InspectRecoveryDependenciesAsync(f.Expected));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Junction_member_cannot_expand_the_inventory_or_change_the_owned_target()
    {
        using var f = new InventoryFixture(); await f.Store(); var target = Path.Combine(f.Cell.Root, "owned-junction-target"); Directory.CreateDirectory(target);
        f.PrivateFile(Path.Combine(target, "preserved.txt"), "synthetic target bytes"); var link = Path.Combine(f.Options.RootPath, "unknown-junction"); await Junction(link, target, f.Cell.Root);
        try
        {
            var before = f.Snapshot(); f.Cell.Keys.Requests.Clear(); await Assert.ThrowsAsync<CredentialException>(() => f.Vault.InspectRecoveryDependenciesAsync(f.Expected));
            Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot());
        }
        finally { Directory.Delete(link, false); }
    }

    static void AssertReportSafe(CredentialRecoveryInventory report, InventoryFixture f)
    {
        var json = JsonSerializer.Serialize(report); Assert.DoesNotContain(CredentialFixture.Token, json); Assert.DoesNotContain(f.Cell.Root, json);
        Assert.DoesNotContain(f.Cell.Scope.AuthProfileId, json); Assert.DoesNotContain(f.Cell.Scope.Audience, json); Assert.DoesNotContain("actions:write", json);
        foreach (var field in new[] { "ciphertext", "nonce", "profileScopeJson", "requiredScopes" }) Assert.DoesNotContain(field, json, StringComparison.OrdinalIgnoreCase);
        Assert.False(report.KeyRetirementAuthorized); Assert.Equal("NotAssessed", report.IndependentBackupCoverage);
    }
    static void AssertOpaque(Exception error, InventoryFixture f) { Assert.DoesNotContain(CredentialFixture.Token, error.ToString()); Assert.DoesNotContain(f.Cell.Root, error.ToString()); }
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    sealed class PausedKeys(ICredentialKeyProvider inner, bool cooperative = true) : ICredentialKeyProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken cancellationToken)
        { Entered.TrySetResult(); if (cooperative) await Release.Task.WaitAsync(cancellationToken); else await Release.Task; return await inner.GetKeyAsync(customer, cell, key, cooperative ? cancellationToken : CancellationToken.None); }
    }
    static async Task Junction(string link, string target, string root)
    {
        var info = new ProcessStartInfo(Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32", "cmd.exe"))
        { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden, WorkingDirectory = root, RedirectStandardOutput = true, RedirectStandardError = true };
        info.Environment.Clear(); foreach (var name in new[] { "SystemRoot", "WINDIR" }) if (Environment.GetEnvironmentVariable(name) is { } value) info.Environment[name] = value;
        foreach (var arg in new[] { "/d", "/c", "mklink", "/J", link, target }) info.ArgumentList.Add(arg);
        using var process = Process.Start(info)!; _ = process.SafeHandle; var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(10));
        try { await process.WaitForExitAsync(deadline.Token); Assert.Equal(0, process.ExitCode); _ = await output; _ = await errors; }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
    }

    sealed class InventoryFixture : IDisposable
    {
        public CredentialFixture Cell { get; } = new();
        public CredentialVaultOptions Options { get; }
        public CredentialVault Source { get; }
        public CredentialVault Vault { get; }
        public CredentialVaultNamespaceIdentity Expected { get; }
        public string? AuthorityGuard => Options.RollbackProtection is null ? null : Path.Combine(Options.RollbackProtection.AuthorityDirectory, ".rollback.lock");
        public InventoryFixture(bool strong = false)
        {
            Options = strong ? Cell.Options with { RootPath = Path.Combine(Cell.Root, "inventory-vault"), RollbackProtection = new(Path.Combine(Cell.Root, "inventory-authority")) } : Cell.Options;
            Source = strong ? CredentialVault.ProvisionStrong(Options, Cell.Keys, Cell.Clock) : Cell.Vault;
            Vault = WithKeys(Cell.Keys); Expected = Vault.DescribeNamespace();
        }
        public CredentialVault WithKeys(ICredentialKeyProvider keys) => new(Options with { EncryptionKeyId = "unavailable-configured-write-key" }, keys, Cell.Clock);
        public async Task Store(CredentialScope? scope = null)
        { using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token); await Source.PutAsync(scope ?? Cell.Scope, secret, Cell.Clock.Now.AddHours(1)); }
        public string Profile(string? id = null) => Path.Combine(Options.RootPath, Hash(Encoding.UTF8.GetBytes(id ?? Cell.Scope.AuthProfileId)) + ".credential.json");
        public void PrivateFile(string path, string text)
        {
            using var identity = WindowsIdentity.GetCurrent(); var acl = new FileSecurity(); acl.SetOwner(identity.User!); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            using var file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.Read, 4096, FileOptions.WriteThrough, acl);
            file.Write(Encoding.UTF8.GetBytes(text)); file.Flush(true);
        }
        public string[] Snapshot()
        {
            var entries = new List<string>(); Walk(Cell.Root, entries); return entries.Order(StringComparer.Ordinal).ToArray();
        }
        void Walk(string directory, List<string> entries)
        {
            foreach (var path in new[] { directory }.Concat(Directory.EnumerateFileSystemEntries(directory)))
            {
                var relative = Path.GetRelativePath(Cell.Root, path); var attributes = File.GetAttributes(path);
                if ((attributes & FileAttributes.ReparsePoint) != 0)
                { entries.Add(relative + ":link:" + new DirectoryInfo(path).LinkTarget); continue; }
                FileSystemSecurity acl = Directory.Exists(path) ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
                var security = Hash(acl.GetSecurityDescriptorBinaryForm());
                if (Directory.Exists(path)) { entries.Add(relative + ":directory:" + security); if (path != directory) Walk(path, entries); }
                else
                {
                    var length = new FileInfo(path).Length;
                    var bytes = (Path.GetFileName(path) is ".vault.lock" or ".rollback.lock") && length == 0 ? Array.Empty<byte>() : File.ReadAllBytes(path);
                    entries.Add(relative + ":file:" + security + ":" + length + ":" + Hash(bytes));
                }
            }
        }
        public void AuthenticatedEnvelope(CredentialProfile profile)
        {
            var nonce = RandomNumberGenerator.GetBytes(12); var clear = Encoding.UTF8.GetBytes(CredentialFixture.Token); var cipher = new byte[clear.Length]; var tag = new byte[16];
            try
            {
                using var aes = new AesGcm(Cell.Keys.Values["key-1"], 16);
                aes.Encrypt(nonce, clear, cipher, tag, JsonSerializer.SerializeToUtf8Bytes(new { schema = 1, keyId = "key-1", profile }, Web));
                File.WriteAllBytes(Profile(profile.Scope.AuthProfileId), JsonSerializer.SerializeToUtf8Bytes(new
                { schema = 1, keyId = "key-1", profile, nonce = Convert.ToBase64String(nonce), ciphertext = Convert.ToBase64String(cipher), tag = Convert.ToBase64String(tag) }, Web));
            }
            finally { CryptographicOperations.ZeroMemory(clear); }
        }
        public void ChangeState(Action<JsonObject> mutate)
        {
            var file = Path.Combine(Options.RollbackProtection!.AuthorityDirectory, "state.json"); var state = JsonNode.Parse(File.ReadAllText(file))!.AsObject(); mutate(state);
            var binding = JsonNode.Parse(File.ReadAllText(Path.Combine(Options.RootPath, "rollback-binding.json")))!;
            var profiles = state["profiles"]!.AsObject().OrderBy(p => p.Key, StringComparer.Ordinal)
                .Select(p => new { id = p.Key, entry = p.Value!.Deserialize<StateEntry>(Web) }).ToArray();
            state["sha256"] = Hash(JsonSerializer.SerializeToUtf8Bytes(new
            {
                customerId = binding["customerId"]!.GetValue<string>(), cellId = binding["cellId"]!.GetValue<string>(),
                vaultNamespaceId = binding["vaultNamespaceId"]!.GetValue<string>(), authorityNamespaceId = binding["authorityNamespaceId"]!.GetValue<string>(),
                schema = state["schema"]!.GetValue<int>(), revision = state["revision"]!.GetValue<long>(), profiles
            }, Web));
            File.WriteAllText(file, state.ToJsonString());
        }
        sealed record StateEntry(long Generation, string EnvelopeSha256, long EnvelopeLength, CredentialRollbackPending? Pending);
        public void Dispose() => Cell.Dispose();
    }
}
