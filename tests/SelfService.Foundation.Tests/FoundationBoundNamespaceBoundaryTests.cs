using System.Net;
using System.Reflection;
using System.Runtime.ExceptionServices;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationBoundNamespaceBoundaryTests
{
    [Theory]
    [InlineData("profile-omitted")]
    [InlineData("rollback-omitted")]
    [InlineData("legacy-format")]
    [InlineData("unknown-profile")]
    public async Task Reopening_cannot_strip_or_substitute_the_persisted_restricted_profile(string change)
    {
        using var f = await BoundNamespaceCoreFixture.Create(); var before = Snapshot(f.Options.RootPath, f.Options.RollbackProtection!.AuthorityDirectory);
        f.Cell.Keys.Requests.Clear();
        var changed = change switch
        {
            "profile-omitted" => f.Options with { AccessProfile = CredentialVaultAccessProfile.General },
            "rollback-omitted" => f.Options with { RollbackProtection = null },
            "legacy-format" => f.Options with { RollbackProtection = new(f.Options.RollbackProtection!.AuthorityDirectory) },
            _ => f.Options with { AccessProfile = (CredentialVaultAccessProfile)99 }
        };
        Assert.Throws<CredentialException>(() => new CredentialVault(changed, f.Cell.Keys, f.Cell.Clock));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, Snapshot(f.Options.RootPath, f.Options.RollbackProtection!.AuthorityDirectory));
        Assert.Equal(f.Namespace, new CredentialVault(f.Options, f.Cell.Keys, f.Cell.Clock).DescribeNamespace());
        Assert.Empty(f.Cell.Keys.Requests);
    }

    [Theory]
    [InlineData("weak")]
    [InlineData("strong-v1")]
    [InlineData("scoped-v2")]
    public void Restricted_provisioning_never_converts_or_repairs_an_existing_general_root(string original)
    {
        using var cell = new CredentialFixture();
        var general = cell.Options with { RootPath = Path.Combine(cell.Root, "general-source"), RollbackProtection = original == "weak" ? null :
            new(Path.Combine(cell.Root, "general-authority")) { Format = original == "scoped-v2" ? CredentialRollbackFormat.AuthenticatedScopeV2 : CredentialRollbackFormat.LegacyV1 } };
        _ = original switch { "weak" => CredentialVault.Provision(general, cell.Keys, cell.Clock), "strong-v1" => CredentialVault.ProvisionStrong(general, cell.Keys, cell.Clock), _ => CredentialVault.ProvisionStrongScoped(general, cell.Keys, cell.Clock) };
        var before = Snapshot(cell.Root); var target = general with
        {
            AccessProfile = CredentialVaultAccessProfile.FoundationBoundDispatchOnly,
            RollbackProtection = new(general.RollbackProtection?.AuthorityDirectory ?? Path.Combine(cell.Root, "not-created-authority")) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 }
        };
        Assert.Throws<CredentialException>(() => CredentialVault.ProvisionStrongBoundDispatch(target, cell.Keys, cell.Clock));
        Assert.Equal(before, Snapshot(cell.Root)); Assert.Empty(cell.Keys.Requests);
    }

    [Theory]
    [InlineData("vault")]
    [InlineData("authority")]
    public async Task Exact_private_root_copies_do_not_adopt_the_original_namespace_or_binding(string copied)
    {
        using var f = await BoundNamespaceCoreFixture.Create(); var copy = Path.Combine(f.Cell.Root, "copied-" + copied + "-" + Guid.NewGuid().ToString("N"));
        CopyPrivateRoot(copied == "vault" ? f.Options.RootPath : f.Options.RollbackProtection!.AuthorityDirectory, copy);
        var options = copied == "vault" ? f.Options with { RootPath = copy } : f.Options with
        { RollbackProtection = new(copy) { Format = CredentialRollbackFormat.AuthenticatedScopeV2 } };
        var before = Snapshot(f.Options.RootPath, f.Options.RollbackProtection!.AuthorityDirectory, copy); f.Cell.Keys.Requests.Clear();
        Assert.Throws<CredentialException>(() => new CredentialVault(options, f.Cell.Keys, f.Cell.Clock));
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, Snapshot(f.Options.RootPath, f.Options.RollbackProtection!.AuthorityDirectory, copy));
    }

    [Theory]
    [InlineData("vault")]
    [InlineData("authority")]
    public async Task Rehashed_marker_mode_changes_cannot_retarget_an_existing_concrete_bound_operation(string changed)
    {
        using var f = await BoundNamespaceCoreFixture.Create();
        var directory = changed == "vault" ? f.Options.RootPath : f.Options.RollbackProtection!.AuthorityDirectory;
        var marker = Path.Combine(directory, ".credential-namespace.json"); var original = File.ReadAllBytes(marker);
        var node = JsonNode.Parse(original)!.AsObject(); node["kind"] = changed == "vault" ? "credential-vault-scoped-fenced" : "credential-rollback-scoped-authority";
        var unsigned = JsonSerializer.SerializeToUtf8Bytes(new
        {
            schema = node["schema"]!.GetValue<int>(), kind = node["kind"]!.GetValue<string>(), customerId = node["customerId"]!.GetValue<string>(),
            cellId = node["cellId"]!.GetValue<string>(), rootPath = node["rootPath"]!.GetValue<string>(), ownerSid = node["ownerSid"]!.GetValue<string>(), namespaceId = node["namespaceId"]!.GetValue<string>()
        }, Json);
        node["sha256"] = Convert.ToHexStringLower(SHA256.HashData(unsigned));
        File.WriteAllText(marker, node.ToJsonString(), new UTF8Encoding(false));
        try
        {
            var before = Snapshot(f.Options.RootPath, f.Options.RollbackProtection!.AuthorityDirectory); f.Cell.Keys.Requests.Clear();
            using var handler = new BoundCaptureHandler(); using var transport = f.Transport(handler);
            var error = await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(f.Wire, default));
            Assert.Equal("pipeline-credential-authority-denied", error.ReasonCode); Assert.Equal(0, handler.Calls); Assert.Empty(f.Cell.Keys.Requests);
            Assert.Equal(before, Snapshot(f.Options.RootPath, f.Options.RollbackProtection!.AuthorityDirectory));
        }
        finally { File.WriteAllBytes(marker, original); }
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Direct_source_reads_refuse_even_for_a_valid_but_absent_profile_before_any_key_lookup(bool revalidate)
    {
        using var f = await BoundNamespaceCoreFixture.Create();
        var missing = f.Cell.Scope with { AuthProfileId = "profile/not-published" }; var before = Snapshot(f.Options.RootPath, f.Options.RollbackProtection!.AuthorityDirectory);
        f.Cell.Keys.Requests.Clear();
        var error = revalidate ? await Assert.ThrowsAsync<CredentialException>(() => f.Vault.RevalidateAsync(missing, 1)) :
            await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ResolveAsync(missing));
        Assert.Equal("credential-bound-dispatch-session-required", error.Code); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(before, Snapshot(f.Options.RootPath, f.Options.RollbackProtection!.AuthorityDirectory));
    }

    [Theory]
    [InlineData(PipelineRequestFactory.GitHub, false)]
    [InlineData(PipelineRequestFactory.GitHub, true)]
    [InlineData(PipelineRequestFactory.AzureDevOps, false)]
    [InlineData(PipelineRequestFactory.AzureDevOps, true)]
    public async Task Token_acquisition_refuses_the_real_restricted_source_before_private_material_or_HTTP(string provider, bool enabledMock)
    {
        using var f = await BoundNamespaceCoreFixture.Create(provider); var providers = new PrivateProviders(); using var handler = new BoundCaptureHandler();
        using var http = enabledMock ? CredentialTokenHttpClient.ForMockHandler(handler) : new CredentialTokenHttpClient();
        var before = Snapshot(f.Options.RootPath, f.Options.RollbackProtection!.AuthorityDirectory); f.Cell.Keys.Requests.Clear();
        Task<CredentialAcquisitionResult> Acquire() => provider == PipelineRequestFactory.GitHub ?
            new CredentialGitHubInstallationSource(f.Cell.Scope, f.Cell.Connection, new("synthetic-app", 123, 456), f.Vault, providers, http, f.Cell.Clock).AcquireAndStoreAsync(1) :
            new CredentialEntraFederatedSource(f.Cell.Scope, new("30000000-0000-0000-0000-000000000001", "40000000-0000-0000-0000-000000000001"), f.Vault, providers, http, f.Cell.Clock).AcquireAndStoreAsync(1);
        var error = await Assert.ThrowsAsync<CredentialException>(Acquire);
        Assert.Equal("credential-bound-dispatch-acquisition-unsupported", error.Code);
        Assert.Equal(0, providers.PrivateKeys); Assert.Equal(0, providers.Assertions); Assert.Equal(0, handler.Calls); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(before, Snapshot(f.Options.RootPath, f.Options.RollbackProtection!.AuthorityDirectory));
    }

    [Theory]
    [InlineData("valid")]
    [InlineData("null")]
    [InlineData("throw")]
    public async Task A_hostile_generic_permit_source_cannot_replace_a_concrete_session_even_while_a_real_metadata_run_exists(string mode)
    {
        using var f = await BoundNamespaceCoreFixture.Create(); using var consumer = Consumer(f); using var run = consumer.OpenRun(f.Store);
        var permits = new HostilePermits(f, mode); using var handler = new BoundCaptureHandler();
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, permits, handler, f.Cell.Clock);
        f.Cell.Keys.Requests.Clear(); var before = Snapshot(f.Options.RootPath, f.Options.RollbackProtection!.AuthorityDirectory);
        var error = await Assert.ThrowsAsync<PipelineContractException>(() => transport.SendAsync(f.Wire, default));
        Assert.Equal("pipeline-credential-authority-denied", error.ReasonCode);
        Assert.Equal(0, permits.Calls); Assert.Equal(0, handler.Calls); Assert.Empty(f.Cell.Keys.Requests); run.AssertCurrent();
        Assert.Equal(before, Snapshot(f.Options.RootPath, f.Options.RollbackProtection!.AuthorityDirectory));
    }

    [Fact]
    public async Task An_actual_metadata_only_consumer_session_cannot_be_used_as_an_internal_credential_read_proof()
    {
        using var f = await BoundNamespaceCoreFixture.Create(); using var consumer = Consumer(f); using var run = consumer.OpenRun(f.Store);
        var session = typeof(FoundationCredentialBoundPipelineRun).GetField("session", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(run)!;
        var before = Snapshot(f.Options.RootPath, f.Options.RollbackProtection!.AuthorityDirectory); f.Cell.Keys.Requests.Clear();
        var error = await Assert.ThrowsAsync<CredentialException>(() => ReadWithSession(f.Vault, f.Cell.Scope, session));
        Assert.Equal("credential-bound-dispatch-session-required", error.Code); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(before, Snapshot(f.Options.RootPath, f.Options.RollbackProtection!.AuthorityDirectory)); run.AssertCurrent();
    }

    [Fact]
    public async Task A_real_current_dispatch_session_is_specific_to_its_actual_vault_reference_even_for_the_same_root_and_customer()
    {
        using var f = await BoundNamespaceCoreFixture.Create();
        var source = new FoundationCredentialDispatchPermitSource(f.Store, f.Dispatch.Job.Id, f.Dispatch.Job.WorkerId!, f.Dispatch.Job.Fence,
            f.Cell.Scope, f.Dispatch.Plan.Digest, f.Dispatch.Plan.SourceRevision, f.Dispatch.Plan.CatalogDigest, f.Verifier, BoundTransportFixture.Runtime);
        var binding = typeof(FoundationCredentialDispatchPermitSource).GetField("binding", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(source)!;
        var sessionType = typeof(CredentialVault).Assembly.GetType("SelfService.Foundation.FoundationCredentialUseSession", throwOnError: true)!;
        var factory = sessionType.GetMethod("Open", BindingFlags.Static | BindingFlags.NonPublic)!;
        using var session = (IDisposable)Invoke(factory, null, [f.Store, f.Vault, f.Authority, f.Namespace, f.AuthorityIdentity, f.Instance, f.Binding.Sha256,
            binding, f.Verifier, BoundTransportFixture.Runtime])!;
        // Positive control: this is an actual factory-produced dispatch session, not an inactive or fabricated proof.
        using (var lease = await ReadWithSession(f.Vault, f.Cell.Scope, session)) Assert.Equal(1, lease.Profile.Generation);
        var sameRootOtherObject = new CredentialVault(f.Options, f.Cell.Keys, f.Cell.Clock);
        Assert.Equal(f.Namespace, sameRootOtherObject.DescribeNamespace()); f.Cell.Keys.Requests.Clear();
        var before = Snapshot(f.Options.RootPath, f.Options.RollbackProtection!.AuthorityDirectory);
        var error = await Assert.ThrowsAsync<CredentialException>(() => ReadWithSession(sameRootOtherObject, f.Cell.Scope, session));
        Assert.Equal("credential-bound-dispatch-session-required", error.Code); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(before, Snapshot(f.Options.RootPath, f.Options.RollbackProtection!.AuthorityDirectory));
    }

    [Theory]
    [InlineData("key")]
    [InlineData("headers")]
    [InlineData("body")]
    [InlineData("disposal")]
    public async Task Restricted_bound_operations_keep_actual_activation_through_cancelled_uncooperative_dependencies(string phase)
    {
        using var f = await BoundNamespaceCoreFixture.Create(); var probe = new HeldOperation(phase, f.Cell.Keys);
        var vault = phase == "key" ? new CredentialVault(f.Options, probe, f.Cell.Clock) : f.Vault;
        using var handler = new HeldHandler(probe); using var transport = Bound(f, vault, handler);
        using var cancel = new CancellationTokenSource(); using var watchdog = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var before = Snapshot(f.Options.RootPath, f.Options.RollbackProtection!.AuthorityDirectory);
        f.Cell.Keys.Requests.Clear();
        var pending = transport.SendAsync(f.Wire, cancel.Token);
        try
        {
            await probe.Entered.Task.WaitAsync(watchdog.Token); Busy(f);
            if (phase == "key")
            {
                // These direct calls must refuse while the real operation still owns its exclusive key/admission gate.
                // Awaiting the actual provider barrier, not a delay, fixes the concurrent boundary.
                Assert.Equal(1, probe.KeyCalls); Assert.Empty(f.Cell.Keys.Requests);
                Assert.Equal("credential-bound-dispatch-session-required", (await Assert.ThrowsAsync<CredentialException>(() => vault.ResolveAsync(f.Cell.Scope))).Code);
                Assert.Equal("credential-bound-dispatch-session-required", (await Assert.ThrowsAsync<CredentialException>(() => vault.RevalidateAsync(f.Cell.Scope, 1))).Code);
                Assert.Equal(1, probe.KeyCalls); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(0, probe.Posts);
                Assert.False(probe.Release.Task.IsCompleted); Assert.False(pending.IsCompleted); Busy(f);
            }
            cancel.Cancel();
            Assert.False(pending.IsCompleted); Busy(f);
            // Generic preflight is independent of the held admission gate and still cannot supply a proof.
            var hostile = new HostilePermits(f, "valid"); using var genericHandler = new BoundCaptureHandler();
            using var generic = PipelineAuthenticatedTransport.ForMockHandler(vault, hostile, genericHandler, f.Cell.Clock);
            var denied = await Assert.ThrowsAsync<PipelineContractException>(() => generic.SendAsync(f.Wire, default));
            Assert.Equal("pipeline-credential-authority-denied", denied.ReasonCode); Assert.Equal(0, hostile.Calls); Assert.Equal(0, genericHandler.Calls);
        }
        finally { probe.Release.TrySetResult(); }
        var interrupted = await Assert.ThrowsAnyAsync<OperationCanceledException>(async () => _ = await pending.WaitAsync(watchdog.Token));
        Assert.Equal(cancel.Token, interrupted.CancellationToken); Released(f);
        Assert.Equal(phase == "key" ? 0 : 1, probe.Posts); Assert.Equal(before, Snapshot(f.Options.RootPath, f.Options.RollbackProtection!.AuthorityDirectory));
        Assert.Equal("Dispatched", f.Store.GetJob(f.Cell.Scope.CustomerId, "operator", f.Dispatch.Job.Id).State);
    }

    [Fact]
    public async Task Restricted_current_grant_loss_after_HTTP_admission_stays_uncertain_and_does_not_create_success()
    {
        using var f = await BoundNamespaceCoreFixture.Create(); var probe = new HeldOperation("headers", f.Cell.Keys);
        using var handler = new HeldHandler(probe); using var transport = f.Transport(handler); using var stop = new CancellationTokenSource(TimeSpan.FromSeconds(25));
        var pending = transport.SendAsync(f.Wire, stop.Token);
        try
        {
            await probe.Entered.Task.WaitAsync(stop.Token); Busy(f);
            f.Store.SetMembership(f.Cell.Scope.CustomerId, "admin", "approver", "default", "dev", ["Approver"], enabled: false);
            Assert.False(pending.IsCompleted); Busy(f);
        }
        finally { probe.Release.TrySetResult(); }
        var error = await Assert.ThrowsAsync<HttpRequestException>(async () => _ = await pending.WaitAsync(stop.Token));
        Assert.Equal("pipeline-credential-bound-operation-uncertain", error.Message); Assert.Equal(1, probe.Posts); Released(f);
        Assert.Equal("Dispatched", f.Store.GetJob(f.Cell.Scope.CustomerId, "operator", f.Dispatch.Job.Id).State);
    }

    static readonly JsonSerializerOptions Json = new(JsonSerializerDefaults.Web);
    static FoundationCredentialBoundPipelineConsumer Consumer(BoundNamespaceCoreFixture f) => FoundationCredentialBoundPipelineConsumer.ForMockHandlers(
        f.Vault, f.Authority, f.Namespace, f.AuthorityIdentity, f.Instance, f.Binding.Sha256, [f.Cell.Scope],
        () => throw new InvalidOperationException("This metadata-only factory must not run."), f.Cell.Clock);
    static FoundationCredentialBoundTransport Bound(BoundNamespaceCoreFixture f, CredentialVault vault, HttpMessageHandler handler) =>
        FoundationCredentialBoundTransport.ForMockHandler(f.Store, vault, f.Authority, f.Namespace, f.AuthorityIdentity, f.Instance,
            f.Binding.Sha256, f.DispatchPins, f.Verifier, BoundTransportFixture.Runtime, handler, f.Cell.Clock);
    static Task<CredentialLease> ReadWithSession(CredentialVault vault, CredentialScope scope, object session) =>
        (Task<CredentialLease>)Invoke(typeof(CredentialVault).GetMethod("ResolveForDispatchAsync", BindingFlags.Instance | BindingFlags.NonPublic)!, vault,
            [scope, CancellationToken.None, session])!;
    static object? Invoke(MethodInfo method, object? target, object?[] arguments)
    { try { return method.Invoke(target, arguments); } catch (TargetInvocationException error) when (error.InnerException is not null) { ExceptionDispatchInfo.Capture(error.InnerException).Throw(); throw; } }
    static void Busy(BoundNamespaceCoreFixture f)
    {
        var head = f.Authority.ReadCurrent()!;
        Assert.Equal("ActivationAuthorityBusy", Assert.Throws<FoundationStoreException>(() => f.Authority.Activate(head.DatabasePath, head.DatabaseInstanceId, head.Generation, head.Sha256)).Code);
    }
    static void Released(BoundNamespaceCoreFixture f)
    { using var guard = new FileStream(Path.Combine(f.Cell.Root, "restricted-activation", ".activation.guard"), FileMode.Open, FileAccess.ReadWrite, FileShare.None); }
    static string[] Snapshot(params string[] roots)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        var result = new List<string>();
        foreach (var root in roots)
            foreach (var path in new[] { root }.Concat(Directory.EnumerateFileSystemEntries(root, "*", SearchOption.AllDirectories)).Order(StringComparer.Ordinal))
            {
                var directory = Directory.Exists(path); var info = directory ? (FileSystemInfo)new DirectoryInfo(path) : new FileInfo(path);
                var acl = directory ? ((DirectoryInfo)info).GetAccessControl().GetSecurityDescriptorBinaryForm() : ((FileInfo)info).GetAccessControl().GetSecurityDescriptorBinaryForm();
                result.Add(path + "|" + info.Attributes + "|" + Convert.ToHexStringLower(SHA256.HashData(acl)) + "|" +
                    (directory ? "directory" : Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))));
            }
        return result.ToArray();
    }
    static void CopyPrivateRoot(string source, string target)
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException();
        ActivationProvisionFixtureRoots.Create(target);
        foreach (var file in Directory.GetFiles(source))
        {
            var destination = Path.Combine(target, Path.GetFileName(file)); File.Copy(file, destination);
            new FileInfo(destination).SetAccessControl(new FileInfo(file).GetAccessControl());
        }
    }
    sealed class PrivateProviders : ICredentialGitHubAppKeyProvider, ICredentialEntraAssertionProvider
    {
        public int PrivateKeys, Assertions;
        public ValueTask<CredentialSecret> GetPrivateKeyAsync(CredentialScope scope, CancellationToken cancellationToken)
        { PrivateKeys++; return ValueTask.FromResult(CredentialSecret.FromUtf8("synthetic-unread-private-material")); }
        public ValueTask<CredentialSecret> GetAssertionAsync(CredentialScope scope, string tenantId, string clientId, CancellationToken cancellationToken)
        { Assertions++; return ValueTask.FromResult(CredentialSecret.FromUtf8("synthetic-unread-assertion")); }
    }
    sealed class HostilePermits(BoundNamespaceCoreFixture f, string mode) : ICredentialDispatchPermitSource
    {
        public int Calls;
        public Task<CredentialDispatchPermit?> AuthorizeAsync(PipelineWireRequest wire, CancellationToken cancellationToken)
        {
            Calls++; if (mode == "throw") throw new InvalidOperationException(CredentialFixture.Token);
            return Task.FromResult(mode == "null" ? null : CredentialDispatchPermit.FromDurableDispatch(f.Dispatch, wire, f.Cell.Scope, f.Cell.Clock.Now));
        }
    }
    sealed class HeldOperation(string phase, ICredentialKeyProvider keys) : ICredentialKeyProvider
    {
        public string Phase => phase;
        public int Posts, KeyCalls;
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customerId, string cellId, string keyId, CancellationToken cancellationToken)
        { Interlocked.Increment(ref KeyCalls); Entered.TrySetResult(); await Release.Task; return await keys.GetKeyAsync(customerId, cellId, keyId, cancellationToken); }
    }
    sealed class HeldHandler(HeldOperation probe) : HttpMessageHandler
    {
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            probe.Posts++; if (probe.Phase == "headers") { probe.Entered.TrySetResult(); await probe.Release.Task; }
            return new(HttpStatusCode.OK) { Content = new StreamContent(new HeldBody(probe)) };
        }
    }
    sealed class HeldBody(HeldOperation probe) : Stream
    {
        readonly byte[] bytes = Encoding.UTF8.GetBytes("{}"); int offset;
        public override bool CanRead => true; public override bool CanSeek => false; public override bool CanWrite => false;
        public override long Length => throw new NotSupportedException(); public override long Position { get => offset; set => throw new NotSupportedException(); }
        public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
        {
            if (offset == 0 && probe.Phase == "body") { probe.Entered.TrySetResult(); await probe.Release.Task; }
            var count = Math.Min(buffer.Length, bytes.Length - offset); bytes.AsMemory(offset, count).CopyTo(buffer); offset += count; return count;
        }
        public override Task<int> ReadAsync(byte[] buffer, int start, int count, CancellationToken cancellationToken) => ReadAsync(buffer.AsMemory(start, count), cancellationToken).AsTask();
        public override async ValueTask DisposeAsync() { if (probe.Phase == "disposal") { probe.Entered.TrySetResult(); await probe.Release.Task; } Dispose(); GC.SuppressFinalize(this); }
        public override int Read(byte[] buffer, int start, int count) => throw new NotSupportedException(); public override void Flush() { }
        public override long Seek(long value, SeekOrigin origin) => throw new NotSupportedException(); public override void SetLength(long value) => throw new NotSupportedException();
        public override void Write(byte[] buffer, int start, int count) => throw new NotSupportedException();
    }
}
