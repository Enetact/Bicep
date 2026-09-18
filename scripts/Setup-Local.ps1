[CmdletBinding()]
param([switch]$CheckOnly)
$ErrorActionPreference = 'Stop'
$root = Split-Path -Parent $PSScriptRoot
# This initial section also runs under Windows PowerShell 5.1.
if ($PSVersionTable.PSVersion -lt [version]'7.4') {
    $pwsh = Join-Path $root '.tools/pwsh/pwsh.exe'
    if (!(Test-Path -LiteralPath $pwsh)) {
        $installed = Get-Command pwsh.exe -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
        if ($installed) {
            $version = & $installed.Source -NoProfile -Command '$PSVersionTable.PSVersion.ToString()'
            if ($LASTEXITCODE -eq 0 -and [version]$version -ge [version]'7.4') { $pwsh = $installed.Source }
        }
    }
    if (!(Test-Path -LiteralPath $pwsh)) {
        if ($CheckOnly) { throw 'PowerShell 7.4+ is missing. Run Setup-Local.ps1 without -CheckOnly to install portable tools.' }
        [Net.ServicePointManager]::SecurityProtocol = [Net.SecurityProtocolType]::Tls12
        $arch = if ($env:PROCESSOR_ARCHITECTURE -eq 'ARM64' -or $env:PROCESSOR_ARCHITEW6432 -eq 'ARM64') { 'arm64' } else { 'x64' }
        $destination = Join-Path $root '.tools/pwsh'
        New-Item -ItemType Directory -Path $destination -Force | Out-Null
        $archive = Join-Path $root '.tools/pwsh.zip'
        Invoke-WebRequest -UseBasicParsing -Uri "https://github.com/PowerShell/PowerShell/releases/download/v7.6.2/PowerShell-7.6.2-win-$arch.zip" -OutFile $archive
        $sums = (Invoke-WebRequest -UseBasicParsing -Uri 'https://github.com/PowerShell/PowerShell/releases/download/v7.6.2/hashes.sha256').Content
        $line = @($sums -split "`n" | Where-Object { $_ -match "PowerShell-7.6.2-win-$arch.zip" })
        if ($line.Count -ne 1 -or ($line[0] -split '\s+')[0] -ne (Get-FileHash $archive).Hash) { throw 'PowerShell download checksum mismatch.' }
        Expand-Archive -LiteralPath $archive -DestinationPath $destination -Force
    }
    & $pwsh -NoProfile -File $PSCommandPath @PSBoundParameters
    if ($LASTEXITCODE -ne 0) { throw 'PowerShell prerequisite setup failed.' }
    return
}
Set-StrictMode -Version Latest
if (!$IsWindows) { throw 'The local lifecycle scripts currently support Windows 11 only.' }
$arch = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if ($arch -notin @('x64','arm64')) { throw 'Use Windows x64 or ARM64.' }
$toolsRoot = Join-Path $root '.tools'

