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
    foreach($name in @('FoundationTestExecutionEvidence.ps1','Test-Foundation.ps1','FoundationBuildRetention.ps1','FoundationOwnedBuildNative.cs','Remove-OldBuildIntermediates.ps1')){Copy-Item -LiteralPath (Join-Path $repositoryRoot ('scripts/'+$name)) -Destination (Join-Path $directory ('scripts/'+$name)) -ErrorAction Stop}
    foreach($arguments in @(@('init','--quiet'),@('config','core.autocrlf','false'),@('config','user.email','synthetic@example.invalid'),@('config','user.name','Synthetic execution fixture'),@('add','--all'),@('commit','--quiet','-m','Owned synthetic fixture'))){$null=Get-FTEGit $directory $arguments}
    $directory
}

$tools=Join-Path $fixtureRoot 'tools';$null=New-Item -ItemType Directory -Path $tools
$source=Join-Path $tools 'Fixture.cs';$assembly=Join-Path $tools 'fixture.dll';$standIn=Join-Path $tools 'dotnet.exe';$dotnetRoot=Split-Path $DotNetPath -Parent
WriteFixture $source @'
using System; using System.IO; using System.Linq; using System.Threading; using System.Xml.Linq;
using System.Diagnostics;
public static class Fixture {
 public static int Main(string[] args){
  if(args[0]=="process"){
   var scenario=args[1];var marker=args[2];
   if(scenario=="bridge"){var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};foreach(var a in new[]{"process","child",marker})start.ArgumentList.Add(a);using var c=Process.Start(start);var clock=Stopwatch.StartNew();while(!File.Exists(marker)&&clock.ElapsedMilliseconds<10000)Thread.Sleep(10);return File.Exists(marker)?0:97;}
   if(scenario=="child"||scenario=="childquiet"){File.WriteAllText(marker,Environment.ProcessId+"|"+Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);if(scenario=="child"){Console.Write("child-out");Console.Error.Write("child-err");}Thread.Sleep(args.Length>3?int.Parse(args[3]):Timeout.Infinite);return 0;}
   Process Spawn(bool quiet,int delay){var start=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=quiet,RedirectStandardError=quiet};foreach(var a in new[]{"process",scenario=="grandchild"?"bridge":quiet?"childquiet":"child",marker,delay.ToString()})start.ArgumentList.Add(a);return Process.Start(start)!;}
   if(scenario=="many"){for(int i=0;i<130;i++){using var c=Spawn(true,0);c.WaitForExit();if(c.ExitCode!=0)return 96;}return 0;}
   using var child=Spawn(scenario=="no-pipes",scenario=="late"?500:Timeout.Infinite);var wait=Stopwatch.StartNew();while(!File.Exists(marker)&&wait.ElapsedMilliseconds<10000)Thread.Sleep(10);if(!File.Exists(marker))return 95;
   Console.Write("parent-out");Console.Error.Write("parent-err");
   if(scenario=="nonzero")return 7;
   if(scenario=="exit259")return 259;
   if(scenario=="overflow"){Console.Write(new string('x',4194305));Console.Out.Flush();Thread.Sleep(Timeout.Infinite);}
   if(scenario=="stderr-overflow"){Console.Error.Write(new string('x',4194305));Console.Error.Flush();Thread.Sleep(Timeout.Infinite);}
   if(scenario=="stall")Thread.Sleep(Timeout.Infinite);
   return 0;
  }
  var root=Environment.CurrentDirectory;if(!File.Exists(Path.Combine(root,"fixture.marker"))||File.ReadAllText(Path.Combine(root,"fixture.marker"))!="owned-test-execution-fixture")return 91;
  var mode=Environment.GetEnvironmentVariable("FOUNDATION_TEST_EXECUTION_FIXTURE")??"valid";
  if(args[0]=="restore")return mode=="restore-exit"?7:0;
  if(args[0]!="test")return 92;
  if(mode=="native-stall"||mode=="native-overflow"){File.WriteAllText(Path.Combine(root,"artifacts","native-child.pid"),Environment.ProcessId.ToString());if(mode=="native-overflow"){Console.Write(new string('x',4194305));Console.Out.Flush();}Thread.Sleep(Timeout.Infinite);return 0;}
  if(mode=="no-trx")return 0;
  if(mode=="test-exit-no-trx"){Console.Error.Write("Synthetic compiler failure before test execution.");return 31;}
  if(mode=="test-exit-invalid-trx"){var output=args[Array.IndexOf(args,"--results-directory")+1];Directory.CreateDirectory(output);File.WriteAllText(Path.Combine(output,"foundation.trx"),"<synthetic-partial-trx");return 32;}
  if(mode=="change")File.AppendAllText(Path.Combine(root,"input.txt"),"synthetic changed input");
  if(mode=="manifest-change")File.AppendAllText(Path.Combine(root,"MANIFEST.sha256"),"synthetic changed manifest input");
  if(mode=="add")File.WriteAllText(Path.Combine(root,"new-source.txt"),"synthetic added input");
  if(mode=="remove"){var path=Path.Combine(root,"input.txt");if(!File.ReadAllText(path).StartsWith("synthetic"))return 93;File.Delete(path);}
  var directory=args[Array.IndexOf(args,"--results-directory")+1];
  var now=mode=="stale"?DateTimeOffset.UnixEpoch:DateTimeOffset.UtcNow;XNamespace ns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
  var results=new XElement(ns+"Results");var definitions=new XElement(ns+"TestDefinitions");var entries=new XElement(ns+"TestEntries");
  for(int i=0;i<2;i++){var test=Guid.NewGuid().ToString("D");var execution=Guid.NewGuid().ToString("D");
   if(mode=="guid-alias"){test="aaaaaaaa-aaaa-4aaa-8aaa-aaaaaaaaaaaa";execution="bbbbbbbb-bbbb-4bbb-8bbb-bbbbbbbbbbbb";if(i==1){test=test.ToUpperInvariant();execution=execution.ToUpperInvariant();}}
   results.Add(new XElement(ns+"UnitTestResult",new XAttribute("testId",test),new XAttribute("executionId",execution),new XAttribute("outcome",i==1&&(mode=="failed"||mode=="test-exit-failed-trx")?"Failed":i==1&&mode=="skipped"?"NotExecuted":"Passed")));
   definitions.Add(new XElement(ns+"UnitTest",new XAttribute("id",test)));entries.Add(new XElement(ns+"TestEntry",new XAttribute("testId",test),new XAttribute("executionId",execution)));
  }
  var failed=mode=="failed"||mode=="test-exit-failed-trx"?1:0;var skipped=mode=="skipped"?1:0;var passed=2-failed-skipped;
  var counters=new XElement(ns+"Counters",new XAttribute("total",2),new XAttribute("executed",2-skipped),new XAttribute("passed",mode=="counter"?99:passed),new XAttribute("failed",failed),new XAttribute("notExecuted",0));
  var run=new XElement(ns+"TestRun",new XAttribute("id",Guid.NewGuid().ToString("D")),new XElement(ns+"Times",new XAttribute("start",now.ToString("O")),new XAttribute("finish",now.ToString("O"))),results,definitions,entries,new XElement(ns+"ResultSummary",new XAttribute("outcome","Completed"),counters));
  Directory.CreateDirectory(directory);new XDocument(run).Save(Path.Combine(directory,"foundation.trx"));return mode=="test-exit"||mode=="test-exit-failed-trx"?9:0;
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

