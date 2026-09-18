#requires -Version 7.4
[CmdletBinding()]
param()
. "$PSScriptRoot/self-service-common.ps1"
$testRoot=Join-Path (Get-ProjectRoot) ('artifacts/discovery-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$results=[Collections.Generic.List[object]]::new()
function Check([bool]$Condition) { if (!$Condition) { throw 'Assertion failed.' } }
function Reject([scriptblock]$Body) { $caught=$false; try { & $Body | Out-Null } catch { $caught=$true }; Check $caught }
function Case([string]$Name,[scriptblock]$Body) { try { & $Body; $results.Add(@{name=$Name;passed=$true}) } catch { throw "Case '$Name': $($_.Exception.Message)" } }
function Clone($Value) { ConvertFrom-Json ($Value | ConvertTo-Json -Depth 100) -AsHashtable }
$sub='11111111-1111-1111-1111-111111111111'
$vnetId="/subscriptions/$sub/resourceGroups/network/providers/Microsoft.Network/virtualNetworks/shared"
$network=@{integrationSubnetId="$vnetId/subnets/functions";privateEndpointSubnetId="$vnetId/subnets/endpoints";privateDnsZoneIds=@{}}
foreach ($key in @('blob','queue','table','dfs','web')) {
    $zone=if ($key -eq 'web') {'privatelink.azurewebsites.net'} else {"privatelink.$key.core.windows.net"}
    $network.privateDnsZoneIds[$key]="/subscriptions/$sub/resourceGroups/dns/providers/Microsoft.Network/privateDnsZones/$zone"
}
$target=@{schemaVersion=2;enabled=$false;workload='blobcopy';environmentName='dev';subscriptionId=$sub;subscriptionAlias='sandbox';networkProfile='shared';parameterFile='environments/dev.bicepparam';resourceGroup='rg-blobcopy-dev-acme-eus2-001';serviceConnection='sc-blobcopy-dev-acme-eus2-001';agentPool='private';deploymentEnvironment='blobcopy-dev-acme-eus2-001';smokePrefix='smoke/';parameterOverrides=@{networkMode='existing';existingNetwork=$network;location='eastus2';namingSuffix='acme-eus2-001';deploymentPrincipalObjectId='22222222-2222-2222-2222-222222222222'}}
$p=@{}; $values=@{workload='blobcopy';environmentName='dev';owner='team';costCenter='CC1';destinationSubscriptionId=$sub;destinationResourceGroupName='lake';destinationStorageAccountName='lakeaccount';destinationContainerName='incoming';destinationIsHnsEnabled=$true}
foreach ($key in $values.Keys) { $p[$key]=@{value=$values[$key]} }
Set-ServiceProfileParameters $target $p
Case 'disabled target permits discovery but not deployment' { Assert-ServiceTarget $target blobcopy dev -AllowDisabled; Reject { Assert-ServiceTarget $target blobcopy dev } }
Case 'placeholder is permitted only for explicit discovery' { $t=Clone $target; $t.subscriptionId=[guid]::Empty.ToString(); $t.subscriptionAlias='unconfigured'; Reject { Assert-ServiceTarget $t blobcopy dev -AllowDisabled }; Assert-ServiceTarget $t blobcopy dev -AllowDisabled -AllowDiscoveryPlaceholder }
Case 'enabled placeholder cannot bypass deployment validation' { $t=Clone $target; $t.subscriptionId=[guid]::Empty.ToString(); $t.subscriptionAlias='unconfigured'; $t.enabled=$true; Reject { Assert-ServiceTarget $t blobcopy dev -AllowDisabled -AllowDiscoveryPlaceholder } }
Case 'profile overlays existing network and standardized name without CIDRs' { Assert-ServiceParameters $target $p; Check ((Get-ServiceStem $p) -ceq 'blobcopy-dev-acme-eus2-001') }
Case 'unknown override cannot bypass target policy' { $t=Clone $target; $t.parameterOverrides.owner='injected'; Reject { Assert-ServiceTarget $t blobcopy dev -AllowDisabled } }
Case 'same subnet rejected' { $n=Clone $network; $n.privateEndpointSubnetId=$n.integrationSubnetId; Reject { Assert-ServiceNetworkIds $target $n } }
Case 'cross-subscription subnet rejected' { $n=Clone $network; $n.integrationSubnetId=$n.integrationSubnetId.Replace($sub,'33333333-3333-3333-3333-333333333333'); Reject { Assert-ServiceNetworkIds $target $n } }
Case 'different VNet rejected' { $n=Clone $network; $n.privateEndpointSubnetId=$n.privateEndpointSubnetId.Replace('/shared/','/other/'); Reject { Assert-ServiceNetworkIds $target $n } }
Case 'incorrect DNS zone type rejected' { $n=Clone $network; $n.privateDnsZoneIds.blob=$n.privateDnsZoneIds.queue; Reject { Assert-ServiceNetworkIds $target $n } }
Case 'malformed naming rejected' { $bad=Clone $p; $bad.namingSuffix.value='ACME-eastus-001'; Reject { Assert-ServiceParameters $target $bad } }
Case 'legacy naming preserved' { $old=Clone $p; $old.Remove('namingSuffix'); Check ((Get-ServiceStem $old) -eq 'blobcopy-dev') }

$catalog=Join-Path $testRoot catalog; $generated=Join-Path $testRoot generated
Write-ServiceJson $target (Join-Path $catalog target.json)
Case 'catalog generation and freshness check' { & "$PSScriptRoot/Update-ServiceCatalog.ps1" -CatalogDirectory $catalog -OutputRoot $generated; & "$PSScriptRoot/Update-ServiceCatalog.ps1" -CatalogDirectory $catalog -OutputRoot $generated -Check }
Case 'catalog passes literal protected resources through stage parameters' {
    $routing=Get-Content (Join-Path $generated pipelines/catalog-bindings.yml) -Raw
    Check ($routing.Contains("serviceConnection: $($target.serviceConnection)") -and $routing.Contains("agentPool: $($target.agentPool)") -and $routing.Contains("deploymentEnvironment: $($target.deploymentEnvironment)"))
    Check ($routing.Contains('stages:') -and !$routing.Contains('variables:') -and $routing.Contains('stage: InvalidSelection'))
}
Case 'discovery task and script receive the same explicit connection parameter' {
    $template=Get-Content (Join-Path (Get-ProjectRoot) pipelines/templates/self-service-discover.yml) -Raw
    Check ($template.Contains('azureSubscription: ${{ parameters.serviceConnection }}') -and $template.Contains('-BoundServiceConnection ''${{ parameters.serviceConnection }}''') -and !$template.Contains('variables.serviceConnection'))
    Check ($template.IndexOf('Prepare evidence before Azure authentication') -lt $template.IndexOf('task: AzureCLI@2'))
}
Case 'deployment forwards explicit resources and relative nested template paths' {
    $template=Get-Content (Join-Path (Get-ProjectRoot) pipelines/templates/self-service-stages.yml) -Raw
    foreach ($key in @('serviceConnection','agentPool','deploymentEnvironment')) { Check (!$template.Contains("variables.$key") -and $template.Contains('parameters.'+$key)) }
    Check ($template.Contains('template: self-service-plan.yml') -and $template.Contains('template: self-service-apply.yml') -and !$template.Contains('template: pipelines/templates/'))
}
Case 'duplicate target cannot select arbitrary credentials' { Write-ServiceJson $target (Join-Path $catalog duplicate.json); Reject { & "$PSScriptRoot/Update-ServiceCatalog.ps1" -CatalogDirectory $catalog -OutputRoot $generated }; Remove-Item -LiteralPath (Join-Path $catalog duplicate.json) }
Case 'one dropdown name cannot map to multiple subscriptions' { $t=Clone $target; $t.subscriptionId='33333333-3333-3333-3333-333333333333'; $t.environmentName='qa'; Write-ServiceJson $t (Join-Path $catalog ambiguous.json); Reject { & "$PSScriptRoot/Update-ServiceCatalog.ps1" -CatalogDirectory $catalog -OutputRoot $generated }; Remove-Item -LiteralPath (Join-Path $catalog ambiguous.json) }

# Mock the az executable boundary, so the discovery entrypoint itself is tested.
# Every unrecognized command fails: the script cannot silently mutate Azure.
$global:BlobTransferDiscoveryTestState=@{calls=[Collections.Generic.List[string]]::new();badNetwork='';duplicateNames=$false;subscription=$sub;network=$network;vnetId=$vnetId;accountState='Enabled'}
function az {
    $sub=$global:BlobTransferDiscoveryTestState.subscription; $network=$global:BlobTransferDiscoveryTestState.network; $vnetId=$global:BlobTransferDiscoveryTestState.vnetId
    $arguments=@($args); $cmd=$arguments -join ' '; $global:BlobTransferDiscoveryTestState.calls.Add($cmd); $global:LASTEXITCODE=0
    $answer=switch -Regex ($cmd) {
        '^account list ' { if ($global:BlobTransferDiscoveryTestState.duplicateNames) { ,@(@{name='Sandbox';state='Enabled';id=$sub},@{name='Sandbox';state='Enabled';id='other'}) } else { ,@(@{name='Sandbox';state='Enabled';id=$sub}) }; break }
        '^account show ' { @{id=$sub;name='Sandbox';state=$global:BlobTransferDiscoveryTestState.accountState;tenantId='tenant'}; break }
        '^network vnet list ' { ,@(@{id=$vnetId;name='shared';resourceGroup='network';location='eastus2'}); break }
        '^network vnet subnet list ' { ,@(@{id=$network.integrationSubnetId;name='functions';addressPrefix='10.2.0.0/26';delegations=@(@{serviceName='Microsoft.Web/serverFarms'});privateEndpointNetworkPolicies='Enabled'},@{id=$network.privateEndpointSubnetId;name='endpoints';addressPrefix='10.2.1.0/26';delegations=@();privateEndpointNetworkPolicies='Disabled'}); break }
        '^network private-dns zone list ' { ,@($network.privateDnsZoneIds.Keys | ForEach-Object { @{id=$network.privateDnsZoneIds[$_];name=($network.privateDnsZoneIds[$_] -split '/')[-1];resourceGroup='dns'} }); break }
        '^rest .*Microsoft.Authorization/permissions' { @{value=@(@{actions=@('*/read');notActions=@()})}; break }
        '^devops service-endpoint list ' {
            if (!$cmd.Contains('--query')) { throw 'Unprojected endpoint data must never be requested.' }
            ,@(@{id='44444444-4444-4444-4444-444444444444';name='safe';ready=$true;subscriptionId=$sub;tenantId='tenant';applicationId='application';scheme='WorkloadIdentityFederation'},@{id='wrong';name='other-subscription';ready=$true;subscriptionId='other';tenantId='tenant';applicationId='application';scheme='WorkloadIdentityFederation'}); break
        }
        '^ad sp show ' { '22222222-2222-2222-2222-222222222222'; break }
        '^resource show ' {
            $id=$arguments[[Array]::IndexOf($arguments,'--ids')+1]
            if ($id -eq $vnetId) { @{location=$(if ($global:BlobTransferDiscoveryTestState.badNetwork -eq 'region') {'westus'} else {'eastus2'})} }
            elseif ($id -eq $network.integrationSubnetId) { @{properties=@{addressPrefix=$(if ($global:BlobTransferDiscoveryTestState.badNetwork -eq 'size') {'10.2.0.0/28'} else {'10.2.0.0/26'});delegations=@(@{properties=@{serviceName=$(if ($global:BlobTransferDiscoveryTestState.badNetwork -eq 'delegation') {'wrong'} else {'Microsoft.Web/serverFarms'})}})}} }
            elseif ($id -eq $network.privateEndpointSubnetId) { @{properties=@{addressPrefix='10.2.1.0/26';delegations=@();privateEndpointNetworkPolicies=$(if ($global:BlobTransferDiscoveryTestState.badNetwork -eq 'policy') {'Enabled'} else {'Disabled'})}} }
            else { @{id=$id} }; break
        }
        '^rest .*virtualNetworkLinks' { @{value=$(if ($global:BlobTransferDiscoveryTestState.badNetwork -eq 'dns') {@()} else {@(@{properties=@{virtualNetwork=@{id=$vnetId};provisioningState='Succeeded'}})})}; break }
        default { throw "Unexpected Azure call in read-only fixture: $cmd" }
    }
    ConvertTo-Json -InputObject $answer -Depth 100 -Compress
}
try {
    $fixture=Join-Path $testRoot connection-context
    New-Item -ItemType Directory -Path (Join-Path $fixture scripts) -Force | Out-Null
    foreach ($file in @('common.ps1','self-service-common.ps1','Export-DeploymentInventory.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $fixture scripts) }
    $placeholder=Clone $target; $placeholder.subscriptionId=[guid]::Empty.ToString(); $placeholder.subscriptionAlias='unconfigured'; $placeholder.serviceConnection='SC-AZ-A-Bicep'
    $profilePath=Join-Path $fixture self-service/targets/placeholder.json
    Write-ServiceJson $placeholder $profilePath
    $contextArgs=@{Workload='blobcopy';EnvironmentName='dev';SubscriptionAlias='unconfigured';NetworkProfile='shared';UseServiceConnectionSubscription=$true;BoundServiceConnection='SC-AZ-A-Bicep';OutputDirectory=(Join-Path $fixture evidence)}
    $entry=Join-Path $fixture scripts/Export-DeploymentInventory.ps1
    Case 'service connection discovers its active subscription from a disabled placeholder' {
        $global:BlobTransferDiscoveryTestState.calls.Clear()
        & $entry @contextArgs
        $r=Get-Content (Join-Path $fixture evidence/inventory.json) -Raw | ConvertFrom-Json
        Check ($r.subscription.id -eq $sub -and $r.subscriptionSource -eq 'service-connection-context' -and $r.boundServiceConnection -eq 'SC-AZ-A-Bicep')
        Check (!@($global:BlobTransferDiscoveryTestState.calls | Where-Object { $_ -match '^account list |^account set ' }).Count)
        Check (!@($global:BlobTransferDiscoveryTestState.calls | Where-Object { $_ -match '^network ' -and !$_.Contains("--subscription $sub") }).Count)
        Check ((Get-Content $profilePath -Raw | ConvertFrom-Json).subscriptionId -eq [guid]::Empty.ToString())
    }
    Case 'connection context needs an explicit YAML binding' {
        $bad=Clone $contextArgs; $bad.BoundServiceConnection=''; $global:BlobTransferDiscoveryTestState.calls.Clear()
        Reject { & $entry @bad }; Check ($global:BlobTransferDiscoveryTestState.calls.Count -eq 0)
    }
    Case 'wrong service connection fails before discovery' {
        $bad=Clone $contextArgs; $bad.BoundServiceConnection='another'; $global:BlobTransferDiscoveryTestState.calls.Clear()
        Reject { & $entry @bad }; Check ($global:BlobTransferDiscoveryTestState.calls.Count -eq 0)
    }
    Case 'registered subscription mismatch cannot fall back to another subscription' {
        $configured=Clone $placeholder; $configured.subscriptionId='33333333-3333-3333-3333-333333333333'; Write-ServiceJson $configured $profilePath
        $global:BlobTransferDiscoveryTestState.calls.Clear(); Reject { & $entry @contextArgs }
        Check (!@($global:BlobTransferDiscoveryTestState.calls | Where-Object { $_ -match '^network ' }).Count)
        Write-ServiceJson $placeholder $profilePath
    }
    Case 'disabled service connection subscription is rejected before inventory' {
        $global:BlobTransferDiscoveryTestState.accountState='Disabled'; $global:BlobTransferDiscoveryTestState.calls.Clear()
        try { Reject { & $entry @contextArgs }; Check (!@($global:BlobTransferDiscoveryTestState.calls | Where-Object { $_ -match '^network ' }).Count) }
        finally { $global:BlobTransferDiscoveryTestState.accountState='Enabled' }
    }
    Case 'discovery by subscription name reads only selected subscription' {
        & "$PSScriptRoot/Export-DeploymentInventory.ps1" -SubscriptionName Sandbox -OutputDirectory (Join-Path $testRoot by-name)
        $r=Get-Content (Join-Path $testRoot by-name/inventory.json) -Raw | ConvertFrom-Json
        Check ($r.readOnly -and $r.subscription.id -eq $sub -and $r.networks[0].subnets[0].integrationCandidate)
        Check (!@($global:BlobTransferDiscoveryTestState.calls | Where-Object { $_ -match '^network ' -and !$_.Contains("--subscription $sub") }).Count)
    }
    Case 'duplicate subscription names rejected before network discovery' { $global:BlobTransferDiscoveryTestState.duplicateNames=$true; $global:BlobTransferDiscoveryTestState.calls.Clear(); Reject { & "$PSScriptRoot/Export-DeploymentInventory.ps1" -SubscriptionName Sandbox -OutputDirectory (Join-Path $testRoot ambiguous) }; Check (!@($global:BlobTransferDiscoveryTestState.calls | Where-Object { $_ -match '^network ' }).Count); $global:BlobTransferDiscoveryTestState.duplicateNames=$false }
    Case 'service connection discovery filters subscription and resolves principal object ID' {
        & "$PSScriptRoot/Export-DeploymentInventory.ps1" -SubscriptionId $sub -OrganizationUrl https://dev.azure.com/example -Project Example -OutputDirectory (Join-Path $testRoot with-devops)
        $r=Get-Content (Join-Path $testRoot with-devops/inventory.json) -Raw | ConvertFrom-Json
        Check ($r.serviceConnections.Count -eq 1 -and $r.serviceConnections[0].principalObjectId -eq '22222222-2222-2222-2222-222222222222')
        Check (!(Get-Content (Join-Path $testRoot with-devops/inventory.json) -Raw).Contains('"authorization":'))
    }
    $bundle=@{target=$target;parameters=@{parameters=$p}}
    $register=@{InventoryPath=(Join-Path $testRoot with-devops/inventory.json);Workload='blobcopy';EnvironmentName='dev';SubscriptionAlias='sandbox';NetworkProfile='shared';IntegrationSubnetId=$network.integrationSubnetId;PrivateEndpointSubnetId=$network.privateEndpointSubnetId;ServiceConnectionId='44444444-4444-4444-4444-444444444444';OrganizationCode='acme';RegionCode='eus2';OutputDirectory=(Join-Path $testRoot registered)}
    Case 'registration derives disabled target names and mapped identity' {
        & "$PSScriptRoot/New-ServiceTarget.ps1" @register
        $registered=Get-Content (Join-Path $testRoot registered/blobcopy.dev.sandbox.shared.json) -Raw | ConvertFrom-Json
        Check (!$registered.enabled -and $registered.resourceGroup -eq 'rg-blobcopy-dev-acme-eus2-001' -and $registered.parameterOverrides.deploymentPrincipalObjectId -eq '22222222-2222-2222-2222-222222222222')
    }
    Case 'registration refuses to overwrite approved target' { Reject { & "$PSScriptRoot/New-ServiceTarget.ps1" @register } }
    Case 'registration refuses a mismatched principal' { $bad=Clone $register; $bad.DeploymentPrincipalObjectId='55555555-5555-5555-5555-555555555555'; Reject { & "$PSScriptRoot/New-ServiceTarget.ps1" @bad } }
    Case 'existing network preflight accepts qualified subnets and linked zones' { $state=Test-ServiceNetwork $bundle; Check ($state.vnetId -eq $vnetId -and $state.zones.Count -eq 5) }
    foreach ($reason in @('region','size','delegation','policy','dns')) {
        Case "existing network rejects $reason mismatch" { $global:BlobTransferDiscoveryTestState.badNetwork=$reason; Reject { Test-ServiceNetwork $bundle }; $global:BlobTransferDiscoveryTestState.badNetwork='' }
    }
} finally { Remove-Item Function:az; Remove-Variable BlobTransferDiscoveryTestState -Scope Global }
Write-ServiceJson @{passed=$results.Count;failed=0;azureCalls='mocked only';cases=$results} (Join-Path $testRoot results.json)
Write-Host "PASS: $($results.Count) catalog/discovery/network cases. No Azure calls. Evidence: $testRoot"
