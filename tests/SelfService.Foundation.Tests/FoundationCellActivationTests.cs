using System.Diagnostics;
using System.Security.AccessControl;
using System.Security.Principal;
using System.Text;
using SelfService.Foundation;
using Xunit;

namespace SelfService.Foundation.Tests;

public sealed class FoundationCellActivationTests
{
    [Fact]
    public void Shared_runtime_guards_require_all_source_runtimes_to_stop_before_switching_to_restored_copy()
    {
        using var f = new ActivationFixture(); var authority = f.Provision();
        Assert.Null(authority.ReadCurrent());
        Assert.Equal("ActivationBindingMissing", Error(() => authority.AcquireLease(f.Cell.Path, f.Instance)));
        var one = authority.Activate(f.Cell.Path, f.Instance, 0, null);
        var api = authority.AcquireLease(f.Cell.Path, f.Instance); var worker = authority.AcquireLease(f.Cell.Path, f.Instance);
        var snapshot = f.Cell.Store.CreateBackup(f.Path("backup.sqlite"));
        var recoveredPath = f.Path("recovered.sqlite"); var recovered = FoundationStore.RestoreBackup(snapshot, recoveredPath, f.Cell.Clock);
        var recoveredId = recovered.GetCellInstanceIdentity().DatabaseInstanceId;
        Assert.NotEqual(f.Instance, recoveredId);
        try
        {
            api.AssertCurrent(f.Cell.Path, f.Instance); worker.AssertCurrent();
            Assert.Equal("ActivationAuthorityBusy", Error(() => authority.Activate(recoveredPath, recoveredId, one.Generation, one.Sha256)));
            api.Dispose();
            Assert.Equal("ActivationAuthorityBusy", Error(() => authority.Activate(recoveredPath, recoveredId, one.Generation, one.Sha256)));
            worker.Dispose();
            var two = authority.Activate(recoveredPath, recoveredId, one.Generation, one.Sha256);
            Assert.Equal(2, two.Generation); Assert.Equal(one.Sha256, two.PreviousSha256);
            Assert.Equal("ActivationBindingDenied", Error(() => authority.AcquireLease(f.Cell.Path, f.Instance)));
            using var active = authority.AcquireLease(recoveredPath, recoveredId); active.AssertCurrent(recoveredPath, recoveredId);
            Assert.Equal("ActivationLeaseStale", Error(() => active.AssertCurrent(recoveredPath, f.Instance)));
            Assert.Equal("ActivationLeaseDisposed", Error(() => api.AssertCurrent()));
        }
        finally { api.Dispose(); worker.Dispose(); }
    }

    [Fact]
    public void Compare_and_swap_generation_and_checksum_prevent_stale_or_duplicate_activation()
    {
        using var f = new ActivationFixture(); var authority = f.Provision(); var one = authority.Activate(f.Cell.Path, f.Instance, 0, null);
        Assert.Equal("ActivationGenerationChanged", Error(() => authority.Activate(f.Cell.Path, f.Instance, 0, null)));
        Assert.Equal("ActivationGenerationChanged", Error(() => authority.Activate(f.Cell.Path, f.Instance, 1, new string('0', 64))));
        Assert.Equal("ActivationBindingAlreadyCurrent", Error(() => authority.Activate(f.Cell.Path, f.Instance, 1, one.Sha256)));
        Assert.Equal("ActivationExpectedStateInvalid", Error(() => authority.Activate(f.Cell.Path, f.Instance, 0, one.Sha256)));
        Assert.Equal("ActivationExpectedStateInvalid", Error(() => authority.Activate(f.Cell.Path, f.Instance, FoundationCellActivationAuthority.MaximumGenerations, one.Sha256)));
        Assert.Equal("ActivationInstanceMismatch", Error(() => authority.Activate(f.Cell.Path, new string('b', 32), 1, one.Sha256)));
        Assert.Equal(one, new FoundationCellActivationAuthority(f.AuthorityPath, f.Cell.Customer, f.Cell.Cell, f.Cell.Clock).ReadCurrent());
        Assert.Single(Directory.GetFiles(f.AuthorityPath, "activation-*.json"));
    }

