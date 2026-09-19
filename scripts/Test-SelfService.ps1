#requires -Version 7.4
[CmdletBinding()]
param()
. "$PSScriptRoot/self-service-common.ps1"
$testRoot=Join-Path (Get-ProjectRoot) ('artifacts/self-service-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$results=[Collections.Generic.List[object]]::new()
function Check([bool]$Condition,[string]$Message='Assertion failed') { if (!$Condition) { throw $Message } }
function Reject([scriptblock]$Body,[string]$Message='') {
    $caught=$false
    try { & $Body | Out-Null } catch { if ($Message -and $_.Exception.Message -notlike "*$Message*") { throw }; $caught=$true }
    Check $caught 'Expected rejection did not occur.'
}
function Case([string]$Name,[scriptblock]$Body) {
    & $Body
    $results.Add(@{name=$Name;passed=$true})
}
function Clone($Value) { ConvertFrom-Json ($Value | ConvertTo-Json -Depth 100) -AsHashtable }
$target=@{schemaVersion=1;enabled=$true;workload='blobcopy';environmentName='dev';subscriptionId='11111111-1111-1111-1111-111111111111';resourceGroup='rg-blobcopy-dev';parameterFile='environments/dev.bicepparam';serviceConnection='sc-blobcopy-dev';agentPool='blob-transfer-private';deploymentEnvironment='blobcopy-dev';smokePrefix='smoke/'}
$values=@{workload='blobcopy';environmentName='dev';owner='platform';costCenter='CC1';destinationSubscriptionId=$target.subscriptionId;destinationResourceGroupName='rg-destination';destinationStorageAccountName='destinationaccount';destinationContainerName='incoming';destinationIsHnsEnabled=$true;vnetAddressPrefix='10.40.0.0/16';integrationSubnetPrefix='10.40.0.0/26';privateEndpointSubnetPrefix='10.40.1.0/26'}
$params=@{}; foreach ($key in $values.Keys) { $params[$key]=@{value=$values[$key]} }
Case 'enabled valid target and parameter contract' { Assert-ServiceTarget $target blobcopy dev; Assert-ServiceParameters $target $params }
Case 'disabled target' { $t=Clone $target; $t.enabled=$false; Reject { Assert-ServiceTarget $t blobcopy dev } disabled }
Case 'string Boolean is not enabled' { $t=Clone $target; $t.enabled='false'; Reject { Assert-ServiceTarget $t blobcopy dev } disabled }
Case 'unregistered or mismatched selection' { Reject { Assert-ServiceTarget $target other dev } mismatch; Reject { Read-ServiceTarget '../blobcopy' dev } Invalid }
Case 'unknown field' { $t=Clone $target; $t.script='evil'; Reject { Assert-ServiceTarget $t blobcopy dev } fields }
Case 'placeholder subscription' { $t=Clone $target; $t.subscriptionId=[guid]::Empty.ToString(); Reject { Assert-ServiceTarget $t blobcopy dev } GUID }
Case 'traversal and rooted file paths' { Reject { Resolve-ServicePath $testRoot '../outside' }; Reject { Resolve-ServicePath $testRoot 'C:\outside' } }
Case 'compiled target mismatch' { $p=Clone $params; $p.environmentName.value='prod'; Reject { Assert-ServiceParameters $target $p } differs }
Case 'placeholder parameter' { $p=Clone $params; $p.owner.value='REPLACE_TEAM'; Reject { Assert-ServiceParameters $target $p } placeholders }
Case 'source-ledger collision' { $p=Clone $params; $p.ledgerContainerName=@{value='incoming'}; Reject { Assert-ServiceParameters $target $p } differ }
Case 'unmapped smoke prefix' { $p=Clone $params; $p.sourceScopePrefixes=@{value=@{'claims/'='claims'}}; Reject { Assert-ServiceParameters $target $p } mapped }
Case 'source versions required' { $p=Clone $params; $p.recoveryIncludeSourceVersions=@{value=$false}; Reject { Assert-ServiceParameters $target $p } retained }
Case 'production requires action group' { $t=Clone $target; $t.environmentName='prod'; $p=Clone $params; $p.environmentName.value='prod'; Reject { Assert-ServiceParameters $t $p } 'action groups' }
Case 'canonical object order is stable; values and array order matter' {
    Check ((Get-ValueHash @{a=1;b=@{x=2;y=3}}) -ceq (Get-ValueHash @{b=@{y=3;x=2};a=1}))
    Check ((Get-ValueHash @(1,2)) -cne (Get-ValueHash @(2,1)))
}
foreach ($kind in @('Delete','Ignore','Unsupported','Deploy')) {
    Case "reject what-if $kind" { Reject { Get-ServiceChanges @{status='Succeeded';changes=@(@{resourceId='/id';changeType=$kind})} } unanalyzed }
}
Case 'failed or incomplete what-if' { Reject { Get-ServiceChanges @{status='Failed';changes=@()} }; Reject { Get-ServiceChanges @{status='Succeeded'} } }
Case 'private address boundaries' { foreach ($ip in @('10.1.2.3','172.16.0.1','172.31.255.254','192.168.0.1')) { Check (Test-ServicePrivateAddress $ip) }; foreach ($ip in @('127.0.0.1','8.8.8.8','172.32.0.1','::1')) { Check (!(Test-ServicePrivateAddress $ip)) } }

$bundleDir=Join-Path $testRoot bundle
Write-ServiceJson @{parameters=$params} (Join-Path $bundleDir parameters.json)
Write-ServiceJson $target (Join-Path $bundleDir target.json)
Write-ServiceJson @{resources=@()} (Join-Path $bundleDir main.json)
[IO.File]::WriteAllText((Join-Path $bundleDir application.zip),'Synthetic package fixture; never uploaded to Azure.')
$metadata=@(); foreach ($pair in @(@('DispatchUploadedBlob','blobTrigger'),@('CopyUploadedBlob','queueTrigger'),@('ReconcileTransfers','timerTrigger'),@('AuditTransferLedger','timerTrigger'),@('MonitorTransferPoison','timerTrigger'))) { $metadata+=@{name=$pair[0];bindings=@(@{type=$pair[1]})} }
Write-ServiceJson $metadata (Join-Path $bundleDir functions.metadata)
$files=@{}; foreach ($file in @('main.json','parameters.json','target.json','application.zip','functions.metadata')) { $files[$file]=Get-ServiceHash (Join-Path $bundleDir $file) }
Write-ServiceJson @{schemaVersion=1;releaseId='offline-test';sourceCommit='test';files=$files} (Join-Path $bundleDir bundle.json)
$bundle=Read-ServiceBundle $bundleDir
Case 'tampered package rejected' { Add-Content (Join-Path $bundleDir application.zip) 'tampered'; Reject { Read-ServiceBundle $bundleDir } integrity; [IO.File]::WriteAllText((Join-Path $bundleDir application.zip),'Synthetic package fixture; never uploaded to Azure.') }
Case 'frozen bundle retains and hashes the discovery handoff' {
    $withDiscovery=Join-Path $testRoot bundle-with-discovery
    Copy-Item -LiteralPath $bundleDir -Destination $withDiscovery -Recurse
    Write-ServiceJson @{source=@{runId='42'}} (Join-Path $withDiscovery discovery/manifest.json)
    Write-ServiceJson @{readOnly=$true;discoveryStatus='Complete'} (Join-Path $withDiscovery discovery/inventory.json)
    $receipt=Get-Content (Join-Path $withDiscovery bundle.json) -Raw | ConvertFrom-Json -AsHashtable
    $receipt.discoverySource=@{runId='42'}
    foreach ($file in @('discovery/manifest.json','discovery/inventory.json')) { $receipt.files[$file]=Get-ServiceHash (Join-Path $withDiscovery $file) }
    Write-ServiceJson $receipt (Join-Path $withDiscovery bundle.json)
    $read=Read-ServiceBundle $withDiscovery
    Check ($read.receipt.discoverySource.runId -eq '42' -and $read.receipt.files.Count -eq 7)
    Add-Content (Join-Path $withDiscovery discovery/inventory.json) ' '
    Reject { Read-ServiceBundle $withDiscovery } integrity
}
foreach ($failure in @('branch','reason','binding','provenance')) {
    Case "entrypoint rejects $failure and retains failed receipt" {
        $saved=@{}; foreach ($key in @('BUILD_SOURCEBRANCH','BUILD_REASON','BUILD_SOURCEVERSION')) { $saved[$key]=[Environment]::GetEnvironmentVariable($key) }
        try {
            $env:BUILD_SOURCEBRANCH='refs/heads/main'; $env:BUILD_REASON='Manual'; $env:BUILD_SOURCEVERSION='test'
            $binding=$target.serviceConnection
            switch ($failure) {
                branch { $env:BUILD_SOURCEBRANCH='refs/heads/feature' }
                reason { $env:BUILD_REASON='PullRequest' }
                binding { $binding='another-connection' }
                provenance { $env:BUILD_SOURCEVERSION='another-commit' }
            }
            $evidence=Join-Path $testRoot "entry-$failure"
            Reject { & "$PSScriptRoot/Invoke-SelfService.ps1" -Action PlanFoundation -BundleDirectory $bundleDir -EvidenceDirectory $evidence -BoundServiceConnection $binding -BoundEnvironment $target.deploymentEnvironment -BoundAgentPool $target.agentPool }
            $receipt=Get-Content (Join-Path $evidence receipt.json) -Raw | ConvertFrom-Json
            Check ($receipt.status -eq 'Failed' -and !$receipt.ready -and $receipt.error)
        } finally { foreach ($key in $saved.Keys) { [Environment]::SetEnvironmentVariable($key,$saved[$key]) } }
    }
}

# The only Azure boundary used below is this fake. Any unexpected CLI call fails.
$script:commands=[Collections.Generic.List[string]]::new()
$script:hasApp=$false; $script:drift=$false; $script:smokePass=$true; $script:connectionFails=$false
$script:packageExists=$false; $script:packageConflict=$false; $script:duplicateRequests=$false
$outputs=@{}
$outValues=@{hostStorageAccountName='sthdevtest';uploadStorageAccountName='studevtest';uploadContainer='incoming';ledgerContainer='transfer-ledger';transferQueue='transfer-work';packageContainer='packages';functionAppName='func-blobcopy-dev-test';functionAppResourceId="/subscriptions/$($target.subscriptionId)/resourceGroups/rg-blobcopy-dev/providers/Microsoft.Web/sites/func-blobcopy-dev-test";managedIdentityPrincipalId='runtime-principal';workspaceId='/workspace'}
foreach ($key in $outValues.Keys) { $outputs[$key]=@{value=$outValues[$key]} }
function Invoke-Az {
    param([string[]]$Arguments)
    $cmd=$Arguments -join ' '; $script:commands.Add($cmd)
    $answer=switch -Regex ($cmd) {
        '^group show ' { @{name='rg-blobcopy-dev'}; break }
        '^account show ' { @{tenantId='tenant'}; break }
        '^storage account show ' { @{id='/destination';isHnsEnabled=$true}; break }
        '^resource show ' { @{id='/destination/container'}; break }
        '^resource list ' { if ($script:hasApp) { ,@(@{name=$outputs.functionAppName.value;id=$outputs.functionAppResourceId.value}) } else { ,@() }; break }
        '^deployment group show ' { @{properties=@{outputs=$outputs}}; break }
        '^deployment group what-if ' { @{status='Succeeded';changes=@(@{resourceId='/resource';changeType='Modify';after=@{version= $(if ($script:drift) {2} else {1})}})}; break }
        '^deployment group create ' { @{properties=@{provisioningState='Succeeded';outputs=$outputs}}; break }
        '^storage blob exists ' { @{exists=$script:packageExists}; break }
        '^storage blob download ' {
            $path=$Arguments[[Array]::IndexOf($Arguments,'--file')+1]
            if ($script:packageConflict) { [IO.File]::WriteAllText($path,'Different package bytes') }
            else { Copy-Item -LiteralPath (Join-Path $bundleDir application.zip) -Destination $path }
            @{etag='existing'}; break
        }
        '^storage blob upload ' { @{etag='uploaded'}; break }
        default { throw "Unexpected mocked Azure call: $cmd" }
    }
    ConvertTo-Json -InputObject $answer -Depth 100 -Compress
}
function Wait-ServiceConnectivity($Bundle,$Outputs) { if ($script:connectionFails) { throw 'Synthetic connectivity failure' } }
function Wait-ServiceFunctions($Bundle,$Outputs) { }
function Invoke-ServiceSmoke($Bundle,$Outputs,$EvidenceDirectory) { @{passed=$script:smokePass;requestIds=$(if ($script:duplicateRequests) {@('r1','r1','r3')} else {@('r1','r2','r3')})} }

$foundationPlan=Join-Path $testRoot foundation-plan
$plan=New-ServicePlan $bundle Foundation $foundationPlan
Case 'new stack foundation is previewed before deployment' { Check (!$plan.skip); Check (!@($script:commands | Where-Object { $_ -match 'deployment group create|storage blob upload' }).Count) }
Case 'approved new foundation applies without claiming app readiness' { $result=Invoke-ServiceApply $bundle Foundation $foundationPlan (Join-Path $testRoot foundation-result); Check (!$result.ready -and $result.status -eq 'FoundationReady') }
Case 'existing stack skips bootstrap and preserves runtime alerts' {
    $script:hasApp=$true; $script:commands.Clear()
    $folder=Join-Path $testRoot existing-plan
    $null=New-ServicePlan $bundle Foundation $folder
    $result=Invoke-ServiceApply $bundle Foundation $folder (Join-Path $testRoot existing-result)
    Check (!@($script:commands | Where-Object { $_ -match '^deployment group create ' }).Count)
    Check ($result.status -eq 'FoundationReady')
}
$releasePlan=Join-Path $testRoot release-plan
$plan=New-ServicePlan $bundle Release $releasePlan
Case 'state drift rejects before mutation' {
    $script:commands.Clear(); $script:drift=$true
    Reject { Invoke-ServiceApply $bundle Release $releasePlan (Join-Path $testRoot drift-result) } drifted
    Check (!@($script:commands | Where-Object { $_ -match '^deployment group create |^storage blob upload ' }).Count)
    $script:drift=$false
}
Case 'expired preview rejected' { $expired=Clone $plan; $expired.createdUtc=[DateTimeOffset]::UtcNow.AddHours(-25).ToString('O'); Reject { Assert-ServicePlan $bundle $expired $plan Release } expired }
Case 'wrong bundle or phase rejected' { $bad=Clone $plan; $bad.bundleHash='other'; Reject { Assert-ServicePlan $bundle $bad $plan Release }; Reject { Assert-ServicePlan $bundle $plan $plan Foundation } }
Case 'failed connectivity prevents package and deployment mutation' {
    $script:commands.Clear(); $script:connectionFails=$true
    Reject { Invoke-ServiceApply $bundle Release $releasePlan (Join-Path $testRoot no-network-result) } connectivity
    Check (!@($script:commands | Where-Object { $_ -match '^deployment group create |^storage blob upload ' }).Count)
    $script:connectionFails=$false
}
Case 'failed smoke never reports Ready' { $script:smokePass=$false; Reject { Invoke-ServiceApply $bundle Release $releasePlan (Join-Path $testRoot failed-smoke) } incomplete; $script:smokePass=$true }
Case 'duplicate request evidence never reports Ready' { $script:duplicateRequests=$true; Reject { Invoke-ServiceApply $bundle Release $releasePlan (Join-Path $testRoot duplicate-smoke) } incomplete; $script:duplicateRequests=$false }
Case 'existing identical package is reused without upload' {
    $script:packageExists=$true; $script:commands.Clear()
    $result=Invoke-ServiceApply $bundle Release $releasePlan (Join-Path $testRoot reuse-result)
    Check ($result.ready -and !@($script:commands | Where-Object { $_ -match '^storage blob upload ' }).Count)
}
Case 'conflicting release ID prevents overwrite and deployment' {
    $script:packageConflict=$true; $script:commands.Clear()
    Reject { Invoke-ServiceApply $bundle Release $releasePlan (Join-Path $testRoot conflict-result) } 'different package bytes'
    Check (!@($script:commands | Where-Object { $_ -match '^deployment group create |^storage blob upload ' }).Count)
    $script:packageConflict=$false; $script:packageExists=$false
}
Case 'approved release uploads exact package then deploys and smoke-gates Ready' {
    $script:commands.Clear()
    $result=Invoke-ServiceApply $bundle Release $releasePlan (Join-Path $testRoot release-result)
    Check ($result.ready -and $result.status -eq 'Ready')
    $calls=@($script:commands | Where-Object { $_ -match '^storage blob upload |^deployment group create ' })
    Check ($calls.Count -eq 2 -and $calls[0].StartsWith('storage blob upload') -and $calls[1].StartsWith('deployment group create'))
    Check ($calls[0].Contains('--overwrite false'))
}
Write-ServiceJson @{passed=$results.Count;failed=0;azureCalls='mocked only';cases=$results} (Join-Path $testRoot results.json)
Write-Host "PASS: $($results.Count) self-service contract/orchestration cases. No Azure calls. Evidence: $testRoot"
