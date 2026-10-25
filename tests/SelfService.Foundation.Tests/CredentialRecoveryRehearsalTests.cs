using System.Collections;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialRecoveryRehearsalTests
{
    static readonly JsonSerializerOptions Web = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(false, PipelineRequestFactory.GitHub)] [InlineData(true, PipelineRequestFactory.GitHub)]
    [InlineData(false, PipelineRequestFactory.AzureDevOps)] [InlineData(true, PipelineRequestFactory.AzureDevOps)]
    public async Task Actual_retained_ciphertext_authenticates_after_current_vault_and_authority_paths_are_removed(bool strong, string provider)
    {
        using var f = new RehearsalFixture(strong, provider); await f.Store(); using var set = await f.Capture(); var pin = CredentialRecoverySets.Authority(set);
        Directory.Move(f.Options.RootPath, Path.Combine(f.Cell.Root, "moved-current-vault"));
        if (strong) Directory.Move(f.Options.RollbackProtection!.AuthorityDirectory, Path.Combine(f.Cell.Root, "moved-current-authority"));
        var before = f.Snapshot(); f.Cell.Keys.Requests.Clear(); using var key = f.PublicKey();
        var result = await f.Rehearsal().RehearseAsync(set, pin, key);
        Assert.Equal(set.Sha256, result.SignedSetSha256); Assert.Equal(1, result.AuthenticatedProfileCount);
        Assert.Equal("key-1", Assert.Single(result.ProfileDependencies).SourceKeyId); Assert.Equal(before, f.Snapshot());
        Assert.Single(f.Cell.Keys.Requests); AssertMetadataOnly(result, f);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Expired_revoked_and_mixed_source_key_envelopes_are_authenticated_without_any_configured_write_key(bool strong)
    {
        using var f = new RehearsalFixture(strong); await f.Store(); var other = f.Cell.Scope with { AuthProfileId = "profile/rehearsal-revoked" }; await f.Store(other);
        f.Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32);
        await new CredentialVault(f.Options with { EncryptionKeyId = "key-2" }, f.Cell.Keys, f.Cell.Clock).RewrapAsync(f.Cell.Scope.AuthProfileId, 1, "key-1");
        await f.Source.RevokeAsync(other.AuthProfileId, 1); f.Cell.Clock.Now = f.Cell.Clock.Now.AddHours(2);
        using var set = await f.Capture(); var pin = CredentialRecoverySets.Authority(set); var before = f.Snapshot(); f.Cell.Keys.Requests.Clear();
        using var key = f.PublicKey(); var result = await f.Rehearsal().RehearseAsync(set, pin, key);
        Assert.Equal(2, result.AuthenticatedProfileCount); Assert.Equal(new[] { "key-1", "key-2" }, result.ProfileDependencies.Select(p => p.SourceKeyId).Order(StringComparer.Ordinal));
        Assert.All(result.ProfileDependencies, p => { Assert.Equal(2, p.Generation); Assert.True(p.ExpiresUnixMilliseconds < result.CompletedUtc.ToUnixTimeMilliseconds()); });
        Assert.Single(result.ProfileDependencies, p => !p.Enabled); Assert.Equal(2, f.Cell.Keys.Requests.Count);
        Assert.All(f.Cell.Keys.Requests, request => { Assert.Equal(f.Cell.Scope.CustomerId, request.Customer); Assert.Equal(f.Cell.Scope.CellId, request.Cell); Assert.Contains(request.KeyId, new[] { "key-1", "key-2" }); });
        Assert.Equal(before, f.Snapshot()); AssertMetadataOnly(result, f);
    }

    [WindowsCredentialFact]
    public async Task Fresh_native_DPAPI_source_provider_can_authenticate_retained_bytes_without_exporting_or_changing_key_custody()
    {
        using var f = new RehearsalFixture(); var keys = f.Cell.ProvisionNativeKeys("rehearsal-synthetic-keys");
        using (await keys.ProvisionKeyAsync(f.Cell.Scope.CustomerId, f.Cell.Scope.CellId, "native-source")) { }
        var options = f.Options with { RootPath = Path.Combine(f.Cell.Root, "native-rehearsal-vault"), EncryptionKeyId = "native-source" };
        var vault = CredentialVault.Provision(options, keys, f.Cell.Clock); using (var secret = CredentialSecret.FromUtf8(CredentialFixture.Token))
            await vault.PutAsync(f.Cell.Scope, secret, f.Cell.Clock.Now.AddHours(1));
        var identity = vault.DescribeNamespace(); using var set = await vault.CaptureSignedRecoverySetAsync(identity, f.Signer, "set-signer", 1, f.Cell.Clock.Now, f.Cell.Clock.Now.AddDays(1));
        Directory.Move(options.RootPath, Path.Combine(f.Cell.Root, "moved-native-vault")); var before = f.Snapshot(); using var key = f.PublicKey();
        var result = await new CredentialRecoveryRehearsal(identity, keys, f.Cell.Clock).RehearseAsync(set, CredentialRecoverySets.Authority(set), key);
        Assert.Equal("native-source", Assert.Single(result.ProfileDependencies).SourceKeyId); Assert.Equal(before, f.Snapshot()); AssertMetadataOnly(result, f);
    }

    [Theory]
    [InlineData("customer")] [InlineData("cell")] [InlineData("namespace")] [InlineData("set-digest")] [InlineData("signer")]
    public async Task Wrong_configured_scope_exact_artifact_pin_or_public_signer_is_refused_before_any_source_key_lookup(string attack)
    {
        using var f = new RehearsalFixture(); await f.Store(); using var set = await f.Capture(); var identity = f.Expected; var pin = CredentialRecoverySets.Authority(set);
        if (attack == "customer") identity = identity with { CustomerId = "10000000-0000-0000-0000-000000000099" };
        else if (attack == "cell") identity = identity with { CellId = "20000000-0000-0000-0000-000000000099" };
        else if (attack == "namespace") identity = identity with { VaultNamespaceId = Guid.NewGuid().ToString("N") };
        else if (attack == "set-digest") pin = pin with { ArtifactSha256 = new string('b', 64) };
        using var key = attack == "signer" ? RSA.Create(2048) : f.PublicKey(); var before = f.Snapshot(); f.Cell.Keys.Requests.Clear();
        var error = await Assert.ThrowsAsync<CredentialException>(() => new CredentialRecoveryRehearsal(identity, f.Cell.Keys, f.Cell.Clock).RehearseAsync(set, pin, key));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.Snapshot()); AssertOpaque(error, f);
    }

    [Theory]
    [InlineData("missing")] [InlineData("wrong-bytes")] [InlineData("exception")] [InlineData("credential-exception")] [InlineData("unrelated-cancellation")]
    public async Task Missing_wrong_or_failing_source_keys_refuse_the_whole_rehearsal_without_secret_or_path_leakage(string attack)
    {
        using var f = new RehearsalFixture(); await f.Store(); using var set = await f.Capture(); var pin = CredentialRecoverySets.Authority(set);
        if (attack == "missing") f.Cell.Keys.Values.Remove("key-1");
        else if (attack == "wrong-bytes") f.Cell.Keys.Values["key-1"] = RandomNumberGenerator.GetBytes(32);
        else f.Cell.Keys.OnGet = () => throw attack switch
        {
            "credential-exception" => new CredentialException(CredentialFixture.Token),
            "unrelated-cancellation" => new OperationCanceledException(CredentialFixture.Token + f.Cell.Root),
            _ => new InvalidOperationException(CredentialFixture.Token + f.Cell.Root)
        };
        var before = f.Snapshot(); using var key = f.PublicKey();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Rehearsal().RehearseAsync(set, pin, key));
        AssertOpaque(error, f); Assert.Equal(before, f.Snapshot()); Assert.DoesNotContain(CredentialFixture.Token, error.Code); Assert.DoesNotContain(f.Cell.Root, error.Code);
    }

    [WindowsCredentialFact]
    public async Task Signed_and_reviewed_bad_AES_tag_in_a_later_entry_never_returns_earlier_authenticated_profiles()
    {
        using var f = new RehearsalFixture(); await f.Store(); await f.Store(f.Cell.Scope with { AuthProfileId = "profile/second-rehearsal" });
        using var original = await f.Capture(); using var bad = ReSignWithBadLastTag(original, f); var pin = CredentialRecoverySets.Authority(bad);
        using var key = f.PublicKey(); _ = CredentialRecoverySets.Verify(bad, pin, key, f.Expected, f.Cell.Clock);
        var before = f.Snapshot(); f.Cell.Keys.Requests.Clear();
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Rehearsal().RehearseAsync(bad, pin, key));
        Assert.Equal("credential-envelope-integrity", error.Code); Assert.Equal(2, f.Cell.Keys.Requests.Count); Assert.Equal(before, f.Snapshot()); AssertOpaque(error, f);
    }

    [WindowsCredentialFact]
    public async Task Live_mutation_is_not_locked_or_consulted_and_original_set_disposal_during_lookup_cannot_change_private_retained_bytes()
    {
        using var f = new RehearsalFixture(true); await f.Store(); using var set = await f.Capture(); var pin = CredentialRecoverySets.Authority(set);
        var paused = new PausedKeys(f.Cell.Keys); using var key = f.PublicKey(); var rehearsal = f.Rehearsal(paused).RehearseAsync(set, pin, key);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); set.Dispose();
            var changed = await f.Source.RevokeAsync(f.Cell.Scope.AuthProfileId, 1).WaitAsync(TimeSpan.FromSeconds(4)); Assert.False(changed.Enabled);
            var before = f.Snapshot(); paused.Release.TrySetResult(); var result = await rehearsal;
            Assert.Equal(1, Assert.Single(result.ProfileDependencies).Generation); Assert.True(result.ProfileDependencies[0].Enabled);
            Assert.Equal(pin.ArtifactSha256, result.SignedSetSha256); Assert.Equal(before, f.Snapshot()); AssertMetadataOnly(result, f);
        }
        finally { paused.Release.TrySetResult(); }
    }

    [WindowsCredentialFact]
    public async Task Caller_cancellation_before_lookup_and_during_cooperative_lookup_never_produces_a_partial_result_or_write()
    {
        using var f = new RehearsalFixture(); await f.Store(); using var set = await f.Capture(); var pin = CredentialRecoverySets.Authority(set); var before = f.Snapshot();
        f.Cell.Keys.Requests.Clear(); using var key = f.PublicKey(); using var caller = new CancellationTokenSource(); caller.Cancel();
        var first = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Rehearsal().RehearseAsync(set, pin, key, caller.Token));
        Assert.Equal(caller.Token, first.CancellationToken); Assert.Empty(f.Cell.Keys.Requests);
        using var lateCaller = new CancellationTokenSource(); var paused = new PausedKeys(f.Cell.Keys);
        var operation = f.Rehearsal(paused).RehearseAsync(set, pin, key, lateCaller.Token); await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); lateCaller.Cancel();
        try { var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); Assert.Equal(lateCaller.Token, error.CancellationToken); AssertOpaque(error, f); }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(before, f.Snapshot()); Assert.Equal(1, (await f.Rehearsal().RehearseAsync(set, pin, key)).AuthenticatedProfileCount);
    }

    [WindowsCredentialFact]
    public async Task Cooperative_default_deadline_returns_an_opaque_timeout_and_does_not_detach_the_key_dependency()
    {
        using var f = new RehearsalFixture(); await f.Store(); using var set = await f.Capture(); var pin = CredentialRecoverySets.Authority(set); var before = f.Snapshot();
        var paused = new PausedKeys(f.Cell.Keys); using var key = f.PublicKey(); var operation = f.Rehearsal(paused).RehearseAsync(set, pin, key);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        try { var error = await Assert.ThrowsAsync<CredentialException>(() => operation.WaitAsync(TimeSpan.FromSeconds(30))); Assert.Equal("credential-recovery-rehearsal-timeout", error.Code); AssertOpaque(error, f); }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Uncooperative_key_provider_is_awaited_until_completion_even_after_caller_cancels_and_original_set_is_disposed()
    {
        using var f = new RehearsalFixture(); await f.Store(); using var set = await f.Capture(); var pin = CredentialRecoverySets.Authority(set); var before = f.Snapshot();
        var paused = new PausedKeys(f.Cell.Keys, cooperative: false); using var key = f.PublicKey(); using var caller = new CancellationTokenSource();
        var operation = f.Rehearsal(paused).RehearseAsync(set, pin, key, caller.Token); await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        caller.Cancel(); set.Dispose();
        try { await Task.Delay(100); Assert.False(operation.IsCompleted); Assert.Equal(before, f.Snapshot()); }
        finally { paused.Release.TrySetResult(); }
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => operation); Assert.Equal(caller.Token, error.CancellationToken); AssertOpaque(error, f); Assert.Equal(before, f.Snapshot());
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Signature_expiry_or_future_clock_after_source_key_await_is_rechecked_before_result(bool future)
    {
        using var f = new RehearsalFixture(); await f.Store(); using var set = await f.Capture(); var pin = CredentialRecoverySets.Authority(set);
        var paused = new PausedKeys(f.Cell.Keys); using var key = f.PublicKey(); var operation = f.Rehearsal(paused).RehearseAsync(set, pin, key);
        await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.Cell.Clock.Now = future ? set.Signature.IssuedUtc.AddMinutes(-2) : set.Signature.ExpiresUtc;
        var before = f.Snapshot(); paused.Release.TrySetResult(); var error = await Assert.ThrowsAsync<CredentialException>(() => operation);
        Assert.StartsWith("credential-recovery-set-", error.Code); AssertOpaque(error, f); Assert.Equal(before, f.Snapshot());
    }

    [WindowsCredentialFact]
    public async Task Empty_set_requires_no_encryption_key_and_public_result_is_immutable_deterministic_metadata_only()
    {
        using var f = new RehearsalFixture(); using var set = await f.Capture(); var pin = CredentialRecoverySets.Authority(set); f.Cell.Keys.Values.Clear(); f.Cell.Keys.Requests.Clear();
        using var key = f.PublicKey(); var first = await f.Rehearsal().RehearseAsync(set, pin, key); var second = await f.Rehearsal().RehearseAsync(set, pin, key);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(0, first.AuthenticatedProfileCount); Assert.False(first.TemporaryInMemoryDecryptionPerformed);
        Assert.Equal(first.Sha256, second.Sha256); Assert.Empty(typeof(CredentialRecoveryRehearsalResult).GetConstructors());
        Assert.All(typeof(CredentialRecoveryRehearsalResult).GetProperties(), property => Assert.Null(property.SetMethod));
        Assert.Throws<NotSupportedException>(() => ((IList)first.ProfileDependencies).Clear()); AssertMetadataOnly(first, f);
    }

    static void AssertMetadataOnly(CredentialRecoveryRehearsalResult result, RehearsalFixture f)
    {
        Assert.True(result.AllRetainedEnvelopesAuthenticated); Assert.False(result.CurrentVaultConsulted); Assert.False(result.PlaintextCredentialMaterialExported);
        Assert.False(result.EncryptionKeyMaterialExported); Assert.False(result.RetainedEnvelopeBytesExported); Assert.False(result.PlaintextPersisted);
        Assert.False(result.RestorePerformed); Assert.False(result.NamespaceAdopted); Assert.False(result.ExternalExecutionAuthorized); Assert.False(result.KeyRetirementAuthorized);
        Assert.False(result.IndependentKeyBackupVerified); Assert.False(result.ProductionRecoveryQualified); Assert.Equal("NotAssessed", result.IndependentBackupCoverage);
        var json = JsonSerializer.Serialize(result); foreach (var excluded in new[] { CredentialFixture.Token, f.Cell.Root, f.Cell.Scope.AuthProfileId, f.Cell.Scope.Audience,
            "actions:write", "ciphertext", "nonce", "requiredScopes", "profileScopeJson", "ownerSid" }) Assert.DoesNotContain(excluded, json, StringComparison.OrdinalIgnoreCase);
    }
    static void AssertOpaque(Exception error, RehearsalFixture f) { Assert.DoesNotContain(CredentialFixture.Token, error.ToString()); Assert.DoesNotContain(f.Cell.Root, error.ToString()); }
    static CredentialRecoverySet ReSignWithBadLastTag(CredentialRecoverySet source, RehearsalFixture f)
    {
        using var output = new MemoryStream(); source.WritePrivateArtifact(output); var artifact = JsonNode.Parse(output.ToArray())!;
        var body = JsonNode.Parse(Convert.FromBase64String(artifact["content"]!.GetValue<string>()))!;
        var files = body["files"]!.AsArray().ToDictionary(node => node!["name"]!.GetValue<string>(), node => Convert.FromBase64String(node!["bytes"]!.GetValue<string>()), StringComparer.Ordinal);
        var badName = files.Keys.Where(name => name.EndsWith(".credential.json", StringComparison.Ordinal)).Order(StringComparer.Ordinal).Last();
        var envelope = JsonNode.Parse(files[badName])!; envelope["tag"] = Convert.ToBase64String(RandomNumberGenerator.GetBytes(16)); files[badName] = JsonSerializer.SerializeToUtf8Bytes(envelope, Web);
        var verified = CredentialRecoverySets.Verify(source, CredentialRecoverySets.Authority(source), f.Signer, f.Expected, f.Cell.Clock); var inventory = verified.Inventory;
        var dependencies = inventory.ProfileDependencies.Select(profile => profile.ProfileIdSha256 == badName[6..70] ?
            (CredentialRecoveryProfileDependency)typeof(CredentialRecoveryProfileDependency).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single().Invoke([
                profile.ProfileIdSha256, profile.ScopeSha256, profile.ProjectId, profile.EnvironmentId, profile.Generation, profile.Enabled, profile.ExpiresUnixMilliseconds,
                profile.SourceKeyId, Convert.ToHexStringLower(SHA256.HashData(files[badName])), (long)files[badName].Length]) : profile).ToArray();
        var changed = (CredentialRecoveryInventory)typeof(CredentialRecoveryInventory).GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic).Single()
            .Invoke([f.Expected, inventory.ObservedUtc, dependencies, inventory.AuthorityRevision, inventory.AuthorityStateSha256]);
        return (CredentialRecoverySet)typeof(CredentialRecoverySets).GetMethod("Create", BindingFlags.Static | BindingFlags.NonPublic)!
            .Invoke(null, [changed, files, f.Signer, f.Signer, "set-signer", 1L, f.Cell.Clock.Now, f.Cell.Clock.Now, f.Cell.Clock.Now.AddDays(1)])!;
    }
    sealed class PausedKeys(ICredentialKeyProvider inner, bool cooperative = true) : ICredentialKeyProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken cancellationToken)
        { Entered.TrySetResult(); if (cooperative) await Release.Task.WaitAsync(cancellationToken); else await Release.Task; return await inner.GetKeyAsync(customerId, cellId, keyId, cooperative ? cancellationToken : CancellationToken.None); }
    }
    sealed class RehearsalFixture : IDisposable
    {
        public CredentialFixture Cell { get; }
        public RSA Signer { get; } = RSA.Create(2048);
        public CredentialVaultOptions Options { get; }
        public CredentialVault Source { get; }
        public CredentialVaultNamespaceIdentity Expected { get; }
        public RehearsalFixture(bool strong = false, string provider = PipelineRequestFactory.GitHub)
        {
            Cell = new(provider); Options = strong ? Cell.Options with { RootPath = Path.Combine(Cell.Root, "rehearsal-vault"), RollbackProtection = new(Path.Combine(Cell.Root, "rehearsal-authority")) } : Cell.Options;
            Source = strong ? CredentialVault.ProvisionStrong(Options, Cell.Keys, Cell.Clock) : Cell.Vault; Expected = Source.DescribeNamespace();
        }
        public async Task Store(CredentialScope? scope = null) { using var token = CredentialSecret.FromUtf8(CredentialFixture.Token); await Source.PutAsync(scope ?? Cell.Scope, token, Cell.Clock.Now.AddHours(1)); }
        public Task<CredentialRecoverySet> Capture() => Source.CaptureSignedRecoverySetAsync(Expected, Signer, "set-signer", 1, Cell.Clock.Now, Cell.Clock.Now.AddDays(1));
        public RSA PublicKey() { var key = RSA.Create(); key.ImportSubjectPublicKeyInfo(Signer.ExportSubjectPublicKeyInfo(), out _); return key; }
        public CredentialRecoveryRehearsal Rehearsal(ICredentialKeyProvider? keys = null) => new(Expected, keys ?? Cell.Keys, Cell.Clock);
        public string[] Snapshot()
        {
            var entries = new List<string>(); Walk(Cell.Root, entries); return entries.Order(StringComparer.Ordinal).ToArray();
        }
        void Walk(string path, List<string> entries)
        {
            var directory = Directory.Exists(path); FileSystemSecurity acl = directory ? new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
            var prefix = Path.GetRelativePath(Cell.Root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(acl.GetSecurityDescriptorBinaryForm()));
            if (directory) { entries.Add(prefix + ":directory"); foreach (var child in Directory.EnumerateFileSystemEntries(path)) Walk(child, entries); }
            else { var length = new FileInfo(path).Length; var bytes = (Path.GetFileName(path) is ".vault.lock" or ".rollback.lock" or ".keys.lock") && length == 0 ? [] : File.ReadAllBytes(path); entries.Add(prefix + ":" + length + ":" + Convert.ToHexStringLower(SHA256.HashData(bytes))); }
        }
        public void Dispose() { Signer.Dispose(); Cell.Dispose(); }
    }
}
