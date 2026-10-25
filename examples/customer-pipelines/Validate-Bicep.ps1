#requires -Version 7.4
[CmdletBinding()]
param()

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# Inputs choose values, never file paths, command text, tool paths or module registry addresses.
$requestId = [Guid]::Empty
if (-not [Guid]::TryParseExact($env:STUDIO_REQUEST_ID, 'D', [ref]$requestId) -or $requestId -eq [Guid]::Empty) { throw 'Invalid request id' }
if ($env:STUDIO_PLAN_DIGEST -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid plan digest' }
if ($env:STUDIO_SOURCE_REVISION -cnotmatch '^(?:[a-f0-9]{40}|[a-f0-9]{64})$') { throw 'Invalid source revision' }
if ($env:STUDIO_ENVIRONMENT -cne 'dev' -or $env:STUDIO_MODULE -cne 'local-storage-example' -or $env:STUDIO_LOCATION -cnotin @('eastus2', 'westus2')) { throw 'Unapproved validation parameters' }
if ($env:STUDIO_TRIGGER_REVISION -cne $env:STUDIO_SOURCE_REVISION) { throw 'Approved source revision drifted' }

$git = (Get-Command git -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$repoRoot = & $git -C $PSScriptRoot rev-parse --show-toplevel
if ($LASTEXITCODE -ne 0 -or @($repoRoot).Count -ne 1) { throw 'A checked-out repository is required' }
$repoRoot = [IO.Path]::GetFullPath($repoRoot)
$head = & $git -C $repoRoot rev-parse --verify HEAD
if ($LASTEXITCODE -ne 0 -or $head -cne $env:STUDIO_SOURCE_REVISION) { throw 'Checkout does not match the approved commit' }
$expectedRoot = [IO.Path]::GetFullPath((Join-Path $repoRoot 'examples/customer-pipelines'))
if ([IO.Path]::GetFullPath($PSScriptRoot) -cne $expectedRoot) { throw 'Unexpected example root' }

$relativeFiles = @(
    'examples/customer-pipelines/Validate-Bicep.ps1',
    'examples/customer-pipelines/bicepconfig.json',
    'examples/customer-pipelines/main.bicep',
    'examples/customer-pipelines/main.bicepparam',
    'examples/customer-pipelines/modules/storage-account.bicep'
)
foreach ($relative in $relativeFiles) {
    $path = [IO.Path]::GetFullPath((Join-Path $repoRoot $relative))
    if (-not $path.StartsWith($expectedRoot + [IO.Path]::DirectorySeparatorChar, [StringComparison]::Ordinal)) { throw 'Source path escaped the example' }
    $candidate = Get-Item -LiteralPath $path -Force -ErrorAction Stop
    while ($true) {
        if (($candidate.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'Linked source paths are forbidden' }
        if ($candidate.FullName -ceq $repoRoot) { break }
        $candidate = Get-Item -LiteralPath (Split-Path -Parent $candidate.FullName) -Force
    }
    & $git -C $repoRoot ls-files --error-unmatch -- $relative | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'Compilation inputs must be tracked in the approved commit' }
}
& $git -C $repoRoot diff --quiet HEAD -- @relativeFiles
if ($LASTEXITCODE -ne 0) { throw 'Compilation inputs differ from the approved commit' }

$bicep = (Get-Command bicep -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$version = & $bicep --version
if ($LASTEXITCODE -ne 0 -or $version -cnotlike 'Bicep CLI version 0.48.1 *') { throw 'Preinstalled Bicep 0.48.1 is required' }
$outputRoot = Join-Path ([IO.Path]::GetTempPath()) ('platform-studio-validation-' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $outputRoot
$template = Join-Path $outputRoot 'main.json'
$parameters = Join-Path $outputRoot 'main.parameters.json'
& $bicep build (Join-Path $expectedRoot 'main.bicep') --no-restore --outfile $template
if ($LASTEXITCODE -ne 0) { throw 'Bicep compilation failed' }
& $bicep build-params (Join-Path $expectedRoot 'main.bicepparam') --no-restore --outfile $parameters
if ($LASTEXITCODE -ne 0) { throw 'Bicep parameter compilation failed' }
$compiled = Get-Content -LiteralPath $parameters -Raw | ConvertFrom-Json
if ($compiled.parameters.location.value -cne $env:STUDIO_LOCATION -or $compiled.parameters.environment.value -cne $env:STUDIO_ENVIRONMENT) { throw 'Compiled parameters differ from approved inputs' }

# This is local compilation evidence. It is never a provider dispatch or deployment receipt.
[ordered]@{
    schemaVersion = 'platform-studio.customer-pipeline-validation.v1'
    mock = $true
    deploymentVerified = $false
    requestId = $env:STUDIO_REQUEST_ID
    planDigest = $env:STUDIO_PLAN_DIGEST
    sourceRevision = $head
    environment = $env:STUDIO_ENVIRONMENT
    location = $env:STUDIO_LOCATION
    module = $env:STUDIO_MODULE
    templateSha256 = (Get-FileHash -LiteralPath $template -Algorithm SHA256).Hash.ToLowerInvariant()
    parametersSha256 = (Get-FileHash -LiteralPath $parameters -Algorithm SHA256).Hash.ToLowerInvariant()
} | ConvertTo-Json -Depth 4
