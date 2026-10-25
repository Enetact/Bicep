#requires -Version 7.4

function Start-PlatformTrace {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [hashtable]$Attributes = @{})
    try {
        if (!$env:OTEL_EXPORTER_OTLP_ENDPOINT) { return $null }
        $endpoint = [uri]$env:OTEL_EXPORTER_OTLP_ENDPOINT
        if (!$endpoint.IsAbsoluteUri -or $endpoint.Scheme -notin @('http','https') -or
            ($endpoint.Scheme -eq 'http' -and !$endpoint.IsLoopback) -or $endpoint.UserInfo -or
            $endpoint.Query -or $endpoint.Fragment -or $endpoint.AbsolutePath -ne '/') { return $null }
        if ($Name -cnotmatch '^[a-z][a-z0-9_.-]{0,79}$') { return $null }
        $headers = @{}
        foreach ($header in $env:OTEL_EXPORTER_OTLP_HEADERS.Split(',')) {
            $parts = $header.Split('=', 2)
            if ($parts.Count -ne 2 -or $parts[0] -cnotin @('x-otlp-api-key','Authorization') -or
                !$parts[1] -or $parts[1].Length -gt 4096 -or $parts[1] -match '[\r\n]') { return $null }
            $headers[$parts[0]] = $parts[1]
        }
        if (!$headers.Count) { return $null }
        $parent = [string]$env:TRACEPARENT
        $traceId = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(16)).ToLowerInvariant()
        $parentId = $null
        $flags = '01'
        if ($parent -cmatch '^00-([0-9a-f]{32})-([0-9a-f]{16})-([0-9a-f]{2})$' -and
            $Matches[1] -ne ('0' * 32) -and $Matches[2] -ne ('0' * 16)) {
            $traceId = $Matches[1]; $parentId = $Matches[2]; $flags = $Matches[3]
        }
        $spanId = [Convert]::ToHexString([Security.Cryptography.RandomNumberGenerator]::GetBytes(8)).ToLowerInvariant()
        $safeAttributes = @()
        foreach ($key in @('platform.phase','platform.mode','platform.workload')) {
            if ($Attributes.ContainsKey($key) -and [string]$Attributes[$key] -cmatch '^[a-zA-Z0-9_.-]{1,64}$') {
                $safeAttributes += @{ key = $key; value = @{ stringValue = [string]$Attributes[$key] } }
            }
        }
        $trace = @{ Name = $Name; Endpoint = $endpoint; Headers = $headers; TraceId = $traceId;
            SpanId = $spanId; ParentId = $parentId; Flags = $flags; Parent = $env:TRACEPARENT;
            TraceState = $env:TRACESTATE; Baggage = $env:BAGGAGE; Started = [datetime]::UtcNow; Attributes = $safeAttributes;
            Outcome = 'success'; ErrorType = $null }
        # Subprocesses receive only W3C identifiers. Arguments, tokens, user identity and baggage stay out of spans.
        $env:TRACEPARENT = "00-$traceId-$spanId-$flags"
        $env:TRACESTATE = $null
        $env:BAGGAGE = $null
        return $trace
    } catch { return $null }
}

function Complete-PlatformTrace {
    [CmdletBinding()]
    param([AllowNull()][hashtable]$Trace)
    if (!$Trace) { return }
    try {
        if (([Convert]::ToInt32($Trace.Flags, 16) -band 1) -eq 0) { return }
        $attributes = @($Trace.Attributes) + @(@{ key = 'platform.outcome'; value = @{ stringValue = $Trace.Outcome } })
        if ($Trace.ErrorType -and $Trace.ErrorType -cmatch '^[a-zA-Z0-9_.+`]{1,256}$') {
            $attributes += @{ key = 'error.type'; value = @{ stringValue = $Trace.ErrorType } }
        }
        $span = @{ traceId = $Trace.TraceId; spanId = $Trace.SpanId; name = $Trace.Name; kind = 1;
            startTimeUnixNano = [string](($Trace.Started.Ticks - [datetime]::UnixEpoch.Ticks) * 100);
            endTimeUnixNano = [string](([datetime]::UtcNow.Ticks - [datetime]::UnixEpoch.Ticks) * 100);
            attributes = $attributes; status = @{ code = $(if ($Trace.Outcome -eq 'error') { 2 } elseif ($Trace.Outcome -eq 'canceled') { 0 } else { 1 }) } }
        if ($Trace.ParentId) { $span.parentSpanId = $Trace.ParentId }
        $payload = @{ resourceSpans = @(@{ resource = @{ attributes = @(@{ key = 'service.name'; value = @{ stringValue = 'platform-studio-pipelines' } }) };
            scopeSpans = @(@{ scope = @{ name = 'PlatformStudio.PowerShell'; version = '1.0.0' }; spans = @($span) }) }) } |
            ConvertTo-Json -Depth 12 -Compress
        $request = @{ Uri = [uri]::new($Trace.Endpoint, 'v1/traces'); Method = 'Post'; Headers = $Trace.Headers;
            ContentType = 'application/json'; Body = $payload; TimeoutSec = 2; MaximumRedirection = 0;
            SkipHttpErrorCheck = $true; ErrorAction = 'Stop' }
        if ($Trace.Endpoint.IsLoopback) { $request.NoProxy = $true }
        # Telemetry is best effort. A failed export never retries, changes approval, or replays a deployment.
        $null = Invoke-WebRequest @request
    } catch { }
    finally {
        $env:TRACEPARENT = $Trace.Parent
        $env:TRACESTATE = $Trace.TraceState
        $env:BAGGAGE = $Trace.Baggage
    }
}

function Invoke-PlatformTrace {
    [CmdletBinding()]
    param([Parameter(Mandatory)][string]$Name, [hashtable]$Attributes = @{}, [Parameter(Mandatory)][scriptblock]$Action)
    $trace = Start-PlatformTrace -Name $Name -Attributes $Attributes
    try { & $Action }
    catch {
        Set-PlatformTraceFailure -Trace $trace -Exception $_.Exception
        throw
    } finally { Complete-PlatformTrace $trace }
}

function Set-PlatformTraceFailure {
    param([AllowNull()][hashtable]$Trace, [Parameter(Mandatory)][Exception]$Exception)
    if (!$Trace) { return }
    $Trace.Outcome = if ($Exception -is [OperationCanceledException] -or $Exception -is [System.Management.Automation.PipelineStoppedException]) { 'canceled' } else { 'error' }
    $Trace.ErrorType = $Exception.GetType().FullName
}
