#requires -Version 7.4
$ErrorActionPreference='Stop'
. "$PSScriptRoot/network-allocation-common.ps1"
$root=Split-Path $PSScriptRoot -Parent
$out=Join-Path $root ('artifacts/network-allocation-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $out -Force|Out-Null
$config=Get-Content (Join-Path $root 'config/network-allocation.json') -Raw|ConvertFrom-Json -AsHashtable
$p=$config.profiles['avnm-private-web']
if($p.enabled){throw 'Example allocation profile must remain disabled.'}
$p.poolId='/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/net/providers/Microsoft.Network/networkManagers/manager/ipamPools/pool'
$p.subscriptionId='11111111-1111-1111-1111-111111111111';$p.tenantId='aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa';$p.routingDomain='fixture';$p.owner='test';$p.costCenter='test'
$file=Join-Path $out 'profile.json';$config|ConvertTo-Json -Depth 10|Set-Content $file
$script:cases=0
function Check([bool]$Pass,[string]$Message){if(!$Pass){throw $Message};$script:cases++}
function Reject([scriptblock]$Action){$rejected=$false;try{& $Action|Out-Null}catch{$rejected=$true};Check $rejected 'Expected rejected unsafe allocation input.'}
$intent=Get-NetworkIntent 'avnm-private-web' 'httpapi' 'dev' $file
$again=Get-NetworkIntent 'avnm-private-web' 'httpapi' 'dev' $file
Check ($intent.intentHash -eq $again.intentHash -and $intent.allocationId -eq $again.allocationId) 'Stable request identity failed.'
$other=Get-NetworkIntent 'avnm-private-web' 'httpapi' 'prod' $file
Check ($intent.allocationId -ne $other.allocationId) 'Environment allocation isolation failed.'
$allocation=@{id=$intent.allocationId;properties=@{description=$intent.description;provisioningState='Succeeded';addressPrefixes=@('10.5.6.0/24')}}
$prefixes=Assert-NetworkAllocation $intent $allocation
Check ($prefixes.integrationPrefix -eq '10.5.6.0/26' -and $prefixes.endpointPrefix -eq '10.5.6.64/27') 'Deterministic child prefixes invalid.'
$parameters=Get-ReservedNetworkParameters $intent $allocation
Check ($parameters.allocationId.value -eq $intent.allocationId) 'Network parameters lost authority binding.'
foreach($state in @('Updating','Failed','Deleting')){$allocation.properties.provisioningState=$state;Reject {Assert-NetworkAllocation $intent $allocation}}
$allocation.properties.provisioningState='Succeeded'
foreach($prefix in @('10.5.6.0/25','10.5.6.1/24','999.5.6.0/24','2001:db8::/64')){$allocation.properties.addressPrefixes=@($prefix);Reject {Assert-NetworkAllocation $intent $allocation}}
$allocation.properties.addressPrefixes=@('10.5.6.0/24','10.5.7.0/24');Reject {Assert-NetworkAllocation $intent $allocation}
$allocation.properties.addressPrefixes=@('10.5.6.0/24');$allocation.properties.description='Another owner';Reject {Assert-NetworkAllocation $intent $allocation}
Reject {Get-NetworkIntent 'avnm-private-web' '../inject' 'dev' $file}
$p.owner='changed';$config|ConvertTo-Json -Depth 10|Set-Content $file
$changed=Get-NetworkIntent 'avnm-private-web' 'httpapi' 'dev' $file
Check ($changed.allocationId -eq $intent.allocationId -and $changed.intentHash -ne $intent.intentHash) 'Policy drift must not create a new allocation identity.'
$errors=@();foreach($script in @('network-allocation-common.ps1','Invoke-NetworkAllocation.ps1','Invoke-ReservedNetwork.ps1')){$tokens=$null;$parse=$null;[Management.Automation.Language.Parser]::ParseFile((Join-Path $PSScriptRoot $script),[ref]$tokens,[ref]$parse)|Out-Null;$errors+=@($parse)}
Check ($errors.Count -eq 0) ('PowerShell parser errors: '+($errors -join '; '))
@{passed=$script:cases;azureCalled=$false;allocationCreated=$false}|ConvertTo-Json|Set-Content (Join-Path $out 'results.json')
Write-Host "PASS: $script:cases offline allocation contract cases. No Azure calls or reservations."
