#requires -Version 7.4
. (Join-Path $PSScriptRoot 'FoundationTestExecutionEvidence.ps1')

function Get-FREProgressPaths { @('MANIFEST.sha256','docs/completion-status.md','docs/plans/platform-studio-commercial-release.md','docs/validation-results.json','docs/validation.md') }
function Get-FRELimits { [ordered]@{independentReviewVerified=$false;independentAttestation=$false;completeDependencyClosureVerified=$false;environmentAttested=$false;noNetworkAttested=$false;portalExecutionBindingVerified=$false;productionReady=$false;publicationAuthorized=$false} }
function Assert-FRELimits($Limits) {
    $expected=Get-FRELimits;Assert-FTEFields $Limits @($expected.Keys)
    foreach($name in $expected.Keys){if($Limits[$name] -isnot [bool] -or $Limits[$name]){throw 'ReleaseExecutionUnsupportedClaim'}}
}
function Assert-FRERelative([string]$Path) {
    if(!$Path -or $Path.Length -gt 1024 -or $Path.StartsWith('/') -or $Path.Contains('\') -or $Path.Contains(':') -or $Path -match '[\x00-\x1f\x7f]'){throw 'ReleaseExecutionRelativePathInvalid'}
    foreach($segment in $Path.Split('/')){if(!$segment -or $segment.Length -gt 255 -or $segment -in @('.','..') -or $segment.EndsWith('.') -or $segment.EndsWith(' ') -or $segment -match '(?i)\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)'){throw 'ReleaseExecutionRelativePathInvalid'}}
}
function Get-FREArtifactPath([string]$Root,[string]$Path) {
    if(!$Path){throw 'ReleaseExecutionArtifactPathRequired'}
    $full=Assert-FTEUnlinkedPath ([IO.Path]::GetFullPath($Path,$Root))
    if(!$full.StartsWith((Join-Path $Root 'artifacts')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'ReleaseExecutionArtifactOutsideRoot'}
    $full
}
function Read-FREJson([string]$Path,[string]$Sha256,[int]$Maximum=16777216) {
    $null=Assert-FTEUnlinkedPath $Path
    $guard=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{if($guard.Length -lt 1 -or $guard.Length -gt $Maximum){throw 'ReleaseExecutionJsonByteLimit'};Read-FTEJsonArtifact $Path $Sha256}finally{$guard.Dispose()}
}
function ConvertTo-FRERecord($Record) {
    Assert-FTEFields $Record @('path','bytes','rawSha256','governedBytes','governedSha256','attributes','crlfNormalized')
    if($Record.path -isnot [string] -or $Record.rawSha256 -isnot [string] -or $Record.governedSha256 -isnot [string] -or
        $Record.rawSha256 -cnotmatch '\A[a-f0-9]{64}\z' -or $Record.governedSha256 -cnotmatch '\A[a-f0-9]{64}\z' -or
        $Record.attributes -isnot [string] -or $Record.attributes.Length -gt 4096 -or $Record.attributes -match '[\x00-\x1f\x7f]' -or $Record.crlfNormalized -isnot [bool]){throw 'ReleaseExecutionSourceRecordInvalid'}
    Assert-FRERelative $Record.path
    foreach($number in @($Record.bytes,$Record.governedBytes)){if(($number -isnot [long] -and $number -isnot [int]) -or $number -lt 0 -or $number -gt 16777216){throw 'ReleaseExecutionSourceRecordInvalid'}}
    if($Record.governedBytes -gt $Record.bytes -or (!$Record.crlfNormalized -and ($Record.bytes -ne $Record.governedBytes -or $Record.rawSha256 -cne $Record.governedSha256))){throw 'ReleaseExecutionSourceRecordInvalid'}
    [ordered]@{path=$Record.path;bytes=$Record.bytes;rawSha256=$Record.rawSha256;governedBytes=$Record.governedBytes;governedSha256=$Record.governedSha256;attributes=$Record.attributes;crlfNormalized=$Record.crlfNormalized}
}
function ConvertTo-FRESnapshot($Snapshot) {
    Assert-FTEFields $Snapshot @('schema','head','excludedPaths','fileCount','rawBytes','files')
    if($Snapshot.schema -isnot [string] -or $Snapshot.schema -cne 'platform-studio.foundation-test-source.v1' -or $Snapshot.head -isnot [string] -or $Snapshot.head -cnotmatch '\A[a-f0-9]{40}(?:[a-f0-9]{24})?\z' -or
        $Snapshot.excludedPaths -isnot [Collections.IList] -or $Snapshot.excludedPaths.Count -ne 0 -or $Snapshot.files -isnot [Collections.IList] -or
        ($Snapshot.fileCount -isnot [long] -and $Snapshot.fileCount -isnot [int]) -or $Snapshot.fileCount -lt 1 -or $Snapshot.fileCount -gt 30000 -or $Snapshot.fileCount -ne $Snapshot.files.Count -or
        ($Snapshot.rawBytes -isnot [long] -and $Snapshot.rawBytes -isnot [int]) -or $Snapshot.rawBytes -lt 0 -or $Snapshot.rawBytes -gt 134217728){throw 'ReleaseExecutionSourceSnapshotInvalid'}
    $records=[Collections.Generic.List[object]]::new();$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);$previous=$null;$bytes=[long]0;$hasManifest=$false
    foreach($inputRecord in $Snapshot.files){
        $record=ConvertTo-FRERecord $inputRecord
        if(!$seen.Add($record.path) -or ($null -ne $previous -and [StringComparer]::Ordinal.Compare($previous,$record.path) -ge 0)){throw 'ReleaseExecutionSourceSnapshotInvalid'}
        $previous=$record.path;$bytes+=$record.bytes;if($record.path -ceq 'MANIFEST.sha256'){$hasManifest=$true};$records.Add($record)
    }
    if(!$hasManifest -or $bytes -ne $Snapshot.rawBytes){throw 'ReleaseExecutionSourceSnapshotInvalid'}
    [ordered]@{schema=$Snapshot.schema;head=$Snapshot.head;excludedPaths=@();fileCount=$Snapshot.fileCount;rawBytes=$bytes;files=$records.ToArray()}
}
function Get-FREJsonHash($Value) {Get-FTEHash ([Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Value -Depth 24)))}
function Assert-FREManifest($Snapshot) {
    $manifest=@($Snapshot.files|Where-Object {$_.path -ceq 'MANIFEST.sha256'})[0]
    $lines=@($Snapshot.files|Where-Object {$_.path -cne 'MANIFEST.sha256'}|ForEach-Object {$_.governedSha256+'  '+$_.path})
    $expected=Get-FTEHash ([Text.UTF8Encoding]::new($false).GetBytes(($lines -join [char]10)+[char]10))
    if($manifest.governedSha256 -cne $expected){throw 'ReleaseExecutionCurrentManifestMismatch'}
}
function Get-FRECurrentSnapshot([string]$Root) {
    $snapshot=ConvertTo-FRESnapshot (Get-FTESourceSnapshot $Root);Assert-FREManifest $snapshot;$snapshot
}
function Get-FREContext([string]$Root,[string]$ReceiptPath,[string]$ReceiptSha256,[int]$AgeHours=24) {
    if($AgeHours -lt 1 -or $AgeHours -gt 168){throw 'ReleaseExecutionAgeInvalid'}
    $rootPath=Assert-FTEUnlinkedPath ([IO.Path]::GetFullPath($Root));$file=Get-FREArtifactPath $rootPath $ReceiptPath
    if([IO.Path]::GetFileName($file) -cne 'completed.json'){throw 'ReleaseExecutionCompletedPathRequired'}
    $run=Split-Path $file -Parent;$verified=Test-FTEExecutionReceipt $run $ReceiptSha256
    $receipt=Read-FREJson $file $ReceiptSha256
    $started=Read-FREJson (Join-Path $run 'started.json') $receipt.started.sha256
    if(![string]::Equals($rootPath,$started.repositoryRoot,[StringComparison]::OrdinalIgnoreCase)){throw 'ReleaseExecutionRepositoryMismatch'}
    $now=[DateTimeOffset]::UtcNow
    foreach($finished in @($receipt.finishedUnixMilliseconds,$receipt.commands[1].finishedUnixMilliseconds)){
        $time=[DateTimeOffset]::FromUnixTimeMilliseconds($finished)
        if($time -gt $now.AddMinutes(5) -or $time -lt $now.AddHours(-$AgeHours)){throw 'ReleaseExecutionStaleOrFuture'}
    }
    foreach($phase in @('sourceBefore','sourceAfter')){
        $descriptor=$receipt[$phase];Assert-FTEFields $descriptor @('path','bytes','sha256')
        $name=if($phase -ceq 'sourceBefore'){'source-before.json'}else{'source-after.json'}
        if($descriptor.path -cne $name -or ($descriptor.bytes -isnot [long] -and $descriptor.bytes -isnot [int]) -or $descriptor.bytes -ne (Get-Item -LiteralPath (Join-Path $run $name)).Length){throw 'ReleaseExecutionSnapshotDescriptorInvalid'}
    }
    $before=ConvertTo-FRESnapshot (Read-FREJson (Join-Path $run 'source-before.json') $receipt.sourceBefore.sha256)
    $after=ConvertTo-FRESnapshot (Read-FREJson (Join-Path $run 'source-after.json') $receipt.sourceAfter.sha256)
    Assert-FREManifest $before;Assert-FREManifest $after
    if((Get-FREJsonHash $before) -cne (Get-FREJsonHash $after)){throw 'ReleaseExecutionSnapshotsDiffer'}
    [pscustomobject]@{root=$rootPath;file=$file;run=$run;receiptSha256=$ReceiptSha256;receipt=$receipt;snapshot=$after;verified=$verified}
}
function Get-FREChanges($Tested,$Final) {
    if($Tested.head -cne $Final.head -or $Tested.fileCount -ne $Final.fileCount){throw 'ReleaseExecutionHeadOrFileSetChanged'}
    $changes=[Collections.Generic.List[object]]::new();$allowed=Get-FREProgressPaths
    for($index=0;$index -lt $Tested.files.Count;$index++){
        $old=$Tested.files[$index];$current=$Final.files[$index]
        if($old.path -cne $current.path){throw 'ReleaseExecutionHeadOrFileSetChanged'}
        if((Get-FREJsonHash $old) -ceq (Get-FREJsonHash $current)){continue}
        if($old.path -cnotin $allowed){throw 'ReleaseExecutionNonProgressInputChanged'}
        if($old.attributes -cne $current.attributes){throw 'ReleaseExecutionProgressAttributesChanged'}
        $changes.Add([ordered]@{path=$old.path;classification=$(if($old.path -ceq 'MANIFEST.sha256'){'DerivedSourceManifest'}else{'ProgressStatusDocumentation'});tested=$old;final=$current})
    }
    $changes.ToArray()
}
function New-FoundationReleaseProgressReview {
    param([string]$RepositoryRoot,[string]$ExecutionReceiptPath,[string]$ExecutionReceiptSha256,[int]$MaximumEvidenceAgeHours=24)
    $context=Get-FREContext $RepositoryRoot $ExecutionReceiptPath $ExecutionReceiptSha256 $MaximumEvidenceAgeHours
    $current=Get-FRECurrentSnapshot $context.root;$changes=@(Get-FREChanges $context.snapshot $current)
    [ordered]@{schema='platform-studio.foundation-release-progress-review.v1';reviewState='Pending';executionReceiptSha256=$ExecutionReceiptSha256;testedSourceInventorySha256=$context.receipt.sourceAfter.sha256;finalSourceInventorySha256=(Get-FREJsonHash $current);sourceHead=$current.head;changes=$changes;limits=(Get-FRELimits)}
}
function Get-FoundationReleaseExecutionBinding {
    param([string]$RepositoryRoot,[string]$ExecutionReceiptPath,[string]$ExecutionReceiptSha256,[string]$IndexedFoundationTrxPath,[string]$IndexedFoundationTrxSha256,
        [string]$ProgressReviewPath='',[string]$ProgressReviewSha256='',[int]$MaximumEvidenceAgeHours=24)
    if([bool]$ProgressReviewPath -ne [bool]$ProgressReviewSha256){throw 'ReleaseExecutionReviewPairRequired'}
    $context=Get-FREContext $RepositoryRoot $ExecutionReceiptPath $ExecutionReceiptSha256 $MaximumEvidenceAgeHours
    $trx=Get-FREArtifactPath $context.root $IndexedFoundationTrxPath;$expectedTrx=Join-Path $context.run 'results/foundation.trx'
    if(![string]::Equals($trx,$expectedTrx,[StringComparison]::OrdinalIgnoreCase) -or $IndexedFoundationTrxSha256 -cne $context.receipt.trx.sha256){throw 'ReleaseExecutionIndexedTrxMismatch'}
    $current=Get-FRECurrentSnapshot $context.root;$currentSha256=Get-FREJsonHash $current;$changes=@(Get-FREChanges $context.snapshot $current)
    $reviewBinding=$null
    if($changes.Count -and !$ProgressReviewPath){throw 'ReleaseExecutionReviewedProgressRequired'}
    if($ProgressReviewPath){
        $reviewPath=Get-FREArtifactPath $context.root $ProgressReviewPath;$review=Read-FREJson $reviewPath $ProgressReviewSha256 65536
        Assert-FTEFields $review @('schema','reviewState','executionReceiptSha256','testedSourceInventorySha256','finalSourceInventorySha256','sourceHead','changes','limits');Assert-FRELimits $review.limits
        foreach($field in @('schema','reviewState','executionReceiptSha256','testedSourceInventorySha256','finalSourceInventorySha256','sourceHead')){if($review[$field] -isnot [string]){throw 'ReleaseExecutionProgressReviewInvalid'}}
        if($review.schema -cne 'platform-studio.foundation-release-progress-review.v1' -or $review.reviewState -cne 'ReviewedForLocalEvidence' -or $review.executionReceiptSha256 -cne $ExecutionReceiptSha256 -or
            $review.testedSourceInventorySha256 -cne $context.receipt.sourceAfter.sha256 -or $review.finalSourceInventorySha256 -cne $currentSha256 -or $review.sourceHead -cne $current.head -or
            $review.changes -isnot [Collections.IList] -or $review.changes.Count -ne $changes.Count -or $review.changes.Count -gt 5){throw 'ReleaseExecutionProgressReviewInvalid'}
        for($index=0;$index -lt $changes.Count;$index++){
            $entry=$review.changes[$index];Assert-FTEFields $entry @('path','classification','tested','final')
            $tested=ConvertTo-FRERecord $entry.tested;$final=ConvertTo-FRERecord $entry.final
            $canonical=[ordered]@{path=$entry.path;classification=$entry.classification;tested=$tested;final=$final}
            if((Get-FREJsonHash $canonical) -cne (Get-FREJsonHash $changes[$index])){throw 'ReleaseExecutionProgressReviewInvalid'}
        }
        $reviewBinding=[ordered]@{path=[IO.Path]::GetRelativePath($context.root,$reviewPath).Replace('\','/');sha256=$ProgressReviewSha256;reviewState=$review.reviewState;independentReviewVerified=$false}
    }
    [ordered]@{schema='platform-studio.foundation-release-execution-binding.v1';state='Verified';foundationExecutionReceiptVerified=$true;
        executionReceipt=[ordered]@{path=[IO.Path]::GetRelativePath($context.root,$context.file).Replace('\','/');sha256=$ExecutionReceiptSha256};
        indexedTrx=[ordered]@{path=[IO.Path]::GetRelativePath($context.root,$trx).Replace('\','/');sha256=$IndexedFoundationTrxSha256};testsPassed=$context.verified.testsPassed;
        testedSourceInventorySha256=$context.receipt.sourceAfter.sha256;finalSourceInventorySha256=$currentSha256;finalSourceManifestSha256=@($current.files|Where-Object {$_.path -ceq 'MANIFEST.sha256'})[0].rawSha256;sourceHead=$current.head;sourceFileCount=$current.fileCount;
        exactFinalSourceMatch=($changes.Count -eq 0);reviewedProgressChanges=$changes;progressReview=$reviewBinding;limits=(Get-FRELimits)}
}
function Assert-FoundationReleaseExecutionCurrent([string]$RepositoryRoot,$Binding) {
    if($Binding.finalSourceInventorySha256 -isnot [string] -or $Binding.finalSourceInventorySha256 -cnotmatch '\A[a-f0-9]{64}\z'){throw 'ReleaseExecutionFinalSnapshotRequired'}
    $current=Get-FRECurrentSnapshot (Assert-FTEUnlinkedPath ([IO.Path]::GetFullPath($RepositoryRoot)))
    if((Get-FREJsonHash $current) -cne $Binding.finalSourceInventorySha256){throw 'ReleaseExecutionQualificationSourceChanged'}
}
