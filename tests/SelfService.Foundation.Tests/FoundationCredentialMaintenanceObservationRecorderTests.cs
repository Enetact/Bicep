using System.Reflection;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationCredentialMaintenanceObservationRecorderTests
{
    [Theory]
    [InlineData(false, CredentialMaintenanceOperation.Revoke)]
    [InlineData(false, CredentialMaintenanceOperation.Rewrap)]
    [InlineData(true, CredentialMaintenanceOperation.Revoke)]
    [InlineData(true, CredentialMaintenanceOperation.Rewrap)]
    public async Task Reviewed_current_candidate_is_recorded_without_touching_vault_or_original_unknown_execution(bool strong, CredentialMaintenanceOperation operation)
    {
        using var f = new ObservationRecorderFixture(strong); await f.Unknown(operation); var original = f.OriginalRows(); var bytes = f.Maintenance.VaultBytes();
        f.Maintenance.Cell.Keys.Requests.Clear(); var request = await f.Prepare(); request = await f.Approve(request); var result = await f.Record(request);
        Assert.Equal("ObservedPublished", result.Receipt!.Status); Assert.Equal(request.Digest, result.Receipt.RequestDigest);
        Assert.Equal(request.ObservationSha256, result.Receipt.ObservationSha256); Assert.Equal(f.Original!.Execution!.ExpectedAfterSha256, request.Observation.EnvelopeSha256);
        Assert.Equal(2, request.Observation.ProfileGeneration); Assert.Equal(operation == CredentialMaintenanceOperation.Rewrap, request.Observation.Enabled);
        Assert.Equal(bytes, f.Maintenance.VaultBytes()); Assert.Equal(original, f.OriginalRows());
        Assert.All(f.Maintenance.Cell.Keys.Requests, key => Assert.Equal("key-2", key.KeyId));
        Assert.DoesNotContain(CredentialFixture.Token, JsonSerializer.Serialize(result)); Assert.DoesNotContain(f.Maintenance.Cell.Root, JsonSerializer.Serialize(result));
        Assert.Equal("OutcomeUnknown", (await f.Maintenance.Read(f.Original.Id)).Execution!.State);
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Maintenance.Execute(f.Original));
        Assert.Equal(1, f.Maintenance.Count("CredentialMaintenanceAdmitted")); Assert.Equal(1, f.Maintenance.Count("CredentialMaintenanceObservedPublished"));
    }

    [WindowsCredentialFact]
    public async Task Observation_needs_only_the_authenticated_source_key_even_if_configured_write_key_is_unavailable()
    {
        using var f = new ObservationRecorderFixture(true); await f.Unknown();
        var vault = new CredentialVault(f.Maintenance.Options with { EncryptionKeyId = "not-provisioned-current-write-key" }, f.Maintenance.Cell.Keys, f.Maintenance.Cell.Clock);
        var recorder = f.Recorder(vault); var bytes = f.Maintenance.VaultBytes(); f.Maintenance.Cell.Keys.Requests.Clear();
        var request = await recorder.PrepareAsync(await f.Context(FoundationCapabilities.CredentialMaintenancePrepare), f.Original!.Id,
            ObservationRecorderFixture.Purpose, f.Now.AddMinutes(10)); request = await f.Approve(request);
        var result = await recorder.RecordAsync(await f.Context(FoundationCapabilities.CredentialMaintenanceExecute), request.Id, request.Digest);
        Assert.Equal("ObservedPublished", result.Receipt!.Status); Assert.Equal("key-2", request.Observation.SourceKeyId);
        Assert.Equal("not-provisioned-current-write-key", request.Observation.DestinationKeyId);
        Assert.All(f.Maintenance.Cell.Keys.Requests, key => Assert.Equal("key-2", key.KeyId)); Assert.Equal(bytes, f.Maintenance.VaultBytes());
    }

    [Theory]
    [InlineData("before-mutation")]
    [InlineData("later-generation")]
    [InlineData("succeeded")]
    public async Task Old_absent_or_later_candidates_do_not_become_a_published_observation_and_need_no_key_lookup(string state)
    {
        using var f = new ObservationRecorderFixture(true); await f.Unknown(phase: state == "before-mutation" ? "AfterAdmission" : state == "succeeded" ? null : "AfterMutation");
        if (state == "later-generation") await f.Maintenance.Vault.RevokeAsync(f.Maintenance.Cell.Scope.AuthProfileId, 2);
        var bytes = f.Maintenance.VaultBytes(); var original = f.OriginalRows(); f.Maintenance.Cell.Keys.Requests.Clear();
        await Assert.ThrowsAnyAsync<Exception>(() => f.Prepare()); Assert.Empty(f.Maintenance.Cell.Keys.Requests);
        Assert.Equal(bytes, f.Maintenance.VaultBytes()); Assert.Equal(original, f.OriginalRows()); Assert.Equal(0, f.ReceiptCount());
    }

    [Theory]
    [InlineData("PendingPersisted")]
    [InlineData("EnvelopePublished")]
    public async Task Pending_highwater_state_is_never_reconciled_or_accepted_by_the_readonly_recorder(string phase)
    {
        using var f = new ObservationRecorderFixture(true); await f.Initialize(); var original = await f.Maintenance.Plan();
        typeof(CredentialVault).GetProperty("RollbackFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Maintenance.Vault,
            (Action<string>)(p => { if (p == phase) throw new IOException("synthetic pending write"); }));
        f.Original = await f.Maintenance.Execute(original); var bytes = f.Maintenance.VaultBytes(); var rows = f.OriginalRows();
        f.Maintenance.Cell.Keys.Requests.Clear(); await Assert.ThrowsAsync<CredentialException>(() => f.Prepare());
        Assert.Empty(f.Maintenance.Cell.Keys.Requests); Assert.Equal(bytes, f.Maintenance.VaultBytes()); Assert.Equal(rows, f.OriginalRows());
        Assert.NotNull((await f.Maintenance.Vault.InspectRollbackStateAsync(f.Maintenance.Cell.Scope.AuthProfileId)).Pending);
    }

    [WindowsCredentialFact]
    public async Task Later_committed_authority_revision_for_another_profile_is_reviewed_as_current_evidence()
    {
        using var f = new ObservationRecorderFixture(true); await f.Unknown(); using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token);
        await f.Maintenance.Vault.PutAsync(f.Maintenance.Cell.Scope with { AuthProfileId = "profile/unrelated-committed" }, secret, f.Now.AddHours(1));
        var request = await f.Approve(await f.Prepare()); Assert.True(request.Observation.AuthorityRevision > f.Original!.Target.AuthorityRevision + 2);
        var rows = f.OriginalRows(); var bytes = f.Maintenance.VaultBytes(); var result = await f.Record(request);
        Assert.Equal("ObservedPublished", result.Receipt!.Status); Assert.Equal(rows, f.OriginalRows()); Assert.Equal(bytes, f.Maintenance.VaultBytes());
    }

    [WindowsCredentialFact]
    public async Task Expired_original_and_revoked_original_humans_do_not_supply_or_prevent_new_current_two_human_review()
    {
        using var f = new ObservationRecorderFixture(true); await f.Unknown(); f.Maintenance.Cell.Clock.Now = f.Now.AddHours(2);
        foreach (var actor in new[] { MaintenanceExecutorFixture.Operator, MaintenanceExecutorFixture.Approver })
            f.Maintenance.Store.SetMembership(f.Customer, "admin", actor, "default", "dev", [actor == MaintenanceExecutorFixture.Operator ? "Operator" : "Approver"], false);
        f.OperatorSubject = "dddddddd-dddd-dddd-dddd-dddddddddddd"; f.ApproverSubject = "eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee";
        f.Maintenance.Store.SetMembership(f.Customer, "admin", f.Actor(f.OperatorSubject), "default", "dev", ["Operator"]);
        f.Maintenance.Store.SetMembership(f.Customer, "admin", f.Actor(f.ApproverSubject), "default", "dev", ["Approver"]);
        var bytes = f.Maintenance.VaultBytes(); var rows = f.OriginalRows(); var request = await f.Approve(await f.Prepare());
        Assert.True(f.Original!.ExpiresAt < f.Now); var result = await f.Record(request);
        Assert.Equal("ObservedPublished", result.Receipt!.Status); Assert.Equal(rows, f.OriginalRows()); Assert.Equal(bytes, f.Maintenance.VaultBytes());
        Assert.Equal(f.Actor(f.OperatorSubject), result.Receipt.RecorderKey); Assert.Equal("OutcomeUnknown", (await f.Maintenance.Read(f.Original.Id)).Execution!.State);
    }

    [Theory]
    [InlineData("operator")]
    [InlineData("approver")]
    [InlineData("policy")]
    [InlineData("expiry")]
    public async Task Final_current_authority_is_rechecked_after_async_source_key_lookup_without_a_held_database_transaction(string changed)
    {
        using var f = new ObservationRecorderFixture(true); await f.Unknown(); var request = await f.Approve(await f.Prepare());
        var keys = new PausedSourceKeys(f.Maintenance.Cell.Keys); var vault = new CredentialVault(f.Maintenance.Options, keys, f.Maintenance.Cell.Clock); var recorder = f.Recorder(vault);
        var context = await f.Context(FoundationCapabilities.CredentialMaintenanceExecute); var bytes = f.Maintenance.VaultBytes(); var rows = f.OriginalRows();
        var recording = recorder.RecordAsync(context, request.Id, request.Digest);
        try
        {
            await keys.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.False(recording.IsCompleted);
            if (changed is "operator" or "approver") f.Maintenance.Store.SetMembership(f.Customer, "admin", changed == "operator" ? MaintenanceExecutorFixture.Operator : MaintenanceExecutorFixture.Approver,
                "default", "dev", [changed == "operator" ? "Operator" : "Approver"], false);
            else if (changed == "policy") f.Maintenance.Store.SetCustomerEnabled(f.Customer, "admin", false);
            else f.Maintenance.Cell.Clock.Now = f.Now.AddMinutes(20);
        }
        finally { keys.Release.TrySetResult(); }
        await Assert.ThrowsAsync<FoundationStoreException>(() => recording); Assert.Equal(0, f.ReceiptCount()); Assert.Equal(rows, f.OriginalRows()); Assert.Equal(bytes, f.Maintenance.VaultBytes());
    }

    [WindowsCredentialFact]
    public async Task Source_guard_orders_competing_mutation_after_the_recorded_point_in_time_observation()
    {
        using var f = new ObservationRecorderFixture(true); await f.Unknown(); var request = await f.Approve(await f.Prepare());
        var keys = new PausedSourceKeys(f.Maintenance.Cell.Keys); var vault = new CredentialVault(f.Maintenance.Options, keys, f.Maintenance.Cell.Clock); var recorder = f.Recorder(vault);
        var recording = recorder.RecordAsync(await f.Context(FoundationCapabilities.CredentialMaintenanceExecute), request.Id, request.Digest);
        await keys.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); var laterMutation = f.Maintenance.Vault.RevokeAsync(f.Maintenance.Cell.Scope.AuthProfileId, 2);
        try { await Task.Delay(60); Assert.False(laterMutation.IsCompleted); Assert.False(recording.IsCompleted); }
        finally { keys.Release.TrySetResult(); }
        var receipt = await recording; Assert.Equal("ObservedPublished", receipt.Receipt!.Status); Assert.Equal(3, (await laterMutation).Generation);
        Assert.Equal(2, receipt.Observation.ProfileGeneration); Assert.Equal(f.Original!.Execution!.ExpectedAfterSha256, receipt.Observation.EnvelopeSha256);
        Assert.Equal("OutcomeUnknown", (await f.Maintenance.Read(f.Original.Id)).Execution!.State);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Caller_cancellation_or_deadline_during_source_lookup_cannot_record_or_mutate_state(bool timeout)
    {
        using var f = new ObservationRecorderFixture(true); await f.Unknown(); var request = await f.Approve(await f.Prepare());
        var keys = new PausedSourceKeys(f.Maintenance.Cell.Keys); var vault = new CredentialVault(f.Maintenance.Options, keys, f.Maintenance.Cell.Clock);
        // Current DB authority and native custody checks precede provider entry. Allow that preflight
        // under the parallel native cohort, then exercise cancellation while the provider is actually paused.
        var recorder = f.Recorder(vault, timeout ? TimeSpan.FromSeconds(3) : null); using var cancellation = new CancellationTokenSource();
        var bytes = f.Maintenance.VaultBytes(); var rows = f.OriginalRows(); var recording = recorder.RecordAsync(await f.Context(FoundationCapabilities.CredentialMaintenanceExecute), request.Id, request.Digest, cancellation.Token);
        try
        {
            await keys.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15)); if (!timeout) cancellation.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => recording.WaitAsync(TimeSpan.FromSeconds(15)));
            Assert.False(keys.Release.Task.IsCompleted); // Cancellation, rather than releasing the provider, ended this attempt.
            Assert.Equal(!timeout, cancellation.IsCancellationRequested);
        }
        finally
        {
            cancellation.Cancel(); keys.Release.TrySetResult();
            try { await recording; } catch { } // Observe actual completion and release custody before disposing the owned fixture.
        }
        Assert.Equal(0, f.ReceiptCount()); Assert.Equal(rows, f.OriginalRows()); Assert.Equal(bytes, f.Maintenance.VaultBytes());
        Assert.Equal("OutcomeUnknown", (await f.Maintenance.Read(f.Original!.Id)).Execution!.State);
        Assert.Equal("ObservedPublished", (await f.Record(request).WaitAsync(TimeSpan.FromSeconds(15))).Receipt!.Status);
    }

    [WindowsCredentialFact]
    public async Task Already_recorded_is_read_explicitly_and_never_creates_another_current_observation()
    {
        using var f = new ObservationRecorderFixture(true); await f.Unknown(); var request = await f.Approve(await f.Prepare()); var recorded = await f.Record(request);
        var bytes = f.Maintenance.VaultBytes(); var rows = f.OriginalRows(); f.Maintenance.Cell.Keys.Requests.Clear();
        Assert.Equal("MaintenanceObservationAlreadyRecorded", (await Assert.ThrowsAsync<FoundationStoreException>(() => f.Record(request))).Code);
        var current = f.Maintenance.Store.GetCredentialMaintenanceObservation(await f.Maintenance.Context(FoundationCapabilities.CredentialMaintenanceRead), request.Id);
        Assert.Equal(recorded.Receipt, current.Receipt); Assert.Empty(f.Maintenance.Cell.Keys.Requests); Assert.Equal(1, f.ReceiptCount());
        Assert.Equal(rows, f.OriginalRows()); Assert.Equal(bytes, f.Maintenance.VaultBytes()); Assert.Equal(1, f.Maintenance.Count("CredentialMaintenanceObservedPublished"));
    }

    [WindowsCredentialFact]
    public async Task Ambiguous_commit_returns_opaque_uncertainty_while_explicit_read_finds_one_immutable_receipt()
    {
        using var f = new ObservationRecorderFixture(true); await f.Unknown(); var request = await f.Approve(await f.Prepare()); var rows = f.OriginalRows(); var bytes = f.Maintenance.VaultBytes();
        typeof(FoundationStore).GetProperty("ObservationCommitFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(f.Maintenance.Store,
            (Action)(() => throw new IOException("synthetic lost observation response")));
        var error = await Assert.ThrowsAsync<FoundationStoreException>(() => f.Record(request)); Assert.Equal("MaintenanceObservationRecordUncertain", error.Code);
        var current = f.Maintenance.Store.GetCredentialMaintenanceObservation(await f.Maintenance.Context(FoundationCapabilities.CredentialMaintenanceRead), request.Id);
        Assert.Equal("ObservedPublished", current.Receipt!.Status); Assert.Equal(1, f.ReceiptCount()); Assert.Equal(0, f.Maintenance.Count("CredentialMaintenanceObservationDenied"));
        Assert.Equal(rows, f.OriginalRows()); Assert.Equal(bytes, f.Maintenance.VaultBytes()); Assert.DoesNotContain("synthetic lost", error.ToString());
    }

    [WindowsCredentialFact]
    public async Task Initial_wrong_capabilities_are_refused_before_any_key_read()
    {
        using var f = new ObservationRecorderFixture(true); await f.Unknown(); var request = await f.Approve(await f.Prepare()); var recorder = f.Recorder();
        var bytes = f.Maintenance.VaultBytes(); f.Maintenance.Cell.Keys.Requests.Clear();
        var readContext = await f.Maintenance.Context(FoundationCapabilities.CredentialMaintenanceRead);
        var executeContext = await f.Maintenance.Context(FoundationCapabilities.CredentialMaintenanceExecute);
        await Assert.ThrowsAsync<FoundationStoreException>(() => recorder.RecordAsync(readContext, request.Id, request.Digest));
        await Assert.ThrowsAsync<FoundationStoreException>(() => recorder.PrepareAsync(executeContext, f.Original!.Id, ObservationRecorderFixture.Purpose, f.Now.AddMinutes(10)));
        Assert.Empty(f.Maintenance.Cell.Keys.Requests); Assert.Equal(bytes, f.Maintenance.VaultBytes()); Assert.Equal(0, f.ReceiptCount());
    }

    sealed class PausedSourceKeys(ICredentialKeyProvider inner) : ICredentialKeyProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken cancellationToken)
        { Entered.TrySetResult(); await Release.Task.WaitAsync(cancellationToken); return await inner.GetKeyAsync(customer, cell, key, cancellationToken); }
    }
}

