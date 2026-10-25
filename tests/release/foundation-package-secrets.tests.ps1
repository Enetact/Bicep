#requires -Version 7.4
[CmdletBinding()]
param([string]$PublishedArchivePath='',[string]$ExpectedArchiveSha256='',
    [string]$DotNetPath='C:\IT\Package\dotnet\dotnet.exe',[string]$SdkPath='C:\IT\Package\dotnet\sdk\10.0.300')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
$repositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$actualArchiveArgument=$PublishedArchivePath;$actualArchiveHashArgument=$ExpectedArchiveSha256
. (Join-Path $repositoryRoot 'scripts/Test-FoundationPackageSecrets.ps1')
Initialize-FPSHelpers
$tool=Join-Path $repositoryRoot '.tools/gitleaks-8.30.1/gitleaks.exe'
$toolHash='200df852fdecbedb19a33960657333cba5e231740bc8968972b507b50f93b194'
$base=Join-Path $repositoryRoot 'artifacts/foundation-package-secret-tests';$null=[IO.Directory]::CreateDirectory($base)
$fixtureRoot=New-FTEPrivateDirectory (Join-Path $base ([Guid]::NewGuid().ToString('N')))
$script:checks=0
function Check([bool]$Condition,[string]$Message){if(!$Condition){throw $Message};$script:checks++}
function Reject([scriptblock]$Action,[string]$Reason='*'){$caught=$false;try{&$Action|Out-Null}catch{$caught=$true;if($_.Exception.Message -notlike $Reason){throw}};Check $caught ('Expected rejection: '+$Reason)}
function Text([string]$Path,[string]$Value){$null=[IO.Directory]::CreateDirectory((Split-Path $Path -Parent));[IO.File]::WriteAllText($Path,$Value,[Text.UTF8Encoding]::new($false))}
function Private([string]$Name){New-FTEPrivateDirectory (Join-Path $fixtureRoot $Name)}
function Compile([string]$Name,[string]$Source,[switch]$Executable){
    $directory=Private $Name;$sourcePath=Join-Path $directory 'Fixture.cs';$assembly=Join-Path $directory 'fixture.dll';Text $sourcePath $Source
    $framework=Join-Path (Split-Path $DotNetPath -Parent) 'packs/Microsoft.NETCore.App.Ref/10.0.8/ref/net10.0'
    $kind=if($Executable){'exe'}else{'library'}
    $arguments=@('exec',(Join-Path $SdkPath 'Roslyn/bincore/csc.dll'),'/noconfig','/nostdlib+',('/target:'+$kind),('/out:'+$assembly),$sourcePath)+@(Get-ChildItem -LiteralPath $framework -File -Filter '*.dll'|ForEach-Object {'/reference:'+$_.FullName})
    $result=[FoundationPackageSecretMetadata]::Run($DotNetPath,$arguments,$directory,30000)
    Check ($result.ExitCode -eq 0) 'Owned constant fixture compilation failed.'
    [pscustomobject]@{Directory=$directory;Assembly=$assembly}
}
function ProjectionMap($Extraction,[string]$PathHash){
    $map=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($file in $Extraction.Files){$ranges=@($Extraction.Ranges|Where-Object {$_.File -ceq $file});$map.Add($file,@{entryId=1;pathSha256=$PathHash;surface='ManagedUserStrings';ranges=$ranges;lines=($ranges[-1].EndLine+1)})}
    ,$map
}

