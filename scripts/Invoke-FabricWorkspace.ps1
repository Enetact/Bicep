#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('Preview','Apply','Verify')][string]$Mode,
    [Parameter(Mandatory)][string]$ManifestPath,
    [Parameter(Mandatory)][string]$PlanPath,
    [Parameter(Mandatory)][string]$ReceiptPath,
    [string]$ExpectedPlanSha256=''
)
. "$PSScriptRoot/PlatformTrace.ps1"
$platformTrace = Start-PlatformTrace -Name 'platform.workflow.fabric' -Attributes @{ 'platform.mode'=$Mode; 'platform.workload'='fabric' }
try {
$ErrorActionPreference='Stop'
. "$PSScriptRoot/fabric-workspace-common.ps1"
. "$PSScriptRoot/fabric-execution-common.ps1"
function Write-FabricJson($Value,[string]$Path) {
    $parent=Split-Path ([IO.Path]::GetFullPath($Path)) -Parent
    New-Item -ItemType Directory -Path $parent -Force | Out-Null
    [IO.File]::WriteAllText([IO.Path]::GetFullPath($Path),($Value | ConvertTo-Json -Depth 80),[Text.UTF8Encoding]::new($false))
}
$manifestText=Get-Content -LiteralPath $ManifestPath -Raw
$manifest=$manifestText | ConvertFrom-Json -AsHashtable
Assert-FabricManifest $manifest
Assert-FabricExecutionProfile $manifest
Set-FabricApiContext $manifest
if ($Mode -eq 'Apply' -and !$manifest.enabled) { throw 'Fabric workspace target is disabled until platform onboarding and acceptance approval.' }
if ($Mode -eq 'Apply') {
    foreach ($attempt in @($env:SYSTEM_STAGEATTEMPT,$env:SYSTEM_JOBATTEMPT)) {
        if ($attempt -and ($attempt -cnotmatch '^[1-9][0-9]*$' -or [long]$attempt -gt 1)) { throw 'Fabric Apply does not support stage/job retries. Reconcile the prior outcome and start a new Preview run.' }
    }
}
if ([IO.Path]::GetFullPath($PlanPath) -ieq [IO.Path]::GetFullPath($ReceiptPath)) { throw 'Fabric plan and receipt must use different paths.' }
$sourceBinding=@{
    buildId=[string]$env:BUILD_BUILDID
    sourceVersion=[string]$env:BUILD_SOURCEVERSION
    wrapperSha256=(Get-FileHash -LiteralPath $PSCommandPath -Algorithm SHA256).Hash.ToLowerInvariant()
    commonSha256=(Get-FileHash -LiteralPath "$PSScriptRoot/fabric-workspace-common.ps1" -Algorithm SHA256).Hash.ToLowerInvariant()
    executionSha256=(Get-FileHash -LiteralPath "$PSScriptRoot/fabric-execution-common.ps1" -Algorithm SHA256).Hash.ToLowerInvariant()
    managedPoolCommonSha256=(Get-FileHash -LiteralPath "$PSScriptRoot/managed-devops-pool-common.ps1" -Algorithm SHA256).Hash.ToLowerInvariant()
    telemetrySha256=(Get-FileHash -LiteralPath "$PSScriptRoot/PlatformTrace.ps1" -Algorithm SHA256).Hash.ToLowerInvariant()
}
# The pipeline supplies WIF Azure CLI authentication. Tokens never enter artifacts or process arguments.
$accountText=& az account show --output json --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Connect the reviewed deployment identity first.' }
$account=$accountText | ConvertFrom-Json -AsHashtable
if ($account.tenantId -ine $manifest.tenantId -or $account.id -ine $manifest.subscriptionId) { throw 'Fabric tenant/subscription mismatch.' }
$tokenText=& az account get-access-token --resource https://api.fabric.microsoft.com --output json --only-show-errors
if ($LASTEXITCODE -ne 0) { throw 'Fabric token unavailable. Check tenant consent and Fabric administrator settings.' }
$token=$tokenText | ConvertFrom-Json -AsHashtable
$script:FabricToken=$token.accessToken
try {
    $payload=$script:FabricToken.Split('.')[1].Replace('-','+').Replace('_','/')
    $payload=$payload.PadRight($payload.Length+(4-$payload.Length%4)%4,'=')
    $claims=[Text.Encoding]::UTF8.GetString([Convert]::FromBase64String($payload)) | ConvertFrom-Json -AsHashtable
    if ($claims.tid -ine $manifest.tenantId -or $claims.oid -ine $manifest.deploymentPrincipalObjectId -or $claims.aud.TrimEnd('/') -cne 'https://api.fabric.microsoft.com' -or [long]$claims.exp -le [DateTimeOffset]::UtcNow.ToUnixTimeSeconds()) { throw 'Fabric token identity/audience/expiry mismatch.' }
    $manifestHash=Get-FabricHash $manifestText
    $executionEvidence=Test-FabricExecutionRequirements $manifest
    if ($Mode -eq 'Preview') {
        $operation=New-FabricOperation $manifest
        $plan=@{schemaVersion=1;createdUtc=[DateTimeOffset]::UtcNow.ToString('o');expiresUtc=[DateTimeOffset]::UtcNow.AddMinutes(30).ToString('o');tenantId=$manifest.tenantId;principalObjectId=$claims.oid;manifestSha256=$manifestHash;sourceBinding=$sourceBinding;executionEvidence=$executionEvidence;operation=$operation}
        Write-FabricJson $plan $PlanPath
        $digest=(Get-FileHash -LiteralPath $PlanPath -Algorithm SHA256).Hash.ToLowerInvariant()
        Write-Host "Fabric API Preview saved. SHA256: $digest. This is a Fabric API plan, not ARM What-If."
        exit 0
    }
    $actualHash=(Get-FileHash -LiteralPath $PlanPath -Algorithm SHA256).Hash.ToLowerInvariant()
    if ($ExpectedPlanSha256 -cnotmatch '^[0-9a-f]{64}$' -or $actualHash -cne $ExpectedPlanSha256) { throw 'The exact reviewed Fabric plan SHA256 is required.' }
    $plan=Get-Content -LiteralPath $PlanPath -Raw | ConvertFrom-Json -AsHashtable
    if ($plan.schemaVersion -ne 1 -or $plan.manifestSha256 -cne $manifestHash -or $plan.tenantId -ine $manifest.tenantId -or $plan.principalObjectId -ine $claims.oid) { throw 'Fabric plan source/identity binding changed.' }
    if ((ConvertTo-FabricCanonical $plan.sourceBinding) -cne (ConvertTo-FabricCanonical $sourceBinding)) { throw 'Fabric plan run, source revision or reviewed deployment code changed. Start a new Preview.' }
    if ((ConvertTo-FabricCanonical $plan.executionEvidence) -cne (ConvertTo-FabricCanonical $executionEvidence)) { throw 'Fabric managed pool, Azure DevOps scope or private-network evidence changed. Start a new Preview.' }
    if ($Mode -eq 'Verify') {
        $receipt=Get-Content -LiteralPath $ReceiptPath -Raw | ConvertFrom-Json -AsHashtable
        if ($receipt.planSha256 -cne $actualHash -or $receipt.status -cne 'Succeeded') { throw 'Only a completed matching operation can be verified. Reconcile unknown outcomes manually.' }
        $operation=$plan.operation;$result=$receipt.result;$o=$manifest.operation;$read=@{}
        if ($result.operationUri) {
            $read=Invoke-FabricApi GET $result.operationUri
            if ($read.status -cne 'Succeeded') { throw 'Fabric operation no longer reports success.' }
            if ($operation.kind -eq 'CreateLakehouse') {
                $entity=Invoke-FabricApi GET ($result.operationUri+'/result')
                Assert-FabricGuid $entity.id
                $result.id=$entity.id
            }
        }
        switch -CaseSensitive ($operation.kind) {
            'ProvisionIdentity' {
                $read=Invoke-FabricApi GET "workspaces/$($o.workspaceId)"
                if (!$read.workspaceIdentity) { throw 'Workspace identity not verified.' }
            }
            'AssignWorkspaceToStage' {
                $read=Get-FabricCollection "deploymentPipelines/$($o.deploymentPipelineId)/stages"
                if (@($read | Where-Object { $_.id -ieq $o.stageId -and $_.workspaceId -ieq $o.workspaceId }).Count -ne 1) { throw 'Workspace/stage assignment not verified.' }
            }
            'PromoteItems' {
                if (!$result.operationUri) {
                    Assert-FabricGuid $result.id
                    $read=Invoke-FabricApi GET "deploymentPipelines/$($o.deploymentPipelineId)/operations/$($result.id)"
                    if ($read.status -cne 'Succeeded') { throw 'Deployment operation not verified.' }
                }
                $stages=@(Get-FabricCollection "deploymentPipelines/$($o.deploymentPipelineId)/stages")
                $target=@($stages | Where-Object { $_.id -ieq $o.targetStageId })
                $beforeTarget=@($operation.before.stages | Where-Object { $_.id -ieq $o.targetStageId })
                if ($target.Count -ne 1 -or $beforeTarget.Count -ne 1 -or $target[0].workspaceId -ine $beforeTarget[0].workspaceId) { throw 'Target stage assignment changed.' }
                $targetItems=@(Get-FabricCollection "workspaces/$($target[0].workspaceId)/items")
                foreach ($item in $o.items) {
                    $source=@($operation.before.sourceItems | Where-Object { $_.id -ieq $item.sourceItemId })
                    if ($source.Count -ne 1 -or @($targetItems | Where-Object { $_.displayName -ceq $source[0].displayName -and $_.type -ceq $item.itemType }).Count -ne 1) { throw 'Promoted item read-back incomplete.' }
                }
            }
            default {
                Assert-FabricGuid $result.id
                $read=Invoke-FabricApi GET ($operation.path+'/'+$result.id)
                if ($read.id -ine $result.id) { throw 'Fabric read-back ID mismatch.' }
                if ($o.Contains('displayName') -and $read.displayName -cne $o.displayName) { throw 'Fabric read-back name mismatch.' }
                if ($operation.kind -eq 'CreateWorkspace' -and $read.capacityId -ine $o.capacityId) { throw 'Workspace capacity assignment not verified.' }
                if ($operation.kind -eq 'AddWorkspaceAdminGroup' -and ($read.principal.id -ine $o.groupObjectId -or $read.role -cne 'Admin')) { throw 'Workspace group role not verified.' }
                if ($operation.kind -eq 'CreateManagedPrivateEndpoint' -and ($read.targetPrivateLinkResourceId -ine $o.targetPrivateLinkResourceId -or $read.targetSubresourceType -cne $o.targetSubresourceType -or $read.provisioningState -cne 'Succeeded' -or $read.connectionState.status -cne 'Approved')) { throw 'Managed endpoint remains unapproved or unprovisioned; source-owner approval and a fresh Verify are required.' }
                if ($operation.kind -eq 'CreateConnection' -and ($read.connectionDetails.type -cne $o.connectionType -or $read.credentialDetails.credentialType -cne 'WorkspaceIdentity' -or $read.credentialDetails.connectionEncryption -cne 'Encrypted')) { throw 'Connection identity/type/encryption not verified.' }
                if ($operation.kind -eq 'CreateDeploymentPipeline' -and (ConvertTo-FabricCanonical @($read.stages | Sort-Object order | ForEach-Object { $_.displayName })) -cne (ConvertTo-FabricCanonical @($o.stages | ForEach-Object { $_.displayName }))) { throw 'Deployment pipeline stages not verified.' }
            }
        }
        $receipt.verifiedUtc=[DateTimeOffset]::UtcNow.ToString('o')
        $receipt.verification='ApiReadBackSucceeded'
        Write-FabricJson $receipt $ReceiptPath
        Write-Host 'Fabric API read-back verified. Data access, connector reachability and business acceptance require separate tests.'
        exit 0
    }
    if ([DateTimeOffset]::Parse($plan.expiresUtc) -lt [DateTimeOffset]::UtcNow) { throw 'Fabric plan expired. Preview again.' }
    $fresh=New-FabricOperation $manifest
    if ((ConvertTo-FabricCanonical $fresh) -cne (ConvertTo-FabricCanonical $plan.operation)) { throw 'Fabric evidence or operation drifted. Preview again.' }
    # A run-local durable attempt marker is created BEFORE the write. There is no automatic write retry.
    $attemptPath=$PlanPath+'.attempt'
    $marker=[IO.File]::Open($attemptPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    $marker.Dispose()
    # Retain the attempt marker beside the published receipt as well as the consumed plan.
    # This does not provide cross-agent exactly-once execution; ADO stage/job retries are refused above.
    $receiptAttemptPath=$ReceiptPath+'.attempt'
    New-Item -ItemType Directory -Path (Split-Path ([IO.Path]::GetFullPath($ReceiptPath)) -Parent) -Force | Out-Null
    $receiptMarker=[IO.File]::Open($receiptAttemptPath,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
    try {
        $markerBytes=[Text.Encoding]::UTF8.GetBytes((@{planSha256=$actualHash;sourceBinding=$sourceBinding;startedUtc=[DateTimeOffset]::UtcNow.ToString('o')} | ConvertTo-Json -Depth 10 -Compress))
        $receiptMarker.Write($markerBytes,0,$markerBytes.Length)
    } finally { $receiptMarker.Dispose() }
    $receipt=@{schemaVersion=1;planSha256=$actualHash;kind=$fresh.kind;status='UnknownOutcome';startedUtc=[DateTimeOffset]::UtcNow.ToString('o');result=@{}}
    Write-FabricJson $receipt $ReceiptPath
    try {
        $result=Invoke-FabricApi $fresh.method $fresh.path $fresh.body
        # Retain identifiers only; no raw connection/credential response is saved.
        $receipt.result=@{}
        foreach ($key in @('id','operationUri','status')) { if ($result.Contains($key)) { $receipt.result[$key]=$result[$key] } }
        $receipt.status='Succeeded';$receipt.completedUtc=[DateTimeOffset]::UtcNow.ToString('o')
        Write-FabricJson $receipt $ReceiptPath
    } catch { Write-FabricJson $receipt $ReceiptPath;throw 'Fabric write failed or its outcome is unknown. Inspect the receipt and reconcile Fabric state before a new Preview.' }
    Write-Host 'Fabric operation completed. Run Verify using the same plan, digest and receipt.'
} finally { $script:FabricToken=$null;$script:FabricApiContext=$null;$token=$null;$tokenText=$null;$claims=$null }
} catch {
    Set-PlatformTraceFailure -Trace $platformTrace -Exception $_.Exception
    throw
} finally { Complete-PlatformTrace $platformTrace }
