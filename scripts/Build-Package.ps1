[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$')][string]$ReleaseId)
. "$PSScriptRoot/common.ps1"
$root = Get-ProjectRoot
$output = Join-Path $root "artifacts/releases/$ReleaseId"
if (Test-Path $output) { throw 'Release folder already exists. Use a fresh immutable release ID.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
& dotnet test (Join-Path $root 'tests/BlobTransfer.Tests/BlobTransfer.Tests.csproj') --configuration Release -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
$publish = Join-Path $output 'publish'
& dotnet publish (Join-Path $root 'src/BlobTransfer/BlobTransfer.csproj') --configuration Release --output $publish --no-self-contained -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
if (!(Test-Path (Join-Path $publish 'host.json'))) { throw 'Package is missing host.json.' }
if (!(Test-Path (Join-Path $publish 'functions.metadata'))) { throw 'Package is missing function metadata.' }
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $output "$ReleaseId.zip"
# Includes dot-prefixed .azurefunctions files, unlike Compress-Archive on some platforms.
[IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
Get-FileHash $zip -Algorithm SHA256 | Format-List
Write-Host "Package: $zip"
