#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$')][string]$ReleaseId)
. "$PSScriptRoot/self-service-common.ps1"
$root=Get-ProjectRoot; $directory=Join-Path $root "artifacts/logic-releases/$ReleaseId"
if (Test-Path $directory){throw 'Use a fresh Logic App release ID.'}
New-Item -ItemType Directory -Path $directory|Out-Null
$zipPath=Join-Path $directory "$ReleaseId.zip"
$zip=[IO.Compression.ZipFile]::Open($zipPath,[IO.Compression.ZipArchiveMode]::Create)
try {
    foreach($name in @('connections.json','host.json','parameters.json','process-event/workflow.json')) {
        $entry=$zip.CreateEntry($name,[IO.Compression.CompressionLevel]::Optimal)
        $entry.LastWriteTime=[DateTimeOffset]::Parse('2000-01-01T00:00:00Z')
        $source=[IO.File]::OpenRead((Join-Path $root "src/LogicAppEventFlow/$name"));$destination=$entry.Open()
        try{$source.CopyTo($destination)}finally{$source.Dispose();$destination.Dispose()}
    }
} finally {$zip.Dispose()}
Test-LogicPackage $zipPath
$commit=& git -C $root rev-parse HEAD
if($LASTEXITCODE -ne 0){throw 'Git provenance unavailable.'}
$changes=@(& git -C $root status --porcelain)
if($LASTEXITCODE -ne 0){throw 'Worktree provenance unavailable.'}
Write-ServiceJson @{schemaVersion=1;workloadType='logic-app-event-grid';releaseId=$ReleaseId;sourceCommit=$commit;dirtyWorktree=($changes.Count -gt 0);packageSha256=(Get-ServiceHash $zipPath);createdUtc=[DateTimeOffset]::UtcNow.ToString('O')} (Join-Path $directory release.json)
Write-Host "Logic App package: $zipPath"
