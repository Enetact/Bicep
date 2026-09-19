#requires -Version 7.4
. "$PSScriptRoot/common.ps1"
. "$PSScriptRoot/what-if-governance.ps1"
. "$PSScriptRoot/stack-service-common.ps1"
. "$PSScriptRoot/workload-common.ps1"
. "$PSScriptRoot/logicapp-service-common.ps1"

function Resolve-ServiceOrganizationUrl([string]$OrganizationUrl) {
    $value=$OrganizationUrl.Trim()
    # Azure DevOps can supply either its current or legacy organization URL.
    # Limit credential-bearing calls to these exact HTTPS host/path forms.
    if ($value -notmatch '\Ahttps://(?:dev\.azure\.com/[a-z0-9][a-z0-9-]*|[a-z0-9][a-z0-9-]*\.visualstudio\.com(?:/DefaultCollection)?)/?\z') {
        throw 'Expected an Azure DevOps organization URL: https://dev.azure.com/<organization>/ or https://<organization>.visualstudio.com/ (optionally DefaultCollection).'
    }
    return $value.TrimEnd('/')+'/'
}

function Write-ServiceJson($Value, [string]$Path) {
    $parent = Split-Path -Parent ([IO.Path]::GetFullPath($Path))
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($Path), ($Value | ConvertTo-Json -Depth 100) + "`n", [Text.UTF8Encoding]::new($false))
}
function Get-ServiceHash([string]$Path) { (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant() }
function ConvertTo-Canonical($Value) {
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [System.Collections.IDictionary]) {
        $keys = [string[]]@($Value.Keys); [Array]::Sort($keys, [StringComparer]::Ordinal)
        return '{' + ((@($keys | ForEach-Object { ($_ | ConvertTo-Json -Compress) + ':' + (ConvertTo-Canonical $Value[$_]) })) -join ',') + '}'
    }
    if ($Value -is [array]) { return '[' + ((@($Value | ForEach-Object { ConvertTo-Canonical $_ })) -join ',') + ']' }
    return ConvertTo-Json -InputObject $Value -Compress -Depth 100
}
function Get-ValueHash($Value) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes((ConvertTo-Canonical $Value)))).ToLowerInvariant()
}
function Resolve-ServicePath([string]$Root, [string]$Relative) {
    if ([IO.Path]::IsPathRooted($Relative) -or $Relative -match '(^|[/\\])\.\.([/\\]|$)') { throw 'Relative path must remain within its approved directory.' }
    $base = [IO.Path]::GetFullPath($Root).TrimEnd('\','/')
    $path = [IO.Path]::GetFullPath((Join-Path $base $Relative))
    if (!$path.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Path escapes approved directory.' }
    $cursor = $path
    while ($cursor.Length -ge $base.Length) {
        if ((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Reparse points are not accepted in release paths.' }
        $cursor = Split-Path -Parent $cursor
    }
    return $path
}
function Read-ServiceTarget([string]$Workload, [string]$EnvironmentName, [string]$SubscriptionAlias='', [string]$NetworkProfile='', [switch]$AllowDisabled, [switch]$AllowDiscoveryPlaceholder) {
    if ($Workload -cnotmatch '^[a-z0-9]{3,10}$' -or $EnvironmentName -cnotin @('dev','qa','uat','prod')) { throw 'Invalid workload/environment selection.' }
    $path = Join-Path (Get-ProjectRoot) "self-service/targets/$Workload.$EnvironmentName.json"
    if ($SubscriptionAlias -or $NetworkProfile) {
        if ($SubscriptionAlias -cnotmatch '^[a-z0-9][a-z0-9-]{0,39}$' -or $NetworkProfile -cnotmatch '^[a-z0-9][a-z0-9-]{0,39}$') { throw 'Invalid catalog selection.' }
        $matches=@(Get-ChildItem (Join-Path (Get-ProjectRoot) 'self-service/targets') -Filter *.json | ForEach-Object {
            $candidate=Get-Content $_.FullName -Raw | ConvertFrom-Json -AsHashtable
            if ($candidate.schemaVersion -in @(2,3) -and $candidate.workload -ceq $Workload -and $candidate.environmentName -ceq $EnvironmentName -and $candidate.subscriptionAlias -ceq $SubscriptionAlias -and $candidate.networkProfile -ceq $NetworkProfile) { $candidate }
        })
        if ($matches.Count -ne 1) { throw 'Selection must match exactly one registered subscription/network target.' }
        $target=$matches[0]
    } else { $target = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable }
    Assert-ServiceTarget $target $Workload $EnvironmentName -AllowDisabled:$AllowDisabled -AllowDiscoveryPlaceholder:$AllowDiscoveryPlaceholder
    return $target
}
function Assert-ServiceTarget($Target, [string]$Workload, [string]$EnvironmentName, [switch]$AllowDisabled, [switch]$AllowDiscoveryPlaceholder) {
    if ($Workload -cnotmatch '^[a-z0-9]{3,10}$' -or $EnvironmentName -cnotin @('dev','qa','uat','prod')) { throw 'Invalid workload/environment selection.' }
    $required = @('schemaVersion','enabled','workload','environmentName','subscriptionId','resourceGroup','parameterFile','serviceConnection','agentPool','deploymentEnvironment','smokePrefix')
    if ($Target.schemaVersion -in @(2,3)) { $required+=@('subscriptionAlias','networkProfile','parameterOverrides') }
    if ($Target.schemaVersion -eq 3) { $required+=@('workloadType'); $null=Get-WorkloadDefinition (Get-TargetWorkloadType $Target) }
    if (@($Target.Keys | Where-Object { $_ -notin $required }).Count -or @($required | Where-Object { !$Target.Contains($_) }).Count) { throw 'Target fields do not match its schemaVersion.' }
    if ($Target.schemaVersion -notin @(1,2,3) -or $Target.enabled -isnot [bool] -or (!$Target.enabled -and !$AllowDisabled)) { throw 'Target is disabled. Platform onboarding must be completed first.' }
    if ($Target.workload -cne $Workload -or $Target.environmentName -cne $EnvironmentName) { throw 'Target selection mismatch.' }
    $discoveryPlaceholder=$AllowDiscoveryPlaceholder -and $AllowDisabled -and !$Target.enabled -and $Target.schemaVersion -in @(2,3) -and $Target.subscriptionAlias -ceq 'unconfigured'
    if ($Target.subscriptionId -notmatch '^[0-9a-fA-F-]{36}$' -or ($Target.subscriptionId -eq [guid]::Empty.ToString() -and !$discoveryPlaceholder)) { throw 'A real subscription GUID is required.' }
    $null = [guid]::Parse($Target.subscriptionId)
    foreach ($key in @('resourceGroup','serviceConnection','agentPool','deploymentEnvironment')) {
        if ($Target[$key] -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$' -or $Target[$key] -match 'REPLACE') { throw "Invalid target field: $key" }
    }
    if ($Target.parameterFile -cnotmatch '^(workloads/(blob-transfer|logic-app-event-grid)/environments|self-service/parameters)/[a-zA-Z0-9/._-]+\.bicepparam$') { throw 'Parameter file must be in an approved source directory.' }
    if ((Get-TargetWorkloadType $Target) -eq 'logic-app-event-grid' -and !$Target.parameterFile.StartsWith('workloads/logic-app-event-grid/environments/')) { throw 'Logic App parameter path mismatch.' }
    if ((Get-TargetWorkloadType $Target) -eq 'blob-transfer' -and $Target.parameterFile.StartsWith('workloads/logic-app-event-grid/')) { throw 'Blob parameter path mismatch.' }
    $null = Resolve-ServicePath (Get-ProjectRoot) $Target.parameterFile
    if ($Target.smokePrefix -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9/_-]{0,199}/$') { throw 'Invalid synthetic smoke prefix.' }
    if ($Target.schemaVersion -in @(2,3)) {
        foreach ($key in @('subscriptionAlias','networkProfile')) { if ($Target[$key] -cnotmatch '^[a-z0-9][a-z0-9-]{0,39}$') { throw "Invalid catalog key: $key" } }
        $allowed=@('namingSuffix','networkMode','existingNetwork','location','deploymentPrincipalObjectId','vnetAddressPrefix','integrationSubnetPrefix','privateEndpointSubnetPrefix','existingLogAnalyticsWorkspaceId')
        if ((Get-TargetWorkloadType $Target) -eq 'logic-app-event-grid') { $allowed=@('location','integrationSubnetId','privateEndpointSubnetId','privateDnsZoneIds','existingLogAnalyticsWorkspaceId','deploymentPrincipalObjectId','trustedServiceException','runtimeStorageCredentialException') }
        if ($Target.parameterOverrides -isnot [Collections.IDictionary] -or @($Target.parameterOverrides.Keys | Where-Object { $_ -notin $allowed }).Count) { throw 'Unapproved profile parameter override.' }
    }
}
function Set-ServiceProfileParameters($Target,$Parameters) {
    if ($Target.schemaVersion -in @(2,3)) {
        foreach ($key in $Target.parameterOverrides.Keys) { $Parameters[$key]=@{value=$Target.parameterOverrides[$key]} }
    }
}
function Get-ServiceStem($Parameters) {
    $suffix=Get-ServiceParameter $Parameters namingSuffix ''
    return "$($Parameters.workload.value)-$($Parameters.environmentName.value)" + $(if ($suffix) { "-$suffix" } else { '' })
}
function Set-ServiceDeploymentOptions($Target, $Parameters, [bool]$CreateDestinationPrivateEndpoints, [bool]$EnableLogAlerts) {
    if ($Target.environmentName -eq 'prod' -and !$EnableLogAlerts) { throw 'Production requires log alerts. Keep Enable log alerts checked.' }
    $Parameters.createDestinationPrivateEndpoints=@{value=$CreateDestinationPrivateEndpoints}
    $Parameters.enableLogAlerts=@{value=$EnableLogAlerts}
}
function Get-ServiceParameter($Parameters, [string]$Name, $Default = $null) {
    if ($Parameters.Contains($Name)) { return $Parameters[$Name].value }
    return $Default
}
function Assert-ServiceParameters($Target, $Parameters) {
    if ((Get-TargetWorkloadType $Target) -eq 'logic-app-event-grid') { Assert-LogicParameters $Target $Parameters; return }
    foreach ($key in @('workload','environmentName','owner','costCenter','destinationSubscriptionId','destinationResourceGroupName','destinationStorageAccountName','destinationContainerName','destinationIsHnsEnabled')) {
        if (!$Parameters.Contains($key) -or $null -eq $Parameters[$key].value -or [string]$Parameters[$key].value -eq '') { throw "Missing explicit workload parameter: $key" }
    }
    if ((ConvertTo-Canonical $Parameters) -match 'REPLACE_|00000000-0000-0000-0000-000000000000') { throw 'Parameter placeholders must be replaced.' }
    if ($Parameters.workload.value -cne $Target.workload -or $Parameters.environmentName.value -cne $Target.environmentName) { throw 'Compiled workload/environment differs from target.' }
    $null = [guid]::Parse($Parameters.destinationSubscriptionId.value)
    $source = Get-ServiceParameter $Parameters uploadContainerName incoming
    $ledger = Get-ServiceParameter $Parameters ledgerContainerName transfer-ledger
    if ($source -eq $ledger) { throw 'Source and ledger must differ.' }
    foreach ($name in @($source,$ledger,(Get-ServiceParameter $Parameters deploymentContainerName packages),$Parameters.destinationContainerName.value,(Get-ServiceParameter $Parameters transferQueueName transfer-work))) {
        if ($name -cnotmatch '^[a-z0-9][a-z0-9-]{1,61}[a-z0-9]$' -or $name.Contains('--')) { throw 'Invalid storage container/queue name.' }
    }
    if ((Get-ServiceParameter $Parameters transferQueueName transfer-work).Length -gt 56) { throw 'Queue name leaves insufficient space for -poison.' }
    if (!(Get-ServiceParameter $Parameters recoveryIncludeSourceVersions $true)) { throw 'Azure acceptance requires retained source-version reconciliation.' }
    if ($Target.environmentName -eq 'prod' -and @((Get-ServiceParameter $Parameters alertActionGroupIds @())).Count -eq 0) { throw 'Production needs action groups.' }
    foreach ($key in @('createDestinationPrivateEndpoints','enableLogAlerts')) {
        if ($Parameters.Contains($key) -and $Parameters[$key].value -isnot [bool]) { throw "Expected boolean deployment option: $key" }
    }
    if ($Target.environmentName -eq 'prod' -and !(Get-ServiceParameter $Parameters enableLogAlerts $true)) { throw 'Production requires log alerts.' }
    $map = Get-ServiceParameter $Parameters sourceScopePrefixes @{ ''='default' }
    foreach ($scope in $map.Values) { if ($scope -cnotmatch '^[a-z0-9][a-z0-9-]{0,62}$') { throw 'Invalid scope map.' } }
    $null = Resolve-SourceScope ($Target.smokePrefix + 'probe/report.txt') $map
    $suffix=Get-ServiceParameter $Parameters namingSuffix ''
    if ($suffix -and $suffix -cnotmatch '^[a-z][a-z0-9]{1,3}-[a-z][a-z0-9]{1,4}-[0-9]{3}$') { throw 'Naming suffix must be organization-region-instance, for example acme-eus2-001 (max 14 characters).' }
    if ($suffix.Length -gt 14) { throw 'Naming suffix is too long.' }
    $principal=Get-ServiceParameter $Parameters deploymentPrincipalObjectId ''
    if ($principal) { if ([guid]::Parse($principal) -eq [guid]::Empty) { throw 'Invalid deployment principal object ID.' } }
    $workspace=Get-ServiceParameter $Parameters existingLogAnalyticsWorkspaceId ''
    if ($workspace -and $workspace -notmatch '^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/]+/providers/Microsoft.OperationalInsights/workspaces/[a-zA-Z0-9-]+$') { throw 'Invalid platform Log Analytics workspace resource ID.' }
    $mode=Get-ServiceParameter $Parameters networkMode new
    if ($mode -notin @('new','existing')) { throw 'Unknown network mode.' }
    if ($mode -eq 'new') {
        Assert-ServiceNewNetwork $Parameters
    } else {
        $network=Get-ServiceParameter $Parameters existingNetwork @{}
        Assert-ServiceNetworkIds $Target $network
        if (!(Get-ServiceParameter $Parameters location '')) { throw 'Existing network requires an explicit deployment location.' }
    }
}
function Assert-ServiceNewNetwork($Parameters) {
    $ranges=@{}
    foreach ($key in @('vnetAddressPrefix','integrationSubnetPrefix','privateEndpointSubnetPrefix')) {
        $cidr=Get-ServiceParameter $Parameters $key ''
        if ($cidr -cnotmatch '^(\d{1,3}\.){3}\d{1,3}/([0-9]{1,2})$') { throw "New network needs a valid IPv4 CIDR for $key." }
        $parts=$cidr.Split('/'); $prefix=[int]$parts[1]; $ip=[Net.IPAddress]::Parse($parts[0])
        if ($ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork -or $prefix -lt 8 -or $prefix -gt 29 -or ($key -eq 'integrationSubnetPrefix' -and $prefix -gt 26)) { throw "Unsupported address range for $key; integration needs /26 or larger." }
        $bytes=$ip.GetAddressBytes(); [uint64]$start=([uint64]$bytes[0]*16777216)+([uint64]$bytes[1]*65536)+([uint64]$bytes[2]*256)+$bytes[3]
        [uint64]$size=[math]::Pow(2,32-$prefix)
        if ($start % $size -ne 0 -or !(Test-ServicePrivateAddress $ip.ToString())) { throw "Use an aligned private IPv4 network for $key." }
        $ranges[$key]=@{start=$start;end=$start+$size-1}
    }
    $vnet=$ranges.vnetAddressPrefix; $integration=$ranges.integrationSubnetPrefix; $endpoints=$ranges.privateEndpointSubnetPrefix
    foreach ($subnet in @($integration,$endpoints)) { if ($subnet.start -lt $vnet.start -or $subnet.end -gt $vnet.end) { throw 'Both subnets must be contained in the VNet address range.' } }
    if ($integration.start -le $endpoints.end -and $endpoints.start -le $integration.end) { throw 'Integration and private endpoint subnet ranges must not overlap.' }
}
function Assert-ServiceNetworkIds($Target,$Network) {
    $pattern='^/subscriptions/([0-9a-fA-F-]{36})/resourceGroups/[^/]+/providers/Microsoft.Network/virtualNetworks/[^/]+/subnets/[^/]+$'
    foreach ($key in @('integrationSubnetId','privateEndpointSubnetId')) {
        if (!$Network.Contains($key) -or $Network[$key] -notmatch $pattern -or $Matches[1] -ine $Target.subscriptionId) { throw 'Subnets must be explicit IDs in the selected subscription.' }
    }
    $integrationVnet=$Network.integrationSubnetId -replace '/subnets/[^/]+$',''
    $endpointVnet=$Network.privateEndpointSubnetId -replace '/subnets/[^/]+$',''
    if ($integrationVnet -ine $endpointVnet -or $Network.integrationSubnetId -ieq $Network.privateEndpointSubnetId) { throw 'Select separate subnets in the same VNet.' }
    $zones=@{blob='privatelink.blob.core.windows.net';queue='privatelink.queue.core.windows.net';table='privatelink.table.core.windows.net';dfs='privatelink.dfs.core.windows.net';web='privatelink.azurewebsites.net'}
    foreach ($key in $zones.Keys) {
        if (!$Network.Contains('privateDnsZoneIds') -or !$Network.privateDnsZoneIds.Contains($key) -or $Network.privateDnsZoneIds[$key] -notmatch ('^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/]+/providers/Microsoft.Network/privateDnsZones/'+[regex]::Escape($zones[$key])+'$')) { throw "Expected existing private DNS zone ID for $key" }
    }
    if ($Network.Contains('dnsResolver')) {
        $resolver=$Network.dnsResolver
        if ($resolver -isnot [Collections.IDictionary] -or !$resolver.Contains('id') -or !$resolver.Contains('inboundEndpointId') -or $resolver.id -notmatch '^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/]+/providers/Microsoft.Network/dnsResolvers/[^/]+$' -or $resolver.inboundEndpointId -notmatch ('^'+[regex]::Escape($resolver.id)+'/inboundEndpoints/[^/]+$')) { throw 'Resolver mode requires explicit resolver and owned inbound endpoint IDs.' }
    }
}
function Test-ServiceNetwork($Bundle) {
    $p=$Bundle.parameters.parameters
    if ((Get-ServiceParameter $p networkMode new) -eq 'new') { return @{} }
    $n=$p.existingNetwork.value
    Assert-ServiceNetworkIds $Bundle.target $n
    $vnetId=$n.integrationSubnetId -replace '/subnets/[^/]+$',''
    $vnet=Invoke-ServiceJson @('resource','show','--ids',$vnetId,'--api-version','2024-05-01')
    if ($vnet.location -ine $p.location.value) { throw 'Integration VNet and Function must be in the same region.' }
    $state=@{vnetId=$vnetId;location=$vnet.location;subnets=@{};zones=@{}}
    foreach ($key in @('integrationSubnetId','privateEndpointSubnetId')) {
        $subnet=Invoke-ServiceJson @('resource','show','--ids',$n[$key],'--api-version','2024-05-01')
        $props=$subnet.properties
        $delegations=@(); if ($props.Contains('delegations')) { $delegations=@($props.delegations | ForEach-Object { $_.properties.serviceName }) }
        if ($key -eq 'integrationSubnetId') {
            if ($delegations.Count -ne 1 -or $delegations[0] -cne 'Microsoft.Web/serverFarms') { throw 'Integration subnet must be delegated to Microsoft.Web/serverFarms.' }
            $cidrs=if ($props.Contains('addressPrefixes')) { @($props.addressPrefixes) } else { @($props.addressPrefix) }
            if (!@($cidrs | Where-Object { $_ -match '^\d+\.\d+\.\d+\.\d+/(\d+)$' -and [int]$Matches[1] -le 26 }).Count) { throw 'Integration subnet requires an IPv4 /26 or larger under this blueprint standard.' }
            if ($props.Contains('privateEndpoints') -and @($props.privateEndpoints).Count) { throw 'Integration subnet cannot contain private endpoints.' }
            if ($props.Contains('serviceEndpointPolicies') -and @($props.serviceEndpointPolicies).Count) { throw 'Integration subnet cannot use service endpoint policies.' }
        } elseif ($delegations.Count -or $props.privateEndpointNetworkPolicies -ne 'Disabled') { throw 'Private endpoint subnet must be undelegated with private endpoint network policies Disabled.' }
        $state.subnets[$key]=$props
    }
    $dnsVnetId=$vnetId
    if ($n.Contains('dnsResolver')) {
        $resolver=Invoke-ServiceJson @('resource','show','--ids',$n.dnsResolver.id,'--api-version','2022-07-01')
        $inbound=Invoke-ServiceJson @('resource','show','--ids',$n.dnsResolver.inboundEndpointId,'--api-version','2022-07-01')
        if ($resolver.properties.provisioningState -ne 'Succeeded' -or $inbound.properties.provisioningState -ne 'Succeeded') { throw 'Central DNS resolver and inbound endpoint must be ready.' }
        $dnsVnetId=$resolver.properties.virtualNetwork.id
        if ($dnsVnetId -notmatch '^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/]+/providers/Microsoft.Network/virtualNetworks/[^/]+$') { throw 'Resolver VNet ID is invalid.' }
        $addresses=@($inbound.properties.ipConfigurations | ForEach-Object { $_.privateIpAddress })
        $servers=@(if ($vnet.properties.Contains('dhcpOptions') -and $vnet.properties.dhcpOptions.Contains('dnsServers')) { $vnet.properties.dhcpOptions.dnsServers })
        if (!$addresses.Count -or !$servers.Count -or @($addresses | Where-Object { !(Test-ServicePrivateAddress $_) }).Count -or @($servers | Where-Object { $_ -notin $addresses }).Count) { throw 'Spoke DNS servers must use the approved resolver inbound addresses.' }
        $state.dnsResolver=@{id=$n.dnsResolver.id;inboundEndpointId=$n.dnsResolver.inboundEndpointId;vnetId=$dnsVnetId;addresses=$addresses}
    }
    foreach ($key in $n.privateDnsZoneIds.Keys) {
        $zoneId=$n.privateDnsZoneIds[$key]
        $null=Invoke-ServiceJson @('resource','show','--ids',$zoneId,'--api-version','2024-06-01')
        # Query the actual link instead of assuming equal DNS names imply connectivity.
        $links=Invoke-ServiceJson @('rest','--method','get','--url',"https://management.azure.com$zoneId/virtualNetworkLinks?api-version=2024-06-01")
        if (!@($links.value | Where-Object { $_.properties.virtualNetwork.id -ieq $dnsVnetId -and $_.properties.provisioningState -eq 'Succeeded' }).Count) { throw "Private DNS zone $key needs an existing link to the selected DNS resolution VNet." }
        $state.zones[$key]=$zoneId
    }
    return $state
}
function Read-ServiceBundle([string]$Directory) {
    $receipt = Get-Content (Join-Path $Directory 'bundle.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($receipt.schemaVersion -eq 2) { return Read-LogicBundle $Directory $receipt }
    if ($receipt.schemaVersion -ne 1 -or $receipt.releaseId -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$') { throw 'Invalid bundle receipt.' }
    $expected = @('main.json','parameters.json','target.json','application.zip','functions.metadata')
    if ($receipt.Contains('discoverySource')) { $expected+=@('discovery/manifest.json','discovery/inventory.json') }
    if ($receipt.Contains('costEstimateIncluded') -and $receipt.costEstimateIncluded) { $expected+=@('cost-estimate.json') }
    if ($receipt.Contains('deploymentEngine')) {
        if ($receipt.deploymentEngine -cne 'deploymentStack') { throw 'Unknown deployment engine.' }
        $expected+=@('stack.json','stack-template.json')
    }
    if (@($receipt.files.Keys).Count -ne $expected.Count) { throw 'Unexpected bundle file set.' }
    foreach ($name in $expected) {
        if (!$receipt.files.Contains($name) -or (Get-ServiceHash (Resolve-ServicePath $Directory $name)) -cne $receipt.files[$name]) { throw "Bundle integrity failure: $name" }
    }
    $target = Get-Content (Join-Path $Directory target.json) -Raw | ConvertFrom-Json -AsHashtable
    Assert-ServiceTarget $target $target.workload $target.environmentName
    $parameters = Get-Content (Join-Path $Directory parameters.json) -Raw | ConvertFrom-Json -AsHashtable
    Assert-ServiceParameters $target $parameters.parameters
    if ($target.schemaVersion -in @(2,3)) {
        foreach ($key in $target.parameterOverrides.Keys) {
            if (!$parameters.parameters.Contains($key) -or (Get-ValueHash $parameters.parameters[$key].value) -cne (Get-ValueHash $target.parameterOverrides[$key])) { throw 'Frozen parameters differ from selected profile.' }
        }
    }
    Test-FunctionMetadata (Join-Path $Directory functions.metadata)
    $bundle=@{ receipt=$receipt; target=$target; parameters=$parameters; directory=$Directory; hash=(Get-ServiceHash (Join-Path $Directory bundle.json)) }
    if ($receipt.Contains('deploymentEngine')) {
        $bundle.stack=Get-Content (Join-Path $Directory stack.json) -Raw | ConvertFrom-Json -AsHashtable
        $template=Get-Content (Join-Path $Directory stack-template.json) -Raw | ConvertFrom-Json -AsHashtable
        Assert-StackContract $bundle.stack $target $template
    }
    return $bundle
}
function Invoke-ServiceJson([string[]]$Arguments) {
    $raw = Invoke-Az -Arguments ($Arguments + @('--output','json'))
    return ($raw -join "`n" | ConvertFrom-Json -AsHashtable)
}
function Get-ServiceState($Bundle) {
    if ($Bundle.Contains('stack')) { return Get-WorkloadStackState $Bundle }
    $t = $Bundle.target
    $null = Invoke-ServiceJson @('group','show','--subscription',$t.subscriptionId,'--name',$t.resourceGroup)
    $apps = @(Invoke-ServiceJson @('resource','list','--subscription',$t.subscriptionId,'--resource-group',$t.resourceGroup,'--resource-type','Microsoft.Web/sites'))
    $prefix = "func-$($t.workload)-$($t.environmentName)-"
    $matching = @($apps | Where-Object { $_.name.StartsWith($prefix,[StringComparison]::OrdinalIgnoreCase) })
    if ($matching.Count -gt 1) { throw 'More than one Function instance matches the target.' }
    return @{ hasApp=($matching.Count -eq 1); appIds=@($matching | ForEach-Object { $_.id.ToLowerInvariant() }) }
}
function Test-ServiceDestination($Bundle) {
    $t = $Bundle.target; $p = $Bundle.parameters.parameters
    $current = Invoke-ServiceJson @('account','show','--subscription',$t.subscriptionId)
    $destination = Invoke-ServiceJson @('account','show','--subscription',$p.destinationSubscriptionId.value)
    if ($current.tenantId -ne $destination.tenantId) { throw 'Cross-tenant destination is unsupported.' }
    $account = Invoke-ServiceJson @('storage','account','show','--subscription',$p.destinationSubscriptionId.value,'--resource-group',$p.destinationResourceGroupName.value,'--name',$p.destinationStorageAccountName.value)
    if ([bool]$account.isHnsEnabled -ne [bool]$p.destinationIsHnsEnabled.value) { throw 'Destination HNS configuration differs.' }
    $null = Invoke-ServiceJson @('resource','show','--ids',"$($account.id)/blobServices/default/containers/$($p.destinationContainerName.value)",'--api-version','2025-01-01')
}
function Get-ServiceOutputs($Bundle) {
    $t=$Bundle.target
    $deployment = if ($Bundle.Contains('stack')) { Get-WorkloadStack $Bundle } else { Invoke-ServiceJson @('deployment','group','show','--subscription',$t.subscriptionId,'--resource-group',$t.resourceGroup,'--name',"$($t.workload)-$($t.environmentName)") }
    if (!$deployment) { throw 'Foundation stack must exist before Release.' }
    $outputs = $deployment.properties.outputs
    foreach ($name in @('hostStorageAccountName','uploadStorageAccountName','uploadContainer','ledgerContainer','transferQueue','packageContainer','functionAppName','functionAppResourceId','managedIdentityPrincipalId','workspaceId')) {
        if (!$outputs.Contains($name) -or !$outputs[$name].value) { throw "Deployment output missing: $name. Import existing stacks using the documented deployment contract." }
    }
    foreach ($name in @('hostStorageAccountName','uploadStorageAccountName')) {
        if ($outputs[$name].value -cnotmatch '^[a-z0-9]{3,24}$') { throw 'Invalid output storage account.' }
    }
    $expectedPrefix = "/subscriptions/$($t.subscriptionId)/resourceGroups/$($t.resourceGroup)/providers/Microsoft.Web/sites/func-$($t.workload)-$($t.environmentName)-"
    if (!$outputs.functionAppResourceId.value.StartsWith($expectedPrefix,[StringComparison]::OrdinalIgnoreCase)) { throw 'Deployment outputs belong to another target.' }
    $stem=Get-ServiceStem $Bundle.parameters.parameters
    if (!$outputs.functionAppName.value.StartsWith("func-$stem-",[StringComparison]::OrdinalIgnoreCase)) { throw 'Existing resource naming differs; naming changes require migration, not self-service replacement.' }
    $oldSuffix=$outputs.functionAppName.value.Substring("func-$($t.workload)-$($t.environmentName)-".Length)
    $configuredSuffix=Get-ServiceParameter $Bundle.parameters.parameters namingSuffix ''
    if (!$configuredSuffix -and $oldSuffix.Contains('-')) { throw 'Existing naming suffix cannot be removed by self-service.' }
    $p=$Bundle.parameters.parameters
    foreach ($pair in @(@('uploadContainer','uploadContainerName','incoming'),@('ledgerContainer','ledgerContainerName','transfer-ledger'),@('transferQueue','transferQueueName','transfer-work'),@('packageContainer','deploymentContainerName','packages'))) {
        if ($outputs[$pair[0]].value -cne (Get-ServiceParameter $p $pair[1] $pair[2])) { throw 'Existing container/queue names differ; migration requires a separate reviewed workflow.' }
    }
    return $outputs
}
function Get-ServiceChanges($Report, $Bundle=$null) {
    if (!$Report.Contains('status') -or $Report.status -ne 'Succeeded' -or !$Report.Contains('changes') -or ($Report.Contains('error') -and $Report.error)) { throw 'Azure what-if did not return successful analyzed changes.' }
    $changes = @($Report.changes | Sort-Object resourceId)
    foreach ($change in $changes) {
        if ($change.changeType -notin @('Create','Modify','NoChange','NoEffect')) { throw "Unapproved or unanalyzed what-if change: $($change.changeType). Review outside self-service." }
        if ($Bundle -and (Get-TargetWorkloadType $Bundle.target) -eq 'logic-app-event-grid') { Assert-LogicChange $change $Bundle } else { Assert-ServiceChange $change }
    }
    return ,$changes
}
function New-ServicePlan($Bundle, [ValidateSet('Foundation','Release')][string]$Phase, [string]$Directory) {
    if ((Get-TargetWorkloadType $Bundle.target) -eq 'logic-app-event-grid') { return New-LogicPlan $Bundle $Phase $Directory }
    New-Item -ItemType Directory -Path $Directory -Force | Out-Null
    Test-ServiceDestination $Bundle
    $state = Get-ServiceState $Bundle
    $state.network = Test-ServiceNetwork $Bundle
    $skip = $Phase -eq 'Foundation' -and $state.hasApp
    $parameters = ConvertFrom-Json (ConvertTo-Json $Bundle.parameters -Depth 100) -AsHashtable
    $parameters.parameters.deployFunctionApp = @{value=($Phase -eq 'Release')}
    $parameters.parameters.packageBlobName = @{value="releases/$($Bundle.receipt.releaseId).zip"}
    if ($Bundle.Contains('stack')) { $parameters.parameters.workloadResourceGroupName=@{value=$Bundle.target.resourceGroup} }
    $parametersPath = Join-Path $Directory 'effective.parameters.json'
    Write-ServiceJson $parameters $parametersPath
    $outputs = if ($state.hasApp -or $Phase -eq 'Release') { Get-ServiceOutputs $Bundle } else { @{} }
    $report = @{status='Succeeded'; changes=@()}
    if (!$skip) {
        $t=$Bundle.target
        if ($Bundle.Contains('stack')) {
            $report=New-StackPreview $Bundle $state $parametersPath $Directory
        } else {
        # ARM/provider validation exercises permissions and applicable deny policies.
        # It is not an asynchronous compliance scan or proof of complete policy coverage.
        $validation=Invoke-ServiceJson @('deployment','group','validate','--subscription',$t.subscriptionId,'--resource-group',$t.resourceGroup,'--template-file',(Join-Path $Bundle.directory 'main.json'),'--parameters',"@$parametersPath",'--validation-level','Provider')
        Write-ServiceJson $validation (Join-Path $Directory 'arm-validation.json')
        $report = Invoke-ServiceJson @('deployment','group','what-if','--subscription',$t.subscriptionId,'--resource-group',$t.resourceGroup,'--name',"$($t.workload)-$($t.environmentName)",'--template-file',(Join-Path $Bundle.directory 'main.json'),'--parameters',"@$parametersPath",'--mode','Incremental','--result-format','FullResourcePayloads')
        }
    }
    Write-ServiceJson $report (Join-Path $Directory 'what-if.json')
    $changes=Get-ServiceChanges $report
    $plan = @{schemaVersion=1; phase=$Phase; skip=$skip; bundleHash=$Bundle.hash; parametersHash=(Get-ServiceHash $parametersPath); state=$state; outputs=$outputs; changes=$changes; createdUtc=[DateTimeOffset]::UtcNow.ToString('O')}
    $plan.fingerprint = Get-ValueHash @{bundleHash=$plan.bundleHash; parametersHash=$plan.parametersHash; state=$state; outputs=$outputs; changes=$changes; phase=$Phase; skip=$skip}
    Write-ServiceJson $plan (Join-Path $Directory 'plan.json')
    $summary = @("# $Phase preview",'',"Target: $($Bundle.target.workload) / $($Bundle.target.environmentName)","Release: $($Bundle.receipt.releaseId)","Package SHA-256: $($Bundle.receipt.files['application.zip'])","Bundle SHA-256: $($Bundle.hash)","Skip existing foundation: $skip",'', 'Review what-if.json and plan.json before approving. Plans expire after 24 hours.','')
    if ($Bundle.Contains('stack')) { $summary+=@("Stack: $($Bundle.stack.stackId)","Template Spec: $($Bundle.stack.templateSpecId)","Template hash: $($Bundle.stack.templateHash)","Lifecycle: $($Bundle.stack.actionOnUnmanage); deny mode: $($Bundle.stack.denySettingsMode). Detach/Delete changes are rejected by self-service.",'') }
    $summary+=@("Create destination private endpoints: $(Get-ServiceParameter $parameters.parameters createDestinationPrivateEndpoints $true)",
        "Enable log alerts: $(Get-ServiceParameter $parameters.parameters enableLogAlerts $true)",
        'Omitting destination endpoints requires existing private connectivity. Stack self-service rejects removal of managed endpoints; the legacy incremental path retains existing endpoints. Disabled alerts keep their rule resources; log collection remains enabled.','')
    if ($Bundle.receipt.Contains('costEstimateIncluded') -and $Bundle.receipt.costEstimateIncluded) {
        $cost=Get-Content (Join-Path $Bundle.directory cost-estimate.json) -Raw | ConvertFrom-Json -AsHashtable
        if ($cost.status -eq 'Estimated') {
            $amount=([double]$cost.fixedMonthlySubtotalUsd).ToString('F2',[Globalization.CultureInfo]::InvariantCulture)
            $summary+="Full-release fixed estimate: USD $amount/month plus usage; retail snapshot $($cost.pricingAsOf). See the bundle's cost-estimate.json for quantities and exclusions. This is not a spending cap."
        } else { $summary+="Cost estimate unavailable: $($cost.reason)" }
    }
    if ((Get-ServiceParameter $parameters.parameters networkMode new) -eq 'new') {
        $summary+=@("Network: Bicep-managed vnet-$(Get-ServiceStem $parameters.parameters) in $($Bundle.target.resourceGroup).",
            'Missing template resources are created on approved apply. Existing resources at the same IDs are reconciled to the template; review every Modify change. DNS zones use the fixed Azure service names.', '')
    } else { $summary+=@('Network: reuse the approved subnet and DNS zone IDs. Shared-network resources must already exist; they are not created by this profile.','') }
    $summary += @($changes | ForEach-Object { "- $($_.changeType): $($_.resourceId)" })
    $summary | Set-Content (Join-Path $Directory 'summary.md')
    return $plan
}
function Assert-ServicePlan($Bundle, $Approved, $Current, [string]$Phase) {
    if ($Approved.schemaVersion -ne 1 -or $Approved.phase -cne $Phase -or $Approved.bundleHash -cne $Bundle.hash) { throw 'Approved plan does not match this bundle/phase.' }
    $age=[DateTimeOffset]::UtcNow-[DateTimeOffset]::Parse($Approved.createdUtc)
    if ($age.TotalHours -gt 24 -or $age.TotalMinutes -lt -5) { throw 'Plan expired or has invalid time; request a new preview and approval.' }
    if ($Approved.fingerprint -cne $Current.fingerprint) { throw 'Azure state or planned changes drifted after preview. Run a new preview; no deployment performed.' }
}
function Test-ServicePrivateAddress([string]$Address) {
    $ip=[Net.IPAddress]::Parse($Address)
    if ($ip.AddressFamily -ne [Net.Sockets.AddressFamily]::InterNetwork) { return $false }
    $b=$ip.GetAddressBytes()
    return $b[0] -eq 10 -or ($b[0] -eq 172 -and $b[1] -ge 16 -and $b[1] -le 31) -or ($b[0] -eq 192 -and $b[1] -eq 168)
}
function Wait-ServiceConnectivity($Bundle, $Outputs) {
    $t=$Bundle.target; $p=$Bundle.parameters.parameters
    for ($attempt=1; $attempt -le 30; $attempt++) {
        try {
            $endpoints=@(Invoke-ServiceJson @('network','private-endpoint','list','--subscription',$t.subscriptionId,'--resource-group',$t.resourceGroup))
            $owned=@($endpoints | Where-Object { $_.name.StartsWith("pe-$($t.workload)-$($t.environmentName)-",[StringComparison]::OrdinalIgnoreCase) })
            if ($owned.Count -lt 5) { throw 'Expected storage private endpoints have not appeared.' }
            foreach ($endpoint in $owned) {
                $connections=@($endpoint.privateLinkServiceConnections)
                if ($endpoint.Contains('manualPrivateLinkServiceConnections')) { $connections+=@($endpoint.manualPrivateLinkServiceConnections) }
                if (!$connections.Count -or @($connections | Where-Object { $_.privateLinkServiceConnectionState.status -ne 'Approved' }).Count) { throw "Private endpoint requires approval: $($endpoint.name)" }
            }
            foreach ($dnsName in @("$($Outputs.hostStorageAccountName.value).blob.core.windows.net","$($Outputs.uploadStorageAccountName.value).blob.core.windows.net","$($Outputs.uploadStorageAccountName.value).queue.core.windows.net","$($p.destinationStorageAccountName.value).blob.core.windows.net")) {
                $addresses=@([Net.Dns]::GetHostAddresses($dnsName))
                if (!$addresses.Count -or @($addresses | Where-Object { !(Test-ServicePrivateAddress $_.ToString()) }).Count) { throw "Private DNS resolution required: $dnsName" }
                $client=[Net.Sockets.TcpClient]::new()
                try { if (!$client.ConnectAsync($dnsName,443).Wait(3000)) { throw "Private endpoint is unreachable: $dnsName" } } finally { $client.Dispose() }
            }
            # Data-plane checks also prove the selected pipeline identity's access.
            foreach ($item in @(@($t.subscriptionId,$Outputs.hostStorageAccountName.value,$Outputs.packageContainer.value),@($t.subscriptionId,$Outputs.uploadStorageAccountName.value,$Outputs.uploadContainer.value),@($t.subscriptionId,$Outputs.uploadStorageAccountName.value,$Outputs.ledgerContainer.value),@($p.destinationSubscriptionId.value,$p.destinationStorageAccountName.value,$p.destinationContainerName.value))) {
                $null=Invoke-ServiceJson @('storage','container','show','--subscription',$item[0],'--account-name',$item[1],'--name',$item[2],'--auth-mode','login')
            }
            $null=Invoke-ServiceJson @('storage','queue','metadata','show','--subscription',$t.subscriptionId,'--account-name',$Outputs.uploadStorageAccountName.value,'--name',$Outputs.transferQueue.value,'--auth-mode','login')
            return
        } catch {
            if ($attempt -eq 30) { throw }
            Write-Host "Connectivity/RBAC not ready (attempt $attempt/30): $($_.Exception.Message)"
            Start-Sleep -Seconds 10
        }
    }
}
function Publish-ServicePackage($Bundle, $Outputs, [string]$Directory) {
    $t=$Bundle.target; $blob="releases/$($Bundle.receipt.releaseId).zip"
    $args=@('--subscription',$t.subscriptionId,'--account-name',$Outputs.hostStorageAccountName.value,'--container-name',$Outputs.packageContainer.value,'--name',$blob,'--auth-mode','login')
    $exists=Invoke-ServiceJson (@('storage','blob','exists')+$args)
    if ($exists.exists) {
        $existing=Join-Path $Directory 'existing-package.zip'
        $null=Invoke-ServiceJson (@('storage','blob','download')+$args+@('--file',$existing,'--overwrite','true'))
        if ((Get-ServiceHash $existing) -cne $Bundle.receipt.files['application.zip']) { throw 'Release ID already contains different package bytes.' }
    } else {
        $null=Invoke-ServiceJson (@('storage','blob','upload')+$args+@('--file',(Join-Path $Bundle.directory application.zip),'--overwrite','false'))
    }
}
function Wait-ServiceFunctions($Bundle, $Outputs) {
    $t=$Bundle.target
    $expected=@('AuditTransferLedger','CopyUploadedBlob','DispatchUploadedBlob','MonitorTransferPoison','ReconcileTransfers')
    for ($attempt=1; $attempt -le 20; $attempt++) {
        try {
            # A successful trigger synchronization may return no JSON body.
            $null=Invoke-Az -Arguments @('rest','--method','post','--url',"https://management.azure.com$($Outputs.functionAppResourceId.value)/syncfunctiontriggers?api-version=2024-11-01")
            $functions=@(Invoke-ServiceJson @('functionapp','function','list','--subscription',$t.subscriptionId,'--resource-group',$t.resourceGroup,'--name',$Outputs.functionAppName.value))
            $names=@($functions | ForEach-Object { ($_.name -split '/')[-1] } | Sort-Object)
            if (($names -join ',') -cne ($expected -join ',')) { throw 'Expected five indexed Functions are not ready.' }
            return
        } catch { if ($attempt -eq 20) { throw }; Start-Sleep -Seconds 15 }
    }
}
function Invoke-ServiceSmoke($Bundle, $Outputs, [string]$EvidenceDirectory) {
    $t=$Bundle.target; $p=$Bundle.parameters.parameters
    $smokeArgs=@{
        SubscriptionId=$t.subscriptionId; UploadAccount=$Outputs.uploadStorageAccountName.value
        UploadContainer=$Outputs.uploadContainer.value; LedgerContainer=$Outputs.ledgerContainer.value
        SourcePrefix=$t.smokePrefix; ScopePrefixesJson=((Get-ServiceParameter $p sourceScopePrefixes @{''='default'}) | ConvertTo-Json -Compress)
        DestinationSubscriptionId=$p.destinationSubscriptionId.value; DestinationAccount=$p.destinationStorageAccountName.value
        DestinationContainer=$p.destinationContainerName.value; EvidencePath=(Join-Path $EvidenceDirectory smoke.json)
    }
    & "$PSScriptRoot/Smoke-Test.ps1" @smokeArgs
    return Get-Content $smokeArgs.EvidencePath -Raw | ConvertFrom-Json -AsHashtable
}
function Invoke-ServiceApply($Bundle, [string]$Phase, [string]$PlanDirectory, [string]$EvidenceDirectory) {
    if ((Get-TargetWorkloadType $Bundle.target) -eq 'logic-app-event-grid') { return Invoke-LogicApply $Bundle $Phase $PlanDirectory $EvidenceDirectory }
    $approved=Get-Content (Join-Path $PlanDirectory plan.json) -Raw | ConvertFrom-Json -AsHashtable
    $current=New-ServicePlan $Bundle $Phase (Join-Path $EvidenceDirectory recheck)
    Assert-ServicePlan $Bundle $approved $current $Phase
    $t=$Bundle.target
    if ($Phase -eq 'Release') {
        Wait-ServiceConnectivity $Bundle $current.outputs
        Publish-ServicePackage $Bundle $current.outputs $EvidenceDirectory
    }
    if (!$current.skip) {
        if ($Bundle.Contains('stack')) {
            $deployment=Invoke-WorkloadStackApply $Bundle (Join-Path $EvidenceDirectory recheck/effective.parameters.json) $EvidenceDirectory $current
        } else {
        $deployment=Invoke-ServiceJson @('deployment','group','create','--subscription',$t.subscriptionId,'--resource-group',$t.resourceGroup,'--name',"$($t.workload)-$($t.environmentName)",'--template-file',(Join-Path $Bundle.directory main.json),'--parameters',('@'+(Join-Path $EvidenceDirectory recheck/effective.parameters.json)),'--mode','Incremental')
        }
        if ($deployment.properties.provisioningState -ne 'Succeeded') { throw 'ARM deployment did not succeed.' }
    }
    $outputs=Get-ServiceOutputs $Bundle
    Write-ServiceJson $outputs (Join-Path $EvidenceDirectory outputs.json)
    Wait-ServiceConnectivity $Bundle $outputs
    if ($Phase -eq 'Release') {
        Wait-ServiceFunctions $Bundle $outputs
        $smoke=Invoke-ServiceSmoke $Bundle $outputs $EvidenceDirectory
        if ($smoke.passed -isnot [bool] -or !$smoke.passed -or $smoke.requestIds.Count -ne 3 -or @($smoke.requestIds | Sort-Object -Unique).Count -ne 3) { throw 'Smoke evidence is incomplete.' }
        return @{ready=$true; status='Ready'; outputs=$outputs; smoke=$smoke}
    }
    return @{ready=$false; status='FoundationReady'; outputs=$outputs}
}
