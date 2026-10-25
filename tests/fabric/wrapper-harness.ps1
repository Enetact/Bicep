param([string]$Mode,[string]$ManifestPath,[string]$PlanPath,[string]$ReceiptPath,[string]$ExpectedPlanSha256,[string]$StatePath,[string]$Fault='none')
$ErrorActionPreference='Stop'
$manifest=Get-Content $ManifestPath -Raw|ConvertFrom-Json -AsHashtable
$state=Get-Content $StatePath -Raw|ConvertFrom-Json -AsHashtable
$fixture=$null
if($manifest.Contains('execution')){
    . "$PSScriptRoot/../../scripts/fabric-workspace-common.ps1"
    . "$PSScriptRoot/../../scripts/fabric-execution-common.ps1"
    . "$PSScriptRoot/execution-fixtures.ps1"
    $fixtureManifest=$manifest|ConvertTo-Json -Depth 50|ConvertFrom-Json -AsHashtable
    if(!$fixtureManifest.execution.Contains('privateWorkspace')){$fixtureManifest.execution.privateWorkspace=(New-FabricExecutionFixtureManifest).execution.privateWorkspace}
    $fixture=New-FabricExecutionFixture $fixtureManifest
    Set-FabricExecutionFixtureEnvironment $manifest
    if($Fault -eq 'execution-drift'){
        $env:SYSTEM_TEAMPROJECTID='80000000-0000-0000-0000-000000000099'
        $fixture.adoAgent.assignedRequest.scopeId=$env:SYSTEM_TEAMPROJECTID
    }
    if($Fault -eq 'agent-job'){$fixture.adoAgent.assignedRequest.jobId='80000000-0000-0000-0000-000000000099'}
    if($Fault -eq 'new-agent'){
        $env:AGENT_ID='8';$fixture.adoAgent.id=8
        $env:SYSTEM_JOBID='80000000-0000-0000-0000-000000000098'
        $fixture.adoAgent.assignedRequest.jobId=$env:SYSTEM_JOBID
    }
    if($Fault -eq 'pool-config'){$fixture.arm[$manifest.execution.poolResourceId].properties.organizationProfile.organizations[0].openAccess=$true}
    if($Fault -eq 'pool-binding'){$env:BOUND_FABRIC_AGENT_POOL='other-private-pool'}
}else{
    # A calling pipeline's environment must not turn the legacy offline fixture into a managed-pool test.
    Remove-Item Env:BOUND_FABRIC_AGENT_POOL -ErrorAction SilentlyContinue
    if($Fault -eq 'unbound-pipeline'){$env:BOUND_FABRIC_AGENT_POOL='private-build-agents'}
}
function Save-State {$state|ConvertTo-Json -Depth 30|Set-Content $StatePath -Encoding utf8}
function Encode-Claim($Value){[Convert]::ToBase64String([Text.Encoding]::UTF8.GetBytes(($Value|ConvertTo-Json -Compress))).TrimEnd('=').Replace('+','-').Replace('/','_')}
function global:az {
    $state.authCalls++;Save-State;$global:LASTEXITCODE=0
    if($args[0] -eq 'account' -and $args[1] -eq 'show'){return (@{tenantId=$manifest.tenantId;id=$manifest.subscriptionId}|ConvertTo-Json -Compress)}
    if($args[0] -eq 'resource' -and $args[1] -eq 'show'){
        $id=[string]$args[[array]::IndexOf($args,'--ids')+1]
        if(!$fixture -or !$fixture.arm.Contains($id)){throw "Unexpected synthetic ARM resource: $id"}
        return ($fixture.arm[$id]|ConvertTo-Json -Depth 50 -Compress)
    }
    if($args[0] -eq 'role' -and $args[1] -eq 'assignment'){
        if(!$fixture){throw 'Unexpected synthetic role read.'}
        return (ConvertTo-Json -InputObject $fixture.grants -Depth 30 -Compress)
    }
    if($args[0] -eq 'rest'){
        $uri=[uri]$args[[array]::IndexOf($args,'--url')+1]
        if(!$fixture -or $uri.Host -cne 'management.azure.com' -or !$fixture.collections.Contains($uri.AbsolutePath)){throw 'Unexpected synthetic ARM collection.'}
        return (@{value=@($fixture.collections[$uri.AbsolutePath])}|ConvertTo-Json -Depth 50 -Compress)
    }
    if($args[0] -ne 'account' -or $args[1] -ne 'get-access-token'){throw 'Unexpected synthetic Azure CLI command.'}
    $oid=if($Fault -eq 'identity'){'99999999-9999-9999-9999-999999999999'}else{$manifest.deploymentPrincipalObjectId}
    $claims=@{tid=$manifest.tenantId;oid=$oid;aud='https://api.fabric.microsoft.com';exp=[DateTimeOffset]::UtcNow.AddHours(1).ToUnixTimeSeconds()}
    return (@{accessToken='e30.'+(Encode-Claim $claims)+'.synthetic'}|ConvertTo-Json -Compress)
}
function global:Invoke-WebRequest {
    param($Uri,$Method,$Headers,$Body,$ContentType,$MaximumRedirection,$TimeoutSec,[switch]$SkipHttpErrorCheck)
    $state.apiCalls++;Save-State
    $u=[uri]$Uri;$path=$u.AbsolutePath
    if($u.Host -ceq 'dev.azure.com'){
        if(!$fixture -or $Method -ine 'GET' -or $Headers.Authorization -cne ('Bearer '+$env:SYSTEM_ACCESSTOKEN)){throw 'Unexpected synthetic Azure DevOps credential/method.'}
        $base=([uri]$manifest.execution.adoOrganizationUrl).AbsolutePath.TrimEnd('/')+'/_apis/distributedtask/pools/'+$manifest.execution.adoPoolId
        if($path -ceq $base){$result=$fixture.adoPool}
        elseif($path -ceq ($base+'/agents/'+$env:AGENT_ID)){$result=$fixture.adoAgent}
        else{throw 'Unexpected synthetic Azure DevOps route.'}
    }elseif($u.Host -cne 'api.fabric.microsoft.com'){
        throw 'The wrapper lifecycle fixture never performs private DNS/TCP or sends traffic to another host.'
    }elseif($Method -ieq 'GET' -and $path -eq '/v1/capacities'){
        $result=@{value=@(@{id=$manifest.operation.capacityId;displayName=$manifest.operation.capacityDisplayName;state='Active';sku=$(if($Fault -eq 'drift'){'F128'}else{'F64'})})}
    }elseif($Method -ieq 'GET' -and $path -eq '/v1/workspaces'){
        $items=if($state.created){@(@{id=$state.createdId;displayName=$manifest.operation.displayName;capacityId=$manifest.operation.capacityId})}else{@()}
        $result=@{value=@($items)}
    }elseif($Method -ieq 'POST' -and $path -eq '/v1/workspaces'){
        $state.writeCalls++;$state.created=$true;Save-State
        if($Fault -eq 'unknown'){throw 'Synthetic response lost after remote commit.'}
        $result=@{id=$state.createdId}
    }elseif($Method -ieq 'GET' -and $path -eq ('/v1/workspaces/'+$state.createdId)){
        $result=@{id=$state.createdId;displayName=$(if($Fault -eq 'verify-name'){'wrong'}else{$manifest.operation.displayName});capacityId=$manifest.operation.capacityId}
    }else{throw "Unexpected synthetic route: $Method $path"}
    return @{StatusCode=200;Content=($result|ConvertTo-Json -Depth 30 -Compress);Headers=@{}}
}
try{
    & "$PSScriptRoot/../../scripts/Invoke-FabricWorkspace.ps1" -Mode $Mode -ManifestPath $ManifestPath -PlanPath $PlanPath -ReceiptPath $ReceiptPath -ExpectedPlanSha256 $ExpectedPlanSha256
    exit $LASTEXITCODE
}catch{Write-Output $_.Exception.Message;exit 1}
