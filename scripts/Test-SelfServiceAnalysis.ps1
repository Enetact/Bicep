#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
& node (Join-Path $root tests/analysis/verify.mjs)
if($LASTEXITCODE -ne 0){throw 'Offline analysis regression failed.'}
$latest=Get-ChildItem (Join-Path $root artifacts/analysis-tests) -Directory | Sort-Object LastWriteTimeUtc -Descending | Select-Object -First 1
$reports=@(Get-ChildItem $latest.FullName -Filter analysis.json -Recurse)
foreach($report in $reports){
    if(!(Test-Json -Json (Get-Content $report.FullName -Raw) -SchemaFile (Join-Path $root schemas/analysis/offline-analysis.schema.json))){throw 'Analysis output schema failed.'}
}
Write-Host "PASS: $($reports.Count) emitted analysis documents match the versioned JSON schema."
