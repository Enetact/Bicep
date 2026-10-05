#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('before','plan','after')][string]$Action,
    [Parameter(Mandatory)][string]$Directory,[string]$InputDirectory='', [string]$WhatIfPath='')
$ErrorActionPreference='Stop'
$runner=Join-Path $PSScriptRoot 'iac/run.mjs'
& {
    $accountText=& az account show --only-show-errors --output json
    if($LASTEXITCODE -ne 0){throw 'Cannot establish Azure task subscription identity.'}
    $account=$accountText|ConvertFrom-Json
    if($account.environmentName -ne 'AzureCloud'){throw 'IaC collector currently supports AzureCloud only.'}
    $tokenText=& az account get-access-token --resource https://management.azure.com/ --subscription $account.id --only-show-errors --output json
    if($LASTEXITCODE -ne 0){throw 'Cannot acquire the task ARM read token.'}
    $token=$tokenText|ConvertFrom-Json
    try{
        @{subscriptionId=$account.id;tenantId=$account.tenantId;accessToken=$token.accessToken}|ConvertTo-Json -Compress | & node $runner $Action $Directory $InputDirectory $WhatIfPath
    }finally{$token=$null;$tokenText=$null}
}
if($LASTEXITCODE -ne 0){throw 'IaC evidence collection or comparison failed. Inspect the retained analysis receipt.'}
