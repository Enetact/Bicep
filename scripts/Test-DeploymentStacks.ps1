#requires -Version 7.4
. "$PSScriptRoot/self-service-common.ps1"
$root=Join-Path (Get-ProjectRoot) ('artifacts/stack-tests/'+[guid]::NewGuid().ToString('N'))
$results=[Collections.Generic.List[object]]::new()
function Check([bool]$Value) { if(!$Value){throw 'Stack test assertion failed.'} }
function Reject([scriptblock]$Body,[string]$Message) { $caught=$false; try{& $Body | Out-Null}catch{if($Message -and $_.Exception.Message -notlike "*$Message*"){throw};$caught=$true};Check $caught }
function Case([string]$Name,[scriptblock]$Body) { & $Body; $results.Add(@{name=$Name;passed=$true}) }
function Clone($Value) { $Value | ConvertTo-Json -Depth 100 | ConvertFrom-Json -AsHashtable }
$target=@{schemaVersion=1;enabled=$true;workload='blobcopy';environmentName='dev';subscriptionId='11111111-1111-1111-1111-111111111111';resourceGroup='rg-blobcopy-dev';parameterFile='workloads/blob-transfer/environments/main.dev.bicepparam';serviceConnection='sc-test';agentPool='private-test';deploymentEnvironment='blobcopy-dev';smokePrefix='smoke/'}
$template=@{'$schema'='https://schema.management.azure.com/schemas/2018-05-01/subscriptionDeploymentTemplate.json#';contentVersion='1.0.0.0';resources=@()}
$values=@{location='eastus2';workload='blobcopy';environmentName='dev';owner='team';costCenter='CC1';destinationSubscriptionId=$target.subscriptionId;destinationResourceGroupName='destination';destinationStorageAccountName='destinationaccount';destinationContainerName='incoming';destinationIsHnsEnabled=$true;vnetAddressPrefix='10.40.0.0/16';integrationSubnetPrefix='10.40.0.0/26';privateEndpointSubnetPrefix='10.40.1.0/26'}
$parameters=@{};foreach($key in $values.Keys){$parameters[$key]=@{value=$values[$key]}}
$dir=Join-Path $root bundle
Write-ServiceJson $template (Join-Path $dir stack-template.json)
Write-ServiceJson @{resources=@()} (Join-Path $dir main.json)
Write-ServiceJson $target (Join-Path $dir target.json)
Write-ServiceJson @{parameters=$parameters} (Join-Path $dir parameters.json)
Write-ServiceJson (New-StackContract $target $template) (Join-Path $dir stack.json)
[IO.File]::WriteAllText((Join-Path $dir application.zip),'Offline stack test package')
$metadata=@();foreach($pair in @(@('DispatchUploadedBlob','blobTrigger'),@('CopyUploadedBlob','queueTrigger'),@('ReconcileTransfers','timerTrigger'),@('AuditTransferLedger','timerTrigger'),@('MonitorTransferPoison','timerTrigger'))){$metadata+=@{name=$pair[0];bindings=@(@{type=$pair[1]})}}
Write-ServiceJson $metadata (Join-Path $dir functions.metadata)
$files=@{};foreach($name in @('main.json','parameters.json','target.json','application.zip','functions.metadata','stack.json','stack-template.json')){$files[$name]=Get-ServiceHash (Join-Path $dir $name)}
Write-ServiceJson @{schemaVersion=1;deploymentEngine='deploymentStack';releaseId='stack-test';sourceCommit='test';files=$files} (Join-Path $dir bundle.json)
$bundle=Read-ServiceBundle $dir
$resourceGroupId="/subscriptions/$($target.subscriptionId)/resourceGroups/$($target.resourceGroup)"
$appId="$resourceGroupId/providers/Microsoft.Web/sites/func-blobcopy-dev-test"
$outputValues=@{hostStorageAccountName='hosttestaccount';uploadStorageAccountName='uploadtestaccount';uploadContainer='incoming';ledgerContainer='transfer-ledger';transferQueue='transfer-work';packageContainer='packages';functionAppName='func-blobcopy-dev-test';functionAppResourceId=$appId;managedIdentityPrincipalId='11111111-1111-1111-1111-111111111111';workspaceId='/workspace'}
$outputs=@{};foreach($key in $outputValues.Keys){$outputs[$key]=@{type='string';value=$outputValues[$key]}}
$script:calls=[Collections.Generic.List[string]]::new();$script:stack=$null;$script:specExists=$false;$script:published=Clone $template;$script:existingGroup=$false;$script:previewMode='ok';$script:packageUploaded=$false
$script:cliVersion='2.89.1';$script:cliHelp='--template-spec --validation-level --no-pretty-print --retention-interval'
function Argument([string[]]$CommandArguments,[string]$Name){$index=[Array]::IndexOf($CommandArguments,$Name);if($index -lt 0){throw "Missing argument $Name"};return $CommandArguments[$index+1]}
function Preview($Effective) {
    $ids=@($resourceGroupId,"$resourceGroupId/providers/Microsoft.Storage/storageAccounts/hosttestaccount")
    if($Effective.parameters.deployFunctionApp.value){$ids+=@($appId)}
    $changes=@($ids | ForEach-Object {@{id=$_;changeType=$(if($script:stack -and $_ -in @($script:stack.properties.resources.id)){'noChange'}else{'create'});changeCertainty='definite';managementStatusChange=@{after='managed'}}})
    if($script:previewMode -eq 'detach'){$changes[0].changeType='detach'}
    if($script:previewMode -eq 'potential'){$changes[0].changeCertainty='potential'}
    return @{properties=@{provisioningState='Succeeded';deploymentStackResourceId=$bundle.stack.stackId;changes=@{resourceChanges=$changes;denySettingsChange=@{delta=@()}}}}
}
function Invoke-ServiceJson([string[]]$Arguments) {
    $command=$Arguments -join ' ';$script:calls.Add($command)
    switch -Regex ($command) {
        '^version' { return @{'azure-cli'=$script:cliVersion} }
        '^ts list ' { if($script:specExists){return @(@{id=($bundle.stack.templateSpecId -replace '/versions/[^/]+$','')})};return @() }
        '^rest ' { return @{value=@(@{id=$bundle.stack.templateSpecId})} }
        '^ts create ' { $script:specExists=$true;return @{id=$bundle.stack.templateSpecId} }
        '^ts show ' { if(!$script:specExists){throw 'Template Spec missing'};return @{id=$bundle.stack.templateSpecId;properties=@{mainTemplate=$script:published}} }
        '^stack sub list ' { if($script:stack){return @(@{id=$script:stack.id})};return @() }
        '^stack sub show ' { return Clone $script:stack }
        '^group list ' { if($script:existingGroup){return @(@{name=$target.resourceGroup})};return @() }
        '^stack sub validate ' { return @{properties=@{provisioningState='Succeeded'}} }
        '^stack-whatif sub create ' { $file=(Argument $Arguments '--parameters').TrimStart('@');return Preview (Get-Content $file -Raw | ConvertFrom-Json -AsHashtable) }
        '^stack sub create ' {
            $file=(Argument $Arguments '--parameters').TrimStart('@');$effective=Get-Content $file -Raw | ConvertFrom-Json -AsHashtable
            $raw=Preview $effective
            $script:stack=@{id=$bundle.stack.stackId;tags=@{targetKey=$bundle.stack.targetKey};properties=@{provisioningState='Succeeded';parameters=$effective.parameters;outputs=$outputs;resources=@($raw.properties.changes.resourceChanges | ForEach-Object {@{id=$_.id}});denySettings=@{mode=$bundle.stack.denySettingsMode};actionOnUnmanage=@{resources='detach';resourceGroups='detach';managementGroups='detach'}}}
            return Clone $script:stack
        }
        '^account show ' {return @{tenantId='tenant'}}
        '^storage account show ' {return @{id='/destination';isHnsEnabled=$true}}
        '^resource show ' {return @{id='/destination/container'}}
        default {throw "Unexpected Azure call: $command"}
    }
}
function Invoke-Az([string[]]$Arguments){$command=$Arguments -join ' ';$script:calls.Add($command);if($command -eq 'stack-whatif sub create --help'){return $script:cliHelp};if($command -notmatch '^stack-whatif sub delete '){throw "Unexpected raw Azure call: $command"}}
function Wait-ServiceConnectivity($Bundle,$Outputs){}
function Wait-ServiceFunctions($Bundle,$Outputs){}
function Invoke-ServiceSmoke($Bundle,$Outputs,$EvidenceDirectory){return @{passed=$true;requestIds=@('one','two','three')}}
function Publish-ServicePackage($Bundle,$Outputs,$Directory){$script:packageUploaded=$true}

