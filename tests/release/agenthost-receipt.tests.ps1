#requires -Version 7.4
[CmdletBinding()]
param([string]$PortalDll='')
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if(![OperatingSystem]::IsWindows()){throw 'This isolated process receipt test currently requires Windows.'}
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
if(!$PortalDll){$PortalDll=Join-Path $root 'artifacts/foundation-portal-regression/bin/SelfService.Portal/release/SelfService.Portal.dll'}
$dll=(Get-Item -LiteralPath $PortalDll -ErrorAction Stop).FullName
$dotnet=(Get-Command dotnet -CommandType Application -ErrorAction Stop|Select-Object -First 1).Source
$run=Join-Path $root ('artifacts/agenthost-receipt-tests/'+[guid]::NewGuid().ToString('N'))
[IO.Directory]::CreateDirectory($run)|Out-Null
$listener=[Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback,0)
$listener.Start();$port=$listener.LocalEndpoint.Port;$listener.Stop()
$process=$null;$started=$null
try{
    $arguments='"'+$dll+'" --Portal:Port='+$port+' --Portal:RepositoryRoot="'+$root+'"'
    $process=Start-Process -FilePath $dotnet -ArgumentList $arguments -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $run 'host.log') -RedirectStandardError (Join-Path $run 'host-error.log')
    $started=$process.StartTime.ToUniversalTime();$ready=$false
    for($attempt=0;$attempt -lt 80;$attempt++){
        $process.Refresh();if($process.HasExited){throw 'Isolated portal exited before readiness; inspect ignored fixture logs.'}
        try{$null=Invoke-RestMethod -Uri "http://localhost:$port/api/bootstrap" -TimeoutSec 1;$ready=$true;break}catch{Start-Sleep -Milliseconds 100}
    }
    if(!$ready){throw 'Isolated portal readiness timed out.'}
    $receiptPath=Join-Path $run 'ahp.json'
    & (Join-Path $root 'scripts/Test-AgentHost.ps1') -Port $port -ReceiptPath $receiptPath
    $receipt=Get-Content -LiteralPath $receiptPath -Raw|ConvertFrom-Json -AsHashtable
    $results=@($receipt.results)
    if($receipt.schema -cne 'platform-studio.ahp-contract-checks.v1' -or $results.Count -eq 0 -or
        $receipt.passed -ne $results.Count -or @($results|Where-Object {$_.passed -isnot [bool] -or !$_.passed}).Count -or
        @($results.name|Sort-Object -Unique).Count -ne $results.Count -or $receipt.origin -cne "http://localhost:$port" -or
        $receipt.liveAzure -or $receipt.liveAdo -or $receipt.modelInference -or $receipt.signIn){throw 'Derived AHP receipt did not match actual isolated checks.'}
    [ordered]@{schema='platform-studio.agenthost-receipt-test.v1';observedUtc=[DateTimeOffset]::UtcNow.ToString('O');
        passed=$results.Count;failed=0;providerCount=@($receipt.providerIds).Count;isolatedPid=$process.Id;isolatedStartUtc=$started.ToString('O');
        portalDll=$dll;primaryPortalChanged=$false;cloudCalls=$false;signIns=$false;modelInference=$false;receipt=$receiptPath}|ConvertTo-Json -Depth 8|
        Set-Content -LiteralPath (Join-Path $run 'results.json') -Encoding utf8
    Write-Host "PASS: derived $($results.Count) named AHP checks from the isolated portal; $(@($receipt.providerIds).Count) catalog providers. $run"
}finally{
    if($process){
        $process.Refresh()
        if(!$process.HasExited){
            $candidate=Get-Process -Id $process.Id -ErrorAction Stop
            if($candidate.StartTime.ToUniversalTime() -ne $started -or $candidate.Path -ne $dotnet){throw 'Isolated portal process ownership changed; refusing to stop it.'}
            Stop-Process -Id $candidate.Id
            if(!$process.WaitForExit(10000)){throw 'Owned isolated portal did not exit.'}
        }
        $process.Dispose()
    }
}
