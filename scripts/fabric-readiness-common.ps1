# Offline projection only. Live prerequisites remain exclusively in the protected Fabric pipeline.
. "$PSScriptRoot/fabric-workspace-common.ps1"
. "$PSScriptRoot/fabric-execution-common.ps1"

function Read-FabricReadinessSource([string]$Root,[string]$Relative,[Collections.Generic.List[object]]$Sources) {
    $base=[IO.Path]::GetFullPath($Root).TrimEnd([IO.Path]::DirectorySeparatorChar,[IO.Path]::AltDirectorySeparatorChar)
    $path=[IO.Path]::GetFullPath((Join-Path $base $Relative))
    if(!$path.StartsWith($base+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase)){throw 'Untrusted readiness source.'}
    $cursor=$path
    while($cursor){
        if((Test-Path -LiteralPath $cursor) -and ((Get-Item -LiteralPath $cursor -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'Untrusted readiness source.'}
        $parent=Split-Path -Path $cursor -Parent
        if($parent -eq $cursor){break};$cursor=$parent
    }
    $file=Get-Item -LiteralPath $path -Force -ErrorAction Stop
    if($file.PSIsContainer -or $file.Length -gt 262144){throw 'Readiness source unavailable.'}
    $bytes=[IO.File]::ReadAllBytes($path)
    if($bytes.Length -gt 262144){throw 'Readiness source unavailable.'}
    $Sources.Add(@{path=$Relative;sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()})
    return [Text.UTF8Encoding]::new($false,$true).GetString($bytes).TrimStart([char]0xfeff)
}
function ConvertFrom-FabricReadinessJson([string]$Text) {
    $value=ConvertFrom-Json -InputObject $Text -AsHashtable -NoEnumerate -Depth 80
    if($value -isnot [Collections.IDictionary]){throw 'Readiness source is not an object.'}
    return $value
}
function New-FabricReadinessLiveChecks {
    @(
        @{id='runtime-managed-pool';label='Managed pool and DevCenter';status='NotVerified';detail='The protected pipeline reads the exact Azure pool and DevCenter project, including provisioning, region, stateless agents, image pin, concurrency and approved Azure DevOps access.'},
        @{id='runtime-agent-subnet';label='Agent subnet and network permissions';status='NotVerified';detail='The pipeline checks the exclusive managed-pool delegation, address capacity, pool ownership, the reserved 172.17 range, and reviewed Reader and Network Contributor grants.'},
        @{id='runtime-agent-job';label='Current job assignment';status='NotVerified';detail='The pipeline checks its Azure DevOps job token, pool ID, active agent, project and assigned job. Browser sign-in does not establish these permissions.'},
        @{id='runtime-private-endpoint';label='Private endpoint and DNS configuration';status='NotVerified';detail='The pipeline requires an approved endpoint for the exact workspace and tenant, a separate nondelegated subnet in the agent VNet, and a completed direct Azure DNS link.'},
        @{id='runtime-private-reachability';label='Private workspace API reachability';status='NotVerified';detail='From the managed agent, the workspace API hostname must resolve only to its endpoint NIC private IPv4 addresses and accept TCP 443. Public fallback is rejected.'},
        @{id='runtime-public-policy';label='Workspace denies public access';status='NotVerified';detail='The pipeline independently reads the existing Deny policy through the global Fabric policy API. That read needs permitted outbound access and tenant policy; the workflow does not change the firewall.'},
        @{id='runtime-plan-verification';label='Reviewed plan, drift and read-back';status='NotVerified';detail='Apply and Verify recheck identity, source hashes, pool and private-network evidence against the exact Preview plan. A configured profile or an ARM deployment is not workspace acceptance.'}
    )
}
function Get-FabricReadiness([string]$Root,[string]$EnvironmentName,[string]$Region) {
    if($EnvironmentName -cnotin @('dev','qa','uat','prod') -or $Region -cnotmatch '^[a-z][a-z0-9]{2,39}$'){throw 'Select a registered Fabric environment and region.'}
    $sources=[Collections.Generic.List[object]]::new()
    $checks=[Collections.Generic.List[object]]::new()
    $result=@{
        schemaVersion=1;environment=$EnvironmentName;region=$Region;checkedUtc=[DateTimeOffset]::UtcNow.ToString('o')
        scope='ReviewedSourceOnly';status='Unavailable';canDeploy=$false;liveVerified=$false;poolName='Not configured';workspaceMode='Unconfigured'
        privateProfile='Same VNet / Azure DNS / IPv4';pipeline='azure-pipelines-fabric-workspace.yml';sources=@();checks=@()
        limitations=@(
            'Native Fabric deployment pipelines cannot promote to deny-public workspaces. A private managed agent does not remove this limitation.',
            'Private execution supports lakehouse creation, workspace identity provisioning, admin-group assignment and managed private endpoint creation. Global workspace, connection and deployment-pipeline operations need their separate reviewed profile.',
            'This private profile requires the same VNet, direct Azure DNS and private IPv4. Custom DNS, forwarders and peered VNets require separate qualification.'
        )
    }
    $previousPool=[Environment]::GetEnvironmentVariable('BOUND_FABRIC_AGENT_POOL','Process')
    $previousContext=$script:FabricApiContext
    try {
        [Environment]::SetEnvironmentVariable('BOUND_FABRIC_AGENT_POOL',$null,'Process')
        $unapprovedRegion=$false
        try {
            $platform=ConvertFrom-FabricReadinessJson (Read-FabricReadinessSource $Root 'config/platform.json' $sources)
            if($platform.approvedRegions -isnot [array] -or !$platform.approvedRegions.Count -or $platform.approvedRegions.Count -gt 32 -or @($platform.approvedRegions|Where-Object {$_ -isnot [string] -or $_ -cnotmatch '^[a-z][a-z0-9]{2,39}$'}).Count){throw 'Readiness region source is invalid.'}
            if($Region -cnotin $platform.approvedRegions){$unapprovedRegion=$true;throw 'Unapproved region.'}
            $manifest=ConvertFrom-FabricReadinessJson (Read-FabricReadinessSource $Root "self-service/fabric/workspace.$EnvironmentName.json" $sources)
            $target=ConvertFrom-FabricReadinessJson (Read-FabricReadinessSource $Root "self-service/targets/fabric.$EnvironmentName.json" $sources)
            $parameters=Read-FabricReadinessSource $Root "workloads/managed-devops-pool/environments/main.$EnvironmentName.bicepparam" $sources
            $bindings=Read-FabricReadinessSource $Root 'pipelines/fabric-execution-bindings.yml' $sources
            # Include the exact reused validators in the local evidence fingerprint.
            foreach($path in @('scripts/fabric-workspace-common.ps1','scripts/fabric-execution-common.ps1','scripts/managed-devops-pool-common.ps1')){$null=Read-FabricReadinessSource $Root $path $sources}
        } catch {
            if($unapprovedRegion){throw 'Select a registered Fabric environment and region.'}
            $checks.Add(@{id='source-unavailable';label='Reviewed source configuration';status='NeedsConfiguration';detail='One or more reviewed source files are missing, malformed, oversized or linked outside the checkout. Review the registered Fabric source files locally; source contents and errors are withheld.'})
            foreach($check in New-FabricReadinessLiveChecks){$checks.Add($check)}
            $result.sources=$sources.ToArray();$result.checks=$checks.ToArray();return $result
        }
        $e=if($manifest.Contains('execution') -and $manifest.execution -is [Collections.IDictionary]){$manifest.execution}else{@{}}
        $targetValid=($target['schemaVersion'] -is [int] -or $target['schemaVersion'] -is [long]) -and $target['schemaVersion'] -eq 3 -and $target['workloadType'] -is [string] -and $target['workloadType'] -ceq 'fabric' -and $target['workload'] -is [string] -and $target['workload'] -ceq 'fabric' -and $target['environmentName'] -is [string] -and $target['environmentName'] -ceq $EnvironmentName -and $target['enabled'] -is [bool]
        $onboarded=$targetValid -and $manifest['enabled'] -is [bool] -and $target['enabled'] -and $manifest['enabled']
        $checks.Add(@{id='source-onboarding';label='Deployment onboarding';status=$(if($onboarded){'Configured'}else{'NeedsConfiguration'});detail=$(if($onboarded){'Both reviewed source enablement flags are set. Protected approvals and live pipeline checks are still required.'}else{'Fabric infrastructure and workspace targets remain blocked until both reviewed source flags and platform onboarding are complete. This page cannot enable deployment.'})})
        $identityValid=$false
        try {
            foreach($key in @('tenantId','subscriptionId','deploymentPrincipalObjectId')){Assert-FabricGuid $manifest[$key]}
            Assert-FabricGuid $target['subscriptionId']
            $identityValid=$targetValid -and $manifest['environmentName'] -ceq $EnvironmentName -and $manifest['subscriptionId'] -ieq $target['subscriptionId']
        }catch{}
        $checks.Add(@{id='source-identity';label='Tenant, subscription and deployment identity';status=$(if($identityValid){'Configured'}else{'NeedsConfiguration'});detail=$(if($identityValid){'Reviewed nonzero identities and environment agree with the registered Fabric target. Live identity and permissions remain unverified.'}else{'Complete the tenant, matching subscription and deployment principal in the workspace manifest for this environment. Zero identifiers and mismatched target scope are not configured.'})})
        $manifestValid=$false
        try{Assert-FabricManifest $manifest;$manifestValid=$true}catch{}
        $checks.Add(@{id='source-operation';label='Reviewed Fabric operation';status=$(if($manifestValid){'Configured'}else{'NeedsConfiguration'});detail=$(if($manifestValid){'The source manifest passes the typed schema checks used by deployment. API support, current resource state and authorization are checked by the pipeline.'}else{'Complete one supported reviewed operation and its required identifiers. Unknown fields, placeholders, malformed operations and credentials are rejected by the deployment validator.'})})
        $alias=[string]''
        $bindingValid=$false
        if($e.Contains('adoPoolName') -and $e.adoPoolName -is [string] -and $e.adoPoolName -cmatch '^[A-Za-z0-9][A-Za-z0-9-]{1,62}[A-Za-z0-9]$' -and $e.adoPoolName -notmatch 'REPLACE_'){
            $parameterAlias=[regex]::Matches($parameters,"(?m)^param adoPoolName = '([^'\r\n]+)'\r?$")
            $bindingPattern='(?m)^  - \$\{\{ if eq\(parameters\.environment, '''+[regex]::Escape($EnvironmentName)+'''\) \}\}:\r?\n    - name: fabricAgentPool\r?\n      value: ([A-Za-z0-9][A-Za-z0-9-]{1,62}[A-Za-z0-9])\r?\n      readonly: true\r?$'
            $pipelineAlias=[regex]::Matches($bindings,$bindingPattern)
            $bindingValid=$targetValid -and $target['agentPool'] -is [string] -and $target['agentPool'] -ceq $e.adoPoolName -and $parameterAlias.Count -eq 1 -and $parameterAlias[0].Groups[1].Value -ceq $e.adoPoolName -and $pipelineAlias.Count -eq 1 -and $pipelineAlias[0].Groups[1].Value -ceq $e.adoPoolName
            if($bindingValid){$alias=$e.adoPoolName;$result.poolName=$alias}
        }
        $checks.Add(@{id='source-pool-binding';label='Managed pool selection';status=$(if($bindingValid){'Configured'}else{'NeedsConfiguration'});detail=$(if($bindingValid){'The Fabric manifest, protected infrastructure target, managed-pool parameters and read-only pipeline binding select the same agent pool. This does not prove the pool exists.'}else{'Use the same safe pool alias in the Fabric manifest, infrastructure target, managed-pool environment parameters and read-only workspace pipeline binding.'})})
        $profileValid=$false
        try {
            if(!$e.Count){throw 'Missing execution profile.'}
            $poolManifest=$manifest | ConvertTo-Json -Depth 80 | ConvertFrom-Json -AsHashtable -NoEnumerate
            $null=$poolManifest.execution.Remove('privateWorkspace')
            Assert-FabricExecutionProfile $poolManifest
            $profileValid=$true
        }catch{}
        $checks.Add(@{id='source-pool-profile';label='Managed pool requirements';status=$(if($profileValid){'Configured'}else{'NeedsConfiguration'});detail=$(if($profileValid){'The reviewed profile passes the shared deployment validator for the pool resource, Azure DevOps pool ID and project, DevCenter, dedicated subnet, immutable Windows Gen2 image, administrators and bounded sizing.'}else{'Complete the exact Azure pool resource and Azure DevOps pool ID, organization/project, same-region DevCenter and dedicated agent subnet, administrator groups, service principal permissions and immutable Windows Gen2 image version.'})})
        $regionValid=$e.Contains('location') -and $e.location -is [string] -and $e.location -ceq $Region
        $checks.Add(@{id='source-region';label='Selected deployment region';status=$(if($regionValid){'Configured'}else{'NeedsConfiguration'});detail=$(if($regionValid){'The selected region agrees with the reviewed managed-pool profile. Azure must still confirm the pool, DevCenter and VNet locations.'}else{'The selected region must match the reviewed managed-pool profile. Review its region before running the protected workflow.'})})
        $privateValid=$false;$privateUnsupported=$false
        if($e.Contains('privateWorkspace')){
            $result.workspaceMode='Private'
            $privateUnsupported=$manifest['operation'] -is [Collections.IDictionary] -and $manifest.operation['kind'] -cnotin @('CreateLakehouse','ProvisionIdentity','AddWorkspaceAdminGroup','CreateManagedPrivateEndpoint')
            try{Assert-FabricExecutionProfile $manifest;Set-FabricApiContext $manifest;$privateValid=$manifestValid -and $profileValid}catch{}
        }elseif($manifest['operation'] -is [Collections.IDictionary]){$result.workspaceMode='Bootstrap'}
        $checks.Add(@{id='source-private-workspace';label='Existing private workspace profile';status=$(if($privateUnsupported){'Unsupported'}elseif($privateValid){'Configured'}else{'NeedsConfiguration'});detail=$(if($privateUnsupported){'This selected operation requires a global API and cannot run through the private workspace profile. Native deployment pipelines cannot bypass a deny-public workspace.'}elseif($privateValid){'The reviewed workspace, canonical API hostname, endpoint, agent VNet and Fabric DNS zone pass the same source validation used by deployment. Endpoint approval, DNS and connectivity are still unverified.'}elseif($result.workspaceMode -ceq 'Bootstrap'){'The current manifest uses the global bootstrap profile. After workspace creation, review an existing-workspace private profile with its exact workspace, endpoint, VNet and Fabric DNS zone bindings.'}else{'Complete the existing workspace UUID, matching workspace API hostname, endpoint, same agent VNet and Fabric private DNS zone. The private profile only permits supported workspace-context operations.'})})
        foreach($check in New-FabricReadinessLiveChecks){$checks.Add($check)}
        $result.status=if(@($checks | Where-Object {$_.status -cin @('NeedsConfiguration','Unsupported')}).Count){'NeedsConfiguration'}else{'AwaitingPipelineVerification'}
        $result.sources=$sources.ToArray();$result.checks=$checks.ToArray();return $result
    } finally {
        [Environment]::SetEnvironmentVariable('BOUND_FABRIC_AGENT_POOL',$previousPool,'Process')
        $script:FabricApiContext=$previousContext
    }
}
