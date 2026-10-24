#requires -Version 7.4
[CmdletBinding()]
param([string]$RuntimeDirectory='', [string]$NuGetPackagesDirectory='', [string]$NuGetSdkDirectory='', [string]$SpdxSchemaPath='')
$ErrorActionPreference='Stop'
$repo=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$generator=Join-Path $repo 'scripts/New-FoundationDependencyEvidence.ps1'
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile($generator,[ref]$tokens,[ref]$errors)
if($errors){throw($errors | Out-String)}
foreach($function in $ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)){. ([scriptblock]::Create($function.Extent.Text))}
if($SpdxSchemaPath -and (Get-FDHash $SpdxSchemaPath) -cne '239208b7ac287b3cf5d9a9af23f9d69863971102a5e1587a27a398b43490b89b'){throw 'OfficialSpdxSchemaHashMismatch'}
$testRoot=Join-Path $repo ('artifacts/foundation-dependency-tests/'+[Guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path $testRoot
$script:checks=0
function Check([bool]$Condition,[string]$Message){if(!$Condition){throw $Message};$script:checks++}
function Reject([scriptblock]$Action,[string]$Reason){$rejected=$false;try{&$Action|Out-Null}catch{$rejected=$true;if($_.Exception.Message -notlike $Reason){throw}};Check $rejected ('Expected rejection: '+$Reason)}
function WriteJson([string]$Path,$Value){[IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 30),[Text.UTF8Encoding]::new($false))}
function NewFixture([string]$Name,[string]$License='MIT',[string]$UnsafePath='',[switch]$MissingLicense,[switch]$FileLicense,[switch]$ExternalXml){
    $root=Join-Path $testRoot $Name;$runtime=Join-Path $root 'runtime';$cache=Join-Path $root 'cache'
    $packageDirectory=Join-Path $cache 'fixture.dependency/1.2.3'
    $null=New-Item -ItemType Directory -Path $runtime,$packageDirectory -Force
    $packagePath=Join-Path $packageDirectory 'fixture.dependency.1.2.3.nupkg'
    $stream=[IO.File]::Open($packagePath,[IO.FileMode]::CreateNew)
    $zip=[IO.Compression.ZipArchive]::new($stream,[IO.Compression.ZipArchiveMode]::Create,$false)
    try{
        $licenseXml=if($MissingLicense){''}elseif($FileLicense){'<license type="file">LICENSE.txt</license>'}else{'<license type="expression">'+$License+'</license>'}
        $nuspec='<?xml version="1.0"?><package><metadata><id>Fixture.Dependency</id><version>1.2.3</version>'+$licenseXml+'</metadata></package>'
        if($ExternalXml){$nuspec='<!DOCTYPE package [<!ENTITY xxe SYSTEM "file:///not-permitted">]><package><metadata><id>&xxe;</id></metadata></package>'}
        $entries=[ordered]@{'fixture.dependency.nuspec'=$nuspec;'lib/net8.0/Fixture.Dependency.dll'='synthetic dependency bytes';'NOTICE.txt'='Synthetic notice; not a commercial clearance.';'build/Fixture.Dependency.targets'='<Project><Target Name="NeverExecute" BeforeTargets="Build"><Error Text="Untrusted package build code must never execute"/></Target></Project>'}
        if($FileLicense){$entries['LICENSE.txt']='Synthetic custom license text retained without inferring an SPDX license ID.'}
        if($UnsafePath){$entries[$UnsafePath]='untrusted traversal entry'}
        foreach($path in $entries.Keys){$entry=$zip.CreateEntry($path);$entry.LastWriteTime=[DateTimeOffset]::new(2000,1,1,0,0,0,[TimeSpan]::Zero);$writer=[IO.StreamWriter]::new($entry.Open(),[Text.UTF8Encoding]::new($false));try{$writer.Write($entries[$path])}finally{$writer.Dispose()}}
    }finally{$zip.Dispose();$stream.Dispose()}
    $hash=[Convert]::ToBase64String([Convert]::FromHexString((Get-FDHash $packagePath 'SHA512')))
    [IO.File]::WriteAllText($packagePath+'.sha512',$hash)
    WriteJson (Join-Path $packageDirectory '.nupkg.metadata') ([ordered]@{version=2;contentHash=$hash;source='https://example.invalid/fixture-only'})
    [IO.File]::WriteAllText((Join-Path $runtime 'Fixture.Dependency.dll'),'synthetic dependency bytes')
    foreach($name in @('SelfService.Foundation.Host','SelfService.Foundation.Worker')){
        $project=Join-Path $root ('src/'+$name);$null=New-Item -ItemType Directory -Path $project -Force
        [IO.File]::WriteAllText((Join-Path $project ($name+'.csproj')),'<Project Sdk="Microsoft.NET.Sdk"/>')
        [IO.File]::WriteAllText((Join-Path $runtime ($name+'.dll')),'synthetic first-party bytes')
        $deps=[ordered]@{runtimeTarget=[ordered]@{name='.NETCoreApp,Version=v10.0'};targets=@{'.NETCoreApp,Version=v10.0'=@{
            ($name+'/1.0.0')=@{runtime=@{($name+'.dll')=@{}};dependencies=@{'Fixture.Dependency'='1.2.3'}};
            'Fixture.Dependency/1.2.3'=@{runtime=@{'lib/net8.0/Fixture.Dependency.dll'=@{}}}}};
            libraries=@{($name+'/1.0.0')=@{type='project';sha512=''};'Fixture.Dependency/1.2.3'=@{type='package';sha512='sha512-'+$hash;path='fixture.dependency/1.2.3';hashPath='fixture.dependency.1.2.3.nupkg.sha512'}}}
        WriteJson (Join-Path $runtime ($name+'.deps.json')) $deps
        WriteJson (Join-Path $runtime ($name+'.runtimeconfig.json')) @{runtimeOptions=@{tfm='net10.0';frameworks=@(@{name='Microsoft.NETCore.App';version='10.0.0'})}}
        WriteJson (Join-Path $project 'packages.lock.json') @{version=1;dependencies=@{net10=@{'Fixture.Dependency'=@{type='Direct';resolved='1.2.3';contentHash=$hash}}}}
    }
    foreach($vendor in @('azure-skills','azure-devops-skills','microsoft-learn-skills','azure-verified-modules')){
        $vendorDir=Join-Path $root ('vendor/'+$vendor);$null=New-Item -ItemType Directory -Path $vendorDir -Force
        $licenseFile=Join-Path $vendorDir 'LICENSE';[IO.File]::WriteAllText($licenseFile,'Synthetic upstream license, not executable instructions.')
        $bundle=[ordered]@{schemaVersion=1;repository='https://github.com/example/fixture';commit=('a'*40);license='MIT';files=@{LICENSE=Get-FDHash $licenseFile}}
        if($vendor -cne 'azure-skills'){$bundle.files=@(@{path='LICENSE';sha256=Get-FDHash $licenseFile})}
        if($vendor -ceq 'azure-verified-modules'){$bundle.Remove('commit');$bundle.modules=@(@{sourceRepository='https://github.com/example/fixture';sourceCommit=('a'*40);id='fixture-module';version='1.0.0'})}
        WriteJson (Join-Path $vendorDir 'bundle.json') $bundle
    }
    [pscustomobject]@{Root=$root;Runtime=$runtime;Cache=$cache;Package=$packagePath;Output=(Join-Path $root 'artifacts/foundation-dependency-evidence/report')}
}
function Evidence($Fixture,[string]$Name='report'){
    New-FoundationDependencyEvidence -RuntimeDirectory $Fixture.Runtime -NuGetPackagesDirectory $Fixture.Cache -OutputDirectory (Join-Path $Fixture.Root ('artifacts/foundation-dependency-evidence/'+$Name)) -RepositoryRoot $Fixture.Root -CreatedUtc '2026-10-24T20:00:00Z' -NuGetSdkDirectory $NuGetSdkDirectory
}
function Report($Fixture,[string]$Name='report'){Read-FDJson (Join-Path $Fixture.Root ('artifacts/foundation-dependency-evidence/'+$Name+'/dependency-evidence.json'))}
function HasIssue($Report,[string]$Code){@($Report.issues|Where-Object code -CEQ $Code).Count -gt 0}

$valid=NewFixture 'valid';$result=Evidence $valid;$report=Report $valid
Check $result.packageEvidenceComplete 'Valid fixture package evidence did not complete.'
Check ($report.technicalStatus -ceq 'Blocked' -and (HasIssue $report 'FirstPartyLicenseUndeclared')) 'Undeclared first-party rights were hidden.'
Check ($report.runtimeFiles.Count -eq 7 -and $report.packages.Count -eq 1 -and $report.projects.Count -eq 2 -and $report.sourceDistribution.Count -eq 4) 'Fixture closure counts differ.'
Check (!$report.binaryLicenseCoverageVerified -and !$report.signatureTrustVerified -and !$report.legalClearance -and !$report.signed -and !$report.sourceFreezeVerified -and !$report.productionReady) 'Evidence overstated release qualification.'
Check ($report.packages[0].cacheArchiveHashVerified -and $report.packages[0].depsContentHashVerified -and $report.packages[0].publishedAssets.Count -eq 1) 'Package hashes or actual asset bytes were not verified.'
Check (@($report.sourceDistribution | Where-Object includedInRuntimeBinaryClosure).Count -eq 0) 'Source vendors were mislabeled as binary dependencies.'
$sbom=Read-FDJson (Join-Path $valid.Output 'sbom.spdx.json')
Check ($sbom.spdxVersion -ceq 'SPDX-2.3' -and $sbom.packages.Count -eq 3 -and $sbom.files.Count -eq 7) 'SPDX component/file inventory differs.'
Check (@($sbom.relationships | Where-Object relationshipType -CEQ 'DEPENDS_ON').Count -eq 2) 'Published dependency graph edges were dropped.'
$before=Get-FDHash $valid.Package 'SHA512';$repeat=Evidence $valid 'repeat'
foreach($name in @('dependency-evidence.json','sbom.spdx.json','THIRD-PARTY-NOTICES.txt','MANIFEST.sha256')){Check ((Get-FDHash (Join-Path $valid.Output $name)) -ceq (Get-FDHash (Join-Path $repeat.outputDirectory $name))) 'Identical input evidence is not deterministic.'}
Check ((Get-FDHash $valid.Package 'SHA512') -ceq $before) 'Read-only package cache was changed.'
Check (![IO.File]::ReadAllText((Join-Path $valid.Output 'dependency-evidence.json')).Contains($valid.Root)) 'Evidence leaked an absolute local source/cache/runtime path.'
$timed=New-FoundationDependencyEvidence $valid.Runtime $valid.Cache (Join-Path $valid.Root 'artifacts/foundation-dependency-evidence/later-time') $valid.Root '2026-10-24T21:00:00Z' $NuGetSdkDirectory
Check ($timed.inputSha256 -cne $result.inputSha256) 'Different explicit timestamps reused the SPDX document namespace.'
foreach($line in [IO.File]::ReadAllLines((Join-Path $valid.Output 'MANIFEST.sha256'))){Check ($line -cmatch '\A([a-f0-9]{64})  ([A-Za-z0-9.-]+)\z' -and (Get-FDHash (Join-Path $valid.Output $Matches[2])) -ceq $Matches[1]) 'Output hash manifest is incomplete or incorrect.'}
Reject {Evidence $valid} '*DependencyFreshOwnedOutputRequired*'
Reject {New-FoundationDependencyEvidence $valid.Runtime $valid.Cache (Join-Path $valid.Root 'outside') $valid.Root '2026-10-24T20:00:00Z' $NuGetSdkDirectory} '*DependencyFreshOwnedOutputRequired*'
Reject {New-FoundationDependencyEvidence $valid.Runtime $valid.Cache (Join-Path $valid.Root 'artifacts/foundation-dependency-evidence/invalid-time') $valid.Root 'today' $NuGetSdkDirectory} '*DependencyExplicitUtcTimestampRequired*'
$tampered=NewFixture 'archive-tamper';[IO.File]::AppendAllText($tampered.Package,'changed');$null=Evidence $tampered;Check (HasIssue (Report $tampered) 'DependencyArchiveHashMismatch') 'Archive tampering was not blocked.'
$asset=NewFixture 'asset-tamper';[IO.File]::WriteAllText((Join-Path $asset.Runtime 'Fixture.Dependency.dll'),'different bytes');$null=Evidence $asset;Check (HasIssue (Report $asset) 'DependencyPublishedAssetMismatch') 'Published asset mismatch was not blocked.'
$extra=NewFixture 'unknown-runtime-file';[IO.File]::WriteAllText((Join-Path $extra.Runtime 'unknown.dll'),'unattributed dependency');$null=Evidence $extra;Check (HasIssue (Report $extra) 'DependencyRuntimeFileUnattributed') 'An unexplained runtime binary was silently accepted.'
$unknown=NewFixture 'unsupported-license' 'Made-Up-License';$null=Evidence $unknown;Check (HasIssue (Report $unknown) 'DependencyUnsupportedLicenseExpression') 'Unknown license was silently inferred.'
Check ((Report $unknown).packages[0].licenseDeclared -ceq 'NOASSERTION') 'Rejected license was not classified NOASSERTION.'
$missing=NewFixture 'missing-license' -MissingLicense;$null=Evidence $missing;Check (HasIssue (Report $missing) 'DependencyDeclaredLicenseMissing') 'Missing license was not blocked.'
foreach($path in @('../escape','folder\escape','folder/%2e%2e/escape')){$unsafe=NewFixture ('unsafe-'+$script:checks) -UnsafePath $path;$null=Evidence $unsafe;Check (HasIssue (Report $unsafe) 'DependencyUnsafeRelativePath') 'Unsafe ZIP path was not rejected.'}
$custom=NewFixture 'custom-license' -FileLicense;$null=Evidence $custom;$customReport=Report $custom
Check ($customReport.packageEvidenceComplete -and $customReport.packages[0].licenseDeclared.StartsWith('LicenseRef-')) 'Declared license file was guessed or omitted.'
$customSbom=Read-FDJson (Join-Path $custom.Output 'sbom.spdx.json');Check ($customSbom.hasExtractedLicensingInfos.Count -eq 1 -and $customSbom.hasExtractedLicensingInfos[0].extractedText.Contains('Synthetic custom license')) 'LicenseRef text was not retained.'
$xml=NewFixture 'external-xml' -ExternalXml;$null=Evidence $xml;Check (!(Report $xml).packageEvidenceComplete) 'External XML entities were not rejected.'
$vendor=NewFixture 'vendor-tamper';[IO.File]::AppendAllText((Join-Path $vendor.Root 'vendor/azure-skills/LICENSE'),'changed');Reject {Evidence $vendor} '*DependencyVendorHashMismatch*'
$lock=NewFixture 'lock-tamper';$lockFile=Join-Path $lock.Root 'src/SelfService.Foundation.Host/packages.lock.json';$changed=Read-FDJson $lockFile;$changed.dependencies.net10['Fixture.Dependency'].contentHash='A'*86+'==';WriteJson $lockFile $changed;Reject {Evidence $lock} '*DependencyLockMismatch*'
$ambiguous=NewFixture 'duplicate-json';$depsPath=Join-Path $ambiguous.Runtime 'SelfService.Foundation.Host.deps.json';$text=[IO.File]::ReadAllText($depsPath);[IO.File]::WriteAllText($depsPath,'{"libraries":{},'+$text.TrimStart().Substring(1));Reject {Evidence $ambiguous} '*DependencyAmbiguousJson*'
if($SpdxSchemaPath){Check (Test-Json -Json ([IO.File]::ReadAllText((Join-Path $valid.Output 'sbom.spdx.json'))) -SchemaFile $SpdxSchemaPath -ErrorAction Stop) 'Official SPDX schema rejected fixture SBOM.';Check (Test-Json -Json ([IO.File]::ReadAllText((Join-Path $custom.Output 'sbom.spdx.json'))) -SchemaFile $SpdxSchemaPath -ErrorAction Stop) 'Official SPDX schema rejected LicenseRef SBOM.'}
$actual=$null
if($RuntimeDirectory){
    if(!$NuGetPackagesDirectory){throw'Actual package evidence needs an explicit read-only NuGetPackagesDirectory.'}
    $actualOutput=Join-Path $repo ('artifacts/foundation-dependency-evidence/acceptance-'+[Guid]::NewGuid().ToString('N'))
    $actual=New-FoundationDependencyEvidence $RuntimeDirectory $NuGetPackagesDirectory $actualOutput $repo '2026-10-24T20:00:00Z' $NuGetSdkDirectory
    $actualReport=Read-FDJson (Join-Path $actualOutput 'dependency-evidence.json')
    Check $actualReport.packageEvidenceComplete 'Actual published dependency package verification failed.'
    Check ($actualReport.runtimeFiles.Count -eq @(Get-FDFiles $RuntimeDirectory).Count) 'Actual runtime file hash coverage is incomplete.'
    Check ($actualReport.projects.id -contains 'SelfService.Foundation.Host' -and $actualReport.projects.id -contains 'SelfService.Foundation.Worker') 'Actual API/worker entry closure is incomplete.'
    if($SpdxSchemaPath){Check (Test-Json -Json ([IO.File]::ReadAllText((Join-Path $actualOutput 'sbom.spdx.json'))) -SchemaFile $SpdxSchemaPath -ErrorAction Stop) 'Official SPDX schema rejected actual SBOM.'}
}
$receipt=[ordered]@{schema='platform-studio.foundation-dependency-tests.v1';checks=$script:checks;passed=$true;actualPublishedClosure=$actual;
    officialSpdxSchemaValidated=[bool]$SpdxSchemaPath;officialSpdxSchemaSha256=if($SpdxSchemaPath){Get-FDHash $SpdxSchemaPath}else{$null};
    cloudOperationsPerformed=$false;legalClearance=$false;productionReady=$false}
WriteJson (Join-Path $testRoot 'summary.json') $receipt
Write-Host "Foundation dependency evidence: $script:checks checks passed. $testRoot"
