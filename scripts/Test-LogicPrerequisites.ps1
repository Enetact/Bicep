#requires -Version 7.4
. "$PSScriptRoot/workload-preview-common.ps1"
$testRoot=Join-Path (Get-ProjectRoot) ('artifacts/prerequisite-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testRoot -Force|Out-Null
$cases=[Collections.Generic.List[object]]::new()
function Check([bool]$Value){if(!$Value){throw 'Prerequisite assertion failed.'}}
function Case([string]$Name,[scriptblock]$Body){try{& $Body;$cases.Add(@{name=$Name;passed=$true})}catch{throw "Prerequisite case '$Name': $($_.Exception.Message)"}}
function Reject([scriptblock]$Body){$caught=$false;try{& $Body|Out-Null}catch{$caught=$true};Check $caught}
function Clone($Value){$Value|ConvertTo-Json -Depth 100|ConvertFrom-Json -AsHashtable}
$t=Read-ServiceTarget eventflow dev azure-subscription-a central-private -AllowDisabled
$policy=Read-LogicPrerequisitePolicy $t
$empty=@{schemaVersion=1;readOnly=$true;generatedUtc=[DateTimeOffset]::UtcNow.ToString('O');discoveryStatus='Complete';subscription=@{id=$t.subscriptionId};networks=@();privateDnsZones=@();resources=@();networkQuery=@{status='Succeeded'};privateDnsQuery=@{status='Succeeded'};resourceQuery=@{status='Succeeded'};prerequisiteOwnership=@{status='Succeeded';stackExists=$false;managedResourceIds=@()}}
$new=Get-LogicPrerequisitePlan $t $empty
Case 'empty successful inventory plans all owned prerequisites without Azure writes' {
    Check ($new.status -eq 'Ready' -and $new.resources.Count -eq 17)
    Check (@($new.resources|Where-Object {$_.action -ne 'Create'}).Count -eq 0)
    Check ($new.bicep.createNetwork -and $new.bicep.createWorkspace -and $new.bicep.createDnsZoneNames.Count -eq 6)
}
foreach($read in @('networkQuery','privateDnsQuery','resourceQuery','prerequisiteOwnership')){
    Case "failed $read is unknown and never authorizes Create" {
        $bad=Clone $empty;$bad[$read].status='Failed';$p=Get-LogicPrerequisitePlan $t $bad
        Check ($p.status -eq 'Blocked' -and $p.resources.Count -eq 0 -and $p.bicep.Count -eq 0)
    }
}
Case 'old inventory without ownership evidence cannot resolve automatic prerequisites' {
    $bad=Clone $empty;$bad.Remove('prerequisiteOwnership');Check ((Get-LogicPrerequisitePlan $t $bad).status -eq 'Blocked')
}
function ExistingInventory($Plan){
    $i=Clone $empty
    $i.networks=@(@{id=$Plan.bicep.vnetId;name=$Plan.bicep.vnetName;location='eastus2';addressPrefixes=@('10.70.0.0/16');subnetQuery=@{status='Succeeded'};subnets=@(@{id=$Plan.parameters.integrationSubnetId;integrationCandidate=$true},@{id=$Plan.parameters.privateEndpointSubnetId;privateEndpointCandidate=$true})})
    $i.privateDnsZones=@($Plan.parameters.privateDnsZoneIds.Values|ForEach-Object {@{id=$_;name=$_.Split('/')[-1]}})
    $i.resources=@(@{id=$Plan.bicep.vnetId;type='Microsoft.Network/virtualNetworks';location='eastus2'},@{id=$Plan.parameters.existingLogAnalyticsWorkspaceId;type='Microsoft.OperationalInsights/workspaces';location='eastus2'})+@($i.privateDnsZones|ForEach-Object {@{id=$_.id;type='Microsoft.Network/privateDnsZones';location='global'}})
    return $i
}
$existing=ExistingInventory $new
Case 'existing matching resources are reused with no creation declarations' {
    $p=Get-LogicPrerequisitePlan $t $existing
    Check ($p.status -eq 'Ready' -and !$p.bicep.createNetwork -and !$p.bicep.createWorkspace -and !$p.bicep.createDnsZoneNames.Count)
    Check (@($p.resources|Where-Object {$_.action -ne 'Reuse'}).Count -eq 0)
}
Case 'repeat discovery retains owned resources as Manage with stable Bicep flags' {
    $owned=Clone $existing;$owned.prerequisiteOwnership.managedResourceIds=@($new.resources.id);$owned.prerequisiteOwnership.stackExists=$true
    $p=Get-LogicPrerequisitePlan $t $owned
    Check ($p.status -eq 'Ready' -and @($p.resources|Where-Object {$_.action -ne 'Manage'}).Count -eq 0)
    Check ((Get-ValueHash $p.bicep) -ceq (Get-ValueHash $new.bicep))
}
Case 'partial prerequisite existence reuses network and creates only absent zones/workspace' {
    $mixed=Clone $existing;$mixed.privateDnsZones=@($existing.privateDnsZones|Select-Object -First 2)
    $mixed.resources=@($existing.resources|Where-Object {$_.type -ne 'Microsoft.OperationalInsights/workspaces'})
    $p=Get-LogicPrerequisitePlan $t $mixed
    Check ($p.status -eq 'Ready' -and !$p.bicep.createNetwork -and $p.bicep.createWorkspace -and $p.bicep.createDnsZoneNames.Count -eq 4)
}
Case 'explicit shared IDs can be reused outside the workload group without adoption' {
    $shared=Clone $new
    $old="/resourceGroups/$($t.resourceGroup)/";$replacement='/resourceGroups/shared/'
    $shared.bicep.vnetId=$shared.bicep.vnetId.Replace($old,$replacement)
    $shared.parameters.integrationSubnetId=$shared.parameters.integrationSubnetId.Replace($old,$replacement)
    $shared.parameters.privateEndpointSubnetId=$shared.parameters.privateEndpointSubnetId.Replace($old,$replacement)
    $shared.parameters.existingLogAnalyticsWorkspaceId=$shared.parameters.existingLogAnalyticsWorkspaceId.Replace($old,$replacement)
    foreach($key in @($shared.parameters.privateDnsZoneIds.Keys)){$shared.parameters.privateDnsZoneIds[$key]=$shared.parameters.privateDnsZoneIds[$key].Replace($old,$replacement)}
    $target=Clone $t;foreach($key in $shared.parameters.Keys){$target.parameterOverrides[$key]=$shared.parameters[$key]}
    $i=ExistingInventory $shared;$p=Get-LogicPrerequisitePlan $target $i
    Check ($p.status -eq 'Ready' -and @($p.resources|Where-Object {$_.action -ne 'Reuse'}).Count -eq 0)
}
Case 'explicit missing shared ID is blocked instead of created at that ID' {
    $target=Clone $t;$target.parameterOverrides.existingLogAnalyticsWorkspaceId=$new.parameters.existingLogAnalyticsWorkspaceId.Replace('/rg-eventflow-dev/','/shared/')
    $p=Get-LogicPrerequisitePlan $target $empty
    Check ($p.status -eq 'Blocked' -and @($p.resources|Where-Object {$_.kind -eq 'workspace'})[0].action -eq 'Blocked')
}
Case 'ambiguous or incompatible subnet selection is blocked' {
    $bad=Clone $existing;$bad.networks[0].subnets[0].integrationCandidate=$false;Check ((Get-LogicPrerequisitePlan $t $bad).status -eq 'Blocked')
    $bad=Clone $existing;$bad.networks+=Clone $bad.networks[0];Check ((Get-LogicPrerequisitePlan $t $bad).status -eq 'Blocked')
}
Case 'new dedicated network rejects overlapping discovered address space' {
    $bad=Clone $empty;$bad.networks=@(@{id='/other';addressPrefixes=@('10.70.0.0/16')})
    Check ((Get-LogicPrerequisitePlan $t $bad).status -eq 'Blocked')
    Check (Test-LogicCidrOverlap '10.70.0.0/16' '10.70.1.0/24')
    Check (!(Test-LogicCidrOverlap '10.70.0.0/16' '10.71.0.0/16'))
}
Case 'unrelated resources are listed as candidates without automatic adoption' {
    $bad=Clone $empty;$bad.resources=@(@{id='/subscriptions/other/resourceGroups/shared/providers/Microsoft.OperationalInsights/workspaces/unrelated';type='Microsoft.OperationalInsights/workspaces';location='eastus2'})
    $p=Get-LogicPrerequisitePlan $t $bad
    Check ($p.status -eq 'Ready' -and $p.candidates.workspaces.Count -eq 1 -and $p.bicep.createWorkspace)
}
$directory=Join-Path $testRoot discovery
$empty.prerequisitePlan=$new
Write-ServiceJson $empty (Join-Path $directory inventory.json)
$resolved=@{}
Case 'saved plan resolves IDs and freezes creation flags into deployment parameters' {
    Set-LogicDiscoveredPrerequisites $t $resolved $directory $testRoot
    Check ((Get-ValueHash $resolved.prerequisitePlan.value) -ceq (Get-ValueHash $new.bicep))
    $null=Assert-LogicResolvedPrerequisites $t $resolved $empty
    Check (Test-Path (Join-Path $testRoot prerequisite-plan.json))
}
Case 'tampered policy decisions or effective flags cannot bypass saved inventory' {
    $bad=Clone $resolved;$bad.prerequisitePlan.value.createNetwork=$false;Reject {Assert-LogicResolvedPrerequisites $t $bad $empty}
    $bad=Clone $empty;$bad.prerequisitePlan.bicep.createWorkspace=$false;Reject {Assert-LogicResolvedPrerequisites $t $resolved $bad}
}
Case 'explicit environment IDs cannot be overwritten by automatic resolution' {
    $bad=@{existingLogAnalyticsWorkspaceId=@{value=$new.parameters.existingLogAnalyticsWorkspaceId.Replace('/rg-eventflow-dev/','/shared/')}}
    Reject {Set-LogicDiscoveredPrerequisites $t $bad $directory}
    Check ($bad.existingLogAnalyticsWorkspaceId.value.Contains('/shared/'))
}
Case 'source policy changes require fresh discovery' {
    $bad=Clone $t;$bad.parameterOverrides.existingLogAnalyticsWorkspaceId=$new.parameters.existingLogAnalyticsWorkspaceId
    Reject {Set-LogicDiscoveredPrerequisites $bad @{} $directory}
}
Case 'legacy inventory remains existing-only and does not infer absent resources' {
    $legacy=Clone $empty;$legacy.Remove('prerequisitePlan');$folder=Join-Path $testRoot legacy
    Write-ServiceJson $legacy (Join-Path $folder inventory.json);$p=@{}
    Set-LogicDiscoveredPrerequisites $t $p $folder;Check ($p.Count -eq 0)
}
Case 'authorized dev settings plus saved prerequisite plan pass onboarding' {
    $path=Join-Path $testRoot parameters.json
    Invoke-Bicep -Arguments @('build-params',(Join-Path (Get-ProjectRoot) $t.parameterFile),'--outfile',$path)
    $p=Get-Content $path -Raw|ConvertFrom-Json -AsHashtable
    Set-LogicDiscoveredPrerequisites $t $p.parameters $directory
    $issues=@(Get-LogicOnboardingIssues $p.parameters)
    Check ($issues.Count -eq 0)
    Assert-LogicParameters $t $p.parameters
    $cost=Get-LogicCostEstimate $p.parameters;Check (@($cost.lines|Where-Object {$_.resource -eq 'Owned private DNS zones'})[0].quantity -eq 6)
}
Case 'resource resolution does not generate ownership identity or approval values' {
    $p=Get-Content (Join-Path $testRoot parameters.json) -Raw|ConvertFrom-Json -AsHashtable
    foreach($key in @('owner','costCenter','deploymentPrincipalObjectId')){$p.parameters[$key].value='REPLACE_TEST_VALUE'}
    foreach($key in @('trustedServiceException','runtimeStorageCredentialException')){$p.parameters[$key].value=@{approved=$false;reviewReference=''}}
    Set-LogicDiscoveredPrerequisites $t $p.parameters $directory
    $issues=@(Get-LogicOnboardingIssues $p.parameters)
    Check ($issues.Count -eq 5)
    foreach($key in @('owner','costCenter','deploymentPrincipalObjectId','trustedServiceException','runtimeStorageCredentialException')){Check ($key -in $issues.parameter)}
    Reject {Assert-LogicParameters $t $p.parameters}
}
Case 'README shows prerequisite Create decisions even while onboarding is blocked' {
    Write-WorkloadPreviewReadme $testRoot 'Blocked' 'Provide ownership and approval values.'
    $report=Get-Content (Join-Path $testRoot README.md) -Raw
    Check ($report.Contains('Prerequisite decisions') -and $report.Contains('Create') -and $report.Contains('log-eventflow-dev') -and $report.Contains('Azure changes unavailable'))
}
$script:live=@();$script:calls=[Collections.Generic.List[string]]::new()
function Invoke-ServiceJson([string[]]$Arguments){$script:calls.Add(($Arguments -join ' '));if($Arguments[0] -eq 'resource' -and $Arguments[1] -eq 'list'){return $script:live};if($Arguments[0] -eq 'provider'){return @{registrationState='Registered'}};throw 'Unexpected Azure call in fresh prerequisite test.'}
$bundle=@{target=$t;parameters=@{parameters=$resolved};directory=$testRoot}
New-Item -ItemType Directory -Path (Join-Path $testRoot discovery) -Force|Out-Null
Case 'fresh Create decisions are rechecked through read-only ARM inventory' {
    Assert-LogicPrerequisiteLiveState $bundle @{managedResources=@()}
    Check (@($script:calls|Where-Object {$_ -match 'create|delete|update|register'}).Count -eq 0)
}
Case 'resource appearing after discovery cannot be silently adopted' {
    $script:live=@(@{id=$new.bicep.vnetId;type='Microsoft.Network/virtualNetworks'})
    Reject {Assert-LogicPrerequisiteLiveState $bundle @{managedResources=@()}}
    Assert-LogicPrerequisiteLiveState $bundle @{managedResources=@($new.bicep.vnetId)}
    $script:live=@()
}
Case 'fresh foundation checks providers while allowing approved future prerequisites' {
    $script:calls.Clear();$null=Test-LogicPrerequisites $bundle -AllowPlannedCreates
    Check ($script:calls.Count -eq 5 -and @($script:calls|Where-Object {$_ -notlike 'provider show *'}).Count -eq 0)
    Reject {Test-LogicPrerequisites $bundle}
}
# Execute the actual discovery entry point with a mocked CLI transport.
$global:LogicPrerequisiteDiscoveryCalls=[Collections.Generic.List[string]]::new()
$global:LogicPrerequisiteDiscoverySubscription=$t.subscriptionId
function az {
    $command=$args -join ' ';$global:LogicPrerequisiteDiscoveryCalls.Add($command);$global:LASTEXITCODE=0
    $result=switch -Regex ($command){
        '^account show' {@{id=$global:LogicPrerequisiteDiscoverySubscription;state='Enabled';name='Fixture';tenantId='11111111-1111-1111-1111-111111111111'};break}
        '^network vnet list|^network private-dns zone list|^resource list|^stack sub list' {,@();break}
        '^provider show' {$index=[Array]::IndexOf([object[]]$args,'--namespace');@{namespace=$args[$index+1];registrationState='Registered'};break}
        '^rest .*Microsoft.Authorization/permissions' {@{value=@()};break}
        default {throw "Unexpected discovery mutation or read: $command"}
    }
    ConvertTo-Json -InputObject $result -Depth 20 -Compress
}
try{
Case 'real Discover entry exports hashed read-only inventory and Create manifest' {
    $output=Join-Path $testRoot export
    & "$PSScriptRoot/Export-DeploymentInventory.ps1" -Workload $t.workload -EnvironmentName $t.environmentName -SubscriptionAlias $t.subscriptionAlias -NetworkProfile $t.networkProfile -BoundServiceConnection $t.serviceConnection -OutputDirectory $output
    $inventory=Get-Content (Join-Path $output inventory.json) -Raw|ConvertFrom-Json -AsHashtable
    $manifest=Read-DiscoveryManifest $output $t $t.serviceConnection
    Check ($inventory.readOnly -and $inventory.prerequisitePlan.status -eq 'Ready' -and $inventory.prerequisitePlan.resources.Count -eq 17)
    Check ($manifest.inventorySha256 -ceq (Get-ServiceHash (Join-Path $output inventory.json)))
    Check (Test-Path (Join-Path $output prerequisite-plan.json))
    Check ((Get-Content (Join-Path $output summary.md) -Raw).Contains('Prerequisite decisions'))
    Check (@($global:LogicPrerequisiteDiscoveryCalls|Where-Object {$_ -match '\b(create|update|delete|register|set)\b'}).Count -eq 0)
}
}finally{
    Remove-Variable LogicPrerequisiteDiscoveryCalls,LogicPrerequisiteDiscoverySubscription -Scope Global
}
Write-ServiceJson @{passed=$cases.Count;failed=0;cases=$cases;azureCalls='mocked'} (Join-Path $testRoot results.json)
Write-Host "PASS: $($cases.Count) prerequisite contracts. Evidence: $testRoot"
