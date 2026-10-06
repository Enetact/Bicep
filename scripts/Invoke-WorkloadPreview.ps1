#requires -Version 7.4
[CmdletBinding()]
param([Parameter(Mandatory)][ValidateSet('Prepare','Preview','VerifyBundle')][string]$Action,
    [Parameter(Mandatory)][string]$Directory,[string]$DiscoveryDirectory='',
    [string]$Workload='',[string]$EnvironmentName='',[string]$SubscriptionAlias='',[string]$NetworkProfile='',
    [string]$ReleaseId='',[string]$BoundServiceConnection='',[string]$BundleDirectory='',[string]$NamingInstanceId='')
. "$PSScriptRoot/workload-preview-common.ps1"
if($NamingInstanceId -ceq 'legacy'){$NamingInstanceId=''}
if($Action -eq 'VerifyBundle'){
    $null=Assert-PreviewMatchesBundle $Directory (Read-ServiceBundle $BundleDirectory)
    Write-Host 'Deployment bundle matches this run''s successful infrastructure preview.'
    return
}
$status='Blocked';$errorText=''
try {
    if($Action -eq 'Prepare'){
        $target=Read-ServiceTarget $Workload $EnvironmentName $SubscriptionAlias $NetworkProfile -AllowDisabled
        New-WorkloadPreviewInputs $target $DiscoveryDirectory $Directory $ReleaseId $NamingInstanceId
        $status='Prepared; Azure validation pending'
    }else{
        if($env:BUILD_SOURCEBRANCH -cne 'refs/heads/main' -or $env:BUILD_REASON -cne 'Manual'){throw 'Preview requires a manually queued main run.'}
        $bundle=Read-WorkloadPreviewInputs $Directory
        if($BoundServiceConnection -cne $bundle.target.serviceConnection){throw 'Preview service connection binding mismatch.'}
        $null=Invoke-WorkloadInfrastructurePreview $bundle (Join-Path $Directory azure) -CaptureIac
        $status='Preview succeeded; no workload deployment performed'
    }
}catch{$errorText=$_.Exception.Message;throw}
finally{
    Write-ServiceJson @{action=$Action;status=$status;error=$errorText;workloadDeployed=$false} (Join-Path $Directory status.json)
    Write-WorkloadPreviewReadme $Directory $status $errorText
}
