#requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-ObservabilityPaths {
    $workspace = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
    $state = Join-Path $workspace '.local/observability'
    @{ Root = $workspace; State = $state; PrivateConfig = (Join-Path $state 'dashboard.json');
        Receipt = (Join-Path $state 'process.json'); Settings = (Join-Path $workspace 'config/observability.json');
        Lock = (Join-Path $state 'lifecycle.lock') }
}

function Assert-ObservabilityPath([string]$Path) {
    $paths = Get-ObservabilityPaths
    $full = [IO.Path]::GetFullPath($Path)
    if (!$full.StartsWith($paths.Root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) {
        throw 'Observability state must stay inside this workspace.'
    }
    for ($current = $full; $current.Length -gt $paths.Root.Length; $current = [IO.Path]::GetDirectoryName($current)) {
        if (Test-Path -LiteralPath $current) {
            if ((Get-Item -LiteralPath $current -Force -ErrorAction Stop).Attributes -band [IO.FileAttributes]::ReparsePoint) {
                throw 'Observability state cannot traverse a symbolic link or junction.'
            }
        }
    }
    $full
}

function Protect-ObservabilityDirectory([string]$Path) {
    $null = Assert-ObservabilityPath $Path
    $null = New-Item -ItemType Directory -Path $Path -Force
    $user = [Security.Principal.WindowsIdentity]::GetCurrent().User
    $system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $user.Value) {
        throw 'Observability directory belongs to another account.'
    }
    # Normalize even an inherited private DACL so later parent changes cannot broaden it.
    $acl.SetAccessRuleProtection($true, $false)
    foreach ($entry in $acl.GetAccessRules($true, $false, [Security.Principal.SecurityIdentifier])) { $null = $acl.RemoveAccessRuleAll($entry) }
    foreach ($identity in @($user, $system)) {
        $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl',
            'ContainerInherit,ObjectInherit', 'None', 'Allow'))
    }
    # Leave owner, group and audit sections untouched when restricting access.
    [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($Path), $acl)
}

function Assert-ObservabilityPrivateFile([string]$Path) {
    $null = Assert-ObservabilityPath $Path
    if (!(Test-Path -LiteralPath $Path -PathType Leaf)) { throw 'Observability private state is unavailable.' }
    $acl = Get-Acl -LiteralPath $Path -ErrorAction Stop
    $user = [Security.Principal.WindowsIdentity]::GetCurrent().User.Value
    if ($acl.GetOwner([Security.Principal.SecurityIdentifier]).Value -ne $user) {
        throw 'Observability state belongs to another account.'
    }
    foreach ($entry in $acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier])) {
        if ($entry.AccessControlType -eq 'Allow' -and $entry.IdentityReference.Value -notin @($user, 'S-1-5-18')) {
            throw 'Observability state has permissions for another account.'
        }
    }
}

function Get-ObservabilitySettings {
    $paths = Get-ObservabilityPaths
    $null = Assert-ObservabilityPath $paths.Settings
    if ((Get-Item -LiteralPath $paths.Settings -ErrorAction Stop).Length -gt 4096) { throw 'Observability configuration exceeds its size limit.' }
    $settings = Get-Content -LiteralPath $paths.Settings -Raw | ConvertFrom-Json
    if ($settings.schemaVersion -ne 1 -or $settings.cliVersion -ne '13.6.0') { throw 'Unsupported Aspire CLI pin.' }
    $ports = @()
    foreach ($name in @('dashboardUrl','otlpGrpcUrl','otlpHttpUrl')) {
        $uri = [uri]$settings.$name
        if (!$uri.IsAbsoluteUri -or $uri.Scheme -ne 'http' -or $uri.Host -ne '127.0.0.1' -or
            $uri.Port -lt 1024 -or $uri.Port -gt 65535 -or $uri.AbsolutePath -ne '/' -or $uri.Query -or $uri.Fragment -or $uri.UserInfo) {
            throw 'Observability endpoints must be literal loopback origins.'
        }
        $ports += $uri.Port
    }
    if (@($ports | Select-Object -Unique).Count -ne 3) { throw 'Observability requires three distinct ports.' }
    foreach ($name in @('maxLogCount','maxTraceCount','maxMetricsCount')) {
        if ([int]$settings.$name -lt 1 -or [int]$settings.$name -gt 100000) { throw 'Observability telemetry limits are invalid.' }
    }
    $settings
}

