# Preview-only source registration. No target enabling or apply path is exposed here.
. "$PSScriptRoot/naming-common.ps1"
function Get-RegisteredNamingDraft($Target,[string]$InstanceId) {
    if($InstanceId -cnotmatch '^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$'){throw 'Select a reviewed naming instance UUID.'}
    $registry=Get-Content (Join-Path (Get-ProjectRoot) self-service/instances/registry.json) -Raw|ConvertFrom-Json -AsHashtable
    $null=Test-StackInstanceRegistry -Registry $registry
    $entries=@($registry.instances|Where-Object {$_.intent.instanceId -ceq $InstanceId})
    if($entries.Count -ne 1){throw 'Naming instance is not registered in this source revision.'}
    $entry=$entries[0];$intent=$entry.intent
    $region=if($Target.Contains('parameterOverrides') -and $Target.parameterOverrides.Contains('location')){$Target.parameterOverrides.location}else{'eastus2'}
    if($entry.workloadType -cne (Get-TargetWorkloadType $Target) -or $intent.subscriptionId -ine $Target.subscriptionId -or $intent.environment -cne $Target.environmentName -or $intent.region -cne $region){throw 'Naming instance does not match the selected workload/subscription/environment/region.'}
    return New-WorkloadNamingDraft @{workloadType=$entry.workloadType;intent=$intent}
}
function Set-NamingPreviewStack($Stack,$Draft) {
    $row=@($Draft.plan.resources|Where-Object {$_.componentKey -ceq 'stack'})[0]
    $Stack.stackName=$row.name;$Stack.stackId=$row.resourceId
}
function Assert-NamingPreviewParameters($Target,$Parameters,$Draft) {
    if($Target.enabled){throw 'Named draft Preview must remain disabled for deployment.'}
    if($Target.resourceGroup -cne $Draft.parameterPatch.parameters.workloadResourceGroupName.value){throw 'Naming resource-group binding mismatch.'}
    if(!$Parameters.Contains('resourceNames') -or (Get-ValueHash $Parameters.resourceNames) -cne (Get-ValueHash $Draft.parameterPatch.parameters.resourceNames)){throw 'Frozen naming parameters changed.'}
    $base=$Parameters|ConvertTo-Json -Depth 100|ConvertFrom-Json -AsHashtable
    $base.Remove('resourceNames')
    Assert-ServiceParameters $Target $base
}
