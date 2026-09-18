#requires -Version 7.4
. "$PSScriptRoot/common.ps1"
$localRoot = Join-Path (Get-ProjectRoot) '.local'

function Assert-LocalPath([string]$Path) {
    $base = [IO.Path]::GetFullPath($localRoot).TrimEnd('\','/')
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -ne $base -and !$full.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Path is outside the project local runtime directory.' }
    $current = $full
    while ($current.Length -ge $base.Length) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Local runtime paths must not be junctions or symbolic links.' }
        }
        $current = Split-Path -Parent $current
    }
    return $full
}

function Get-LocalTools {
    $path = Join-Path $localRoot 'tools.json'
    if (!(Test-Path $path)) { throw 'Run scripts/Setup-Local.ps1 first.' }
    $tools = Get-Content $path -Raw | ConvertFrom-Json
    $env:PATH = (Split-Path $tools.dotnet -Parent) + ';' + (Split-Path $tools.node -Parent) + ';' + $env:PATH
    $env:DOTNET_ROOT = Split-Path $tools.dotnet -Parent
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:FUNCTIONS_CORE_TOOLS_TELEMETRY_OPTOUT = '1'
    return $tools
}

function Get-OwnedProcess($Record) {
    $process = Get-Process -Id $Record.id -ErrorAction SilentlyContinue
    if (!$process) { return $null }
    if ($process.StartTime.ToUniversalTime().Ticks.ToString() -ne $Record.startTicks -or $process.Path -ine $Record.path) {
        throw "PID $($Record.id) no longer belongs to this local run; refusing to control it."
    }
    return $process
}

function New-ProcessRecord($Process) {
    @{ id=$Process.Id; startTicks=$Process.StartTime.ToUniversalTime().Ticks.ToString(); path=$Process.Path }
}

function Get-LocalState {
    $path = Join-Path $localRoot 'run.json'
    if (!(Test-Path -LiteralPath $path)) { return $null }
    $state = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($state.root -ine [IO.Path]::GetFullPath((Get-ProjectRoot))) { throw 'Local process receipt belongs to another checkout.' }
    return $state
}

function Assert-LocalPortsFree {
    foreach ($port in @(10000,10001,10002,7071)) {
        if (Get-NetTCPConnection -LocalPort $port -State Listen -ErrorAction SilentlyContinue) { throw "Port $port is occupied. Stop its owning service or the existing local run; no process was stopped." }
    }
}
