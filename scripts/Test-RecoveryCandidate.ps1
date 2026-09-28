#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$CandidatePath,[Parameter(Mandatory)][string]$OutputDirectory)
. "$PSScriptRoot/recovery-common.ps1"
$inputFile=Get-Item -LiteralPath $CandidatePath
if($inputFile.PSIsContainer -or $inputFile.Length -gt 4MB){throw 'Candidate must be a JSON file no larger than 4 MiB.'}
$inputStream=[IO.File]::OpenRead($inputFile.FullName)
try{
    if($inputStream.Length -gt 4MB){throw 'Candidate exceeds 4 MiB.'}
    $bytes=[byte[]]::new([int]$inputStream.Length);$inputStream.ReadExactly($bytes)
    if($inputStream.ReadByte() -ne -1){throw 'Candidate changed while being read.'}
}finally{$inputStream.Dispose()}
$candidate=[Text.Encoding]::UTF8.GetString($bytes).TrimStart([char]0xfeff)|ConvertFrom-Json -AsHashtable -Depth 30
$report=Get-RecoveryAssessment $candidate
$report.candidateSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
$output=[IO.Path]::GetFullPath($OutputDirectory)
New-Item -ItemType Directory -Path $output -Force|Out-Null
# CreateNew preserves earlier evidence and also prevents overwriting the input file.
$path=Join-Path $output recovery-assessment.json
$stream=[IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write)
try{$bytes=[Text.Encoding]::UTF8.GetBytes(($report|ConvertTo-Json -Depth 30));$stream.Write($bytes)}finally{$stream.Dispose()}
Write-Host "Recovery rules: $($report.status). Execution authorized: false. Evidence: $path"
if($report.status -eq 'Blocked'){exit 2}
