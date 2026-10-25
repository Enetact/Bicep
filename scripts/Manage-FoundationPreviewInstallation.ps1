#requires -Version 7.4
[CmdletBinding()]
param([ValidateSet('Stage','Verify','Repair','Readiness')][string]$Action='Verify', [string]$InstallRoot='',
    [string]$ArchivePath='',[string]$ExpectedArchiveSha256='',[long]$ExpectedGeneration=-1,[string]$ExpectedStateSha256='',
    [long]$Generation=0,[switch]$LocalPreview,[string]$DotNetPath='')
$ErrorActionPreference='Stop'

function Get-FPISid { if(!$IsWindows){throw 'PreviewInstallationWindowsRequired'};[Security.Principal.WindowsIdentity]::GetCurrent().User }
function Assert-FPINewRootNamespace([string]$Root) {
    # The same operational sentinels used by the Owner CLI, including incomplete custody and legacy entries.
    # A valid installation may contain its own installation marker/guard; no operational custody may contain it.
    $markers=@('.activation.guard','authority.json','.installation-owner.json','.installation.guard','.package-owner.json',
        '.foundation-owner-initialization.json','.credential-namespace.json','.vault.lock','.keys.lock','cell-binding.json','.rollback.lock','.backup-checkpoint.guard',
        '.credential-recovery-stage.guard','.credential-recovery-stage.json','.credential-recovery-checkpoint.guard','.credential-recovery-checkpoint.json')
    $leaf=[IO.Path]::GetFileName($Root)
    if(($leaf -iin $markers) -or $leaf.EndsWith('.credential.json',[StringComparison]::OrdinalIgnoreCase) -or
        $leaf.EndsWith('.dpapi.json',[StringComparison]::OrdinalIgnoreCase)){throw 'PreviewInstallationProtectedAncestorNamespace'}
    $ancestor=$Root
    $options=[IO.EnumerationOptions]::new();$options.AttributesToSkip=[IO.FileAttributes]0
    $options.IgnoreInaccessible=$false;$options.MatchCasing=[IO.MatchCasing]::CaseInsensitive
    while($ancestor){
        if([IO.Directory]::Exists($ancestor)){
            $patterns=@($markers|Where-Object { ![string]::Equals($ancestor,$Root,[StringComparison]::OrdinalIgnoreCase) -or $_ -notin @('.installation-owner.json','.installation.guard') })+@('*.credential.json','*.dpapi.json')
            foreach($pattern in $patterns){
                $entries=[IO.Directory]::EnumerateFileSystemEntries($ancestor,$pattern,$options).GetEnumerator()
                try{if($entries.MoveNext()){throw 'PreviewInstallationProtectedAncestorNamespace'}}finally{$entries.Dispose()}
            }
        }
        $next=Split-Path $ancestor -Parent;if($next -eq $ancestor){break};$ancestor=$next
    }
}
function Assert-FPILocalPath([string]$Path) {
    if($Path -cnotmatch '\A[A-Za-z]:\\' -or $Path.Substring(2).Contains(':') -or $Path.Contains('/') -or $Path -match '[\x00-\x1f\x7f]' -or
        @($Path.Substring(3).Split('\')|Where-Object {$_ -in @('','.','..') -or $_.EndsWith('.') -or $_.EndsWith(' ') -or $_ -match '\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)'}).Count){throw 'PreviewInstallationLocalCanonicalPathRequired'}
    $full=[IO.Path]::GetFullPath($Path)
    if($full.TrimEnd('\') -ieq [IO.Path]::GetPathRoot($full).TrimEnd('\') -or [IO.DriveInfo]::new([IO.Path]::GetPathRoot($full)).DriveType -ne [IO.DriveType]::Fixed){throw 'PreviewInstallationLocalCanonicalPathRequired'}
    Assert-FoundationUnlinkedPath $full
    $full.TrimEnd('\')
}
function Assert-FPIAcl([string]$Path,[switch]$Root) {
    Assert-FoundationUnlinkedPath $Path;$sid=Get-FPISid;$acl=Get-Acl -LiteralPath $Path
    if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -cne $sid.Value -or ($Root -and !$acl.AreAccessRulesProtected)){throw 'PreviewInstallationOwnerAclRejected'}
    $rules=@($acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier]));$allowed=$false
    foreach($rule in $rules){
        if($rule.IdentityReference.Value -cne $sid.Value -or $rule.AccessControlType -ne [Security.AccessControl.AccessControlType]::Allow){throw 'PreviewInstallationOwnerAclRejected'}
        if(($rule.FileSystemRights -band [Security.AccessControl.FileSystemRights]::FullControl) -eq [Security.AccessControl.FileSystemRights]::FullControl){$allowed=$true}
    }
    if(!$allowed){throw 'PreviewInstallationOwnerAclRejected'}
}
function New-FPIPrivateRoot([string]$Path) {
    if(!('FoundationPreviewDirectoryProtection' -as [type])){
        Add-Type -TypeDefinition @'
using System; using System.Runtime.InteropServices;
public static class FoundationPreviewDirectoryProtection {
 [StructLayout(LayoutKind.Sequential)] struct Attributes { public int Length; public IntPtr Descriptor; [MarshalAs(UnmanagedType.Bool)] public bool Inherit; }
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]
 static extern bool CreateDirectoryW(string path,ref Attributes attributes);
 public static bool Create(string path,byte[] descriptor) {
  var pinned=GCHandle.Alloc(descriptor,GCHandleType.Pinned);
  try {var attributes=new Attributes{Length=Marshal.SizeOf<Attributes>(),Descriptor=pinned.AddrOfPinnedObject(),Inherit=false};return CreateDirectoryW(path,ref attributes);}
  finally {pinned.Free();}
 }
}
'@
    }
    $sid=Get-FPISid;$acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetOwner($sid);$acl.SetAccessRuleProtection($true,$false)
    $rule=[Security.AccessControl.FileSystemAccessRule]::new($sid,[Security.AccessControl.FileSystemRights]::FullControl,
        [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow)
    $acl.AddAccessRule($rule)
    # Supply the owner-only DACL in the atomic create; no inherited permissive interval or existing-root adoption.
    if(![FoundationPreviewDirectoryProtection]::Create($Path,$acl.GetSecurityDescriptorBinaryForm())){throw 'PreviewInstallationRootReservationRejected'}
    Assert-FPIAcl $Path -Root
}
function Get-FPIHash([byte[]]$Bytes) {[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()}
function Get-FPIJsonHash($Record) {
    $copy=[ordered]@{};foreach($key in $Record.Keys){if($key -cne 'sha256'){$copy[$key]=$Record[$key]}}
    Get-FPIHash ([Text.Encoding]::UTF8.GetBytes((ConvertTo-Json -InputObject $copy -Depth 20 -Compress)))
}
function Assert-FPIArchiveRelative([string]$Path) {
    try{Assert-FoundationArchiveRelativePath $Path}catch{throw 'PreviewInstallationArchivePathRejected'}
    if($Path.Length -gt 1024 -or @($Path.Split('/')|Where-Object {$_.Length -gt 255}).Count){throw 'PreviewInstallationArchivePathRejected'}
}
function Assert-FPIArchiveEntries([object[]]$Entries) {
    $names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($entry in $Entries){Assert-FPIArchiveRelative $entry.FullName;if(!$names.Add($entry.FullName)){throw 'PreviewInstallationArchivePathRejected'}}
}
function Write-FPIRecord([string]$Path,$Record) {
    $Record['sha256']=Get-FPIJsonHash $Record
    $bytes=[Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Record -Depth 20))
    if($bytes.Length -gt 4194304){throw 'PreviewInstallationMetadataLimit'}
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None,8192,[IO.FileOptions]::WriteThrough)
    try{$stream.Write($bytes,0,$bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
    Assert-FPIAcl $Path
}
function Read-FPIJson([string]$Path,[string[]]$Keys,[int]$Maximum=4194304) {
    Assert-FPIAcl $Path
    $stream=[IO.File]::OpenRead($Path);$memory=[IO.MemoryStream]::new()
    try{$buffer=[byte[]]::new(8192);while(($n=$stream.Read($buffer,0,$buffer.Length)) -gt 0){if($memory.Length+$n -gt $Maximum){throw 'PreviewInstallationMetadataLimit'};$memory.Write($buffer,0,$n)};$bytes=$memory.ToArray()}
    finally{$memory.Dispose();$stream.Dispose()}
    $text=[Text.UTF8Encoding]::new($false,$true).GetString($bytes);$options=[System.Text.Json.JsonDocumentOptions]::new();$options.MaxDepth=24
    $doc=[System.Text.Json.JsonDocument]::Parse($text,$options)
    try{
        $stack=[Collections.Generic.Stack[System.Text.Json.JsonElement]]::new();$stack.Push($doc.RootElement)
        while($stack.Count){$node=$stack.Pop();if($node.ValueKind -eq [System.Text.Json.JsonValueKind]::Object){$names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);foreach($property in $node.EnumerateObject()){if(!$names.Add($property.Name)){throw 'PreviewInstallationAmbiguousMetadata'};$stack.Push($property.Value)}}elseif($node.ValueKind -eq [System.Text.Json.JsonValueKind]::Array){foreach($child in $node.EnumerateArray()){$stack.Push($child)}}}
    }finally{$doc.Dispose()}
    $record=ConvertFrom-Json -InputObject $text -AsHashtable -Depth 24
    if($record -isnot [Collections.IDictionary]){throw 'PreviewInstallationMetadataIntegrityFailed'}
    foreach($stringKey in @('schema','scope','installId','ownerSid','rootPath','operation','archiveSha256','previousSha256','sha256')){
        if($record.Contains($stringKey) -and $record[$stringKey] -isnot [string]){throw 'PreviewInstallationMetadataIntegrityFailed'}
    }
    if($record -isnot [Collections.IDictionary] -or $record.Count -ne $Keys.Count -or @($record.Keys|Where-Object {$_ -cnotin $Keys}).Count -or
        $record.sha256 -cnotmatch '\A[a-f0-9]{64}\z' -or (Get-FPIJsonHash $record) -cne $record.sha256){throw 'PreviewInstallationMetadataIntegrityFailed'}
    $record
}
function Open-FPIGuard([string]$Root,[switch]$Exclusive) {
    Assert-FPIAcl $Root -Root;$path=Join-Path $Root '.installation.guard';Assert-FPIAcl $path
    if((Get-Item -LiteralPath $path).Length -ne 0){throw 'PreviewInstallationMetadataIntegrityFailed'}
    try{[IO.FileStream]::new($path,[IO.FileMode]::Open, $(if($Exclusive){[IO.FileAccess]::ReadWrite}else{[IO.FileAccess]::Read}),
        $(if($Exclusive){[IO.FileShare]::None}else{[IO.FileShare]::Read}))}catch [IO.IOException]{throw 'PreviewInstallationBusy'}
}
function Assert-FPIPackageStructure([string]$Package,[object[]]$Inventory) {
    $files=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);$directories=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($file in $Inventory){[void]$files.Add($file.path);$parent=[IO.Path]::GetDirectoryName($file.path.Replace('/','\'));while($parent){[void]$directories.Add($parent.Replace('\','/'));$parent=[IO.Path]::GetDirectoryName($parent)}}
    $pending=[Collections.Generic.Stack[string]]::new();$pending.Push($Package);$count=0
    while($pending.Count){foreach($item in Get-ChildItem -LiteralPath $pending.Pop() -Force){Assert-FPIAcl $item.FullName;$count++;if($count -gt 10000){throw 'PreviewInstallationEntryLimit'}
        $relative=[IO.Path]::GetRelativePath($Package,$item.FullName).Replace('\','/')
        if($item.PSIsContainer){if(!$directories.Contains($relative)){throw 'PreviewInstallationUnknownPackageEntry'};$pending.Push($item.FullName)}elseif(!$files.Contains($relative)){throw 'PreviewInstallationUnknownPackageEntry'}
    }}
}
function Read-FPIState([string]$Root) {
    Assert-FPIAcl $Root -Root
    $expected=@('.installation-owner.json','.installation.guard','generations')
    foreach($entry in Get-ChildItem -LiteralPath $Root -Force){Assert-FPIAcl $entry.FullName;if($entry.Name -cnotin $expected){throw 'PreviewInstallationUnknownEntry'}}
    $marker=Read-FPIJson (Join-Path $Root '.installation-owner.json') @('schema','scope','installId','ownerSid','rootPath','createdUnixMilliseconds','localPreview','sha256') 16384
    if($marker.schema -cne 'platform-studio.foundation-preview-installation.v1' -or $marker.scope -cne 'GenericSoftwareStagingOnly' -or $marker.installId -cnotmatch '\A[a-f0-9]{32}\z' -or
        $marker.ownerSid -cne (Get-FPISid).Value -or ![string]::Equals($marker.rootPath,$Root,[StringComparison]::OrdinalIgnoreCase) -or
        ($marker.createdUnixMilliseconds -isnot [long] -and $marker.createdUnixMilliseconds -isnot [int]) -or $marker.createdUnixMilliseconds -le 0 -or
        $marker.localPreview -isnot [bool] -or !$marker.localPreview){throw 'PreviewInstallationBindingRejected'}
    $folder=Join-Path $Root 'generations';Assert-FPIAcl $folder
    $directories=@(Get-ChildItem -LiteralPath $folder -Force|Sort-Object Name -CaseSensitive)
    if($directories.Count -gt 64){throw 'PreviewInstallationGenerationLimit'}
    $records=[Collections.Generic.List[object]]::new();$previous='';$total=[long]0;$number=0
    foreach($directory in $directories){
        $number++;Assert-FPIAcl $directory.FullName
        if(!$directory.PSIsContainer -or $directory.Name -cne $number.ToString('000000')){throw 'PreviewInstallationPartialOrUnknownGeneration'}
        foreach($entry in Get-ChildItem -LiteralPath $directory.FullName -Force){Assert-FPIAcl $entry.FullName;if($entry.Name -cnotin @('package','generation.json')){throw 'PreviewInstallationUnknownEntry'}}
        if(!(Test-Path -LiteralPath (Join-Path $directory.FullName 'generation.json') -PathType Leaf) -or !(Test-Path -LiteralPath (Join-Path $directory.FullName 'package') -PathType Container)){throw 'PreviewInstallationPartialOrUnknownGeneration'}
        $receipt=Read-FPIJson (Join-Path $directory.FullName 'generation.json') @('schema','installId','generation','operation','repairOf','archiveSha256','createdUnixMilliseconds','previousSha256','packageFiles','localPreview','signed','cleanMachineQualified','architectureQualified','productionReady','sha256')
        if($receipt.schema -cne 'platform-studio.foundation-preview-generation.v1' -or $receipt.installId -cne $marker.installId -or
            ($receipt.generation -isnot [long] -and $receipt.generation -isnot [int]) -or $receipt.generation -ne $number -or $receipt.previousSha256 -cne $previous -or
            $receipt.operation -cnotin @('Stage','Repair') -or $receipt.archiveSha256 -cnotmatch '\A[a-f0-9]{64}\z' -or
            ($receipt.createdUnixMilliseconds -isnot [long] -and $receipt.createdUnixMilliseconds -isnot [int]) -or $receipt.createdUnixMilliseconds -le 0 -or
            $receipt.packageFiles -isnot [Collections.IList] -or $receipt.packageFiles.Count -lt 1 -or $receipt.packageFiles.Count -gt 5000){throw 'PreviewInstallationMetadataIntegrityFailed'}
        foreach($flag in @('signed','cleanMachineQualified','architectureQualified','productionReady')){if($receipt[$flag] -isnot [bool] -or $receipt[$flag]){throw 'PreviewInstallationUnsupportedClaim'}}
        if($receipt.localPreview -isnot [bool] -or !$receipt.localPreview -or ($receipt.operation -ceq 'Stage' -and $null -ne $receipt.repairOf) -or
            ($receipt.operation -ceq 'Repair' -and (($receipt.repairOf -isnot [long] -and $receipt.repairOf -isnot [int]) -or $receipt.repairOf -lt 1 -or $receipt.repairOf -ge $number))){throw 'PreviewInstallationMetadataIntegrityFailed'}
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach($file in $receipt.packageFiles){
            if($file -isnot [Collections.IDictionary] -or $file.Count -ne 3 -or @($file.Keys|Where-Object{$_ -cnotin @('path','bytes','sha256')}).Count -or
                $file.path -isnot [string] -or $file.sha256 -isnot [string] -or $file.path -cnotmatch '\A[A-Za-z0-9_./+-]+\z' -or $file.path.Split('/') -contains '..' -or !$seen.Add($file.path) -or
                ($file.bytes -isnot [long] -and $file.bytes -isnot [int]) -or $file.bytes -lt 0 -or $file.bytes -gt 67108864 -or $file.sha256 -cnotmatch '\A[a-f0-9]{64}\z'){throw 'PreviewInstallationMetadataIntegrityFailed'}
            $total+=$file.bytes
            Assert-FPIArchiveRelative $file.path
        }
        if($total -gt 1073741824){throw 'PreviewInstallationStorageLimit'}
        $package=Join-Path $directory.FullName 'package';Assert-FPIAcl $package
        Assert-FPIPackageStructure $package $receipt.packageFiles
        foreach($file in @(Get-FoundationPackageFiles $package)){Assert-FPIAcl $file.FullName;if(!$seen.Contains($file.Path)){throw 'PreviewInstallationUnknownPackageEntry'}}
        $previous=$receipt.sha256;$records.Add($receipt)
    }
    [pscustomobject]@{marker=$marker;generations=$records.ToArray();generation=$number;stateSha256=$previous;logicalBytes=$total}
}
function Get-FPIVerification([string]$Root,$State,[long]$Generation) {
    if($Generation -lt 1 -or $Generation -gt $State.generation){throw 'PreviewInstallationGenerationNotFound'}
    $receipt=$State.generations[$Generation-1];$package=Join-Path $Root ('generations/'+$Generation.ToString('000000')+'/package')
    $actual=@{};foreach($file in @(Get-FoundationPackageFiles $package)){Assert-FPIAcl $file.FullName;$actual[$file.Path]=$file}
    $failures=[Collections.Generic.List[string]]::new()
    foreach($file in $receipt.packageFiles){if(!$actual.ContainsKey($file.path)){$failures.Add('MissingPackageFile');continue};if($actual[$file.path].Length -ne $file.bytes -or
        (Get-FileHash -LiteralPath $actual[$file.path].FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $file.sha256){$failures.Add('PackageFileHashMismatch')}}
    if(!$failures.Count){try{$null=Test-FoundationPackageTree $package}catch{$failures.Add('PackageEvidenceRejected')}}
    [pscustomobject]@{generation=$Generation;packageDirectory=$package;archiveSha256=$receipt.archiveSha256;verified=($failures.Count -eq 0);reasons=@($failures|Sort-Object -Unique);fileCount=$receipt.packageFiles.Count}
}
function Invoke-FoundationPreviewInstallation {
    [CmdletBinding()]
    param([Parameter(Mandatory)][ValidateSet('Stage','Verify','Repair','Readiness')][string]$Action,[Parameter(Mandatory)][string]$InstallRoot,
        [string]$ArchivePath='',[string]$ExpectedArchiveSha256='',[long]$ExpectedGeneration=-1,[string]$ExpectedStateSha256='',
        [long]$Generation=0,[switch]$LocalPreview,[string]$DotNetPath='')
    $root=Assert-FPILocalPath $InstallRoot;$mutating=$Action -in @('Stage','Repair');$archiveStream=$null;$guard=$null
    try{
        if($mutating -and !$LocalPreview){throw 'PreviewInstallationExplicitLocalPreviewRequired'}
        Assert-FPINewRootNamespace $root
        if($mutating){
            if($ExpectedArchiveSha256 -cnotmatch '\A[a-f0-9]{64}\z' -or $ExpectedGeneration -lt 0 -or
                ($ExpectedGeneration -eq 0 -and $ExpectedStateSha256) -or ($ExpectedGeneration -gt 0 -and $ExpectedStateSha256 -cnotmatch '\A[a-f0-9]{64}\z')){throw 'PreviewInstallationExpectedStateRequired'}
            $archive=Assert-FPILocalPath $ArchivePath
            # Retain the original read-only archive handle through validation and copying, denying replacement/writes.
            $archiveStream=[IO.FileStream]::new($archive,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            if($archiveStream.Length -gt 268435456 -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($archiveStream)).ToLowerInvariant() -cne $ExpectedArchiveSha256){throw 'PreviewInstallationArchivePinMismatch'}
            $pathArchive=[IO.Compression.ZipFile]::OpenRead($archive)
            try{Assert-FPIArchiveEntries @($pathArchive.Entries|Where-Object {!$_.FullName.EndsWith('/',[StringComparison]::Ordinal)})}finally{$pathArchive.Dispose()}
            $null=Test-FoundationArchive $archive
            if(!(Test-Path -LiteralPath $root)){
                if($Action -cne 'Stage' -or $ExpectedGeneration -ne 0){throw 'PreviewInstallationExistingRootRequired'}
                if(!(Test-Path -LiteralPath (Split-Path $root -Parent) -PathType Container)){throw 'PreviewInstallationParentMissing'}
                Assert-FPINewRootNamespace $root
                New-FPIPrivateRoot $root
                $created=[IO.FileStream]::new((Join-Path $root '.installation.guard'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None);$created.Dispose()
                $null=New-Item -ItemType Directory -Path (Join-Path $root 'generations')
                Write-FPIRecord (Join-Path $root '.installation-owner.json') ([ordered]@{schema='platform-studio.foundation-preview-installation.v1';scope='GenericSoftwareStagingOnly';installId=[Guid]::NewGuid().ToString('N');ownerSid=(Get-FPISid).Value;rootPath=$root;createdUnixMilliseconds=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds();localPreview=$true})
            }
        }
        if(!(Test-Path -LiteralPath $root -PathType Container)){throw 'PreviewInstallationExistingRootRequired'}
        $guard=Open-FPIGuard $root -Exclusive:$mutating;$state=Read-FPIState $root
        if(!$mutating){
            if($Generation -eq 0){$Generation=$state.generation}
            $verification=Get-FPIVerification $root $state $Generation
            if($Action -ceq 'Verify'){return [pscustomobject]@{schema='platform-studio.foundation-preview-verification.v1';scope='GenericSoftwareStagingOnly';installId=$state.marker.installId;generation=$state.generation;stateSha256=$state.stateSha256;verification=$verification;customerAuthorityGranted=$false;localPreview=$true;signed=$false;architectureQualified=$false;cleanMachineQualified=$false;productionReady=$false}}
            return Get-FPIReadiness $root $state $verification $DotNetPath
        }
        if($state.generation -ne $ExpectedGeneration -or $state.stateSha256 -cne $ExpectedStateSha256){throw 'PreviewInstallationGenerationChanged'}
        if($state.generation -ge 64){throw 'PreviewInstallationGenerationLimit'}
        $repairOf=$null
        if($Action -ceq 'Repair'){
            if($Generation -eq 0){$Generation=$state.generation}
            if($Generation -lt 1 -or $Generation -gt $state.generation){throw 'PreviewInstallationGenerationNotFound'}
            if($state.generations[$Generation-1].archiveSha256 -cne $ExpectedArchiveSha256){throw 'PreviewInstallationRepairArchiveMismatch'}
            $repairOf=$Generation
        }elseif($Generation -ne 0){throw 'PreviewInstallationInvalidGenerationSelector'}
        $zip=[IO.Compression.ZipFile]::OpenRead($archive)
        try{
            $entries=@($zip.Entries|Where-Object {!$_.FullName.EndsWith('/',[StringComparison]::Ordinal)}|Sort-Object FullName -CaseSensitive)
            $bytes=[long]0;foreach($entry in $entries){if($entry.FullName.Split('/') -contains '.' -or $entry.FullName.Contains('//')){throw 'PreviewInstallationArchivePathRejected'};$bytes+=$entry.Length}
            if($state.logicalBytes+$bytes -gt 1073741824){throw 'PreviewInstallationStorageLimit'}
            $number=$state.generation+1;$directory=Join-Path $root ('generations/'+$number.ToString('000000'))
            # Receipt is the final commit marker. Interrupted generations are retained and reject future operations.
            Assert-FPINewRootNamespace $root
            $null=New-Item -ItemType Directory -Path (Join-Path $directory 'package')
            $inventory=[Collections.Generic.List[object]]::new()
            foreach($entry in $entries){
                $read=Read-FoundationArchiveEntry $entry 67108864 -IncludeBytes
                $target=Join-Path (Join-Path $directory 'package') $entry.FullName
                $null=New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force;Assert-FoundationUnlinkedPath $target
                $file=[IO.FileStream]::new($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
                try{[byte[]]$copyBytes=$read.Content;$file.Write($copyBytes,0,$copyBytes.Length);$file.Flush($true)}finally{$file.Dispose()}
                Assert-FPIAcl $target;$inventory.Add([ordered]@{path=$entry.FullName;bytes=$read.Bytes;sha256=$read.Sha256})
            }
            $null=Test-FoundationPackageTree (Join-Path $directory 'package')
            $receipt=[ordered]@{schema='platform-studio.foundation-preview-generation.v1';installId=$state.marker.installId;generation=$number;
                operation=$Action;repairOf=$repairOf;archiveSha256=$ExpectedArchiveSha256;createdUnixMilliseconds=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds();previousSha256=$state.stateSha256;
                packageFiles=$inventory.ToArray();localPreview=$true;signed=$false;cleanMachineQualified=$false;architectureQualified=$false;productionReady=$false}
            Write-FPIRecord (Join-Path $directory 'generation.json') $receipt
            $state=Read-FPIState $root
            [pscustomobject]@{schema='platform-studio.foundation-preview-stage.v1';scope='GenericSoftwareStagingOnly';installId=$state.marker.installId;generation=$state.generation;stateSha256=$state.stateSha256;
                stagedDirectory=(Join-Path $directory 'package');archiveSha256=$ExpectedArchiveSha256;operation=$Action;repairOf=$repairOf;
                activeVersionChanged=$false;customerDataTouched=$false;customerAuthorityGranted=$false;localPreview=$true;signed=$false;architectureQualified=$false;cleanMachineQualified=$false;productionReady=$false}
        }finally{$zip.Dispose()}
    }finally{if($null -ne $guard){$guard.Dispose()};if($null -ne $archiveStream){$archiveStream.Dispose()}}
}
function Initialize-FPIProbe {
    if('FoundationPreviewRuntimeProbe' -as [type]){return}
    Add-Type -TypeDefinition @'
using System; using System.Diagnostics; using System.IO; using System.Text; using System.Threading; using System.Threading.Tasks;
public sealed record FoundationPreviewProbeResult(int ExitCode,string Output);
public static class FoundationPreviewRuntimeProbe {
 static async Task<string> Read(StreamReader reader,int maximum,CancellationToken token) {
  var text=new StringBuilder();var buffer=new char[2048];int count;
  while((count=await reader.ReadAsync(buffer.AsMemory(),token))>0){if(text.Length+count>maximum)throw new InvalidDataException("PreviewRuntimeProbeOutputLimit");text.Append(buffer,0,count);}return text.ToString();
 }
 public static FoundationPreviewProbeResult Run(string executable,string temporaryDirectory) {
  using var process=new Process();var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=temporaryDirectory};
  start.ArgumentList.Add("--list-runtimes");start.Environment.Clear();
  foreach(var name in new[]{"SystemRoot","windir"})if(Environment.GetEnvironmentVariable(name) is string value)start.Environment[name]=value;
  start.Environment["TEMP"]=temporaryDirectory;start.Environment["TMP"]=temporaryDirectory;
  start.Environment["DOTNET_ROOT"]=Path.GetDirectoryName(executable);start.Environment["PATH"]=Path.GetDirectoryName(executable)+";"+Path.Combine(Environment.GetEnvironmentVariable("SystemRoot")!,"System32");
  start.Environment["DOTNET_CLI_TELEMETRY_OPTOUT"]="1";start.Environment["DOTNET_SKIP_FIRST_TIME_EXPERIENCE"]="1";start.Environment["DOTNET_GENERATE_ASPNET_CERTIFICATE"]="false";start.Environment["DOTNET_ADD_GLOBAL_TOOLS_TO_PATH"]="false";
  process.StartInfo=start;using var deadline=new CancellationTokenSource(TimeSpan.FromSeconds(5));
  try {if(!process.Start())throw new IOException("PreviewRuntimeProbeStartFailed");var stdout=Read(process.StandardOutput,65536,deadline.Token);var stderr=Read(process.StandardError,16384,deadline.Token);
   Task.WhenAll(stdout,stderr,process.WaitForExitAsync(deadline.Token)).GetAwaiter().GetResult();if(stderr.Result.Length!=0)throw new IOException("PreviewRuntimeProbeStderrRejected");return new(process.ExitCode,stdout.Result);
  } finally {try{if(process.Id>0&&!process.HasExited){process.Kill(false);if(!process.WaitForExit(5000))throw new IOException("PreviewRuntimeProbeDidNotStop");}}catch(InvalidOperationException){} }
 }
}
'@
}
function Get-FPIReadiness([string]$Root,$State,$Verification,[string]$DotNetPath) {
    $executable=Assert-FPILocalPath $DotNetPath
    if([IO.Path]::GetFileName($executable) -ine 'dotnet.exe' -or !(Test-Path -LiteralPath $executable -PathType Leaf) -or
        $executable.StartsWith($Root+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'PreviewReadinessExplicitDotnetExecutableRequired'}
    # Bind the probe identity to the same non-replaceable executable bytes through process completion.
    $executableStream=[IO.FileStream]::new($executable,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        $executableSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($executableStream)).ToLowerInvariant()
        Initialize-FPIProbe;$probe=[FoundationPreviewRuntimeProbe]::Run($executable,$Root)
    }finally{$executableStream.Dispose()}
    if($probe.ExitCode -ne 0){throw 'PreviewRuntimeProbeFailed'}
    $installed=@{}
    foreach($line in $probe.Output.Split("`n",[StringSplitOptions]::RemoveEmptyEntries)){
        if($line.Trim() -cnotmatch '\A([A-Za-z0-9.]+) ([0-9]+\.[0-9]+\.[0-9]+) \[[^\r\n]+\]\z'){throw 'PreviewRuntimeProbeInvalidOutput'}
        $name=$Matches[1];if(!$installed.ContainsKey($name)){$installed[$name]=@()};$installed[$name]+=$Matches[2]
    }
    $requirements=[Collections.Generic.List[object]]::new();$descriptorValid=$true
    $configurationFiles=@();if(!$Verification.verified){$descriptorValid=$false}else{$configurationFiles=@(Get-ChildItem -LiteralPath (Join-Path $Verification.packageDirectory 'runtime') -Filter '*.runtimeconfig.json' -File)}
    foreach($file in $configurationFiles){
        if($file.Length -gt 65536){$descriptorValid=$false;continue}
        $config=Get-Content -LiteralPath $file.FullName -Raw|ConvertFrom-Json -AsHashtable -Depth 16;$options=$config.runtimeOptions;$requested=@()
        # This is a deliberately narrow prerequisite check, not the .NET host's resolver.
        # Reject policies we do not implement rather than accepting a later patch incorrectly.
        if($options -isnot [Collections.IDictionary] -or @($options.Keys|Where-Object {$_ -cnotin @('tfm','frameworks','framework','configProperties','rollForward','applyPatches')}).Count -or
            ($options.Contains('rollForward') -and $options.rollForward -cne 'LatestPatch') -or
            ($options.Contains('applyPatches') -and ($options.applyPatches -isnot [bool] -or !$options.applyPatches)) -or
            ($options.Contains('frameworks') -and ($options.frameworks -isnot [Collections.IList] -or $options.frameworks.Count -gt 8)) -or
            ($options.Contains('framework') -and $options.Contains('frameworks'))){$descriptorValid=$false;continue}
        if($options.Contains('frameworks')){$requested+=$options.frameworks};if($options.Contains('framework')){$requested+=$options.framework}
        if(!$requested.Count -or $options.Contains('includedFrameworks')){$descriptorValid=$false;continue}
        foreach($framework in $requested){
            $wanted=$null;if($framework -isnot [Collections.IDictionary] -or $framework.Count -ne 2 -or @($framework.Keys|Where-Object {$_ -cnotin @('name','version')}).Count -or
                $framework.name -cnotin @('Microsoft.NETCore.App','Microsoft.AspNetCore.App') -or $framework.version -cnotmatch '\A(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\.(?:0|[1-9][0-9]*)\z' -or
                ![Version]::TryParse($framework.version,[ref]$wanted)){$descriptorValid=$false;continue}
            $matching=@();if($installed.ContainsKey($framework.name)){$matching=@($installed[$framework.name]|Where-Object{$version=[Version]::Parse($_);$version.Major -eq $wanted.Major -and $version.Minor -eq $wanted.Minor -and $version.Build -ge $wanted.Build})}
            $requirements.Add([ordered]@{name=$framework.name;minimumVersion=$framework.version;matchingInstalledVersions=$matching;available=($matching.Count -gt 0)})
        }
    }
    $ready=$Verification.verified -and $descriptorValid -and $requirements.Count -gt 0 -and @($requirements|Where-Object {!$_.available}).Count -eq 0
    [pscustomobject]@{schema='platform-studio.foundation-preview-readiness.v1';scope='GenericSoftwareStagingOnly';installId=$State.marker.installId;generation=$Verification.generation;stateSha256=$State.stateSha256;
        packageBytesVerified=$Verification.verified;runtimePrerequisitesReady=$ready;frameworkDescriptorsValid=$descriptorValid;frameworks=$requirements.ToArray();
        runtimeProbeExecutableSha256=$executableSha256;
        customerConfigurationEvaluated=$false;activationEvaluated=$false;identityEvaluated=$false;providerAccessEvaluated=$false;sourceBackedFeaturesEvaluated=$false;
        customerAuthorityGranted=$false;localPreview=$true;signed=$false;architectureQualified=$false;cleanMachineQualified=$false;legalClearance=$false;productionReady=$false}
}

if($MyInvocation.InvocationName -ne '.'){
    $publisher=Join-Path $PSScriptRoot 'Publish-Foundation.ps1';$tokens=$null;$errors=$null
    $ast=[Management.Automation.Language.Parser]::ParseFile($publisher,[ref]$tokens,[ref]$errors);if($errors){throw($errors|Out-String)}
    foreach($function in $ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)){. ([scriptblock]::Create($function.Extent.Text))}
    Invoke-FoundationPreviewInstallation -Action $Action -InstallRoot $InstallRoot -ArchivePath $ArchivePath -ExpectedArchiveSha256 $ExpectedArchiveSha256 -ExpectedGeneration $ExpectedGeneration -ExpectedStateSha256 $ExpectedStateSha256 -Generation $Generation -LocalPreview:$LocalPreview -DotNetPath $DotNetPath
}