# Deliberately synthetic token; never sent to a service. Split source prevents a source-scan false positive.
$token='ghp_'+('Ab7cD9eF2gH4jK6mN8pQ1rS3tU5vW7xY9zA2')
$executionMarker=Join-Path $fixtureRoot 'assembly-must-not-run.marker'
$literalSource='using System.IO;using System.Runtime.CompilerServices;public static class Fixture{[ModuleInitializer] public static void Init(){File.WriteAllText(@"'+$executionMarker+'","unexpected execution");} public static string Value()=>"'+$token+'";public static string Controls()=>"a\0b\r\nc";}'
$literal=Compile 'literal' $literalSource
$bytes=[IO.File]::ReadAllBytes($literal.Assembly)
Check (![Text.Encoding]::ASCII.GetString($bytes).Contains($token)) 'Synthetic token unexpectedly existed as ASCII.'
Check ([Convert]::ToHexString($bytes).Contains([Convert]::ToHexString([Text.Encoding]::Unicode.GetBytes($token)))) 'Synthetic UTF16 token was not emitted into metadata.'
Reject {Assert-FoundationSafeBytes 'docs/obvious.txt' ([Text.Encoding]::Unicode.GetBytes($token))} '*Credential-shaped*'
$projectionDirectory=Private 'literal-inputs'
$extracted=[FoundationPackageSecretMetadata]::Extract($literal.Assembly,$projectionDirectory,'000001-us',134217728)
Check ($extracted.HasMetadata -and ($extracted.Entries -gt 0) -and ($extracted.Utf8Bytes -gt 0)) 'Managed metadata was not extracted.'
Check (!(Test-Path -LiteralPath $executionMarker)) 'Metadata extraction executed the inspected assembly.'
$projected=(@($extracted.Files|ForEach-Object {[IO.File]::ReadAllText((Join-Path $projectionDirectory $_))}) -join "`n")
Check $projected.Contains($token) 'UTF16 literal did not reach UTF8 scanner projection.'
Check (!$projected.Contains([char]0)) 'NUL control was not projected safely.'
$pathHash=Get-FTEHash ([Text.Encoding]::UTF8.GetBytes('runtime/'+$token+'.dll'))
$map=ProjectionMap $extracted $pathHash
$literalRun=Private 'literal-scan'
$scan=Invoke-FPSGitleaks $tool $toolHash $literalRun $projectionDirectory $map
Check ($scan.state -ceq 'FindingsRequireReview') 'Pinned scanner missed the managed-only UTF16 token.'
Check (@($scan.findings|Where-Object {($_.surface -ceq 'ManagedUserStrings') -and ($_.userStringHeapOffsets.Count -gt 0)}).Count -gt 0) 'Finding lost its heap provenance.'
$safe=ConvertTo-Json -InputObject $scan.findings -Depth 12
Check (!$safe.Contains($token) -and !$safe.Contains($fixtureRoot) -and !$safe.Contains('runtime/')) 'Safe finding leaked token or raw package/private path.'
Check (!(Test-Path -LiteralPath (Join-Path $literalRun 'raw-redacted.json'))) 'Raw scanner report was retained.'
Check (!(Test-Path -LiteralPath $executionMarker)) 'Scanner executed the inspected managed assembly.'
Reject {[FoundationPackageSecretMetadata]::Extract($literal.Assembly,(Private 'tiny-projection'),'tiny',1)} '*PackageSecretProjectionLimit*'
$malformed=Join-Path $fixtureRoot 'malformed.dll';[IO.File]::WriteAllBytes($malformed,[byte[]]@(77,90,0,0))
Reject {[FoundationPackageSecretMetadata]::Extract($malformed,(Private 'malformed-projection'),'bad',134217728)} '*'

Check (([Text.Encoding]::UTF8.GetString((ConvertTo-FPSText ([Text.Encoding]::Unicode.GetPreamble()+[Text.Encoding]::Unicode.GetBytes($token))))) -ceq $token) 'UTF16 BOM text was not projected.'
Check (([Text.Encoding]::UTF8.GetString((ConvertTo-FPSText ([Text.Encoding]::BigEndianUnicode.GetPreamble()+[Text.Encoding]::BigEndianUnicode.GetBytes($token))))) -ceq $token) 'Big-endian UTF16 text was not projected.'
Reject {ConvertTo-FPSText ([byte[]]@(0,65))} '*EncodingUnsupported*'
Reject {ConvertTo-FPSText ([byte[]]@(255,255,255))} '*'
Reject {ConvertTo-FPSText ([byte[]]::new(16777217))} '*TextLimit*'
Check ((Get-FPSExitState 0 @()) -ceq 'NoFindingsInCoveredSurfaces') 'Clean result state is wrong.'
Reject {Get-FPSExitState 0 @(@{redacted=$true})} '*ToolOrReportMismatch*'
Reject {Get-FPSExitState 17 @()} '*ToolOrReportMismatch*'
Reject {Get-FPSExitState 2 @()} '*ToolOrReportMismatch*'
$firstName=$extracted.Files[0]
$raw=[ordered]@{RuleID='github-pat';File=(Join-Path $projectionDirectory $firstName);StartLine=1;Secret='REDACTED'}
foreach($mutation in @('unredacted','outside','unknown','line','fraction','rule')){
    $bad=@{};foreach($key in $raw.Keys){$bad[$key]=$raw[$key]}
    switch($mutation){'unredacted'{$bad.Secret=$token};'outside'{$bad.File=Join-Path $fixtureRoot 'outside.txt'};'unknown'{$bad.File=Join-Path $projectionDirectory 'unknown.txt'};'line'{$bad.StartLine=10000000};'fraction'{$bad.StartLine=1.5};'rule'{$bad.RuleID='unsafe rule'}}
    Reject {ConvertTo-FPSSafeFindings $projectionDirectory $map ([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject @($bad))))} '*PackageSecret*'
}
Reject {ConvertTo-FPSSafeFindings $projectionDirectory $map ([Text.Encoding]::UTF8.GetBytes('[{"File":"a","file":"b"}]'))} '*Ambiguous*'
Reject {ConvertTo-FPSSafeFindings $projectionDirectory $map ([Text.Encoding]::UTF8.GetBytes('{}'))} '*Invalid*'
$oldInventory=Get-FPSProjectionInventory $projectionDirectory $map
Text (Join-Path $projectionDirectory 'unexpected.txt') 'unlisted projection'
Reject {Get-FPSProjectionInventory $projectionDirectory $map} '*ProjectionChanged*'
$nestedProjection=Private 'unexpected-directory'
foreach($name in $extracted.Files){Copy-Item -LiteralPath (Join-Path $projectionDirectory $name) -Destination (Join-Path $nestedProjection $name)}
$null=[IO.Directory]::CreateDirectory((Join-Path $nestedProjection 'unlisted-directory'))
Reject {Get-FPSProjectionInventory $nestedProjection $map} '*ProjectionChanged*'

