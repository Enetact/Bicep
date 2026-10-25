#requires -Version 7.4
[CmdletBinding()]
param([string]$DotNetPath = 'dotnet',[switch]$SkipBuildCleanup)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'FoundationTestExecutionEvidence.ps1')
$root=[IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$parent=Join-Path $root 'artifacts/foundation-test-executions'
$null=Assert-FTEUnlinkedPath $parent
if(!(Test-Path -LiteralPath $parent -PathType Container)){$null=New-Item -ItemType Directory -Path $parent -Force}
$run=New-FTEPrivateDirectory (Join-Path $parent ([Guid]::NewGuid().ToString('N')))
$project=Join-Path $root 'tests/SelfService.Foundation.Tests/SelfService.Foundation.Tests.csproj'
$output=Join-Path $run 'build';$results=Join-Path $run 'results'
$null=New-Item -ItemType Directory -Path $output,$results
$restoreArguments=@('restore',$project,'--locked-mode','--artifacts-path',$output,'-m:1','-nodeReuse:false')
$testArguments=@('test',$project,'--no-restore','--artifacts-path',$output,'-c','Release','-m:1','-nodeReuse:false','-p:UseSharedCompilation=false','--logger','trx;LogFileName=foundation.trx','--results-directory',$results)
$toolHandle=$null;$before=$null;$after=$null;$beforeFile=$null;$afterFile=$null;$trx=$null;$failure='TestExecutionIncomplete';$commands=[Collections.Generic.List[object]]::new()
$started=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()
$flags=[ordered]@{independentAttestation=$false;completeDependencyClosureVerified=$false;environmentAttested=$false;noNetworkAttested=$false;descendantProcessCleanupVerified=$false;signed=$false;productionReady=$false;publicationAuthorized=$false}
try{
    $dotnet=Resolve-FTEExecutable $DotNetPath
    $toolHandle=[IO.FileStream]::new($dotnet,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    if($toolHandle.Length -gt 67108864){throw 'TestExecutionExecutableByteLimit'}
    $toolSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($toolHandle)).ToLowerInvariant()
    $startedFile=Write-FTEJson (Join-Path $run 'started.json') ([ordered]@{schema='platform-studio.foundation-test-execution-started.v1';state='Started';startedUnixMilliseconds=$started;repositoryRoot=$root;profile='disconnected-signed-identity-sqlite-mock-pipelines';executable=$dotnet;executableSha256=$toolSha256;plannedArguments=@($restoreArguments,$testArguments);limits=$flags})
    $before=Get-FTESourceSnapshot $root;$beforeFile=Write-FTEJson (Join-Path $run 'source-before.json') $before
    foreach($step in @('restore','test')){
        $arguments=if($step -ceq 'restore'){$restoreArguments}else{$testArguments}
        $timeout=if($step -ceq 'restore'){300000}else{900000}
        $observation=Invoke-FTEProcess $dotnet $arguments $root $timeout
        if($observation.executableSha256 -cne $toolSha256){throw 'TestExecutionExecutableChanged'}
        $stdout=Write-FTEFile (Join-Path $run ($step+'.stdout.txt')) ([Text.Encoding]::UTF8.GetBytes($observation.stdout))
        $stderr=Write-FTEFile (Join-Path $run ($step+'.stderr.txt')) ([Text.Encoding]::UTF8.GetBytes($observation.stderr))
        $commands.Add([ordered]@{step=$step;executable=$dotnet;executableSha256=$toolSha256;arguments=$arguments;processId=$observation.processId;startedUnixMilliseconds=$observation.startedUnixMilliseconds;finishedUnixMilliseconds=$observation.finishedUnixMilliseconds;exitCode=$observation.exitCode;stdout=$stdout;stderr=$stderr})
        if($observation.exitCode -ne 0){$failure='TestExecutionNonzeroExit';break}
    }
    $after=Get-FTESourceSnapshot $root;$afterFile=Write-FTEJson (Join-Path $run 'source-after.json') $after
    if($beforeFile.sha256 -cne $afterFile.sha256){throw 'TestExecutionSourceInputsChanged'}
    $test=@($commands|Where-Object {$_.step -ceq 'test'})
    if($test.Count -ne 1){throw $failure}
    $trx=Read-FTETrx (Join-Path $results 'foundation.trx') $test[0].startedUnixMilliseconds $test[0].finishedUnixMilliseconds
    if($failure -ceq 'TestExecutionNonzeroExit'){throw $failure}
    if($trx.failed -ne 0){throw 'TestExecutionFailedResults'}
    if($trx.notExecuted -ne 0){throw 'TestExecutionSkippedResults'}
    if($trx.summaryOutcome -cne 'Completed'){throw 'TestExecutionSummaryNotCompleted'}
    $summary=[ordered]@{schema='platform-studio.foundation-tests.v1';observedUtc=[DateTimeOffset]::UtcNow.ToString('O');passed=$trx.passed;failed=$trx.failed;total=$trx.total;notExecuted=$trx.notExecuted;profile='disconnected-signed-identity-sqlite-mock-pipelines';externalDispatch=$false;deploymentVerified=$false}
    $summaryFile=Write-FTEJson (Join-Path $results 'summary.json') $summary;$summaryFile.path='results/summary.json'
    $completed=[ordered]@{schema='platform-studio.foundation-test-execution.v1';state='Completed';passed=$true;startedUnixMilliseconds=$started;finishedUnixMilliseconds=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds();profile=$summary.profile;started=$startedFile;sourceBefore=$beforeFile;sourceAfter=$afterFile;sourceInputsUnchanged=$true;commands=$commands.ToArray();trx=$trx;summary=$summaryFile;limits=$flags}
    $commit=Write-FTEJson (Join-Path $run 'completed.json') $completed
}catch{
    $message=$_.Exception.Message;$failure=if($message -cmatch '\ATestExecution[A-Za-z]+\z'){$message}else{'TestExecutionRejected'}
    if($before -and !$after){try{$after=Get-FTESourceSnapshot $root;$afterFile=Write-FTEJson (Join-Path $run 'source-after.json') $after}catch{}}
    $rejected=[ordered]@{schema='platform-studio.foundation-test-execution.v1';state='Rejected';passed=$false;code=$failure;startedUnixMilliseconds=$started;finishedUnixMilliseconds=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds();sourceBefore=$beforeFile;sourceAfter=$afterFile;commands=$commands.ToArray();trx=$trx;limits=$flags}
    $null=Write-FTEJson (Join-Path $run 'rejected.json') $rejected
    throw "Foundation test execution rejected ($failure). Evidence: $run"
}finally{if($toolHandle){$toolHandle.Dispose()}}
# Housekeeping is outside the test-verdict transaction and cannot create a rejected test record.
$cleanup=[pscustomobject]@{state='Skipped';reason='NotInstalledOrExplicitlySkipped'}
if(!$SkipBuildCleanup -and (Test-Path -LiteralPath (Join-Path $PSScriptRoot 'FoundationBuildRetention.ps1'))) {
    try {
        . (Join-Path $PSScriptRoot 'FoundationBuildRetention.ps1')
        $cleanup=Invoke-FoundationBuildRetention -Root $root -RunDirectory $run -CompletedSha256 $commit.sha256 -Apply
    } catch {
        $reason=if($_.Exception.Message -ceq 'BuildRetentionProcessInspectionUnavailable'){'ProcessInspectionUnavailable'}else{'QualificationOrInspectionRefused'}
        $cleanup=[pscustomobject]@{state='Skipped';reason=$reason;removedCount=$(if($reason -ceq 'ProcessInspectionUnavailable'){0}else{$null});removedBytes=$(if($reason -ceq 'ProcessInspectionUnavailable'){0}else{$null});continuousProcessFencing=$false}
        Write-Warning 'Build cleanup was skipped; the successful test result is unchanged.' -WarningAction Continue
    }
}
try { $null=Write-FTEJson (Join-Path $run 'build-cleanup.json') $cleanup }
catch { Write-Warning 'Build cleanup observation could not be saved; the successful test result is unchanged.' -WarningAction Continue }
Write-Host "Foundation disconnected profile: $($trx.passed) passed, 0 failed, 0 not executed. Evidence: $run"
[pscustomobject]@{runDirectory=$run;receipt=(Join-Path $run 'completed.json');receiptSha256=$commit.sha256;resultsDirectory=$results;passed=$trx.passed;failed=0;notExecuted=0;buildCleanup=$cleanup;independentAttestation=$false;productionReady=$false}