# Actual descendant/pipe/error paths use the same production FTE entry point.
# The sentinel belongs to this test but is outside each runner job.
$sentinelMarker=Join-Path $tools 'outside-job.pid';$sentinel=$null
try{
    $start=[Diagnostics.ProcessStartInfo]::new($standIn);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($arg in @('process','childquiet',$sentinelMarker)){$start.ArgumentList.Add($arg)}
    $sentinel=[Diagnostics.Process]::Start($start);$null=$sentinel.SafeHandle
    foreach($scenario in @('late','many','pipe-hold','no-pipes','grandchild','nonzero','exit259','overflow','stderr-overflow','stall','cancel')){
        $marker=Join-Path $tools ($scenario+'.pid');$cts=[Threading.CancellationTokenSource]::new();$observation=$null;$caught=$null
        $timeout=if($scenario -in @('pipe-hold','no-pipes','grandchild','stall')){1000}else{30000}
        if($scenario -ceq 'cancel'){$cts.CancelAfter(1000)}
        $clock=[Diagnostics.Stopwatch]::StartNew()
        try{
            try{$observation=Invoke-FTEProcess $standIn @('process',$scenario,$marker) $tools $timeout $cts.Token}catch{
                $caught=$_.Exception;for($cause=$caught;$cause;$cause=$cause.InnerException){if($cause.Data.Contains('FoundationTestProcessObservation')){$observation=$cause.Data['FoundationTestProcessObservation'];break}}
            }
            Check ($null -ne $observation) ('Owned process diagnostics missing: '+$scenario)
            $process=$observation.processObservation
            Check ($process.ownedJobStopped -and $process.activeProcesses -eq 0 -and !$process.independentAttestation) ('Owned descendants were not observed stopped: '+$scenario)
            Check ($process.maximumActiveProcesses -eq 64 -and $process.maximumTotalProcesses -eq 16384 -and $process.totalProcesses -ge 2) ('Explicit test process bounds missing: '+$scenario)
            Check ([Text.Encoding]::UTF8.GetByteCount($observation.stdout) -le 4194304 -and [Text.Encoding]::UTF8.GetByteCount($observation.stderr) -le 4194304) ('Process capture exceeded bound: '+$scenario)
            if($scenario -in @('late','many','nonzero','exit259')){
                Check (!$caught -and $observation.exitCode -eq $(switch($scenario){'nonzero'{7};'exit259'{259};default{0}})) ('Observed nonzero/success was changed: '+$scenario)
                if($scenario -ceq 'many'){Check ($process.totalProcesses -ge 131) 'Full-cohort process policy failed after ordinary build cumulative limit.'}
            }else{
                $expected=switch($scenario){'overflow'{'TestExecutionOutputLimit'};'stderr-overflow'{'TestExecutionOutputLimit'};'cancel'{'TestExecutionCancelled'};default{'TestExecutionTimeout'}}
                Check ($caught -and $process.failureCode -ceq $expected) ('Process error was not preserved: '+$scenario)
            }
            if($scenario -cne 'many'){Check ($observation.stdout.Contains('parent-out') -and $observation.stderr.Contains('parent-err')) ('Failure/descendant output was lost: '+$scenario)}
            if($scenario -ceq 'grandchild'){Check ($process.totalProcesses -ge 3) 'Nested descendant was not held in the job.'}
            Check ($clock.ElapsedMilliseconds -lt $timeout+10000) ('Process cleanup exceeded its bounded allowance: '+$scenario)
            Check (Test-Path -LiteralPath $marker) ('Owned descendant never ran: '+$scenario)
            $identity=[IO.File]::ReadAllText($marker).Split('|');$child=Get-Process -Id ([int]$identity[0]) -ErrorAction SilentlyContinue
            try{Check ($null -eq $child -or $child.HasExited -or $child.StartTime.ToUniversalTime().Ticks -ne [long]$identity[1]) ('Runner left a matching live descendant: '+$scenario)}finally{if($child){$child.Dispose()}}
            Check (!$sentinel.HasExited) ('Runner stopped an out-of-job process: '+$scenario)
        }finally{$cts.Dispose()}
    }
    # Ordinary build policy is unchanged even though FTE admits many serial test fixtures.
    $session=$null
    try{$session=[PlatformStudio.FoundationOwnedBuild.Native]::Start($standIn,[string[]]@('process','many',(Join-Path $tools 'build-default.pid')),$tools,[Environment]::GetEnvironmentVariables());Reject {$session.Wait(30000,[Threading.CancellationToken]::None)} '*ProcessLimit*'}finally{if($session){$session.Dispose()}}
}finally{if($sentinel){try{if(!$sentinel.HasExited){$sentinel.Kill();Check ($sentinel.WaitForExit(5000)) 'Owned sentinel did not stop.'}}finally{$sentinel.Dispose()}}}

