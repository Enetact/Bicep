#requires -Version 7.4
[CmdletBinding()]
param()
. "$PSScriptRoot/common.ps1"
$testRoot=Join-Path (Get-ProjectRoot) ('artifacts/tooling-tests/manifest-'+[guid]::NewGuid().ToString('N'))
$repo=Join-Path $testRoot source
New-Item -ItemType Directory -Path $repo -Force|Out-Null
$results=[Collections.Generic.List[object]]::new()
function Invoke-FixtureGit([string[]]$Arguments){& git -C $repo @Arguments|Out-Null;if($LASTEXITCODE){throw 'Fixture Git command failed.'}}
function Check([bool]$Value){if(!$Value){throw 'Manifest assertion failed.'}}
function Case([string]$Name,[scriptblock]$Action){try{& $Action;$results.Add(@{name=$Name;passed=$true})}catch{throw "Manifest case '$Name': $($_.Exception.Message)"}}
function Reject([scriptblock]$Action,[string]$Expected){$caught=$false;try{& $Action}catch{$caught=$true;Check ($_.Exception.Message.Contains($Expected))};Check $caught}
function Write-Text([string]$Name,[string]$Text){[IO.File]::WriteAllText((Join-Path $repo $Name),$Text,[Text.UTF8Encoding]::new($false))}
Invoke-FixtureGit @('init','--quiet');Invoke-FixtureGit @('config','core.autocrlf','false')
Invoke-FixtureGit @('config','user.email','manifest-fixture@example.invalid');Invoke-FixtureGit @('config','user.name','Manifest fixture')
Write-Text '.gitattributes' "* text=auto eol=lf`n*.bin -text`n*.cmd text eol=crlf`n"
Write-Text 'pricing.json' "{`r`n  `"label`": `"café`",`n  `"rate`": 1`r`n}`n"
Write-Text 'name with spaces.txt' "one`r`ntwo`r`n"
Write-Text 'ascii.bin' "binary-marked`r`nbytes`r`n"
Write-Text 'sample.cmd' "first`r`nsecond`r`n"
[IO.File]::WriteAllBytes((Join-Path $repo sample.bin),[byte[]]@(0,13,10,255,65))
$manifest=Join-Path $repo MANIFEST.sha256
Case 'generation and check accept mixed EOL LF-governed source' {
    & "$PSScriptRoot/Update-Manifest.ps1" -RepositoryRoot $repo
    & "$PSScriptRoot/Update-Manifest.ps1" -RepositoryRoot $repo -Check
}
$snapshot=[IO.File]::ReadAllText($manifest)
Case 'LF normalization preserves manifest identity and non-ASCII content' {
    foreach($file in @('pricing.json','name with spaces.txt')){$path=Join-Path $repo $file;$text=[IO.File]::ReadAllText($path).Replace("`r`n","`n");Write-Text $file $text}
    & "$PSScriptRoot/Update-Manifest.ps1" -RepositoryRoot $repo -Check
    & "$PSScriptRoot/Update-Manifest.ps1" -RepositoryRoot $repo
    Check ([IO.File]::ReadAllText($manifest) -ceq $snapshot)
}
Case 'binary and explicit CRLF paths retain exact byte hashes' {
    foreach($file in @('sample.bin','ascii.bin','sample.cmd')){Check ($snapshot.Contains((Get-FileHash (Join-Path $repo $file)).Hash.ToLowerInvariant()+"  $file"))}
}
Case 'fresh Git clone passes the same committed manifest' {
    Invoke-FixtureGit @('add','--all');Invoke-FixtureGit @('commit','--quiet','-m','Manifest fixture')
    $clone=Join-Path $testRoot checkout
    & git clone --quiet --no-hardlinks $repo $clone
    if($LASTEXITCODE){throw 'Fixture clone failed.'}
    & "$PSScriptRoot/Update-Manifest.ps1" -RepositoryRoot $clone -Check
}
Case 'content change is named and check does not regenerate manifest' {
    Write-Text 'pricing.json' "{`n  `"rate`": 999`n}`n"
    Reject {& "$PSScriptRoot/Update-Manifest.ps1" -RepositoryRoot $repo -Check} 'Changed content: pricing.json'
    Check ([IO.File]::ReadAllText($manifest) -ceq $snapshot)
    Invoke-FixtureGit @('restore','pricing.json')
}
Case 'new untracked source is named' {
    Write-Text 'new.txt' "unlisted`n"
    try{Reject {& "$PSScriptRoot/Update-Manifest.ps1" -RepositoryRoot $repo -Check} 'Unlisted source: new.txt'}finally{Remove-Item -LiteralPath (Join-Path $repo new.txt)}
}
Case 'missing tracked source is named' {
    Remove-Item -LiteralPath (Join-Path $repo 'name with spaces.txt')
    try{Reject {& "$PSScriptRoot/Update-Manifest.ps1" -RepositoryRoot $repo -Check} 'Missing source: name with spaces.txt'}finally{Invoke-FixtureGit @('restore','name with spaces.txt')}
}
Case 'binary CRLF byte change is detected' {
    [IO.File]::WriteAllBytes((Join-Path $repo sample.bin),[byte[]]@(0,10,255,65))
    try{Reject {& "$PSScriptRoot/Update-Manifest.ps1" -RepositoryRoot $repo -Check} 'Changed content: sample.bin'}finally{Invoke-FixtureGit @('restore','sample.bin')}
}
Case 'malformed and duplicate manifest records fail closed' {
    try{
        Write-Text 'MANIFEST.sha256' 'not-a-hash'
        Reject {& "$PSScriptRoot/Update-Manifest.ps1" -RepositoryRoot $repo -Check} 'Malformed'
        Write-Text 'MANIFEST.sha256' ($snapshot+($snapshot.Split("`n")[0])+"`n")
        Reject {& "$PSScriptRoot/Update-Manifest.ps1" -RepositoryRoot $repo -Check} 'Duplicate'
    }finally{Write-Text 'MANIFEST.sha256' $snapshot}
}
$report=@{passed=$results.Count;failed=0;azureCalls=$false;cases=$results}
[IO.File]::WriteAllText((Join-Path $testRoot results.json),($report|ConvertTo-Json -Depth 10),[Text.UTF8Encoding]::new($false))
Write-Host "PASS: $($results.Count) manifest contracts. Evidence: $testRoot"
