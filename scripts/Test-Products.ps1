#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipCompile)
. "$PSScriptRoot/workload-preview-common.ps1"
$root=Get-ProjectRoot;$testRoot=Join-Path $root ('artifacts/product-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force|Out-Null
$cases=[Collections.Generic.List[object]]::new()
function Check([bool]$ok){if(!$ok){throw 'Product assertion failed.'}}
function Reject([scriptblock]$body){$failed=$false;try{& $body|Out-Null}catch{$failed=$true};Check $failed}
function Case([string]$name,[scriptblock]$body){try{& $body;$cases.Add(@{name=$name;passed=$true})}catch{throw "$name : $($_.Exception.Message)"}}
function Clone($v){$v|ConvertTo-Json -Depth 100|ConvertFrom-Json -AsHashtable}
$definitions=Get-Content (Join-Path $root config/workloads.json) -Raw|ConvertFrom-Json -AsHashtable
foreach($type in @($definitions.workloads.Keys|Where-Object {$definitions.workloads[$_].adapter -eq 'product'}|Sort-Object)){
    $d=Get-WorkloadDefinition $type;$out=Join-Path $root "artifacts/products/$type"
    New-Item -ItemType Directory -Path $out -Force|Out-Null
    if(!$SkipCompile){
        Invoke-Bicep -Arguments @('build',(Join-Path $root $d.composition),'--outfile',(Join-Path $out main.json))
        Invoke-Bicep -Arguments @('build',(Join-Path $root $d.stack),'--outfile',(Join-Path $out stack.json))
        foreach($envName in @('dev','qa','uat','prod')){Invoke-Bicep -Arguments @('build-params',(Join-Path $root "workloads/$type/environments/main.$envName.bicepparam"),'--outfile',(Join-Path $out "$envName.parameters.json"))}
    }
    $t=Get-Content (Join-Path $root "self-service/targets/$($d.menuSlug).dev.json") -Raw|ConvertFrom-Json -AsHashtable
    $p=(Get-Content (Join-Path $out dev.parameters.json) -Raw|ConvertFrom-Json -AsHashtable).parameters
    Case "$type disabled until onboarding" {Check (!$t.enabled);Reject {Assert-ServiceTarget $t $t.workload dev};Reject {Assert-ProductParameters $t $p}}
    $scope="/subscriptions/$($t.subscriptionId)/resourceGroups/shared/providers"
    $p.owner.value='platform-test';$p.costCenter.value='test';$p.deploymentPrincipalObjectId.value='11111111-1111-1111-1111-111111111111'
    $p.existingLogAnalyticsWorkspaceId.value="$scope/Microsoft.OperationalInsights/workspaces/shared"
    foreach($key in @('consumerPrincipalObjectId','readerPrincipalObjectId','apiClientId')|Where-Object {$p.Contains($_)}){$p[$key].value='22222222-2222-2222-2222-222222222222'}
    if($p.Contains('allowedClientApplications')){$p.allowedClientApplications.value=@('33333333-3333-3333-3333-333333333333')}
    if($p.Contains('alertActionGroupIds')){$p.alertActionGroupIds.value=@("$scope/Microsoft.Insights/actionGroups/oncall")}
    $vnet="$scope/Microsoft.Network/virtualNetworks/pilot"
    if($d.dnsKeys.Count){$p.privateEndpointSubnetId.value="$vnet/subnets/endpoints";foreach($key in $d.dnsKeys){$p.privateDnsZoneIds.value[$key]="$scope/Microsoft.Network/privateDnsZones/$((Get-ProductDnsNames)[$key])"}}
    if($p.Contains('integrationSubnetId')){$p.integrationSubnetId.value="$vnet/subnets/integration"}
    Case "$type reviewed parameters resolve typed intent" {
        Assert-ProductParameters $t $p
        $intent=Resolve-PlatformRequest @{workloadType=$type;workloadName=$t.workload;environment='dev';region='eastus2'} @($t)
        Check (!$intent.deploymentEnabled -and $intent.composition -ceq $d.composition)
    }
    Case "$type rejects cross-workload parameter path" {$bad=Clone $t;$bad.parameterFile='workloads/blob-transfer/environments/main.dev.bicepparam';Reject {Assert-ServiceTarget $bad $bad.workload dev -AllowDisabled}}
    Case "$type accepts source custom tags and rejects platform overrides" {
        $custom=Clone $p;$custom.customTags=@{value=@{'custom.team'='platform'}};Assert-ProductParameters $t $custom
        $custom.customTags.value=@{Owner='override'};Reject {Assert-ProductParameters $t $custom}
    }
    Case "$type rejects arbitrary executable/secret option" {$bad=Clone $p;$bad.scriptPath=@{value='injected'};Reject {Assert-ProductParameters $t $bad}}
    Case "$type rejects wrong workspace scope" {$bad=Clone $p;$bad.existingLogAnalyticsWorkspaceId.value=$bad.existingLogAnalyticsWorkspaceId.value.Replace($t.subscriptionId,'99999999-9999-9999-9999-999999999999');Reject {Assert-ProductParameters $t $bad}}
    $inv=@{schemaVersion=1;readOnly=$true;workloadType=$type;discoveryStatus='Complete';generatedUtc=[DateTimeOffset]::UtcNow.ToString('O');subscription=@{id=$t.subscriptionId};providers=@($d.providers|ForEach-Object {@{namespace=$_;registrationState='Registered'}});resources=@(@{id=$p.existingLogAnalyticsWorkspaceId.value;type='Microsoft.OperationalInsights/workspaces'});resourceQuery=@{status='Succeeded'};networkQuery=@{status='Succeeded'};privateDnsQuery=@{status='Succeeded'};networks=@();privateDnsZones=@();serviceConnections=@();serviceConnectionQuery=@{status='NotRequested'}}
    if($p.Contains('alertActionGroupIds')){$inv.resources+=@{id=$p.alertActionGroupIds.value[0];type='Microsoft.Insights/actionGroups'}}
    if($d.dnsKeys.Count){
        $subnets=@(@{id=$p.privateEndpointSubnetId.value;privateEndpointCandidate=$true;integrationCandidate=$false})
        if($p.Contains('integrationSubnetId')){$subnets+=@{id=$p.integrationSubnetId.value;privateEndpointCandidate=$false;integrationCandidate=$true}}
        $inv.networks=@(@{id=$vnet;location='eastus2';subnets=$subnets;subnetQuery=@{status='Succeeded'}})
        $inv.privateDnsZones=@($p.privateDnsZoneIds.value.Values|ForEach-Object {@{id=$_}})
    }
    $dir=Join-Path $testRoot $type;New-Item -ItemType Directory -Path $dir|Out-Null
    Write-ServiceJson $inv (Join-Path $dir inventory.json)
    $m=@{schemaVersion=2;kind='workload-discovery';workloadType=$type;discoveryStatus='Complete';generatedUtc=$inv.generatedUtc;inventorySha256=(Get-ServiceHash (Join-Path $dir inventory.json));subscriptionId=$t.subscriptionId;serviceConnection=$t.serviceConnection;selection=@{workload=$t.workload;environment='dev';region='eastus2';subscription=$t.subscriptionAlias;network=$t.networkProfile};source=@{runId='42';pipelineId='1';branch='refs/heads/main';commit=('a'*40);repositoryId='test';projectId='test'}}
    Write-ServiceJson $m (Join-Path $dir manifest.json)
    Case "$type consumes versioned discovery and parameters" {$null=Read-DiscoveryManifest $dir $t $t.serviceConnection;Assert-ProductDiscovery $t $p $dir}
    Case "$type cost unavailable is never free" {$c=Get-ProductCostEstimate $t $p;Check ($c.status -ceq 'Unavailable' -and $null -eq $c.fixedMonthlySubtotalUsd)}
    Case "$type failed resource query blocks preview" {$bad=Clone $inv;$bad.resourceQuery.status='Failed';Write-ServiceJson $bad (Join-Path $dir inventory.json);Reject {Assert-ProductDiscovery $t $p $dir};Write-ServiceJson $inv (Join-Path $dir inventory.json)}
    Case "$type shared dependency absent does not authorize creation" {$bad=Clone $inv;$bad.resources=@();Write-ServiceJson $bad (Join-Path $dir inventory.json);Reject {Assert-ProductDiscovery $t $p $dir};Write-ServiceJson $inv (Join-Path $dir inventory.json)}
    Case "$type unregistered provider blocks" {$bad=Clone $inv;$bad.providers[0].registrationState='NotRegistered';Write-ServiceJson $bad (Join-Path $dir inventory.json);Reject {Assert-ProductDiscovery $t $p $dir};Write-ServiceJson $inv (Join-Path $dir inventory.json)}
    Case "$type manifest cannot switch workload" {$bad=Clone $m;$bad.workloadType='blob-transfer';Write-ServiceJson $bad (Join-Path $dir manifest.json);Reject {Read-DiscoveryManifest $dir $t $t.serviceConnection};Write-ServiceJson $m (Join-Path $dir manifest.json)}
    Case "$type wrapper and composition parameters match" {
        $main=Get-Content (Join-Path $out main.json) -Raw|ConvertFrom-Json -AsHashtable
        $stack=Get-Content (Join-Path $out stack.json) -Raw|ConvertFrom-Json -AsHashtable
        Assert-StackTemplate $stack
        Check (@(Compare-Object @($main.parameters.Keys|Sort-Object) @($stack.parameters.Keys|Where-Object {$_ -ne 'workloadResourceGroupName'}|Sort-Object)).Count -eq 0)
    }
    Case "$type full preview package semantics" {
        $effective=Get-PreviewParameters $t @{parameters=$p} '42'
        Check $effective.parameters.releaseActivated.value
        Check ($effective.parameters.Contains('packageBlobName') -eq ($d.packageKind -eq 'productFunctions'))
        $contract=New-StackContract $t (Get-Content (Join-Path $out stack.json) -Raw|ConvertFrom-Json -AsHashtable)
        Check ($contract.templateSpecId.Contains("/templateSpecs/$type/versions/sha256-"))
        Reject {Assert-StackManagedId @{target=$t;parameters=@{parameters=$p}} "$scope/Microsoft.Storage/storageAccounts/external"}
    }
    if($d.dnsKeys.Count){Case "$type mismatched DNS zone fails" {$bad=Clone $p;$bad.privateDnsZoneIds.value[$d.dnsKeys[0]]="$scope/Microsoft.Network/privateDnsZones/invalid";Reject {Assert-ProductParameters $t $bad}}}
    Case "$type live prerequisite adapter rejects unreadable and drifted dependencies (mocked Azure)" {
        $originalJson=${function:Invoke-ServiceJson};$originalLinks=${function:Get-StackRestCollection}
        try {
            $scenario='healthy';$fixtureVnetId=$vnet
            function Invoke-ServiceJson([string[]]$Arguments){
                $id=$Arguments[[Array]::IndexOf($Arguments,'--ids')+1]
                if($scenario -eq 'denied'){throw 'AuthorizationFailed fixture'}
                if($id -match '/virtualNetworks/[^/]+$'){
                    return @{id=$id;location=$(if($scenario -eq 'wrong-region'){'westus'}else{'eastus2'});properties=@{dhcpOptions=@{dnsServers=$(if($scenario -eq 'custom-dns'){@('10.0.0.4')}else{@()})}}}
                }
                if($id -match '/subnets/'){
                    return @{id=$id;properties=@{privateEndpointNetworkPolicies='Disabled';delegations=$(if($id.EndsWith('/integration')){@(@{properties=@{serviceName='Microsoft.Web/serverFarms'}})}else{@()});addressPrefix='10.0.2.0/26';privateEndpoints=@()}}
                }
                return @{id=$(if($scenario -eq 'wrong-id'){'unrelated'}else{$id});properties=@{}}
            }
            function Get-StackRestCollection {return @(@{properties=@{virtualNetwork=@{id=$fixtureVnetId};provisioningState=$(if($scenario -eq 'unlinked'){'Failed'}else{'Succeeded'})}})}
            $b=@{target=$t;parameters=@{parameters=$p}}
            $e=Test-ProductPrerequisites $b;Check ($e.Count -gt 0)
            foreach($scenario in @('denied','wrong-id')){Reject {Test-ProductPrerequisites $b}}
            if($d.dnsKeys.Count){foreach($scenario in @('wrong-region','custom-dns','unlinked')){Reject {Test-ProductPrerequisites $b}}}
        }finally{Set-Item Function:Invoke-ServiceJson $originalJson;Set-Item Function:Get-StackRestCollection $originalLinks}
    }
    Case "$type plan serializes a flat governed change list (mocked Azure)" {
        $originalState=${function:Get-WorkloadStackState};$originalPrereq=${function:Test-ProductPrerequisites};$originalPreview=${function:New-StackPreview}
        try {
            function Get-WorkloadStackState {return @{stackExists=$true;hasApp=$false}}
            function Test-ProductPrerequisites {return @{fixture='reviewed'}}
            function New-StackPreview {return @{status='Succeeded';changes=@(@{resourceId='/subscriptions/test/resourceGroups/owned';changeType='Create';after=@{tags=@{owner='platform'}}})}}
            $b=@{target=$t;parameters=@{parameters=$p};receipt=@{releaseId='test42'};hash='testhash';stack=@{stackId='teststack';templateSpecId='testspec'}}
            $planDir=Join-Path $dir plan
            $plan=New-ProductPlan $b Release $planDir
            Check ($plan.changes.Count -eq 1 -and $plan.changes[0] -is [Collections.IDictionary])
            $saved=Get-Content (Join-Path $planDir plan.json) -Raw|ConvertFrom-Json -AsHashtable
            Check ($saved.changes[0].changeType -ceq 'Create' -and $saved.fingerprint -ceq $plan.fingerprint)
        }finally{Set-Item Function:Get-WorkloadStackState $originalState;Set-Item Function:Test-ProductPrerequisites $originalPrereq;Set-Item Function:New-StackPreview $originalPreview}
    }
    # The producer contract is also consumed by the actual offline report path.
    Case "$type saved discovery report compatibility" {
        # Invoke the documented wrapper so report output and schema remain shared with ADO.
        & "$PSScriptRoot/Export-SelfServiceAnalysis.ps1" -DiscoveryDirectory $dir -OutputDirectory (Join-Path $dir analysis) -Workload $t.workload -EnvironmentName dev
    }
    if($d.packageKind -eq 'infrastructure'){
        Case "$type real bundle roundtrip without application and tamper rejection" {
            $selected=Clone $t;$selected.enabled=$true
            foreach($key in $p.Keys|Where-Object {$_ -notin @('workload','environmentName')}){$selected.parameterOverrides[$key]=$p[$key].value}
            $originalCompile=${function:Invoke-Bicep};$originalTarget=${function:Read-ServiceTarget}
            try {
                # Reuse templates compiled above. Target override is confined to this mock case, never written to the catalog.
                function Invoke-Bicep([string[]]$Arguments){
                    $dest=$Arguments[[Array]::IndexOf($Arguments,'--outfile')+1]
                    $src=if($Arguments[0] -eq 'build-params'){Join-Path $out dev.parameters.json}elseif($Arguments[1].EndsWith('stack.bicep')){Join-Path $out stack.json}else{Join-Path $out main.json}
                    Copy-Item $src $dest
                }
                function Read-ServiceTarget {return $selected}
                $release=Join-Path $dir release;New-Item -ItemType Directory -Path $release|Out-Null
                $commit=& git -C $root rev-parse HEAD
                Write-ServiceJson @{workloadType=$type;releaseId='test42';sourceCommit=$commit;dirtyWorktree=$false;packageKind='infrastructure'} (Join-Path $release release.json)
                $bundleDir=Join-Path $dir bundle
                New-ProductBundle $selected $release $bundleDir $dir
                $bundle=Read-ServiceBundle $bundleDir
                Check ($bundle.receipt.schemaVersion -eq 3 -and !$bundle.receipt.files.Contains('application.zip') -and !(Test-Path (Join-Path $bundleDir application.zip)))
                Add-Content (Join-Path $bundleDir parameters.json) 'tampered'
                Reject {Read-ServiceBundle $bundleDir}
            }finally{Set-Item Function:Invoke-Bicep $originalCompile;Set-Item Function:Read-ServiceTarget $originalTarget}
        }
    }
}
foreach($pair in @(@('Microsoft.KeyVault/vaults','enablePurgeProtection'),@('Microsoft.KeyVault/vaults','enableRbacAuthorization'),@('Microsoft.ServiceBus/namespaces','disableLocalAuth'),@('Microsoft.Web/sites','requireAuthentication'))){
    Case "security gate rejects disabling $($pair[1])" {Reject {Assert-ServiceChange @{resourceId="/subscriptions/test/resourceGroups/test/providers/$($pair[0])/test";changeType='Create';after=@{properties=@{$pair[1]=$false}}}}}
}
Write-ServiceJson @{passed=$cases.Count;failed=0;azureCalls=$false;cases=$cases} (Join-Path $testRoot results.json)
Write-Host "PASS: $($cases.Count) product contracts. Evidence: $testRoot"
& dotnet test (Join-Path $root tests/ProductFunctions.Tests/ProductFunctions.Tests.csproj) -c Release -p:RestoreLockedMode=true --logger 'trx;LogFileName=products.trx' --results-directory (Join-Path $root artifacts/test-results)
if($LASTEXITCODE -ne 0){throw 'Product runtime tests failed.'}
