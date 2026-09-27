#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$WorkloadType,[Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$')][string]$ReleaseId)
. "$PSScriptRoot/self-service-common.ps1"
$d=Get-WorkloadDefinition $WorkloadType
if($d.adapter -cne 'product'){throw 'Use the existing qualified package adapter for this workload.'}
$root=Get-ProjectRoot;$directory=Join-Path $root "artifacts/product-releases/$WorkloadType/$ReleaseId"
if(Test-Path $directory){throw 'Use a fresh product release ID.'}
New-Item -ItemType Directory -Path $directory|Out-Null
$receipt=@{schemaVersion=1;workloadType=$WorkloadType;packageKind=$d.packageKind;releaseId=$ReleaseId;createdUtc=[DateTimeOffset]::UtcNow.ToString('O')}
if($d.packageKind -eq 'productFunctions'){
    $publish=Join-Path $directory content
    & dotnet publish (Join-Path $root src/ProductFunctions/ProductFunctions.csproj) -c Release -p:RestoreLockedMode=true -o $publish
    if($LASTEXITCODE -ne 0){throw 'Product application build failed.'}
    $zip=Join-Path $directory "$ReleaseId.zip"
    [IO.Compression.ZipFile]::CreateFromDirectory($publish,$zip)
    Test-ProductPackage $zip
    $receipt.packageSha256=Get-ServiceHash $zip
}
$receipt.sourceCommit=& git -C $root rev-parse HEAD
if($LASTEXITCODE -ne 0){throw 'Source provenance unavailable.'}
$changes=@(& git -C $root status --porcelain)
if($LASTEXITCODE -ne 0){throw 'Worktree provenance unavailable.'}
$receipt.dirtyWorktree=$changes.Count -gt 0
Write-ServiceJson $receipt (Join-Path $directory release.json)
Write-Host "Product release receipt: $directory (package kind: $($d.packageKind))."
