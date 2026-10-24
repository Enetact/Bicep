#requires -Version 7.4
[CmdletBinding(DefaultParameterSetName = 'Start')]
param(
    [Parameter(Mandatory, ParameterSetName = 'Start')][string]$ConfigurationPath,
    [Parameter(ParameterSetName = 'Start')][string]$DotNetPath = 'dotnet',
    [Parameter(ParameterSetName = 'Start')][string]$WorkerDllPath,
    [Parameter(ParameterSetName = 'Start')][switch]$EnableMockExecution,
    [Parameter(Mandatory, ParameterSetName = 'Stop')][switch]$Stop,
    [Parameter(Mandatory, ParameterSetName = 'Stop')][string]$ReceiptPath
)
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
function Assert-PlainPath([string]$Path) {
    $item = [IO.FileInfo]::new([IO.Path]::GetFullPath($Path))
    while ($null -ne $item) {
        if ($item.Exists -and ($item.Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Linked worker paths are rejected.' }
        $item = if ($item -is [IO.DirectoryInfo]) { $item.Parent } else { $item.Directory }
    }
}
function Stop-OwnedWorker([string]$Path) {
    Assert-PlainPath $Path
    $receipt = Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json
    if ($receipt.schema -cne 'platform-studio.foundation-worker-process.v1' -or
        $receipt.workerAssemblyPath -notlike '*SelfService.Foundation.Worker.dll' -or $receipt.processId -le 0) { throw 'Invalid worker ownership receipt.' }
    $process = Get-Process -Id $receipt.processId -ErrorAction SilentlyContinue
    if ($null -eq $process) { Write-Host 'Owned worker has already exited.'; return }
    # ConvertFrom-Json may return a DateTime; casting preserves its subsecond precision instead of culture-formatting it.
    $expectedStart = ([DateTimeOffset]$receipt.processStartedUtc).UtcDateTime
    if ($process.StartTime.ToUniversalTime().Ticks -ne $expectedStart.Ticks -or
        -not [string]::Equals($process.Path, $receipt.executablePath, [StringComparison]::OrdinalIgnoreCase)) { throw 'Worker PID ownership changed; stop refused.' }
    # PID and start time alone are insufficient: verify the running command still names this exact worker and configuration.
    $commandLine = (Get-CimInstance Win32_Process -Filter "ProcessId = $($process.Id)").CommandLine
    function Argument-Pattern([string]$Value) { return '(?:"' + [regex]::Escape($Value) + '"|' + [regex]::Escape($Value) + ')' }
    $expectedCommand = '\A\s*' + (Argument-Pattern $receipt.executablePath) + '\s+' +
        (Argument-Pattern $receipt.workerAssemblyPath) + '\s+--config\s+' + (Argument-Pattern $receipt.configurationPath) +
        '(?:\s+--allow-mock-execution)?\s*\z'
    if (-not [regex]::IsMatch($commandLine, $expectedCommand, [Text.RegularExpressions.RegexOptions]::IgnoreCase)) { throw 'Worker command ownership mismatch; stop refused.' }
    Assert-PlainPath $receipt.workerAssemblyPath
    Assert-PlainPath $receipt.configurationPath
    if ((Get-FileHash -LiteralPath $receipt.configurationPath -Algorithm SHA256).Hash.ToLowerInvariant() -cne $receipt.configurationSha256) { throw 'Worker configuration changed; stop refused.' }
    # Only this verified helper process is stopped. Its durable heartbeat becomes stale; pre-effect leases recover normally.
    $process.Kill($true)
    if (-not $process.WaitForExit(5000)) { throw 'Owned worker did not stop within five seconds.' }
    Write-Host 'Owned foundation worker stopped. No primary host or provider process was touched.'
}
if ($Stop) { Stop-OwnedWorker ([IO.Path]::GetFullPath($ReceiptPath)); return }
$configPath = [IO.Path]::GetFullPath($ConfigurationPath)
Assert-PlainPath $configPath
$config = Get-Content -LiteralPath $configPath -Raw | ConvertFrom-Json
if ([string]::IsNullOrWhiteSpace($config.receiptPath)) { throw 'Set a fresh receiptPath in the worker configuration before launching.' }
$receiptFullPath = [IO.Path]::GetFullPath($config.receiptPath, (Split-Path $configPath -Parent))
Assert-PlainPath $receiptFullPath
if (Test-Path -LiteralPath $receiptFullPath) { throw 'Receipt already exists. Choose a fresh receiptPath.' }
if (-not (Test-Path -LiteralPath (Split-Path $receiptFullPath -Parent) -PathType Container)) { throw 'Receipt directory must already exist.' }
if (-not $WorkerDllPath) { $WorkerDllPath = Join-Path $root 'artifacts/foundation-build/bin/SelfService.Foundation.Worker/release/SelfService.Foundation.Worker.dll' }
$dll = [IO.Path]::GetFullPath($WorkerDllPath)
Assert-PlainPath $dll
if (-not (Test-Path -LiteralPath $dll -PathType Leaf) -or [IO.Path]::GetFileName($dll) -cne 'SelfService.Foundation.Worker.dll') { throw 'Build the foundation worker before launching it.' }
$dotnet = (Get-Command $DotNetPath -CommandType Application -ErrorAction Stop).Source
# Do not inherit Azure, GitHub, ADO, Codex, proxy, profiler, startup-hook or other credential-bearing environment values.
$childEnvironment = @{}
foreach ($target in @([EnvironmentVariableTarget]::Process, [EnvironmentVariableTarget]::User, [EnvironmentVariableTarget]::Machine)) {
    foreach ($name in [Environment]::GetEnvironmentVariables($target).Keys) { $childEnvironment[[string]$name] = $null }
}
foreach ($name in @('SystemRoot','windir','ComSpec','TEMP','TMP')) {
    $value = [Environment]::GetEnvironmentVariable($name)
    if ($value) { $childEnvironment[$name] = $value }
}
$childEnvironment['DOTNET_ROOT'] = Split-Path $dotnet -Parent
$childEnvironment['PATH'] = (Split-Path $dotnet -Parent) + ';' + (Join-Path $env:SystemRoot 'System32')
$childEnvironment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'
$childEnvironment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE'] = '1'
$childEnvironment['DOTNET_GENERATE_ASPNET_CERTIFICATE'] = 'false'
$childEnvironment['DOTNET_ADD_GLOBAL_TOOLS_TO_PATH'] = 'false'
# Native file redirection prevents a detached worker retaining the launcher's capture pipes.
$logId = [Guid]::NewGuid().ToString('N')
$stdoutPath = $receiptFullPath + '.' + $logId + '.stdout.log'
$stderrPath = $receiptFullPath + '.' + $logId + '.stderr.log'
foreach ($path in @($stdoutPath, $stderrPath)) {
    Assert-PlainPath $path
    if (Test-Path -LiteralPath $path) { throw 'Worker log path already exists; launch refused.' }
}
# Windows filenames cannot contain a quote. The two validated file arguments are quoted as complete arguments.
$arguments = '"' + $dll + '" --config "' + $configPath + '"'
if ($EnableMockExecution) { $arguments += ' --allow-mock-execution' }
# Windows Start-Process can also inherit an enclosing tool's capture handles. An explicit handle list permits only
# the two fresh log files and NUL input to cross the process boundary; no parent capture pipe remains open.
if (-not ('FoundationOwnedWorkerNative' -as [type])) {
    Add-Type -TypeDefinition @'
using System;
using System.Collections;
using System.ComponentModel;
using System.Linq;
using System.Runtime.InteropServices;
using System.Text;
using Microsoft.Win32.SafeHandles;
public static class FoundationOwnedWorkerNative
{
    [StructLayout(LayoutKind.Sequential)] struct Security { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
    [StructLayout(LayoutKind.Sequential)] struct Startup {
        public int Size; public IntPtr Reserved, Desktop, Title; public uint X,Y,XSize,YSize,XCount,YCount,Fill,Flags;
        public ushort Show, ReservedSize; public IntPtr ReservedBytes,Input,Output,Error;
    }
    [StructLayout(LayoutKind.Sequential)] struct StartupEx { public Startup Startup; public IntPtr Attributes; }
    [StructLayout(LayoutKind.Sequential)] struct Information { public IntPtr Process,Thread; public int ProcessId,ThreadId; }
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern SafeFileHandle CreateFileW(string path,uint access,uint share,ref Security security,uint disposition,uint flags,IntPtr template);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool InitializeProcThreadAttributeList(IntPtr list,int count,int flags,ref IntPtr bytes);
    [DllImport("kernel32.dll", SetLastError=true)]
    static extern bool UpdateProcThreadAttribute(IntPtr list,uint flags,IntPtr attribute,IntPtr value,IntPtr bytes,IntPtr previous,IntPtr returned);
    [DllImport("kernel32.dll")] static extern void DeleteProcThreadAttributeList(IntPtr list);
    [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr handle);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool GetExitCodeProcess(SafeProcessHandle process,out uint exitCode);
    [DllImport("kernel32.dll", SetLastError=true)] static extern bool TerminateProcess(SafeProcessHandle process,uint exitCode);
    [DllImport("kernel32.dll", SetLastError=true)] static extern uint WaitForSingleObject(SafeProcessHandle process,uint milliseconds);
    [DllImport("kernel32.dll", CharSet=CharSet.Unicode, SetLastError=true)]
    static extern bool CreateProcessW(string application,StringBuilder command,IntPtr processSecurity,IntPtr threadSecurity,
        [MarshalAs(UnmanagedType.Bool)] bool inherit,uint flags,IntPtr environment,string directory,ref StartupEx startup,out Information information);
    static SafeFileHandle File(string path,uint access,uint share,uint disposition,ref Security security) {
        var handle=CreateFileW(path,access,share,ref security,disposition,0x80,IntPtr.Zero);
        if(handle.IsInvalid) { handle.Dispose(); throw new Win32Exception(Marshal.GetLastWin32Error()); }
        return handle;
    }
    public sealed class OwnedLaunch : IDisposable {
        readonly SafeProcessHandle handle;
        public int Id { get; }
        internal OwnedLaunch(IntPtr originalHandle,int id) { handle=new SafeProcessHandle(originalHandle,true); Id=id; }
        public bool HasExited { get { return WaitForExit(0); } }
        public int ExitCode {
            get {
                if(!HasExited) throw new InvalidOperationException("Owned worker is still running.");
                if(!GetExitCodeProcess(handle,out var code)) throw new Win32Exception(Marshal.GetLastWin32Error());
                return unchecked((int)code);
            }
        }
        public bool WaitForExit(int milliseconds) {
            if(milliseconds<0) throw new ArgumentOutOfRangeException(nameof(milliseconds));
            var result=WaitForSingleObject(handle,(uint)milliseconds);
            if(result==0) return true;
            if(result==258) return false;
            throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Kill(bool entireProcessTree) {
            // This worker creates no children. Terminate only the original owned process handle; never enumerate/reopen PIDs.
            if(!TerminateProcess(handle,2) && !HasExited) throw new Win32Exception(Marshal.GetLastWin32Error());
        }
        public void Dispose() { handle.Dispose(); }
    }
    public static OwnedLaunch Start(string executable,string arguments,string directory,string stdout,string stderr,IDictionary variables) {
        var security=new Security { Length=Marshal.SizeOf<Security>(),Inherit=true };
        using var output=File(stdout,0x40000000,1,1,ref security); // CREATE_NEW; never overwrite an existing log.
        using var error=File(stderr,0x40000000,1,1,ref security);
        using var input=File("NUL",0x80000000,3,3,ref security);
        IntPtr size=IntPtr.Zero,attributes=IntPtr.Zero,handles=IntPtr.Zero,environment=IntPtr.Zero;
        var initialized=false; var information=new Information();
        try {
            InitializeProcThreadAttributeList(IntPtr.Zero,1,0,ref size);
            if(size==IntPtr.Zero) throw new Win32Exception(Marshal.GetLastWin32Error());
            attributes=Marshal.AllocHGlobal(size);
            if(!InitializeProcThreadAttributeList(attributes,1,0,ref size)) throw new Win32Exception(Marshal.GetLastWin32Error());
            initialized=true; handles=Marshal.AllocHGlobal(3*IntPtr.Size);
            Marshal.WriteIntPtr(handles,0,input.DangerousGetHandle()); Marshal.WriteIntPtr(handles,IntPtr.Size,output.DangerousGetHandle());
            Marshal.WriteIntPtr(handles,2*IntPtr.Size,error.DangerousGetHandle());
            if(!UpdateProcThreadAttribute(attributes,0,new IntPtr(0x00020002),handles,new IntPtr(3*IntPtr.Size),IntPtr.Zero,IntPtr.Zero))
                throw new Win32Exception(Marshal.GetLastWin32Error());
            var entries=variables.Cast<DictionaryEntry>().Where(v=>v.Value!=null)
                .OrderBy(v=>Convert.ToString(v.Key),StringComparer.OrdinalIgnoreCase).Select(v=>Convert.ToString(v.Key)+"="+Convert.ToString(v.Value));
            environment=Marshal.StringToHGlobalUni(string.Join("\0",entries)+"\0\0");
            var startup=new StartupEx { Attributes=attributes, Startup=new Startup { Size=Marshal.SizeOf<StartupEx>(),
                Flags=0x101,Show=0,Input=input.DangerousGetHandle(),Output=output.DangerousGetHandle(),Error=error.DangerousGetHandle() } };
            if(!CreateProcessW(executable,new StringBuilder("\""+executable+"\" "+arguments),IntPtr.Zero,IntPtr.Zero,true,
                0x08080400,environment,directory,ref startup,out information)) throw new Win32Exception(Marshal.GetLastWin32Error());
            var owned=new OwnedLaunch(information.Process,information.ProcessId);
            information.Process=IntPtr.Zero; // Transfer the original process handle; cleanup must never reopen its PID.
            return owned;
        } finally {
            if(information.Thread!=IntPtr.Zero) CloseHandle(information.Thread);
            if(information.Process!=IntPtr.Zero) CloseHandle(information.Process);
            if(environment!=IntPtr.Zero) Marshal.FreeHGlobal(environment);
            if(handles!=IntPtr.Zero) Marshal.FreeHGlobal(handles);
            if(initialized) DeleteProcThreadAttributeList(attributes);
            if(attributes!=IntPtr.Zero) Marshal.FreeHGlobal(attributes);
        }
    }
}
'@
}
$process = [FoundationOwnedWorkerNative]::Start($dotnet, $arguments, (Split-Path $configPath -Parent), $stdoutPath, $stderrPath, $childEnvironment)
try {
    $deadline = [DateTimeOffset]::UtcNow.AddSeconds(10)
    $ready = $false
    while ([DateTimeOffset]::UtcNow -lt $deadline) {
        if ($process.HasExited) { throw "Worker exited before registering (exit $($process.ExitCode))." }
        if (Test-Path -LiteralPath $receiptFullPath) {
            try {
                $receipt = Get-Content -LiteralPath $receiptFullPath -Raw | ConvertFrom-Json
                $ready = $receipt.processId -eq $process.Id -and $receipt.configurationPath -ceq $configPath
            } catch { $ready = $false }
            if ($ready) { break }
        }
        Start-Sleep -Milliseconds 100
    }
    if (-not $ready) {
        # This wrapper retains CreateProcessW's original process handle and never selects a process by PID.
        if (-not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
        throw 'Owned worker registration timed out.'
    }
    [pscustomobject]@{ state = 'Registered'; mode = $receipt.mode; receiptPath = $receiptFullPath; processId = $process.Id;
        stdoutPath = $stdoutPath; stderrPath = $stderrPath; externalDispatch = $false }
} catch {
    if (-not $process.HasExited) { $process.Kill($true); [void]$process.WaitForExit(5000) }
    throw
} finally { $process.Dispose() }
