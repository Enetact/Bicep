#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$RequestPath,[Parameter(Mandatory)][string]$OutputPath)
. "$PSScriptRoot/self-service-common.ps1"
. "$PSScriptRoot/platform-contract.ps1"
if (Test-Path -LiteralPath $OutputPath) { throw 'Use a new output path to preserve request evidence.' }
$request=Get-Content -LiteralPath $RequestPath -Raw | ConvertFrom-Json -AsHashtable
$targets=@(Get-ChildItem (Join-Path (Get-ProjectRoot) self-service/targets) -Filter *.json | ForEach-Object {
    $target=Get-Content $_.FullName -Raw | ConvertFrom-Json -AsHashtable
    Assert-ServiceTarget $target $target.workload $target.environmentName -AllowDisabled
    $target
})
$resolved=Resolve-PlatformRequest $request $targets
Write-ServiceJson $resolved $OutputPath
Write-Host "Resolved approved request. Deployment enabled: $($resolved.deploymentEnabled). No Azure calls or changes."
