using System.Collections.Concurrent;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using System.Security.Cryptography;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationWorkerProcessTests
{
    [Fact]
    public void Registry_is_durable_fenced_customer_bound_and_reports_aggregate_health_only()
    {
        using var f = new StoreFixture();
        f.Store.SetMembership(f.Customer, "admin", "operations", "default", "dev", ["PlatformOperator"]);
        var first = f.Store.RegisterWorker(f.Customer, "owned-worker", 123, f.Clock.GetUtcNow(), "MockLoopback");
        Assert.Equal("WorkerAlreadyLive", Assert.Throws<FoundationStoreException>(() => f.Store.RegisterWorker(f.Customer, "owned-worker", 124, f.Clock.GetUtcNow(), "Disabled")).Code);
        f.Clock.Advance(TimeSpan.FromSeconds(31));
        Assert.Equal(1, f.Reopen().WorkerHealth(f.Customer, "operations", "default", "dev").Stale);
        var second = f.Store.RegisterWorker(f.Customer, "owned-worker", 124, f.Clock.GetUtcNow(), "Disabled");
        Assert.Equal(first.Generation + 1, second.Generation);
        Assert.Equal("WorkerFenceConflict", Assert.Throws<FoundationStoreException>(() => f.Store.HeartbeatWorker(f.Customer, first)).Code);
        Assert.Equal("WorkerFenceConflict", Assert.Throws<FoundationStoreException>(() => f.Store.StopWorker(f.Customer, first)).Code);
        Assert.Equal("CustomerScopeDenied", Assert.Throws<FoundationStoreException>(() => f.Store.HeartbeatWorker("other-customer", second)).Code);
        f.Store.HeartbeatWorker(f.Customer, second);
        var health = f.Reopen().WorkerHealth(f.Customer, "operations", "default", "dev");
        Assert.Equal(1, health.Disabled); Assert.Equal(0, health.Live);
        var json = JsonSerializer.Serialize(health);
        Assert.DoesNotContain("owned-worker", json); Assert.DoesNotContain("Process", json); Assert.DoesNotContain(second.InstanceId, json);
        Assert.Equal("RoleDenied", Assert.Throws<FoundationStoreException>(() => f.Store.WorkerHealth(f.Customer, "viewer", "default", "dev")).Code);
        f.Store.StopWorker(f.Customer, second);
        Assert.Equal(1, f.Reopen().WorkerHealth(f.Customer, "operations", "default", "dev").Stopped);
        Assert.True(f.Store.VerifyAuditIntegrity(f.Customer, "auditor"));
    }

    [Fact]
    public async Task Required_activation_fences_real_worker_startup_and_releases_only_after_owned_process_exit()
    {
        using var cell = new WorkerCell(); var job = cell.Queue();
        var authorityPath = Path.Combine(cell.Directory, "activation-authority");
        var authority = FoundationCellActivationAuthority.Provision(authorityPath, cell.Customer, cell.Cell);
        var binding = authority.Activate(cell.Database, cell.Store.GetCellInstanceIdentity().DatabaseInstanceId, 0, null);
        using var source = cell.Start("Disabled", activationAuthority: authorityPath); await source.Ready();
        var snapshot = cell.Store.CreateBackup(Path.Combine(cell.Directory, "snapshot.sqlite"));
        var restoredPath = Path.Combine(cell.Directory, "restored.sqlite");
        var restored = FoundationStore.RestoreBackup(snapshot, restoredPath);
        var restoredInstance = restored.GetCellInstanceIdentity().DatabaseInstanceId;
        Assert.Equal(0, restored.WorkerHealth(cell.Customer, "operations", "default", "dev").Live);
        Assert.Equal(0, restored.WorkerHealth(cell.Customer, "operations", "default", "dev").Disabled);
        Assert.Equal("OutcomeUnknown", restored.GetJob(cell.Customer, "operator", job.Id).State);
        Assert.Throws<FoundationStoreException>(() => authority.Activate(restoredPath, restoredInstance, binding.Generation, binding.Sha256));
        source.KillOwned(); _ = await source.Exit();
        FoundationCellActivationBinding? switched = null; var released = Stopwatch.StartNew();
        while (switched is null)
        {
            try { switched = authority.Activate(restoredPath, restoredInstance, binding.Generation, binding.Sha256); }
            catch (FoundationStoreException error) when (error.Code == "ActivationAuthorityBusy" && released.Elapsed < TimeSpan.FromSeconds(5))
            { await Task.Delay(25); }
        }
        Assert.Equal(2, switched.Generation);
        var auditBefore = cell.Scalar("SELECT count(*) FROM audit");
        using var denied = cell.Start("Disabled", runSeconds: 1, activationAuthority: authorityPath); Assert.Equal(2, await denied.Exit());
        Assert.Equal(auditBefore, cell.Scalar("SELECT count(*) FROM audit"));
        using var replacement = cell.Start("Disabled", runSeconds: 1, database: restoredPath, activationAuthority: authorityPath);
        await replacement.Ready(); await replacement.SuccessfulExit();
        Assert.Equal("RecoveryExecutionHeld", Assert.Throws<FoundationStoreException>(() => restored.ClaimNext(cell.Customer, "worker", TimeSpan.FromSeconds(5))).Code);
    }

    [Fact]
    public async Task Default_disabled_process_heartbeats_and_stops_without_claiming_a_queued_job()
    {
        using var cell = new WorkerCell(); var job = cell.Queue();
        using var worker = cell.Start("Disabled", runSeconds: 2);
        await worker.Ready();
        await WaitUntil(() => cell.Health().Disabled == 1);
        Assert.Equal("Queued", cell.Job(job).State);
        Assert.Equal(0, cell.Scalar("SELECT count(*) FROM audit WHERE action='JobClaimed'"));
        await worker.SuccessfulExit();
        Assert.Equal(1, cell.Health().Stopped); Assert.Equal("Queued", cell.Job(job).State);
    }

    [Fact]
    public async Task Synthetic_mode_requires_both_explicit_configuration_and_cli_opt_in()
    {
        using var cell = new WorkerCell(); var job = cell.Queue();
        using var worker = cell.Start("ClaimHold", runSeconds: 1, allowMock: false);
        Assert.Equal(2, await worker.Exit());
        Assert.Contains("SyntheticWorkerOptInRequired", worker.Error);
        Assert.Equal("Queued", cell.Job(job).State); Assert.Equal(0, cell.Health().Registered);
    }

    [Theory]
    [InlineData("missing")]
    [InlineData("binding")]
    [InlineData("schema")]
    public async Task Worker_rejects_missing_wrong_customer_or_old_schema_without_creating_or_migrating(string failure)
    {
        using var cell = new WorkerCell();
        var database = cell.Database;
        if (failure == "missing") database = Path.Combine(cell.Directory, "missing.sqlite");
        if (failure == "schema") cell.Execute("UPDATE cell SET schema_version=schema_version-1");
        var beforeAudit = cell.Scalar("SELECT count(*) FROM audit");
        using var worker = cell.Start("Disabled", runSeconds: 1, database: database, customer: failure == "binding" ? "other-customer" : null);
        Assert.Equal(2, await worker.Exit());
        Assert.Equal(beforeAudit, cell.Scalar("SELECT count(*) FROM audit"));
        if (failure == "missing") Assert.False(File.Exists(database));
        if (failure == "schema") Assert.Equal(FoundationStore.CurrentSchemaVersion - 1, cell.Scalar("SELECT schema_version FROM cell"));
    }

    [Fact]
    public async Task Two_real_workers_compete_for_one_queue_and_each_plan_dispatches_exactly_once_to_loopback()
    {
        await using var fixture = new LoopbackFixture(); using var cell = new WorkerCell();
        var jobs = Enumerable.Range(0, 6).Select(_ => cell.Queue()).ToArray();
        using var first = cell.Start("MockLoopback", fixture.Origin);
        using var second = cell.Start("MockLoopback", fixture.Origin);
        await Task.WhenAll(first.Ready(), second.Ready());
        await WaitUntil(() => jobs.All(j => cell.Job(j).State == "Succeeded"));
        Assert.Equal(6, fixture.Requests.Count);
        Assert.Equal(6, fixture.Requests.Select(r => r.PlanDigest).Distinct(StringComparer.Ordinal).Count());
        Assert.All(fixture.Requests, request => { Assert.True(request.Mock); Assert.False(request.Authorization); });
        Assert.All(jobs, job => { var observed = cell.Job(job); Assert.Equal(1, observed.Attempt); Assert.Equal(1, observed.Fence); Assert.Contains("\"mock\":true", observed.ReceiptJson); Assert.Contains("\"deploymentVerified\":false", observed.ReceiptJson); });
        Assert.Equal(2, cell.Health().Live);
        Assert.Equal(6, cell.Scalar("SELECT count(*) FROM audit WHERE action='JobClaimed'"));
    }

    [Fact]
    public async Task Crashed_pre_effect_process_leaves_stale_heartbeat_and_lease_recovers_with_a_new_fence()
    {
        await using var fixture = new LoopbackFixture(); using var cell = new WorkerCell(); var job = cell.Queue();
        using var crashed = cell.Start("ClaimHold"); await crashed.Ready();
        await WaitUntil(() => cell.Job(job).State == "Claimed");
        crashed.KillOwned(); Assert.Empty(fixture.Requests);
        await Task.Delay(TimeSpan.FromMilliseconds(2200));
        Assert.Equal(1, cell.Health(TimeSpan.FromSeconds(1)).Stale);
        using var recovered = cell.Start("MockLoopback", fixture.Origin); await recovered.Ready();
        await WaitUntil(() => cell.Job(job).State == "Succeeded");
        var completed = cell.Job(job); Assert.Equal(2, completed.Attempt); Assert.Equal(2, completed.Fence);
        Assert.Single(fixture.Requests);
        Assert.Equal(1, cell.Scalar("SELECT count(*) FROM audit WHERE action='JobLeaseExpired' AND reason='NoEffectDispatched'"));
    }

    [Fact]
    public async Task Crashed_post_effect_process_becomes_outcome_unknown_and_is_never_automatically_replayed()
    {
        await using var fixture = new LoopbackFixture(blockResponses: true); using var cell = new WorkerCell(); var job = cell.Queue();
        using var crashed = cell.Start("MockLoopback", fixture.Origin); await crashed.Ready();
        await WaitUntil(() => fixture.Requests.Count == 1 && cell.Job(job).State == "Dispatched");
        crashed.KillOwned();
        // Accelerate only this disposable fixture's two-minute deadline; the real process and dispatch path are unchanged.
        cell.Execute("UPDATE jobs SET lease_until=0 WHERE state='Dispatched'");
        using var recovery = cell.Start("MockLoopback", fixture.Origin); await recovery.Ready();
        await WaitUntil(() => cell.Job(job).State == "OutcomeUnknown");
        await Task.Delay(400);
        Assert.Single(fixture.Requests); Assert.Equal(1, cell.Job(job).Attempt);
        Assert.Equal(1, cell.Health().OutcomeUnknownJobs);
    }

    [Fact]
    public async Task Licensed_worker_uses_only_explicit_public_trust_and_rechecks_feature_before_dispatch()
    {
        await using var fixture = new LoopbackFixture(); using var cell = new WorkerCell(); using var key = RSA.Create(2048);
        var trust = new EntitlementTrustKey("fixture-issuer", "fixture-key", key.ExportSubjectPublicKeyInfoPem());
        var now = DateTimeOffset.FromUnixTimeSeconds(DateTimeOffset.UtcNow.ToUnixTimeSeconds());
        var doc = new EntitlementDocument(EntitlementVerifier.DocumentSchema, trust.Issuer, "worker-license", cell.Customer, cell.Cell, 1,
            "evaluation", FoundationFeatures.Available.ToArray(), now.AddMinutes(-1), now.AddHours(1), trust.KeyId);
        var signed = new SignedEntitlement(doc, Convert.ToBase64String(key.SignData(EntitlementVerifier.CanonicalDocument(doc), HashAlgorithmName.SHA256, RSASignaturePadding.Pss)));
        var verifier = new EntitlementVerifier([trust]);
        var change = cell.Store.PrepareConfigurationChange(cell.Customer, "admin", "default", "dev",
            new("Licensed", FoundationFeatures.Available.ToArray(), 100, "0.0.0", "2.0.0", signed), 0,
            "Synthetic worker public trust qualification", verifier, "1.0.0");
        cell.Store.ApproveAdministrativePlan(cell.Customer, "approver", change.Id, change.Digest);
        cell.Store.ExecuteConfigurationChange(cell.Customer, "admin", change.Id, change.Digest, verifier, "1.0.0");
        var deniedJob = cell.Queue();
        using (var untrusted = cell.Start("MockLoopback", fixture.Origin))
        {
            await untrusted.Ready(); await WaitUntil(() => cell.Job(deniedJob).State == "Blocked");
            Assert.Empty(fixture.Requests); Assert.Equal("DispatchFeatureUnavailable", cell.Job(deniedJob).Reason);
        }
        var trustedJob = cell.Queue();
        using var worker = cell.Start("MockLoopback", fixture.Origin, trust: [trust]); await worker.Ready();
        await WaitUntil(() => cell.Job(trustedJob).State == "Succeeded"); Assert.Single(fixture.Requests);
    }

    [Fact]
    public async Task Stop_script_refuses_changed_process_start_time_then_stops_only_the_owned_worker()
    {
        using var cell = new WorkerCell(); using var worker = cell.Start("Disabled"); await worker.Ready();
        var receipt = JsonSerializer.Deserialize<Dictionary<string, JsonElement>>(File.ReadAllText(worker.Receipt))!;
        receipt["processStartedUtc"] = JsonSerializer.SerializeToElement(worker.Started.AddSeconds(1));
        var forged = Path.Combine(cell.Directory, "wrong-owner.json"); File.WriteAllText(forged, JsonSerializer.Serialize(receipt));
        Assert.NotEqual(0, await worker.RunStopScript(forged)); Assert.False(worker.HasExited);
        Assert.True(await worker.RunStopScript(worker.Receipt) == 0, worker.HelperError); Assert.True(worker.HasExited);
    }

    [Fact]
    public async Task Launch_script_registers_a_separate_disabled_worker_and_its_receipt_survives_the_launcher()
    {
        using var cell = new WorkerCell(); var job = cell.Queue();
        using var worker = cell.Start("Disabled", throughLauncher: true);
        await worker.Ready(); Assert.Equal(1, cell.Health().Disabled);
        Assert.Equal("Queued", cell.Job(job).State);
        Assert.True(await worker.RunStopScript(worker.Receipt) == 0, worker.HelperError); Assert.True(worker.HasExited);
        Assert.True(File.Exists(worker.Receipt));
    }

    static async Task WaitUntil(Func<bool> condition)
    {
        var stop = Stopwatch.StartNew();
        while (stop.Elapsed < TimeSpan.FromSeconds(15)) { if (condition()) return; await Task.Delay(50); }
        Assert.True(condition(), "Synthetic independent worker condition did not complete within fifteen seconds.");
    }

    sealed class WorkerCell : IDisposable
    {
        public string Directory { get; } = Path.Combine(Path.GetTempPath(), "PlatformStudio-WorkerTests", Guid.NewGuid().ToString("N"));
        public string Database => Path.Combine(Directory, "cell.sqlite");
        public string Customer { get; } = Guid.NewGuid().ToString("D");
        public string Cell { get; } = Guid.NewGuid().ToString("D");
        public FoundationStore Store { get; }
        public WorkerCell()
        {
            if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Native Windows worker fixture required.");
            using var identity = WindowsIdentity.GetCurrent(); var sid = identity.User!; var acl = new DirectorySecurity();
            acl.SetOwner(sid); acl.SetAccessRuleProtection(true, false);
            acl.AddAccessRule(new FileSystemAccessRule(sid, FileSystemRights.FullControl, InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit,
                PropagationFlags.None, AccessControlType.Allow));
            new DirectoryInfo(Directory).Create(acl); Store = new(Database, Cell, Customer);
            Store.BootstrapAdministrator("admin");
            foreach (var pair in new[] { ("requester", "Requester"), ("approver", "Approver"), ("operator", "Operator"), ("operations", "PlatformOperator"), ("auditor", "Auditor") })
                Store.SetMembership(Customer, "admin", pair.Item1, "default", "dev", [pair.Item2]);
            var connection = new ConnectionCommand(PipelineRequestFactory.GitHub, "worker-org", null, "worker-repo", "validate.yml",
                ["main"], ["dev"], PipelineRequestFactory.ReservedInputs.Concat(["region"]).ToArray(), "profile/worker", true);
            Store.PutConnection(Customer, "admin", "pipeline", connection.Provider, JsonSerializer.Serialize(connection, FoundationApplication.Json));
        }
        public FoundationJob Queue()
        {
            var request = new PipelineDispatchRequest(Customer, Cell, "pipeline", 1, Guid.NewGuid().ToString("D"), "main", "dev", new string('a', 40), new() { ["region"] = "eastus2" });
            var plan = Store.CreatePlan(Customer, "requester", new("default", "dev", "pipeline", "pipeline.dispatch",
                JsonSerializer.Serialize(request, FoundationApplication.Json), true, DateTimeOffset.UtcNow.AddMinutes(20), request.SourceRevision));
            Store.ApprovePlan(Customer, "approver", plan.Id, plan.Digest);
            return Store.EnqueuePlan(Customer, "operator", plan.Id, plan.Digest, "request-" + Guid.NewGuid().ToString("N"));
        }
        public FoundationJob Job(FoundationJob job) => Store.GetJob(Customer, "operator", job.Id);
        public FoundationWorkerHealth Health(TimeSpan? staleAfter = null) => Store.WorkerHealth(Customer, "operations", "default", "dev", staleAfter);
        public OwnedWorker Start(string mode, Uri? origin = null, int runSeconds = 30, bool allowMock = true,
            string? database = null, string? customer = null, EntitlementTrustKey[]? trust = null, bool throughLauncher = false,
            string? activationAuthority = null)
        {
            var id = Guid.NewGuid().ToString("N"); var config = Path.Combine(Directory, id + ".json"); var receipt = Path.Combine(Directory, id + ".receipt.json");
            File.WriteAllText(config, JsonSerializer.Serialize(new { customerId = customer ?? Customer, cellId = Cell, databasePath = database ?? Database,
                mode, qualificationOnly = mode != "Disabled", mockEndpoint = origin?.ToString(), workerId = "fixture-" + id,
                pollIntervalMilliseconds = 50, heartbeatIntervalMilliseconds = 250, runSeconds, receiptPath = receipt, entitlementTrustKeys = trust,
                requireActivationAuthority = activationAuthority is not null, activationAuthorityDirectory = activationAuthority }, FoundationApplication.Json));
            return new(config, receipt, allowMock && mode != "Disabled", Directory, throughLauncher);
        }
        public long Scalar(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        public void Execute(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; command.ExecuteNonQuery(); }
        SqliteConnection Open() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); db.Open(); return db; }
        public void Dispose()
        {
            // Every child is disposed before its cell. Delete known direct fixture files only; never recursively follow paths.
            var expected = Path.GetFullPath(Path.Combine(Path.GetTempPath(), "PlatformStudio-WorkerTests")) + Path.DirectorySeparatorChar;
            var full = Path.GetFullPath(Directory);
            if (!full.StartsWith(expected, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(Path.GetFileName(full), "N", out _))
                throw new InvalidOperationException("Fixture cleanup path was not owned.");
            var authority = Path.Combine(full, "activation-authority");
            if (System.IO.Directory.Exists(authority))
            {
                Assert.False(File.GetAttributes(authority).HasFlag(FileAttributes.ReparsePoint));
                foreach (var path in System.IO.Directory.EnumerateFiles(authority)) File.Delete(path);
                System.IO.Directory.Delete(authority, false);
            }
            foreach (var path in System.IO.Directory.EnumerateFiles(Directory)) File.Delete(path);
            System.IO.Directory.Delete(Directory, false);
        }
    }

    sealed class OwnedWorker : IDisposable
    {
        readonly Process process;
        readonly string executable;
        readonly string directory;
        readonly Task<string> output;
        readonly Task<string> error;
        public string Receipt { get; }
        public DateTimeOffset Started { get; }
        public bool HasExited => process.HasExited;
        public string Error => error.IsCompletedSuccessfully ? error.Result : "";
        public string HelperError { get; private set; } = "";
        public OwnedWorker(string config, string receipt, bool allowMock, string directory, bool throughLauncher = false)
        {
            this.directory = directory; Receipt = receipt;
            var dotnetRoot = Environment.GetEnvironmentVariable("DOTNET_ROOT");
            executable = dotnetRoot is not null ? Path.Combine(dotnetRoot, "dotnet.exe") : Path.Combine(Path.GetDirectoryName(Environment.ProcessPath)!, "dotnet.exe");
            if (!File.Exists(executable)) executable = "C:\\IT\\Package\\dotnet\\dotnet.exe";
            if (throughLauncher)
            {
                var launch = StartInfo(PowerShellPath(), directory); launch.ArgumentList.Add("-NoProfile"); launch.ArgumentList.Add("-NonInteractive");
                launch.ArgumentList.Add("-File"); launch.ArgumentList.Add(Path.Combine(RepositoryRoot(), "scripts", "Run-FoundationWorker.ps1"));
                launch.ArgumentList.Add("-ConfigurationPath"); launch.ArgumentList.Add(config); launch.ArgumentList.Add("-DotNetPath"); launch.ArgumentList.Add(executable);
                launch.ArgumentList.Add("-WorkerDllPath"); launch.ArgumentList.Add(WorkerAssembly());
                if (allowMock) launch.ArgumentList.Add("-EnableMockExecution");
                using var launcher = Process.Start(launch)!;
                var launcherOutput = launcher.StandardOutput.ReadToEndAsync(); var launcherError = launcher.StandardError.ReadToEndAsync();
                if (!launcher.WaitForExit(15000)) { launcher.Kill(true); throw new InvalidOperationException("Owned launcher timed out."); }
                if (launcher.ExitCode != 0) Assert.Fail(launcherError.GetAwaiter().GetResult());
                using var ownership = JsonDocument.Parse(File.ReadAllText(receipt));
                process = Process.GetProcessById(ownership.RootElement.GetProperty("processId").GetInt32());
                Started = ownership.RootElement.GetProperty("processStartedUtc").GetDateTimeOffset();
                Assert.Equal(Started.UtcTicks, new DateTimeOffset(process.StartTime.ToUniversalTime()).UtcTicks);
                Assert.Equal(Path.GetFullPath(executable), Path.GetFullPath(process.MainModule!.FileName), ignoreCase: true);
                output = launcherOutput; error = launcherError;
                if (!Task.WhenAll(output, error).Wait(5000))
                {
                    KillOwned(); Assert.Fail("Detached worker retained the launcher's capture pipes.");
                }
                return;
            }
            var start = StartInfo(executable, directory); start.ArgumentList.Add(WorkerAssembly()); start.ArgumentList.Add("--config"); start.ArgumentList.Add(config);
            if (allowMock) start.ArgumentList.Add("--allow-mock-execution");
            process = Process.Start(start)!; Started = process.StartTime.ToUniversalTime();
            output = process.StandardOutput.ReadToEndAsync(); error = process.StandardError.ReadToEndAsync();
        }
        public async Task Ready()
        {
            await WaitUntil(() => File.Exists(Receipt) || process.HasExited);
            if (process.HasExited) { await Exit(); Assert.Fail("Synthetic worker exited before registration: " + Error); }
            await WaitUntil(() =>
            {
                try { using var receipt = JsonDocument.Parse(File.ReadAllText(Receipt)); return receipt.RootElement.GetProperty("processId").GetInt32() == process.Id; }
                catch (JsonException) { return false; }
                // Publication is complete; another native reader can still briefly hold an exclusive handle.
                // Match the launcher's bounded readiness retry without hiding other I/O failures.
                catch (IOException error) when ((error.HResult & 0xffff) is 32 or 33) { return false; }
            });
        }
        public async Task<int> Exit() { using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(40)); await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(output, error); return process.ExitCode; }
        public async Task SuccessfulExit() => Assert.Equal(0, await Exit());
        public void KillOwned()
        {
            if (process.HasExited) return;
            Assert.Equal(Started.UtcTicks, new DateTimeOffset(process.StartTime.ToUniversalTime()).UtcTicks);
            Assert.Equal(Path.GetFullPath(executable), Path.GetFullPath(process.MainModule!.FileName), ignoreCase: true);
            process.Kill(entireProcessTree: true); Assert.True(process.WaitForExit(5000));
        }
        public async Task<int> RunStopScript(string receipt)
        {
            var start = StartInfo(PowerShellPath(), directory); start.ArgumentList.Add("-NoProfile"); start.ArgumentList.Add("-NonInteractive");
            start.ArgumentList.Add("-File"); start.ArgumentList.Add(Path.Combine(RepositoryRoot(), "scripts", "Run-FoundationWorker.ps1"));
            start.ArgumentList.Add("-Stop"); start.ArgumentList.Add("-ReceiptPath"); start.ArgumentList.Add(receipt);
            using var helper = Process.Start(start)!; var stdout = helper.StandardOutput.ReadToEndAsync(); var stderr = helper.StandardError.ReadToEndAsync();
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(15));
            try { await helper.WaitForExitAsync(timeout.Token); } catch (OperationCanceledException) { helper.Kill(true); throw; }
            await Task.WhenAll(stdout, stderr); HelperError = stderr.Result; return helper.ExitCode;
        }
        public void Dispose() { KillOwned(); process.Dispose(); }
        static string PowerShellPath() => (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator)
            .Where(p => !string.IsNullOrWhiteSpace(p)).Select(p => Path.Combine(p, "pwsh.exe")).FirstOrDefault(File.Exists)
            ?? throw new InvalidOperationException("PowerShell 7.4 or later must be available on PATH for launcher qualification.");
        static ProcessStartInfo StartInfo(string executable, string directory)
        {
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = directory };
            start.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "windir", "ComSpec" }) if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(executable)!;
            start.Environment["PATH"] = Path.GetDirectoryName(executable) + ";" + Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32");
            start.Environment["TEMP"] = directory; start.Environment["TMP"] = directory;
            start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1"; start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"] = "false";
            return start;
        }
        static string WorkerAssembly()
        {
            var artifactBin = new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!;
            var candidates = new[] { Path.Combine(artifactBin.FullName, "SelfService.Foundation.Worker", "release", "SelfService.Foundation.Worker.dll"),
                Path.Combine(artifactBin.FullName, "SelfService.Foundation.Worker", "debug", "SelfService.Foundation.Worker.dll"),
                Path.Combine(RepositoryRoot(), "src", "SelfService.Foundation.Worker", "bin", "Release", "net10.0", "SelfService.Foundation.Worker.dll"),
                Path.Combine(RepositoryRoot(), "src", "SelfService.Foundation.Worker", "bin", "Debug", "net10.0", "SelfService.Foundation.Worker.dll") };
            return candidates.FirstOrDefault(File.Exists) ?? throw new InvalidOperationException("Build the Worker project through the Foundation test project reference before process qualification.");
        }
        static string RepositoryRoot()
        {
            for (var current = new DirectoryInfo(AppContext.BaseDirectory); current is not null; current = current.Parent)
                if (File.Exists(Path.Combine(current.FullName, "scripts", "Run-FoundationWorker.ps1"))) return current.FullName;
            throw new InvalidOperationException("Foundation repository root not found.");
        }
    }

    sealed record FixtureRequest(string PlanDigest, bool Mock, bool Authorization);
    sealed class LoopbackFixture : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new();
        readonly ConcurrentBag<Task> clients = [];
        readonly Task accepting;
        readonly bool blockResponses;
        public ConcurrentQueue<FixtureRequest> Requests { get; } = new();
        public Uri Origin { get; }
        public LoopbackFixture(bool blockResponses = false)
        {
            this.blockResponses = blockResponses; listener.Start(); Origin = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/");
            accepting = Accept();
        }
        async Task Accept()
        {
            try { while (!stop.IsCancellationRequested) { var client = await listener.AcceptTcpClientAsync(stop.Token); clients.Add(Respond(client)); } }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        async Task Respond(TcpClient client)
        {
            using (client)
            {
                try
                {
                    using var stream = client.GetStream(); var headerBytes = new List<byte>(); var one = new byte[1];
                    while (headerBytes.Count < 16384)
                    {
                        if (await stream.ReadAsync(one, stop.Token) != 1) return;
                        headerBytes.Add(one[0]);
                        if (headerBytes.Count >= 4 && Encoding.ASCII.GetString(headerBytes.TakeLast(4).ToArray()) == "\r\n\r\n") break;
                    }
                    var lines = Encoding.ASCII.GetString(headerBytes.ToArray()).Split("\r\n", StringSplitOptions.RemoveEmptyEntries);
                    Assert.StartsWith("POST /repos/worker-org/worker-repo/actions/workflows/validate.yml/dispatches", lines[0]);
                    var headers = lines.Skip(1).Select(line => line.Split(':', 2)).ToDictionary(p => p[0], p => p[1].Trim(), StringComparer.OrdinalIgnoreCase);
                    var length = int.Parse(headers["Content-Length"]); Assert.InRange(length, 1, 65536);
                    var bytes = new byte[length]; await stream.ReadExactlyAsync(bytes, stop.Token);
                    using var body = JsonDocument.Parse(bytes);
                    Requests.Enqueue(new(body.RootElement.GetProperty("inputs").GetProperty("studio_plan_digest").GetString()!,
                        headers.TryGetValue("X-Platform-Studio-Mock", out var mock) && mock == "true", headers.ContainsKey("Authorization")));
                    if (blockResponses) await Task.Delay(Timeout.InfiniteTimeSpan, stop.Token);
                    var id = 1000 + Requests.Count;
                    var response = JsonSerializer.SerializeToUtf8Bytes(new { workflow_run_id = id,
                        run_url = $"https://api.github.com/repos/worker-org/worker-repo/actions/runs/{id}",
                        html_url = $"https://github.com/worker-org/worker-repo/actions/runs/{id}", head_sha = new string('a', 40) });
                    await stream.WriteAsync(Encoding.ASCII.GetBytes($"HTTP/1.1 200 OK\r\nContent-Type: application/json\r\nContent-Length: {response.Length}\r\nConnection: close\r\n\r\n"), stop.Token);
                    await stream.WriteAsync(response, stop.Token);
                }
                catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
                catch (IOException) { }
            }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); listener.Stop(); await accepting; await Task.WhenAll(clients); stop.Dispose(); }
    }
}