# Constant owned native probe qualifies timeout/output cleanup and environment isolation. It is not package code.
$probe=Compile 'probe' @'
using System;using System.IO;using System.Threading;
public static class Fixture {public static int Main(string[] args){
 if(args[0]=="env"){Console.Write(Environment.GetEnvironmentVariable("GITLEAKS_CONFIG")==null&&Environment.GetEnvironmentVariable("SYNTHETIC_PACKAGE_SECRET_ENV")==null&&Environment.GetEnvironmentVariable("DOTNET_STARTUP_HOOKS")==null&&Environment.GetEnvironmentVariable("HTTPS_PROXY")==null?"clear":"inherited");return 0;}
 File.WriteAllText(Path.Combine(Environment.CurrentDirectory,"probe.pid"),Environment.ProcessId.ToString());
 if(args[0]=="overflow"){Console.Write(new string('x',1048577));Console.Out.Flush();}Thread.Sleep(Timeout.Infinite);return 0;}}
'@ -Executable
Text (Join-Path $probe.Directory 'fixture.runtimeconfig.json') '{"runtimeOptions":{"tfm":"net10.0","framework":{"name":"Microsoft.NETCore.App","version":"10.0.0"}}}'
[void][Reflection.Assembly]::LoadFrom((Join-Path $SdkPath 'Microsoft.NET.HostModel.dll'))
$dotnetRoot=Split-Path $DotNetPath -Parent;$architecture=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
$template=Join-Path $dotnetRoot ('packs/Microsoft.NETCore.App.Host.win-'+$architecture+'/10.0.8/runtimes/win-'+$architecture+'/native/apphost.exe')
$search=[Microsoft.NET.HostModel.AppHost.HostWriter+DotNetSearchOptions]::new();$search.Location=[Microsoft.NET.HostModel.AppHost.HostWriter+DotNetSearchOptions+SearchLocation]::AppRelative;$search.AppRelativeDotNet=[IO.Path]::GetRelativePath($probe.Directory,$dotnetRoot)
$probeExe=Join-Path $probe.Directory 'probe.exe';[Microsoft.NET.HostModel.AppHost.HostWriter]::CreateAppHost($template,$probeExe,'fixture.dll',$false,$probe.Assembly,$false,$false,$search)
$saved=@{};foreach($name in @('GITLEAKS_CONFIG','SYNTHETIC_PACKAGE_SECRET_ENV','DOTNET_STARTUP_HOOKS','HTTPS_PROXY')){$saved[$name]=[Environment]::GetEnvironmentVariable($name);[Environment]::SetEnvironmentVariable($name,'synthetic-invalid-parent-value')}
try{$observed=[FoundationPackageSecretMetadata]::Run($probeExe,@('env'),$probe.Directory,10000);Check ($observed.Stdout -ceq 'clear') 'Scanner inherited config, proxy, startup-hook or credential environment.'}
finally{foreach($name in $saved.Keys){[Environment]::SetEnvironmentVariable($name,$saved[$name])}}
foreach($mode in @('stall','overflow')){
    $probeRun=Private ('probe-'+$mode)
    Reject {[FoundationPackageSecretMetadata]::Run($probeExe,@($mode),$probeRun,1500)} '*'
    $id=[int][IO.File]::ReadAllText((Join-Path $probeRun 'probe.pid'))
    Check ($null -eq (Get-Process -Id $id -ErrorAction SilentlyContinue)) 'Bounded process was left running.'
}

