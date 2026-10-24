#requires -Version 7.4
[CmdletBinding()]
param([string]$DotNetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'tests/SelfService.Foundation.Tests/SelfService.Foundation.Tests.csproj'
$output = Join-Path $root 'artifacts/foundation-worker-build'
$results = Join-Path $root 'artifacts/test-results/foundation-worker'
# Actual owned child workers use synthetic cells and literal loopback HTTP fixtures. No sign-in/provider/deployment occurs.
& $DotNetPath restore $project --locked-mode --artifacts-path $output -m:1 -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw 'Worker locked restore or vulnerability audit failed.' }
& $DotNetPath test $project --no-restore --artifacts-path $output -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false --filter 'FullyQualifiedName~FoundationWorkerProcess' --logger 'trx;LogFileName=foundation-worker.trx' --results-directory $results
if ($LASTEXITCODE -ne 0) { throw 'Independent worker qualification failed.' }
[xml]$trx = Get-Content -LiteralPath (Join-Path $results 'foundation-worker.trx') -Raw
$outcomes = @($trx.TestRun.Results.UnitTestResult)
if ($outcomes.Count -eq 0 -or $outcomes.Count -ne [int]$trx.TestRun.ResultSummary.Counters.total) { throw 'Worker test evidence is incomplete.' }
$receipt = [ordered]@{
    schema = 'platform-studio.foundation-worker-tests.v1'
    observedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    total = $outcomes.Count
    passed = @($outcomes | Where-Object { $_.outcome -ceq 'Passed' }).Count
    failed = @($outcomes | Where-Object { $_.outcome -ceq 'Failed' }).Count
    notExecuted = @($outcomes | Where-Object { $_.outcome -ceq 'NotExecuted' }).Count
    profile = 'owned-process-customer-cell-loopback-qualification'
    externalDispatch = $false
    deploymentVerified = $false
}
$receipt | ConvertTo-Json -Depth 4 | Set-Content -LiteralPath (Join-Path $results 'summary.json') -Encoding utf8
if ($receipt.failed -or $receipt.notExecuted -or $receipt.passed -ne $receipt.total) { throw 'Worker test acceptance requires every qualification to pass.' }
Write-Host "Independent worker qualification: $($receipt.passed) passed. $results"