    [Fact]
    public void Missing_mismatched_and_reprovisioned_authority_never_fall_back_or_create_state()
    {
        using var f = new ActivationFixture();
        Assert.Equal("ActivationAuthorityMissing", Error(() => new FoundationCellActivationAuthority(f.AuthorityPath, f.Cell.Customer, f.Cell.Cell)));
        Assert.False(Directory.Exists(f.AuthorityPath));
        f.Provision();
        Assert.Equal("ActivationAuthorityAlreadyExists", Error(() => f.Provision()));
        Assert.Equal("ActivationAuthorityInvalid", Error(() => new FoundationCellActivationAuthority(f.AuthorityPath, "customer-b", f.Cell.Cell, f.Cell.Clock)));
        Assert.Equal("ActivationAuthorityInvalid", Error(() => new FoundationCellActivationAuthority(f.AuthorityPath, f.Cell.Customer, "cell-b", f.Cell.Clock)));
    }

    [Fact]
    public void No_argument_assertion_rechecks_actual_database_identity_and_disposed_leases_fail_without_opening_database()
    {
        using var f = new ActivationFixture(); var authority = f.Provision(); authority.Activate(f.Cell.Path, f.Instance, 0, null);
        using var lease = authority.AcquireLease(f.Cell.Path, f.Instance);
        f.Cell.Execute("UPDATE cell SET database_instance_id=lower(hex(randomblob(16))) WHERE singleton=1");
        Assert.Equal("ActivationLeaseStale", Error(() => lease.AssertCurrent()));
        lease.Dispose();
        Assert.Equal("ActivationLeaseDisposed", Error(() => lease.AssertCurrent()));
    }

    [Fact]
    public void Lease_overload_reads_actual_identity_under_guard_and_rejects_nonlocal_paths_before_database_access()
    {
        using var f = new ActivationFixture(); var authority = f.Provision(); authority.Activate(f.Cell.Path, f.Instance, 0, null);
        using var actual = authority.AcquireLease(f.Cell.Path); actual.AssertCurrent();
        Assert.Equal(f.Instance, actual.Binding.DatabaseInstanceId);
        Assert.Equal("ActivationPathInvalid", Error(() => authority.AcquireLease("relative.sqlite")));
        if (OperatingSystem.IsWindows())
        {
            // These deliberately unreachable paths must fail lexical validation, rather than trying a SQLite/SMB open.
            foreach (var nonlocal in new[] { "\\\\192.0.2.1\\nonexistent-share\\cell.sqlite", "//192.0.2.1/nonexistent-share/cell.sqlite", "\\\\?\\UNC\\192.0.2.1\\share\\cell.sqlite" })
                Assert.Equal("ActivationPathInvalid", Error(() => authority.AcquireLease(nonlocal)));
        }
        var restored = f.Recover();
        Assert.Equal("ActivationBindingDenied", Error(() => authority.AcquireLease(restored.Path)));
    }

    [Theory]
    [InlineData("checksum")]
    [InlineData("unknown")]
    [InlineData("duplicate")]
    [InlineData("oversized")]
    [InlineData("temporary")]
    public void Altered_or_partial_metadata_fails_closed_without_overwriting_history(string attack)
    {
        using var f = new ActivationFixture(); var authority = f.Provision(); authority.Activate(f.Cell.Path, f.Instance, 0, null);
        using var lease = authority.AcquireLease(f.Cell.Path, f.Instance);
        var record = System.IO.Path.Combine(f.AuthorityPath, "activation-0001.json"); var original = File.ReadAllText(record);
        var changed = attack switch
        {
            "checksum" => original.Replace(f.Instance, new string('b', 32), StringComparison.Ordinal),
            "unknown" => original.Insert(1, "\"unknown\":true,"),
            "duplicate" => original.Insert(1, "\"SCHEMA\":1,"),
            "oversized" => new string(' ', FoundationCellActivationAuthority.MaximumMetadataBytes + 1),
            _ => original
        };
        File.WriteAllText(record, changed);
        if (attack == "temporary") File.WriteAllText(System.IO.Path.Combine(f.AuthorityPath, ".write-abandoned.tmp"), "partial");
        Assert.Equal("ActivationAuthorityInvalid", Error(() => lease.AssertCurrent()));
        Assert.Equal("ActivationAuthorityInvalid", Error(() => authority.ReadCurrent()));
        Assert.Equal(changed, File.ReadAllText(record));
    }

