#requires -Version 7.4
. "$PSScriptRoot/workload-preview-common.ps1"
$root=Join-Path (Get-ProjectRoot) ('artifacts/preview-tests/'+[guid]::NewGuid().ToString('N'))
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


Case 'full release parameters include Function App and immutable package name' {
    $p=Get-PreviewParameters $target $bundle.parameters '42'
    Check $p.parameters.deployFunctionApp.value;Check ($p.parameters.packageBlobName.value -eq 'releases/42.zip')
    Check ($p.parameters.workloadResourceGroupName.value -eq $target.resourceGroup)
    Check (!$bundle.parameters.parameters.Contains('deployFunctionApp'))
}
Case 'Logic App preview activates full release without Function App parameters' {
    $t=Clone $target;$t.workloadType='logic-app-event-grid'
    $p=Get-PreviewParameters $t $bundle.parameters '42'
    Check $p.parameters.releaseActivated.value;Check (!$p.parameters.Contains('packageBlobName'))
}
# Supply a valid real discovery contract, without calling ADO or Azure.
$stamp=[DateTimeOffset]::UtcNow.ToString('O')
$inventory=@{schemaVersion=1;readOnly=$true;discoveryStatus='Complete';subscription=@{id=$target.subscriptionId};generatedUtc=$stamp;networks=@();privateDnsZones=@()}
Write-ServiceJson $inventory (Join-Path $dir discovery/inventory.json)
$manifest=@{schemaVersion=1;kind='blob-transfer-discovery';discoveryStatus='Complete';subscriptionId=$target.subscriptionId;serviceConnection=$target.serviceConnection;generatedUtc=$stamp;inventorySha256=(Get-ServiceHash (Join-Path $dir discovery/inventory.json));selection=@{workload=$target.workload;environment=$target.environmentName};source=@{runId='10';pipelineId='1';branch='refs/heads/main';commit='test'}}
Write-ServiceJson $manifest (Join-Path $dir discovery/manifest.json)
Write-ServiceJson (Get-PreviewParameters $target $bundle.parameters 'stack-test') (Join-Path $dir effective.parameters.json)
Write-ServiceJson @{status='Unavailable';fixedMonthlySubtotalUsd=$null} (Join-Path $dir cost-estimate.json)
$previewFiles=@{};foreach($f in @('target.json','main.json','parameters.json','stack-template.json','stack.json','effective.parameters.json','cost-estimate.json','discovery/manifest.json','discovery/inventory.json')){$previewFiles[$f]=Get-ServiceHash (Join-Path $dir $f)}
$inputReceipt=@{schemaVersion=1;kind='workload-preview-inputs';releaseId='stack-test';sourceCommit='test';runId=$env:BUILD_BUILDID;files=$previewFiles;discoverySource=$manifest.source}
Write-ServiceJson $inputReceipt (Join-Path $dir preview-inputs.json)
$savedBuild=$env:TF_BUILD;$env:TF_BUILD='False'
try{
Case 'frozen preview inputs validate without an application package' {$script:previewBundle=Read-WorkloadPreviewInputs $dir;Check ($script:previewBundle.stack.stackId -eq $bundle.stack.stackId)}
Case 'preview uses local compiled Bicep without publishing or applying' {
    $script:calls.Clear()
    $script:approved=Invoke-WorkloadInfrastructurePreview $script:previewBundle (Join-Path $dir azure)
    Check $script:approved.deployable;Check (!$script:approved.workloadDeployed)
    Check (@($script:calls|Where-Object {$_ -match '^stack sub validate .*--template-file '}).Count -eq 1)
    Check (@($script:calls|Where-Object {$_ -match '^stack-whatif sub create .*--template-file '}).Count -eq 1)
    Check (@($script:calls|Where-Object {$_ -match '^stack-whatif sub delete '}).Count -eq 1)
    Check (!@($script:calls|Where-Object {$_ -match '^ts |^stack sub create |^deployment |^storage '}).Count)
    Check ($script:approved.changes.Count -eq 3)
}
Case 'successful README lists each returned resource and no deployment claim' {
    Write-WorkloadPreviewReadme $dir 'Preview succeeded'
    $text=Get-Content (Join-Path $dir README.md) -Raw
    Check ($text.Contains($appId) -and $text.Contains('No workload resources were deployed') -and $text.Contains('Azure resource changes'))
}
Case 'identical qualified inputs match and resource drift fails' {
    $null=Assert-PreviewMatchesBundle $dir $bundle
    Assert-PreviewFingerprint $script:approved $script:approved
    $bad=Clone $script:approved;$bad.fingerprint='changed';Reject {Assert-PreviewFingerprint $script:approved $bad} 'drifted'
}
Case 'stale preview is rejected' {
    $bad=Clone $script:approved;$bad.createdUtc=[DateTimeOffset]::UtcNow.AddHours(-25).ToString('O')
    Write-ServiceJson $bad (Join-Path $dir azure/preview-plan.json)
    try{Reject {Assert-PreviewMatchesBundle $dir $bundle} 'expired'}finally{Write-ServiceJson $script:approved (Join-Path $dir azure/preview-plan.json)}
}
Case 'modified input hashes and plan fingerprints fail closed' {
    Add-Content (Join-Path $dir main.json) ' '
    try{Reject {Read-WorkloadPreviewInputs $dir} 'integrity'}finally{Write-ServiceJson @{resources=@()} (Join-Path $dir main.json)}
    $bad=Clone $script:approved;$bad.fingerprint='tampered';Write-ServiceJson $bad (Join-Path $dir azure/preview-plan.json)
    try{Reject {Assert-PreviewMatchesBundle $dir $bundle} 'integrity'}finally{Write-ServiceJson $script:approved (Join-Path $dir azure/preview-plan.json)}
}
Case 'bundle from another release or source rejected' {
    $bad=Clone $bundle;$bad.receipt.releaseId='other';Reject {Assert-PreviewMatchesBundle $dir $bad} 'differs'
    $bad=Clone $bundle;$bad.receipt.sourceCommit='other';Reject {Assert-PreviewMatchesBundle $dir $bad} 'differs'
}
Case 'preview from another pipeline run rejected' {
    $env:TF_BUILD='True'
    try{Reject {Read-WorkloadPreviewInputs $dir} 'different run or commit'}finally{$env:TF_BUILD='False'}
}
Case 'disabled target preview admission does not allow deployment' {
    $t=Clone $target;$t.enabled=$false;Assert-ServiceTarget $t $t.workload $t.environmentName -AllowDisabled
    Reject {Assert-ServiceTarget $t $t.workload $t.environmentName} 'disabled'
}
Case 'blocked Delete Detach and uncertain changes remain visible without a deployable plan' {
    foreach($mode in @('detach','potential')){
        $script:previewMode=$mode;$folder=Join-Path $root $mode
        Reject {Invoke-WorkloadInfrastructurePreview $script:previewBundle (Join-Path $folder azure)} ''
        Check (!(Test-Path (Join-Path $folder azure/preview-plan.json)))
        Write-WorkloadPreviewReadme $folder 'Blocked' 'Policy rejected the changes.'
        $text=Get-Content (Join-Path $folder README.md) -Raw;Check ($text.Contains($mode) -and $text.Contains('Blocker'))
    };$script:previewMode='ok'
}
Case 'failed preview rerun cannot leave an old deployable plan' {
    $folder=Join-Path $root retry/azure
    $null=Invoke-WorkloadInfrastructurePreview $script:previewBundle $folder
    Check (Test-Path (Join-Path $folder preview-plan.json))
    $script:previewMode='detach'
    try{Reject {Invoke-WorkloadInfrastructurePreview $script:previewBundle $folder} 'lifecycle';Check (!(Test-Path (Join-Path $folder preview-plan.json)))}finally{$script:previewMode='ok'}
}
Case 'property report includes nested differences unchanged deleted and redacted values' {
    $folder=Join-Path $root rendering;$raw=Preview @{parameters=@{deployFunctionApp=@{value=$true}}}
    $raw.properties.changes.resourceChanges=@(
        @{id='/example/create';changeType='create';changeCertainty='definite'},
        @{id='/example/delete';changeType='delete';changeCertainty='definite'},
        @{id='/example/noChange';changeType='noChange';changeCertainty='definite'},
        @{id='/example/modify';changeType='modify';changeCertainty='definite';resourceConfigurationChanges=@{before=@{secret='never-print-this';name='old'};after=@{secret='never-print-that';name='new'};delta=@(@{path='properties';changeType='modify';children=@(@{path='name';changeType='modify';before='old';after='new'},@{path='secret';changeType='modify';after='never-print-that'},@{path='caption';changeType='create';after='<script>|payload'})})}}
    )
    Write-ServiceJson $raw (Join-Path $folder azure/stack-what-if.json)
    Write-WorkloadPreviewReadme $folder 'Blocked' 'Deletion rejected'
    $text=Get-Content (Join-Path $folder README.md) -Raw
    foreach($word in @('/example/create','/example/delete','/example/noChange','properties.name','old','new','[redacted]','&lt;script&gt;&#124;payload')){Check ($text.Contains($word))}
    Check (!$text.Contains('never-print'))
}
Case 'blocked onboarding README lists exact fields and repository file' {
    $folder=Join-Path $root onboarding-report
    Write-ServiceJson @{parameterFile='workloads/logic-app-event-grid/environments/main.dev.bicepparam';issues=@(@{parameter='privateDnsZoneIds.topic';requirement='Select the existing topic DNS zone.'},@{parameter='owner';requirement='Supply the responsible team.'})} (Join-Path $folder onboarding-requirements.json)
    Write-WorkloadPreviewReadme $folder 'Blocked' 'Incomplete environment settings'
    $text=Get-Content (Join-Path $folder README.md) -Raw
    foreach($phrase in @('Required environment settings','privateDnsZoneIds.topic','main.dev.bicepparam','platform provisioning is required first','Azure changes unavailable')){Check ($text.Contains($phrase))}
}
Case 'missing Azure output produces an honest incomplete report' {
    $folder=Join-Path $root no-azure
    Write-ServiceJson @{resources=@(@{type='Microsoft.Web/sites';apiVersion='2024-01-01'})} (Join-Path $folder stack-template.json)
    Write-WorkloadPreviewReadme $folder 'Blocked' 'Missing configuration'
    $text=Get-Content (Join-Path $folder README.md) -Raw
    Check ($text.Contains('Azure changes unavailable') -and $text.Contains('Microsoft.Web/sites') -and $text.Contains('not expanded'))
}

Case 'Prepare compiles freezes discovery and full release without Azure access' {
    function Invoke-Bicep {param([string[]]$Arguments)
        $file=$Arguments[[Array]::IndexOf($Arguments,'--outfile')+1]
        $value=if($Arguments[0] -eq 'build-params'){@{parameters=$parameters}}elseif($Arguments[1].EndsWith('stack.bicep')){$template}else{@{resources=@()}}
        Write-ServiceJson $value $file
    }
    $script:calls.Clear();$folder=Join-Path $root prepared
    New-WorkloadPreviewInputs $target (Join-Path $dir discovery) $folder '42'
    $prepared=Read-WorkloadPreviewInputs $folder
    Check ($prepared.receipt.files.Count -eq 9 -and $prepared.receipt.releaseId -eq '42')
    Check ($script:calls.Count -eq 0)
}
# Exercise the actual Deploy coordinator with mocked infrastructure/application boundaries.
$script:sequence=[Collections.Generic.List[string]]::new();$script:drift=$false;$script:releaseFails=$false
function Read-ServiceBundle([string]$Directory){return $bundle}
function Read-WorkloadPreviewInputs([string]$Directory){return $script:previewBundle}
function Assert-PreviewMatchesBundle([string]$PreviewDirectory,$Bundle){return $script:approved}
function Invoke-WorkloadInfrastructurePreview($Bundle,[string]$Directory){$script:sequence.Add('Recheck');$result=Clone $script:approved;if($script:drift){$result.fingerprint='drift'};return $result}
function New-ServicePlan($Bundle,[string]$Phase,[string]$Directory){$script:sequence.Add("Plan$Phase");return @{}}
function Invoke-ServiceApply($Bundle,[string]$Phase,[string]$PlanDirectory,[string]$EvidenceDirectory){$script:sequence.Add("Apply$Phase");if($script:releaseFails -and $Phase -eq 'Release'){throw 'Runtime verification failed'};return @{status=$(if($Phase -eq 'Release'){'Ready'}else{'FoundationReady'});ready=($Phase -eq 'Release');outputs=@{}}}
$savedBranch=$env:BUILD_SOURCEBRANCH;$savedReason=$env:BUILD_REASON
$env:BUILD_SOURCEBRANCH='refs/heads/main';$env:BUILD_REASON='Manual'
try{
Case 'Deploy rechecks then applies Foundation and Release in order' {
    $script:sequence.Clear();$folder=Join-Path $root deploy-success
    Invoke-PreviewedWorkloadDeployment $dir $dir $folder $target.serviceConnection $target.deploymentEnvironment $target.agentPool
    Check (($script:sequence -join ',') -eq 'Recheck,PlanFoundation,ApplyFoundation,PlanRelease,ApplyRelease')
    $r=Get-Content (Join-Path $folder receipt.json) -Raw|ConvertFrom-Json -AsHashtable;Check $r.ready
}
Case 'drift aborts before Foundation or Release writes' {
    $script:sequence.Clear();$script:drift=$true
    try{Reject {Invoke-PreviewedWorkloadDeployment $dir $dir (Join-Path $root deploy-drift) $target.serviceConnection $target.deploymentEnvironment $target.agentPool} 'drifted';Check (($script:sequence -join ',') -eq 'Recheck')}finally{$script:drift=$false}
}
Case 'runtime verification failure never emits Ready' {
    $script:releaseFails=$true;$folder=Join-Path $root deploy-failed
    try{Reject {Invoke-PreviewedWorkloadDeployment $dir $dir $folder $target.serviceConnection $target.deploymentEnvironment $target.agentPool} 'Runtime';$r=Get-Content (Join-Path $folder receipt.json) -Raw|ConvertFrom-Json -AsHashtable;Check (!$r.ready -and $r.status -eq 'Failed')}finally{$script:releaseFails=$false}
}
Case 'wrong service connection fails before Azure recheck' {
    $script:sequence.Clear();Reject {Invoke-PreviewedWorkloadDeployment $dir $dir (Join-Path $root wrong-binding) 'other' $target.deploymentEnvironment $target.agentPool} 'binding';Check (!$script:sequence.Count)
}
Case 'feature branch cannot apply reviewed preview' {
    $env:BUILD_SOURCEBRANCH='refs/heads/feature';$script:sequence.Clear()
    Reject {Invoke-PreviewedWorkloadDeployment $dir $dir (Join-Path $root feature) $target.serviceConnection $target.deploymentEnvironment $target.agentPool} 'main';Check (!$script:sequence.Count)
}
}finally{$env:BUILD_SOURCEBRANCH=$savedBranch;$env:BUILD_REASON=$savedReason}
}finally{$env:TF_BUILD=$savedBuild}
$report=@{passed=$results.Count;failed=0;azureCalls='mocked';cases=$results}
Write-ServiceJson $report (Join-Path $root results.json)
Write-ServiceJson $report (Join-Path (Get-ProjectRoot) artifacts/test-results/workload-preview.json)
Write-Host "PASS: $($results.Count) preview contracts. Evidence: $root"
