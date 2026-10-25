#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $root 'scripts/Test-CommercialSourceSecrets.ps1')
$run=Join-Path $root ('artifacts/source-secret-contract-tests/'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($run)|Out-Null
$previous=@{}
foreach($key in @('GIT_CONFIG_NOSYSTEM','GIT_CONFIG_GLOBAL','GIT_CONFIG_COUNT','GIT_DIR','GIT_WORK_TREE','GIT_INDEX_FILE','GIT_COMMON_DIR','GIT_OBJECT_DIRECTORY','GIT_ALTERNATE_OBJECT_DIRECTORIES')){
    $previous[$key]=[Environment]::GetEnvironmentVariable($key);Remove-Item -LiteralPath ('Env:'+$key) -ErrorAction SilentlyContinue
}
$env:GIT_CONFIG_NOSYSTEM='1';$env:GIT_CONFIG_GLOBAL=Join-Path $run 'no-global-gitconfig'
$results=[Collections.Generic.List[object]]::new()
function Check([bool]$Value){if(!$Value){throw 'Source secret contract assertion failed'}}
function Reject([scriptblock]$Action,[string]$Expected){$caught=$false;try{& $Action|Out-Null}catch{if(!$_.Exception.Message.Contains($Expected)){throw};$caught=$true};Check $caught}
function Case([string]$Name,[scriptblock]$Action){try{& $Action;$results.Add(@{name=$Name;passed=$true})}catch{throw "Source secret case '$Name': $($_.Exception.Message)"}}
function Write-Source([string]$Path,[string]$Text){[IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))|Out-Null;[IO.File]::WriteAllText($Path,$Text,[Text.UTF8Encoding]::new($false))}
function New-Fixture{
    $fixture=Join-Path $run ([guid]::NewGuid().ToString('N'));[IO.Directory]::CreateDirectory($fixture)|Out-Null
    & git -C $fixture init --quiet;if($LASTEXITCODE){throw 'Fixture git init failed'}
    Write-Source (Join-Path $fixture '.gitignore') "artifacts/`n.local/`n"
    Write-Source (Join-Path $fixture '.gitattributes') "* text=auto eol=lf`n*.bin -text`n"
    Write-Source (Join-Path $fixture 'src/example.cs') "// café`r`npublic record Example(string Name);`r`n"
    [IO.File]::WriteAllBytes((Join-Path $fixture 'asset.bin'),[byte[]]@(0,13,10,255))
    & git -C $fixture add --all;if($LASTEXITCODE){throw 'Fixture git add failed'}
    & git -C $fixture -c user.name='Secret Snapshot Fixture' -c user.email='fixture@example.invalid' -c commit.gpgsign=false -c core.hooksPath=nonexistent-hooks commit --quiet -m 'Local source-only fixture'
    if($LASTEXITCODE){throw 'Fixture git commit failed'}
    & (Join-Path $root 'scripts/Update-Manifest.ps1') -RepositoryRoot $fixture 6>$null
    return $fixture
}
try{
    Case 'snapshot contains only manifest sources, canonical LF text and exact binary bytes' {
        $f=New-Fixture
        Write-Source (Join-Path $f '.local/auth.json') '{"privateRuntime":"must not stage"}'
        Write-Source (Join-Path $f 'artifacts/ignored.txt') 'runtime output must not stage'
        $snapshot=New-SourceSecretSnapshot $f 'artifacts/snapshot'
        Check ($snapshot.index.files -eq 4 -and !(Test-Path (Join-Path $snapshot.snapshot '.git')) -and !(Test-Path (Join-Path $snapshot.snapshot '.local')) -and !(Test-Path (Join-Path $snapshot.snapshot 'artifacts')))
        Check ([IO.File]::ReadAllText((Join-Path $snapshot.snapshot 'src/example.cs')) -ceq "// café`npublic record Example(string Name);`n")
        Check ([Convert]::ToHexString([IO.File]::ReadAllBytes((Join-Path $snapshot.snapshot 'asset.bin'))) -ceq '000D0AFF')
    }
    Case 'changed manifest-listed source fails before staging' {
        $f=New-Fixture;Write-Source (Join-Path $f 'src/example.cs') '// changed'
        Reject {New-SourceSecretSnapshot $f 'artifacts/snapshot'} 'Changed content:'
    }
    Case 'unlisted source prevents an incomplete clean snapshot' {
        $f=New-Fixture;Write-Source (Join-Path $f 'new.txt') 'unlisted'
        Reject {New-SourceSecretSnapshot $f 'artifacts/snapshot'} 'Unlisted source:'
    }
    Case 'tracked runtime credential path is rejected rather than scanned' {
        $f=New-Fixture;Write-Source (Join-Path $f '.local/auth.json') '{"synthetic":true}'
        & git -C $f add --force .local/auth.json;if($LASTEXITCODE){throw 'Fixture force add failed'}
        & (Join-Path $root 'scripts/Update-Manifest.ps1') -RepositoryRoot $f 6>$null
        Reject {New-SourceSecretSnapshot $f 'artifacts/snapshot'} 'SecretScanUnsafeSourcePath'
    }
    Case 'staging cannot overwrite an existing snapshot' {
        $f=New-Fixture;$null=New-SourceSecretSnapshot $f 'artifacts/snapshot'
        Reject {New-SourceSecretSnapshot $f 'artifacts/snapshot'} 'SecretScanOutputAlreadyExists'
    }
    Case 'tool arguments force source dir mode, complete redaction and explicit no baseline policy' {
        $args=Get-SourceSecretArguments 'source' 'config' 'empty-ignore' 'report'
        Check ($args[0] -ceq 'dir' -and $args -contains '--redact=100' -and $args -contains '--ignore-gitleaks-allow' -and $args -notcontains 'git' -and $args -notcontains '--baseline-path')
        Check ($args -contains '--config' -and $args -contains '--gitleaks-ignore-path' -and $args -contains '--report-path' -and $args -contains '--timeout')
    }
    Case 'empty report plus success records factual zero findings' {
        $safe=@(ConvertTo-SafeSecretFindings $run '[]');Check ($safe.Count -eq 0)
        Check ((Get-SourceSecretExitState 0 $safe) -ceq 'NoFindings')
    }
    Case 'findings remain findings and secret-bearing raw fields are never projected' {
        $f=New-Fixture;$snapshot=New-SourceSecretSnapshot $f 'artifacts/snapshot'
        $raw=@(@{RuleID='synthetic-rule';File='src/example.cs';StartLine=2;Secret='REDACTED';Match='DO-NOT-PROJECT';Fingerprint='DO-NOT-PROJECT'})|ConvertTo-Json -AsArray
        $safe=@(ConvertTo-SafeSecretFindings $snapshot.snapshot $raw)
        Check ($safe.Count -eq 1 -and $safe[0].redacted -and (Get-SourceSecretExitState 17 $safe) -ceq 'FindingsRequireReview')
        $text=$safe|ConvertTo-Json -Depth 5;Check (!$text.Contains('DO-NOT-PROJECT') -and @($safe[0].Keys).Count -eq 4)
    }
    Case 'unredacted tool result is labeled without echoing its value' {
        $f=New-Fixture;$snapshot=New-SourceSecretSnapshot $f 'artifacts/snapshot'
        $raw=@(@{RuleID='synthetic-rule';File='src/example.cs';StartLine=1;Secret='DO-NOT-PROJECT'})|ConvertTo-Json -AsArray
        $safe=@(ConvertTo-SafeSecretFindings $snapshot.snapshot $raw)
        Check (!$safe[0].redacted -and !(($safe|ConvertTo-Json).Contains('DO-NOT-PROJECT')))
    }
    Case 'tool failures and exit-report contradictions never become zero findings' {
        Reject {Get-SourceSecretExitState 2 @()} 'SecretScanToolOrReportMismatch'
        Reject {Get-SourceSecretExitState 17 @()} 'SecretScanToolOrReportMismatch'
        Reject {Get-SourceSecretExitState 0 @(@{ruleId='finding'})} 'SecretScanToolOrReportMismatch'
    }
    Case 'malformed and non-array reports fail closed' {
        Reject {ConvertTo-SafeSecretFindings $run '{invalid'} 'SecretScanReportInvalid'
        Reject {ConvertTo-SafeSecretFindings $run '{}'} 'SecretScanReportInvalid'
    }
    Case 'finding paths cannot refer outside the staged source' {
        $f=New-Fixture;$snapshot=New-SourceSecretSnapshot $f 'artifacts/snapshot'
        $raw=@(@{RuleID='synthetic-rule';File='../../outside.txt';StartLine=1;Secret='REDACTED'})|ConvertTo-Json -AsArray
        Reject {ConvertTo-SafeSecretFindings $snapshot.snapshot $raw} 'ReleasePathOutsideRepository'
    }
    $receipt=@{schema='platform-studio.source-secret-contract-tests.v1';observedUtc=[DateTimeOffset]::UtcNow.ToString('O');passed=$results.Count;failed=0;
        results=$results.ToArray();syntheticFixturesOnly=$true;realRuntimeCachesScanned=$false;cloudCalls=$false}
    Write-Source (Join-Path $run 'results.json') ($receipt|ConvertTo-Json -Depth 8)
    Write-Host "PASS: $($results.Count) source secret scan contracts; no real runtime caches, credentials or history. $run"
}finally{foreach($key in $previous.Keys){if($null -eq $previous[$key]){Remove-Item -LiteralPath ('Env:'+$key) -ErrorAction SilentlyContinue}else{[Environment]::SetEnvironmentVariable($key,$previous[$key])}}}