    [Fact]
    public void Path_and_instance_validation_reject_missing_relative_network_and_embedded_database_files()
    {
        using var f = new ActivationFixture(); var authority = f.Provision();
        Assert.Equal("ActivationPathInvalid", Error(() => authority.Activate("relative.sqlite", f.Instance, 0, null)));
        Assert.Equal("ActivationDatabasePathInvalid", Error(() => authority.Activate(f.Path("missing.sqlite"), f.Instance, 0, null)));
        Assert.Equal("ActivationInstanceInvalid", Error(() => authority.Activate(f.Cell.Path, Guid.NewGuid().ToString("D"), 0, null)));
        var embedded = System.IO.Path.Combine(f.AuthorityPath, "embedded.sqlite"); File.WriteAllText(embedded, "synthetic");
        Assert.Equal("ActivationDatabasePathInvalid", Error(() => authority.Activate(embedded, f.Instance, 0, null)));
        File.Delete(embedded);
        if (OperatingSystem.IsWindows())
        {
            Assert.Equal("ActivationPathInvalid", Error(() => authority.Activate("\\\\server\\share\\cell.sqlite", f.Instance, 0, null)));
            Assert.Equal("ActivationPathInvalid", Error(() => authority.Activate(f.Cell.Path + ".", f.Instance, 0, null)));
        }
    }

    [Fact]
    public void Independent_authority_roots_do_not_share_leases_or_customer_bindings()
    {
        using var a = new ActivationFixture(); using var b = new ActivationFixture("customer-b", "cell-b");
        var first = a.Provision(); var second = b.Provision();
        first.Activate(a.Cell.Path, a.Instance, 0, null); second.Activate(b.Cell.Path, b.Instance, 0, null);
        using var lease = first.AcquireLease(a.Cell.Path, a.Instance);
        Assert.Equal("ActivationAuthorityBusy", Error(() => first.Activate(a.Cell.Path, new string('b', 32), 1, first.ReadCurrent()!.Sha256)));
        var current = second.ReadCurrent()!;
        var recovered = b.Recover();
        var advanced = second.Activate(recovered.Path, recovered.Id, current.Generation, current.Sha256);
        Assert.Equal(2, advanced.Generation); lease.AssertCurrent();
    }