function Get-ObservabilityCliPath {
    $architecture = [Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
    if (!$IsWindows -or $architecture -notin @('arm64','x64')) { throw 'Observability supports Windows ARM64 and x64.' }
    Join-Path (Get-ObservabilityPaths).Root ".tools/aspire-cli/node_modules/@microsoft/aspire-cli-win-$architecture/bin/aspire.exe"
}

function Read-ObservabilityReceipt {
    $paths = Get-ObservabilityPaths
    Assert-ObservabilityPrivateFile $paths.Receipt
    if ((Get-Item -LiteralPath $paths.Receipt -ErrorAction Stop).Length -gt 16384) { throw 'Observability ownership receipt exceeds its size limit.' }
    $receipt = Get-Content -LiteralPath $paths.Receipt -Raw | ConvertFrom-Json
    $settings = Get-ObservabilitySettings
    if ($receipt.schema -ne 'platform.observability-process/v1' -or $receipt.cliVersion -ne $settings.cliVersion -or
        [int]$receipt.pid -le 0 -or [IO.Path]::GetFullPath($receipt.executable) -cne (Get-ObservabilityCliPath)) {
        throw 'Unrecognized observability ownership receipt.'
    }
    foreach ($name in @('dashboardUrl','otlpGrpcUrl','otlpHttpUrl')) {
        if ($receipt.$name -cne $settings.$name) { throw 'Observability source endpoints differ from the owned receipt.' }
    }
    $null = [datetimeoffset]$receipt.startedUtc
    $null = Assert-ObservabilityPath $receipt.executable
    $receipt
}

function Get-ObservabilityPorts($Settings) {
    @(([uri]$Settings.dashboardUrl).Port, ([uri]$Settings.otlpGrpcUrl).Port, ([uri]$Settings.otlpHttpUrl).Port)
}

function Get-ObservabilityListeners($Settings) {
    $ports = Get-ObservabilityPorts $Settings
    # Inspection failure remains Unknown; it is never evidence that a port is available.
    @(Get-NetTCPConnection -State Listen -ErrorAction Stop | Where-Object LocalPort -In $ports)
}

function Get-ObservabilityExactProcess([int]$Number, [string]$Executable, [datetimeoffset]$Started, [int]$Parent = 0, [switch]$CimTimestamp) {
    $info = Get-CimInstance Win32_Process -Filter "ProcessId=$Number" -ErrorAction Stop
    if (!$info) { return $null }
    $process = Get-Process -Id $Number -ErrorAction Stop
    if (!$process.Path -or !$info.ExecutablePath -or !$info.CommandLine -or !$info.CreationDate) {
        throw 'Observability process metadata is unavailable.'
    }
    # Win32_Process timestamps truncate to microseconds; Process.StartTime retains 100 ns precision.
    # Receipts bind the full Process timestamp. Only newly discovered children use a CIM timestamp.
    $actualTicks = $process.StartTime.ToUniversalTime().Ticks
    $cimTicks = ([datetimeoffset]$info.CreationDate).UtcTicks
    $expectedTicks = $Started.UtcTicks
    $startMatches = if ($CimTimestamp) { ($actualTicks - $actualTicks % 10) -eq ($expectedTicks - $expectedTicks % 10) } else { $actualTicks -eq $expectedTicks }
    if ($process.Path -cne $Executable -or $info.ExecutablePath -cne $Executable -or
        !$startMatches -or ($cimTicks - $cimTicks % 10) -ne ($expectedTicks - $expectedTicks % 10) -or
        ($Parent -gt 0 -and [int]$info.ParentProcessId -ne $Parent)) {
        throw 'Observability process ownership differs from its receipt.'
    }
    [pscustomobject]@{ Process = $process; Info = $info }
}

function Get-ObservabilityProcess {
    $receipt = Read-ObservabilityReceipt
    $owned = Get-ObservabilityExactProcess -Number $receipt.pid -Executable $receipt.executable -Started $receipt.startedUtc
    if (!$owned) { throw 'Observability CLI is stopped. No process was adopted or stopped.' }
    $config = [regex]::Escape((Join-Path (Get-ObservabilityPaths).State 'dashboard-settings.json'))
    if ($owned.Info.CommandLine -notmatch '\bdashboard\s+run\b' -or $owned.Info.CommandLine -notmatch $config) {
        throw 'The recorded Aspire CLI is not running this dashboard configuration.'
    }
    @{ Process = $owned.Process; Receipt = $receipt }
}

function Get-ObservabilityListener($Owned) {
    $receipt = $Owned.Receipt
    $listeners = @(Get-ObservabilityListeners $receipt)
    $owners = @($listeners.OwningProcess | Select-Object -Unique)
    if ($listeners.Count -ne 3 -or $owners.Count -ne 1 -or @($listeners | Where-Object LocalAddress -ne '127.0.0.1').Count) {
        throw 'The owned dashboard is not listening on its three private loopback endpoints.'
    }
    $listener = Get-CimInstance Win32_Process -Filter "ProcessId=$($owners[0])" -ErrorAction Stop
    $dashboardRoot = (Join-Path (Get-ObservabilityPaths).State 'aspire-home/versions') + [IO.Path]::DirectorySeparatorChar
    if (!$listener -or !$listener.ExecutablePath -or !$listener.CreationDate -or
        [int]$listener.ParentProcessId -ne [int]$receipt.pid -or
        !$listener.ExecutablePath.StartsWith($dashboardRoot, [StringComparison]::OrdinalIgnoreCase) -or
        [IO.Path]::GetFileName($listener.ExecutablePath) -cne 'Aspire.Dashboard.exe') {
        throw 'The dashboard listener does not belong to the recorded Aspire CLI process.'
    }
    $null = Assert-ObservabilityPath $listener.ExecutablePath
    if ($receipt.PSObject.Properties['listenerPid'] -and
        ([int]$receipt.listenerPid -ne [int]$listener.ProcessId -or $receipt.listenerExecutable -cne $listener.ExecutablePath -or
         (([datetimeoffset]$receipt.listenerStartedUtc).UtcTicks - ([datetimeoffset]$receipt.listenerStartedUtc).UtcTicks % 10) -ne
         (([datetimeoffset]$listener.CreationDate).UtcTicks - ([datetimeoffset]$listener.CreationDate).UtcTicks % 10))) {
        throw 'The dashboard listener differs from its owned receipt.'
    }
    $listener
}

function Get-ObservabilityPrivateConfig {
    $paths = Get-ObservabilityPaths
    Assert-ObservabilityPrivateFile $paths.PrivateConfig
    if ((Get-Item -LiteralPath $paths.PrivateConfig -ErrorAction Stop).Length -gt 16384) { throw 'Observability private configuration exceeds its size limit.' }
    try {
        $config = Get-Content -LiteralPath $paths.PrivateConfig -Raw | ConvertFrom-Json
        $settings = Get-ObservabilitySettings
        if ($config.schemaVersion -ne 1 -or $config.cliVersion -ne $settings.cliVersion -or $config.cliPath -cne (Get-ObservabilityCliPath)) { throw 'Invalid private configuration.' }
        foreach ($name in @('dashboardUrl','otlpGrpcUrl','otlpHttpUrl')) {
            if ($config.$name -cne $settings.$name) { throw 'Invalid private endpoint.' }
        }
        foreach ($key in @($config.apiKey, $config.otlpApiKey, $config.browserToken)) {
            if ($key -cnotmatch '^[a-f0-9]{64}$') { throw 'Invalid private authentication.' }
        }
        $config
    } catch {
        # Parser diagnostics can quote private content. Never echo an invalid private file or rotate its keys.
        throw 'Saved dashboard configuration is invalid or differs from source. Preserve it and review the local configuration.'
    }
}

function Get-ObservabilityInventory($Receipt) {
    $parent = Get-ObservabilityExactProcess -Number $Receipt.pid -Executable $Receipt.executable -Started $Receipt.startedUtc
    if ($parent) { $null = Get-ObservabilityProcess }
    $native = $null
    if ($Receipt.PSObject.Properties['listenerPid']) {
        $dashboardRoot = (Join-Path (Get-ObservabilityPaths).State 'aspire-home/versions') + [IO.Path]::DirectorySeparatorChar
        if (!$Receipt.listenerExecutable.StartsWith($dashboardRoot, [StringComparison]::OrdinalIgnoreCase) -or
            [IO.Path]::GetFileName($Receipt.listenerExecutable) -cne 'Aspire.Dashboard.exe') { throw 'Unexpected recorded dashboard executable.' }
        $null = Assert-ObservabilityPath $Receipt.listenerExecutable
        $native = Get-ObservabilityExactProcess -Number $Receipt.listenerPid -Executable $Receipt.listenerExecutable -Started $Receipt.listenerStartedUtc -Parent $Receipt.pid
    }
    $consoles = @()
    $known = @([int]$Receipt.pid)
    if ($native) { $known += [int]$Receipt.listenerPid }
    $pending = [Collections.Generic.Queue[int]]::new()
    $pending.Enqueue([int]$Receipt.pid)
    if ($native) { $pending.Enqueue([int]$native.Process.Id) }
    $inspected = [Collections.Generic.HashSet[int]]::new()
    while ($pending.Count) {
        $number = $pending.Dequeue()
        if (!$inspected.Add($number)) { continue }
        foreach ($child in @(Get-CimInstance Win32_Process -Filter "ParentProcessId=$number" -ErrorAction Stop)) {
            if ([int]$child.ProcessId -in $known) { continue }
            # Before readiness is receipted, recognize only a direct dashboard child at its private runtime path.
            $nativeRoot = (Join-Path (Get-ObservabilityPaths).State 'aspire-home/versions') + [IO.Path]::DirectorySeparatorChar
            if (!$Receipt.PSObject.Properties['listenerPid'] -and [int]$child.ParentProcessId -eq [int]$Receipt.pid -and
                $child.ExecutablePath -and $child.ExecutablePath.StartsWith($nativeRoot, [StringComparison]::OrdinalIgnoreCase) -and
                [IO.Path]::GetFileName($child.ExecutablePath) -ceq 'Aspire.Dashboard.exe' -and $child.CreationDate) {
                $null = Assert-ObservabilityPath $child.ExecutablePath
                $native = Get-ObservabilityExactProcess -Number $child.ProcessId -Executable $child.ExecutablePath -Started $child.CreationDate -Parent $Receipt.pid -CimTimestamp
                $known += [int]$child.ProcessId
                $pending.Enqueue([int]$child.ProcessId)
                continue
            }
            $console = Join-Path ([Environment]::SystemDirectory) 'conhost.exe'
            if (!$child.ExecutablePath -or $child.ExecutablePath -cne $console -or !$child.CreationDate -or
                ([datetimeoffset]$child.CreationDate).UtcTicks -lt ([datetimeoffset]$Receipt.startedUtc).UtcTicks) {
                throw 'An unverified observability child exists. No process was stopped.'
            }
            $consoles += Get-ObservabilityExactProcess -Number $child.ProcessId -Executable $console -Started $child.CreationDate -Parent $number -CimTimestamp
            $pending.Enqueue([int]$child.ProcessId)
        }
    }
    [pscustomobject]@{ Parent = $parent; Native = $native; Consoles = $consoles }
}

function Get-ObservabilityStatus {
    $paths = Get-ObservabilityPaths
    $result = [ordered]@{ State = 'Unknown'; status = 'Unknown'; Healthy = $false; OwnershipVerified = $false;
        ProcessId = $null; pid = $null; Identity = $null; cliVersion = '13.6.0'; dashboardUrl = $null; otlpHttpUrl = $null;
        shortcutPath = (Join-Path $paths.State 'Dashboard.url'); Detail = 'Observability state has not been verified.' }
    try {
        $settings = Get-ObservabilitySettings
        $result.dashboardUrl = $settings.dashboardUrl; $result.otlpHttpUrl = $settings.otlpHttpUrl
        $listeners = @(Get-ObservabilityListeners $settings)
        if (!(Test-Path -LiteralPath $paths.Receipt)) {
            $cli = Get-ObservabilityCliPath
            $nativeRoot = (Join-Path $paths.State 'aspire-home/versions') + [IO.Path]::DirectorySeparatorChar
            $unregistered = @(Get-CimInstance Win32_Process -Filter "Name='aspire.exe' OR Name='Aspire.Dashboard.exe'" -ErrorAction Stop | Where-Object {
                $_.ExecutablePath -and ($_.ExecutablePath -ceq $cli -or $_.ExecutablePath.StartsWith($nativeRoot,[StringComparison]::OrdinalIgnoreCase))
            })
            if ($listeners.Count -or $unregistered.Count) {
                $result.State = 'UnownedListener'; $result.Detail = 'A configured port or unregistered observability process is present. No process may be adopted.'
            } else { $result.State = 'Stopped'; $result.Detail = 'No observability process or configured listener is present.' }
        } else {
            $receipt = Read-ObservabilityReceipt
            $result.ProcessId = [int]$receipt.pid; $result.pid = [int]$receipt.pid
            # Bind orchestration to both recorded process generations; a recycled PID is insufficient.
            $result.Identity = "$([int]$receipt.pid):$(([datetimeoffset]$receipt.startedUtc).UtcTicks)"
            if ($receipt.PSObject.Properties['listenerPid']) {
                $result.Identity += "|$([int]$receipt.listenerPid):$(([datetimeoffset]$receipt.listenerStartedUtc).UtcTicks)"
            }
            $inventory = Get-ObservabilityInventory $receipt
            $allowed = @()
            if ($inventory.Native) { $allowed += [int]$inventory.Native.Process.Id }
            if (@($listeners | Where-Object { $_.OwningProcess -notin $allowed -or $_.LocalAddress -ne '127.0.0.1' }).Count) {
                $result.State = 'UnownedListener'; $result.Detail = 'A configured observability port belongs to another process.'
            } elseif (!$inventory.Parent -and !$inventory.Native -and !$inventory.Consoles.Count -and !$listeners.Count) {
                $result.State = 'StaleReceipt'; $result.Detail = 'Both recorded processes are absent and all configured ports are free. Private keys are retained.'
            } else {
                $result.OwnershipVerified = $true
                if ($inventory.Parent -and $inventory.Native -and $listeners.Count -eq 3) {
                    $null = Get-ObservabilityListener @{ Process = $inventory.Parent.Process; Receipt = $receipt }
                    $null = Get-ObservabilityPrivateConfig
                    $serving = $false
                    try {
                        $response = Invoke-WebRequest ($settings.dashboardUrl + '/login') -NoProxy -MaximumRedirection 0 -SkipHttpErrorCheck -TimeoutSec 2 -ErrorAction Stop
                        $serving = [int]$response.StatusCode -eq 200
                    } catch { } # A live process can own its ports while its frontend is unhealthy.
                    if ($serving) {
                        $result.State = 'Running'; $result.Healthy = $true; $result.Detail = 'Owned CLI, dashboard, private configuration, three loopback listeners and frontend readiness verified.'
                    } else { $result.State = 'Unhealthy'; $result.Detail = 'Owned dashboard listeners are present, but the frontend readiness probe failed.' }
                } else { $result.State = 'Unhealthy'; $result.Detail = 'Owned observability processes are present, but the dashboard is not ready.' }
            }
        }
    } catch {
        $result.State = if ($_.Exception.Message -match 'ownership differs|unverified observability child|differs from its owned receipt|not running this dashboard') { 'OwnershipMismatch' } else { 'Unknown' }
        $result.OwnershipVerified = $false; $result.Healthy = $false
        $result.Detail = 'Observability inspection failed. Run with the Windows account that launched the suite and review its private ownership state.'
    }
    $result.status = $result.State
    [pscustomobject]$result
}

function Write-ObservabilityJson([string]$Path, $Value) {
    $null = Assert-ObservabilityPath $Path
    $temporary = Join-Path (Split-Path $Path -Parent) ([guid]::NewGuid().ToString('N') + '.tmp')
    try {
        [IO.File]::WriteAllText($temporary, ($Value | ConvertTo-Json -Depth 8), [Text.UTF8Encoding]::new($false))
        Move-Item -LiteralPath $temporary -Destination $Path -Force -ErrorAction Stop
    } finally { if (Test-Path -LiteralPath $temporary) { Remove-Item -LiteralPath $temporary -ErrorAction Stop } }
}

function Open-ObservabilityLock {
    $path = (Get-ObservabilityPaths).Lock
    $null = Assert-ObservabilityPath $path
    try { [IO.File]::Open($path, [IO.FileMode]::OpenOrCreate, [IO.FileAccess]::ReadWrite, [IO.FileShare]::None) }
    catch { throw 'Another observability lifecycle operation is active, or its private lock is unavailable.' }
}
