# Reviewed workload identifiers map to code; artifacts cannot select executable paths.
function Get-TargetWorkloadType($Target) {
    if ($Target.Contains('workloadType')) { return [string]$Target.workloadType }
    return 'blob-transfer'
}
function Get-WorkloadDefinition([string]$Type) {
    # Executable adapters are selected by reviewed code, never a manifest path or script name.
    $adapters=@{'blob-transfer'=@('functions','deployFunctionApp','blob','blobcopy');'logic-app-event-grid'=@('logicAppStandard','releaseActivated','logic','eventflow');'private-storage'=@('infrastructure','releaseActivated','product','storage');'key-vault'=@('infrastructure','releaseActivated','product','keyvault');'observability'=@('infrastructure','releaseActivated','product','observe');'http-functions'=@('productFunctions','releaseActivated','product','httpapi');'service-bus-worker'=@('productFunctions','releaseActivated','product','busworker')}
    if (!$adapters.ContainsKey($Type)) { throw 'Unsupported workload adapter.' }
    $catalog=Get-Content (Join-Path (Get-ProjectRoot) 'config/workloads.json') -Raw | ConvertFrom-Json -AsHashtable
    if ($catalog.schemaVersion -ne 1 -or !$catalog.workloads.Contains($Type)) { throw 'Workload definition is missing.' }
    $d=$catalog.workloads[$Type]
    if ($d.composition -cne "workloads/$Type/main.bicep" -or $d.stack -cne "workloads/$Type/stack.bicep" -or $d.templateSpecName -cne $Type) { throw 'Unapproved workload source paths.' }
    $expected=$adapters[$Type]
    if ($d.packageKind -cne $expected[0] -or $d.phaseParameter -cne $expected[1] -or $d.adapter -cne $expected[2] -or $d.menuSlug -cne $expected[3]) { throw 'Unapproved workload package or lifecycle adapter.' }
    return $d
}
function Test-ProductWorkload([string]$Type) { return (Get-WorkloadDefinition $Type).adapter -ceq 'product' }
