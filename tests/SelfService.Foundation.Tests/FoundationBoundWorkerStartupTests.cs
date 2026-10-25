using System.Diagnostics;
using System.Globalization;
using System.Net;
using System.Net.Sockets;
using System.Runtime.Versioning;
using System.Security.AccessControl;
using System.Security.Cryptography;
using System.Text.Json;
using Microsoft.Data.Sqlite;
using SelfService.Foundation;
using SelfService.Foundation.Host;
using Xunit;

namespace SelfService.Foundation.Tests;

[SupportedOSPlatform("windows")]
public sealed class FoundationBoundWorkerStartupTests
{
    [Theory]
    [InlineData("MockLoopback")]
    [InlineData("ClaimHold")]
    public async Task Bound_changing_CLI_refuses_before_registration_heartbeat_claim_or_POST(string mode)
    {
        using var f = await WorkerBoundCell.Create(); await using var loopback = new LoopbackProbe();
        var job = f.Queue(); var config = f.Configuration(mode, loopback.Origin);
        var rows = f.Rows(); var credentials = f.CredentialFiles(); var database = f.DatabaseHash();
        using var child = f.Start(config);
        Assert.Equal(2, await child.Exit());
        using var error = JsonDocument.Parse(child.Error);
        Assert.Equal("BoundPipelineConsumerRequired", error.RootElement.GetProperty("error").GetString());
        Assert.Empty(child.Output); Assert.Equal(rows, f.Rows()); Assert.Equal(database, f.DatabaseHash());
        Assert.Equal(credentials, f.CredentialFiles()); Assert.Equal(0, f.Number("SELECT count(*) FROM worker_heartbeats"));
        Assert.Equal("Queued", f.Store.GetJob(f.Customer, "operator", job.Id).State);
        Assert.Equal(0, loopback.Connections); Assert.False(File.Exists(config.Receipt)); Assert.False(File.Exists(config.Receipt + ".reserve"));
        Assert.DoesNotContain(CredentialFixture.Token, child.Error);
    }

