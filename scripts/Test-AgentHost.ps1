#requires -Version 7.4
[CmdletBinding()]
param([int]$Port=5087,[string]$ReceiptPath='')
$ErrorActionPreference='Stop'
if($Port -lt 1024 -or $Port -gt 65535){throw 'Invalid local portal port.'}
$origin="http://localhost:$Port"
$bootstrap=Invoke-WebRequest "$origin/api/bootstrap" -SessionVariable portalSession
$data=$bootstrap.Content|ConvertFrom-Json
$socket=[Net.WebSockets.ClientWebSocket]::new()
$socket.Options.SetRequestHeader('Origin',$origin)
$socket.Options.Cookies=$portalSession.Cookies
$socket.Options.AddSubProtocol('platform-studio')
$socket.Options.AddSubProtocol($data.csrf)
$timeout=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(30))
$checks=[Collections.Generic.List[object]]::new()
function Check-AgentHost([bool]$Passed,[string]$Name){
    if(!$Passed){throw "AHP check failed: $Name"}
    $checks.Add([ordered]@{name=$Name;passed=$true})
}
function Test-AgentRpcError([object]$Response){
    $property=$Response.PSObject.Properties['error']
    return $null -ne $property -and $null -ne $property.Value
}
function Invoke-AgentRpc([int]$Id,[string]$Method,[hashtable]$Parameters){
    $json=@{jsonrpc='2.0';id=$Id;method=$Method;params=$Parameters}|ConvertTo-Json -Depth 8 -Compress
    $payload=[Text.Encoding]::UTF8.GetBytes($json)
    $socket.SendAsync([ArraySegment[byte]]::new($payload),[Net.WebSockets.WebSocketMessageType]::Text,$true,$timeout.Token).GetAwaiter().GetResult()|Out-Null
    $buffer=[byte[]]::new(32768);$stream=[IO.MemoryStream]::new()
    do{$r=$socket.ReceiveAsync([ArraySegment[byte]]::new($buffer),$timeout.Token).GetAwaiter().GetResult();$stream.Write($buffer,0,$r.Count)}while(!$r.EndOfMessage)
    $response=[Text.Encoding]::UTF8.GetString($stream.ToArray())|ConvertFrom-Json
    $stream.Dispose()
    if($response.id -ne $Id){throw 'AHP response correlation failed.'}
    return $response
}
try{
    $socket.ConnectAsync([uri]"ws://localhost:$Port/api/agent/ahp",$timeout.Token).GetAwaiter().GetResult()|Out-Null
    $r=Invoke-AgentRpc 1 initialize @{channel='ahp-root://';protocolVersions=@('0.9.0');clientId='portal-contract-test'}
    Check-AgentHost ($r.result.protocolVersion -ceq '0.9.0') 'protocol-version'
    Check-AgentHost ($r.result.snapshots[0].state.platformStudio.profile -ceq 'workflow-orchestration' -and $r.result.snapshots[0].state.platformStudio.actions -contains 'platform/mcpPlan') 'typed-provider-orchestration-advertisement'
    $model=$r.result.snapshots[0].state.agents[0].models[0]
    Check-AgentHost ($model.id -ceq 'gpt-6.1-sol' -and $model.name -ceq 'GPT-6.1 Sol') 'model-advertisement'
    $channel='ahp-session:/'+[guid]::NewGuid().ToString()
    $r=Invoke-AgentRpc 2 createSession @{channel=$channel;provider='codex'}
    Check-AgentHost (!(Test-AgentRpcError $r)) 'session-creation'
    $r=Invoke-AgentRpc 3 subscribe @{channel=$channel}
    $state=$r.result.snapshot.state.platformStudio
    $providerStatus=(Invoke-WebRequest "$origin/api/mcp/status" -WebSession $portalSession).Content|ConvertFrom-Json
    $providerIds=@($providerStatus.providers|ForEach-Object {'mcp:'+$_.id}|Sort-Object -CaseSensitive)
    $projectedIds=@($r.result.snapshot.state.customizations|ForEach-Object {$_.id}|Sort-Object -CaseSensitive)
    Check-AgentHost ($providerIds.Count -gt 0 -and @($r.result.snapshot.state.customizations | Where-Object type -ne mcpServer).Count -eq 0 -and
        @((Compare-Object -ReferenceObject $providerIds -DifferenceObject $projectedIds -CaseSensitive)).Count -eq 0) 'complete-provider-customization-projection'
    $expected=(Invoke-WebRequest "$origin/api/agent/status" -WebSession $portalSession).Content|ConvertFrom-Json
    Check-AgentHost ($state.workflows.Count -eq $expected.workflows.Count -and $state.workflows.Count -gt 0) 'workflow-readiness-projection'
    Check-AgentHost ($state.policy.model -ceq 'gpt-6.1-sol' -and $state.policy.effort -ceq 'ultra' -and $state.policy.serviceTier -ceq 'default') 'model-policy'
    $r=Invoke-AgentRpc 4 subscribe @{channel='ahp-session:/'+[guid]::NewGuid().ToString()}
    Check-AgentHost (Test-AgentRpcError $r) 'cross-session-channel-rejected'
    $r=Invoke-AgentRpc 5 dispatchAction @{channel=$channel;action=@{type='chat/turnStarted'}}
    Check-AgentHost (Test-AgentRpcError $r) 'arbitrary-model-dispatch-rejected'
    $socket.CloseAsync([Net.WebSockets.WebSocketCloseStatus]::NormalClosure,'Done',$timeout.Token).GetAwaiter().GetResult()|Out-Null
    if($ReceiptPath){
        $receipt=[ordered]@{schema='platform-studio.ahp-contract-checks.v1';observedUtc=[DateTimeOffset]::UtcNow.ToString('O');origin=$origin;
            passed=$checks.Count;failed=0;results=$checks.ToArray();providerIds=$providerIds;liveAzure=$false;liveAdo=$false;modelInference=$false;signIn=$false}
        $path=[IO.Path]::GetFullPath($ReceiptPath)
        [IO.Directory]::CreateDirectory([IO.Path]::GetDirectoryName($path))|Out-Null
        [IO.File]::WriteAllText($path,($receipt|ConvertTo-Json -Depth 8),[Text.UTF8Encoding]::new($false))
    }
    Write-Host "PASS: $($checks.Count) AHP coordination/orchestration/policy checks; no login, inference, Azure or ADO calls."
}finally{$socket.Dispose();$timeout.Dispose()}
