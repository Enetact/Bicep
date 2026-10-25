#!/usr/bin/env pwsh
#requires -Version 7.4
[CmdletBinding()]
param([switch]$VerifyOnly)

$ErrorActionPreference = 'Stop'
$repoRoot = Split-Path -Parent $PSScriptRoot
$providerRoot = Join-Path $PSScriptRoot 'mcp'
$localRoot = Join-Path $repoRoot '.local/mcp'
$bicepTools = Join-Path $repoRoot '.tools/mcp/bicep'
$bicepVersion = '0.48.1'
$nodeCommand = (Get-Command node -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$npmCommand = (Get-Command npm -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$dotnetCommand = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
$nodeVersion = & $nodeCommand --version
if ($LASTEXITCODE -ne 0 -or [int]($nodeVersion.TrimStart('v').Split('.')[0]) -lt 22) {
    throw 'The pinned Azure MCP provider requires Node.js 22 or later.'
}

# All installation state and caches stay inside this checkout. No global tool,
# npm, NuGet or CLI configuration is changed, and no provider is signed in.
$environment = @{
    DOTNET_CLI_HOME = (Join-Path $localRoot 'dotnet-home')
    DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    DOTNET_NOLOGO = '1'
    DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE = 'true'
    NUGET_PACKAGES = (Join-Path $localRoot 'nuget-packages')
    NUGET_HTTP_CACHE_PATH = (Join-Path $localRoot 'nuget-http-cache')
    NUGET_PLUGINS_CACHE_PATH = (Join-Path $localRoot 'nuget-plugins-cache')
    DOTNET_BUNDLE_EXTRACT_BASE_DIR = (Join-Path $localRoot '.net-bundle')
}
$previous = @{}
foreach ($name in $environment.Keys) {
    $previous[$name] = [Environment]::GetEnvironmentVariable($name, 'Process')
}
try {
    foreach ($name in $environment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $environment[$name], 'Process')
    }
    if (!$VerifyOnly) {
        New-Item -ItemType Directory -Path $localRoot -Force | Out-Null
        Push-Location $providerRoot
        try {
            & $npmCommand ci --ignore-scripts --no-audit --no-fund --cache (Join-Path $localRoot 'npm-cache')
            if ($LASTEXITCODE -ne 0) { throw 'Pinned MCP npm installation failed.' }
        }
        finally { Pop-Location }

        # Azure DevOps imports keytar through MSAL even in bearer-token mode.
        # Keep npm lifecycle scripts disabled and install only the exact upstream
        # N-API addon asset. SHA256 pins were recorded from atom/node-keytar's
        # v7.9.0 release on 17 October 2026; no native compiler/fallback is run.
        $nativePlatform = (& $nodeCommand -p 'process.platform + "-" + process.arch').Trim()
        $nativeHashes = @{
            'darwin-arm64' = '195f0855e26f83e0d61e228d1b61c7769baa993244518dc9879d9d57104c7cec'
            'darwin-x64' = '4ce56e3896e76a2deaef13f8a36207efa6d94d96678d30200952d83d327eb5f9'
            'linux-arm64' = '1c51ac9d22caa885b0c2f46116fa26ab17cb115951263daea3d60c4417a543bd'
            'linux-x64' = '2d75753fabf9dceb49537ae231e1897de53727bc0fadbc2a01e6374a1443c67c'
            'win32-arm64' = '5af88eb404aaa11dfd984f4bd52eff1721129c811f6d08355dbbf43c9554a94c'
            'win32-x64' = 'f00156103976f77847f9ff96ca96c3facac2b4527edf41dfee5d50be39736459'
        }
        if (!$nativeHashes.ContainsKey($nativePlatform)) { throw "No reviewed keytar prebuild pin exists for $nativePlatform." }
        $keytarRoot = Join-Path $providerRoot 'node_modules/keytar'
        $keytarPackage = Get-Content -LiteralPath (Join-Path $keytarRoot 'package.json') -Raw | ConvertFrom-Json
        if ($keytarPackage.version -ne '7.9.0') { throw 'The keytar package pin changed. Review the native prebuild before installing.' }
        $nativeName = "keytar-v7.9.0-napi-v3-$nativePlatform.tar.gz"
        $nativeFile = Join-Path $localRoot $nativeName
        Invoke-WebRequest "https://github.com/atom/node-keytar/releases/download/v7.9.0/$nativeName" -OutFile $nativeFile
        if ((Get-FileHash -LiteralPath $nativeFile -Algorithm SHA256).Hash.ToLowerInvariant() -ne $nativeHashes[$nativePlatform]) { throw 'Keytar native prebuild SHA256 differs from the reviewed pin.' }
        & tar -xzf $nativeFile -C $keytarRoot 'build/Release/keytar.node'
        if ($LASTEXITCODE -ne 0) { throw 'Could not extract the pinned keytar native addon.' }

        $nugetConfig = Join-Path $localRoot 'nuget.config'
        @'
<?xml version="1.0" encoding="utf-8"?>
<configuration><packageSources><clear /><add key="nuget.org" value="https://api.nuget.org/v3/index.json" /></packageSources></configuration>
'@ | Set-Content -LiteralPath $nugetConfig -Encoding utf8NoBOM
        New-Item -ItemType Directory -Path $bicepTools -Force | Out-Null
        $installedTools = (& $dotnetCommand tool list --tool-path $bicepTools) -join "`n"
        if ($LASTEXITCODE -ne 0) { throw 'Could not inspect the checkout-local Bicep MCP installation.' }
        if ($installedTools -match '(?im)^azure\.bicep\.mcpserver\s+') {
            if ($installedTools -notmatch "(?im)^azure\.bicep\.mcpserver\s+$([regex]::Escape($bicepVersion))\s+") {
                throw 'The checkout-local Bicep MCP tool has a different version. Review it before changing the pin.'
            }
        }
        else {
            & $dotnetCommand tool install Azure.Bicep.McpServer --version $bicepVersion --tool-path $bicepTools --configfile $nugetConfig --verbosity minimal
            if ($LASTEXITCODE -ne 0) { throw 'Pinned Bicep MCP tool installation failed.' }
        }
    }

    $platform = (& $nodeCommand -p 'process.platform').Trim()
    $architecture = (& $nodeCommand -p 'process.arch').Trim()
    $azurePackageRoot = Join-Path $providerRoot "node_modules/@azure/mcp-$platform-$architecture"
    $azurePackage = Get-Content -LiteralPath (Join-Path $azurePackageRoot 'package.json') -Raw | ConvertFrom-Json
    $adoRoot = Join-Path $providerRoot 'node_modules/@azure-devops/mcp'
    $adoPackage = Get-Content -LiteralPath (Join-Path $adoRoot 'package.json') -Raw | ConvertFrom-Json
    $azureBin = @($azurePackage.bin.PSObject.Properties)[0].Value
    $azureExecutable = Join-Path $azurePackageRoot $azureBin
    $adoEntryPoint = Join-Path $adoRoot 'dist/index.js'
    $bicepExecutableName = if ($IsWindows) { 'Azure.Bicep.McpServer.exe' } else { 'Azure.Bicep.McpServer' }
    $bicepExecutable = Join-Path $bicepTools $bicepExecutableName
    foreach ($requiredFile in @($azureExecutable, $adoEntryPoint, $bicepExecutable)) {
        if (!(Test-Path -LiteralPath $requiredFile -PathType Leaf)) { throw "Missing MCP provider executable: $requiredFile" }
    }
    if ($azurePackage.version -ne '3.0.0' -or $adoPackage.version -ne '2.10.0') {
        throw 'Installed MCP package versions do not match the reviewed pins.'
    }
    & $nodeCommand $adoEntryPoint --help | Out-Null
    if ($LASTEXITCODE -ne 0) { throw 'The Azure DevOps MCP entry point could not load. Re-run setup to restore its pinned native dependency.' }
    $installedBicep = (& $dotnetCommand tool list --tool-path $bicepTools) -join "`n"
    if ($LASTEXITCODE -ne 0 -or $installedBicep -notmatch "(?im)^azure\.bicep\.mcpserver\s+$([regex]::Escape($bicepVersion))\s+") {
        throw 'Installed Bicep MCP package version does not match the reviewed pin.'
    }

    # Return executable locations, never credentials. The broker must resolve its
    # browser session credentials and approve exact state-changing calls separately.
    [ordered]@{
        schema = 'platform.mcp-provider-installation/v1'
        verifiedUtc = [DateTimeOffset]::UtcNow.ToString('O')
        nodeVersion = $nodeVersion
        azure = @{ version = $azurePackage.version; command = $azureExecutable; arguments = @('server', 'start', '--mode', 'all') }
        azureDevOps = @{ version = $adoPackage.version; command = $nodeCommand; entryPoint = $adoEntryPoint; authentication = 'envvar'; tokenVariable = 'ADO_MCP_AUTH_TOKEN' }
        bicep = @{ version = $bicepVersion; command = $bicepExecutable; arguments = @() }
        providerAuthenticationTested = $false
        cloudExecutionPerformed = $false
    } | ConvertTo-Json -Depth 6
}
finally {
    foreach ($name in $environment.Keys) {
        [Environment]::SetEnvironmentVariable($name, $previous[$name], 'Process')
    }
}
