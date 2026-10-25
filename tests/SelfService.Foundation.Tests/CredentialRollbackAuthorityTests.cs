using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json.Nodes;
using System.Net;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class CredentialRollbackAuthorityTests
{
    [WindowsCredentialFact]
    public async Task Strong_mode_is_fresh_only_cannot_be_downgraded_and_preserves_default_namespaces()
    {
        using var f = new StrongFixture();
        Assert.Throws<CredentialException>(() => new CredentialVault(f.Options with { RollbackProtection = null }, f.Cell.Keys, f.Cell.Clock));
        Assert.Throws<CredentialException>(() => new CredentialVault(f.Cell.Options with { RollbackProtection = f.Options.RollbackProtection }, f.Cell.Keys));
        Assert.Throws<CredentialException>(() => CredentialVault.Provision(f.Options, f.Cell.Keys));
        Assert.Throws<CredentialException>(() => CredentialVault.ProvisionStrong(f.Options, f.Cell.Keys));
        Assert.Throws<CredentialException>(() => CredentialVault.ProvisionStrong(f.Cell.Options, f.Cell.Keys));
        await f.Store(); var current = await f.Vault.InspectRollbackStateAsync(f.Scope.AuthProfileId);
        Assert.Equal(1, current.CommittedGeneration); Assert.Equal(2, current.AuthorityRevision); Assert.Null(current.Pending);
        Assert.Equal(Hash(File.ReadAllBytes(f.Profile)), current.CommittedEnvelopeSha256);
        await f.Cell.StoreToken(); using var defaultLease = await f.Cell.Reopen().ResolveAsync(f.Scope); Assert.Equal(1, defaultLease.Profile.Generation);
        await Assert.ThrowsAsync<CredentialException>(() => f.Cell.Vault.InspectRollbackStateAsync(f.Scope.AuthProfileId));
    }

    [Theory]
    [InlineData("revoke")]
    [InlineData("replace")]
    [InlineData("rewrap")]
    public async Task Restoring_an_older_valid_envelope_does_not_restore_credential_authority(string operation)
    {
        using var f = new StrongFixture(); await f.Store(); var old = File.ReadAllBytes(f.Profile);
        if (operation == "revoke") await f.Vault.RevokeAsync(f.Scope.AuthProfileId, 1);
        else if (operation == "rewrap")
        {
            f.Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32);
            await new CredentialVault(f.Options with { EncryptionKeyId = "key-2" }, f.Cell.Keys, f.Cell.Clock).RewrapAsync(f.Scope.AuthProfileId, 1, "key-1");
        }
        else { using var secret = CredentialSecret.FromUtf8("synthetic-replaced-opaque-material"); await f.Vault.PutAsync(f.Scope, secret, f.Cell.Clock.Now.AddHours(1), 1); }
        var state = File.ReadAllBytes(f.State); File.WriteAllBytes(f.Profile, old); f.Cell.Keys.Requests.Clear();
        Assert.Equal("credential-rollback-envelope-mismatch", (await Assert.ThrowsAsync<CredentialException>(() => f.Reopen().ResolveAsync(f.Scope))).Code);
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(state, File.ReadAllBytes(f.State)); Assert.Equal(old, File.ReadAllBytes(f.Profile));
    }

    [WindowsCredentialFact]
    public async Task Missing_tracked_file_or_same_generation_substitution_is_not_adopted_as_a_fresh_profile()
    {
        using var f = new StrongFixture(); await f.Store(); var original = File.ReadAllBytes(f.Profile);
        await f.Cell.StoreToken(); var other = Assert.Single(Directory.GetFiles(f.Cell.VaultRoot, "*.credential.json"));
        File.WriteAllBytes(f.Profile, File.ReadAllBytes(other));
        Assert.Equal("credential-rollback-envelope-mismatch", (await Assert.ThrowsAsync<CredentialException>(() => f.Vault.GetProfileAsync(f.Scope.AuthProfileId))).Code);
        File.Delete(f.Profile); var state = File.ReadAllBytes(f.State); using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token);
        await Assert.ThrowsAsync<CredentialException>(() => f.Vault.PutAsync(f.Scope, secret, f.Cell.Clock.Now.AddHours(1)));
        Assert.False(File.Exists(f.Profile)); Assert.Equal(state, File.ReadAllBytes(f.State));
        File.WriteAllBytes(f.Profile, original);
    }

    [Theory]
    [InlineData("PendingPersisted", CredentialRollbackResolution.AbandonUnpublished)]
    [InlineData("EnvelopePublished", CredentialRollbackResolution.AcceptPublished)]
    public async Task Interrupted_writes_require_exact_explicit_old_or_new_reconciliation(string phase, CredentialRollbackResolution correct)
    {
        using var f = new StrongFixture(); await f.Store(); var old = File.ReadAllBytes(f.Profile);
        Fault(f.Vault, p => { if (p == phase) throw new IOException("synthetic interrupted write"); });
        await Assert.ThrowsAsync<IOException>(() => f.Vault.RevokeAsync(f.Scope.AuthProfileId, 1)); Fault(f.Vault, null);
        var status = await f.Reopen().InspectRollbackStateAsync(f.Scope.AuthProfileId); Assert.NotNull(status.Pending); Assert.Equal(3, status.AuthorityRevision);
        Assert.Equal("credential-rollback-reconciliation-required", (await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ResolveAsync(f.Scope))).Code);
        var state = File.ReadAllBytes(f.State); var bytes = File.ReadAllBytes(f.Profile);
        await Assert.ThrowsAsync<CredentialException>(() => f.Reconcile(status with { AuthorityRevision = 1 }, correct));
        await Assert.ThrowsAsync<CredentialException>(() => f.Reconcile(status, correct == CredentialRollbackResolution.AcceptPublished ? CredentialRollbackResolution.AbandonUnpublished : CredentialRollbackResolution.AcceptPublished));
        Assert.Equal(state, File.ReadAllBytes(f.State)); Assert.Equal(bytes, File.ReadAllBytes(f.Profile));
        var done = await f.Reconcile(status, correct); Assert.Null(done.Pending); Assert.Equal(4, done.AuthorityRevision);
        var profile = await f.Vault.GetProfileAsync(f.Scope.AuthProfileId);
        Assert.Equal(phase == "EnvelopePublished" ? 2 : 1, profile.Generation); Assert.Equal(phase != "EnvelopePublished", profile.Enabled);
        if (phase == "PendingPersisted") Assert.Equal(old, File.ReadAllBytes(f.Profile));
    }

    [WindowsCredentialFact]
    public async Task Prior_absence_can_only_be_reconciled_as_true_absence_and_never_as_a_target_directory()
    {
        using var f = new StrongFixture(); Fault(f.Vault, p => { if (p == "PendingPersisted") throw new IOException("synthetic interrupted create"); });
        await Assert.ThrowsAsync<IOException>(f.Store); Fault(f.Vault, null); var status = await f.Vault.InspectRollbackStateAsync(f.Scope.AuthProfileId);
        Assert.Equal(0, status.Pending!.PreviousGeneration); Assert.False(Path.Exists(f.Profile)); Directory.CreateDirectory(f.Profile);
        var state = File.ReadAllBytes(f.State);
        Assert.Equal("credential-rollback-vault-contents-invalid", (await Assert.ThrowsAsync<CredentialException>(() => f.Reconcile(status, CredentialRollbackResolution.AbandonUnpublished))).Code);
        Assert.True(Directory.Exists(f.Profile)); Assert.Equal(state, File.ReadAllBytes(f.State)); Directory.Delete(f.Profile);
        var resolved = await f.Reconcile(status, CredentialRollbackResolution.AbandonUnpublished); Assert.Equal(0, resolved.CommittedGeneration);
        await f.Store(); Assert.Equal(1, (await f.Vault.GetProfileAsync(f.Scope.AuthProfileId)).Generation);
    }

    [Theory]
    [InlineData("authority")]
    [InlineData("state-copy")]
    [InlineData("unknown-authority-file")]
    [InlineData("unknown-vault-file")]
    [InlineData("oversized-state")]
    public async Task Missing_wrong_or_unknown_authority_state_and_untracked_vault_data_fail_without_repair(string mutation)
    {
        using var f = new StrongFixture(); await f.Store(); var original = File.ReadAllBytes(f.Profile); var state = File.ReadAllBytes(f.State);
        if (mutation == "authority") Directory.Move(f.Authority, f.Authority + "-preserved");
        else if (mutation == "state-copy") { using var other = new StrongFixture(); File.WriteAllBytes(f.State, File.ReadAllBytes(other.State)); }
        else if (mutation == "unknown-authority-file") File.WriteAllText(Path.Combine(f.Authority, "unexpected.json"), "synthetic preserved state");
        else if (mutation == "oversized-state") File.WriteAllText(f.State, new string('x', 1048577));
        else File.WriteAllText(Path.Combine(f.Options.RootPath, "unexpected.txt"), "synthetic preserved state");
        var after = File.Exists(f.State) ? File.ReadAllBytes(f.State) : state; f.Cell.Keys.Requests.Clear();
        await Assert.ThrowsAsync<CredentialException>(async () => { using var lease = await f.Reopen().ResolveAsync(f.Scope); });
        Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(original, File.ReadAllBytes(f.Profile));
        if (File.Exists(f.State)) Assert.Equal(after, File.ReadAllBytes(f.State));
    }

    [WindowsCredentialFact]
    public async Task Two_competing_reconcilers_admit_one_exact_pending_decision()
    {
        using var f = new StrongFixture(); await f.Store(); Fault(f.Vault, p => { if (p == "EnvelopePublished") throw new IOException("synthetic stop"); });
        await Assert.ThrowsAsync<IOException>(() => f.Vault.RevokeAsync(f.Scope.AuthProfileId, 1)); Fault(f.Vault, null);
        var status = await f.Vault.InspectRollbackStateAsync(f.Scope.AuthProfileId); var second = f.Reopen();
        async Task<bool> Attempt(CredentialVault vault)
        {
            try { await vault.ReconcilePendingWriteAsync(f.Scope.AuthProfileId, status.AuthorityRevision, status.AuthorityStateSha256, status.Pending!.PendingSha256, CredentialRollbackResolution.AcceptPublished); return true; }
            catch (CredentialException e) when (e.Code == "credential-rollback-state-changed") { return false; }
        }
        Assert.Single(await Task.WhenAll(Attempt(f.Vault), Attempt(second)), success => success);
        Assert.Equal(2, (await f.Vault.GetProfileAsync(f.Scope.AuthProfileId)).Generation);
    }

    [WindowsCredentialFact]
    public async Task Caller_cancellation_after_pending_does_not_automatically_abandon_a_revocation()
    {
        using var f = new StrongFixture(); await f.Store(); var old = File.ReadAllBytes(f.Profile); using var cancellation = new CancellationTokenSource();
        Fault(f.Vault, p => { if (p == "PendingPersisted") cancellation.Cancel(); });
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Vault.RevokeAsync(f.Scope.AuthProfileId, 1, cancellation.Token));
        Assert.Equal(cancellation.Token, error.CancellationToken); Fault(f.Vault, null); Assert.Equal(old, File.ReadAllBytes(f.Profile));
        var state = await f.Vault.InspectRollbackStateAsync(f.Scope.AuthProfileId); Assert.NotNull(state.Pending);
        await Assert.ThrowsAsync<CredentialException>(() => f.Vault.GetProfileAsync(f.Scope.AuthProfileId));
        await f.Reconcile(state, CredentialRollbackResolution.AbandonUnpublished);
    }

    [WindowsCredentialFact]
    public async Task Failed_or_cancelled_destination_key_resolution_does_not_publish_pending_state_or_hold_guards()
    {
        using var f = new StrongFixture(); await f.Store(); var state = File.ReadAllBytes(f.State); var old = File.ReadAllBytes(f.Profile);
        var destination = new CredentialVault(f.Options with { EncryptionKeyId = "key-2" }, f.Cell.Keys, f.Cell.Clock);
        await Assert.ThrowsAsync<CredentialException>(() => destination.RewrapAsync(f.Scope.AuthProfileId, 1, "key-1"));
        Assert.Equal(state, File.ReadAllBytes(f.State)); Assert.Equal(old, File.ReadAllBytes(f.Profile));
        f.Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32); using var cancellation = new CancellationTokenSource();
        f.Cell.Keys.OnGet = () => { if (f.Cell.Keys.Requests[^1].KeyId == "key-2") cancellation.Cancel(); };
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => destination.RewrapAsync(f.Scope.AuthProfileId, 1, "key-1", cancellation.Token));
        f.Cell.Keys.OnGet = null; Assert.Equal(state, File.ReadAllBytes(f.State)); Assert.Equal(old, File.ReadAllBytes(f.Profile));
        Assert.Equal(2, (await f.Vault.RevokeAsync(f.Scope.AuthProfileId, 1)).Generation);
    }

    [WindowsCredentialFact]
    public async Task Strong_dispatch_admission_orders_revocation_without_holding_the_authority_for_remote_completion()
    {
        using var f = new StrongFixture(); await f.Store(); var writer = f.Reopen();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var allow = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var permits = new HeldPermits(f.Cell, entered, allow); var handler = new HeldHandler();
        using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, permits, handler, f.Cell.Clock);
        var sending = transport.SendAsync(f.Cell.Wire, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var revoke = writer.RevokeAsync(f.Scope.AuthProfileId, 1); await Task.Delay(80);
            Assert.False(revoke.IsCompleted); Assert.False(handler.Admitted.Task.IsCompleted); allow.TrySetResult();
            await handler.Admitted.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False((await revoke.WaitAsync(TimeSpan.FromSeconds(5))).Enabled);
            Assert.False(sending.IsCompleted); Assert.Equal(2, (await writer.InspectRollbackStateAsync(f.Scope.AuthProfileId)).CommittedGeneration);
        }
        finally { allow.TrySetResult(); handler.Release.TrySetResult(); await sending; }
    }

    [WindowsCredentialFact]
    public async Task Strong_profile_capacity_rejects_new_admission_without_publishing_or_rewriting_state()
    {
        using var f = new StrongFixture(); using var material = CredentialSecret.FromUtf8(CredentialFixture.Token);
        for (var i = 0; i < 256; i++) await f.Vault.PutAsync(f.Scope with { AuthProfileId = "profile/capacity-" + i }, material, f.Cell.Clock.Now.AddHours(1));
        var before = File.ReadAllBytes(f.State);
        Assert.Equal("credential-rollback-profile-limit", (await Assert.ThrowsAsync<CredentialException>(() =>
            f.Vault.PutAsync(f.Scope with { AuthProfileId = "profile/over-capacity" }, material, f.Cell.Clock.Now.AddHours(1)))).Code);
        Assert.Equal(before, File.ReadAllBytes(f.State)); Assert.Equal(256, Directory.GetFiles(f.Options.RootPath, "*.credential.json").Length);
        Assert.Equal(2, (await f.Vault.RevokeAsync("profile/capacity-0", 1)).Generation);
    }

    [Theory]
    [InlineData("PendingPersisted", CredentialRollbackResolution.AbandonUnpublished)]
    [InlineData("EnvelopePublished", CredentialRollbackResolution.AcceptPublished)]
    public async Task An_owned_process_interrupted_at_publication_boundaries_reopens_pending_until_review(string phase, CredentialRollbackResolution resolution)
    {
        using var f = new StrongFixture(native: true); await f.Store(); var ready = Path.Combine(f.Cell.Root, "ready-" + Guid.NewGuid().ToString("N"));
        using var process = StartInterruptedWriter(f, phase, ready); var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(20));
            while (!File.Exists(ready))
            {
                Assert.False(process.HasExited, process.HasExited ? await errors : "Owned process exited before ready.");
                await Task.Delay(25, deadline.Token);
            }
            Assert.Equal(process.Id.ToString(), File.ReadAllText(ready)); process.Kill(); await process.WaitForExitAsync();
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
        _ = await output; _ = await errors;
        var status = await f.Reopen().InspectRollbackStateAsync(f.Scope.AuthProfileId); Assert.NotNull(status.Pending);
        await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ResolveAsync(f.Scope));
        await f.Reconcile(status, resolution); Assert.Equal(phase == "EnvelopePublished" ? 2 : 1, (await f.Vault.GetProfileAsync(f.Scope.AuthProfileId)).Generation);
    }

    static void Fault(CredentialVault vault, Action<string>? hook) => typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(vault, hook);
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    static Process StartInterruptedWriter(StrongFixture f, string phase, string ready)
    {
        var executable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.Combine(p.Trim('"'), "pwsh.exe")).First(File.Exists);
        static string Q(string value) => "'" + value.Replace("'", "''") + "'";
        var script = "$ErrorActionPreference='Stop';[void][Reflection.Assembly]::LoadFrom(" + Q(typeof(CredentialVault).Assembly.Location) + ");" +
            "Add-Type -TypeDefinition 'using System; using System.IO; using System.Threading; using System.Diagnostics; public static class OwnedRollbackPause { public static Action<string> Build(string phase,string ready) { return p => {if(p==phase) {File.WriteAllText(ready,Process.GetCurrentProcess().Id.ToString());Thread.Sleep(60000);}};}}';" +
            "$o=[SelfService.Foundation.CredentialVaultOptions]::new(" + Q(f.Options.RootPath) + "," + Q(f.Scope.CustomerId) + "," + Q(f.Scope.CellId) + ",'key-1');" +
            "$o.RollbackProtection=[SelfService.Foundation.CredentialRollbackProtectionOptions]::new(" + Q(f.Authority) + ");" +
            "$k=[SelfService.Foundation.CredentialWindowsUserKeyProvider]::new(" + Q(Path.Combine(f.Cell.Root, "rollback-keys")) + "," + Q(f.Scope.CustomerId) + "," + Q(f.Scope.CellId) + ");" +
            "$v=[SelfService.Foundation.CredentialVault]::new($o,$k,$null);" +
            "[SelfService.Foundation.CredentialVault].GetProperty('RollbackFault',[Reflection.BindingFlags]'Instance,NonPublic').SetValue($v,[OwnedRollbackPause]::Build(" + Q(phase) + "," + Q(ready) + "));" +
            "$v.RevokeAsync(" + Q(f.Scope.AuthProfileId) + ",1,[Threading.CancellationToken]::None).GetAwaiter().GetResult()|Out-Null";
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = f.Cell.Root };
        start.Environment.Clear(); foreach (var name in new[] { "SystemRoot", "WINDIR" }) if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["TEMP"] = f.Cell.Root; start.Environment["TMP"] = f.Cell.Root; start.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(executable)!, "Modules");
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script))); return Process.Start(start)!;
    }
    sealed class StrongFixture : IDisposable
    {
        public CredentialFixture Cell { get; } = new();
        public CredentialScope Scope => Cell.Scope;
        public string Authority => Path.Combine(Cell.Root, "rollback-authority");
        public string State => Path.Combine(Authority, "state.json");
        public string Profile => Path.Combine(Options.RootPath, Hash(Encoding.UTF8.GetBytes(Scope.AuthProfileId)) + ".credential.json");
        public CredentialVaultOptions Options { get; }
        readonly ICredentialKeyProvider keys;
        readonly CredentialWindowsUserKeyProvider? nativeKeys;
        bool nativeKeyProvisioned;
        public CredentialVault Vault { get; }
        public StrongFixture(bool native = false)
        {
            if (native)
            {
                nativeKeys = Cell.ProvisionNativeKeys("rollback-keys"); keys = nativeKeys;
            }
            else keys = Cell.Keys;
            Options = Cell.Options with { RootPath = Path.Combine(Cell.Root, "strong-vault"), RollbackProtection = new(Authority) };
            Vault = CredentialVault.ProvisionStrong(Options, keys, Cell.Clock);
        }
        public CredentialVault Reopen() => new(Options, keys, Cell.Clock);
        public async Task Store()
        {
            if (nativeKeys is not null && !nativeKeyProvisioned)
            { using (await nativeKeys.ProvisionKeyAsync(Scope.CustomerId, Scope.CellId, "key-1")) { } nativeKeyProvisioned = true; }
            using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token); await Vault.PutAsync(Scope, secret, Cell.Clock.Now.AddHours(1));
        }
        public Task<CredentialRollbackStatus> Reconcile(CredentialRollbackStatus status, CredentialRollbackResolution resolution) => Vault.ReconcilePendingWriteAsync(Scope.AuthProfileId,
            status.AuthorityRevision, status.AuthorityStateSha256, status.Pending!.PendingSha256, resolution);
        public void Dispose() => Cell.Dispose();
    }
    sealed class HeldPermits(CredentialFixture cell, TaskCompletionSource entered, TaskCompletionSource allow) : ICredentialDispatchPermitSource
    {
        int calls;
        public async Task<CredentialDispatchPermit?> AuthorizeAsync(PipelineWireRequest wire, CancellationToken cancellationToken)
        {
            if (Interlocked.Increment(ref calls) == 2) { entered.TrySetResult(); await allow.Task.WaitAsync(cancellationToken); }
            return CredentialDispatchPermit.FromDurableDispatch(cell.Dispatch, wire, cell.Scope, cell.Clock.Now);
        }
    }
    sealed class HeldHandler : HttpMessageHandler
    {
        public TaskCompletionSource Admitted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        protected override async Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        {
            Admitted.TrySetResult(); await Release.Task.WaitAsync(cancellationToken);
            return new(HttpStatusCode.OK) { Content = new StringContent("{\"workflow_run_id\":123,\"run_url\":\"https://api.github.com/repos/customer-org/customer-repo/actions/runs/123\",\"html_url\":\"https://github.com/customer-org/customer-repo/actions/runs/123\"}") };
        }
    }
}
