using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Net;
using SelfService.Foundation;
using Xunit;
using BoundHost = SelfService.Foundation.Tests.FoundationBoundConsumerHostIntegrationTests.BoundHost;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialBoundDispatchRecoveryCompatibilityTests
{
    [Theory]
    [InlineData(PipelineRequestFactory.GitHub)]
    [InlineData(PipelineRequestFactory.AzureDevOps)]
    public async Task Restricted_namespace_reaches_real_signed_Host_workflow_only_through_its_actual_bound_consumer(string provider)
    {
        await using var f = await BoundHost.Create(provider, accessProfile: CredentialVaultAccessProfile.FoundationBoundDispatchOnly);
        var plan = await f.Plan();
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("requester", HttpMethod.Post, $"/plans/{plan.Id}/approve", new { plan.Digest })).StatusCode);
        Assert.Equal(HttpStatusCode.Forbidden, (await f.Send("operator", HttpMethod.Post, $"/plans/{plan.Id}/enqueue", new { plan.Digest, idempotencyKey = "restricted-before-approval" })).StatusCode);
        Assert.Equal(0, f.Number("SELECT count(*) FROM jobs")); Assert.Equal(0, f.FactoryCalls); var keyReads = f.Keys.Requests.Count;
        Assert.Equal("credential-bound-dispatch-session-required", (await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ResolveAsync(f.Template.Scope))).Code);
        Assert.Equal(keyReads, f.Keys.Requests.Count);
        await f.Approve(plan); var job = await f.Enqueue(plan);
        Assert.Equal(keyReads, f.Keys.Requests.Count); Assert.Equal(0, f.FactoryCalls);
        Assert.True(await f.Runner.RunOnceAsync("restricted-host-worker")); var result = await f.Job(job.Id);
        Assert.Equal("Succeeded", result.State); Assert.Contains("\"mock\":true", result.ReceiptJson); Assert.Contains("\"deploymentVerified\":false", result.ReceiptJson);
        Assert.Equal(1, f.Posts); Assert.Equal(1, f.FactoryCalls); Assert.Equal(1, f.HandlerDisposals); Assert.Equal(keyReads + 1, f.Keys.Requests.Count);
        Assert.Equal("Bearer " + CredentialFixture.Token, f.Authorization); Assert.Equal(1, f.Number("SELECT count(*) FROM audit WHERE action='JobCompleted'"));
        Assert.True(f.Store.VerifyAuditIntegrity(f.Customer, BoundHost.Actor("auditor")));
        Assert.Equal("credential-bound-dispatch-session-required", (await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ResolveAsync(f.Template.Scope))).Code);
        Assert.Equal(keyReads + 1, f.Keys.Requests.Count);
    }

    [Theory]
    [InlineData("inventory", false)] [InlineData("inventory", true)]
    [InlineData("set", false)] [InlineData("set", true)]
    [InlineData("scoped-inventory", false)] [InlineData("scoped-inventory", true)]
    [InlineData("scoped-set", false)] [InlineData("scoped-set", true)]
    [InlineData("absence-scope", false)] [InlineData("absence-scope", true)]
    public async Task Restricted_recovery_entrypoints_refuse_before_any_key_or_signer_and_preserve_all_owned_paths(string operation, bool populated)
    {
        using var f = new RestrictedFixture(); if (populated) await f.Put();
        var identity = f.Vault.DescribeNamespace(); f.Cell.Keys.Requests.Clear(); f.Cell.Keys.Fail = true;
        using var signer = new UnusableSigner(); var before = Snapshot(f.Cell.Root);
        var error = await Assert.ThrowsAsync<CredentialException>(async () =>
        {
            switch (operation)
            {
                case "inventory": _ = await f.Vault.InspectRecoveryDependenciesAsync(identity); break;
                case "set": using (await f.Vault.CaptureSignedRecoverySetAsync(identity, signer, "unused", 1, f.Cell.Clock.Now, f.Cell.Clock.Now.AddHours(1))) { } break;
                case "scoped-inventory": _ = await f.Vault.InspectScopedRecoveryDependenciesAsync(identity); break;
                case "scoped-set": using (await f.Vault.CaptureScopedSignedRecoverySetAsync(identity, signer, "unused", 1, f.Cell.Clock.Now, f.Cell.Clock.Now.AddHours(1))) { } break;
                default: _ = await f.Vault.InspectAbsenceScopeProvenanceAsync(identity); break;
            }
        });
        var domain = operation.StartsWith("scoped-", StringComparison.Ordinal) ? "scoped" : operation;
        Assert.Equal("credential-recovery-" + domain + "-bound-dispatch-profile-unsupported", error.Code);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(0, signer.Calls); Assert.Equal(before, Snapshot(f.Cell.Root));
        Assert.False(Directory.Exists(f.FutureDestination));
    }

    [Fact]
    public async Task Restricted_namespace_retains_metadata_and_explicit_owner_profile_maintenance_without_a_plaintext_lease()
    {
        using var f = new RestrictedFixture(); await f.Put(); var identity = f.Vault.DescribeNamespace();
        var profile = await f.Vault.GetProfileAsync(f.Cell.Scope.AuthProfileId); Assert.Equal(1, profile.Generation); Assert.True(profile.Enabled);
        var state = await f.Vault.InspectRollbackStateAsync(f.Cell.Scope.AuthProfileId); Assert.Equal(1, state.CommittedGeneration);
        Assert.Equal(identity, new CredentialVault(f.Options, f.Cell.Keys, f.Cell.Clock).DescribeNamespace());
        f.Cell.Keys.Requests.Clear(); var before = Snapshot(f.Cell.Root);
        Assert.Equal("credential-bound-dispatch-session-required", (await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ResolveAsync(f.Cell.Scope))).Code);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Fact]
    public void Stripping_the_profile_on_the_original_root_cannot_open_a_general_credential_namespace()
    {
        using var f = new RestrictedFixture(); var before = Snapshot(f.Cell.Root); f.Cell.Keys.Requests.Clear();
        Assert.Throws<CredentialException>(() => new CredentialVault(f.Options with { AccessProfile = CredentialVaultAccessProfile.General }, f.Cell.Keys, f.Cell.Clock));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData(CredentialVaultAccessProfile.General)]
    [InlineData(CredentialVaultAccessProfile.FoundationBoundDispatchOnly)]
    public async Task A_private_copy_keeps_restricted_marker_and_root_binding_and_is_never_adopted(CredentialVaultAccessProfile profile)
    {
        using var f = new RestrictedFixture(); await f.Put();
        var vaultCopy = Path.Combine(f.Cell.Root, "copy-vault"); var rollbackCopy = Path.Combine(f.Cell.Root, "copy-authority");
        CopyPrivate(f.Options.RootPath, vaultCopy); CopyPrivate(f.Options.RollbackProtection!.AuthorityDirectory, rollbackCopy);
        var before = Snapshot(f.Cell.Root); f.Cell.Keys.Requests.Clear();
        var copied = f.Options with { RootPath = vaultCopy, AccessProfile = profile, RollbackProtection = new(rollbackCopy) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 } };
        Assert.Throws<CredentialException>(() => new CredentialVault(copied, f.Cell.Keys, f.Cell.Clock));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, Snapshot(f.Cell.Root));
    }

    [Theory]
    [InlineData("", CredentialVaultAccessProfile.General)]
    [InlineData("profiles", CredentialVaultAccessProfile.General)]
    [InlineData("rollback", CredentialVaultAccessProfile.General)]
    [InlineData("", CredentialVaultAccessProfile.FoundationBoundDispatchOnly)]
    [InlineData("profiles", CredentialVaultAccessProfile.FoundationBoundDispatchOnly)]
    [InlineData("rollback", CredentialVaultAccessProfile.FoundationBoundDispatchOnly)]
    public async Task A_real_completed_inactive_stage_cannot_be_reopened_as_either_operational_vault_profile(string child, CredentialVaultAccessProfile profile)
    {
        using var cell = new CredentialFixture(); using var signer = RSA.Create(2048); var destinationKeys = new CredentialTestKeys();
        var options = cell.Options with { RootPath = Path.Combine(cell.Root, "general-scoped-source"),
            RollbackProtection = new(Path.Combine(cell.Root, "general-scoped-authority")) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 } };
        var source = CredentialVault.ProvisionStrongScoped(options, cell.Keys, cell.Clock);
        using (var material = CredentialSecret.FromUtf8(CredentialFixture.Token)) await source.PutAsync(cell.Scope, material, cell.Clock.Now.AddHours(1));
        var identity = source.DescribeNamespace();
        using var set = await source.CaptureScopedSignedRecoverySetAsync(identity, signer, "general-source", 1, cell.Clock.Now, cell.Clock.Now.AddHours(1));
        var authority = CredentialScopedRecoverySets.Authority(set); var verified = CredentialScopedRecoverySets.Verify(set, authority, signer, identity, cell.Clock);
        var key = await CredentialRecoveryStaging.ObserveDestinationKeyAsync(identity.CustomerId, identity.CellId, "key-1", destinationKeys);
        var root = Path.Combine(cell.Root, "held-stage");
        var plan = CredentialScopedRecoveryStaging.PreparePlan(verified, authority, identity, root, key, "Synthetic held-stage profile compatibility", cell.Clock.Now.AddMinutes(10), cell.Clock);
        var receipt = await new CredentialScopedRecoveryStageExecutor(cell.Keys, destinationKeys, cell.Clock).StageAsync(set, plan, plan.Sha256, signer);
        Assert.True((await new CredentialScopedRecoveryStageVerifier(destinationKeys, cell.Clock).VerifyAsync(plan, plan.Sha256, receipt.Sha256)).InactiveRecoveryHold);
        var before = Snapshot(cell.Root); cell.Keys.Requests.Clear(); destinationKeys.Requests.Clear();
        var proposed = options with { RootPath = Path.Combine(root, child), AccessProfile = profile,
            RollbackProtection = new(Path.Combine(root, "rollback")) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 } };
        Assert.Throws<CredentialException>(() => new CredentialVault(proposed, destinationKeys, cell.Clock));
        Assert.Empty(cell.Keys.Requests); Assert.Empty(destinationKeys.Requests); Assert.Equal(before, Snapshot(cell.Root));
    }

    sealed class RestrictedFixture : IDisposable
    {
        internal CredentialFixture Cell { get; } = new();
        internal CredentialVaultOptions Options { get; }
        internal CredentialVault Vault { get; }
        internal string FutureDestination => Path.Combine(Cell.Root, "not-created-recovery-destination");
        internal RestrictedFixture()
        {
            Options = Cell.Options with { RootPath = Path.Combine(Cell.Root, "restricted-vault"), AccessProfile = CredentialVaultAccessProfile.FoundationBoundDispatchOnly,
                RollbackProtection = new(Path.Combine(Cell.Root, "restricted-authority")) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 } };
            Vault = CredentialVault.ProvisionStrongBoundDispatch(Options, Cell.Keys, Cell.Clock);
        }
        internal async Task Put() { using var material = CredentialSecret.FromUtf8(CredentialFixture.Token); await Vault.PutAsync(Cell.Scope, material, Cell.Clock.Now.AddHours(1)); }
        public void Dispose() => Cell.Dispose();
    }
    static void CopyPrivate(string source, string target)
    {
        Assert.False(Path.Exists(target));
        Assert.Equal(Path.GetDirectoryName(source), Path.GetDirectoryName(target));
        using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User!; var acl = new DirectorySecurity();
        acl.SetOwner(sid); acl.SetAccessRuleProtection(true, false);
        acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        new DirectoryInfo(target).Create(acl);
        foreach (var path in Directory.GetFiles(source)) File.Copy(path, Path.Combine(target, Path.GetFileName(path)));
    }
    static string[] Snapshot(string root) => Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories).Order(StringComparer.Ordinal)
        .Select(path =>
        {
            var item = new FileInfo(path); var directory = Directory.Exists(path); Assert.False(File.GetAttributes(path).HasFlag(FileAttributes.ReparsePoint));
            var acl = directory ? new DirectoryInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm() : item.GetAccessControl().GetSecurityDescriptorBinaryForm();
            return Path.GetRelativePath(root, path) + ":" + File.GetAttributes(path) + ":" + Convert.ToHexStringLower(SHA256.HashData(acl)) + ":" +
                (directory ? "directory" : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path))));
        }).ToArray();
    sealed class UnusableSigner : RSA
    {
        internal int Calls { get; private set; }
        Exception Used() { Calls++; return new InvalidOperationException("Signer must remain unused for an unsupported profile."); }
        public override byte[] Decrypt(byte[] data, RSAEncryptionPadding padding) => throw Used();
        public override byte[] Encrypt(byte[] data, RSAEncryptionPadding padding) => throw Used();
        public override RSAParameters ExportParameters(bool includePrivateParameters) => throw Used();
        public override void ImportParameters(RSAParameters parameters) => throw Used();
        public override byte[] SignHash(byte[] hash, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) => throw Used();
        public override bool VerifyHash(byte[] hash, byte[] signature, HashAlgorithmName hashAlgorithm, RSASignaturePadding padding) => throw Used();
    }
}
