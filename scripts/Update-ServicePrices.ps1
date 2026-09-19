#requires -Version 7.4
[CmdletBinding()]
param([string]$OutputPath=(Join-Path $PSScriptRoot '../self-service/pricing/usd-eastus2.json'))
. "$PSScriptRoot/self-service-common.ps1"
# Public retail API only. This does not sign in, query a subscription or deploy.
function Read-RetailPrices([string]$Filter) {
    $url='https://prices.azure.com/api/retail/prices?currencyCode=USD&$filter='+[uri]::EscapeDataString($Filter+" and priceType eq 'Consumption'")
    $items=@()
    while ($url) {
        $response=$null
        for ($attempt=1; $attempt -le 4; $attempt++) {
            try { $response=Invoke-RestMethod -Uri $url -TimeoutSec 45; break }
            catch { if ($attempt -eq 4) { throw }; Start-Sleep -Seconds (5*$attempt) }
        }
        $items+=@($response.Items); $url=$response.NextPageLink
    }
    return $items
}
function Select-Meter($Items,[string]$Name,[string]$Region,[double]$Tier,[string]$Unit) {
    $matches=@($Items | Where-Object { $_.meterName -ceq $Name -and $_.armRegionName -ceq $Region -and $_.tierMinimumUnits -eq $Tier -and $_.unitOfMeasure -ceq $Unit -and $_.currencyCode -eq 'USD' })
    if ($matches.Count -ne 1 -or $matches[0].retailPrice -le 0) { throw "Expected exactly one positive USD meter: $Name / $Region / $Tier / $Unit; got $($matches.Count). Existing snapshot was not replaced." }
    return $matches[0]
}
$rates=[ordered]@{}
$apps=Read-RetailPrices "serviceName eq 'Azure App Service' and armRegionName eq 'eastus2' and contains(productName, 'Linux')"
foreach ($pair in @(@('B1','B1'),@('S1','S1 App'),@('P1v3','P1 v3 App'))) { $rates[$pair[0]]=Select-Meter $apps $pair[1] eastus2 0 '1 Hour' }
$private=Read-RetailPrices "productName eq 'Virtual Network Private Link' and armRegionName eq 'Global'"
$rates.privateEndpoint=Select-Meter $private 'Standard Private Endpoint' Global 0 '1 Hour'
$dns=Read-RetailPrices "serviceName eq 'Azure DNS' and skuName eq 'Private' and armRegionName eq 'Zone 1'"
$rates.privateDnsZone=Select-Meter $dns 'Private Zone' 'Zone 1' 0 '1'
$rates.privateDnsQueries=Select-Meter $dns 'Private Queries' 'Zone 1' 0 '1M'
$alerts=Read-RetailPrices "serviceName eq 'Azure Monitor' and armRegionName eq 'eastus2' and skuName eq 'Alerts'"
$rates.logAlert=Select-Meter $alerts 'Alerts System Log Monitored at 5 Minute Frequency' eastus2 0 '1/Month'
$logs=Read-RetailPrices "serviceName eq 'Log Analytics' and armRegionName eq 'eastus2' and skuName eq 'Analytics Logs'"
$rates.logIngestion=Select-Meter $logs 'Analytics Logs Data Ingestion' eastus2 5 '1 GB'
$snapshot=@{schemaVersion=1;currency='USD';region='eastus2';hoursPerMonth=730;retrievedUtc=[DateTimeOffset]::UtcNow.ToString('O');source='https://prices.azure.com/api/retail/prices';rates=$rates;
    assumptions=@('Public pay-as-you-go retail; no tax, discounts, credits or reservations.','Private DNS zone price uses the first-25-zones monthly tier; API unit 1 represents a zone-month.','Log alerts use three 5-minute rules without dimensional splitting.','Fixed subtotal excludes storage, transactions, data processing/egress, DNS queries, log ingestion/retention, notifications, existing destination and private agent costs.','Log ingestion unit rate is the paid tier; free allowances depend on subscription usage.')}
Write-ServiceJson $snapshot $OutputPath
Write-Host "Saved $($rates.Count) verified retail meters: $OutputPath. Review the snapshot and regenerate the catalog."