$valid=$null
foreach($mode in @('valid','change','manifest-change','add','remove','no-trx','stale','counter','guid-alias','failed','skipped','restore-exit','test-exit','test-exit-no-trx','test-exit-invalid-trx','test-exit-failed-trx','native-overflow')){
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
        if($mode -in @('restore-exit','test-exit','test-exit-no-trx','test-exit-invalid-trx','test-exit-failed-trx')){Check ($rejected.code -ceq 'TestExecutionNonzeroExit') 'Nonzero observed process exit was not rejected.'}
        if($mode -ceq 'test-exit-no-trx'){
            Check ($null -eq $rejected.trx -and $rejected.commands.Count -eq 2 -and $rejected.commands[1].exitCode -eq 31) 'Compile failure without results was misrepresented as executed tests.'
            Check ([IO.File]::ReadAllText((Join-Path $run[0].FullName 'test.stderr.txt')).Contains('Synthetic compiler failure')) 'Compiler diagnostic was discarded.'
            Check (!(Test-Path -LiteralPath (Join-Path $run[0].FullName 'results/foundation.trx'))) 'Compile failure fabricated a TRX.'
        }
        if($mode -ceq 'test-exit-invalid-trx'){Check ($null -eq $rejected.trx -and (Test-Path -LiteralPath (Join-Path $run[0].FullName 'results/foundation.trx'))) 'Partial failed-run TRX was trusted or discarded.'}
        if($mode -ceq 'test-exit-failed-trx'){Check ($rejected.trx.failed -eq 1 -and $rejected.trx.passed -eq 1) 'Valid failed TRX metadata was lost behind the nonzero exit.'}
        if($mode -ceq 'no-trx'){Check (Test-Path -LiteralPath (Join-Path $directory 'artifacts/test-results/foundation/foundation.trx')) 'Runner modified historical evidence.'}
        if($mode -ceq 'native-overflow'){
            Check ($rejected.code -ceq 'TestExecutionOutputLimit' -and $rejected.commands.Count -eq 2) 'Runner lost failed-command classification/evidence.'
            $process=Get-Content -LiteralPath (Join-Path $run[0].FullName 'test.process.json') -Raw|ConvertFrom-Json -AsHashtable
            Check ($process.ownedJobStopped -and $process.activeProcesses -eq 0 -and $process.failureCode -ceq 'TestExecutionOutputLimit') 'Rejected runner did not retain bounded cleanup observation.'
            Check ((Get-Item -LiteralPath (Join-Path $run[0].FullName 'test.stdout.txt')).Length -gt 0) 'Rejected runner discarded captured partial output.'
        }
    }
    $runs.Add([ordered]@{fixture=$mode;run=$run[0].FullName;completed=($mode -ceq 'valid')})
}

