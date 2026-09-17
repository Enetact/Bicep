[CmdletBinding()]
param()
. "$PSScriptRoot/common.ps1"
$root = Get-ProjectRoot
$results = Join-Path $root 'artifacts/test-results'
New-Item -ItemType Directory -Path $results -Force | Out-Null
& "$PSScriptRoot/Test-Tooling.ps1"
foreach ($environmentName in @('dev','qa','uat','prod')) {
    $output = Export-Templates -EnvironmentName $environmentName
    Write-Host "Compiled $environmentName -> $output"
}
& dotnet test (Join-Path $root 'tests/BlobTransfer.Tests/BlobTransfer.Tests.csproj') --configuration Release -m:1 -p:RestoreLockedMode=true --logger 'trx;LogFileName=unit.trx' --results-directory $results
if ($LASTEXITCODE -ne 0) { throw 'Tests failed.' }
& dotnet build (Join-Path $root 'src/TransferTool/TransferTool.csproj') -c Release -m:1 -p:RestoreLockedMode=true
if ($LASTEXITCODE -ne 0) { throw 'Upload/recovery tool build failed.' }
$audit = & dotnet list (Join-Path $root 'src/BlobTransfer/BlobTransfer.csproj') package --vulnerable --include-transitive --format json
if ($LASTEXITCODE -ne 0) { throw 'Package vulnerability query failed.' }
$audit | Set-Content -LiteralPath (Join-Path $results 'vulnerabilities.json') -Encoding utf8
$report = $audit -join "`n" | ConvertFrom-Json -AsHashtable
Assert-NoVulnerablePackages -Report $report
Write-Host "No reported vulnerable Function dependencies. Advisory evidence: $results/vulnerabilities.json"
