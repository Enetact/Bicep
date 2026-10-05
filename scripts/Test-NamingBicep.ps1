#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BicepPath)
$ErrorActionPreference='Stop'
. "$PSScriptRoot/naming-common.ps1"
$root=Split-Path $PSScriptRoot -Parent
$output=Join-Path $root 'artifacts/naming-integration/bicep'
New-Item -ItemType Directory -Force $output|Out-Null
$compiler=(Get-Item -LiteralPath $BicepPath -ErrorAction Stop).FullName
$intent=(Get-Content (Join-Path $root tests/naming/request.example.json) -Raw|ConvertFrom-Json -AsHashtable).intent
$results=@()
foreach($type in @('private-storage','key-vault','observability','http-functions','service-bus-worker','private-aks','private-data-platform','logic-app-event-grid','blob-transfer')) {
    $stack=Join-Path $root "workloads/$type/stack.bicep"
    & $compiler build $stack --outfile (Join-Path $output "$type.json")
    if($LASTEXITCODE){throw "Stack compile failed: $type"}
    foreach($environment in @('dev','qa','uat','prod')) {
        $intent.environment=$environment
        $draft=New-WorkloadNamingDraft @{workloadType=$type;intent=$intent}
        $directory=Join-Path $output "$type-$environment";New-Item -ItemType Directory -Force $directory|Out-Null
        $draft|ConvertTo-Json -Depth 30|Set-Content (Join-Path $directory naming-draft.json)
        $draft.parameterPatch.parameters.resourceNames.value|ConvertTo-Json -Depth 10|Set-Content (Join-Path $directory names.json)
        $source=Get-Content (Join-Path $root "workloads/$type/environments/main.$environment.bicepparam") -Raw
        $relative=[IO.Path]::GetRelativePath($directory,$stack).Replace('\','/')
        $source=$source -replace "(?m)^using '[^']+'", "using '$relative'"
        $source+="`nparam workloadResourceGroupName = '$($draft.parameterPatch.parameters.workloadResourceGroupName.value)'`nparam resourceNames = loadJsonContent('./names.json')`n"
        $file=Join-Path $directory standard.bicepparam;$source|Set-Content $file
        & $compiler build-params $file --outfile (Join-Path $directory standard.json)
        if($LASTEXITCODE){throw "Concrete name map failed compilation: $type/$environment"}
        # The original checked-in environment remains a legacy request with no name-map override.
        & $compiler build-params (Join-Path $root "workloads/$type/environments/main.$environment.bicepparam") --outfile (Join-Path $directory legacy.json)
        if($LASTEXITCODE){throw "Legacy environment compile failed: $type/$environment"}
        $legacy=Get-Content (Join-Path $directory legacy.json) -Raw|ConvertFrom-Json -AsHashtable
        if($legacy.parameters.Contains('resourceNames')){throw 'Existing environment unexpectedly opted into standard names.'}
        $compiled=Get-Content (Join-Path $directory standard.json) -Raw|ConvertFrom-Json -AsHashtable
        if($compiled.parameters.resourceNames.value.profile -cne 'standard-v2'){throw 'Name profile lost during compile.'}
        $results+=@{workload=$type;environment=$environment;standardCompiled=$true;legacyCompiled=$true;legacyHasNoNamingOverride=$true}
    }
}
@{cases=$results;azureCalls=$false;deploymentQualified=$false}|ConvertTo-Json -Depth 8|Set-Content (Join-Path $output receipt.json)
Write-Host "PASS: 9 stack templates and $($results.Count) standard/legacy environment pairs compiled offline. No deployment."
