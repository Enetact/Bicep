#requires -Version 7.4
[CmdletBinding()]
param([int]$Port=5087)
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
    if($r.result.protocolVersion -ne '0.9.0'){throw 'AHP initialize failed.'}
    $channel='ahp-session:/'+[guid]::NewGuid().ToString()
    $r=Invoke-AgentRpc 2 createSession @{channel=$channel;provider='codex'}
    if($r.error){throw 'AHP session creation failed.'}
    $r=Invoke-AgentRpc 3 subscribe @{channel=$channel}
    if($r.result.snapshot.state.platformStudio.workflows.Count -ne 6){throw 'AHP workflow readiness projection failed.'}
    $r=Invoke-AgentRpc 4 subscribe @{channel='ahp-session:/'+[guid]::NewGuid().ToString()}
    if(!$r.error){throw 'Cross-session channel was not rejected.'}
    $r=Invoke-AgentRpc 5 dispatchAction @{channel=$channel;action=@{type='chat/turnStarted'}}
    if(!$r.error){throw 'Arbitrary model dispatch was not rejected.'}
    $socket.CloseAsync([Net.WebSockets.WebSocketCloseStatus]::NormalClosure,'Done',$timeout.Token).GetAwaiter().GetResult()|Out-Null
    Write-Host 'PASS: 5 AHP coordination checks; no login, inference, Azure or ADO calls.'
}finally{$socket.Dispose();$timeout.Dispose()}
