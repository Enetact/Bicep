#requires -Version 7.4
[CmdletBinding()]
param(
    [string]$DotNetPath='dotnet',
    [ValidateRange(10,900)][int]$TimeoutSeconds=300,
    [Threading.CancellationToken]$CancellationToken=[Threading.CancellationToken]::None
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'FoundationBuildRetention.ps1')
$root=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent));$null=Assert-FTEUnlinkedPath $root
$nativeSha=Initialize-FBROwnedNative;$dotnet=Resolve-FTEExecutable $DotNetPath
$tool=[IO.FileStream]::new($dotnet,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
$gate=$null;$session=$null;$generation=$null;$successful=$false
try {
    $CancellationToken.ThrowIfCancellationRequested()
    if($tool.Length -gt 67108864){throw 'OwnedBuildExecutableLimit'}
    $toolSha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($tool)).ToLowerInvariant()
    $parent=Join-Path $root 'artifacts/foundation-owned-builds';Assert-FBROwnedBuildNamespace $root $parent
    if(!(Test-Path -LiteralPath $parent)){$null=New-Item -ItemType Directory -Path $parent}
    Assert-FBROwnedBuildNamespace $root $parent
    $id=[Guid]::NewGuid().ToString('N');$generation=New-FTEPrivateDirectory (Join-Path $parent $id)
    $gate=[IO.FileStream]::new((Join-Path $generation '.compiler-build.lock'),[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    $gate.Flush($true);$created=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
    $build=New-FTEPrivateDirectory (Join-Path $generation 'build');$cliHome=New-FTEPrivateDirectory (Join-Path $generation 'cli-home')
    $source=Get-FTESourceSnapshot $root;$sourceFile=Write-FTEJson (Join-Path $generation 'source.json') $source
    $arguments=@(Get-FBROwnedArguments $root $build)
    $marker=Write-FTEJson (Join-Path $generation '.compiler-build.json') ([ordered]@{
        schema='platform-studio.foundation-owned-build.v1';repositoryRoot=$root;generation=$id;buildRoot=$build;
        ownerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value;createdUnixMilliseconds=$created;
        executable=$dotnet;executableSha256=$toolSha;nativeHelperSha256=$nativeSha;sourceSha256=$sourceFile.sha256;arguments=$arguments
    })
    # Exact minimal child environment. No provider credentials, profilers, startup hooks or ambient MSBuild overrides.
    $environment=@{}
    foreach($name in @('SystemRoot','WINDIR','ComSpec','ProgramFiles','ProgramFiles(x86)','ProgramW6432','ProgramData')){$value=[Environment]::GetEnvironmentVariable($name);if($value){$environment[$name]=$value}}
    $appData=New-FTEPrivateDirectory (Join-Path $cliHome 'appdata');$localAppData=New-FTEPrivateDirectory (Join-Path $cliHome 'local-appdata');$temporary=New-FTEPrivateDirectory (Join-Path $cliHome 'temp')
    $environment['USERPROFILE']=$cliHome;$environment['APPDATA']=$appData;$environment['LOCALAPPDATA']=$localAppData
    $environment['TEMP']=$temporary;$environment['TMP']=$temporary
    $environment['DOTNET_ROOT']=Split-Path $dotnet -Parent;$environment['PATH']=(Split-Path $dotnet -Parent)+';'+(Join-Path $env:SystemRoot 'System32')
    $environment['DOTNET_CLI_HOME']=$cliHome;$environment['MSBuildEnableWorkloadResolver']='false';$environment['DOTNET_CLI_TELEMETRY_OPTOUT']='1'
    $environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE']='1';$environment['DOTNET_GENERATE_ASPNET_CERTIFICATE']='false';$environment['DOTNET_ADD_GLOBAL_TOOLS_TO_PATH']='false'
    $environment['DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE']='true';$environment['DOTNET_MULTILEVEL_LOOKUP']='0'
    if($env:NUGET_PACKAGES){$environment['NUGET_PACKAGES']=$env:NUGET_PACKAGES}
    $CancellationToken.ThrowIfCancellationRequested()
    $session=[PlatformStudio.FoundationOwnedBuild.Native]::Start($dotnet,[string[]]$arguments,$root,$environment)
    $started=Write-FTEJson (Join-Path $generation 'started.json') ([ordered]@{schema='platform-studio.foundation-owned-build-started.v1';
        processId=$session.ProcessId;processStartTicks=$session.ProcessStartTicks;executable=$session.Executable;executableSha256=$toolSha;startedUnixMilliseconds=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()})
    $observed=$session.Wait($TimeoutSeconds*1000,$CancellationToken)
    $stdout=Write-FTEFile (Join-Path $generation 'stdout.txt') $session.StandardOutput() 4194304
    $stderr=Write-FTEFile (Join-Path $generation 'stderr.txt') $session.StandardError() 4194304
    $after=Get-FTESourceSnapshot $root
    if((Get-FTEHash ([Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $after -Depth 24)))) -cne $sourceFile.sha256){throw 'OwnedBuildSourceChanged'}
    $tree=Get-FBRTree $root (Join-Path $build 'obj');$CancellationToken.ThrowIfCancellationRequested()
    $completed=Write-FTEJson (Join-Path $generation 'completed.json') ([ordered]@{schema='platform-studio.foundation-owned-build-completed.v1';
        markerSha256=$marker.sha256;startedSha256=$started.sha256;sourceSha256=$sourceFile.sha256;sourceInputsUnchanged=$true;
        observation=[ordered]@{processId=$observed.ProcessId;processStartTicks=$observed.ProcessStartTicks;executable=$observed.Executable;exitCode=$observed.ExitCode;
            totalProcesses=$observed.TotalProcesses;activeProcesses=$observed.ActiveProcesses;finishedUnixMilliseconds=$observed.FinishedUnixMilliseconds};
        objFingerprint=$tree.fingerprint;stdout=$stdout;stderr=$stderr;independentAttestation=$false;productionReady=$false})
    $successful=$true
    [pscustomobject]@{state='Built';generationDirectory=$generation;buildDirectory=$build;completionSha256=$completed.sha256;sourceSha256=$sourceFile.sha256;
        processId=$observed.ProcessId;processStartTicks=$observed.ProcessStartTicks;ownedJobActiveProcesses=0;independentAttestation=$false;productionReady=$false}
} catch {
    $code=$_.Exception.GetBaseException().Message
    if($code -cnotmatch '\AOwnedBuild[A-Za-z]+\z'){$code=if($CancellationToken.IsCancellationRequested){'OwnedBuildCancelled'}else{'OwnedBuildRejected'}}
    # Diagnostic capture is already bounded by the native pump. Never print child output or grant completion on failure.
    if($generation -and $session){foreach($name in @('stdout','stderr')){try{
        $path=Join-Path $generation ($name+'.txt')
        if(![IO.Path]::Exists($path)){if($name -ceq 'stdout'){$bytes=$session.StandardOutput()}else{$bytes=$session.StandardError()};$null=Write-FTEFile $path $bytes 4194304}
    }catch{}}}
    if($generation){try{$null=Write-FTEJson (Join-Path $generation 'rejected.json') ([ordered]@{schema='platform-studio.foundation-owned-build-rejected.v1';code=$code;completed=$false})}catch{}}
    throw $code
} finally {
    $terminationUncertain=$false
    try {if($session){$session.Dispose()}} catch {$terminationUncertain=$true}
    finally {if($gate){$gate.Dispose()};$tool.Dispose()}
    if($terminationUncertain){throw 'OwnedBuildTerminationUncertain'}
}