    [Fact]
    public async Task Separate_owned_Windows_process_shared_guard_blocks_activation_until_process_stops()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new ActivationFixture(); var authority = f.Provision(); var current = authority.Activate(f.Cell.Path, f.Instance, 0, null);
        var recovered = f.Recover();
        var executable = (Environment.GetEnvironmentVariable("PATH") ?? "").Split(System.IO.Path.PathSeparator)
            .Where(directory => !string.IsNullOrWhiteSpace(directory)).Select(directory => System.IO.Path.Combine(directory.Trim('"'), "pwsh.exe"))
            .FirstOrDefault(File.Exists);
        Assert.NotNull(executable); // Use the repository's existing PowerShell 7 prerequisite, including portable installations.
        var guard = System.IO.Path.Combine(f.AuthorityPath, ".activation.guard"); var processes = new List<Process>();
        try
        {
            for (var index = 0; index < 2; index++)
            {
                var ready = f.Path("child-ready-" + index);
                var script = "$h=[IO.File]::Open('" + guard.Replace("'", "''") + "',[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);" +
                    "[IO.File]::WriteAllText('" + ready.Replace("'", "''") + "',[Diagnostics.Process]::GetCurrentProcess().Id.ToString());[Threading.Thread]::Sleep(30000);$h.Dispose()";
                var info = new ProcessStartInfo(executable) { UseShellExecute = false, CreateNoWindow = true, WindowStyle = ProcessWindowStyle.Hidden,
                    WorkingDirectory = f.Root, RedirectStandardOutput = true, RedirectStandardError = true };
                info.ArgumentList.Add("-NoProfile"); info.ArgumentList.Add("-NonInteractive"); info.ArgumentList.Add("-EncodedCommand");
                info.ArgumentList.Add(Convert.ToBase64String(Encoding.Unicode.GetBytes(script)));
                info.Environment.Clear(); info.Environment["SystemRoot"] = Environment.GetEnvironmentVariable("SystemRoot");
                info.Environment["WINDIR"] = Environment.GetEnvironmentVariable("SystemRoot"); info.Environment["TEMP"] = f.Root; info.Environment["TMP"] = f.Root;
                var process = Process.Start(info)!; processes.Add(process);
                _ = process.StandardOutput.ReadToEndAsync(); _ = process.StandardError.ReadToEndAsync();
                using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(10));
                while (true)
                {
                    Assert.False(process.HasExited);
                    try { if (File.Exists(ready) && File.ReadAllText(ready) == process.Id.ToString()) break; }
                    catch (IOException) { }
                    await Task.Delay(20, timeout.Token);
                }
                Assert.Equal(process.Id.ToString(), File.ReadAllText(ready));
            }
            Assert.Equal("ActivationAuthorityBusy", Error(() => authority.Activate(recovered.Path, recovered.Id, current.Generation, current.Sha256)));
            processes[0].Kill(); await processes[0].WaitForExitAsync();
            Assert.Equal("ActivationAuthorityBusy", Error(() => authority.Activate(recovered.Path, recovered.Id, current.Generation, current.Sha256)));
        }
        finally
        {
            foreach (var process in processes)
            {
                if (!process.HasExited) { process.Kill(); await process.WaitForExitAsync(); }
                process.Dispose();
            }
        }
        FoundationCellActivationBinding? next = null;
        var released = Stopwatch.StartNew();
        while (next is null)
        {
            try { next = authority.Activate(recovered.Path, recovered.Id, current.Generation, current.Sha256); }
            catch (FoundationStoreException error) when (error.Code == "ActivationAuthorityBusy" && released.Elapsed < TimeSpan.FromSeconds(5))
            { await Task.Delay(25); }
        }
        Assert.Equal(2, next.Generation);
    }

    [Fact]
    public void Windows_owner_only_acl_is_checked_on_every_existing_authority_open()
    {
        if (!OperatingSystem.IsWindows()) return;
        using var f = new ActivationFixture(); f.Provision();
        var directory = new DirectoryInfo(f.AuthorityPath); var security = directory.GetAccessControl();
        Assert.True(security.AreAccessRulesProtected);
        using var identity = WindowsIdentity.GetCurrent(); Assert.Equal(identity.User, security.GetOwner(typeof(SecurityIdentifier)));
        security.AddAccessRule(new FileSystemAccessRule(new SecurityIdentifier(WellKnownSidType.WorldSid, null), FileSystemRights.ReadAndExecute,
            InheritanceFlags.ContainerInherit | InheritanceFlags.ObjectInherit, PropagationFlags.None, AccessControlType.Allow));
        directory.SetAccessControl(security);
        Assert.Equal("ActivationAuthorityPermissionsDenied", Error(() => new FoundationCellActivationAuthority(f.AuthorityPath, f.Cell.Customer, f.Cell.Cell, f.Cell.Clock)));
    }

    static string Error(Action action) => Assert.Throws<FoundationStoreException>(action).Code;
}

internal sealed class ActivationFixture : IDisposable
{
    public string Root { get; } = System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PlatformStudio-ActivationTests", Guid.NewGuid().ToString("N"));
    public string AuthorityPath => System.IO.Path.Combine(Root, "authority");
    public StoreFixture Cell { get; }
    public string Instance => Cell.Store.GetCellInstanceIdentity().DatabaseInstanceId;
    public ActivationFixture(string customer = "customer-a", string cell = "cell-a")
    { Cell = new(customer, cell); if (OperatingSystem.IsWindows()) ActivationProvisionFixtureRoots.Create(Root); else Directory.CreateDirectory(Root); }
    public string Path(string name) => System.IO.Path.Combine(Root, name);
    public FoundationCellActivationAuthority Provision() => FoundationCellActivationAuthority.Provision(AuthorityPath, Cell.Customer, Cell.Cell, Cell.Clock);
    public (string Path, string Id) Recover()
    {
        var backup = Cell.Store.CreateBackup(Path("backup-" + Guid.NewGuid().ToString("N") + ".sqlite"));
        var target = Path("recovered-" + Guid.NewGuid().ToString("N") + ".sqlite");
        var recovered = FoundationStore.RestoreBackup(backup, target, Cell.Clock);
        return (target, recovered.GetCellInstanceIdentity().DatabaseInstanceId);
    }
    public void Dispose()
    {
        Cell.Dispose();
        var expected = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetTempPath(), "PlatformStudio-ActivationTests")) + System.IO.Path.DirectorySeparatorChar;
        var full = System.IO.Path.GetFullPath(Root);
        if (!full.StartsWith(expected, StringComparison.OrdinalIgnoreCase) || !Guid.TryParseExact(System.IO.Path.GetFileName(full), "N", out _))
            throw new InvalidOperationException("Fixture cleanup path was not owned.");
        if (Directory.Exists(full)) Directory.Delete(full, recursive: true);
    }
}
