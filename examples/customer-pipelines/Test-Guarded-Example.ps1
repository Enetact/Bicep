#requires -Version 7.4
[CmdletBinding()]
param(
    [string] $BicepExecutable
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if (-not $BicepExecutable) { $BicepExecutable = Join-Path $repositoryRoot '.tools/bicep/0.48.1/bicep.exe' }
$BicepExecutable = (Get-Item -LiteralPath $BicepExecutable -ErrorAction Stop).FullName
$git = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$fixtureRoot = Join-Path $repositoryRoot ('artifacts/customer-pipeline-guard-tests/' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'examples') -Force
Copy-Item -LiteralPath $PSScriptRoot -Destination (Join-Path $fixtureRoot 'examples/customer-pipelines') -Recurse
$null = New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'empty-git-template')
$null = New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'temp')
$null = New-Item -ItemType Directory -Path (Join-Path $fixtureRoot 'bundle')

# Only this new fixture repository receives a local commit. No global Git settings or original source are changed.
$previousEnvironment = @{}
foreach ($key in @('GIT_CONFIG_NOSYSTEM', 'GIT_CONFIG_GLOBAL', 'GIT_CONFIG_COUNT', 'GIT_DIR', 'GIT_COMMON_DIR', 'GIT_WORK_TREE',
    'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES', 'PATH', 'TMP', 'TEMP', 'DOTNET_BUNDLE_EXTRACT_BASE_DIR',
    'STUDIO_REQUEST_ID', 'STUDIO_PLAN_DIGEST', 'STUDIO_SOURCE_REVISION', 'STUDIO_TRIGGER_REVISION', 'STUDIO_ENVIRONMENT', 'STUDIO_LOCATION', 'STUDIO_MODULE')) {
    $previousEnvironment[$key] = [Environment]::GetEnvironmentVariable($key)
}
try {
    $env:GIT_CONFIG_NOSYSTEM = '1'
    $env:GIT_CONFIG_GLOBAL = Join-Path $fixtureRoot 'nonexistent-global-gitconfig'
    foreach ($key in @('GIT_CONFIG_COUNT', 'GIT_DIR', 'GIT_COMMON_DIR', 'GIT_WORK_TREE', 'GIT_INDEX_FILE', 'GIT_OBJECT_DIRECTORY', 'GIT_ALTERNATE_OBJECT_DIRECTORIES')) {
        Remove-Item -LiteralPath ('Env:' + $key) -ErrorAction SilentlyContinue
    }
    $env:PATH = (Split-Path -Parent $BicepExecutable) + [IO.Path]::PathSeparator + $env:PATH
    $env:TMP = Join-Path $fixtureRoot 'temp'
    $env:TEMP = $env:TMP
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR = Join-Path $fixtureRoot 'bundle'
    & $git -c "init.templateDir=$(Join-Path $fixtureRoot 'empty-git-template')" -C $fixtureRoot init --initial-branch=fixture | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Fixture Git initialization failed' }
    & $git -C $fixtureRoot add -- examples
    if ($LASTEXITCODE -ne 0) { throw 'Fixture Git add failed' }
    & $git -C $fixtureRoot -c user.name='Platform Studio Local Fixture' -c user.email='fixture@invalid.example' -c commit.gpgsign=false -c core.hooksPath=nonexistent-hooks commit -m 'Local offline customer pipeline fixture' | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Fixture Git commit failed' }
    $sha = & $git -C $fixtureRoot rev-parse --verify HEAD
    if ($LASTEXITCODE -ne 0 -or $sha -cnotmatch '^[a-f0-9]{40}$') { throw 'Fixture source SHA unavailable' }
    $env:STUDIO_REQUEST_ID = [Guid]::NewGuid().ToString('D')
    $env:STUDIO_PLAN_DIGEST = 'b' * 64
    $env:STUDIO_SOURCE_REVISION = $sha
    $env:STUDIO_TRIGGER_REVISION = $sha
    $env:STUDIO_ENVIRONMENT = 'dev'
    $env:STUDIO_MODULE = 'local-storage-example'
    $entryPoint = Join-Path $fixtureRoot 'examples/customer-pipelines/Validate-Bicep.ps1'
    foreach ($location in @('eastus2', 'westus2')) {
        $env:STUDIO_LOCATION = $location
        $receipt = (& $entryPoint) | ConvertFrom-Json
        if ($receipt.location -cne $location -or $receipt.environment -cne 'dev' -or
            $receipt.sourceRevision -cne $sha -or -not $receipt.mock -or $receipt.deploymentVerified -or
            $receipt.templateSha256 -cnotmatch '^[a-f0-9]{64}$' -or $receipt.parametersSha256 -cnotmatch '^[a-f0-9]{64}$') {
            throw 'Guarded compilation evidence did not match fixture inputs'
        }
        Write-Output "Guarded clean-commit compilation passed: $location/dev; mock=true; deploymentVerified=false."
    }
    $env:STUDIO_SOURCE_REVISION = 'a' * 40
    $env:STUDIO_TRIGGER_REVISION = $env:STUDIO_SOURCE_REVISION
    $rejected = $false
    try { & $entryPoint | Out-Null }
    catch { if ($_.Exception.Message -eq 'Checkout does not match the approved commit') { $rejected = $true } else { throw } }
    if (-not $rejected) { throw 'Source drift was not rejected' }
    $env:STUDIO_SOURCE_REVISION = $sha
    $env:STUDIO_TRIGGER_REVISION = $sha
    $child = Join-Path $fixtureRoot 'examples/customer-pipelines/modules/storage-account.bicep'
    $original = [IO.File]::ReadAllBytes($child)
    try {
        [IO.File]::AppendAllText($child, "`n// modified after approval`n")
        $rejected = $false
        try { & $entryPoint | Out-Null }
        catch { if ($_.Exception.Message -eq 'Compilation inputs differ from the approved commit') { $rejected = $true } else { throw } }
        if (-not $rejected) { throw 'Modified source was not rejected' }
    }
    finally { [IO.File]::WriteAllBytes($child, $original) }
    Write-Output 'Source drift and modified tracked compilation inputs were rejected before compilation.'
    Write-Output "Local fixture retained under $fixtureRoot"
}
finally {
    foreach ($key in $previousEnvironment.Keys) {
        if ($null -eq $previousEnvironment[$key]) { Remove-Item -LiteralPath ('Env:' + $key) -ErrorAction SilentlyContinue }
        else { [Environment]::SetEnvironmentVariable($key, $previousEnvironment[$key]) }
    }
}
