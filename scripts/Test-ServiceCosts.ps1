#requires -Version 7.4
[CmdletBinding()]
param()
. "$PSScriptRoot/service-cost-common.ps1"
$results=[Collections.Generic.List[object]]::new()
function Check([bool]$Condition) { if (!$Condition) { throw 'Assertion failed.' } }
function Clone($Value) { ConvertFrom-Json ($Value | ConvertTo-Json -Depth 100) -AsHashtable }
function Case([string]$Name,[scriptblock]$Body) { try { & $Body; $results.Add(@{name=$Name;passed=$true}) } catch { throw "Case '$Name': $($_.Exception.Message)" } }
function Reject([scriptblock]$Body) { $caught=$false; try { & $Body | Out-Null } catch { $caught=$true }; Check $caught }
$prices=@{retrievedUtc=[DateTimeOffset]::UtcNow.ToString('O');currency='USD';region='eastus2';source='offline fixture';assumptions=@('Usage excluded');rates=@{}}
foreach ($pair in @(@('B1',0.017),@('S1',0.095),@('P1v3',0.155),@('privateEndpoint',0.01),@('privateDnsZone',0.5),@('logAlert',1.5),@('logIngestion',2.76),@('privateDnsQueries',0.4))) { $prices.rates[$pair[0]]=@{retailPrice=$pair[1]} }
$parameters=@{location=@{value='eastus2'};planSku=@{value='B1'};instanceCount=@{value=1};destinationIsHnsEnabled=@{value=$true}}
$target=@{environmentName='dev'}
foreach ($destination in @($true,$false)) {
    foreach ($alerts in @($true,$false)) {
        Case "checkbox options change frozen parameters and subtotal: endpoints=$destination alerts=$alerts" {
            $p=Clone $parameters
            Set-ServiceDeploymentOptions $target $p $destination $alerts
            Check ($p.createDestinationPrivateEndpoints.value -eq $destination -and $p.enableLogAlerts.value -eq $alerts)
            $estimate=Get-ServiceCostEstimate $p $prices
            $expected=58.71 + $(if ($destination) {14.60} else {0}) + $(if ($alerts) {4.50} else {0})
            Check ($estimate.status -eq 'Estimated' -and $estimate.fixedMonthlySubtotalUsd -eq [Math]::Round($expected,2))
            Check ($estimate.selection.destinationPrivateEndpoints -eq $(if ($destination) {2} else {0}))
            Check ($estimate.selection.enabledLogAlerts -eq $(if ($alerts) {3} else {0}))
        }
    }
}
Case 'non-HNS destination provisions and prices one destination endpoint' {
    $p=Clone $parameters; $p.destinationIsHnsEnabled.value=$false
    $e=Get-ServiceCostEstimate $p $prices
    Check ($e.selection.destinationPrivateEndpoints -eq 1 -and $e.fixedMonthlySubtotalUsd -eq 70.51)
}
Case 'existing network excludes newly provisioned DNS but retains required endpoints' {
    $p=Clone $parameters; $p.networkMode=@{value='existing'}
    $e=Get-ServiceCostEstimate $p $prices
    Check ($e.selection.newPrivateDnsZones -eq 0 -and $e.fixedMonthlySubtotalUsd -eq 75.31)
}
Case 'scale and hosting tiers affect fixed estimate' {
    $p=Clone $parameters; $p.planSku.value='P1v3'; $p.instanceCount.value=3
    Check ((Get-ServiceCostEstimate $p $prices).fixedMonthlySubtotalUsd -eq 404.85)
    $p.planSku.value='S1'; $p.instanceCount.value=1
    Check ((Get-ServiceCostEstimate $p $prices).fixedMonthlySubtotalUsd -eq 134.75)
}
Case 'production refuses unchecked alerts without mutating parameters' {
    $p=Clone $parameters; $before=Get-ValueHash $p
    Reject { Set-ServiceDeploymentOptions @{environmentName='prod'} $p $true $false }
    Check ((Get-ValueHash $p) -eq $before)
}
Case 'production permits alerts and destination connectivity reuse' {
    $p=Clone $parameters; Set-ServiceDeploymentOptions @{environmentName='prod'} $p $false $true
    Check ($p.enableLogAlerts.value -and !$p.createDestinationPrivateEndpoints.value)
}
foreach ($region in @('westus2','')) {
    Case "unpriced or implicit region '$region' returns unavailable rather than zero" {
        $p=Clone $parameters; $p.location.value=$region
        $e=Get-ServiceCostEstimate $p $prices
        Check ($e.status -eq 'Unavailable' -and $null -eq $e.fixedMonthlySubtotalUsd)
    }
}
foreach ($days in @(-31,2)) {
    Case "stale or future price snapshot ($days days) cannot yield a current estimate" {
        $old=Clone $prices; $old.retrievedUtc=[DateTimeOffset]::UtcNow.AddDays($days).ToString('O')
        $e=Get-ServiceCostEstimate $parameters $old
        Check ($e.status -eq 'Unavailable' -and $null -eq $e.fixedMonthlySubtotalUsd)
    }
}
Case 'unsupported SKU returns unavailable' {
    $p=Clone $parameters; $p.planSku.value='F1'
    Check ((Get-ServiceCostEstimate $p $prices).status -eq 'Unavailable')
}
$testRoot=Join-Path (Get-ProjectRoot) ('artifacts/cost-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
Case 'selected option report preserves exclusions and estimate basis' {
    $e=Get-ServiceCostEstimate $parameters $prices
    Write-ServiceCostSummary $e (Join-Path $testRoot summary.md)
    $text=Get-Content (Join-Path $testRoot summary.md) -Raw
    Check ($text.Contains('$77.81') -and $text.Contains('730 hours/month') -and $text.Contains('Usage excluded') -and $text.Contains('not an Azure quote'))
}
Write-ServiceJson @{passed=$results.Count;failed=0;azureCalls='none; deterministic price fixtures';cases=$results} (Join-Path $testRoot results.json)
Write-Host "PASS: $($results.Count) option/cost cases. Evidence: $testRoot"
