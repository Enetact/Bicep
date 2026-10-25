#requires -Version 7.4
[CmdletBinding()]
param([string]$DotNetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
$root = Split-Path $PSScriptRoot -Parent
$project = Join-Path $root 'tests/SelfService.Foundation.Tests/SelfService.Foundation.Tests.csproj'
$output = Join-Path $root 'artifacts/foundation-load-check'
$runId = [DateTimeOffset]::UtcNow.ToString('yyyyMMdd-HHmmss') + '-' + [Guid]::NewGuid().ToString('N')
$results = Join-Path $root ('artifacts/test-results/foundation-load/' + $runId)
[void][IO.Directory]::CreateDirectory($results)
# Fixed before measurement. Changes require a new target version; these are local regression gates, never production SLOs.
$targetVersion = 'platform-studio.local-foundation-targets.v1'
$targets = [ordered]@{
    enqueue = [ordered]@{ attempts = 160; clients = 4; accepted = 128; rejected429 = 32; p95Milliseconds = 5000.0; elapsedMilliseconds = 60000.0 }
    isolation = [ordered]@{ cells = 2; signedApiReads = 40; accepted = 127; rejected429 = 32; minimumOverlappedReads = 1;
        p95Milliseconds = 3000.0; maximumMilliseconds = 10000.0; elapsedMilliseconds = 60000.0 }
    competingProcesses = [ordered]@{ maximumTestElapsedMilliseconds = 15000.0 }
    preEffectRecovery = [ordered]@{ maximumTestElapsedMilliseconds = 15000.0 }
    postEffectNoReplay = [ordered]@{ maximumTestElapsedMilliseconds = 10000.0 }
}
$intent = [ordered]@{ schema = 'platform-studio.foundation-load-intent.v1'; targetsVersion = $targetVersion; targets = $targets;
    localQualificationOnly = $true; productionSloVerified = $false; externalDispatch = $false; deploymentVerified = $false }
$intent | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $results 'intent.json') -Encoding utf8
$total = [Diagnostics.Stopwatch]::StartNew()
$started = [DateTimeOffset]::UtcNow
$machine = [ordered]@{
    os = [Runtime.InteropServices.RuntimeInformation]::OSDescription
    architecture = [Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString()
    logicalProcessors = [Environment]::ProcessorCount
    physicalMemoryGiB = $null
    processorModel = $null
}
# Read only aggregate hardware facts. Never include machine/user names, domain, serial, UUID, device ID or environment values.
try {
    $memory = (Get-CimInstance Win32_ComputerSystem -Property TotalPhysicalMemory).TotalPhysicalMemory
    $machine.physicalMemoryGiB = [Math]::Round([double]$memory / 1GB, 2)
    $cpu = @(Get-CimInstance Win32_Processor -Property Name | ForEach-Object { $_.Name.Trim() } | Sort-Object -Unique)
    $machine.processorModel = ($cpu -join '; ').Substring(0, [Math]::Min(256, ($cpu -join '; ').Length))
} catch { } # Unavailable metadata remains null; it is never invented or substituted with identifying values.
$sdk = (& $DotNetPath --version | Select-Object -Last 1).Trim()
if ($LASTEXITCODE -ne 0 -or $sdk -notmatch '^\d+\.\d+\.\d+(?:[-+][A-Za-z0-9.-]+)?$') { throw 'A supported .NET SDK is required for local load qualification.' }
$phase = [Diagnostics.Stopwatch]::StartNew()
& $DotNetPath restore $project --locked-mode --artifacts-path $output -m:1 -nodeReuse:false
if ($LASTEXITCODE -ne 0) { throw 'Load qualification locked restore or vulnerability audit failed.' }
$restoreMilliseconds = $phase.Elapsed.TotalMilliseconds
$filter = 'FullyQualifiedName~FoundationLoadQualificationTests|FullyQualifiedName~FoundationWorkerProcessTests.Two_real_workers_compete|FullyQualifiedName~FoundationWorkerProcessTests.Crashed_pre_effect|FullyQualifiedName~FoundationWorkerProcessTests.Crashed_post_effect'
$phase.Restart()
& $DotNetPath test $project --no-restore --artifacts-path $output -c Release -m:1 -nodeReuse:false -p:UseSharedCompilation=false --filter $filter --logger 'trx;LogFileName=foundation-load.trx' --results-directory $results
$testExitCode = $LASTEXITCODE
$testInvocationMilliseconds = $phase.Elapsed.TotalMilliseconds
$trxPath = Join-Path $results 'foundation-load.trx'
if (-not (Test-Path -LiteralPath $trxPath)) { throw 'Load qualification did not produce a TRX; acceptance is unavailable.' }
if ((Get-Item -LiteralPath $trxPath).Length -gt 8MB) { throw 'Load qualification TRX exceeded the bounded evidence size.' }
[xml]$trx = Get-Content -LiteralPath $trxPath -Raw
$outcomes = @($trx.TestRun.Results.UnitTestResult)
if ($outcomes.Count -ne [int]$trx.TestRun.ResultSummary.Counters.total -or $outcomes.Count -ne 5) { throw 'Load qualification discovered an unexpected test set.' }
$expectedTests = @(
    'SelfService.Foundation.Tests.FoundationLoadQualificationTests.Concurrent_enqueue_flood_is_bounded_at_128_with_atomic_backpressure_and_measured_latency',
    'SelfService.Foundation.Tests.FoundationLoadQualificationTests.Other_customer_signed_API_remains_isolated_while_first_cell_has_slow_provider_and_queue_flood',
    'SelfService.Foundation.Tests.FoundationWorkerProcessTests.Two_real_workers_compete_for_one_queue_and_each_plan_dispatches_exactly_once_to_loopback',
    'SelfService.Foundation.Tests.FoundationWorkerProcessTests.Crashed_pre_effect_process_leaves_stale_heartbeat_and_lease_recovers_with_a_new_fence',
    'SelfService.Foundation.Tests.FoundationWorkerProcessTests.Crashed_post_effect_process_becomes_outcome_unknown_and_is_never_automatically_replayed'
)
if (@(Compare-Object $expectedTests @($outcomes | ForEach-Object { [string]$_.testName })).Count) { throw 'Load qualification test identities changed; update the versioned profile explicitly.' }
function Distribution([double[]]$Samples) {
    if ($Samples.Count -eq 0 -or @($Samples | Where-Object { -not [double]::IsFinite($_) -or $_ -lt 0 }).Count) { throw 'Invalid local latency observations.' }
    $sorted = @($Samples | Sort-Object)
    return [ordered]@{ count = $sorted.Count; minimumMilliseconds = $sorted[0]; meanMilliseconds = ($sorted | Measure-Object -Average).Average;
        p50Milliseconds = $sorted[[Math]::Max(0, [Math]::Ceiling($sorted.Count * 0.50) - 1)];
        p95Milliseconds = $sorted[[Math]::Max(0, [Math]::Ceiling($sorted.Count * 0.95) - 1)];
        p99Milliseconds = $sorted[[Math]::Max(0, [Math]::Ceiling($sorted.Count * 0.99) - 1)]; maximumMilliseconds = $sorted[-1] }
}
$measurements = @{}
$checks = [Collections.Generic.List[object]]::new()
function Check([string]$Name, [bool]$Passed, $Actual, $Limit) {
    $checks.Add([ordered]@{ name = $Name; passed = $Passed; actual = $Actual; limit = $Limit })
}
$testTimings = [Collections.Generic.List[object]]::new()
foreach ($result in $outcomes) {
    $name = [string]$result.testName
    $duration = [TimeSpan]::Parse([string]$result.duration, [Globalization.CultureInfo]::InvariantCulture).TotalMilliseconds
    $testTimings.Add([ordered]@{ test = $name; outcome = [string]$result.outcome; elapsedMilliseconds = $duration })
    Check ('test:' + $name) ($result.outcome -ceq 'Passed') ([string]$result.outcome) 'Passed'
    # Only per-test StdOut is admissible: aggregate runner output may repeat or mix other tests' data.
    foreach ($line in ([string]$result.Output.StdOut -split '\r?\n')) {
        if (-not $line.StartsWith('PLATFORM_FOUNDATION_LOAD ', [StringComparison]::Ordinal)) { continue }
        $m = $line.Substring('PLATFORM_FOUNDATION_LOAD '.Length) | ConvertFrom-Json
        if ($m.schema -cne 'platform-studio.foundation-load-measurement.v1' -or $m.targetsVersion -cne $targetVersion -or
            $m.localQualificationOnly -ne $true -or $m.productionSloVerified -ne $false -or $m.externalDispatch -ne $false -or
            $m.scenario -notin @('concurrent-enqueue-backpressure','isolated-signed-api-under-pressure') -or $measurements.ContainsKey($m.scenario)) {
            throw 'Ambiguous or unsupported load measurement evidence.'
        }
        $measurements[$m.scenario] = $m
    }
    foreach ($definition in @(
        @{ match = 'Two_real_workers_compete'; name = 'competingProcesses'; milliseconds = $targets.competingProcesses.maximumTestElapsedMilliseconds },
        @{ match = 'Crashed_pre_effect'; name = 'preEffectRecovery'; milliseconds = $targets.preEffectRecovery.maximumTestElapsedMilliseconds },
        @{ match = 'Crashed_post_effect'; name = 'postEffectNoReplay'; milliseconds = $targets.postEffectNoReplay.maximumTestElapsedMilliseconds }
    )) {
        if ($name.Contains($definition.match, [StringComparison]::Ordinal)) { Check $definition.name ($duration -le $definition.milliseconds) $duration $definition.milliseconds }
    }
}
$scenarios = [Collections.Generic.List[object]]::new()
foreach ($scenario in @('concurrent-enqueue-backpressure','isolated-signed-api-under-pressure')) {
    if (-not $measurements.ContainsKey($scenario)) { Check ($scenario + ':measurement') $false 'Missing' 'Exactly one per-test measurement'; continue }
    $m = $measurements[$scenario]; $distribution = Distribution ([double[]]$m.samplesMilliseconds)
    if ($scenario -ceq 'concurrent-enqueue-backpressure') {
        $workload = [ordered]@{ clients = [int]$m.workload.clients; attempts = [int]$m.workload.attempts; accepted = [int]$m.workload.accepted;
            rejected429 = [int]$m.workload.rejected429; queueLimit = [int]$m.workload.queueLimit; arrivalPacingMilliseconds = [int]$m.workload.arrivalPacingMilliseconds }
        Check 'enqueue:observations' ($distribution.count -eq 160) $distribution.count 160
        Check 'enqueue:workload' ($workload.clients -eq 4 -and $workload.attempts -eq 160 -and $workload.queueLimit -eq 128 -and $workload.arrivalPacingMilliseconds -eq 0) $workload $targets.enqueue
        Check 'enqueue:accepted' ($m.workload.accepted -eq 128) $m.workload.accepted 128
        Check 'enqueue:backpressure429' ($m.workload.rejected429 -eq 32) $m.workload.rejected429 32
        $observedTargets = [ordered]@{ p95Milliseconds = [double]$m.targets.p95Milliseconds; elapsedMilliseconds = [double]$m.targets.elapsedMilliseconds }
        Check 'enqueue:fixedTargets' ($m.targets.p95Milliseconds -eq 5000 -and $m.targets.elapsedMilliseconds -eq 60000) $observedTargets $targets.enqueue
        Check 'enqueue:p95' ($distribution.p95Milliseconds -le 5000) $distribution.p95Milliseconds 5000
        Check 'enqueue:elapsed' ([double]::IsFinite($m.elapsedMilliseconds) -and $m.elapsedMilliseconds -ge 0 -and $m.elapsedMilliseconds -le 60000) $m.elapsedMilliseconds 60000
    } else {
        $workload = [ordered]@{ cells = [int]$m.workload.cells; signedApiReads = [int]$m.workload.signedApiReads; statuses200 = [int]$m.workload.statuses200;
            overlappedReads = [int]$m.workload.overlappedReads; pressureClients = [int]$m.workload.pressureClients; pressureAttempts = [int]$m.workload.pressureAttempts;
            accepted = [int]$m.workload.accepted; rejected429 = [int]$m.workload.rejected429; heldProviderRequests = [int]$m.workload.heldProviderRequests;
            arrivalPacingMilliseconds = [int]$m.workload.arrivalPacingMilliseconds }
        Check 'isolation:observations' ($distribution.count -eq 40 -and $m.workload.statuses200 -eq 40) $distribution.count 40
        Check 'isolation:workload' ($workload.cells -eq 2 -and $workload.signedApiReads -eq 40 -and $workload.pressureClients -eq 4 -and $workload.pressureAttempts -eq 159 -and $workload.arrivalPacingMilliseconds -eq 2) $workload $targets.isolation
        Check 'isolation:accepted' ($m.workload.accepted -eq 127) $m.workload.accepted 127
        Check 'isolation:backpressure429' ($m.workload.rejected429 -eq 32) $m.workload.rejected429 32
        Check 'isolation:overlap' ($m.workload.overlappedReads -ge 1 -and $m.workload.heldProviderRequests -eq 1) $m.workload.overlappedReads '>=1, with one held provider request'
        $observedTargets = [ordered]@{ p95Milliseconds = [double]$m.targets.p95Milliseconds; maximumMilliseconds = [double]$m.targets.maximumMilliseconds; elapsedMilliseconds = [double]$m.targets.elapsedMilliseconds }
        Check 'isolation:fixedTargets' ($m.targets.p95Milliseconds -eq 3000 -and $m.targets.maximumMilliseconds -eq 10000 -and $m.targets.elapsedMilliseconds -eq 60000) $observedTargets $targets.isolation
        Check 'isolation:p95' ($distribution.p95Milliseconds -le 3000) $distribution.p95Milliseconds 3000
        Check 'isolation:maximum' ($distribution.maximumMilliseconds -le 10000) $distribution.maximumMilliseconds 10000
        Check 'isolation:elapsed' ([double]::IsFinite($m.elapsedMilliseconds) -and $m.elapsedMilliseconds -ge 0 -and $m.elapsedMilliseconds -le 60000) $m.elapsedMilliseconds 60000
    }
    $scenarios.Add([ordered]@{ scenario = $scenario; elapsedMilliseconds = [double]$m.elapsedMilliseconds; workload = $workload;
        latency = $distribution; samplesMilliseconds = [double[]]$m.samplesMilliseconds })
}
$total.Stop()
$passed = @($outcomes | Where-Object { $_.outcome -ceq 'Passed' }).Count
$accepted = $testExitCode -eq 0 -and $passed -eq 5 -and @($checks | Where-Object { -not $_.passed }).Count -eq 0
$receipt = [ordered]@{
    schema = 'platform-studio.foundation-load-qualification.v1'; targetsVersion = $targetVersion
    startedUtc = $started.ToString('O'); observedUtc = [DateTimeOffset]::UtcNow.ToString('O'); machine = $machine
    runtime = [ordered]@{ sdk = $sdk; powershell = $PSVersionTable.PSVersion.ToString(); profile = 'net10.0-release-native-windows';
        measuredTestFrameworks = @($measurements.Values | ForEach-Object { if ($_.runtime -match '^\.NET \d+\.\d+\.\d+$') { $_.runtime } } | Sort-Object -Unique);
        measuredTestArchitectures = @($measurements.Values | ForEach-Object { if ($_.processArchitecture -in @('Arm64','X64','X86','Arm')) { $_.processArchitecture } } | Sort-Object -Unique);
        measuredLogicalProcessorCounts = @($measurements.Values | ForEach-Object { if ([int]$_.logicalProcessors -gt 0 -and [int]$_.logicalProcessors -le 1024) { [int]$_.logicalProcessors } } | Sort-Object -Unique) }
    source = [ordered]@{ scriptSha256 = (Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant();
        testsSha256 = (Get-FileHash -LiteralPath (Join-Path $root 'tests/SelfService.Foundation.Tests/FoundationLoadQualificationTests.cs') -Algorithm SHA256).Hash.ToLowerInvariant() }
    elapsed = [ordered]@{ profileMilliseconds = $total.Elapsed.TotalMilliseconds; restoreMilliseconds = $restoreMilliseconds;
        buildAndTestInvocationMilliseconds = $testInvocationMilliseconds }
    tests = [ordered]@{ total = $outcomes.Count; passed = $passed; failedOrNotExecuted = $outcomes.Count - $passed; timings = @($testTimings) }
    targets = $targets; checks = @($checks); scenarios = @($scenarios); accepted = $accepted
    percentileMethod = 'nearest-rank'; localQualificationOnly = $true; productionSloVerified = $false
    externalDispatch = $false; deploymentVerified = $false
}
$receipt | ConvertTo-Json -Depth 12 | Set-Content -LiteralPath (Join-Path $results 'summary.json') -Encoding utf8
if (-not $accepted) { throw "Local load qualification failed; inspect $results/summary.json. Targets were not changed." }
Write-Host "Local load qualification: 5 tests passed, all fixed targets passed. $results"
