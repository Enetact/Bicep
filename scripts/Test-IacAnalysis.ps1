#requires -Version 7.4
[CmdletBinding()]
param([switch]$SkipRestore)
. "$PSScriptRoot/common.ps1"
$root=Get-ProjectRoot
if(!$SkipRestore){
    & npm ci --prefix (Join-Path $PSScriptRoot iac) --ignore-scripts --no-audit --no-fund
    if($LASTEXITCODE -ne 0){throw 'IaC dependency restore failed.'}
}
& node --test (Join-Path $root 'tests/iac/core.test.mjs')
if($LASTEXITCODE -ne 0){throw 'IaC evidence tests failed.'}
& node (Join-Path $PSScriptRoot 'iac/run.mjs') qualify
if($LASTEXITCODE -ne 0){throw 'Installed read-only Azure MCP schema mismatch.'}
Invoke-Bicep -Arguments @('build',(Join-Path $root 'artifacts/iac/before-test.bicep'),'--outfile',(Join-Path $root 'artifacts/iac/before-test.json'))
# Exercise the real PowerShell -> Node stdin/argv/artifact contract with an empty plan.
# The Azure command is replaced in this test script's scope; zero IDs means zero ARM requests.
$contract=Join-Path $root 'artifacts/iac/wrapper-contract'
New-Item -ItemType Directory -Force $contract|Out-Null
$provenance=@{product='storage';environment='dev';region='eastus2';runId=$env:BUILD_BUILDID;sourceCommit=$env:BUILD_SOURCEVERSION;pipelineId=$env:SYSTEM_DEFINITIONID;repositoryId=$env:BUILD_REPOSITORY_ID;inputSha256=('a'*64);templateSha256=('b'*64)}
@{schema='platform.iac-analysis/v1';scope=@{subscriptionId='11111111-1111-4111-8111-111111111111';resourceGroup='rg-test'};provenance=$provenance;apiVersions=@{};before=@{inventoryStatus='GroupAbsent';resources=@();coverage='Test fixture only'};planned=$null;after=$null;comparison=@();limitations=@('Test fixture only');deploymentAuthorized=$false}|ConvertTo-Json -Depth 20|Set-Content -Encoding utf8 (Join-Path $contract analysis.json)
@{properties=@{provisioningState='Succeeded';changes=@{resourceChanges=@()}}}|ConvertTo-Json -Depth 20|Set-Content -Encoding utf8 (Join-Path $contract whatif.json)
function az {
    $global:LASTEXITCODE=0
    if(($args -join ' ') -like 'account show*'){@{id='11111111-1111-4111-8111-111111111111';tenantId='22222222-2222-4222-8222-222222222222';environmentName='AzureCloud'}|ConvertTo-Json -Compress}
    elseif(($args -join ' ') -like 'account get-access-token*'){@{accessToken='local-fixture-only'}|ConvertTo-Json -Compress}
    else{throw 'Unexpected Azure command in the isolated wrapper test.'}
}
& "$PSScriptRoot/Invoke-IacAnalysis.ps1" -Action plan -Directory $contract -WhatIfPath (Join-Path $contract whatif.json)
$receipt=Get-Content (Join-Path $contract receipt.json) -Raw|ConvertFrom-Json
if($receipt.schema -ne 'platform.iac-receipt/v1' -or (Get-Content (Join-Path $contract analysis.json) -Raw).Contains('local-fixture-only')){throw 'IaC wrapper receipt or token exclusion failed.'}
Set-Content -Encoding utf8 -LiteralPath (Join-Path $contract whatif.json) -Value '{}'
$rejected=$false
try{& "$PSScriptRoot/Invoke-IacAnalysis.ps1" -Action plan -Directory $contract -WhatIfPath (Join-Path $contract whatif.json)}catch{$rejected=$true}
$failed=Get-Content (Join-Path $contract analysis.json) -Raw|ConvertFrom-Json
if(!$rejected -or $null -ne $failed.planned -or !$failed.issue){throw 'A failed repeated plan retained an old successful prediction.'}
$global:LASTEXITCODE=0
Write-Host 'PASS: actual PowerShell/Node wrapper with a mocked credential and zero-resource plan; no Azure requests.'
Write-Host 'IaC rules, installed MCP handshake and existing-only Bicep qualified locally. No live Azure calls or model turns performed.'