# Retention uses the actual successful synthetic execution. Production process inspection has no bypass.
. (Join-Path $repositoryRoot 'scripts/FoundationBuildRetention.ps1')
$retentionRoot=Split-Path (Split-Path $valid -Parent) -Parent | Split-Path -Parent
function SetRetentionAge([string]$Directory,[int]$Minutes) {
    $old=[DateTime]::UtcNow.AddMinutes(-$Minutes)
    foreach($item in @(Get-ChildItem -LiteralPath $Directory -Recurse -Force)+(Get-Item -LiteralPath $Directory)) { $item.LastWriteTimeUtc=$old }
}
function NewRetentionTree([string]$Relative,[int]$AgeMinutes,[string]$File='') {
    $directory=Join-Path $retentionRoot ('artifacts/'+$Relative);$project=Join-Path $directory 'OwnedCompiler'
    foreach($name in @('project.assets.json','project.nuget.cache','OwnedCompiler.csproj.nuget.dgspec.json','OwnedCompiler.csproj.nuget.g.props','OwnedCompiler.csproj.nuget.g.targets')) { WriteFixture (Join-Path $project $name) 'owned synthetic SDK metadata name' }
    foreach($name in @('OwnedCompiler.dll','OwnedCompiler.pdb','OwnedCompiler.assets.cache')) { WriteFixture (Join-Path $project ('release/'+$name)) 'owned synthetic compiler bytes' }
    if($File) { WriteFixture (Join-Path $directory $File) 'owned preservation canary' }
    SetRetentionAge $directory $AgeMinutes;return $directory
}
$newest=NewRetentionTree 'retention-newest/build/obj' 120
$second=NewRetentionTree 'retention-second/build/obj' 180
$old=NewRetentionTree 'retention-old/build/obj' 240
$explicit=NewRetentionTree 'retention-keep/.build/obj' 300
$database=NewRetentionTree 'retention-data/build/obj' 360 'customer.sqlite-journal'
$key=NewRetentionTree 'retention-key/build/obj' 360 'private.pem'
$package=NewRetentionTree 'foundation-packages/build/obj' 360
$release=NewRetentionTree 'commercial-release/build/obj' 360
$preview=NewRetentionTree 'foundation-preview/build/obj' 360
$nested=NewRetentionTree ('retention-check/'+('a'*32)+'/build/obj') 240
$active=NewRetentionTree 'retention-active/build/obj' 300
$unrelatedActivation=Join-Path $retentionRoot 'artifacts/foundation-activation-provision-check/keep.txt';WriteFixture $unrelatedActivation 'owned unrelated check family'
$unrelatedBackup=Join-Path $retentionRoot 'artifacts/foundation-backup-checkpoint-check/keep.txt';WriteFixture $unrelatedBackup 'owned unrelated check family'
Check ((Get-FBRTree $retentionRoot $old).path -ieq $old) 'Unrelated protected check-family siblings falsely implied ancestor custody.'
$unknown=NewRetentionTree 'retention-unknown/build/obj' 400 'unrecognized-private-payload.json'
$unrecognized=Join-Path $retentionRoot 'artifacts/retention-arbitrary/build/obj';WriteFixture (Join-Path $unrecognized 'compile.cache') 'not an SDK layout'
$unsupported=NewRetentionTree 'retention-unsupported/build/obj' 400 'OwnedCompiler/release/new-sdk-layout.bin'
WriteFixture (Join-Path (Split-Path $old -Parent) 'bin/keep.dll') 'owned synthetic binary';$bin=Join-Path (Split-Path $old -Parent) 'bin/keep.dll'
$target=Join-Path $retentionRoot 'artifacts/retention-link-target';WriteFixture (Join-Path $target 'keep.txt') 'owned link target'
$linkBuild=Join-Path $retentionRoot 'artifacts/retention-link/build';$null=New-Item -ItemType Directory -Path $linkBuild -Force
$junction=Join-Path $linkBuild 'obj';$null=New-Item -ItemType Junction -Path $junction -Target $target
$request=@{Root=$retentionRoot;RunDirectory=$valid;CompletedSha256=(Get-FileHash -LiteralPath (Join-Path $valid 'completed.json')).Hash.ToLowerInvariant();KeepBuildDirectories=@((Split-Path $explicit -Parent))}
$wrongPin=$request.Clone();$wrongPin.CompletedSha256='0'*64
Reject { Invoke-FoundationBuildRetention @wrongPin -Apply } '*ArtifactPinMismatch*'
$input=Join-Path $retentionRoot 'input.txt';$originalInput=[IO.File]::ReadAllBytes($input)
try { [IO.File]::AppendAllText($input,'changed after successful execution');Reject { Invoke-FoundationBuildRetention @request -Apply } '*BuildRetentionSourceChanged*' }
finally { [IO.File]::WriteAllBytes($input,$originalInput) }
Check (Test-Path -LiteralPath $old) 'Failed qualification mutated a candidate.'
Reject { Get-FBRTree $retentionRoot $unknown } '*UnknownCompilerContent*'
Reject { Get-FBRTree $retentionRoot $unrecognized } '*UnknownCompilerContent*'
Reject { Get-FBRTree $retentionRoot $unsupported } '*UnknownCompilerContent*'
foreach($marker in @('.activation.guard','authority.json','state.json','binding.json','completion.json','.installation-owner.json','.installation.guard','.package-owner.json',
    '.credential-namespace.json','.vault.lock','.keys.lock','.rollback.lock','.backup-checkpoint.guard','cell-binding.json','.foundation-owner-initialization.json',
    '.credential-recovery-stage.guard','.credential-recovery-stage.json','.credential-recovery-checkpoint.guard','.credential-recovery-checkpoint.json','legacy.credential.json','legacy.dpapi.json')) {
    foreach($directoryMarker in @($false,$true)) {
        $scope='retention-marker-'+[Guid]::NewGuid().ToString('N');$canary=NewRetentionTree ($scope+'/build/obj') 500
        $markerPath=Join-Path (Split-Path (Split-Path $canary -Parent) -Parent) $marker
        if($directoryMarker){$null=New-Item -ItemType Directory -Path $markerPath}else{WriteFixture $markerPath 'owned sentinel'}
        Reject { Get-FBRTree $retentionRoot $canary } '*ProtectedAncestor*';Check (Test-Path -LiteralPath $markerPath) 'Operational ancestor sentinel was modified.'
    }
}
$treeMarker=NewRetentionTree 'retention-tree-marker/build/obj' 500 'OwnedCompiler/release/.keys.lock'
Reject { Get-FBRTree $retentionRoot $treeMarker } '*ProtectedContent*'
foreach($process in @([pscustomobject]@{ProcessId=111;ExecutablePath=$null;CommandLine=$null},[pscustomobject]@{ProcessId=112;ExecutablePath='owned.exe';CommandLine=$null},[pscustomobject]@{ProcessId=113;ExecutablePath=$null;CommandLine='owned command'})) {
    Reject { Assert-FBRProcessVisibility $process } '*ProcessInspectionUnavailable*'
}
Assert-FBRProcessVisibility ([pscustomobject]@{ProcessId=0;ExecutablePath=$null;CommandLine=$null});Assert-FBRProcessVisibility ([pscustomobject]@{ProcessId=4;ExecutablePath=$null;CommandLine=$null})
$relativeActive=[IO.Path]::GetRelativePath($retentionRoot,(Split-Path $active -Parent))
foreach($argument in @((Split-Path $active -Parent),$relativeActive,('./'+$relativeActive.Replace('\','/')))) {
    $records=@([pscustomobject]@{ProcessId=200;ExecutablePath='C:\owned\dotnet.exe';CommandLine=('dotnet build --artifacts-path "'+$argument+'"')})
    Check (Test-FBRProcessReference $records (Split-Path $active -Parent) $retentionRoot) 'Absolute/relative active build reference was missed.'
}
$processDefinition=${function:Get-FBRProcesses}
try { function Get-FBRProcesses { throw 'BuildRetentionProcessInspectionUnavailable' };Reject { Invoke-FoundationBuildRetention @request -Apply } '*BuildRetentionProcessInspectionUnavailable*' }
finally { Set-Item -LiteralPath Function:\Get-FBRProcesses -Value $processDefinition }
Check (Test-Path -LiteralPath $old) 'Unavailable process inspection mutated a candidate.'
$nativeVisibility='Available'
try { $nativeRecords=@(Get-FBRProcesses);Check ($nativeRecords.Count -gt 0) 'Native process inventory was empty.' }
catch { if($_.Exception.Message -cne 'BuildRetentionProcessInspectionUnavailable'){throw};$nativeVisibility='Unavailable';Check (Test-Path -LiteralPath $old) 'Opaque native process visibility changed an intermediate.' }
$child=$null;$oldMode=$env:FOUNDATION_TEST_EXECUTION_FIXTURE
try {
    $env:FOUNDATION_TEST_EXECUTION_FIXTURE='native-stall'
    $start=[Diagnostics.ProcessStartInfo]::new($standIn);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.WorkingDirectory=$retentionRoot
    foreach($arg in @('test','--artifacts-path',$relativeActive)) { $start.ArgumentList.Add($arg) }
    $child=[Diagnostics.Process]::Start($start)
    $deadline=[DateTime]::UtcNow.AddSeconds(10)
    do { $seen=@(Get-CimInstance -ClassName Win32_Process -Filter ('ProcessId='+$child.Id) -ErrorAction Stop);if($seen.Count -eq 1 -and $seen[0].CommandLine -and $seen[0].ExecutablePath){break};Start-Sleep -Milliseconds 50 } while([DateTime]::UtcNow -lt $deadline)
    Check ($seen.Count -eq 1 -and $seen[0].ExecutablePath -and $seen[0].CommandLine) 'Owned active child lacked complete CIM metadata.'
    $script:retentionVisibleOwnedProcess=$seen[0]
    # TEST ONLY: an actual owned child subset qualifies deletion mechanics, not global native visibility.
    function Get-FBRProcesses { @($script:retentionVisibleOwnedProcess) }
    $previewResult=Invoke-FoundationBuildRetention @request
    Check ($previewResult.removedCount -eq 0 -and (Test-Path -LiteralPath $old)) 'Retention preview deleted files.'
    $whatIf=Invoke-FoundationBuildRetention @request -Apply -WhatIf
    Check ($whatIf.removedCount -eq 0 -and (Test-Path -LiteralPath $old)) 'Retention WhatIf deleted files.'
    $retained=Invoke-FoundationBuildRetention @request -Apply
    Check ($retained.removedCount -eq 2 -and !(Test-Path -LiteralPath $old) -and !(Test-Path -LiteralPath $nested)) 'Recognized old ordinary/GUID obj trees were not removed.'
    foreach($path in @($newest,$second,$explicit,$database,$key,$package,$release,$preview,$active,$unrelatedActivation,$unrelatedBackup,$unknown,$unsupported,$unrecognized,$treeMarker,$junction,$target,$bin,(Join-Path $valid 'completed.json'),(Join-Path $valid 'results/foundation.trx'))) { Check (Test-Path -LiteralPath $path) ('Retention changed protected/current/evidence path: '+$path) }
    Check (@($retained.decisions | Where-Object { $_.path -ieq $active -and $_.reason -ceq 'ProcessReference' }).Count -eq 1) 'Owned relative-path active build was not explicitly retained.'
    Check (!$retained.continuousProcessFencing -and !$retained.physicalSpaceReclaimedVerified -and $retained.revisionHistory -ceq 'GitHub') 'Cleanup overstated its process/space/version boundary.'
    $partial=NewRetentionTree 'retention-partial/build/obj' 600
    function Remove-Item { param($LiteralPath,[switch]$Recurse,[switch]$Force,$ErrorAction)
        if($LiteralPath -ine $script:retentionPartialTarget){throw 'Unexpected synthetic deletion target'}
        [IO.File]::Delete((Join-Path $LiteralPath 'OwnedCompiler/release/OwnedCompiler.dll'));throw 'Synthetic partial removal'
    }
    $script:retentionPartialTarget=$partial
    try {
        $uncertain=Invoke-FoundationBuildRetention @request -Apply
        Check ($uncertain.removalUncertainCount -eq 1 -and $uncertain.removedCount -eq 0 -and $uncertain.removedBytes -eq 0) 'Partial deletion was falsely counted as preserved/reclaimed.'
        Check ((Test-Path -LiteralPath $partial) -and !(Test-Path -LiteralPath (Join-Path $partial 'OwnedCompiler/release/OwnedCompiler.dll'))) 'Partial-removal fixture did not actually remove one owned intermediate.'
    } finally { Microsoft.PowerShell.Management\Remove-Item -LiteralPath Function:\Remove-Item -Force }
} finally {
    Set-Item -LiteralPath Function:\Get-FBRProcesses -Value $processDefinition
    $env:FOUNDATION_TEST_EXECUTION_FIXTURE=$oldMode
    if($child) { try { if(!$child.HasExited){$child.Kill();$null=$child.WaitForExit(10000)} } finally { $child.Dispose() } }
}
$cleanupFile=Join-Path $valid 'build-cleanup.json';Check (Test-Path -LiteralPath $cleanupFile) 'Successful test execution did not save its independent cleanup observation.'
$cleanupObservation=Get-Content -LiteralPath $cleanupFile -Raw|ConvertFrom-Json -AsHashtable
Check ($cleanupObservation.state -cin @('Skipped','Completed')) 'Cleanup observation was not a bounded independent outcome.'
$buildRetentionObservation=[ordered]@{nativeProcessInventory=$nativeVisibility;deletionFixtureProcessInventory='TestOnlyActualOwnedChildSubset';actualRepositoryCleanupPerformed=$false;syntheticObjTreesRemoved=2;partialRemovalQualified=$true;continuousProcessFencing=$false;physicalSpaceReclaimedVerified=$false}

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
$summary=[ordered]@{schema='platform-studio.foundation-test-execution-fixtures.v1';passed=$true;checks=$script:checks;buildRetention=$buildRetentionObservation;runs=$runs.ToArray();actualFoundationSuiteExecuted=$false;constantOwnedStandInExecuted=$true;restorePerformed=$false;noNetworkAttested=$false;independentAttestation=$false;productionReady=$false;cloudOperationsPerformed=$false}
$null=Write-FTEJson (Join-Path $fixtureRoot 'summary.json') $summary
Write-Host "Foundation execution evidence: $script:checks checks passed. $fixtureRoot"
