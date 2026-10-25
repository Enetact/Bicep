#requires -Version 7.4
[CmdletBinding()]
param([string]$DotNetPath='C:\IT\Package\dotnet\dotnet.exe',[string]$SdkPath='C:\IT\Package\dotnet\sdk\10.0.300')
$ErrorActionPreference='Stop'
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repositoryRoot 'scripts/FoundationTestExecutionEvidence.ps1')
$fixtureRoot=Join-Path $repositoryRoot ('artifacts/foundation-test-execution-tests/'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $fixtureRoot;$script:checks=0;$runs=[Collections.Generic.List[object]]::new()
function Check([bool]$Condition,[string]$Message){if(!$Condition){throw $Message};$script:checks++}
function Reject([scriptblock]$Action,[string]$Reason){$caught=$false;try{&$Action|Out-Null}catch{$caught=$true;if($_.Exception.Message -notlike $Reason){throw}};Check $caught ('Expected rejection '+$Reason)}
function WriteFixture([string]$Path,[string]$Text){$null=New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force;[IO.File]::WriteAllText($Path,$Text,[Text.UTF8Encoding]::new($false))}
function NewExecutionFixture([string]$Name){
    $directory=Join-Path $fixtureRoot $Name;$null=New-Item -ItemType Directory -Path $directory
    WriteFixture (Join-Path $directory '.gitignore') "artifacts/`n"
    WriteFixture (Join-Path $directory '.gitattributes') "* text=auto eol=lf`n*.bin -text`n*.cmd text eol=crlf`n"
    WriteFixture (Join-Path $directory 'fixture.marker') 'owned-test-execution-fixture'
    WriteFixture (Join-Path $directory 'MANIFEST.sha256') "synthetic manifest consumed as an input`n"
    WriteFixture (Join-Path $directory 'input.txt') "synthetic input`r`nsecond line`r`n"
    WriteFixture (Join-Path $directory 'vendor/notice.txt') "synthetic vendor fixture`n"
    WriteFixture (Join-Path $directory 'docs/guide.md') "synthetic guide fixture`n"
    WriteFixture (Join-Path $directory '.agents/skills/fixture/SKILL.md') "synthetic instruction fixture`n"
    WriteFixture (Join-Path $directory 'tests/SelfService.Foundation.Tests/SelfService.Foundation.Tests.csproj') '<Project Sdk="Microsoft.NET.Sdk" />'
    $null=New-Item -ItemType Directory -Path (Join-Path $directory 'scripts')
    foreach($name in @('FoundationTestExecutionEvidence.ps1','Test-Foundation.ps1')){Copy-Item -LiteralPath (Join-Path $repositoryRoot ('scripts/'+$name)) -Destination (Join-Path $directory ('scripts/'+$name)) -ErrorAction Stop}
    foreach($arguments in @(@('init','--quiet'),@('config','core.autocrlf','false'),@('config','user.email','synthetic@example.invalid'),@('config','user.name','Synthetic execution fixture'),@('add','--all'),@('commit','--quiet','-m','Owned synthetic fixture'))){$null=Get-FTEGit $directory $arguments}
    $directory
}

$tools=Join-Path $fixtureRoot 'tools';$null=New-Item -ItemType Directory -Path $tools
$source=Join-Path $tools 'Fixture.cs';$assembly=Join-Path $tools 'fixture.dll';$standIn=Join-Path $tools 'dotnet.exe';$dotnetRoot=Split-Path $DotNetPath -Parent
WriteFixture $source @'
using System; using System.IO; using System.Linq; using System.Threading; using System.Xml.Linq;
public static class Fixture {
 public static int Main(string[] args){
  var root=Environment.CurrentDirectory;if(!File.Exists(Path.Combine(root,"fixture.marker"))||File.ReadAllText(Path.Combine(root,"fixture.marker"))!="owned-test-execution-fixture")return 91;
  var mode=Environment.GetEnvironmentVariable("FOUNDATION_TEST_EXECUTION_FIXTURE")??"valid";
  if(args[0]=="restore")return mode=="restore-exit"?7:0;
  if(args[0]!="test")return 92;
  if(mode=="native-stall"||mode=="native-overflow"){File.WriteAllText(Path.Combine(root,"artifacts","native-child.pid"),Environment.ProcessId.ToString());if(mode=="native-overflow"){Console.Write(new string('x',4194305));Console.Out.Flush();}Thread.Sleep(Timeout.Infinite);return 0;}
  if(mode=="no-trx")return 0;
  if(mode=="change")File.AppendAllText(Path.Combine(root,"input.txt"),"synthetic changed input");
  if(mode=="manifest-change")File.AppendAllText(Path.Combine(root,"MANIFEST.sha256"),"synthetic changed manifest input");
  if(mode=="add")File.WriteAllText(Path.Combine(root,"new-source.txt"),"synthetic added input");
  if(mode=="remove"){var path=Path.Combine(root,"input.txt");if(!File.ReadAllText(path).StartsWith("synthetic"))return 93;File.Delete(path);}
  var directory=args[Array.IndexOf(args,"--results-directory")+1];
  var now=mode=="stale"?DateTimeOffset.UnixEpoch:DateTimeOffset.UtcNow;XNamespace ns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
  var results=new XElement(ns+"Results");var definitions=new XElement(ns+"TestDefinitions");var entries=new XElement(ns+"TestEntries");
  for(int i=0;i<2;i++){var test=Guid.NewGuid().ToString("D");var execution=Guid.NewGuid().ToString("D");
   if(mode=="guid-alias"){test="aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";execution="bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";if(i==1){test=test.ToUpperInvariant();execution=execution.ToUpperInvariant();}}
   results.Add(new XElement(ns+"UnitTestResult",new XAttribute("testId",test),new XAttribute("executionId",execution),new XAttribute("outcome",i==1&&mode=="failed"?"Failed":i==1&&mode=="skipped"?"NotExecuted":"Passed")));
   definitions.Add(new XElement(ns+"UnitTest",new XAttribute("id",test)));entries.Add(new XElement(ns+"TestEntry",new XAttribute("testId",test),new XAttribute("executionId",execution)));
  }
  var failed=mode=="failed"?1:0;var skipped=mode=="skipped"?1:0;var passed=2-failed-skipped;
  var counters=new XElement(ns+"Counters",new XAttribute("total",2),new XAttribute("executed",2-skipped),new XAttribute("passed",mode=="counter"?99:passed),new XAttribute("failed",failed),new XAttribute("notExecuted",0));
  var run=new XElement(ns+"TestRun",new XAttribute("id",Guid.NewGuid().ToString("D")),new XElement(ns+"Times",new XAttribute("start",now.ToString("O")),new XAttribute("finish",now.ToString("O"))),results,definitions,entries,new XElement(ns+"ResultSummary",new XAttribute("outcome","Completed"),counters));
  Directory.CreateDirectory(directory);new XDocument(run).Save(Path.Combine(directory,"foundation.trx"));return mode=="test-exit"?9:0;
 }
}
'@
$references=Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref/10.0.8/ref/net10.0'
$arguments=@('exec',(Join-Path $SdkPath 'Roslyn/bincore/csc.dll'),'/noconfig','/nostdlib+','/target:exe',('/out:'+$assembly),$source)+@(Get-ChildItem -LiteralPath $references -Filter '*.dll' -File|ForEach-Object {'/reference:'+$_.FullName})
$compiled=Invoke-FTEProcess $DotNetPath $arguments $tools 30000
Check ($compiled.exitCode -eq 0) ('Constant synthetic executable compilation failed: '+$compiled.stdout+$compiled.stderr)
WriteFixture (Join-Path $tools 'fixture.runtimeconfig.json') '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}'
[void][Reflection.Assembly]::LoadFrom((Join-Path $SdkPath 'Microsoft.NET.HostModel.dll'))
$architecture=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
$template=Join-Path $dotnetRoot ('packs/Microsoft.NETCore.App.Host.win-'+$architecture+'/10.0.8/runtimes/win-'+$architecture+'/native/apphost.exe')
$search=[Microsoft.NET.HostModel.AppHost.HostWriter+DotNetSearchOptions]::new();$search.Location=[Microsoft.NET.HostModel.AppHost.HostWriter+DotNetSearchOptions+SearchLocation]::AppRelative;$search.AppRelativeDotNet=[IO.Path]::GetRelativePath($tools,$dotnetRoot)
[Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost($template,$standIn,'fixture.dll',$false,$assembly,$false,$false,$search)

$valid=$null
foreach($mode in @('valid','change','manifest-change','add','remove','no-trx','stale','counter','guid-alias','failed','skipped','restore-exit','test-exit')){
    $directory=NewExecutionFixture $mode;$oldMode=$env:FOUNDATION_TEST_EXECUTION_FIXTURE
    # A historical TRX must never be reused by the fresh run directory.
    if($mode -ceq 'no-trx'){WriteFixture (Join-Path $directory 'artifacts/test-results/foundation/foundation.trx') '<synthetic-old-trx />'}
    try{
        $env:FOUNDATION_TEST_EXECUTION_FIXTURE=$mode;$result=$null;$caught=$false
        try{$result=& (Join-Path $directory 'scripts/Test-Foundation.ps1') -DotNetPath $standIn}catch{$caught=$true}
    }finally{$env:FOUNDATION_TEST_EXECUTION_FIXTURE=$oldMode}
    $run=@(Get-ChildItem -LiteralPath (Join-Path $directory 'artifacts/foundation-test-executions') -Directory)
    Check ($run.Count -eq 1) 'Runner failed to create exactly one fresh evidence directory.'
    if($mode -ceq 'valid'){
        Check (!$caught -and $result.passed -eq 2) 'Valid actual synthetic process execution did not complete.'
        $valid=$run[0].FullName;$pin=(Get-FileHash -LiteralPath (Join-Path $valid 'completed.json')).Hash.ToLowerInvariant()
        $verified=Test-FTEExecutionReceipt $valid $pin
        Check ($verified.passed -and !$verified.independentAttestation -and !$verified.completeDependencyClosureVerified -and !$verified.noNetworkAttested -and !$verified.productionReady) 'Execution receipt overstated attestation.'
        $snapshot=Get-Content -LiteralPath (Join-Path $valid 'source-before.json') -Raw|ConvertFrom-Json -AsHashtable
        Check (@($snapshot.files.path|Where-Object {$_ -in @('docs/guide.md','vendor/notice.txt','.agents/skills/fixture/SKILL.md')}).Count -eq 3) 'Capture omitted guidance/docs/vendor source.'
        Check ($snapshot.files.path -ccontains 'MANIFEST.sha256' -and $snapshot.excludedPaths.Count -eq 0) 'Capture omitted the consumed source manifest.'
        $completed=Get-Content -LiteralPath (Join-Path $valid 'completed.json') -Raw|ConvertFrom-Json -AsHashtable
        Check ($completed.commands[0].arguments[0] -ceq 'restore' -and $completed.commands[1].arguments[0] -ceq 'test' -and $completed.commands[0].executableSha256 -ceq (Get-FileHash -LiteralPath $standIn).Hash.ToLowerInvariant()) 'Receipt failed to pin observed executable/arguments.'
    }else{
        Check ($caught -and !(Test-Path -LiteralPath (Join-Path $run[0].FullName 'completed.json'))) 'Rejected run produced a completed receipt.'
        $rejected=Get-Content -LiteralPath (Join-Path $run[0].FullName 'rejected.json') -Raw|ConvertFrom-Json -AsHashtable
        Check ($rejected.state -ceq 'Rejected' -and !$rejected.passed) 'Rejected run was recorded as success.'
        Reject {Test-FTEExecutionReceipt $run[0].FullName ('0'*64)} '*CompletedReceiptRequired*'
        if($mode -ceq 'skipped'){Check ($rejected.code -ceq 'TestExecutionSkippedResults' -and $rejected.trx.notExecuted -eq 1) 'A zero-summary skipped result was accepted or misclassified.'}
        if($mode -ceq 'failed'){Check ($rejected.code -ceq 'TestExecutionFailedResults' -and $rejected.trx.failed -eq 1) 'A real failed result was accepted or misclassified.'}
        if($mode -in @('restore-exit','test-exit')){Check ($rejected.code -ceq 'TestExecutionNonzeroExit') 'Nonzero observed process exit was not rejected.'}
        if($mode -ceq 'no-trx'){Check (Test-Path -LiteralPath (Join-Path $directory 'artifacts/test-results/foundation/foundation.trx')) 'Runner modified historical evidence.'}
    }
    $runs.Add([ordered]@{fixture=$mode;run=$run[0].FullName;completed=($mode -ceq 'valid')})
}

$normalization=NewExecutionFixture 'normalization';$before=Get-FTESourceSnapshot $normalization
[IO.File]::WriteAllText((Join-Path $normalization 'input.txt'),"synthetic input`nsecond line`n")
$after=Get-FTESourceSnapshot $normalization;$a=@($before.files|Where-Object path -CEQ 'input.txt')[0];$b=@($after.files|Where-Object path -CEQ 'input.txt')[0]
Check ($a.governedSha256 -ceq $b.governedSha256 -and $a.rawSha256 -cne $b.rawSha256) 'LF-governed hashing did not preserve canonical identity and raw-byte distinction.'
WriteFixture (Join-Path $normalization 'ascii.bin') "synthetic`r`nbytes`r`n";$binary=Get-FTESourceSnapshot $normalization;$entry=@($binary.files|Where-Object path -CEQ 'ascii.bin')[0]
Check ($entry.rawSha256 -ceq $entry.governedSha256 -and !$entry.crlfNormalized) 'Explicit binary data was normalized.'
$incomplete=New-FTEPrivateDirectory (Join-Path $fixtureRoot 'incomplete');$null=Write-FTEJson (Join-Path $incomplete 'started.json') @{state='Started'}
Reject {Test-FTEExecutionReceipt $incomplete ('0'*64)} '*CompletedReceiptRequired*'
$completedPath=Join-Path $valid 'completed.json';$original=[IO.File]::ReadAllText($completedPath);$validPin=(Get-FileHash -LiteralPath $completedPath).Hash.ToLowerInvariant()
Reject {Test-FTEExecutionReceipt $valid ('0'*64)} '*ArtifactPinMismatch*'
foreach($fault in @('filter','tool','order','exit-type','unsupported-claim','missing-started')){
    $tampered=ConvertFrom-Json -InputObject $original -AsHashtable
    switch($fault){
        'filter'{$tampered.commands[1].arguments+=@('--filter','synthetic-filter-that-must-not-pass')}
        'tool'{$tampered.commands[1].executableSha256='0'*64}
        'order'{$tampered.commands=@($tampered.commands[1],$tampered.commands[0])}
        'exit-type'{$tampered.commands[1].exitCode='0'}
        'unsupported-claim'{$tampered.limits.independentAttestation=$true}
        'missing-started'{$tampered.started.sha256='0'*64}
    }
    [IO.File]::WriteAllText($completedPath,($tampered|ConvertTo-Json -Depth 24));$repinned=(Get-FileHash -LiteralPath $completedPath).Hash.ToLowerInvariant()
    Reject {Test-FTEExecutionReceipt $valid $repinned} '*'
}
[IO.File]::WriteAllText($completedPath,$original)
foreach($fault in @('started-claim','summary-claim')){
    $binding=ConvertFrom-Json -InputObject $original -AsHashtable;$artifactPath=if($fault -ceq 'started-claim'){Join-Path $valid 'started.json'}else{Join-Path $valid 'results/summary.json'};$artifactOriginal=[IO.File]::ReadAllText($artifactPath)
    try{
        $artifact=ConvertFrom-Json -InputObject $artifactOriginal -AsHashtable
        if($fault -ceq 'started-claim'){$artifact.limits.noNetworkAttested=$true}else{$artifact.deploymentVerified=$true}
        [IO.File]::WriteAllText($artifactPath,($artifact|ConvertTo-Json -Depth 24));$artifactHash=(Get-FileHash -LiteralPath $artifactPath).Hash.ToLowerInvariant()
        if($fault -ceq 'started-claim'){$binding.started.sha256=$artifactHash}else{$binding.summary.sha256=$artifactHash}
        [IO.File]::WriteAllText($completedPath,($binding|ConvertTo-Json -Depth 24));$repinned=(Get-FileHash -LiteralPath $completedPath).Hash.ToLowerInvariant()
        Reject {Test-FTEExecutionReceipt $valid $repinned} '*'
    }finally{[IO.File]::WriteAllText($artifactPath,$artifactOriginal);[IO.File]::WriteAllText($completedPath,$original)}
}
foreach($mode in @('native-stall','native-overflow')){
    $oldMode=$env:FOUNDATION_TEST_EXECUTION_FIXTURE
    $reason=if($mode -ceq 'native-overflow'){'*TestExecutionOutputLimit*'}else{'*'};$timeout=if($mode -ceq 'native-overflow'){5000}else{1000}
    try{$env:FOUNDATION_TEST_EXECUTION_FIXTURE=$mode;Reject {Invoke-FTEProcess $standIn @('test') (Join-Path $fixtureRoot 'valid') $timeout} $reason}finally{$env:FOUNDATION_TEST_EXECUTION_FIXTURE=$oldMode}
    $ownedPid=[int][IO.File]::ReadAllText((Join-Path $fixtureRoot 'valid/artifacts/native-child.pid'));$ownedProcess=Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
    try{Check ($null -eq $ownedProcess -or $ownedProcess.HasExited) 'Interrupted/oversized synthetic observation left its owned child running.'}finally{if($ownedProcess){$ownedProcess.Dispose()}}
}
$validTrx=Join-Path $valid 'results/foundation.trx';$changed=[xml](Get-Content -LiteralPath $validTrx -Raw);$changed.DocumentElement.SetAttribute('id',[Guid]::NewGuid().ToString('D'));$changed.Save($validTrx)
Reject {Test-FTEExecutionReceipt $valid $validPin} '*TrxReceiptMismatch*'
$summary=[ordered]@{schema='platform-studio.foundation-test-execution-fixtures.v1';passed=$true;checks=$script:checks;runs=$runs.ToArray();actualFoundationSuiteExecuted=$false;constantOwnedStandInExecuted=$true;restorePerformed=$false;noNetworkAttested=$false;independentAttestation=$false;productionReady=$false;cloudOperationsPerformed=$false}
$null=Write-FTEJson (Join-Path $fixtureRoot 'summary.json') $summary
Write-Host "Foundation execution evidence: $script:checks checks passed. $fixtureRoot"
