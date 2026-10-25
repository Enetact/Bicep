#requires -Version 7.4
# Exercises the actual command wrapper with synthetic Azure CLI identity and Fabric HTTP surfaces in child processes.
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$directory=Join-Path $root ('artifacts/fabric-wrapper-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory -Force|Out-Null
$pwsh=(Get-Process -Id $PID).Path;$harness=Join-Path $root tests/fabric/wrapper-harness.ps1
. (Join-Path $root tests/fabric/execution-fixtures.ps1)
$manifest=@{schemaVersion=1;enabled=$true;tenantId='11111111-1111-1111-1111-111111111111';subscriptionId='22222222-2222-2222-2222-222222222222';deploymentPrincipalObjectId='33333333-3333-3333-3333-333333333333';environmentName='dev';operation=@{kind='CreateWorkspace';displayName='fabric-wrapper-test';capacityId='44444444-4444-4444-4444-444444444444';capacityDisplayName='fabriccapacity'}}
$count=0
function Check([bool]$Value){if(!$Value){throw 'Fabric wrapper lifecycle assertion failed.'};$script:count++}
function Write-Json($Value,$Path){$Value|ConvertTo-Json -Depth 50|Set-Content $Path -Encoding utf8}
function New-Case([string]$Name,[switch]$ManagedPool,[switch]$PrivateWorkspace){
    $script:case=Join-Path $directory $Name;New-Item -ItemType Directory -Path $case -Force|Out-Null
    $script:manifestPath=Join-Path $case manifest.json;$script:planPath=Join-Path $case plan.json;$script:receiptPath=Join-Path $case receipt.json;$script:statePath=Join-Path $case state.json
    $caseManifest=$manifest
    if($ManagedPool -or $PrivateWorkspace){
        $caseManifest=New-FabricExecutionFixtureManifest
        if(!$PrivateWorkspace){
            $caseManifest.execution.Remove('privateWorkspace')
            $caseManifest.operation=$manifest.operation|ConvertTo-Json -Depth 30|ConvertFrom-Json -AsHashtable
        }
    }
    Write-Json $caseManifest $manifestPath
    Write-Json @{authCalls=0;apiCalls=0;writeCalls=0;created=$false;createdId='55555555-5555-5555-5555-555555555555'} $statePath
}
function Run([string]$Mode,[string]$Fault='none',[string]$Digest=''){
    $output=& $pwsh -NoProfile -File $harness -Mode $Mode -ManifestPath $manifestPath -PlanPath $planPath -ReceiptPath $receiptPath -StatePath $statePath -Fault $Fault -ExpectedPlanSha256 $Digest 2>&1
    $script:code=$LASTEXITCODE;$output|Set-Content (Join-Path $case "$Mode-$Fault.log")
}
function Read-State {Get-Content $statePath -Raw|ConvertFrom-Json -AsHashtable}
function Digest {(Get-FileHash -LiteralPath $planPath -Algorithm SHA256).Hash.ToLowerInvariant()}
New-Case happy
Run Preview;Check ($code -eq 0 -and (Test-Path $planPath) -and (Read-State).writeCalls -eq 0)
$hash=Digest
Run Apply none $hash;Check ($code -eq 0 -and (Read-State).writeCalls -eq 1 -and (Test-Path ($planPath+'.attempt')) -and (Test-Path ($receiptPath+'.attempt')))
$receipt=Get-Content $receiptPath -Raw|ConvertFrom-Json -AsHashtable;Check ($receipt.status -ceq 'Succeeded' -and $receipt.planSha256 -ceq $hash)
Run Verify none $hash;Check ($code -eq 0)
$receipt=Get-Content $receiptPath -Raw|ConvertFrom-Json -AsHashtable;Check ($receipt.verification -ceq 'ApiReadBackSucceeded' -and (Read-State).writeCalls -eq 1)
Run Apply none $hash;Check ($code -ne 0 -and (Read-State).writeCalls -eq 1)
Run Verify verify-name $hash;Check ($code -ne 0 -and (Read-State).writeCalls -eq 1)
New-Case disabled
$disabled=$manifest|ConvertTo-Json -Depth 50|ConvertFrom-Json -AsHashtable;$disabled.enabled=$false;Write-Json $disabled $manifestPath
Run Apply;Check ($code -ne 0 -and (Read-State).authCalls -eq 0 -and (Read-State).apiCalls -eq 0)
New-Case identity
Run Preview identity;Check ($code -ne 0 -and (Read-State).apiCalls -eq 0 -and !(Test-Path $planPath))
foreach($fault in @('hash','expiry','manifest','drift','attempt','wrapper-hash','common-hash','execution-hash','managed-pool-hash')){
    New-Case $fault;Run Preview;Check ($code -eq 0);$hash=Digest
    if($fault -eq 'hash'){$hash='a'*64}
    if($fault -eq 'expiry'){$plan=Get-Content $planPath -Raw|ConvertFrom-Json -AsHashtable;$plan.expiresUtc=[DateTimeOffset]::UtcNow.AddMinutes(-1).ToString('o');Write-Json $plan $planPath;$hash=Digest}
    if($fault -eq 'manifest'){$changed=$manifest|ConvertTo-Json -Depth 50|ConvertFrom-Json -AsHashtable;$changed.environmentName='qa';Write-Json $changed $manifestPath}
    if($fault -eq 'attempt'){[IO.File]::WriteAllText($planPath+'.attempt','prior attempt')}
    if($fault -in @('wrapper-hash','common-hash','execution-hash','managed-pool-hash')){$plan=Get-Content $planPath -Raw|ConvertFrom-Json -AsHashtable;$key=switch($fault){'wrapper-hash'{'wrapperSha256'}'common-hash'{'commonSha256'}'execution-hash'{'executionSha256'}'managed-pool-hash'{'managedPoolCommonSha256'}};$plan.sourceBinding[$key]='b'*64;Write-Json $plan $planPath;$hash=Digest}
    Run Apply $(if($fault -eq 'drift'){'drift'}else{'none'}) $hash
    Check ($code -ne 0 -and (Read-State).writeCalls -eq 0)
}
foreach($setting in @('SYSTEM_STAGEATTEMPT','SYSTEM_JOBATTEMPT','BUILD_BUILDID','BUILD_SOURCEVERSION')){
    New-Case $setting;Run Preview;Check ($code -eq 0);$hash=Digest;$before=(Read-State).authCalls
    $previous=[Environment]::GetEnvironmentVariable($setting,'Process')
    try{
        [Environment]::SetEnvironmentVariable($setting,$(if($setting -like '*ATTEMPT'){'2'}else{'synthetic-changed-binding'}),'Process')
        Run Apply none $hash
    }finally{[Environment]::SetEnvironmentVariable($setting,$previous,'Process')}
    Check ($code -ne 0 -and (Read-State).writeCalls -eq 0)
    if($setting -like '*ATTEMPT'){Check ((Read-State).authCalls -eq $before)}
}
New-Case unknown
Run Preview;$hash=Digest;Run Apply unknown $hash
$receipt=Get-Content $receiptPath -Raw|ConvertFrom-Json -AsHashtable
Check ($code -ne 0 -and $receipt.status -ceq 'UnknownOutcome' -and (Read-State).writeCalls -eq 1 -and (Test-Path ($planPath+'.attempt')) -and (Test-Path ($receiptPath+'.attempt')))
Run Verify none $hash;Check ($code -ne 0 -and (Read-State).writeCalls -eq 1)
New-Case managed-happy -ManagedPool
Run Preview;Check ($code -eq 0 -and (Read-State).writeCalls -eq 0)
$plan=Get-Content $planPath -Raw|ConvertFrom-Json -AsHashtable
Check ($plan.executionEvidence.kind -ceq 'ManagedDevOpsPool' -and $plan.executionEvidence.ado.poolId -eq 123 -and !$plan.executionEvidence.privateWorkspace)
Check ($plan.sourceBinding.executionSha256 -cmatch '^[0-9a-f]{64}$' -and $plan.sourceBinding.managedPoolCommonSha256 -cmatch '^[0-9a-f]{64}$')
Check ((Get-Content $planPath -Raw) -notmatch 'synthetic-job-token-never-sent|accessToken|Bearer ')
$hash=Digest
Run Apply new-agent $hash;Check ($code -eq 0 -and (Read-State).writeCalls -eq 1)
Run Verify new-agent $hash;Check ($code -eq 0 -and (Read-State).writeCalls -eq 1)
$receipt=Get-Content $receiptPath -Raw|ConvertFrom-Json -AsHashtable
Check ($receipt.verification -ceq 'ApiReadBackSucceeded')
foreach($fault in @('agent-job','pool-config','pool-binding','execution-drift')){
    New-Case ('managed-'+$fault) -ManagedPool
    Run Preview;Check ($code -eq 0);$hash=Digest
    Run Apply $fault $hash;Check ($code -ne 0 -and (Read-State).writeCalls -eq 0 -and !(Test-Path ($planPath+'.attempt')))
}
New-Case managed-verify-drift -ManagedPool
Run Preview;Check ($code -eq 0);$hash=Digest
Run Apply none $hash;Check ($code -eq 0 -and (Read-State).writeCalls -eq 1)
Run Verify execution-drift $hash;Check ($code -ne 0 -and (Read-State).writeCalls -eq 1)
$receipt=Get-Content $receiptPath -Raw|ConvertFrom-Json -AsHashtable
Check (!$receipt.Contains('verifiedUtc'))
New-Case managed-source-drift -ManagedPool
Run Preview;Check ($code -eq 0);$hash=Digest
$changed=Get-Content $manifestPath -Raw|ConvertFrom-Json -AsHashtable
$changed.execution.adoPoolName='another-reviewed-pool';Write-Json $changed $manifestPath
Run Apply none $hash;Check ($code -ne 0 -and (Read-State).writeCalls -eq 0)
New-Case missing-managed-profile
Run Preview unbound-pipeline;Check ($code -ne 0 -and (Read-State).authCalls -eq 0 -and (Read-State).writeCalls -eq 0)
New-Case private-global-operation -PrivateWorkspace
$changed=Get-Content $manifestPath -Raw|ConvertFrom-Json -AsHashtable
$changed.operation=$manifest.operation;Write-Json $changed $manifestPath
Run Preview;Check ($code -ne 0 -and (Read-State).authCalls -eq 0 -and (Read-State).writeCalls -eq 0)
New-Case private-wrong-api-host -PrivateWorkspace
$changed=Get-Content $manifestPath -Raw|ConvertFrom-Json -AsHashtable
$changed.execution.privateWorkspace.apiBaseUrl='https://api.fabric.microsoft.com';Write-Json $changed $manifestPath
Run Preview;Check ($code -ne 0 -and (Read-State).authCalls -eq 0 -and (Read-State).writeCalls -eq 0)
Write-Json @{passed=$count;azureCalls=0;fabricCalls=0;evidence='Synthetic actual-wrapper lifecycle, managed-pool/agent identity and source/evidence drift, hash, expiry, attempted replay and unknown-outcome tests; private DNS/TCP acceptance covered separately';directory=$directory} (Join-Path $directory results.json)
Write-Host "PASS: $count Fabric wrapper lifecycle checks. Synthetic Azure/Fabric only. Evidence: $directory"
