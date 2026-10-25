using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text.Json;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialRecoveryStageTests
{
    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);

    [Theory]
    [InlineData(PipelineRequestFactory.GitHub, false)] [InlineData(PipelineRequestFactory.GitHub, true)]
    [InlineData(PipelineRequestFactory.AzureDevOps, false)] [InlineData(PipelineRequestFactory.AzureDevOps, true)]
    public async Task Exact_reviewed_stage_preserves_original_profiles_and_rebuilds_fresh_inactive_high_water(string provider, bool strong)
    {
        using var f = new StageFixture(provider, strong); var expiry = f.Cell.Clock.Now.AddHours(1).AddTicks(1234).ToOffset(TimeSpan.FromHours(5.5));
        await f.Put(f.Cell.Scope, expiry); var sibling = f.Cell.Scope with { AuthProfileId = "profile/recovery-sibling", ProjectId = "sibling-project" };
        await f.Put(sibling, expiry); await f.Vault.RevokeAsync(f.Cell.Scope.AuthProfileId, 1); f.Cell.Clock.Now = f.Cell.Clock.Now.AddHours(2);
        using var set = await f.Capture(); var plan = await f.Plan(set); var before = Snapshot(f.Cell.Root); using var publicKey = f.Public();
        var originalProfiles = Directory.GetFiles(f.Options.RootPath, "*.credential.json").ToDictionary(p => Path.GetFileName(p)!, p => JsonDocument.Parse(File.ReadAllBytes(p)));
        try
        {
            f.Cell.Keys.Requests.Clear(); f.Destination.Requests.Clear(); var receipt = await f.Executor.StageAsync(set, plan, plan.Sha256, publicKey);
            Assert.Equal(2, f.Cell.Keys.Requests.Count); Assert.Single(f.Destination.Requests); Assert.Equal(plan.Sha256, receipt.PlanSha256);
            Assert.Equal(set.Sha256, receipt.SourceSetSha256); Assert.Equal(2, receipt.Profiles.Count); Assert.Empty(receipt.AbsentProfileIds);
            Assert.NotEqual(plan.SourceNamespace.VaultNamespaceId, receipt.StagedVaultNamespaceId); Assert.NotEqual(plan.SourceNamespace.AuthorityNamespaceId, receipt.StagedAuthorityNamespaceId);
            Assert.True(receipt.InactiveRecoveryHold); Assert.False(receipt.NormalVaultUsable); Assert.False(receipt.ActivationPerformed); Assert.False(receipt.ExternalExecutionAuthorized);
            Assert.False(receipt.KeyMaterialExported); Assert.False(receipt.KeyRetirementAuthorized); Assert.Equal("NotAssessed", receipt.IndependentBackupCoverage); Assert.False(receipt.ProductionRecoveryQualified);
            foreach (var pair in originalProfiles)
            {
                using var destination = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(f.StagePath, "profiles", pair.Key!)));
                Assert.Equal(pair.Value.RootElement.GetProperty("profile").GetRawText(), destination.RootElement.GetProperty("profile").GetRawText());
                Assert.Equal("destination-key", destination.RootElement.GetProperty("keyId").GetString());
                Assert.NotEqual(pair.Value.RootElement.GetProperty("ciphertext").GetString(), destination.RootElement.GetProperty("ciphertext").GetString());
            }
            using var state = JsonDocument.Parse(File.ReadAllBytes(Path.Combine(f.StagePath, "rollback", "state.json")));
            Assert.Equal("credential-recovery-stage-state", state.RootElement.GetProperty("kind").GetString()); Assert.Equal(0, state.RootElement.GetProperty("revision").GetInt64());
            Assert.Equal(2, state.RootElement.GetProperty("entries").GetArrayLength());
            Assert.All(Directory.EnumerateFileSystemEntries(f.StagePath, "*", SearchOption.AllDirectories).Prepend(f.StagePath), AssertPrivate);
            Assert.Equal(before, Snapshot(f.Cell.Root, f.StagePath));
            Assert.Throws<CredentialException>(() => new CredentialVault(f.Options with { RootPath = f.StagePath, RollbackProtection = null, EncryptionKeyId = "destination-key" }, f.Destination, f.Cell.Clock));
            Assert.Throws<CredentialException>(() => new CredentialVault(f.Options with { RootPath = Path.Combine(f.StagePath, "profiles"), RollbackProtection = null, EncryptionKeyId = "destination-key" }, f.Destination, f.Cell.Clock));
            var held = Snapshot(f.StagePath); f.Cell.Keys.Requests.Clear(); f.Destination.Requests.Clear();
            await Assert.ThrowsAsync<CredentialException>(() => f.Executor.StageAsync(set, plan, plan.Sha256, publicKey));
            Assert.Empty(f.Cell.Keys.Requests); Assert.Empty(f.Destination.Requests); Assert.Equal(held, Snapshot(f.StagePath));
            var verified = await new CredentialRecoveryStageVerifier(f.Destination, f.Cell.Clock).VerifyAsync(plan, plan.Sha256, receipt.Sha256);
            Assert.Equal(receipt.Sha256, verified.ReceiptSha256); Assert.Equal(2, verified.AuthenticatedProfileCount); Assert.True(verified.OutputCiphertextsAuthenticated);
            var publicText = JsonSerializer.Serialize(new { plan, receipt, verified }, Json);
            Assert.DoesNotContain(f.Cell.Root, publicText); Assert.DoesNotContain(CredentialFixture.Token, publicText); Assert.DoesNotContain(f.Cell.Scope.AuthProfileId, publicText);
            Assert.DoesNotContain("\"ciphertext\":", publicText, StringComparison.OrdinalIgnoreCase);
        }
        finally { foreach (var profile in originalProfiles.Values) profile.Dispose(); }
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Empty_stage_is_complete_private_and_permanently_held_without_minting_credentials(bool strong)
    {
        using var f = new StageFixture(strong: strong); using var set = await f.Capture(); var plan = await f.Plan(set); using var publicKey = f.Public();
        f.Cell.Keys.Requests.Clear(); f.Destination.Requests.Clear(); var receipt = await f.Executor.StageAsync(set, plan, plan.Sha256, publicKey);
        Assert.Empty(receipt.Profiles); Assert.Empty(receipt.AbsentProfileIds); Assert.Empty(Directory.GetFiles(Path.Combine(f.StagePath, "profiles")));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Single(f.Destination.Requests); Assert.True(receipt.InactiveRecoveryHold);
        var verified = await new CredentialRecoveryStageVerifier(f.Destination, f.Cell.Clock).VerifyAsync(plan, plan.Sha256, receipt.Sha256);
        Assert.Equal(0, verified.AuthenticatedProfileCount); Assert.False(verified.TemporaryInMemoryDecryptionPerformed); Assert.False(verified.NormalVaultUsable);
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Actual_current_user_source_and_destination_keys_stage_without_exporting_or_modifying_either_key_namespace(bool strong)
    {
        using var f = new CredentialFixture(); f.Clock.Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
        var source = f.ProvisionNativeKeys("stage-native-source"); var destination = f.ProvisionNativeKeys("stage-native-destination");
        using (await source.ProvisionKeyAsync(f.Scope.CustomerId, f.Scope.CellId, "key-1")) { }
        using (await destination.ProvisionKeyAsync(f.Scope.CustomerId, f.Scope.CellId, "native-destination")) { }
        var options = f.Options with { RootPath = Path.Combine(f.Root, "native-stage-source-vault"), RollbackProtection = strong ? new(Path.Combine(f.Root, "native-stage-source-authority")) : null };
        var vault = strong ? CredentialVault.ProvisionStrong(options, source, f.Clock) : CredentialVault.Provision(options, source, f.Clock);
        using (var material = CredentialSecret.FromUtf8(CredentialFixture.Token)) await vault.PutAsync(f.Scope, material, f.Clock.Now.AddHours(1));
        using var signer = RSA.Create(2048); var identity = vault.DescribeNamespace(); using var set = await vault.CaptureSignedRecoverySetAsync(identity, signer, "stage-signer", 1, f.Clock.Now, f.Clock.Now.AddHours(1));
        using var publicKey = Public(signer); var pin = CredentialRecoverySets.Authority(set); var selected = CredentialRecoverySets.Verify(set, pin, publicKey, identity, f.Clock);
        var key = await CredentialRecoveryStaging.ObserveDestinationKeyAsync(f.Scope.CustomerId, f.Scope.CellId, "native-destination", destination);
        var path = Path.Combine(f.Root, "native-recovered-held"); var plan = CredentialRecoveryStaging.PreparePlan(selected, pin, identity, path, key, "Synthetic native inactive recovery.", f.Clock.Now.AddMinutes(20), f.Clock);
        var before = Snapshot(f.Root); var receipt = await new CredentialRecoveryStageExecutor(source, destination, f.Clock).StageAsync(set, plan, plan.Sha256, publicKey);
        var verified = await new CredentialRecoveryStageVerifier(destination, f.Clock).VerifyAsync(plan, plan.Sha256, receipt.Sha256);
        Assert.Equal(1, verified.AuthenticatedProfileCount); Assert.Equal(before, Snapshot(f.Root, path));
        Assert.Equal(2, Directory.GetFiles(f.Root, "*.dpapi.json", SearchOption.AllDirectories).Length); Assert.Empty(Directory.GetFiles(path, "*.dpapi.json", SearchOption.AllDirectories));
        Assert.False(verified.IndependentKeyBackupVerified); Assert.False(verified.NamespaceAdopted); Assert.Equal("NotAssessed", verified.IndependentBackupCoverage);
    }

    [WindowsCredentialFact]
    public async Task Destination_provider_handle_is_cloned_and_frozen_before_source_authentication_can_dispose_its_alias()
    {
        using var f = new StageFixture(); await f.Put(f.Cell.Scope); using var set = await f.Capture();
        var alias = new AliasKeyProvider(f.Destination.Values["destination-key"]); var key = await CredentialRecoveryStaging.ObserveDestinationKeyAsync(f.Cell.Scope.CustomerId, f.Cell.Scope.CellId, "destination-key", alias);
        using var publicKey = f.Public(); var selected = CredentialRecoverySets.Verify(set, CredentialRecoverySets.Authority(set), publicKey, f.Vault.DescribeNamespace(), f.Cell.Clock);
        var plan = CredentialRecoveryStaging.PreparePlan(selected, CredentialRecoverySets.Authority(set), f.Vault.DescribeNamespace(), f.StagePath, key, "Synthetic frozen key.", f.Cell.Clock.Now.AddMinutes(20), f.Cell.Clock);
        f.Cell.Keys.OnGet = () => alias.Last!.Dispose(); alias.Calls = 0;
        var receipt = await new CredentialRecoveryStageExecutor(f.Cell.Keys, alias, f.Cell.Clock).StageAsync(set, plan, plan.Sha256, publicKey);
        Assert.Equal(1, alias.Calls); Assert.Single(receipt.Profiles);
        Assert.Equal(1, (await new CredentialRecoveryStageVerifier(f.Destination, f.Cell.Clock).VerifyAsync(plan, plan.Sha256, receipt.Sha256)).AuthenticatedProfileCount);
    }

    [WindowsCredentialFact]
    public async Task Unrelated_provider_cancellation_is_unavailable_and_does_not_claim_the_operation_deadline_elapsed()
    {
        using var f = new CredentialFixture(); var error = await Assert.ThrowsAsync<CredentialException>(() => CredentialRecoveryStaging.ObserveDestinationKeyAsync(
            f.Scope.CustomerId, f.Scope.CellId, "destination-key", new UnrelatedCanceledKeyProvider()));
        Assert.Equal("credential-recovery-stage-destination-key-unavailable", error.Code);
    }

    static RSA Public(RSA signer) { var copy = RSA.Create(); copy.ImportSubjectPublicKeyInfo(signer.ExportSubjectPublicKeyInfo(), out _); return copy; }
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static string[] Snapshot(string root, string? excluded = null) => new[] { root }.Concat(Directory.GetFileSystemEntries(root, "*", SearchOption.AllDirectories))
        .Where(p => excluded is null || p != excluded && !p.StartsWith(excluded + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase)).Order(StringComparer.Ordinal)
        .Select(p => Path.GetRelativePath(root, p) + ":" + (Directory.Exists(p) ? "directory" : Hash(File.ReadAllBytes(p))) + ":" +
            (Directory.Exists(p) ? (FileSystemSecurity)new DirectoryInfo(p).GetAccessControl() : new FileInfo(p).GetAccessControl()).GetSecurityDescriptorSddlForm(AccessControlSections.Access | AccessControlSections.Owner)).ToArray();
    static void AssertPrivate(string path)
    {
        using var identity = WindowsIdentity.GetCurrent(); var acl = Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl();
        Assert.True(acl.AreAccessRulesProtected); Assert.Equal(identity.User, acl.GetOwner(typeof(SecurityIdentifier)));
        Assert.All(acl.GetAccessRules(true, true, typeof(SecurityIdentifier)).Cast<FileSystemAccessRule>().Where(r => r.AccessControlType == AccessControlType.Allow), r => Assert.Equal(identity.User!.Value, r.IdentityReference.Value));
    }
    sealed class AliasKeyProvider(byte[] bytes) : ICredentialKeyProvider
    {
        internal int Calls; internal CredentialEncryptionKey? Last;
        public ValueTask<CredentialEncryptionKey> GetKeyAsync(string c, string e, string k, CancellationToken ct)
        { ct.ThrowIfCancellationRequested(); Calls++; Last = new(bytes); return ValueTask.FromResult(Last); }
    }
    sealed class UnrelatedCanceledKeyProvider : ICredentialKeyProvider
    { public ValueTask<CredentialEncryptionKey> GetKeyAsync(string c, string e, string k, CancellationToken ct) => throw new OperationCanceledException("Synthetic unrelated provider cancellation."); }

    sealed class StageFixture : IDisposable
    {
        internal CredentialFixture Cell { get; }
        internal CredentialVaultOptions Options { get; }
        internal CredentialVault Vault { get; }
        internal CredentialTestKeys Destination { get; } = new();
        internal RSA Signer { get; } = RSA.Create(2048);
        internal string StagePath => Path.Combine(Cell.Root, "held-recovery-stage");
        internal CredentialRecoveryStageExecutor Executor => new(Cell.Keys, Destination, Cell.Clock);
        internal StageFixture(string provider = PipelineRequestFactory.GitHub, bool strong = false)
        {
            Cell = new(provider); Cell.Clock.Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z");
            Options = strong ? Cell.Options with { RootPath = Path.Combine(Cell.Root, "stage-source-strong"), RollbackProtection = new(Path.Combine(Cell.Root, "stage-source-authority")) } : Cell.Options;
            Vault = strong ? CredentialVault.ProvisionStrong(Options, Cell.Keys, Cell.Clock) : Cell.Vault;
            Destination.Values["destination-key"] = RandomNumberGenerator.GetBytes(32);
        }
        internal async Task Put(CredentialScope scope, DateTimeOffset? expiry = null)
        { using var material = CredentialSecret.FromUtf8(CredentialFixture.Token); await Vault.PutAsync(scope, material, expiry ?? Cell.Clock.Now.AddHours(1)); }
        internal Task<CredentialRecoverySet> Capture() => Vault.CaptureSignedRecoverySetAsync(Vault.DescribeNamespace(), Signer, "stage-signer", 1, Cell.Clock.Now, Cell.Clock.Now.AddHours(1));
        internal RSA Public() => CredentialRecoveryStageTests.Public(Signer);
        internal async Task<CredentialRecoveryStagePlan> Plan(CredentialRecoverySet set)
        {
            using var key = Public(); var pin = CredentialRecoverySets.Authority(set); var ns = Vault.DescribeNamespace(); var selected = CredentialRecoverySets.Verify(set, pin, key, ns, Cell.Clock);
            var destination = await CredentialRecoveryStaging.ObserveDestinationKeyAsync(Cell.Scope.CustomerId, Cell.Scope.CellId, "destination-key", Destination);
            return CredentialRecoveryStaging.PreparePlan(selected, pin, ns, StagePath, destination, "Synthetic owner-reviewed inactive recovery.", Cell.Clock.Now.AddMinutes(20), Cell.Clock);
        }
        public void Dispose() { Signer.Dispose(); Cell.Dispose(); }
    }
}
