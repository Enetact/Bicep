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
$report.discoveryStatus='Complete'
$report.networkQuery=@{status='Succeeded'}
try { $vnets=@(Invoke-ServiceJson @('network','vnet','list','--subscription',$SubscriptionId)) }
catch {
    $vnets=@(); $report.discoveryStatus='Partial'; $report.networkQuery=@{status='Failed';error=$_.Exception.Message}
    $report.warnings+='VNet listing failed; network availability is unknown. No networks were assumed absent.'
}
foreach ($vnet in $vnets) {
    try { $subnets=@(Invoke-ServiceJson @('network','vnet','subnet','list','--subscription',$SubscriptionId,'--resource-group',$vnet.resourceGroup,'--vnet-name',$vnet.name)) }
    catch {
        $report.discoveryStatus='Partial'
        $report.networks+=@{id=$vnet.id;name=$vnet.name;location=$vnet.location;resourceGroup=$vnet.resourceGroup;subnets=@();subnetQuery=@{status='Failed';error=$_.Exception.Message}}
        $report.warnings+="Subnet listing failed for $($vnet.id); its subnets are unknown."
        continue
    }
    $items=@($subnets | ForEach-Object {
        $delegations=@(); if ($_.Contains('delegations')) { $delegations=@($_.delegations | ForEach-Object { $_.serviceName }) }
        $prefixes=if ($_.Contains('addressPrefixes')) { @($_.addressPrefixes) } else { @($_.addressPrefix) }
        $largeEnough=@($prefixes | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+/(\d+)$' -and [int]$Matches[1] -le 26 }).Count -gt 0
        $hasPrivateEndpoints=$_.Contains('privateEndpoints') -and @($_.privateEndpoints).Count -gt 0
        @{id=$_.id;name=$_.name;addressPrefixes=@($prefixes);delegations=$delegations;privateEndpointNetworkPolicies=$_.privateEndpointNetworkPolicies;
          integrationCandidate=($delegations.Count -eq 1 -and $delegations[0] -eq 'Microsoft.Web/serverFarms' -and $largeEnough -and !$hasPrivateEndpoints);
          privateEndpointCandidate=($delegations.Count -eq 0 -and $_.privateEndpointNetworkPolicies -eq 'Disabled')}
    })
    $report.networks+=@{id=$vnet.id;name=$vnet.name;location=$vnet.location;resourceGroup=$vnet.resourceGroup;addressPrefixes=$(if($vnet.Contains('addressSpace')){@($vnet.addressSpace.addressPrefixes)}else{@()});subnets=$items;subnetQuery=@{status='Succeeded';count=$items.Count}}
}
if ($report.networkQuery.status -eq 'Succeeded') { $report.networkQuery.count=$vnets.Count }
$report.privateDnsQuery=@{status='Succeeded';primaryStatus='Succeeded';source='private-dns-api';command="az network private-dns zone list --subscription $SubscriptionId"}
$report.diagnostics=@{}
try {
    $zones=@(Invoke-ServiceJson @('network','private-dns','zone','list','--subscription',$SubscriptionId))
    $report.privateDnsZones=@($zones | ForEach-Object { @{id=$_.id;name=$_.name;resourceGroup=$_.resourceGroup} })
    $report.privateDnsQuery.count=$zones.Count
} catch {
    # An unavailable DNS API is not evidence that the subscription has no zones.
    # ARM's general resource inventory can still establish which zones exist.
    $report.privateDnsQuery.primaryStatus='Failed'
    $report.privateDnsQuery.primaryError=$_.Exception.Message
    $report.privateDnsQuery.fallback=@{status='Succeeded';command="az resource list --subscription $SubscriptionId --resource-type Microsoft.Network/privateDnsZones"}
    try {
        $zones=@(Invoke-ServiceJson @('resource','list','--subscription',$SubscriptionId,'--resource-type','Microsoft.Network/privateDnsZones'))
        $report.privateDnsZones=@($zones | ForEach-Object { @{id=$_.id;name=$_.name;resourceGroup=$_.resourceGroup} })
        $report.privateDnsQuery.source='arm-resource-inventory'
        $report.privateDnsQuery.count=$zones.Count
        $report.privateDnsQuery.fallback.count=$zones.Count
        $report.warnings+='Private DNS API listing failed; DNS inventory was recovered using the subscription-scoped ARM resource list. This establishes inventory, not DNS API health or deployment readiness.'
    } catch {
        $report.discoveryStatus='Partial'
        $report.privateDnsQuery.status='Failed'
        $report.privateDnsQuery.source='unavailable'
        $report.privateDnsQuery.fallback.status='Failed'
        $report.privateDnsQuery.fallback.error=$_.Exception.Message
        $report.warnings+='Both private DNS API and ARM resource inventory lookups failed. The empty privateDnsZones array means unknown, not zero zones. See diagnostics; do not register a target from this partial inventory.'
    }
    try {
        $details=Invoke-ServiceJson @('rest','--method','get','--url',"https://management.azure.com/subscriptions/${SubscriptionId}?api-version=2022-12-01")
        $report.diagnostics.subscriptionArm=@{status='Succeeded';subscriptionId=$details.subscriptionId;displayName=$details.displayName;state=$details.state}
    } catch { $report.diagnostics.subscriptionArm=@{status='Failed';error=$_.Exception.Message} }
    try {
        $provider=Invoke-ServiceJson @('provider','show','--namespace','Microsoft.Network','--subscription',$SubscriptionId,
            '--query',"{namespace:namespace,registrationState:registrationState,privateDnsResourceTypes:resourceTypes[?resourceType=='privateDnsZones'].{resourceType:resourceType,apiVersions:apiVersions,locations:locations}}")
        $report.diagnostics.networkProvider=@{status='Succeeded';details=$provider}
    } catch { $report.diagnostics.networkProvider=@{status='Failed';error=$_.Exception.Message} }
}
# Subscription-level ARM permissions are evidence only: conditional role grants,
# deny assignments, downstream RG/subnet grants and data-plane access need live checks.
try {
    $permissions=Invoke-ServiceJson @('rest','--method','get','--url',"https://management.azure.com/subscriptions/$SubscriptionId/providers/Microsoft.Authorization/permissions?api-version=2022-04-01")
    $report.permissionEvidence=@($permissions.value)
} catch { $report.warnings+='Current identity permissions could not be read; discovery does not prove deployment authority.' }
if ($OrganizationUrl -or $Project) {
    try {
        # Optional discovery must not prevent saving required Azure inventory.
        if ([string]::IsNullOrWhiteSpace($OrganizationUrl) -or [string]::IsNullOrWhiteSpace($Project)) { throw 'Provide an Azure DevOps organization URL and project together.' }
        $OrganizationUrl=Resolve-ServiceOrganizationUrl $OrganizationUrl
        # Query projection is deliberate: never serialize endpoint authorization parameters.
        $connections=@(Invoke-ServiceJson @('devops','service-endpoint','list','--organization',$OrganizationUrl,'--project',$Project,
            '--query',"[?type=='azurerm'].{id:id,name:name,ready:isReady,subscriptionId:data.subscriptionId,tenantId:authorization.parameters.tenantid,applicationId:authorization.parameters.serviceprincipalid,scheme:authorization.scheme}"))
        $report.serviceConnectionQuery=@{status='Succeeded'}
        foreach ($connection in @($connections | Where-Object { $_.subscriptionId -ieq $SubscriptionId -and $_.tenantId -ieq $account.tenantId })) {
            $objectId=$null
            if ($connection.applicationId) {
                try { $objectId=Invoke-ServiceJson @('ad','sp','show','--id',$connection.applicationId,'--query','id') }
                catch { $report.warnings+="Cannot resolve the principal object ID for connection $($connection.id); application ID is not an object ID." }
            }
            $report.serviceConnections+=@{id=$connection.id;name=$connection.name;ready=$connection.ready;subscriptionId=$connection.subscriptionId;scheme=$connection.scheme;applicationId=$connection.applicationId;principalObjectId=$objectId}
        }
        $report.serviceConnectionQuery.count=$report.serviceConnections.Count
    } catch {
        $report.serviceConnectionQuery=@{status='Failed';error=$_.Exception.Message}
        $report.warnings+="Azure DevOps endpoint discovery unavailable: $($_.Exception.Message) Verify the organization/project, CLI extension and project endpoint-read access. No connection was selected automatically."
    }
} else { $report.serviceConnectionQuery=@{status='NotRequested'}; $report.warnings+='Azure DevOps organization/project not supplied: service connections were not discovered.' }
$report.warnings+='Candidate flags do not prove free IP capacity, route/NSG safety, DNS resolution, pipeline authorization, or effective deployment/data-plane permissions. Cross-subscription DNS zones must be supplied explicitly.'
Write-ServiceJson $report (Join-Path $OutputDirectory inventory.json)
if ($PSCmdlet.ParameterSetName -eq 'Profile' -and (Get-TargetWorkloadType $target) -ne 'blob-transfer') {
    $type=Get-TargetWorkloadType $target;$definition=Get-WorkloadDefinition $type
    $report.workloadType=$type; $report.providers=@(); $report.resources=@(); $report.resourceQuery=@{status='Succeeded'}
    try {
        foreach($provider in $(if($type -eq 'logic-app-event-grid'){@('Microsoft.Web','Microsoft.Storage','Microsoft.EventGrid','Microsoft.Insights','Microsoft.OperationalInsights')}else{$definition.providers})) {
            $item=Invoke-ServiceJson @('provider','show','--namespace',$provider,'--subscription',$SubscriptionId)
            $report.providers+=@{namespace=$item.namespace;registrationState=$item.registrationState}
        }
        $report.resources=@(Invoke-ServiceJson @('resource','list','--subscription',$SubscriptionId,'--query','[].{id:id,name:name,type:type,location:location}'))
    } catch { $report.discoveryStatus='Partial'; $report.resourceQuery.status='Failed'; $report.warnings+=('Workload prerequisite inventory failed: '+$_.Exception.Message) }
    if($type -eq 'logic-app-event-grid' -and (Read-LogicPrerequisitePolicy $target)){
        try{$report.prerequisiteOwnership=Get-LogicPrerequisiteOwnership $target}
        catch{$report.discoveryStatus='Partial';$report.prerequisiteOwnership=@{status='Failed';stackExists=$false;managedResourceIds=@()};$report.warnings+=('Stack ownership read failed: '+$_.Exception.Message)}
        $report.prerequisitePlan=Get-LogicPrerequisitePlan $target $report
        Write-ServiceJson $report.prerequisitePlan (Join-Path $OutputDirectory prerequisite-plan.json)
    }
    Write-ServiceJson $report (Join-Path $OutputDirectory inventory.json)
}
$manifest=@{schemaVersion=1;kind='blob-transfer-discovery';generatedUtc=$report.generatedUtc;discoveryStatus=$report.discoveryStatus;inventorySha256=(Get-ServiceHash (Join-Path $OutputDirectory inventory.json));subscriptionId=$SubscriptionId;
    serviceConnection=$BoundServiceConnection;selection=@{workload=$Workload;environment=$EnvironmentName;subscription=$SubscriptionAlias;network=$NetworkProfile};
    source=@{runId=$env:BUILD_BUILDID;pipelineId=$env:SYSTEM_DEFINITIONID;projectId=$env:SYSTEM_TEAMPROJECTID;repositoryId=$env:BUILD_REPOSITORY_ID;branch=$env:BUILD_SOURCEBRANCH;commit=$env:BUILD_SOURCEVERSION}}
if ($report.Contains('workloadType')) { $manifest.schemaVersion=2; $manifest.kind='workload-discovery'; $manifest.workloadType=$report.workloadType; $manifest.selection.region=if($target.parameterOverrides.Contains('location')){$target.parameterOverrides.location}else{'eastus2'} }
Write-ServiceJson $manifest (Join-Path $OutputDirectory manifest.json)
$lines=@('# Read-only subscription discovery','',"Status: $($report.discoveryStatus); private DNS query: $($report.privateDnsQuery.status)",'',"Subscription: $($account.name) ($SubscriptionId)",'','Review inventory.json. No resources or permissions were changed.','')
$lines+=@('Scope: networks, subnets, private DNS zones and accessible matching ADO service connections. This is not an inventory of every Azure resource or a validation of Template Spec publishing, Deployment Stack ownership, private connectivity or application readiness.','')
foreach ($section in @(@{label='Virtual networks';query=$report.networkQuery;items=$report.networks},@{label='Private DNS zones';query=$report.privateDnsQuery;items=$report.privateDnsZones},@{label='Matching service connections';query=$report.serviceConnectionQuery;items=$report.serviceConnections})) {
    $description=if ($section.query.status -eq 'NotRequested') { 'Not requested' } elseif ($section.query.status -ne 'Succeeded') { 'Unknown (listing failed)' } elseif ($section.items.Count -eq 0) { 'None found' } else { "$($section.items.Count) found" }
    $lines+="$($section.label): $description."
}
foreach ($vnet in $report.networks) {
    $description=if ($vnet.subnetQuery.status -ne 'Succeeded') { 'Unknown (listing failed)' } elseif ($vnet.subnets.Count -eq 0) { 'None found' } else { "$($vnet.subnets.Count) found" }
    $lines+="Subnets in $($vnet.name): $description."
}
if ($report.privateDnsQuery.primaryStatus -eq 'Failed') {
    if ($report.privateDnsQuery.status -eq 'Succeeded') {
        $lines+='DNS inventory source: subscription-scoped ARM resource inventory (fallback succeeded).'
    } else {
        $lines+='Private DNS discovery failed through both inventory paths. Azure CLI account context alone does not prove that the DNS service can access this subscription.'
    }
    $lines+=@(
        "ARM subscription diagnostic: $($report.diagnostics.subscriptionArm.status); Microsoft.Network diagnostic: $($report.diagnostics.networkProvider.status).",
        'Inspect diagnostics in inventory.json and the original Azure CLI error. Verify the service connection subscription/tenant, subscription state, provider registration, and read access. Provider registration is a separate platform action; this script never registers providers.','')
}
if($report.Contains('workloadType')) {
    $lines+=@('',"Workload pattern: $($report.workloadType). Resource catalog: $($report.resources.Count) ARM resources (ID/name/type/location only).")
    foreach($provider in $report.providers){$lines+="Provider $($provider.namespace): $($provider.registrationState)."}
    $lines+='This does not enumerate data, secrets, workflow content or every child resource. Review the saved prerequisite plan for Reuse/Create/Manage decisions. Deployment creates only resources owned by this workload; shared-resource selection and platform approvals remain explicit.'
}
$lines+=@($report.warnings | ForEach-Object { "- $_" })
if ($env:TF_BUILD -eq 'True') {
    $deployMenu='azure-pipelines-self-service-deploy.yml'
    if($PSCmdlet.ParameterSetName -eq 'Profile'){
        $type=Get-TargetWorkloadType $target
        $pipelineSettings=Get-Content (Join-Path (Get-ProjectRoot) self-service/pipeline-settings.json) -Raw|ConvertFrom-Json -AsHashtable
        if($pipelineSettings.Contains('workloadDiscoveryPipelineNames') -and $pipelineSettings.workloadDiscoveryPipelineNames.Contains($type) -and $env:BUILD_DEFINITIONNAME -ceq $pipelineSettings.workloadDiscoveryPipelineNames[$type]){
            $deployMenu='azure-pipelines-'+(Get-WorkloadDefinition $type).menuSlug+'-deploy.yml'
        }
    }
    $lines+=@('', '## Deployment handoff', '', "Discovery pipeline ID: $($manifest.source.pipelineId)", "Discovery run ID: $($manifest.source.runId)",
        'Artifact: subscription-discovery (manifest.json + inventory.json).',
        "Next: open the matching Deploy pipeline ($deployMenu) on main. In Run pipeline > Resources > discovery, select this run. Choose the matching instance/environment and approved region. Pipeline/run IDs are supplied automatically by the resource picker.",
        'Deployment requires a successful main-branch discovery from this repository within seven days. Feature-branch runs remain diagnostic only. Resource selection does not bypass these checks.',
        'Platform configuration supplies subscription, connection, networking and alert settings. Dedicated Deploy menus run Preview first, then qualify/publish/apply only when deployment is requested and the target is enabled. Disabled dedicated targets can preview; the legacy generic route retains SetupOnly. Discovery does not enable targets.',
        'Discover and Deploy have separate native run menus. Deployment dropdowns come from the reviewed catalog; they are not dynamically generated by this artifact.')
}
if($report.Contains('prerequisitePlan')){$lines+=@(Get-LogicPrerequisiteSummary $report.prerequisitePlan)}
$lines | Set-Content (Join-Path $OutputDirectory summary.md)
if ($env:TF_BUILD -eq 'True') { Write-Host "##vso[task.uploadsummary]$(Join-Path $OutputDirectory summary.md)" }
Write-Host "Read-only inventory saved: $OutputDirectory"
if ($report.discoveryStatus -eq 'Partial') { throw 'Network discovery is incomplete. Available inventory and read-only diagnostics were saved to inventory.json; see summary.md. Failed listings are unknown, not evidence that resources are absent.' }
# AzureCLI@2 propagates the last native exit code. Optional endpoint/directory or
# permission probes may have failed and been reported above; required reads passed.
$global:LASTEXITCODE=0
