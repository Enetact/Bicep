#requires -Version 7.4
. "$PSScriptRoot/self-service-common.ps1"

function Read-DiscoveryManifest([string]$Directory, $Target, [string]$BoundServiceConnection) {
    $manifest=Get-Content (Resolve-ServicePath $Directory manifest.json) -Raw | ConvertFrom-Json -AsHashtable
    $inventoryPath=Resolve-ServicePath $Directory inventory.json
    $logic=(Get-TargetWorkloadType $Target) -eq 'logic-app-event-grid'
    $schema=if($logic){2}else{1}; $kind=if($logic){'workload-discovery'}else{'blob-transfer-discovery'}
    if ($manifest.schemaVersion -ne $schema -or $manifest.kind -ne $kind -or $manifest.discoveryStatus -ne 'Complete') { throw 'A complete discovery manifest is required.' }
    if ((Get-ServiceHash $inventoryPath) -cne $manifest.inventorySha256) { throw 'Discovery inventory hash does not match its manifest.' }
    $age=[DateTimeOffset]::UtcNow-[DateTimeOffset]::Parse($manifest.generatedUtc)
    if ($age.TotalDays -gt 7 -or $age.TotalMinutes -lt -5) { throw 'Discovery is stale or has a future timestamp; rerun discovery.' }
    if ($manifest.subscriptionId -ine $Target.subscriptionId -or $manifest.serviceConnection -cne $BoundServiceConnection -or $Target.serviceConnection -cne $BoundServiceConnection) { throw 'Discovery subscription/service connection does not match the selected target.' }
    if ($manifest.selection.workload -cne $Target.workload -or $manifest.selection.environment -cne $Target.environmentName) { throw 'Discovery workload/environment does not match the selected target.' }
    $region=if($Target.Contains('parameterOverrides') -and $Target.parameterOverrides.Contains('location')){$Target.parameterOverrides.location}else{'eastus2'}
    if ($logic -and ($manifest.workloadType -cne 'logic-app-event-grid' -or $manifest.selection.region -cne $region -or $manifest.selection.network -cne $Target.networkProfile -or $manifest.selection.subscription -cne $Target.subscriptionAlias)) { throw 'Logic App discovery intent mismatch.' }
    $inventory=Get-Content $inventoryPath -Raw | ConvertFrom-Json -AsHashtable
    if ($inventory.schemaVersion -ne 1 -or $inventory.readOnly -isnot [bool] -or !$inventory.readOnly -or $inventory.discoveryStatus -ne 'Complete' -or $inventory.subscription.id -ine $manifest.subscriptionId -or $inventory.generatedUtc -cne $manifest.generatedUtc) { throw 'Discovery inventory is incomplete or inconsistent with its manifest.' }
    if ($logic) {
        if (!$inventory.Contains('workloadType') -or $inventory.workloadType -cne 'logic-app-event-grid' -or !$inventory.Contains('providers') -or $inventory.providers.Count -ne 5 -or !$inventory.Contains('resources')) { throw 'Logic App inventory lacks workload prerequisites.' }
        $expectedProviders=@('Microsoft.Web','Microsoft.Storage','Microsoft.EventGrid','Microsoft.Insights','Microsoft.OperationalInsights')
        if (@(Compare-Object ($expectedProviders|Sort-Object) (@($inventory.providers|ForEach-Object {$_.namespace})|Sort-Object)).Count) { throw 'Logic App provider inventory is inconsistent.' }
        return $manifest
    }
    # A placeholder alias may be onboarded under a real alias after discovery.
    # Subscription and connection stay exact; shared resource IDs must be in evidence.
    if ($Target.schemaVersion -eq 2 -and $Target.parameterOverrides.Contains('networkMode') -and $Target.parameterOverrides.networkMode -eq 'existing') {
        $network=$Target.parameterOverrides.existingNetwork
        Assert-ServiceNetworkIds $Target $network
        foreach ($key in @('integrationSubnetId','privateEndpointSubnetId')) {
            $found=@($inventory.networks | ForEach-Object { $_.subnets } | Where-Object { $_.id -ieq $network[$key] })
            if ($found.Count -ne 1) { throw 'Selected subnet is missing or ambiguous in the saved discovery inventory.' }
        }
        # Approved cross-subscription DNS IDs remain supported; live preflight checks them.
        foreach ($id in $network.privateDnsZoneIds.Values) {
            if ($id.StartsWith("/subscriptions/$($Target.subscriptionId)/",[StringComparison]::OrdinalIgnoreCase) -and @($inventory.privateDnsZones | Where-Object { $_.id -ieq $id }).Count -ne 1) { throw 'Selected DNS zone is missing or ambiguous in the saved discovery inventory.' }
        }
    }
    return $manifest
}

function Assert-DiscoveryRun($Manifest, $Build, [string]$PipelineId, [string]$RunId, [string]$ProjectId, [string]$RepositoryId) {
    if ($PipelineId -cnotmatch '^[1-9][0-9]*$' -or $RunId -cnotmatch '^[1-9][0-9]*$') { throw 'Supply the numeric discovery pipeline ID and run ID from the discovery summary.' }
    if ([string]$Build.id -cne $RunId -or [string]$Build.definition.id -cne $PipelineId -or [string]$Build.project.id -ine $ProjectId -or [string]$Build.repository.id -ine $RepositoryId) { throw 'Discovery run belongs to another pipeline, project, or repository.' }
    if ($Build.status -cne 'completed' -or $Build.result -cne 'succeeded' -or $Build.sourceBranch -cne 'refs/heads/main') { throw 'Deployment requires successful discovery from protected main. Feature-branch and incomplete runs are diagnostic only.' }
    $source=$Manifest.source
    if ([string]$source.runId -cne $RunId -or [string]$source.pipelineId -cne $PipelineId -or [string]$source.projectId -ine $ProjectId -or [string]$source.repositoryId -ine $RepositoryId -or $source.branch -cne $Build.sourceBranch -or $source.commit -cne $Build.sourceVersion) { throw 'Manifest provenance differs from the Azure DevOps run record.' }
}
