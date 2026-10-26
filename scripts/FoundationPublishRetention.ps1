#requires -Version 7.4
# Prospective, current-invocation compiler housekeeping only. Never a repository/history purger.
. (Join-Path $PSScriptRoot 'FoundationBuildRetention.ps1')
if (!(Get-Variable FoundationPublishContexts -Scope Script -ErrorAction SilentlyContinue)) {
    $script:FoundationPublishContexts = [Collections.Generic.Dictionary[string,object]]::new([StringComparer]::Ordinal)
}

function Test-FoundationPublishSpaceBudget([long]$AvailableBytes) { $AvailableBytes -ge 2147483648 }
function Assert-FoundationPublishCapacity([string]$Root) {
    $rootPath=Assert-FTEUnlinkedPath $Root
    $available=[IO.DriveInfo]::new([IO.Path]::GetPathRoot($rootPath)).AvailableFreeSpace
    if (!(Test-FoundationPublishSpaceBudget $available)) { throw 'FoundationPublishInsufficientFreeSpace' }
}

function New-FoundationPublishContext([string]$Root,$Selection) {
    if (!$IsWindows) { throw 'FoundationPublishRetentionWindowsRequired' }
    $rootPath=Assert-FTEUnlinkedPath $Root
    $destination=Assert-FTEUnlinkedPath $Selection.Destination
    if ([IO.Path]::GetDirectoryName($destination) -ine (Join-Path $rootPath 'artifacts/foundation-packages') -or
        $Selection.RunId -cnotmatch '\A[a-f0-9]{32}\z' -or [IO.Path]::Exists($destination)) { throw 'FoundationPublishFreshOwnershipRequired' }
    $null=New-FTEPrivateDirectory $destination
    $guard=$null
    try {
        $guard=[IO.FileStream]::new((Join-Path $destination '.publish.lock'),[IO.FileMode]::CreateNew,[IO.FileAccess]::ReadWrite,[IO.FileShare]::None)
        $guard.Flush($true)
        $marker=Write-FTEJson (Join-Path $destination '.package-owner.json') ([ordered]@{
            schema='platform-studio.foundation-package-owner.v1';runId=$Selection.RunId;destination=$destination;localPreview=$true
        })
        $null=New-FTEPrivateDirectory (Join-Path $destination '.publish-logs')
        $context=[pscustomobject]@{id=[Guid]::NewGuid().ToString('N');root=$rootPath;destination=$destination;guard=$guard}
        $script:FoundationPublishContexts.Add($context.id,[pscustomobject]@{context=$context;root=$rootPath;destination=$destination;guard=$guard;markerSha256=$marker.sha256;stages=@{};started=[DateTimeOffset]::UtcNow.ToUnixTimeMilliseconds()})
        $guard=$null; return $context
    } finally { if($guard){$guard.Dispose()} }
}

function Assert-FoundationPublishContext($Context) {
    if ($null -eq $Context -or !$script:FoundationPublishContexts.ContainsKey([string]$Context.id)) { throw 'FoundationPublishLiveOwnershipRequired' }
    $state=$script:FoundationPublishContexts[$Context.id]
    if (![object]::ReferenceEquals($state.context,$Context) -or ![object]::ReferenceEquals($state.guard,$Context.guard) -or
        $Context.root -cne $state.root -or $Context.destination -cne $state.destination -or $Context.guard -isnot [IO.FileStream] -or
        $Context.guard.SafeFileHandle.IsClosed -or $Context.guard.Name -cne (Join-Path $Context.destination '.publish.lock') -or $Context.guard.Length -ne 0) {
        throw 'FoundationPublishLiveOwnershipRequired'
    }
    $null=Assert-FBROwnedPrivate $Context.destination
    $marker=Read-FBROwnedJson (Join-Path $Context.destination '.package-owner.json') $state.markerSha256
    if($marker.value.schema -cne 'platform-studio.foundation-package-owner.v1' -or $marker.value.destination -cne $Context.destination -or
        $marker.value.runId -cnotmatch '\A[a-f0-9]{32}\z' -or !$marker.value.localPreview) { throw 'FoundationPublishOwnershipChanged' }
    return $state
}

