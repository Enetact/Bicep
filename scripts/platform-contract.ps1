# Requires self-service-common.ps1. The target catalog is platform-owned configuration.
function Read-PlatformConfiguration {
    $config=Get-Content (Join-Path (Get-ProjectRoot) config/platform.json) -Raw | ConvertFrom-Json -AsHashtable
    if ($config.schemaVersion -ne 1 -or $config.workloadType -cne 'blob-transfer' -or $config.composition -cne 'workloads/blob-transfer/main.bicep') { throw 'Unsupported platform composition. Add and qualify a real workload implementation before registering it.' }
    if ($config.defaultRegion -cnotin $config.approvedRegions -or @($config.approvedRegions | Where-Object { $_ -cnotmatch '^[a-z0-9]+$' }).Count) { throw 'Invalid approved region configuration.' }
    foreach ($key in @('createDestinationPrivateEndpoints','enableLogAlerts')) { if ($config[$key] -isnot [bool]) { throw "Platform option $key must be boolean." } }
    if ((Get-ValueHash $config.requiredCapabilities) -cne (Get-ValueHash @{storage=$true;observability=$true})) { throw 'Blob transfer requires storage and observability.' }
    return $config
}
function Get-PlatformIntentTargets($Targets, $Configuration=(Read-PlatformConfiguration)) {
    $seen=@{}
    foreach ($target in $Targets) {
        $region=if ($target.parameterOverrides.Contains('location')) { $target.parameterOverrides.location } else { $Configuration.defaultRegion }
        if ($region -cnotin $Configuration.approvedRegions) { throw "Unapproved region: $region" }
        $type=Get-TargetWorkloadType $target
        $null=Get-WorkloadDefinition $type
        $key="$type/$($target.workload)/$($target.environmentName)/$region"
        if ($seen.ContainsKey($key)) { throw "Ambiguous developer intent: $key. Platform must choose one topology/subscription for this intent." }
        $seen[$key]=$true
        if ($type -eq 'logic-app-event-grid') {
            [pscustomobject]@{workloadName=$target.workload;environment=$target.environmentName;region=$region;workloadType=$type;target=$target}; continue
        }
        $mode=if ($target.parameterOverrides.Contains('networkMode')) { $target.parameterOverrides.networkMode } else { 'new' }
        if ($target.enabled -and $mode -ne 'existing') {
            $exceptions=@($Configuration.isolatedNetworkExceptions | Where-Object { $_.workloadName -ceq $target.workload -and $_.environment -ceq $target.environmentName -and $_.region -ceq $region -and $_.reason.Length -ge 20 -and $_.reviewReference })
            if ($exceptions.Count -ne 1) { throw "Enabled enterprise target $key requires existing centrally managed networking, or one documented platform exception." }
        }
        [pscustomobject]@{workloadName=$target.workload;environment=$target.environmentName;region=$region;workloadType=$Configuration.workloadType;target=$target}
    }
}
function Resolve-PlatformRequest($Request, $Targets, $Configuration=(Read-PlatformConfiguration)) {
    $required=@('workloadName','environment','region','workloadType')
    if ($Request -isnot [Collections.IDictionary] -or @($required | Where-Object { !$Request.Contains($_) }).Count -or @($Request.Keys | Where-Object { $_ -notin ($required+@('capabilities')) }).Count) { throw 'Request must contain only workloadName, environment, region, workloadType and optional capabilities.' }
    if ($Request.workloadType -cnotin @('blob-transfer','logic-app-event-grid')) { throw 'Unsupported workload type.' }
    if ($Request.Contains('capabilities') -and $Request.workloadType -ne 'blob-transfer') { throw 'Capabilities are platform controlled.' }
    if ($Request.Contains('capabilities') -and (Get-ValueHash $Request.capabilities) -cne (Get-ValueHash $Configuration.requiredCapabilities)) { throw 'Unsupported capability selection; this composition requires storage and observability.' }
    $matches=@(Get-PlatformIntentTargets $Targets $Configuration | Where-Object { $_.workloadType -ceq $Request.workloadType -and $_.workloadName -ceq $Request.workloadName -and $_.environment -ceq $Request.environment -and $_.region -ceq $Request.region })
    if ($matches.Count -ne 1) { throw 'Request does not identify an approved workload/environment/region.' }
    return @{schemaVersion=1;request=$Request;target=$matches[0].target;deploymentEnabled=$matches[0].target.enabled;composition=(Get-WorkloadDefinition $Request.workloadType).composition;capabilities=$(if($Request.workloadType -eq 'blob-transfer'){$Configuration.requiredCapabilities}else{@{storage=$true;observability=$true;workflows=$true;events=$true}});region=$matches[0].region;options=$(if($Request.workloadType -eq 'blob-transfer'){@{createDestinationPrivateEndpoints=$Configuration.createDestinationPrivateEndpoints;enableLogAlerts=$Configuration.enableLogAlerts}}else{@{privateEndpointCount=8;enableLogAlerts=$true}})}
}
