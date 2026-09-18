#requires -Version 7.4
[CmdletBinding(DefaultParameterSetName='Subscription')]
param(
    [Parameter(Mandatory,ParameterSetName='Subscription')][string]$SubscriptionId,
    [Parameter(Mandatory,ParameterSetName='Name')][string]$SubscriptionName,
    [Parameter(Mandatory,ParameterSetName='Profile')][string]$Workload,
    [Parameter(Mandatory,ParameterSetName='Profile')][string]$EnvironmentName,
    [Parameter(Mandatory,ParameterSetName='Profile')][string]$SubscriptionAlias,
    [Parameter(Mandatory,ParameterSetName='Profile')][string]$NetworkProfile,
    [Parameter(ParameterSetName='Profile')][switch]$UseServiceConnectionSubscription,
    [Parameter(ParameterSetName='Profile')][string]$BoundServiceConnection='',
    [string]$OrganizationUrl='', [string]$Project='',
    [Parameter(Mandatory)][string]$OutputDirectory
)
. "$PSScriptRoot/self-service-common.ps1"
# Read-only Azure calls. Do not set the default subscription, install extensions,
# create endpoints, alter delegates, or grant permissions from this script.
if ($PSCmdlet.ParameterSetName -eq 'Profile') {
    $target=Read-ServiceTarget $Workload $EnvironmentName $SubscriptionAlias $NetworkProfile -AllowDisabled -AllowDiscoveryPlaceholder:$UseServiceConnectionSubscription
    $SubscriptionId=$target.subscriptionId
    if ($UseServiceConnectionSubscription) {
        if (!$BoundServiceConnection -or $target.serviceConnection -cne $BoundServiceConnection) { throw 'Discovery target does not match the YAML-bound service connection.' }
        # AzureCLI@2 selects the subscription configured on its service connection.
        # Read that context; do not enumerate or arbitrarily choose a subscription.
        $connected=Invoke-ServiceJson @('account','show')
        if (!$connected -or $connected.state -ne 'Enabled' -or [guid]::Parse($connected.id) -eq [guid]::Empty) { throw 'Service connection has no enabled subscription context.' }
        if ($SubscriptionId -ne [guid]::Empty.ToString() -and $SubscriptionId -ine $connected.id) { throw 'Service connection subscription differs from the registered target.' }
        $SubscriptionId=$connected.id
    }
}
if ($PSCmdlet.ParameterSetName -eq 'Name') {
    $subscriptions=@(Invoke-ServiceJson @('account','list','--all'))
    $selected=@($subscriptions | Where-Object { $_.name -ceq $SubscriptionName -and $_.state -eq 'Enabled' })
    if ($selected.Count -ne 1) { throw 'Subscription name must match exactly one accessible enabled subscription; use its ID when names are duplicated.' }
    $SubscriptionId=$selected[0].id
}
if ([guid]::Parse($SubscriptionId) -eq [guid]::Empty) { throw 'Select a configured subscription before discovery.' }
$account=Invoke-ServiceJson @('account','show','--subscription',$SubscriptionId)
if ($account.id -ine $SubscriptionId -or $account.state -ne 'Enabled') { throw 'Selected subscription is unavailable.' }
New-Item -ItemType Directory -Path $OutputDirectory -Force | Out-Null
$report=@{schemaVersion=1;readOnly=$true;generatedUtc=[DateTimeOffset]::UtcNow.ToString('O');subscription=@{id=$account.id;name=$account.name;tenantId=$account.tenantId};networks=@();privateDnsZones=@();serviceConnections=@();permissionEvidence=@();warnings=@()}
if ($UseServiceConnectionSubscription) { $report.subscriptionSource='service-connection-context'; $report.boundServiceConnection=$BoundServiceConnection }
$vnets=@(Invoke-ServiceJson @('network','vnet','list','--subscription',$SubscriptionId))
foreach ($vnet in $vnets) {
    $subnets=@(Invoke-ServiceJson @('network','vnet','subnet','list','--subscription',$SubscriptionId,'--resource-group',$vnet.resourceGroup,'--vnet-name',$vnet.name))
    $items=@($subnets | ForEach-Object {
        $delegations=@(); if ($_.Contains('delegations')) { $delegations=@($_.delegations | ForEach-Object { $_.serviceName }) }
        $prefixes=if ($_.Contains('addressPrefixes')) { @($_.addressPrefixes) } else { @($_.addressPrefix) }
        $largeEnough=@($prefixes | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+/(\d+)$' -and [int]$Matches[1] -le 26 }).Count -gt 0
        $hasPrivateEndpoints=$_.Contains('privateEndpoints') -and @($_.privateEndpoints).Count -gt 0
        @{id=$_.id;name=$_.name;addressPrefixes=@($prefixes);delegations=$delegations;privateEndpointNetworkPolicies=$_.privateEndpointNetworkPolicies;
          integrationCandidate=($delegations.Count -eq 1 -and $delegations[0] -eq 'Microsoft.Web/serverFarms' -and $largeEnough -and !$hasPrivateEndpoints);
          privateEndpointCandidate=($delegations.Count -eq 0 -and $_.privateEndpointNetworkPolicies -eq 'Disabled')}
    })
    $report.networks+=@{id=$vnet.id;name=$vnet.name;location=$vnet.location;resourceGroup=$vnet.resourceGroup;subnets=$items}
}
$zones=@(Invoke-ServiceJson @('network','private-dns','zone','list','--subscription',$SubscriptionId))
$report.privateDnsZones=@($zones | ForEach-Object { @{id=$_.id;name=$_.name;resourceGroup=$_.resourceGroup} })
# Subscription-level ARM permissions are evidence only: conditional role grants,
# deny assignments, downstream RG/subnet grants and data-plane access need live checks.
try {
    $permissions=Invoke-ServiceJson @('rest','--method','get','--url',"https://management.azure.com/subscriptions/$SubscriptionId/providers/Microsoft.Authorization/permissions?api-version=2022-04-01")
    $report.permissionEvidence=@($permissions.value)
} catch { $report.warnings+='Current identity permissions could not be read; discovery does not prove deployment authority.' }
if ($OrganizationUrl -or $Project) {
    if ($OrganizationUrl -cnotmatch '^https://dev\.azure\.com/[a-zA-Z0-9][a-zA-Z0-9-]*/?$' -or !$Project) { throw 'Provide an Azure DevOps organization URL and project together.' }
    try {
        # Query projection is deliberate: never serialize endpoint authorization parameters.
        $connections=@(Invoke-ServiceJson @('devops','service-endpoint','list','--organization',$OrganizationUrl,'--project',$Project,
            '--query',"[?type=='azurerm'].{id:id,name:name,ready:isReady,subscriptionId:data.subscriptionId,tenantId:authorization.parameters.tenantid,applicationId:authorization.parameters.serviceprincipalid,scheme:authorization.scheme}"))
        foreach ($connection in @($connections | Where-Object { $_.subscriptionId -ieq $SubscriptionId -and $_.tenantId -ieq $account.tenantId })) {
            $objectId=$null
            if ($connection.applicationId) {
                try { $objectId=Invoke-ServiceJson @('ad','sp','show','--id',$connection.applicationId,'--query','id') }
                catch { $report.warnings+="Cannot resolve the principal object ID for connection $($connection.id); application ID is not an object ID." }
            }
            $report.serviceConnections+=@{id=$connection.id;name=$connection.name;ready=$connection.ready;subscriptionId=$connection.subscriptionId;scheme=$connection.scheme;applicationId=$connection.applicationId;principalObjectId=$objectId}
        }
    } catch { $report.warnings+='Azure DevOps endpoint discovery unavailable. Install/authenticate the azure-devops CLI extension separately and verify project endpoint-read access. No connection was selected automatically.' }
} else { $report.warnings+='Azure DevOps organization/project not supplied: service connections were not discovered.' }
$report.warnings+='Candidate flags do not prove free IP capacity, route/NSG safety, DNS resolution, pipeline authorization, or effective deployment/data-plane permissions. Cross-subscription DNS zones must be supplied explicitly.'
Write-ServiceJson $report (Join-Path $OutputDirectory inventory.json)
$lines=@('# Read-only subscription discovery','',"Subscription: $($account.name) ($SubscriptionId)",'',"Networks: $($report.networks.Count); matching service connections: $($report.serviceConnections.Count)",'','Review inventory.json. No resources or permissions were changed.','')
$lines+=@($report.warnings | ForEach-Object { "- $_" })
$lines | Set-Content (Join-Path $OutputDirectory summary.md)
if ($env:TF_BUILD -eq 'True') { Write-Host "##vso[task.uploadsummary]$(Join-Path $OutputDirectory summary.md)" }
Write-Host "Read-only inventory saved: $OutputDirectory"
