#requires -Version 7.4
. "$PSScriptRoot/common.ps1"
$localRoot = Join-Path (Get-ProjectRoot) '.local'

function Assert-LocalPath([string]$Path) {
    $base = [IO.Path]::GetFullPath($localRoot).TrimEnd('\','/')
    $full = [IO.Path]::GetFullPath($Path)
    if ($full -ne $base -and !$full.StartsWith($base + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Path is outside the project local runtime directory.' }
    $current = $full
    while ($current.Length -ge $base.Length) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'Local runtime paths must not be junctions or symbolic links.' }
        }
        $current = Split-Path -Parent $current
    }
    return $full
}

function Get-LocalTools([switch]$ConfigureEnvironment) {
    $path = Join-Path $localRoot 'tools.json'
    if (!(Test-Path $path)) { throw 'Run scripts/Setup-Local.ps1 first.' }
    $tools = Get-Content $path -Raw | ConvertFrom-Json
    if ($ConfigureEnvironment) {
        $env:PATH = (Split-Path $tools.dotnet -Parent) + ';' + (Split-Path $tools.node -Parent) + ';' + $env:PATH
        $env:DOTNET_ROOT = Split-Path $tools.dotnet -Parent
        $env:DOTNET_CLI_TELEMETRY_OPTOUT = '1'
        $env:FUNCTIONS_CORE_TOOLS_TELEMETRY_OPTOUT = '1'
    }
    return $tools
}

function Get-OwnedProcess($Record) {
    $process = Get-Process -Id $Record.id -ErrorAction SilentlyContinue
    if (!$process) { return $null }
    if ($process.StartTime.ToUniversalTime().Ticks.ToString() -ne $Record.startTicks -or $process.Path -ine $Record.path) {
        throw "PID $($Record.id) no longer belongs to this local run; refusing to control it."
    }
    return $process
}

function New-ProcessRecord($Process) {
    @{ id=$Process.Id; startTicks=$Process.StartTime.ToUniversalTime().Ticks.ToString(); path=$Process.Path }
}

function Get-LocalState {
    $path = Assert-LocalPath (Join-Path $localRoot 'run.json')
    if (!(Test-Path -LiteralPath $path)) { return $null }
    $state = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json
    if ($state.root -ine [IO.Path]::GetFullPath((Get-ProjectRoot))) { throw 'Local process receipt belongs to another checkout.' }
    return $state
}

function Assert-LocalPortsFree {
    # A denied listener query is not evidence that a port is free.
    $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -in @(10000,10001,10002,7071))
    if ($listeners.Count) { throw 'A local runtime port is occupied. No process was adopted or stopped.' }
}

function Get-LocalChildEnvironment([switch]$Telemetry) {
    # Explicitly remove inherited cloud credentials. Only the worker/operator receives collector authentication.
    $allowed = @('PATH','PATHEXT','SystemRoot','WINDIR','COMSPEC','TEMP','TMP','USERPROFILE','HOME','HOMEDRIVE','HOMEPATH',
        'ProgramFiles','ProgramFiles(x86)','ProgramW6432','LANG','LC_ALL','DOTNET_ROOT','DOTNET_BUNDLE_EXTRACT_BASE_DIR',
        'PROCESSOR_ARCHITECTURE','PROCESSOR_ARCHITEW6432','NUMBER_OF_PROCESSORS','DOTNET_CLI_HOME')
    $environment = @{}
    foreach ($key in [Environment]::GetEnvironmentVariables().Keys) {
        if ($key -notin $allowed) { $environment[[string]$key] = $null }
    }
    if ($Telemetry) {
        foreach ($key in @('OTEL_EXPORTER_OTLP_ENDPOINT','OTEL_EXPORTER_OTLP_PROTOCOL','OTEL_EXPORTER_OTLP_HEADERS')) {
            $value = [Environment]::GetEnvironmentVariable($key)
            if ($value) { $environment[$key] = $value }
        }
        $parent = [Environment]::GetEnvironmentVariable('TRACEPARENT')
        if ($parent -cmatch '^00-[0-9a-f]{32}-[0-9a-f]{16}-[0-9a-f]{2}$' -and
            $parent.Substring(3,32) -ne ('0'*32) -and $parent.Substring(36,16) -ne ('0'*16)) { $environment.TRACEPARENT = $parent }
    }
    $environment.DOTNET_CLI_TELEMETRY_OPTOUT = '1'
    $environment.FUNCTIONS_CORE_TOOLS_TELEMETRY_OPTOUT = '1'
    return $environment
}

