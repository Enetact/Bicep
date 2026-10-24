#requires -Version 7.4
[CmdletBinding()]
param([string]$DotNetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'tests/SelfService.Foundation.Tests/SelfService.Foundation.Tests.csproj'
$output = Join-Path $root 'artifacts/foundation-build'
$results = Join-Path $root 'artifacts/test-results/foundation'
# TestServer, owned worker processes and loopback-only synthetic mocks. No sign-in, provider run or Azure deployment.
& $DotNetPath restore $project --locked-mode --artifacts-path $output -m:1 -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw 'Foundation locked restore or vulnerability audit failed.' }
& $DotNetPath test $project --no-restore --artifacts-path $output -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false --logger 'trx;LogFileName=foundation.trx' --results-directory $results
if ($LASTEXITCODE -ne 0) { throw 'Foundation disconnected tests failed.' }
[xml]$trx = Get-Content -LiteralPath (Join-Path $results 'foundation.trx') -Raw
$counts = $trx.TestRun.ResultSummary.Counters
$outcomes = @($trx.TestRun.Results.UnitTestResult)
if ($outcomes.Count -ne [int]$counts.total) { throw 'Foundation TRX result count does not match its summary.' }
# Some xUnit TRX writers leave Counters.notExecuted at zero despite skipped results.
$passed = @($outcomes | Where-Object { $_.outcome -ceq 'Passed' }).Count
$notExecuted = @($outcomes | Where-Object { $_.outcome -ceq 'NotExecuted' }).Count
$receipt = [ordered]@{
    schema = 'platform-studio.foundation-tests.v1'
    observedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    passed = $passed
    failed = $outcomes.Count - $passed - $notExecuted
    total = $outcomes.Count
    notExecuted = $notExecuted
    profile = 'disconnected-signed-identity-sqlite-mock-pipelines'
    externalDispatch = $false
    deploymentVerified = $false
}
$receipt | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath (Join-Path $results 'summary.json') -Encoding utf8
Write-Host "Foundation disconnected profile: $($receipt.passed) passed, $($receipt.failed) failed, $($receipt.notExecuted) not executed. $results"
