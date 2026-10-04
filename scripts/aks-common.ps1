# Bounded Azure CNI Overlay / direct Azure DNS / NAT profile. No address allocation.
function Get-AksParameterKeys {
    @('kubernetesVersion','nodeSubnetId','privateDnsZoneId','controlPlaneIdentityId','kubeletIdentityId','kubeletClientId','kubeletObjectId','registryResourceId','adminGroupObjectIds','podCidr','serviceCidr','dnsServiceIP','nodeVmSize','maxUserNodes','applicationNamespace','serviceAccountName')
}
function Get-AksRange([string]$Cidr) {
    if($Cidr -cnotmatch '^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.(\d{1,3})/(\d{1,2})$'){throw 'Expected canonical IPv4 CIDR.'}
    $parts=$Cidr.Split('/');$octets=$parts[0].Split('.');[long]$ip=0
    foreach($octet in $octets){if([int]$octet -gt 255 -or [string][int]$octet -cne $octet){throw 'Invalid IPv4 octet.'};$ip=$ip*256+[int]$octet}
    $prefix=[int]$parts[1];if($prefix -lt 8 -or $prefix -gt 32){throw 'Unsupported IPv4 prefix.'}
    [long]$size=[math]::Pow(2,32-$prefix);if($ip % $size){throw 'CIDR has host bits set.'}
    @{start=$ip;end=$ip+$size-1;size=$size;prefix=$prefix}
}
function Assert-AksNoOverlap($A,$B) {if($A.start -le $B.end -and $B.start -le $A.end){throw 'AKS address ranges overlap.'}}
function Assert-AksParameters($Target,$P) {
    $prefix='^/subscriptions/'+[regex]::Escape($Target.subscriptionId)+'/resourceGroups/[^/]+/providers/'
    $patterns=@{nodeSubnetId='Microsoft.Network/virtualNetworks/[^/]+/subnets/[^/]+$';privateDnsZoneId='Microsoft.Network/privateDnsZones/privatelink\.eastus2\.azmk8s\.io$';controlPlaneIdentityId='Microsoft.ManagedIdentity/userAssignedIdentities/[^/]+$';kubeletIdentityId='Microsoft.ManagedIdentity/userAssignedIdentities/[^/]+$';registryResourceId='Microsoft.ContainerRegistry/registries/[a-zA-Z0-9]+$'}
    foreach($key in $patterns.Keys){if($P[$key].value -notmatch ($prefix+$patterns[$key])){throw "Unapproved AKS dependency scope: $key"}}
    if($P.controlPlaneIdentityId.value -ieq $P.kubeletIdentityId.value){throw 'Use separate control-plane and kubelet identities.'}
    foreach($id in @($P.kubeletClientId.value,$P.kubeletObjectId.value)+@($P.adminGroupObjectIds.value)){if([guid]::Parse($id) -eq [guid]::Empty){throw 'Invalid AKS identity.'}}
    if($P.adminGroupObjectIds.value -isnot [array] -or !$P.adminGroupObjectIds.value.Count -or $P.adminGroupObjectIds.value.Count -gt 5){throw 'Supply 1-5 reviewed Entra administrator groups.'}
    if($P.kubernetesVersion.value -cnotmatch '^1\.(33|34|35|36|37)\.\d{1,2}$'){throw 'Pin an available, supported AKS patch version.'}
    if($P.nodeVmSize.value -cnotin @('Standard_D4ds_v5','Standard_D8ds_v5') -or $P.maxUserNodes.value -isnot [int] -and $P.maxUserNodes.value -isnot [long] -or $P.maxUserNodes.value -lt 3 -or $P.maxUserNodes.value -gt 10){throw 'Unapproved AKS compute profile.'}
    foreach($key in @('applicationNamespace','serviceAccountName')){if($P[$key].value -cnotmatch '^[a-z][a-z0-9-]{2,39}$' -or $P[$key].value.StartsWith('kube-')){throw 'Invalid application Kubernetes name.'}}
    $pod=Get-AksRange $P.podCidr.value;$service=Get-AksRange $P.serviceCidr.value;$dns=Get-AksRange ($P.dnsServiceIP.value+'/32')
    if($pod.prefix -gt 20 -or $service.prefix -gt 24 -or $service.prefix -lt 16 -or $dns.start -le $service.start+1 -or $dns.end -ge $service.end){throw 'Review overlay capacity, service range and DNS service IP.'}
    Assert-AksNoOverlap $pod $service
}
function Assert-AksDiscovery($Target,$P,$Inventory) {
    foreach($key in @('controlPlaneIdentityId','kubeletIdentityId','registryResourceId')){if(@($Inventory.resources|Where-Object {$_.id -ieq $P[$key].value}).Count -ne 1){throw "AKS shared dependency absent from discovery: $key"}}
    $networks=@($Inventory.networks|Where-Object {$_.subnetQuery.status -ceq 'Succeeded' -and $_.location -ieq $P.location.value})
    if(@($networks|ForEach-Object {$_.subnets}|Where-Object {$_.id -ieq $P.nodeSubnetId.value}).Count -ne 1){throw 'AKS node subnet missing or unreadable.'}
    if(@($Inventory.privateDnsZones|Where-Object {$_.id -ieq $P.privateDnsZoneId.value}).Count -ne 1){throw 'AKS private DNS zone missing; platform onboarding required.'}
}
function Assert-AksRole([string]$Principal,[string]$Scope,[string]$Role) {
    $roles=Invoke-ServiceJson @('role','assignment','list','--assignee-object-id',$Principal,'--scope',$Scope,'--include-inherited','--all','--fill-principal-name','false')
    if(!@($roles|Where-Object {$_.principalId -ieq $Principal -and $_.roleDefinitionId.EndsWith('/'+$Role) -and (!$_.Contains('condition') -or !$_.condition)}).Count){throw "Required unconditioned role not found at approved scope: $Scope / $Role"}
    return $roles
}
function Test-AksPrerequisites($Bundle) {
    $p=$Bundle.parameters.parameters;$state=@{};$vnetId=$p.nodeSubnetId.value -replace '/subnets/[^/]+$',''
    $vnet=Get-DataPlatformResource $vnetId '2024-05-01'
    if($vnet.location -ine $p.location.value -or ($vnet.properties.Contains('dhcpOptions') -and @($vnet.properties.dhcpOptions.dnsServers).Count)){throw 'AKS initial profile requires same-region Azure-provided DNS.'}
    $pod=Get-AksRange $p.podCidr.value;$service=Get-AksRange $p.serviceCidr.value
    foreach($cidr in $vnet.properties.addressSpace.addressPrefixes){$range=Get-AksRange $cidr;Assert-AksNoOverlap $pod $range;Assert-AksNoOverlap $service $range}
    foreach($peer in @(if($vnet.properties.Contains('virtualNetworkPeerings')){$vnet.properties.virtualNetworkPeerings})){
        if($peer.properties.peeringState -cne 'Connected' -or !$peer.properties.Contains('remoteAddressSpace')){throw 'Peering coverage incomplete.'}
        foreach($cidr in $peer.properties.remoteAddressSpace.addressPrefixes){$range=Get-AksRange $cidr;Assert-AksNoOverlap $pod $range;Assert-AksNoOverlap $service $range}
    }
    $subnet=Get-DataPlatformResource $p.nodeSubnetId.value '2024-05-01';$s=$subnet.properties
    $cidrs=@(if($s.Contains('addressPrefixes')){$s.addressPrefixes}else{$s.addressPrefix})
    if($cidrs.Count -ne 1 -or (Get-AksRange $cidrs[0]).prefix -gt 24 -or @($s.delegations).Count -or ($s.Contains('routeTable') -and $s.routeTable) -or !$s.Contains('natGateway') -or !$s.natGateway.id){throw 'Dedicated undelegated /24-or-larger node subnet with reviewed NAT and no custom route table required.'}
    $nat=Get-DataPlatformResource $s.natGateway.id '2024-05-01'
    $addresses=@();foreach($key in @('publicIpAddresses','publicIpPrefixes')){if($nat.properties.Contains($key)){$addresses+=@($nat.properties[$key])}}
    if($nat.location -ine $p.location.value -or !$addresses.Count){throw 'NAT egress not ready.'}
    $links=Get-StackRestCollection "https://management.azure.com$($p.privateDnsZoneId.value)/virtualNetworkLinks?api-version=2024-06-01"
    if(@($links|Where-Object {$_.properties.virtualNetwork.id -ieq $vnetId -and $_.properties.provisioningState -ceq 'Succeeded'}).Count -ne 1){throw 'AKS direct private DNS link missing.'}
    $cp=Invoke-ServiceJson @('resource','show','--ids',$p.controlPlaneIdentityId.value,'--api-version','2024-11-30');$ki=Invoke-ServiceJson @('resource','show','--ids',$p.kubeletIdentityId.value,'--api-version','2024-11-30')
    if($cp.id -ine $p.controlPlaneIdentityId.value -or $ki.id -ine $p.kubeletIdentityId.value){throw 'Wrong AKS identity resource.'}
    if($ki.properties.clientId -ine $p.kubeletClientId.value -or $ki.properties.principalId -ine $p.kubeletObjectId.value){throw 'Kubelet identity binding mismatch.'}
    $state.networkRole=Assert-AksRole $cp.properties.principalId $vnetId '4d97b98b-1d4f-4787-a291-c67834d212e7'
    $state.dnsRole=Assert-AksRole $cp.properties.principalId $p.privateDnsZoneId.value 'b12aa53e-6015-4669-85d0-8515ebb3ae7f'
    $state.identityRole=Assert-AksRole $cp.properties.principalId $ki.id 'f1a07417-d97a-45cb-824c-7a7467783830'
    $state.pullRole=Assert-AksRole $ki.properties.principalId $p.registryResourceId.value '7f951dda-4ed3-4680-a7ca-43fe172d538d'
    $acr=Get-DataPlatformResource $p.registryResourceId.value '2023-07-01'
    if($acr.sku.name -cne 'Premium' -or $acr.properties.adminUserEnabled -ne $false -or $acr.properties.publicNetworkAccess -cne 'Disabled' -or ($acr.properties.Contains('roleAssignmentMode') -and $acr.properties.roleAssignmentMode -cne 'LegacyRegistryPermissions') -or !@($acr.properties.privateEndpointConnections|Where-Object {$_.properties.privateLinkServiceConnectionState.status -ceq 'Approved'}).Count){throw 'Approved private Premium ACR with classic AcrPull authorization required.'}
    Wait-ProductPrivateHost $acr.properties.loginServer
    $versions=Invoke-ServiceJson @('aks','get-versions','--location',$p.location.value)
    $available=@($versions.values|Where-Object {$_.patchVersions.Contains($p.kubernetesVersion.value) -and (!$_.Contains('isPreview') -or !$_.isPreview)})
    if($available.Count -ne 1){throw 'Selected AKS patch is unavailable or preview; review supported version before deployment.'}
    $state.vnet=$vnet;$state.subnet=$subnet;$state.nat=$nat;$state.dns=$links;$state.controlPlane=$cp;$state.kubelet=$ki;$state.registry=$acr
    return $state
}
function Test-AksInfrastructure($Bundle,$Outputs) {
    $r=Get-DataPlatformResource $Outputs.clusterResourceId.value '2026-02-01';$p=$r.properties
    if($p.disableLocalAccounts -ne $true -or $p.enableRBAC -ne $true -or $p.aadProfile.enableAzureRBAC -ne $true -or $p.apiServerAccessProfile.enablePrivateCluster -ne $true -or $p.apiServerAccessProfile.enablePrivateClusterPublicFQDN -ne $false -or $p.oidcIssuerProfile.enabled -ne $true -or $p.securityProfile.workloadIdentity.enabled -ne $true -or $p.networkProfile.networkPluginMode -cne 'overlay' -or $p.networkProfile.networkDataplane -cne 'cilium'){throw 'AKS private identity/network verification failed.'}
    if($r.name -cne $Outputs.clusterName.value -or $p.privateFQDN -cne $Outputs.privateFqdn.value -or $p.apiServerAccessProfile.privateDNSZone -ine $Bundle.parameters.parameters.privateDnsZoneId.value){throw 'AKS endpoint binding mismatch.'}
    Wait-ProductPrivateHost $p.privateFQDN
    foreach($pool in $p.agentPoolProfiles){if($pool.provisioningState -cne 'Succeeded' -or $pool.osSKU -cne 'AzureLinux3' -or $pool.vnetSubnetID -ine $Bundle.parameters.parameters.nodeSubnetId.value -or $pool.enableNodePublicIP -ne $false){throw 'Node pool verification failed.'}}
    return @{checks=@('Private API and DNS','Entra RBAC/local accounts disabled','OIDC/workload identity','Cilium overlay','Azure Linux 3 pools');kubernetesDataPlaneVerified=$false;helmReleaseVerified=$false;note='Infrastructure only. Namespace/RBAC, observability acceptance, image pulls and Helm rollout require separate qualification.'}
}
