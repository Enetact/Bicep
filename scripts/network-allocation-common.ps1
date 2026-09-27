#requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-NetworkIntent {
    param([string]$Profile,[string]$Workload,[string]$EnvironmentName,[string]$ConfigPath)
    if($Workload -notin @('blobcopy','eventflow','storage','keyvault','httpapi','busworker') -or $EnvironmentName -notin @('dev','qa','uat','prod')){throw 'Unsupported network workload/environment.'}
    $config=Get-Content -LiteralPath $ConfigPath -Raw|ConvertFrom-Json -AsHashtable
    if($config.schemaVersion -ne 1 -or !$config.profiles.ContainsKey($Profile)){throw 'Unknown allocation profile.'}
    $p=$config.profiles[$Profile]
    if($p.poolId -notmatch '^/subscriptions/([0-9a-fA-F-]{36})/resourceGroups/([a-zA-Z0-9_.()-]+)/providers/Microsoft.Network/networkManagers/([a-zA-Z0-9_.-]+)/ipamPools/([a-zA-Z0-9_.-]+)$'){throw 'Configure an approved existing AVNM IPAM pool ID in config/network-allocation.json.'}
    $parts=$Matches.Clone()
    if($p.subscriptionId -ne $parts[1] -or ![guid]::TryParse($p.tenantId,[ref]([guid]::Empty))){throw 'Pool subscription and tenant must match reviewed profile.'}
    foreach($field in @('routingDomain','owner','costCenter')){if([string]::IsNullOrWhiteSpace($p[$field])){throw "Complete profile setting: $field"}}
    if($p.location -notmatch '^[a-z0-9]+$' -or $p.addressCount -ne 256 -or $p.integrationPrefixLength -ne 26 -or $p.endpointPrefixLength -ne 27){throw 'Only the reviewed IPv4 /24 spoke with /26 integration and /27 endpoint layout is qualified by this adapter.'}
    $payload=[ordered]@{schemaVersion=1;profile=$Profile;workload=$Workload;environment=$EnvironmentName;settings=$p}
    $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes(($payload|ConvertTo-Json -Depth 12 -Compress)))).ToLowerInvariant()
    # Stable allocation identity does not change when profile sizing/configuration changes. Drift must block reuse.
    $key="$($p.tenantId)/$($p.routingDomain)/$Profile/$Workload/$EnvironmentName"
    $suffix=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($key))).ToLowerInvariant().Substring(0,16)
    $name="ps-$Workload-$EnvironmentName-$suffix"
    return [ordered]@{schemaVersion=1;profile=$Profile;workload=$Workload;environment=$EnvironmentName;intentHash=$hash;allocationName=$name;allocationId="$($p.poolId)/staticCidrs/$name";poolResourceGroup=$parts[2];networkManagerName=$parts[3];poolName=$parts[4];description="PlatformStudio:$hash";settings=$p}
}
function Invoke-NetworkRead {
    param([string]$Id,[switch]$AllowMissing)
    if($Id -notmatch '^/subscriptions/[0-9a-fA-F-]{36}/resourceGroups/[a-zA-Z0-9_.()-]+/providers/Microsoft.Network/networkManagers/[a-zA-Z0-9_.-]+/ipamPools/[a-zA-Z0-9_.-]+(?:/staticCidrs/[a-zA-Z0-9_.-]+)?$'){throw 'Invalid IPAM resource path.'}
    $raw=& az rest --method get --url "https://management.azure.com${Id}?api-version=2025-07-01" --only-show-errors --output json 2>&1
    if($LASTEXITCODE -ne 0){
        if($AllowMissing -and ($raw -join "`n") -match '\(ResourceNotFound\)'){return $null}
        throw 'IPAM read failed. Missing permissions/provider/RG or unknown failures are not an empty allocation.'
    }
    return ($raw -join "`n")|ConvertFrom-Json -AsHashtable
}
function Assert-NetworkAllocation {
    param($Intent,$Allocation)
    if(!$Allocation -or $Allocation.id -ine $Intent.allocationId -or $Allocation.properties.description -cne $Intent.description -or $Allocation.properties.provisioningState -ne 'Succeeded'){throw 'Allocation ownership, policy hash or provider completion is not established. Reconcile; never overwrite or release it automatically.'}
    $prefixes=@($Allocation.properties.addressPrefixes)
    if($prefixes.Count -ne 1 -or $prefixes[0] -notmatch '^(\d{1,3})\.(\d{1,3})\.(\d{1,3})\.0/24$'){throw 'Provider allocation is not one canonical IPv4 /24; quarantine for platform review.'}
    foreach($octet in @($Matches[1],$Matches[2],$Matches[3])){if([int]$octet -gt 255){throw 'Provider prefix invalid.'}}
    $base="$($Matches[1]).$($Matches[2]).$($Matches[3])"
    return [ordered]@{addressPrefix=$prefixes[0];integrationPrefix="$base.0/26";endpointPrefix="$base.64/27"}
}
function Get-ReservedNetworkParameters {
    param($Intent,$Allocation)
    $prefixes=Assert-NetworkAllocation $Intent $Allocation
    $suffix=$Intent.allocationName.Substring($Intent.allocationName.Length-16)
    return [ordered]@{location=@{value=$Intent.settings.location};resourceGroupName=@{value="rg-net-$($Intent.workload)-$($Intent.environment)-$suffix"};vnetName=@{value="vnet-$($Intent.workload)-$($Intent.environment)-$suffix"};addressPrefix=@{value=$prefixes.addressPrefix};integrationPrefix=@{value=$prefixes.integrationPrefix};endpointPrefix=@{value=$prefixes.endpointPrefix};allocationId=@{value=$Intent.allocationId};owner=@{value=$Intent.settings.owner};costCenter=@{value=$Intent.settings.costCenter}}
}
