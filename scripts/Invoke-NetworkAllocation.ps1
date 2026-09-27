#requires -Version 7.4
[CmdletBinding()]
param(
    [ValidateSet('Plan','Reserve','Reconcile')][string]$Mode='Plan',
    [string]$Profile='avnm-private-web',
    [Parameter(Mandatory)][string]$Workload,
    [ValidateSet('dev','qa','uat','prod')][string]$EnvironmentName='dev',
    [string]$ExpectedIntentPath='',
    [Parameter(Mandatory)][string]$OutputDirectory
)
. "$PSScriptRoot/network-allocation-common.ps1"
$root=Split-Path $PSScriptRoot -Parent
New-Item -ItemType Directory -Path $OutputDirectory -Force|Out-Null
$intent=Get-NetworkIntent $Profile $Workload $EnvironmentName (Join-Path $root 'config/network-allocation.json')
$account=& az account show --output json --only-show-errors|ConvertFrom-Json
if($LASTEXITCODE -ne 0 -or $account.tenantId -ne $intent.settings.tenantId -or $account.id -ne $intent.settings.subscriptionId){throw 'Service connection must be in the configured pool tenant/subscription.'}
$null=Invoke-NetworkRead $intent.settings.poolId
$intent|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $OutputDirectory 'intent.json')
$status='Proposed';$allocation=$null;$failure=$null
try {
    $allocation=Invoke-NetworkRead $intent.allocationId -AllowMissing
    if($allocation){$null=Assert-NetworkAllocation $intent $allocation;$status='Reserved'}
    if($Mode -eq 'Reserve' -and !$allocation){
        if(!$intent.settings.enabled -or !$intent.settings.exclusiveLockReviewed -or !$intent.settings.externalPrefixesReconciled -or $intent.settings.reviewReference -notmatch '^https://[^\s]+$'){throw 'Enablement, pool-wide serialization, routing-domain reconciliation and an HTTPS platform review are required before reservation.'}
        if(!$env:TF_BUILD -or $env:BUILD_SOURCEBRANCH -ne 'refs/heads/main'){throw 'Reserve must run from protected ADO main; local execution is Plan/Reconcile only.'}
        if(!$ExpectedIntentPath -or !(Test-Path -LiteralPath $ExpectedIntentPath)){throw 'Provide the same-run reviewed Plan intent.'}
        $expected=Get-Content -LiteralPath $ExpectedIntentPath -Raw|ConvertFrom-Json
        if($expected.intentHash -ne $intent.intentHash -or $expected.allocationId -ne $intent.allocationId){throw 'Reviewed intent drifted; rerun Plan and approval.'}
        $status='Applying'
        & az deployment group create --resource-group $intent.poolResourceGroup --name $intent.allocationName --template-file (Join-Path $root 'modules/network/ipam-reservation.bicep') --parameters "networkManagerName=$($intent.networkManagerName)" "poolName=$($intent.poolName)" "allocationName=$($intent.allocationName)" "ownerDescription=$($intent.description)" addressCount=256 --only-show-errors --output none
        if($LASTEXITCODE -ne 0){throw 'Reservation outcome uncertain. Reconcile this exact allocation ID; do not retry with a new name.'}
        $allocation=Invoke-NetworkRead $intent.allocationId
        $null=Assert-NetworkAllocation $intent $allocation;$status='Reserved'
    }
    if($Mode -eq 'Reconcile' -and !$allocation){$status='Absent'}
    if($allocation){@{'$schema'='https://schema.management.azure.com/schemas/2019-04-01/deploymentParameters.json#';contentVersion='1.0.0.0';parameters=(Get-ReservedNetworkParameters $intent $allocation)}|ConvertTo-Json -Depth 12|Set-Content -LiteralPath (Join-Path $OutputDirectory 'network.parameters.json')}
} catch {$failure=$_.Exception.Message;$status='Quarantined'}
$receipt=[ordered]@{schemaVersion=1;kind='avnm-network-allocation';status=$status;intentHash=$intent.intentHash;allocationId=$intent.allocationId;observedUtc=[DateTimeOffset]::UtcNow.ToString('O');allocation=$allocation;deploymentAuthorized=$false;failure=$failure}
$receipt|ConvertTo-Json -Depth 18|Set-Content -LiteralPath (Join-Path $OutputDirectory 'receipt.json')
@"
# AVNM address reservation

Status: **$status**

Allocation: $($intent.allocationId)

Intent hash: $($intent.intentHash)

Plan performs reads only. Reserve consumes pool space but does not create a VNet. Prefixes remain held for the network lifetime; no automatic release or TTL is implemented. A retained allocation is not proof of DNS/routing or workload placement approval. Review exact returned prefixes before applying the separately owned network stack.

$failure
"@|Set-Content -LiteralPath (Join-Path $OutputDirectory 'README.md')
if($failure){throw $failure}
