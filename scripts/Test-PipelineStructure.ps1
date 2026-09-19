#requires -Version 7.4
[CmdletBinding()]
param()
. "$PSScriptRoot/common.ps1"
$root=Get-ProjectRoot
$checks=Join-Path $root 'tests/infrastructure'
foreach($tool in @('node','npm')) { if(!(Get-Command $tool -ErrorAction SilentlyContinue)){throw "Install Node.js 22+ with npm before pipeline verification: missing $tool."} }
& npm ci --prefix $checks --ignore-scripts --no-audit --no-fund
if($LASTEXITCODE -ne 0){throw 'Infrastructure check dependency restore failed.'}
& node (Join-Path $checks 'verify.mjs')
if($LASTEXITCODE -ne 0){throw 'Pipeline or compiled infrastructure contract check failed.'}
