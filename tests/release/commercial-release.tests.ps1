#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $root 'scripts/Test-CommercialRelease.ps1')
$run=Join-Path $root ('artifacts/commercial-release-contract-tests/'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($run)|Out-Null
$results=[Collections.Generic.List[object]]::new()
$previous=@{}
foreach($name in @('GIT_CONFIG_NOSYSTEM','GIT_CONFIG_GLOBAL','GIT_CONFIG_COUNT','GIT_DIR','GIT_WORK_TREE','GIT_INDEX_FILE','GIT_COMMON_DIR','GIT_OBJECT_DIRECTORY','GIT_ALTERNATE_OBJECT_DIRECTORIES')){
    $previous[$name]=[Environment]::GetEnvironmentVariable($name)
    Remove-Item -LiteralPath ('Env:'+$name) -ErrorAction SilentlyContinue
}
$env:GIT_CONFIG_NOSYSTEM='1';$env:GIT_CONFIG_GLOBAL=Join-Path $run 'nonexistent-global-gitconfig'

function Write-Fixture([string]$Path,[string]$Text){
    [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($Path))|Out-Null
    [IO.File]::WriteAllText($Path,$Text,[Text.UTF8Encoding]::new($false))
}
function Write-Trx([string]$Path,[string[]]$Outcomes=@('Passed','NotExecuted'),[DateTimeOffset]$Finish=[DateTimeOffset]::UtcNow,[switch]$Duplicate,[int]$TotalOverride=-1){
    $passed=@($Outcomes|Where-Object {$_ -ceq 'Passed'}).Count
    $executed=@($Outcomes|Where-Object {$_ -cne 'NotExecuted'}).Count
    $first=[guid]::NewGuid().ToString('D');$records=[Collections.Generic.List[string]]::new()
    foreach($outcome in $Outcomes){$id=if($Duplicate){$first}else{[guid]::NewGuid().ToString('D')};$records.Add("<UnitTestResult executionId=`"$id`" testId=`"$([guid]::NewGuid())`" testName=`"Synthetic fixture`" outcome=`"$outcome`" />")}
    $total=if($TotalOverride -ge 0){$TotalOverride}else{$Outcomes.Count}
    $text=@"
<TestRun id="$([guid]::NewGuid())" xmlns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010">
  <Times creation="$($Finish.AddSeconds(-2).ToString('O'))" start="$($Finish.AddSeconds(-1).ToString('O'))" finish="$($Finish.ToString('O'))" />
  <Results>$($records -join '')</Results>
  <ResultSummary outcome="Completed"><Counters total="$total" executed="$executed" passed="$passed" failed="0" notExecuted="0" /></ResultSummary>
</TestRun>
"@
    Write-Fixture $Path $text
}
function New-Fixture{
    $fixture=Join-Path $run ([guid]::NewGuid().ToString('N'))
    [IO.Directory]::CreateDirectory($fixture)|Out-Null
    $template=Join-Path $fixture 'empty-template';[IO.Directory]::CreateDirectory($template)|Out-Null
    & git -c "init.templateDir=$template" -C $fixture init --quiet
    if($LASTEXITCODE -ne 0){throw 'Fixture Git init failed'}
    Write-Fixture (Join-Path $fixture '.gitignore') "artifacts/`nempty-template/`n"
    Write-Fixture (Join-Path $fixture '.gitattributes') "* text=auto eol=lf`n"
    Write-Fixture (Join-Path $fixture 'src/example.cs') 'public sealed record SyntheticReleaseExample(string Name);'
    Write-Fixture (Join-Path $fixture 'self-service/targets/example.json') '{"enabled":false,"synthetic":true}'
    & git -C $fixture add -- .gitignore .gitattributes src self-service
    if($LASTEXITCODE -ne 0){throw 'Fixture Git add failed'}
    & git -C $fixture -c user.name='Commercial Release Fixture' -c user.email='fixture@example.invalid' -c commit.gpgsign=false -c core.hooksPath=nonexistent-hooks commit --quiet -m 'Synthetic local release fixture'
    if($LASTEXITCODE -ne 0){throw 'Fixture Git commit failed'}
    & (Join-Path $root 'scripts/Update-Manifest.ps1') -RepositoryRoot $fixture
    Write-Trx (Join-Path $fixture 'artifacts/tests/synthetic.trx')
    return $fixture
}
function Seal-Fixture([string]$Fixture){
    & (Join-Path $root 'scripts/Update-Manifest.ps1') -RepositoryRoot $Fixture
    $index=New-ReleaseEvidenceIndex $Fixture @('artifacts/tests/synthetic.trx') 24
    Write-ReleaseJson $Fixture 'artifacts/tests/index.json' $index|Out-Null
}
function Check([bool]$Value){if(!$Value){throw 'Release assertion failed'}}
function Reject([scriptblock]$Action,[string]$Code){
    $rejected=$false
    try{& $Action|Out-Null}catch{if(!$_.Exception.Message.Contains($Code)){throw};$rejected=$true}
    Check $rejected
}
function Case([string]$Name,[scriptblock]$Action){
    try{& $Action;$results.Add([ordered]@{name=$Name;passed=$true})}catch{throw "Release case '$Name': $($_.Exception.Message)"}
}

try{
    Case 'actual TRX outcomes count skipped cases despite zero notExecuted summary counter' {
        $f=New-Fixture;Seal-Fixture $f
        $receipt=Test-CommercialReleaseEvidence $f 'artifacts/tests/index.json' 24
        Check ($receipt.tests[0].passed -eq 1 -and $receipt.tests[0].skipped -eq 1 -and $receipt.tests[0].total -eq 2)
        Check ($receipt.localQualificationPassed -and !$receipt.productionReady -and !$receipt.publicationAuthorized -and !$receipt.externalExecutionAuthorized -and !$receipt.azureDeploymentAuthorized)
        Check ($receipt.productionBlockers.Count -ge 5 -and $receipt.enabledTargets -eq 0 -and $receipt.sourceScan.findings.Count -eq 0)
        $path=Write-ReleaseJson $f 'artifacts/tests/qualification.json' $receipt
        Check ((ConvertFrom-ReleaseJson ([IO.File]::ReadAllText($path))).productionReady -eq $false)
    }
    Case 'capture preserves explicit source execution attestation limit' {
        $f=New-Fixture;$index=New-ReleaseEvidenceIndex $f @('artifacts/tests/synthetic.trx') 24
        Check ($index.captureDoesNotAttestTestedSource -and $index.tests.Count -eq 1 -and $index.sourceManifestSha256 -cmatch '\A[a-f0-9]{64}\z')
    }
    Case 'changed TRX bytes fail against frozen evidence digest' {
        $f=New-Fixture;Seal-Fixture $f
        [IO.File]::AppendAllText((Join-Path $f 'artifacts/tests/synthetic.trx'),'<!-- changed -->')
        Reject {Test-CommercialReleaseEvidence $f 'artifacts/tests/index.json' 24} 'ReleaseEvidenceDigestMismatch'
    }
    Case 'missing test receipt fails closed' {$f=New-Fixture;Reject {New-ReleaseEvidenceIndex $f @('artifacts/tests/missing.trx') 24} 'ReleaseEvidenceMissing'}
    Case 'stale test finish cannot be refreshed by newly capturing index' {
        $f=New-Fixture;Write-Trx (Join-Path $f 'artifacts/tests/synthetic.trx') -Finish ([DateTimeOffset]::UtcNow.AddDays(-3))
        Reject {New-ReleaseEvidenceIndex $f @('artifacts/tests/synthetic.trx') 24} 'ReleaseTrxStaleOrInvalidTime'
    }
    Case 'future test finish fails closed' {
        $f=New-Fixture;Write-Trx (Join-Path $f 'artifacts/tests/synthetic.trx') -Finish ([DateTimeOffset]::UtcNow.AddHours(1))
        Reject {New-ReleaseEvidenceIndex $f @('artifacts/tests/synthetic.trx') 24} 'ReleaseTrxStaleOrInvalidTime'
    }
    Case 'unknown test outcome cannot be counted as passed' {
        $f=New-Fixture;Write-Trx (Join-Path $f 'artifacts/tests/synthetic.trx') @('Passed','InventedSuccess')
        Reject {New-ReleaseEvidenceIndex $f @('artifacts/tests/synthetic.trx') 24} 'ReleaseTrxUnknownOutcome'
    }
    Case 'failed test outcome cannot be overridden by summary counter' {
        $f=New-Fixture;Write-Trx (Join-Path $f 'artifacts/tests/synthetic.trx') @('Passed','Failed')
        Reject {New-ReleaseEvidenceIndex $f @('artifacts/tests/synthetic.trx') 24} 'ReleaseTrxHasFailures'
    }
    Case 'entirely skipped profile is not qualifying evidence' {
        $f=New-Fixture;Write-Trx (Join-Path $f 'artifacts/tests/synthetic.trx') @('NotExecuted')
        Reject {New-ReleaseEvidenceIndex $f @('artifacts/tests/synthetic.trx') 24} 'ReleaseTrxCountMismatch'
    }
    Case 'duplicate execution ids are rejected' {
        $f=New-Fixture;Write-Trx (Join-Path $f 'artifacts/tests/synthetic.trx') @('Passed','Passed') -Duplicate
        Reject {New-ReleaseEvidenceIndex $f @('artifacts/tests/synthetic.trx') 24} 'ReleaseTrxDuplicateOrMissingExecution'
    }
    Case 'total count mismatch is rejected' {
        $f=New-Fixture;Write-Trx (Join-Path $f 'artifacts/tests/synthetic.trx') -TotalOverride 99
        Reject {New-ReleaseEvidenceIndex $f @('artifacts/tests/synthetic.trx') 24} 'ReleaseTrxCountMismatch'
    }
    Case 'DTD and external entities are not evaluated' {
        $f=New-Fixture
        Write-Fixture (Join-Path $f 'artifacts/tests/synthetic.trx') '<!DOCTYPE TestRun [<!ENTITY external SYSTEM "file:///synthetic-never-read">]><TestRun>&external;</TestRun>'
        Reject {New-ReleaseEvidenceIndex $f @('artifacts/tests/synthetic.trx') 24} 'DTD'
    }
    Case 'index source digest mismatch is rejected' {
        $f=New-Fixture;Seal-Fixture $f;$p=Join-Path $f 'artifacts/tests/index.json';$index=ConvertFrom-ReleaseJson ([IO.File]::ReadAllText($p));$index.sourceManifestSha256='a'*64
        Write-Fixture $p ($index|ConvertTo-Json -Depth 12)
        Reject {Test-CommercialReleaseEvidence $f 'artifacts/tests/index.json' 24} 'ReleaseIndexSourceOrTimeMismatch'
    }
    Case 'stale index is rejected' {
        $f=New-Fixture;Seal-Fixture $f;$p=Join-Path $f 'artifacts/tests/index.json';$index=ConvertFrom-ReleaseJson ([IO.File]::ReadAllText($p));$index.createdUtc=[DateTimeOffset]::UtcNow.AddDays(-3).ToString('O')
        Write-Fixture $p ($index|ConvertTo-Json -Depth 12)
        Reject {Test-CommercialReleaseEvidence $f 'artifacts/tests/index.json' 24} 'ReleaseIndexSourceOrTimeMismatch'
    }
    Case 'duplicate index names and paths are rejected' {
        $f=New-Fixture;Seal-Fixture $f;$p=Join-Path $f 'artifacts/tests/index.json';$index=ConvertFrom-ReleaseJson ([IO.File]::ReadAllText($p));$index.tests=@($index.tests[0],$index.tests[0])
        Write-Fixture $p ($index|ConvertTo-Json -Depth 12)
        Reject {Test-CommercialReleaseEvidence $f 'artifacts/tests/index.json' 24} 'ReleaseTestIndexEntryInvalid'
    }
    Case 'duplicate case ambiguous index properties are rejected' {
        $f=New-Fixture;Seal-Fixture $f;Write-Fixture (Join-Path $f 'artifacts/tests/index.json') '{"schema":"one","Schema":"two"}'
        Reject {Test-CommercialReleaseEvidence $f 'artifacts/tests/index.json' 24} 'DuplicateReleaseJsonProperty'
    }
    Case 'caller cannot insert a production approval flag into evidence contract' {
        $f=New-Fixture;Seal-Fixture $f;$p=Join-Path $f 'artifacts/tests/index.json';$index=ConvertFrom-ReleaseJson ([IO.File]::ReadAllText($p));$index.productionReady=$true
        Write-Fixture $p ($index|ConvertTo-Json -Depth 12)
        Reject {Test-CommercialReleaseEvidence $f 'artifacts/tests/index.json' 24} 'ReleaseJsonContractMismatch'
    }
    Case 'source changes without regenerating manifest are rejected' {
        $f=New-Fixture;Seal-Fixture $f;Write-Fixture (Join-Path $f 'src/example.cs') '// changed source'
        Reject {Test-CommercialReleaseEvidence $f 'artifacts/tests/index.json' 24} 'Changed content:'
    }
    Case 'regenerated source manifest cannot preserve an old test index binding' {
        $f=New-Fixture;Seal-Fixture $f;Write-Fixture (Join-Path $f 'src/example.cs') '// changed source'
        & (Join-Path $root 'scripts/Update-Manifest.ps1') -RepositoryRoot $f
        Reject {Test-CommercialReleaseEvidence $f 'artifacts/tests/index.json' 24} 'ReleaseIndexSourceOrTimeMismatch'
    }
    Case 'enabled deployment target is rejected despite passing tests' {
        $f=New-Fixture;Write-Fixture (Join-Path $f 'self-service/targets/example.json') '{"enabled":true}'
        Seal-Fixture $f;Reject {Test-CommercialReleaseEvidence $f 'artifacts/tests/index.json' 24} 'ReleaseEnabledTargetRejected'
    }
    Case 'string false is not a disabled target boolean' {
        $f=New-Fixture;Write-Fixture (Join-Path $f 'self-service/targets/example.json') '{"enabled":"false"}'
        Seal-Fixture $f;Reject {Test-CommercialReleaseEvidence $f 'artifacts/tests/index.json' 24} 'ReleaseEnabledTargetRejected'
    }
    Case 'credential runtime path is rejected even with synthetic safe content' {
        $f=New-Fixture;Write-Fixture (Join-Path $f '.local/session/auth.json') '{"synthetic":true}'
        Seal-Fixture $f;Reject {Test-CommercialReleaseEvidence $f 'artifacts/tests/index.json' 24} 'ReleaseSourceArtifactRejected'
    }
    Case 'secret shaped source value is rejected without echoing the value' {
        $f=New-Fixture;$synthetic='ghp_'+('a'*30);Write-Fixture (Join-Path $f 'config/example.json') ('{"syntheticValue":"'+$synthetic+'"}')
        Seal-Fixture $f;Reject {Test-CommercialReleaseEvidence $f 'artifacts/tests/index.json' 24} 'ReleaseSourceArtifactRejected'
    }
    Case 'private key shaped source artifact is rejected' {
        $f=New-Fixture;Write-Fixture (Join-Path $f 'config/example.pem') ("-----BEGIN PRIVATE KEY-----`n"+('a'*80)+"`n-----END PRIVATE KEY-----")
        Seal-Fixture $f;Reject {Test-CommercialReleaseEvidence $f 'artifacts/tests/index.json' 24} 'ReleaseSourceArtifactRejected'
    }
    Case 'synthetic fixture exclusion is disclosed and cannot approve production' {
        $f=New-Fixture;Write-Fixture (Join-Path $f 'tests/example/Fixtures/synthetic.json') ('{"syntheticValue":"ghp_'+('a'*30)+'"}')
        Seal-Fixture $f;$receipt=Test-CommercialReleaseEvidence $f 'artifacts/tests/index.json' 24
        Check ($receipt.sourceScan.excludedSyntheticFixtures.Count -eq 1 -and !$receipt.productionReady -and $receipt.sourceScan.limitations.Count -gt 0)
    }
    Case 'outside repository evidence cannot be selected' {
        $f=New-Fixture;Reject {New-ReleaseEvidenceIndex $f @((Join-Path $run 'outside.trx')) 24} 'ReleasePathOutsideRepository'
    }
    Case 'evidence index cannot escape artifact directory' {
        $f=New-Fixture;Reject {Write-ReleaseJson $f 'src/overwrite.json' @{synthetic=$true}} 'ReleaseEvidenceMustBeUnderArtifacts'
    }
    Case 'malicious index evidence path cannot escape repository' {
        $f=New-Fixture;Seal-Fixture $f;$p=Join-Path $f 'artifacts/tests/index.json';$index=ConvertFrom-ReleaseJson ([IO.File]::ReadAllText($p));$index.tests[0].path='../../../outside.trx'
        Write-Fixture $p ($index|ConvertTo-Json -Depth 12)
        Reject {Test-CommercialReleaseEvidence $f 'artifacts/tests/index.json' 24} 'ReleasePathOutsideRepository'
    }
    $receipt=[ordered]@{schema='platform-studio.commercial-release-contract-tests.v1';observedUtc=[DateTimeOffset]::UtcNow.ToString('O');passed=$results.Count;failed=0;results=$results.ToArray();
        syntheticFixturesOnly=$true;cloudCalls=$false;signIns=$false;pipelineSubmissions=$false;productionQualification=$false}
    Write-Fixture (Join-Path $run 'results.json') ($receipt|ConvertTo-Json -Depth 12)
    Write-Host "PASS: $($results.Count) mechanical release contracts; no cloud, sign-in, pipeline or publication. $run"
}finally{
    foreach($name in $previous.Keys){if($null -eq $previous[$name]){Remove-Item -LiteralPath ('Env:'+$name) -ErrorAction SilentlyContinue}else{[Environment]::SetEnvironmentVariable($name,$previous[$name])}}
}
