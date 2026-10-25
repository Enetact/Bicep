#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][int]$PortalPid,[Parameter(Mandatory)][datetimeoffset]$PortalStartedUtc)
$ErrorActionPreference = 'Stop'
$root = [IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) '.local/portal/mcp'))
if (!(Test-Path -LiteralPath $root)) { return }
if ((Get-Item -LiteralPath $root).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'MCP ownership root is a reparse point. No process was stopped.' }
foreach ($session in Get-ChildItem -LiteralPath $root -Directory) {
    if ($session.Name -notmatch '^[a-f0-9]{32}$' -or $session.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
    foreach ($provider in Get-ChildItem -LiteralPath $session.FullName -Directory) {
        if ($provider.Name -notin @('bicep', 'azure', 'ado', 'aspire') -or $provider.Attributes -band [IO.FileAttributes]::ReparsePoint) { continue }
        $receipt = [IO.Path]::GetFullPath((Join-Path $provider.FullName 'process.json'))
        if (!$receipt.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'MCP ownership receipt escaped its root.' }
        if (!(Test-Path -LiteralPath $receipt)) { continue }
        if ((Get-Item -LiteralPath $receipt).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'MCP ownership receipt is a reparse point. No process was stopped.' }
        $record = Get-Content -LiteralPath $receipt -Raw | ConvertFrom-Json
        if ($record.schema -ne 'platform.mcp-process/v1' -or $record.provider -ne $provider.Name) { throw 'MCP ownership receipt has an invalid schema or provider.' }
        if ($record.parentPid -ne $PortalPid -or ([datetimeoffset]$record.parentStartedUtc).UtcTicks -ne $PortalStartedUtc.UtcTicks) { continue }
        $child = Get-Process -Id $record.pid -ErrorAction SilentlyContinue
        if ($child) {
            $info = Get-CimInstance Win32_Process -Filter "ProcessId=$([int]$record.pid)"
            $expectedName = switch ($provider.Name) { bicep { 'Azure.Bicep.McpServer.exe' } azure { 'azmcp.exe' } ado { 'node.exe' } aspire { 'aspire.exe' } }
            if ($provider.Name -eq 'aspire') {
                $runtimeRoot = [IO.Path]::GetFullPath((Join-Path (Split-Path $PSScriptRoot -Parent) '.tools/aspire-cli')) + [IO.Path]::DirectorySeparatorChar
                $keyPattern = [regex]::Escape($root) + '\\[a-f0-9]{32}\\aspire\\api-key-[a-f0-9]{32}\.rsp'
                if (!$child.Path.StartsWith($runtimeRoot, [StringComparison]::OrdinalIgnoreCase) -or
                    $info.CommandLine -notmatch ('\sagent\s+mcp\s+--dashboard-url\s+http://127\.0\.0\.1:[0-9]{4,5}/?\s+(?:"@' + $keyPattern + '"|@' + $keyPattern + ')\s+--log-level\s+None\s+--non-interactive\s+--nologo\s*$')) {
                    throw 'Aspire child must use the pinned standalone dashboard-only MCP command. No further process was stopped.'
                }
            }
            if ($info.ParentProcessId -ne $PortalPid -or
                [IO.Path]::GetFullPath($child.Path) -ne [IO.Path]::GetFullPath($record.executable) -or
                [IO.Path]::GetFileName($child.Path) -ne $expectedName -or
                $child.StartTime.ToUniversalTime().Ticks -ne ([datetimeoffset]$record.startedUtc).UtcTicks) {
                throw 'MCP child ownership could not be verified. No further process was stopped.'
            }
            # Use the verified process object, stopping its descendants as well.
            # No process name search or unrelated desktop MCP process is adopted.
            $child.Kill($true)
            if (!$child.WaitForExit(10000)) { throw 'Verified MCP process did not exit within the stop budget.' }
        }
        try { [IO.File]::Delete($receipt) } catch [IO.DirectoryNotFoundException] { } # Concurrent normal disposal.
    }
}
