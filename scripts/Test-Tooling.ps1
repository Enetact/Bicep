[CmdletBinding()]
param()
. "$PSScriptRoot/common.ps1"
$cases = 0
function Assert-Throws([scriptblock]$Action) {
    $threw = $false
    try { & $Action } catch { $threw = $true }
    if (!$threw) { throw 'Expected validation to reject invalid input.' }
}
foreach ($command in @('build', 'build-params')) {
    $argsWithSpaces = @($command, 'C:/source folder/input.bicep', '--outfile', 'C:/output folder/result.json')
    $actual = @(ConvertTo-AzBicepArguments -Arguments $argsWithSpaces)
    if (($actual | ConvertTo-Json -Compress) -cne (@($command, '--file', $argsWithSpaces[1], '--outfile', $argsWithSpaces[3]) | ConvertTo-Json -Compress)) {
        throw 'Azure CLI argument translation lost a path or option.'
    }
    $cases++
}
Assert-Throws { ConvertTo-AzBicepArguments -Arguments @('build', '--outfile', 'file') }
$cases++
$map = [System.Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
$map.Add('', 'default'); $map.Add('claims/', 'claims'); $map.Add('claims/special/', 'special')
if ((Resolve-SourceScope 'claims/special/smoke/file.txt' $map) -ne 'special' -or
    (Resolve-SourceScope 'Claims/smoke/file.txt' $map) -ne 'default') { throw 'Scope resolution must use ordinal longest-prefix matching.' }
$cases++
Assert-Throws { Resolve-SourceScope 'smoke/file.txt' @{ 'claims/' = 'claims' } }
$cases++
Assert-Throws { Resolve-SourceScope 'smoke/file.txt' @{ '' = 'InvalidScope' } }
$cases++
Assert-FirstBootstrap -Apps @() -Workload 'teamone' -EnvironmentName 'dev'
$cases++
Assert-FirstBootstrap -Apps @(@{name='func-teamtwo-dev-123'}, @{name='func-teamone-qa-123'}) -Workload 'teamone' -EnvironmentName 'dev'
$cases++
Assert-Throws { Assert-FirstBootstrap -Apps @(@{name='FUNC-TEAMONE-DEV-123'}) -Workload 'teamone' -EnvironmentName 'dev' }
$cases++
$folder = Join-Path (Get-ProjectRoot) ('artifacts/tooling-tests/' + [guid]::NewGuid().ToString('N'))
Assert-NoVulnerablePackages @{ projects=@(@{path='clean.csproj'}) }
$cases++
Assert-Throws { Assert-NoVulnerablePackages @{ projects=@() } }
$cases++
Assert-Throws { Assert-NoVulnerablePackages @{ projects=@(@{path='app.csproj'}); problems=@(@{text='feed unavailable'}) } }
$cases++
foreach ($kind in @('topLevelPackages', 'transitivePackages')) {
    $framework = @{ framework='net10.0'; $kind=@(@{ id='Unsafe.Package'; vulnerabilities=@(@{severity='High'}) }) }
    Assert-Throws { Assert-NoVulnerablePackages @{ projects=@(@{path='app.csproj'; frameworks=@($framework)}) } }
    $cases++
}
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$path = Join-Path $folder 'functions.metadata'
$functions = @(
    @{ name='DispatchUploadedBlob'; bindings=@(@{type='blobTrigger'}) }
    @{ name='CopyUploadedBlob'; bindings=@(@{type='queueTrigger'}) }
    @{ name='ReconcileTransfers'; bindings=@(@{type='timerTrigger'}) }
    @{ name='MonitorTransferPoison'; bindings=@(@{type='timerTrigger'}) }
    @{ name='AuditTransferLedger'; bindings=@(@{type='timerTrigger'}) }
)
$functions | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path
Test-FunctionMetadata $path
$cases++
$functions[1].bindings[0].type = 'blobTrigger'
$functions | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path
Assert-Throws { Test-FunctionMetadata $path }
$cases++
$functions[1].bindings[0].type = 'queueTrigger'
$functions[0].name = 'CopyUploadedBlob'
$functions | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path
Assert-Throws { Test-FunctionMetadata $path }
$cases++
$functions[0].name = 'LegacyCopy'
$functions | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path
Assert-Throws { Test-FunctionMetadata $path }
$cases++
$functions | Select-Object -Skip 1 | ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $path
Assert-Throws { Test-FunctionMetadata $path }
$cases++
@{ passed=$cases; failed=0 } | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'results.json')
Write-Host "PASS: $cases tooling contract cases. Evidence: $folder"