internal sealed class ObservationRecorderFixture(bool strong = true) : IDisposable
{
    public const string Purpose = "Review current synthetic published candidate only";
    public MaintenanceExecutorFixture Maintenance { get; } = new(strong);
    public FoundationCredentialMaintenanceIntent? Original { get; set; }
    public string OperatorSubject { get; set; } = IdentityTestIssuer.Subject;
    public string ApproverSubject { get; set; } = IdentityTestIssuer.OtherSubject;
    public string Customer => Maintenance.Cell.Scope.CustomerId;
    public DateTimeOffset Now => Maintenance.Cell.Clock.Now;
    public string Actor(string subject) => "user:" + IdentityTestIssuer.Tenant + ":" + subject;
    public Task Initialize() => Maintenance.Initialize();
    public async Task Unknown(CredentialMaintenanceOperation operation = CredentialMaintenanceOperation.Rewrap, string? phase = "AfterMutation")
    {
        await Initialize(); var intent = await Maintenance.Plan(operation); var executor = Maintenance.Executor();
        if (phase is not null) typeof(FoundationCredentialMaintenanceExecutor).GetProperty("MaintenanceFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(executor,
            (Action<string>)(p => { if (p == phase) throw new IOException("synthetic ambiguous original"); }));
        Original = await executor.ExecuteAsync(await Maintenance.Context(FoundationCapabilities.CredentialMaintenanceExecute), intent.Id, intent.Digest);
    }
    public Task<CustomerContext> Context(string capability) => Maintenance.Context(capability,
        capability == FoundationCapabilities.CredentialMaintenanceApprove ? "Approver" : "Operator",
        capability == FoundationCapabilities.CredentialMaintenanceApprove ? ApproverSubject : OperatorSubject);
    public FoundationCredentialMaintenanceObservationRecorder Recorder(CredentialVault? vault = null, TimeSpan? timeout = null)
    { vault ??= Maintenance.Vault; return new(Maintenance.Store, vault, vault.DescribeNamespace(), Maintenance.Instance, timeout); }
    public async Task<FoundationCredentialMaintenanceObservationRequest> Prepare() => await Recorder().PrepareAsync(
        await Context(FoundationCapabilities.CredentialMaintenancePrepare), Original!.Id, Purpose, Now.AddMinutes(10));
    public async Task<FoundationCredentialMaintenanceObservationRequest> Approve(FoundationCredentialMaintenanceObservationRequest request) =>
        Maintenance.Store.ApproveCredentialMaintenanceObservation(await Context(FoundationCapabilities.CredentialMaintenanceApprove), request.Id, request.Digest);
    public async Task<FoundationCredentialMaintenanceObservationRequest> Record(FoundationCredentialMaintenanceObservationRequest request) =>
        await Recorder().RecordAsync(await Context(FoundationCapabilities.CredentialMaintenanceExecute), request.Id, request.Digest);
    public long ReceiptCount() => Maintenance.Scalar("SELECT count(*) FROM credential_maintenance_observation_receipts");
    public string OriginalRows()
    {
        using var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Path.Combine(Maintenance.Cell.Root, "cell.db"), Pooling = false }.ToString()); db.Open();
        var result = new List<object>();
        foreach (var table in new[] { "credential_maintenance_intents", "credential_maintenance_approvals", "credential_maintenance_executions" })
        {
            using var c = db.CreateCommand(); c.CommandText = "SELECT * FROM " + table + " ORDER BY 1,2"; using var reader = c.ExecuteReader();
            while (reader.Read()) { var row = new object[reader.FieldCount]; reader.GetValues(row); result.Add(row); }
        }
        return Convert.ToHexStringLower(SHA256.HashData(Encoding.UTF8.GetBytes(JsonSerializer.Serialize(result))));
    }
    public void Dispose() => Maintenance.Dispose();
}
