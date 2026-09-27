#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
Push-Location $root
try {
    function Get-IgnoredPaths([string[]]$Paths){
        # Avoid PowerShell's CRLF stdin conversion: Git treats that CR as part of a path.
        for($offset=0;$offset -lt $Paths.Count;$offset+=40){
            $batch=$Paths[$offset..([Math]::Min($offset+39,$Paths.Count-1))]
            & git -c core.quotepath=false check-ignore --no-index -- @batch
            if($LASTEXITCODE -notin @(0,1)){throw 'Unable to evaluate ignore rules.'}
        }
    }
    # Gitignore does not untrack existing files: check the index independently.
    $trackedIgnored=@(& git ls-files --cached --ignored --exclude-standard)
    if($LASTEXITCODE -ne 0){throw 'Unable to inspect the Git index.'}
    if($trackedIgnored.Count){throw "Tracked files now match ignore rules; review explicitly before untracking:`n$($trackedIgnored -join "`n")"}

    $privateProbes=@(
        '.local/portal/agents/example/auth.json','artifacts/portal-agents/example/evidence.json',
        'src/SelfService.Portal/portal.local.json','src/BlobTransfer/local.settings.json',
        '.env','.env.development','.codex/auth.json','.codex/sessions/example.jsonl',
        '.codex/state.sqlite-wal','credentials/msal_token_cache.bin','credentials/client.pfx',
        'src/SelfService.Portal/bin/Release/app.dll','tests/example/TestResults/run.trx',
        'playwright-report/index.html','test-results/browser/screenshot.png'
    )
    $ignored=@(Get-IgnoredPaths $privateProbes)
    foreach($path in $privateProbes){if($path -notin $ignored){throw "Generated/private path is not ignored: $path"}}

    # Evaluate real first-party source independently of Git's normal ignored-file filtering.
    $sourceRoots=@('src','scripts','workloads','modules','environments','platform','pipelines','.agents','config','schemas','self-service')
    $sourceFiles=@(foreach($folder in $sourceRoots){
        Get-ChildItem -LiteralPath (Join-Path $root $folder) -Recurse -File |
            Where-Object {$_.Extension -in @('.cs','.csproj','.js','.mjs','.ps1','.bicep','.bicepparam') -and $_.FullName -notmatch '[\\/](bin|obj|node_modules|\.local|\.tools|artifacts|TestResults)[\\/]'} |
            ForEach-Object {[IO.Path]::GetRelativePath($root,$_.FullName).Replace('\','/')}
    })
    $sourceFiles+=@('src/SelfService.Portal/portal.example.json','config/portal-topologies.json',
        '.agents/skills/platform-change-review/SKILL.md','.agents/skills/platform-request-design/SKILL.md',
        'tests/SelfService.Portal.Tests/AgentTests.cs','tests/portal/diagram-fixture-server.mjs',
        '.env.example','.codex/AGENTS.md','.codex/skills/example/SKILL.md','.codex/config.toml')
    $hiddenSource=@(Get-IgnoredPaths $sourceFiles)
    if($hiddenSource.Count){throw "Source files incorrectly ignored:`n$($hiddenSource -join "`n")"}

    $portal=Join-Path $root 'src/SelfService.Portal'
    [xml]$project=Get-Content -LiteralPath (Join-Path $portal 'SelfService.Portal.csproj') -Raw
    foreach($reference in $project.SelectNodes('//ProjectReference')){
        $target=[IO.Path]::GetFullPath((Join-Path $portal $reference.Include))
        if($target -match '[\\/]tests[\\/]|\.Tests\.csproj$'){throw 'The portal must not reference a test project.'}
    }
    foreach($source in Get-ChildItem -LiteralPath $portal -Recurse -File | Where-Object {$_.Extension -in @('.cs','.js','.mjs') -and $_.FullName -notmatch '[\\/](bin|obj)[\\/]'}){
        $text=Get-Content -LiteralPath $source.FullName -Raw
        if($text -match 'diagram-fixture-server|localhost:5098|["''](?:\.\.?/)*tests[/\\]'){
            throw "Application source references a test-only path: $([IO.Path]::GetRelativePath($root,$source.FullName))"
        }
    }
    $deps=Join-Path $portal 'bin/Release/net10.0/SelfService.Portal.deps.json'
    if(Test-Path -LiteralPath $deps){
        $libraries=(Get-Content -LiteralPath $deps -Raw|ConvertFrom-Json).libraries.PSObject.Properties.Name
        if(@($libraries|Where-Object {$_ -match '^xunit|TestPlatform|Microsoft.NET.Test.Sdk|SelfService.Portal.Tests'}).Count){throw 'Test dependencies found in the application Release dependency graph.'}
    }
    $untracked=@(& git ls-files --others --exclude-standard)
    if($LASTEXITCODE -ne 0){throw 'Unable to inspect untracked source.'}
    Write-Host "PASS: $($privateProbes.Count) ignore probes; $($sourceFiles.Count) source paths remain eligible for Git; no tracked ignored files or portal test dependencies."
    Write-Host "Untracked reviewable files: $($untracked.Count). Include intended source and docs in your next commit. Nothing was staged or removed."
} finally {Pop-Location}
