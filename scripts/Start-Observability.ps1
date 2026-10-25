#requires -Version 7.4
[CmdletBinding(SupportsShouldProcess)]
param([switch]$NoBrowser, [switch]$InstallMissing)
. (Join-Path $PSScriptRoot 'Observability.Common.ps1')
$paths = Get-ObservabilityPaths
$before = Get-ObservabilityStatus
if ($before.State -notin @('Running','Stopped','StaleReceipt')) { throw "Observability start blocked: $($before.State). $($before.Detail)" }
if (!$PSCmdlet.ShouldProcess('Owned local Aspire dashboard', 'Start telemetry before application producers')) { return }
if ($before.State -eq 'Running') {
    if (!$NoBrowser) { Start-Process -FilePath $before.shortcutPath -WindowStyle Hidden -ErrorAction Stop | Out-Null }
    $before | Add-Member -NotePropertyName StartedByInvocation -NotePropertyValue $false
    $before
    return
}
Protect-ObservabilityDirectory $paths.State
$lock = Open-ObservabilityLock
$private = $null
try {
    $current = Get-ObservabilityStatus
    if ($current.State -eq 'Running') { $current | Add-Member -NotePropertyName StartedByInvocation -NotePropertyValue $false; $current; return }
    if ($current.State -notin @('Stopped','StaleReceipt')) { throw "Observability changed before start: $($current.State). No process was launched." }
    if ($current.State -eq 'StaleReceipt') {
        Assert-ObservabilityPrivateFile $paths.Receipt
        Remove-Item -LiteralPath $paths.Receipt -ErrorAction Stop
    }
    $settings = Get-ObservabilitySettings
    $cli = Get-ObservabilityCliPath
    $null = Assert-ObservabilityPath $cli
    if (!(Test-Path -LiteralPath $cli -PathType Leaf)) {
        if (!$InstallMissing) { throw 'The pinned Aspire CLI is missing. Rerun the suite with -InstallMissing to install its official workspace-local package.' }
        $null = Get-Command npm -ErrorAction Stop
        $cache = Join-Path $paths.State 'npm-cache'
        Protect-ObservabilityDirectory $cache
        $installLog = Join-Path $paths.State 'install.log'
        $null = Assert-ObservabilityPath $installLog
        try {
            # Native installers can write arbitrary stdout/stderr; keep both inside the private diagnostics boundary.
            & npm --cache $cache install --prefix (Join-Path $paths.Root '.tools/aspire-cli') --no-audit --no-fund --ignore-scripts --save-exact '@microsoft/aspire-cli@13.6.0' *> $installLog
            $installExitCode = $LASTEXITCODE
        } catch { throw 'The pinned Aspire CLI install failed. Its private installation diagnostics were preserved.' }
        if ($installExitCode -ne 0 -or !(Test-Path -LiteralPath $cli -PathType Leaf)) { throw 'The pinned Aspire CLI could not be installed. Its private installation diagnostics were preserved.' }
    }
    if (Test-Path -LiteralPath $paths.PrivateConfig) {
        # Preserve credentials across stop/start so clients and browser sessions can reconnect.
        $private = Get-ObservabilityPrivateConfig
    } else {
        function New-ObservabilityKey { [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(32)).ToLowerInvariant() }
        $private = [pscustomobject]@{ schemaVersion = 1; cliVersion = '13.6.0'; cliPath = $cli; dashboardUrl = $settings.dashboardUrl;
            otlpGrpcUrl = $settings.otlpGrpcUrl; otlpHttpUrl = $settings.otlpHttpUrl;
            apiKey = (New-ObservabilityKey); otlpApiKey = (New-ObservabilityKey); browserToken = (New-ObservabilityKey) }
        Write-ObservabilityJson $paths.PrivateConfig $private
    }
    $dashboardConfig = Join-Path $paths.State 'dashboard-settings.json'
    Write-ObservabilityJson $dashboardConfig @{ Dashboard = @{ Frontend = @{ AuthMode = 'BrowserToken'; BrowserToken = $private.browserToken };
        Otlp = @{ AuthMode = 'ApiKey'; PrimaryApiKey = $private.otlpApiKey }; Api = @{ AuthMode = 'ApiKey'; PrimaryApiKey = $private.apiKey };
        TelemetryLimits = @{ MaxLogCount = [int]$settings.maxLogCount; MaxTraceCount = [int]$settings.maxTraceCount; MaxMetricsCount = [int]$settings.maxMetricsCount };
        Data = @{ Directory = (Join-Path $paths.State 'data'); PersistenceMode = 'None' } } }
    $shortcut = Join-Path $paths.State 'Dashboard.url'
    $null = Assert-ObservabilityPath $shortcut
    [IO.File]::WriteAllText($shortcut, "[InternetShortcut]"+[Environment]::NewLine+"URL=$($private.dashboardUrl)/login?t=$($private.browserToken)", [Text.UTF8Encoding]::new($false))
    $aspireHome = Join-Path $paths.State 'aspire-home'
    $privateTemp = Join-Path $paths.State 'temp'
    Protect-ObservabilityDirectory $aspireHome
    Protect-ObservabilityDirectory $privateTemp
    # Process-specific overrides leave the caller's ASPIRE_HOME and temporary directory unchanged.
    $arguments = @('dashboard', 'run', '--frontend-url', $private.dashboardUrl, '--otlp-grpc-url', $private.otlpGrpcUrl,
        '--otlp-http-url', $private.otlpHttpUrl, '--config-file-path', ('"' + $dashboardConfig + '"'), '--non-interactive', '--nologo', '--log-level', 'Warning')
    $process = Start-Process -FilePath $cli -ArgumentList $arguments -WorkingDirectory $paths.Root -WindowStyle Hidden -PassThru `
        -Environment @{ ASPIRE_HOME = $aspireHome; TEMP = $privateTemp; TMP = $privateTemp } `
        -RedirectStandardOutput (Join-Path $paths.State 'stdout.log') -RedirectStandardError (Join-Path $paths.State 'stderr.log') -ErrorAction Stop
    $receipt = [ordered]@{ schema = 'platform.observability-process/v1'; pid = $process.Id;
        startedUtc = $process.StartTime.ToUniversalTime().ToString('O'); executable = $cli; cliVersion = '13.6.0';
        dashboardUrl = $private.dashboardUrl; otlpGrpcUrl = $private.otlpGrpcUrl; otlpHttpUrl = $private.otlpHttpUrl }
    Write-ObservabilityJson $paths.Receipt $receipt
    $timer = [Diagnostics.Stopwatch]::StartNew()
    while ($timer.Elapsed.TotalSeconds -lt 60) {
        $process.Refresh()
        if ($process.HasExited) { throw 'Aspire exited before readiness. Its private diagnostics, keys and ownership receipt were retained.' }
        $ready = $false
        try { $response = Invoke-WebRequest ($private.dashboardUrl + '/login') -NoProxy -SkipHttpErrorCheck -TimeoutSec 1 -ErrorAction Stop; $ready = [int]$response.StatusCode -eq 200 }
        catch { } # A refused probe is expected during startup; ownership failures below are never suppressed.
        if ($ready) {
            $listener = Get-ObservabilityListener @{ Process = $process; Receipt = [pscustomobject]$receipt }
            $receipt.listenerPid = [int]$listener.ProcessId
            $receipt.listenerStartedUtc = (Get-Process -Id $listener.ProcessId -ErrorAction Stop).StartTime.ToUniversalTime().ToString('O')
            $receipt.listenerExecutable = $listener.ExecutablePath
            Write-ObservabilityJson $paths.Receipt $receipt
            $after = Get-ObservabilityStatus
            if ($after.State -ne 'Running') { throw 'Aspire failed ownership or readiness verification. Its private state was retained.' }
            if (!$NoBrowser) { Start-Process -FilePath $shortcut -WindowStyle Hidden -ErrorAction Stop | Out-Null }
            $after | Add-Member -NotePropertyName StartedByInvocation -NotePropertyValue $true
            $after
            return
        }
        Start-Sleep -Milliseconds 250
    }
    throw 'Aspire did not become ready within 60 seconds. Its private diagnostics, keys and ownership receipt were retained.'
} finally {
    $private = $null
    if ($null -ne $lock) { $lock.Dispose() }
}
