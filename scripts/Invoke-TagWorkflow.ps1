#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('discover','analyze','preview','apply','verify')][string]$Mode,[Parameter(Mandatory)][string]$OutputDirectory,[string]$InventoryPath,[string]$PlanPath)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$profile=Get-Content (Join-Path $root config/tag-governance.json) -Raw|ConvertFrom-Json
$account=& az account show --output json --only-show-errors|ConvertFrom-Json
if($LASTEXITCODE -ne 0 -or $account.id -cne $profile.subscriptionId -or $account.tenantId -cne $profile.tenantId){throw 'Service connection tenant/subscription mismatch.'}
$assembly=Join-Path $root 'src/SelfService.Tagging.Tool/bin/Release/net10.0/SelfService.Tagging.Tool.dll'
if(!(Test-Path -LiteralPath $assembly)){throw 'Build the tagging tool in the qualification step first.'}
$argsList=@($assembly,$Mode,'--root',$root,'--output',$OutputDirectory)
if($InventoryPath){$argsList+=@('--inventory',$InventoryPath)}
if($PlanPath){$argsList+=@('--plan',$PlanPath)}
$previous=$env:TAG_ARM_TOKEN
try{
    $token=& az account get-access-token --resource https://management.azure.com/ --query accessToken --output tsv --only-show-errors
    if($LASTEXITCODE -ne 0 -or !$token){throw 'Azure token unavailable.'}
    $env:TAG_ARM_TOKEN=[string]$token
    & dotnet @argsList
    if($LASTEXITCODE -ne 0){throw 'Tag workflow blocked or incomplete. Review sanitized artifacts.'}
}finally{$env:TAG_ARM_TOKEN=$previous;$token=$null}
