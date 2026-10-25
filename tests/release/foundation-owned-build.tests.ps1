#requires -Version 7.4
[CmdletBinding()]
param([string]$DotNetPath='C:\IT\Package\dotnet\dotnet.exe',[string]$SdkPath='C:\IT\Package\dotnet\sdk\10.0.300')
$ErrorActionPreference='Stop'
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repositoryRoot 'scripts/FoundationBuildRetention.ps1')
$parent=Join-Path $repositoryRoot 'artifacts/foundation-owned-build-tests'
$null=New-Item -ItemType Directory -Path $parent -Force
$fixtureRoot=New-FTEPrivateDirectory (Join-Path $parent ([Guid]::NewGuid().ToString('N')))
$script:checks=0
function Check([bool]$Condition,[string]$Message){if(!$Condition){throw $Message};$script:checks++}
function Reject([scriptblock]$Action,[string]$Reason='*'){$caught=$false;try{&$Action|Out-Null}catch{$caught=$true;if($_.Exception.GetBaseException().Message -notlike $Reason){throw}};Check $caught ('Expected refusal '+$Reason)}
function WriteFixture([string]$Path,[string]$Text){$null=New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force;[IO.File]::WriteAllText($Path,$Text,[Text.UTF8Encoding]::new($false))}
$tools=New-FTEPrivateDirectory (Join-Path $fixtureRoot 'tools');$source=Join-Path $tools 'Fixture.cs';$assembly=Join-Path $tools 'fixture.dll';$standIn=Join-Path $tools 'dotnet.exe'
WriteFixture $source @'
using System;using System.Diagnostics;using System.IO;using System.Threading;using System.Xml.Linq;
public static class Fixture {
 public static int Main(string[] args){
  var root=Environment.CurrentDirectory;var mode=args[0];
  if(mode=="child"){File.WriteAllText(args[1],Environment.ProcessId+"|"+Process.GetCurrentProcess().StartTime.ToUniversalTime().Ticks);Thread.Sleep(args.Length>2?int.Parse(args[2]):Timeout.Infinite);return 0;}
  if(mode=="spawn"||mode=="delay"){var p=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};p.ArgumentList.Add("child");p.ArgumentList.Add(args[1]);if(mode=="delay")p.ArgumentList.Add("300");using var c=Process.Start(p);return 0;}
  if(mode=="failure")return 7;
  if(mode=="overflow"){Console.Write(new string('x',1048576));Console.Out.Flush();return 0;}
  if(mode=="success"){Console.Write("owned-success");return 0;}
  if(!File.Exists(Path.Combine(root,"fixture.marker")))return 91;
  if(mode=="build"){
   var cli=Environment.GetEnvironmentVariable("DOTNET_CLI_HOME");
   if(string.IsNullOrEmpty(cli)||Environment.GetEnvironmentVariable("USERPROFILE")!=cli||Environment.GetEnvironmentVariable("APPDATA")!=Path.Combine(cli,"appdata")||Environment.GetEnvironmentVariable("LOCALAPPDATA")!=Path.Combine(cli,"local-appdata")||Environment.GetEnvironmentVariable("TEMP")!=Path.Combine(cli,"temp")||Environment.GetEnvironmentVariable("TMP")!=Path.Combine(cli,"temp")||string.IsNullOrEmpty(Environment.GetEnvironmentVariable("ProgramFiles"))||Environment.GetEnvironmentVariable("FOUNDATION_OWNED_FIXTURE_SECRET")!=null)return 94;
   mode=File.ReadAllText(Path.Combine(root,"fixture-mode.txt"));if(mode=="failure"){Console.Write("private fixture failure");Console.Error.Write("bounded fixture diagnostic");return 7;}
   var directory=args[Array.IndexOf(args,"--artifacts-path")+1];var project=Path.Combine(directory,"obj","SelfService.Foundation");Directory.CreateDirectory(Path.Combine(project,"release"));
   foreach(var n in new[]{"project.assets.json","project.nuget.cache","SelfService.Foundation.csproj.nuget.dgspec.json","SelfService.Foundation.csproj.nuget.g.props","SelfService.Foundation.csproj.nuget.g.targets"})File.WriteAllText(Path.Combine(project,n),"owned SDK metadata stand-in");
   foreach(var n in new[]{"SelfService.Foundation.dll","SelfService.Foundation.pdb","SelfService.Foundation.assets.cache"})File.WriteAllText(Path.Combine(project,"release",n),"owned SDK bytes stand-in");
   Directory.CreateDirectory(Path.Combine(directory,"bin"));File.WriteAllText(Path.Combine(directory,"bin","keep.dll"),"retained binary");
   if(mode=="change")File.AppendAllText(Path.Combine(root,"input.txt"),"changed during build");Console.Write("owned-build");return 0;
  }
  if(mode=="restore")return 0;if(mode!="test")return 92;
  var results=args[Array.IndexOf(args,"--results-directory")+1];Directory.CreateDirectory(results);var now=DateTimeOffset.UtcNow;var id=Guid.NewGuid().ToString("D");var execution=Guid.NewGuid().ToString("D");XNamespace ns="http://microsoft.com/schemas/VisualStudio/TeamTest/2010";
  new XDocument(new XElement(ns+"TestRun",new XAttribute("id",Guid.NewGuid().ToString("D")),new XElement(ns+"Times",new XAttribute("start",now.ToString("O")),new XAttribute("finish",now.ToString("O"))),
   new XElement(ns+"Results",new XElement(ns+"UnitTestResult",new XAttribute("testId",id),new XAttribute("executionId",execution),new XAttribute("outcome","Passed"))),
   new XElement(ns+"TestDefinitions",new XElement(ns+"UnitTest",new XAttribute("id",id))),new XElement(ns+"TestEntries",new XElement(ns+"TestEntry",new XAttribute("testId",id),new XAttribute("executionId",execution))),
   new XElement(ns+"ResultSummary",new XAttribute("outcome","Completed"),new XElement(ns+"Counters",new XAttribute("total",1),new XAttribute("executed",1),new XAttribute("passed",1),new XAttribute("failed",0),new XAttribute("notExecuted",0))))).Save(Path.Combine(results,"foundation.trx"));return 0;
 }
}
'@
$dotnetRoot=Split-Path $DotNetPath -Parent;$references=Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref/10.0.8/ref/net10.0'
$arguments=@('exec',(Join-Path $SdkPath 'Roslyn/bincore/csc.dll'),'/noconfig','/nostdlib+','/target:exe',('/out:'+$assembly),$source)+@(Get-ChildItem -LiteralPath $references -Filter '*.dll' -File|ForEach-Object {'/reference:'+$_.FullName})
$compiled=Invoke-FTEProcess $DotNetPath $arguments $tools 30000
Check ($compiled.exitCode -eq 0) ('Constant fixture failed compilation '+$compiled.stdout+$compiled.stderr)
WriteFixture (Join-Path $tools 'fixture.runtimeconfig.json') '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}'
[void][Reflection.Assembly]::LoadFrom((Join-Path $SdkPath 'Microsoft.NET.HostModel.dll'))
$architecture=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant();$template=Join-Path $dotnetRoot ('packs/Microsoft.NETCore.App.Host.win-'+$architecture+'/10.0.8/runtimes/win-'+$architecture+'/native/apphost.exe')
$search=[Microsoft.NET.HostModel.AppHost.HostWriter+DotNetSearchOptions]::new();$search.Location=[Microsoft.NET.HostModel.AppHost.HostWriter+DotNetSearchOptions+SearchLocation]::AppRelative;$search.AppRelativeDotNet=[IO.Path]::GetRelativePath($tools,$dotnetRoot)
[Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost($template,$standIn,'fixture.dll',$false,$assembly,$false,$false,$search)
$null=Initialize-FBROwnedNative
$environment=@{SystemRoot=$env:SystemRoot;WINDIR=$env:WINDIR;DOTNET_ROOT=$dotnetRoot;PATH=($dotnetRoot+';'+(Join-Path $env:SystemRoot 'System32'))}
foreach($mode in @('success','failure','overflow','delay')){
 $session=$null;$marker=Join-Path $tools ($mode+'.pid')
 try{
  $session=[PlatformStudio.FoundationOwnedBuild.Native]::Start($standIn,[string[]]@($mode,$marker),$tools,$environment,4096)
  if($mode -eq 'failure'){Reject {$session.Wait(15000,[Threading.CancellationToken]::None)} '*ExitNonzero*'}
  elseif($mode -eq 'overflow'){Reject {$session.Wait(15000,[Threading.CancellationToken]::None)} '*OutputLimit*';Check ($session.StandardOutput().Length -le 4096) 'Output capture exceeded cap'}
  else{$o=$session.Wait(15000,[Threading.CancellationToken]::None);Check ($o.ExitCode -eq 0 -and $o.ActiveProcesses -eq 0 -and $o.ProcessStartTicks -gt 0 -and $o.Executable -ieq $standIn) 'Job completion identity was incomplete';if($mode -eq 'delay'){Check ($o.TotalProcesses -ge 2 -and (Test-Path $marker)) 'Descendant was not observed before completion'}}
 }finally{if($session){$session.Dispose()}}
}
foreach($cancel in @($false,$true)){
 $session=$null;$marker=Join-Path $tools ('long-'+$cancel+'.pid');$cts=[Threading.CancellationTokenSource]::new()
 try{
  $session=[PlatformStudio.FoundationOwnedBuild.Native]::Start($standIn,[string[]]@('spawn',$marker),$tools,$environment)
  $clock=[Diagnostics.Stopwatch]::StartNew();while(!(Test-Path $marker) -and $clock.ElapsedMilliseconds -lt 15000){Start-Sleep -Milliseconds 10}
  Check (Test-Path $marker) 'Owned descendant did not enter';$identity=[IO.File]::ReadAllText($marker).Split('|')
  $child=[Diagnostics.Process]::GetProcessById([int]$identity[0]);try{Check ($child.StartTime.ToUniversalTime().Ticks -eq [long]$identity[1]) 'Owned descendant identity drifted'}finally{$child.Dispose()}
  if($cancel){$cts.CancelAfter(150);Reject {$session.Wait(15000,$cts.Token)} '*cancel*'}else{Reject {$session.Wait(150,[Threading.CancellationToken]::None)} '*Timeout*'}
 }finally{if($session){$session.Dispose()};$cts.Dispose()}
 $child=Get-Process -Id ([int]$identity[0]) -ErrorAction SilentlyContinue
 try{Check ($null -eq $child -or $child.HasExited -or $child.StartTime.ToUniversalTime().Ticks -ne [long]$identity[1]) 'Owned job left a running descendant'}finally{if($child){$child.Dispose()}}
}
# Handle-held deletion has no recursive/path fallback; unknown new entries and borrowed files are preserved.
$leaseRoot=New-FTEPrivateDirectory (Join-Path $fixtureRoot 'leases')
$locked=New-FTEPrivateDirectory (Join-Path $leaseRoot 'locked');WriteFixture (Join-Path $locked 'owned.dll') 'owned bytes'
$borrowed=[IO.FileStream]::new((Join-Path $locked 'owned.dll'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
try{Reject {[PlatformStudio.FoundationOwnedBuild.Native]::AcquireFiles($locked)} '*FileLeaseRejected*';Check ([IO.File]::ReadAllText((Join-Path $locked 'owned.dll')) -eq 'owned bytes') 'Borrowed file mutated'}finally{$borrowed.Dispose()}
$lease=[PlatformStudio.FoundationOwnedBuild.Native]::AcquireFiles($locked)
try{Reject {[IO.File]::WriteAllText((Join-Path $locked 'owned.dll'),'changed')};$lease.Delete()}finally{$lease.Dispose()}
Check (!(Test-Path $locked)) 'Exact leased tree not removed'
$changed=New-FTEPrivateDirectory (Join-Path $leaseRoot 'changed');WriteFixture (Join-Path $changed 'owned.dll') 'owned bytes';$lease=[PlatformStudio.FoundationOwnedBuild.Native]::AcquireFiles($changed)
try{WriteFixture (Join-Path $changed 'unexpected.txt') 'preservation canary';Reject {$lease.Delete()} '*TreeChanged*';Check (Test-Path (Join-Path $changed 'unexpected.txt')) 'Unexpected new entry was removed'}finally{$lease.Dispose()}
$linked=New-FTEPrivateDirectory (Join-Path $leaseRoot 'linked');$null=New-Item -ItemType Junction -Path (Join-Path $linked 'junction') -Target $changed
Reject {[PlatformStudio.FoundationOwnedBuild.Native]::AcquireFiles($linked)} '*FileLeaseRejected*'
Check (Test-Path (Join-Path $changed 'unexpected.txt')) 'Linked target mutated'

$root=New-FTEPrivateDirectory (Join-Path $fixtureRoot 'repository')
foreach($pair in @(@('.gitignore',"artifacts/`n"),@('fixture.marker','owned-build-fixture'),@('fixture-mode.txt','success'),@('input.txt','source'),@('MANIFEST.sha256','synthetic manifest'),@('src/SelfService.Foundation/SelfService.Foundation.csproj','<Project/>'),@('tests/SelfService.Foundation.Tests/SelfService.Foundation.Tests.csproj','<Project/>'))){WriteFixture (Join-Path $root $pair[0]) $pair[1]}
foreach($name in @('FoundationTestExecutionEvidence.ps1','FoundationBuildRetention.ps1','FoundationOwnedBuildNative.cs','Build-FoundationOwned.ps1','Test-Foundation.ps1')){WriteFixture (Join-Path $root ('scripts/'+$name)) ([IO.File]::ReadAllText((Join-Path $repositoryRoot ('scripts/'+$name))))}
foreach($args in @(@('init','--quiet'),@('config','core.autocrlf','false'),@('config','user.email','synthetic@example.invalid'),@('config','user.name','Synthetic owned build fixture'),@('add','--all'),@('commit','--quiet','-m','Owned synthetic fixture'))){$null=Get-FTEGit $root $args}
$built=[Collections.Generic.List[object]]::new();$priorSecret=$env:FOUNDATION_OWNED_FIXTURE_SECRET
try{$env:FOUNDATION_OWNED_FIXTURE_SECRET='synthetic secret must not reach builder'
 for($i=0;$i -lt 3;$i++){$built.Add((& (Join-Path $root 'scripts/Build-FoundationOwned.ps1') -DotNetPath $standIn));Start-Sleep -Milliseconds 25}
}finally{$env:FOUNDATION_OWNED_FIXTURE_SECRET=$priorSecret}
Check ($built.Count -eq 3 -and @($built|Where-Object state -CEQ 'Built').Count -eq 3) 'Fixed launcher did not produce three native fenced completions'
foreach($item in $built){$l=Open-FBROwnedCompletion $root $item.generationDirectory;try{Check ($l.completedSha256 -ceq $item.completionSha256) 'Completion pin drifted';Reject {Open-FBROwnedCompletion $root $item.generationDirectory}}finally{$l.guard.Dispose()}}
foreach($directoryMarker in @($false,$true)){
 $ownedParent=Join-Path $root 'artifacts/foundation-owned-builds';$sentinel=Join-Path $ownedParent '.vault.lock'
 if($directoryMarker){$null=New-Item -ItemType Directory -Path $sentinel}else{WriteFixture $sentinel 'operational namespace canary'}
 $before=@(Get-ChildItem -LiteralPath $ownedParent -Force|ForEach-Object {$_.Name}) -join '|';$acl=(Get-Acl -LiteralPath $sentinel).Sddl;$caught=$false
 try{try{& (Join-Path $root 'scripts/Build-FoundationOwned.ps1') -DotNetPath $standIn|Out-Null}catch{$caught=$_.Exception.Message -ceq 'OwnedBuildProtectedAncestor'}
  Check ($caught -and $before -ceq (@(Get-ChildItem -LiteralPath $ownedParent -Force|ForEach-Object {$_.Name}) -join '|') -and $acl -ceq (Get-Acl -LiteralPath $sentinel).Sddl -and ($directoryMarker -or [IO.File]::ReadAllText($sentinel) -ceq 'operational namespace canary')) 'Namespace marker refusal created a generation or changed protected bytes/ACL'
 }finally{if($directoryMarker){[IO.Directory]::Delete($sentinel)}else{[IO.File]::Delete($sentinel)}}
}
foreach($mode in @('failure','change')){
 WriteFixture (Join-Path $root 'fixture-mode.txt') $mode
 Reject {& (Join-Path $root 'scripts/Build-FoundationOwned.ps1') -DotNetPath $standIn} $(if($mode -eq 'failure'){'*ExitNonzero*'}else{'*SourceChanged*'})
 $rejected=@(Get-ChildItem (Join-Path $root 'artifacts/foundation-owned-builds') -Directory|Where-Object {Test-Path (Join-Path $_.FullName 'rejected.json')})
 Check ($rejected.Count -ge 1 -and @($rejected|Where-Object {Test-Path (Join-Path $_.FullName 'completed.json')}).Count -eq 0) 'Failed/source-mutated build gained completed authority'
 if($mode -eq 'failure'){
  $failed=($rejected|Sort-Object CreationTimeUtc|Select-Object -Last 1).FullName
  Check ([IO.File]::ReadAllText((Join-Path $failed 'stdout.txt')) -ceq 'private fixture failure' -and [IO.File]::ReadAllText((Join-Path $failed 'stderr.txt')) -ceq 'bounded fixture diagnostic') 'Failure diagnostics were lost or unbounded'
  $null=Assert-FBROwnedPrivate (Join-Path $failed 'stdout.txt');$null=Assert-FBROwnedPrivate (Join-Path $failed 'stderr.txt')
 }
}
WriteFixture (Join-Path $root 'fixture-mode.txt') 'success'
# Historical owned builds need their own matching source pin, not today's bytes. Fresh full test must match today's source.
WriteFixture (Join-Path $root 'input.txt') 'new current source after historical owned builds'
$full=& (Join-Path $root 'scripts/Test-Foundation.ps1') -DotNetPath $standIn -SkipBuildCleanup
$request=@{Root=$root;RunDirectory=$full.runDirectory;CompletedSha256=$full.receiptSha256}
$legacy=Join-Path $root 'artifacts/legacy-check/build/obj';$null=New-Item -ItemType Directory -Path $legacy -Force
# No synthetic process-inventory override: the prospective protocol does not need global visibility.
$preview=Invoke-FoundationBuildRetention @request
Check ($preview.ownedCompletedBuilds -eq 3 -and @($preview.decisions|Where-Object state -CEQ 'Eligible').Count -eq 1) 'Newest-two owned retention selection failed'
$oldest=$built[0];$stdout=Join-Path $oldest.generationDirectory 'stdout.txt';$original=[IO.File]::ReadAllBytes($stdout)
try{[IO.File]::AppendAllText($stdout,'tampered');Reject {Open-FBROwnedCompletion $root $oldest.generationDirectory} '*OutputBindingInvalid*'}finally{[IO.File]::WriteAllBytes($stdout,$original)}
$unknown=Join-Path $oldest.generationDirectory 'unexpected.json';WriteFixture $unknown '{}'
try{Reject {Open-FBROwnedCompletion $root $oldest.generationDirectory} '*IncompleteOrUnknown*'}finally{[IO.File]::Delete($unknown)}
$applied=Invoke-FoundationBuildRetention @request -Apply -Confirm:$false
Check ($applied.removedCount -eq 1 -and $applied.removalUncertainCount -eq 0) 'Proven-quiescent owned intermediate was not removed'
Check (!(Test-Path (Join-Path $oldest.buildDirectory 'obj')) -and (Test-Path (Join-Path $oldest.buildDirectory 'bin/keep.dll'))) 'Removal crossed obj boundary'
foreach($item in @($built|Select-Object -Skip 1)){Check (Test-Path (Join-Path $item.buildDirectory 'obj')) 'A newest-two build was removed'}
Check ((Test-Path $legacy) -and (Test-Path (Join-Path $oldest.generationDirectory 'completed.json')) -and !$applied.physicalSpaceReclaimedVerified -and !$applied.continuousProcessFencing) 'Legacy/evidence or assurance boundary changed'
$summary=[ordered]@{schema='platform-studio.foundation-owned-build-fixtures.v1';passed=$true;checks=$script:checks;nativeJobObjectExecuted=$true;ownedDescendantTerminationVerified=$true;nativeHandleRemovalExecuted=$true;syntheticFixedProjectBuilds=3;syntheticRemoval=$applied;actualFoundationBuildExecuted=$false;syntheticProcessInventory=$false;independentAttestation=$false;productionReady=$false;cloudOperationsPerformed=$false}
$null=Write-FTEJson (Join-Path $fixtureRoot 'summary.json') $summary
Write-Host "Foundation owned build fixtures: $script:checks passed. $fixtureRoot"
