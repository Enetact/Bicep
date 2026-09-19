# Reviewed workload identifiers map to code; artifacts cannot select executable paths.
function Get-TargetWorkloadType($Target) {
    if ($Target.Contains('workloadType')) { return [string]$Target.workloadType }
    return 'blob-transfer'
}
function Get-WorkloadDefinition([string]$Type) {
    if ($Type -cnotin @('blob-transfer','logic-app-event-grid')) { throw 'Unsupported workload adapter.' }
    $catalog=Get-Content (Join-Path (Get-ProjectRoot) 'config/workloads.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($catalog.schemaVersion -ne 1 -or !$catalog.workloads.Contains($Type)) { throw 'Workload definition is missing.' }
    $d=$catalog.workloads[$Type]
    if ($d.composition -cne "workloads/$Type/main.bicep" -or $d.stack -cne "workloads/$Type/stack.bicep" -or $d.templateSpecName -cne $Type) { throw 'Unapproved workload source paths.' }
    $kind=if($Type -eq 'blob-transfer'){'functions'}else{'logicAppStandard'}
    $phase=if($Type -eq 'blob-transfer'){'deployFunctionApp'}else{'releaseActivated'}
    if ($d.packageKind -cne $kind -or $d.phaseParameter -cne $phase) { throw 'Unapproved workload package or lifecycle adapter.' }
    return $d
}
