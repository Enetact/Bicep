#requires -Version 7.4
[CmdletBinding()]
param()
. "$PSScriptRoot/self-service-common.ps1"
. "$PSScriptRoot/discovery-manifest-common.ps1"
$testRoot=Join-Path (Get-ProjectRoot) ('artifacts/discovery-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force | Out-Null
$results=[Collections.Generic.List[object]]::new()
function Check([bool]$Condition) { if (!$Condition) { throw 'Assertion failed.' } }
function Reject([scriptblock]$Body) { $caught=$false; try { & $Body | Out-Null } catch { $caught=$true }; Check $caught }
function Case([string]$Name,[scriptblock]$Body) { try { & $Body; $results.Add(@{name=$Name;passed=$true}) } catch { throw "Case '$Name': $($_.Exception.Message)" } }
function Clone($Value) { ConvertFrom-Json ($Value | ConvertTo-Json -Depth 100) -AsHashtable }
Case 'organization URL normalization accepts current and legacy collection forms' {
    foreach ($pair in @(@('https://dev.azure.com/example','https://dev.azure.com/example/'),@(' https://example.visualstudio.com/ ','https://example.visualstudio.com/'),@('https://example.visualstudio.com/DefaultCollection','https://example.visualstudio.com/DefaultCollection/'),@('HTTPS://DEV.AZURE.COM/Example/','HTTPS://DEV.AZURE.COM/Example/'))) {
        Check ((Resolve-ServiceOrganizationUrl $pair[0]) -ceq $pair[1])
    }
}
Case 'organization URL validation rejects non-organization credential destinations' {
    foreach ($value in @('', 'http://dev.azure.com/example/', 'https://dev.azure.com.example.com/example/', 'https://example.visualstudio.com.evil.test/', 'https://user:secret@dev.azure.com/example/', 'https://dev.azure.com/example/?token=secret', 'https://dev.azure.com/example/#fragment', 'https://dev.azure.com/example/project', 'https://example.visualstudio.com/unexpected/', 'https://dev.azure.com:8443/example/', 'https://dev.azure.com/example/../other', '$(System.CollectionUri)')) {
        Reject { Resolve-ServiceOrganizationUrl $value }
    }
}
$sub='11111111-1111-1111-1111-111111111111'
$vnetId="/subscriptions/$sub/resourceGroups/network/providers/Microsoft.Network/virtualNetworks/shared"
$network=@{integrationSubnetId="$vnetId/subnets/functions";privateEndpointSubnetId="$vnetId/subnets/endpoints";privateDnsZoneIds=@{}}
foreach ($key in @('blob','queue','table','dfs','web')) {
    $zone=if ($key -eq 'web') {'privatelink.azurewebsites.net'} else {"privatelink.$key.core.windows.net"}
    $network.privateDnsZoneIds[$key]="/subscriptions/$sub/resourceGroups/dns/providers/Microsoft.Network/privateDnsZones/$zone"
}
$target=@{schemaVersion=2;enabled=$false;workload='blobcopy';environmentName='dev';subscriptionId=$sub;subscriptionAlias='sandbox';networkProfile='shared';parameterFile='workloads/blob-transfer/environments/main.dev.bicepparam';resourceGroup='rg-blobcopy-dev-acme-eus2-001';serviceConnection='sc-blobcopy-dev-acme-eus2-001';agentPool='private';deploymentEnvironment='blobcopy-dev-acme-eus2-001';smokePrefix='smoke/';parameterOverrides=@{networkMode='existing';existingNetwork=$network;location='eastus2';namingSuffix='acme-eus2-001';deploymentPrincipalObjectId='22222222-2222-2222-2222-222222222222'}}
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
Case 'disabled catalog routes deploy to hosted setup without private resources' {
    $routing=Get-Content (Join-Path $generated pipelines/catalog-bindings.yml) -Raw
    Check ($routing.Contains("serviceConnection: $($target.serviceConnection)") -and !$routing.Contains('agentPool:') -and !$routing.Contains('deploymentEnvironment:'))
    Check ($routing.Contains('template: templates/self-service-setup.yml') -and !$routing.Contains('template: templates/self-service-stages.yml'))
    Check ($routing.Contains('stages:') -and !$routing.Contains('variables:') -and $routing.Contains('stage: InvalidSelection'))
}
Case 'enabled catalog restores exact private deployment bindings' {
    $enabledCatalog=Join-Path $testRoot enabled-catalog; $enabledOutput=Join-Path $testRoot enabled-output
    $enabledTarget=Clone $target; $enabledTarget.enabled=$true
    Write-ServiceJson $enabledTarget (Join-Path $enabledCatalog target.json)
    & "$PSScriptRoot/Update-ServiceCatalog.ps1" -CatalogDirectory $enabledCatalog -OutputRoot $enabledOutput
    $routing=Get-Content (Join-Path $enabledOutput pipelines/catalog-bindings.yml) -Raw
    Check ($routing.Contains('template: templates/self-service-stages.yml') -and !$routing.Contains('template: templates/self-service-setup.yml'))
    foreach ($key in @('serviceConnection','agentPool','deploymentEnvironment')) { Check ($routing.Contains("${key}: $($target[$key])")) }
    $publication=(Read-StackConfiguration).templateSpec
    foreach ($key in @('publisherServiceConnection','publisherAgentPool','publisherEnvironment')) { Check ($routing.Contains("${key}: $($publication[$key])")) }
}
Case 'hosted setup has no Azure deployment or protected execution resources' {
    $template=Get-Content (Join-Path (Get-ProjectRoot) pipelines/templates/self-service-setup.yml) -Raw
    Check ($template.Contains('vmImage: windows-latest') -and $template.Contains('stage: SetupOnly') -and $template.Contains('if ($target.enabled)'))
    foreach ($text in @('AzureCLI@','deployment:','Invoke-SelfService.ps1','self-service-apply.yml','name: blob-transfer-private','environment: ${{')) { Check (!$template.Contains($text)) }
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
$global:BlobTransferDiscoveryTestState=@{calls=[Collections.Generic.List[string]]::new();badNetwork='';duplicateNames=$false;subscription=$sub;network=$network;vnetId=$vnetId;accountState='Enabled';dnsFailure=$false;diagnosticFailure=$false;emptyDns=$false;emptyNetwork=$false;emptySubnets=$false;networkFailure=$false;subnetFailure=$false;connectionFailure=$false;emptyConnections=$false}
function az {
    $sub=$global:BlobTransferDiscoveryTestState.subscription; $network=$global:BlobTransferDiscoveryTestState.network; $vnetId=$global:BlobTransferDiscoveryTestState.vnetId
    $arguments=@($args); $cmd=$arguments -join ' '; $global:BlobTransferDiscoveryTestState.calls.Add($cmd); $global:LASTEXITCODE=0
    if (($global:BlobTransferDiscoveryTestState.dnsFailure -and $cmd -match '^network private-dns zone list ') -or
        (!$global:BlobTransferDiscoveryTestState.dnsFallbackSucceeds -and $cmd -match '^resource list ') -or
        ($global:BlobTransferDiscoveryTestState.diagnosticFailure -and $cmd -match '^provider show |^rest .*subscriptions/[^/]+\?') -or
        ($global:BlobTransferDiscoveryTestState.networkFailure -and $cmd -match '^network vnet list ') -or
        ($global:BlobTransferDiscoveryTestState.subnetFailure -and $cmd -match '^network vnet subnet list ') -or
        ($global:BlobTransferDiscoveryTestState.connectionFailure -and $cmd -match '^devops service-endpoint list ')) {
        $global:LASTEXITCODE=1
        return
    }
    $answer=switch -Regex ($cmd) {
        '^account list ' { if ($global:BlobTransferDiscoveryTestState.duplicateNames) { ,@(@{name='Sandbox';state='Enabled';id=$sub},@{name='Sandbox';state='Enabled';id='other'}) } else { ,@(@{name='Sandbox';state='Enabled';id=$sub}) }; break }
        '^account show ' { @{id=$sub;name='Sandbox';state=$global:BlobTransferDiscoveryTestState.accountState;tenantId='tenant'}; break }
        '^network vnet list ' { if ($global:BlobTransferDiscoveryTestState.emptyNetwork) { ,@() } else { ,@(@{id=$vnetId;name='shared';resourceGroup='network';location='eastus2'}) }; break }
        '^network vnet subnet list ' { if ($global:BlobTransferDiscoveryTestState.emptySubnets) { ,@() } else { ,@(@{id=$network.integrationSubnetId;name='functions';addressPrefix='10.2.0.0/26';delegations=@(@{serviceName='Microsoft.Web/serverFarms'});privateEndpointNetworkPolicies='Enabled'},@{id=$network.privateEndpointSubnetId;name='endpoints';addressPrefix='10.2.1.0/26';delegations=@();privateEndpointNetworkPolicies='Disabled'}) }; break }
        '^network private-dns zone list ' { if ($global:BlobTransferDiscoveryTestState.emptyDns) { ,@() } else { ,@($network.privateDnsZoneIds.Keys | ForEach-Object { @{id=$network.privateDnsZoneIds[$_];name=($network.privateDnsZoneIds[$_] -split '/')[-1];resourceGroup='dns'} }) }; break }
        '^resource list ' {
            Check ($cmd -eq "resource list --subscription $sub --resource-type Microsoft.Network/privateDnsZones --output json --only-show-errors")
            if ($global:BlobTransferDiscoveryTestState.emptyDns) { ,@() } else { ,@($network.privateDnsZoneIds.Keys | ForEach-Object { @{id=$network.privateDnsZoneIds[$_];name=($network.privateDnsZoneIds[$_] -split '/')[-1];resourceGroup='dns'} }) }; break
        }
        '^rest .*subscriptions/[^/]+\?' { @{subscriptionId=$sub;displayName='Sandbox';state='Enabled'}; break }
        '^provider show ' { Check ($cmd.Contains("--subscription $sub") -and $cmd.Contains('--namespace Microsoft.Network') -and $cmd.Contains('--query')); @{namespace='Microsoft.Network';registrationState='Registered';privateDnsResourceTypes=@(@{resourceType='privateDnsZones';apiVersions=@('2024-06-01');locations=@('global')})}; break }
        '^rest .*Microsoft.Authorization/permissions' { @{value=@(@{actions=@('*/read');notActions=@()})}; break }
        '^devops service-endpoint list ' {
            if (!$cmd.Contains('--query')) { throw 'Unprojected endpoint data must never be requested.' }
            if ($global:BlobTransferDiscoveryTestState.emptyConnections) { ,@(); break }
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
$global:BlobTransferDiscoveryTestState.dnsFallbackSucceeds=$false
try {
    $fixture=Join-Path $testRoot connection-context
    New-Item -ItemType Directory -Path (Join-Path $fixture scripts) -Force | Out-Null
    foreach ($file in @('common.ps1','self-service-common.ps1','what-if-governance.ps1','stack-service-common.ps1','workload-common.ps1','logicapp-service-common.ps1','logic-prerequisites-common.ps1','Export-DeploymentInventory.ps1')) { Copy-Item -LiteralPath (Join-Path $PSScriptRoot $file) -Destination (Join-Path $fixture scripts) }
    $placeholder=Clone $target; $placeholder.subscriptionId=[guid]::Empty.ToString(); $placeholder.subscriptionAlias='unconfigured'; $placeholder.serviceConnection='SC-AZ-A-Bicep'
    $profilePath=Join-Path $fixture self-service/targets/placeholder.json
    Write-ServiceJson $placeholder $profilePath
    $pipelineSettings=Get-Content (Join-Path (Get-ProjectRoot) self-service/pipeline-settings.json) -Raw|ConvertFrom-Json -AsHashtable
    Write-ServiceJson $pipelineSettings (Join-Path $fixture self-service/pipeline-settings.json)
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
        $m=Get-Content (Join-Path $fixture evidence/manifest.json) -Raw | ConvertFrom-Json
        Check ($m.inventorySha256 -eq (Get-ServiceHash (Join-Path $fixture evidence/inventory.json)) -and $m.subscriptionId -eq $sub)
    }
    foreach($binding in @(@($pipelineSettings.workloadDiscoveryPipelineNames['blob-transfer'],'azure-pipelines-blobcopy-deploy.yml'),@($pipelineSettings.discoveryPipelineName,'azure-pipelines-self-service-deploy.yml'))){
        Case "discovery summary links matching deployment menu for $($binding[0])" {
            $savedBuildFlag=$env:TF_BUILD;$savedDefinition=$env:BUILD_DEFINITIONNAME
            try{
                $env:TF_BUILD='True';$env:BUILD_DEFINITIONNAME=$binding[0]
                & $entry @contextArgs
                Check ((Get-Content (Join-Path $fixture evidence/summary.md) -Raw).Contains("Deploy pipeline ($($binding[1]))"))
            }finally{$env:TF_BUILD=$savedBuildFlag;$env:BUILD_DEFINITIONNAME=$savedDefinition}
        }
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
    Case 'legacy organization URLs reach projected endpoint discovery' {
        foreach ($org in @('https://example.visualstudio.com','https://example.visualstudio.com/DefaultCollection/','https://enetactgames.visualstudio.com/')) {
            $global:BlobTransferDiscoveryTestState.calls.Clear()
            $project=if ($org -eq 'https://enetactgames.visualstudio.com/') { 'Enetact' } else { 'Example' }
            & "$PSScriptRoot/Export-DeploymentInventory.ps1" -SubscriptionId $sub -OrganizationUrl $org -Project $project -OutputDirectory (Join-Path $testRoot legacy-organization)
            $r=Get-Content (Join-Path $testRoot legacy-organization/inventory.json) -Raw | ConvertFrom-Json
            Check ($r.discoveryStatus -eq 'Complete' -and $r.serviceConnectionQuery.status -eq 'Succeeded' -and $r.serviceConnections.Count -eq 1)
            Check (@($global:BlobTransferDiscoveryTestState.calls | Where-Object { $_.Contains('--organization '+(Resolve-ServiceOrganizationUrl $org)+" --project $project") }).Count -eq 1)
        }
    }
    Case 'missing or invalid optional DevOps settings preserve complete Azure inventory' {
        foreach ($settings in @(@{OrganizationUrl='https://dev.azure.com/example';Project=''},@{OrganizationUrl='';Project='Example'},@{OrganizationUrl='https://unexpected.invalid';Project='Example'})) {
            $global:BlobTransferDiscoveryTestState.calls.Clear()
            & "$PSScriptRoot/Export-DeploymentInventory.ps1" -SubscriptionId $sub @settings -OutputDirectory (Join-Path $testRoot invalid-devops)
            $r=Get-Content (Join-Path $testRoot invalid-devops/inventory.json) -Raw | ConvertFrom-Json
            $m=Get-Content (Join-Path $testRoot invalid-devops/manifest.json) -Raw | ConvertFrom-Json
            Check ($r.discoveryStatus -eq 'Complete' -and $r.serviceConnectionQuery.status -eq 'Failed' -and $r.serviceConnectionQuery.error -and $r.networks.Count -eq 1 -and $r.privateDnsZones.Count -eq 5)
            Check ($m.inventorySha256 -eq (Get-ServiceHash (Join-Path $testRoot invalid-devops/inventory.json)) -and $global:LASTEXITCODE -eq 0)
            Check (!@($global:BlobTransferDiscoveryTestState.calls | Where-Object { $_ -match '^devops |^ad ' }).Count)
        }
    }
    Case 'DNS failure plus invalid optional DevOps settings still saves partial diagnostics' {
        $global:BlobTransferDiscoveryTestState.dnsFailure=$true
        try {
            $message=''
            try { & "$PSScriptRoot/Export-DeploymentInventory.ps1" -SubscriptionId $sub -OrganizationUrl 'https://unexpected.invalid' -Project Example -OutputDirectory (Join-Path $testRoot combined-failure) }
            catch { $message=$_.Exception.Message }
            Check ($message.StartsWith('Network discovery is incomplete.'))
            $r=Get-Content (Join-Path $testRoot combined-failure/inventory.json) -Raw | ConvertFrom-Json
            $m=Get-Content (Join-Path $testRoot combined-failure/manifest.json) -Raw | ConvertFrom-Json
            Check ($r.privateDnsQuery.status -eq 'Failed' -and $r.serviceConnectionQuery.status -eq 'Failed' -and $r.diagnostics.subscriptionArm.status -eq 'Succeeded' -and $r.networks.Count -eq 1)
            Check ($m.discoveryStatus -eq 'Partial' -and $m.inventorySha256 -eq (Get-ServiceHash (Join-Path $testRoot combined-failure/inventory.json)))
            Check (Test-Path (Join-Path $testRoot combined-failure/summary.md))
        } finally { $global:BlobTransferDiscoveryTestState.dnsFailure=$false }
    }
    Case 'optional connection failure reports unknown without leaking a native failure exit code' {
        $global:BlobTransferDiscoveryTestState.connectionFailure=$true
        try {
            & "$PSScriptRoot/Export-DeploymentInventory.ps1" -SubscriptionId $sub -OrganizationUrl https://dev.azure.com/example -Project Example -OutputDirectory (Join-Path $testRoot connection-failed)
            Check ($global:LASTEXITCODE -eq 0)
            $r=Get-Content (Join-Path $testRoot connection-failed/inventory.json) -Raw | ConvertFrom-Json
            Check ($r.discoveryStatus -eq 'Complete' -and $r.serviceConnectionQuery.status -eq 'Failed')
            Check ((Get-Content (Join-Path $testRoot connection-failed/summary.md) -Raw).Contains('Matching service connections: Unknown (listing failed)'))
        } finally { $global:BlobTransferDiscoveryTestState.connectionFailure=$false }
    }
    Case 'successful empty connection list reports none found' {
        $global:BlobTransferDiscoveryTestState.emptyConnections=$true
        try {
            & "$PSScriptRoot/Export-DeploymentInventory.ps1" -SubscriptionId $sub -OrganizationUrl https://dev.azure.com/example -Project Example -OutputDirectory (Join-Path $testRoot empty-connections)
            Check ((Get-Content (Join-Path $testRoot empty-connections/summary.md) -Raw).Contains('Matching service connections: None found.'))
        } finally { $global:BlobTransferDiscoveryTestState.emptyConnections=$false }
    }
    $bundle=@{target=$target;parameters=@{parameters=$p}}
    $register=@{InventoryPath=(Join-Path $testRoot with-devops/inventory.json);Workload='blobcopy';EnvironmentName='dev';SubscriptionAlias='sandbox';NetworkProfile='shared';IntegrationSubnetId=$network.integrationSubnetId;PrivateEndpointSubnetId=$network.privateEndpointSubnetId;ServiceConnectionId='44444444-4444-4444-4444-444444444444';OrganizationCode='acme';RegionCode='eus2';OutputDirectory=(Join-Path $testRoot registered)}
    Case 'both DNS inventory failures preserve other inventory and diagnostics before failing' {
        $global:BlobTransferDiscoveryTestState.dnsFailure=$true
        $global:BlobTransferDiscoveryTestState.calls.Clear()
        try {
            Reject { & "$PSScriptRoot/Export-DeploymentInventory.ps1" -SubscriptionId $sub -OrganizationUrl https://dev.azure.com/example -Project Example -OutputDirectory (Join-Path $testRoot dns-failed) }
            $r=Get-Content (Join-Path $testRoot dns-failed/inventory.json) -Raw | ConvertFrom-Json
            Check ($r.discoveryStatus -eq 'Partial' -and $r.privateDnsQuery.status -eq 'Failed' -and $r.privateDnsZones.Count -eq 0)
            Check ($r.privateDnsQuery.primaryStatus -eq 'Failed' -and $r.privateDnsQuery.fallback.status -eq 'Failed' -and $r.privateDnsQuery.fallback.error)
            Check ($r.networks.Count -eq 1 -and $r.serviceConnections.Count -eq 1 -and $r.permissionEvidence.Count -eq 1)
            Check ($r.diagnostics.subscriptionArm.subscriptionId -eq $sub -and $r.diagnostics.networkProvider.details.registrationState -eq 'Registered')
            Check ((Get-Content (Join-Path $testRoot dns-failed/summary.md) -Raw).Contains('Status: Partial'))
            Check (!@($global:BlobTransferDiscoveryTestState.calls | Where-Object { $_ -match '^provider register |^account set |^role assignment create ' }).Count)
        } finally { $global:BlobTransferDiscoveryTestState.dnsFailure=$false }
    }
    Case 'partial inventory cannot register a target even with explicit DNS IDs' {
        $bad=Clone $register; $bad.InventoryPath=Join-Path $testRoot dns-failed/inventory.json; $bad.PrivateDnsZoneIds=$network.privateDnsZoneIds
        Reject { & "$PSScriptRoot/New-ServiceTarget.ps1" @bad }
        Check (!(Test-Path (Join-Path $testRoot registered/blobcopy.dev.sandbox.shared.json)))
    }
    Case 'unavailable diagnostics do not discard partial inventory' {
        $global:BlobTransferDiscoveryTestState.dnsFailure=$true; $global:BlobTransferDiscoveryTestState.diagnosticFailure=$true
        try {
            Reject { & "$PSScriptRoot/Export-DeploymentInventory.ps1" -SubscriptionId $sub -OutputDirectory (Join-Path $testRoot diagnostics-failed) }
            $r=Get-Content (Join-Path $testRoot diagnostics-failed/inventory.json) -Raw | ConvertFrom-Json
            Check ($r.discoveryStatus -eq 'Partial' -and $r.networks.Count -eq 1 -and $r.diagnostics.subscriptionArm.status -eq 'Failed' -and $r.diagnostics.networkProvider.status -eq 'Failed')
        } finally { $global:BlobTransferDiscoveryTestState.dnsFailure=$false; $global:BlobTransferDiscoveryTestState.diagnosticFailure=$false }
    }
    Case 'successful empty DNS inventory is distinct from a failed query' {
        $global:BlobTransferDiscoveryTestState.emptyDns=$true; $global:BlobTransferDiscoveryTestState.calls.Clear()
        try {
            & "$PSScriptRoot/Export-DeploymentInventory.ps1" -SubscriptionId $sub -OutputDirectory (Join-Path $testRoot empty-dns)
            $r=Get-Content (Join-Path $testRoot empty-dns/inventory.json) -Raw | ConvertFrom-Json
            Check ($r.discoveryStatus -eq 'Complete' -and $r.privateDnsQuery.status -eq 'Succeeded' -and $r.privateDnsZones.Count -eq 0)
            Check (!@($global:BlobTransferDiscoveryTestState.calls | Where-Object { $_ -match '^resource list |^provider show |^rest .*subscriptions/[^/]+\?' }).Count)
        } finally { $global:BlobTransferDiscoveryTestState.emptyDns=$false }
    }
    foreach ($empty in @($true,$false)) {
        Case "DNS API failure recovers complete ARM inventory (empty=$empty)" {
            $global:BlobTransferDiscoveryTestState.dnsFailure=$true
            $global:BlobTransferDiscoveryTestState.dnsFallbackSucceeds=$true
            $global:BlobTransferDiscoveryTestState.emptyDns=$empty
            $global:BlobTransferDiscoveryTestState.calls.Clear()
            $path=Join-Path $testRoot "dns-recovered-$empty"
            try {
                & "$PSScriptRoot/Export-DeploymentInventory.ps1" -SubscriptionId $sub -OutputDirectory $path
                Check ($global:LASTEXITCODE -eq 0)
                $r=Get-Content (Join-Path $path inventory.json) -Raw | ConvertFrom-Json
                $m=Get-Content (Join-Path $path manifest.json) -Raw | ConvertFrom-Json
                $count=if ($empty) {0} else {5}
                Check ($r.discoveryStatus -eq 'Complete' -and $r.privateDnsQuery.status -eq 'Succeeded' -and $r.privateDnsZones.Count -eq $count)
                Check ($r.privateDnsQuery.primaryStatus -eq 'Failed' -and $r.privateDnsQuery.primaryError -and $r.privateDnsQuery.source -eq 'arm-resource-inventory')
                Check ($r.privateDnsQuery.fallback.status -eq 'Succeeded' -and $r.privateDnsQuery.count -eq $count -and $r.privateDnsQuery.fallback.count -eq $count)
                Check ($m.discoveryStatus -eq 'Complete' -and $m.inventorySha256 -eq (Get-ServiceHash (Join-Path $path inventory.json)))
                Check ($r.diagnostics.subscriptionArm.status -eq 'Succeeded' -and $r.diagnostics.networkProvider.status -eq 'Succeeded')
                $summary=Get-Content (Join-Path $path summary.md) -Raw
                Check ($summary.Contains('fallback succeeded'))
                if ($empty) { Check ($summary.Contains('Private DNS zones: None found.')) }
                else { Check (!@(Compare-Object @($network.privateDnsZoneIds.Values) @($r.privateDnsZones.id)).Count) }
                Check (@($global:BlobTransferDiscoveryTestState.calls | Where-Object { $_ -match '^resource list ' }).Count -eq 1)
                Check (!@($global:BlobTransferDiscoveryTestState.calls | Where-Object { $_ -match '^provider register |^account set |^role assignment create ' }).Count)
            } finally {
                $global:BlobTransferDiscoveryTestState.dnsFailure=$false
                $global:BlobTransferDiscoveryTestState.dnsFallbackSucceeds=$false
                $global:BlobTransferDiscoveryTestState.emptyDns=$false
            }
        }
    }
    foreach ($failure in @('networkFailure','subnetFailure')) {
        Case "DNS fallback cannot erase $failure partial status" {
            $global:BlobTransferDiscoveryTestState.dnsFailure=$true
            $global:BlobTransferDiscoveryTestState.dnsFallbackSucceeds=$true
            $global:BlobTransferDiscoveryTestState[$failure]=$true
            try {
                $path=Join-Path $testRoot "dns-recovered-$failure"
                Reject { & "$PSScriptRoot/Export-DeploymentInventory.ps1" -SubscriptionId $sub -OutputDirectory $path }
                $r=Get-Content (Join-Path $path inventory.json) -Raw | ConvertFrom-Json
                Check ($r.discoveryStatus -eq 'Partial' -and $r.privateDnsQuery.status -eq 'Succeeded' -and $r.privateDnsZones.Count -eq 5)
                Check ((Get-Content (Join-Path $path manifest.json) -Raw | ConvertFrom-Json).discoveryStatus -eq 'Partial')
            } finally {
                $global:BlobTransferDiscoveryTestState.dnsFailure=$false
                $global:BlobTransferDiscoveryTestState.dnsFallbackSucceeds=$false
                $global:BlobTransferDiscoveryTestState[$failure]=$false
            }
        }
    }
    Case 'empty network and DNS lists succeed with explicit none-found summary' {
        $global:BlobTransferDiscoveryTestState.emptyDns=$true; $global:BlobTransferDiscoveryTestState.emptyNetwork=$true
        try {
            & "$PSScriptRoot/Export-DeploymentInventory.ps1" -SubscriptionId $sub -OrganizationUrl https://dev.azure.com/example -Project Example -OutputDirectory (Join-Path $testRoot empty-network)
            $r=Get-Content (Join-Path $testRoot empty-network/inventory.json) -Raw | ConvertFrom-Json
            Check ($r.discoveryStatus -eq 'Complete' -and $r.networkQuery.count -eq 0 -and $r.privateDnsQuery.count -eq 0)
            $summary=Get-Content (Join-Path $testRoot empty-network/summary.md) -Raw
            Check ($summary.Contains('Virtual networks: None found.') -and $summary.Contains('Private DNS zones: None found.'))
        } finally { $global:BlobTransferDiscoveryTestState.emptyDns=$false; $global:BlobTransferDiscoveryTestState.emptyNetwork=$false }
    }
    Case 'empty subnet list succeeds with explicit none-found summary' {
        $global:BlobTransferDiscoveryTestState.emptySubnets=$true
        try {
            & "$PSScriptRoot/Export-DeploymentInventory.ps1" -SubscriptionId $sub -OutputDirectory (Join-Path $testRoot empty-subnets)
            $r=Get-Content (Join-Path $testRoot empty-subnets/inventory.json) -Raw | ConvertFrom-Json
            Check ($r.discoveryStatus -eq 'Complete' -and $r.networks[0].subnetQuery.count -eq 0)
            Check ((Get-Content (Join-Path $testRoot empty-subnets/summary.md) -Raw).Contains('Subnets in shared: None found.'))
        } finally { $global:BlobTransferDiscoveryTestState.emptySubnets=$false }
    }
    foreach ($failure in @('networkFailure','subnetFailure')) {
        Case "$failure saves unknown status while retaining successful DNS listing" {
            $global:BlobTransferDiscoveryTestState[$failure]=$true
            try {
                Reject { & "$PSScriptRoot/Export-DeploymentInventory.ps1" -SubscriptionId $sub -OutputDirectory (Join-Path $testRoot $failure) }
                $r=Get-Content (Join-Path $testRoot "$failure/inventory.json") -Raw | ConvertFrom-Json
                Check ($r.discoveryStatus -eq 'Partial' -and $r.privateDnsQuery.status -eq 'Succeeded' -and $r.privateDnsZones.Count -eq 5)
                Check ((Get-Content (Join-Path $testRoot "$failure/summary.md") -Raw).Contains('Unknown (listing failed)'))
            } finally { $global:BlobTransferDiscoveryTestState[$failure]=$false }
        }
    }
    $newRegister=Clone $register
    $newRegister.Remove('IntegrationSubnetId'); $newRegister.Remove('PrivateEndpointSubnetId')
    $newRegister.InventoryPath=Join-Path $testRoot empty-network/inventory.json
    $newRegister.NetworkMode='new'; $newRegister.NetworkProfile='new-private'; $newRegister.Location='eastus2'
    $newRegister.VnetAddressPrefix='10.40.0.0/16'; $newRegister.IntegrationSubnetPrefix='10.40.0.0/26'; $newRegister.PrivateEndpointSubnetPrefix='10.40.1.0/26'
    Case 'empty inventory registers a standard-name managed network without existing IDs' {
        $global:BlobTransferDiscoveryTestState.calls.Clear()
        & "$PSScriptRoot/New-ServiceTarget.ps1" @newRegister
        $registered=Get-Content (Join-Path $testRoot registered/blobcopy.dev.sandbox.new-private.json) -Raw | ConvertFrom-Json -AsHashtable
        $compiled=Clone $p; Set-ServiceProfileParameters $registered $compiled; Assert-ServiceParameters $registered $compiled
        Check (!$registered.enabled -and $registered.resourceGroup -eq 'rg-blobcopy-dev-acme-eus2-001' -and $compiled.networkMode.value -eq 'new')
        Check ((Get-ServiceStem $compiled) -eq 'blobcopy-dev-acme-eus2-001' -and $compiled.vnetAddressPrefix.value -eq '10.40.0.0/16' -and !$registered.parameterOverrides.ContainsKey('existingNetwork'))
        Check ($global:BlobTransferDiscoveryTestState.calls.Count -eq 0)
    }
    Case 'partial discovery cannot select new provisioning as a fallback' {
        $bad=Clone $newRegister; $bad.InventoryPath=Join-Path $testRoot dns-failed/inventory.json
        Reject { & "$PSScriptRoot/New-ServiceTarget.ps1" @bad }
    }
    $handoff=Join-Path $testRoot handoff
    New-Item -ItemType Directory -Path $handoff -Force | Out-Null
    Copy-Item (Join-Path $testRoot empty-network/inventory.json) (Join-Path $handoff inventory.json)
    $handoffInventory=Get-Content (Join-Path $handoff inventory.json) -Raw | ConvertFrom-Json -AsHashtable
    $handoffTarget=Get-Content (Join-Path $testRoot registered/blobcopy.dev.sandbox.new-private.json) -Raw | ConvertFrom-Json -AsHashtable
    $manifest=@{schemaVersion=1;kind='blob-transfer-discovery';generatedUtc=$handoffInventory.generatedUtc;discoveryStatus='Complete';inventorySha256=(Get-ServiceHash (Join-Path $handoff inventory.json));subscriptionId=$sub;serviceConnection=$handoffTarget.serviceConnection;selection=@{workload='blobcopy';environment='dev'};source=@{runId='42';pipelineId='7';projectId='project';repositoryId='repo';branch='refs/heads/main';commit='commit'}}
    $build=@{id=42;definition=@{id=7};project=@{id='project'};repository=@{id='repo'};status='completed';result='succeeded';sourceBranch='refs/heads/main';sourceVersion='commit'}
    Write-ServiceJson $manifest (Join-Path $handoff manifest.json)
    Case 'second pipeline accepts complete empty inventory for approved new-network target' {
        $read=Read-DiscoveryManifest $handoff $handoffTarget $handoffTarget.serviceConnection
        Assert-DiscoveryRun $read $build 7 42 project repo
        Check ($read.inventorySha256 -eq $manifest.inventorySha256)
    }
    Case 'manifest rejects changed inventory bytes' {
        Add-Content (Join-Path $handoff inventory.json) ' '
        Reject { Read-DiscoveryManifest $handoff $handoffTarget $handoffTarget.serviceConnection }
        Copy-Item (Join-Path $testRoot empty-network/inventory.json) (Join-Path $handoff inventory.json) -Force
    }
    foreach ($change in @(@('discoveryStatus','Partial'),@('subscriptionId','33333333-3333-3333-3333-333333333333'),@('serviceConnection','wrong'),@('generatedUtc',[DateTimeOffset]::UtcNow.AddDays(-8).ToString('O')))) {
        Case "manifest rejects $($change[0]) mismatch or stale evidence" {
            $bad=Clone $manifest; $bad[$change[0]]=$change[1]; Write-ServiceJson $bad (Join-Path $handoff manifest.json)
            Reject { Read-DiscoveryManifest $handoff $handoffTarget $handoffTarget.serviceConnection }
            Write-ServiceJson $manifest (Join-Path $handoff manifest.json)
        }
    }
    foreach ($change in @(@('sourceBranch','refs/heads/feature'),@('result','failed'),@('result','partiallySucceeded'),@('sourceVersion','other'),@('status','inProgress'))) {
        Case "handoff rejects run $($change[0])=$($change[1])" {
            $bad=Clone $build; $bad[$change[0]]=$change[1]
            Reject { Assert-DiscoveryRun $manifest $bad 7 42 project repo }
        }
    }
    Case 'handoff rejects another run pipeline project or repository' {
        foreach ($args in @(@('8','42','project','repo'),@('7','43','project','repo'),@('7','42','other','repo'),@('7','42','project','other'))) {
            Reject { Assert-DiscoveryRun $manifest $build @args }
        }
    }
    Case 'handoff rejects subnet selection absent from saved discovery' {
        $existing=Clone $target; $existing.serviceConnection=$handoffTarget.serviceConnection
        Reject { Read-DiscoveryManifest $handoff $existing $existing.serviceConnection }
    }
    Case 'deployment entry point fixes deploy and requires the chosen discovery artifact' {
        $yaml=Get-Content (Join-Path $generated azure-pipelines-self-service-deploy.yml) -Raw
        $entry=Get-Content (Join-Path $generated pipelines/deploy-entry.yml) -Raw
        Check ($yaml.Contains('extends:') -and $entry.Contains('operation: deploy') -and !$yaml.Contains('name: operation') -and !$yaml.Contains('name: discoveryRunId'))
        Check ($yaml.Contains('pipeline: discovery') -and $yaml.Contains('branch: refs/heads/main') -and $yaml.Contains('discoveryRunId: $(resources.pipeline.discovery.runID)') -and $yaml.Contains('discoveryPipelineId: $(resources.pipeline.discovery.pipelineID)'))
        $template=Get-Content (Join-Path (Get-ProjectRoot) pipelines/templates/self-service-stages.yml) -Raw
        Check ($template.Contains('buildVersionToDownload: specific') -and $template.Contains('Test-DiscoveryHandoff.ps1') -and $template.Contains('-DiscoveryDirectory'))
        Check ($template.IndexOf('Test-DiscoveryHandoff.ps1') -lt $template.IndexOf('template: steps/qualify-application.yml'))
    }
    Case 'generated price date uses UTC regardless of the local timezone' {
        $snapshot=Get-Content (Join-Path (Get-ProjectRoot) self-service/pricing/usd-eastus2.json) -Raw
        $document=[System.Text.Json.JsonDocument]::Parse($snapshot)
        try { $expected=([DateTimeOffset]::Parse($document.RootElement.GetProperty('retrievedUtc').GetString())).UtcDateTime.ToString('yyyy-MM-dd',[Globalization.CultureInfo]::InvariantCulture) }
        finally { $document.Dispose() }
        $yaml=Get-Content (Join-Path $generated azure-pipelines-self-service-deploy.yml) -Raw
        Check ($yaml.Contains("USD East US 2 retail as of $expected") -and $yaml.Contains("name: usageEstimate"))
    }
    Case 'discovery menu cannot select deployment or expose deployment-only choices' {
        $yaml=Get-Content (Join-Path $generated azure-pipelines-self-service.yml) -Raw
        Check ($yaml.Contains('operation: discover') -and !$yaml.Contains('operation: deploy'))
        foreach ($key in @('operation','discoveryRunId','discoveryPipelineId','createDestinationPrivateEndpoints','enableLogAlerts','hostingEstimate','packageResources')) { Check (!$yaml.Contains("name: $key")) }
        Check (!$yaml.Contains('resources:') -and !$yaml.Contains('resources.pipeline.'))
    }
    Case 'new network needs explicit location and approved CIDRs' {
        foreach ($key in @('Location','VnetAddressPrefix','IntegrationSubnetPrefix','PrivateEndpointSubnetPrefix')) {
            $bad=Clone $newRegister; $bad[$key]=''; $bad.OutputDirectory=Join-Path $testRoot "missing-$key"
            Reject { & "$PSScriptRoot/New-ServiceTarget.ps1" @bad }; Check (!(Test-Path $bad.OutputDirectory))
        }
    }
    foreach ($pair in @(@('IntegrationSubnetPrefix','10.40.0.0/28'),@('IntegrationSubnetPrefix','10.41.0.0/26'),@('PrivateEndpointSubnetPrefix','10.40.0.0/26'),@('VnetAddressPrefix','8.8.0.0/16'))) {
        Case "new network rejects invalid allocation $($pair[0]) $($pair[1])" {
            $bad=Clone $newRegister; $bad[$pair[0]]=$pair[1]; $bad.OutputDirectory=Join-Path $testRoot invalid-allocation
            Reject { & "$PSScriptRoot/New-ServiceTarget.ps1" @bad }; Check (!(Test-Path $bad.OutputDirectory))
        }
    }
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