Case 'CLI baseline and required preview flags accepted' {Assert-StackTooling}
Case 'older CLI rejected before mutation' {$script:cliVersion='2.88.0';try{Reject {Assert-StackTooling} '2.89.1'}finally{$script:cliVersion='2.89.1'}}
Case 'missing machine readable preview support rejected' {$script:cliHelp='--template-spec --validation-level --retention-interval';try{Reject {Assert-StackTooling} 'no-pretty-print'}finally{$script:cliHelp+=' --no-pretty-print'}}
Case 'bundle freezes stack identity and hash-addressed spec version' {Check ($bundle.stack.stackId -like '/subscriptions/*/providers/Microsoft.Resources/deploymentStacks/stack-blobcopy-dev');Check ($bundle.stack.templateSpecId.EndsWith('/versions/sha256-'+(Get-ValueHash $template)))}
Case 'nested linked templates rejected before publication' {$bad=Clone $template;$bad.resources=@(@{properties=@{templateLink=@{id='/unreviewed'}}});Reject {Assert-StackTemplate $bad} 'templateLink'}
Case 'template tampering fails bundle integrity' {Add-Content (Join-Path $dir stack-template.json) ' ';Reject {Read-ServiceBundle $dir} 'integrity';Write-ServiceJson $template (Join-Path $dir stack-template.json)}
Case 'publication creates absent version and verifies content' {$r=Publish-StackTemplate $bundle (Join-Path $root publish);Check (!$r.reused -and $script:specExists)}
Case 'existing identical version is reused without overwrite' {$script:calls.Clear();$r=Publish-StackTemplate $bundle (Join-Path $root republish);Check $r.reused;Check (!@($script:calls | Where-Object {$_ -like 'ts create *'}).Count)}
Case 'changed existing version rejected without overwrite' {$script:published.contentVersion='2.0.0.0';try{Reject {Publish-StackTemplate $bundle (Join-Path $root badpublish)} 'hash mismatch'}finally{$script:published=Clone $template}}
Case 'new stack rejects pre-existing RG adoption' {$script:existingGroup=$true;try{Reject {Get-WorkloadStackState $bundle} 'adoption'}finally{$script:existingGroup=$false}}
$planDirectory=Join-Path $root foundation-plan
Case 'new foundation uses native stack validation preview and metadata cleanup' {$script:calls.Clear();$script:foundation=New-ServicePlan $bundle Foundation $planDirectory;Check (!$script:foundation.skip);Check (@($script:calls | Where-Object {$_ -like 'stack-whatif sub create *'}).Count -eq 1);Check (@($script:calls | Where-Object {$_ -like 'stack-whatif sub delete *'}).Count -eq 1);Check (!@($script:calls | Where-Object {$_ -like 'stack sub create *'}).Count)}
Case 'foundation apply creates stack with lifecycle evidence but not Ready' {$r=Invoke-ServiceApply $bundle Foundation $planDirectory (Join-Path $root foundation-apply);Check ($r.status -eq 'FoundationReady' -and !$r.ready);Check (Test-Path (Join-Path $root foundation-apply/lifecycle.json))}
$releasePlan=Join-Path $root release-plan
Case 'release preview preserves foundation ownership and adds runtime' {$script:release=New-ServicePlan $bundle Release $releasePlan;Check ($script:release.state.managedResources.Count -eq 2);Check ($script:release.changes.Count -eq 3)}
Case 'release applies pinned spec uploads package and reaches smoke readiness' {$r=Invoke-ServiceApply $bundle Release $releasePlan (Join-Path $root release-apply);Check ($r.ready -and $script:packageUploaded);Check ($script:stack.properties.resources.Count -eq 3)}
Case 'Foundation after Release is skipped and cannot shrink ownership' {$script:calls.Clear();$p=New-ServicePlan $bundle Foundation (Join-Path $root repeated-foundation);Check $p.skip;Check (!@($script:calls | Where-Object {$_ -like 'stack-whatif sub create *'}).Count)}
Case 'detach detected by fresh preview blocks apply' {$p=Join-Path $root safe-plan;$null=New-ServicePlan $bundle Release $p;$script:previewMode='detach';$script:calls.Clear();try{Reject {Invoke-ServiceApply $bundle Release $p (Join-Path $root denied-apply)} 'lifecycle';Check (!@($script:calls | Where-Object {$_ -like 'stack sub create *'}).Count)}finally{$script:previewMode='ok'}}
Case 'potential changes fail closed and still clean preview metadata' {$script:previewMode='potential';$script:calls.Clear();try{Reject {New-ServicePlan $bundle Release (Join-Path $root uncertain)} 'potential';Check (@($script:calls | Where-Object {$_ -like 'stack-whatif sub delete *'}).Count -eq 1)}finally{$script:previewMode='ok'}}
Case 'stack state drift invalidates an approved plan' {$p=Join-Path $root drift-plan;$null=New-ServicePlan $bundle Release $p;$script:stack.properties.outputs.workspaceId.value='/changed-workspace';$script:calls.Clear();Reject {Invoke-ServiceApply $bundle Release $p (Join-Path $root drift-apply)} 'drifted';Check (!@($script:calls | Where-Object {$_ -like 'stack sub create *'}).Count);$script:stack.properties.outputs.workspaceId.value='/workspace'}
Case 'stack ownership tag mismatch is rejected' {$script:stack.tags.targetKey='another';try{Reject {Get-WorkloadStack $bundle} 'adoption'}finally{$script:stack.tags.targetKey=$bundle.stack.targetKey}}
Case 'failed stack requires explicit recovery' {$script:stack.properties.provisioningState='Failed';try{Reject {Get-WorkloadStack $bundle} 'recovery'}finally{$script:stack.properties.provisioningState='Succeeded'}}
Case 'missing persisted release phase fails closed' {$saved=$script:stack.properties.parameters.deployFunctionApp;$script:stack.properties.parameters.Remove('deployFunctionApp');try{Reject {Get-WorkloadStackState $bundle} 'phase'}finally{$script:stack.properties.parameters.deployFunctionApp=$saved}}
Case 'unhealthy managed resource requires recovery' {$script:stack.properties.resources[0].status='deleteFailed';try{Reject {Get-WorkloadStackState $bundle} 'recovery'}finally{$script:stack.properties.resources[0].Remove('status')}}
$state=Get-WorkloadStackState $bundle
$raw=Preview @{parameters=@{deployFunctionApp=@{value=$true}}}
foreach($kind in @('delete','detach','unsupported')){Case "parser rejects $kind" {$bad=Clone $raw;$bad.properties.changes.resourceChanges[0].changeType=$kind;Reject {Convert-StackPreview $bad $bundle $state} 'lifecycle'}}
Case 'parser rejects missing inventory coverage' {$bad=Clone $raw;$bad.properties.changes.resourceChanges=@($bad.properties.changes.resourceChanges | Select-Object -Skip 1);Reject {Convert-StackPreview $bad $bundle $state} 'omitted'}
Case 'parser rejects diagnostics and wrong stack' {$bad=Clone $raw;$bad.properties.diagnostics=@(@{message='Partial evaluation'});Reject {Convert-StackPreview $bad $bundle $state} 'diagnostics';$bad=Clone $raw;$bad.properties.deploymentStackResourceId='/other';Reject {Convert-StackPreview $bad $bundle $state} 'another stack'}
Case 'property adapter preserves sensitive nested changes' {$bad=Clone $raw;$r=$bad.properties.changes.resourceChanges[-1];$r.changeType='modify';$r.resourceConfigurationChanges=@{delta=@(@{path='properties';changeType='modify';children=@(@{path='publicNetworkAccess';changeType='modify';after='Enabled'})})};Reject {Convert-StackPreview $bad $bundle $state} 'High-risk'}
Case 'deny and scope changes require separate review' {$bad=Clone $raw;$bad.properties.changes.denySettingsChange.delta=@(@{path='mode';changeType='modify'});Reject {Convert-StackPreview $bad $bundle $state} 'Deny';$bad=Clone $raw;$bad.properties.changes.deploymentScopeChange=@{before='/subscriptions/old';after='/subscriptions/new'};Reject {Convert-StackPreview $bad $bundle $state} 'scope'}
Case 'shared DNS cannot enter workload ownership' {$bad=Clone $raw;$bad.properties.changes.resourceChanges[0].id='/subscriptions/22222222-2222-2222-2222-222222222222/resourceGroups/hub/providers/Microsoft.Network/privateDnsZones/privatelink.blob.core.windows.net';Reject {Convert-StackPreview $bad $bundle $state} 'ownership'}
Case 'destination account excluded but workload container grants admitted' {$destination="/subscriptions/$($target.subscriptionId)/resourceGroups/destination/providers/Microsoft.Storage/storageAccounts/destinationaccount";Reject {Assert-StackManagedId $bundle $destination} 'ownership';Assert-StackManagedId $bundle "$destination/blobServices/default/containers/incoming/providers/Microsoft.Authorization/roleAssignments/11111111-1111-1111-1111-111111111111"}
Case 'deny protection removal is rejected' {$bad=Clone $raw;$bad.properties.changes.resourceChanges[0].denyStatusChange=@{before='denyDelete';after='none'};Reject {Convert-StackPreview $bad $bundle $state} 'weaken'}
Case 'resource group must be in first stack preview' {$bad=Clone $raw;$bad.properties.changes.resourceChanges=@($bad.properties.changes.resourceChanges | Select-Object -Skip 1);Reject {Convert-StackPreview $bad $bundle @{stackExists=$false;managedResources=@()}} 'resource group'}
Case 'no ordinary deployment or workload deletion command used' {Check (!@($script:calls | Where-Object {$_ -match '^deployment |^stack sub delete |^group delete '}).Count)}
$report=@{passed=$results.Count;failed=0;azureCalls='mocked';cases=$results;schemaSource='Azure/azure-rest-api-specs Microsoft.Resources/deploymentStacks stable/2025-07-01'}
Write-ServiceJson $report (Join-Path $root results.json)
Write-ServiceJson $report (Join-Path (Get-ProjectRoot) artifacts/test-results/deployment-stacks.json)
Write-Host "PASS: $($results.Count) stack contracts. Evidence: $root"
