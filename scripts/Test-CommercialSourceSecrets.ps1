#requires -Version 7.4
[CmdletBinding()]
param([string]$RepositoryRoot='',[string]$GitleaksPath='',[string]$OutputDirectory='')
$ErrorActionPreference='Stop'
$sourceRootArgument=$RepositoryRoot
. (Join-Path $PSScriptRoot 'Test-CommercialRelease.ps1')

function Get-SourceSecretArguments([string]$Snapshot,[string]$Config,[string]$Ignore,[string]$Report){
    return @('dir',$Snapshot,'--config',$Config,'--gitleaks-ignore-path',$Ignore,'--ignore-gitleaks-allow',
        '--redact=100','--report-format','json','--report-path',$Report,'--exit-code','17','--timeout','120',
        '--max-archive-depth','0','--max-decode-depth','5','--max-target-megabytes','8','--no-banner','--no-color','--log-level','error')
}

function New-SourceSecretSnapshot([string]$Root,[string]$RunDirectory){
    $identity=Get-ReleaseSourceIdentity $Root
    $run=Get-ReleasePath -Root $Root -Path $RunDirectory -Artifact
    if(Test-Path -LiteralPath $run){throw 'SecretScanOutputAlreadyExists'}
    $snapshot=Join-Path $run 'source';[IO.Directory]::CreateDirectory($snapshot)|Out-Null
    $metadata=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach($line in @(& git -C $Root -c core.quotepath=false ls-files --cached --others --exclude-standard --eol)){
        if($line -notmatch '^(?<metadata>.*?)\t(?<path>.+)$' -or !$metadata.TryAdd($Matches.path,$Matches.metadata)){throw 'SecretScanGitInventoryInvalid'}
    }
    if($LASTEXITCODE -ne 0){throw 'SecretScanGitInventoryInvalid'}
    $manifest=[Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach($line in Get-Content -LiteralPath (Join-Path $Root 'MANIFEST.sha256')){
        if($line -cnotmatch '\A([0-9a-f]{64})  (.+)\z'){throw 'SecretScanManifestInvalid'}
        if($manifest.ContainsKey($Matches[2])){throw 'SecretScanManifestInvalid'}
        $manifest.Add($Matches[2],$Matches[1])
    }
    $copied=0;[long]$totalBytes=0;[long]$sourceBytes=0
    foreach($relative in $manifest.Keys){
        if(!$metadata.ContainsKey($relative) -or $relative -match '[\x00-\x1f]' -or $relative.Replace('\','/') -match '(?i)(^|/)(\.git|\.local|\.tools|node_modules|bin|obj|\.azure|\.aws)/' -or
            $relative.Replace('\','/') -match '(?i)(^|/)(auth\.json|hosts\.yml|accessTokens\.json|azureProfile\.json|msal_[^/]*|portal\.local\.json|local\.settings\.json)$' -or
            $relative -match '(?i)\.(pfx|p12|key|sqlite|sqlite3|db|trx|zip)$'){
            throw 'SecretScanUnsafeSourcePath'
        }
        $source=Get-ReleasePath -Root $Root -Path $relative -Existing
        if((Get-Item -LiteralPath $source).Length -gt 8388608){throw 'SecretScanFileSizeLimit'}
        $bytes=[IO.File]::ReadAllBytes($source)
        if($bytes.Length -gt 8388608){throw 'SecretScanFileSizeLimit'}
        $sourceBytes+=$bytes.Length;if($sourceBytes -gt 536870912){throw 'SecretScanSnapshotSizeLimit'}
        $eol=$metadata[$relative]
        if($eol -match '\bw/(crlf|mixed)\b' -and $eol -match '\beol=lf\b' -and $eol -notmatch '\battr/-text\b'){
            $bytes=[Text.Encoding]::Latin1.GetBytes([Text.Encoding]::Latin1.GetString($bytes).Replace("`r`n","`n"))
        }
        if([Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant() -cne $manifest[$relative]){throw 'SecretScanSourceChangedDuringSnapshot'}
        $totalBytes+=$bytes.Length
        $destination=Get-ReleasePath -Root $snapshot -Path $relative
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($destination))|Out-Null
        [IO.File]::WriteAllBytes($destination,$bytes);$copied++
    }
    if($copied -eq 0){throw 'SecretScanEmptySnapshot'}
    $index=[ordered]@{schema='platform-studio.source-secret-snapshot.v1';capturedUtc=[DateTimeOffset]::UtcNow.ToString('O');
        head=$identity.head;manifestSha256=$identity.manifestSha256;files=$copied;bytes=$totalBytes;sourceBytesBeforeCanonicalization=$sourceBytes;
        scope='Manifest-listed Git source only; LF-governed text canonicalized; binary bytes preserved.';
        excludes=@('Git history and .git data','Ignored runtime/token caches and user databases','Tools/downloads/build outputs');
        filesSha256=$manifest}
    [IO.File]::WriteAllText((Join-Path $run 'snapshot.json'),($index|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    return [ordered]@{root=$run;snapshot=$snapshot;index=$index}
}

function ConvertTo-SafeSecretFindings([string]$Snapshot,[string]$Json){
    if($Json.Length -gt 16777216){throw 'SecretScanReportSizeLimit'}
    try{$raw=$Json|ConvertFrom-Json -AsHashtable -Depth 32 -NoEnumerate}catch{throw 'SecretScanReportInvalid'}
    if($raw -isnot [array]){throw 'SecretScanReportInvalid'}
    $records=@($raw)
    $safe=[Collections.Generic.List[object]]::new()
    foreach($finding in $records){
        if($finding -isnot [hashtable] -or $finding.RuleID -cnotmatch '\A[A-Za-z0-9._-]{1,128}\z' -or $finding.File -isnot [string]){throw 'SecretScanReportInvalid'}
        $file=if([IO.Path]::IsPathFullyQualified($finding.File)){[IO.Path]::GetFullPath($finding.File)}else{[IO.Path]::GetFullPath($finding.File,$Snapshot)}
        $file=Get-ReleasePath -Root $Snapshot -Path $file -Existing
        $line=0;if(![int]::TryParse([string]$finding.StartLine,[ref]$line) -or $line -lt 1){throw 'SecretScanReportInvalid'}
        $redacted=$finding.Secret -is [string] -and $finding.Secret -cin @('REDACTED','[REDACTED]')
        $safe.Add([ordered]@{ruleId=$finding.RuleID;path=[IO.Path]::GetRelativePath($Snapshot,$file).Replace('\','/');line=$line;redacted=$redacted})
    }
    return $safe.ToArray()
}

function Get-SourceSecretExitState([int]$ExitCode,[object[]]$Findings){
    if($ExitCode -eq 0 -and $Findings.Count -eq 0){return 'NoFindings'}
    if($ExitCode -eq 17 -and $Findings.Count -gt 0){return 'FindingsRequireReview'}
    throw 'SecretScanToolOrReportMismatch'
}

if($MyInvocation.InvocationName -eq '.'){return}
$root=if($sourceRootArgument){[IO.Path]::GetFullPath($sourceRootArgument)}else{[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))}
if(!$GitleaksPath){$GitleaksPath=Join-Path $root '.tools/gitleaks-8.30.1/gitleaks.exe'}
$tool=Get-ReleasePath -Root $root -Path $GitleaksPath -Existing
if(!$OutputDirectory){$OutputDirectory=Join-Path $root ('artifacts/commercial-release/source-secrets/'+[guid]::NewGuid().ToString('N'))}
$version=@(& $tool version 2>$null)
if($LASTEXITCODE -ne 0 -or $version.Count -ne 1 -or $version[0].Trim() -cne '8.30.1'){throw 'SecretScanPinnedToolUnavailable'}
$toolHash=(Get-FileHash -LiteralPath $tool -Algorithm SHA256).Hash.ToLowerInvariant()
$staged=New-SourceSecretSnapshot $root $OutputDirectory
$config=Join-Path $staged.root 'gitleaks-defaults.toml';$ignore=Join-Path $staged.root 'empty.gitleaksignore';$rawReport=Join-Path $staged.root 'gitleaks-redacted-raw.json'
[IO.File]::WriteAllText($config,"[extend]`nuseDefault = true`n",[Text.UTF8Encoding]::new($false))
[IO.File]::WriteAllText($ignore,'',[Text.UTF8Encoding]::new($false))
$info=[Diagnostics.ProcessStartInfo]::new($tool);$info.UseShellExecute=$false;$info.CreateNoWindow=$true
$info.WorkingDirectory=$staged.root;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true
foreach($argument in (Get-SourceSecretArguments $staged.snapshot $config $ignore $rawReport)){$info.ArgumentList.Add($argument)}
$process=[Diagnostics.Process]::Start($info);$started=$process.StartTime.ToUniversalTime();$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
try{
    $finished=$false
    for($attempt=0;$attempt -lt 5;$attempt++){if($process.WaitForExit(30000)){$finished=$true;break}}
    if(!$finished){
        $process.Refresh()
        if(!$process.HasExited -and $process.StartTime.ToUniversalTime() -eq $started -and $process.MainModule.FileName -eq $tool){$process.Kill();$null=$process.WaitForExit(10000)}
        throw 'SecretScanToolTimedOut'
    }
    $null=$stdout.GetAwaiter().GetResult();$null=$stderr.GetAwaiter().GetResult()
    if(!(Test-Path -LiteralPath $rawReport -PathType Leaf)){throw 'SecretScanReportMissing'}
    $null=Get-ReleasePath -Root $root -Path $rawReport -Artifact -Existing
    if((Get-Item -LiteralPath $rawReport).Length -gt 16777216){throw 'SecretScanReportSizeLimit'}
    $safe=@(ConvertTo-SafeSecretFindings $staged.snapshot ([IO.File]::ReadAllText($rawReport)))
    $state=Get-SourceSecretExitState $process.ExitCode $safe
    $report=[ordered]@{schema='platform-studio.source-secrets.v1';observedUtc=[DateTimeOffset]::UtcNow.ToString('O');state=$state;
        findingCount=$safe.Count;findings=$safe;tool=@{name='Gitleaks';version='8.30.1';sha256=$toolHash;redactPercent=100;exitCode=$process.ExitCode;
            configuration='Built-in rules; explicit empty project ignore; inline allow comments ignored; no baseline.'};
        snapshot=@{head=$staged.index.head;manifestSha256=$staged.index.manifestSha256;files=$staged.index.files;bytes=$staged.index.bytes};
        redactionVerified=@($safe|Where-Object {!$_.redacted}).Count -eq 0;productionReady=$false;publicationAuthorized=$false;
        sourceOnly=$true;gitHistoryScanned=$false;runtimeCachesScanned=$false;cloudCalls=$false;
        limits=@('A source-only snapshot scan does not clear Git history, runtime caches, final binary archives or future source edits.','No result grants legal, license, ownership, production or publication clearance.','Findings require independent review; this script creates no ignore rules or baseline.')}
    $null=Write-ReleaseJson $root (Join-Path $staged.root 'findings.json') $report
    Write-Host "Gitleaks source snapshot: $state; findings=$($safe.Count); productionReady=false. $($staged.root)"
    foreach($finding in $safe){Write-Output ([pscustomobject]$finding)}
}finally{
    $process.Dispose()
    if(Test-Path -LiteralPath $rawReport){$null=Get-ReleasePath -Root $root -Path $rawReport -Artifact -Existing;Remove-Item -LiteralPath $rawReport}
}
