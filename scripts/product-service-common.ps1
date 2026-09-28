# Reviewed product adapter. No executable paths are accepted from developer requests or artifacts.
function Get-ProductDnsNames {
    return @{blob='privatelink.blob.core.windows.net';queue='privatelink.queue.core.windows.net';table='privatelink.table.core.windows.net';sites='privatelink.azurewebsites.net';vault='privatelink.vaultcore.azure.net';servicebus='privatelink.servicebus.windows.net'}
}
function Get-ProductRequiredParameters([string]$Type) {
    $d=Get-WorkloadDefinition $Type
    $keys=@('workload','environmentName','location','owner','costCenter','existingLogAnalyticsWorkspaceId','deploymentPrincipalObjectId')
    if($d.dnsKeys.Count){$keys+=@('privateEndpointSubnetId','privateDnsZoneIds')}
    if($d.packageKind -eq 'productFunctions'){$keys+='integrationSubnetId'}
    switch($Type){'private-storage'{$keys+='consumerPrincipalObjectId'} 'key-vault'{$keys+='readerPrincipalObjectId'} 'observability'{$keys+='alertActionGroupIds'} 'http-functions'{$keys+=@('apiClientId','allowedClientApplications')}}
    return $keys
}
function Assert-ProductParameters($Target,$P) {
    Assert-CustomTags (Get-ServiceParameter $P customTags)
    $type=Get-TargetWorkloadType $Target;$d=Get-WorkloadDefinition $type
    $keys=Get-ProductRequiredParameters $type
    if(@($P.Keys|Where-Object {$_ -notin ($keys+@('releaseActivated','packageBlobName','customTags'))}).Count){throw 'Unsupported product parameter.'}
    foreach($k in $keys){if(!$P.Contains($k) -or $null -eq $P[$k].value -or [string]::IsNullOrWhiteSpace([string]$P[$k].value)){throw "Platform onboarding required: $k"}}
    if((ConvertTo-Canonical $P) -match 'REPLACE_|00000000-0000-0000-0000-000000000000'){throw 'Complete reviewed product onboarding settings.'}
    if($P.workload.value -cne $Target.workload -or $P.environmentName.value -cne $Target.environmentName -or $P.location.value -cnotin @('eastus2')){throw 'Product intent mismatch.'}
    foreach($k in @('deploymentPrincipalObjectId','consumerPrincipalObjectId','readerPrincipalObjectId','apiClientId')|Where-Object {$P.Contains($_)}){if([guid]::Parse($P[$k].value) -eq [guid]::Empty){throw "Invalid principal: $k"}}
    $scope='^/subscriptions/'+[regex]::Escape($Target.subscriptionId)+'/resourceGroups/[^/]+/providers/'
    if($P.existingLogAnalyticsWorkspaceId.value -notmatch ($scope+'Microsoft.OperationalInsights/workspaces/[^/]+$')){throw 'Monitoring must use an approved same-subscription workspace.'}
    if($d.dnsKeys.Count){
        $subnet=$P.privateEndpointSubnetId.value
        if($subnet -notmatch ($scope+'Microsoft.Network/virtualNetworks/[^/]+/subnets/[^/]+$')){throw 'Private endpoint subnet must be in the registered subscription.'}
        $zones=$P.privateDnsZoneIds.value;$names=Get-ProductDnsNames
        if($zones -isnot [Collections.IDictionary] -or $zones.Count -ne $d.dnsKeys.Count){throw 'Supply exactly the product DNS bindings.'}
        foreach($k in $d.dnsKeys){if(!$zones.Contains($k) -or $zones[$k] -notmatch ($scope+'Microsoft.Network/privateDnsZones/'+[regex]::Escape($names[$k])+'$')){throw "Invalid DNS binding: $k"}}
        if($d.packageKind -eq 'productFunctions'){
            if($P.integrationSubnetId.value -notmatch ($scope+'Microsoft.Network/virtualNetworks/[^/]+/subnets/[^/]+$') -or $P.integrationSubnetId.value -ieq $subnet -or ($P.integrationSubnetId.value -replace '/subnets/[^/]+$','') -ine ($subnet -replace '/subnets/[^/]+$','')){throw 'Use distinct integration and endpoint subnets in the same reviewed VNet.'}
        }
    }
    if($type -eq 'http-functions'){
        if($P.allowedClientApplications.value -isnot [array] -or !$P.allowedClientApplications.value.Count){throw 'HTTP API needs explicit allowed Entra client application IDs.'}
        foreach($id in $P.allowedClientApplications.value){if([guid]::Parse($id) -eq [guid]::Empty){throw 'Invalid allowed API client.'}}
    }
    if($type -eq 'observability'){
        if($P.alertActionGroupIds.value -isnot [array] -or !$P.alertActionGroupIds.value.Count){throw 'Configure a reviewed action group; no notification destination is invented.'}
        foreach($id in $P.alertActionGroupIds.value){if($id -notmatch ($scope+'Microsoft.Insights/actionGroups/[^/]+$')){throw 'Invalid action group.'}}
    }
    if($P.Contains('releaseActivated') -and $P.releaseActivated.value -isnot [bool]){throw 'Invalid lifecycle flag.'}
    if($P.Contains('packageBlobName') -and $P.packageBlobName.value -cnotmatch '^releases/[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}\.zip$'){throw 'Invalid application package path.'}
}
function Assert-ProductDiscovery($Target,$P,[string]$Directory) {
    Assert-ProductParameters $Target $P
    $i=Get-Content (Join-Path $Directory inventory.json) -Raw|ConvertFrom-Json -AsHashtable
    $d=Get-WorkloadDefinition (Get-TargetWorkloadType $Target)
    if($i.resourceQuery.status -cne 'Succeeded' -or $i.networkQuery.status -cne 'Succeeded' -or $i.privateDnsQuery.status -cne 'Succeeded'){throw 'Failed discovery does not establish empty resources.'}
    foreach($ns in $d.providers){$found=@($i.providers|Where-Object {$_.namespace -ceq $ns -and $_.registrationState -eq 'Registered'});if($found.Count -ne 1){throw "Provider not registered or unknown: $ns"}}
    foreach($id in @($P.existingLogAnalyticsWorkspaceId.value)+@($(if($P.Contains('alertActionGroupIds')){$P.alertActionGroupIds.value}))){
        if(@($i.resources|Where-Object {$_.id -ieq $id}).Count -ne 1){throw "Shared dependency missing from discovery: $id. Platform must provision/approve it first."}
    }
    if($d.dnsKeys.Count){
        foreach($k in @('privateEndpointSubnetId','integrationSubnetId')|Where-Object {$P.Contains($_)}){
            $nets=@($i.networks|Where-Object {$_.location -ieq $P.location.value -and $_.subnetQuery.status -eq 'Succeeded'})
            $subnets=@($nets|ForEach-Object {$_.subnets}|Where-Object {$_.id -ieq $P[$k].value})
            if($subnets.Count -ne 1){throw "Required subnet missing or incomplete: $k"}
            if($k -eq 'integrationSubnetId' -and !$subnets[0].integrationCandidate){throw 'Integration subnet is ineligible.'}
            if($k -eq 'privateEndpointSubnetId' -and !$subnets[0].privateEndpointCandidate){throw 'Private endpoint subnet is ineligible.'}
        }
        foreach($id in $P.privateDnsZoneIds.value.Values){if(@($i.privateDnsZones|Where-Object {$_.id -ieq $id}).Count -ne 1){throw 'Required shared DNS zone missing; platform onboarding must supply it.'}}
    }
}
function Test-ProductPrerequisites($Bundle) {
    $p=$Bundle.parameters.parameters;$d=Get-WorkloadDefinition (Get-TargetWorkloadType $Bundle.target)
    $evidence=@{}
    foreach($id in @($p.existingLogAnalyticsWorkspaceId.value)+@($(if($p.Contains('alertActionGroupIds')){$p.alertActionGroupIds.value}))){
        $version=if($id -match '/actionGroups/'){'2023-01-01'}else{'2023-09-01'}
        $r=Invoke-ServiceJson @('resource','show','--ids',$id,'--api-version',$version)
        if($r.id -ine $id){throw 'Shared dependency identity mismatch.'};$evidence[$id]=$r
    }
    if($d.dnsKeys.Count){
        $vnetId=$p.privateEndpointSubnetId.value -replace '/subnets/[^/]+$',''
        $vnet=Invoke-ServiceJson @('resource','show','--ids',$vnetId,'--api-version','2024-05-01')
        if($vnet.location -ine $p.location.value){throw 'Network region differs from workload.'}
        # This initial profile supports Azure-provided DNS with direct links only. Hub resolver profiles need separate qualification.
        if($vnet.properties.Contains('dhcpOptions') -and @($vnet.properties.dhcpOptions.dnsServers).Count){throw 'Custom DNS requires a qualified resolver profile; direct-link pilot cannot infer reachability.'}
        foreach($k in @('privateEndpointSubnetId','integrationSubnetId')|Where-Object {$p.Contains($_)}){
            $s=Invoke-ServiceJson @('resource','show','--ids',$p[$k].value,'--api-version','2024-05-01');$props=$s.properties
            $delegations=@($(if($props.Contains('delegations')){$props.delegations|ForEach-Object {$_.properties.serviceName}}))
            if($k -eq 'privateEndpointSubnetId' -and ($delegations.Count -or $props.privateEndpointNetworkPolicies -ne 'Disabled')){throw 'Endpoint subnet policies changed.'}
            if($k -eq 'integrationSubnetId'){
                $cidrs=if($props.Contains('addressPrefixes')){@($props.addressPrefixes)}else{@($props.addressPrefix)}
                if($delegations.Count -ne 1 -or $delegations[0] -cne 'Microsoft.Web/serverFarms' -or !@($cidrs|Where-Object {$_ -match '/(\d+)$' -and [int]$Matches[1] -le 26}).Count -or ($props.Contains('privateEndpoints') -and @($props.privateEndpoints).Count)){throw 'Integration subnet delegation/capacity profile changed.'}
            }
            $evidence[$k]=$props
        }
        foreach($id in $p.privateDnsZoneIds.value.Values){
            $links=Get-StackRestCollection "https://management.azure.com$id/virtualNetworkLinks?api-version=2024-06-01"
            if(@($links|Where-Object {$_.properties.virtualNetwork.id -ieq $vnetId -and $_.properties.provisioningState -eq 'Succeeded'}).Count -ne 1){throw 'Required private DNS link missing or not ready.'}
            $evidence[$id]=$links
        }
    }
    return $evidence
}
function Get-ProductCostEstimate($Target,$P) {
    $d=Get-WorkloadDefinition (Get-TargetWorkloadType $Target)
    return @{status='Unavailable';currency='USD';region=$P.location.value;pricingAsOf=$null;fixedMonthlySubtotalUsd=$null;reason='A complete reviewed price snapshot for this offering is not yet configured. This does not mean zero cost.';exclusions=@($d.costDescription);costDrivers=$d.costDescription}
}
function New-ProductBundle($Target,[string]$ReleaseDirectory,[string]$Directory,[string]$DiscoveryDirectory) {
    if(!(Get-Command Read-DiscoveryManifest -ErrorAction SilentlyContinue)){. "$PSScriptRoot/discovery-manifest-common.ps1"}
    $d=Get-WorkloadDefinition (Get-TargetWorkloadType $Target)
    $r=Get-Content (Join-Path $ReleaseDirectory release.json) -Raw|ConvertFrom-Json -AsHashtable
    $commit=& git -C (Get-ProjectRoot) rev-parse HEAD
    if($LASTEXITCODE -ne 0 -or $r.workloadType -cne (Get-TargetWorkloadType $Target) -or $r.sourceCommit -cne $commit -or $r.dirtyWorktree -isnot [bool] -or $r.dirtyWorktree){throw 'Clean qualified product release required.'}
    if(!(Get-Command New-WorkloadPreviewInputs -ErrorAction SilentlyContinue)){. "$PSScriptRoot/workload-preview-common.ps1"}
    New-WorkloadPreviewInputs $Target $DiscoveryDirectory $Directory $r.releaseId
    Write-ServiceJson $d (Join-Path $Directory workload-definition.json)
    $files=@{};$expected=@('main.json','parameters.json','target.json','stack-template.json','stack.json','workload-definition.json','cost-estimate.json','discovery/manifest.json','discovery/inventory.json')
    if($d.packageKind -eq 'productFunctions'){
        $zip=Resolve-ServicePath $ReleaseDirectory "$($r.releaseId).zip"
        if((Get-ServiceHash $zip) -cne $r.packageSha256){throw 'Product application hash mismatch.'}
        Test-ProductPackage $zip
        Copy-Item $zip (Join-Path $Directory application.zip);$expected+='application.zip'
    }elseif($r.packageKind -cne 'infrastructure'){throw 'Infrastructure-only receipt must not pretend to contain an application.'}
    foreach($f in $expected){$files[$f]=Get-ServiceHash (Join-Path $Directory $f)}
    $m=Read-DiscoveryManifest (Join-Path $Directory discovery) $Target $Target.serviceConnection
    Write-ServiceJson @{schemaVersion=3;workloadType=(Get-TargetWorkloadType $Target);deploymentEngine='deploymentStack';releaseId=$r.releaseId;sourceCommit=$r.sourceCommit;files=$files;discoverySource=$m.source} (Join-Path $Directory bundle.json)
    $null=Read-ProductBundle $Directory (Get-Content (Join-Path $Directory bundle.json) -Raw|ConvertFrom-Json -AsHashtable)
}
function Read-ProductBundle([string]$Directory,$Receipt) {
    if(!(Get-Command Read-DiscoveryManifest -ErrorAction SilentlyContinue)){. "$PSScriptRoot/discovery-manifest-common.ps1"}
    $d=Get-WorkloadDefinition $Receipt.workloadType
    if($d.adapter -cne 'product' -or $Receipt.deploymentEngine -cne 'deploymentStack' -or $Receipt.releaseId -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$'){throw 'Invalid product release.'}
    $files=@('main.json','parameters.json','target.json','stack-template.json','stack.json','workload-definition.json','cost-estimate.json','discovery/manifest.json','discovery/inventory.json')
    if($d.packageKind -eq 'productFunctions'){$files+='application.zip'}
    if($Receipt.files.Count -ne $files.Count){throw 'Unexpected product bundle file set.'}
    foreach($f in $files){if(!$Receipt.files.Contains($f) -or (Get-ServiceHash (Resolve-ServicePath $Directory $f)) -cne $Receipt.files[$f]){throw "Product bundle integrity failure: $f"}}
    $frozen=Get-Content (Join-Path $Directory workload-definition.json) -Raw|ConvertFrom-Json -AsHashtable
    if((Get-ValueHash $frozen) -cne (Get-ValueHash $d)){throw 'Product adapter contract changed.'}
    $t=Get-Content (Join-Path $Directory target.json) -Raw|ConvertFrom-Json -AsHashtable
    Assert-ServiceTarget $t $t.workload $t.environmentName
    if((Get-TargetWorkloadType $t) -cne $Receipt.workloadType){throw 'Cross-product bundle rejected.'}
    $current=Read-ServiceTarget $t.workload $t.environmentName $t.subscriptionAlias $t.networkProfile
    if((Get-ValueHash $t) -cne (Get-ValueHash $current)){throw 'Frozen target no longer matches registered configuration.'}
    $p=Get-Content (Join-Path $Directory parameters.json) -Raw|ConvertFrom-Json -AsHashtable
    Assert-ProductParameters $t $p.parameters
    foreach($k in $t.parameterOverrides.Keys){if((Get-ValueHash $p.parameters[$k].value) -cne (Get-ValueHash $t.parameterOverrides[$k])){throw 'Frozen product profile mismatch.'}}
    $m=Read-DiscoveryManifest (Join-Path $Directory discovery) $t $t.serviceConnection
    if((Get-ValueHash $m.source) -cne (Get-ValueHash $Receipt.discoverySource)){throw 'Product discovery provenance mismatch.'}
    Assert-ProductDiscovery $t $p.parameters (Join-Path $Directory discovery)
    if($d.packageKind -eq 'productFunctions'){Test-ProductPackage (Join-Path $Directory application.zip)}
    $stack=Get-Content (Join-Path $Directory stack.json) -Raw|ConvertFrom-Json -AsHashtable
    Assert-StackContract $stack $t (Get-Content (Join-Path $Directory stack-template.json) -Raw|ConvertFrom-Json -AsHashtable)
    return @{receipt=$Receipt;target=$t;parameters=$p;stack=$stack;directory=$Directory;hash=(Get-ServiceHash (Join-Path $Directory bundle.json))}
}
function Test-ProductPackage([string]$Path) {
    $zip=[IO.Compression.ZipFile]::OpenRead($Path)
    try {
        foreach($name in @('host.json','functions.metadata','ProductFunctions.dll')){if(!$zip.GetEntry($name)){throw "Product package lacks $name"}}
        foreach($entry in $zip.Entries){if($entry.FullName -match '(^|[/\\])\.\.([/\\]|$)|local.settings.json' -or [IO.Path]::IsPathRooted($entry.FullName)){throw 'Unsafe package member.'}}
        $reader=[IO.StreamReader]::new($zip.GetEntry('functions.metadata').Open());try{$metadata=$reader.ReadToEnd()|ConvertFrom-Json -AsHashtable}finally{$reader.Dispose()}
        if(@($metadata).Count -ne 2 -or @($metadata|Where-Object {$_.name -eq 'Health' -and @($_.bindings|Where-Object {$_.type -eq 'httpTrigger'}).Count -eq 1}).Count -ne 1 -or @($metadata|Where-Object {$_.name -eq 'ProcessWork' -and @($_.bindings|Where-Object {$_.type -eq 'serviceBusTrigger'}).Count -eq 1}).Count -ne 1){throw 'Wrong product Functions metadata.'}
    }finally{$zip.Dispose()}
}
function Get-ProductOutputs($Bundle) {
    $s=Get-WorkloadStack $Bundle;if(!$s){throw 'Product stack not provisioned.'};$o=$s.properties.outputs
    $d=Get-WorkloadDefinition (Get-TargetWorkloadType $Bundle.target)
    $expected=switch($d.menuSlug){storage{@('storageResourceId')} keyvault{@('vaultResourceId')} observe{@('workbookId','alertId')} httpapi{@('storageResourceId')} busworker{@('storageResourceId','busResourceId')}}
    foreach($k in $expected){if(!$o.Contains($k) -or !$o[$k].value){throw "Missing product output $k"};Assert-StackManagedId $Bundle $o[$k].value}
    if($o.workspaceId.value -ine $Bundle.parameters.parameters.existingLogAnalyticsWorkspaceId.value){throw 'Unexpected shared workspace.'}
    # Validate data-plane host names before sending tokens; ARM outputs alone are not trusted URLs.
    foreach($pair in @(@('storageAccountName','storageResourceId','Microsoft.Storage/storageAccounts'),@('vaultName','vaultResourceId','Microsoft.KeyVault/vaults'),@('busName','busResourceId','Microsoft.ServiceBus/namespaces'),@('functionAppName','functionAppId','Microsoft.Web/sites'))){
        if($o.Contains($pair[0]) -and $o[$pair[0]].value){
            $name=$o[$pair[0]].value
            if($name -cnotmatch '^[a-z0-9][a-z0-9-]{1,59}[a-z0-9]$' -or $o[$pair[1]].value -ine "/subscriptions/$($Bundle.target.subscriptionId)/resourceGroups/$($Bundle.target.resourceGroup)/providers/$($pair[2])/$name"){throw 'Product output endpoint identity mismatch.'}
        }
    }
    return $o
}
function New-ProductPlan($Bundle,[string]$Phase,[string]$Directory) {
    New-Item -ItemType Directory -Path $Directory -Force|Out-Null
    $d=Get-WorkloadDefinition (Get-TargetWorkloadType $Bundle.target);$state=Get-WorkloadStackState $Bundle
    $state.network=Test-ProductPrerequisites $Bundle
    if($Phase -eq 'Release' -and $d.packageKind -eq 'productFunctions' -and !$state.stackExists){throw 'Application release requires Foundation.'}
    $skip=$Phase -eq 'Foundation' -and $state.hasApp
    $p=$Bundle.parameters|ConvertTo-Json -Depth 100|ConvertFrom-Json -AsHashtable
    $p.parameters.releaseActivated=@{value=($Phase -eq 'Release')};$p.parameters.workloadResourceGroupName=@{value=$Bundle.target.resourceGroup}
    if($d.packageKind -eq 'productFunctions'){$p.parameters.packageBlobName=@{value="releases/$($Bundle.receipt.releaseId).zip"}}
    $path=Join-Path $Directory effective.parameters.json;Write-ServiceJson $p $path
    $report=if($skip){@{status='Succeeded';changes=@()}}else{New-StackPreview $Bundle $state $path $Directory}
    $plan=@{schemaVersion=1;phase=$Phase;skip=$skip;bundleHash=$Bundle.hash;parametersHash=(Get-ServiceHash $path);state=$state;outputs=@{};changes=(Get-ServiceChanges $report $Bundle);createdUtc=[DateTimeOffset]::UtcNow.ToString('O')}
    $plan.fingerprint=Get-ValueHash @{bundleHash=$plan.bundleHash;parametersHash=$plan.parametersHash;state=$plan.state;outputs=$plan.outputs;changes=$plan.changes;phase=$Phase;skip=$skip}
    Write-ServiceJson $plan (Join-Path $Directory plan.json)
    @("# $($d.displayName) $Phase plan",'',"Stack: $($Bundle.stack.stackId)","Template Spec: $($Bundle.stack.templateSpecId)",'Review property changes and shared prerequisite state. No deletion/adoption or automatic address allocation is authorized.','Cost estimate is unavailable until all meters are reviewed; it is not zero.','InfrastructureReady denotes control-plane verification only. Runtime products additionally require their smoke test.','This plan expires after 24 hours.')|Set-Content (Join-Path $Directory summary.md)
    return $plan
}
function Wait-ProductPrivateHost([string]$HostName) {
    $addresses=@([Net.Dns]::GetHostAddresses($HostName)|ForEach-Object {$_.IPAddressToString})
    if(!$addresses.Count -or @($addresses|Where-Object {!(Test-ServicePrivateAddress $_)}).Count){throw "Endpoint does not resolve exclusively to private addresses: $HostName"}
}
function Test-ProductInfrastructure($Bundle,$Outputs) {
    $type=Get-TargetWorkloadType $Bundle.target
    $checks=@()
    if($Outputs.Contains('storageResourceId')){
        $s=Invoke-ServiceJson @('resource','show','--ids',$Outputs.storageResourceId.value,'--api-version','2025-01-01')
        if($s.properties.publicNetworkAccess -cne 'Disabled' -or $s.properties.allowSharedKeyAccess -ne $false -or $s.properties.allowBlobPublicAccess -ne $false){throw 'Storage security acceptance failed.'}
        foreach($service in $(if($type -eq 'private-storage'){@('blob','queue')}else{@('blob','queue','table')})){Wait-ProductPrivateHost "$($Outputs.storageAccountName.value).$service.core.windows.net"}
        $checks+='Storage private DNS and identity-only configuration'
    }
    if($type -eq 'key-vault'){
        $v=Invoke-ServiceJson @('resource','show','--ids',$Outputs.vaultResourceId.value,'--api-version','2025-05-01')
        if($v.properties.publicNetworkAccess -cne 'Disabled' -or $v.properties.enablePurgeProtection -ne $true -or $v.properties.enableRbacAuthorization -ne $true){throw 'Key Vault security acceptance failed.'}
        Wait-ProductPrivateHost "$($Outputs.vaultName.value).vault.azure.net";$checks+='Vault private DNS, RBAC and purge protection'
    }
    if($type -eq 'observability'){
        $a=Invoke-ServiceJson @('resource','show','--ids',$Outputs.alertId.value,'--api-version','2023-12-01')
        if($a.properties.enabled -ne $true -or (Get-ValueHash @($a.properties.actions.actionGroups)) -cne (Get-ValueHash @($Bundle.parameters.parameters.alertActionGroupIds.value))){throw 'Alert action-group configuration differs from approved input.'}
        $checks+='Alert enabled and approved action-group references'
    }
    if($type -eq 'service-bus-worker'){
        $n=Invoke-ServiceJson @('resource','show','--ids',$Outputs.busResourceId.value,'--api-version','2024-01-01')
        if($n.properties.publicNetworkAccess -cne 'Disabled' -or $n.properties.disableLocalAuth -ne $true -or $n.sku.name -cne 'Premium'){throw 'Service Bus private identity-only configuration failed.'}
        $checks+='Premium Service Bus private access and local auth disabled'
    }
    return @{checks=$checks;dataPlaneVerified=$false;notificationDeliveryVerified=$false;note='Resource configuration and private DNS verified. Consumer authorization, recovery and alert delivery require separate live acceptance.'}
}
function Invoke-ProductApply($Bundle,[string]$Phase,[string]$PlanDirectory,[string]$Directory) {
    $approved=Get-Content (Join-Path $PlanDirectory plan.json) -Raw|ConvertFrom-Json -AsHashtable
    $current=New-ProductPlan $Bundle $Phase (Join-Path $Directory recheck);Assert-ServicePlan $Bundle $approved $current $Phase
    $d=Get-WorkloadDefinition (Get-TargetWorkloadType $Bundle.target)
    if($Phase -eq 'Release' -and $d.packageKind -eq 'productFunctions'){
        $o=Get-ProductOutputs $Bundle;$account=$o.storageAccountName.value;Wait-ProductPrivateHost "$account.blob.core.windows.net"
        $name="releases/$($Bundle.receipt.releaseId).zip";$zip=Join-Path $Bundle.directory application.zip
        # Immutable content: a retry may reuse only exactly matching bytes.
        $exists=Invoke-ServiceJson @('storage','blob','exists','--account-name',$account,'--container-name','packages','--name',$name,'--auth-mode','login')
        if(!$exists.exists){$null=Invoke-ServiceJson @('storage','blob','upload','--account-name',$account,'--container-name','packages','--name',$name,'--file',$zip,'--auth-mode','login','--overwrite','false')}
        $copy=Join-Path $Directory package-verified.zip
        $null=Invoke-ServiceJson @('storage','blob','download','--account-name',$account,'--container-name','packages','--name',$name,'--file',$copy,'--auth-mode','login')
        if((Get-ServiceHash $copy) -cne $Bundle.receipt.files['application.zip']){throw 'Remote immutable package differs from qualified source.'}
    }
    if(!$current.skip){$null=Invoke-WorkloadStackApply $Bundle (Join-Path $Directory recheck/effective.parameters.json) $Directory $current}
    $o=Get-ProductOutputs $Bundle;Write-ServiceJson $o (Join-Path $Directory outputs.json)
    if($Phase -eq 'Foundation'){return @{ready=$false;status='FoundationReady';outputs=$o}}
    $acceptance=Test-ProductInfrastructure $Bundle $o
    Write-ServiceJson $acceptance (Join-Path $Directory infrastructure-checks.json)
    if($d.packageKind -eq 'infrastructure'){
        # Control-plane readiness is distinct from the separately required platform acceptance drills.
        return @{ready=$true;status='InfrastructureReady';outputs=$o;acceptance=$acceptance}
    }
    Wait-ProductPrivateHost "$($o.functionAppName.value).azurewebsites.net"
    $smoke=Invoke-ProductSmoke $Bundle $o $Directory
    return @{ready=$true;status='Ready';outputs=$o;smoke=$smoke}
}
function Invoke-ProductSmoke($Bundle,$Outputs,[string]$Directory) {
    $type=Get-TargetWorkloadType $Bundle.target
    if($type -eq 'http-functions'){
        $uri="https://$($Outputs.functionAppName.value).azurewebsites.net/api/health"
        $token=Invoke-ServiceJson @('account','get-access-token','--resource',"api://$($Bundle.parameters.parameters.apiClientId.value)")
        $response=Invoke-RestMethod $uri -Headers @{Authorization="Bearer $($token.accessToken)"} -TimeoutSec 60 -MaximumRedirection 0
        if($response.status -cne 'Healthy' -or $response.product -cne 'http-functions'){throw 'HTTP API smoke failed.'}
        $unauth=Invoke-WebRequest $uri -SkipHttpErrorCheck -TimeoutSec 30 -MaximumRedirection 0
        if([int]$unauth.StatusCode -ne 401){throw 'HTTP API did not reject unauthenticated requests.'}
        $result=@{passed=$true;authenticatedHealth=$true;unauthenticatedStatus=401}
    }else{
        $hostName="$($Outputs.busName.value).servicebus.windows.net";Wait-ProductPrivateHost $hostName
        $token=Invoke-ServiceJson @('account','get-access-token','--resource','https://servicebus.azure.net/')
        $id=[guid]::NewGuid().ToString();$body=@{id=$id;operation='record'}|ConvertTo-Json -Compress
        $null=Invoke-WebRequest "https://$hostName/work/messages" -Method Post -Headers @{Authorization="Bearer $($token.accessToken)";BrokerProperties=(@{MessageId=$id}|ConvertTo-Json -Compress)} -Body $body -ContentType application/json -TimeoutSec 60 -MaximumRedirection 0
        $account=$Outputs.storageAccountName.value;Wait-ProductPrivateHost "$account.blob.core.windows.net"
        $found=$false
        for($attempt=0;$attempt -lt 30;$attempt++){
            $exists=Invoke-ServiceJson @('storage','blob','exists','--account-name',$account,'--container-name','receipts','--name',"$id.json",'--auth-mode','login')
            if($exists.exists){$found=$true;break};Start-Sleep -Seconds 10
        }
        if(!$found){throw 'Worker did not retain a receipt within five minutes.'}
        $file=Join-Path $Directory smoke-receipt.json
        $null=Invoke-ServiceJson @('storage','blob','download','--account-name',$account,'--container-name','receipts','--name',"$id.json",'--file',$file,'--auth-mode','login')
        $receipt=Get-Content $file -Raw|ConvertFrom-Json
        $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($body))).ToLowerInvariant()
        if($receipt.Id -cne $id -or $receipt.BodySha256 -cne $hash -or $receipt.Status -cne 'Recorded'){throw 'Worker receipt mismatch.'}
        $result=@{passed=$true;messageId=$id;receiptHash=$hash;dlqReplayAcceptance='Pending separate live drill'}
    }
    Write-ServiceJson $result (Join-Path $Directory smoke.json);return $result
}
