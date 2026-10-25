#requires -Version 7.4
[CmdletBinding()]
param([string]$PublishedArchivePath='',[string]$ExpectedPublishedArchiveSha256='',[string]$DotNetPath='',[string]$ProbeSdkPath='')
$ErrorActionPreference='Stop'
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
foreach($script in @('scripts/Publish-Foundation.ps1','tests/release/foundation-package.tests.ps1','scripts/Manage-FoundationPreviewInstallation.ps1')){
    $tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $repositoryRoot $script),[ref]$tokens,[ref]$errors)
    if($errors){throw($errors|Out-String)}
    foreach($function in $ast.FindAll({param($node)$node -is [Management.Automation.Language.FunctionDefinitionAst]},$true)){. ([scriptblock]::Create($function.Extent.Text))}
}
$fixtureRoot=Join-Path $repositoryRoot ('artifacts/foundation-installation-tests/'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $fixtureRoot;$script:checks=0
function Check([bool]$Condition,[string]$Message){if(!$Condition){throw $Message};$script:checks++}
function Reject([scriptblock]$Action,[string]$Reason){$caught=$false;try{&$Action|Out-Null}catch{$caught=$true;if($_.Exception.Message -notlike $Reason){throw}};Check $caught ('Expected rejection '+$Reason)}
function NamespaceInventory([string]$Root){
    $entries=@((Get-Item -LiteralPath $Root -Force))+@(Get-ChildItem -LiteralPath $Root -Force -Recurse)
    $records=foreach($entry in @($entries|Sort-Object FullName -CaseSensitive)){
        $acl=Get-Acl -LiteralPath $entry.FullName
        [ordered]@{path=[IO.Path]::GetRelativePath($Root,$entry.FullName);directory=$entry.PSIsContainer;
            bytes=if($entry.PSIsContainer){0}else{$entry.Length};sha256=if($entry.PSIsContainer){''}else{(Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash.ToLowerInvariant()};
            sddl=$acl.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]'Access,Owner,Group')}
    }
    ConvertTo-Json -InputObject @($records) -Depth 8 -Compress
}
function NewInstallFixture([string]$Name,[string]$RuntimeConfiguration='{"runtimeOptions":{"tfm":"net10.0","frameworks":[{"name":"Microsoft.NETCore.App","version":"10.0.0"},{"name":"Microsoft.AspNetCore.App","version":"10.0.0"}]}}'){
    $fixture=New-PackageFixture $Name
    foreach($file in Get-ChildItem -LiteralPath $fixture.Runtime -Filter '*.runtimeconfig.json' -File){[IO.File]::WriteAllText($file.FullName,$RuntimeConfiguration)}
    # Refresh evidence after the intentional public descriptor change, before building an owned fixture archive.
    $runtimeFiles=@(Get-FoundationPackageFiles $fixture.Runtime|ForEach-Object{[ordered]@{path=$_.Path;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}})
    $evidencePath=Join-Path $fixture.Package 'dependency-evidence/dependency-evidence.json';$evidence=Get-Content -LiteralPath $evidencePath -Raw|ConvertFrom-Json -AsHashtable;$evidence.runtimeFiles=$runtimeFiles
    [IO.File]::WriteAllText($evidencePath,($evidence|ConvertTo-Json -Depth 16))
    $lines=foreach($noticeName in @('dependency-evidence.json','sbom.spdx.json','THIRD-PARTY-NOTICES.txt')){(Get-FileHash -LiteralPath (Join-Path $fixture.Package ('dependency-evidence/'+$noticeName))).Hash.ToLowerInvariant()+'  '+$noticeName}
    [IO.File]::WriteAllText((Join-Path $fixture.Package 'dependency-evidence/MANIFEST.sha256'),($lines-join "`n")+"`n")
    $archive=New-FoundationPackageArchive $fixture.Package $fixture.Zip
    [pscustomobject]@{Archive=$fixture.Zip;Sha256=$archive.Sha256;Install=(Join-Path $fixtureRoot ('installation-'+$Name));Fixture=$fixture}
}
function Stage($Fixture,$Prior=$null){
    $arguments=@{Action='Stage';InstallRoot=$Fixture.Install;ArchivePath=$Fixture.Archive;ExpectedArchiveSha256=$Fixture.Sha256;ExpectedGeneration=0;LocalPreview=$true}
    if($Prior){$arguments.ExpectedGeneration=$Prior.generation;$arguments.ExpectedStateSha256=$Prior.stateSha256}
    Invoke-FoundationPreviewInstallation @arguments
}
function NewBoundedProbeFixture([string]$Sdk,[string]$Dotnet){
    $sdkPath=Assert-FPILocalPath $Sdk;$dotnetRoot=Split-Path $Dotnet -Parent
    $reference=Join-Path $dotnetRoot 'packs/Microsoft.NETCore.App.Ref/10.0.8/ref/net10.0'
    $architecture=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
    if($architecture -notin @('arm64','x64')){throw 'Synthetic probe architecture not qualified.'}
    $template=Join-Path $dotnetRoot ('packs/Microsoft.NETCore.App.Host.win-'+$architecture+'/10.0.8/runtimes/win-'+$architecture+'/native/apphost.exe')
    $directory=Join-Path $fixtureRoot 'bounded-runtime-probe';$null=New-Item -ItemType Directory -Path $directory
    $source=Join-Path $directory 'Fixture.cs';$assembly=Join-Path $directory 'fixture.dll';$executable=Join-Path $directory 'dotnet.exe'
    [IO.File]::WriteAllText($source,@'
using System; using System.IO; using System.Linq; using System.Threading;
public static class Fixture {
 public static void Main(){
  var root=AppContext.BaseDirectory;File.WriteAllText(Path.Combine(root,"owned-pid.txt"),Environment.ProcessId.ToString());
  var mode=File.ReadAllText(Path.Combine(root,"mode.txt"));
  if(mode=="stall"){Thread.Sleep(Timeout.Infinite);return;}
  if(mode=="overflow"){Console.Write(new string('x',70000));Console.Out.Flush();Thread.Sleep(Timeout.Infinite);return;}
  File.WriteAllLines(Path.Combine(root,"environment-names.txt"),Environment.GetEnvironmentVariables().Keys.Cast<string>().OrderBy(x=>x));
  Console.WriteLine("Microsoft.NETCore.App 10.0.8 [synthetic-test-runtime]");
  Console.WriteLine("Microsoft.AspNetCore.App 10.0.8 [synthetic-test-runtime]");
 }
}
'@)
    # Compile only this owned constant fixture with existing SDK/reference packs. No restore or ZIP code.
    $info=[Diagnostics.ProcessStartInfo]::new($Dotnet);$info.UseShellExecute=$false;$info.CreateNoWindow=$true;$info.RedirectStandardOutput=$true;$info.RedirectStandardError=$true;$info.WorkingDirectory=$directory;$info.Environment.Clear()
    foreach($environmentName in @('SystemRoot','windir')){if([Environment]::GetEnvironmentVariable($environmentName)){$info.Environment[$environmentName]=[Environment]::GetEnvironmentVariable($environmentName)}}
    $info.Environment['DOTNET_ROOT']=$dotnetRoot;$info.Environment['TEMP']=$directory;$info.Environment['TMP']=$directory;$info.Environment['DOTNET_CLI_TELEMETRY_OPTOUT']='1'
    foreach($argument in @('exec',(Join-Path $sdkPath 'Roslyn/bincore/csc.dll'),'/noconfig','/nostdlib+','/target:exe',('/out:'+$assembly),$source)){$info.ArgumentList.Add($argument)}
    foreach($referenceFile in Get-ChildItem -LiteralPath $reference -Filter '*.dll' -File){$info.ArgumentList.Add('/reference:'+$referenceFile.FullName)}
    $compiler=[Diagnostics.Process]::new();$compiler.StartInfo=$info
    try{$null=$compiler.Start();$output=$compiler.StandardOutput.ReadToEndAsync();$errors=$compiler.StandardError.ReadToEndAsync();if(!$compiler.WaitForExit(20000)){throw 'Synthetic probe compiler timeout.'};if($compiler.ExitCode -ne 0){throw ('Synthetic probe compiler failed: '+$output.GetAwaiter().GetResult()+$errors.GetAwaiter().GetResult())}}
    finally{if($compiler.Id -gt 0 -and !$compiler.HasExited){$compiler.Kill($false);$null=$compiler.WaitForExit(5000)};$compiler.Dispose()}
    [IO.File]::WriteAllText((Join-Path $directory 'fixture.runtimeconfig.json'),'{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}')
    [void][Reflection.Assembly]::LoadFrom((Join-Path $sdkPath 'Microsoft.NET.HostModel.dll'))
    $search=[Microsoft.NET.HostModel.AppHost.HostWriter+DotNetSearchOptions]::new();$search.Location=[Microsoft.NET.HostModel.AppHost.HostWriter+DotNetSearchOptions+SearchLocation]::AppRelative
    $search.AppRelativeDotNet=[IO.Path]::GetRelativePath($directory,$dotnetRoot)
    [Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost($template,$executable,'fixture.dll',$false,$assembly,$false,$false,$search)
    [pscustomobject]@{Directory=$directory;Executable=$executable}
}

$reservation=Join-Path $fixtureRoot 'atomic-root-reservation';New-FPIPrivateRoot $reservation
Check ((Get-Acl -LiteralPath $reservation).AreAccessRulesProtected) 'Atomic root reservation did not start with a protected owner ACL.'
$reservationFile=Join-Path $reservation 'existing-synthetic.txt';[IO.File]::WriteAllText($reservationFile,'preserve atomic reservation fixture');$reservationHash=(Get-FileHash -LiteralPath $reservationFile).Hash
Reject {New-FPIPrivateRoot $reservation} '*RootReservationRejected*'
Check ((Get-FileHash -LiteralPath $reservationFile).Hash -ceq $reservationHash -and @(Get-ChildItem -LiteralPath $reservation -Force).Count -eq 1) 'Atomic reservation adopted or changed an existing root.'

$fixture=NewInstallFixture 'basic';$external=Join-Path $fixture.Fixture.Parent 'synthetic-customer-state';$null=New-Item -ItemType Directory -Path $external
foreach($name in @('cell.sqlite','cell.sqlite-wal','cell.sqlite-shm','private-config.json','credential-fixture.bin','signer-fixture.bin')){[IO.File]::WriteAllText((Join-Path $external $name),'synthetic preserved '+$name)}
$before=@{};foreach($file in Get-ChildItem -LiteralPath $external -File){$before[$file.Name]=(Get-FileHash -LiteralPath $file.FullName).Hash}
Reject {Invoke-FoundationPreviewInstallation -Action Stage -InstallRoot $fixture.Install -ArchivePath $fixture.Archive -ExpectedArchiveSha256 $fixture.Sha256 -ExpectedGeneration 0} '*ExplicitLocalPreviewRequired*'
Reject {Invoke-FoundationPreviewInstallation -Action Stage -InstallRoot $fixture.Install -ArchivePath $fixture.Archive -ExpectedArchiveSha256 ('0'*64) -ExpectedGeneration 0 -LocalPreview} '*ArchivePinMismatch*'
Check (!(Test-Path -LiteralPath $fixture.Install)) 'Rejected archive created an installation root.'
foreach($marker in @('.activation.guard','authority.json','.installation-owner.json','.installation.guard','.package-owner.json')){
    $parent=Join-Path $fixtureRoot ('protected-'+[Guid]::NewGuid().ToString('N'));$null=New-Item -ItemType Directory -Path $parent
    $markerPath=Join-Path $parent $marker;[IO.File]::WriteAllText($markerPath,'synthetic protected namespace marker');$markerHash=(Get-FileHash -LiteralPath $markerPath).Hash
    $nestedRoot=Join-Path $parent 'nested-software-root'
    Reject {Invoke-FoundationPreviewInstallation -Action Stage -InstallRoot $nestedRoot -ArchivePath $fixture.Archive -ExpectedArchiveSha256 $fixture.Sha256 -ExpectedGeneration 0 -LocalPreview} '*ProtectedAncestorNamespace*'
    Check (!(Test-Path -LiteralPath $nestedRoot) -and (Get-FileHash -LiteralPath $markerPath).Hash -ceq $markerHash -and @(Get-ChildItem -LiteralPath $parent -Force).Count -eq 1) 'Protected parent authority/installation/package namespace changed.'
}
$custodySentinels=@('.credential-namespace.json','.vault.lock','.keys.lock','cell-binding.json','.rollback.lock','.backup-checkpoint.guard','.credential-recovery-stage.guard','.credential-recovery-stage.json','.credential-recovery-checkpoint.guard','.credential-recovery-checkpoint.json','.foundation-owner-initialization.json',
    'legacy.credential.json','legacy.dpapi.json','.CREDENTIAL-NAMESPACE.JSON','LEGACY.CREDENTIAL.JSON','LEGACY.DPAPI.JSON')
foreach($marker in $custodySentinels){
    foreach($directorySentinel in @($false,$true)){
        $parent=Join-Path $fixtureRoot ('credential-custody-'+[Guid]::NewGuid().ToString('N'));New-FPIPrivateRoot $parent
        $entry=Join-Path $parent $marker
        if($directorySentinel){$null=New-Item -ItemType Directory -Path $entry;[IO.File]::WriteAllText((Join-Path $entry 'private.bin'),'synthetic retained custody bytes')}
        else{[IO.File]::WriteAllText($entry,'synthetic retained custody marker')}
        $nested=Join-Path $parent 'existing-child';$null=New-Item -ItemType Directory -Path $nested
        [IO.File]::WriteAllText((Join-Path $nested 'retained.bin'),'synthetic retained nested bytes')
        $beforeNamespace=NamespaceInventory $parent;$install=Join-Path $nested 'new-software-root'
        foreach($controllerAction in @('Stage','Repair','Verify','Readiness')){
            Reject {Invoke-FoundationPreviewInstallation -Action $controllerAction -InstallRoot $install -ArchivePath $fixture.Archive -ExpectedArchiveSha256 $fixture.Sha256 -ExpectedGeneration 0 -LocalPreview} '*ProtectedAncestorNamespace*'
            Check ((NamespaceInventory $parent) -ceq $beforeNamespace) 'Credential namespace refusal changed entries, bytes or ACLs.'
        }
    }
}
foreach($leaf in @('.activation.guard','authority.json','.installation-owner.json','.installation.guard','.package-owner.json',
    '.foundation-owner-initialization.json','.credential-namespace.json','.vault.lock','.keys.lock','cell-binding.json','.rollback.lock','.backup-checkpoint.guard','.credential-recovery-stage.guard','.credential-recovery-stage.json','.credential-recovery-checkpoint.guard','.credential-recovery-checkpoint.json',
    'legacy.credential.json','legacy.dpapi.json','.CREDENTIAL-NAMESPACE.JSON','LEGACY.CREDENTIAL.JSON','LEGACY.DPAPI.JSON')){
    $parent=Join-Path $fixtureRoot ('protected-leaf-'+[Guid]::NewGuid().ToString('N'));New-FPIPrivateRoot $parent
    [IO.File]::WriteAllText((Join-Path $parent 'retained.bin'),'synthetic retained clean-parent bytes')
    $beforeNamespace=NamespaceInventory $parent;$install=Join-Path $parent $leaf
    foreach($controllerAction in @('Stage','Repair','Verify','Readiness')){
        Reject {Invoke-FoundationPreviewInstallation -Action $controllerAction -InstallRoot $install -ArchivePath $fixture.Archive -ExpectedArchiveSha256 $fixture.Sha256 -ExpectedGeneration 0 -LocalPreview} '*ProtectedAncestorNamespace*'
        Check ((NamespaceInventory $parent) -ceq $beforeNamespace) 'Protected leaf refusal created entries or changed parent bytes/ACLs.'
    }
}
# A previously valid software installation is also refused without modification if its location becomes operational custody.
foreach($marker in @('.credential-namespace.json','.vault.lock','.keys.lock','cell-binding.json','.rollback.lock','.backup-checkpoint.guard','.credential-recovery-stage.guard','.credential-recovery-stage.json','.credential-recovery-checkpoint.guard','.credential-recovery-checkpoint.json','legacy.credential.json','legacy.dpapi.json')){
    $misplaced=NewInstallFixture ('existing-custody-'+[Guid]::NewGuid().ToString('N'))
    $parent=Join-Path $fixtureRoot ('existing-parent-'+[Guid]::NewGuid().ToString('N'));New-FPIPrivateRoot $parent;$misplaced.Install=Join-Path $parent 'software'
    $initial=Stage $misplaced
    [IO.File]::WriteAllText((Join-Path $parent $marker),'synthetic later discovered custody sentinel')
    $beforeNamespace=NamespaceInventory $parent
    foreach($controllerAction in @('Stage','Repair','Verify','Readiness')){
        Reject {Invoke-FoundationPreviewInstallation -Action $controllerAction -InstallRoot $misplaced.Install -ArchivePath $misplaced.Archive -ExpectedArchiveSha256 $misplaced.Sha256 -ExpectedGeneration 1 -ExpectedStateSha256 $initial.stateSha256 -Generation $(if($controllerAction -ceq 'Repair'){1}else{0}) -LocalPreview} '*ProtectedAncestorNamespace*'
        Check ((NamespaceInventory $parent) -ceq $beforeNamespace) 'Existing misplaced installation/custody was altered by refusal.'
    }
}
$selfCustody=NewInstallFixture 'self-custody-sentinel';$selfStage=Stage $selfCustody
[IO.File]::WriteAllText((Join-Path $selfCustody.Install '.vault.lock'),'synthetic operational custody inside software root')
$selfBefore=NamespaceInventory $selfCustody.Install
foreach($controllerAction in @('Stage','Repair','Verify','Readiness')){
    Reject {Invoke-FoundationPreviewInstallation -Action $controllerAction -InstallRoot $selfCustody.Install -ArchivePath $selfCustody.Archive -ExpectedArchiveSha256 $selfCustody.Sha256 -ExpectedGeneration 1 -ExpectedStateSha256 $selfStage.stateSha256 -LocalPreview} '*ProtectedAncestorNamespace*'
    Check ((NamespaceInventory $selfCustody.Install) -ceq $selfBefore) 'Custody sentinel inside an existing software root was altered.'
}
$first=Stage $fixture
Check ($first.generation -eq 1 -and !$first.activeVersionChanged -and !$first.customerDataTouched -and !$first.signed -and !$first.architectureQualified -and !$first.cleanMachineQualified -and !$first.productionReady) 'Stage overstated activation or qualification.'
Check ((Invoke-FoundationPreviewInstallation -Action Verify -InstallRoot $fixture.Install).verification.verified) 'Fresh staged bytes did not verify.'
Check ((Get-Acl -LiteralPath $fixture.Install).AreAccessRulesProtected) 'Installation root did not receive protected ACLs.'
Reject {Stage $fixture} '*GenerationChanged*'
$second=Stage $fixture $first
Check ($second.generation -eq 2 -and (Test-Path -LiteralPath $first.stagedDirectory)) 'Side-by-side staging altered an earlier generation.'
$target=@(Get-ChildItem -LiteralPath (Join-Path $first.stagedDirectory 'runtime') -Filter '*.dll' -File)[0].FullName
[IO.File]::AppendAllText($target,'synthetic damage');$damagedHash=(Get-FileHash -LiteralPath $target).Hash
$broken=Invoke-FoundationPreviewInstallation -Action Verify -InstallRoot $fixture.Install -Generation 1
Check (!$broken.verification.verified -and $broken.verification.reasons -contains 'PackageFileHashMismatch') 'Corrupt staged bytes were accepted.'
$repair=Invoke-FoundationPreviewInstallation -Action Repair -InstallRoot $fixture.Install -ArchivePath $fixture.Archive -ExpectedArchiveSha256 $fixture.Sha256 -ExpectedGeneration 2 -ExpectedStateSha256 $second.stateSha256 -Generation 1 -LocalPreview
Check ($repair.generation -eq 3 -and $repair.repairOf -eq 1 -and (Get-FileHash -LiteralPath $target).Hash -ceq $damagedHash) 'Repair changed or discarded the damaged original.'
Check ((Invoke-FoundationPreviewInstallation -Action Verify -InstallRoot $fixture.Install -Generation 3).verification.verified) 'Repair restage did not verify.'
foreach($file in Get-ChildItem -LiteralPath $external -File){Check ((Get-FileHash -LiteralPath $file.FullName).Hash -ceq $before[$file.Name]) 'External customer/credential/configuration fixture changed.'}
$guard=Open-FPIGuard $fixture.Install -Exclusive
try{Reject {Invoke-FoundationPreviewInstallation -Action Verify -InstallRoot $fixture.Install} '*PreviewInstallationBusy*'}finally{$guard.Dispose()}
Reject {Invoke-FoundationPreviewInstallation -Action Repair -InstallRoot $fixture.Install -ArchivePath $fixture.Archive -ExpectedArchiveSha256 $fixture.Sha256 -ExpectedGeneration 3 -ExpectedStateSha256 ('0'*64) -Generation 1 -LocalPreview} '*GenerationChanged*'
Reject {Invoke-FoundationPreviewInstallation -Action Verify -InstallRoot '\\localhost\unadmitted\installation'} '*LocalCanonicalPathRequired*'
Reject {Invoke-FoundationPreviewInstallation -Action Verify -InstallRoot 'C:\'} '*LocalCanonicalPathRequired*'
Reject {Invoke-FoundationPreviewInstallation -Action Verify -InstallRoot ($fixture.Install+'\..\installation')} '*LocalCanonicalPathRequired*'
foreach($paths in @(@('runtime/./alias.dll','runtime/alias.dll'),@('runtime/Case.dll','runtime/case.dll'),@('runtime//alias.dll'),@('runtime/../outside.dll'),@('runtime/trailing./alias.dll'),@('runtime/CON.dll'),@('runtime/trailing /alias.dll'))){
    $id=[Guid]::NewGuid().ToString('N');$invalidArchive=Join-Path $fixtureRoot ($id+'.zip');$invalidRoot=Join-Path $fixtureRoot ('rejected-'+$id)
    $zip=[IO.Compression.ZipFile]::Open($invalidArchive,[IO.Compression.ZipArchiveMode]::Create)
    try{foreach($name in $paths){$entry=$zip.CreateEntry($name);$writer=[IO.StreamWriter]::new($entry.Open());try{$writer.Write('synthetic non-executable archive fixture')}finally{$writer.Dispose()}}}finally{$zip.Dispose()}
    $pin=(Get-FileHash -LiteralPath $invalidArchive).Hash.ToLowerInvariant()
    Reject {Invoke-FoundationPreviewInstallation -Action Stage -InstallRoot $invalidRoot -ArchivePath $invalidArchive -ExpectedArchiveSha256 $pin -ExpectedGeneration 0 -LocalPreview} '*ArchivePathRejected*'
    Check (!(Test-Path -LiteralPath $invalidRoot)) 'Rejected noncanonical or colliding ZIP paths created a staging root.'
}
$unknown=NewInstallFixture 'unknown-metadata';$null=Stage $unknown;[IO.File]::WriteAllText((Join-Path $unknown.Install 'unrecognized.json'),'{}')
Reject {Invoke-FoundationPreviewInstallation -Action Verify -InstallRoot $unknown.Install} '*UnknownEntry*'
$unknownPackage=NewInstallFixture 'unknown-package-file';$u=Stage $unknownPackage;[IO.File]::WriteAllText((Join-Path $u.stagedDirectory 'customer-data.txt'),'preserve unknown synthetic data')
Reject {Invoke-FoundationPreviewInstallation -Action Repair -InstallRoot $unknownPackage.Install -ArchivePath $unknownPackage.Archive -ExpectedArchiveSha256 $unknownPackage.Sha256 -ExpectedGeneration 1 -ExpectedStateSha256 $u.stateSha256 -LocalPreview} '*UnknownPackageEntry*'
Check (Test-Path -LiteralPath (Join-Path $u.stagedDirectory 'customer-data.txt')) 'Unknown data was deleted.'
$partial=NewInstallFixture 'partial-generation';$null=Stage $partial;$null=New-Item -ItemType Directory -Path (Join-Path $partial.Install 'generations/000002/package')
Reject {Invoke-FoundationPreviewInstallation -Action Verify -InstallRoot $partial.Install} '*PartialOrUnknownGeneration*'
$tampered=NewInstallFixture 'tampered-receipt';$null=Stage $tampered;$receipt=Join-Path $tampered.Install 'generations/000001/generation.json';$value=Get-Content -LiteralPath $receipt -Raw|ConvertFrom-Json -AsHashtable;$value.signed=$true
[IO.File]::WriteAllText($receipt,($value|ConvertTo-Json -Depth 16));Reject {Invoke-FoundationPreviewInstallation -Action Verify -InstallRoot $tampered.Install} '*MetadataIntegrityFailed*'
$value.sha256=Get-FPIJsonHash $value;[IO.File]::WriteAllText($receipt,($value|ConvertTo-Json -Depth 16));Reject {Invoke-FoundationPreviewInstallation -Action Verify -InstallRoot $tampered.Install} '*UnsupportedClaim*'
$value.signed=$false;$value.installId=('0'*32);$value.sha256=Get-FPIJsonHash $value;[IO.File]::WriteAllText($receipt,($value|ConvertTo-Json -Depth 16));Reject {Invoke-FoundationPreviewInstallation -Action Verify -InstallRoot $tampered.Install} '*MetadataIntegrityFailed*'
$value.installId=(Get-Content -LiteralPath (Join-Path $tampered.Install '.installation-owner.json') -Raw|ConvertFrom-Json -AsHashtable).installId
foreach($mutation in @(@{key='schema';value='unrecognized.schema'},@{key='schema';value=@($value.schema)},@{key='localPreview';value='true'},@{key='generation';value='1'})){
    $semantic=ConvertFrom-Json -InputObject ($value|ConvertTo-Json -Depth 16) -AsHashtable;$semantic[$mutation.key]=$mutation.value;$semantic.sha256=Get-FPIJsonHash $semantic
    [IO.File]::WriteAllText($receipt,($semantic|ConvertTo-Json -Depth 16));Reject {Invoke-FoundationPreviewInstallation -Action Verify -InstallRoot $tampered.Install} '*MetadataIntegrityFailed*'
}
$markerFile=Join-Path $tampered.Install '.installation-owner.json';$marker=Get-Content -LiteralPath $markerFile -Raw|ConvertFrom-Json -AsHashtable;$marker.rootPath+='-wrong';$marker.sha256=Get-FPIJsonHash $marker
[IO.File]::WriteAllText($markerFile,($marker|ConvertTo-Json -Depth 16));Reject {Invoke-FoundationPreviewInstallation -Action Verify -InstallRoot $tampered.Install} '*BindingRejected*'
$aclFixture=NewInstallFixture 'widened-acl';$null=Stage $aclFixture;$directoryInfo=[IO.DirectoryInfo]::new($aclFixture.Install)
$acl=[IO.FileSystemAclExtensions]::GetAccessControl($directoryInfo,[Security.AccessControl.AccessControlSections]::Access)
$acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new([Security.Principal.SecurityIdentifier]::new('S-1-1-0'),[Security.AccessControl.FileSystemRights]::ReadAndExecute,[Security.AccessControl.AccessControlType]::Allow));[IO.FileSystemAclExtensions]::SetAccessControl($directoryInfo,$acl)
Reject {Invoke-FoundationPreviewInstallation -Action Verify -InstallRoot $aclFixture.Install} '*OwnerAclRejected*'
$junctionFixture=NewInstallFixture 'linked-root';$junction=Join-Path $junctionFixture.Fixture.Parent 'junction';$null=New-Item -ItemType Directory -Path $junctionFixture.Install
$null=New-Item -ItemType Junction -Path $junction -Target $junctionFixture.Install
try{Reject {Invoke-FoundationPreviewInstallation -Action Verify -InstallRoot $junction} '*Linked package paths*'}finally{Remove-Item -LiteralPath $junction}
$readiness=$null
$boundedProbeQualified=$false
if($DotNetPath){
    $readiness=Invoke-FoundationPreviewInstallation -Action Readiness -InstallRoot $fixture.Install -Generation 3 -DotNetPath $DotNetPath
    Check ($readiness.packageBytesVerified -and $readiness.runtimePrerequisitesReady -and !$readiness.architectureQualified -and !$readiness.cleanMachineQualified -and !$readiness.productionReady -and !$readiness.customerConfigurationEvaluated -and !$readiness.activationEvaluated) 'Runtime readiness was false or overstated installation/application acceptance.'
    $oldHook=$env:DOTNET_STARTUP_HOOKS;try{$env:DOTNET_STARTUP_HOOKS='synthetic-invalid-hook-that-must-not-be-inherited';Check ((Invoke-FoundationPreviewInstallation -Action Readiness -InstallRoot $fixture.Install -Generation 3 -DotNetPath $DotNetPath).runtimePrerequisitesReady) 'Readiness inherited parent runtime settings.'}finally{$env:DOTNET_STARTUP_HOOKS=$oldHook}
    foreach($policy in @('"rollForward":"Disable"','"applyPatches":false')){
        $pinned=NewInstallFixture ('pinned-policy-'+[Guid]::NewGuid().ToString('N')) ('{"runtimeOptions":{"tfm":"net10.0",'+$policy+',"framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}')
        $null=Stage $pinned;$pinnedReadiness=Invoke-FoundationPreviewInstallation -Action Readiness -InstallRoot $pinned.Install -DotNetPath $DotNetPath
        Check ($pinnedReadiness.packageBytesVerified -and !$pinnedReadiness.frameworkDescriptorsValid -and !$pinnedReadiness.runtimePrerequisitesReady) 'Unsupported pinned runtime policy incorrectly accepted a later installed patch.'
    }
    if($ProbeSdkPath){
        $bounded=NewBoundedProbeFixture $ProbeSdkPath $DotNetPath
        foreach($mode in @('stall','overflow')){
            [IO.File]::WriteAllText((Join-Path $bounded.Directory 'mode.txt'),$mode);$elapsed=[Diagnostics.Stopwatch]::StartNew()
            Reject {Invoke-FoundationPreviewInstallation -Action Readiness -InstallRoot $fixture.Install -Generation 3 -DotNetPath $bounded.Executable} '*'
            $elapsed.Stop();Check ($elapsed.Elapsed.TotalSeconds -lt 45) 'Bounded probe did not finish within the local qualification deadline.'
            $ownedPid=[int][IO.File]::ReadAllText((Join-Path $bounded.Directory 'owned-pid.txt'));$ownedProcess=Get-Process -Id $ownedPid -ErrorAction SilentlyContinue
            try{Check ($null -eq $ownedProcess -or $ownedProcess.HasExited) 'Failed probe left its original synthetic child running.'}finally{if($ownedProcess){$ownedProcess.Dispose()}}
        }
        [IO.File]::WriteAllText((Join-Path $bounded.Directory 'mode.txt'),'environment');$oldSynthetic=$env:PLATFORM_STUDIO_SYNTHETIC_SECRET;$oldHook=$env:DOTNET_STARTUP_HOOKS
        try{$env:PLATFORM_STUDIO_SYNTHETIC_SECRET='owned noncredential fixture';$env:DOTNET_STARTUP_HOOKS='owned invalid hook';$null=Invoke-FoundationPreviewInstallation -Action Readiness -InstallRoot $fixture.Install -Generation 3 -DotNetPath $bounded.Executable}
        finally{$env:PLATFORM_STUDIO_SYNTHETIC_SECRET=$oldSynthetic;$env:DOTNET_STARTUP_HOOKS=$oldHook}
        $childEnvironment=[IO.File]::ReadAllLines((Join-Path $bounded.Directory 'environment-names.txt'))
        # Windows supplies PROCESSOR_ARCHITECTURE during process creation after our explicit environment is cleared.
        Check (@($childEnvironment|Where-Object {$_ -notin @('SystemRoot','windir','TEMP','TMP','DOTNET_ROOT','PATH','DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_SKIP_FIRST_TIME_EXPERIENCE','DOTNET_GENERATE_ASPNET_CERTIFICATE','DOTNET_ADD_GLOBAL_TOOLS_TO_PATH','PROCESSOR_ARCHITECTURE')}).Count -eq 0) 'Actual synthetic child inherited environment entries outside the allowlist.'
        $boundedProbeQualified=$true
    }
}
$actual=$null;$actualRepair=$null
if($PublishedArchivePath){
    $actualRoot=Join-Path $fixtureRoot 'actual-published-installation'
    $actual=Invoke-FoundationPreviewInstallation -Action Stage -InstallRoot $actualRoot -ArchivePath $PublishedArchivePath -ExpectedArchiveSha256 $ExpectedPublishedArchiveSha256 -ExpectedGeneration 0 -LocalPreview
    Check ((Invoke-FoundationPreviewInstallation -Action Verify -InstallRoot $actualRoot).verification.verified) 'Actual published package did not stage/verify.'
    if($DotNetPath){Check ((Invoke-FoundationPreviewInstallation -Action Readiness -InstallRoot $actualRoot -DotNetPath $DotNetPath).runtimePrerequisitesReady) 'Actual package runtime prerequisites were unavailable.'}
    $damagedOwner=Join-Path $actual.stagedDirectory 'runtime/SelfService.Foundation.Owner.dll'
    [IO.File]::WriteAllText($damagedOwner,'synthetic corruption of this disposable staged copy')
    $damagedSha=(Get-FileHash -LiteralPath $damagedOwner -Algorithm SHA256).Hash
    Check (!(Invoke-FoundationPreviewInstallation -Action Verify -InstallRoot $actualRoot).verification.verified) 'Actual staged runtime corruption was not detected.'
    $actualRepair=Invoke-FoundationPreviewInstallation -Action Repair -InstallRoot $actualRoot -ArchivePath $PublishedArchivePath -ExpectedArchiveSha256 $ExpectedPublishedArchiveSha256 -ExpectedGeneration $actual.generation -ExpectedStateSha256 $actual.stateSha256 -Generation $actual.generation -LocalPreview
    Check ($actualRepair.generation -eq 2 -and (Get-FileHash -LiteralPath $damagedOwner -Algorithm SHA256).Hash -ceq $damagedSha) 'Actual package repair modified the damaged original.'
    Check ((Invoke-FoundationPreviewInstallation -Action Verify -InstallRoot $actualRoot -Generation $actualRepair.generation).verification.verified) 'Fresh actual package repair did not verify.'
    if($DotNetPath){Check ((Invoke-FoundationPreviewInstallation -Action Readiness -InstallRoot $actualRoot -Generation $actualRepair.generation -DotNetPath $DotNetPath).runtimePrerequisitesReady) 'Repaired actual package prerequisites were unavailable.'}
}
$summary=[ordered]@{schema='platform-studio.foundation-preview-installation-tests.v1';checks=$script:checks;passed=$true;actualPublishedStage=$actual;actualPublishedRepair=$actualRepair;runtimePrerequisites=$readiness;boundedProbeFailuresQualified=$boundedProbeQualified;
    noPackageCodeExecuted=$true;activeVersionChanged=$false;customerDataTouched=$false;signInPerformed=$false;cloudOperationsPerformed=$false;signed=$false;architectureQualified=$false;cleanMachineQualified=$false;productionReady=$false}
$summary|ConvertTo-Json -Depth 20|Set-Content -LiteralPath (Join-Path $fixtureRoot 'installation-summary.json') -Encoding utf8NoBOM
Write-Host "Foundation preview installation: $script:checks checks passed. $fixtureRoot"
