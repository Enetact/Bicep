# Two-stage infrastructure preview. Azure calls retain the shared mockable boundary.
. "$PSScriptRoot/discovery-manifest-common.ps1"
. "$PSScriptRoot/platform-contract.ps1"
. "$PSScriptRoot/service-cost-common.ps1"
. "$PSScriptRoot/recovery-common.ps1"
function Get-PreviewParameters($Target,$Parameters,[string]$ReleaseId) {
    $p=$Parameters|ConvertTo-Json -Depth 100|ConvertFrom-Json -AsHashtable
    $type=Get-TargetWorkloadType $Target
    $p.parameters[(Get-WorkloadDefinition $type).phaseParameter]=@{value=$true}
    $p.parameters.workloadResourceGroupName=@{value=$Target.resourceGroup}
    if((Get-WorkloadDefinition $type).packageKind -in @('functions','productFunctions')){$p.parameters.packageBlobName=@{value="releases/$ReleaseId.zip"}}
    return $p
}
function New-WorkloadPreviewInputs($Target,[string]$DiscoveryDirectory,[string]$Directory,[string]$ReleaseId) {
    if($ReleaseId -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$'){throw 'Invalid preview release ID.'}
    $root=Get-ProjectRoot;$type=Get-TargetWorkloadType $Target;$definition=Get-WorkloadDefinition $type
    $manifest=Read-DiscoveryManifest $DiscoveryDirectory $Target $Target.serviceConnection
    New-Item -ItemType Directory -Path $Directory -Force|Out-Null
    Write-ServiceJson $Target (Join-Path $Directory target.json)
    # Informational recovery rules, not an approval artifact or proof of a retained baseline.
    Write-ServiceJson (Get-RecoveryPolicy $type) (Join-Path $Directory recovery-policy.json)
    Invoke-Bicep -Arguments @('build',(Join-Path $root $definition.composition),'--outfile',(Join-Path $Directory main.json))
    Invoke-Bicep -Arguments @('build',(Join-Path $root $definition.stack),'--outfile',(Join-Path $Directory stack-template.json))
    Invoke-Bicep -Arguments @('build-params',(Resolve-ServicePath $root $Target.parameterFile),'--outfile',(Join-Path $Directory parameters.json))
    $p=Get-Content (Join-Path $Directory parameters.json) -Raw|ConvertFrom-Json -AsHashtable
    Set-ServiceProfileParameters $Target $p.parameters
    if($type -eq 'logic-app-event-grid'){Set-LogicDiscoveredPrerequisites $Target $p.parameters $DiscoveryDirectory $Directory}
    if($type -eq 'blob-transfer'){$platform=Read-PlatformConfiguration;Set-ServiceDeploymentOptions $Target $p.parameters $platform.createDestinationPrivateEndpoints $platform.enableLogAlerts}
    Write-ServiceJson $p (Join-Path $Directory parameters.json)
    if($type -eq 'logic-app-event-grid'){
        Write-ServiceJson @{schemaVersion=1;parameterFile=$Target.parameterFile;issues=@(Get-LogicOnboardingIssues $p.parameters)} (Join-Path $Directory onboarding-requirements.json)
    }
    # Compile first so a blocked README can still list locally declared resource types.
    Assert-ServiceParameters $Target $p.parameters
    if(Test-ProductWorkload $type){Assert-ProductDiscovery $Target $p.parameters $DiscoveryDirectory}
    if($type -eq 'logic-app-event-grid'){Assert-LogicDiscoveryResources $Target $p.parameters $DiscoveryDirectory}
    $stackTemplate=Get-Content (Join-Path $Directory stack-template.json) -Raw|ConvertFrom-Json -AsHashtable
    Write-ServiceJson (New-StackContract $Target $stackTemplate) (Join-Path $Directory stack.json)
    Write-ServiceJson (Get-PreviewParameters $Target $p $ReleaseId) (Join-Path $Directory effective.parameters.json)
    $cost=if(Test-ProductWorkload $type){Get-ProductCostEstimate $Target $p.parameters}elseif($type -eq 'logic-app-event-grid'){Get-LogicCostEstimate $p.parameters}else{Get-ServiceCostEstimate $p.parameters}
    Write-ServiceJson $cost (Join-Path $Directory cost-estimate.json)
    New-Item -ItemType Directory -Path (Join-Path $Directory discovery) -Force|Out-Null
    foreach($f in @('manifest.json','inventory.json')){Copy-Item (Join-Path $DiscoveryDirectory $f) (Join-Path $Directory "discovery/$f") -Force}
    $files=@{};foreach($f in @('target.json','main.json','parameters.json','stack-template.json','stack.json','effective.parameters.json','cost-estimate.json','discovery/manifest.json','discovery/inventory.json')){$files[$f]=Get-ServiceHash (Join-Path $Directory $f)}
    $commit=& git -C $root rev-parse HEAD
    if($LASTEXITCODE -ne 0){throw 'Preview source provenance unavailable.'}
    Write-ServiceJson @{schemaVersion=1;kind='workload-preview-inputs';releaseId=$ReleaseId;sourceCommit=$commit;runId=$env:BUILD_BUILDID;files=$files;discoverySource=$manifest.source;createdUtc=[DateTimeOffset]::UtcNow.ToString('O')} (Join-Path $Directory preview-inputs.json)
}
function Read-WorkloadPreviewInputs([string]$Directory) {
    $r=Get-Content (Join-Path $Directory preview-inputs.json) -Raw|ConvertFrom-Json -AsHashtable
    $expected=@('target.json','main.json','parameters.json','stack-template.json','stack.json','effective.parameters.json','cost-estimate.json','discovery/manifest.json','discovery/inventory.json')
    if($r.schemaVersion -ne 1 -or $r.kind -cne 'workload-preview-inputs' -or $r.files.Count -ne $expected.Count){throw 'Invalid preview input contract.'}
    foreach($f in $expected){if(!$r.files.Contains($f) -or (Get-ServiceHash (Resolve-ServicePath $Directory $f)) -cne $r.files[$f]){throw "Preview input integrity failure: $f"}}
    $target=Get-Content (Join-Path $Directory target.json) -Raw|ConvertFrom-Json -AsHashtable
    Assert-ServiceTarget $target $target.workload $target.environmentName -AllowDisabled
    $p=Get-Content (Join-Path $Directory parameters.json) -Raw|ConvertFrom-Json -AsHashtable
    Assert-ServiceParameters $target $p.parameters
    $manifest=Read-DiscoveryManifest (Join-Path $Directory discovery) $target $target.serviceConnection
    if(Test-ProductWorkload (Get-TargetWorkloadType $target)){Assert-ProductDiscovery $target $p.parameters (Join-Path $Directory discovery)}
    if((Get-TargetWorkloadType $target) -eq 'logic-app-event-grid'){Assert-LogicDiscoveryResources $target $p.parameters (Join-Path $Directory discovery)}
    if((Get-ValueHash $manifest.source) -cne (Get-ValueHash $r.discoverySource)){throw 'Preview discovery provenance mismatch.'}
    $stack=Get-Content (Join-Path $Directory stack.json) -Raw|ConvertFrom-Json -AsHashtable
    $template=Get-Content (Join-Path $Directory stack-template.json) -Raw|ConvertFrom-Json -AsHashtable
    Assert-StackContract $stack $target $template
    $effective=Get-Content (Join-Path $Directory effective.parameters.json) -Raw|ConvertFrom-Json -AsHashtable
    if((Get-ValueHash $effective) -cne (Get-ValueHash (Get-PreviewParameters $target $p $r.releaseId))){throw 'Preview did not evaluate the full release.'}
    if($env:TF_BUILD -eq 'True' -and ($r.runId -cne $env:BUILD_BUILDID -or $r.sourceCommit -cne $env:BUILD_SOURCEVERSION)){throw 'Preview belongs to a different run or commit.'}
    return @{receipt=$r;target=$target;parameters=$p;stack=$stack;directory=$Directory;hash=(Get-ServiceHash (Join-Path $Directory preview-inputs.json))}
}
function Invoke-WorkloadInfrastructurePreview($Bundle,[string]$Directory) {
    New-Item -ItemType Directory -Path $Directory -Force|Out-Null
    foreach($file in @('preview-plan.json','stack-what-if.json','arm-validation.json')){
        $old=Join-Path $Directory $file
        if(Test-Path -LiteralPath $old){Remove-Item -LiteralPath $old -Force}
    }
    Assert-StackTooling
    $state=Get-WorkloadStackState $Bundle
    if(Test-ProductWorkload (Get-TargetWorkloadType $Bundle.target)){$state.network=Test-ProductPrerequisites $Bundle}
    if((Get-TargetWorkloadType $Bundle.target) -eq 'logic-app-event-grid'){Assert-LogicPrerequisiteLiveState $Bundle $state}
    $path=Join-Path $Bundle.directory effective.parameters.json
    $report=New-StackPreview $Bundle $state $path $Directory -UseLocalTemplate
    $plan=@{schemaVersion=1;kind='full-release-preview';inputHash=$Bundle.hash;sourceCommit=$Bundle.receipt.sourceCommit;runId=$Bundle.receipt.runId;createdUtc=[DateTimeOffset]::UtcNow.ToString('O');state=$state;changes=$report.changes;deployable=$true;workloadDeployed=$false}
    $plan.fingerprint=Get-ValueHash @{inputHash=$plan.inputHash;state=$state;changes=$report.changes}
    Write-ServiceJson $plan (Join-Path $Directory preview-plan.json)
    return $plan
}
function Assert-PreviewMatchesBundle([string]$PreviewDirectory,$Bundle) {
    $preview=Read-WorkloadPreviewInputs $PreviewDirectory
    $approved=Get-Content (Join-Path $PreviewDirectory azure/preview-plan.json) -Raw|ConvertFrom-Json -AsHashtable
    if($approved.kind -cne 'full-release-preview' -or $approved.schemaVersion -ne 1 -or $approved.deployable -isnot [bool] -or !$approved.deployable -or $approved.inputHash -cne $preview.hash){throw 'A successful current-run preview is required.'}
    if($approved.fingerprint -cne (Get-ValueHash @{inputHash=$approved.inputHash;state=$approved.state;changes=$approved.changes}) -or $approved.sourceCommit -cne $preview.receipt.sourceCommit -or $approved.runId -cne $preview.receipt.runId){throw 'Preview plan integrity failure.'}
    $age=[DateTimeOffset]::UtcNow-[DateTimeOffset]::Parse($approved.createdUtc)
    if($age.TotalHours -gt 24 -or $age.TotalMinutes -lt -5){throw 'Preview expired; rerun Preview.'}
    if((Get-ValueHash $Bundle.target) -cne (Get-ValueHash $preview.target) -or $Bundle.receipt.sourceCommit -cne $preview.receipt.sourceCommit -or $Bundle.receipt.releaseId -cne $preview.receipt.releaseId){throw 'Deployment bundle differs from the preview target/source/release.'}
    foreach($file in @('main.json','stack-template.json','stack.json','parameters.json','discovery/manifest.json','discovery/inventory.json')){
        $a=Get-Content (Join-Path $Bundle.directory $file) -Raw|ConvertFrom-Json -AsHashtable
        $b=Get-Content (Join-Path $PreviewDirectory $file) -Raw|ConvertFrom-Json -AsHashtable
        if((Get-ValueHash $a) -cne (Get-ValueHash $b)){throw "Deployment input changed after Preview: $file"}
    }
    return $approved
}
function Assert-PreviewFingerprint($Approved,$Current) {
    if($Approved.fingerprint -cne $Current.fingerprint){throw 'Azure resources or planned changes drifted after Preview. Run a new preview; no workload deployment performed.'}
}
function Protect-PreviewValue($Value) {
    if($Value -is [Collections.IDictionary]){
        $copy=@{};foreach($key in $Value.Keys){$copy[$key]=if($key -match '(?i)password|secret|accountkey|connectionstring|sasToken|accessToken'){'[redacted]'}else{Protect-PreviewValue $Value[$key]}};return $copy
    }
    if($Value -is [array]){return ,@($Value|ForEach-Object {Protect-PreviewValue $_})}
    return $Value
}
function ConvertTo-PreviewCell($Value,[string]$Property='') {
    if($Property -match '(?i)password|secret|accountkey|connectionstring|sasToken|accessToken'){return '[redacted]'}
    $text=if($null -eq $Value){'(not supplied)'}elseif($Value -is [string]){$Value}else{ConvertTo-Json -InputObject (Protect-PreviewValue $Value) -Depth 100 -Compress}
    $text=[regex]::Replace($text,'(?i)AccountKey=[^;"\s]+','AccountKey=[redacted]')
    return [Net.WebUtility]::HtmlEncode($text).Replace('|','&#124;').Replace("`r",'').Replace("`n",'<br>')
}
function Get-PreviewDeltaRows($Deltas,[string]$Prefix='') {
    foreach($d in $Deltas){
        $path="$Prefix$($d.path)"
        '| '+(ConvertTo-PreviewCell $path)+' | '+(ConvertTo-PreviewCell $d.changeType)+' | '+(ConvertTo-PreviewCell $d['before'] $path)+' | '+(ConvertTo-PreviewCell $d['after'] $path)+' |'
        if($d.Contains('children')){Get-PreviewDeltaRows $d.children "$path."}
    }
}
function Write-WorkloadPreviewReadme([string]$Directory,[string]$Status,[string]$ErrorText='') {
    New-Item -ItemType Directory -Path $Directory -Force|Out-Null
    $lines=@('# Bicep deployment preview','',"**Status: $(ConvertTo-PreviewCell $Status)**",'', 'No workload resources were deployed by this stage. The full release is evaluated, including the application host. Workflow/Function package contents are separate application changes, not ARM property changes.','', 'This pipeline uses Deployment Stacks. Preview validates the compiled Bicep and creates a temporary Azure stack What-If result, then requests its deletion. It does not publish a Template Spec or apply the workload stack. Only preview metadata is written to Azure.','')
    if($ErrorText){$lines+=@('## Blocker','', (ConvertTo-PreviewCell $ErrorText),'','Do not interpret a failed or incomplete preview as zero changes. Deploy is blocked; correct the reported prerequisites or policy findings and rerun.','')}
    if(Test-Path (Join-Path $Directory recovery-policy.json)){
        $recovery=Get-Content (Join-Path $Directory recovery-policy.json) -Raw|ConvertFrom-Json -AsHashtable
        $lines+=@('## Recovery rules','', (ConvertTo-PreviewCell $recovery.policy.summary),'','Recovery eligibility has not been assessed. No restore executor is registered. These rules do not guarantee that this deployment can be undone.','', '| Excluded from this recovery policy |','|---|')
        foreach($exclusion in $recovery.policy.exclusions){$lines+='| '+(ConvertTo-PreviewCell $exclusion)+' |'}
        $lines+=@('','Required checks (all must be independently verified before any future recovery execution):','')
        foreach($check in $recovery.checks){$lines+='- '+(ConvertTo-PreviewCell $check.description)}
        $lines+=@('','See recovery-policy.json for policy identity and catalog hash. A fresh recovery Preview and protected approval are required; the forward Preview is not a restore plan.','')
    }
    if(Test-Path (Join-Path $Directory target.json)){$t=Get-Content (Join-Path $Directory target.json) -Raw|ConvertFrom-Json -AsHashtable;$lines+=@("Workload: $(ConvertTo-PreviewCell (Get-TargetWorkloadType $t)) / $(ConvertTo-PreviewCell $t.workload) / $(ConvertTo-PreviewCell $t.environmentName).","Subscription: $(ConvertTo-PreviewCell $t.subscriptionId). Resource group: $(ConvertTo-PreviewCell $t.resourceGroup).","Deployment enabled: $($t.enabled). Disabled targets can be previewed but cannot deploy.",'')}
    if(Test-Path (Join-Path $Directory cost-estimate.json)){$cost=Get-Content (Join-Path $Directory cost-estimate.json) -Raw|ConvertFrom-Json -AsHashtable;$lines+=@("Cost status: $(ConvertTo-PreviewCell $cost.status). Fixed monthly subtotal USD: $(ConvertTo-PreviewCell $cost['fixedMonthlySubtotalUsd']); usage is additional. See cost-estimate.json.",'')}
    $onboardingPath=Join-Path $Directory onboarding-requirements.json
    if(Test-Path -LiteralPath $onboardingPath){
        $onboarding=Get-Content -LiteralPath $onboardingPath -Raw|ConvertFrom-Json -AsHashtable
        if($onboarding.issues.Count){
            $lines+=@('## Required environment settings','',"Update repository file: $(ConvertTo-PreviewCell $onboarding.parameterFile)",'','| Parameter | Required action |','|---|---|')
            foreach($issue in $onboarding.issues){$lines+="| $(ConvertTo-PreviewCell $issue.parameter) | $(ConvertTo-PreviewCell $issue.requirement) |"}
            $lines+=@('','Discovery lists available resources; it does not choose approved IDs or grant platform approvals. Review the prerequisite plan for Reuse/Create/Manage decisions. Owner, cost-center, identity and platform approvals still require real values. The target can remain disabled for Preview.','')
        }
    }
    $prerequisitePath=Join-Path $Directory prerequisite-plan.json
    if(Test-Path -LiteralPath $prerequisitePath){$lines+=@(Get-LogicPrerequisiteSummary (Get-Content -LiteralPath $prerequisitePath -Raw|ConvertFrom-Json -AsHashtable))}
    $rawPath=Join-Path $Directory azure/stack-what-if.json
    if(Test-Path $rawPath){
        $raw=Get-Content $rawPath -Raw|ConvertFrom-Json -AsHashtable
        $resources=@($raw.properties.changes.resourceChanges)
        $lines+=@('## Azure resource changes','', 'Every resource returned by Azure is listed below, including unchanged and uncertain results. A Delete or Detach is shown even though self-service governance blocks applying it. This is not a subscription-wide inventory.','', '| Action | Count |','|---|---:|')
        foreach($group in @($resources|Group-Object changeType|Sort-Object Name)){$lines+="| $(ConvertTo-PreviewCell $group.Name) | $($group.Count) |"}
        $lines+=@('','| Action | Certainty | Resource ID |','|---|---|---|')
        foreach($r in @($resources|Sort-Object id)){$lines+="| $(ConvertTo-PreviewCell $r.changeType) | $(ConvertTo-PreviewCell $r.changeCertainty) | $(ConvertTo-PreviewCell $r.id) |"}
        foreach($r in @($resources|Sort-Object id)){
            $lines+=@('',"### $(ConvertTo-PreviewCell $r.changeType): $(ConvertTo-PreviewCell $r.id)",'')
            if($r.Contains('resourceConfigurationChanges') -and $r.resourceConfigurationChanges){
                $c=$r.resourceConfigurationChanges
                if($c.Contains('delta') -and $c.delta.Count){$lines+=@('| Property | Action | Before | After |','|---|---|---|---|');$lines+=@(Get-PreviewDeltaRows $c.delta)}
                else{$lines+='No property deltas supplied by Azure.'}
                foreach($side in @('before','after')){if($c.Contains($side)){$lines+=@('',"$side configuration: $(ConvertTo-PreviewCell $c[$side])")}}
            }
            foreach($key in @('managementStatusChange','denyStatusChange')){if($r.Contains($key)){$lines+="$(ConvertTo-PreviewCell $key): $(ConvertTo-PreviewCell $r[$key])"}}
        }
        if($raw.properties.Contains('diagnostics') -and @($raw.properties.diagnostics).Count){$lines+=@('','## Azure diagnostics','',(ConvertTo-PreviewCell $raw.properties.diagnostics))}
        $lines+=@('','Stack deny/scope changes: '+(ConvertTo-PreviewCell @{deny=$raw.properties.changes.denySettingsChange;scope=$raw.properties.changes['deploymentScopeChange']}))
    }else{$lines+=@('## Azure changes unavailable','','No successful Azure resource-change report was captured. The local declarations below are not an Azure What-If result.','')}
    if(Test-Path (Join-Path $Directory stack-template.json)){
        $template=Get-Content (Join-Path $Directory stack-template.json) -Raw|ConvertFrom-Json -AsHashtable
        $pending=[Collections.Generic.Queue[object]]::new();$pending.Enqueue($template);$types=@()
        while($pending.Count){$node=$pending.Dequeue();if($node -is [Collections.IDictionary]){if($node.Contains('type') -and $node.Contains('apiVersion') -and $node.type -is [string]){$types+=$node.type};foreach($v in $node.Values){if($null -ne $v){$pending.Enqueue($v)}}}elseif($node -is [array]){foreach($v in $node){if($null -ne $v){$pending.Enqueue($v)}}}}
        $lines+=@('','## Locally declared resource types','','These are template declaration counts, not expanded loop/condition instance counts or proof that Azure analyzed every resource.','', '| Resource type | Declarations |','|---|---:|')
        foreach($g in @($types|Group-Object|Sort-Object Name)){$lines+="| $(ConvertTo-PreviewCell $g.Name) | $($g.Count) |"}
    }
    $lines+=@('','## Review and next step','','Inspect all resource/property changes, Azure diagnostics, cost-estimate.json, effective.parameters.json and stack-template.json in deployment-preview. Azure What-If can leave expressions or resources unresolved. Incomplete/potential changes, Delete/Detach and disallowed security changes block Deploy.','', 'Preview only is the default. To apply, queue a fresh run with Preview and deploy; approve Deploy after reviewing that run''s report. Deploy requires an enabled target, consumes the same run''s frozen preview, compares the compiled inputs and rechecks drift before applying. The deployment path keeps Template Specs, stack ownership, Foundation/Release sequencing and application readiness checks.','', 'Azure commands: az stack sub validate --template-file ...; az stack-whatif sub create --template-file .... Actual apply occurs only in Deploy through az stack sub create --template-spec ....')
    # Single-quoted PowerShell strings escape apostrophes by doubling, not backslash.
    [IO.File]::WriteAllText((Join-Path $Directory README.md),($lines -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
}

function Invoke-PreviewedWorkloadDeployment([string]$PreviewDirectory,[string]$BundleDirectory,[string]$EvidenceDirectory,[string]$BoundServiceConnection,[string]$BoundEnvironment,[string]$BoundAgentPool) {
    $receipt=@{schemaVersion=1;ready=$false;status='Failed';startedUtc=[DateTimeOffset]::UtcNow.ToString('O')}
    try{
        if($env:BUILD_SOURCEBRANCH -cne 'refs/heads/main' -or $env:BUILD_REASON -cne 'Manual'){throw 'Deploy requires a manually queued protected main run.'}
        $bundle=Read-ServiceBundle $BundleDirectory
        $target=$bundle.target
        $policy=Get-RecoveryPolicy (Get-TargetWorkloadType $target)
        $receipt.recovery=@{policyId=$policy.policy.id;mode=$policy.policy.mode;catalogSha256=$policy.catalogSha256;assessmentStatus='Not assessed';canExecute=$false;executorRegistered=$false}
        if($target.serviceConnection -cne $BoundServiceConnection -or $target.deploymentEnvironment -cne $BoundEnvironment -or $target.agentPool -cne $BoundAgentPool){throw 'Deployment protected resource binding mismatch.'}
        $approved=Assert-PreviewMatchesBundle $PreviewDirectory $bundle
        $preview=Read-WorkloadPreviewInputs $PreviewDirectory
        $current=Invoke-WorkloadInfrastructurePreview $preview (Join-Path $EvidenceDirectory recheck)
        Assert-PreviewFingerprint $approved $current
        $definition=Get-WorkloadDefinition (Get-TargetWorkloadType $target)
        $phases=if($definition.packageKind -eq 'infrastructure'){@('Release')}else{@('Foundation','Release')}
        foreach($phase in $phases){
            $planDirectory=Join-Path $EvidenceDirectory "plan-$phase"
            $null=New-ServicePlan $bundle $phase $planDirectory
            $result=Invoke-ServiceApply $bundle $phase $planDirectory (Join-Path $EvidenceDirectory "result-$phase")
            $receipt[$phase]=@{status=$result.status;ready=$result.ready}
        }
        $receipt.status=$result.status;$receipt.ready=$result.ready;$receipt.outputs=$result.outputs
        if($result.Contains('smoke')){$receipt.smoke=$result.smoke}
        if($result.Contains('acceptance')){$receipt.acceptance=$result.acceptance}
    }catch{$receipt.error=$_.Exception.Message;throw}
    finally{
        $receipt.finishedUtc=[DateTimeOffset]::UtcNow.ToString('O')
        Write-ServiceJson $receipt (Join-Path $EvidenceDirectory receipt.json)
    }
}