function Install-PortableZip([string]$Url, [string]$Destination, [string]$Hash = '', [string]$Algorithm = 'SHA256') {
    if ($CheckOnly) { throw "Missing prerequisite at $Destination. Run Setup-Local.ps1 without -CheckOnly." }
    New-Item -ItemType Directory -Path $toolsRoot -Force | Out-Null
    $archive = Join-Path $toolsRoot ([guid]::NewGuid().ToString('N') + '.zip')
    Write-Host "Installing portable prerequisite into $Destination"
    Invoke-WebRequest -Uri $Url -OutFile $archive
    if ($Hash -and (Get-FileHash -LiteralPath $archive -Algorithm $Algorithm).Hash -ine $Hash) { throw 'Download checksum mismatch.' }
    Expand-Archive -LiteralPath $archive -DestinationPath $Destination -Force
    Remove-Item -LiteralPath $archive
}
function Find-Binary([string]$Name, [string]$Portable) {
    if (Test-Path -LiteralPath $Portable) { return $Portable }
    $command = Get-Command $Name -CommandType Application -ErrorAction SilentlyContinue | Select-Object -First 1
    if ($command) { return $command.Source }
    return $null
}
$sdk = (Get-Content (Join-Path $root 'global.json') -Raw | ConvertFrom-Json).sdk.version
$dotnet = Find-Binary 'dotnet.exe' (Join-Path $toolsRoot 'dotnet/dotnet.exe')
$sdkReady = $false
if ($dotnet) {
    Push-Location $root
    try { $version = & $dotnet --version 2>$null; $sdkReady = $LASTEXITCODE -eq 0 -and $version -match '^10\.0\.\d+$' -and [version]$version -ge [version]$sdk } finally { Pop-Location }
}
if (!$sdkReady) {
    if ($CheckOnly) { throw "Missing compatible .NET SDK ($sdk or allowed stable roll-forward)." }
    $catalog = Invoke-RestMethod 'https://builds.dotnet.microsoft.com/dotnet/release-metadata/10.0/releases.json'
    $selected = @($catalog.releases | ForEach-Object { $_.sdks } | Where-Object version -eq $sdk | Select-Object -First 1)
    if ($selected.Count -ne 1) { throw "SDK $sdk was not found in Microsoft's release metadata." }
    $file = @($selected[0].files | Where-Object { $_.rid -eq "win-$arch" -and $_.url.EndsWith('.zip') })
    if ($file.Count -ne 1) { throw 'Cannot resolve the .NET SDK archive.' }
    Install-PortableZip $file[0].url (Join-Path $toolsRoot 'dotnet') $file[0].hash 'SHA512'
    $dotnet = Join-Path $toolsRoot 'dotnet/dotnet.exe'
}
$nodeVersion = '24.16.0'
$node = Find-Binary 'node.exe' (Join-Path $toolsRoot "node/node-v$nodeVersion-win-$arch/node.exe")
$nodeReady = $false
if ($node) { $nodeReady = (& $node --version) -match '^v(22|24)\.' -and (Test-Path -LiteralPath (Join-Path (Split-Path $node -Parent) 'npm.cmd')) }
if (!$nodeReady) {
    if ($CheckOnly) { throw 'A complete Node.js 22 or 24 installation with npm is missing.' }
    $filename = "node-v$nodeVersion-win-$arch.zip"
    $sums = (Invoke-WebRequest "https://nodejs.org/dist/v$nodeVersion/SHASUMS256.txt").Content
    $line = @($sums -split "`n" | Where-Object { $_.Trim().EndsWith($filename) })
    if ($line.Count -ne 1) { throw 'Cannot resolve Node.js checksum.' }
    Install-PortableZip "https://nodejs.org/dist/v$nodeVersion/$filename" (Join-Path $toolsRoot 'node') (($line[0] -split '\s+')[0])
    $node = Join-Path $toolsRoot "node/node-v$nodeVersion-win-$arch/node.exe"
}
$npm = Join-Path (Split-Path $node -Parent) 'npm.cmd'
if (!(Test-Path -LiteralPath $npm)) { throw 'The selected Node installation is missing npm.cmd; install a complete Node.js distribution.' }
$func = Find-Binary 'func.exe' (Join-Path $toolsRoot 'functions/func.exe')
$funcReady = $false
if ($func) { $version = & $func --version; $funcReady = $LASTEXITCODE -eq 0 -and $version -match '^4\.' -and [version]$version -ge [version]'4.14.0' }
if (!$funcReady) {
    if ($CheckOnly) { throw 'Azure Functions Core Tools 4.14+ (v4) is missing.' }
    $release = Invoke-RestMethod 'https://api.github.com/repos/Azure/azure-functions-core-tools/releases/tags/4.14.0'
    # The minimal distribution is sufficient for this compiled isolated .NET app.
    $asset = @($release.assets | Where-Object name -eq "Azure.Functions.Cli.min.win-$arch.4.14.0.zip")
    if ($asset.Count -ne 1) { throw 'Cannot resolve the official Functions Core Tools archive.' }
    if (!$asset[0].PSObject.Properties['digest'] -or $asset[0].digest -notlike 'sha256:*') { throw 'Official Functions release is missing its SHA-256 digest.' }
    $digest = $asset[0].digest.Substring(7)
    Install-PortableZip $asset[0].browser_download_url (Join-Path $toolsRoot 'functions') $digest
    $func = Join-Path $toolsRoot 'functions/func.exe'
}
$env:PATH = (Split-Path $dotnet -Parent) + ';' + (Split-Path $node -Parent) + ';' + $env:PATH
$env:DOTNET_ROOT = Split-Path $dotnet -Parent
$npmRoot = Join-Path $toolsRoot 'npm'
$azurite = Join-Path $npmRoot 'node_modules/azurite/dist/src/azurite.js'
$azuritePackage = Join-Path $npmRoot 'node_modules/azurite/package.json'
if (!(Test-Path $azurite) -or !(Test-Path $azuritePackage) -or (Get-Content $azuritePackage -Raw | ConvertFrom-Json).version -ne '3.37.0') {
    if ($CheckOnly) { throw 'Project-local Azurite 3.37.0 is missing.' }
    & $npm install --prefix $npmRoot azurite@3.37.0 --no-audit --no-fund
    if ($LASTEXITCODE -ne 0) { throw 'Local Azurite installation failed.' }
}
# Execute each selected tool before declaring setup successful.
foreach ($entry in @(@{path=$dotnet; args=@('--version')}, @{path=$node; args=@('--version')}, @{path=$func; args=@('--version')}, @{path=$node; args=@($azurite,'--version')})) {
    & $entry.path @($entry.args)
    if ($LASTEXITCODE -ne 0) { throw "Prerequisite could not run: $($entry.path)" }
}
if (!$CheckOnly) {
    $local = Join-Path $root '.local'
    New-Item -ItemType Directory -Path $local -Force | Out-Null
    @{ dotnet=$dotnet; node=$node; func=$func; azurite=$azurite; powershell=(Get-Process -Id $PID).Path; checkedUtc=[DateTimeOffset]::UtcNow.ToString('O') } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $local 'tools.json') -Encoding utf8
}
Write-Host 'Local prerequisites are ready. No Azure account, Docker, Azure CLI, or Bicep is required for local execution.'