# Reuse fixture definitions only, not the package test entry point. Replace pseudo-DLLs with actual clean managed PEs.
$packageTest=Join-Path $repositoryRoot 'tests/release/foundation-package.tests.ps1';$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($packageTest,[ref]$tokens,[ref]$errors);if($errors){throw 'Trusted package fixture parse failed.'}
foreach($definition in $ast.FindAll({param($node) ($node -is [Management.Automation.Language.FunctionDefinitionAst]) -and ($node.Name -in @('New-PackageFixture','Write-PackageDependencyFixture'))},$true)){. ([scriptblock]::Create($definition.Extent.Text))}
$clean=Compile 'clean' 'public static class Fixture{public static string Value()=>"ordinary disconnected synthetic fixture";}'
$package=New-PackageFixture 'package'
foreach($name in Get-FoundationEntryNames){Copy-Item -LiteralPath $clean.Assembly -Destination (Join-Path $package.Runtime ($name+'.dll')) -Force}
Write-PackageDependencyFixture $package.Package
$archive=New-FoundationPackageArchive $package.Package $package.Zip
$result=Invoke-FoundationPackageSecretReview $package.Zip $archive.Sha256 $tool $toolHash ''
$report=Get-Content -LiteralPath (Join-Path $result.runDirectory 'findings.json') -Raw|ConvertFrom-Json -AsHashtable
Check ($result.state -ceq 'NoFindingsInCoveredSurfaces') 'Clean synthetic package scan was not clean.'
Check ($report.archive.sha256 -ceq $archive.Sha256) 'Report lost exact archive pin.'
Check ($report.coveredEntries.Count -eq $archive.EntryCount) 'Not every synthetic entry was classified.'
Check (@($report.coveredEntries|Where-Object {$_.surface -ceq 'ManagedUserStrings'}).Count -eq 3) 'Entry DLL metadata coverage is missing.'
Check (@($report.limits.Values|Where-Object {$_ -isnot [bool] -or $_}).Count -eq 0) 'Report made unsupported assurance claim.'
Check ($report.sources.Count -eq 4) 'Trusted helper source hashes missing.'
foreach($name in @('execution','projectionIndex')){Check ($report[$name].sha256 -ceq (Get-FileHash -LiteralPath (Join-Path $result.runDirectory $report[$name].path)).Hash.ToLowerInvariant()) 'Private execution/projection hash binding is wrong.'}
$publicJson=[IO.File]::ReadAllText((Join-Path $result.runDirectory 'findings.json'))
Check (!$publicJson.Contains($result.runDirectory) -and !$publicJson.Contains($package.Zip) -and !$publicJson.Contains($token)) 'Public report leaked local paths or token.'
$execution=Get-Content -LiteralPath (Join-Path $result.runDirectory 'execution.json') -Raw|ConvertFrom-Json -AsHashtable
Check (($execution.tool.arguments -contains '--ignore-gitleaks-allow') -and ($execution.tool.arguments -contains '--redact=100') -and ($execution.tool.arguments -contains '32')) 'Fixed scanner contract was not recorded.'
Check (!(Test-Path -LiteralPath (Join-Path $result.runDirectory 'raw-redacted.json'))) 'Raw package scanner report persisted.'
$damaged=New-PackageFixture 'malformed-package'
foreach($name in Get-FoundationEntryNames){Copy-Item -LiteralPath $clean.Assembly -Destination (Join-Path $damaged.Runtime ($name+'.dll')) -Force}
[IO.File]::WriteAllBytes((Join-Path $damaged.Runtime 'SelfService.Foundation.Host.dll'),[byte[]]@(77,90,0,0))
Write-PackageDependencyFixture $damaged.Package;$damagedArchive=New-FoundationPackageArchive $damaged.Package $damaged.Zip
Reject {Invoke-FoundationPackageSecretReview $damaged.Zip $damagedArchive.Sha256 $tool $toolHash ''} '*PackageSecretReviewRejected*'
# An aligned plain UTF16 token is rejected by admission; odd PE offsets can evade that coarse check.
# If this fixture is admitted, the complete package scan must still report its managed string finding.
$secretPackage=New-PackageFixture 'literal-package'
foreach($name in Get-FoundationEntryNames){Copy-Item -LiteralPath $clean.Assembly -Destination (Join-Path $secretPackage.Runtime ($name+'.dll')) -Force}
Copy-Item -LiteralPath $literal.Assembly -Destination (Join-Path $secretPackage.Runtime 'SelfService.Foundation.Host.dll') -Force
Write-PackageDependencyFixture $secretPackage.Package
$admissionRejected=$false
try{$secretArchive=New-FoundationPackageArchive $secretPackage.Package $secretPackage.Zip}catch{if($_.Exception.Message -notlike '*Credential-shaped*'){throw};$admissionRejected=$true}
if(!$admissionRejected){
    $secretResult=Invoke-FoundationPackageSecretReview $secretPackage.Zip $secretArchive.Sha256 $tool $toolHash ''
    Check ($secretResult.state -ceq 'FindingsRequireReview') 'Admitted managed-literal package incorrectly appeared clean.'
    $secretReport=[IO.File]::ReadAllText((Join-Path $secretResult.runDirectory 'findings.json'))
    Check (!$secretReport.Contains($token) -and !$secretReport.Contains($fixtureRoot) -and !$secretReport.Contains($secretResult.runDirectory)) 'Full package findings leaked raw token/path.'
    Check (!(Test-Path -LiteralPath $executionMarker)) 'Full package scan executed package initializer.'
}else{Check $true 'Admission rejected the planted fixture before scan.'}
Reject {Invoke-FoundationPackageSecretReview $package.Zip ('0'*64) $tool $toolHash ''} '*PackageSecretReviewRejected*'
Reject {Invoke-FoundationPackageSecretReview $package.Zip $archive.Sha256 $tool ('0'*64) ''} '*ReviewedScannerPinRequired*'
Reject {Invoke-FoundationPackageSecretReview $package.Zip $archive.Sha256 $tool $toolHash $result.runDirectory} '*FreshDirectoryRequired*'
Reject {Invoke-FoundationPackageSecretReview $package.Zip $archive.Sha256 $tool $toolHash (Join-Path $fixtureRoot 'wrong-output')} '*FreshOwnedOutputRequired*'

