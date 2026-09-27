#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
& dotnet test (Join-Path $root 'tests/SelfService.Portal.Tests/SelfService.Portal.Tests.csproj') -c Release -p:RestoreLockedMode=true --logger 'trx;LogFileName=portal.trx' --results-directory (Join-Path $root 'artifacts/test-results')
if($LASTEXITCODE -ne 0){throw 'Portal tests failed.'}
