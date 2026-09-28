#requires -Version 7.4
. "$PSScriptRoot/recovery-common.ps1"
$cases=[Collections.Generic.List[object]]::new()
function Check([bool]$Value){if(!$Value){throw 'Recovery rule assertion failed.'}}
function Case([string]$Name,[scriptblock]$Body){& $Body;$cases.Add(@{name=$Name;passed=$true})}
function Reject([scriptblock]$Body){$failed=$false;try{& $Body|Out-Null}catch{$failed=$true};Check $failed}
function Candidate([string]$Id='private-storage'){
    $p=Get-RecoveryPolicy $Id;$facts=@{};foreach($c in $p.checks){$facts[$c.id]=$true}
    return @{schemaVersion=1;kind='recovery-candidate';workflow=$Id;outcome='FailedReconciled';targetScope='/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/test';checks=$facts;changes=@(@{resourceId='/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/test/providers/Microsoft.Storage/storageAccounts/example';action='Modify';certainty='Definite';impact='MutableConfiguration'})}
}
function Blocked($c,[string]$code){$r=Get-RecoveryAssessment $c;Check ($r.status -ceq 'Blocked');Check ($code -cin @($r.issues.code));Check (!$r.canExecute)}
Case 'all registered products and operations have rules; configuration cannot enable execution' {
    $catalog=Read-RecoveryCatalog;Check ($catalog.policies.Count -eq 14);Check (!$catalog.executionEnabled)
    $workloads=(Get-Content (Join-Path (Get-ProjectRoot) config/workloads.json) -Raw|ConvertFrom-Json -AsHashtable).workloads
    foreach($id in $workloads.Keys){Check ((Get-RecoveryPolicy $id).policy.id -ceq $id)}
}
Case 'unknown policy and malformed candidate rejected' {Reject {Get-RecoveryPolicy 'arbitrary-script'};Reject {Get-RecoveryAssessment @{schemaVersion=2}}}
Case 'complete submitted checks remain unverified and never authorize execution' {
    $r=Get-RecoveryAssessment (Candidate);Check ($r.status -ceq 'ChecksSatisfiedUnverified');Check (!$r.canExecute -and !$r.executorRegistered);Check ($r.evidenceTrust -match 'not verified')
}
foreach($id in @(Get-RecoveryCheckIds 'ConfigurationRestore')){
    Case "missing or false prerequisite blocks: $id" {$c=Candidate;$c.checks.Remove($id);Blocked $c $id;$c.checks[$id]=$false;Blocked $c $id}
}
Case 'truthy strings do not satisfy boolean checks' {$c=Candidate;$c.checks.noDrift='true';Blocked $c 'noDrift'}
foreach($outcome in @('Unknown','Pending','Failed','Cancelled','')){
    Case "unreconciled outcome blocks: $outcome" {$c=Candidate;$c.outcome=$outcome;Blocked $c 'outcomeUnknown'}
}
foreach($action in @('Create','Delete','Detach','Recreate','ReleaseAllocation','RestoreData','ReplayEvents','RestoreSecret','ChangeIdentity','RemoveTag','Unknown')){
    Case "unsupported action blocks: $action" {$c=Candidate;$c.changes[0].action=$action;Blocked $c 'unsupportedAction'}
}
Case 'missing or empty changes cannot masquerade as no change' {$c=Candidate;$c.Remove('changes');Blocked $c 'changesMissing';$c.changes=@();Blocked $c 'changesMissing'}
Case 'explicit definite NoChange may satisfy checks without authority' {$c=Candidate;$c.changes[0].action='NoChange';Check ((Get-RecoveryAssessment $c).status -ceq 'ChecksSatisfiedUnverified')}
Case 'potential changes and missing impact block' {$c=Candidate;$c.changes[0].certainty='Potential';Blocked $c 'uncertainChange';$c.changes[0].Remove('impact');Blocked $c 'unsupportedImpact'}
Case 'data or credentials are not mutable configuration' {$c=Candidate;$c.changes[0].impact='RestoreData';Blocked $c 'unsupportedImpact'}
Case 'resource-group prefix collision blocks' {$c=Candidate;$c.changes[0].resourceId=$c.changes[0].resourceId.Replace('/test/','/test-other/');Blocked $c 'scopeOrDuplicate'}
Case 'malformed subscription scope blocks' {$c=Candidate;$c.targetScope='/subscriptions/------------------------------------/resourceGroups/test';Blocked $c 'invalidScope'}
Case 'incomplete provider resource ID blocks' {$c=Candidate;$c.changes[0].resourceId=$c.targetScope+'/providers/Microsoft.Storage';Blocked $c 'scopeOrDuplicate'}
Case 'cross-subscription and malformed resource IDs block' {$c=Candidate;$c.changes[0].resourceId=$c.changes[0].resourceId.Replace('11111111-','22222222-');Blocked $c 'scopeOrDuplicate';$c=Candidate;$c.changes[0].resourceId+='?query=bad';Blocked $c 'scopeOrDuplicate'}
Case 'duplicate resource IDs ignore case' {$c=Candidate;$copy=$c.changes[0].Clone();$copy.resourceId=$copy.resourceId.ToUpperInvariant();$c.changes+=@($copy);Blocked $c 'scopeOrDuplicate'}
Case 'app recovery requires package compatibility and processing impact review' {$c=Candidate 'http-functions';$c.checks.Remove('packageCompatible');Blocked $c 'packageCompatible';$c.checks.Remove('processingImpactReviewed');Blocked $c 'processingImpactReviewed'}
Case 'tag compensation requires ownership and matching current values' {$c=Candidate 'tags-external';$c.changes[0].action='RestoreTag';$c.changes[0].impact='OwnedTagValues';Check ((Get-RecoveryAssessment $c).status -ceq 'ChecksSatisfiedUnverified');$c.checks.tagCurrentMatchesApplied=$false;Blocked $c 'tagCurrentMatchesApplied';$c.checks.tagKeysOwned=$false;Blocked $c 'tagKeysOwned'}
Case 'new tag removal has no qualified adapter' {$c=Candidate 'tags-external';$c.changes[0].action='RemoveTag';$c.changes[0].impact='OwnedTagValues';Blocked $c 'unsupportedAction'}
foreach($id in @('network-ipam','pipeline-registration','tags-source','discovery','agent-review','bicep-draft')){
    Case "manual source or read-only workflow cannot become executable: $id" {$c=Candidate $id;$c.canExecute=$true;$r=Get-RecoveryAssessment $c;Check (!$r.canExecute -and !$r.executorRegistered);Check ($r.status -cin @('ManualRecoveryRequired','SourceChangeRequired','NotApplicable'))}
}
$root=Join-Path (Get-ProjectRoot) ('artifacts/recovery-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $root -Force|Out-Null
(Candidate)|ConvertTo-Json -Depth 30|Set-Content -LiteralPath (Join-Path $root candidate.json) -Encoding utf8
Case 'CLI writes non-authorizing evidence and preserves previous receipts' {
    & "$PSScriptRoot/Test-RecoveryCandidate.ps1" -CandidatePath (Join-Path $root candidate.json) -OutputDirectory (Join-Path $root assessment)
    $saved=Get-Content (Join-Path $root assessment/recovery-assessment.json) -Raw|ConvertFrom-Json -AsHashtable;Check (!$saved.canExecute);Check ($saved.candidateSha256 -ceq (Get-FileHash (Join-Path $root candidate.json)).Hash.ToLowerInvariant())
    Reject {& "$PSScriptRoot/Test-RecoveryCandidate.ps1" -CandidatePath (Join-Path $root candidate.json) -OutputDirectory (Join-Path $root assessment)}
}
Case 'CLI reports a blocked candidate with exit code 2 and a retained receipt' {
    $c=Candidate;$c.checks.qualifiedBaseline=$false;$c|ConvertTo-Json -Depth 30|Set-Content (Join-Path $root blocked.json) -Encoding utf8
    & (Get-Process -Id $PID).Path -NoProfile -File "$PSScriptRoot/Test-RecoveryCandidate.ps1" -CandidatePath (Join-Path $root blocked.json) -OutputDirectory (Join-Path $root blocked)
    Check ($LASTEXITCODE -eq 2);Check ((Get-Content (Join-Path $root blocked/recovery-assessment.json) -Raw|ConvertFrom-Json).status -ceq 'Blocked')
    $global:LASTEXITCODE=0
}
@{passed=$cases.Count;failed=0;azureCalls=0;adoCalls=0;cases=@($cases)}|ConvertTo-Json -Depth 20|Set-Content -LiteralPath (Join-Path $root results.json) -Encoding utf8
Write-Host "PASS: $($cases.Count) recovery rule contracts. Evidence: $root"
