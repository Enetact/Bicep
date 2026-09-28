# Deterministic recovery assessment only. No Azure/ADO calls or executable inverse code.
# Inputs are caller-supplied assertions until a future trusted evidence adapter verifies them.
. "$PSScriptRoot/common.ps1"
function Get-RecoveryModes {
    return @{
        'blob-transfer'='ApplicationRestore';'logic-app-event-grid'='ApplicationRestore'
        'private-storage'='ConfigurationRestore';'key-vault'='ConfigurationRestore';'observability'='ConfigurationRestore'
        'http-functions'='ApplicationRestore';'service-bus-worker'='ApplicationRestore'
        'tags-external'='TagCompensation';'tags-source'='SourceRevert';'network-ipam'='ManualRecovery'
        'pipeline-registration'='ManualRecovery';'discovery'='NotApplicable';'agent-review'='NotApplicable';'bicep-draft'='NotApplicable'
    }
}
function Get-RecoveryCheckIds([string]$Mode) {
    if($Mode -in @('NotApplicable','ManualRecovery','SourceRevert')){return @()}
    $ids=@('beforeCaptured','qualifiedBaseline','artifactsRetained','integrityVerified','sameTarget','coverageComplete','ownershipVerified','currentPolicyPassed','compatibleChanges','credentialsCurrent','freshPreview','noDrift','protectedApproval','verificationDefined')
    if($Mode -eq 'ApplicationRestore'){$ids+=@('packageCompatible','processingImpactReviewed')}
    if($Mode -eq 'TagCompensation'){$ids+=@('tagKeysOwned','tagCurrentMatchesApplied')}
    return $ids
}
function Read-RecoveryCatalog {
    $c=Get-Content -LiteralPath (Join-Path (Get-ProjectRoot) config/recovery-capabilities.json) -Raw|ConvertFrom-Json -AsHashtable
    $modes=Get-RecoveryModes
    if($c.schemaVersion -ne 1 -or $c.executionEnabled -isnot [bool] -or $c.executionEnabled -or $c.policies.Count -ne $modes.Count){throw 'Invalid recovery catalog; execution cannot be enabled through configuration.'}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($p in $c.policies){
        if(!$seen.Add($p.id) -or !$modes.ContainsKey($p.id) -or $p.mode -cne $modes[$p.id] -or !$p.summary -or !$p.exclusions.Count){throw 'Unknown, duplicate or inconsistent recovery policy.'}
        foreach($id in @(Get-RecoveryCheckIds $p.mode)){if(!$c.checks.ContainsKey($id) -or $c.checks[$id] -isnot [string] -or !$c.checks[$id]){throw "Missing recovery check: $id"}}
    }
    return $c
}
function Get-RecoveryPolicy([string]$Workflow) {
    $c=Read-RecoveryCatalog
    $p=@($c.policies|Where-Object { $_.id -ceq $Workflow })
    if($p.Count -ne 1){throw 'Unknown recovery workflow.'}
    return @{schemaVersion=1;kind='recovery-policy';policy=$p[0];checks=@(foreach($id in @(Get-RecoveryCheckIds $p[0].mode)){@{id=$id;description=$c.checks[$id]}});executionEnabled=$false;assessmentStatus='Not assessed';catalogSha256=(Get-FileHash -LiteralPath (Join-Path (Get-ProjectRoot) config/recovery-capabilities.json) -Algorithm SHA256).Hash.ToLowerInvariant()}
}
function Get-RecoveryAssessment([Collections.IDictionary]$Candidate) {
    if($Candidate['schemaVersion'] -ne 1 -or $Candidate['kind'] -cne 'recovery-candidate' -or $Candidate['workflow'] -isnot [string]){throw 'Invalid recovery candidate contract.'}
    $snapshot=Get-RecoveryPolicy $Candidate.workflow;$p=$snapshot.policy
    $issues=[Collections.Generic.List[object]]::new()
    $result=@{schemaVersion=1;kind='recovery-assessment';workflow=$p.id;mode=$p.mode;catalogSha256=$snapshot.catalogSha256;createdUtc=[DateTimeOffset]::UtcNow.ToString('O');evidenceTrust='Caller assertions; provenance not verified';canExecute=$false;executorRegistered=$false;issues=@();exclusions=$p.exclusions;requiredChecks=$snapshot.checks;nextAction='Collect and verify retained evidence through a qualified recovery adapter.'}
    # No configuration-only switch can turn read-only/manual/source-owned workflows into apply.
    if($p.mode -in @('NotApplicable','ManualRecovery','SourceRevert')){
        $result.status=switch($p.mode){'NotApplicable'{'NotApplicable'}'ManualRecovery'{'ManualRecoveryRequired'}'SourceRevert'{'SourceChangeRequired'}}
        $result.nextAction=$p.summary;return $result
    }
    $facts=$Candidate['checks']
    if($facts -isnot [Collections.IDictionary]){$facts=@{}}
    foreach($check in $snapshot.checks){
        if($facts[$check.id] -isnot [bool] -or !$facts[$check.id]){$issues.Add(@{code=$check.id;message=$check.description})}
    }
    # An unreconciled response cannot be treated as a known failure or automatically replayed.
    if($Candidate['outcome'] -cnotin @('Succeeded','FailedReconciled')){$issues.Add(@{code='outcomeUnknown';message='Reconcile the current Azure outcome before planning another mutation.'})}
    if($Candidate['targetScope'] -isnot [string] -or $Candidate.targetScope -cnotmatch '^/subscriptions/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}/resourceGroups/[^/\s?#\\]+$'){
        $issues.Add(@{code='invalidScope';message='Supply the exact owned resource-group scope.'})
    }
    $changes=$Candidate['changes']
    if($changes -isnot [array] -or $changes.Count -eq 0 -or $changes.Count -gt 10000){$issues.Add(@{code='changesMissing';message='Supply 1–10000 explicit resource changes; empty or incomplete evidence is not a no-change result.'});$changes=@()}
    $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($change in $changes){
        if($change -isnot [Collections.IDictionary]){throw 'Invalid recovery change.'}
        $id=$change['resourceId'];$scope=[string]$Candidate['targetScope']
        $resourcePattern='^'+[regex]::Escape($scope)+'/providers/[^/\s]+/[^/\s]+/[^/\s]+(?:/[^/\s]+/[^/\s]+)*$'
        # Property evaluation/normalization is deliberately delegated to the required qualified adapter.
        if($id -isnot [string] -or !$scope -or $id -notmatch $resourcePattern -or $id.Contains('?') -or $id.Contains('#') -or $id -match '/\.\.?(/|$)|\\|\s' -or !$seen.Add($id)){
            $issues.Add(@{code='scopeOrDuplicate';message='A resource is outside the owned scope, malformed or duplicated.'})
        }
        $allowed=if($p.mode -eq 'TagCompensation'){@('RestoreTag','NoChange')}else{@('Modify','NoChange')}
        if($change['action'] -cnotin $allowed){$issues.Add(@{code='unsupportedAction';message='Only qualified mutable changes are candidates. Creation, deletion, detach, replacement, tag removal, allocation release and data/event recovery require separate adapters.'})}
        if($change['certainty'] -cne 'Definite'){$issues.Add(@{code='uncertainChange';message='Unknown or potential resource changes block recovery.'})}
        if($change['impact'] -cne $(if($p.mode -eq 'TagCompensation'){'OwnedTagValues'}else{'MutableConfiguration'})){
            $issues.Add(@{code='unsupportedImpact';message='Data, credentials, identity replacement, immutable settings and external effects are outside this recovery contract.'})
        }
    }
    $result.issues=@($issues);$result.status=if($issues.Count){'Blocked'}else{'ChecksSatisfiedUnverified'}
    if(!$issues.Count){$result.nextAction='Submitted checks are satisfied, but require independent artifact/live-state verification and a qualified protected recovery executor. This result is not an approval.'}
    return $result
}
