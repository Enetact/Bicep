using System.Reflection;
using System.Diagnostics;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationCredentialMaintenanceExecutorTests
{
    [Theory]
    [InlineData(false, CredentialMaintenanceOperation.Revoke)]
    [InlineData(false, CredentialMaintenanceOperation.Rewrap)]
    [InlineData(true, CredentialMaintenanceOperation.Revoke)]
    [InlineData(true, CredentialMaintenanceOperation.Rewrap)]
    public async Task Exact_reviewed_maintenance_has_one_scoped_audited_result_and_preserves_token_scope_and_expiry(bool strong, CredentialMaintenanceOperation operation)
    {
        using var f = new MaintenanceExecutorFixture(strong); await f.Initialize(); var before = await f.Source.GetProfileAsync(f.Cell.Scope.AuthProfileId);
        var intent = await f.Plan(operation); var result = await f.Execute(intent);
        Assert.Equal("Succeeded", result.Execution!.State); Assert.NotNull(result.Execution.ResultSha256);
        var after = await f.Vault.GetProfileAsync(f.Cell.Scope.AuthProfileId);
        Assert.Equal(JsonSerializer.Serialize(before with { Generation = 2, Enabled = operation != CredentialMaintenanceOperation.Revoke }), JsonSerializer.Serialize(after));
        Assert.Equal("key-2", (await f.Vault.InspectMaintenanceAsync(f.Cell.Scope.AuthProfileId)).SourceKeyId);
        Assert.Equal(result.Execution.ExpectedAfterSha256, f.EnvelopeHash()); Assert.Equal(1, f.Count("CredentialMaintenanceAdmitted")); Assert.Equal(1, f.Count("CredentialMaintenanceCompleted"));
        Assert.Equal(strong ? 4 : 0, result.Execution.Result!.AuthorityRevision);
        Assert.DoesNotContain(CredentialFixture.Token, JsonSerializer.Serialize(result)); Assert.DoesNotContain(f.Cell.Root, JsonSerializer.Serialize(result));
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Execute(intent)); Assert.Equal(1, f.Count("CredentialMaintenanceAdmitted")); Assert.Equal(2, after.Generation);
        if (operation == CredentialMaintenanceOperation.Rewrap)
        {
            var handler = new RecordedHandler(); using var transport = PipelineAuthenticatedTransport.ForMockHandler(f.Vault, new FixturePermit(f.Cell), handler, f.Cell.Clock);
            await transport.SendAsync(f.Cell.Wire, default); Assert.Equal("Bearer " + CredentialFixture.Token, handler.Authorization);
        }
        else await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ResolveAsync(f.Cell.Scope));
    }

    [WindowsCredentialFact]
    public async Task Metadata_observations_are_authenticated_immutable_and_do_not_grant_execution_or_reveal_material()
    {
        using var f = new MaintenanceExecutorFixture(true); await f.Initialize(); var before = f.VaultBytes();
        var observation = await f.Vault.InspectMaintenanceAsync(f.Cell.Scope.AuthProfileId); var identity = f.Vault.DescribeNamespace();
        Assert.Equal(identity.VaultNamespaceId, observation.VaultNamespaceId); Assert.Equal(identity.NamespaceCommitmentSha256, observation.NamespaceCommitmentSha256);
        Assert.NotNull(identity.AuthorityNamespaceId); Assert.NotNull(identity.AuthorityBindingSha256); Assert.Equal(2, observation.AuthorityRevision);
        var json = JsonSerializer.Serialize(new { observation, identity }); Assert.DoesNotContain(CredentialFixture.Token, json); Assert.DoesNotContain(f.Cell.Root, json);
        Assert.Equal(before, f.VaultBytes()); Assert.Equal(0, f.Scalar("SELECT count(*) FROM credential_maintenance_executions"));
        var path = f.ProfilePath; var text = await File.ReadAllTextAsync(path); var node = System.Text.Json.Nodes.JsonNode.Parse(text)!;
        node["profile"]!["enabled"] = false; await File.WriteAllTextAsync(path, node.ToJsonString()); var modified = await File.ReadAllBytesAsync(path);
        await Assert.ThrowsAsync<CredentialException>(() => f.Vault.InspectMaintenanceAsync(f.Cell.Scope.AuthProfileId)); Assert.Equal(modified, await File.ReadAllBytesAsync(path));
    }

    [Theory]
    [InlineData("generation")]
    [InlineData("envelope")]
    [InlineData("source-key")]
    [InlineData("destination-key")]
    [InlineData("namespace")]
    [InlineData("commitment")]
    [InlineData("enabled")]
    [InlineData("expiry")]
    public async Task Approved_caller_pins_are_constraints_and_cannot_substitute_for_actual_vault_metadata(string field)
    {
        using var f = new MaintenanceExecutorFixture(); await f.Initialize(); var target = await f.Vault.InspectMaintenanceAsync(f.Cell.Scope.AuthProfileId);
        target = field switch
        {
            "generation" => target with { ProfileGeneration = target.ProfileGeneration + 1 }, "envelope" => target with { EnvelopeSha256 = new string('a', 64) },
            "source-key" => target with { SourceKeyId = "other-source" }, "destination-key" => target with { DestinationKeyId = "other-destination" },
            "namespace" => target with { VaultNamespaceId = Guid.NewGuid().ToString("N") }, "commitment" => target with { NamespaceCommitmentSha256 = new string('b', 64) },
            "enabled" => target with { Enabled = false }, _ => target with { ExpiresUnixMilliseconds = target.ExpiresUnixMilliseconds + 1 }
        };
        var intent = await f.Plan(CredentialMaintenanceOperation.Revoke, target); var bytes = f.VaultBytes();
        await Assert.ThrowsAsync<CredentialException>(() => f.Execute(intent)); Assert.Equal(bytes, f.VaultBytes()); Assert.Null((await f.Read(intent.Id)).Execution);
        Assert.Equal(0, f.Count("CredentialMaintenanceAdmitted")); Assert.Equal(1, f.Count("CredentialMaintenanceDenied"));
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("approver")]
    [InlineData("connection")]
    [InlineData("policy")]
    public async Task Authority_changed_during_asynchronous_destination_lookup_is_rechecked_before_any_vault_or_highwater_write(string authority)
    {
        using var f = new MaintenanceExecutorFixture(true); await f.Initialize(); var intent = await f.Plan(); var bytes = f.VaultBytes();
        var blocking = new BlockingKeys(f.Cell.Keys); var vault = new CredentialVault(f.Options, blocking, f.Cell.Clock); var executor = f.Executor(vault);
        var context = await f.Context(FoundationCapabilities.CredentialMaintenanceExecute); var executing = executor.ExecuteAsync(context, intent.Id, intent.Digest);
        try
        {
            await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(executing.IsCompleted); Assert.Equal(bytes, f.VaultBytes());
            if (authority is "operator" or "approver") f.Store.SetMembership(f.Cell.Scope.CustomerId, "admin", authority == "operator" ? MaintenanceExecutorFixture.Operator : MaintenanceExecutorFixture.Approver,
                "default", "dev", [authority == "operator" ? "Operator" : "Approver"], false);
            else if (authority == "connection")
            { var connection = f.Store.GetConnection(f.Cell.Scope.CustomerId, "admin", f.Cell.Scope.ConnectionId); f.Store.PutConnection(f.Cell.Scope.CustomerId, "admin", connection.Id, connection.Provider, connection.ConfigurationJson); }
            else f.Store.SetCustomerEnabled(f.Cell.Scope.CustomerId, "admin", false);
        }
        finally { blocking.Release.TrySetResult(); }
        await Assert.ThrowsAsync<FoundationStoreException>(() => executing); Assert.Equal(bytes, f.VaultBytes()); Assert.Equal(0, f.Count("CredentialMaintenanceAdmitted")); Assert.Null((await f.Read(intent.Id)).Execution);
        Assert.True(f.Count("CredentialMaintenanceDenied") >= 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Cancellation_or_deadline_during_key_preparation_is_definite_preadmission_and_releases_the_guard(bool timeout)
    {
        using var f = new MaintenanceExecutorFixture(true); await f.Initialize(); var intent = await f.Plan(); var bytes = f.VaultBytes();
        var blocking = new BlockingKeys(f.Cell.Keys); var vault = new CredentialVault(f.Options, blocking, f.Cell.Clock);
        // Leave enough budget for native custody/SQLite preflight under full-suite load;
        // the deadline must expire inside the deliberately blocked provider, before admission.
        var executor = f.Executor(vault, timeout ? TimeSpan.FromSeconds(3) : null); using var cancellation = new CancellationTokenSource();
        var executing = executor.ExecuteAsync(await f.Context(FoundationCapabilities.CredentialMaintenanceExecute), intent.Id, intent.Digest, cancellation.Token);
        try
        {
            await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); if (!timeout) cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => executing);
        }
        finally { blocking.Release.TrySetResult(); }
        Assert.Equal(bytes, f.VaultBytes()); Assert.Null((await f.Read(intent.Id)).Execution); Assert.Equal(0, f.Count("CredentialMaintenanceAdmitted"));
        Assert.Equal("Succeeded", (await f.Execute(intent)).Execution!.State);
    }

    [Theory]
    [InlineData("key-1")]
    [InlineData("key-2")]
    public async Task Missing_source_or_destination_key_is_a_redacted_preadmission_refusal_without_consuming_review(string key)
    {
        using var f = new MaintenanceExecutorFixture(true); await f.Initialize(); var intent = await f.Plan(); var bytes = f.VaultBytes();
        var held = f.Cell.Keys.Values[key]; f.Cell.Keys.Values.Remove(key);
        var error = await Assert.ThrowsAsync<CredentialException>(() => f.Execute(intent)); Assert.Equal("credential-key-unavailable", error.Code);
        Assert.DoesNotContain(CredentialFixture.Token, error.ToString()); Assert.Equal(bytes, f.VaultBytes()); Assert.Null((await f.Read(intent.Id)).Execution);
        Assert.Equal(0, f.Count("CredentialMaintenanceAdmitted")); f.Cell.Keys.Values[key] = held;
        Assert.Equal("Succeeded", (await f.Execute(intent)).Execution!.State);
    }

    [WindowsCredentialFact]
    public async Task Runtime_activation_revoked_during_lookup_prevents_admission_and_any_vault_write()
    {
        using var f = new MaintenanceExecutorFixture(true); await f.Initialize(); var intent = await f.Plan(); var bytes = f.VaultBytes(); var active = true;
        var guarded = new FoundationStore(Path.Combine(f.Cell.Root, "cell.db"), f.Cell.Scope.CellId, f.Cell.Scope.CustomerId, f.Cell.Clock,
            initializeSchema: false, activationGuard: (_, _) => { if (!active) throw new FoundationStoreException("SyntheticActivationRevoked", 403); });
        var blocking = new BlockingKeys(f.Cell.Keys); var vault = new CredentialVault(f.Options, blocking, f.Cell.Clock);
        var executor = new FoundationCredentialMaintenanceExecutor(guarded, vault, vault.DescribeNamespace(), f.Instance);
        var executing = executor.ExecuteAsync(await f.Context(FoundationCapabilities.CredentialMaintenanceExecute), intent.Id, intent.Digest);
        try { await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); active = false; }
        finally { blocking.Release.TrySetResult(); }
        Assert.Equal("SyntheticActivationRevoked", (await Assert.ThrowsAsync<FoundationStoreException>(() => executing)).Code);
        Assert.Equal(bytes, f.VaultBytes()); Assert.Null((await f.Read(intent.Id)).Execution); Assert.Equal(0, f.Count("CredentialMaintenanceAdmitted"));
    }

    [Theory]
    [InlineData("Suspend")]
    [InlineData("BeginOffboarding")]
    public async Task Inactive_human_revocation_finishes_without_reactivation_while_rewrap_is_refused(string action)
    {
        using var f = new MaintenanceExecutorFixture(true); await f.Initialize();
        var change = f.Store.PrepareLifecycleChange(f.Cell.Scope.CustomerId, "admin", action, "Review synthetic inactive revocation");
        f.Store.ApproveAdministrativePlan(f.Cell.Scope.CustomerId, MaintenanceExecutorFixture.Approver, change.Id, change.Digest);
        f.Store.ExecuteLifecycleChange(f.Cell.Scope.CustomerId, "admin", change.Id, change.Digest);
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Plan());
        var intent = await f.Plan(CredentialMaintenanceOperation.Revoke); var result = await f.Execute(intent);
        Assert.Equal("Succeeded", result.Execution!.State); Assert.False((await f.Vault.GetProfileAsync(f.Cell.Scope.AuthProfileId)).Enabled);
        Assert.False(f.Store.CustomerLifecycle(f.Cell.Scope.CustomerId, "admin").Enabled);
    }

    [Theory]
    [InlineData(false, "AfterAdmission")]
    [InlineData(false, "AfterMutation")]
    [InlineData(true, "AfterAdmission")]
    [InlineData(true, "AfterMutation")]
    public async Task Any_interruption_after_one_use_admission_remains_unknown_and_cannot_retry(bool strong, string phase)
    {
        using var f = new MaintenanceExecutorFixture(strong); await f.Initialize(); var intent = await f.Plan(CredentialMaintenanceOperation.Revoke); var before = f.EnvelopeHash();
        var executor = f.Executor(); Fault(executor, p => { if (p == phase) throw new IOException("synthetic interruption"); });
        var outcome = await executor.ExecuteAsync(await f.Context(FoundationCapabilities.CredentialMaintenanceExecute), intent.Id, intent.Digest);
        Assert.Equal("OutcomeUnknown", outcome.Execution!.State); Assert.Null(outcome.Execution.Result); Assert.Equal(1, f.Count("CredentialMaintenanceAdmitted"));
        Assert.Equal(phase == "AfterAdmission" ? before : outcome.Execution.ExpectedAfterSha256, f.EnvelopeHash());
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Execute(intent)); Assert.Equal(1, f.Count("CredentialMaintenanceAdmitted"));
    }

    [Theory]
    [InlineData("PendingPersisted")]
    [InlineData("EnvelopePublished")]
    public async Task Interrupted_strong_write_is_unknown_and_leaves_the_existing_explicit_rollback_reconciliation_required(string phase)
    {
        using var f = new MaintenanceExecutorFixture(true); await f.Initialize(); var intent = await f.Plan();
        typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Vault,
            (Action<string>)(p => { if (p == phase) throw new IOException("synthetic interrupted publication"); }));
        var outcome = await f.Execute(intent); Assert.Equal("OutcomeUnknown", outcome.Execution!.State);
        Assert.NotNull((await f.Vault.InspectRollbackStateAsync(f.Cell.Scope.AuthProfileId)).Pending);
        await Assert.ThrowsAsync<CredentialException>(() => f.Vault.ResolveAsync(f.Cell.Scope)); await Assert.ThrowsAsync<FoundationStoreException>(() => f.Execute(intent));
        Assert.Equal(1, f.Count("CredentialMaintenanceAdmitted"));
    }

    [WindowsCredentialFact]
    public async Task Known_completion_is_recorded_after_role_revocation()
    {
        using var f = new MaintenanceExecutorFixture(true); await f.Initialize(); var intent = await f.Plan(); var executor = f.Executor();
        Fault(executor, p => { if (p == "AfterMutation") f.Store.SetMembership(f.Cell.Scope.CustomerId, "admin", MaintenanceExecutorFixture.Operator, "default", "dev", ["Operator"], false); });
        var result = await executor.ExecuteAsync(await f.Context(FoundationCapabilities.CredentialMaintenanceExecute), intent.Id, intent.Digest);
        Assert.Equal("Succeeded", result.Execution!.State); Assert.Equal(1, f.Count("CredentialMaintenanceCompleted")); Assert.Equal(2, (await f.Vault.GetProfileAsync(f.Cell.Scope.AuthProfileId)).Generation);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Concurrent_executors_and_distinct_intents_for_one_source_generation_admit_one_effect(bool sameIntent)
    {
        using var f = new MaintenanceExecutorFixture(true); await f.Initialize(); var first = await f.Plan(); var second = sameIntent ? first : await f.Plan();
        var blocking = new BlockingKeys(f.Cell.Keys); var vault = new CredentialVault(f.Options, blocking, f.Cell.Clock);
        var one = f.Executor(vault); var two = f.Executor(vault); var context = await f.Context(FoundationCapabilities.CredentialMaintenanceExecute);
        async Task<bool> Attempt(FoundationCredentialMaintenanceExecutor executor, FoundationCredentialMaintenanceIntent intent)
        {
            try { return (await executor.ExecuteAsync(context, intent.Id, intent.Digest)).Execution!.State == "Succeeded"; }
            catch (Exception error) when (error is FoundationStoreException or CredentialException) { return false; }
        }
        var a = Attempt(one, first); await blocking.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var b = Attempt(two, second);
        try { await Task.Delay(60); Assert.False(a.IsCompleted); Assert.False(b.IsCompleted); }
        finally { blocking.Release.TrySetResult(); }
        Assert.Single(await Task.WhenAll(a, b), accepted => accepted); Assert.Equal(1, f.Count("CredentialMaintenanceAdmitted")); Assert.Equal(1, f.Count("CredentialMaintenanceCompleted"));
        Assert.Equal(2, (await f.Vault.GetProfileAsync(f.Cell.Scope.AuthProfileId)).Generation);
    }

    [WindowsCredentialFact]
    public async Task Failed_completion_audit_preserves_unknown_without_undoing_or_repeating_a_known_vault_write()
    {
        using var f = new MaintenanceExecutorFixture(true); await f.Initialize(); var intent = await f.Plan(CredentialMaintenanceOperation.Revoke);
        f.Exec("CREATE TRIGGER synthetic_completion_failure BEFORE INSERT ON audit WHEN NEW.action='CredentialMaintenanceCompleted' BEGIN SELECT RAISE(ABORT,'synthetic audit failure'); END;");
        var result = await f.Execute(intent); Assert.Equal("OutcomeUnknown", result.Execution!.State); Assert.Equal("CompletionAuditUnavailable", result.Execution.Reason);
        Assert.Equal(result.Execution.ExpectedAfterSha256, f.EnvelopeHash()); Assert.False((await f.Vault.GetProfileAsync(f.Cell.Scope.AuthProfileId)).Enabled);
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Execute(intent)); Assert.Equal(1, f.Count("CredentialMaintenanceAdmitted"));
    }

    [WindowsCredentialFact]
    public async Task Namespace_and_database_pins_refuse_foreign_composition_without_creating_state()
    {
        using var f = new MaintenanceExecutorFixture(); await f.Initialize(); var bytes = f.VaultBytes(); var identity = f.Vault.DescribeNamespace();
        Assert.Throws<CredentialException>(() => new FoundationCredentialMaintenanceExecutor(f.Store, f.Vault, identity with { VaultNamespaceId = Guid.NewGuid().ToString("N") }, f.Instance));
        Assert.Throws<CredentialException>(() => new FoundationCredentialMaintenanceExecutor(f.Store, f.Vault, identity, Guid.NewGuid().ToString("N")));
        Assert.Equal(bytes, f.VaultBytes()); Assert.Equal(0, f.Scalar("SELECT count(*) FROM credential_maintenance_executions"));
    }

    [Theory]
    [InlineData("AfterAdmission")]
    [InlineData("AfterMutation")]
    public async Task Actual_owned_process_interruption_leaves_consumed_unknown_with_no_automatic_replay(string phase)
    {
        using var f = new MaintenanceExecutorFixture(true, native: true); await f.Initialize(); var intent = await f.Plan(CredentialMaintenanceOperation.Revoke);
        var context = await f.Context(FoundationCapabilities.CredentialMaintenanceExecute); var before = f.EnvelopeHash(); var ready = Path.Combine(f.Cell.Root, "owned-ready-" + Guid.NewGuid().ToString("N"));
        using var process = StartInterruptedExecutor(f, intent, context, phase, ready); _ = process.SafeHandle;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (!File.Exists(ready))
            { Assert.False(process.HasExited, process.HasExited ? await errors : "Owned process exited before its synthetic checkpoint."); await Task.Delay(25, deadline.Token); }
            Assert.Equal(process.Id.ToString(), File.ReadAllText(ready)); process.Kill(); await process.WaitForExitAsync();
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
        _ = await output; _ = await errors;
        var result = await f.Read(intent.Id); Assert.Equal("OutcomeUnknown", result.Execution!.State); Assert.Null(result.Execution.Result);
        Assert.Equal(phase == "AfterAdmission" ? before : result.Execution.ExpectedAfterSha256, f.EnvelopeHash());
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Execute(intent)); Assert.Equal(1, f.Count("CredentialMaintenanceAdmitted"));
    }

    static Process StartInterruptedExecutor(MaintenanceExecutorFixture f, FoundationCredentialMaintenanceIntent intent, CustomerContext context, string phase, string ready)
    {
        var executable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.Combine(p.Trim('"'), "pwsh.exe")).First(File.Exists);
        static string Q(string value) => "'" + value.Replace("'", "''") + "'";
        var basePath = AppContext.BaseDirectory;
        var script = "$ErrorActionPreference='Stop';";
        foreach (var assembly in new[] { "SQLitePCLRaw.core.dll", "SQLitePCLRaw.provider.e_sqlite3.dll", "SQLitePCLRaw.batteries_v2.dll", "Microsoft.Data.Sqlite.dll", "SelfService.Foundation.dll" })
            script += "[void][Reflection.Assembly]::LoadFrom(" + Q(Path.Combine(basePath, assembly)) + ");";
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        script += "[void][Runtime.InteropServices.NativeLibrary]::Load(" + Q(Path.Combine(basePath, "runtimes", "win-" + architecture, "native", "e_sqlite3.dll")) + ");[SQLitePCL.Batteries_V2]::Init();" +
            "Add-Type -TypeDefinition 'using System; using System.IO; using System.Threading; using System.Diagnostics; public sealed class OwnedMaintenanceClock : TimeProvider { readonly DateTimeOffset now; public OwnedMaintenanceClock(long value) { now=DateTimeOffset.FromUnixTimeMilliseconds(value); } public override DateTimeOffset GetUtcNow()=>now; } public static class OwnedMaintenancePause { public static Action<string> Build(string phase,string ready) { return p=>{if(p==phase){File.WriteAllText(ready,Process.GetCurrentProcess().Id.ToString());Thread.Sleep(60000);}};}}';" +
            "$clock=[OwnedMaintenanceClock]::new([long]" + f.Cell.Clock.Now.ToUnixTimeMilliseconds() + ");" +
            "$o=[SelfService.Foundation.CredentialVaultOptions]::new(" + Q(f.Options.RootPath) + "," + Q(f.Cell.Scope.CustomerId) + "," + Q(f.Cell.Scope.CellId) + ",'key-2');" +
            "$o.RollbackProtection=[SelfService.Foundation.CredentialRollbackProtectionOptions]::new(" + Q(f.Options.RollbackProtection!.AuthorityDirectory) + ");" +
            "$k=[SelfService.Foundation.CredentialWindowsUserKeyProvider]::new(" + Q(Path.Combine(f.Cell.Root, "maintenance-keys")) + "," + Q(f.Cell.Scope.CustomerId) + "," + Q(f.Cell.Scope.CellId) + ");" +
            "$v=[SelfService.Foundation.CredentialVault]::new($o,$k,$clock);" +
            "$s=[SelfService.Foundation.FoundationStore]::new(" + Q(Path.Combine(f.Cell.Root, "cell.db")) + "," + Q(f.Cell.Scope.CellId) + "," + Q(f.Cell.Scope.CustomerId) + ",$clock,$false,$null);" +
            // A child-only fixture transfers the already signed/validated parent's exact context, without a public constructor or request seam.
            "$constructor=[SelfService.Foundation.CustomerContext].GetConstructors([Reflection.BindingFlags]'Instance,NonPublic')[0];" +
            "$c=$constructor.Invoke([object[]]@(" + string.Join(",", new[] { Q(context.CustomerId), Q(context.CellId), Q(context.ActorKey), Q(context.ProjectId), Q(context.EnvironmentId), Q(context.Capability) }) +
            ",[long]" + context.RoleVersion + ",[SelfService.Foundation.PrincipalKind]::User,[DateTimeOffset]::FromUnixTimeMilliseconds([long]" + context.ExpiresUtc.ToUnixTimeMilliseconds() + "),$null,$null,$null));" +
            "$e=[SelfService.Foundation.FoundationCredentialMaintenanceExecutor]::new($s,$v,$v.DescribeNamespace()," + Q(f.Instance) + ",$null);" +
            "[SelfService.Foundation.FoundationCredentialMaintenanceExecutor].GetProperty('MaintenanceFault',[Reflection.BindingFlags]'Instance,NonPublic').SetValue($e,[OwnedMaintenancePause]::Build(" + Q(phase) + "," + Q(ready) + "));" +
            "$e.ExecuteAsync($c," + Q(intent.Id) + "," + Q(intent.Digest) + ",[Threading.CancellationToken]::None).GetAwaiter().GetResult()|Out-Null";
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = f.Cell.Root };
        start.Environment.Clear(); foreach (var name in new[] { "SystemRoot", "WINDIR" }) if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["TEMP"] = f.Cell.Root; start.Environment["TMP"] = f.Cell.Root; start.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(executable)!, "Modules");
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand");
        start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script))); return Process.Start(start)!;
    }

    static void Fault(FoundationCredentialMaintenanceExecutor executor, Action<string> action) => typeof(FoundationCredentialMaintenanceExecutor)
        .GetProperty("MaintenanceFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(executor, action);

    sealed class BlockingKeys(ICredentialKeyProvider inner) : ICredentialKeyProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken cancellationToken)
        { if (key == "key-2") { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); } return await inner.GetKeyAsync(customer, cell, key, cancellationToken); }
    }
    sealed class FixturePermit(CredentialFixture f) : ICredentialDispatchPermitSource
    { public Task<CredentialDispatchPermit?> AuthorizeAsync(PipelineWireRequest wire, CancellationToken cancellationToken) => Task.FromResult<CredentialDispatchPermit?>(CredentialDispatchPermit.FromDurableDispatch(f.Dispatch, wire, f.Scope, f.Clock.Now)); }
    sealed class RecordedHandler : HttpMessageHandler
    {
        public string? Authorization { get; private set; }
        protected override Task<HttpResponseMessage> SendAsync(HttpRequestMessage request, CancellationToken cancellationToken)
        { Authorization = request.Headers.Authorization?.ToString(); return Task.FromResult(new HttpResponseMessage(System.Net.HttpStatusCode.NoContent)); }
    }
}

