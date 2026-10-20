#requires -Version 7.4
$ErrorActionPreference='Stop'
. "$PSScriptRoot/../../scripts/PlatformTrace.ps1"
$saved=@{}
foreach($key in @('OTEL_EXPORTER_OTLP_ENDPOINT','OTEL_EXPORTER_OTLP_HEADERS','TRACEPARENT','TRACESTATE','BAGGAGE')) { $saved[$key]=[Environment]::GetEnvironmentVariable($key) }
$script:requests=[Collections.Generic.List[object]]::new()
$script:failExport=$false
function Invoke-WebRequest {
    param($Uri,$Method,$Headers,$ContentType,$Body,$TimeoutSec,$MaximumRedirection,[switch]$SkipHttpErrorCheck,[switch]$NoProxy)
    $script:requests.Add(@{Uri=$Uri;Body=$Body;Timeout=$TimeoutSec;Redirects=$MaximumRedirection;Headers=$Headers})
    if($script:failExport){throw 'synthetic export outage'}
    @{StatusCode=200}
}
function Check($Condition,[string]$Name){if(!$Condition){throw "FAIL: $Name"};Write-Host "PASS: $Name"}
try {
    $env:OTEL_EXPORTER_OTLP_ENDPOINT=$null
    $result=Invoke-PlatformTrace -Name platform.workflow.deploy -Action {42}
    Check ($result -eq 42 -and $requests.Count -eq 0) 'disabled tracing performs no HTTP and preserves output'
    $env:OTEL_EXPORTER_OTLP_ENDPOINT='http://127.0.0.1:4318'
    $env:OTEL_EXPORTER_OTLP_HEADERS='x-otlp-api-key=synthetic-private-key'
    $env:TRACEPARENT='00-11111111111111111111111111111111-2222222222222222-01'
    $env:TRACESTATE='vendor=private'
    $env:BAGGAGE='user=private'
    $result=Invoke-PlatformTrace -Name platform.workflow.fabric -Attributes @{'platform.mode'='Preview';'tool.arguments'='private-argument'} -Action {
        Check ($env:TRACEPARENT -cmatch '^00-11111111111111111111111111111111-[0-9a-f]{16}-01$') 'child receives linked W3C parent'
        Check (!$env:TRACESTATE -and !$env:BAGGAGE) 'child does not inherit trace state or baggage'
        7
    }
    $body=$requests[0].Body|ConvertFrom-Json -AsHashtable
    $span=$body.resourceSpans[0].scopeSpans[0].spans[0]
    Check ($result -eq 7 -and $span.parentSpanId -ceq '2222222222222222' -and $span.traceId -ceq ('1'*32)) 'export uses parent identifiers and preserves action output'
    Check ($span.status.code -eq 1 -and @($span.attributes|Where-Object key -eq platform.mode)[0].value.stringValue -ceq 'Preview') 'success and approved static metadata are exported'
    Check ($requests[0].Body -notmatch 'private|argument|baggage|vendor|credential') 'payload excludes credentials, content, and caller state'
    Check ($env:TRACEPARENT -ceq '00-11111111111111111111111111111111-2222222222222222-01' -and $env:TRACESTATE -ceq 'vendor=private' -and $env:BAGGAGE -ceq 'user=private') 'parent environment is restored'
    try { Invoke-PlatformTrace -Name platform.workflow.deploy -Action {throw 'private-exception-message'} } catch { $errorMessage=$_.Exception.Message }
    $span=($requests[1].Body|ConvertFrom-Json -AsHashtable).resourceSpans[0].scopeSpans[0].spans[0]
    Check ($errorMessage -ceq 'private-exception-message' -and $span.status.code -eq 2 -and $requests[1].Body -notmatch 'private-exception-message') 'failure preserves deployment exception and exports type only'
    $script:failExport=$true;$script:actions=0
    Invoke-PlatformTrace -Name platform.workflow.deploy -Action {$script:actions++}
    Check ($script:actions -eq 1 -and $requests.Count -eq 3 -and $requests[2].Timeout -eq 2 -and $requests[2].Redirects -eq 0) 'export outage never fails or replays deployment'
    $script:failExport=$false
    $env:TRACEPARENT='00-11111111111111111111111111111111-2222222222222222-00'
    Invoke-PlatformTrace -Name platform.workflow.deploy -Action {$script:actions++}
    Check ($requests.Count -eq 3) 'unsampled parent suppresses export'
    foreach($endpoint in @('http://remote.invalid:4318','http://127.0.0.1:4318?secret=value','http://user:secret@127.0.0.1:4318')){
        $env:OTEL_EXPORTER_OTLP_ENDPOINT=$endpoint
        Invoke-PlatformTrace -Name platform.workflow.deploy -Action {$script:actions++}
    }
    Check ($requests.Count -eq 3 -and $script:actions -eq 5) 'unsafe telemetry configuration leaves deployment operational without HTTP'
    $env:OTEL_EXPORTER_OTLP_ENDPOINT='http://127.0.0.1:4318'
    $env:TRACEPARENT=$null
    try { Invoke-PlatformTrace -Name platform.workflow.deploy -Action {throw [OperationCanceledException]::new('private cancellation')} } catch { }
    $span=($requests[3].Body|ConvertFrom-Json -AsHashtable).resourceSpans[0].scopeSpans[0].spans[0]
    Check ($span.status.code -eq 0 -and @($span.attributes|Where-Object key -eq platform.outcome)[0].value.stringValue -ceq 'canceled') 'cancellation has a distinct content-free outcome'
} finally {
    foreach($key in $saved.Keys){
        if($null -eq $saved[$key]){if(Test-Path -LiteralPath "Env:\$key"){Remove-Item -LiteralPath "Env:\$key" -ErrorAction Stop}}
        else{[Environment]::SetEnvironmentVariable($key,$saved[$key],'Process')}
    }
}
