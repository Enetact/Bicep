#requires -Version 7.4
. "$PSScriptRoot/discovery-manifest-common.ps1"
. "$PSScriptRoot/platform-contract.ps1"
$cases=[Collections.Generic.List[object]]::new()
function Check([bool]$Value){if(!$Value){throw 'Logic App assertion failed.'}}
function Reject([scriptblock]$Body){$failed=$false;try{& $Body|Out-Null}catch{$failed=$true};Check $failed}
function Case([string]$Name,[scriptblock]$Body){try{& $Body;$cases.Add(@{name=$Name;passed=$true})}catch{throw "Logic App case '$Name': $($_.Exception.Message)"}}
function Clone($Value){$Value|ConvertTo-Json -Depth 100|ConvertFrom-Json -AsHashtable}
$root=Get-ProjectRoot;$testRoot=Join-Path $root ('artifacts/logic-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot|Out-Null
$target=Get-Content (Join-Path $root self-service/targets/eventflow.dev.json) -Raw|ConvertFrom-Json -AsHashtable
$sub=$target.subscriptionId;$rg="/subscriptions/$sub/resourceGroups/platform"
$zones=@{};foreach($pair in @(@('blob','blob.core.windows.net'),@('queue','queue.core.windows.net'),@('table','table.core.windows.net'),@('file','file.core.windows.net'),@('sites','azurewebsites.net'),@('topic','eventgrid.azure.net'))){$zones[$pair[0]]="$rg/providers/Microsoft.Network/privateDnsZones/privatelink.$($pair[1])"}
$p=@{workload=@{value='eventflow'};environmentName=@{value='dev'};location=@{value='eastus2'};owner=@{value='platform'};costCenter=@{value='engineering'};integrationSubnetId=@{value="$rg/providers/Microsoft.Network/virtualNetworks/hub/subnets/integration"};privateEndpointSubnetId=@{value="$rg/providers/Microsoft.Network/virtualNetworks/hub/subnets/endpoints"};privateDnsZoneIds=@{value=$zones};existingLogAnalyticsWorkspaceId=@{value="$rg/providers/Microsoft.OperationalInsights/workspaces/shared"};deploymentPrincipalObjectId=@{value='11111111-1111-1111-1111-111111111111'};trustedServiceException=@{value=@{approved=$true;reviewReference='https://example.test/review/network'}};runtimeStorageCredentialException=@{value=@{approved=$true;reviewReference='https://example.test/review/runtime'}}}
Case 'valid onboarding has no missing-setting findings' {Check (@(Get-LogicOnboardingIssues $p).Count -eq 0)}
Case 'unconfigured QA parameters still report all fourteen onboarding blockers together' {
    $unconfigured=Clone $target
    $unconfigured.environmentName='qa'
    $unconfigured.parameterFile='workloads/logic-app-event-grid/environments/main.qa.bicepparam'
    $parametersFile=Join-Path $testRoot onboarding.parameters.json
    Invoke-Bicep -Arguments @('build-params',(Join-Path $root $unconfigured.parameterFile),'--outfile',$parametersFile)
    $example=Get-Content $parametersFile -Raw|ConvertFrom-Json -AsHashtable
    $issues=@(Get-LogicOnboardingIssues $example.parameters)
    Check ($issues.Count -eq 14)
    foreach($name in @('owner','costCenter','integrationSubnetId','privateEndpointSubnetId','existingLogAnalyticsWorkspaceId','deploymentPrincipalObjectId','privateDnsZoneIds.blob','privateDnsZoneIds.queue','privateDnsZoneIds.table','privateDnsZoneIds.file','privateDnsZoneIds.sites','privateDnsZoneIds.topic','trustedServiceException','runtimeStorageCredentialException')){Check ($name -in $issues.parameter)}
    $errorText='';try{Assert-LogicParameters $unconfigured $example.parameters}catch{$errorText=$_.Exception.Message}
    Check ($errorText.Contains($unconfigured.parameterFile) -and $errorText.Contains('runtimeStorageCredentialException') -and !$errorText.Contains('REPLACE_OWNER'))
}
Case 'missing and blank required settings are actionable without echoing values' {
    $bad=Clone $p;$bad.Remove('owner');$bad.costCenter.value=' ';$bad.deploymentPrincipalObjectId.value='REPLACE_PRIVATE_VALUE'
    $issues=@(Get-LogicOnboardingIssues $bad);Check ($issues.Count -eq 3)
    Check (!(ConvertTo-Canonical $issues).Contains('REPLACE_PRIVATE_VALUE'))
}
Case 'platform approval cannot be replaced by a nonboolean flag or missing reference' {
    $bad=Clone $p;$bad.trustedServiceException.value.approved='true';$bad.runtimeStorageCredentialException.value.reviewReference=''
    $issues=@(Get-LogicOnboardingIssues $bad);Check ($issues.Count -eq 2);Reject {Assert-LogicParameters $target $bad}
}
Case 'disabled typed profile validates without enabling Azure' {Assert-ServiceTarget $target eventflow dev -AllowDisabled;Reject{Assert-ServiceTarget $target eventflow dev}}
Case 'registered adapter resolves only approved source paths' {Check ((Get-WorkloadDefinition 'logic-app-event-grid').phaseParameter -eq 'releaseActivated');Reject{Get-WorkloadDefinition '../../script'}}
Case 'both patterns resolve independently' {$request=@{workloadType='logic-app-event-grid';workloadName='eventflow';environment='dev';region='eastus2'};Check ((Resolve-PlatformRequest $request @($target)).composition -eq 'workloads/logic-app-event-grid/main.bicep');$request.workloadType='blob-transfer';Reject{Resolve-PlatformRequest $request @($target)}}
Case 'valid platform parameters pass' {Assert-LogicParameters $target $p}
foreach($key in @('trustedServiceException','runtimeStorageCredentialException')){Case "unapproved $key rejected" {$bad=Clone $p;$bad[$key].value.approved=$false;Reject{Assert-LogicParameters $target $bad}}}
Case 'same subnet rejected' {$bad=Clone $p;$bad.privateEndpointSubnetId.value=$bad.integrationSubnetId.value;Reject{Assert-LogicParameters $target $bad}}
Case 'wrong DNS zone rejected' {$bad=Clone $p;$bad.privateDnsZoneIds.value.topic=$zones.blob;Reject{Assert-LogicParameters $target $bad}}
Case 'wrong region rejected' {$bad=Clone $p;$bad.location.value='westus';Reject{Assert-LogicParameters $target $bad}}
Case 'production needs alert recipients' {$t=Clone $target;$t.environmentName='prod';$bad=Clone $p;$bad.environmentName.value='prod';Reject{Assert-LogicParameters $t $bad}}
Case 'blob target cannot point to Logic App parameters' {$t=Clone $target;$t.schemaVersion=2;$t.Remove('workloadType');Reject{Assert-ServiceTarget $t eventflow dev -AllowDisabled}}
$compiled=Join-Path $testRoot stack-template.json
Invoke-Bicep -Arguments @('build',(Join-Path $root workloads/logic-app-event-grid/stack.bicep),'--outfile',$compiled)
$template=Get-Content $compiled -Raw|ConvertFrom-Json -AsHashtable
Case 'spec and stack are separate from blobcopy' {$contract=New-StackContract $target $template;Check($contract.stackName -eq 'stack-eventflow-dev' -and $contract.templateSpecId.Contains('/templateSpecs/logic-app-event-grid/'));Check((Read-StackConfiguration).templateSpec.name -eq 'blob-transfer')}
$bundle=@{target=$target;parameters=@{parameters=$p};stack=(New-StackContract $target $template)}
Case 'shared resources cannot become owned by Logic App stack' {Reject{Assert-StackManagedId $bundle $p.existingLogAnalyticsWorkspaceId.value}}
$change=@{changeType='Create';resourceId="/subscriptions/$sub/resourceGroups/rg-eventflow-dev/providers/Microsoft.Storage/storageAccounts/steveventflowdevabc123";after=@{properties=@{publicNetworkAccess='Enabled';allowBlobPublicAccess=$false;allowSharedKeyAccess=$false;networkAcls=@{bypass='AzureServices';defaultAction='Deny'}}}}
Case 'reviewed bridge create passes only contextual governance' {Assert-LogicChange $change $bundle;Reject{Assert-ServiceChange $change}}
Case 'arbitrary public storage create remains forbidden' {$c=Clone $change;$c.resourceId=$c.resourceId.Replace('steveventflowdevabc123','arbitrary');Reject{Assert-LogicChange $c $bundle}}
Case 'allow-all firewall is not a trusted-service exception' {$c=Clone $change;$c.after.properties.networkAcls.defaultAction='Allow';Reject{Assert-LogicChange $c $bundle}}
Case 'bridge does not accept extra IP access' {$c=Clone $change;$c.after.properties.networkAcls.ipRules=@(@{value='1.2.3.4'});Reject{Assert-LogicChange $c $bundle}}
Case 'existing network security changes remain blocked' {$c=Clone $change;$c.changeType='Modify';$c.delta=@(@{path='properties.publicNetworkAccess';propertyChangeType='Modify';before='Disabled';after='Enabled'});Reject{Assert-LogicChange $c $bundle}}
Case 'retail estimate includes Standard hosting and eight endpoints' {$cost=Get-LogicCostEstimate $p;Check ($cost.status -eq 'Estimated' -and $cost.lines[1].quantity -eq 8 -and $cost.fixedMonthlySubtotalUsd -gt 200)}
$release='logic-test-'+[guid]::NewGuid().ToString('N');& "$PSScriptRoot/Build-LogicPackage.ps1" -ReleaseId $release
$zip=Join-Path $root "artifacts/logic-releases/$release/$release.zip"
Case 'workflow package has exact inventory' {Test-LogicPackage $zip}
Case 'package rejects injected or extra file' {$badZip=Join-Path $testRoot extra.zip;Copy-Item $zip $badZip;$z=[IO.Compression.ZipFile]::Open($badZip,[IO.Compression.ZipArchiveMode]::Update);try{$null=$z.CreateEntry('../injected.json')}finally{$z.Dispose()};Reject{Test-LogicPackage $badZip}}
$workflow=Get-Content (Join-Path $root src/LogicAppEventFlow/process-event/workflow.json) -Raw|ConvertFrom-Json -AsHashtable
$actions=$workflow.definition.actions.For_each_message.actions.Process.actions
Case 'workflow has conditional write and acknowledgement durability gate' {Check ($actions.Create_receipt.inputs.headers['If-None-Match'] -eq '*' -and $actions.Durable_result.actions.Delete_message.inputs.method -eq 'DELETE' -and $actions.Durable_result.expression.Contains('Existing_receipt'))}
Case 'malformed event cannot reach a successful receipt branch' {Check ($actions.Validate_schema.inputs.schema.additionalProperties -eq $false -and $actions.Validate_scope.else.actions.Reject_scope.inputs.schema.required[0] -eq 'scopeMustMatch')}
Case 'conflicting duplicate does not acknowledge' {Check ($actions.Existing_receipt.actions.Compare_receipt.else.actions.Contains('Reject_conflict') -and !$actions.Existing_receipt.actions.Compare_receipt.else.actions.Contains('Delete_message'))}
Case 'retry quarantine precedes deletion' {$failure=$workflow.definition.actions.For_each_message.actions.Failed_message;Check($failure.expression.Contains(',5)') -and $failure.actions.Delete_quarantined.runAfter.Quarantine[0] -eq 'Succeeded')}
Case 'workflow exposes no public callback or managed connector requirement' {Check($workflow.definition.triggers.Poll.type -eq 'Recurrence');$c=Get-Content (Join-Path $root src/LogicAppEventFlow/connections.json) -Raw|ConvertFrom-Json -AsHashtable;Check(!$c.managedApiConnections.Count -and !$c.serviceProviderConnections.Count)}
$discovery=Join-Path $testRoot discovery
$now=[DateTimeOffset]::UtcNow.ToString('O')
$inventory=@{schemaVersion=1;readOnly=$true;generatedUtc=$now;subscription=@{id=$sub};discoveryStatus='Complete';workloadType='logic-app-event-grid';providers=@('Web','Storage','EventGrid','Insights','OperationalInsights'|ForEach-Object {@{namespace="Microsoft.$_";registrationState='Registered'}});resources=@()}
Write-ServiceJson $inventory (Join-Path $discovery inventory.json)
$manifest=@{schemaVersion=2;kind='workload-discovery';workloadType='logic-app-event-grid';generatedUtc=$now;discoveryStatus='Complete';inventorySha256=(Get-ServiceHash (Join-Path $discovery inventory.json));subscriptionId=$sub;serviceConnection=$target.serviceConnection;selection=@{workload='eventflow';environment='dev';region='eastus2';subscription=$target.subscriptionAlias;network=$target.networkProfile};source=@{runId='42'}}
Write-ServiceJson $manifest (Join-Path $discovery manifest.json)
Case 'typed discovery accepts complete scoped empty resource inventory' {$m=Read-DiscoveryManifest $discovery $target $target.serviceConnection;Check($m.workloadType -eq 'logic-app-event-grid')}
Case 'blob discovery cannot qualify Logic App workload' {$bad=Clone $manifest;$bad.schemaVersion=1;$bad.kind='blob-transfer-discovery';Write-ServiceJson $bad (Join-Path $discovery manifest.json);Reject{Read-DiscoveryManifest $discovery $target $target.serviceConnection};Write-ServiceJson $manifest (Join-Path $discovery manifest.json)}
foreach($key in @('region','network','subscription')){Case "discovery rejects mismatched $key" {$bad=Clone $manifest;$bad.selection[$key]='wrong';Write-ServiceJson $bad (Join-Path $discovery manifest.json);Reject{Read-DiscoveryManifest $discovery $target $target.serviceConnection};Write-ServiceJson $manifest (Join-Path $discovery manifest.json)}}
Case 'discovery rejects a changed inventory hash' {$bad=Clone $inventory;$bad.resources=@('changed');Write-ServiceJson $bad (Join-Path $discovery inventory.json);Reject{Read-DiscoveryManifest $discovery $target $target.serviceConnection};Write-ServiceJson $inventory (Join-Path $discovery inventory.json)}
Case 'event schema accepts a valid current contract' {$event=@{id=[guid]::NewGuid().ToString();topic='/subscriptions/test/topic';subject='/documents/test';eventType='Document.Received';eventTime=$now;dataVersion='1';metadataVersion='1';data=@{documentId='doc';correlationId='run'}};Check(Test-Json -Json ($event|ConvertTo-Json) -SchemaFile (Join-Path $root workloads/logic-app-event-grid/event.schema.json))}
Case 'event schema rejects unexpected data fields' {$event=@{id=[guid]::NewGuid().ToString();topic='/subscriptions/test/topic';subject='/documents/test';eventType='Document.Received';eventTime=$now;dataVersion='1';metadataVersion='1';data=@{documentId='doc';correlationId='run';url='https://untrusted.test'}};Reject{Test-Json -Json ($event|ConvertTo-Json) -SchemaFile (Join-Path $root workloads/logic-app-event-grid/event.schema.json) -ErrorAction Stop}}
# Preserve the one-element Event Grid batch through the actual transport helper.
function Invoke-ServiceJson {return @{accessToken='fixture-token'}}
function Invoke-RestMethod {param($Uri,$Method,$Headers,$ErrorAction,$TimeoutSec,$MaximumRedirection,$Body,$ContentType);$script:sentBody=$Body;return @{ok=$true}}
Case 'publisher sends one event as an array' {$null=Invoke-LogicDataRequest 'https://example.test/' POST 'https://eventgrid.azure.net/' -Body @(@{id='fixture'});Check($script:sentBody.StartsWith('['));Check(($script:sentBody|ConvertFrom-Json -NoEnumerate).Count -eq 1)}
Case 'resolved runtime key is redacted while its drift stays detectable' {$inputValue=@{nested=@(@{value='DefaultEndpointsProtocol=https;AccountKey=fixture-secret;EndpointSuffix=core.windows.net'})};$safe=Protect-LogicEvidence $inputValue;$text=ConvertTo-Canonical $safe;Check(!$text.Contains('fixture-secret') -and $text.Contains('redacted-sha256'));$inputValue.nested[0].value='AccountKey=different';Check((Get-ValueHash $safe) -cne (Get-ValueHash (Protect-LogicEvidence $inputValue)))}
Case 'evidence redaction preserves ARM expressions and arrays' {$value=@{value="[concat('AccountKey=',listKeys('id','version'))]";items=@('one')};Check((Get-ValueHash $value) -ceq (Get-ValueHash (Protect-LogicEvidence $value)))}
$fullInventory=Clone $inventory
$fullInventory.networks=@(@{subnets=@(@{id=$p.integrationSubnetId.value},@{id=$p.privateEndpointSubnetId.value})})
$fullInventory.privateDnsZones=@($zones.Values|ForEach-Object {@{id=$_}})
$fullInventory.resources=@(@{id=$p.existingLogAnalyticsWorkspaceId.value})
Write-ServiceJson $fullInventory (Join-Path $discovery inventory.json)
Case 'saved shared prerequisites must match effective parameters' {Assert-LogicDiscoveryResources $target $p $discovery}
Case 'empty discovery cannot qualify missing shared prerequisites' {Write-ServiceJson $inventory (Join-Path $discovery inventory.json);Reject{Assert-LogicDiscoveryResources $target $p $discovery};Write-ServiceJson $fullInventory (Join-Path $discovery inventory.json)}
# Assemble a local integrity fixture, not a qualified clean-source release.
$fixture=Join-Path $testRoot bundle
$enabled=Clone $target;$enabled.enabled=$true
Write-ServiceJson $enabled (Join-Path $fixture target.json)
Write-ServiceJson @{parameters=$p} (Join-Path $fixture parameters.json)
Copy-Item $compiled (Join-Path $fixture stack-template.json)
Write-ServiceJson $template (Join-Path $fixture main.json)
Write-ServiceJson (New-StackContract $enabled $template) (Join-Path $fixture stack.json)
Write-ServiceJson (Get-WorkloadDefinition 'logic-app-event-grid') (Join-Path $fixture workload-definition.json)
Write-ServiceJson (Get-LogicCostEstimate $p) (Join-Path $fixture cost-estimate.json)
Copy-Item $zip (Join-Path $fixture application.zip)
$manifest.inventorySha256=Get-ServiceHash (Join-Path $discovery inventory.json)
Write-ServiceJson $manifest (Join-Path $discovery manifest.json)
Copy-Item $discovery (Join-Path $fixture discovery) -Recurse
$files=@{};foreach($file in @('main.json','parameters.json','target.json','application.zip','stack-template.json','stack.json','workload-definition.json','cost-estimate.json','discovery/manifest.json','discovery/inventory.json')){$files[$file]=Get-ServiceHash (Join-Path $fixture $file)}
$fixtureReceipt=@{schemaVersion=2;workloadType='logic-app-event-grid';deploymentEngine='deploymentStack';releaseId='fixture';sourceCommit='fixture';files=$files;discoverySource=$manifest.source}
Write-ServiceJson $fixtureReceipt (Join-Path $fixture bundle.json)
Case 'typed frozen bundle validates file inventory and shared prerequisites' {$read=Read-ServiceBundle $fixture;Check($read.receipt.workloadType -eq 'logic-app-event-grid')}
Case 'bundle rejects changed package content' {$path=Join-Path $fixture application.zip;$saved=[IO.File]::ReadAllBytes($path);[IO.File]::WriteAllText($path,'changed');Reject{Read-ServiceBundle $fixture};[IO.File]::WriteAllBytes($path,$saved)}
Case 'bundle rejects changed discovery provenance' {$bad=Clone $fixtureReceipt;$bad.discoverySource.runId='99';Write-ServiceJson $bad (Join-Path $fixture bundle.json);Reject{Read-ServiceBundle $fixture};Write-ServiceJson $fixtureReceipt (Join-Path $fixture bundle.json)}

# Exercise actual apply dispatch with mocked I/O; successful provisioning must
# never manufacture readiness after failed package deployment or failed smoke.
function New-LogicPlan {return @{skip=$false;outputs=@{}}}
function Assert-ServicePlan {}
function Invoke-WorkloadStackApply {$script:sequence.Add('stack')}
function Get-LogicOutputs {return @{logicAppName=@{value='test'}}}
function Wait-LogicConnectivity {}
function Publish-LogicPackage {$script:sequence.Add('package');if($script:packageFails){throw 'fixture package failure'}}
function Invoke-LogicSmoke {$script:sequence.Add('smoke');if($script:smokeFails){throw 'fixture smoke failure'};return @{passed=$true}}
$script:sequence=[Collections.Generic.List[string]]::new();$script:packageFails=$false;$script:smokeFails=$false
$plan=Join-Path $testRoot plan;Write-ServiceJson @{} (Join-Path $plan plan.json)
Case 'Foundation never reports Ready or deploys workflow content' {$script:sequence.Clear();$r=Invoke-LogicApply $bundle Foundation $plan (Join-Path $testRoot foundation);Check(!$r.ready -and ($script:sequence -join ',') -eq 'stack')}
Case 'Release applies stack before content and smoke' {$script:sequence.Clear();$r=Invoke-LogicApply $bundle Release $plan (Join-Path $testRoot release);Check($r.ready -and ($script:sequence -join ',') -eq 'stack,package,smoke')}
Case 'failed content prevents smoke and Ready' {$script:sequence.Clear();$script:packageFails=$true;Reject{Invoke-LogicApply $bundle Release $plan (Join-Path $testRoot badpackage)};Check(!($script:sequence.Contains('smoke')));$script:packageFails=$false}
Case 'failed smoke prevents Ready' {$script:smokeFails=$true;Reject{Invoke-LogicApply $bundle Release $plan (Join-Path $testRoot badsmoke)};$script:smokeFails=$false}
Write-ServiceJson @{passed=$cases.Count;failed=0;cases=$cases;azureCalls=$false;logicAppsRuntimeExecuted=$false} (Join-Path $testRoot results.json)
Write-Host "PASS: $($cases.Count) Logic App contracts. No Azure calls or hosted workflow execution. Evidence: $testRoot"