# Admission guards run before extraction or scanner. Invalid ZIP inputs cannot become clean secret evidence.
foreach($name in @('runtime/./alias.dll','runtime//alias.dll','runtime/CON.dll','../escape.txt')){
    $path=Join-Path $fixtureRoot ([guid]::NewGuid().ToString('N')+'.zip');$zip=[IO.Compression.ZipFile]::Open($path,[IO.Compression.ZipArchiveMode]::Create)
    try{$null=$zip.CreateEntry($name)}finally{$zip.Dispose()}
    Reject {Invoke-FoundationPackageSecretReview $path (Get-FileHash -LiteralPath $path).Hash.ToLowerInvariant() $tool $toolHash ''} '*PackageSecretReviewRejected*'
}
$accepted=@{synthetic=$result}
if(!$admissionRejected){$accepted.plantedManagedLiteral=$secretResult}
if($actualArchiveArgument -or $actualArchiveHashArgument){
    if(!$actualArchiveArgument -or !$actualArchiveHashArgument){throw 'Both published archive and exact SHA are required.'}
    $actual=Invoke-FoundationPackageSecretReview $actualArchiveArgument $actualArchiveHashArgument $tool $toolHash ''
    $accepted.actual=$actual
    Check ($actual.state -in @('NoFindingsInCoveredSurfaces','FindingsRequireReview')) 'Actual package has no bounded completed review.'
}
$summary=[ordered]@{schema='platform-studio.foundation-package-secret-tests.v1';passed=$true;checks=$script:checks;profile=if($actualArchiveArgument){'SyntheticAndPinnedActual'}else{'SyntheticOnly'};
    utf16ManagedLiteralDetected=$true;coarseAdmissionRejectedCompiledLiteral=$admissionRejected;packageCodeExecuted=$false;scannerVersion='8.30.1';scannerSha256=$toolHash;runs=$accepted;limits=(Get-FPSLimits)}
$null=Write-FTEJson (Join-Path $fixtureRoot 'summary.json') $summary
Write-Host "Foundation package secret review contracts passed: $script:checks. $fixtureRoot"