function Invoke-FoundationContainedPublish($Context,[ValidateSet('host','worker','owner')][string]$Stage,[string]$DotNetPath) {
    $state=Assert-FoundationPublishContext $Context
    if($state.stages.ContainsKey($Stage)){throw 'FoundationPublishStageAlreadyConsumed'}
    $nativeSha=Initialize-FBROwnedNative; $executable=Resolve-FTEExecutable $DotNetPath
    $projects=@{host='SelfService.Foundation.Host';worker='SelfService.Foundation.Worker';owner='SelfService.Foundation.Owner'}
    $names=@{host='.publish-stage';worker='.worker-publish-stage';owner='.owner-publish-stage'}
    $project=Join-Path $Context.root ('src/'+$projects[$Stage]+'/'+$projects[$Stage]+'.csproj')
    $build=Join-Path $Context.destination '.build'; $stagePath=Join-Path $Context.destination $names[$Stage]
    $arguments=@('publish',$project,'-c','Release','--no-self-contained','--artifacts-path',$build,'--output',$stagePath,
        '-m:1','-nodeReuse:false','-p:RestoreLockedMode=true',('-p:RestoreConfigFile='+(Join-Path $Context.destination '.restore/NuGet.Config')),
        '-p:UseSharedCompilation=false','-p:UseAppHost=false','-p:PublishSingleFile=false','-p:PublishTrimmed=false','-p:DebugType=None',
        '-p:DebugSymbols=false','-p:StaticWebAssetsEnabled=false','-p:EnableDefaultContentItems=false','-p:IsTransformWebConfigDisabled=true')
    $cli=Join-Path $Context.destination '.cli'
    foreach($relative in @('.cli','.cli/appdata','.cli/local-appdata','.cli/temp')) {
        $path=Join-Path $Context.destination $relative
        if(![IO.Path]::Exists($path)){$null=New-FTEPrivateDirectory $path}else{$null=Assert-FBROwnedPrivate $path}
    }
    $environment=@{}
    foreach($name in @('SystemRoot','WINDIR','ComSpec','ProgramFiles','ProgramFiles(x86)','ProgramW6432','ProgramData')) {
        $value=[Environment]::GetEnvironmentVariable($name);if($value){$environment[$name]=$value}
    }
    $environment['USERPROFILE']=$cli;$environment['APPDATA']=Join-Path $cli 'appdata';$environment['LOCALAPPDATA']=Join-Path $cli 'local-appdata'
    $environment['TEMP']=Join-Path $cli 'temp';$environment['TMP']=$environment['TEMP']
    $environment['DOTNET_ROOT']=Split-Path $executable -Parent;$environment['PATH']=(Split-Path $executable -Parent)+';'+(Join-Path $env:SystemRoot 'System32')
    $environment['DOTNET_CLI_HOME']=$cli;$environment['NUGET_HTTP_CACHE_PATH']=Join-Path $Context.destination '.restore/http-cache'
    $environment['MSBuildEnableWorkloadResolver']='false';$environment['DOTNET_CLI_TELEMETRY_OPTOUT']='1';$environment['DOTNET_SKIP_FIRST_TIME_EXPERIENCE']='1'
    $environment['DOTNET_GENERATE_ASPNET_CERTIFICATE']='false';$environment['DOTNET_ADD_GLOBAL_TOOLS_TO_PATH']='false'
    $environment['DOTNET_CLI_WORKLOAD_UPDATE_NOTIFY_DISABLE']='true';$environment['DOTNET_MULTILEVEL_LOOKUP']='0'
    if($env:NUGET_PACKAGES){$environment['NUGET_PACKAGES']=$env:NUGET_PACKAGES}
    $tool=[IO.FileStream]::new($executable,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    $session=$null;$observed=$null;$failure=$null
    try {
        $executableSha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($tool)).ToLowerInvariant()
        $session=[PlatformStudio.FoundationOwnedBuild.Native]::Start($executable,[string[]]$arguments,$Context.root,$environment)
        try {$observed=$session.Wait(900000,[Threading.CancellationToken]::None)}catch{$failure='FoundationPublishContainedStageFailed'}
        $stdout=Write-FTEFile (Join-Path $Context.destination ('.publish-logs/'+$Stage+'-stdout.txt')) $session.StandardOutput() 4194304
        $stderr=Write-FTEFile (Join-Path $Context.destination ('.publish-logs/'+$Stage+'-stderr.txt')) $session.StandardError() 4194304
        $null=Write-FTEJson (Join-Path $Context.destination ('.publish-logs/'+$Stage+'.json')) ([ordered]@{
            schema='platform-studio.foundation-contained-publish.v1';stage=$Stage;executable=$executable;executableSha256=$executableSha;nativeHelperSha256=$nativeSha;
            arguments=$arguments;observation=$observed;stdout=$stdout;stderr=$stderr;completed=($null -ne $observed);independentAttestation=$false
        })
        if($failure){throw $failure}
        $state.stages[$Stage]=$observed
        $null=Assert-FoundationPublishContext $Context
    } finally {try{if($session){$session.Dispose()}}finally{$tool.Dispose()}}
}

