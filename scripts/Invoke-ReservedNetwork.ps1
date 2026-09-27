#requires -Version 7.4
[CmdletBinding()]
param([ValidateSet('Preview','Apply')][string]$Mode='Preview',[Parameter(Mandatory)][string]$ReservationDirectory,[Parameter(Mandatory)][string]$OutputDirectory,[string]$ExpectedPreviewPath='')
. "$PSScriptRoot/network-allocation-common.ps1"
$root=Split-Path $PSScriptRoot -Parent
$saved=Get-Content -LiteralPath (Join-Path $ReservationDirectory 'intent.json') -Raw|ConvertFrom-Json
$intent=Get-NetworkIntent $saved.profile $saved.workload $saved.environment (Join-Path $root 'config/network-allocation.json')
if($intent.intentHash -ne $saved.intentHash -or $intent.allocationId -ne $saved.allocationId){throw 'Reservation intent drifted.'}
$account=& az account show --output json --only-show-errors|ConvertFrom-Json
if($LASTEXITCODE -ne 0 -or $account.tenantId -ne $intent.settings.tenantId -or $account.id -ne $intent.settings.subscriptionId){throw 'Service connection does not match the reviewed pool scope.'}
$allocation=Invoke-NetworkRead $intent.allocationId
$parameters=Get-ReservedNetworkParameters $intent $allocation
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$file=Join-Path $OutputDirectory 'network.parameters.json'
@{'$schema'='https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#';contentVersion='1.0.0.0';parameters=$parameters}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath $file
$template=Join-Path $root 'platform/network/reserved-spoke.bicep'
# Initial delivery is create-only. Existing resources or changed network configuration require separate platform review.
$whatIf=& az deployment sub what-if --location $intent.settings.location --template-file $template --parameters "@$file" --no-pretty-print --result-format FullResourcePayloads --output json --only-show-errors
if($LASTEXITCODE -ne 0){throw 'Reserved network What-If failed; allocation remains held.'}
$plan=($whatIf -join "`n")|ConvertFrom-Json -AsHashtable
if($plan.status -ne 'Succeeded' -or !$plan.ContainsKey('changes')){throw 'What-If is incomplete; allocation remains held.'}
if(@($plan.changes|Where-Object {$_.changeType -notin @('Create','NoChange')}).Count){throw 'Initial reserved-network flow allows only Create/NoChange. Modifications, unknown changes and deletion require a separate platform review.'}
$canonical=@($plan.changes|Sort-Object resourceId)|ConvertTo-Json -Depth 100 -Compress
$digest=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($canonical))).ToLowerInvariant()
$templateHash=(Get-FileHash -LiteralPath $template -Algorithm SHA256).Hash
$moduleHash=(Get-FileHash -LiteralPath (Join-Path $root 'platform/network/reserved-vnet.bicep') -Algorithm SHA256).Hash
$preview=[ordered]@{schemaVersion=1;intentHash=$intent.intentHash;whatIfHash=$digest;templateHash=$templateHash;moduleHash=$moduleHash;changes=$plan.changes}
$preview|ConvertTo-Json -Depth 100|Set-Content -LiteralPath (Join-Path $OutputDirectory 'preview.json')
$rows=@($plan.changes|ForEach-Object {"- $($_.changeType): $($_.resourceId)"}) -join "`n"
"# Reserved network What-If`n`n$rows`n`nAddress authority: $($intent.allocationId)`n`nNo effective connectivity or workload readiness is implied."|Set-Content -LiteralPath (Join-Path $OutputDirectory 'README.md')
if($Mode -eq 'Apply'){
    if(!$env:TF_BUILD -or $env:BUILD_SOURCEBRANCH -ne 'refs/heads/main' -or !$intent.settings.enabled -or !$intent.settings.exclusiveLockReviewed -or !$intent.settings.externalPrefixesReconciled){throw 'Apply requires reviewed enabled profile and protected ADO main.'}
    if(!$ExpectedPreviewPath){throw 'Expected same-run Preview artifact is required.'}
    $expected=Get-Content -LiteralPath $ExpectedPreviewPath -Raw|ConvertFrom-Json
    foreach($key in @('intentHash','whatIfHash','templateHash','moduleHash')){if($preview[$key] -ne $expected.$key){throw "Network Preview drift: $key. Allocation remains held; obtain a new Preview/approval."}}
    # Do not adopt an existing RG, even if its names happen to match. A successful earlier apply is reconciled through its receipt.
    $existing=& az group exists --name $parameters.resourceGroupName.value --output tsv --only-show-errors
    if($LASTEXITCODE -ne 0 -or $existing -ne 'false'){throw 'Network resource group already exists or visibility is unknown. Reconcile the earlier stack; no implicit adoption or overwrite.'}
    $stacks=& az stack sub list --output json --only-show-errors
    if($LASTEXITCODE -ne 0){throw 'Stack ownership inventory failed. No deployment is authorized.'}
    if(@(($stacks -join "`n"|ConvertFrom-Json)|Where-Object {$_.name -eq $intent.allocationName}).Count){throw 'A network stack with this identity already exists. Reconcile it; create-only does not update or detach existing resources.'}
    & az stack sub create --name $intent.allocationName --location $intent.settings.location --template-file $template --parameters "@$file" --action-on-unmanage detachAll --deny-settings-mode none --yes --output json --only-show-errors | Set-Content -LiteralPath (Join-Path $OutputDirectory 'stack-result.json')
    if($LASTEXITCODE -ne 0){throw 'Network apply outcome uncertain. Keep the IPAM allocation and reconcile the stack; do not release or blindly retry.'}
    @{schemaVersion=1;status='NetworkCreated';allocationId=$intent.allocationId;intentHash=$intent.intentHash;resourceGroup=$parameters.resourceGroupName.value;vnetId="/subscriptions/$($intent.settings.subscriptionId)/resourceGroups/$($parameters.resourceGroupName.value)/providers/Microsoft.Network/virtualNetworks/$($parameters.vnetName.value)";workloadDeploymentAuthorized=$false;effectiveConnectivityVerified=$false;observedUtc=[DateTimeOffset]::UtcNow.ToString('O')}|ConvertTo-Json|Set-Content -LiteralPath (Join-Path $OutputDirectory 'binding.json')
}
