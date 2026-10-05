# Source-reviewed named instances reuse raw facts, with independently read stack ownership.
function Get-LogicInventoryView($Target,$Inventory) {
    if(!$Inventory.Contains('namedPrerequisiteProfiles')){return $Inventory}
    $matches=@($Inventory.namedPrerequisiteProfiles|Where-Object {$_.selectionHash -ceq (Get-ValueHash $Target)})
    if(!$matches.Count){return $Inventory}
    if($matches.Count -ne 1){throw 'Ambiguous named prerequisite evidence.'}
    if(!(Get-Command Get-RegisteredNamingDraft -ErrorAction SilentlyContinue)){. "$PSScriptRoot/naming-preview-common.ps1"}
    $draft=Get-RegisteredNamingDraft $Target $matches[0].instanceId
    if($draft.draftSha256 -cne $matches[0].namingDraftSha256){throw 'Named prerequisite policy changed; rerun Discover.'}
    $view=$Inventory|ConvertTo-Json -Depth 100|ConvertFrom-Json -AsHashtable
    $view.Remove('namedPrerequisiteProfiles')
    $view.prerequisitePlan=$matches[0].plan;$view.prerequisiteOwnership=$matches[0].ownership
    $view.namingDraft=$draft
    return $view
}
function Add-LogicNamingDiscovery($Target,$Report) {
    $file=Join-Path (Get-ProjectRoot) self-service/instances/registry.json
    if(!(Test-Path -LiteralPath $file)){return} # Legacy isolated catalog/test fixtures.
    $registry=Get-Content $file -Raw|ConvertFrom-Json -AsHashtable
    if(!$registry.instances.Count){return}
    . "$PSScriptRoot/naming-preview-common.ps1"
    $null=Test-StackInstanceRegistry -Registry $registry
    $region=if($Target.parameterOverrides.Contains('location')){$Target.parameterOverrides.location}else{'eastus2'}
    $entries=@($registry.instances|Where-Object {$_.workloadType -ceq 'logic-app-event-grid' -and $_.intent.environment -ceq $Target.environmentName -and $_.intent.subscriptionId -ieq $Target.subscriptionId -and $_.intent.region -ceq $region})
    if($entries.Count -gt 32){throw 'At most 32 named discovery profiles per scope are supported.'}
    $Report.namedPrerequisiteProfiles=@()
    foreach($entry in $entries){
        $draft=Get-RegisteredNamingDraft $Target $entry.intent.instanceId
        $named=$Target|ConvertTo-Json -Depth 100|ConvertFrom-Json -AsHashtable
        $named.enabled=$false;$named.resourceGroup=$draft.parameterPatch.parameters.workloadResourceGroupName.value
        $view=$Report|ConvertTo-Json -Depth 100|ConvertFrom-Json -AsHashtable;$view.Remove('namedPrerequisiteProfiles')
        $view.namingDraft=$draft
        try{$view.prerequisiteOwnership=Get-LogicPrerequisiteOwnership $named $draft}
        catch{$view.prerequisiteOwnership=@{status='Failed';stackExists=$false;managedResourceIds=@()}}
        $plan=Get-LogicPrerequisitePlan $named $view
        $Report.namedPrerequisiteProfiles+=@{instanceId=$entry.intent.instanceId;selectionHash=(Get-ValueHash $named);namingDraftSha256=$draft.draftSha256;ownership=$view.prerequisiteOwnership;plan=$plan}
    }
}
