using System.Diagnostics;
using System.Reflection;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationCredentialRecoveryReviewExecutionTests
{
    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task All_scope_review_admits_before_key_then_records_only_held_evidence_without_replay(bool multiple)
    {
        using var f = new ReviewFixture(multiple); await f.Initialize(); var before = f.StageAndKeys(); f.ClearKeys(); var review = await f.Prepare();
        Assert.Empty(f.DestinationKeys.Requests); Assert.Empty(f.Cell.Keys.Requests);
        review = f.Store.ApproveCredentialRecoveryStageReview(await f.Contexts(FoundationCapabilities.CredentialRecoveryApprove), review.Id, review.Digest);
        Assert.Empty(f.DestinationKeys.Requests); long admittedBeforeLookup = 0; var guardHeld = false; var transactionAvailable = false;
        f.DestinationKeys.OnGet = () =>
        {
            admittedBeforeLookup = f.Admissions;
            try { using var guard = new FileStream(f.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None); } catch (IOException) { guardHeld = true; }
            using var db = f.Open(); using var tx = db.BeginTransaction(deferred: false); transactionAvailable = true; tx.Commit();
        };
        var completed = await f.Execute(review); Assert.Equal("Succeeded", completed.Execution!.State); Assert.Equal("HeldStageVerified", completed.Execution.Result!.Status);
        Assert.True(completed.Execution.Result.InactiveRecoveryHold); Assert.False(completed.Execution.Result.PromotionAuthorized); Assert.False(completed.Execution.Result.LiveAdoptionAuthorized);
        Assert.False(completed.Execution.Result.ActivationPerformed); Assert.False(completed.Execution.Result.ExternalExecutionAuthorized);
        Assert.Equal(1, admittedBeforeLookup); Assert.True(guardHeld); Assert.True(transactionAvailable); Assert.Single(f.DestinationKeys.Requests); Assert.Empty(f.Cell.Keys.Requests);
        Assert.Equal(multiple ? 2 : 1, f.Count("CredentialRecoveryReviewCompleted")); Assert.Equal(before, f.StageAndKeys());
        f.ClearKeys(); await Assert.ThrowsAsync<FoundationStoreException>(() => f.Execute(review)); Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(1, f.Admissions);
    }

    [WindowsCredentialFact]
    public async Task Preparation_checks_current_capability_before_opening_private_stage_and_never_reads_keys()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var readers = await f.Contexts(FoundationCapabilities.CredentialRecoveryRead); f.ClearKeys();
        using (var held = new FileStream(f.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            await Assert.ThrowsAsync<FoundationStoreException>(() => f.Coordinator().PrepareAsync(readers, f.Plan, f.Plan.Sha256, f.Receipt.Sha256, "Synthetic rejected preparation", f.Cell.Clock.Now.AddMinutes(5)));
        Assert.Equal(0, f.Number("SELECT count(*) FROM credential_recovery_stage_review_requests")); Assert.Empty(f.DestinationKeys.Requests);
        var approved = await f.Approved(); Assert.Equal("Approved", approved.State); Assert.Empty(f.DestinationKeys.Requests);
    }

    [Theory]
    [InlineData("requester")] [InlineData("approver")] [InlineData("executor-role")] [InlineData("executor-version")]
    [InlineData("context-expiry")] [InlineData("policy")] [InlineData("lifecycle")] [InlineData("instance")]
    public async Task Current_authority_drift_during_key_await_keeps_admission_consumed_unknown(string change)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify,
            lifetime: change == "context-expiry" ? TimeSpan.FromMinutes(1) : null); var paused = new PausedKeys(f.Keys); var coordinator = f.Coordinator(paused); var before = f.StageAndKeys();
        var executing = coordinator.ExecuteAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); Assert.Equal("OutcomeUnknown", f.ExecutionState(review.Id));
            if (change == "requester") f.Grant(ReviewFixture.Requester, ["Operator"], false);
            else if (change == "approver") f.Grant(ReviewFixture.Approver, ["Approver"], false);
            else if (change == "executor-role") f.Grant(ReviewFixture.Executor, ["Operator"], false);
            else if (change == "executor-version") f.Grant(ReviewFixture.Executor, ["Operator"]);
            else if (change == "context-expiry") f.Cell.Clock.Now = f.Cell.Clock.Now.AddMinutes(2);
            else if (change == "policy") f.Sql("UPDATE cell SET policy_version=policy_version+1 WHERE singleton=1");
            else if (change == "lifecycle") f.Sql("UPDATE customer_lifecycle SET state='Offboarded',version=version+1 WHERE singleton=1;UPDATE cell SET enabled=0 WHERE singleton=1;");
            else f.Sql("UPDATE cell SET database_instance_id='" + Guid.NewGuid().ToString("N") + "' WHERE singleton=1");
            paused.Release.TrySetResult(); AssertUnknown(await executing);
        }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal("OutcomeUnknown", f.ExecutionState(review.Id)); Assert.Equal(0, f.Count("CredentialRecoveryReviewCompleted")); Assert.Equal(before, f.StageAndKeys());
        f.ClearKeys(); await Assert.ThrowsAnyAsync<Exception>(() => coordinator.ExecuteAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256)); Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(1, f.Admissions);
    }

    [WindowsCredentialFact]
    public async Task Caller_context_list_replacement_during_await_cannot_extend_original_executor_expiry()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); var supplied = (await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify, lifetime: TimeSpan.FromMinutes(1))).ToList();
        var paused = new PausedKeys(f.Keys); var executing = f.Coordinator(paused).ExecuteAsync(supplied, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256);
        try { await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); f.Cell.Clock.Now = f.Cell.Clock.Now.AddMinutes(2); supplied.Clear(); supplied.AddRange(await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify)); paused.Release.TrySetResult(); AssertUnknown(await executing); }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(0, f.Count("CredentialRecoveryReviewCompleted")); Assert.Equal(1, f.Admissions);
    }

    [WindowsCredentialFact]
    public async Task Completion_clock_crossing_only_executor_context_expiry_cannot_publish_success()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved();
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify, lifetime: TimeSpan.FromSeconds(40));
        var clock = new CompletionBoundaryClock(f.Cell.Clock.Now, contexts.Min(context => context.ExpiresUtc).AddMilliseconds(1));
        var store = new FoundationStore(f.Database, f.CellId, f.Customer, clock);
        var coordinator = new FoundationCredentialRecoveryStageReviewCoordinator(store, new(f.Keys, f.Cell.Clock), f.Instance);
        var before = f.StageAndKeys(); f.ClearKeys(); f.DestinationKeys.OnGet = clock.Arm;
        AssertUnknown(await coordinator.ExecuteAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256));
        Assert.True(clock.ObservedCutoff); Assert.Single(f.DestinationKeys.Requests); Assert.Equal("OutcomeUnknown", f.ExecutionState(review.Id));
        Assert.Equal(0, f.Count("CredentialRecoveryReviewCompleted")); Assert.Equal(before, f.StageAndKeys());
    }

    [Theory]
    [InlineData("requester")] [InlineData("approver")] [InlineData("stage")]
    public async Task Change_after_AES_verification_is_rechecked_before_durable_success(string change)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); var coordinator = f.Coordinator(); var before = f.StageAndKeys();
        Fault(coordinator, phase =>
        {
            if (phase != "AfterVerification") return;
            if (change == "requester") f.Grant(ReviewFixture.Requester, ["Operator"], false);
            else if (change == "approver") f.Grant(ReviewFixture.Approver, ["Approver"], false);
            else f.PrivateFile(Path.Combine(f.StageRoot, "unexpected.json"), "{}"u8.ToArray());
        });
        AssertUnknown(await f.Execute(review, coordinator)); Assert.Equal(0, f.Count("CredentialRecoveryReviewCompleted")); Assert.Equal(1, f.Admissions);
        if (change != "stage") Assert.Equal(before, f.StageAndKeys());
        else Assert.True(File.Exists(Path.Combine(f.StageRoot, "unexpected.json"))); // No repair or deletion of owner interference.
    }

    [Theory]
    [InlineData(false)] [InlineData(true)]
    public async Task Completion_audit_failure_cannot_publish_success_or_release_guard_before_unknown_recording(bool failUnknownAudit)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); var before = f.StageAndKeys();
        f.Sql("CREATE TRIGGER reject_review_completed BEFORE INSERT ON audit WHEN NEW.action='CredentialRecoveryReviewCompleted' BEGIN SELECT RAISE(ABORT,'synthetic completion rejection'); END;");
        if (failUnknownAudit) f.Sql("CREATE TRIGGER reject_review_unknown BEFORE INSERT ON audit WHEN NEW.action='CredentialRecoveryReviewUnknown' BEGIN SELECT RAISE(ABORT,'synthetic unknown rejection'); END;");
        var executing = f.Execute(review);
        if (failUnknownAudit) { var error = await Assert.ThrowsAsync<CredentialException>(() => executing); Assert.Equal("credential-recovery-review-outcome-unknown", error.Code); }
        else AssertUnknown(await executing);
        Assert.Equal("OutcomeUnknown", f.ExecutionState(review.Id)); Assert.Equal(0, f.Count("CredentialRecoveryReviewCompleted")); Assert.Equal(before, f.StageAndKeys());
        using var released = new FileStream(f.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [Theory]
    [InlineData("digest")] [InlineData("plan-pin")] [InlineData("receipt-pin")] [InlineData("stage")]
    public async Task Rejected_before_admission_never_reads_keys_or_creates_execution(string change)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); f.ClearKeys();
        if (change == "stage") f.PrivateFile(Path.Combine(f.StageRoot, "unexpected.json"), "{}"u8.ToArray()); var before = f.StageAndKeys();
        var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify);
        await Assert.ThrowsAnyAsync<Exception>(() => f.Coordinator().ExecuteAsync(contexts, review.Id, change == "digest" ? new string('f', 64) : review.Digest,
            f.Plan, change == "plan-pin" ? new string('e', 64) : f.Plan.Sha256, change == "receipt-pin" ? new string('d', 64) : f.Receipt.Sha256));
        Assert.Equal(0, f.Admissions); Assert.Empty(f.DestinationKeys.Requests); Assert.Empty(f.Cell.Keys.Requests); Assert.Equal(before, f.StageAndKeys());
    }

    [WindowsCredentialFact]
    public async Task Cancellation_before_admission_has_no_claim_while_after_admission_is_consumed_unknown()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify); f.ClearKeys();
        using (var canceled = new CancellationTokenSource())
        { canceled.Cancel(); await Assert.ThrowsAnyAsync<OperationCanceledException>(() => f.Coordinator().ExecuteAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256, canceled.Token)); }
        Assert.Equal(0, f.Admissions); Assert.Empty(f.DestinationKeys.Requests); var before = f.StageAndKeys();
        using var caller = new CancellationTokenSource(); var paused = new PausedKeys(f.Keys);
        var executing = f.Coordinator(paused).ExecuteAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256, caller.Token);
        try { await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel(); AssertUnknown(await executing); }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(1, f.Admissions); Assert.Equal(before, f.StageAndKeys()); Assert.Equal(0, f.Count("CredentialRecoveryReviewCompleted"));
    }

    [WindowsCredentialFact]
    public async Task Cooperative_operation_deadline_leaves_one_consumed_unknown_and_releases_held_guard()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); var paused = new PausedKeys(f.Keys); var before = f.StageAndKeys();
        // Leave native metadata/admission preflight a realistic budget before testing cancellation at the paused provider.
        var executing = f.Execute(review, f.Coordinator(paused, TimeSpan.FromSeconds(3)));
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(15));
            Assert.False(paused.Release.Task.IsCompleted); // Only the operation deadline can release the cooperative await.
            var unknown = await executing.WaitAsync(TimeSpan.FromSeconds(15)); AssertUnknown(unknown);
            Assert.Equal("StageVerificationInterrupted", unknown.Execution!.Reason);
        }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(1, f.Admissions); Assert.Equal(before, f.StageAndKeys()); using var guard = new FileStream(f.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    [WindowsCredentialFact]
    public async Task Uncooperative_key_provider_is_retained_with_guard_until_actual_return_then_cancellation_is_unknown()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); var contexts = await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify); var before = f.StageAndKeys();
        using var caller = new CancellationTokenSource(); var paused = new PausedKeys(f.Keys, cooperative: false);
        var executing = f.Coordinator(paused).ExecuteAsync(contexts, review.Id, review.Digest, f.Plan, f.Plan.Sha256, f.Receipt.Sha256, caller.Token);
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); caller.Cancel(); await Task.Delay(50); Assert.False(executing.IsCompleted);
            Assert.Throws<IOException>(() => new FileStream(f.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            using (var db = f.Open()) using (var tx = db.BeginTransaction(deferred: false)) tx.Commit();
            Assert.Equal("OutcomeUnknown", f.ExecutionState(review.Id)); paused.Release.TrySetResult(); AssertUnknown(await executing);
        }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(before, f.StageAndKeys()); Assert.Equal(0, f.Count("CredentialRecoveryReviewCompleted"));
    }

    [WindowsCredentialFact]
    public async Task Concurrent_execution_claims_one_admission_and_resolves_only_one_destination_key()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); var paused = new PausedKeys(f.Keys); f.ClearKeys(); var first = f.Execute(review, f.Coordinator(paused));
        try
        {
            await paused.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5)); await Assert.ThrowsAnyAsync<Exception>(() => f.Execute(review)); Assert.Empty(f.DestinationKeys.Requests);
            paused.Release.TrySetResult(); Assert.Equal("Succeeded", (await first).Execution!.State);
        }
        finally { paused.Release.TrySetResult(); }
        Assert.Equal(1, f.Admissions); Assert.Single(f.DestinationKeys.Requests);
    }

    [Theory]
    [InlineData("AfterAdmission")] [InlineData("AfterVerification")]
    public async Task Interrupted_execution_returns_unknown_without_stage_writes_or_automatic_replay(string phase)
    {
        using var f = new ReviewFixture(); await f.Initialize(); var review = await f.Approved(); var before = f.StageAndKeys(); f.ClearKeys(); var coordinator = f.Coordinator();
        Fault(coordinator, checkpoint => { if (checkpoint == phase) throw new IOException("synthetic interrupted private path " + f.StageRoot); });
        var unknown = await f.Execute(review, coordinator); AssertUnknown(unknown); Assert.DoesNotContain(f.StageRoot, JsonSerializer.Serialize(unknown)); Assert.DoesNotContain(CredentialFixture.Token, JsonSerializer.Serialize(unknown));
        Assert.Equal(phase == "AfterAdmission" ? 0 : 1, f.DestinationKeys.Requests.Count); Assert.Equal(before, f.StageAndKeys());
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Execute(review)); Assert.Equal(1, f.Admissions);
    }

    [WindowsCredentialFact]
    public async Task Instance_pin_refuses_foreign_composition_without_new_execution_or_key_access()
    {
        using var f = new ReviewFixture(); await f.Initialize(); var before = f.StageAndKeys(); f.ClearKeys();
        Assert.Throws<CredentialException>(() => new FoundationCredentialRecoveryStageReviewCoordinator(f.Store, new(f.Keys, f.Cell.Clock), Guid.NewGuid().ToString("N")));
        Assert.Equal(0, f.Admissions); Assert.Empty(f.DestinationKeys.Requests); Assert.Equal(before, f.StageAndKeys());
    }

    [Theory]
    [InlineData("AfterAdmission")] [InlineData("AfterVerification")]
    public async Task Actual_owned_process_termination_preserves_consumed_unknown_and_unchanged_held_stage(string phase)
    {
        using var f = new ReviewFixture(multipleScopes: false, native: true); await f.Initialize(); var review = await f.Approved();
        var context = (await f.Contexts(FoundationCapabilities.CredentialRecoveryVerify)).Single(); var before = f.StageAndKeys();
        var ready = f.Child("review-ready-" + Guid.NewGuid().ToString("N"));
        using var process = StartInterruptedReview(f, review, context, phase, ready); _ = process.SafeHandle;
        var output = process.StandardOutput.ReadToEndAsync(); var errors = process.StandardError.ReadToEndAsync();
        try
        {
            using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(25));
            while (!File.Exists(ready))
            { Assert.False(process.HasExited, process.HasExited ? await errors : "Owned review child exited before its checkpoint."); await Task.Delay(25, deadline.Token); }
            Assert.Equal(process.Id.ToString(), File.ReadAllText(ready)); Assert.Equal("OutcomeUnknown", f.ExecutionState(review.Id));
            Assert.Throws<IOException>(() => new FileStream(f.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None));
            process.Kill(); await process.WaitForExitAsync();
        }
        finally { if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); } }
        _ = await output; _ = await errors;
        AssertUnknown(await f.Read(review.Id)); Assert.Equal(before, f.StageAndKeys()); Assert.Equal(0, f.Count("CredentialRecoveryReviewCompleted"));
        await Assert.ThrowsAsync<FoundationStoreException>(() => f.Execute(review)); Assert.Equal(1, f.Admissions);
        using var released = new FileStream(f.Guard, FileMode.Open, FileAccess.ReadWrite, FileShare.None);
    }

    static Process StartInterruptedReview(ReviewFixture f, FoundationCredentialRecoveryStageReview review, CustomerContext context, string phase, string ready)
    {
        var executable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator).Where(p => !string.IsNullOrWhiteSpace(p))
            .Select(p => Path.Combine(p.Trim('"'), "pwsh.exe")).First(File.Exists);
        static string Q(string value) => "'" + value.Replace("'", "''") + "'";
        var sourcePath = f.Child("review-private-source-set.json"); using (var stream = new MemoryStream()) { f.Set.WritePrivateArtifact(stream); f.PrivateFile(sourcePath, stream.ToArray()); }
        var planPath = f.Child("review-private-plan.json"); f.PrivateFile(planPath, JsonSerializer.SerializeToUtf8Bytes(f.Plan, new JsonSerializerOptions(JsonSerializerDefaults.Web)));
        var basePath = AppContext.BaseDirectory; var script = "$ErrorActionPreference='Stop';";
        foreach (var assembly in new[] { "SQLitePCLRaw.core.dll", "SQLitePCLRaw.provider.e_sqlite3.dll", "SQLitePCLRaw.batteries_v2.dll", "Microsoft.Data.Sqlite.dll", "SelfService.Foundation.dll" })
            script += "[void][Reflection.Assembly]::LoadFrom(" + Q(Path.Combine(basePath, assembly)) + ");";
        var architecture = System.Runtime.InteropServices.RuntimeInformation.ProcessArchitecture.ToString().ToLowerInvariant();
        script += "[void][Runtime.InteropServices.NativeLibrary]::Load(" + Q(Path.Combine(basePath, "runtimes", "win-" + architecture, "native", "e_sqlite3.dll")) + ");[SQLitePCL.Batteries_V2]::Init();" +
            "Add-Type -TypeDefinition 'using System; using System.IO; using System.Threading; using System.Diagnostics; public sealed class OwnedReviewClock : TimeProvider { readonly DateTimeOffset now; public OwnedReviewClock(long value) { now=DateTimeOffset.FromUnixTimeMilliseconds(value); } public override DateTimeOffset GetUtcNow()=>now; } public static class OwnedReviewPause { public static Action<string> Build(string phase,string ready) { return p=>{if(p==phase){File.WriteAllText(ready,Process.GetCurrentProcess().Id.ToString());Thread.Sleep(60000);}};}}';" +
            "$clock=[OwnedReviewClock]::new([long]" + f.Cell.Clock.Now.ToUnixTimeMilliseconds() + ");" +
            "$rsa=[Security.Cryptography.RSA]::Create();$rsa.ImportSubjectPublicKeyInfo([Convert]::FromBase64String(" + Q(Convert.ToBase64String(f.Signer.ExportSubjectPublicKeyInfo())) + "),[ref]([int]$used=0));" +
            "$set=[SelfService.Foundation.CredentialRecoverySets]::Parse([IO.File]::ReadAllBytes(" + Q(sourcePath) + "));" +
            "$j=[Text.Json.JsonSerializerOptions]::new([Text.Json.JsonSerializerDefaults]::Web);$p=[Text.Json.JsonDocument]::Parse([IO.File]::ReadAllText(" + Q(planPath) + ")).RootElement;" +
            "$ns=[Text.Json.JsonSerializer]::Deserialize($p.GetProperty('sourceNamespace').GetRawText(),[SelfService.Foundation.CredentialVaultNamespaceIdentity],$j);" +
            "$a=[Text.Json.JsonSerializer]::Deserialize($p.GetProperty('sourceSetAuthority').GetRawText(),[SelfService.Foundation.CredentialRecoverySetAuthority],$j);" +
            "$sig=[Text.Json.JsonSerializer]::Deserialize($p.GetProperty('sourceSignature').GetRawText(),[SelfService.Foundation.CredentialRecoverySetSignature],$j);" +
            "$dk=[Text.Json.JsonSerializer]::Deserialize($p.GetProperty('destinationKey').GetRawText(),[SelfService.Foundation.CredentialRecoveryDestinationKeyIdentity],$j);" +
            "$pp=[Text.Json.JsonSerializer]::Deserialize($p.GetProperty('preservedProfiles').GetRawText(),[SelfService.Foundation.CredentialRecoveryPreservedProfile[]],$j);" +
            "$v=[SelfService.Foundation.CredentialRecoverySets]::Verify($set,$a,$rsa,$ns,$clock);" +
            // Recreate the already reviewed parent's exact immutable plan through its internal fixture-only constructor.
            "$pc=[SelfService.Foundation.CredentialRecoveryStagePlan].GetConstructors([Reflection.BindingFlags]'Instance,NonPublic')[0];" +
            "$plan=$pc.Invoke([object[]]@(" + Q(f.StageRoot) + ",[Security.Principal.WindowsIdentity]::GetCurrent().User.Value," + Q(f.Plan.PlanId) + ",$a,$sig,$ns," + Q(f.Plan.DestinationRootCommitmentSha256) + ",$dk," + Q(f.Plan.PurposeSha256) +
            ",[DateTimeOffset]::FromUnixTimeMilliseconds([long]" + f.Plan.PreparedUtc.ToUnixTimeMilliseconds() + "),[DateTimeOffset]::FromUnixTimeMilliseconds([long]" + f.Plan.ExpiresUtc.ToUnixTimeMilliseconds() + "),$pp,[string[]]@(),$v.Inventory));" +
            "if($plan.Sha256 -cne " + Q(f.Plan.Sha256) + "){throw 'FixturePlanMismatch'};" +
            "$keys=[SelfService.Foundation.CredentialWindowsUserKeyProvider]::new(" + Q(f.Child("review-destination-keys")) + "," + Q(f.Customer) + "," + Q(f.CellId) + ");" +
            "$s=[SelfService.Foundation.FoundationStore]::new(" + Q(f.Database) + "," + Q(f.CellId) + "," + Q(f.Customer) + ",$clock,$false,$null);" +
            "$cc=[SelfService.Foundation.CustomerContext].GetConstructors([Reflection.BindingFlags]'Instance,NonPublic')[0];" +
            "$c=$cc.Invoke([object[]]@(" + string.Join(",", new[] { Q(context.CustomerId), Q(context.CellId), Q(context.ActorKey), Q(context.ProjectId), Q(context.EnvironmentId), Q(context.Capability) }) +
            ",[long]" + context.RoleVersion + ",[SelfService.Foundation.PrincipalKind]::User,[DateTimeOffset]::FromUnixTimeMilliseconds([long]" + context.ExpiresUtc.ToUnixTimeMilliseconds() + "),$null,$null,$null));" +
            "$e=[SelfService.Foundation.FoundationCredentialRecoveryStageReviewCoordinator]::new($s,[SelfService.Foundation.CredentialRecoveryStageVerifier]::new($keys,$clock)," + Q(f.Instance) + ",$null);" +
            "[SelfService.Foundation.FoundationCredentialRecoveryStageReviewCoordinator].GetProperty('ReviewFault',[Reflection.BindingFlags]'Instance,NonPublic').SetValue($e,[OwnedReviewPause]::Build(" + Q(phase) + "," + Q(ready) + "));" +
            "$e.ExecuteAsync([SelfService.Foundation.CustomerContext[]]@($c)," + Q(review.Id) + "," + Q(review.Digest) + ",$plan," + Q(f.Plan.Sha256) + "," + Q(f.Receipt.Sha256) + ",[Threading.CancellationToken]::None).GetAwaiter().GetResult()|Out-Null";
        var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
            RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = f.Cell.Root };
        start.Environment.Clear(); foreach (var name in new[] { "SystemRoot", "WINDIR" }) if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
        start.Environment["TEMP"] = f.Cell.Root; start.Environment["TMP"] = f.Cell.Root; start.Environment["PSModulePath"] = Path.Combine(Path.GetDirectoryName(executable)!, "Modules");
        start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive"); start.ArgumentList.Add("-EncodedCommand"); start.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script))); return Process.Start(start)!;
    }

    static void Fault(FoundationCredentialRecoveryStageReviewCoordinator coordinator, Action<string> callback) => typeof(FoundationCredentialRecoveryStageReviewCoordinator)
        .GetProperty("ReviewFault", BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(coordinator, callback);
    static void AssertUnknown(FoundationCredentialRecoveryStageReview result)
    { Assert.Equal("OutcomeUnknown", result.Execution!.State); Assert.Null(result.Execution.Result); Assert.Null(result.Execution.ResultSha256); }
    static string Hash(byte[] bytes) => Convert.ToHexStringLower(SHA256.HashData(bytes));
    sealed class CompletionBoundaryClock(DateTimeOffset initial, DateTimeOffset expired) : TimeProvider
    {
        bool armed; int samples;
        public bool ObservedCutoff { get; private set; }
        public void Arm() { armed = true; samples = 0; }
        public override DateTimeOffset GetUtcNow()
        {
            // After the sole destination await, completion checks contexts and standing authority,
            // then samples its publication instant. Cross only the shorter executor lifetime there.
            if (armed && ++samples >= 3) { ObservedCutoff = true; return expired; }
            return initial;
        }
    }
    sealed class PausedKeys(ICredentialKeyProvider inner, bool cooperative = true) : ICredentialKeyProvider
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async ValueTask<CredentialEncryptionKey> GetKeyAsync(string customer, string cell, string key, CancellationToken cancellationToken)
        { Entered.TrySetResult(); if (cooperative) await Release.Task.WaitAsync(cancellationToken); else await Release.Task; return await inner.GetKeyAsync(customer, cell, key, cooperative ? cancellationToken : CancellationToken.None); }
    }
    sealed class ReviewFixture : IDisposable
    {
        public const string RequesterSubject = "dddddddd-dddd-dddd-dddd-dddddddddddd";
        public const string Requester = "user:" + IdentityTestIssuer.Tenant + ":" + RequesterSubject;
        public const string Executor = MaintenanceExecutorFixture.Operator, Approver = MaintenanceExecutorFixture.Approver;
        public CredentialFixture Cell { get; } = new();
        readonly IdentityTestIssuer issuer = new();
        public RSA Signer { get; } = RSA.Create(2048);
        public FoundationStore Store { get; }
        public CredentialTestKeys DestinationKeys { get; } = new();
        public CredentialWindowsUserKeyProvider? NativeKeys { get; }
        public ICredentialKeyProvider Keys => NativeKeys is null ? DestinationKeys : NativeKeys;
        public CredentialRecoverySet Set { get; private set; } = null!;
        public CredentialRecoveryStagePlan Plan { get; private set; } = null!;
        public CredentialRecoveryStageReceipt Receipt { get; private set; } = null!;
        public string Customer => Cell.Scope.CustomerId;
        public string CellId => Cell.Scope.CellId;
        public string Database => Child("cell.db");
        public string StageRoot => Child("reviewed-held-stage");
        public string Guard => Path.Combine(StageRoot, ".credential-recovery-stage.guard");
        public string Instance { get; }
        public bool MultipleScopes { get; }
        public string Child(string name) => Path.Combine(Cell.Root, name);
        public ReviewFixture(bool multipleScopes = true, bool native = false)
        {
            MultipleScopes = multipleScopes; Cell.Clock.Now = DateTimeOffset.Parse("2026-10-25T12:00:00Z"); Store = new(Database, CellId, Customer, Cell.Clock);
            if (multipleScopes)
            {
                var onboarding = Store.PrepareScopeOnboarding(Customer, "admin", "review-other", "qa", "other-admin", "Synthetic review scope onboarding");
                Store.ApproveAdministrativePlan(Customer, "approver", onboarding.Id, onboarding.Digest); Store.ExecuteScopeOnboarding(Customer, "admin", onboarding.Id, onboarding.Digest);
            }
            Grant(Requester, ["Operator"]); Grant(Executor, ["Operator"]); Grant(Approver, ["Approver"]); Grant(MaintenanceExecutorFixture.Auditor, ["Auditor"]);
            DestinationKeys.Values.Clear(); DestinationKeys.Values["review-destination"] = RandomNumberGenerator.GetBytes(32);
            if (native) NativeKeys = Cell.ProvisionNativeKeys("review-destination-keys");
            Instance = Store.GetCellInstanceIdentity().DatabaseInstanceId;
        }
        public void Grant(string actor, string[] roles, bool enabled = true)
        {
            Store.SetMembership(Customer, "admin", actor, "default", "dev", roles, enabled);
            if (MultipleScopes) Store.SetMembership(Customer, "other-admin", actor, "review-other", "qa", roles, enabled);
        }
        public async Task Initialize()
        {
            if (NativeKeys is not null) using (await NativeKeys.ProvisionKeyAsync(Customer, CellId, "review-destination")) { }
            using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token); await Cell.Vault.PutAsync(Cell.Scope, secret, Cell.Clock.Now.AddHours(1));
            if (MultipleScopes) await Cell.Vault.PutAsync(Cell.Scope with { ProjectId = "review-other", EnvironmentId = "qa", AuthProfileId = "profile/review-other" }, secret, Cell.Clock.Now.AddHours(1));
            Set = await Cell.Vault.CaptureSignedRecoverySetAsync(Cell.Vault.DescribeNamespace(), Signer, "review-signer", 1, Cell.Clock.Now, Cell.Clock.Now.AddHours(1));
            var authority = CredentialRecoverySets.Authority(Set); var selected = CredentialRecoverySets.Verify(Set, authority, Signer, Cell.Vault.DescribeNamespace(), Cell.Clock);
            var key = await CredentialRecoveryStaging.ObserveDestinationKeyAsync(Customer, CellId, "review-destination", Keys);
            Plan = CredentialRecoveryStaging.PreparePlan(selected, authority, Cell.Vault.DescribeNamespace(), StageRoot, key, "Synthetic durable held-stage review", Cell.Clock.Now.AddMinutes(20), Cell.Clock);
            Receipt = await new CredentialRecoveryStageExecutor(Cell.Keys, Keys, Cell.Clock).StageAsync(Set, Plan, Plan.Sha256, Signer);
        }
        public async Task<IReadOnlyList<CustomerContext>> Contexts(string capability, string? subject = null, TimeSpan? lifetime = null)
        {
            var role = capability == FoundationCapabilities.CredentialRecoveryApprove ? "Approver" : capability == FoundationCapabilities.CredentialRecoveryRead ? "Auditor" : "Operator";
            subject ??= capability == FoundationCapabilities.CredentialRecoveryPrepare ? RequesterSubject : role == "Approver" ? IdentityTestIssuer.OtherSubject : role == "Auditor" ? IdentityTestIssuer.WorkerSubject : IdentityTestIssuer.Subject;
            var options = issuer.Options(Customer, CellId); var claims = issuer.UserClaims(role, subject);
            claims["iat"] = claims["nbf"] = Cell.Clock.Now.AddSeconds(-30).ToUnixTimeSeconds(); claims["exp"] = Cell.Clock.Now.Add(lifetime ?? TimeSpan.FromMinutes(10)).ToUnixTimeSeconds();
            var principal = await new IdentityTokenValidator(options, Cell.Clock).ValidateAsync(issuer.Token(claims)); var resolver = new CustomerContextResolver(options, new Memberships(Store), Cell.Clock);
            var contexts = new List<CustomerContext> { resolver.Require(principal, Customer, "default", "dev", capability) };
            if (MultipleScopes) contexts.Add(resolver.Require(principal, Customer, "review-other", "qa", capability)); return contexts;
        }
        public FoundationCredentialRecoveryStageReviewCoordinator Coordinator(ICredentialKeyProvider? keys = null, TimeSpan? timeout = null) =>
            new(Store, new CredentialRecoveryStageVerifier(keys ?? Keys, Cell.Clock), Instance, timeout);
        public async Task<FoundationCredentialRecoveryStageReview> Prepare() => await Coordinator().PrepareAsync(await Contexts(FoundationCapabilities.CredentialRecoveryPrepare), Plan, Plan.Sha256, Receipt.Sha256, "Synthetic exact stage review", Cell.Clock.Now.AddMinutes(5));
        public async Task<FoundationCredentialRecoveryStageReview> Approved() { var review = await Prepare(); return Store.ApproveCredentialRecoveryStageReview(await Contexts(FoundationCapabilities.CredentialRecoveryApprove), review.Id, review.Digest); }
        public async Task<FoundationCredentialRecoveryStageReview> Execute(FoundationCredentialRecoveryStageReview review, FoundationCredentialRecoveryStageReviewCoordinator? coordinator = null) =>
            await (coordinator ?? Coordinator()).ExecuteAsync(await Contexts(FoundationCapabilities.CredentialRecoveryVerify), review.Id, review.Digest, Plan, Plan.Sha256, Receipt.Sha256);
        public async Task<FoundationCredentialRecoveryStageReview> Read(string id) => Store.GetCredentialRecoveryStageReview(await Contexts(FoundationCapabilities.CredentialRecoveryRead), id);
        public void ClearKeys() { Cell.Keys.Requests.Clear(); DestinationKeys.Requests.Clear(); }
        public long Count(string action) => Number("SELECT count(*) FROM audit WHERE action='" + action + "'");
        public long Admissions => Number("SELECT count(*) FROM credential_recovery_stage_review_executions");
        public long Number(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        public string ExecutionState(string id) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT state FROM credential_recovery_stage_review_executions WHERE request_id=$id"; command.Parameters.AddWithValue("$id", id); return (string)command.ExecuteScalar()!; }
        public void Sql(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        public SqliteConnection Open() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false, DefaultTimeout = 1 }.ToString()); db.Open(); return db; }
        public string[] StageAndKeys() => new[] { StageRoot, Cell.VaultRoot, NativeKeys is null ? null : Child("review-destination-keys") }.Where(p => p is not null)
            .SelectMany(p => Directory.EnumerateFileSystemEntries(p!, "*", SearchOption.AllDirectories).Prepend(p!)).Order(StringComparer.Ordinal)
            .Select(path => Path.GetRelativePath(Cell.Root, path) + ":" + Hash((Directory.Exists(path) ? (FileSystemSecurity)new DirectoryInfo(path).GetAccessControl() : new FileInfo(path).GetAccessControl()).GetSecurityDescriptorBinaryForm()) + ":" +
                (Directory.Exists(path) ? "directory" : new FileInfo(path).Length == 0 ? Hash([]) : Hash(File.ReadAllBytes(path)))).ToArray();
        public void PrivateFile(string path, byte[] bytes)
        {
            using var identity = WindowsIdentity.GetCurrent(); var security = new FileSecurity(); security.SetOwner(identity.User!); security.SetAccessRuleProtection(true, false); security.AddAccessRule(new FileSystemAccessRule(identity.User!, FileSystemRights.FullControl, AccessControlType.Allow));
            using var file = new FileInfo(path).Create(FileMode.CreateNew, FileSystemRights.FullControl, FileShare.Read, 4096, FileOptions.WriteThrough, security); file.Write(bytes); file.Flush(true);
        }
        public void Dispose() { Set?.Dispose(); Signer.Dispose(); issuer.Dispose(); Cell.Dispose(); }
        sealed class Memberships(FoundationStore store) : ICustomerMembershipSource
        { public IReadOnlyList<MembershipGrant> Find(string customer, string actor) => store.GetCredentialMaintenanceMemberships(customer, actor).Select(m => new MembershipGrant(m.CustomerId, m.CellId, m.ActorKey, m.ProjectId, m.EnvironmentId, m.Roles, m.Version, m.Enabled)).ToArray(); }
    }
}
