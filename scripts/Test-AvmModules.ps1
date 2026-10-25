#requires -Version 7.4
[CmdletBinding()]
param()
. "$PSScriptRoot/common.ps1"
$root=Get-ProjectRoot
& "$PSScriptRoot/Setup-AvmTooling.ps1" -VerifyOnly
$compiler=Join-Path $root '.tools/bicep/0.48.1/bicep.exe'
if(!(Test-Path -LiteralPath $compiler -PathType Leaf)){throw 'Prepare the reviewed Bicep 0.48.1 CLI under .tools/bicep/0.48.1 before running this offline test.'}
$previousPath=$env:PATH
$previousExtraction=$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
$env:PATH=(Split-Path $compiler)+[IO.Path]::PathSeparator+$env:PATH
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR=Join-Path $root '.local/avm-bicep-bundle-tests'
$fixtures=Join-Path $root ('artifacts/avm-module-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtures -Force|Out-Null

function ConvertTo-AvmBicepLiteral($Value){
    if($Value -is [string]){return "'"+$Value.Replace('\','\\').Replace("'","\'").Replace('${','\${').Replace("`r",'\r').Replace("`n",'\n')+"'"}
    if($Value -is [bool]){return $Value.ToString().ToLowerInvariant()}
    if($Value -is [Collections.IDictionary]){
        $properties=@(foreach($key in $Value.Keys){
            if([string]$key -cnotmatch '^[A-Za-z][A-Za-z0-9]*$'){throw 'Fixture property is not a reviewed Bicep identifier.'}
            [string]$key+': '+(ConvertTo-AvmBicepLiteral $Value[$key])
        })
        return "{`n"+($properties -join "`n")+"`n}"
    }
    if($Value -is [Collections.IEnumerable]){return "[`n"+(@(foreach($item in $Value){ConvertTo-AvmBicepLiteral $item}) -join "`n")+"`n]"}
    if($Value -is [int] -or $Value -is [long]){return [string]$Value}
    throw 'Unsupported fixture literal type.'
}
function Invoke-AvmParamsFixture([string]$Id,[string]$Label,[Collections.IDictionary]$Values,[bool]$ShouldPass){
    # Resolve IDs through the exact reviewed catalog, including resource names containing hyphens.
    $module=@($script:catalog.modules|Where-Object id -CEQ "avm-$Id")
    if($module.Count -ne 1){throw 'Fixture does not select an exact reviewed wrapper.'}
    $source=Join-Path $root $module[0].path
    $relative=[IO.Path]::GetRelativePath($fixtures,$source).Replace('\','/')
    $lines=@("using '"+$relative.Replace("'","\'")+"'")+@(foreach($key in $Values.Keys){'param '+$key+' = '+(ConvertTo-AvmBicepLiteral $Values[$key])})
    $path=Join-Path $fixtures "$Label.bicepparam"
    [IO.File]::WriteAllText($path,($lines -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
    $start=[Diagnostics.ProcessStartInfo]::new($compiler)
    $start.UseShellExecute=$false; $start.CreateNoWindow=$true
    $start.RedirectStandardOutput=$true; $start.RedirectStandardError=$true
    foreach($argument in @('build-params',$path,'--outfile',(Join-Path $fixtures "$Label.json"))){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start)
    try{
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        $process.WaitForExit()
        $log=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult()
        [IO.File]::WriteAllText((Join-Path $fixtures "$Label.log"),$log,[Text.UTF8Encoding]::new($false))
        if(($process.ExitCode -eq 0) -ne $ShouldPass){throw "Unexpected compile result for $Label. Inspect its local fixture log."}
        if(!$ShouldPass -and $log -notmatch 'Error BCP'){throw "Invalid fixture failed without a Bicep contract diagnostic: $Label"}
    }finally{$process.Dispose()}
}
function Assert-AvmFixed([Collections.IDictionary]$Parameters,[string]$Name,$Expected){
    if(!$Parameters.Contains($Name) -or !$Parameters[$Name].Contains('value') -or
        (ConvertTo-Json -InputObject $Parameters[$Name].value -Compress -Depth 10) -cne (ConvertTo-Json -InputObject $Expected -Compress -Depth 10)){
        throw "Wrapper security setting is not fixed to the reviewed value: $Name"
    }
    $script:securityChecks++
}

try{
    & "$PSScriptRoot/Verify-AvmSources.ps1"
    & "$PSScriptRoot/Update-AvmModuleContracts.ps1" -Check
    $script:catalog=Get-Content -LiteralPath (Join-Path $root 'config/avm-module-contracts.json') -Raw|ConvertFrom-Json -AsHashtable
    $script:securityChecks=0
    foreach($module in $catalog.modules){
        $id=$module.id.Substring(4)
        $compiled=Get-Content -LiteralPath (Join-Path $root "artifacts/module-audit/avm/$id.json") -Raw|ConvertFrom-Json -AsHashtable
        $resources=@(if($compiled.resources -is [Collections.IDictionary]){$compiled.resources.Values}else{$compiled.resources})
        if($resources.Count -ne 1 -or $resources[0].type -cne 'Microsoft.Resources/deployments'){throw 'Expected one exact pinned AVM deployment per wrapper.'}
        $parameters=$resources[0].properties.parameters
        Assert-AvmFixed $parameters enableTelemetry $false
        if(@('roleAssignments','lock','webhooks','tasks','tokens','credentialSets')|Where-Object {$parameters.Contains($_)}){throw 'Wrapper exposes an unreviewed role, lock or execution/credential child.'}
        $script:securityChecks++
        if(@('enableTelemetry','publicNetworkAccess','acrAdminUserEnabled','anonymousPullEnabled','disableLocalAuth','roleAssignments')|Where-Object {$module.parameters.Contains($_)}){throw 'Wrapper has an unreviewed security relaxation input.'}
        $script:securityChecks++
        switch($id){
            'identity-user-assigned-identity' {Assert-AvmFixed $parameters isolationScope 'Regional'}
            'containers-container-registry' {
                Assert-AvmFixed $parameters acrSku 'Premium'
                Assert-AvmFixed $parameters acrAdminUserEnabled $false
                Assert-AvmFixed $parameters anonymousPullEnabled $false
                Assert-AvmFixed $parameters publicNetworkAccess 'Disabled'
                Assert-AvmFixed $parameters networkRuleBypassOptions 'None'
                Assert-AvmFixed $parameters networkRuleSetDefaultAction 'Deny'
                Assert-AvmFixed $parameters networkRuleBypassAllowedForTasks $false
                Assert-AvmFixed $parameters networkRuleSetIpRules @()
                Assert-AvmFixed $parameters azureADAuthenticationAsArmPolicyStatus 'disabled'
                Assert-AvmFixed $parameters exportPolicyStatus 'disabled'
            }
            'configuration-app-configuration' {
                Assert-AvmFixed $parameters disableLocalAuth $true
                Assert-AvmFixed $parameters enablePurgeProtection $true
                Assert-AvmFixed $parameters publicNetworkAccess 'Disabled'
                Assert-AvmFixed $parameters dataPlaneProxy ([ordered]@{authenticationMode='Pass-through';privateLinkDelegation='Enabled'})
            }
            'monitoring-application-insights' {
                Assert-AvmFixed $parameters disableLocalAuth $true
                Assert-AvmFixed $parameters disableIpMasking $false
                Assert-AvmFixed $parameters publicNetworkAccessForIngestion 'Disabled'
                Assert-AvmFixed $parameters publicNetworkAccessForQuery 'Disabled'
                if(@('instrumentationKey','connectionString')|Where-Object {$module.outputs.Contains($_)}){throw 'Telemetry routing values must not be wrapper outputs.'}
                $script:securityChecks++
            }
            'network-network-security-group' {Assert-AvmFixed $parameters flushConnection $false}
        }
    }

    $examples=[ordered]@{
        'identity-user-assigned-identity'=@{name='synthetic-identity';federatedIdentityCredentials=@(@{name='synthetic-federation';issuer='https://issuer.example.test/';subject='repo:example/repo:environment:reviewed';audiences=@('api://AzureADTokenExchange')})}
        'containers-container-registry'=@{name='syntheticavmregistry';replications=@(@{name='westus3';location='westus3';zoneRedundancy='Disabled'})}
        'configuration-app-configuration'=@{name='synthetic-avm-config';keyValues=@(@{name='synthetic-setting';value='reviewed non-secret setting';contentType='text/plain'});replicaLocations=@(@{name='syntheticreplica';replicaLocation='westus3'})}
        'monitoring-application-insights'=@{name='synthetic-app-insights';workspaceId='/subscriptions/11111111-1111-1111-1111-111111111111/resourceGroups/synthetic/providers/Microsoft.OperationalInsights/workspaces/synthetic'}
        'network-network-security-group'=@{name='synthetic-nsg';securityRules=@(@{name='deny-inbound';properties=@{priority=100;access='Deny';direction='Inbound';protocol='*';sourceAddressPrefix='*';sourcePortRange='*';destinationAddressPrefix='*';destinationPortRange='*'}})}
        'network-route-table'=@{name='synthetic-route-table';routes=@(@{name='drop-review-range';properties=@{addressPrefix='10.250.0.0/16';nextHopType='None'}});applianceRoutes=@(@{name='reviewed-appliance';properties=@{addressPrefix='10.251.0.0/16';nextHopIpAddress='10.250.0.4'}})}
    }
    $positive=0
    foreach($id in $examples.Keys){
        $examples[$id].location='eastus2';$examples[$id].tags=@{owner='synthetic-fixture'}
        Invoke-AvmParamsFixture $id "positive-$id" $examples[$id] $true
        $positive++
    }
    $negative=0
    foreach($case in @('unknown-child-property','invalid-replica-enum','missing-appliance-ip','invalid-federation-audience')){
        $id=switch($case){'unknown-child-property' {'network-network-security-group'} 'invalid-replica-enum' {'containers-container-registry'} 'missing-appliance-ip' {'network-route-table'} 'invalid-federation-audience' {'identity-user-assigned-identity'}}
        $values=ConvertTo-Json -InputObject $examples[$id] -Depth 20|ConvertFrom-Json -AsHashtable
        switch($case){
            'unknown-child-property' {$values.securityRules[0].properties.unreviewed=$true}
            'invalid-replica-enum' {$values.replications[0].zoneRedundancy='Bypass'}
            'missing-appliance-ip' {$values.applianceRoutes[0].properties.Remove('nextHopIpAddress')}
            'invalid-federation-audience' {$values.federatedIdentityCredentials[0].audiences=@('invented-audience')}
        }
        Invoke-AvmParamsFixture $id "negative-$case" $values $false
        $negative++
    }
    $children=@($catalog.modules|ForEach-Object {$_.parameters.Values}|Where-Object {$_.type -eq 'array' -and $_.Contains('items') -and $_.items.type -eq 'object'})
    if($children.Count -ne 7 -or @($children|Where-Object {!$_.Contains('defaultValue') -or @($_.defaultValue).Count -ne 0}).Count){throw 'All seven reviewed child collections must default to empty.'}
    $script:securityChecks++
    $result=[ordered]@{schema='platform.avm-module-tests/v1';generatedUtc=[DateTimeOffset]::UtcNow.ToString('o');compilerVersion='0.48.1';modules=$catalog.modules.Count;childCollections=$children.Count;securityChecks=$securityChecks;positiveParameterCompilations=$positive;invalidParameterRejections=$negative;azureCalls=$false;agentInference=$false;deploymentQualified=$false;fixtures=[IO.Path]::GetRelativePath($root,$fixtures).Replace('\','/')}
    $result|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $fixtures 'results.json') -Encoding utf8NoBOM
    Write-Host "PASS: $positive nonempty-child parameter fixtures compiled; $negative invalid fixtures rejected; $securityChecks fixed security/default checks. No Azure or model calls. Evidence: $fixtures"
}finally{
    $env:PATH=$previousPath
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR=$previousExtraction
}
