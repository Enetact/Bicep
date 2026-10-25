#requires -Version 7.4
[CmdletBinding()]
param(
    [string]$RepositoryRoot='',
    [string]$TestEvidenceIndex='',
    [string]$ReceiptPath='',
    [string]$CaptureTestEvidenceIndex='',
    [string[]]$TrxPath=@(),
    [ValidateRange(1,168)][int]$MaximumEvidenceAgeHours=24,
    [string]$FoundationExecutionReceipt='',
    [string]$ExpectedFoundationExecutionReceiptSha256='',
    [string]$ProgressSourceReview='',
    [string]$ExpectedProgressSourceReviewSha256=''
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'FoundationReleaseExecutionEvidence.ps1')

function Get-ReleasePath([string]$Root,[string]$Path,[switch]$Artifact,[switch]$Existing){
    if([string]::IsNullOrWhiteSpace($Path)){throw 'ReleasePathRequired'}
    $full=[IO.Path]::GetFullPath($Path,$Root)
    $comparison=if([OperatingSystem]::IsWindows()){[StringComparison]::OrdinalIgnoreCase}else{[StringComparison]::Ordinal}
    if(!$full.StartsWith($Root+[IO.Path]::DirectorySeparatorChar,$comparison)){throw 'ReleasePathOutsideRepository'}
    if($Artifact -and !$full.StartsWith((Join-Path $Root 'artifacts')+[IO.Path]::DirectorySeparatorChar,$comparison)){throw 'ReleaseEvidenceMustBeUnderArtifacts'}
    if($Existing -and !(Test-Path -LiteralPath $full -PathType Leaf)){throw 'ReleaseEvidenceMissing'}
    $current=$full
    while($current -and $current -ne $Root){
        if(Test-Path -LiteralPath $current){
            $item=Get-Item -LiteralPath $current -Force
            if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'LinkedReleasePathRejected'}
        }
        $current=[IO.Path]::GetDirectoryName($current)
    }
    return $full
}

function ConvertFrom-ReleaseJson([string]$Text){
    $doc=[Text.Json.JsonDocument]::Parse($Text,[Text.Json.JsonDocumentOptions]@{MaxDepth=16})
    function Assert-ReleaseUniqueJson([Text.Json.JsonElement]$Element){
        if($Element.ValueKind -eq [Text.Json.JsonValueKind]::Object){
            $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach($property in $Element.EnumerateObject()){
                if(!$names.Add($property.Name)){throw 'DuplicateReleaseJsonProperty'}
                Assert-ReleaseUniqueJson $property.Value
            }
        }elseif($Element.ValueKind -eq [Text.Json.JsonValueKind]::Array){foreach($item in $Element.EnumerateArray()){Assert-ReleaseUniqueJson $item}}
    }
    try{
        if($doc.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object){throw 'ReleaseJsonObjectRequired'}
        Assert-ReleaseUniqueJson $doc.RootElement
        return $Text|ConvertFrom-Json -AsHashtable -Depth 16
    }finally{$doc.Dispose()}
}

function Assert-ReleaseJsonFields([hashtable]$Object,[string[]]$Fields){
    if(@($Object.Keys|Where-Object {$_ -cnotin $Fields}).Count -or @($Fields|Where-Object {!$Object.ContainsKey($_)}).Count){throw 'ReleaseJsonContractMismatch'}
}

function Get-ReleaseTime([object]$Value){
    if($Value -is [DateTimeOffset]){return $Value}
    if($Value -is [DateTime]){
        if($Value.Kind -eq [DateTimeKind]::Unspecified){throw 'ReleaseTimestampRequired'}
        return [DateTimeOffset]$Value
    }
    if(!$Value -or $Value -notmatch '(Z|[+-][0-9]{2}:[0-9]{2})$'){throw 'ReleaseTimestampRequired'}
    try{return [DateTimeOffset]::Parse($Value,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind)}catch{throw 'ReleaseTimestampInvalid'}
}