    [Theory]
    [InlineData("MockLoopback")]
    [InlineData("ClaimHold")]
    public async Task Configuration_only_preflight_on_bound_cell_remains_keyfree_software_and_path_validation(string mode)
    {
        using var f = await WorkerBoundCell.Create(); await using var loopback = new LoopbackProbe();
        _ = f.Queue(); var config = f.Configuration(mode, loopback.Origin);
        var rows = f.Rows(); var credentials = f.CredentialFiles(); var database = f.DatabaseHash();
        using var child = f.Start(config, validateOnly: true); Assert.Equal(0, await child.Exit()); Assert.Empty(child.Error);
        using var output = JsonDocument.Parse(child.Output); var result = output.RootElement;
        Assert.Equal("platform-studio.foundation-worker-preflight.v1", result.GetProperty("schema").GetString());
        Assert.True(result.GetProperty("receiptPathValidated").GetBoolean()); Assert.False(result.GetProperty("workerStartupPerformed").GetBoolean());
        Assert.False(result.GetProperty("externalDispatch").GetBoolean());
        Assert.Equal(Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(config.Path))), result.GetProperty("configurationSha256").GetString());
        Assert.Equal(rows, f.Rows()); Assert.Equal(database, f.DatabaseHash()); Assert.Equal(credentials, f.CredentialFiles());
        Assert.Equal(0, loopback.Connections); Assert.False(File.Exists(config.Receipt)); Assert.False(File.Exists(config.Receipt + ".reserve"));
    }

    [Fact]
    public async Task Bound_Disabled_CLI_still_registers_and_stops_without_claiming_approved_work()
    {
        using var f = await WorkerBoundCell.Create(); var job = f.Queue(); var config = f.Configuration("Disabled");
        var before = f.JobRows(); var credentials = f.CredentialFiles();
        using var child = f.Start(config); Assert.Equal(0, await child.Exit()); Assert.Empty(child.Error);
        Assert.True(File.Exists(config.Receipt)); Assert.False(File.Exists(config.Receipt + ".reserve"));
        Assert.Equal(before, f.JobRows()); Assert.Equal("Queued", f.Store.GetJob(f.Customer, "operator", job.Id).State);
        Assert.Equal(1, f.Number("SELECT count(*) FROM worker_heartbeats WHERE state='Stopped'"));
        Assert.Equal(credentials, f.CredentialFiles());
    }

    [Fact]
    public async Task Never_bound_ClaimHold_CLI_still_claims_approved_synthetic_work()
    {
        using var f = await WorkerBoundCell.Create(bind: false); var job = f.Queue(); var config = f.Configuration("ClaimHold");
        using var child = f.Start(config); Assert.Equal(0, await child.Exit()); Assert.Empty(child.Error);
        Assert.Equal("Claimed", f.Store.GetJob(f.Customer, "operator", job.Id).State); Assert.True(File.Exists(config.Receipt));
        Assert.Equal(1, f.Number("SELECT count(*) FROM worker_heartbeats WHERE state='Stopped'"));
    }

    sealed record WorkerConfig(string Path, string Receipt, string Mode);

    sealed class WorkerBoundCell : IDisposable
    {
        readonly CredentialFixture template = new();
        readonly string vaultRoot, rollbackRoot;
        public string Customer => template.Scope.CustomerId;
        public string Cell => template.Scope.CellId;
        public string Database { get; }
        public FoundationStore Store { get; }
        readonly FoundationCellActivationAuthority authority;

        WorkerBoundCell(bool bind)
        {
            Database = Path.Combine(template.Root, "worker-bound.sqlite"); Store = new(Database, Cell, Customer);
            Store.BootstrapAdministrator("admin");
            foreach (var (actor, role) in new[] { ("requester", "Requester"), ("approver", "Approver"), ("operator", "Operator") })
                Store.SetMembership(Customer, "admin", actor, "default", "dev", [role]);
            Store.PutConnection(Customer, "admin", template.Connection.Id, template.Connection.Provider, template.Dispatch.Connection.ConfigurationJson);
            vaultRoot = Path.Combine(template.Root, "worker-vault"); rollbackRoot = Path.Combine(template.Root, "worker-rollback");
            authority = FoundationCellActivationAuthority.Provision(Path.Combine(template.Root, "worker-authority"), Customer, Cell);
            _ = authority.Activate(Database, Store.GetCellInstanceIdentity().DatabaseInstanceId, 0, null);
            Vault = CredentialVault.ProvisionStrong(template.Options with { RootPath = vaultRoot, RollbackProtection = new(rollbackRoot) }, template.Keys);
            if (bind)
                _ = FoundationCredentialBoundTransport.BindCurrent(Store, Vault, authority, Vault.DescribeNamespace(), authority.DescribeIdentity(),
                    Store.GetCellInstanceIdentity().DatabaseInstanceId);
        }

        CredentialVault Vault { get; }
        public static async Task<WorkerBoundCell> Create(bool bind = true)
        {
            var f = new WorkerBoundCell(bind);
            try { using var secret = CredentialSecret.FromUtf8(CredentialFixture.Token); await f.Vault.PutAsync(f.template.Scope, secret, DateTimeOffset.UtcNow.AddHours(1)); return f; }
            catch { f.Dispose(); throw; }
        }
        public FoundationJob Queue()
        {
            var plan = Store.CreatePlan(Customer, "requester", new("default", "dev", template.Connection.Id, "pipeline.dispatch",
                JsonSerializer.Serialize(template.Request, FoundationApplication.Json), true, DateTimeOffset.UtcNow.AddMinutes(20), template.Request.SourceRevision));
            Store.ApprovePlan(Customer, "approver", plan.Id, plan.Digest);
            return Store.EnqueuePlan(Customer, "operator", plan.Id, plan.Digest, "worker-bound-request");
        }
        public WorkerConfig Configuration(string mode, Uri? origin = null)
        {
            var path = Path.Combine(template.Root, "worker-config.json"); var receipt = Path.Combine(template.Root, "worker-receipt.json");
            File.WriteAllText(path, JsonSerializer.Serialize(new
            {
                customerId = Customer, cellId = Cell, databasePath = Database, mode, qualificationOnly = mode != "Disabled",
                mockEndpoint = mode == "MockLoopback" ? origin!.ToString() : null,
                workerId = "standalone-test-worker", pollIntervalMilliseconds = 50, heartbeatIntervalMilliseconds = 250, runSeconds = 1,
                receiptPath = receipt, requireActivationAuthority = true, activationAuthorityDirectory = Path.Combine(template.Root, "worker-authority")
            }, FoundationApplication.Json));
            return new(path, receipt, mode);
        }
        public OwnedChild Start(WorkerConfig config, bool validateOnly = false) => new(config, template.Root, validateOnly);
        public string CredentialFiles() => string.Join("\n", new[] { vaultRoot, rollbackRoot }.SelectMany(root => Directory.GetFiles(root))
            .Order(StringComparer.Ordinal).Select(path => Path.GetRelativePath(template.Root, path) + ":" + Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(path)))
                + ":" + Convert.ToBase64String(new FileInfo(path).GetAccessControl().GetSecurityDescriptorBinaryForm())));
        public string DatabaseHash() => Convert.ToHexStringLower(SHA256.HashData(File.ReadAllBytes(Database)));
        public long Number(string sql) { using var db = Open(); using var command = db.CreateCommand(); command.CommandText = sql; return Convert.ToInt64(command.ExecuteScalar()); }
        public string JobRows() => Rows("jobs");
        public string Rows() => string.Join("\n", new[] { "jobs", "worker_heartbeats", "audit", "credential_runtime_bindings" }.Select(Rows));
        string Rows(string table)
        {
            using var db = Open(); using var command = db.CreateCommand(); command.CommandText = "SELECT * FROM " + table + " ORDER BY rowid";
            using var reader = command.ExecuteReader(); var rows = new List<string[]>();
            while (reader.Read()) rows.Add(Enumerable.Range(0, reader.FieldCount).Select(i => Convert.ToString(reader.GetValue(i), CultureInfo.InvariantCulture)!).ToArray());
            return table + ":" + JsonSerializer.Serialize(rows);
        }
        SqliteConnection Open() { var db = new SqliteConnection(new SqliteConnectionStringBuilder { DataSource = Database, Pooling = false }.ToString()); db.Open(); return db; }
        public void Dispose() => template.Dispose();
    }

    sealed class OwnedChild : IDisposable
    {
        readonly Process process;
        readonly Task<string> output, error;
        public string Output => output.GetAwaiter().GetResult();
        public string Error => error.GetAwaiter().GetResult();
        public OwnedChild(WorkerConfig config, string root, bool validateOnly)
        {
            var executable = Path.Combine(Environment.GetEnvironmentVariable("DOTNET_ROOT") ?? "C:\\IT\\Package\\dotnet", "dotnet.exe");
            var worker = Path.Combine(new DirectoryInfo(AppContext.BaseDirectory).Parent!.Parent!.FullName,
                "SelfService.Foundation.Worker", "release", "SelfService.Foundation.Worker.dll");
            Assert.True(File.Exists(executable)); Assert.True(File.Exists(worker));
            var start = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, RedirectStandardOutput = true, RedirectStandardError = true, WorkingDirectory = root };
            start.Environment.Clear();
            foreach (var name in new[] { "SystemRoot", "windir", "ComSpec" }) if (Environment.GetEnvironmentVariable(name) is { } value) start.Environment[name] = value;
            start.Environment["DOTNET_ROOT"] = Path.GetDirectoryName(executable)!;
            start.Environment["PATH"] = Path.GetDirectoryName(executable) + ";" + Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!, "System32");
            start.Environment["TEMP"] = root; start.Environment["TMP"] = root; start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"] = "1";
            start.ArgumentList.Add(worker); start.ArgumentList.Add(validateOnly ? "--validate-config" : "--config"); start.ArgumentList.Add(config.Path);
            if (!validateOnly && config.Mode != "Disabled") start.ArgumentList.Add("--allow-mock-execution");
            process = Process.Start(start)!; output = process.StandardOutput.ReadToEndAsync(); error = process.StandardError.ReadToEndAsync();
        }
        public async Task<int> Exit()
        {
            using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(30));
            await process.WaitForExitAsync(timeout.Token); await Task.WhenAll(output, error); return process.ExitCode;
        }
        public void Dispose() { if (!process.HasExited) { process.Kill(entireProcessTree: true); Assert.True(process.WaitForExit(5000)); } process.Dispose(); }
    }

    sealed class LoopbackProbe : IAsyncDisposable
    {
        readonly TcpListener listener = new(IPAddress.Loopback, 0);
        readonly CancellationTokenSource stop = new();
        readonly Task accepting;
        int connections;
        public int Connections => Volatile.Read(ref connections);
        public Uri Origin { get; }
        public LoopbackProbe()
        {
            listener.Start(); Origin = new Uri($"http://127.0.0.1:{((IPEndPoint)listener.LocalEndpoint).Port}/"); accepting = Accept();
        }
        async Task Accept()
        {
            try { while (true) { using var client = await listener.AcceptTcpClientAsync(stop.Token); Interlocked.Increment(ref connections); } }
            catch (OperationCanceledException) when (stop.IsCancellationRequested) { }
        }
        public async ValueTask DisposeAsync() { stop.Cancel(); await accepting; listener.Stop(); stop.Dispose(); }
    }
}
