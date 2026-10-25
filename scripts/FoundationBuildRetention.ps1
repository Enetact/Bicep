#requires -Version 7.4
# Local compiler housekeeping, not revision history or an independent attestation.
. (Join-Path $PSScriptRoot 'FoundationTestExecutionEvidence.ps1')

function Initialize-FBROwnedNative {
    $path=Join-Path $PSScriptRoot 'FoundationOwnedBuildNative.cs';$null=Assert-FTEUnlinkedPath $path
    $sha=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()
    $loaded=[AppDomain]::CurrentDomain.GetData('PlatformStudio.FoundationOwnedBuild.NativeSha256')
    if('PlatformStudio.FoundationOwnedBuild.Native' -as [type]) { if($loaded -cne $sha){throw 'OwnedBuildNativeChanged'};return $sha }
    Add-Type -Path $path -ErrorAction Stop
    [AppDomain]::CurrentDomain.SetData('PlatformStudio.FoundationOwnedBuild.NativeSha256',$sha);return $sha
}

function Assert-FBROwnedPrivate([string]$Path) {
    $full=Assert-FTEUnlinkedPath $Path;$item=Get-Item -LiteralPath $full -Force -ErrorAction Stop
    $acl=Get-Acl -LiteralPath $full -ErrorAction Stop;$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    $raw=[Security.AccessControl.RawSecurityDescriptor]::new($acl.GetSecurityDescriptorBinaryForm(),0)
    if($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -cne $sid -or $null -eq $raw.DiscretionaryAcl -or ($raw.ControlFlags -band [Security.AccessControl.ControlFlags]::DiscretionaryAclPresent) -eq 0){throw 'OwnedBuildPrivateCustodyRequired'}
    foreach($rule in $acl.GetAccessRules($true,$true,[Security.Principal.SecurityIdentifier])) {
        if($rule.AccessControlType -eq [Security.AccessControl.AccessControlType]::Allow -and ($rule.PropagationFlags -band [Security.AccessControl.PropagationFlags]::InheritOnly) -eq 0 -and $rule.IdentityReference.Value -cne $sid){throw 'OwnedBuildPrivateCustodyRequired'}
    }
    return $full
}

function Read-FBROwnedJson([string]$Path,[string]$ExpectedSha256='') {
    $null=Assert-FBROwnedPrivate $Path;$stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try {
        if($stream.Length -gt 16777216){throw 'OwnedBuildMetadataLimit'}
        $sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()
        if($ExpectedSha256 -and $ExpectedSha256 -cne $sha){throw 'OwnedBuildMetadataChanged'}
        $value=Read-FTEJsonArtifact $Path $sha
        [pscustomobject]@{sha256=$sha;value=$value}
    } finally {$stream.Dispose()}
}

function Get-FBROwnedArguments([string]$Root,[string]$BuildRoot) {
    @('build',(Join-Path $Root 'src/SelfService.Foundation/SelfService.Foundation.csproj'),'-c','Release','--artifacts-path',$BuildRoot,
        '-m:1','-nodeReuse:false','-p:UseSharedCompilation=false','-p:RestoreLockedMode=true')
}

function Assert-FBROwnedBuildNamespace([string]$Root,[string]$Parent) {
    $null=Assert-FTEUnlinkedPath $Parent
    if($Parent -ine (Join-Path $Root 'artifacts/foundation-owned-builds')){throw 'OwnedBuildParentBindingInvalid'}
    $count=0
    for($ancestor=$Parent;$ancestor;$ancestor=Split-Path $ancestor -Parent) {
        if(Test-Path -LiteralPath $ancestor -PathType Container) {
            foreach($entry in Get-ChildItem -LiteralPath $ancestor -Force -ErrorAction Stop) {
                if(++$count -gt 100000){throw 'OwnedBuildAncestorLimit'}
                if(Test-FBRNamespaceSentinel $entry.Name){throw 'OwnedBuildProtectedAncestor'}
            }
        }
        if($ancestor -ieq $Root){break}
    }
}

