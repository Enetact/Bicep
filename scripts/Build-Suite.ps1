#requires -Version 7.4
<# .SYNOPSIS
Publishes every executable to a new isolated directory without replacing any running application's files.
#>
[CmdletBinding(SupportsShouldProcess)]
param()
. (Join-Path $PSScriptRoot 'Suite.Common.ps1')
. (Join-Path $PSScriptRoot 'local-common.ps1')
if (!$IsWindows) { throw 'The local application suite currently supports Windows only.' }
$root = Get-SuiteRoot
$dotnet = (Get-Command dotnet -CommandType Application -ErrorAction Stop | Select-Object -First 1).Source
if (!$PSCmdlet.ShouldProcess($root,'Build all six applications into a new isolated suite bundle')) { return }
$buildRoot = New-SuitePrivateDirectory (Join-Path $root ('.local/suite/builds/'+[guid]::NewGuid().ToString('N')))
$sourceHash = Get-SuiteSourceHash
$projects = @(Get-SuiteProjects)
$results = @()
Push-Location $root
try {
    foreach ($project in $projects) {
        $output = Join-Path $buildRoot $project.Name
        $log = Join-Path $buildRoot ($project.Name+'.build.log')
        # Native exit codes are captured immediately; PowerShell helper success is represented by exceptions.
        $lines = @(& $dotnet publish (Join-Path $root $project.Project) -c Release --artifacts-path (Join-Path $buildRoot 'intermediate') --no-self-contained `
            -o $output -p:RestoreLockedMode=true -p:UseSharedCompilation=false -m:1 -nodeReuse:false 2>&1)
        $nativeExit = $LASTEXITCODE
        [IO.File]::WriteAllText($log,($lines -join "`n"),[Text.UTF8Encoding]::new($false))
        if ($nativeExit -ne 0) { throw "Suite build failed for $($project.Name). Inspect $log. Running applications were not changed." }
        $assembly = Join-Path $output $project.Assembly
        if (!(Test-Path -LiteralPath $assembly -PathType Leaf)) { throw "Suite build did not produce $($project.Assembly)." }
        if (Test-Path -LiteralPath (Join-Path $output 'portal.local.json')) { throw 'Local portal configuration must not enter a suite build bundle.' }
        $results += [pscustomobject]@{ Name=$project.Name; Project=$project.Project; Assembly=$project.Assembly; Sha256=(Get-FileHash -LiteralPath $assembly -Algorithm SHA256).Hash.ToLowerInvariant(); Runtime=$project.Runtime }
    }
    if ((Get-SuiteSourceHash) -cne $sourceHash) { throw 'Source changed during the suite build. No build receipt was committed; running services are unchanged.' }
    Write-LocalBuildReceipt -BuildRoot $buildRoot | Out-Null
    $receipt = [ordered]@{ Schema='platform.suite-build/v1'; Root=$root; SourceSha256=$sourceHash; BuiltUtc=[DateTimeOffset]::UtcNow.ToString('O'); Projects=$results }
    [IO.File]::WriteAllText((Join-Path $buildRoot 'build.json'),($receipt | ConvertTo-Json -Depth 6),[Text.UTF8Encoding]::new($false))
    [pscustomobject]@{ State='Built'; BuildRoot=$buildRoot; SourceSha256=$sourceHash; ProjectCount=$results.Count; Receipt=(Join-Path $buildRoot 'build.json') }
}
catch { Write-Warning "The isolated suite build is incomplete. Its logs are retained at $buildRoot. No service was stopped."; throw }
finally { Pop-Location }
