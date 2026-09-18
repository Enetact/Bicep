#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('PlanFoundation','ApplyFoundation','PlanRelease','ApplyRelease')][string]$Action,
    [Parameter(Mandatory)][string]$BundleDirectory,[Parameter(Mandatory)][string]$EvidenceDirectory,
    [string]$PlanDirectory='',[Parameter(Mandatory)][string]$BoundServiceConnection,
    [Parameter(Mandatory)][string]$BoundEnvironment,[Parameter(Mandatory)][string]$BoundAgentPool)
. "$PSScriptRoot/self-service-common.ps1"
New-Item -ItemType Directory -Path $EvidenceDirectory -Force | Out-Null
$receipt=@{schemaVersion=1; action=$Action;ready=$false;status='Failed';startedUtc=[DateTimeOffset]::UtcNow.ToString('O')}
try {
    if ($env:BUILD_SOURCEBRANCH -cne 'refs/heads/main' -or $env:BUILD_REASON -cne 'Manual') { throw 'Self-service Azure actions require a manually queued protected main-branch run.' }
    $bundle=Read-ServiceBundle $BundleDirectory
    $target=$bundle.target
    if ($target.serviceConnection -cne $BoundServiceConnection -or $target.deploymentEnvironment -cne $BoundEnvironment -or $target.agentPool -cne $BoundAgentPool) { throw 'Target does not match the YAML-bound protected resources.' }
    if ($bundle.receipt.sourceCommit -cne $env:BUILD_SOURCEVERSION) { throw 'Artifact provenance does not match this run.' }
    $receipt.target=$target; $receipt.releaseId=$bundle.receipt.releaseId; $receipt.bundleHash=$bundle.hash
    $receipt.packageSha256=$bundle.receipt.files['application.zip']; $receipt.sourceCommit=$bundle.receipt.sourceCommit
    $phase=if ($Action.EndsWith('Foundation')) {'Foundation'} else {'Release'}
    if ($Action.StartsWith('Plan')) {
        $plan=New-ServicePlan $bundle $phase $EvidenceDirectory
        $receipt.status='PreviewReady'; $receipt.planFingerprint=$plan.fingerprint
        Write-Host "##vso[task.uploadsummary]$(Join-Path $EvidenceDirectory summary.md)"
    } else {
        if (!$PlanDirectory) { throw 'Apply requires the previous stage plan artifact.' }
        $result=Invoke-ServiceApply $bundle $phase $PlanDirectory $EvidenceDirectory
        $receipt.status=$result.status; $receipt.ready=$result.ready; $receipt.outputs=$result.outputs
        if ($result.Contains('smoke')) { $receipt.smoke=$result.smoke }
    }
} catch {
    $receipt.status='Failed'; $receipt.ready=$false; $receipt.error=$_.Exception.Message
    throw
} finally {
    $receipt.finishedUtc=[DateTimeOffset]::UtcNow.ToString('O')
    Write-ServiceJson $receipt (Join-Path $EvidenceDirectory receipt.json)
}
