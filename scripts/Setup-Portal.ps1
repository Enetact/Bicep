#requires -Version 7.4
[CmdletBinding()]
param([string]$TenantId='', [string]$ClientId='', [switch]$InstallMissing)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(Test-Path -LiteralPath (Join-Path $root '.local/portal/process.json')){throw 'Stop the portal with Stop-Portal.ps1 before setup/build.'}
$arch=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if(!$IsWindows -or $arch -notin @('arm64','x64')){throw 'This setup supports Windows 11 ARM64 and x64.'}
foreach($item in @(@('dotnet','Microsoft.DotNet.SDK.10'),@('node','OpenJS.NodeJS.LTS'))){
    if(!(Get-Command $item[0] -ErrorAction SilentlyContinue)){
        if(!$InstallMissing){throw "$($item[0]) is missing. Rerun with -InstallMissing or install the documented prerequisite."}
        if(!(Get-Command winget -ErrorAction SilentlyContinue)){throw 'Install Windows App Installer (winget), then retry.'}
        & winget install --id $item[1] --exact --architecture $arch --accept-package-agreements --accept-source-agreements --disable-interactivity
        if($LASTEXITCODE -ne 0){throw "Installation failed for $($item[1])."}
        throw 'Installation finished. Open a new PowerShell window and rerun setup to refresh PATH.'
    }
}
$nodeVersion=& node --version
if([int]($nodeVersion.TrimStart('v').Split('.')[0]) -lt 22){throw 'Node.js 22+ is required for saved-discovery analysis.'}
Push-Location $root
try {
    & dotnet --version
    if($LASTEXITCODE -ne 0){throw 'Install the .NET SDK pinned in global.json or a compatible feature-band update.'}
    if($TenantId -or $ClientId){
        $tenant=[guid]::Empty;$client=[guid]::Empty
        if(![guid]::TryParse($TenantId,[ref]$tenant) -or ![guid]::TryParse($ClientId,[ref]$client)){throw 'Provide both portal TenantId and ClientId as GUIDs.'}
        $path=Join-Path $root 'src/SelfService.Portal/portal.local.json'
        $settings=if(Test-Path -LiteralPath $path){Get-Content -LiteralPath $path -Raw|ConvertFrom-Json -AsHashtable}else{Get-Content (Join-Path $root 'src/SelfService.Portal/portal.example.json') -Raw|ConvertFrom-Json -AsHashtable}
        $settings.Portal.TenantId=$tenant.ToString();$settings.Portal.ClientId=$client.ToString()
        $settings|ConvertTo-Json -Depth 10|Set-Content -LiteralPath $path -Encoding utf8
    }
    & dotnet restore src/SelfService.Portal/SelfService.Portal.csproj --locked-mode
    if($LASTEXITCODE -ne 0){throw 'Portal restore failed.'}
    & dotnet build src/SelfService.Portal/SelfService.Portal.csproj -c Release --no-restore
    if($LASTEXITCODE -ne 0){throw 'Portal build failed.'}
    Write-Host 'Portal ready. Run ./scripts/Start-Portal.ps1. Sign-in requires a separate Entra app registration; see docs/local-portal.md.'
} finally {Pop-Location}
