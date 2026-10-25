#requires -Version 7.4
[CmdletBinding()]
param([string]$DotNetPath='C:\IT\Package\dotnet\dotnet.exe',[string]$SdkPath='C:\IT\Package\dotnet\sdk\10.0.300')
$ErrorActionPreference='Stop';$projectRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $projectRoot 'scripts/Test-CommercialRelease.ps1')
$repositoryRoot=$projectRoot;$fixtureRoot=Join-Path $projectRoot ('artifacts/foundation-release-execution-tests/'+[Guid]::NewGuid().ToString('N'));$null=New-Item -ItemType Directory -Path $fixtureRoot
$script:checks=0
function Check([bool]$Condition,[string]$Message){if(!$Condition){throw $Message};$script:checks++}
function Reject([scriptblock]$Action,[string]$Reason){$caught=$false;try{&$Action|Out-Null}catch{$caught=$true;if($_.Exception.Message -notlike $Reason){throw}};Check $caught ('Expected '+$Reason)}
$tokens=$null;$errors=$null;$fixtureAst=[Management.Automation.Language.Parser]::ParseFile((Join-Path $projectRoot 'tests/release/foundation-test-execution.tests.ps1'),[ref]$tokens,[ref]$errors)
if($errors){throw($errors|Out-String)}
foreach($function in $fixtureAst.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -in @('WriteFixture','NewExecutionFixture')},$true)){. ([scriptblock]::Create($function.Extent.Text))}
# Reuse only the owned constant stand-in source, not the previous test program's top-level execution.
$constant=@($fixtureAst.FindAll({param($node)$node -is [Management.Automation.Language.StringConstantExpressionAst] -and $node.Value.StartsWith('using System; using System.IO; using System.Linq; using System.Threading; using System.Xml.Linq;')},$true))
if($constant.Count -ne 1){throw 'Constant executable fixture source missing.'}
$tools=Join-Path $fixtureRoot 'tools';$null=New-Item -ItemType Directory -Path $tools;$source=Join-Path $tools 'Fixture.cs';$assembly=Join-Path $tools 'fixture.dll';$standIn=Join-Path $tools 'dotnet.exe';$dotnetRoot=Split-Path $DotNetPath -Parent
WriteFixture $source $constant[0].Value
$references=Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref/10.0.8/ref/net10.0'
$compileArguments=@('exec',(Join-Path $SdkPath 'Roslyn/bincore/csc.dll'),'/noconfig','/nostdlib+','/target:exe',('/out:'+$assembly),$source)+@(Get-ChildItem -LiteralPath $references -Filter '*.dll' -File|ForEach-Object {'/reference:'+$_.FullName})
$compiled=Invoke-FTEProcess $DotNetPath $compileArguments $tools 30000;Check ($compiled.exitCode -eq 0) 'Constant stand-in did not compile.'
WriteFixture (Join-Path $tools 'fixture.runtimeconfig.json') '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}'
[void][Reflection.Assembly]::LoadFrom((Join-Path $SdkPath 'Microsoft.NET.HostModel.dll'));$architecture=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
$template=Join-Path $dotnetRoot ('packs/Microsoft.NETCore.App.Host.win-'+$architecture+'/10.0.8/runtimes/win-'+$architecture+'/native/apphost.exe')
$search=[Microsoft.NET.HostModel.AppHost.HostWriter+DotNetSearchOptions]::new();$search.Location=[Microsoft.NET.HostModel.AppHost.HostWriter+DotNetSearchOptions+SearchLocation]::AppRelative;$search.AppRelativeDotNet=[IO.Path]::GetRelativePath($tools,$dotnetRoot)
[Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost($template,$standIn,'fixture.dll',$false,$assembly,$false,$false,$search)
function SealManifest([string]$Directory){& (Join-Path $projectRoot 'scripts/Update-Manifest.ps1') -RepositoryRoot $Directory|Out-Null}
function NewReleaseFixture([string]$Name){
    $directory=NewExecutionFixture $Name
    foreach($path in Get-FREProgressPaths){if($path -cne 'MANIFEST.sha256'){WriteFixture (Join-Path $directory $path) ('Synthetic initial progress '+$path+[char]10)}}
    foreach($path in @('src/example.cs','config/example.json','pipelines/example.yml','tests/example.test.mjs')){WriteFixture (Join-Path $directory $path) ('Synthetic fixed input '+$path+[char]10)}
    WriteFixture (Join-Path $directory 'self-service/targets/example.json') '{"enabled":false}'
    SealManifest $directory;$oldMode=$env:FOUNDATION_TEST_EXECUTION_FIXTURE
    try{$env:FOUNDATION_TEST_EXECUTION_FIXTURE='valid';$run=& (Join-Path $directory 'scripts/Test-Foundation.ps1') -DotNetPath $standIn}finally{$env:FOUNDATION_TEST_EXECUTION_FIXTURE=$oldMode}
    $receipt=Get-Content -LiteralPath $run.receipt -Raw|ConvertFrom-Json -AsHashtable
    [pscustomobject]@{Root=$directory;Run=$run;Trx=(Join-Path $run.resultsDirectory 'foundation.trx');TrxSha256=$receipt.trx.sha256}
}
function Bind($Fixture,[string]$ReviewPath='',[string]$ReviewSha256=''){
    Get-FoundationReleaseExecutionBinding -RepositoryRoot $Fixture.Root -ExecutionReceiptPath $Fixture.Run.receipt -ExecutionReceiptSha256 $Fixture.Run.receiptSha256 `
        -IndexedFoundationTrxPath $Fixture.Trx -IndexedFoundationTrxSha256 $Fixture.TrxSha256 -ProgressReviewPath $ReviewPath -ProgressReviewSha256 $ReviewSha256
}
function WriteReviewed($Fixture,$Review,[string]$Name='reviewed.json'){
    $path=Join-Path $Fixture.Root ('artifacts/'+$Name);$record=Write-FTEJson $path $Review;[pscustomobject]@{Path=$path;Sha256=$record.sha256}
}
$fixture=NewReleaseFixture 'current';$binding=Bind $fixture
Check ($binding.exactFinalSourceMatch -and $binding.foundationExecutionReceiptVerified -and $binding.testsPassed -eq 2 -and $binding.reviewedProgressChanges.Count -eq 0) 'Exact observed source was not bound.'
Check (!$binding.limits.independentAttestation -and !$binding.limits.portalExecutionBindingVerified -and !$binding.limits.completeDependencyClosureVerified -and !$binding.limits.noNetworkAttested -and !$binding.limits.productionReady) 'Binding invented broader acceptance.'
Assert-FoundationReleaseExecutionCurrent $fixture.Root $binding;Check $true 'Final recheck failed.'
Reject {Get-FoundationReleaseExecutionBinding $fixture.Root $fixture.Run.receipt ('0'*64) $fixture.Trx $fixture.TrxSha256} '*ArtifactPinMismatch*'
Reject {Get-FoundationReleaseExecutionBinding $fixture.Root $fixture.Run.receipt $fixture.Run.receiptSha256 $fixture.Trx ('0'*64)} '*IndexedTrxMismatch*'
$copy=Join-Path $fixture.Root 'artifacts/copied-foundation.trx';Copy-Item -LiteralPath $fixture.Trx -Destination $copy
Reject {Get-FoundationReleaseExecutionBinding $fixture.Root $fixture.Run.receipt $fixture.Run.receiptSha256 $copy $fixture.TrxSha256} '*IndexedTrxMismatch*'
Reject {Bind $fixture 'artifacts/absent.json' ''} '*ReviewPairRequired*'
Reject {Get-FREContext $fixture.Root $fixture.Run.receipt $fixture.Run.receiptSha256 0} '*AgeInvalid*'

# Legacy capture-only remains the same schema; strong mode is explicitly selected.
$index=New-ReleaseEvidenceIndex $fixture.Root @($fixture.Trx) 24;$null=Write-ReleaseJson $fixture.Root 'artifacts/index.json' $index
$legacy=Test-CommercialReleaseEvidence $fixture.Root 'artifacts/index.json' 24
Check ($index.captureDoesNotAttestTestedSource -and !$legacy.Contains('foundationExecution') -and $legacy.localQualificationPassed -and !$legacy.productionReady) 'Legacy capture-only contract changed.'
$qualified=Test-CommercialReleaseEvidence $fixture.Root 'artifacts/index.json' 24 $fixture.Run.receipt $fixture.Run.receiptSha256
Check ($qualified.foundationExecution.exactFinalSourceMatch -and !$qualified.productionReady -and !$qualified.publicationAuthorized) 'Commercial qualification lost the explicit binding.'
Reject {Test-CommercialReleaseEvidence $fixture.Root 'artifacts/index.json' 24 $fixture.Run.receipt ''} '*ExplicitPairsRequired*'

$status=Join-Path $fixture.Root 'docs/validation.md';[IO.File]::AppendAllText($status,'Synthetic completed progress'+[char]10);SealManifest $fixture.Root
Reject {Bind $fixture} '*ReviewedProgressRequired*'
Reject {Assert-FoundationReleaseExecutionCurrent $fixture.Root $binding} '*QualificationSourceChanged*'
$candidate=New-FoundationReleaseProgressReview $fixture.Root $fixture.Run.receipt $fixture.Run.receiptSha256
Check ($candidate.reviewState -ceq 'Pending' -and $candidate.changes.Count -eq 2) 'Draft did not require explicit review of status plus derived manifest.'
$pending=WriteReviewed $fixture $candidate 'pending.json';Reject {Bind $fixture $pending.Path $pending.Sha256} '*ProgressReviewInvalid*'
$candidate.reviewState='ReviewedForLocalEvidence';$review=WriteReviewed $fixture $candidate
$progress=Bind $fixture $review.Path $review.Sha256
Check (!$progress.exactFinalSourceMatch -and $progress.reviewedProgressChanges.Count -eq 2 -and !$progress.progressReview.independentReviewVerified) 'Reviewed progress changes were promoted to exact/independent source identity.'
Assert-FoundationReleaseExecutionCurrent $fixture.Root $progress;Check $true 'Reviewed current source did not recheck.'
$index=New-ReleaseEvidenceIndex $fixture.Root @($fixture.Trx) 24;$null=Write-ReleaseJson $fixture.Root 'artifacts/progress-index.json' $index
$qualified=Test-CommercialReleaseEvidence $fixture.Root 'artifacts/progress-index.json' 24 $fixture.Run.receipt $fixture.Run.receiptSha256 $review.Path $review.Sha256
Check ($qualified.foundationExecution.reviewedProgressChanges.Count -eq 2 -and !$qualified.foundationExecution.exactFinalSourceMatch) 'Commercial progress binding was omitted.'
& (Join-Path $projectRoot 'scripts/Test-CommercialRelease.ps1') -RepositoryRoot $fixture.Root -TestEvidenceIndex 'artifacts/progress-index.json' -ReceiptPath 'artifacts/cli-qualification.json' `
    -FoundationExecutionReceipt $fixture.Run.receipt -ExpectedFoundationExecutionReceiptSha256 $fixture.Run.receiptSha256 -ProgressSourceReview $review.Path -ExpectedProgressSourceReviewSha256 $review.Sha256
Check (Test-Path -LiteralPath (Join-Path $fixture.Root 'artifacts/cli-qualification.json')) 'CLI pairs did not write qualified local evidence.'

# Deterministically mutate only allowed progress between the outer index check and inner binding.
# This local function override exists only in the fixture; production exposes no callback seam.
$statusBeforeRace=[IO.File]::ReadAllBytes($status)
[IO.File]::AppendAllText($status,'Synthetic raced progress'+[char]10);SealManifest $fixture.Root
$raceCandidate=New-FoundationReleaseProgressReview $fixture.Root $fixture.Run.receipt $fixture.Run.receiptSha256;$raceCandidate.reviewState='ReviewedForLocalEvidence';$raceReview=WriteReviewed $fixture $raceCandidate 'race-review.json'
[IO.File]::WriteAllBytes($status,$statusBeforeRace);SealManifest $fixture.Root
$script:bindingRaceOriginal=(Get-Item Function:Get-FoundationReleaseExecutionBinding).ScriptBlock;$script:bindingRaceStatus=$status;$script:bindingRaceRoot=$fixture.Root
Set-Item Function:Get-FoundationReleaseExecutionBinding -Value {
    param([string]$RepositoryRoot,[string]$ExecutionReceiptPath,[string]$ExecutionReceiptSha256,[string]$IndexedFoundationTrxPath,[string]$IndexedFoundationTrxSha256,[string]$ProgressReviewPath='',[string]$ProgressReviewSha256='',[int]$MaximumEvidenceAgeHours=24)
    [IO.File]::AppendAllText($script:bindingRaceStatus,'Synthetic raced progress'+[char]10);SealManifest $script:bindingRaceRoot
    & $script:bindingRaceOriginal @PSBoundParameters
}
try{Reject {Test-CommercialReleaseEvidence $fixture.Root 'artifacts/progress-index.json' 24 $fixture.Run.receipt $fixture.Run.receiptSha256 $raceReview.Path $raceReview.Sha256} '*IndexSourceMismatch*'}
finally{Set-Item Function:Get-FoundationReleaseExecutionBinding -Value $script:bindingRaceOriginal;[IO.File]::WriteAllBytes($status,$statusBeforeRace);SealManifest $fixture.Root}

foreach($fault in @('record-raw','record-governed','record-attributes','omitted-change','duplicate-change','wrong-receipt','unsupported-claim','wrong-final-snapshot')){
    $bad=ConvertFrom-Json -InputObject ($candidate|ConvertTo-Json -Depth 24) -AsHashtable
    switch($fault){
        'record-raw'{$bad.changes[0].tested.rawSha256='0'*64}
        'record-governed'{$bad.changes[0].final.governedSha256='0'*64}
        'record-attributes'{$bad.changes[0].final.attributes='-text'}
        'omitted-change'{$bad.changes=@($bad.changes[0])}
        'duplicate-change'{$bad.changes=@($bad.changes[0],$bad.changes[0])}
        'wrong-receipt'{$bad.executionReceiptSha256='0'*64}
        'unsupported-claim'{$bad.limits.independentReviewVerified=$true}
        'wrong-final-snapshot'{$bad.finalSourceInventorySha256='0'*64}
    }
    $badReview=WriteReviewed $fixture $bad ($fault+'.json');Reject {Bind $fixture $badReview.Path $badReview.Sha256} '*'
}
Reject {Bind $fixture $review.Path ('0'*64)} '*ArtifactPinMismatch*'
foreach($path in @('src/example.cs','config/example.json','pipelines/example.yml','tests/example.test.mjs','.agents/skills/fixture/SKILL.md','vendor/notice.txt')){
    $full=Join-Path $fixture.Root $path;$original=[IO.File]::ReadAllBytes($full)
    try{[IO.File]::AppendAllText($full,'Synthetic non-progress mutation');SealManifest $fixture.Root;Reject {Bind $fixture $review.Path $review.Sha256} '*NonProgressInputChanged*'}
    finally{[IO.File]::WriteAllBytes($full,$original);SealManifest $fixture.Root}
}
$unlisted=Join-Path $fixture.Root 'new-source.txt';WriteFixture $unlisted 'Synthetic new source';SealManifest $fixture.Root
Reject {Bind $fixture $review.Path $review.Sha256} '*HeadOrFileSetChanged*'
Remove-Item -LiteralPath $unlisted;SealManifest $fixture.Root
$oldStatus=[IO.File]::ReadAllBytes($status);try{[IO.File]::AppendAllText($status,'Further unreviewed progress');SealManifest $fixture.Root;Reject {Bind $fixture $review.Path $review.Sha256} '*ProgressReviewInvalid*'}finally{[IO.File]::WriteAllBytes($status,$oldStatus);SealManifest $fixture.Root}
$manifest=Join-Path $fixture.Root 'MANIFEST.sha256';$manifestOriginal=[IO.File]::ReadAllBytes($manifest)
try{[IO.File]::AppendAllText($manifest,'Synthetic invalid manifest');Reject {Bind $fixture $review.Path $review.Sha256} '*CurrentManifestMismatch*'}finally{[IO.File]::WriteAllBytes($manifest,$manifestOriginal)}
$receiptPath=$fixture.Run.receipt;$receiptOriginal=[IO.File]::ReadAllText($receiptPath);$future=ConvertFrom-Json -InputObject $receiptOriginal -AsHashtable;$future.finishedUnixMilliseconds=[DateTimeOffset]::UtcNow.AddHours(1).ToUnixTimeMilliseconds()
try{[IO.File]::WriteAllText($receiptPath,($future|ConvertTo-Json -Depth 24));$futurePin=(Get-FileHash -LiteralPath $receiptPath).Hash.ToLowerInvariant();Reject {Get-FREContext $fixture.Root $receiptPath $futurePin 24} '*StaleOrFuture*'}finally{[IO.File]::WriteAllText($receiptPath,$receiptOriginal)}
$sourceBeforePath=Join-Path $fixture.Run.runDirectory 'source-before.json';$sourceAfterPath=Join-Path $fixture.Run.runDirectory 'source-after.json';$sourceOriginal=[IO.File]::ReadAllText($sourceBeforePath)
try{
    $malformed=ConvertFrom-Json -InputObject $sourceOriginal -AsHashtable;$malformed.files[0].bytes='1';$json=$malformed|ConvertTo-Json -Depth 24
    [IO.File]::WriteAllText($sourceBeforePath,$json);[IO.File]::WriteAllText($sourceAfterPath,$json);$snapshotHash=(Get-FileHash -LiteralPath $sourceBeforePath).Hash.ToLowerInvariant()
    $repinned=ConvertFrom-Json -InputObject $receiptOriginal -AsHashtable
    foreach($phase in @('sourceBefore','sourceAfter')){$repinned[$phase].sha256=$snapshotHash;$repinned[$phase].bytes=(Get-Item -LiteralPath $sourceBeforePath).Length}
    [IO.File]::WriteAllText($receiptPath,($repinned|ConvertTo-Json -Depth 24));$pin=(Get-FileHash -LiteralPath $receiptPath).Hash.ToLowerInvariant()
    Reject {Get-FREContext $fixture.Root $receiptPath $pin 24} '*SourceRecordInvalid*'
}finally{[IO.File]::WriteAllText($sourceBeforePath,$sourceOriginal);[IO.File]::WriteAllText($sourceAfterPath,$sourceOriginal);[IO.File]::WriteAllText($receiptPath,$receiptOriginal)}
$null=Get-FTEGit $fixture.Root @('add','--all');$null=Get-FTEGit $fixture.Root @('commit','--quiet','-m','Synthetic changed head')
Reject {Bind $fixture $review.Path $review.Sha256} '*HeadOrFileSetChanged*'
$report=[ordered]@{schema='platform-studio.foundation-release-execution-contracts.v1';checks=$script:checks;passed=$true;constantOwnedStandInExecuted=$true;actualFoundationSuiteExecuted=$false;legacyCaptureOnlyPreserved=$true;independentAttestation=$false;completeDependencyClosureVerified=$false;noNetworkAttested=$false;portalExecutionBindingVerified=$false;productionReady=$false;cloudOperationsPerformed=$false}
$null=Write-FTEJson (Join-Path $fixtureRoot 'summary.json') $report
Write-Host "Foundation release execution: $script:checks checks passed. $fixtureRoot"
