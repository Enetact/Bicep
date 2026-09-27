#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    & node tests/portal/verify-skills.mjs;if($LASTEXITCODE -ne 0){throw 'Azure Skills bundle verification failed.'}
    & node --test tests/portal/topology.test.mjs;if($LASTEXITCODE -ne 0){throw 'Portal topology verification failed.'}
}finally{Pop-Location}
& (Join-Path $PSScriptRoot 'Test-NetworkAllocation.ps1')
& dotnet test (Join-Path $root 'tests/SelfService.Portal.Tests/SelfService.Portal.Tests.csproj') -c Release -p:RestoreLockedMode=true --logger 'trx;LogFileName=portal.trx' --results-directory (Join-Path $root 'artifacts/test-results')
if($LASTEXITCODE -ne 0){throw 'Portal tests failed.'}
