[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('dev','qa','uat','prod')][string]$EnvironmentName,
    [Parameter(Mandatory)][string]$SubscriptionId,
    [Parameter(Mandatory)][string]$ResourceGroup,
    [ValidateSet('Bootstrap','Release')][string]$Phase = 'Bootstrap',
    [ValidateSet('WhatIf','Deploy')][string]$Mode = 'WhatIf',
    [string]$ReleaseId = '',
    [string]$PackagePath = ''
)
. "$PSScriptRoot/common.ps1"
$output = Export-Templates -EnvironmentName $EnvironmentName
$parameterFile = Join-Path $output 'parameters.json'
$document = Get-Content $parameterFile -Raw | ConvertFrom-Json -AsHashtable
$parameters = $document.parameters
$uploadContainer = if ($parameters.ContainsKey('uploadContainerName')) { $parameters.uploadContainerName.value } else { 'incoming' }
$ledgerContainer = if ($parameters.ContainsKey('ledgerContainerName')) { $parameters.ledgerContainerName.value } else { 'transfer-ledger' }
if ($uploadContainer -eq $ledgerContainer) { throw 'Upload and ledger containers must be different to prevent dispatch loops.' }
$serialized = $document | ConvertTo-Json -Depth 100
if ($serialized -match 'REPLACE_|00000000-0000-0000-0000-000000000000') { throw 'Edit the environment placeholders before deployment.' }
if ($parameters.workload.value -notmatch '^[a-z0-9]{3,10}$') { throw 'Workload must contain 3-10 lowercase letters or digits.' }
if ($Phase -eq 'Release' -and $ReleaseId -notmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$') { throw 'Release requires an immutable ReleaseId.' }
if ($EnvironmentName -eq 'prod' -and (!$parameters.ContainsKey('alertActionGroupIds') -or $parameters.alertActionGroupIds.value.Count -eq 0)) {
    throw 'Production requires an existing monitored Azure Monitor action group.'
}
if ($parameters.ContainsKey('zoneRedundant') -and $parameters.zoneRedundant.value -and
    ($parameters.planSku.value -ne 'P1v3' -or $parameters.instanceCount.value -lt 2)) { throw 'Zone redundancy requires a supported Premium plan and at least two instances.' }

$null = Invoke-Az -Arguments @('group','show','--name',$ResourceGroup,'--subscription',$SubscriptionId,'--output','json')
$targetSubscription = $parameters.destinationSubscriptionId.value
$targetRg = $parameters.destinationResourceGroupName.value
$targetAccount = $parameters.destinationStorageAccountName.value
$targetContainer = $parameters.destinationContainerName.value
$currentTenant = (Invoke-Az -Arguments @('account','show','--subscription',$SubscriptionId,'--output','json') | ConvertFrom-Json).tenantId
$targetTenant = (Invoke-Az -Arguments @('account','show','--subscription',$targetSubscription,'--output','json') | ConvertFrom-Json).tenantId
if ($currentTenant -ne $targetTenant) { throw 'Cross-tenant destination access is not supported by this project.' }
$destination = Invoke-Az -Arguments @('storage','account','show','--name',$targetAccount,'--resource-group',$targetRg,'--subscription',$targetSubscription,'--output','json') | ConvertFrom-Json
if ([bool]$destination.isHnsEnabled -ne [bool]$parameters.destinationIsHnsEnabled.value) { throw 'destinationIsHnsEnabled does not match the existing account.' }
$containerId = "$($destination.id)/blobServices/default/containers/$targetContainer"
$null = Invoke-Az -Arguments @('resource','show','--ids',$containerId,'--api-version','2025-01-01','--output','json')
if ($destination.publicNetworkAccess -ne 'Disabled') { Write-Warning 'Existing destination public access is not disabled. Have its owner review its network policy; this template does not change it.' }

$parameters.deployFunctionApp = @{ value = ($Phase -eq 'Release') }
if ($Phase -eq 'Release') { $parameters.packageBlobName = @{ value = "releases/$ReleaseId.zip" } }
$effective = Join-Path $output 'effective.parameters.json'
$document | ConvertTo-Json -Depth 100 | Set-Content $effective -Encoding utf8
$deploymentName = "blobcopy-$EnvironmentName"
$common = @('--subscription',$SubscriptionId,'--resource-group',$ResourceGroup,'--name',$deploymentName,'--template-file',(Join-Path $output 'main.json'),'--parameters',"@$effective")
if ($Mode -eq 'WhatIf') {
    Invoke-Az -Arguments (@('deployment','group','what-if') + $common)
    return
}

if ($Phase -eq 'Release') {
    if (!(Test-Path -LiteralPath $PackagePath -PathType Leaf)) { throw 'Release requires a built ZIP PackagePath.' }
    $receipt = Invoke-Az -Arguments @('deployment','group','show','--name',$deploymentName,'--resource-group',$ResourceGroup,'--subscription',$SubscriptionId,'--output','json') | ConvertFrom-Json
    $hostAccount = $receipt.properties.outputs.hostStorageAccountName.value
    $packageContainer = $receipt.properties.outputs.packageContainer.value
    # Must run from a private network connected to the VNet. Explicit Entra auth; never falls back to keys.
    $releaseBlob = "releases/$ReleaseId.zip"
    $exists = Invoke-Az -Arguments @('storage','blob','exists','--account-name',$hostAccount,'--container-name',$packageContainer,'--name',$releaseBlob,'--auth-mode','login','--subscription',$SubscriptionId,'--output','json') | ConvertFrom-Json
    if ($exists.exists) {
        $downloadPath = Join-Path $output "$ReleaseId.existing.zip"
        $null = Invoke-Az -Arguments @('storage','blob','download','--account-name',$hostAccount,'--container-name',$packageContainer,'--name',$releaseBlob,'--file',$downloadPath,'--overwrite','true','--auth-mode','login','--subscription',$SubscriptionId,'--output','json')
        if ((Get-FileHash $PackagePath).Hash -ne (Get-FileHash $downloadPath).Hash) { throw 'Release ID already exists with different bytes. Choose a new ID.' }
    } else {
        $null = Invoke-Az -Arguments @('storage','blob','upload','--account-name',$hostAccount,'--container-name',$packageContainer,'--name',$releaseBlob,'--file',$PackagePath,'--auth-mode','login','--overwrite','false','--subscription',$SubscriptionId,'--output','json')
    }
}
Invoke-Az -Arguments (@('deployment','group','create','--mode','Incremental') + $common)
if ($Phase -eq 'Release') {
    $receipt = Invoke-Az -Arguments @('deployment','group','show','--name',$deploymentName,'--resource-group',$ResourceGroup,'--subscription',$SubscriptionId,'--output','json') | ConvertFrom-Json
    $appId = $receipt.properties.outputs.functionAppResourceId.value
    # Identity propagation/startup may take several minutes. Do not mistake ARM success for app readiness.
    $synced = $false
    for ($attempt = 0; $attempt -lt 10; $attempt++) {
        try {
            $null = Invoke-Az -Arguments @('rest','--method','post','--url',"https://management.azure.com${appId}/syncfunctiontriggers?api-version=2024-11-01")
            $synced = $true
            break
        } catch { Write-Warning 'Trigger sync not ready; retrying in 30 seconds.'; Start-Sleep -Seconds 30 }
    }
    if (!$synced) { throw 'Trigger synchronization failed. Check package access, RBAC, approved endpoints, DNS, and runtime logs.' }
    Write-Host 'Trigger sync accepted. Run the end-to-end smoke test to establish readiness.'
}