function Get-ReleaseSourceFiles([string]$Root){
    $lines=@(& git -C $Root -c core.quotepath=false ls-files --cached --others --exclude-standard -z)
    if($LASTEXITCODE -ne 0){throw 'ReleaseGitSourceInventoryFailed'}
    $paths=@(($lines -join "`n").Split([char]0,[StringSplitOptions]::RemoveEmptyEntries))
    if($paths.Count -eq 0 -or $paths.Count -gt 30000){throw 'ReleaseSourceInventoryInvalid'}
    foreach($relative in $paths){
        if($relative -match '[\x00-\x1f]' -or [IO.Path]::IsPathRooted($relative)){throw 'ReleaseSourcePathInvalid'}
        $null=Get-ReleasePath -Root $Root -Path $relative -Existing
    }
    return $paths
}

function Get-ReleaseSourceIdentity([string]$Root){
    $rootInfo=Get-Item -LiteralPath $Root -Force
    if(!$rootInfo.PSIsContainer -or ($rootInfo.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0){throw 'ReleaseRepositoryRootInvalid'}
    $gitRoot=@(& git -C $Root rev-parse --show-toplevel)
    if($LASTEXITCODE -ne 0 -or $gitRoot.Count -ne 1 -or [IO.Path]::GetFullPath($gitRoot[0]) -ne $Root){throw 'ReleaseRepositoryRootInvalid'}
    $head=@(& git -C $Root rev-parse HEAD)
    if($LASTEXITCODE -ne 0 -or $head.Count -ne 1 -or $head[0] -cnotmatch '\A(?:[a-f0-9]{40}|[a-f0-9]{64})\z'){throw 'ReleaseSourceRevisionInvalid'}
    $paths=@(Get-ReleaseSourceFiles $Root)
    $manifest=Get-ReleasePath -Root $Root -Path 'MANIFEST.sha256' -Existing
    & (Join-Path $PSScriptRoot 'Update-Manifest.ps1') -RepositoryRoot $Root -Check
    return [ordered]@{head=$head[0];manifestSha256=(Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash.ToLowerInvariant();sourceFiles=$paths}
}

function Get-ReleaseTrxEvidence([string]$Root,[string]$Path,[string]$ExpectedSha256,[DateTimeOffset]$Now,[int]$AgeHours){
    $full=Get-ReleasePath -Root $Root -Path $Path -Artifact -Existing
    if($ExpectedSha256 -cnotmatch '\A[a-f0-9]{64}\z'){throw 'ReleaseEvidenceDigestInvalid'}
    $stream=[IO.FileStream]::new($full,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        if($stream.Length -eq 0 -or $stream.Length -gt 33554432){throw 'ReleaseTrxSizeInvalid'}
        $actual=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
        if($actual -cne $ExpectedSha256){throw 'ReleaseEvidenceDigestMismatch'}
        $stream.Position=0
        $settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null;$settings.MaxCharactersInDocument=33554432
        $reader=[Xml.XmlReader]::Create($stream,$settings)
        try{$document=[Xml.XmlDocument]::new();$document.XmlResolver=$null;$document.Load($reader)}finally{$reader.Dispose()}
        $namespace=[Xml.XmlNamespaceManager]::new($document.NameTable);$namespace.AddNamespace('t','http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
        if($document.DocumentElement.LocalName -cne 'TestRun' -or $document.DocumentElement.NamespaceURI -cne 'http://microsoft.com/schemas/VisualStudio/TeamTest/2010'){throw 'ReleaseTrxSchemaInvalid'}
        $results=@($document.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult',$namespace))
        $counter=$document.SelectSingleNode('/t:TestRun/t:ResultSummary/t:Counters',$namespace)
        $times=$document.SelectSingleNode('/t:TestRun/t:Times',$namespace)
        if(!$counter -or !$times -or $results.Count -eq 0){throw 'ReleaseTrxIncomplete'}
        $start=Get-ReleaseTime $times.GetAttribute('start');$finish=Get-ReleaseTime $times.GetAttribute('finish')
        if($finish -lt $start -or $finish -gt $Now.AddMinutes(5) -or $start -gt $Now.AddMinutes(5) -or $finish -lt $Now.AddHours(-$AgeHours)){throw 'ReleaseTrxStaleOrInvalidTime'}
        $executions=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
        $passed=0;$skipped=0
        foreach($result in $results){
            $execution=[guid]::Empty
            if(![guid]::TryParseExact($result.GetAttribute('executionId'),'D',[ref]$execution) -or $execution -eq [guid]::Empty -or !$executions.Add($execution.ToString('D'))){throw 'ReleaseTrxDuplicateOrMissingExecution'}
            switch -CaseSensitive ($result.GetAttribute('outcome')){
                'Passed'{$passed++}
                'NotExecuted'{$skipped++}
                {$_ -cin @('Failed','Error','Timeout','Aborted')}{throw 'ReleaseTrxHasFailures'}
                default{throw 'ReleaseTrxUnknownOutcome'}
            }
        }
        $total=0;$declaredPassed=0;$executed=0
        if(![int]::TryParse($counter.GetAttribute('total'),[ref]$total) -or $total -ne $results.Count -or
            ![int]::TryParse($counter.GetAttribute('passed'),[ref]$declaredPassed) -or $declaredPassed -ne $passed -or
            ![int]::TryParse($counter.GetAttribute('executed'),[ref]$executed) -or $executed -ne $passed -or $passed -eq 0){throw 'ReleaseTrxCountMismatch'}
        return [ordered]@{path=[IO.Path]::GetRelativePath($Root,$full).Replace('\','/');sha256=$actual;finishedUtc=$finish.ToString('O');passed=$passed;failed=0;skipped=$skipped;total=$results.Count}
    }finally{$stream.Dispose()}
}

function Get-ReleaseSourceScan([string]$Root,[string[]]$Files){
    $findings=[Collections.Generic.List[object]]::new();$excluded=[Collections.Generic.List[string]]::new();$scanned=0
    foreach($relative in $Files){
        $name=$relative.Replace('\','/')
        if($name -match '(^|/)(\.local|\.azure|\.aws|node_modules|bin|obj|\.vs)/' -or
            $name -match '(?i)(^|/)(auth\.json|hosts\.yml|accessTokens\.json|azureProfile\.json|service_principal_entries\.json|portal\.local\.json|local\.settings\.json|msal_[^/]*)$' -or
            $name -match '(?i)(^|/)\.env$' -or $name -match '(?i)\.(pfx|p12|key|sqlite|sqlite3|db|trx|zip)$'){
            $findings.Add([ordered]@{path=$name;rule='credential-or-runtime-artifact-path'});continue
        }
        # Synthetic test fixture contents are not an independent secret-scanner clearance.
        if($name -match '(?i)^tests/.*/fixtures/' -or $name -match '(?i)^tests/release/fixtures/' -or
            $name -match '(?i)^tests/.*(?:Tests\.cs|\.test\.mjs|\.tests\.ps1)$'){$excluded.Add($name);continue}
        if([IO.Path]::GetExtension($name) -cnotin @('.json','.yml','.yaml','.ps1','.cs','.mjs','.js','.ts','.sh','.md','.pem','.txt')){continue}
        $path=Get-ReleasePath -Root $Root -Path $relative -Existing
        $info=Get-Item -LiteralPath $path
        if($info.Length -gt 2097152){$findings.Add([ordered]@{path=$name;rule='source-content-scan-size-limit'});continue}
        $text=[IO.File]::ReadAllText($path);$scanned++
        $rules=[ordered]@{
            'private-key-material'='-----BEGIN (?:(?:RSA|EC|OPENSSH) )?PRIVATE KEY-----\r?\n[A-Za-z0-9+/=\r\n]{64,}'
            'github-token-shape'='\b(?:gh[pousr]_[A-Za-z0-9]{20,}|github_pat_[A-Za-z0-9_]{40,})\b'
            'access-token-shape'='\beyJ[A-Za-z0-9_-]{20,}\.[A-Za-z0-9_-]{40,}\.[A-Za-z0-9_-]{20,}\b'
            'storage-key-shape'='AccountKey=[A-Za-z0-9+/]{60,}={0,2}'
        }
        foreach($rule in $rules.Keys){if([regex]::IsMatch($text,$rules[$rule])){$findings.Add([ordered]@{path=$name;rule=$rule})}}
    }
    return [ordered]@{scannedTextFiles=$scanned;excludedSyntheticFixtures=$excluded.ToArray();findings=$findings.ToArray();
        rules=@('credential-or-runtime-artifact-path','source-content-scan-size-limit','private-key-material','github-token-shape','access-token-shape','storage-key-shape');
        limitations=@('Pattern classification only; not a complete secret scanner or historical Git scan.','Synthetic fixture and convention-named test source contents excluded; paths are still checked. Independent full-source/final-archive scanning remains required.','No dependency vulnerability, ownership, license or legal clearance follows from this scan.')}
}

function Write-ReleaseJson([string]$Root,[string]$Path,[object]$Value){
    $full=Get-ReleasePath -Root $Root -Path $Path -Artifact
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($full))|Out-Null
    $temporary=$full+'.'+[guid]::NewGuid().ToString('N')+'.tmp'
    try{
        [IO.File]::WriteAllText($temporary,($Value|ConvertTo-Json -Depth 16),[Text.UTF8Encoding]::new($false))
        [IO.File]::Move($temporary,$full,$true)
    }finally{if(Test-Path -LiteralPath $temporary){Remove-Item -LiteralPath $temporary}}
    return $full
}

function New-ReleaseEvidenceIndex([string]$Root,[string[]]$Paths,[int]$AgeHours){
    $source=Get-ReleaseSourceIdentity $Root;$now=[DateTimeOffset]::UtcNow
    if($Paths.Count -eq 0 -or $Paths.Count -gt 32){throw 'ReleaseTrxPathsRequired'}
    $tests=[Collections.Generic.List[object]]::new();$names=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($path in $Paths){
        $full=Get-ReleasePath -Root $Root -Path $path -Artifact -Existing
        $name=[IO.Path]::GetFileNameWithoutExtension($full)
        if($name -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z' -or !$names.Add($name)){throw 'ReleaseTestNameInvalid'}
        $hash=(Get-FileHash -LiteralPath $full -Algorithm SHA256).Hash.ToLowerInvariant()
        $entry=Get-ReleaseTrxEvidence $Root $full $hash $now $AgeHours
        $tests.Add([ordered]@{name=$name;path=$entry.path;sha256=$hash})
    }
    return [ordered]@{schema='platform-studio.test-evidence-index.v1';createdUtc=$now.ToString('O');sourceHead=$source.head;
        sourceManifestSha256=$source.manifestSha256;captureDoesNotAttestTestedSource=$true;tests=$tests.ToArray()}
}

function Test-CommercialReleaseEvidence([string]$Root,[string]$IndexPath,[int]$AgeHours,
    [string]$ExecutionReceiptPath='',[string]$ExecutionReceiptSha256='',[string]$ProgressReviewPath='',[string]$ProgressReviewSha256=''){
    if([bool]$ExecutionReceiptPath -ne [bool]$ExecutionReceiptSha256 -or [bool]$ProgressReviewPath -ne [bool]$ProgressReviewSha256 -or
        (!$ExecutionReceiptPath -and [bool]$ProgressReviewPath)){throw 'ReleaseExecutionExplicitPairsRequired'}
    $source=Get-ReleaseSourceIdentity $Root;$now=[DateTimeOffset]::UtcNow
    $indexFile=Get-ReleasePath -Root $Root -Path $IndexPath -Artifact -Existing
    # Bind the report to the same bounded index bytes parsed here, even if an owner edits the file later.
    $indexStream=[IO.FileStream]::new($indexFile,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        if($indexStream.Length -gt 1048576){throw 'ReleaseIndexSizeInvalid'}
        $indexSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($indexStream)).ToLowerInvariant();$indexStream.Position=0
        $indexReader=[IO.StreamReader]::new($indexStream,[Text.UTF8Encoding]::new($false,$true),$true,8192,$true)
        try{$index=ConvertFrom-ReleaseJson $indexReader.ReadToEnd()}finally{$indexReader.Dispose()}
    }finally{$indexStream.Dispose()}
    Assert-ReleaseJsonFields $index @('schema','createdUtc','sourceHead','sourceManifestSha256','captureDoesNotAttestTestedSource','tests')
    $created=Get-ReleaseTime $index.createdUtc
    if($index.schema -cne 'platform-studio.test-evidence-index.v1' -or $index.sourceHead -cne $source.head -or
        $index.sourceManifestSha256 -cne $source.manifestSha256 -or $index.captureDoesNotAttestTestedSource -isnot [bool] -or !$index.captureDoesNotAttestTestedSource -or
        $created -gt $now.AddMinutes(5) -or $created -lt $now.AddHours(-$AgeHours) -or $index.tests -isnot [array] -or $index.tests.Count -eq 0 -or $index.tests.Count -gt 32){throw 'ReleaseIndexSourceOrTimeMismatch'}
    $tests=[Collections.Generic.List[object]]::new();$names=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal);$paths=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($test in $index.tests){
        if($test -isnot [hashtable]){throw 'ReleaseTestIndexEntryInvalid'}
        Assert-ReleaseJsonFields $test @('name','path','sha256')
        if($test.name -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z' -or !$names.Add($test.name) -or !$paths.Add($test.path)){throw 'ReleaseTestIndexEntryInvalid'}
        $entry=Get-ReleaseTrxEvidence $Root $test.path $test.sha256 $now $AgeHours;$entry.name=$test.name;$tests.Add($entry)
    }
    $executionBinding=$null
    if($ExecutionReceiptPath){
        $foundation=@($tests|Where-Object {$_.name -ceq 'foundation'})
        if($foundation.Count -ne 1){throw 'ReleaseExecutionFoundationIndexRequired'}
        $executionBinding=Get-FoundationReleaseExecutionBinding -RepositoryRoot $Root -ExecutionReceiptPath $ExecutionReceiptPath -ExecutionReceiptSha256 $ExecutionReceiptSha256 `
            -IndexedFoundationTrxPath $foundation[0].path -IndexedFoundationTrxSha256 $foundation[0].sha256 -ProgressReviewPath $ProgressReviewPath -ProgressReviewSha256 $ProgressReviewSha256 -MaximumEvidenceAgeHours $AgeHours
        if($executionBinding.sourceHead -cne $source.head -or $executionBinding.sourceFileCount -ne $source.sourceFiles.Count -or $executionBinding.finalSourceManifestSha256 -cne $source.manifestSha256){throw 'ReleaseExecutionIndexSourceMismatch'}
    }
    $scan=Get-ReleaseSourceScan $Root $source.sourceFiles
    if($scan.findings.Count){throw 'ReleaseSourceArtifactRejected'}
    $targetRoot=Join-Path $Root 'self-service/targets'
    if(!(Test-Path -LiteralPath $targetRoot -PathType Container)){throw 'ReleaseTargetInventoryMissing'}
    $targets=@(Get-ChildItem -LiteralPath $targetRoot -File -Filter '*.json')
    if($targets.Count -eq 0){throw 'ReleaseTargetInventoryMissing'}
    foreach($target in $targets){
        $null=Get-ReleasePath -Root $Root -Path $target.FullName -Existing
        $settings=ConvertFrom-ReleaseJson ([IO.File]::ReadAllText($target.FullName))
        if(!$settings.ContainsKey('enabled') -or $settings.enabled -isnot [bool] -or $settings.enabled){throw 'ReleaseEnabledTargetRejected'}
    }
    $blockers=@(
        @{code='commercial-and-threat-review-pending';phase='F0';scope='Product ownership, threat acceptance, distribution and qualified legal review are not attested by this tool.'},
        @{code='live-identity-and-provider-acceptance-unqualified';phase='F1/F2';scope='Entra consent, downstream credentials and least-privilege provider acceptance need their own approved evidence.'},
        @{code='production-operations-acceptance-unqualified';phase='F2';scope='Supported production capacity/SLO, recovery, retention and support acceptance are not established by local fixtures.'},
        @{code='native-clean-machine-package-qualification-pending';phase='PS-05';scope='ARM64/x64 clean install, startup, update, repair and uninstall have not been attested here.'},
        @{code='binary-signing-and-final-archive-review-pending';phase='PS-05/PS-17';scope='Signing, final-archive secret scans, SBOM/notices and complete dependency/license review remain required.'},
        @{code='test-source-attestation-not-independent';phase='PS-02';scope='A captured index binds observed source/evidence bytes; it is not independent proof the reported run tested those bytes.'}
    )
    if($executionBinding){Assert-FoundationReleaseExecutionCurrent $Root $executionBinding}
    $qualification=[ordered]@{schema='platform-studio.commercial-local-qualification.v1';observedUtc=$now.ToString('O');localQualificationPassed=$true;
        productionReady=$false;publicationAuthorized=$false;externalExecutionAuthorized=$false;azureDeploymentAuthorized=$false;
        source=@{head=$source.head;manifestSha256=$source.manifestSha256;sourceFileCount=$source.sourceFiles.Count};
        evidenceIndexSha256=$indexSha256;tests=$tests.ToArray();
        sourceScan=$scan;targetFiles=$targets.Count;enabledTargets=0;productionBlockers=$blockers;
        actionsPerformed=@{cloudCalls=$false;signIns=$false;pipelineSubmissions=$false;packageBuilds=$false;publication=$false};
        limits=@('Protect the evidence index independently; a privileged source/evidence owner can rewrite both.','Passing local checks never enables production gates, publication or cloud execution.')}
    if($executionBinding){$qualification.foundationExecution=$executionBinding}
    return $qualification
}

if($MyInvocation.InvocationName -eq '.'){return}
$root=if($RepositoryRoot){[IO.Path]::GetFullPath($RepositoryRoot)}else{[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))}
if($CaptureTestEvidenceIndex){
    if($TestEvidenceIndex -or $ReceiptPath -or $FoundationExecutionReceipt -or $ExpectedFoundationExecutionReceiptSha256 -or $ProgressSourceReview -or $ExpectedProgressSourceReviewSha256){throw 'Choose capture mode or qualification mode, not both.'}
    $index=New-ReleaseEvidenceIndex $root $TrxPath $MaximumEvidenceAgeHours
    $path=Write-ReleaseJson $root $CaptureTestEvidenceIndex $index
    Write-Host "Captured observed test/source hashes: $path. Capture does not attest which source the tests executed."
}else{
    if(!$TestEvidenceIndex){throw 'TestEvidenceIndex is required. Capture hashes from actual existing TRX files first.'}
    if(!$ReceiptPath){$ReceiptPath=Join-Path $root 'artifacts/commercial-release/local-qualification.json'}
    $receipt=Test-CommercialReleaseEvidence $root $TestEvidenceIndex $MaximumEvidenceAgeHours $FoundationExecutionReceipt $ExpectedFoundationExecutionReceiptSha256 $ProgressSourceReview $ExpectedProgressSourceReviewSha256
    $path=Write-ReleaseJson $root $ReceiptPath $receipt
    Write-Host "PASS: local release evidence checked; productionReady=false; no external execution or publication authority. $path"
}
