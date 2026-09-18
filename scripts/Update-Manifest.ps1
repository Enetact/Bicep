[CmdletBinding()]
param([switch]$Check)
. "$PSScriptRoot/common.ps1"
$root = Get-ProjectRoot
$paths = @(& git -C $root -c core.quotepath=false ls-files --cached --others --exclude-standard)
if ($LASTEXITCODE -ne 0) { throw 'Manifest generation requires a Git checkout.' }
$lines = @($paths | Sort-Object -Unique | Where-Object {
    $_ -ne 'MANIFEST.sha256' -and (Test-Path -LiteralPath (Join-Path $root $_) -PathType Leaf)
} | ForEach-Object {
    $hash = (Get-FileHash -LiteralPath (Join-Path $root $_) -Algorithm SHA256).Hash.ToLowerInvariant()
    "$hash  $_"
})
$manifest = Join-Path $root 'MANIFEST.sha256'
if ($Check) {
    $existing = @(Get-Content -LiteralPath $manifest)
    if (@(Compare-Object $existing $lines -CaseSensitive).Count -gt 0) {
        throw 'Source manifest differs: regenerate with scripts/Update-Manifest.ps1 and review the change.'
    }
    Write-Host "PASS: all $($lines.Count) source manifest entries match; no unlisted source files."
} else {
    [IO.File]::WriteAllText($manifest, ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    Write-Host "Updated $($lines.Count) source manifest entries."
}