function Open-FBROwnedCompletion([string]$Root,[string]$GenerationDirectory) {
    $directory=Assert-FBROwnedPrivate $GenerationDirectory;$parent=Join-Path $Root 'artifacts/foundation-owned-builds'
    if((Split-Path $directory -Parent) -ine $parent -or (Split-Path $directory -Leaf) -cnotmatch '\A[a-f0-9]{32}\z'){throw 'OwnedBuildGenerationBindingRequired'}
    $guardPath=Join-Path $directory '.compiler-build.lock';$null=Assert-FBROwnedPrivate $guardPath
    $guard=[IO.FileStream]::new($guardPath,[IO.FileMode]::Open,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
    try {
        if($guard.Length -ne 0){throw 'OwnedBuildGuardInvalid'}
        $null=Assert-FBROwnedPrivate $directory;$null=Assert-FBROwnedPrivate $guardPath
        foreach($entry in Get-ChildItem -LiteralPath $directory -Force) {
            if($entry.Name -cnotin @('.compiler-build.json','.compiler-build.lock','source.json','started.json','completed.json','stdout.txt','stderr.txt','build','cli-home') -or ($entry.Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'OwnedBuildIncompleteOrUnknown'}
        }
        $marker=Read-FBROwnedJson (Join-Path $directory '.compiler-build.json');$m=$marker.value
        Assert-FTEFields $m @('schema','repositoryRoot','generation','buildRoot','ownerSid','createdUnixMilliseconds','executable','executableSha256','nativeHelperSha256','sourceSha256','arguments')
        $build=Join-Path $directory 'build'
        if($m.schema -cne 'platform-studio.foundation-owned-build.v1' -or $m.repositoryRoot -cne $Root -or $m.generation -cne (Split-Path $directory -Leaf) -or
            $m.buildRoot -cne $build -or $m.ownerSid -cne [Security.Principal.WindowsIdentity]::GetCurrent().User.Value -or
            $m.createdUnixMilliseconds -isnot [long] -or $m.createdUnixMilliseconds -le 0 -or $m.executableSha256 -cnotmatch '\A[a-f0-9]{64}\z' -or
            $m.nativeHelperSha256 -cnotmatch '\A[a-f0-9]{64}\z' -or $m.sourceSha256 -cnotmatch '\A[a-f0-9]{64}\z' -or
            (ConvertTo-Json -InputObject $m.arguments -Compress) -cne (ConvertTo-Json -InputObject @(Get-FBROwnedArguments $Root $build) -Compress)){throw 'OwnedBuildMarkerInvalid'}
        $completed=Read-FBROwnedJson (Join-Path $directory 'completed.json');$c=$completed.value
        Assert-FTEFields $c @('schema','markerSha256','startedSha256','sourceSha256','sourceInputsUnchanged','observation','objFingerprint','stdout','stderr','independentAttestation','productionReady')
        if($c.schema -cne 'platform-studio.foundation-owned-build-completed.v1' -or $c.markerSha256 -cne $marker.sha256 -or $c.sourceSha256 -cne $m.sourceSha256 -or
            $c.sourceInputsUnchanged -isnot [bool] -or !$c.sourceInputsUnchanged -or $c.independentAttestation -isnot [bool] -or $c.independentAttestation -or
            $c.productionReady -isnot [bool] -or $c.productionReady -or $c.objFingerprint -cnotmatch '\A[a-f0-9]{64}\z'){throw 'OwnedBuildCompletionInvalid'}
        $source=(Read-FBROwnedJson (Join-Path $directory 'source.json') $c.sourceSha256).value
        if($source.schema -cne 'platform-studio.foundation-test-source.v1' -or $source.fileCount -lt 1 -or $source.fileCount -gt 30000 -or $source.fileCount -ne $source.files.Count -or
            @($source.files|Where-Object {$_.path -ceq 'scripts/FoundationOwnedBuildNative.cs' -and $_.rawSha256 -ceq $m.nativeHelperSha256}).Count -ne 1){throw 'OwnedBuildSourceBindingInvalid'}
        $started=(Read-FBROwnedJson (Join-Path $directory 'started.json') $c.startedSha256).value;$o=$c.observation
        Assert-FTEFields $started @('schema','processId','processStartTicks','executable','executableSha256','startedUnixMilliseconds')
        Assert-FTEFields $o @('processId','processStartTicks','executable','exitCode','totalProcesses','activeProcesses','finishedUnixMilliseconds')
        if($started.schema -cne 'platform-studio.foundation-owned-build-started.v1' -or $started.executable -cne $m.executable -or $started.executableSha256 -cne $m.executableSha256 -or
            $started.processId -le 0 -or $started.processStartTicks -le 0 -or $started.startedUnixMilliseconds -lt $m.createdUnixMilliseconds -or
            $o.processId -ne $started.processId -or $o.processStartTicks -ne $started.processStartTicks -or $o.executable -cne $m.executable -or $o.exitCode -ne 0 -or
            $o.totalProcesses -lt 1 -or $o.totalProcesses -gt 128 -or $o.activeProcesses -ne 0 -or $o.finishedUnixMilliseconds -lt $started.startedUnixMilliseconds -or
            $o.finishedUnixMilliseconds -gt [DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()){throw 'OwnedBuildProcessBindingInvalid'}
        foreach($name in @('stdout','stderr')) {
            $binding=$c[$name];Assert-FTEFields $binding @('path','bytes','sha256')
            if($binding.path -cne ($name+'.txt') -or $binding.bytes -lt 0 -or $binding.bytes -gt 4194304){throw 'OwnedBuildOutputBindingInvalid'}
            $path=Join-Path $directory $binding.path;$null=Assert-FBROwnedPrivate $path
            if((Get-Item -LiteralPath $path).Length -ne $binding.bytes -or (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() -cne $binding.sha256){throw 'OwnedBuildOutputBindingInvalid'}
        }
        $lease=[pscustomobject]@{guard=$guard;completedSha256=$completed.sha256;objFingerprint=$c.objFingerprint;generation=$directory;buildRoot=$build;finishedUnixMilliseconds=$o.finishedUnixMilliseconds}
        $guard=$null;return $lease
    } finally {if($guard){$guard.Dispose()}}
}

function Get-FBRProcesses {
    # Do not emit command lines: they can contain private arguments. Missing visibility refuses cleanup.
    if (!$IsWindows) { throw 'BuildRetentionWindowsRequired' }
    try {
        $records=@(Get-CimInstance -ClassName Win32_Process -ErrorAction Stop)
        if(!$records.Count) { throw 'BuildRetentionProcessInspectionUnavailable' }
        foreach($process in $records) { Assert-FBRProcessVisibility $process }
        $records | Select-Object ProcessId, ExecutablePath, CommandLine
    }
    catch { throw 'BuildRetentionProcessInspectionUnavailable' }
}

function Assert-FBRProcessVisibility($Process) {
    if($null -eq $Process -or $null -eq $Process.ProcessId) { throw 'BuildRetentionProcessInspectionUnavailable' }
    if($Process.ProcessId -in @(0,4)) { return } # Known kernel identities, never SDK builds.
    if(!$Process.ExecutablePath -or !$Process.CommandLine) { throw 'BuildRetentionProcessInspectionUnavailable' }
}

function Test-FBRNamespaceSentinel([string]$Name) {
    return $Name -iin @('.activation.guard','authority.json','state.json','binding.json','completion.json',
        '.installation-owner.json','.installation.guard','.package-owner.json','.credential-namespace.json',
        '.vault.lock','.keys.lock','.rollback.lock','.backup-checkpoint.guard','cell-binding.json',
        '.foundation-owner-initialization.json','.credential-recovery-stage.guard','.credential-recovery-stage.json',
        '.credential-recovery-checkpoint.guard','.credential-recovery-checkpoint.json','rollback-binding.json') -or
        $Name -match '(?i)\.(?:credential|dpapi)\.json\z'
}

function Test-FBRProtectedName([string]$Name) {
    return (Test-FBRNamespaceSentinel $Name) -or $Name -iin @('vault','rollback','keys','credentials','storage') -or
        $Name -match '(?i)(?:\.credential|\.dpapi|custody|activation|recovery|checkpoint|portal\.local\.json|namespace|(?:\.db|\.sqlite(?:3)?|\.pfx|\.p12|\.pem|\.key|\.trx|\.log)(?:-wal|-shm|-journal)?$)'
}

function Test-FBRProcessReference([object[]]$Processes, [string]$BuildDirectory, [string]$Root) {
    $needles=@($BuildDirectory.Replace('/','\').ToLowerInvariant())
    if($Root) { $needles+=([IO.Path]::GetRelativePath($Root,$BuildDirectory)).Replace('/','\').ToLowerInvariant() }
    foreach($process in $Processes) {
        Assert-FBRProcessVisibility $process
        foreach($value in @($process.ExecutablePath,$process.CommandLine)) {
            if (!$value) { continue }
            $text=([string]$value).Replace('/','\').ToLowerInvariant()
            foreach($needle in $needles) {
                $offset=0
                while(($offset=$text.IndexOf($needle,$offset,[StringComparison]::Ordinal)) -ge 0) {
                    $end=$offset+$needle.Length
                    if($end -eq $text.Length -or $text[$end] -in @('\','"',"'",' ',"`t",';',',')) { return $true }
                    $offset=$end
                }
            }
        }
    }
    return $false
}

function Test-FBRCompilerEntry([string]$Relative,[bool]$Directory) {
    if($Relative -ceq '.') { return $Directory }
    $parts=$Relative.Split('\');$project=$parts[0]
    if($project -cnotmatch '\A[A-Za-z0-9][A-Za-z0-9_.-]{0,127}\z') { return $false }
    if($parts.Count -eq 1) { return $Directory }
    if($parts.Count -eq 2 -and !$Directory) {
        return $parts[1] -iin @('project.assets.json','project.nuget.cache',($project+'.csproj.nuget.dgspec.json'),($project+'.csproj.nuget.g.props'),($project+'.csproj.nuget.g.targets'))
    }
    if($parts[1] -cnotmatch '\A(?:debug|release)(?:_[A-Za-z0-9.-]+)*\z') { return $false }
    if($parts.Count -eq 2) { return $Directory }
    if($Directory) { return $parts.Count -eq 3 -and $parts[2] -cin @('ref','refint','staticwebassets','compressed') }
    if($parts.Count -eq 4) {
        if($parts[2] -cin @('ref','refint')) { return $parts[3] -ieq ($project+'.dll') }
        if($parts[2] -ceq 'compressed') { return $parts[3] -cmatch '\A[a-z0-9]{10}-\{0\}-[a-z0-9]{10}-[a-z0-9]{10}\.(?:gz|br)\z' }
        return $false # Unknown static-asset/generated layouts are preserved until explicitly qualified.
    }
    if($parts.Count -ne 3) { return $false }
    $name=$parts[2]
    $suffixes=@('.dll','.pdb','.AssemblyInfo.cs','.AssemblyInfoInputs.cache','.assets.cache','.csproj.AssemblyReference.cache',
        '.csproj.CoreCompileInputs.cache','.csproj.FileListAbsolute.txt','.GeneratedMSBuildEditorConfig.editorconfig','.GlobalUsings.g.cs',
        '.genruntimeconfig.cache','.sourcelink.json','.MvcApplicationPartsAssemblyInfo.cache')
    if($name -iin @($suffixes | ForEach-Object { $project+$_ })) { return $true }
    return $name -ceq 'apphost.exe' -or $name -cmatch '\A\.NETCoreApp,Version=v[0-9]+\.[0-9]+\.AssemblyAttributes\.cs\z' -or
        $name -cmatch '\A[A-Za-z0-9_.-]{1,64}\.[A-Fa-f0-9]{8}\.Up2Date\z' -or
        $name -cin @('rbcswa.dswa.cache.json','rjimswa.dswa.cache.json','rjsmcshtml.dswa.cache.json','rjsmrazor.dswa.cache.json','rpswa.dswa.cache.json',
            'staticwebassets.build.endpoints.json','staticwebassets.build.json','staticwebassets.build.json.cache','staticwebassets.development.json','swae.build.ex.cache')
}

function Assert-FBRTarget([string]$Root,[string]$Target) {
    $canonical=Assert-FTEUnlinkedPath $Target
    $parent=Join-Path $Root 'artifacts'
    if(!$canonical.StartsWith($parent+'\',[StringComparison]::OrdinalIgnoreCase)) { throw 'BuildRetentionTargetOutsideArtifacts' }
    $relative=[IO.Path]::GetRelativePath($parent,$canonical)
    if($relative -cnotmatch '\A[a-z0-9][a-z0-9-]*(?:\\[a-f0-9]{32})?\\(?:build|\.build)\\obj\z') { throw 'BuildRetentionTargetShapeDenied' }
    $family=$relative.Split('\')[0]
    # Only ordinary checks. Packages, execution evidence and live/release generations are retained intact.
    if($family -match '(?i)(?:package|commercial|release|publish|preview|install|suite|runtime|cache|model|database|credential|custody|backup|checkpoint|stage|foundation-test-executions)') { throw 'BuildRetentionProtectedFamily' }
    $ancestorEntries=0
    for($ancestor=$canonical;$ancestor;$ancestor=Split-Path $ancestor -Parent) {
        foreach($entry in Get-ChildItem -LiteralPath $ancestor -Force -ErrorAction Stop) {
            if(++$ancestorEntries -gt 100000) { throw 'BuildRetentionAncestorLimit' }
            # A sibling check-family name is not evidence of custody in this ancestor.
            if(Test-FBRNamespaceSentinel $entry.Name) { throw 'BuildRetentionProtectedAncestor' }
        }
        if($ancestor -ieq $Root) { break }
    }
    return $canonical
}

function Get-FBRTree([string]$Root,[string]$Target) {
    $target=Assert-FBRTarget $Root $Target
    $owner=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    foreach($directory in @((Split-Path $target -Parent),$target)) {
        $sid=(Get-Acl -LiteralPath $directory -ErrorAction Stop).GetOwner([Security.Principal.SecurityIdentifier]).Value
        if($sid -cne $owner) { throw 'BuildRetentionForeignOwner' }
    }
    $stack=[Collections.Generic.Stack[string]]::new();$stack.Push($target)
    $records=[Collections.Generic.List[string]]::new();[long]$bytes=0;[long]$latest=0
    while($stack.Count -gt 0) {
        $path=$stack.Pop();$item=Get-Item -LiteralPath $path -Force -ErrorAction Stop
        if(($item.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0) { throw 'BuildRetentionLinkedTree' }
        if(Test-FBRProtectedName $item.Name) { throw 'BuildRetentionProtectedContent' }
        if((Get-Acl -LiteralPath $path -ErrorAction Stop).GetOwner([Security.Principal.SecurityIdentifier]).Value -cne $owner) { throw 'BuildRetentionForeignOwner' }
        if(!$item.PSIsContainer) { $bytes+=$item.Length }
        if($records.Count -ge 100000 -or $bytes -gt 4294967296) { throw 'BuildRetentionTreeLimit' }
        $latest=[Math]::Max($latest,$item.LastWriteTimeUtc.Ticks)
        $relative=[IO.Path]::GetRelativePath($target,$item.FullName)
        $length=if($item.PSIsContainer){0}else{$item.Length}
        if(!(Test-FBRCompilerEntry $relative $item.PSIsContainer)) { throw 'BuildRetentionUnknownCompilerContent' }
        $records.Add("$relative|$($item.Attributes)|$length|$($item.LastWriteTimeUtc.Ticks)")
        if($item.PSIsContainer) {
            foreach($child in Get-ChildItem -LiteralPath $path -Force -ErrorAction Stop) { $stack.Push($child.FullName) }
        }
    }
    $projects=@(Get-ChildItem -LiteralPath $target -Directory -Force -ErrorAction Stop)
    if(!$projects.Count) { throw 'BuildRetentionSdkLayoutRequired' }
    foreach($project in $projects) {
        foreach($name in @('project.assets.json','project.nuget.cache',($project.Name+'.csproj.nuget.dgspec.json'),($project.Name+'.csproj.nuget.g.props'),($project.Name+'.csproj.nuget.g.targets'))) {
            if(!(Test-Path -LiteralPath (Join-Path $project.FullName $name) -PathType Leaf)) { throw 'BuildRetentionSdkLayoutRequired' }
        }
    }
    $records.Sort([StringComparer]::Ordinal)
    [pscustomobject]@{path=$target;buildDirectory=(Split-Path $target -Parent);bytes=$bytes;entries=$records.Count;latestUtc=[DateTime]::new($latest,[DateTimeKind]::Utc);fingerprint=(Get-FTEHash ([Text.Encoding]::UTF8.GetBytes([string]::Join("`n",$records))))}
}

function Assert-FBRQualification([string]$Root,[string]$RunDirectory,[string]$CompletedSha256) {
    $root=Assert-FTEUnlinkedPath $Root;$run=Assert-FTEUnlinkedPath $RunDirectory
    $parent=Join-Path $root 'artifacts/foundation-test-executions'
    if((Split-Path $run -Parent) -ine $parent -or (Split-Path $run -Leaf) -cnotmatch '\A[a-f0-9]{32}\z') { throw 'BuildRetentionFreshFullRunRequired' }
    $null=Test-FTEExecutionReceipt $run $CompletedSha256
    $completed=Read-FTEJsonArtifact (Join-Path $run 'completed.json') $CompletedSha256
    $started=Read-FTEJsonArtifact (Join-Path $run 'started.json') $completed.started.sha256
    if($started.repositoryRoot -ine $root) { throw 'BuildRetentionRepositoryMismatch' }
    $now=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds();$age=$now-$completed.finishedUnixMilliseconds
    if($age -lt 0 -or $age -gt 86400000) { throw 'BuildRetentionFreshFullRunRequired' }
    $snapshot=Get-FTESourceSnapshot $root
    $hash=Get-FTEHash ([Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $snapshot -Depth 24)))
    if($hash -cne $completed.sourceAfter.sha256) { throw 'BuildRetentionSourceChanged' }
    return $run
}

function Invoke-FoundationBuildRetention {
    [CmdletBinding(SupportsShouldProcess,ConfirmImpact='Medium')]
    param(
        [Parameter(Mandatory)][string]$Root,
        [Parameter(Mandatory)][string]$RunDirectory,
        [Parameter(Mandatory)][ValidatePattern('\A[a-f0-9]{64}\z')][string]$CompletedSha256,
        [ValidateRange(2,100)][int]$KeepRecentBuilds=2,
        [ValidateRange(60,525600)][int]$MinimumAgeMinutes=60,
        [string[]]$KeepBuildDirectories=@(),
        [switch]$Apply
    )
    $root=Assert-FTEUnlinkedPath $Root
    $null=Assert-FBRQualification $root $RunDirectory $CompletedSha256
    $processes=@();$processInspection='Available'
    try {$processes=@(Get-FBRProcesses)} catch {
        if($_.Exception.Message -cne 'BuildRetentionProcessInspectionUnavailable'){throw}
        $processInspection='Unavailable'
    }
    $keep=@($KeepBuildDirectories | ForEach-Object { Assert-FTEUnlinkedPath $_ })
    $items=[Collections.Generic.List[object]]::new();$decisions=[Collections.Generic.List[object]]::new()
    $artifacts=Join-Path $root 'artifacts'
    foreach($family in Get-ChildItem -LiteralPath $artifacts -Directory -Force -ErrorAction Stop) {
        if(($family.Attributes -band [IO.FileAttributes]::ReparsePoint) -ne 0 -or $family.Name -cnotmatch '\A[a-z0-9][a-z0-9-]*\z' -or $family.Name -match '(?i)(?:package|commercial|release|publish|preview|install|suite|runtime|cache|model|database|credential|custody|backup|checkpoint|stage|foundation-test-executions)') { continue }
        $generations=@($family.FullName)+@(Get-ChildItem -LiteralPath $family.FullName -Directory -Force -ErrorAction Stop | Where-Object { $_.Name -cmatch '\A[a-f0-9]{32}\z' -and ($_.Attributes -band [IO.FileAttributes]::ReparsePoint) -eq 0 } | ForEach-Object { $_.FullName })
        foreach($generation in $generations) {
            foreach($build in @('build','.build')) {
                $target=Join-Path $generation ($build+'/obj')
                if(!(Test-Path -LiteralPath $target)) { continue }
                try {
                    $item=Get-FBRTree $root $target
                    $owned=$null
                    try {
                        if($family.Name -ceq 'foundation-owned-builds') {
                            $owned=Open-FBROwnedCompletion $root $generation
                            if($owned.objFingerprint -cne $item.fingerprint){throw 'OwnedBuildObjectTreeChanged'}
                            $item|Add-Member -NotePropertyName ownedGeneration -NotePropertyValue $generation;$item|Add-Member -NotePropertyName ownedCompletionSha256 -NotePropertyValue $owned.completedSha256
                        } else { $item|Add-Member -NotePropertyName ownedGeneration -NotePropertyValue $null;$item|Add-Member -NotePropertyName ownedCompletionSha256 -NotePropertyValue $null }
                        $items.Add($item)
                    } finally {if($owned){$owned.guard.Dispose()}}
                }
                catch { $decisions.Add([pscustomobject]@{path=$target;state='Preserved';reason='UnsafeOrProtectedTree';bytes=0}) }
            }
        }
    }
    # Never turn an opaque inventory into permission for unregistered legacy trees.
    if($processInspection -ceq 'Unavailable' -and !@($items|Where-Object ownedGeneration).Count){throw 'BuildRetentionProcessInspectionUnavailable'}
    $ordered=@($items | Sort-Object latestUtc -Descending)
    $recent=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach($ownedClass in @($false,$true)) {
        foreach($candidate in @($ordered|Where-Object {[bool]$_.ownedGeneration -eq $ownedClass}|Select-Object -First $KeepRecentBuilds)){$null=$recent.Add($candidate.path)}
    }
    for($index=0;$index -lt $ordered.Count;$index++) {
        $item=$ordered[$index];$reason=$null
        if($recent.Contains($item.path)) { $reason='RecentBuildRetained' }
        elseif(!$item.ownedGeneration -and $item.latestUtc -gt [DateTime]::UtcNow.AddMinutes(-$MinimumAgeMinutes)) { $reason='RecentActivity' }
        elseif($keep -icontains $item.buildDirectory -or $keep -icontains $item.path) { $reason='ExplicitlyRetained' }
        elseif(!$item.ownedGeneration -and $processInspection -ceq 'Unavailable') { $reason='ProcessInspectionUnavailable' }
        elseif($processInspection -ceq 'Available' -and (Test-FBRProcessReference $processes $item.buildDirectory $root)) { $reason='ProcessReference' }
        if($reason) { $decisions.Add([pscustomobject]@{path=$item.path;state='Preserved';reason=$reason;bytes=$item.bytes});continue }
        $state='Eligible';$reason='SupersededCompilerIntermediates'
        if($Apply -and $PSCmdlet.ShouldProcess($item.path,'Remove superseded compiler obj tree')) {
            $removalAttempted=$false;$owned=$null;$files=$null
            try {
                # Recheck successful current-source evidence, live processes and exact unlinked tree immediately before deletion.
                $null=Assert-FBRQualification $root $RunDirectory $CompletedSha256
                if($item.ownedGeneration) {
                    $owned=Open-FBROwnedCompletion $root $item.ownedGeneration
                    if($owned.completedSha256 -cne $item.ownedCompletionSha256 -or $owned.objFingerprint -cne $item.fingerprint){throw 'OwnedBuildCompletionChanged'}
                    $null=Initialize-FBROwnedNative
                    $files=[PlatformStudio.FoundationOwnedBuild.Native]::AcquireFiles($item.path)
                } elseif(Test-FBRProcessReference (Get-FBRProcesses) $item.buildDirectory $root) { throw 'BuildRetentionProcessReference' }
                $current=Get-FBRTree $root $item.path
                if($current.fingerprint -cne $item.fingerprint) { throw 'BuildRetentionTreeChanged' }
                $verified=Assert-FBRTarget $root $item.path
                $removalAttempted=$true
                if($files){$files.AssertCurrent();$files.Delete()}
                else {Remove-Item -LiteralPath $verified -Recurse -Force -ErrorAction Stop}
                if([IO.Path]::Exists($verified)) { throw 'BuildRetentionRemovalIncomplete' }
                $state='Removed'
            } catch {
                if($removalAttempted) { $state='RemovalUncertain';$reason='RemovalIncompleteOrChanged' }
                else { $state='Preserved';$reason='RecheckRefused' }
            } finally {if($files){$files.Dispose()};if($owned){$owned.guard.Dispose()}}
        }
        $decisions.Add([pscustomobject]@{path=$item.path;state=$state;reason=$reason;bytes=$item.bytes})
    }
    [pscustomobject]@{state='Completed';applied=[bool]$Apply;processInspection=$processInspection;ownedCompletedBuilds=@($items|Where-Object ownedGeneration).Count;removedCount=@($decisions | Where-Object state -CEQ 'Removed').Count;removedBytes=[long](($decisions | Where-Object state -CEQ 'Removed' | Measure-Object bytes -Sum).Sum);removalUncertainCount=@($decisions | Where-Object state -CEQ 'RemovalUncertain').Count;decisions=$decisions.ToArray();physicalSpaceReclaimedVerified=$false;continuousProcessFencing=$false;revisionHistory='GitHub'}
}
