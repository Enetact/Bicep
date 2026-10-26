#requires -Version 7.4
[CmdletBinding()]
param(
    [switch]$LocalPreview,
    [switch]$ProductionReady,
    [string]$OutputDirectory = '',
    [string]$DotNetPath = 'dotnet'
)

$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

function Assert-FoundationUnlinkedPath {
    param([Parameter(Mandatory)][string]$Path)
    $resolved = [IO.Path]::GetFullPath($Path)
    for ($candidate = $resolved; $candidate; $candidate = [IO.Path]::GetDirectoryName($candidate)) {
        if (([IO.File]::Exists($candidate) -or [IO.Directory]::Exists($candidate)) -and
            ((Get-Item -LiteralPath $candidate -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) {
            throw 'Linked package paths are forbidden.'
        }
    }
}

function Resolve-FoundationPackageDestination {
    param([Parameter(Mandatory)][string]$RepositoryRoot, [string]$RequestedDirectory = '', [string]$RunId = '')
    if (!$RunId) { $RunId = [Guid]::NewGuid().ToString('N') }
    if ($RunId -cnotmatch '\A[a-f0-9]{32}\z') { throw 'Invalid package run identifier.' }
    $packageRoot = [IO.Path]::GetFullPath((Join-Path $RepositoryRoot 'artifacts/foundation-packages'))
    $destination = if ($RequestedDirectory) { [IO.Path]::GetFullPath($RequestedDirectory) } else { Join-Path $packageRoot $RunId }
    $comparison = if ($IsWindows) { [StringComparison]::OrdinalIgnoreCase } else { [StringComparison]::Ordinal }
    if (![string]::Equals([IO.Path]::GetDirectoryName($destination), $packageRoot, $comparison) -or
        [IO.Path]::GetFileName($destination) -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9._-]{0,100}\z') {
        throw 'Package output must be a new direct child of this checkout artifacts/foundation-packages directory.'
    }
    Assert-FoundationUnlinkedPath $packageRoot
    Assert-FoundationUnlinkedPath $destination
    if (Test-Path -LiteralPath $destination) { throw 'Package output already exists; reuse and overwrite are forbidden.' }
    [pscustomobject]@{ Root = $packageRoot; Destination = $destination; RunId = $RunId }
}

function Get-FoundationPackageFiles {
    param([Parameter(Mandatory)][string]$Directory)
    Assert-FoundationUnlinkedPath $Directory
    $root = [IO.Path]::GetFullPath($Directory)
    if (![IO.Directory]::Exists($root)) { throw 'Package directory is missing.' }
    $pending = [Collections.Generic.Stack[string]]::new()
    $pending.Push($root)
    $files = [Collections.Generic.List[object]]::new()
    while ($pending.Count) {
        $current = $pending.Pop()
        foreach ($item in Get-ChildItem -LiteralPath $current -Force) {
            if ($item.Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Linked package contents are forbidden.' }
            if ($item.PSIsContainer) { $pending.Push($item.FullName); continue }
            $relative = [IO.Path]::GetRelativePath($root, $item.FullName).Replace('\', '/')
            if ($relative.StartsWith('../', [StringComparison]::Ordinal) -or $relative.Contains(':') -or $relative.Contains('\')) {
                throw 'Package content escaped its owned directory.'
            }
            $files.Add([pscustomobject]@{ Path = $relative; FullName = $item.FullName; Length = $item.Length })
            if ($files.Count -gt 5000) { throw 'Package file limit exceeded.' }
        }
    }
    @($files | Sort-Object Path -CaseSensitive)
}

function Assert-FoundationSafeBytes {
    param([Parameter(Mandatory)][string]$Path, [Parameter(Mandatory)][byte[]]$Bytes)
    if ($Bytes.Length -gt 67108864) { throw 'Runtime file exceeds the preview validation limit.' }
    $pattern = '(?i)(-----BEGIN(?: RSA| EC| OPENSSH)? PRIVATE KEY-----\s+[A-Za-z0-9+/=\r\n]{48,}-----END|github_pat_[A-Za-z0-9_]{16,}|gh[pousr]_[A-Za-z0-9]{16,}|Bearer [A-Za-z0-9_.-]{24,})'
    foreach ($encoding in @([Text.Encoding]::UTF8, [Text.Encoding]::Unicode)) {
        if ([regex]::IsMatch($encoding.GetString($Bytes), $pattern, [Text.RegularExpressions.RegexOptions]::CultureInvariant, [TimeSpan]::FromSeconds(2))) {
            throw 'Credential-shaped content is forbidden in a local preview package.'
        }
    }
    if ($Path.EndsWith('.json', [StringComparison]::OrdinalIgnoreCase)) {
        $json = [Text.Encoding]::UTF8.GetString($Bytes) | ConvertFrom-Json -AsHashtable -Depth 64
        $pending = [Collections.Generic.Stack[object]]::new()
        if ($null -ne $json) { $pending.Push($json) }
        while ($pending.Count) {
            $value = $pending.Pop()
            if ($value -is [Collections.IDictionary]) {
                foreach ($key in $value.Keys) {
                    $normalizedKey = $key -replace '[-_\s]', ''
                    if ($normalizedKey -match '\A(?:accessToken|refreshToken|idToken|clientSecret|password|privateKey|connectionString|authorization|bearer|personalAccessToken|pat|apiKey|accountKey|sharedAccessKey|sharedAccessSignature)\z') {
                        throw 'Credential fields are forbidden in package JSON.'
                    }
                    if ($null -ne $value[$key]) { $pending.Push($value[$key]) }
                }
            } elseif ($value -is [Collections.IEnumerable] -and $value -isnot [string]) {
                foreach ($child in $value) { if ($null -ne $child) { $pending.Push($child) } }
            }
        }
    }
}

function Assert-FoundationRuntimeName {
    param([Parameter(Mandatory)][string]$RelativePath)
    if ($RelativePath -match '(?i)(?:\A|/)(?:\.git|\.local|\.azure|\.codex|\.aws|node_modules|TestResults|credential-cache|azure-cli|github-cli|backups?|exports?|wwwroot)(?:/|\z)' -or
        $RelativePath -match '(?i)(?:\.sqlite(?:3)?(?:-wal|-shm|-journal)?|\.db(?:-wal|-shm)?|\.pem|\.key|\.pfx|\.p12|\.pdb|\.cs|\.ps1|\.bicep|\.bicepparam)\z' -or
        $RelativePath -match '(?i)(?:auth\.json|accessTokens\.json|azureProfile\.json|hosts\.yml|portal\.local\.json|local\.settings[^/]*|appsettings[^/]*)\z') {
        throw 'Runtime/customer state or source files are forbidden in the runtime package.'
    }
    if ($RelativePath -cnotmatch '\A[A-Za-z0-9_./+-]+\z' -or $RelativePath.Split('/') -contains '..' -or
        $RelativePath -match '(?i)(?:\.Tests?|testhost|xunit|Microsoft\.TestPlatform)' -or
        $RelativePath -notmatch '(?i)(?:\.dll|\.so(?:\.\d+)*|\.dylib|\.a|\.lib|\.(?:deps|runtimeconfig)\.json)\z') {
        throw 'Unexpected runtime package file type.'
    }
}

function Test-FoundationRuntimeTree {
    param([Parameter(Mandatory)][string]$RuntimeDirectory)
    $files = @(Get-FoundationPackageFiles $RuntimeDirectory)
    if (!$files.Count) { throw 'Empty runtime packages are forbidden.' }
    $length = [long]0
    foreach ($file in $files) {
        Assert-FoundationRuntimeName $file.Path
        $length += $file.Length
        if ($length -gt 268435456) { throw 'Runtime package exceeds the local preview size limit.' }
        Assert-FoundationSafeBytes $file.Path ([IO.File]::ReadAllBytes($file.FullName))
    }
    [pscustomobject]@{ FileCount = $files.Count; Bytes = $length }
}

function Get-FoundationEntryNames { @('SelfService.Foundation.Host','SelfService.Foundation.Worker','SelfService.Foundation.Owner') }
function Get-FoundationCorePublicFiles {
    # Preserve the existing preview contract when newer optional guides are added to newly published archives.
    @('config/foundation-cell.example.json','config/foundation-entra-onboarding.example.json',
      'docs/foundation-service.md','docs/foundation-owner-cli.md','docs/foundation-entra-onboarding.md')
}
function Get-FoundationPublicFiles {
    @(Get-FoundationCorePublicFiles) + @('docs/foundation-inactive-reconciliation.md','docs/foundation-credential-lifecycle.md',
      'docs/foundation-credential-rewrap.md','docs/foundation-durable-dispatch-authorization.md',
      'docs/foundation-credential-namespaces.md','docs/foundation-worker-receipts.md','docs/foundation-credential-rollback.md',
      'docs/foundation-credential-maintenance.md','docs/foundation-maintenance-observation.md',
      'docs/foundation-credential-recovery-inventory.md','docs/foundation-credential-recovery-retention.md',
      'docs/foundation-backup-checkpoint-custody.md','docs/foundation-backup-checkpoint-recovery.md',
      'docs/foundation-credential-recovery-set.md','docs/foundation-credential-recovery-rehearsal.md',
      'docs/foundation-credential-recovery-staging.md','docs/foundation-credential-recovery-stage-verification.md',
      'docs/foundation-credential-recovery-review.md','docs/foundation-credential-recovery-review-execution.md',
      'docs/foundation-credential-recovery-checkpoints.md','docs/foundation-credential-checkpoint-staging.md',
      'docs/foundation-absence-scope-provenance.md','docs/foundation-absence-scope-inspection.md',
      'docs/foundation-scoped-recovery.md','docs/foundation-scoped-recovery-staging.md',
      'docs/foundation-scoped-recovery-review.md','docs/foundation-scoped-recovery-checkpoints.md',
      'docs/foundation-scoped-recovery-joint-verification.md','docs/foundation-recovery-execution-hold.md',
      'docs/foundation-credential-runtime-binding.md','docs/foundation-bound-pipeline-consumer.md',
      'docs/foundation-restricted-recovery.md','docs/foundation-restricted-recovery-staging.md',
      'docs/foundation-restricted-recovery-checkpoints.md','docs/foundation-restricted-recovery-review.md',
      'docs/foundation-restricted-recovery-joint-verification.md')
}
function Get-FoundationRequiredPackageFiles {
    foreach ($entry in @(Get-FoundationEntryNames)) { foreach ($extension in @('.dll','.deps.json','.runtimeconfig.json')) { 'runtime/' + $entry + $extension } }
    @(Get-FoundationCorePublicFiles) + @('README.md')
}

function Test-FoundationPackageTree {
    param([Parameter(Mandatory)][string]$PackageDirectory)
    $runtime = Test-FoundationRuntimeTree (Join-Path $PackageDirectory 'runtime')
    foreach ($required in @(Get-FoundationRequiredPackageFiles)) {
        if (!(Test-Path -LiteralPath (Join-Path $PackageDirectory $required) -PathType Leaf)) { throw 'Required package content is missing.' }
    }
    $null = Test-FoundationDependencyTree $PackageDirectory
    foreach ($file in @(Get-FoundationPackageFiles $PackageDirectory)) {
        if ($file.Path.StartsWith('runtime/', [StringComparison]::Ordinal)) { continue }
        if ($file.Path -cnotin (@(Get-FoundationPublicFiles) + @('README.md', 'inventory.json', 'MANIFEST.sha256') + @(Get-FoundationDependencyFiles))) {
            throw 'Unreviewed source/runtime state is forbidden in a package.'
        }
        Assert-FoundationSafeBytes $file.Path ([IO.File]::ReadAllBytes($file.FullName))
    }
    $runtime
}

function Get-FoundationDependencyFiles {
    @('dependency-evidence/dependency-evidence.json','dependency-evidence/sbom.spdx.json','dependency-evidence/THIRD-PARTY-NOTICES.txt','dependency-evidence/MANIFEST.sha256')
}

function Assert-FoundationDependencyReport {
    param([Parameter(Mandatory)][byte[]]$Bytes, [Parameter(Mandatory)][object[]]$RuntimeFiles)
    if ($Bytes.Length -gt 16777216) { throw 'Dependency evidence is too large.' }
    Assert-FoundationSafeBytes 'dependency-evidence.json' $Bytes
    $report = [Text.Encoding]::UTF8.GetString($Bytes) | ConvertFrom-Json -AsHashtable -Depth 64
    if ($report.schema -cne 'platform-studio.foundation-dependency-evidence.v1' -or $report.packageEvidenceComplete -isnot [bool] -or !$report.packageEvidenceComplete -or
        $report.technicalStatus -cnotin @('Complete','Blocked')) { throw 'Dependency evidence is incomplete.' }
    foreach ($flag in @('binaryLicenseCoverageVerified','signatureTrustVerified','legalClearance','signed','sourceFreezeVerified','productionReady')) {
        if (!$report.ContainsKey($flag) -or $report[$flag] -isnot [bool] -or $report[$flag]) { throw 'Dependency evidence makes an unsupported clearance claim.' }
    }
    $actual = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
    foreach ($file in $RuntimeFiles) { if (!$actual.TryAdd($file.Path,$file)) { throw 'Duplicate runtime dependency input.' } }
    $seen = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    if ($report.runtimeFiles.Count -ne $actual.Count) { throw 'Dependency evidence runtime coverage mismatch.' }
    foreach ($file in $report.runtimeFiles) {
        if (!$seen.Add($file.path) -or !$actual.ContainsKey($file.path) -or $file.sha256 -cne $actual[$file.path].Sha256 -or
            $file.bytes -ne $actual[$file.path].Bytes) { throw 'Dependency evidence runtime binding mismatch.' }
    }
}

function Test-FoundationDependencyTree {
    param([Parameter(Mandatory)][string]$PackageDirectory)
    foreach ($relative in @(Get-FoundationDependencyFiles)) {
        if (!(Test-Path -LiteralPath (Join-Path $PackageDirectory $relative) -PathType Leaf)) { throw 'Required dependency evidence is missing.' }
    }
    $folder = Join-Path $PackageDirectory 'dependency-evidence'
    $files = @(Get-FoundationPackageFiles $folder)
    if ($files.Count -ne 4) { throw 'Unexpected dependency evidence files.' }
    $expected = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($line in Get-Content -LiteralPath (Join-Path $folder 'MANIFEST.sha256')) {
        if ($line -cnotmatch '\A([a-f0-9]{64})  (dependency-evidence\.json|sbom\.spdx\.json|THIRD-PARTY-NOTICES\.txt)\z' -or
            !$expected.TryAdd($Matches[2],$Matches[1])) { throw 'Invalid dependency evidence manifest.' }
    }
    if ($expected.Count -ne 3) { throw 'Incomplete dependency evidence manifest.' }
    foreach ($entry in $expected.GetEnumerator()) {
        if ((Get-FileHash -LiteralPath (Join-Path $folder $entry.Key)).Hash.ToLowerInvariant() -cne $entry.Value) { throw 'Dependency evidence content hash mismatch.' }
    }
    $runtimeFiles = @(Get-FoundationPackageFiles (Join-Path $PackageDirectory 'runtime') | ForEach-Object {
        [pscustomobject]@{Path=$_.Path;Bytes=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
    })
    Assert-FoundationDependencyReport ([IO.File]::ReadAllBytes((Join-Path $folder 'dependency-evidence.json'))) $runtimeFiles
}

function Read-FoundationArchiveEntry {
    param([Parameter(Mandatory)][IO.Compression.ZipArchiveEntry]$Entry, [Parameter(Mandatory)][long]$MaximumBytes, [switch]$IncludeBytes)
    if ($Entry.Length -gt $MaximumBytes) { throw 'Archive entry declared size limit exceeded.' }
    $stream=$Entry.Open(); $hasher=[Security.Cryptography.IncrementalHash]::CreateHash([Security.Cryptography.HashAlgorithmName]::SHA256)
    $output=if($IncludeBytes){[IO.MemoryStream]::new()}else{$null}; $total=[long]0; $buffer=[byte[]]::new(8192)
    try {
        while (($count=$stream.Read($buffer,0,$buffer.Length)) -gt 0) {
            $total+=$count
            if ($total -gt $MaximumBytes) { throw 'Archive entry actual size limit exceeded.' }
            $hasher.AppendData($buffer,0,$count)
            if ($null -ne $output) { $output.Write($buffer,0,$count) }
        }
        if ($total -ne $Entry.Length) { throw 'Archive entry actual length mismatch.' }
        [pscustomobject]@{Sha256=[Convert]::ToHexString($hasher.GetHashAndReset()).ToLowerInvariant();Bytes=$total;Content=if($null -ne $output){$output.ToArray()}else{$null}}
    } finally { $stream.Dispose();$hasher.Dispose();if($null -ne $output){$output.Dispose()} }
}

function Assert-FoundationArchiveRelativePath {
    param([Parameter(Mandatory)][string]$Path)
    if($Path -cnotmatch '\A[A-Za-z0-9_./+-]+\z' -or $Path.StartsWith('/') -or
        @($Path.Split('/') | Where-Object { !$_ -or $_ -in @('.','..') -or $_.EndsWith('.') -or $_.EndsWith(' ') -or
            $_ -match '(?i)\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)' }).Count){
        throw 'Unsafe or ambiguous archive path.'
    }
}

function Test-FoundationArchive {
    param([Parameter(Mandatory)][string]$ArchivePath)
    Assert-FoundationUnlinkedPath $ArchivePath
    $archiveStream = [IO.File]::Open([IO.Path]::GetFullPath($ArchivePath),[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $archive = $null
    try {
        if($archiveStream.Length -gt 268435456){throw 'Archive size limit exceeded.'}
        $archive = [IO.Compression.ZipArchive]::new($archiveStream,[IO.Compression.ZipArchiveMode]::Read,$false)
        if($archive.Entries.Count -gt 5000){throw 'Archive entry limit exceeded.'}
        foreach($entry in $archive.Entries){
            $directory = $entry.FullName.EndsWith('/',[StringComparison]::Ordinal)
            $path = if($directory){$entry.FullName.Substring(0,$entry.FullName.Length-1)}else{$entry.FullName}
            Assert-FoundationArchiveRelativePath $path
            if((($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000 -or ($directory -and $entry.Length -ne 0)){
                throw 'Unsafe archive directory or symbolic link.'
            }
        }
        $entries = @($archive.Entries | Where-Object { !$_.FullName.EndsWith('/', [StringComparison]::Ordinal) })
        if ($entries.Count -gt 5000) { throw 'Archive entry limit exceeded.' }
        $byName = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::OrdinalIgnoreCase)
        $length = [long]0
        foreach ($entry in $entries) {
            $name = $entry.FullName
            if ($name.Contains('\') -or $name.Contains(':') -or $name.StartsWith('/') -or $name.Split('/') -contains '..' -or
                (($entry.ExternalAttributes -shr 16) -band 0xF000) -eq 0xA000 -or !$byName.TryAdd($name, $entry)) {
                throw 'Unsafe or duplicate archive entry.'
            }
            if ($name.StartsWith('runtime/', [StringComparison]::Ordinal)) { Assert-FoundationRuntimeName ($name.Substring(8)) }
            elseif ($name -cnotin (@(Get-FoundationPublicFiles) + @('README.md', 'inventory.json', 'MANIFEST.sha256') + @(Get-FoundationDependencyFiles))) {
                throw 'Unexpected archive contents.'
            }
            $length += $entry.Length
            if ($length -gt 268435456) { throw 'Archive size limit exceeded.' }
        }
        foreach ($required in (@(Get-FoundationRequiredPackageFiles) + @('inventory.json', 'MANIFEST.sha256'))) {
            if (!$byName.ContainsKey($required)) { throw 'Archive integrity inventory or required content is missing.' }
        }
        foreach ($required in @(Get-FoundationDependencyFiles)) { if (!$byName.ContainsKey($required)) { throw 'Archive dependency evidence is missing.' } }
        $manifestRead = Read-FoundationArchiveEntry $byName['MANIFEST.sha256'] 1048576 -IncludeBytes
        $manifest = [Text.Encoding]::UTF8.GetString($manifestRead.Content)
        $expected = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
        foreach ($line in $manifest.Split("`n", [StringSplitOptions]::RemoveEmptyEntries)) {
            if ($line -cnotmatch '\A([a-f0-9]{64})  ([A-Za-z0-9_./+-]+)\z' -or !$expected.TryAdd($Matches[2], $Matches[1])) {
                throw 'Invalid archive hash inventory.'
            }
        }
        if ($expected.Count -ne $entries.Count - 1) { throw 'Archive inventory coverage is incomplete.' }
        $runtimeFiles = [Collections.Generic.List[object]]::new()
        $verifiedHashes = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
        $actualLength = $manifestRead.Bytes
        foreach ($entry in $entries) {
            if ($entry.FullName -ceq 'MANIFEST.sha256') { continue }
            if (!$expected.ContainsKey($entry.FullName)) { throw 'Unlisted archive contents.' }
            $entryRead = Read-FoundationArchiveEntry $entry 67108864
            $actual = $entryRead.Sha256; $actualLength += $entryRead.Bytes
            if ($actualLength -gt 268435456) { throw 'Archive actual size limit exceeded.' }
            if ($actual -cne $expected[$entry.FullName]) { throw 'Archive content hash mismatch.' }
            $verifiedHashes.Add($entry.FullName,$actual)
            if ($entry.FullName.StartsWith('runtime/',[StringComparison]::Ordinal)) {
                $runtimeFiles.Add([pscustomobject]@{Path=$entry.FullName.Substring(8);Bytes=$entry.Length;Sha256=$actual})
            }
        }
        $innerRead = Read-FoundationArchiveEntry $byName['dependency-evidence/MANIFEST.sha256'] 4096 -IncludeBytes
        if ($innerRead.Sha256 -cne $verifiedHashes['dependency-evidence/MANIFEST.sha256']) { throw 'Archive dependency evidence changed during verification.' }
        $inner = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
        foreach ($line in [Text.Encoding]::UTF8.GetString($innerRead.Content).Split("`n",[StringSplitOptions]::RemoveEmptyEntries)) {
            if ($line -cnotmatch '\A([a-f0-9]{64})  (dependency-evidence\.json|sbom\.spdx\.json|THIRD-PARTY-NOTICES\.txt)\z' -or
                !$inner.TryAdd($Matches[2],$Matches[1])) { throw 'Invalid archive dependency evidence manifest.' }
        }
        if ($inner.Count -ne 3) { throw 'Incomplete archive dependency evidence manifest.' }
        foreach ($entry in $inner.GetEnumerator()) {
            if ($entry.Value -cne $verifiedHashes['dependency-evidence/'+$entry.Key]) { throw 'Archive dependency evidence manifest hash mismatch.' }
        }
        $reportEntry = $byName['dependency-evidence/dependency-evidence.json']
        $reportRead = Read-FoundationArchiveEntry $reportEntry 16777216 -IncludeBytes
        if ($reportRead.Sha256 -cne $verifiedHashes[$reportEntry.FullName]) { throw 'Archive dependency evidence changed during verification.' }
        Assert-FoundationDependencyReport $reportRead.Content $runtimeFiles.ToArray()
        [pscustomobject]@{ Entries = $entries.Count; UncompressedBytes = $length; HashesVerified = $expected.Count }
    } finally { if($null -ne $archive){$archive.Dispose()};$archiveStream.Dispose() }
}

function Merge-FoundationRuntimeClosures {
    param([Parameter(Mandatory)][string]$PrimaryDirectory, [Parameter(Mandatory)][string]$AdditionalDirectory)
    $null = Test-FoundationRuntimeTree $PrimaryDirectory
    $null = Test-FoundationRuntimeTree $AdditionalDirectory
    $additionalFiles = @(Get-FoundationPackageFiles $AdditionalDirectory)
    # Validate every collision before copying any new entry.
    foreach ($file in $additionalFiles) {
        $target = Join-Path $PrimaryDirectory $file.Path
        if ((Test-Path -LiteralPath $target) -and (Get-FileHash -LiteralPath $target).Hash -cne (Get-FileHash -LiteralPath $file.FullName).Hash) {
            throw 'API/worker dependency closures conflict.'
        }
    }
    foreach ($file in $additionalFiles) {
        $target = Join-Path $PrimaryDirectory $file.Path
        if (!(Test-Path -LiteralPath $target)) {
            $null = New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force
            Copy-Item -LiteralPath $file.FullName -Destination $target
        }
    }
    Test-FoundationRuntimeTree $PrimaryDirectory
}

function Get-FoundationPublishHelperFiles {
    @('scripts/Publish-Foundation.ps1','scripts/New-FoundationDependencyEvidence.ps1','scripts/Test-FoundationPackageNoticePins.ps1',
      'scripts/FoundationPublishRetention.ps1','scripts/FoundationBuildRetention.ps1','scripts/FoundationTestExecutionEvidence.ps1','scripts/FoundationOwnedBuildNative.cs')
}

function Get-FoundationPublishSourceStamp {
    param([Parameter(Mandatory)][string]$RepositoryRoot)
    $paths = @(& git -C $RepositoryRoot -c core.quotepath=false ls-files --cached --others --exclude-standard -- src vendor global.json NuGet.Config Directory.Build.props Directory.Build.targets LICENSE 'LICENSE.*' 'NOTICE*')
    if ($LASTEXITCODE -ne 0 -or !$paths.Count) { throw 'Publish source inventory is unavailable.' }
    $paths += @(Get-FoundationPublishHelperFiles) + @(Get-FoundationPublicFiles)
    $rows = foreach ($relative in @($paths | Sort-Object -Unique)) {
        if ($relative -notmatch '\A[A-Za-z0-9_./+-]+\z' -or $relative.Split('/') -contains '..') { throw 'Unsafe publish source path.' }
        $path = Join-Path $RepositoryRoot $relative
        Assert-FoundationUnlinkedPath $path
        $relative + ':' + (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    }
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($rows -join "`n")))).ToLowerInvariant()
}

function New-FoundationPackageArchive {
    param([Parameter(Mandatory)][string]$PackageDirectory, [Parameter(Mandatory)][string]$ArchivePath)
    $parent = [IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($PackageDirectory))
    if ([IO.Path]::GetDirectoryName([IO.Path]::GetFullPath($ArchivePath)) -cne $parent) { throw 'Archive must stay in its owned output directory.' }
    $marker = Join-Path $parent '.package-owner.json'
    if (!(Test-Path -LiteralPath $marker -PathType Leaf)) { throw 'Package output has no ownership marker.' }
    Assert-FoundationUnlinkedPath $marker
    $owner = Get-Content -LiteralPath $marker -Raw | ConvertFrom-Json
    if ($owner.schema -cne 'platform-studio.foundation-package-owner.v1' -or $owner.destination -cne $parent -or
        $owner.runId -cnotmatch '\A[a-f0-9]{32}\z' -or !$owner.localPreview) { throw 'Package output ownership mismatch.' }
    if (Test-Path -LiteralPath $ArchivePath) { throw 'Archive overwrite is forbidden.' }
    $runtime = Test-FoundationPackageTree $PackageDirectory
    if ((Test-Path -LiteralPath (Join-Path $PackageDirectory 'inventory.json')) -or (Test-Path -LiteralPath (Join-Path $PackageDirectory 'MANIFEST.sha256'))) {
        throw 'Package inventories already exist; reuse is forbidden.'
    }
    $inventory = @(@(Get-FoundationPackageFiles $PackageDirectory) | ForEach-Object {
        [ordered]@{ path = $_.Path; bytes = $_.Length; sha256 = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() }
    })
    [ordered]@{ schema = 'platform-studio.foundation-package-inventory.v1'; localPreview = $true; productionReady = $false; files = $inventory } |
        ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $PackageDirectory 'inventory.json') -Encoding utf8NoBOM
    $lines = @(@(Get-FoundationPackageFiles $PackageDirectory) | ForEach-Object {
        (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant() + '  ' + $_.Path
    })
    [IO.File]::WriteAllText((Join-Path $PackageDirectory 'MANIFEST.sha256'), ($lines -join "`n") + "`n", [Text.UTF8Encoding]::new($false))
    Assert-FoundationUnlinkedPath $ArchivePath
    [IO.Compression.ZipFile]::CreateFromDirectory([IO.Path]::GetFullPath($PackageDirectory), [IO.Path]::GetFullPath($ArchivePath), [IO.Compression.CompressionLevel]::Optimal, $false)
    $verified = Test-FoundationArchive $ArchivePath
    [pscustomobject]@{ Archive = [IO.Path]::GetFullPath($ArchivePath); Sha256 = (Get-FileHash -LiteralPath $ArchivePath -Algorithm SHA256).Hash.ToLowerInvariant();
        ArchiveBytes = (Get-Item -LiteralPath $ArchivePath).Length; EntryCount = $verified.Entries; HashesVerified = $verified.HashesVerified; RuntimeFiles = $runtime.FileCount }
}

if (!$LocalPreview) { throw 'Publishing requires explicit -LocalPreview; no production package is authorized or qualified.' }
if ($ProductionReady) { throw 'This publisher cannot claim production readiness, signing, or clean-machine qualification.' }
$repositoryRoot = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
. (Join-Path $PSScriptRoot 'FoundationPublishRetention.ps1')
if($IsWindows){Assert-FoundationPublishCapacity $repositoryRoot}
$buildInputsSha256 = Get-FoundationPublishSourceStamp $repositoryRoot
$selection = Resolve-FoundationPackageDestination $repositoryRoot $OutputDirectory
$destination = $selection.Destination
$publishContext=$null
if($IsWindows){$publishContext=New-FoundationPublishContext $repositoryRoot $selection}
else {
    $null = New-Item -ItemType Directory -Path $destination
    [ordered]@{ schema = 'platform-studio.foundation-package-owner.v1'; runId = $selection.RunId; destination = $destination; localPreview = $true } |
        ConvertTo-Json | Set-Content -LiteralPath (Join-Path $destination '.package-owner.json') -Encoding utf8NoBOM
}
try {
$stage = Join-Path $destination '.publish-stage'
$workerStage = Join-Path $destination '.worker-publish-stage'
$ownerStage = Join-Path $destination '.owner-publish-stage'
$build = Join-Path $destination '.build'
$package = Join-Path $destination 'package'
$runtime = Join-Path $package 'runtime'
$project = Join-Path $repositoryRoot 'src/SelfService.Foundation.Host/SelfService.Foundation.Host.csproj'
$restoreConfig = Join-Path $destination '.restore/NuGet.Config'
$null = New-Item -ItemType Directory -Path (Split-Path $restoreConfig -Parent) -Force
'<configuration><packageSources><clear/><add key="nuget.org" value="https://api.nuget.org/v3/index.json"/></packageSources></configuration>' |
    Set-Content -LiteralPath $restoreConfig -Encoding utf8NoBOM
$priorCliHome = $env:DOTNET_CLI_HOME
$priorHttpCache = $env:NUGET_HTTP_CACHE_PATH
$priorCliFlags = @{}
foreach ($name in @('DOTNET_CLI_TELEMETRY_OPTOUT','DOTNET_SKIP_FIRST_TIME_EXPERIENCE','DOTNET_GENERATE_ASPNET_CERTIFICATE','DOTNET_ADD_GLOBAL_TOOLS_TO_PATH')) {
    $priorCliFlags[$name] = [Environment]::GetEnvironmentVariable($name)
}
try {
    # Ignore user feed configuration and use an isolated CLI/HTTP cache. No sign-in or credential cache is copied.
    $env:DOTNET_CLI_HOME = Join-Path $destination '.cli'
    $env:NUGET_HTTP_CACHE_PATH = Join-Path $destination '.restore/http-cache'
    $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $env:DOTNET_SKIP_FIRST_TIME_EXPERIENCE = '1'
    $env:DOTNET_GENERATE_ASPNET_CERTIFICATE = 'false'
    $env:DOTNET_ADD_GLOBAL_TOOLS_TO_PATH = 'false'
    if($publishContext){Invoke-FoundationContainedPublish $publishContext host $DotNetPath;Invoke-FoundationContainedPublish $publishContext worker $DotNetPath;Invoke-FoundationContainedPublish $publishContext owner $DotNetPath}
    else {
        & $DotNetPath publish $project -c Release --no-self-contained --artifacts-path $build --output $stage -m:1 -nodeReuse:false -p:RestoreLockedMode=true "-p:RestoreConfigFile=$restoreConfig" -p:UseSharedCompilation=false -p:UseAppHost=false -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:StaticWebAssetsEnabled=false -p:EnableDefaultContentItems=false -p:IsTransformWebConfigDisabled=true
        if ($LASTEXITCODE -ne 0) { throw 'Local preview publish or locked dependency audit failed; owned diagnostic output was preserved.' }
        & $DotNetPath publish (Join-Path $repositoryRoot 'src/SelfService.Foundation.Worker/SelfService.Foundation.Worker.csproj') -c Release --no-self-contained --artifacts-path $build --output $workerStage -m:1 -nodeReuse:false -p:RestoreLockedMode=true "-p:RestoreConfigFile=$restoreConfig" -p:UseSharedCompilation=false -p:UseAppHost=false -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:StaticWebAssetsEnabled=false -p:EnableDefaultContentItems=false -p:IsTransformWebConfigDisabled=true
        if ($LASTEXITCODE -ne 0) { throw 'Independent worker publish or locked dependency audit failed; owned diagnostic output was preserved.' }
        & $DotNetPath publish (Join-Path $repositoryRoot 'src/SelfService.Foundation.Owner/SelfService.Foundation.Owner.csproj') -c Release --no-self-contained --artifacts-path $build --output $ownerStage -m:1 -nodeReuse:false -p:RestoreLockedMode=true "-p:RestoreConfigFile=$restoreConfig" -p:UseSharedCompilation=false -p:UseAppHost=false -p:PublishSingleFile=false -p:PublishTrimmed=false -p:DebugType=None -p:DebugSymbols=false -p:StaticWebAssetsEnabled=false -p:EnableDefaultContentItems=false -p:IsTransformWebConfigDisabled=true
        if ($LASTEXITCODE -ne 0) { throw 'Trusted owner publish or locked dependency audit failed; owned diagnostic output was preserved.' }
    }
} finally {
    $env:DOTNET_CLI_HOME = $priorCliHome
    $env:NUGET_HTTP_CACHE_PATH = $priorHttpCache
    foreach ($name in $priorCliFlags.Keys) { [Environment]::SetEnvironmentVariable($name, $priorCliFlags[$name]) }
}
if ((Get-FoundationPublishSourceStamp $repositoryRoot) -cne $buildInputsSha256) { throw 'Source changed during publish; package creation refused.' }
$null = Merge-FoundationRuntimeClosures $stage $workerStage
$null = Merge-FoundationRuntimeClosures $stage $ownerStage
$null = New-Item -ItemType Directory -Path $runtime -Force
foreach ($file in @(Get-FoundationPackageFiles $stage)) {
    $target = Join-Path $runtime $file.Path
    $null = New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force
    Copy-Item -LiteralPath $file.FullName -Destination $target
}
$dependencyRoot = Join-Path $repositoryRoot ('artifacts/foundation-dependency-evidence/package-' + $selection.RunId)
$nugetCache = if ($env:NUGET_PACKAGES) { $env:NUGET_PACKAGES } else { Join-Path ([Environment]::GetFolderPath('UserProfile')) '.nuget/packages' }
$sdkVersion = (Get-Content -LiteralPath (Join-Path $repositoryRoot 'global.json') -Raw | ConvertFrom-Json).sdk.version
$dotnetExecutable = (Get-Command $DotNetPath -CommandType Application -ErrorAction Stop).Source
$dependencyParameters = @{
    RuntimeDirectory=$runtime;NuGetPackagesDirectory=$nugetCache;OutputDirectory=$dependencyRoot;RepositoryRoot=$repositoryRoot
    CreatedUtc=[DateTimeOffset]::UtcNow.ToString('yyyy-MM-ddTHH:mm:ssZ')
    NuGetSdkDirectory=(Join-Path (Split-Path $dotnetExecutable -Parent) ('sdk/' + $sdkVersion))
}
$dependency = & {
    param($generatorFile,$evidenceParameters)
    . $generatorFile
    New-FoundationDependencyEvidence @evidenceParameters
} (Join-Path $repositoryRoot 'scripts/New-FoundationDependencyEvidence.ps1') $dependencyParameters
if (!$dependency.packageEvidenceComplete) { throw 'Runtime dependency evidence is technically incomplete; package creation refused.' }
$dependencyDestination = Join-Path $package 'dependency-evidence'
$null = New-Item -ItemType Directory -Path $dependencyDestination
foreach ($relative in @(Get-FoundationDependencyFiles)) {
    Copy-Item -LiteralPath (Join-Path $dependencyRoot ([IO.Path]::GetFileName($relative))) -Destination (Join-Path $package $relative)
}
foreach ($relative in @(Get-FoundationPublicFiles)) {
    $source = Join-Path $repositoryRoot $relative
    Assert-FoundationUnlinkedPath $source
    $target = Join-Path $package $relative
    $null = New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force
    Copy-Item -LiteralPath $source -Destination $target
}
@'
# Platform Studio Foundation — local preview

This framework-dependent package requires a supported .NET 10 ASP.NET Core runtime. Start the API with
`dotnet runtime/SelfService.Foundation.Host.dll` after pointing PLATFORM_FOUNDATION_CONFIG at a protected
cell configuration containing reviewed public identity metadata. The bundled example intentionally has
unusable key placeholders. Never put a private key, access token, PAT, browser cache or customer database
in this package. External pipeline execution remains disabled by this host.

The separate worker is included with its complete dependency closure. It accepts an explicit protected
configuration using `dotnet runtime/SelfService.Foundation.Worker.dll --config <path>`; its default Disabled
mode never creates/migrates a database or claims a job. MockLoopback requires explicit double opt-in and
an already initialized current-schema cell on the same trusted local machine. Full worker/configuration guides
and launch scripts are in the matching checkout. This package grants no provider credentials or rights.

Trusted owner operations use `dotnet runtime/SelfService.Foundation.Owner.dll --request <public-json> --output <fresh-result>`.
Read docs/foundation-owner-cli.md before initialization, signed restore or exact activation. Existing databases
are never initialized or migrated in place by this command. Entra identity proposals are public setup drafts,
not Graph calls or grants; the bundled Entra input contains synthetic placeholders. Protect operator result
files separately; do not place them, databases or keys inside this runtime package.

Source-backed catalog, AVM/Bicep composition, instructions and workflow features require the full reviewed
matching repository checkout and its pinned prerequisites; this binary ZIP does not contain those sources.
Set the explicit RepositoryRoot configuration only to that reviewed checkout. The included guide contains
links into that full documentation set. This preview does not establish an offline full-product demo.

Hashes cover the actual final ZIP contents, including inventory.json; MANIFEST.sha256 excludes itself.
This is an unsigned local preview, not a licensed production release, clean-machine install acceptance,
SQL Server qualification, multi-host service, ARM64/x64 certification or customer credential onboarding.
No Azure deployment or real pipeline submission is performed by publishing this package. Protect the
runtime configuration, local cell database and backups separately using appropriate OS access controls.

dependency-evidence/ inventories the actual runtime bytes, NuGet archive/content hashes, declared licenses,
supplied notices and separately classified source bundles. Read dependency-evidence.json issues before any
distribution. Package evidence completeness does not grant commercial rights; missing first-party declarations
or license texts remain explicit blockers. This local preview does not resolve them or provide legal clearance.
'@ | Set-Content -LiteralPath (Join-Path $package 'README.md') -Encoding utf8NoBOM
if ((Get-FoundationPublishSourceStamp $repositoryRoot) -cne $buildInputsSha256) { throw 'Source changed during package evidence generation; package creation refused.' }
$archive = New-FoundationPackageArchive $package (Join-Path $destination 'PlatformStudio.Foundation.LocalPreview.zip')
$head = & git -C $repositoryRoot rev-parse HEAD
if ($LASTEXITCODE -ne 0) { throw 'Source revision could not be recorded.' }
$receipt = [ordered]@{
    schema = 'platform-studio.foundation-package-receipt.v1'; observedUtc = [DateTimeOffset]::UtcNow.ToString('O')
    localPreview = $true; productionReady = $false; signed = $false; cleanMachineQualified = $false
    sourceFreezeVerified = $false; sourceRevision = $head; frameworkDependent = $true; useAppHost = $false
    buildInputsStableDuringPublish = $true; buildInputsSha256 = $buildInputsSha256
    dependencyEvidenceStatus = $dependency.technicalStatus; dependencyPackageEvidenceComplete = $dependency.packageEvidenceComplete
    dependencyEvidenceIssues = $dependency.issues; commercialRightsCleared = $false
    architecturesQualified = @(); liveCredentialsIncluded = $false; customerDataIncluded = $false
    azureOperationsPerformed = $false; livePipelinesSubmitted = $false; archive = $archive
}
$receipt | ConvertTo-Json -Depth 8 | Set-Content -LiteralPath (Join-Path $destination 'receipt.json') -Encoding utf8NoBOM
if($publishContext){
    try {
        $cleanup=Remove-FoundationPublishedTemporaries $publishContext (Get-FileHash -LiteralPath (Join-Path $destination 'receipt.json') -Algorithm SHA256).Hash.ToLowerInvariant()
        if($cleanup.state -cne 'Completed' -or $cleanup.preservedCount -or $cleanup.removalUncertainCount){Write-Warning 'Some publisher temporary files were preserved or have uncertain removal; read temporary-cleanup.json. The package receipt is unchanged.'}
    } catch {Write-Warning 'Publisher temporary cleanup was refused; package receipt and diagnostic files are preserved.'}
}
Write-Output "Local preview package: $($archive.Archive)"
Write-Output "Final ZIP SHA-256: $($archive.Sha256); verified entries: $($archive.EntryCount). Production ready: false."
} finally {if($publishContext){Close-FoundationPublishContext $publishContext}}