function Get-LocalStatus {
    $result = [ordered]@{ State='Unknown'; Healthy=$false; OwnershipVerified=$false; Identity=$null; Url='http://localhost:7071'; Detail=''; Processes=@() }
    try {
        $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -in @(10000,10001,10002,7071))
        $state = Get-LocalState
        if (!$state) {
            $result.State = if ($listeners.Count) { 'UnownedListener' } else { 'Stopped' }
            $result.Detail = 'No local Functions/Azurite run is registered.'
            return [pscustomobject]$result
        }
        $live = @($state.processes | ForEach-Object { $process=Get-OwnedProcess $_; if ($process) { $process } })
        if ($live.Count) { $result.Identity = (@($live | Sort-Object Id | ForEach-Object { [string]$_.Id+':'+$_.StartTime.ToUniversalTime().Ticks }) -join ',') }
        $ownedIds = @($live | ForEach-Object { $_.Id })
        if (@($listeners | Where-Object OwningProcess -notin $ownedIds).Count) {
            $result.State='UnownedListener'; $result.Detail='A runtime port belongs to an unregistered process.'
            return [pscustomobject]$result
        }
        $result.Processes = @($live | ForEach-Object { @{ id=$_.Id; executable=[IO.Path]::GetFileName($_.Path) } })
        foreach ($record in $state.processes) { $process=Get-OwnedProcess $record; if ($process) { Assert-LocalOwnedTree $state $record $process } }
        $result.OwnershipVerified = $true
        if (!$live.Count) { $result.State='StaleReceipt'; $result.Detail='Recorded processes are absent; data and build outputs remain intact.'; return [pscustomobject]$result }
        $result.State='Unhealthy'; $result.Detail='The local run is partial or is still starting.'
        if ($live.Count -eq @($state.processes).Count -and $state.ready -and $listeners.Count -eq 4 -and
            !@($listeners | Where-Object LocalAddress -ne '127.0.0.1').Count) {
            try {
                $health = Invoke-RestMethod 'http://127.0.0.1:7071/admin/host/status' -TimeoutSec 2 -MaximumRedirection 0
                if ($health.state -eq 'Running') { $result.State='Ready'; $result.Healthy=$true; $result.Detail='Owned Functions and Azurite listeners are ready; no transfer smoke test was run.' }
            } catch { $result.Detail='Owned local processes and loopback listeners are present, but the host readiness probe failed.' }
        }
    } catch { $result.State='Unknown'; $result.OwnershipVerified=$false; $result.Detail='Local runtime ownership/readiness could not be verified. No process may be adopted.' }
    return [pscustomobject]$result
}

function Assert-LocalOwnedTree($State, $Record, $Process) {
    $rootInfo = Get-CimInstance Win32_Process -Filter "ProcessId=$($Process.Id)" -ErrorAction Stop
    if (!$rootInfo -or [string]::IsNullOrWhiteSpace($rootInfo.CommandLine)) { throw 'Local process command metadata is unavailable.' }
    $name = [IO.Path]::GetFileName($Process.Path)
    $tools = Get-Content -LiteralPath (Assert-LocalPath (Join-Path $localRoot 'tools.json')) -Raw | ConvertFrom-Json
    if ($name -ieq 'func.exe') {
        if ($Process.Path -ine $tools.func -or $rootInfo.CommandLine -notmatch '\bstart\s+--address\s+127\.0\.0\.1\s+--port\s+7071\s+--no-build\b') { throw 'The recorded Functions command differs from its local profile.' }
    } elseif ($name -ieq 'node.exe') {
        if ($Process.Path -ine $tools.node -or !$rootInfo.CommandLine.Contains([IO.Path]::GetFullPath($tools.azurite)) -or $rootInfo.CommandLine -notmatch '--blobHost\s+127\.0\.0\.1' -or $rootInfo.CommandLine -notmatch '--queueHost\s+127\.0\.0\.1' -or $rootInfo.CommandLine -notmatch '--tableHost\s+127\.0\.0\.1') { throw 'The recorded Azurite command differs from its local profile.' }
    } else { throw 'Unexpected local runtime executable.' }
    $pending = [Collections.Generic.Queue[int]]::new(); $pending.Enqueue([int]$Process.Id)
    while ($pending.Count) {
        $parent = $pending.Dequeue()
        foreach ($child in @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$parent" -ErrorAction Stop)) {
            if (!$child.ExecutablePath -or !$child.CreationDate -or $child.CreationDate.ToUniversalTime().Ticks -lt $Process.StartTime.ToUniversalTime().Ticks) { throw 'A local child process cannot be verified.' }
            $worker = $name -ieq 'func.exe' -and $child.ExecutablePath -ieq $tools.dotnet -and $child.CommandLine -and
                $child.CommandLine.Contains((Join-Path ([IO.Path]::GetFullPath($State.app)) 'BlobTransfer.dll'))
            $console = $child.ExecutablePath -ieq (Join-Path ([Environment]::SystemDirectory) 'conhost.exe')
            if (!$worker -and !$console) { throw 'An unregistered local child process is present. No process was stopped.' }
            $pending.Enqueue([int]$child.ProcessId)
        }
    }
}