internal sealed class MaintenanceExecutorFixture : IDisposable
{
    public const string Operator = "user:" + IdentityTestIssuer.Tenant + ":" + IdentityTestIssuer.Subject;
    public const string Approver = "user:" + IdentityTestIssuer.Tenant + ":" + IdentityTestIssuer.OtherSubject;
    public const string Auditor = "user:" + IdentityTestIssuer.Tenant + ":" + IdentityTestIssuer.WorkerSubject;
    public CredentialFixture Cell { get; } = new();
    readonly IdentityTestIssuer issuer = new();
    public FoundationStore Store { get; }
    public CredentialVault Source { get; }
    public CredentialVault Vault { get; }
    public CredentialVaultOptions Options { get; }
    readonly CredentialWindowsUserKeyProvider? nativeKeys;
    public string Instance => Store.GetCellInstanceIdentity().DatabaseInstanceId;
    public string ProfilePath => Directory.GetFiles(Options.RootPath, "*.credential.json").Single();
    public MaintenanceExecutorFixture(bool strong = false, bool native = false)
    {
        Cell.Keys.Values["key-2"] = RandomNumberGenerator.GetBytes(32);
        var source = strong ? Cell.Options with { RootPath = Path.Combine(Cell.Root, "strong-maintenance"), RollbackProtection = new(Path.Combine(Cell.Root, "maintenance-authority")) } : Cell.Options;
        if (native) nativeKeys = Cell.ProvisionNativeKeys("maintenance-keys");
        ICredentialKeyProvider keys = nativeKeys is null ? Cell.Keys : nativeKeys;
        Source = strong ? CredentialVault.ProvisionStrong(source, keys, Cell.Clock) : Cell.Vault;
        Options = source with { EncryptionKeyId = "key-2" }; Vault = new(Options, keys, Cell.Clock);
        Store = new(Path.Combine(Cell.Root, "cell.db"), Cell.Scope.CellId, Cell.Scope.CustomerId, Cell.Clock);
        foreach (var (actor, role) in new[] { (Operator, "Operator"), (Approver, "Approver"), (Auditor, "Auditor") })
            Store.SetMembership(Cell.Scope.CustomerId, "admin", actor, "default", "dev", [role]);
    }
    public async Task Initialize()
    {
        if (nativeKeys is not null)
            foreach (var key in new[] { "key-1", "key-2" }) using (await nativeKeys.ProvisionKeyAsync(Cell.Scope.CustomerId, Cell.Scope.CellId, key)) { }
        using var material = CredentialSecret.FromUtf8(CredentialFixture.Token); await Source.PutAsync(Cell.Scope, material, Cell.Clock.Now.AddHours(1));
    }
    public async Task<CustomerContext> Context(string capability, string? role = null, string? subject = null)
    {
        role ??= capability == FoundationCapabilities.CredentialMaintenanceApprove ? "Approver" : capability == FoundationCapabilities.CredentialMaintenanceRead ? "Auditor" : "Operator";
        subject ??= role == "Approver" ? IdentityTestIssuer.OtherSubject : role == "Auditor" ? IdentityTestIssuer.WorkerSubject : IdentityTestIssuer.Subject;
        var options = issuer.Options(Cell.Scope.CustomerId, Cell.Scope.CellId); var claims = issuer.UserClaims(role, subject);
        claims["iat"] = claims["nbf"] = Cell.Clock.Now.AddSeconds(-30).ToUnixTimeSeconds(); claims["exp"] = Cell.Clock.Now.AddMinutes(10).ToUnixTimeSeconds();
        var principal = await new IdentityTokenValidator(options, Cell.Clock).ValidateAsync(issuer.Token(claims));
        return new CustomerContextResolver(options, new Memberships(Store), Cell.Clock).Require(principal, Cell.Scope.CustomerId, "default", "dev", capability);
    }
    public async Task<FoundationCredentialMaintenanceIntent> Plan(CredentialMaintenanceOperation operation = CredentialMaintenanceOperation.Rewrap, FoundationCredentialMaintenanceTarget? target = null)
    {
        target ??= await Vault.InspectMaintenanceAsync(Cell.Scope.AuthProfileId);
        var intent = Store.PrepareCredentialMaintenance(await Context(FoundationCapabilities.CredentialMaintenancePrepare), operation, target,
            "Reviewed synthetic credential maintenance", Cell.Clock.Now.AddMinutes(10));
        return Store.ApproveCredentialMaintenance(await Context(FoundationCapabilities.CredentialMaintenanceApprove), intent.Id, intent.Digest);
    }
    public FoundationCredentialMaintenanceExecutor Executor(CredentialVault? vault = null, TimeSpan? timeout = null)
    { vault ??= Vault; return new(Store, vault, vault.DescribeNamespace(), Instance, timeout); }
    public async Task<FoundationCredentialMaintenanceIntent> Execute(FoundationCredentialMaintenanceIntent intent) => await Executor().ExecuteAsync(await Context(FoundationCapabilities.CredentialMaintenanceExecute), intent.Id, intent.Digest);
    public async Task<FoundationCredentialMaintenanceIntent> Read(string id) => Store.GetCredentialMaintenance(await Context(FoundationCapabilities.CredentialMaintenanceRead), id);
    public string EnvelopeHash() => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(ProfilePath)));
    public string[] VaultBytes() => new[] { Options.RootPath, Options.RollbackProtection?.AuthorityDirectory }.Where(p => p is not null)
        .SelectMany(p => Directory.GetFiles(p!)).Where(p => Path.GetFileName(p) is not (".vault.lock" or ".rollback.lock"))
        .Order(StringComparer.Ordinal).Select(p => Path.GetFileName(p) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(p)))).ToArray();
    public long Scalar(string sql) { using var db = Open(); using var c = db.CreateCommand(); c.CommandText = sql; return Convert.ToInt64(c.ExecuteScalar()); }
    public void Exec(string sql) { using var db = Open(); using var c = db.CreateCommand(); c.CommandText = sql; c.ExecuteNonQuery(); }
    public long Count(string action) { using var db = Open(); using var c = db.CreateCommand(); c.CommandText = "SELECT count(*) FROM audit WHERE action=$action"; c.Parameters.AddWithValue("$action", action); return Convert.ToInt64(c.ExecuteScalar()); }
    SqliteConnection Open() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Cell.Root, "cell.db"), Pooling = false }.ToString()); db.Open(); return db; }
    public void Dispose() { issuer.Dispose(); Cell.Dispose(); }
    sealed class Memberships(FoundationStore store) : ICustomerMembershipSource
    { public IReadOnlyList<MembershipGrant> Find(string customerId, string actorKey) => store.GetCredentialMaintenanceMemberships(customerId, actorKey).Select(m => new MembershipGrant(m.CustomerId, m.CellId, m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version, m.Enabled)).ToArray(); }
}
