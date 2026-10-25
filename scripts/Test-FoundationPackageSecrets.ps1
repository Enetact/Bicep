#requires -Version 7.4
[CmdletBinding()]
param(
    [string]$ArchivePath='', [string]$ExpectedArchiveSha256='', [string]$GitleaksPath='',
    [string]$ExpectedGitleaksSha256='200df852fdecbedb19a33960657333cba5e231740bc8968972b507b50f93b194',
    [string]$OutputDirectory=''
)
$ErrorActionPreference='Stop'
. (Join-Path $PSScriptRoot 'FoundationTestExecutionEvidence.ps1')

function Initialize-FPSHelpers {
    $metadataPath=Join-Path $PSScriptRoot 'FoundationPackageSecretMetadata.cs'
    $metadataHash=(Get-FileHash -LiteralPath $metadataPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if(!('FoundationPackageSecretMetadata' -as [type])){Add-Type -Path $metadataPath;$script:FPSLoadedMetadataSha=$metadataHash}
    elseif(!(Get-Variable -Name FPSLoadedMetadataSha -Scope Script -ErrorAction SilentlyContinue) -or ($script:FPSLoadedMetadataSha -cne $metadataHash)){throw 'PackageSecretHelperChangedRestartRequired'}
    $tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot 'Publish-Foundation.ps1'),[ref]$tokens,[ref]$errors)
    if($errors){throw 'PackageSecretTrustedHelperInvalid'}
    # Definitions from the trusted checkout only; the publisher entry point is never executed.
    foreach($function in $ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]},$true)){
        . ([scriptblock]::Create('function script:'+$function.Name+$function.Extent.Text.Substring(('function '+$function.Name).Length)))
    }
}
function Get-FPSStreamHash([IO.Stream]$Stream) {
    $Stream.Position=0;[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Stream)).ToLowerInvariant()
}
function Assert-FPSPin([string]$Hash) {if($Hash -cnotmatch '\A[a-f0-9]{64}\z'){throw 'PackageSecretPinRequired'}}
function Get-FPSLimits {
    [ordered]@{independentAttestation=$false;fullBinarySecretCoverage=$false;nativeBinarySecretCoverage=$false;
        resourcesAndBlobHeapsScanned=$false;computedOrObfuscatedSecretsScanned=$false;gitHistoryScanned=$false;
        runtimeCachesScanned=$false;environmentAttested=$false;noNetworkAttested=$false;descendantProcessCleanupVerified=$false;
        signatureTrustVerified=$false;securityClearance=$false;legalClearance=$false;productionReady=$false;publicationAuthorized=$false}
}
function Get-FPSArguments([string]$InputDirectory,[string]$Config,[string]$Ignore,[string]$Report) {
    @('dir',$InputDirectory,'--config',$Config,'--gitleaks-ignore-path',$Ignore,'--ignore-gitleaks-allow','--redact=100',
        '--report-format','json','--report-path',$Report,'--exit-code','17','--timeout','120','--max-archive-depth','0',
        '--max-decode-depth','5','--max-target-megabytes','32','--no-banner','--no-color','--log-level','error')
}
function Get-FPSExitState([int]$ExitCode,[object[]]$Findings) {
    if(($ExitCode -eq 0) -and ($Findings.Count -eq 0)){return 'NoFindingsInCoveredSurfaces'}
    if(($ExitCode -eq 17) -and ($Findings.Count -gt 0)){return 'FindingsRequireReview'}
    throw 'PackageSecretToolOrReportMismatch'
}
function ConvertTo-FPSText([byte[]]$Bytes) {
    if($Bytes.Length -gt 16777216){throw 'PackageSecretTextLimit'}
    if(($Bytes.Length -ge 2) -and ($Bytes[0] -eq 255) -and ($Bytes[1] -eq 254)){
        $text=[Text.UnicodeEncoding]::new($false,$false,$true).GetString($Bytes,2,$Bytes.Length-2)
    }elseif(($Bytes.Length -ge 2) -and ($Bytes[0] -eq 254) -and ($Bytes[1] -eq 255)){
        $text=[Text.UnicodeEncoding]::new($true,$false,$true).GetString($Bytes,2,$Bytes.Length-2)
    }else{
        if($Bytes -contains [byte]0){throw 'PackageSecretTextEncodingUnsupported'}
        $text=[Text.UTF8Encoding]::new($false,$true).GetString($Bytes)
    }
    $text=[regex]::Replace($text,'[\x00-\x08\x0b\x0c\x0e-\x1f\x7f]',"`n").Replace("`r`n","`n").Replace("`r","`n")
    $encoded=[Text.UTF8Encoding]::new($false,$true).GetBytes($text)
    if($encoded.Length -gt 16777216){throw 'PackageSecretTextLimit'}
    ,$encoded
}
function Get-FPSProjectionInventory([string]$Directory,$Map) {
    $children=@(Get-ChildItem -LiteralPath $Directory -Force)
    if(@($children|Where-Object {$_.PSIsContainer}).Count){throw 'PackageSecretProjectionChanged'}
    $files=@($children)
    if($files.Count -ne $Map.Count){throw 'PackageSecretProjectionChanged'}
    $result=foreach($file in @($files|Sort-Object Name -CaseSensitive)){
        $null=Assert-FTEUnlinkedPath $file.FullName
        if(!$Map.ContainsKey($file.Name) -or $file.Length -gt 16777216){throw 'PackageSecretProjectionChanged'}
        [ordered]@{file=$file.Name;bytes=$file.Length;sha256=(Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}
    }
    ,@($result)
}
function ConvertTo-FPSSafeFindings([string]$InputDirectory,$Map,[byte[]]$Bytes) {
    if($Bytes.Length -gt 16777216){throw 'PackageSecretReportLimit'}
    $text=[Text.UTF8Encoding]::new($false,$true).GetString($Bytes)
    $document=[Text.Json.JsonDocument]::Parse($text)
    try{
        if($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Array){throw 'PackageSecretReportInvalid'}
        $stack=[Collections.Generic.Stack[Text.Json.JsonElement]]::new();$stack.Push($document.RootElement)
        while($stack.Count){$node=$stack.Pop();if($node.ValueKind -eq [Text.Json.JsonValueKind]::Object){
            $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
            foreach($property in $node.EnumerateObject()){if(!$seen.Add($property.Name)){throw 'PackageSecretReportAmbiguous'};$stack.Push($property.Value)}
        }elseif($node.ValueKind -eq [Text.Json.JsonValueKind]::Array){foreach($child in $node.EnumerateArray()){$stack.Push($child)}}}
    }finally{$document.Dispose()}
    $records=$text|ConvertFrom-Json -AsHashtable -Depth 32 -NoEnumerate
    if($records -isnot [array] -or $records.Count -gt 100000){throw 'PackageSecretReportInvalid'}
    $safe=[Collections.Generic.List[object]]::new()
    foreach($finding in $records){
        if(($finding -isnot [Collections.IDictionary]) -or ($finding.RuleID -isnot [string]) -or
            ($finding.RuleID -cnotmatch '\A[A-Za-z0-9._-]{1,128}\z') -or ($finding.File -isnot [string]) -or
            (($finding.StartLine -isnot [long]) -and ($finding.StartLine -isnot [int])) -or ($finding.StartLine -lt 1) -or
            ($finding.Secret -isnot [string]) -or ($finding.Secret -cnotin @('REDACTED','[REDACTED]'))){throw 'PackageSecretReportInvalidOrUnredacted'}
        $path=[IO.Path]::GetFullPath($finding.File,$InputDirectory)
        if(![string]::Equals((Split-Path $path -Parent),$InputDirectory,[StringComparison]::OrdinalIgnoreCase)){throw 'PackageSecretReportUnknownInput'}
        $name=[IO.Path]::GetFileName($path)
        if(!$Map.ContainsKey($name)){throw 'PackageSecretReportUnknownInput'}
        $entry=$Map[$name]
        if($finding.StartLine -gt $entry.lines){throw 'PackageSecretReportLineInvalid'}
        $offsets=@();if($entry.surface -ceq 'ManagedUserStrings'){
            $offsets=@($entry.ranges|Where-Object {($_.StartLine -le $finding.StartLine) -and ($_.EndLine -ge $finding.StartLine)}|ForEach-Object {$_.HeapOffset})
            if(!$offsets.Count){throw 'PackageSecretReportLineInvalid'}
        }
        # No package path, scanner path, match, secret, fingerprint or scanner error text crosses this boundary.
        $safe.Add([ordered]@{ruleId=$finding.RuleID;entryId=$entry.entryId;pathSha256=$entry.pathSha256;surface=$entry.surface;
            projection=$name;line=$finding.StartLine;userStringHeapOffsets=$offsets;redacted=$true})
    }
    ,$safe.ToArray()
}
function Invoke-FPSGitleaks([string]$Tool,[string]$ExpectedToolSha256,[string]$Run,[string]$Inputs,$Map) {
    Assert-FPSPin $ExpectedToolSha256
    $null=Assert-FTEUnlinkedPath $Tool
    $raw=$null;$handle=[IO.FileStream]::new($Tool,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        if(($handle.Length -gt 67108864) -or ((Get-FPSStreamHash $handle) -cne $ExpectedToolSha256)){throw 'PackageSecretToolPinMismatch'}
        $version=[FoundationPackageSecretMetadata]::Run($Tool,@('version'),$Run,10000)
        if(($version.ExitCode -ne 0) -or ($version.Stdout.Trim() -cne '8.30.1') -or $version.Stderr.Trim()){throw 'PackageSecretToolVersionMismatch'}
        $config=Join-Path $Run 'gitleaks-defaults.toml';$ignore=Join-Path $Run 'empty.gitleaksignore';$raw=Join-Path $Run 'raw-redacted.json'
        $configPin=Write-FTEFile $config ([Text.Encoding]::UTF8.GetBytes("[extend]`nuseDefault = true`n"));$ignorePin=Write-FTEFile $ignore ([byte[]]::new(0))
        $before=Get-FPSProjectionInventory $Inputs $Map
        $arguments=Get-FPSArguments $Inputs $config $ignore $raw
        $observation=[FoundationPackageSecretMetadata]::Run($Tool,$arguments,$Run,150000)
        $rawHandle=[IO.FileStream]::new($raw,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        try{if($rawHandle.Length -gt 16777216){throw 'PackageSecretReportLimit'};$bytes=[byte[]]::new($rawHandle.Length);$rawHandle.ReadExactly($bytes)}finally{$rawHandle.Dispose()}
        $findings=ConvertTo-FPSSafeFindings $Inputs $Map $bytes
        $state=Get-FPSExitState $observation.ExitCode $findings
        $after=Get-FPSProjectionInventory $Inputs $Map
        if((ConvertTo-Json -InputObject $before -Depth 8 -Compress) -cne (ConvertTo-Json -InputObject $after -Depth 8 -Compress)){throw 'PackageSecretProjectionChanged'}
        if((Get-FPSStreamHash $handle) -cne $ExpectedToolSha256){throw 'PackageSecretToolChanged'}
        [ordered]@{state=$state;findings=$findings;inputs=$before;tool=[ordered]@{name='Gitleaks';version='8.30.1';sha256=$ExpectedToolSha256;
            arguments=$arguments;exitCode=$observation.ExitCode;environment='Cleared; SystemRoot/WINDIR retained; TEMP/TMP/HOME/USERPROFILE set to private run.';
            configurationInputs=@($configPin,$ignorePin);
            configuration='Built-in rules; empty explicit ignore; inline allow ignored; no baseline; redact=100.'}}
    }finally{
        $handle.Dispose()
        # This exact generated file belongs to this fresh private run. No source, package or customer file is removed.
        if($raw -and (Test-Path -LiteralPath $raw -PathType Leaf)){Remove-Item -LiteralPath $raw}
    }
}
function New-FPSPackageProjection([IO.Stream]$ArchiveStream,[string]$Extraction,[string]$Inputs) {
    $ArchiveStream.Position=0;$zip=[IO.Compression.ZipArchive]::new($ArchiveStream,[IO.Compression.ZipArchiveMode]::Read,$true)
    $map=[Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
    $covered=[Collections.Generic.List[object]]::new();$unqualified=[Collections.Generic.List[object]]::new()
    [long]$expanded=0;[long]$projection=0;[long]$metadataEntries=0;$entryId=0
    try{
        foreach($entry in @($zip.Entries|Where-Object {!$_.FullName.EndsWith('/')}|Sort-Object FullName -CaseSensitive)){
            Assert-FoundationArchiveRelativePath $entry.FullName;$entryId++
            $read=Read-FoundationArchiveEntry $entry 67108864 -IncludeBytes;$expanded+=$read.Bytes
            if($expanded -gt 268435456){throw 'PackageSecretExpandedLimit'}
            $pathHash=Get-FTEHash ([Text.Encoding]::UTF8.GetBytes($entry.FullName))
            $destination=Join-Path $Extraction $entry.FullName
            $null=Assert-FTEUnlinkedPath $destination;$null=[IO.Directory]::CreateDirectory((Split-Path $destination -Parent))
            $null=Write-FTEFile $destination ([byte[]]$read.Content) 67108864
            $item=[ordered]@{entryId=$entryId;pathSha256=$pathHash;bytes=$read.Bytes;sha256=$read.Sha256;surface='';projectionBytes=0;userStringEntries=0}
            if($entry.FullName.EndsWith('.dll',[StringComparison]::OrdinalIgnoreCase)){
                $extracted=[FoundationPackageSecretMetadata]::Extract($destination,$Inputs,($entryId.ToString('D6')+'-us'),(134217728-$projection))
                if(!$extracted.HasMetadata){$item.surface='NativePeUnqualified';$unqualified.Add($item);continue}
                $item.surface='ManagedUserStrings';$item.projectionBytes=$extracted.Utf8Bytes;$item.userStringEntries=$extracted.Entries
                $metadataEntries+=$extracted.Entries;if($metadataEntries -gt 200000){throw 'PackageSecretMetadataEntryLimit'}
                foreach($file in $extracted.Files){
                    $ranges=@($extracted.Ranges|Where-Object {$_.File -ceq $file})
                    $map.Add($file,@{entryId=$entryId;pathSha256=$pathHash;surface=$item.surface;ranges=$ranges;lines=($ranges[-1].EndLine+1)})
                }
            }elseif($entry.FullName -match '(?i)\.(?:so|dylib|a|lib)$'){$item.surface='NativeOrRawBinaryUnqualified';$unqualified.Add($item);continue}
            else{
                $text=ConvertTo-FPSText ([byte[]]$read.Content);$file=$entryId.ToString('D6')+'-text.txt'
                if($projection+$text.Length -gt 134217728){throw 'PackageSecretProjectionLimit'}
                $null=Write-FTEFile (Join-Path $Inputs $file) $text
                $item.surface='OrdinaryText';$item.projectionBytes=$text.Length
                $map.Add($file,@{entryId=$entryId;pathSha256=$pathHash;surface=$item.surface;ranges=@();lines=[FoundationPackageSecretMetadata]::CountLines($text)})
            }
            $projection+=$item.projectionBytes;if($projection -gt 134217728){throw 'PackageSecretProjectionLimit'}
            $covered.Add($item)
        }
    }finally{$zip.Dispose()}
    [ordered]@{covered=$covered.ToArray();unqualified=$unqualified.ToArray();map=$map;expandedBytes=$expanded;projectionBytes=$projection;metadataEntries=$metadataEntries}
}
function Invoke-FoundationPackageSecretReview([string]$Archive,[string]$ExpectedArchive,[string]$Tool,[string]$ExpectedTool,[string]$Output) {
    Assert-FPSPin $ExpectedArchive;Assert-FPSPin $ExpectedTool
    if($ExpectedTool -cne '200df852fdecbedb19a33960657333cba5e231740bc8968972b507b50f93b194'){throw 'PackageSecretReviewedScannerPinRequired'}
    $root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'));$parent=Join-Path $root 'artifacts/foundation-package-secrets'
    $null=Assert-FTEUnlinkedPath $Archive;$null=Assert-FTEUnlinkedPath $parent
    $null=[IO.Directory]::CreateDirectory($parent)
    if(!$Output){$Output=Join-Path $parent ([guid]::NewGuid().ToString('N'))}
    $Output=Assert-FTEUnlinkedPath $Output
    if(![string]::Equals((Split-Path $Output -Parent),$parent,[StringComparison]::OrdinalIgnoreCase) -or ([IO.Path]::GetFileName($Output) -cnotmatch '\A[a-f0-9]{32}\z')){throw 'PackageSecretFreshOwnedOutputRequired'}
    $run=New-FTEPrivateDirectory $Output;$archiveHandle=$null;$sourceHandles=[Collections.Generic.List[object]]::new()
    try{
        $sources=foreach($name in @('Test-FoundationPackageSecrets.ps1','FoundationPackageSecretMetadata.cs','Publish-Foundation.ps1','FoundationTestExecutionEvidence.ps1')){
            $sourceHandle=[IO.FileStream]::new((Join-Path $PSScriptRoot $name),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            $sourceRecord=[ordered]@{file=('scripts/'+$name);sha256=(Get-FPSStreamHash $sourceHandle)}
            $sourceHandles.Add(@{handle=$sourceHandle;record=$sourceRecord});$sourceRecord
        }
        Initialize-FPSHelpers
        $archiveHandle=[IO.FileStream]::new($Archive,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
        if(($archiveHandle.Length -gt 268435456) -or ((Get-FPSStreamHash $archiveHandle) -cne $ExpectedArchive)){throw 'PackageSecretArchivePinMismatch'}
        $validation=Test-FoundationArchive $Archive
        $extraction=New-FTEPrivateDirectory (Join-Path $run 'extracted');$inputs=New-FTEPrivateDirectory (Join-Path $run 'inputs')
        $projection=New-FPSPackageProjection $archiveHandle $extraction $inputs
        $index=Write-FTEJson (Join-Path $run 'projection-index.json') ([ordered]@{schema='platform-studio.foundation-package-secret-projection.v1';map=$projection.map;
            covered=$projection.covered;unqualified=$projection.unqualified;inputs=(Get-FPSProjectionInventory $inputs $projection.map)})
        $scan=Invoke-FPSGitleaks $Tool $ExpectedTool $run $inputs $projection.map
        if((Get-FPSStreamHash $archiveHandle) -cne $ExpectedArchive){throw 'PackageSecretArchiveChanged'}
        foreach($source in $sourceHandles){if((Get-FPSStreamHash $source.handle) -cne $source.record.sha256){throw 'PackageSecretSourceChanged'}}
        # Exact argv is private evidence: it contains this run's absolute paths. Public findings never contain these paths.
        $execution=Write-FTEJson (Join-Path $run 'execution.json') ([ordered]@{schema='platform-studio.foundation-package-secret-execution.v1';tool=$scan.tool;sources=@($sources);inputs=$scan.inputs;projectionIndex=$index})
        $report=[ordered]@{schema='platform-studio.foundation-package-secret-review.v1';observedUtc=[DateTimeOffset]::UtcNow.ToString('O');state=$scan.state;
            findingsRequireReview=($scan.findings.Count -gt 0);findingCount=$scan.findings.Count;findings=$scan.findings;
            archive=[ordered]@{sha256=$ExpectedArchive;bytes=$archiveHandle.Length;entries=$validation.Entries;hashesVerified=$validation.HashesVerified};
            tool=[ordered]@{name='Gitleaks';version='8.30.1';sha256=$ExpectedTool;exitCode=$scan.tool.exitCode;redactPercent=100};sources=@($sources);
            coveredEntries=$projection.covered;unqualifiedEntries=$projection.unqualified;projectionBytes=$projection.projectionBytes;
            projectionIndex=$index;execution=$execution;redactionVerified=$true;limits=(Get-FPSLimits)}
        $pin=Write-FTEJson (Join-Path $run 'findings.json') $report
        [pscustomobject]@{state=$scan.state;findingCount=$scan.findings.Count;reportSha256=$pin.sha256;runDirectory=$run}
    }catch{
        $null=Write-FTEJson (Join-Path $run 'rejected.json') ([ordered]@{schema='platform-studio.foundation-package-secret-rejection.v1';state='Rejected';reason='PackageSecretReviewRejected';limits=(Get-FPSLimits)})
        throw 'PackageSecretReviewRejected'
    }finally{if($archiveHandle){$archiveHandle.Dispose()};foreach($source in $sourceHandles){$source.handle.Dispose()}}
}
if($MyInvocation.InvocationName -eq '.'){return}
if(!$GitleaksPath){$GitleaksPath=Join-Path ([IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))) '.tools/gitleaks-8.30.1/gitleaks.exe'}
Invoke-FoundationPackageSecretReview $ArchivePath $ExpectedArchiveSha256 $GitleaksPath $ExpectedGitleaksSha256 $OutputDirectory