function Get-FoundationPublishTemporaryTree([string]$Path,[switch]$MetadataOnly) {
    $root=Assert-FBROwnedPrivate $Path; $stack=[Collections.Generic.Stack[string]]::new();$stack.Push($root)
    $rows=[Collections.Generic.List[object]]::new();[long]$bytes=0
    while($stack.Count){
        $path=$stack.Pop();$null=Assert-FBROwnedPrivate $path;$item=Get-Item -LiteralPath $path -Force
        if($rows.Count -ge 8192 -or [IO.Path]::GetRelativePath($root,$path).Split([IO.Path]::DirectorySeparatorChar).Count -gt 64){throw 'FoundationPublishTemporaryLimit'}
        if((Test-FBRNamespaceSentinel $item.Name) -or $item.Name -match '(?i)\.(?:db|sqlite(?:3)?|key|pem|pfx|p12|trx|log)\z'){throw 'FoundationPublishProtectedTemporaryContent'}
        $length=if($item.PSIsContainer){0}else{$item.Length};$bytes+=$length
        if($bytes -gt 4294967296){throw 'FoundationPublishTemporaryLimit'}
        $rows.Add([pscustomobject]@{path=$item.FullName;directory=$item.PSIsContainer;bytes=$length;metadata=($item.Attributes.ToString()+'|'+$length+'|'+$item.LastWriteTimeUtc.Ticks);
            sha256=$(if(!$item.PSIsContainer -and !$MetadataOnly){(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant()}else{''})})
        if($item.PSIsContainer){foreach($child in [IO.Directory]::EnumerateFileSystemEntries($path)){$stack.Push($child)}}
    }
    $ordered=@($rows | Sort-Object path -CaseSensitive)
    $fingerprint=Get-FTEHash ([Text.Encoding]::UTF8.GetBytes((($ordered | ForEach-Object {$_.path+'|'+$_.metadata}) -join "`n")))
    [pscustomobject]@{path=$root;bytes=$bytes;entries=$ordered;fingerprint=$fingerprint}
}

function Open-FoundationPublishRetainedPins($Context) {
    $excluded=@('.build/bin','.build/obj','.publish-stage','.worker-publish-stage','.owner-publish-stage','.publish.lock')
    $stack=[Collections.Generic.Stack[string]]::new();$stack.Push($Context.destination);$pins=[Collections.Generic.List[object]]::new()
    $entries=0;[long]$bytes=0
    try {
        while($stack.Count){
            $path=$stack.Pop();$null=Assert-FBROwnedPrivate $path
            if([IO.Path]::GetRelativePath($Context.destination,$path).Split([IO.Path]::DirectorySeparatorChar).Count -gt 64){throw 'FoundationPublishRetainedLimit'}
            foreach($child in [IO.Directory]::EnumerateFileSystemEntries($path)){
                if(++$entries -gt 20000){throw 'FoundationPublishRetainedLimit'}
                $item=Get-Item -LiteralPath $child -Force
                $relative=[IO.Path]::GetRelativePath($Context.destination,$item.FullName).Replace('\','/')
                if($relative -cin $excluded){continue};$null=Assert-FBROwnedPrivate $item.FullName
                if($item.PSIsContainer){$stack.Push($item.FullName);continue}
                $bytes+=$item.Length
                if($item.Length -gt 67108864 -or $bytes -gt 4294967296){throw 'FoundationPublishRetainedLimit'}
                $handle=[IO.FileStream]::new($item.FullName,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
                try{$sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($handle)).ToLowerInvariant();$pins.Add([pscustomobject]@{path=$item.FullName;handle=$handle;sha256=$sha;bytes=$handle.Length});$handle=$null}finally{if($handle){$handle.Dispose()}}
            }
        }
        return ,$pins
    } catch {foreach($pin in $pins){$pin.handle.Dispose()};throw}
}

function Assert-FoundationPublishRetainedPins($Context,$Pins) {
    $null=Assert-FoundationPublishContext $Context
    foreach($pin in $Pins){
        $null=Assert-FBROwnedPrivate $pin.path;$pin.handle.Position=0
        if($pin.handle.Length -ne $pin.bytes -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($pin.handle)).ToLowerInvariant() -cne $pin.sha256){throw 'FoundationPublishRetainedChanged'}
    }
}

function Assert-FoundationPublishExtractedPackage($Context,[string]$ArchivePath) {
    $package=Join-Path $Context.destination 'package';$manifest=Join-Path $package 'MANIFEST.sha256'
    $zip=[IO.Compression.ZipFile]::OpenRead($ArchivePath)
    try {
        $entry=$zip.GetEntry('MANIFEST.sha256');if(!$entry){throw 'FoundationPublishVerifiedArchiveRequired'}
        $stream=$entry.Open()
        try{$manifestSha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()}finally{$stream.Dispose()}
    } finally {$zip.Dispose()}
    if((Get-FileHash -LiteralPath $manifest -Algorithm SHA256).Hash.ToLowerInvariant() -cne $manifestSha){throw 'FoundationPublishExtractedPackageChanged'}
    $expected=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach($line in Get-Content -LiteralPath $manifest){
        if($line -cnotmatch '\A([a-f0-9]{64})  ([A-Za-z0-9_./+-]+)\z' -or !$expected.TryAdd($Matches[2],$Matches[1])){throw 'FoundationPublishExtractedPackageChanged'}
        Assert-FoundationArchiveRelativePath $Matches[2]
    }
    $files=@(Get-FoundationPackageFiles $package | Where-Object Path -CNE 'MANIFEST.sha256')
    if($files.Count -ne $expected.Count){throw 'FoundationPublishExtractedPackageChanged'}
    foreach($file in $files){if(!$expected.ContainsKey($file.Path) -or (Get-FileHash -LiteralPath $file.FullName -Algorithm SHA256).Hash.ToLowerInvariant() -cne $expected[$file.Path]){throw 'FoundationPublishExtractedPackageChanged'}}
}

function Remove-FoundationPublishedTemporaries($Context,[string]$ExpectedReceiptSha256) {
    $decisions=[Collections.Generic.List[object]]::new();$leases=[Collections.Generic.List[object]]::new();$pins=$null;$started=$false
    $retainedPinsVerified=$false;$validationFailure=$null;$refusalCode=$null;$ownedContext=$false
    $targets=@('.build/bin','.build/obj','.publish-stage','.worker-publish-stage','.owner-publish-stage')
    try {
        $state=Assert-FoundationPublishContext $Context
        $ownedContext=$true
        foreach($stage in @('host','worker','owner')) {
            if(!$state.stages.ContainsKey($stage) -or $state.stages[$stage] -isnot [PlatformStudio.FoundationOwnedBuild.Native+Observation] -or
                $state.stages[$stage].ExitCode -ne 0 -or $state.stages[$stage].ActiveProcesses -ne 0 -or $state.stages[$stage].FinishedUnixMilliseconds -lt $state.started){throw 'FoundationPublishQuiescenceRequired'}
        }
        $pins=Open-FoundationPublishRetainedPins $Context
        $receipt=Read-FBROwnedJson (Join-Path $Context.destination 'receipt.json') $ExpectedReceiptSha256
        if($receipt.value.schema -cne 'platform-studio.foundation-package-receipt.v1' -or !$receipt.value.localPreview -or $receipt.value.productionReady -or
            $receipt.value.archive.Archive -cne (Join-Path $Context.destination 'PlatformStudio.Foundation.LocalPreview.zip') -or
            (Get-FileHash -LiteralPath $receipt.value.archive.Archive -Algorithm SHA256).Hash.ToLowerInvariant() -cne $receipt.value.archive.Sha256){throw 'FoundationPublishVerifiedArchiveRequired'}
        $null=Test-FoundationArchive $receipt.value.archive.Archive
        $null=Test-FoundationPackageTree (Join-Path $Context.destination 'package')
        Assert-FoundationPublishExtractedPackage $Context $receipt.value.archive.Archive
        foreach($relative in $targets){
            $path=Join-Path $Context.destination $relative
            if(![IO.Path]::Exists($path)){$decisions.Add([pscustomobject]@{path=$relative;state='Absent';bytes=0;reason=$null});continue}
            $tree=Get-FoundationPublishTemporaryTree $path
            $lease=[PlatformStudio.FoundationOwnedBuild.Native]::AcquireFiles($path)
            $leases.Add([pscustomobject]@{relative=$relative;tree=$tree;lease=$lease})
            if((Get-FoundationPublishTemporaryTree $path -MetadataOnly).fingerprint -cne $tree.fingerprint){throw 'FoundationPublishTemporaryChanged'}
        }
        Assert-FoundationPublishRetainedPins $Context $pins
        foreach($entry in $leases){$entry.lease.AssertCurrent()}
        foreach($entry in $leases){
            $started=$true
            try {$entry.lease.Delete();$decisions.Add([pscustomobject]@{path=$entry.relative;state='Removed';bytes=$entry.tree.bytes;reason=$null})}
            catch {$decisions.Add([pscustomobject]@{path=$entry.relative;state='RemovalUncertain';bytes=0;reason='RemovalIncompleteOrChanged'});break}
        }
        Assert-FoundationPublishRetainedPins $Context $pins
        $retainedPinsVerified=$true
    } catch {
        $refusalCode=$_.Exception.GetBaseException().Message
        if($refusalCode -cnotmatch '\A(?:FoundationPublish|OwnedBuild|FileLease)[A-Za-z]+\z'){$refusalCode='ValidationUnavailable'}
        $validationFailure=if($started){'PostRemovalValidationRefused'}else{'ValidationRefused'}
        foreach($relative in $targets){if(!@($decisions | Where-Object path -CEQ $relative).Count){$decisions.Add([pscustomobject]@{path=$relative;state='Preserved';bytes=0;reason=$(if($started){'PostRemovalValidationRefused'}else{'ValidationRefused'})})}}
    } finally {
        foreach($entry in $leases){$entry.lease.Dispose()}
        if($pins){foreach($pin in $pins){$pin.handle.Dispose()}}
    }
    foreach($relative in $targets){if(!@($decisions | Where-Object path -CEQ $relative).Count){$decisions.Add([pscustomobject]@{path=$relative;state='Preserved';bytes=0;reason='EarlierRemovalUncertain'})}}
    [long]$removedBytes=0;foreach($decision in $decisions){if($decision.state -ceq 'Removed'){$removedBytes+=$decision.bytes}}
    $result=[ordered]@{schema='platform-studio.foundation-publish-temporary-cleanup.v1';state=$(if($validationFailure -or @($decisions|Where-Object state -CEQ 'RemovalUncertain').Count){'NeedsAttention'}else{'Completed'});decisions=$decisions.ToArray();
        removedCount=@($decisions | Where-Object state -CEQ 'Removed').Count;removedBytes=$removedBytes;
        removalUncertainCount=@($decisions | Where-Object state -CEQ 'RemovalUncertain').Count;preservedCount=@($decisions | Where-Object state -CEQ 'Preserved').Count;
        retainedPinsVerified=$retainedPinsVerified;validationFailure=$validationFailure;refusalCode=$refusalCode;physicalSpaceReclaimedVerified=$false;packageVerdictChanged=$false;independentAttestation=$false;revisionHistory='GitHub'}
    if($ownedContext){try{$null=Write-FTEJson (Join-Path $Context.destination 'temporary-cleanup.json') $result}catch{Write-Warning 'Publisher temporary cleanup report could not be written; package receipt is unchanged.'}}
    return [pscustomobject]$result
}

function Close-FoundationPublishContext($Context) {
    if($null -ne $Context -and $script:FoundationPublishContexts.ContainsKey([string]$Context.id)){
        $state=$script:FoundationPublishContexts[$Context.id]
        if([object]::ReferenceEquals($state.context,$Context)){$state.guard.Dispose();$script:FoundationPublishContexts.Remove($Context.id)|Out-Null}
    }
}
