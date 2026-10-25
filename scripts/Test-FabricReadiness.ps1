#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
. "$PSScriptRoot/fabric-readiness-common.ps1"
. "$PSScriptRoot/../tests/fabric/execution-fixtures.ps1"
$repository=Split-Path $PSScriptRoot -Parent
$directory=Join-Path $repository ('artifacts/fabric-readiness-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory -Force|Out-Null
$results=[Collections.Generic.List[object]]::new()
function Assert-Readiness([bool]$Condition,[string]$Message){if(!$Condition){throw $Message}}
function Write-ReadinessFile([string]$Path,[string]$Text){New-Item -ItemType Directory -Path (Split-Path $Path -Parent) -Force|Out-Null;[IO.File]::WriteAllText($Path,$Text,[Text.UTF8Encoding]::new($false))}
function Write-ReadinessJson($Value,[string]$Path){Write-ReadinessFile $Path ($Value|ConvertTo-Json -Depth 80)}
function New-ReadinessFixture([string]$Name){
    $script:fixture=Join-Path $directory $Name
    $script:manifest=New-FabricExecutionFixtureManifest
    $script:target=@{schemaVersion=3;workloadType='fabric';workload='fabric';environmentName='dev';enabled=$true;subscriptionId=$manifest.subscriptionId;agentPool=$manifest.execution.adoPoolName}
    Write-ReadinessJson @{approvedRegions=@('eastus2','westus2')} (Join-Path $fixture config/platform.json)
    Write-ReadinessJson $manifest (Join-Path $fixture self-service/fabric/workspace.dev.json)
    Write-ReadinessJson $target (Join-Path $fixture self-service/targets/fabric.dev.json)
    Write-ReadinessFile (Join-Path $fixture workloads/managed-devops-pool/environments/main.dev.bicepparam) "param adoPoolName = '$($manifest.execution.adoPoolName)'`n"
    Write-ReadinessFile (Join-Path $fixture pipelines/fabric-execution-bindings.yml) @'
parameters:
  - name: environment
    type: string
    values: [dev, qa, uat, prod]
variables:
  - ${{ if eq(parameters.environment, 'dev') }}:
    - name: fabricAgentPool
      value: private-build-agents
      readonly: true
'@
    foreach($path in @('scripts/fabric-workspace-common.ps1','scripts/fabric-execution-common.ps1','scripts/managed-devops-pool-common.ps1')){Write-ReadinessFile (Join-Path $fixture $path) ([IO.File]::ReadAllText((Join-Path $repository $path)))}
}
function Test-ReadinessCase([string]$Name,[scriptblock]$Action){
    New-ReadinessFixture $Name
    try{& $Action|Out-Null;$results.Add(@{name=$Name;passed=$true})}
    catch{$results.Add(@{name=$Name;passed=$false;error=$_.Exception.Message});Write-Host "FAIL: $Name - $($_.Exception.Message)"}
}
function Get-Check($Report,[string]$Id){@($Report.checks|Where-Object id -ceq $Id)[0]}
function Read-Fixture {Get-FabricReadiness $fixture dev eastus2}
function Save-Manifest {Write-ReadinessJson $manifest (Join-Path $fixture self-service/fabric/workspace.dev.json)}
function Save-Target {Write-ReadinessJson $target (Join-Path $fixture self-service/targets/fabric.dev.json)}
# Any accidental expansion into a live prerequisite flow fails in this suite.
function az {throw 'External Azure CLI access forbidden.'}
function Invoke-WebRequest {throw 'External HTTP access forbidden.'}
function Test-FabricExecutionRequirements {throw 'Live Fabric execution requirements must not be invoked by local readiness.'}
function Resolve-FabricExecutionAddresses {throw 'External DNS access forbidden.'}
function Test-FabricExecutionTcp {throw 'External TCP access forbidden.'}
$previousPool=[Environment]::GetEnvironmentVariable('BOUND_FABRIC_AGENT_POOL','Process')
$previousContext=$script:FabricApiContext
try{
    Test-ReadinessCase 'Complete private source remains awaiting live pipeline verification' {
        $r=Read-Fixture
        Assert-Readiness ($r.schemaVersion -eq 1 -and $r.status -ceq 'AwaitingPipelineVerification' -and !$r.canDeploy -and !$r.liveVerified) 'Configuration was treated as deployment acceptance.'
        Assert-Readiness ($r.scope -ceq 'ReviewedSourceOnly' -and $r.workspaceMode -ceq 'Private' -and $r.poolName -ceq 'private-build-agents') 'Readiness scope projection changed.'
        Assert-Readiness ($r.checks.Count -eq 14 -and @($r.checks|Where-Object {$_.id -clike 'runtime-*' -and $_.status -cne 'NotVerified'}).Count -eq 0) 'A live check was inferred from source.'
        Assert-Readiness ($r.sources.Count -eq 8 -and @($r.sources|Where-Object {$_.sha256 -cnotmatch '^[0-9a-f]{64}$' -or [IO.Path]::IsPathRooted($_.path)}).Count -eq 0) 'Source evidence hash or relative paths invalid.'
        $json=$r|ConvertTo-Json -Depth 50
        Assert-Readiness ($json -notmatch [regex]::Escape($manifest.subscriptionId) -and $json -notmatch [regex]::Escape($manifest.tenantId) -and $json -notmatch '/subscriptions/') 'Private identifiers escaped the readiness projection.'
    }
    Test-ReadinessCase 'Inherited bound-pool environment is ignored and restored' {
        $env:BOUND_FABRIC_AGENT_POOL='foreign-inherited-pool'
        $script:FabricApiContext=@{baseUrl='https://synthetic.invalid';workspaceId='prior-context'}
        $r=Read-Fixture
        Assert-Readiness ($r.status -ceq 'AwaitingPipelineVerification' -and $env:BOUND_FABRIC_AGENT_POOL -ceq 'foreign-inherited-pool' -and $script:FabricApiContext.workspaceId -ceq 'prior-context') 'Source assessment altered or inherited runner state.'
    }
    Test-ReadinessCase 'Disabled source flags remain visible blockers' {
        $manifest.enabled=$false;$target.enabled=$false;Save-Manifest;Save-Target;$r=Read-Fixture
        Assert-Readiness ($r.status -ceq 'NeedsConfiguration' -and (Get-Check $r source-onboarding).status -ceq 'NeedsConfiguration' -and !$r.canDeploy) 'Disabled onboarding was hidden or bypassed.'
    }
    Test-ReadinessCase 'Manifest identity must agree with registered target' {
        $target.subscriptionId='90000000-0000-0000-0000-000000000009';Save-Target;$r=Read-Fixture
        Assert-Readiness ((Get-Check $r source-identity).status -ceq 'NeedsConfiguration') 'Cross-subscription source mismatch accepted.'
    }
    Test-ReadinessCase 'Zero deployment identity remains incomplete' {
        $manifest.deploymentPrincipalObjectId='00000000-0000-0000-0000-000000000000';Save-Manifest;$r=Read-Fixture
        Assert-Readiness ((Get-Check $r source-identity).status -ceq 'NeedsConfiguration' -and (Get-Check $r source-operation).status -ceq 'NeedsConfiguration') 'Zero identity accepted.'
    }
    foreach($source in @('target','parameters','binding')){
        Test-ReadinessCase "Pool alias drift in $source blocks configuration" {
            switch($source){
                target{$target.agentPool='foreign-agents';Save-Target}
                parameters{Write-ReadinessFile (Join-Path $fixture workloads/managed-devops-pool/environments/main.dev.bicepparam) "param adoPoolName = 'foreign-agents'`n"}
                binding{$path=Join-Path $fixture pipelines/fabric-execution-bindings.yml;Write-ReadinessFile $path ([IO.File]::ReadAllText($path).Replace('private-build-agents','foreign-agents'))}
            }
            $r=Read-Fixture;Assert-Readiness ((Get-Check $r source-pool-binding).status -ceq 'NeedsConfiguration' -and $r.poolName -ceq 'Not configured') 'Unbound pool name was displayed as configured.'
        }
    }
    Test-ReadinessCase 'Duplicate parameter alias is rejected' {
        Add-Content (Join-Path $fixture workloads/managed-devops-pool/environments/main.dev.bicepparam) "param adoPoolName = 'private-build-agents'";$r=Read-Fixture
        Assert-Readiness ((Get-Check $r source-pool-binding).status -ceq 'NeedsConfiguration') 'Ambiguous source alias accepted.'
    }
    Test-ReadinessCase 'Mutable pipeline variable binding is rejected' {
        $path=Join-Path $fixture pipelines/fabric-execution-bindings.yml;Write-ReadinessFile $path ([IO.File]::ReadAllText($path).Replace('readonly: true','readonly: false'));$r=Read-Fixture
        Assert-Readiness ((Get-Check $r source-pool-binding).status -ceq 'NeedsConfiguration') 'Mutable source pool binding accepted.'
    }
    Test-ReadinessCase 'Rolling image label fails the shared managed-pool validator' {
        $manifest.execution.agentImageVersion='latest';Save-Manifest;$r=Read-Fixture
        Assert-Readiness ((Get-Check $r source-pool-profile).status -ceq 'NeedsConfiguration') 'Mutable image accepted.'
    }
    Test-ReadinessCase 'Private API host cannot leave the selected workspace' {
        $manifest.execution.privateWorkspace.apiBaseUrl='https://untrusted.invalid';Save-Manifest;$r=Read-Fixture
        Assert-Readiness ((Get-Check $r source-private-workspace).status -ceq 'NeedsConfiguration') 'Foreign private host accepted.'
    }
    Test-ReadinessCase 'Native promotion cannot be classified as a private operation' {
        $manifest.operation=@{kind='PromoteItems';deploymentPipelineId='40000000-0000-0000-0000-000000000001';sourceStageId='40000000-0000-0000-0000-000000000002';targetStageId='40000000-0000-0000-0000-000000000003';items=@()};Save-Manifest;$r=Read-Fixture
        Assert-Readiness ((Get-Check $r source-private-workspace).status -ceq 'Unsupported' -and $r.status -ceq 'NeedsConfiguration') 'Native private pipeline restriction was hidden.'
    }
    Test-ReadinessCase 'Bootstrap profile is distinct from private workspace readiness' {
        $manifest.execution.Remove('privateWorkspace');$manifest.operation=@{kind='CreateWorkspace';displayName='Claims analytics';capacityId='20000000-0000-0000-0000-000000000001';capacityDisplayName='Claims capacity'};Save-Manifest;$r=Read-Fixture
        Assert-Readiness ($r.workspaceMode -ceq 'Bootstrap' -and (Get-Check $r source-private-workspace).status -ceq 'NeedsConfiguration') 'Bootstrap was represented as private-ready.'
    }
    Test-ReadinessCase 'Selected approved region must match source pool region' {
        $r=Get-FabricReadiness $fixture dev westus2
        Assert-Readiness ($r.status -ceq 'NeedsConfiguration' -and (Get-Check $r source-region).status -ceq 'NeedsConfiguration') 'Selected region mismatch accepted.'
    }
    foreach($bad in @(@('environment','../dev'),@('environment','DEV'),@('region','../eastus2'),@('region','unapproved'))){
        Test-ReadinessCase "Invalid $($bad[0]) selection $($bad[1]) is rejected" {
            $message='';try{if($bad[0] -eq 'environment'){Get-FabricReadiness $fixture $bad[1] eastus2|Out-Null}else{Get-FabricReadiness $fixture dev $bad[1]|Out-Null}}catch{$message=$_.Exception.Message}
            Assert-Readiness ($message -ceq 'Select a registered Fabric environment and region.') 'Invalid request escaped registered selection.'
        }
    }
    foreach($payload in @('{"secret":"SYNTHETIC_PRIVATE_INVALID_JSON"','[]','[{"schemaVersion":1}]','null','"SYNTHETIC_PRIVATE_STRING"')){
        Test-ReadinessCase "Malformed or nonobject source is unavailable $($results.Count)" {
            Write-ReadinessFile (Join-Path $fixture self-service/fabric/workspace.dev.json) $payload;$r=Read-Fixture;$json=$r|ConvertTo-Json -Depth 40
            Assert-Readiness ($r.status -ceq 'Unavailable' -and $r.checks.Count -eq 8 -and $json -notmatch 'SYNTHETIC_PRIVATE') 'Malformed source was accepted or leaked.'
        }
    }
    Test-ReadinessCase 'Credential-bearing manifest is blocked without source disclosure' {
        $manifest.operation.displayName='Bearer SYNTHETIC_PRIVATE_TOKEN';Save-Manifest;$r=Read-Fixture;$json=$r|ConvertTo-Json -Depth 40
        Assert-Readiness ((Get-Check $r source-operation).status -ceq 'NeedsConfiguration' -and $json -notmatch 'SYNTHETIC_PRIVATE_TOKEN') 'Credential-bearing manifest accepted or exposed.'
    }
    Test-ReadinessCase 'Missing source is unavailable' {
        Remove-Item -LiteralPath (Join-Path $fixture self-service/fabric/workspace.dev.json);$r=Read-Fixture
        Assert-Readiness ($r.status -ceq 'Unavailable' -and @($r.checks|Where-Object {$_.id -clike 'runtime-*' -and $_.status -ceq 'NotVerified'}).Count -eq 7) 'Missing source became ready or live checked.'
    }
    Test-ReadinessCase 'Oversized source is unavailable' {
        Write-ReadinessFile (Join-Path $fixture self-service/fabric/workspace.dev.json) ('x'*262145);$r=Read-Fixture
        Assert-Readiness ($r.status -ceq 'Unavailable') 'Oversized source accepted.'
    }
    Test-ReadinessCase 'Linked checkout root is unavailable' {
        $link=Join-Path $directory 'linked-fixture'
        New-Item -ItemType Junction -Path $link -Target $fixture -ErrorAction Stop|Out-Null
        $r=Get-FabricReadiness $link dev eastus2
        Assert-Readiness ($r.status -ceq 'Unavailable') 'Reparse-point source accepted.'
    }
    Test-ReadinessCase 'Source file edits change the local evidence hash' {
        $first=Read-Fixture;$path=Join-Path $fixture scripts/fabric-execution-common.ps1;Add-Content $path '# synthetic provenance change';$second=Read-Fixture
        $a=@($first.sources|Where-Object path -ceq 'scripts/fabric-execution-common.ps1')[0].sha256;$b=@($second.sources|Where-Object path -ceq 'scripts/fabric-execution-common.ps1')[0].sha256
        Assert-Readiness ($a -cne $b -and !$second.liveVerified -and !$second.canDeploy) 'Changed source evidence was not detected.'
    }
    foreach($stage in @('dev','qa','uat','prod')){
        Test-ReadinessCase "Current disabled $stage source reports configured alias and live unknowns" {
            $r=Get-FabricReadiness $repository $stage eastus2
            Assert-Readiness ($r.status -ceq 'NeedsConfiguration' -and $r.poolName -ceq "fabric-$stage-agents" -and !$r.canDeploy -and !$r.liveVerified -and @($r.checks|Where-Object {$_.id -clike 'runtime-*' -and $_.status -cne 'NotVerified'}).Count -eq 0) 'Current source boundary changed.'
        }
    }
}finally{
    [Environment]::SetEnvironmentVariable('BOUND_FABRIC_AGENT_POOL',$previousPool,'Process')
    $script:FabricApiContext=$previousContext
}
$failed=@($results|Where-Object {!$_.passed}).Count
$receipt=@{passed=$results.Count-$failed;failed=$failed;scope='Offline reviewed-source readiness only. No Azure, ADO, Fabric, DNS or TCP calls.';scenarios=$results.ToArray()}
$receipt|ConvertTo-Json -Depth 30|Set-Content -LiteralPath (Join-Path $directory receipt.json)
if($failed){throw "$failed Fabric source readiness checks failed. Evidence: $directory"}
Write-Host "PASS: $($receipt.passed) offline Fabric readiness checks. Evidence: $directory"
