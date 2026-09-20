# Logic App Standard adapter. Loaded by self-service-common.ps1; Azure calls use
# the same mockable CLI boundary and stack/preview governance as blob-transfer.
function Get-LogicPlaceholderPaths($Value,[string]$Path) {
    if($Value -is [Collections.IDictionary]){
        foreach($key in @($Value.Keys|Sort-Object)){Get-LogicPlaceholderPaths $Value[$key] "$Path.$key"}
    }elseif($Value -is [array]){
        for($i=0;$i -lt $Value.Count;$i++){Get-LogicPlaceholderPaths $Value[$i] "$Path[$i]"}
    }elseif($Value -is [string] -and $Value -match 'REPLACE|00000000-0000-0000-0000-000000000000'){$Path}
}
function Get-LogicOnboardingIssues($P) {
    $requirements=@{
        owner='Supply the responsible team or owner tag.'
        costCenter='Supply the approved cost-center tag.'
        integrationSubnetId='Select the existing Logic App integration subnet resource ID.'
        privateEndpointSubnetId='Select a different existing private-endpoint subnet in the same VNet.'
        privateDnsZoneIds='Supply the six existing private DNS zone resource IDs (blob, queue, table, file, sites, topic).'
        existingLogAnalyticsWorkspaceId='Select the existing Log Analytics workspace resource ID.'
        deploymentPrincipalObjectId='Supply the service connection identity object ID, not its application/client ID.'
        trustedServiceException='Platform review required: approve trusted-service Event Grid delivery and provide its HTTPS review reference.'
        runtimeStorageCredentialException='Platform review required: approve private runtime-storage credentials and provide its HTTPS review reference.'
    }
    foreach($key in @('workload','environmentName','location','owner','costCenter','integrationSubnetId','privateEndpointSubnetId','privateDnsZoneIds','existingLogAnalyticsWorkspaceId','deploymentPrincipalObjectId','trustedServiceException','runtimeStorageCredentialException')){
        if(!$P.Contains($key) -or $P[$key] -isnot [Collections.IDictionary] -or !$P[$key].Contains('value') -or $null -eq $P[$key].value -or ($P[$key].value -is [string] -and [string]::IsNullOrWhiteSpace($P[$key].value))){
            $message=if($requirements.Contains($key)){$requirements[$key]}else{'Supply the registered workload/environment/region value.'}
            @{parameter=$key;requirement=$message}
        }
    }
    foreach($key in @($P.Keys|Sort-Object)){
        if($P[$key] -isnot [Collections.IDictionary] -or !$P[$key].Contains('value')){continue}
        foreach($path in @(Get-LogicPlaceholderPaths $P[$key].value $key)){
            $message=if($requirements.Contains($key)){$requirements[$key]}else{'Replace the onboarding placeholder with an approved value.'}
            @{parameter=$path;requirement=$message}
        }
    }
    foreach($key in @('trustedServiceException','runtimeStorageCredentialException')){
        if(!$P.Contains($key) -or $P[$key] -isnot [Collections.IDictionary] -or !$P[$key].Contains('value') -or $null -eq $P[$key].value){continue}
        $review=$P[$key].value
        if($review -isnot [Collections.IDictionary] -or !$review.Contains('approved') -or $review.approved -isnot [bool] -or !$review.approved -or !$review.Contains('reviewReference') -or $review.reviewReference -cnotmatch '^https://[^\s]+$'){
            @{parameter=$key;requirement=$requirements[$key]}
        }
    }
}
function Assert-LogicParameters($Target,$P) {
    $issues=@(Get-LogicOnboardingIssues $P)
    if($issues.Count){
        $details=@($issues|ForEach-Object {"- $($_.parameter): $($_.requirement)"}) -join "`n"
        throw "Logic App onboarding is incomplete. Update $($Target.parameterFile):`n$details`nDiscovery records available resources; it does not select shared resources, supply ownership tags or grant platform approvals."
    }
    if ($P.workload.value -cne $Target.workload -or $P.environmentName.value -cne $Target.environmentName -or $P.location.value -cne 'eastus2') { throw 'Logic App intent mismatch.' }
    if ([guid]::Parse($P.deploymentPrincipalObjectId.value) -eq [guid]::Empty) { throw 'Invalid deployment principal.' }
    foreach($key in @('integrationSubnetId','privateEndpointSubnetId')) { if ($P[$key].value -cnotmatch '^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/]+/providers/Microsoft.Network/virtualNetworks/[^/]+/subnets/[^/]+$') { throw 'Invalid approved subnet ID.' } }
    if ($P.integrationSubnetId.value -ieq $P.privateEndpointSubnetId.value -or ($P.integrationSubnetId.value -replace '/subnets/[^/]+$','') -ine ($P.privateEndpointSubnetId.value -replace '/subnets/[^/]+$','')) { throw 'Logic App requires separate subnets in the same approved VNet.' }
    $zoneNames=@{blob='privatelink.blob.core.windows.net';queue='privatelink.queue.core.windows.net';table='privatelink.table.core.windows.net';file='privatelink.file.core.windows.net';sites='privatelink.azurewebsites.net';topic='privatelink.eventgrid.azure.net'}
    if ($P.privateDnsZoneIds.value.Count -ne $zoneNames.Count) { throw 'Exactly six DNS service zones are required.' }
    foreach($key in $zoneNames.Keys) { if (!$P.privateDnsZoneIds.value.Contains($key) -or $P.privateDnsZoneIds.value[$key] -notmatch ('^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/]+/providers/Microsoft.Network/privateDnsZones/'+[regex]::Escape($zoneNames[$key])+'$')) { throw "Missing or incorrect DNS zone: $key" } }
    if ($P.existingLogAnalyticsWorkspaceId.value -notmatch '^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[^/]+/providers/Microsoft.OperationalInsights/workspaces/[^/]+$') { throw 'Invalid monitoring workspace.' }
    if ((Get-ServiceParameter $P hostingSku WS1) -cnotin @('WS1','WS2','WS3')) { throw 'Unsupported Workflow Standard SKU.' }
    if ($Target.environmentName -eq 'prod' -and !(Get-ServiceParameter $P alertActionGroupIds @()).Count) { throw 'Production requires alert action groups.' }
}
function Get-LogicCostEstimate($P) {
    $snapshot=Get-Content (Join-Path (Get-ProjectRoot) 'self-service/pricing/logic-app-eastus2.json') -Raw | ConvertFrom-Json -AsHashtable
    $sku=Get-ServiceParameter $P hostingSku WS1
    if ($snapshot.currency -ne 'USD' -or $snapshot.region -ne $P.location.value -or !$snapshot.rates.Contains($sku) -or ([DateTimeOffset]::UtcNow-[DateTimeOffset]::Parse($snapshot.retrievedUtc)).TotalDays -gt 30) { return @{status='Unavailable';reason='Refresh reviewed regional Logic App prices.';fixedMonthlySubtotalUsd=$null} }
    $lines=@(@{resource="Workflow Standard $sku";quantity=1;monthlyUnitUsd=$snapshot.rates[$sku]*730},@{resource='Private endpoints';quantity=8;monthlyUnitUsd=$snapshot.rates.privateEndpoint*730})
    $prerequisites=Get-ServiceParameter $P prerequisitePlan @{}
    if($prerequisites.Count -and @($prerequisites.createDnsZoneNames).Count){
        $dnsPrices=Get-Content (Join-Path (Get-ProjectRoot) self-service/pricing/usd-eastus2.json) -Raw|ConvertFrom-Json -AsHashtable
        if(([DateTimeOffset]::UtcNow-[DateTimeOffset]::Parse($dnsPrices.retrievedUtc)).TotalDays -gt 30){return @{status='Unavailable';reason='Refresh reviewed DNS prices.';fixedMonthlySubtotalUsd=$null}}
        $lines+=@{resource='Owned private DNS zones';quantity=@($prerequisites.createDnsZoneNames).Count;monthlyUnitUsd=$dnsPrices.rates.privateDnsZone.retailPrice}
    }
    return @{status='Estimated';currency='USD';region=$snapshot.region;pricingAsOf=$snapshot.retrievedUtc;lines=$lines;fixedMonthlySubtotalUsd=($lines|ForEach-Object {$_.quantity*$_.monthlyUnitUsd}|Measure-Object -Sum).Sum;exclusions=@('Storage, Event Grid operations, logs, alerts, DNS queries, network transfer and pipeline agents are usage or additional charges.','Retail estimate; not a spending cap.');workflowPackageSeparateFromTemplateSpec=$true}
}
function Test-LogicPackage([string]$ZipPath) {
    $expected=@('connections.json','host.json','parameters.json','process-event/workflow.json')
    $zip=[IO.Compression.ZipFile]::OpenRead($ZipPath)
    try {
        $names=@($zip.Entries | ForEach-Object {$_.FullName})
        if ($names.Count -ne $expected.Count -or @(Compare-Object ($names|Sort-Object) ($expected|Sort-Object)).Count) { throw 'Logic workflow package inventory differs; removal/addition requires a reviewed adapter change.' }
        foreach($entry in $zip.Entries) {
            if ($entry.Length -gt 1048576) { throw 'Oversized workflow definition.' }
            $reader=[IO.StreamReader]::new($entry.Open());try{$value=$reader.ReadToEnd()|ConvertFrom-Json -AsHashtable}finally{$reader.Dispose()}
            if ($entry.FullName -eq 'process-event/workflow.json' -and ($value.kind -cne 'Stateful' -or !$value.definition.actions.Contains('For_each_message'))) { throw 'Unexpected workflow contract.' }
        }
    } finally { $zip.Dispose() }
}
function New-LogicBundle($Target,[string]$ReleaseDirectory,[string]$Directory,[string]$DiscoveryDirectory) {
    if (!$DiscoveryDirectory) { throw 'Logic App bundles require verified discovery.' }
    $discovery=Read-DiscoveryManifest $DiscoveryDirectory $Target $Target.serviceConnection
    $release=Get-Content (Join-Path $ReleaseDirectory release.json) -Raw | ConvertFrom-Json -AsHashtable
    if ($release.workloadType -cne 'logic-app-event-grid' -or $release.dirtyWorktree -isnot [bool] -or $release.dirtyWorktree -or $release.releaseId -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$') { throw 'Clean qualified Logic App release required.' }
    $root=Get-ProjectRoot; $commit=& git -C $root rev-parse HEAD
    if ($LASTEXITCODE -ne 0 -or $release.sourceCommit -cne $commit) { throw 'Logic App release source mismatch.' }
    $package=Resolve-ServicePath $ReleaseDirectory "$($release.releaseId).zip"
    if ((Get-ServiceHash $package) -cne $release.packageSha256) { throw 'Workflow package hash mismatch.' }
    Test-LogicPackage $package
    New-Item -ItemType Directory -Path $Directory | Out-Null
    $definition=Get-WorkloadDefinition 'logic-app-event-grid'
    Invoke-Bicep -Arguments @('build',(Join-Path $root $definition.composition),'--outfile',(Join-Path $Directory main.json))
    Invoke-Bicep -Arguments @('build-params',(Resolve-ServicePath $root $Target.parameterFile),'--outfile',(Join-Path $Directory parameters.json))
    $p=Get-Content (Join-Path $Directory parameters.json) -Raw|ConvertFrom-Json -AsHashtable
    Set-ServiceProfileParameters $Target $p.parameters
    Set-LogicDiscoveredPrerequisites $Target $p.parameters $DiscoveryDirectory $Directory
    Assert-LogicParameters $Target $p.parameters
    Assert-LogicDiscoveryResources $Target $p.parameters $DiscoveryDirectory
    Write-ServiceJson $p (Join-Path $Directory parameters.json)
    Invoke-Bicep -Arguments @('build',(Join-Path $root $definition.stack),'--outfile',(Join-Path $Directory stack-template.json))
    $template=Get-Content (Join-Path $Directory stack-template.json) -Raw|ConvertFrom-Json -AsHashtable
    Write-ServiceJson (New-StackContract $Target $template) (Join-Path $Directory stack.json)
    Write-ServiceJson $definition (Join-Path $Directory workload-definition.json)
    Write-ServiceJson $Target (Join-Path $Directory target.json)
    Write-ServiceJson (Get-LogicCostEstimate $p.parameters) (Join-Path $Directory cost-estimate.json)
    Copy-Item -LiteralPath $package -Destination (Join-Path $Directory application.zip)
    New-Item -ItemType Directory -Path (Join-Path $Directory discovery) | Out-Null
    foreach($f in @('manifest.json','inventory.json')) { Copy-Item (Join-Path $DiscoveryDirectory $f) (Join-Path $Directory "discovery/$f") }
    $files=@{};foreach($f in @('main.json','parameters.json','target.json','application.zip','stack-template.json','stack.json','workload-definition.json','cost-estimate.json','discovery/manifest.json','discovery/inventory.json')){$files[$f]=Get-ServiceHash (Join-Path $Directory $f)}
    Write-ServiceJson @{schemaVersion=2;workloadType='logic-app-event-grid';deploymentEngine='deploymentStack';releaseId=$release.releaseId;sourceCommit=$release.sourceCommit;files=$files;createdUtc=[DateTimeOffset]::UtcNow.ToString('O');discoverySource=$discovery.source;costEstimateIncluded=$true} (Join-Path $Directory bundle.json)
    $null=Read-ServiceBundle $Directory
    $cost=Get-Content (Join-Path $Directory cost-estimate.json) -Raw|ConvertFrom-Json -AsHashtable
    $summary=Join-Path (Split-Path -Parent $Directory) cost-summary.md
    @('# Logic App deployment estimate','',"Status: $($cost.status)","Fixed monthly subtotal USD: $($cost.fixedMonthlySubtotalUsd)",'Includes selected Workflow Standard SKU and eight private endpoints at 730 hours/month. Storage, Event Grid, logs, alerts, DNS, transfer and agents are additional. Not a spending cap.','Platform approval required for the trusted-service delivery and private runtime-storage credential exceptions.')|Set-Content -LiteralPath $summary
    if($env:TF_BUILD -eq 'True'){Write-Host "##vso[task.uploadsummary]$summary"}
}

function Read-LogicBundle([string]$Directory,$Receipt) {
    if ($Receipt.workloadType -cne 'logic-app-event-grid' -or $Receipt.deploymentEngine -cne 'deploymentStack' -or $Receipt.releaseId -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$') { throw 'Invalid Logic App bundle.' }
    $expected=@('main.json','parameters.json','target.json','application.zip','stack-template.json','stack.json','workload-definition.json','cost-estimate.json','discovery/manifest.json','discovery/inventory.json')
    if ($Receipt.files.Count -ne $expected.Count) { throw 'Unexpected Logic App bundle file set.' }
    foreach($f in $expected) { if (!$Receipt.files.Contains($f) -or (Get-ServiceHash (Resolve-ServicePath $Directory $f)) -cne $Receipt.files[$f]) { throw "Logic App integrity failure: $f" } }
    $definition=Get-Content (Join-Path $Directory workload-definition.json) -Raw|ConvertFrom-Json -AsHashtable
    if ((Get-ValueHash $definition) -cne (Get-ValueHash (Get-WorkloadDefinition 'logic-app-event-grid'))) { throw 'Workload definition differs from approved source.' }
    $target=Get-Content (Join-Path $Directory target.json) -Raw|ConvertFrom-Json -AsHashtable
    if ((Get-TargetWorkloadType $target) -cne $Receipt.workloadType) { throw 'Cross-workload bundle rejected.' }
    Assert-ServiceTarget $target $target.workload $target.environmentName
    $p=Get-Content (Join-Path $Directory parameters.json) -Raw|ConvertFrom-Json -AsHashtable
    Assert-LogicParameters $target $p.parameters
    foreach($key in $target.parameterOverrides.Keys){if ((Get-ValueHash $target.parameterOverrides[$key]) -cne (Get-ValueHash $p.parameters[$key].value)){throw 'Frozen Logic App parameters differ from target.'}}
    if(!(Get-Command Read-DiscoveryManifest -ErrorAction SilentlyContinue)){. "$PSScriptRoot/discovery-manifest-common.ps1"}
    $manifest=Read-DiscoveryManifest (Join-Path $Directory discovery) $target $target.serviceConnection
    if((Get-ValueHash $Receipt.discoverySource) -cne (Get-ValueHash $manifest.source)){throw 'Bundle discovery provenance mismatch.'}
    Assert-LogicDiscoveryResources $target $p.parameters (Join-Path $Directory discovery)
    Test-LogicPackage (Join-Path $Directory application.zip)
    $stack=Get-Content (Join-Path $Directory stack.json) -Raw|ConvertFrom-Json -AsHashtable
    $template=Get-Content (Join-Path $Directory stack-template.json) -Raw|ConvertFrom-Json -AsHashtable
    Assert-StackContract $stack $target $template
    return @{receipt=$Receipt;target=$target;parameters=$p;stack=$stack;directory=$Directory;hash=(Get-ServiceHash (Join-Path $Directory bundle.json))}
}
function Assert-LogicChange($Change,$Bundle) {
    Assert-LogicParameters $Bundle.target $Bundle.parameters.parameters
    # Only reviewed CREATE exceptions for the two known storage roles. Changes to
    # existing network/security policy continue through the strict shared gate.
    $c=$Change|ConvertTo-Json -Depth 100|ConvertFrom-Json -AsHashtable
    if ($c.changeType -eq 'Create' -and $c.Contains('after') -and $c.after.Contains('properties')) {
        $prefix="/subscriptions/$($Bundle.target.subscriptionId)/resourceGroups/$($Bundle.target.resourceGroup)/providers/Microsoft.Storage/storageAccounts/"
        $stem="$($Bundle.target.workload)$($Bundle.target.environmentName)"
        $p=$c.after.properties
        if ($c.resourceId -match ('^'+[regex]::Escape($prefix+'stev'+$stem)+'[a-z0-9]+$')) {
            if ($p.publicNetworkAccess -cne 'Enabled' -or $p.networkAcls.defaultAction -cne 'Deny' -or $p.networkAcls.bypass -cne 'AzureServices' -or $p.allowSharedKeyAccess -ne $false -or $p.allowBlobPublicAccess -ne $false) { throw 'Event bridge storage differs from reviewed exception.' }
            foreach($key in @('ipRules','virtualNetworkRules')){if($p.networkAcls.Contains($key) -and @($p.networkAcls[$key]).Count){throw 'Additional bridge network access is not approved.'}}
            $p.publicNetworkAccess='Disabled'
        } elseif ($c.resourceId -match ('^'+[regex]::Escape($prefix+'strt'+$stem)+'[a-z0-9]+$')) {
            if ($p.publicNetworkAccess -cne 'Disabled' -or $p.networkAcls.bypass -cne 'None' -or $p.networkAcls.defaultAction -cne 'Deny') { throw 'Runtime credential exception requires private storage.' }
            $p.allowSharedKeyAccess=$false
        }
    }
    Assert-ServiceChange $c
}
function Test-LogicPrerequisites($Bundle,[switch]$AllowPlannedCreates) {
    $p=$Bundle.parameters.parameters
    foreach($provider in @('Microsoft.Web','Microsoft.Storage','Microsoft.EventGrid','Microsoft.Insights','Microsoft.OperationalInsights')) {
        $r=Invoke-ServiceJson @('provider','show','--namespace',$provider,'--subscription',$Bundle.target.subscriptionId)
        if($r.registrationState -ne 'Registered'){throw "Provider must be registered by platform: $provider"}
    }
    $resolved=Get-ServiceParameter $p prerequisitePlan @{}
    $skipNetwork=$AllowPlannedCreates -and $resolved.Count -and $resolved.createNetwork
    $network=@{}
    foreach($key in @('integrationSubnetId','privateEndpointSubnetId')){
        if($skipNetwork){continue}
        $s=Invoke-ServiceJson @('network','vnet','subnet','show','--ids',$p[$key].value)
        $delegations=@($s.delegations|ForEach-Object {$_.serviceName})
        if ($key -eq 'integrationSubnetId' -and 'Microsoft.Web/serverFarms' -notin $delegations) { throw 'Integration subnet must be delegated to Microsoft.Web/serverFarms.' }
        if ($key -eq 'privateEndpointSubnetId' -and $delegations.Count) { throw 'Private endpoint subnet cannot be delegated.' }
        $network[$key]=$s
    }
    $vnetId=$p.integrationSubnetId.value -replace '/subnets/[^/]+$',''
    if(!$skipNetwork){
    $vnet=Invoke-ServiceJson @('network','vnet','show','--ids',$vnetId)
    if ($vnet.location -cne $p.location.value -or @($vnet.dhcpOptions.dnsServers).Count) { throw 'V1 requires same-region VNet and Azure-provided DNS with directly linked zones.' }
    }
    foreach($zone in $p.privateDnsZoneIds.value.Values){
        if($AllowPlannedCreates -and $resolved.Count -and ($zone.Split('/')[-1] -in $resolved.createDnsZoneNames)){continue}
        $links=Invoke-ServiceJson @('rest','--method','get','--url',"https://management.azure.com$zone/virtualNetworkLinks?api-version=2024-06-01")
        if(!@($links.value|Where-Object {$_.properties.virtualNetwork.id -ieq $vnetId -and $_.properties.provisioningState -eq 'Succeeded'}).Count){throw 'Approved private DNS zone needs a VNet link.'}
    }
    if(!($AllowPlannedCreates -and $resolved.Count -and $resolved.createWorkspace)){$null=Invoke-ServiceJson @('resource','show','--ids',$p.existingLogAnalyticsWorkspaceId.value,'--api-version','2023-09-01')}
    return $network
}
function Get-LogicOutputs($Bundle) {
    $stack=Get-WorkloadStack $Bundle
    if (!$stack) { throw 'Logic App Foundation must exist.' }
    $o=$stack.properties.outputs
    foreach($key in @('logicAppName','logicAppResourceId','runtimeStorageAccountName','eventStorageAccountName','topicId','topicEndpoint','workspaceId')){if(!$o.Contains($key) -or !$o[$key].value){throw "Missing Logic App output: $key"}}
    $rg="/subscriptions/$($Bundle.target.subscriptionId)/resourceGroups/$($Bundle.target.resourceGroup)"
    if ($o.logicAppName.value -cnotmatch ('^logic-'+[regex]::Escape("$($Bundle.target.workload)-$($Bundle.target.environmentName)")+'-[a-z0-9]+$') -or $o.logicAppResourceId.value -ine "$rg/providers/Microsoft.Web/sites/$($o.logicAppName.value)" -or $o.topicId.value -ine "$rg/providers/Microsoft.EventGrid/topics/evgt-$($Bundle.target.workload)-$($Bundle.target.environmentName)") { throw 'Logic App outputs belong to another target.' }
    foreach($pair in @(@('runtimeStorageAccountName','strt'),@('eventStorageAccountName','stev'))){
        $name=$o[$pair[0]].value
        $prefix=$pair[1]+$Bundle.target.workload+$Bundle.target.environmentName
        if($name.Length -gt 24 -or $name -cnotmatch ('^'+[regex]::Escape($prefix)+'[a-z0-9]+$')){throw 'Storage output does not belong to this target.'}
        $account=Invoke-ServiceJson @('resource','show','--ids',"$rg/providers/Microsoft.Storage/storageAccounts/$name",'--api-version','2025-01-01')
        if($account.id -ine "$rg/providers/Microsoft.Storage/storageAccounts/$name"){throw 'Storage ownership mismatch.'}
    }
    if($o.workspaceId.value -ine $Bundle.parameters.parameters.existingLogAnalyticsWorkspaceId.value){throw 'Monitoring workspace mismatch.'}
    $topic=Invoke-ServiceJson @('resource','show','--ids',$o.topicId.value,'--api-version','2025-02-15')
    if ($topic.properties.endpoint -cne $o.topicEndpoint.value) { throw 'Topic endpoint mismatch.' }
    return $o
}
function Get-LogicWorkflowState($Outputs) {
    $uri="https://management.azure.com$($Outputs.logicAppResourceId.value)/hostruntime/runtime/webhooks/workflow/api/management/workflows?api-version=2024-04-01"
    $report=Invoke-ServiceJson @('rest','--method','get','--url',$uri)
    if(!$report.Contains('value')){throw 'Workflow inventory is incomplete.'}
    $names=@($report.value|ForEach-Object {$_.name.Split('/')[-1]}|Sort-Object)
    if(@($names|Where-Object {$_ -cne 'process-event'}).Count -or $names.Count -gt 1){throw 'Unexpected deployed workflow; content replacement/removal requires platform review.'}
    $hashes=@{}
    if($names.Count){
        foreach($file in @('host.json','connections.json','parameters.json','process-event/workflow.json')){
            $content=Invoke-LogicDataRequest "https://$($Outputs.logicAppName.value).scm.azurewebsites.net/api/vfs/site/wwwroot/$file" GET 'https://management.azure.com/'
            $content=$content|ConvertTo-Json -Depth 100|ConvertFrom-Json -AsHashtable
            $hashes[$file]=Get-ValueHash $content
        }
    }
    return @{names=$names;contentHashes=$hashes}
}
function New-LogicPlan($Bundle,[string]$Phase,[string]$Directory) {
    New-Item -ItemType Directory -Path $Directory -Force|Out-Null
    $state=Get-WorkloadStackState $Bundle
    Assert-LogicPrerequisiteLiveState $Bundle $state
    $network=Test-LogicPrerequisites $Bundle -AllowPlannedCreates:(!$state.stackExists -and $Phase -eq 'Foundation')
    $state.network=$network
    $skip=$Phase -eq 'Foundation' -and $state.hasApp
    $p=$Bundle.parameters|ConvertTo-Json -Depth 100|ConvertFrom-Json -AsHashtable
    $p.parameters.releaseActivated=@{value=($Phase -eq 'Release')}
    $p.parameters.workloadResourceGroupName=@{value=$Bundle.target.resourceGroup}
    $path=Join-Path $Directory effective.parameters.json;Write-ServiceJson $p $path
    $outputs=if($state.stackExists){Get-LogicOutputs $Bundle}else{@{}}
    if($Phase -eq 'Release' -and !$state.stackExists){throw 'Release needs Foundation.'}
    if($Phase -eq 'Release'){Wait-LogicConnectivity $outputs;$state.workflow=Get-LogicWorkflowState $outputs}
    $report=if($skip){@{status='Succeeded';changes=@()}}else{New-StackPreview $Bundle $state $path $Directory}
    $changes=Get-ServiceChanges $report $Bundle
    Write-ServiceJson $report (Join-Path $Directory what-if.json)
    $plan=@{schemaVersion=1;phase=$Phase;skip=$skip;bundleHash=$Bundle.hash;parametersHash=(Get-ServiceHash $path);state=$state;outputs=$outputs;changes=$changes;createdUtc=[DateTimeOffset]::UtcNow.ToString('O')}
    $plan.fingerprint=Get-ValueHash @{bundleHash=$plan.bundleHash;parametersHash=$plan.parametersHash;state=$state;outputs=$outputs;changes=$changes;phase=$Phase;skip=$skip}
    Write-ServiceJson $plan (Join-Path $Directory plan.json)
    $cost=Get-Content (Join-Path $Bundle.directory cost-estimate.json) -Raw|ConvertFrom-Json -AsHashtable
    @("# Logic App $Phase preview",'',"Stack: $($Bundle.stack.stackId)","Template Spec: $($Bundle.stack.templateSpecId)","Workflow package SHA256: $($Bundle.receipt.files['application.zip'])",'Expected package: host.json, connections.json, parameters.json, process-event/workflow.json. Workflow content is in application.zip, not ARM What-If.','Release can interrupt workflow execution while content is replaced. Automatic rollback is not implemented.',"Cost status: $($cost.status); fixed monthly subtotal USD: $($cost.fixedMonthlySubtotalUsd). Usage is additional.",'Review both approved exception references in parameters.json. Plan expires after 24 hours.')|Set-Content (Join-Path $Directory summary.md)
    return $plan
}
function Wait-LogicConnectivity($Outputs) {
    foreach($hostName in @("$($Outputs.logicAppName.value).scm.azurewebsites.net",([uri]$Outputs.topicEndpoint.value).Host,"$($Outputs.eventStorageAccountName.value).blob.core.windows.net","$($Outputs.eventStorageAccountName.value).queue.core.windows.net","$($Outputs.runtimeStorageAccountName.value).blob.core.windows.net","$($Outputs.runtimeStorageAccountName.value).file.core.windows.net","$($Outputs.runtimeStorageAccountName.value).queue.core.windows.net","$($Outputs.runtimeStorageAccountName.value).table.core.windows.net")){
        $addresses=@([Net.Dns]::GetHostAddresses($hostName)|ForEach-Object {$_.ToString()})
        if (!$addresses.Count -or @($addresses|Where-Object {!(Test-ServicePrivateAddress $_)}).Count){throw "Private DNS resolution failed: $hostName"}
    }
}
function Invoke-LogicDataRequest([string]$Uri,[string]$Method,[string]$Audience,$Body=$null,[string]$InFile='') {
    $token=Invoke-ServiceJson @('account','get-access-token','--resource',$Audience)
    $args=@{Uri=$Uri;Method=$Method;Headers=@{Authorization="Bearer $($token.accessToken)"};ErrorAction='Stop';TimeoutSec=300;MaximumRedirection=0}
    if($InFile){$args.InFile=$InFile;$args.ContentType='application/zip'}elseif($null -ne $Body){$args.Body=(ConvertTo-Json -InputObject $Body -Depth 100 -Compress);$args.ContentType='application/json'}
    # Never log tokens, response bodies or credential-bearing request data.
    try { return Invoke-RestMethod @args } catch { throw "Logic App data operation failed: $Method $(([uri]$Uri).Host). Inspect the service operation status." }
}
function Publish-LogicPackage($Bundle,$Outputs,[string]$Directory) {
    $name=$Outputs.logicAppName.value; $base="https://$name.scm.azurewebsites.net"
    $package=Join-Path $Bundle.directory application.zip;Test-LogicPackage $package
    if((Get-ServiceHash $package) -cne $Bundle.receipt.files['application.zip']){throw 'Package changed after approval.'}
    # Synchronous ZipDeploy avoids following an untrusted Location header.
    $null=Invoke-LogicDataRequest "$base/api/zipdeploy?isAsync=false" POST 'https://management.azure.com/' -InFile $package
    $zip=[IO.Compression.ZipFile]::OpenRead($package)
    try {
        foreach($entry in $zip.Entries){
            $reader=[IO.StreamReader]::new($entry.Open());try{$expected=$reader.ReadToEnd()|ConvertFrom-Json -AsHashtable}finally{$reader.Dispose()}
            $actual=Invoke-LogicDataRequest "$base/api/vfs/site/wwwroot/$($entry.FullName)" GET 'https://management.azure.com/'
            $actual=$actual|ConvertTo-Json -Depth 100|ConvertFrom-Json -AsHashtable
            if((Get-ValueHash $expected) -cne (Get-ValueHash $actual)){throw 'Deployed workflow content mismatch.'}
        }
    } finally {$zip.Dispose()}
    $inventory=$null
    for($attempt=0;$attempt -lt 12;$attempt++){
        try{$inventory=Get-LogicWorkflowState $Outputs;if($inventory.names.Count -eq 1){break}}catch{if($attempt -eq 11){throw}}
        if($attempt -lt 11){Start-Sleep -Seconds 10}
    }
    if(!$inventory -or $inventory.names.Count -ne 1 -or $inventory.names[0] -cne 'process-event'){throw 'Expected workflow is not indexed.'}
    Write-ServiceJson $inventory (Join-Path $Directory workflow-inventory.json)
    Write-ServiceJson @{packageSha256=$Bundle.receipt.files['application.zip'];verified=$true;expectedWorkflows=@('process-event')} (Join-Path $Directory workflow-package.json)
}
function Invoke-LogicSmoke($Bundle,$Outputs,[string]$Directory) {
    $id=[guid]::NewGuid().ToString();$correlation=[guid]::NewGuid().ToString()
    $event=@{id=$id;eventType='Document.Received';subject='/documents/smoke';eventTime=[DateTimeOffset]::UtcNow.ToString('O');dataVersion='1';data=@{documentId='synthetic-pipeline-probe';correlationId=$correlation}}
    $null=Invoke-LogicDataRequest $Outputs.topicEndpoint.value POST 'https://eventgrid.azure.net/' -Body @($event)
    $deadline=[DateTimeOffset]::UtcNow.AddMinutes(5);$receipt=$null
    $temp=Join-Path $Directory smoke-receipt.json
    do {
        $exists=Invoke-ServiceJson @('storage','blob','exists','--account-name',$Outputs.eventStorageAccountName.value,'--container-name','receipts','--name',"$id.json",'--auth-mode','login')
        if($exists.exists){$null=Invoke-ServiceJson @('storage','blob','download','--account-name',$Outputs.eventStorageAccountName.value,'--container-name','receipts','--name',"$id.json",'--file',$temp,'--auth-mode','login','--overwrite');$receipt=Get-Content $temp -Raw|ConvertFrom-Json -AsHashtable;break}
        Start-Sleep -Seconds 10
    } while([DateTimeOffset]::UtcNow -lt $deadline)
    if(!$receipt -or $receipt.eventId -cne $id -or $receipt.correlationId -cne $correlation -or $receipt.topic -ine $Outputs.topicId.value){throw 'Current-run Logic App smoke receipt missing or mismatched.'}
    $smoke=@{passed=$true;eventId=$id;correlationId=$correlation;topicId=$Outputs.topicId.value;packageSha256=$Bundle.receipt.files['application.zip'];verifiedUtc=[DateTimeOffset]::UtcNow.ToString('O')}
    Write-ServiceJson $smoke (Join-Path $Directory smoke.json);return $smoke
}
function Invoke-LogicApply($Bundle,[string]$Phase,[string]$PlanDirectory,[string]$Directory) {
    $approved=Get-Content (Join-Path $PlanDirectory plan.json) -Raw|ConvertFrom-Json -AsHashtable
    $current=New-LogicPlan $Bundle $Phase (Join-Path $Directory recheck)
    Assert-ServicePlan $Bundle $approved $current $Phase
    if(!$current.skip){$null=Invoke-WorkloadStackApply $Bundle (Join-Path $Directory recheck/effective.parameters.json) $Directory $current}
    $outputs=Get-LogicOutputs $Bundle;Write-ServiceJson $outputs (Join-Path $Directory outputs.json)
    Wait-LogicConnectivity $outputs
    if($Phase -eq 'Foundation'){return @{ready=$false;status='FoundationReady';outputs=$outputs}}
    Publish-LogicPackage $Bundle $outputs $Directory
    $smoke=Invoke-LogicSmoke $Bundle $outputs $Directory
    if($smoke.passed -isnot [bool] -or !$smoke.passed){throw 'Logic App smoke did not establish readiness.'}
    return @{ready=$true;status='Ready';outputs=$outputs;smoke=$smoke}
}

function Protect-LogicEvidence($Value) {
    if($Value -is [Collections.IDictionary]){
        $copy=@{};foreach($key in $Value.Keys){$copy[$key]=Protect-LogicEvidence $Value[$key]};return $copy
    }
    if($Value -is [string]){
        if(!$Value.StartsWith('[') -and $Value -match '(?i)AccountKey=([^;]+)'){
            $fingerprint=Get-ValueHash $Matches[1]
            return [regex]::Replace($Value,'(?i)AccountKey=[^;]+',"AccountKey=[redacted-sha256:$fingerprint]")
        }
        return $Value
    }
    if($Value -is [Collections.IEnumerable]){
        $items=@(foreach($item in $Value){Protect-LogicEvidence $item});return ,$items
    }
    return $Value
}
function Assert-LogicDiscoveryResources($Target,$P,[string]$Directory) {
    $inventory=Get-Content (Join-Path $Directory inventory.json) -Raw|ConvertFrom-Json -AsHashtable
    if((Get-ServiceParameter $P prerequisitePlan @{}).Count){$null=Assert-LogicResolvedPrerequisites $Target $P $inventory;return}
    foreach($key in @('integrationSubnetId','privateEndpointSubnetId')){
        if(@($inventory.networks|ForEach-Object {$_.subnets}|Where-Object {$_.id -ieq $P[$key].value}).Count -ne 1){throw 'Selected subnet is missing or ambiguous in saved discovery.'}
    }
    foreach($id in $P.privateDnsZoneIds.value.Values){
        if($id.StartsWith("/subscriptions/$($Target.subscriptionId)/",[StringComparison]::OrdinalIgnoreCase) -and @($inventory.privateDnsZones|Where-Object {$_.id -ieq $id}).Count -ne 1){throw 'Selected DNS zone is missing or ambiguous in saved discovery.'}
    }
    $workspace=$P.existingLogAnalyticsWorkspaceId.value
    if($workspace.StartsWith("/subscriptions/$($Target.subscriptionId)/",[StringComparison]::OrdinalIgnoreCase) -and @($inventory.resources|Where-Object {$_.id -ieq $workspace}).Count -ne 1){throw 'Selected workspace is missing or ambiguous in saved discovery.'}
}
