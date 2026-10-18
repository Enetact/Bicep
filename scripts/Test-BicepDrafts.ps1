#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipContractTests)
. "$PSScriptRoot/common.ps1"
$root=Get-ProjectRoot
& "$PSScriptRoot/Update-BicepModuleContracts.ps1" -Check
if(!$SkipContractTests){
    & dotnet test (Join-Path $root tests/SelfService.Portal.Tests/SelfService.Portal.Tests.csproj) -c Release -p:RestoreLockedMode=true --artifacts-path (Join-Path $root artifacts/bicep-draft-build) --filter 'FullyQualifiedName~BicepDraftTests|FullyQualifiedName~AvmModuleTests'
    if($LASTEXITCODE -ne 0){throw 'Bicep draft contracts failed.'}
}
$native=(Get-Content (Join-Path $root config/bicep-module-contracts.json) -Raw|ConvertFrom-Json).modules
$avm=(Get-Content (Join-Path $root config/avm-module-contracts.json) -Raw|ConvertFrom-Json).modules
$modules=@($native)+@($avm)|Where-Object draftSelectable
$count=0
foreach($module in $modules){
    foreach($entry in @('main','stack')){
        $source=Join-Path $root "artifacts/bicep-draft-tests/$($module.id)/$entry.bicep"
        Invoke-Bicep -Arguments @('build',$source,'--outfile',([IO.Path]::ChangeExtension($source,'.json')))
        $count++
    }
}
$typedCount=0
foreach($module in $avm){
    if(!@($module.parameters.PSObject.Properties|Where-Object {$_.Value.PSObject.Properties.Name -contains 'items'}).Count){continue}
    foreach($entry in @('main','stack')){
        $source=Join-Path $root "artifacts/bicep-draft-tests/$($module.id)/typed-children/$entry.bicep"
        if(!(Test-Path -LiteralPath $source)){throw 'Run the AVM draft tests to generate explicit typed-child fixtures.'}
        Invoke-Bicep -Arguments @('build',$source,'--outfile',([IO.Path]::ChangeExtension($source,'.json')))
        $typedCount++
    }
}
$resolved=Join-Path $root artifacts/bicep-draft-tests/resolved-data
Invoke-Bicep -Arguments @('build-params',(Join-Path $resolved main.bicepparam),'--outfile',(Join-Path $resolved parameters.json))
$parameters=Get-Content (Join-Path $resolved parameters.json) -Raw|ConvertFrom-Json -AsHashtable
if($parameters.parameters.p_workspace_name.value -cne 'O''Brien ${fake.expression}'){throw 'Parameter data was interpreted or changed.'}
@{generatedTemplatesCompiled=$count;typedChildTemplatesCompiled=$typedCount;moduleContracts=(@($native).Count+@($avm).Count);resolvedParameterDataChecks=1;azureCalls=$false;agentInference=$false;deploymentQualified=$false}|ConvertTo-Json|Set-Content (Join-Path $root artifacts/bicep-draft-tests/results.json)
Write-Host "PASS: $count emitted Bicep main/stack drafts and $typedCount explicit child-input drafts compiled. Unresolved parameter files deliberately require platform input; no Azure or model calls."
