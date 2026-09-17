[CmdletBinding()]
param([Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$')][string]$ReleaseId)
. "$PSScriptRoot/common.ps1"
$root = Get-ProjectRoot
$output = Join-Path $root "artifacts/releases/$ReleaseId"
if (Test-Path $output) { throw 'Release folder already exists. Use a fresh immutable release ID.' }
New-Item -ItemType Directory -Path $output -Force | Out-Null
& dotnet test (Join-Path $root 'tests/BlobTransfer.Tests/BlobTransfer.Tests.csproj') --configuration Release -m:1 -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
$publish = Join-Path $root "artifacts/publish/$ReleaseId"
& dotnet publish (Join-Path $root 'src/BlobTransfer/BlobTransfer.csproj') --configuration Release --output $publish --no-self-contained -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Publish failed.' }
if (!(Test-Path (Join-Path $publish 'host.json'))) { throw 'Package is missing host.json.' }
if (!(Test-Path (Join-Path $publish 'functions.metadata'))) { throw 'Package is missing function metadata.' }
Test-FunctionMetadata -Path (Join-Path $publish 'functions.metadata')
Add-Type -AssemblyName System.IO.Compression.FileSystem
$zip = Join-Path $output "$ReleaseId.zip"
# Includes dot-prefixed .azurefunctions files, unlike Compress-Archive on some platforms.
[IO.Compression.ZipFile]::CreateFromDirectory($publish, $zip, [IO.Compression.CompressionLevel]::Optimal, $false)
$hash = (Get-FileHash $zip -Algorithm SHA256).Hash.ToLowerInvariant()
"$hash  $ReleaseId.zip" | Set-Content -LiteralPath (Join-Path $output 'SHA256SUMS') -Encoding utf8
Copy-Item -LiteralPath (Join-Path $publish 'functions.metadata') -Destination $output
foreach ($environmentName in @('dev','qa','uat','prod')) {
    $null = Export-Templates -EnvironmentName $environmentName -OutputPath (Join-Path $output "templates/$environmentName")
}
$commit = & git -C $root rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Package provenance requires a Git checkout.' }
$changes = @(& git -C $root status --porcelain)
if ($LASTEXITCODE -ne 0) { throw 'Cannot determine Git worktree state.' }
$files = @(Get-ChildItem -LiteralPath $output -Recurse -File | ForEach-Object {
    @{ path = [IO.Path]::GetRelativePath($output, $_.FullName).Replace('\','/'); sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
})
@{
    schemaVersion = 1; releaseId = $ReleaseId; packageSha256 = $hash
    sourceCommit = "$commit"; dirtyWorktree = ($changes.Count -gt 0)
    sdkVersion = "$(& dotnet --version)"; createdUtc = [DateTimeOffset]::UtcNow.ToString('O')
    files = $files
} | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $output 'release.json') -Encoding utf8
Write-Host "Package: $zip"
Write-Host "SHA-256: $hash"