function Stop-LocalOwnedRun($State) {
    # Validate the complete inventory before the first stop, then recheck each PID against reuse.
    $live = @($State.processes | ForEach-Object { $process=Get-OwnedProcess $_; if ($process) { @{ Record=$_; Process=$process } } })
    $listeners = @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -in @(10000,10001,10002,7071))
    $ownedIds = @($live | ForEach-Object { $_.Process.Id })
    if (@($listeners | Where-Object OwningProcess -notin $ownedIds).Count) { throw 'A local runtime port is owned by an unregistered process. No process was stopped.' }
    foreach ($entry in $live) { Assert-LocalOwnedTree $State $entry.Record $entry.Process }
    [array]::Reverse($live)
    foreach ($entry in $live) {
        $process = Get-OwnedProcess $entry.Record
        if (!$process) { continue }
        Assert-LocalOwnedTree $State $entry.Record $process
        $process.Kill($true)
        if (!$process.WaitForExit(10000)) { throw 'An owned local process did not stop. Its receipt was retained.' }
    }
    Assert-LocalPortsFree
}

function Get-LocalBuildFiles([string]$BuildRoot) {
    $build = Assert-LocalPath $BuildRoot
    $files = [Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($folder in @('blob-transfer','transfer-tool')) {
        $directory = Assert-LocalPath (Join-Path $build $folder)
        if (!(Test-Path -LiteralPath $directory -PathType Container)) { throw 'Prepared local runtime publish folders are missing.' }
        foreach ($entry in Get-ChildItem -LiteralPath $directory -Recurse -Force) {
            $null = Assert-LocalPath $entry.FullName
            if ($entry.PSIsContainer) { continue }
            $relative = [IO.Path]::GetRelativePath($build,$entry.FullName).Replace('\','/')
            $files.Add($relative,(Get-FileHash -LiteralPath $entry.FullName -Algorithm SHA256).Hash.ToLowerInvariant())
            if ($files.Count -gt 15000) { throw 'Prepared runtime file count exceeds the supported limit.' }
        }
    }
    foreach ($required in @('blob-transfer/BlobTransfer.dll','blob-transfer/functions.metadata','blob-transfer/host.json','transfer-tool/TransferTool.dll')) {
        if (!$files.ContainsKey($required)) { throw 'Prepared runtime is incomplete.' }
    }
    return ,$files
}

function Write-LocalBuildReceipt {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BuildRoot)
    $build = Assert-LocalPath $BuildRoot
    $files = Get-LocalBuildFiles $build
    Test-FunctionMetadata (Join-Path $build 'blob-transfer/functions.metadata')
    $record = [ordered]@{schema='platform.local-runtime-build/v1';root=[IO.Path]::GetFullPath((Get-ProjectRoot));
        files=@(foreach ($name in $files.Keys) { @{path=$name;sha256=$files[$name]} })}
    $path = Assert-LocalPath (Join-Path $build 'local-runtime-build.json')
    # Build bundles are immutable. A second build gets a new directory and cannot bless changed files in place.
    $stream = [IO.File]::Open($path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try { $bytes=[Text.Encoding]::UTF8.GetBytes(($record|ConvertTo-Json -Depth 5)); $stream.Write($bytes,0,$bytes.Length) }
    finally { $stream.Dispose() }
}

function Get-LocalPreparedBuild {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$BuildRoot)
    $build = Assert-LocalPath $BuildRoot
    $path = Assert-LocalPath (Join-Path $build 'local-runtime-build.json')
    if (!(Test-Path -LiteralPath $path -PathType Leaf) -or (Get-Item -LiteralPath $path).Length -gt 8MB) { throw 'Prepared runtime receipt is missing or excessive.' }
    $record = Get-Content -LiteralPath $path -Raw | ConvertFrom-Json -AsHashtable
    if ((($record.Keys | Sort-Object) -join ',') -cne 'files,root,schema' -or $record.schema -cne 'platform.local-runtime-build/v1' -or
        [IO.Path]::GetFullPath($record.root) -ine [IO.Path]::GetFullPath((Get-ProjectRoot)) -or $record.files.Count -gt 15000) { throw 'Prepared runtime receipt schema or repository differs.' }
    $actual = Get-LocalBuildFiles $build
    $expected = [Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach ($file in $record.files) {
        if ((($file.Keys | Sort-Object) -join ',') -cne 'path,sha256' -or $file.path -cnotmatch '^(blob-transfer|transfer-tool)/[^\\:]+$' -or
            $file.path.Contains('/../') -or $file.path.Contains('/./') -or $file.sha256 -cnotmatch '^[a-f0-9]{64}$' -or !$expected.TryAdd($file.path,$file.sha256)) { throw 'Prepared runtime file binding is invalid.' }
    }
    if ($actual.Count -ne $expected.Count) { throw 'Prepared runtime file closure changed.' }
    foreach ($name in $actual.Keys) {
        if (!$expected.ContainsKey($name) -or $expected[$name] -cne $actual[$name]) { throw 'Prepared runtime content changed after build.' }
    }
    Test-FunctionMetadata (Join-Path $build 'blob-transfer/functions.metadata')
    return @{Root=$build;App=(Join-Path $build 'blob-transfer');Operator=(Join-Path $build 'transfer-tool')}
}
