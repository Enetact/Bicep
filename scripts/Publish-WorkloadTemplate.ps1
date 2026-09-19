#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][string]$BundleDirectory,[Parameter(Mandatory)][string]$EvidenceDirectory,
    [Parameter(Mandatory)][string]$BoundServiceConnection,[Parameter(Mandatory)][string]$BoundEnvironment,[Parameter(Mandatory)][string]$BoundAgentPool)
. "$PSScriptRoot/self-service-common.ps1"
$receipt=@{status='Failed';azureWorkloadDeployed=$false}
try {
    if ($env:BUILD_SOURCEBRANCH -cne 'refs/heads/main' -or $env:BUILD_REASON -cne 'Manual') { throw 'Template publication requires a manually queued protected main run.' }
    $bundle=Read-ServiceBundle $BundleDirectory
    if (!$bundle.Contains('stack') -or $bundle.receipt.sourceCommit -cne $env:BUILD_SOURCEVERSION) { throw 'Publication requires a stack bundle from this checkout.' }
    $s=$bundle.stack.publication
    if ($s.publisherServiceConnection -cne $BoundServiceConnection -or $s.publisherEnvironment -cne $BoundEnvironment -or $s.publisherAgentPool -cne $BoundAgentPool) { throw 'Publication protected resource binding mismatch.' }
    Assert-StackTooling
    $receipt=Publish-StackTemplate $bundle $EvidenceDirectory
    $receipt.status='Published';$receipt.azureWorkloadDeployed=$false
} catch { $receipt.error=$_.Exception.Message; throw }
finally { Write-ServiceJson $receipt (Join-Path $EvidenceDirectory receipt.json) }
