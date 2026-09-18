#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$Workload,[Parameter(Mandatory)][ValidateSet('dev','qa','uat','prod')][string]$EnvironmentName,
    [Parameter(Mandatory)][string]$ReleaseDirectory,[Parameter(Mandatory)][string]$OutputDirectory)
. "$PSScriptRoot/self-service-common.ps1"
if (Test-Path -LiteralPath $OutputDirectory) { throw 'Bundle directory already exists; use a fresh run directory.' }
$target=Read-ServiceTarget $Workload $EnvironmentName
$release=Get-Content (Join-Path $ReleaseDirectory release.json) -Raw | ConvertFrom-Json -AsHashtable
if ($release.releaseId -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$' -or $release.dirtyWorktree) { throw 'Self-service requires a clean, qualified release receipt.' }
$package=Resolve-ServicePath $ReleaseDirectory ($release.releaseId + '.zip')
if ((Get-ServiceHash $package) -cne $release.packageSha256) { throw 'Release package hash mismatch.' }
$root=Get-ProjectRoot
$commit=& git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0 -or $commit -cne $release.sourceCommit) { throw 'Release source does not match selected checkout.' }
$null=Export-Templates -EnvironmentName $EnvironmentName -ParameterPath (Resolve-ServicePath $root $target.parameterFile) -OutputPath $OutputDirectory
$parameters=Get-Content (Join-Path $OutputDirectory parameters.json) -Raw | ConvertFrom-Json -AsHashtable
Assert-ServiceParameters $target $parameters.parameters
Copy-Item -LiteralPath $package -Destination (Join-Path $OutputDirectory application.zip)
Copy-Item -LiteralPath (Join-Path $ReleaseDirectory functions.metadata) -Destination $OutputDirectory
Write-ServiceJson $target (Join-Path $OutputDirectory target.json)
$files=@{}
foreach ($file in @('main.json','parameters.json','target.json','application.zip','functions.metadata')) { $files[$file]=Get-ServiceHash (Join-Path $OutputDirectory $file) }
Write-ServiceJson @{schemaVersion=1;releaseId=$release.releaseId;sourceCommit=$release.sourceCommit;createdUtc=[DateTimeOffset]::UtcNow.ToString('O');files=$files} (Join-Path $OutputDirectory bundle.json)
$null=Read-ServiceBundle $OutputDirectory
Write-Host "Frozen self-service bundle: $OutputDirectory"
