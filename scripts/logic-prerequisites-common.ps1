# Resolve prerequisite lifecycle from successful scoped inventory, never from a failed lookup.
function Read-LogicPrerequisitePolicy($Target) {
    $path=Join-Path (Get-ProjectRoot) config/logic-prerequisites.json
    if(!(Test-Path -LiteralPath $path)){return $null}
    $policy=Get-Content -LiteralPath $path -Raw|ConvertFrom-Json -AsHashtable
    if($policy.schemaVersion -ne 1 -or $policy.mode -cne 'discover-or-create' -or !$policy.environments.Contains($Target.environmentName)){throw 'Invalid Logic App prerequisite policy.'}
    return $policy
}
function Get-LogicZoneNames {
    return [ordered]@{blob='privatelink.blob.core.windows.net';queue='privatelink.queue.core.windows.net';table='privatelink.table.core.windows.net';file='privatelink.file.core.windows.net';sites='privatelink.azurewebsites.net';topic='privatelink.eventgrid.azure.net'}
}
function Get-LogicPrerequisiteOwnership($Target) {
    $contract=New-StackContract $Target @{'$schema'='https://schema.management.azure.com/schemas/2018-05-01/subscriptionDeploymentTemplate.json#';resources=@()}
    $stack=Get-WorkloadStack @{target=$Target;stack=$contract}
    return @{status='Succeeded';stackExists=($null -ne $stack);managedResourceIds=@(if($stack){$stack.properties.resources|ForEach-Object {$_.id}})}
}
function Get-LogicPrerequisitePlan($Target,$Inventory,$Policy=(Read-LogicPrerequisitePolicy $Target)) {
    if(!$Policy){throw 'Configure config/logic-prerequisites.json before resolving prerequisites.'}
    $rg="/subscriptions/$($Target.subscriptionId)/resourceGroups/$($Target.resourceGroup)"
    $stem="$($Target.workload)-$($Target.environmentName)"
    $region=if($Target.parameterOverrides.Contains('location')){$Target.parameterOverrides.location}else{'eastus2'}
    $prefixes=$Policy.environments[$Target.environmentName]
    $plan=@{schemaVersion=1;mode='discover-or-create';status='Ready';subscriptionId=$Target.subscriptionId;resourceGroup=$Target.resourceGroup;policyHash=(Get-ValueHash $Policy);selectionHash=(Get-ValueHash $Target);resources=@();blockers=@();bicep=@{};parameters=@{};candidates=@{networks=@($Inventory.networks);privateDnsZones=@($Inventory.privateDnsZones);workspaces=@($Inventory.resources|Where-Object {$_.type -ieq 'Microsoft.OperationalInsights/workspaces'})}}
    if($Inventory.discoveryStatus -ne 'Complete' -or !$Inventory.Contains('prerequisiteOwnership') -or $Inventory.prerequisiteOwnership.status -ne 'Succeeded' -or !$Inventory.Contains('resourceQuery') -or $Inventory.resourceQuery.status -ne 'Succeeded' -or $Inventory.networkQuery.status -ne 'Succeeded' -or $Inventory.privateDnsQuery.status -ne 'Succeeded'){
        $plan.status='Blocked';$plan.blockers=@('Resource availability or stack ownership is unknown. Rerun complete discovery; no Create decision is authorized.');return $plan
    }
    if($Inventory.subscription.id -ine $Target.subscriptionId){throw 'Prerequisite inventory subscription mismatch.'}
    $managed=@($Inventory.prerequisiteOwnership.managedResourceIds)
    $overrides=$Target.parameterOverrides
    $vnetId="$rg/providers/Microsoft.Network/virtualNetworks/vnet-$stem"
    $integration="$vnetId/subnets/snet-integration";$endpoints="$vnetId/subnets/snet-private-endpoints"
    $explicitNetwork=$overrides.Contains('integrationSubnetId') -or $overrides.Contains('privateEndpointSubnetId')
    if($explicitNetwork){
        if(!$overrides.Contains('integrationSubnetId') -or !$overrides.Contains('privateEndpointSubnetId')){$plan.blockers+='Select both existing subnets together.'}
        else{$integration=$overrides.integrationSubnetId;$endpoints=$overrides.privateEndpointSubnetId;$vnetId=$integration -replace '/subnets/[^/]+$',''}
    }
    if($vnetId -notmatch ('^/subscriptions/'+[regex]::Escape($Target.subscriptionId)+'/resourceGroups/[^/]+/providers/Microsoft.Network/virtualNetworks/[^/]+$') -or ($endpoints -replace '/subnets/[^/]+$','') -ine $vnetId -or $integration -ieq $endpoints){$plan.blockers+='Select distinct subnets in one VNet in this subscription.'}
    $networks=@($Inventory.networks|Where-Object {$_.id -ieq $vnetId})
    $networkAction='Create'
    if($networks.Count -gt 1){$networkAction='Blocked';$plan.blockers+='Ambiguous selected VNet.'}
    elseif($networks.Count -eq 1){
        $networkAction=if($vnetId -in $managed){'Manage'}else{'Reuse'}
        $net=$networks[0]
        $si=@($net.subnets|Where-Object {$_.id -ieq $integration});$se=@($net.subnets|Where-Object {$_.id -ieq $endpoints})
        if($net.location -ine $region -or $net.subnetQuery.status -ne 'Succeeded' -or $si.Count -ne 1 -or $se.Count -ne 1 -or !$si[0].integrationCandidate -or !$se[0].privateEndpointCandidate){$networkAction='Blocked';$plan.blockers+='Existing VNet needs compatible, inventoried integration and private-endpoint subnets in the selected region. Shared subnets are not modified automatically.'}
    }elseif($explicitNetwork){$networkAction='Blocked';$plan.blockers+='Explicitly selected existing subnets were not found. Select valid IDs or remove the overrides to plan a new dedicated VNet.'}
    # Validate proposed CIDRs with the existing deterministic address validator.
    if($networkAction -eq 'Create'){
        try{
            $ranges=@{};foreach($key in @('vnetAddressPrefix','integrationSubnetPrefix','privateEndpointSubnetPrefix')){$ranges[$key]=@{value=$prefixes[$key]}}
            Assert-ServiceNewNetwork $ranges
            foreach($net in $Inventory.networks){
                if(!$net.Contains('addressPrefixes')){throw 'Discovery lacks VNet address-space evidence; rerun discovery.'}
                foreach($prefix in $net.addressPrefixes){if(Test-LogicCidrOverlap $prefix $prefixes.vnetAddressPrefix){throw 'Proposed VNet address space overlaps discovered networking; select reviewed non-overlapping CIDRs.'}}
            }
        }catch{$networkAction='Blocked';$plan.blockers+=$_.Exception.Message}
    }
    foreach($pair in @(@('virtualNetwork',$vnetId),@('integrationSubnet',$integration),@('privateEndpointSubnet',$endpoints))){$plan.resources+=@{kind=$pair[0];id=$pair[1];action=$networkAction;reason=$(if($networkAction -eq 'Create'){'No matching resource in successful scoped inventory.'}elseif($networkAction -eq 'Manage'){'Already owned by this stack; retain its Bicep declaration.'}else{'Existing approved resource; reference without adoption.'})}}
    $createNetwork=$networkAction -in @('Create','Manage')
    if($createNetwork -and $vnetId -ine "$rg/providers/Microsoft.Network/virtualNetworks/vnet-$stem"){$plan.blockers+='Managed network name does not match this workload.'}
    if($createNetwork){$plan.resources+=@{kind='integrationNsg';id="$rg/providers/Microsoft.Network/networkSecurityGroups/nsg-vnet-$stem-integration";action=$networkAction;reason='Owned integration subnet network security group.'}}
    $zones=Get-LogicZoneNames;$zoneIds=@{};$createdZones=@()
    foreach($key in $zones.Keys){
        $expected="$rg/providers/Microsoft.Network/privateDnsZones/$($zones[$key])"
        $explicit=$overrides.Contains('privateDnsZoneIds') -and $overrides.privateDnsZoneIds.Contains($key)
        $id=if($explicit){$overrides.privateDnsZoneIds[$key]}else{$expected}
        $found=@($Inventory.privateDnsZones|Where-Object {$_.id -ieq $id})
        $action=if($found.Count -eq 1){if($id -in $managed){'Manage'}else{'Reuse'}}elseif($found.Count -eq 0 -and !$explicit){'Create'}else{'Blocked'}
        if($id -notmatch ('^/subscriptions/'+[regex]::Escape($Target.subscriptionId)+'/resourceGroups/[^/]+/providers/Microsoft.Network/privateDnsZones/'+[regex]::Escape($zones[$key])+'$')){$action='Blocked'}
        if($action -eq 'Manage' -and $id -ine $expected){$action='Blocked'}
        if($action -eq 'Reuse' -and $networkAction -eq 'Create'){$action='Blocked';$plan.blockers+="Existing DNS zone $key requires a reviewed link to the new VNet. Select existing linked networking or allow a new workload-owned zone."}
        if($action -eq 'Blocked'){$plan.blockers+="DNS $key is missing, ambiguous or incompatible with the selected scope; never create at an unverified shared ID."}
        $zoneIds[$key]=$id
        $plan.resources+=@{kind="dns.$key";id=$id;action=$action;reason=$(if($action -eq 'Create'){'Absent in successful DNS inventory; create workload-owned zone and link.'}elseif($action -eq 'Manage'){'Retain stack-owned zone and link.'}else{'Reference selected existing zone; verify VNet link before apply.'})}
        if($action -in @('Create','Manage')){$createdZones+=$zones[$key];$plan.resources+=@{kind="dnsLink.$key";id="$id/virtualNetworkLinks/link-$stem";action=$action;reason='Link owned zone to selected VNet; registration disabled.'}}
    }
    $expectedWorkspace="$rg/providers/Microsoft.OperationalInsights/workspaces/log-$stem"
    $explicit=$overrides.Contains('existingLogAnalyticsWorkspaceId')
    $workspace=if($explicit){$overrides.existingLogAnalyticsWorkspaceId}else{$expectedWorkspace}
    $workspaceMatches=@($Inventory.resources|Where-Object {$_.id -ieq $workspace -and $_.type -ieq 'Microsoft.OperationalInsights/workspaces'})
    $action=if($workspaceMatches.Count -eq 1){if($workspace -in $managed){'Manage'}else{'Reuse'}}elseif($workspaceMatches.Count -eq 0 -and !$explicit){'Create'}else{'Blocked'}
    if($workspace -notmatch ('^/subscriptions/'+[regex]::Escape($Target.subscriptionId)+'/resourceGroups/[^/]+/providers/Microsoft.OperationalInsights/workspaces/[^/]+$') -or ($workspaceMatches.Count -eq 1 -and $workspaceMatches[0].location -ine $region) -or ($action -eq 'Manage' -and $workspace -ine $expectedWorkspace)){$action='Blocked'}
    if($action -eq 'Blocked'){$plan.blockers+='Selected monitoring workspace was not found uniquely in the approved region/scope.'}
    $plan.resources+=@{kind='workspace';id=$workspace;action=$action;reason=$(if($action -eq 'Create'){'No matching workspace; create a workload-owned PerGB2018 workspace with 30-day retention.'}elseif($action -eq 'Manage'){'Retain stack-owned workspace.'}else{'Reference selected existing workspace.'})}
    $plan.bicep=@{createNetwork=$createNetwork;createWorkspace=($action -in @('Create','Manage'));createDnsZoneNames=@($createdZones);vnetName="vnet-$stem";workspaceName="log-$stem";vnetId=$vnetId;linkName="link-$stem";vnetAddressPrefix=$prefixes.vnetAddressPrefix;integrationSubnetPrefix=$prefixes.integrationSubnetPrefix;privateEndpointSubnetPrefix=$prefixes.privateEndpointSubnetPrefix}
    $plan.parameters=@{integrationSubnetId=$integration;privateEndpointSubnetId=$endpoints;privateDnsZoneIds=$zoneIds;existingLogAnalyticsWorkspaceId=$workspace}
    if($plan.blockers.Count){$plan.status='Blocked'}
    return $plan
}
function Test-LogicCidrOverlap([string]$Left,[string]$Right) {
    $ranges=@(foreach($cidr in @($Left,$Right)){
        $parts=$cidr.Split('/');$ip=[Net.IPAddress]::Parse($parts[0]);$bits=[int]$parts[1]
        if($ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $bits -lt 0 -or $bits -gt 32){throw 'Unsupported discovered address prefix.'}
        $bytes=$ip.GetAddressBytes();[uint64]$start=([uint64]$bytes[0]*16777216)+([uint64]$bytes[1]*65536)+([uint64]$bytes[2]*256)+$bytes[3]
        @{start=$start;end=$start+[uint64][math]::Pow(2,32-$bits)-1}
    })
    return $ranges[0].start -le $ranges[1].end -and $ranges[1].start -le $ranges[0].end
}
function Set-LogicDiscoveredPrerequisites($Target,$P,[string]$Directory,[string]$OutputDirectory='') {
    $inventory=Get-Content (Join-Path $Directory inventory.json) -Raw|ConvertFrom-Json -AsHashtable
    if(!$inventory.Contains('prerequisitePlan')){return} # Backward-compatible existing-only artifacts.
    $plan=Get-LogicPrerequisitePlan $Target $inventory
    if((Get-ValueHash $plan) -cne (Get-ValueHash $inventory.prerequisitePlan)){throw 'Prerequisite policy/selection changed since discovery; rerun Discover.'}
    if($OutputDirectory){Write-ServiceJson $plan (Join-Path $OutputDirectory prerequisite-plan.json)}
    if($plan.status -cne 'Ready'){throw ('Prerequisite resolution blocked: '+($plan.blockers -join '; '))}
    foreach($key in $plan.parameters.Keys){
        if($P.Contains($key) -and $P[$key].Contains('value') -and $null -ne $P[$key].value -and (ConvertTo-Canonical $P[$key].value) -notmatch 'REPLACE|00000000-0000-0000-0000-000000000000' -and (Get-ValueHash $P[$key].value) -cne (Get-ValueHash $plan.parameters[$key])){throw "Explicit parameter $key differs from discovery selection. Put approved shared IDs in target parameterOverrides and rerun Discover; no selection was overwritten."}
    }
    foreach($key in $plan.parameters.Keys){$P[$key]=@{value=$plan.parameters[$key]}}
    $P.prerequisitePlan=@{value=$plan.bicep}
}
function Assert-LogicResolvedPrerequisites($Target,$P,$Inventory) {
    $plan=Get-LogicPrerequisitePlan $Target $Inventory
    if($plan.status -cne 'Ready' -or (Get-ValueHash $plan) -cne (Get-ValueHash $Inventory.prerequisitePlan)){throw 'Saved prerequisite plan is blocked or no longer matches policy.'}
    if((Get-ValueHash $P.prerequisitePlan.value) -cne (Get-ValueHash $plan.bicep)){throw 'Effective prerequisite creation flags differ from discovery.'}
    foreach($key in $plan.parameters.Keys){if((Get-ValueHash $P[$key].value) -cne (Get-ValueHash $plan.parameters[$key])){throw "Effective prerequisite ID differs from discovery: $key"}}
    return $plan
}
function Assert-LogicPrerequisiteLiveState($Bundle,$State) {
    $p=$Bundle.parameters.parameters
    if(!(Get-ServiceParameter $p prerequisitePlan @{}).Count){return}
    $inventory=Get-Content (Join-Path $Bundle.directory discovery/inventory.json) -Raw|ConvertFrom-Json -AsHashtable
    $plan=Assert-LogicResolvedPrerequisites $Bundle.target $p $inventory
    $live=@(Invoke-ServiceJson @('resource','list','--subscription',$Bundle.target.subscriptionId,'--query','[].{id:id,type:type}'))
    foreach($r in @($plan.resources|Where-Object {$_.kind -eq 'virtualNetwork' -or $_.kind -eq 'workspace' -or $_.kind -like 'dns.*'})){
        $found=@($live|Where-Object {$_.id -ieq $r.id})
        if($r.action -eq 'Create'){
            if($found.Count -and $r.id -notin $State.managedResources){throw "Resource appeared after discovery without stack ownership: $($r.id). Rerun discovery; do not adopt it."}
        }elseif($found.Count -ne 1){throw "Selected prerequisite disappeared or is ambiguous: $($r.id)"}
    }
}
function Get-LogicPrerequisiteSummary($Plan) {
    @('## Prerequisite decisions','','Discovery is read-only. Create/Manage declarations will be included in the workload stack; Reuse resources remain externally owned. Other subscription resources are candidates only; use reviewed target overrides to select them.','',"Resolution status: $($Plan.status)",'','| Action | Kind | Resource ID |','|---|---|---|')
    foreach($r in $Plan.resources){'| '+$r.action+' | '+$r.kind+' | '+[Net.WebUtility]::HtmlEncode($r.id).Replace('|','&#124;')+' |'}
    foreach($blocker in $Plan.blockers){'Blocked: '+[Net.WebUtility]::HtmlEncode($blocker)}
    @('','### Existing resources available for reviewed selection','','These are candidates, not automatic substitutions. Select resource IDs in the target parameterOverrides, then rerun Discover.','', '| Kind | Candidate ID |','|---|---|')
    foreach($kind in @('networks','privateDnsZones','workspaces')){
        foreach($candidate in $Plan.candidates[$kind]){'| '+$kind+' | '+[Net.WebUtility]::HtmlEncode($candidate.id).Replace('|','&#124;')+' |'}
    }
    ''
}
