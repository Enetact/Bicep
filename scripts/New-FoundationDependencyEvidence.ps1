#requires -Version 7.4
[CmdletBinding()]
param(
    [string]$RuntimeDirectory = '', [string]$NuGetPackagesDirectory = '', [string]$OutputDirectory = '',
    [string]$RepositoryRoot = '', [string]$CreatedUtc = '', [string]$NuGetSdkDirectory = ''
)
$ErrorActionPreference = 'Stop'

function Assert-FDPlainPath([string]$Path) {
    $full = [IO.Path]::GetFullPath($Path)
    for ($part = $full; $part; $part = [IO.Path]::GetDirectoryName($part)) {
        if (([IO.File]::Exists($part) -or [IO.Directory]::Exists($part)) -and
            ((Get-Item -LiteralPath $part -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'DependencyLinkedPath' }
    }
    $full
}
function Assert-FDRelative([string]$Value) {
    if (!$Value -or $Value.Length -gt 1024 -or $Value.Contains('\') -or $Value.Contains(':') -or $Value.Contains('%') -or
        $Value.StartsWith('/') -or $Value -match '[\x00-\x1f\x7f]' -or $Value.Split('/') -contains '..' -or
        $Value.Split('/') -contains '.' -or $Value.Contains('//')) { throw 'DependencyUnsafeRelativePath' }
}
function Get-FDHash([string]$Path, [string]$Algorithm = 'SHA256') {
    $null = Assert-FDPlainPath $Path
    (Get-FileHash -LiteralPath $Path -Algorithm $Algorithm).Hash.ToLowerInvariant()
}
function Get-FDTextHash([string]$Text) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant() }
function Read-FDText([string]$Path, [long]$Maximum = 2097152) {
    $null = Assert-FDPlainPath $Path
    if (!(Test-Path -LiteralPath $Path -PathType Leaf) -or (Get-Item -LiteralPath $Path).Length -gt $Maximum) { throw 'DependencyInputMissingOrOversized' }
    [Text.UTF8Encoding]::new($false, $true).GetString([IO.File]::ReadAllBytes($Path)).TrimStart([char]0xfeff)
}
function Assert-FDJsonUnique([System.Text.Json.JsonElement]$Element) {
    if ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Object) {
        $names = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($p in $Element.EnumerateObject()) { if (!$names.Add($p.Name)) { throw 'DependencyAmbiguousJson' }; Assert-FDJsonUnique $p.Value }
    } elseif ($Element.ValueKind -eq [System.Text.Json.JsonValueKind]::Array) { foreach ($p in $Element.EnumerateArray()) { Assert-FDJsonUnique $p } }
}
function Read-FDJson([string]$Path,[long]$Maximum=2097152,[switch]$IncludeReadHash) {
    $readHash=$null
    if($IncludeReadHash){
        $null=Assert-FDPlainPath $Path;$stream=[IO.File]::OpenRead($Path);$memory=[IO.MemoryStream]::new()
        try{$buffer=[byte[]]::new(8192);while(($n=$stream.Read($buffer,0,$buffer.Length)) -gt 0){if($memory.Length+$n -gt $Maximum){throw 'DependencyInputMissingOrOversized'};$memory.Write($buffer,0,$n)};$bytes=$memory.ToArray()}
        finally{$memory.Dispose();$stream.Dispose()}
        $readHash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
        $text=[Text.UTF8Encoding]::new($false,$true).GetString($bytes).TrimStart([char]0xfeff)
    }else{$text = Read-FDText $Path $Maximum}
    $options = [System.Text.Json.JsonDocumentOptions]::new(); $options.MaxDepth = 32
    $doc = [System.Text.Json.JsonDocument]::Parse($text, $options)
    try { Assert-FDJsonUnique $doc.RootElement } finally { $doc.Dispose() }
    $value=ConvertFrom-Json -InputObject $text -AsHashtable -Depth 32
    if($IncludeReadHash){return [ordered]@{value=$value;sha256=$readHash}}
    $value
}
function Get-FDFiles([string]$Directory) {
    $directoryFull = Assert-FDPlainPath $Directory
    if (!(Test-Path -LiteralPath $directoryFull -PathType Container)) { throw 'DependencyRuntimeMissing' }
    $stack = [Collections.Generic.Stack[string]]::new(); $stack.Push($directoryFull)
    $rows = [Collections.Generic.List[object]]::new(); $bytes = [long]0
    while ($stack.Count) {
        foreach ($item in Get-ChildItem -LiteralPath $stack.Pop() -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'DependencyLinkedPath' }
            if ($item.PSIsContainer) { $stack.Push($item.FullName); continue }
            $relative = [IO.Path]::GetRelativePath($directoryFull, $item.FullName).Replace('\','/'); Assert-FDRelative $relative
            $bytes += $item.Length
            if ($rows.Count -ge 5000 -or $item.Length -gt 67108864 -or $bytes -gt 268435456) { throw 'DependencyRuntimeLimit' }
            $rows.Add([ordered]@{ path = $relative; bytes = $item.Length; sha256 = Get-FDHash $item.FullName })
        }
    }
    @($rows | Sort-Object { $_.path } -CaseSensitive)
}
function Read-FDZipText([IO.Compression.ZipArchiveEntry]$Entry) {
    if ($Entry.Length -gt 262144) { throw 'DependencyNoticeLimit' }
    $stream = $Entry.Open(); $output = [IO.MemoryStream]::new()
    try {
        $buffer = [byte[]]::new(8192)
        while (($n = $stream.Read($buffer,0,$buffer.Length)) -gt 0) {
            if ($output.Length + $n -gt 262144) { throw 'DependencyNoticeLimit' }; $output.Write($buffer,0,$n)
        }
        if ($output.Length -ne $Entry.Length) { throw 'DependencyArchiveLengthMismatch' }
        [Text.UTF8Encoding]::new($false,$true).GetString($output.ToArray()).TrimStart([char]0xfeff)
    } finally { $output.Dispose(); $stream.Dispose() }
}
function Get-FDZipHash([IO.Compression.ZipArchiveEntry]$Entry) {
    if($Entry.Length -gt 67108864){throw 'DependencyArchiveLimit'}
    $stream=$Entry.Open();$hash=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    try{
        $buffer=[byte[]]::new(8192);$total=[long]0
        while(($n=$stream.Read($buffer,0,$buffer.Length)) -gt 0){$total+=$n;if($total -gt 67108864){throw 'DependencyArchiveLimit'};$hash.AppendData($buffer,0,$n)}
        if($total -ne $Entry.Length){throw 'DependencyArchiveLengthMismatch'}
        [Convert]::ToHexString($hash.GetHashAndReset()).ToLowerInvariant()
    }finally{$hash.Dispose();$stream.Dispose()}
}
function Initialize-FDNuGetReader([string]$SdkDirectory) {
    $sdk = Assert-FDPlainPath $SdkDirectory
    foreach ($name in @('NuGet.Versioning','NuGet.Frameworks','NuGet.Common','NuGet.Configuration','NuGet.Packaging')) {
        $path = Join-Path $sdk ($name + '.dll'); $null = Assert-FDPlainPath $path
        if (!(Test-Path -LiteralPath $path -PathType Leaf)) { throw 'DependencyNuGetReaderMissing' }
        [void][Reflection.Assembly]::LoadFrom($path)
    }
}
function Get-FDPackage([string]$Name, [string]$Version, [string]$ExpectedContentHash,
    [string]$CacheDirectory, [string]$SdkDirectory, [object[]]$Assets) {
    if ($Name -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z' -or $Version -cnotmatch '\A[0-9][A-Za-z0-9.+-]{0,79}\z') { throw 'DependencyInvalidPackageIdentity' }
    $id = $Name.ToLowerInvariant(); $versionLower = $Version.ToLowerInvariant()
    $directory = Join-Path $CacheDirectory "$id/$versionLower"; $null = Assert-FDPlainPath $directory
    $archivePath = Join-Path $directory "$id.$versionLower.nupkg"; $null = Assert-FDPlainPath $archivePath
    if (!(Test-Path -LiteralPath $archivePath -PathType Leaf) -or (Get-Item -LiteralPath $archivePath).Length -gt 134217728) { throw 'DependencyPackageMissingOrOversized' }
    $cacheHash = (Read-FDText ($archivePath + '.sha512') 256).Trim()
    if ($cacheHash -cnotmatch '\A[A-Za-z0-9+/]{86}==\z' -or $ExpectedContentHash -cnotmatch '\A[A-Za-z0-9+/]{86}==\z') { throw 'DependencyInvalidPackageHash' }
    $archiveHash = Get-FDHash $archivePath 'SHA512'
    if ([Convert]::ToBase64String([Convert]::FromHexString($archiveHash)) -cne $cacheHash) { throw 'DependencyArchiveHashMismatch' }
    $zip = [IO.Compression.ZipFile]::OpenRead($archivePath)
    try {
        $entries = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
        $total = [long]0
        foreach ($entry in $zip.Entries) {
            $relative = $entry.FullName.TrimEnd('/'); Assert-FDRelative $relative
            if ((($entry.ExternalAttributes -shr 16) -band 0xf000) -eq 0xa000 -or !$entries.TryAdd($relative,$entry)) { throw 'DependencyUnsafeArchive' }
            $total += $entry.Length
            if ($entries.Count -gt 20000 -or $entry.Length -gt 67108864 -or $total -gt 536870912) { throw 'DependencyArchiveLimit' }
        }
        $signed = $entries.ContainsKey('.signature.p7s'); $contentHash = $cacheHash
        if ($signed) {
            Initialize-FDNuGetReader $SdkDirectory
            $reader = [NuGet.Packaging.PackageArchiveReader]::new($archivePath)
            try {
                $signature = $reader.GetPrimarySignatureAsync([Threading.CancellationToken]::None).GetAwaiter().GetResult()
                $null = $reader.ValidateIntegrityAsync($signature.SignatureContent,[Threading.CancellationToken]::None).GetAwaiter().GetResult()
                $contentHash = $reader.GetContentHash([Threading.CancellationToken]::None,$null)
            } finally { $reader.Dispose() }
        }
        if ($contentHash -cne $ExpectedContentHash) { throw 'DependencyContentHashMismatch' }
        $metadataPath = Join-Path $directory '.nupkg.metadata'
        if (Test-Path -LiteralPath $metadataPath) {
            $cacheMetadata = Read-FDJson $metadataPath
            if (!$cacheMetadata.Contains('contentHash') -or $cacheMetadata.contentHash -cne $contentHash) { throw 'DependencyCacheMetadataHashMismatch' }
        }
        $nuspecs = @($entries.Keys | Where-Object { $_ -notmatch '/' -and $_.EndsWith('.nuspec',[StringComparison]::OrdinalIgnoreCase) })
        if ($nuspecs.Count -ne 1) { throw 'DependencyPackageMetadataMissing' }
        $nuspec = Read-FDZipText $entries[$nuspecs[0]]
        $xmlOptions = [Xml.XmlReaderSettings]::new(); $xmlOptions.DtdProcessing = [Xml.DtdProcessing]::Prohibit
        $xmlOptions.XmlResolver = $null; $xmlOptions.MaxCharactersInDocument = 262144
        $textReader = [IO.StringReader]::new($nuspec); $xmlReader = [Xml.XmlReader]::Create($textReader,$xmlOptions)
        $document = [Xml.XmlDocument]::new(); $document.XmlResolver = $null
        try { $document.Load($xmlReader) } finally { $xmlReader.Dispose(); $textReader.Dispose() }
        $metadata = $document.SelectSingleNode('/*[local-name()="package"]/*[local-name()="metadata"]')
        if ($null -eq $metadata -or $metadata.SelectSingleNode('*[local-name()="id"]').InnerText -cne $Name -or
            $metadata.SelectSingleNode('*[local-name()="version"]').InnerText -cne $Version) { throw 'DependencyPackageIdentityMismatch' }
        $repositoryNodes=@($metadata.SelectNodes('*[local-name()="repository"]'));$copyrightNodes=@($metadata.SelectNodes('*[local-name()="copyright"]'))
        if($repositoryNodes.Count -gt 1 -or $copyrightNodes.Count -gt 1){throw 'DependencyAmbiguousRepositoryMetadata'}
        $repositoryUrl=if($repositoryNodes.Count){$repositoryNodes[0].GetAttribute('url')}else{''}
        $repositoryCommit=if($repositoryNodes.Count){$repositoryNodes[0].GetAttribute('commit')}else{''}
        $declaredCopyright=if($copyrightNodes.Count){$copyrightNodes[0].InnerText}else{''}
        $licenses = @($metadata.SelectNodes('*[local-name()="license"]'))
        if ($licenses.Count -ne 1) { throw 'DependencyDeclaredLicenseMissing' }
        $license = $licenses[0]; $licenseValue = $license.InnerText.Trim(); $licenseKind = $license.GetAttribute('type')
        $noticeRows = [Collections.Generic.List[object]]::new(); $declared = 'NOASSERTION'; $extracted = $null
        if ($licenseKind -ceq 'expression') {
            # Deliberately bounded reviewed subset. Unsupported/compound expressions are blocked rather than guessed.
            if ($licenseValue -cnotin @('MIT','Apache-2.0','BSD-2-Clause','BSD-3-Clause','ISC','MPL-2.0','CC0-1.0','CC-BY-4.0','Unlicense','Zlib','BSL-1.0')) { throw 'DependencyUnsupportedLicenseExpression' }
            $declared = $licenseValue
        } elseif ($licenseKind -ceq 'file') {
            Assert-FDRelative $licenseValue
            if (!$entries.ContainsKey($licenseValue)) { throw 'DependencyLicenseFileMissing' }
            $text = Read-FDZipText $entries[$licenseValue]
            if ([string]::IsNullOrWhiteSpace($text)) { throw 'DependencyLicenseFileMissing' }
            $declared = 'LicenseRef-' + (Get-FDTextHash ($Name + '/' + $Version)).Substring(0,24)
            $extracted = [ordered]@{ licenseId = $declared; name = "$Name declared license file"; extractedText = $text }
        } else { throw 'DependencyUnsupportedLicenseMetadata' }
        foreach ($key in @($entries.Keys | Sort-Object -CaseSensitive)) {
            if ($key -ceq $licenseValue -or [IO.Path]::GetFileName($key) -match '(?i)\A(?:license(?:[.-].*)?|copying(?:[.-].*)?|notice(?:[.-].*)?|third[-_]?party[-_]?notices?(?:[.-].*)?)\z') {
                $text = Read-FDZipText $entries[$key]
                $noticeRows.Add([ordered]@{ path = $key; sha256 = Get-FDZipHash $entries[$key]; text = $text })
                if ($noticeRows.Count -gt 64) { throw 'DependencyNoticeLimit' }
            }
        }
        $assetRows = [Collections.Generic.List[object]]::new()
        foreach ($asset in $Assets) {
            Assert-FDRelative $asset.packagePath
            if (!$entries.ContainsKey($asset.packagePath)) { throw 'DependencyPackageAssetMissing' }
            $digest = Get-FDZipHash $entries[$asset.packagePath]
            if ($digest -cne $asset.sha256) { throw 'DependencyPublishedAssetMismatch' }
            $assetRows.Add([ordered]@{ runtimePath = $asset.runtimePath; packagePath = $asset.packagePath; sha256 = $digest })
        }
        if ((Get-FDHash $archivePath 'SHA512') -cne $archiveHash) { throw 'DependencyPackageChangedDuringRead' }
        [ordered]@{ id=$Name; version=$Version; archiveSha512=$archiveHash; contentSha512=$contentHash;
            cacheArchiveHashVerified=$true; depsContentHashVerified=$true; packageSignaturePresent=$signed; signatureTrustVerified=$false;
            nuspecSha256=Get-FDZipHash $entries[$nuspecs[0]]; licenseDeclared=$declared; licenseKind=$licenseKind;
            licenseMetadata=$licenseValue; repositoryUrl=$repositoryUrl;repositoryCommit=$repositoryCommit;declaredCopyright=$declaredCopyright;
            archiveMissingLicenseText=($noticeRows.Count -eq 0);
            archiveDeclaredLicenseFileMissing=($licenseKind -cne 'file' -and @($noticeRows|Where-Object{[IO.Path]::GetFileName($_.path) -match '(?i)\A(?:license(?:[.-].*)?|copying(?:[.-].*)?)\z'}).Count -eq 0);
            notices=$noticeRows.ToArray(); extractedLicense=$extracted; publishedAssets=$assetRows.ToArray() }
    } finally { $zip.Dispose() }
}

function Get-FDGitBlobHash([byte[]]$Bytes) {
    $header=[Text.Encoding]::UTF8.GetBytes('blob '+$Bytes.Length+[char]0);$all=[byte[]]::new($header.Length+$Bytes.Length)
    [Array]::Copy($header,$all,$header.Length);[Array]::Copy($Bytes,0,$all,$header.Length,$Bytes.Length)
    [Convert]::ToHexString([Security.Cryptography.SHA1]::HashData($all)).ToLowerInvariant()
}
function Read-FDNoticeSupplements([string]$Root) {
    $directory=Join-Path $Root 'vendor/foundation-package-notices';$path=Join-Path $directory 'bundle.json'
    if(!(Test-Path -LiteralPath $path -PathType Leaf)){return [ordered]@{manifestSha256=$null;packages=@{};files=@()}}
    $manifestRead=Read-FDJson $path -IncludeReadHash;$manifest=$manifestRead.value
    if($manifest.schema -cne 'platform-studio.foundation-package-notices.v1' -or $manifest.packages.Count -gt 256){throw 'DependencySupplementManifestRejected'}
    foreach($flag in @('legalClearance','distributionRightsGranted','productionReady')){if(!$manifest.Contains($flag) -or $manifest[$flag] -isnot [bool] -or $manifest[$flag]){throw 'DependencySupplementClearanceRejected'}}
    $packages=@{};$files=@{};$total=[long]0;$retained=[long]0
    foreach($record in $manifest.packages){
        $key=$record.id+'/'+$record.version
        if($record.id -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9._-]{0,127}\z' -or $record.version -cnotmatch '\A[0-9][A-Za-z0-9.+-]{0,79}\z' -or $packages.ContainsKey($key) -or
            $record.nuspecSha256 -cnotmatch '\A[a-f0-9]{64}\z' -or $record.archiveSha512 -cnotmatch '\A[a-f0-9]{128}\z' -or
            $record.repository -cnotmatch '\Ahttps://github\.com/[A-Za-z0-9_.-]+/[A-Za-z0-9_.-]+\z' -or $record.files.Count -gt 8 -or
            $record.licensingReviewRequired -isnot [bool] -or $record.status -cnotin @('Pinned','MissingRepositoryCommit')){throw 'DependencySupplementManifestRejected'}
        if($record.status -ceq 'Pinned'){
            if($record.commit -cnotmatch '\A[a-f0-9]{40}\z' -or !$record.files.Count){throw 'DependencySupplementCommitRejected'}
        }elseif($record.commit -or $record.files.Count){throw 'DependencySupplementCommitRejected'}
        $noticeRows=[Collections.Generic.List[object]]::new()
        foreach($file in $record.files){
            Assert-FDRelative $file.path;Assert-FDRelative $file.sourcePath
            if(!$file.path.StartsWith('licenses/',[StringComparison]::Ordinal) -or $file.sha256 -cnotmatch '\A[a-f0-9]{64}\z' -or $file.gitBlobSha -cnotmatch '\A[a-f0-9]{40}\z' -or
                $file.sourceUrl -cne ($record.repository.Replace('https://github.com/','https://raw.githubusercontent.com/')+'/'+$record.commit+'/'+$file.sourcePath)){throw 'DependencySupplementSourceRejected'}
            $full=Join-Path $directory $file.path;$null=Assert-FDPlainPath $full
            if(!(Test-Path -LiteralPath $full -PathType Leaf) -or (Get-Item -LiteralPath $full).Length -gt 524288){throw 'DependencySupplementTextMissingOrOversized'}
            $bytes=[IO.File]::ReadAllBytes($full)
            $retained+=$bytes.Length;if($retained -gt 16777216){throw 'DependencySupplementLimit'}
            if($bytes.Length -ne $file.bytes -or (Get-FDHash $full) -cne $file.sha256 -or (Get-FDGitBlobHash $bytes) -cne $file.gitBlobSha){throw 'DependencySupplementTextHashMismatch'}
            $text=[Text.UTF8Encoding]::new($false,$true).GetString($bytes)
            if([string]::IsNullOrWhiteSpace($text)){throw 'DependencySupplementTextMissingOrOversized'}
            $row=[ordered]@{path=$file.path;sourcePath=$file.sourcePath;sourceUrl=$file.sourceUrl;gitBlobSha=$file.gitBlobSha;sha256=$file.sha256;bytes=$file.bytes;text=$text}
            $noticeRows.Add($row)
            if($files.ContainsKey($file.path)){if($files[$file.path].sha256 -cne $file.sha256){throw 'DependencySupplementSourceRejected'}}
            else{$files[$file.path]=[ordered]@{path=$file.path;sha256=$file.sha256;gitBlobSha=$file.gitBlobSha;bytes=$file.bytes};$total+=$bytes.Length;if($total -gt 4194304){throw 'DependencySupplementLimit'}}
        }
        $packages[$key]=[ordered]@{binding=$record;notices=$noticeRows.ToArray()}
    }
    if((Get-FDHash $path) -cne $manifestRead.sha256){throw 'DependencySupplementManifestChangedDuringRead'}
    [ordered]@{manifestSha256=$manifestRead.sha256;packages=$packages;files=@($files.Values|Sort-Object path -CaseSensitive)}
}
function Set-FDPackageSupplement($Package,$Supplements) {
    $Package['supplementaryNotices']=@();$Package['supplementStatus']='NotRequiredArchiveTextPresent';$Package['licensingReviewRequired']=$false
    if(!$Package.archiveDeclaredLicenseFileMissing){return}
    if($Package.repositoryCommit -cnotmatch '\A[a-f0-9]{40}\z'){$Package.supplementStatus='MissingRepositoryCommit';return}
    $key=$Package.id+'/'+$Package.version
    if(!$Supplements.packages.ContainsKey($key)){$Package.supplementStatus='NoPinnedSupplement';return}
    $item=$Supplements.packages[$key];$binding=$item.binding
    if($binding.status -cne 'Pinned' -or $binding.repository -cne $Package.repositoryUrl -or $binding.commit -cne $Package.repositoryCommit -or
        $binding.licenseExpression -cne $Package.licenseDeclared -or $binding.archiveSha512 -cne $Package.archiveSha512 -or
        $binding.nuspecSha256 -cne $Package.nuspecSha256 -or $binding.declaredCopyright -cne $Package.declaredCopyright){throw 'DependencySupplementPackageBindingMismatch'}
    $Package.supplementStatus='PinnedTextVerified';$Package.supplementaryNotices=$item.notices
    $Package.licensingReviewRequired=$binding.licensingReviewRequired;$Package['supplementReviewReason']=$binding.reviewReason
}

function Get-FDSourceDistribution([string]$Root) {
    $result = [Collections.Generic.List[object]]::new()
    $sourceBytes=[long]0;$noticeBytes=[long]0
    foreach ($name in @('azure-skills','azure-devops-skills','microsoft-learn-skills','azure-verified-modules')) {
        $directory = Join-Path $Root ('vendor/' + $name); $manifestPath = Join-Path $directory 'bundle.json'
        $bundle = Read-FDJson $manifestPath; $expected = @{}
        if ($bundle.files -is [Collections.IDictionary]) {
            foreach ($path in $bundle.files.Keys) { $expected[$path] = $bundle.files[$path] }
        } else { foreach ($file in $bundle.files) { if ($expected.ContainsKey($file.path)) { throw 'DependencyDuplicateVendorFile' }; $expected[$file.path] = $file.sha256 } }
        if ($expected.Count -lt 1 -or $expected.Count -gt 5000) { throw 'DependencyVendorLimit' }
        $files = [Collections.Generic.List[object]]::new(); $licenses = [Collections.Generic.List[object]]::new()
        foreach ($path in @($expected.Keys | Sort-Object -CaseSensitive)) {
            Assert-FDRelative $path; $filePath = Join-Path $directory $path
            $null=Assert-FDPlainPath $filePath;$length=(Get-Item -LiteralPath $filePath).Length;$sourceBytes+=$length
            if($length -gt 67108864 -or $sourceBytes -gt 134217728){throw 'DependencyVendorLimit'}
            $digest = Get-FDHash $filePath
            if ($expected[$path] -cnotmatch '\A[a-f0-9]{64}\z' -or $digest -cne $expected[$path]) { throw 'DependencyVendorHashMismatch' }
            $files.Add([ordered]@{path=$path;sha256=$digest})
            if ([IO.Path]::GetFileName($path) -match '(?i)\A(?:license(?:[.-].*)?|third[-_]?party[-_]?notices?(?:[.-].*)?)\z') {
                $noticeBytes+=$length;if($noticeBytes -gt 4194304 -or $licenses.Count -ge 64){throw 'DependencyNoticeLimit'}
                $licenses.Add([ordered]@{path=$path;sha256=$digest;text=Read-FDText $filePath 262144})
            }
        }
        if (!$licenses.Count) { throw 'DependencyVendorLicenseMissing' }
        if ($bundle.Contains('commit')) {
            if ($bundle.commit -cnotmatch '\A[a-f0-9]{40}\z' -or $bundle.repository -cnotmatch '\Ahttps://github\.com/[^\s?#]+\z') { throw 'DependencyVendorPinMissing' }
            $pins = @([ordered]@{repository=$bundle.repository;commit=$bundle.commit})
        } else {
            $pins = @($bundle.modules | ForEach-Object {
                if ($_.sourceCommit -cnotmatch '\A[a-f0-9]{40}\z' -or $_.sourceRepository -cnotmatch '\Ahttps://github\.com/[^\s?#]+\z') { throw 'DependencyVendorPinMissing' }
                [ordered]@{repository=$_.sourceRepository;commit=$_.sourceCommit;module=$_.id;version=$_.version}
            })
            if (!$pins.Count) { throw 'DependencyVendorPinMissing' }
        }
        $declared = if ($bundle.Contains('license')) { [string]$bundle.license } else { 'NOASSERTION; retained upstream license files require review' }
        $result.Add([ordered]@{id=$name;distribution='SourceGuidanceAndTemplatesOnly';includedInRuntimeBinaryClosure=$false;
            manifestSha256=Get-FDHash $manifestPath;pins=$pins;licenseMetadata=$declared;licenseConcluded='NOASSERTION';files=$files.ToArray();notices=$licenses.ToArray()})
    }
    $result.ToArray()
}

function New-FoundationDependencyEvidence {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$RuntimeDirectory,[Parameter(Mandatory)][string]$NuGetPackagesDirectory,
        [Parameter(Mandatory)][string]$OutputDirectory,[Parameter(Mandatory)][string]$RepositoryRoot,
        [Parameter(Mandatory)][string]$CreatedUtc,[string]$NuGetSdkDirectory='')
    $root = Assert-FDPlainPath $RepositoryRoot; $runtime = Assert-FDPlainPath $RuntimeDirectory
    $cache = Assert-FDPlainPath $NuGetPackagesDirectory; $destination = Assert-FDPlainPath $OutputDirectory
    $outputRoot = [IO.Path]::GetFullPath((Join-Path $root 'artifacts/foundation-dependency-evidence'))
    if (![string]::Equals([IO.Path]::GetDirectoryName($destination),$outputRoot,[StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($destination) -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9._-]{0,100}\z' -or (Test-Path -LiteralPath $destination)) { throw 'DependencyFreshOwnedOutputRequired' }
    if ($CreatedUtc -cnotmatch '\A[0-9]{4}-[0-9]{2}-[0-9]{2}T[0-9]{2}:[0-9]{2}:[0-9]{2}Z\z' -or
        ![DateTimeOffset]::TryParse($CreatedUtc,[ref]([DateTimeOffset]::MinValue))) { throw 'DependencyExplicitUtcTimestampRequired' }
    if (!$NuGetSdkDirectory -and (Test-Path -LiteralPath (Join-Path $root 'global.json'))) {
        $dotnet = Get-Command dotnet -CommandType Application -ErrorAction SilentlyContinue
        if($dotnet){$global = Read-FDJson (Join-Path $root 'global.json');$NuGetSdkDirectory = Join-Path (Split-Path $dotnet.Source -Parent) ('sdk/' + $global.sdk.version)}
    }
    $runtimeFiles = @(Get-FDFiles $runtime)
    if (!$runtimeFiles.Count) { throw 'DependencyRuntimeMissing' }
    $fileIndex = @{}; foreach ($file in $runtimeFiles) { if ($fileIndex.ContainsKey($file.path)) { throw 'DependencyDuplicateRuntimeFile' }; $fileIndex[$file.path]=$file }
    $issues = [Collections.Generic.List[object]]::new(); $libraries = @{}; $assetIndex = @{}; $frameworks = @{}; $inputRows = [Collections.Generic.List[object]]::new(); $locks=[Collections.Generic.List[object]]::new()
    foreach ($entry in @('SelfService.Foundation.Host','SelfService.Foundation.Worker','SelfService.Foundation.Owner')) {
        foreach ($extension in @('.dll','.deps.json','.runtimeconfig.json')) { if (!$fileIndex.ContainsKey($entry+$extension)) { throw 'DependencyEntryClosureMissing' } }
        $depsPath = Join-Path $runtime ($entry+'.deps.json'); $deps = Read-FDJson $depsPath
        if (!$deps.Contains('libraries') -or !$deps.Contains('targets') -or !$deps.Contains('runtimeTarget') -or !$deps.targets.Contains($deps.runtimeTarget.name)) { throw 'DependencyInvalidDeps' }
        $target = $deps.targets[$deps.runtimeTarget.name]
        $lockPath=Join-Path $root ('src/'+$entry+'/packages.lock.json'); $lock=Read-FDJson $lockPath
        $lockPackages=@{}
        foreach($framework in $lock.dependencies.Values){foreach($name in $framework.Keys){$resolved=$framework[$name];if($resolved.Contains('resolved')){$lockPackages[$name+'/'+$resolved.resolved]=$resolved.contentHash}}}
        $locks.Add([ordered]@{path='src/'+$entry+'/packages.lock.json';sha256=Get-FDHash $lockPath})
        foreach ($key in @($deps.libraries.Keys | Sort-Object -CaseSensitive)) {
            if ($key -cnotmatch '\A([A-Za-z0-9][A-Za-z0-9._-]{0,127})/([0-9][A-Za-z0-9.+-]{0,79})\z') { throw 'DependencyInvalidLibraryIdentity' }
            $id=$Matches[1]; $version=$Matches[2]; $library=$deps.libraries[$key]
            if ($library.type -cnotin @('package','project') -or !$target.Contains($key)) { throw 'DependencyUnsupportedLibrary' }
            if ($libraries.ContainsKey($key)) {
                if ($libraries[$key].type -cne $library.type -or $libraries[$key].sha512 -cne $library.sha512) { throw 'DependencyEntryClosureConflict' }
            } else { if($libraries.Count -ge 512){throw 'DependencyComponentLimit'};$libraries[$key]=[ordered]@{id=$id;version=$version;type=$library.type;sha512=$library.sha512;entries=[Collections.Generic.List[string]]::new();dependencies=@{}} }
            $libraries[$key].entries.Add($entry)
            if ($library.type -ceq 'package' -and ($library.path -cne ($id.ToLowerInvariant()+'/'+$version.ToLowerInvariant()) -or
                $library.hashPath -cne ($id.ToLowerInvariant()+'.'+$version.ToLowerInvariant()+'.nupkg.sha512'))) { throw 'DependencyUnsafePackageLocation' }
            if($library.type -ceq 'package' -and (!$lockPackages.ContainsKey($key) -or ('sha512-'+$lockPackages[$key]) -cne $library.sha512)){throw 'DependencyLockMismatch'}
            if($target[$key].Contains('dependencies')){foreach($dependency in $target[$key].dependencies.Keys){
                $dependencyKey=$dependency+'/'+$target[$key].dependencies[$dependency]
                if(!$deps.libraries.Contains($dependencyKey)){throw 'DependencyEdgeMissing'}
                $libraries[$key].dependencies[$dependencyKey]=$true
            }}
            if (!$assetIndex.ContainsKey($key)) { $assetIndex[$key]=@{} }
            foreach ($group in @('runtime','native','runtimeTargets','resources')) {
                if (!$target[$key].Contains($group)) { continue }
                foreach ($asset in $target[$key][$group].Keys) {
                    if ($asset -ceq '_._' -or $asset.EndsWith('/_._',[StringComparison]::Ordinal)) { continue }
                    Assert-FDRelative $asset
                    $published = if ($group -ceq 'runtimeTargets') { $asset } elseif ($group -ceq 'resources') {
                        ($target[$key][$group][$asset].locale + '/' + [IO.Path]::GetFileName($asset))
                    } else { [IO.Path]::GetFileName($asset) }
                    if (!$fileIndex.ContainsKey($published)) { throw 'DependencyPublishedAssetMissing' }
                    $assetIndex[$key][$published]=[ordered]@{runtimePath=$published;packagePath=$asset;sha256=$fileIndex[$published].sha256}
                }
            }
        }
        foreach ($key in $target.Keys) { if (!$deps.libraries.Contains($key)) { throw 'DependencyUnlistedLibrary' } }
        $runtimeConfig = Read-FDJson (Join-Path $runtime ($entry+'.runtimeconfig.json'))
        if ($runtimeConfig.runtimeOptions.Contains('includedFrameworks')) { throw 'DependencySelfContainedUnsupported' }
        $requested = @(); if ($runtimeConfig.runtimeOptions.Contains('frameworks')) { $requested += $runtimeConfig.runtimeOptions.frameworks }
        if ($runtimeConfig.runtimeOptions.Contains('framework')) { $requested += $runtimeConfig.runtimeOptions.framework }
        foreach ($framework in $requested) { $frameworks[$framework.name+'/'+$framework.version]=[ordered]@{name=$framework.name;version=$framework.version;includedInRuntime=$false;rightsAndInstallationQualification='ExternalPrerequisiteNotAssessed'} }
    }
    $packages=[Collections.Generic.List[object]]::new(); $projects=[Collections.Generic.List[object]]::new()
    $supplements=Read-FDNoticeSupplements $root
    $attributed=@{}
    foreach($key in $assetIndex.Keys){foreach($path in $assetIndex[$key].Keys){$attributed[$path]=$true}}
    foreach($key in $libraries.Keys){if($libraries[$key].type -ceq 'project'){foreach($extension in @('.deps.json','.runtimeconfig.json')){$attributed[$libraries[$key].id+$extension]=$true}}}
    foreach($file in $runtimeFiles){if(!$attributed.ContainsKey($file.path)){$issues.Add([ordered]@{component=$file.path;code='DependencyRuntimeFileUnattributed';classification='TechnicalBlocker'})}}
    $packageMetadataBytes=[long]0
    foreach ($key in @($libraries.Keys | Sort-Object -CaseSensitive)) {
        $library=$libraries[$key]; $assets=@($assetIndex[$key].Values | Sort-Object runtimePath -CaseSensitive)
        if ($library.type -ceq 'project') {
            $projectPath='src/'+$library.id+'/'+$library.id+'.csproj'; $projectFull=Join-Path $root $projectPath
            if (!(Test-Path -LiteralPath $projectFull -PathType Leaf)) { throw 'DependencyProjectSourceMissing' }
            $projects.Add([ordered]@{id=$library.id;version=$library.version;projectPath=$projectPath;projectSha256=Get-FDHash $projectFull;
                entries=@($library.entries);licenseDeclared='NOASSERTION';licenseStatus='FirstPartyCommercialRightsUnspecified';publishedAssets=$assets})
            $issues.Add([ordered]@{component=$key;code='FirstPartyLicenseUndeclared';classification='ReleaseBlocker'})
            continue
        }
        try {
            if (!$library.sha512.StartsWith('sha512-',[StringComparison]::Ordinal)) { throw 'DependencyInvalidPackageHash' }
            $package = Get-FDPackage $library.id $library.version $library.sha512.Substring(7) $cache $NuGetSdkDirectory $assets
            try{Set-FDPackageSupplement $package $supplements}catch{
                $package['supplementStatus']='Rejected';$package['supplementaryNotices']=@();$package['licensingReviewRequired']=$true
                $issues.Add([ordered]@{component=$key;code='DependencySupplementPackageBindingMismatch';classification='TechnicalBlocker'})
            }
            $packageMetadataBytes+=[Text.Encoding]::UTF8.GetByteCount(($package|ConvertTo-Json -Depth 30 -Compress))
            if($packageMetadataBytes -gt 16777216){throw 'DependencyEvidenceLimit'}
            $package['entries']=@($library.entries); $package['dependencies']=@($library.dependencies.Keys | Sort-Object -CaseSensitive); $packages.Add($package)
            if($package.archiveDeclaredLicenseFileMissing -and $package.supplementStatus -cne 'PinnedTextVerified'){
                $code=if($package.supplementStatus -ceq 'MissingRepositoryCommit'){'LicenseTextRepositoryCommitMissing'}else{'LicenseTextNotSuppliedByArchive'}
                $issues.Add([ordered]@{component=$key;code=$code;classification='ReleaseBlocker'})
            }
            if($package.supplementStatus -ceq 'PinnedTextVerified' -and $package.licensingReviewRequired){
                $issues.Add([ordered]@{component=$key;code='UpstreamLicenseTransitionReviewRequired';classification='ReleaseBlocker'})
            }
        } catch {
            if($_.Exception.Message -ceq 'DependencyEvidenceLimit'){throw}
            $code=if ($_.Exception.Message -cmatch '\ADependency[A-Za-z]+\z') { $_.Exception.Message } else { 'DependencyPackageRejected' }
            $issues.Add([ordered]@{component=$key;code=$code;classification='TechnicalBlocker'})
            $observed=$null
            try{$candidate=Join-Path $cache ($library.id.ToLowerInvariant()+'/'+$library.version.ToLowerInvariant()+'/'+$library.id.ToLowerInvariant()+'.'+$library.version.ToLowerInvariant()+'.nupkg');
                if((Test-Path -LiteralPath $candidate -PathType Leaf) -and (Get-Item -LiteralPath $candidate).Length -le 134217728){$observed=Get-FDHash $candidate 'SHA512'}}catch{}
            $packages.Add([ordered]@{id=$library.id;version=$library.version;licenseDeclared='NOASSERTION';rejected=$true;reason=$code;
                observedRejectedArchiveSha512=$observed;expectedContentSha512=$library.sha512;entries=@($library.entries)})
        }
    }
    $source=@(Get-FDSourceDistribution $root)
    $tooling=[ordered]@{generatorVersion='1.1';format='SPDX-2.3';nugetReaderAssemblySha256=$null;nugetReaderAssemblyVersion=$null}
    if(@($packages | Where-Object {$_.Contains('packageSignaturePresent') -and $_.packageSignaturePresent}).Count){
        $readerPath=Join-Path $NuGetSdkDirectory 'NuGet.Packaging.dll';$tooling.nugetReaderAssemblySha256=Get-FDHash $readerPath
        $tooling.nugetReaderAssemblyVersion=[Reflection.AssemblyName]::GetAssemblyName($readerPath).Version.ToString()
    }
    $inputRows.Add([ordered]@{kind='generation';createdUtc=$CreatedUtc;tooling=$tooling})
    $inputRows.Add([ordered]@{kind='runtime';files=$runtimeFiles})
    $inputRows.Add([ordered]@{kind='entryLocks';files=$locks.ToArray()})
    $inputRows.Add([ordered]@{kind='projects';files=@($projects | ForEach-Object {[ordered]@{path=$_.projectPath;sha256=$_.projectSha256}})})
    $inputRows.Add([ordered]@{kind='packages';files=@($packages | ForEach-Object {if($_.Contains('archiveSha512')){[ordered]@{id=$_.id;version=$_.version;archiveSha512=$_.archiveSha512;contentSha512=$_.contentSha512;nuspecSha256=$_.nuspecSha256}}
        else{[ordered]@{id=$_.id;version=$_.version;observedRejectedArchiveSha512=$_.observedRejectedArchiveSha512;expectedContentSha512=$_.expectedContentSha512;reason=$_.reason}}})})
    $inputRows.Add([ordered]@{kind='sourceDistribution';bundles=@($source | ForEach-Object {[ordered]@{id=$_.id;manifestSha256=$_.manifestSha256;files=$_.files}})})
    $supplementSource=[ordered]@{manifestSha256=$supplements.manifestSha256;files=$supplements.files;distributionRightsGranted=$false;legalClearance=$false}
    $inputRows.Add([ordered]@{kind='pinnedPackageNoticeSupplements';source=$supplementSource})
    $inputHash=Get-FDTextHash (ConvertTo-Json -InputObject $inputRows.ToArray() -Depth 30 -Compress)
    $runtimeAfter=@(Get-FDFiles $runtime)
    if ((ConvertTo-Json -InputObject $runtimeFiles -Compress) -cne (ConvertTo-Json -InputObject $runtimeAfter -Compress)) { throw 'DependencyRuntimeChangedDuringRead' }
    $technical=@($issues | Where-Object classification -CEQ 'TechnicalBlocker')
    $report=[ordered]@{schema='platform-studio.foundation-dependency-evidence.v1';createdUtc=$CreatedUtc;inputSha256=$inputHash;
        technicalStatus=if($issues.Count){'Blocked'}else{'Complete'};packageEvidenceComplete=($technical.Count -eq 0);
        releaseClassification='BlockedPendingCommercialRightsAndQualification';issues=$issues.ToArray();runtimeFiles=$runtimeFiles;
        packages=$packages.ToArray();projects=$projects.ToArray();entryLocks=$locks.ToArray();tooling=$tooling;sourceDistribution=$source;pinnedPackageNoticeSupplements=$supplementSource;
        licenseTextSummary=[ordered]@{archiveMissing=@($packages|Where-Object{$_.Contains('archiveMissingLicenseText') -and $_.archiveMissingLicenseText}).Count;
            archiveDeclaredLicenseFileMissing=@($packages|Where-Object{$_.Contains('archiveDeclaredLicenseFileMissing') -and $_.archiveDeclaredLicenseFileMissing}).Count;
            supplementResolved=@($packages|Where-Object{$_.Contains('supplementStatus') -and $_.supplementStatus -ceq 'PinnedTextVerified'}).Count;
            rawArchiveMissingSupplementResolved=@($packages|Where-Object{$_.Contains('archiveMissingLicenseText') -and $_.archiveMissingLicenseText -and $_.supplementStatus -ceq 'PinnedTextVerified'}).Count;
            unresolved=@($packages|Where-Object{$_.Contains('archiveDeclaredLicenseFileMissing') -and $_.archiveDeclaredLicenseFileMissing -and $_.supplementStatus -cne 'PinnedTextVerified'}).Count;
            upstreamReviewRequired=@($packages|Where-Object{$_.Contains('licensingReviewRequired') -and $_.licensingReviewRequired}).Count};
        externalFrameworks=@($frameworks.Values | Sort-Object name);
        runtimeFileHashesComplete=$true;publishedPackageAssetsByteMatched=($technical.Count -eq 0);binaryLicenseCoverageVerified=$false;
        signatureTrustVerified=$false;legalClearance=$false;signed=$false;sourceFreezeVerified=$false;productionReady=$false}
    $spdxPackages=[Collections.Generic.List[object]]::new(); $extract=[Collections.Generic.List[object]]::new(); $relationships=[Collections.Generic.List[object]]::new()
    foreach ($package in $packages) {
        $spdxId='SPDXRef-Package-'+(Get-FDTextHash ($package.id+'/'+$package.version)).Substring(0,24)
        $record=[ordered]@{SPDXID=$spdxId;name=$package.id;versionInfo=$package.version;downloadLocation='NOASSERTION';filesAnalyzed=$false;
            licenseDeclared=$package.licenseDeclared;licenseConcluded='NOASSERTION';copyrightText='NOASSERTION';
            comment='NuGet dependency declared by published entry deps; publisher identity/signature trust and legal rights are unqualified.'}
        if($package.Contains('declaredCopyright') -and $package.declaredCopyright){$record.copyrightText=$package.declaredCopyright}
        if($package.Contains('licensingReviewRequired') -and $package.licensingReviewRequired){$record['licenseComments']='Original package declaration is preserved; upstream supplementary licensing context requires review. No legal conclusion is asserted.'}
        if ($package.Contains('archiveSha512')) { $record['checksums']=@([ordered]@{algorithm='SHA512';checksumValue=$package.archiveSha512}) }
        if ($package.Contains('extractedLicense') -and $package.extractedLicense) { $extract.Add($package.extractedLicense) }
        $spdxPackages.Add($record)
    }
    foreach ($project in $projects) { $spdxPackages.Add([ordered]@{SPDXID='SPDXRef-Project-'+(Get-FDTextHash $project.id).Substring(0,24);name=$project.id;
        versionInfo=$project.version;downloadLocation='NOASSERTION';filesAnalyzed=$false;licenseDeclared='NOASSERTION';licenseConcluded='NOASSERTION';copyrightText='NOASSERTION';comment='First-party project. Commercial distribution rights are unspecified; source project hash is in dependency-evidence.json.'}) }
    foreach ($package in $spdxPackages) { $relationships.Add([ordered]@{spdxElementId='SPDXRef-DOCUMENT';relationshipType='DESCRIBES';relatedSpdxElement=$package.SPDXID}) }
    $spdxIndex=@{};foreach($package in $spdxPackages){$spdxIndex[$package.name+'/'+$package.versionInfo]=$package.SPDXID}
    foreach($key in @($libraries.Keys | Sort-Object -CaseSensitive)){foreach($dependency in @($libraries[$key].dependencies.Keys | Sort-Object -CaseSensitive)){
        $relationships.Add([ordered]@{spdxElementId=$spdxIndex[$key];relationshipType='DEPENDS_ON';relatedSpdxElement=$spdxIndex[$dependency]})
    }}
    $spdxFiles=@($runtimeFiles | ForEach-Object {[ordered]@{SPDXID='SPDXRef-File-'+(Get-FDTextHash $_.path).Substring(0,24);fileName='./runtime/'+$_.path;
        checksums=@([ordered]@{algorithm='SHA256';checksumValue=$_.sha256});licenseConcluded='NOASSERTION';copyrightText='NOASSERTION'}})
    $sbom=[ordered]@{spdxVersion='SPDX-2.3';dataLicense='CC0-1.0';SPDXID='SPDXRef-DOCUMENT';name='Platform Studio Foundation published runtime dependency evidence';
        documentNamespace='https://spdx.org/spdxdocs/platform-studio-foundation-'+$inputHash;creationInfo=[ordered]@{created=$CreatedUtc;creators=@('Tool: PlatformStudio-FoundationDependencyEvidence-1.1')};
        packages=$spdxPackages.ToArray();files=$spdxFiles;relationships=$relationships.ToArray();hasExtractedLicensingInfos=$extract.ToArray();
        comment='Local evidence only. Framework-dependent runtime prerequisites and source-only vendors are classified separately in dependency-evidence.json. filesAnalyzed=false does not claim binary license coverage or legal clearance.'}
    $notices=[Text.StringBuilder]::new(); [void]$notices.AppendLine('Platform Studio Foundation dependency declarations and supplied notices')
    [void]$notices.AppendLine('LOCAL EVIDENCE ONLY. No legal clearance, license conclusion, signing, source freeze or production acceptance.')
    foreach ($package in $packages) {
        [void]$notices.AppendLine("`nPACKAGE: $($package.id)/$($package.version)`nDECLARED LICENSE: $($package.licenseDeclared)")
        if($package.Contains('declaredCopyright')){[void]$notices.AppendLine("PACKAGE-DECLARED COPYRIGHT: $($package.declaredCopyright)")}
        if ($package.Contains('notices')) { foreach ($notice in $package.notices) { [void]$notices.AppendLine("--- $($notice.path) ---`n$($notice.text)") } }
        if($package.Contains('supplementStatus')){[void]$notices.AppendLine("ORIGINAL ARCHIVE-NOTICE ABSENCE: $($package.archiveMissingLicenseText); DECLARED LICENSE FILE MISSING: $($package.archiveDeclaredLicenseFileMissing); SUPPLEMENT STATUS: $($package.supplementStatus)")}
        if($package.Contains('supplementaryNotices')){foreach($notice in $package.supplementaryNotices){[void]$notices.AppendLine("--- PINNED UPSTREAM SUPPLEMENT: $($notice.sourceUrl) SHA256 $($notice.sha256) ---`n$($notice.text)")}}
    }
    [void]$notices.AppendLine("`nSOURCE-ONLY GUIDANCE AND TEMPLATES (not part of the binary runtime closure)")
    foreach ($bundle in $source) { foreach ($notice in $bundle.notices) { [void]$notices.AppendLine("`n$($bundle.id): $($notice.path)`n$($notice.text)") } }
    $outputs=[ordered]@{'dependency-evidence.json'=($report | ConvertTo-Json -Depth 40);'sbom.spdx.json'=($sbom | ConvertTo-Json -Depth 30);'THIRD-PARTY-NOTICES.txt'=$notices.ToString()}
    $total=[long]0; foreach ($value in $outputs.Values) { $size=[Text.Encoding]::UTF8.GetByteCount($value);$total+=$size;if($size -gt 16777216 -or $total -gt 33554432){throw 'DependencyEvidenceLimit'} }
    $null=New-Item -ItemType Directory -Path $destination
    $lines=foreach($name in $outputs.Keys){$stream=[IO.File]::Open((Join-Path $destination $name),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
        try{$bytes=[Text.Encoding]::UTF8.GetBytes($outputs[$name]);$stream.Write($bytes)}finally{$stream.Dispose()};(Get-FDHash (Join-Path $destination $name))+'  '+$name}
    $stream=[IO.File]::Open((Join-Path $destination 'MANIFEST.sha256'),[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try{$bytes=[Text.Encoding]::UTF8.GetBytes((($lines | Sort-Object -CaseSensitive)-join "`n")+"`n");$stream.Write($bytes)}finally{$stream.Dispose()}
    [pscustomobject]@{technicalStatus=$report.technicalStatus;packageEvidenceComplete=$report.packageEvidenceComplete;inputSha256=$inputHash;outputDirectory=$destination;
        runtimeFiles=$runtimeFiles.Count;packages=$packages.Count;projects=$projects.Count;issues=$issues.Count;productionReady=$false}
}

if ($MyInvocation.InvocationName -ne '.') {
    if (!$RepositoryRoot) { $RepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..')) }
    $result=New-FoundationDependencyEvidence -RuntimeDirectory $RuntimeDirectory -NuGetPackagesDirectory $NuGetPackagesDirectory -OutputDirectory $OutputDirectory -RepositoryRoot $RepositoryRoot -CreatedUtc $CreatedUtc -NuGetSdkDirectory $NuGetSdkDirectory
    $result
    if ($result.technicalStatus -cne 'Complete') { throw 'DependencyEvidenceBlocked; review dependency-evidence.json issues before commercial distribution.' }
}
