#requires -Version 7.4
[CmdletBinding()]
param([string]$DotNetPath='C:\IT\Package\dotnet\dotnet.exe',[string]$SdkPath='C:\IT\Package\dotnet\sdk\10.0.300')
$ErrorActionPreference='Stop';Set-StrictMode -Version Latest
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
. (Join-Path $repositoryRoot 'scripts/FoundationPublishRetention.ps1')
foreach($file in @('scripts/Publish-Foundation.ps1','tests/release/foundation-package.tests.ps1')){
    $tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repositoryRoot $file),[ref]$tokens,[ref]$errors)
    if($errors){throw 'Fixture helper parse failed'}
    foreach($function in $ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)){
        if($file -like 'scripts/*' -or $function.Name -in @('New-PackageFixture','Write-PackageDependencyFixture')){. ([scriptblock]::Create($function.Extent.Text))}
    }
}
$base=Join-Path $repositoryRoot 'artifacts/foundation-publish-retention-tests';$null=New-Item -ItemType Directory -Path $base -Force
$fixtureRoot=New-FTEPrivateDirectory (Join-Path $base ([Guid]::NewGuid().ToString('N')));$script:checks=0
function Check([bool]$Condition,[string]$Message){if(!$Condition){throw $Message};$script:checks++}
function Reject([scriptblock]$Action,[string]$Expected){$caught=$false;try{&$Action|Out-Null}catch{$caught=$true;if($_.Exception.GetBaseException().Message -notlike $Expected){throw}};Check $caught ('Expected refusal '+$Expected)}
function WriteFixture([string]$Path,[string]$Text){$null=New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force;[IO.File]::WriteAllText($Path,$Text,[Text.UTF8Encoding]::new($false))}
$tools=New-FTEPrivateDirectory (Join-Path $fixtureRoot 'tools');$source=Join-Path $tools 'fixture.cs';$assembly=Join-Path $tools 'fixture.dll';$standIn=Join-Path $tools 'dotnet.exe'
WriteFixture $source @'
using System;using System.IO;using System.Diagnostics;using System.Threading;
public static class Fixture {
 public static int Main(string[] a){
  if(a[0]=="child"){Thread.Sleep(250);return 0;}
  if(a[0]!="publish")return 9;
  var mode=File.ReadAllText("fixture-mode.txt");if(mode=="fail"){Console.Error.Write("bounded synthetic failure");return 7;}
  var stage=a[Array.IndexOf(a,"--output")+1];var build=a[Array.IndexOf(a,"--artifacts-path")+1];
  foreach(var p in new[]{stage,Path.Combine(build,"bin","fixture"),Path.Combine(build,"obj","fixture")}){Directory.CreateDirectory(p);File.WriteAllText(Path.Combine(p,"temporary.dll"),"synthetic temporary bytes");}
  if(mode=="descendant"){var p=new ProcessStartInfo(Environment.ProcessPath!){UseShellExecute=false,CreateNoWindow=true};p.ArgumentList.Add("child");using var child=Process.Start(p);}
  Console.Write("synthetic contained publish");return 0;
 }
}
'@
$dotnetRoot=Split-Path $DotNetPath -Parent;$references=Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref/10.0.8/ref/net10.0'
$arguments=@('exec',(Join-Path $SdkPath 'Roslyn/bincore/csc.dll'),'/noconfig','/nostdlib+','/target:exe',('/out:'+$assembly),$source)+@(Get-ChildItem -LiteralPath $references -Filter '*.dll' -File|ForEach-Object {'/reference:'+$_.FullName})
$compiled=Invoke-FTEProcess $DotNetPath $arguments $tools 30000
Check ($compiled.exitCode -eq 0) ('Synthetic child compilation failed '+$compiled.stdout+$compiled.stderr)
WriteFixture (Join-Path $tools 'fixture.runtimeconfig.json') '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}'
[void][Reflection.Assembly]::LoadFrom((Join-Path $SdkPath 'Microsoft.NET.HostModel.dll'))
$architecture=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant();$template=Join-Path $dotnetRoot ('packs/Microsoft.NETCore.App.Host.win-'+$architecture+'/10.0.8/runtimes/win-'+$architecture+'/native/apphost.exe')
$search=[Microsoft.NET.HostModel.AppHost.HostWriter+DotNetSearchOptions]::new();$search.Location=[Microsoft.NET.HostModel.AppHost.HostWriter+DotNetSearchOptions+SearchLocation]::AppRelative;$search.AppRelativeDotNet=[IO.Path]::GetRelativePath($tools,$dotnetRoot)
[Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost($template,$standIn,'fixture.dll',$false,$assembly,$false,$false,$search)

function NewCompletedFixture([switch]$Incomplete,[string]$Mode='success'){
    $root=New-FTEPrivateDirectory (Join-Path $fixtureRoot ([Guid]::NewGuid().ToString('N')))
    $null=New-FTEPrivateDirectory (Join-Path $root 'artifacts');$null=New-FTEPrivateDirectory (Join-Path $root 'artifacts/foundation-packages')
    WriteFixture (Join-Path $root 'fixture-mode.txt') $Mode
    $selection=Resolve-FoundationPackageDestination $root;$context=New-FoundationPublishContext $root $selection
    try{
        WriteFixture (Join-Path $context.destination '.restore/NuGet.Config') '<configuration/>'
        if(!$Incomplete){foreach($stage in @('host','worker','owner')){Invoke-FoundationContainedPublish $context $stage $standIn}}
        $packageFixture=New-PackageFixture ([Guid]::NewGuid().ToString('N'))
        Copy-Item -LiteralPath $packageFixture.Package -Destination (Join-Path $context.destination 'package') -Recurse
        $archive=New-FoundationPackageArchive (Join-Path $context.destination 'package') (Join-Path $context.destination 'PlatformStudio.Foundation.LocalPreview.zip')
        $receipt=Write-FTEJson (Join-Path $context.destination 'receipt.json') ([ordered]@{schema='platform-studio.foundation-package-receipt.v1';localPreview=$true;productionReady=$false;archive=$archive})
        [pscustomobject]@{context=$context;receipt=$receipt;archive=$archive}
    }catch{Close-FoundationPublishContext $context;throw}
}
function RetainedHash($Fixture){
    $pins=Open-FoundationPublishRetainedPins $Fixture.context
    try{Get-FTEHash ([Text.Encoding]::UTF8.GetBytes(((@($pins | Where-Object {$_.path -cne (Join-Path $Fixture.context.destination 'temporary-cleanup.json')} | Sort-Object path | ForEach-Object {$_.path+'|'+$_.sha256})) -join "`n")))}finally{foreach($pin in $pins){$pin.handle.Dispose()}}
}
function Clean($Fixture){Remove-FoundationPublishedTemporaries $Fixture.context $Fixture.receipt.sha256}
Check (!(Test-FoundationPublishSpaceBudget 0)) 'Zero capacity was admitted'
Check (!(Test-FoundationPublishSpaceBudget 2147483647)) 'Below-budget capacity was admitted'
Check (Test-FoundationPublishSpaceBudget 2147483648) 'Exact budget was refused'
$good=NewCompletedFixture -Mode descendant
try{
    $before=RetainedHash $good;$result=Clean $good
    Check ($result.state -ceq 'Completed' -and $result.removedCount -eq 5 -and $result.removalUncertainCount -eq 0 -and $result.retainedPinsVerified) ('Owned quiescent temporary cleanup did not complete '+($result|ConvertTo-Json -Depth 6 -Compress))
    Check ($before -ceq (RetainedHash $good)) 'Successful cleanup changed retained bytes'
    Check (!$result.physicalSpaceReclaimedVerified -and !$result.packageVerdictChanged) 'Cleanup overstated physical space or package verdict'
    foreach($name in @('.cli','.restore','.publish-logs','package','receipt.json','.package-owner.json','PlatformStudio.Foundation.LocalPreview.zip')){Check ([IO.Path]::Exists((Join-Path $good.context.destination $name))) ('Required retained entry removed '+$name)}
    foreach($stage in @('host','worker','owner')){$o=Get-Content (Join-Path $good.context.destination ('.publish-logs/'+$stage+'.json')) -Raw|ConvertFrom-Json;Check ($o.completed -and $o.observation.ActiveProcesses -eq 0 -and $o.observation.TotalProcesses -ge 2) 'Actual descendant quiescence missing'}
}finally{Close-FoundationPublishContext $good.context}
foreach($fault in @('receipt','archive','marker','locked-temp','locked-retained','protected','hardlink','junction','foreign-acl','deep-retained','unknown-after-plan')){
    $f=NewCompletedFixture;$blocked=$null;$originalTreeFunction=$null
    try{
        $before=RetainedHash $f
        switch($fault){
            'receipt' {[IO.File]::AppendAllText((Join-Path $f.context.destination 'receipt.json'),' ')}
            'archive' {[IO.File]::AppendAllText($f.archive.Archive,'changed archive')}
            'marker' {[IO.File]::AppendAllText((Join-Path $f.context.destination '.package-owner.json'),' ')}
            'locked-temp' {$blocked=[IO.FileStream]::new((Join-Path $f.context.destination '.publish-stage/temporary.dll'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)}
            'locked-retained' {$blocked=[IO.FileStream]::new((Join-Path $f.context.destination 'receipt.json'),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::None)}
            'protected' {WriteFixture (Join-Path $f.context.destination '.publish-stage/state.json') 'protected marker'}
            'hardlink' {$null=New-Item -ItemType HardLink -Path (Join-Path $f.context.destination '.publish-stage/borrowed.dll') -Target (Join-Path $f.context.destination 'package/runtime/SelfService.Foundation.Host.dll')}
            'junction' {$null=New-Item -ItemType Junction -Path (Join-Path $f.context.destination '.publish-stage/borrowed') -Target $tools}
            'foreign-acl' {$path=Join-Path $f.context.destination '.publish-stage/temporary.dll';$acl=Get-Acl $path;$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-1-0'),[Security.AccessControl.FileSystemRights]::Read,[Security.AccessControl.AccessControlType]::Allow));Set-Acl $path $acl}
            'deep-retained' {$path=Join-Path $f.context.destination 'retained';for($i=0;$i -lt 66;$i++){$path=Join-Path $path 'd'};$null=New-Item -ItemType Directory -Path $path -Force}
            'unknown-after-plan' {
                $script:injected=$false;$originalTreeFunction=(Get-Item Function:Get-FoundationPublishTemporaryTree).ScriptBlock
                $script:treeOriginal=$originalTreeFunction
                Set-Item Function:Get-FoundationPublishTemporaryTree -Value {param($Path,[switch]$MetadataOnly);$tree=&$script:treeOriginal $Path -MetadataOnly:$MetadataOnly;if(!$script:injected -and !$MetadataOnly){$script:injected=$true;[IO.File]::WriteAllText((Join-Path $Path 'new-child.dll'),'arrived after planned snapshot')};$tree}
            }
        }
        $result=Clean $f
        Check ($result.state -ceq 'NeedsAttention' -and $result.removedCount -eq 0 -and !$result.retainedPinsVerified) ('Unsafe cleanup was admitted '+$fault)
        Check ([IO.Path]::Exists((Join-Path $f.context.destination '.publish-stage/temporary.dll'))) ('Refused cleanup removed a file '+$fault)
        Check (!$result.packageVerdictChanged) 'Refusal changed package verdict'
        if($blocked){$blocked.Dispose();$blocked=$null}
        if($fault -notin @('receipt','archive','marker','deep-retained')){Check ($before -ceq (RetainedHash $f)) ('Refusal changed retained bytes '+$fault)}
    }finally{if($originalTreeFunction){Set-Item Function:Get-FoundationPublishTemporaryTree -Value $originalTreeFunction};if($blocked){$blocked.Dispose()};Close-FoundationPublishContext $f.context}
}
$incomplete=NewCompletedFixture -Incomplete
try{$before=RetainedHash $incomplete;$r=Clean $incomplete;Check ($r.removedCount -eq 0 -and !$r.retainedPinsVerified) 'Marker/receipt without actual completed jobs admitted cleanup';Check ($before -ceq (RetainedHash $incomplete)) 'Incomplete publish changed retained data'}finally{Close-FoundationPublishContext $incomplete.context}
$failed=NewCompletedFixture -Incomplete
try{WriteFixture (Join-Path $failed.context.root 'fixture-mode.txt') 'fail';Reject {Invoke-FoundationContainedPublish $failed.context host $standIn} '*ContainedStageFailed*';Check ([IO.Path]::Exists((Join-Path $failed.context.destination '.publish-logs/host-stderr.txt'))) 'Failed child diagnostics missing';Check (!(Test-Path (Join-Path $failed.context.destination 'temporary-cleanup.json'))) 'Failed publish automatically ran cleanup'}finally{Close-FoundationPublishContext $failed.context}
$partial=NewCompletedFixture
try{
    $path=Join-Path $partial.context.destination '.build/bin/x.dll';WriteFixture $path 'read-only';[IO.File]::SetAttributes($path,[IO.FileAttributes]::ReadOnly)
    $r=Clean $partial;Check ($r.state -ceq 'NeedsAttention' -and $r.removalUncertainCount -eq 1 -and $r.removedCount -eq 0 -and $r.removedBytes -eq 0) 'Partial removal was mislabeled as fully removed'
    Check ([IO.Path]::Exists($path)) 'Partial-removal fixture did not preserve blocked file';Check $r.retainedPinsVerified 'Partial removal changed retained data'
}finally{Close-FoundationPublishContext $partial.context}
$post=NewCompletedFixture;$original=(Get-Item Function:Assert-FoundationPublishRetainedPins).ScriptBlock
try{
    $script:pinChecks=0;$script:pinsOriginal=$original
    Set-Item Function:Assert-FoundationPublishRetainedPins -Value {param($Context,$Pins);$script:pinChecks++;&$script:pinsOriginal $Context $Pins;if($script:pinChecks -eq 2){throw 'SyntheticFinalRetainedValidationRefused'}}
    $r=Clean $post;Check ($r.removedCount -eq 5 -and $r.state -ceq 'NeedsAttention' -and !$r.retainedPinsVerified -and $r.validationFailure -ceq 'PostRemovalValidationRefused') 'Final validation failure was hidden behind completed removals'
}finally{Set-Item Function:Assert-FoundationPublishRetainedPins -Value $original;Close-FoundationPublishContext $post.context}
$borrowed=NewCompletedFixture
try{
    $fake=$borrowed.context.PSObject.Copy();$outside=Join-Path $fixtureRoot 'outside';$null=New-FTEPrivateDirectory $outside;$fake.destination=$outside
    $r=Remove-FoundationPublishedTemporaries $fake $borrowed.receipt.sha256;Check ($r.removedCount -eq 0 -and $r.state -ceq 'NeedsAttention') 'Caller DTO supplied ownership';Check (!(Test-Path (Join-Path $outside 'temporary-cleanup.json'))) 'Rejected context wrote outside its actual root'
}finally{Close-FoundationPublishContext $borrowed.context}
$summary=[ordered]@{schema='platform-studio.foundation-publish-retention-tests.v1';checks=$script:checks;passed=$true;syntheticOnly=$true;actualSdkPublishPerformed=$false;existingNativeHelperUnchanged=$true;physicalSpaceReclaimedVerified=$false;packageVerdictChanged=$false;productionReady=$false}
$null=Write-FTEJson (Join-Path $fixtureRoot 'summary.json') $summary
Write-Host "Publisher retention: $($script:checks) checks passed. $fixtureRoot"
