#requires -Version 7.4
[CmdletBinding()]
param([switch]$ClearData)
. "$PSScriptRoot/local-common.ps1"
if (!$ClearData) { throw 'Specify -ClearData to explicitly erase this checkout''s local emulator data. Tools and logs are retained.' }
& "$PSScriptRoot/Stop-Local.ps1"
Assert-LocalPortsFree
$data = Assert-LocalPath (Join-Path $localRoot 'data')
if (Test-Path -LiteralPath $data) {
    if (@(Get-ChildItem -LiteralPath $data -Recurse -Force | Where-Object { $_.Attributes -band [IO.FileAttributes]::ReparsePoint }).Count) { throw 'Refusing to reset data containing junctions or symbolic links.' }
    Remove-Item -LiteralPath $data -Recurse -Force
}
Write-Host 'Local emulator data reset. Start a new run with Run-Local.ps1.'
