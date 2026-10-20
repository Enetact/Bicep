#requires -Version 7.4
[CmdletBinding()]
param([int]$Port=5099)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$directory=Join-Path $root 'artifacts/naming-integration'
$dll=Join-Path $directory 'build/bin/SelfService.Portal/release/SelfService.Portal.dll'
if(!(Test-Path -LiteralPath $dll)){throw 'Build the actual Release portal with --artifacts-path artifacts/naming-integration/build first.'}
if($Port -eq 5087 -or $Port -lt 1024 -or $Port -gt 65535){throw 'Use a separate local verification port.'}
if(Get-NetTCPConnection -State Listen -LocalPort $Port -ErrorAction SilentlyContinue){throw 'Verification port is occupied.'}
$primary=Join-Path $root '.local/portal/process.json'
$before=if(Test-Path $primary){[IO.File]::ReadAllText($primary)}else{$null}
$exe=(Get-Command dotnet -ErrorAction Stop).Source
$process=Start-Process -FilePath $exe -ArgumentList @('"'+$dll+'"',"--Portal:Port=$Port",'--contentRoot="'+(Join-Path $root 'src/SelfService.Portal')+'"','--Portal:RepositoryRoot="'+$root+'"') -WorkingDirectory $root -WindowStyle Hidden -PassThru -RedirectStandardOutput (Join-Path $directory http.log) -RedirectStandardError (Join-Path $directory http-errors.log)
$started=$process.StartTime.ToUniversalTime()
$checks=0
try {
    $url="http://localhost:$Port";$ready=$null
    for($i=0;$i -lt 60;$i++){
        $process.Refresh();if($process.HasExited){throw 'Candidate portal exited before readiness.'}
        try{$ready=Invoke-RestMethod "$url/api/bootstrap" -SessionVariable candidateSession -TimeoutSec 1;break}catch{Start-Sleep -Milliseconds 250}
    }
    if(!$ready -or $ready.products.Count -ne 11 -or !$ready.naming.draftCalculations -or $ready.naming.deploymentSubmission){throw 'Actual Release candidate did not become ready with non-deploying naming capabilities.'};$checks++
    $headers=@{Origin=$url;'X-Portal-CSRF'=$ready.csrf}
    $intent=(Get-Content (Join-Path $root tests/naming/request.example.json) -Raw|ConvertFrom-Json -AsHashtable).intent
    foreach($type in @('managed-devops-pool','fabric','private-storage','key-vault','observability','http-functions','service-bus-worker','private-aks','private-data-platform','logic-app-event-grid','blob-transfer')) {
        $body=@{workloadType=$type;intent=$intent}|ConvertTo-Json -Depth 20
        $draft=Invoke-RestMethod "$url/api/naming/draft" -Method Post -ContentType 'application/json' -Headers $headers -WebSession $candidateSession -Body $body
        if($draft.schema -ne 'platform.workload-naming-draft/v1' -or $draft.deploymentAuthorized -or $draft.plan.intent.instanceId -ne $intent.instanceId){throw 'Unexpected live naming response.'};$checks++
    }
    $bad=Invoke-WebRequest "$url/api/naming/draft" -Method Post -ContentType 'application/json' -Headers $headers -WebSession $candidateSession -Body (@{workloadType='unknown-product';intent=$intent}|ConvertTo-Json -Depth 20) -SkipHttpErrorCheck
    if($bad.StatusCode -ne 400){throw 'Unsupported workload was not rejected.'};$checks++
    $csrf=Invoke-WebRequest "$url/api/naming/draft" -Method Post -ContentType 'application/json' -Headers @{Origin=$url;'X-Portal-CSRF'='invalid-test-csrf'} -WebSession $candidateSession -Body '{}' -SkipHttpErrorCheck
    if($csrf.StatusCode -ne 403){throw 'CSRF boundary failed.'};$checks++
    $fixture=& node --input-type=module -e "import {fixture,input} from './tests/analysis/fixtures.mjs';const i=input(fixture('blob-transfer',true));process.stdout.write(JSON.stringify({manifest:i.manifestBytes.toString('base64'),inventory:i.inventoryBytes.toString('base64')}));"
    if($LASTEXITCODE){throw 'Synthetic discovery fixture failed.'}
    $analysis=Invoke-RestMethod "$url/api/analysis" -Method Post -ContentType 'application/json' -Headers $headers -WebSession $candidateSession -Body $fixture
    if(!$analysis.report.observedNames.Count -or $analysis.namingDrafts.schema -ne 'platform.discovery-naming-drafts/v1' -or $analysis.namingDrafts.deploymentAuthorized){throw 'Generated discovery names missing from actual API response.'};$checks++
    $asset=Invoke-WebRequest "$url/naming.mjs" -WebSession $candidateSession
    if($asset.StatusCode -ne 200 -or !$asset.Content.Contains('renderNamingEditor')){throw 'Naming UI asset unavailable.'};$checks++
    if($before -and [IO.File]::ReadAllText($primary) -cne $before){throw 'Primary portal ownership receipt changed during verification.'};$checks++
    @{checksPassed=$checks;actualReleaseApp=$true;port=$Port;browserVisualAcceptance=$false;primaryPortalRestarted=$false;azureCalls=$false;adoCalls=$false;authenticationAttempted=$false}|ConvertTo-Json|Set-Content (Join-Path $directory http-receipt.json)
    Write-Host "PASS: $checks actual Release HTTP checks. Separate HTTP cookie session; primary portal and authentication untouched."
} finally {
    $process.Refresh()
    if(!$process.HasExited){
        if($process.StartTime.ToUniversalTime() -ne $started -or $process.Path -ine $exe){throw 'Candidate process identity changed; refusing to stop it.'}
        Stop-Process -Id $process.Id
        $process.WaitForExit(10000)|Out-Null
    }
}
