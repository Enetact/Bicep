#requires -Version 7.4
. "$PSScriptRoot/self-service-common.ps1"
function Read-ServicePrices {
    $path=Join-Path (Get-ProjectRoot) self-service/pricing/usd-eastus2.json
    $prices=Get-Content $path -Raw | ConvertFrom-Json -AsHashtable
    if ($prices.schemaVersion -ne 1 -or $prices.currency -ne 'USD' -or $prices.region -ne 'eastus2' -or $prices.hoursPerMonth -ne 730) { throw 'Unsupported price snapshot.' }
    foreach ($key in @('B1','S1','P1v3','privateEndpoint','privateDnsZone','privateDnsQueries','logAlert','logIngestion')) {
        if (!$prices.rates.Contains($key) -or $prices.rates[$key].retailPrice -le 0) { throw "Missing retail price: $key" }
    }
    return $prices
}
function Format-ServiceUsd([double]$Amount) { '$'+$Amount.ToString('F2',[Globalization.CultureInfo]::InvariantCulture) }
function Get-ServiceCostEstimate($Parameters, $Prices=(Read-ServicePrices)) {
    $region=Get-ServiceParameter $Parameters location ''
    $sku=Get-ServiceParameter $Parameters planSku P1v3
    $instances=[int](Get-ServiceParameter $Parameters instanceCount 2)
    $age=([DateTimeOffset]::UtcNow-[DateTimeOffset]::Parse($Prices.retrievedUtc)).TotalDays
    $exclusions=@($Prices.assumptions)
    $reason=if ($region -ne $Prices.region) { "No reviewed price snapshot for location '$region'." } elseif ($sku -notin @('B1','S1','P1v3') -or $instances -lt 1) { 'Unsupported hosting SKU or instance count.' } elseif ($age -gt 30 -or $age -lt -1) { 'Retail snapshot is stale or future-dated; refresh pricing.' } else { '' }
    if ($reason) { return @{status='Unavailable';reason=$reason;currency='USD';region=$region;pricingAsOf=$Prices.retrievedUtc;fixedMonthlySubtotalUsd=$null;exclusions=$exclusions} }
    $destinationEndpoints=if (Get-ServiceParameter $Parameters createDestinationPrivateEndpoints $true) { if (Get-ServiceParameter $Parameters destinationIsHnsEnabled $true) {2} else {1} } else {0}
    $zones=if ((Get-ServiceParameter $Parameters networkMode new) -eq 'new') {5} else {0}
    $alerts=if (Get-ServiceParameter $Parameters enableLogAlerts $true) {3} else {0}
    $lines=@(
        @{resource="Linux $sku hosting";quantity=$instances;monthlyUnitUsd=([double]$Prices.rates[$sku].retailPrice*730)},
        @{resource='Required storage and Function App private endpoints';quantity=6;monthlyUnitUsd=([double]$Prices.rates.privateEndpoint.retailPrice*730)},
        @{resource='Destination private endpoints';quantity=$destinationEndpoints;monthlyUnitUsd=([double]$Prices.rates.privateEndpoint.retailPrice*730)},
        @{resource='New private DNS zones';quantity=$zones;monthlyUnitUsd=[double]$Prices.rates.privateDnsZone.retailPrice},
        @{resource='Enabled 5-minute log alerts';quantity=$alerts;monthlyUnitUsd=[double]$Prices.rates.logAlert.retailPrice}
    )
    $subtotal=0.0
    foreach ($line in $lines) { $line.monthlyUsd=[Math]::Round(($line.quantity*$line.monthlyUnitUsd),2); $subtotal+=$line.monthlyUsd }
    return @{status='Estimated';currency='USD';region=$region;pricingAsOf=$Prices.retrievedUtc;hoursPerMonth=730;phase='Full Release steady state';
        fixedMonthlySubtotalUsd=[Math]::Round($subtotal,2);lines=$lines;exclusions=$exclusions;source=$Prices.source;
        selection=@{planSku=$sku;instanceCount=$instances;destinationPrivateEndpoints=$destinationEndpoints;newPrivateDnsZones=$zones;enabledLogAlerts=$alerts};
        unitRates=@{logIngestionUsdPerGb=$Prices.rates.logIngestion.retailPrice;dnsQueriesUsdPerMillion=$Prices.rates.privateDnsQueries.retailPrice};
        note='Estimated ongoing footprint, not an Azure quote or incremental bill. Existing resources may remain after options are unchecked; review what-if. Usage costs are additional.'}
}
function Write-ServiceCostSummary($Estimate,[string]$Path) {
    $lines=@('# Deployment cost estimate','',"Status: $($Estimate.status). Currency: USD. Region: $($Estimate.region). Retail snapshot: $($Estimate.pricingAsOf).",'')
    if ($Estimate.status -eq 'Estimated') {
        $lines+=@('Full Release steady state at 730 hours/month.','', '| Resource | Quantity | Estimated USD/month |','|---|---:|---:|')
        foreach ($line in $Estimate.lines) { $lines+="| $($line.resource) | $($line.quantity) | $(Format-ServiceUsd $line.monthlyUsd) |" }
        $lines+=@('',"Fixed monthly subtotal: **$(Format-ServiceUsd $Estimate.fixedMonthlySubtotalUsd)**, plus usage.",'',$Estimate.note)
    } else { $lines+=$Estimate.reason }
    $lines+=@('','Excluded or usage dependent:')+@($Estimate.exclusions | ForEach-Object { "- $_" })
    $lines | Set-Content -LiteralPath $Path
}
