[CmdletBinding()]
param()
. "$PSScriptRoot/common.ps1"
$root = Get-ProjectRoot
foreach ($environmentName in @('dev','qa','uat','prod')) {
    $output = Export-Templates -EnvironmentName $environmentName
    Write-Host "Compiled $environmentName -> $output"
}
& dotnet test (Join-Path $root 'tests/BlobTransfer.Tests/BlobTransfer.Tests.csproj') --configuration Release -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
& dotnet build (Join-Path $root 'src/TransferTool/TransferTool.csproj') -c Release -m:1 -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Upload/recovery tool build failed.' }
& dotnet list (Join-Path $root 'src/BlobTransfer/BlobTransfer.csproj') package --vulnerable --include-transitive
if ($LASTEXITCODE -ne 0) { throw 'Package vulnerability query failed.' }
Write-Host 'Review vulnerability output. A successful CLI exit alone does not prove no advisories.'
