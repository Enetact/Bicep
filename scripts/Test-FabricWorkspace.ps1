#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
. "$PSScriptRoot/fabric-workspace-common.ps1"
$script:ActualFabricApi=${function:Invoke-FabricApi}
$script:FabricIds=@{
    Tenant='10000000-0000-0000-0000-000000000001';Subscription='10000000-0000-0000-0000-000000000002'
    Principal='10000000-0000-0000-0000-000000000003';Capacity='20000000-0000-0000-0000-000000000001'
    Workspace='30000000-0000-0000-0000-000000000001';TargetWorkspace='30000000-0000-0000-0000-000000000002'
    Group='40000000-0000-0000-0000-000000000001';Pipeline='50000000-0000-0000-0000-000000000001'
    SourceStage='60000000-0000-0000-0000-000000000001';TargetStage='60000000-0000-0000-0000-000000000002'
    Item='70000000-0000-0000-0000-000000000001';Other='90000000-0000-0000-0000-000000000001'
}
function Copy-FabricFixture($Value) { ConvertFrom-Json -InputObject (ConvertTo-Json -InputObject $Value -Depth 80 -Compress) -AsHashtable }
function Reset-FabricFixture {
    Set-FabricApiContext @{}
    $i=$script:FabricIds
    $script:FabricFixture=@{Calls=[Collections.Generic.List[object]]::new();Responses=@{};Handler=$null;Page=0;WebCalls=[Collections.Generic.List[object]]::new();Web=[Collections.Generic.Queue[object]]::new();SleepCount=0}
    $r=$script:FabricFixture.Responses
    $r.capacities=@{value=@(@{id=$i.Other;displayName='Other capacity';state='Active';sku='F2'},@{id=$i.Capacity;displayName='Reviewed capacity';state='Active';sku='F64'})}
    $r.workspaces=@{value=@(@{id=$i.Other;displayName='Unrelated workspace'})}
    $r["workspaces/$($i.Workspace)"]=@{id=$i.Workspace;capacityId=$i.Capacity;workspaceIdentity=@{servicePrincipalId=$i.Principal}}
    foreach ($suffix in @('lakehouses','roleAssignments','managedPrivateEndpoints')) { $r["workspaces/$($i.Workspace)/$suffix"]=@{value=@()} }
    $r['connections']=@{value=@()};$r['deploymentPipelines']=@{value=@()}
    $r['connections/supportedConnectionTypes?showAllCreationMethods=true']=@{value=@(
        @{type='AzureDataLakeStorage';creationMethods=@(@{name='AzureDataLakeStorage';parameters=@(@{name='url';dataType='Text';required=$true})});supportedCredentialTypes=@('WorkspaceIdentity');supportedConnectionEncryptionTypes=@('Encrypted')},
        @{type='SQL';creationMethods=@(@{name='SQL';parameters=@(@{name='server';dataType='Text';required=$true},@{name='database';dataType='Text';required=$false})});supportedCredentialTypes=@('WorkspaceIdentity');supportedConnectionEncryptionTypes=@('Encrypted')}
    )}
    $r["deploymentPipelines/$($i.Pipeline)/stages"]=@{value=@(@{id=$i.SourceStage;workspaceId=$i.Workspace;order=0},@{id=$i.TargetStage;workspaceId=$i.TargetWorkspace;order=1})}
    foreach ($workspace in @($i.Workspace,$i.TargetWorkspace)) { $r["workspaces/$workspace/networking/communicationPolicy"]=@{inbound=@{publicAccessRules=@{defaultAction='Allow'}}} }
    $r["workspaces/$($i.Workspace)/items"]=@{value=@(@{id=$i.Item;displayName='Claims lake';type='Lakehouse'},@{id=$i.Other;displayName='Other';type='Notebook'})}
    $r["deploymentPipelines/$($i.Pipeline)/stages/$($i.SourceStage)/items"]=@{value=@(@{itemId=$i.Item;itemDisplayName='Claims lake';itemType='Lakehouse';targetItemId=$i.Other})}
    $r["workspaces/$($i.TargetWorkspace)/items"]=@{value=@(@{id=$i.Other;displayName='Claims lake';type='Lakehouse'})}
    $r["workspaces/$($i.Workspace)/items/$($i.Item)/getDefinition"]=@{definition=@{parts=@(@{path='definition.json';payload='eyJ2ZXJzaW9uIjoxfQ==';payloadType='InlineBase64'})}}
    $r["workspaces/$($i.TargetWorkspace)/items/$($i.Other)/getDefinition"]=@{definition=@{parts=@(@{path='definition.json';payload='eyJ2ZXJzaW9uIjowfQ==';payloadType='InlineBase64'})}}
}
function New-FabricFixtureManifest($Operation) {
    $i=$script:FabricIds
    @{schemaVersion=1;enabled=$false;tenantId=$i.Tenant;subscriptionId=$i.Subscription;deploymentPrincipalObjectId=$i.Principal;environmentName='dev';operation=$Operation}
}
function New-FabricPrivateFixtureManifest($Operation) {
    $manifest=New-FabricFixtureManifest $Operation
    $compact=([guid]$script:FabricIds.Workspace).ToString('N')
    $manifest.execution=@{kind='ManagedDevOpsPool';privateWorkspace=@{workspaceId=$script:FabricIds.Workspace;apiBaseUrl="https://$compact.z$($compact.Substring(0,2)).w.api.fabric.microsoft.com";privateEndpointResourceId='reviewed-by-execution-validator';virtualNetworkResourceId='reviewed-by-execution-validator';privateDnsZoneResourceId='reviewed-by-execution-validator'}}
    return $manifest
}
function Invoke-FabricApi([string]$Method,[string]$Path,$Body=$null) {
    $h=$script:FabricFixture
    $h.Calls.Add(@{method=$Method;path=$Path;body=$Body})
    if ($Method -cne 'GET' -and !($Method -ceq 'POST' -and $Path -match '^workspaces/[0-9a-f-]+/items/[0-9a-f-]+/getDefinition$')) { throw 'Synthetic planner attempted a write.' }
    if ($h.Handler) { return & $h.Handler $Method $Path $Body }
    if (!$h.Responses.Contains($Path)) { throw "No synthetic response for $Method $Path" }
    Copy-FabricFixture $h.Responses[$Path]
}
function Invoke-WebRequest {
    [CmdletBinding()]param($Uri,$Method,$Headers,$MaximumRedirection,$TimeoutSec,[switch]$SkipHttpErrorCheck,$ContentType,$Body)
    $h=$script:FabricFixture;$h.WebCalls.Add(@{uri=$Uri;method=$Method})
    if ($h.Web.Count -eq 0) { throw 'No synthetic HTTP response. Network access is forbidden in these tests.' }
    $h.Web.Dequeue()
}
function Start-Sleep { param([int]$Seconds) $script:FabricFixture.SleepCount++ }
function Assert-FabricTest($Condition,[string]$Message) { if (!$Condition) { throw $Message } }
function Assert-FabricRejected([scriptblock]$Action,[string]$Pattern='.') {
    $errorText=''
    try { & $Action | Out-Null } catch { $errorText=$_.Exception.Message }
    Assert-FabricTest ($errorText -match $Pattern) "Expected rejection matching '$Pattern'; received '$errorText'."
}
$script:FabricTestResults=[Collections.Generic.List[object]]::new()
function Test-FabricCase([string]$Name,[scriptblock]$Action) {
    Reset-FabricFixture
    try { & $Action; $script:FabricTestResults.Add(@{name=$Name;passed=$true}) }
    catch { $script:FabricTestResults.Add(@{name=$Name;passed=$false;error=$_.Exception.Message});Write-Host "FAIL: $Name - $($_.Exception.Message)" }
}
$i=$script:FabricIds
Test-FabricCase 'CreateWorkspace selects exact active premium capacity among unrelated capacities' {
    $p=New-FabricOperation (New-FabricFixtureManifest @{kind='CreateWorkspace';displayName='New workspace';capacityId=$i.Capacity;capacityDisplayName='Reviewed capacity'})
    Assert-FabricTest ($p.path -ceq 'workspaces' -and $p.body.capacityId -ceq $i.Capacity -and $p.before.capacity.id -ceq $i.Capacity) 'Wrong capacity/route.'
}
Test-FabricCase 'CreateLakehouse preserves scoped workspace and name' {
    $p=New-FabricOperation (New-FabricFixtureManifest @{kind='CreateLakehouse';workspaceId=$i.Workspace;displayName='Claims lake'})
    Assert-FabricTest ($p.path -ceq "workspaces/$($i.Workspace)/lakehouses" -and $p.body.displayName -ceq 'Claims lake') 'Wrong lakehouse target.'
}
Test-FabricCase 'ProvisionIdentity requires absent identity' {
    $script:FabricFixture.Responses["workspaces/$($i.Workspace)"].Remove('workspaceIdentity')
    $p=New-FabricOperation (New-FabricFixtureManifest @{kind='ProvisionIdentity';workspaceId=$i.Workspace})
    Assert-FabricTest ($p.path -ceq "workspaces/$($i.Workspace)/provisionIdentity" -and $p.body.Count -eq 0) 'Unexpected identity payload.'
}
Test-FabricCase 'AddWorkspaceAdminGroup emits group role rather than an Azure grant' {
    $p=New-FabricOperation (New-FabricFixtureManifest @{kind='AddWorkspaceAdminGroup';workspaceId=$i.Workspace;groupObjectId=$i.Group})
    Assert-FabricTest ($p.body.principal.id -ceq $i.Group -and $p.body.principal.type -ceq 'Group' -and $p.body.role -ceq 'Admin') 'Wrong role target.'
}
Test-FabricCase 'CreateConnection ADLS uses live encrypted workspace identity capability' {
    $p=New-FabricOperation (New-FabricFixtureManifest @{kind='CreateConnection';workspaceId=$i.Workspace;displayName='ADLS source';connectionType='AzureDataLakeStorage';creationMethod='AzureDataLakeStorage';parameters=@(@{name='url';dataType='Text';value='https://reviewedlake.dfs.core.windows.net/landing'})})
    Assert-FabricTest ($p.path -ceq 'connections' -and $p.body.credentialDetails.credentials.credentialType -ceq 'WorkspaceIdentity' -and !$p.body.credentialDetails.skipTestConnection -and $p.body.credentialDetails.connectionEncryption -ceq 'Encrypted') 'Connection weakened authentication/testing.'
}
Test-FabricCase 'CreateConnection SQL accepts only reviewed server/database parameters' {
    $p=New-FabricOperation (New-FabricFixtureManifest @{kind='CreateConnection';workspaceId=$i.Workspace;displayName='SQL source';connectionType='SQL';creationMethod='SQL';parameters=@(@{name='server';dataType='Text';value='reviewed-sql.database.windows.net'},@{name='database';dataType='Text';value='claims'})})
    Assert-FabricTest ($p.body.connectionDetails.type -ceq 'SQL' -and $p.body.connectionDetails.parameters.Count -eq 2) 'SQL route/payload changed.'
}
Test-FabricCase 'CreateManagedPrivateEndpoint binds source subscription and dfs subresource' {
    $target="/subscriptions/$($i.Subscription)/resourceGroups/source/providers/Microsoft.Storage/storageAccounts/reviewedlake"
    $p=New-FabricOperation (New-FabricFixtureManifest @{kind='CreateManagedPrivateEndpoint';workspaceId=$i.Workspace;name='reviewed-dfs';targetPrivateLinkResourceId=$target;targetSubresourceType='dfs'})
    Assert-FabricTest ($p.body.targetPrivateLinkResourceId -ceq $target -and $p.body.targetSubresourceType -ceq 'dfs') 'MPE changed source.'
}
Test-FabricCase 'CreateDeploymentPipeline carries explicit private stage definitions' {
    $p=New-FabricOperation (New-FabricFixtureManifest @{kind='CreateDeploymentPipeline';displayName='Claims promotion';stages=@(@{displayName='Development'},@{displayName='Production'})})
    Assert-FabricTest ($p.path -ceq 'deploymentPipelines' -and $p.body.stages.Count -eq 2 -and @($p.body.stages | Where-Object isPublic).Count -eq 0) 'Stages were broadened.'
}
Test-FabricCase 'AssignWorkspaceToStage requires an unassigned exact stage' {
    $script:FabricFixture.Responses["deploymentPipelines/$($i.Pipeline)/stages"].value[1].Remove('workspaceId')
    $p=New-FabricOperation (New-FabricFixtureManifest @{kind='AssignWorkspaceToStage';workspaceId=$i.Workspace;deploymentPipelineId=$i.Pipeline;stageId=$i.TargetStage})
    Assert-FabricTest ($p.path -ceq "deploymentPipelines/$($i.Pipeline)/stages/$($i.TargetStage)/assignWorkspace" -and $p.body.workspaceId -ceq $i.Workspace) 'Wrong stage assignment.'
}
Test-FabricCase 'PromoteItems selects only known typed source items across adjacent stages' {
    $p=New-FabricOperation (New-FabricFixtureManifest @{kind='PromoteItems';deploymentPipelineId=$i.Pipeline;sourceStageId=$i.SourceStage;targetStageId=$i.TargetStage;items=@(@{sourceItemId=$i.Item;itemType='Lakehouse'})})
    Assert-FabricTest ($p.path -ceq "deploymentPipelines/$($i.Pipeline)/deploy" -and $p.body.items.Count -eq 1 -and !$p.body.options.allowCrossRegionDeployment) 'Promotion scope broadened.'
    Assert-FabricTest ($p.before.sourceDefinitions[$i.Item] -cmatch '^[a-f0-9]{64}$' -and $p.before.targetDefinitions[$i.Other] -cmatch '^[a-f0-9]{64}$' -and $p.before.promotionTargets[$i.Item] -ceq $i.Other) 'Definition or target binding missing.'
    Assert-FabricTest ((ConvertTo-FabricCanonical $p) -notmatch 'InlineBase64|eyJ2ZXJzaW9uIj') 'Raw definition content leaked to the plan.'
}
Test-FabricCase 'Manifest unknown fields rejected before any read' {
    $m=New-FabricFixtureManifest @{kind='ProvisionIdentity';workspaceId=$i.Workspace};$m.extra='ignored'
    Assert-FabricRejected {New-FabricOperation $m} 'Unsupported';Assert-FabricTest ($script:FabricFixture.Calls.Count -eq 0) 'Invalid manifest caused a read.'
}
Test-FabricCase 'Operation irrelevant scope field rejected before any read' {
    Assert-FabricRejected {New-FabricOperation (New-FabricFixtureManifest @{kind='CreateWorkspace';displayName='New';capacityId=$i.Capacity;capacityDisplayName='Reviewed capacity';workspaceId=$i.Other})} 'Unsupported'
    Assert-FabricTest ($script:FabricFixture.Calls.Count -eq 0) 'Ignored scope field triggered a read.'
}
Test-FabricCase 'Missing required workspace rejected before any read' {
    Assert-FabricRejected {New-FabricOperation (New-FabricFixtureManifest @{kind='CreateLakehouse';displayName='New'})} 'Required';Assert-FabricTest ($script:FabricFixture.Calls.Count -eq 0) 'Missing workspace caused a read.'
}
Test-FabricCase 'Invalid and zero GUIDs rejected' {
    foreach ($bad in @('00000000-0000-0000-0000-000000000000','../connections',@{id=$i.Workspace})) { Assert-FabricRejected {New-FabricOperation (New-FabricFixtureManifest @{kind='ProvisionIdentity';workspaceId=$bad})} 'GUID' }
    Assert-FabricTest ($script:FabricFixture.Calls.Count -eq 0) 'Invalid ID reached the API.'
}
Test-FabricCase 'Workspace read-back cannot substitute another identity' {
    $script:FabricFixture.Responses["workspaces/$($i.Workspace)"].id=$i.Other
    Assert-FabricRejected {New-FabricOperation (New-FabricFixtureManifest @{kind='CreateLakehouse';workspaceId=$i.Workspace;displayName='New'})} 'identity/capacity'
}
Test-FabricCase 'Capacity unknown or insufficient SKU is blocked' {
    $script:FabricFixture.Responses.capacities.value[1].sku='F32'
    Assert-FabricRejected {New-FabricOperation (New-FabricFixtureManifest @{kind='CreateWorkspace';displayName='New';capacityId=$i.Capacity;capacityDisplayName='Reviewed capacity'})} 'F64'
}
Test-FabricCase 'Existing workspace creation is never adopted or replayed' {
    Assert-FabricRejected {New-FabricOperation (New-FabricFixtureManifest @{kind='CreateWorkspace';displayName='Unrelated workspace';capacityId=$i.Capacity;capacityDisplayName='Reviewed capacity'})} 'never adopt'
}
Test-FabricCase 'Identity provisioning cannot replace an existing identity' {
    Assert-FabricRejected {New-FabricOperation (New-FabricFixtureManifest @{kind='ProvisionIdentity';workspaceId=$i.Workspace})} 'already provisioned'
}
Test-FabricCase 'Existing group role is not overwritten' {
    $script:FabricFixture.Responses["workspaces/$($i.Workspace)/roleAssignments"].value=@(@{principal=@{id=$i.Group};role='Viewer'})
    Assert-FabricRejected {New-FabricOperation (New-FabricFixtureManifest @{kind='AddWorkspaceAdminGroup';workspaceId=$i.Workspace;groupObjectId=$i.Group})} 'separate review'
}
Test-FabricCase 'Credential-bearing and non-HTTPS ADLS locations rejected' {
    foreach ($bad in @('http://reviewedlake.dfs.core.windows.net/landing','file:///tmp/data','https://user:password@reviewedlake.dfs.core.windows.net/landing','https://reviewedlake.dfs.core.windows.net/landing?sig=secret','https://reviewedlake.dfs.core.windows.net/landing#secret','https://evil.example/landing')) {
        Assert-FabricRejected {New-FabricOperation (New-FabricFixtureManifest @{kind='CreateConnection';workspaceId=$i.Workspace;displayName='ADLS';connectionType='AzureDataLakeStorage';creationMethod='AzureDataLakeStorage';parameters=@(@{name='url';dataType='Text';value=$bad})})} 'Credentials|locations|HTTPS'
    }
}
Test-FabricCase 'SQL cannot embed credentials or connection strings' {
    Assert-FabricRejected {New-FabricOperation (New-FabricFixtureManifest @{kind='CreateConnection';workspaceId=$i.Workspace;displayName='SQL';connectionType='SQL';creationMethod='SQL';parameters=@(@{name='server';dataType='Text';value='server;Password=secret'})})} 'locations'
}
Test-FabricCase 'Duplicate connection parameters rejected' {
    $p=@{name='url';dataType='Text';value='https://reviewedlake.dfs.core.windows.net/landing'}
    Assert-FabricRejected {New-FabricOperation (New-FabricFixtureManifest @{kind='CreateConnection';workspaceId=$i.Workspace;displayName='ADLS';connectionType='AzureDataLakeStorage';creationMethod='AzureDataLakeStorage';parameters=@($p,$p)})} 'Duplicate'
}
Test-FabricCase 'Connector without live encrypted identity capability is blocked' {
    $script:FabricFixture.Responses['connections/supportedConnectionTypes?showAllCreationMethods=true'].value[0].supportedConnectionEncryptionTypes=@('NotEncrypted')
    Assert-FabricRejected {New-FabricOperation (New-FabricFixtureManifest @{kind='CreateConnection';workspaceId=$i.Workspace;displayName='ADLS';connectionType='AzureDataLakeStorage';creationMethod='AzureDataLakeStorage';parameters=@(@{name='url';dataType='Text';value='https://reviewedlake.dfs.core.windows.net/landing'})})} 'encryption support'
}
Test-FabricCase 'Managed endpoint cannot target another subscription or wrong service subresource' {
    foreach ($pair in @(@{subscription=$i.Other;subresource='dfs'},@{subscription=$i.Subscription;subresource='sqlServer'})) {
        Assert-FabricRejected {New-FabricOperation (New-FabricFixtureManifest @{kind='CreateManagedPrivateEndpoint';workspaceId=$i.Workspace;name='source';targetPrivateLinkResourceId="/subscriptions/$($pair.subscription)/resourceGroups/source/providers/Microsoft.Storage/storageAccounts/reviewedlake";targetSubresourceType=$pair.subresource})} 'same-subscription|subresource'
    }
}
Test-FabricCase 'Duplicate stage names rejected' {
    Assert-FabricRejected {New-FabricOperation (New-FabricFixtureManifest @{kind='CreateDeploymentPipeline';displayName='Promotion';stages=@(@{displayName='Dev'},@{displayName='Dev'})})} 'Duplicate'
}
Test-FabricCase 'Assignment cannot replace a populated stage' {
    Assert-FabricRejected {New-FabricOperation (New-FabricFixtureManifest @{kind='AssignWorkspaceToStage';workspaceId=$i.Workspace;deploymentPipelineId=$i.Pipeline;stageId=$i.TargetStage})} 'already assigned'
}
Test-FabricCase 'Native assignment blocks unknown or denied inbound policy' {
    $script:FabricFixture.Responses["deploymentPipelines/$($i.Pipeline)/stages"].value[1].Remove('workspaceId')
    $script:FabricFixture.Responses["workspaces/$($i.Workspace)/networking/communicationPolicy"].inbound.publicAccessRules.defaultAction='Deny'
    Assert-FabricRejected {New-FabricOperation (New-FabricFixtureManifest @{kind='AssignWorkspaceToStage';workspaceId=$i.Workspace;deploymentPipelineId=$i.Pipeline;stageId=$i.TargetStage})} 'require public access Allow'
}
Test-FabricCase 'Native promotion blocks deny-public without changing firewall' {
    $script:FabricFixture.Responses["workspaces/$($i.TargetWorkspace)/networking/communicationPolicy"].inbound.publicAccessRules.defaultAction='Deny'
    Assert-FabricRejected {New-FabricOperation (New-FabricFixtureManifest @{kind='PromoteItems';deploymentPipelineId=$i.Pipeline;sourceStageId=$i.SourceStage;targetStageId=$i.TargetStage;items=@(@{sourceItemId=$i.Item;itemType='Lakehouse'})})} 'deny-public'
    Assert-FabricTest (@($script:FabricFixture.Calls | Where-Object method -cne 'GET').Count -eq 0) 'Firewall was mutated.'
}
Test-FabricCase 'Promotion cannot substitute unknown source item/type' {
    Assert-FabricRejected {New-FabricOperation (New-FabricFixtureManifest @{kind='PromoteItems';deploymentPipelineId=$i.Pipeline;sourceStageId=$i.SourceStage;targetStageId=$i.TargetStage;items=@(@{sourceItemId=$i.Item;itemType='Notebook'})})} 'does not match'
}
Test-FabricCase 'Promotion requires adjacent stages and complete workspace IDs' {
    $script:FabricFixture.Responses["deploymentPipelines/$($i.Pipeline)/stages"].value[1].order=3
    Assert-FabricRejected {New-FabricOperation (New-FabricFixtureManifest @{kind='PromoteItems';deploymentPipelineId=$i.Pipeline;sourceStageId=$i.SourceStage;targetStageId=$i.TargetStage;items=@(@{sourceItemId=$i.Item;itemType='Lakehouse'})})} 'consecutive'
}
Test-FabricCase 'Source code drift changes promotion binding even when item IDs and names are unchanged' {
    $manifest=New-FabricFixtureManifest @{kind='PromoteItems';deploymentPipelineId=$i.Pipeline;sourceStageId=$i.SourceStage;targetStageId=$i.TargetStage;items=@(@{sourceItemId=$i.Item;itemType='Lakehouse'})}
    $before=New-FabricOperation $manifest
    $script:FabricFixture.Responses["workspaces/$($i.Workspace)/items/$($i.Item)/getDefinition"].definition.parts[0].payload='eyJ2ZXJzaW9uIjoyfQ=='
    $after=New-FabricOperation $manifest
    Assert-FabricTest ((ConvertTo-FabricCanonical $before.before.sourceItems) -ceq (ConvertTo-FabricCanonical $after.before.sourceItems)) 'Fixture unexpectedly changed metadata.'
    Assert-FabricTest ($before.before.sourceDefinitions[$i.Item] -cne $after.before.sourceDefinitions[$i.Item] -and (ConvertTo-FabricCanonical $before) -cne (ConvertTo-FabricCanonical $after)) 'Code drift was invisible to Apply binding.'
}
Test-FabricCase 'Target code drift changes promotion binding independently of source' {
    $manifest=New-FabricFixtureManifest @{kind='PromoteItems';deploymentPipelineId=$i.Pipeline;sourceStageId=$i.SourceStage;targetStageId=$i.TargetStage;items=@(@{sourceItemId=$i.Item;itemType='Lakehouse'})}
    $before=New-FabricOperation $manifest
    $script:FabricFixture.Responses["workspaces/$($i.TargetWorkspace)/items/$($i.Other)/getDefinition"].definition.parts[0].payload='eyJ2ZXJzaW9uIjoyfQ=='
    $after=New-FabricOperation $manifest
    Assert-FabricTest ($before.before.sourceDefinitions[$i.Item] -ceq $after.before.sourceDefinitions[$i.Item] -and $before.before.targetDefinitions[$i.Other] -cne $after.before.targetDefinitions[$i.Other]) 'Target code drift was invisible.'
}
Test-FabricCase 'Unsupported or malformed definitions cannot authorize promotion' {
    $path="workspaces/$($i.Workspace)/items/$($i.Item)/getDefinition"
    $script:FabricFixture.Responses[$path]=@{}
    Assert-FabricRejected {Get-FabricItemDefinitionHash $i.Workspace $i.Item} 'unavailable'
    $script:FabricFixture.Responses[$path]=@{definition=@{parts=@(@{path='definition.json';payload='not base64';payloadType='InlineBase64'})}}
    Assert-FabricRejected {Get-FabricItemDefinitionHash $i.Workspace $i.Item} 'valid InlineBase64'
    $script:FabricFixture.Responses[$path]=@{definition=@{parts=@()}}
    Assert-FabricRejected {Get-FabricItemDefinitionHash $i.Workspace $i.Item} 'missing'
}
Test-FabricCase 'Definition LRO resolves to a hash and discards raw source' {
    $operationUri='https://api.fabric.microsoft.com/v1/operations/'+$i.Other
    $path="workspaces/$($i.Workspace)/items/$($i.Item)/getDefinition"
    $definition=Copy-FabricFixture $script:FabricFixture.Responses[$path]
    $script:FabricFixture.Responses[$path]=@{operationUri=$operationUri;status='Succeeded'}
    $script:FabricFixture.Responses[$operationUri+'/result']=$definition
    $hash=Get-FabricItemDefinitionHash $i.Workspace $i.Item
    Assert-FabricTest ($hash -cmatch '^[a-f0-9]{64}$' -and $script:FabricFixture.Calls.Count -eq 2) 'Definition result was not resolved and hashed.'
}
Test-FabricCase 'Promotion refuses unpaired target collision and missing paired target' {
    $manifest=New-FabricFixtureManifest @{kind='PromoteItems';deploymentPipelineId=$i.Pipeline;sourceStageId=$i.SourceStage;targetStageId=$i.TargetStage;items=@(@{sourceItemId=$i.Item;itemType='Lakehouse'})}
    $pair=$script:FabricFixture.Responses["deploymentPipelines/$($i.Pipeline)/stages/$($i.SourceStage)/items"].value[0]
    $pair.Remove('targetItemId')
    Assert-FabricRejected {New-FabricOperation $manifest} 'Unpaired target name collision'
    $pair.targetItemId=$i.Group
    Assert-FabricRejected {New-FabricOperation $manifest} 'Paired target item is absent'
}
Test-FabricCase 'Promotion may create a genuinely absent target without guessing an existing item' {
    $script:FabricFixture.Responses["deploymentPipelines/$($i.Pipeline)/stages/$($i.SourceStage)/items"].value[0].Remove('targetItemId')
    $script:FabricFixture.Responses["workspaces/$($i.TargetWorkspace)/items"].value=@()
    $p=New-FabricOperation (New-FabricFixtureManifest @{kind='PromoteItems';deploymentPipelineId=$i.Pipeline;sourceStageId=$i.SourceStage;targetStageId=$i.TargetStage;items=@(@{sourceItemId=$i.Item;itemType='Lakehouse'})})
    Assert-FabricTest ($p.before.promotionTargets[$i.Item] -ceq '' -and $p.before.targetDefinitions.Count -eq 0) 'New target was silently adopted.'
}
Test-FabricCase 'Cross-host continuation is blocked before a second read' {
    $script:FabricFixture.Responses.workspaces.continuationUri='https://evil.example/v1/workspaces'
    Assert-FabricRejected {Get-FabricCollection 'workspaces'} 'Untrusted';Assert-FabricTest ($script:FabricFixture.Calls.Count -eq 1) 'Malicious continuation was followed.'
}
Test-FabricCase 'Same-host continuation cannot change workspace collection' {
    $script:FabricFixture.Responses["workspaces/$($i.Workspace)/items"].continuationUri="https://api.fabric.microsoft.com/v1/workspaces/$($i.TargetWorkspace)/items?continuationToken=x"
    Assert-FabricRejected {Get-FabricCollection "workspaces/$($i.Workspace)/items"} 'changed collection';Assert-FabricTest ($script:FabricFixture.Calls.Count -eq 1) 'Cross-workspace continuation was followed.'
}
Test-FabricCase 'Documented supported-types continuation alias remains supported' {
    $initial='connections/supportedConnectionTypes?showAllCreationMethods=true';$next='https://api.fabric.microsoft.com/v1/connections/supportedConnections?connectivityType=ShareableCloud&continuationToken=x'
    $script:FabricFixture.Responses[$initial].continuationUri=$next;$script:FabricFixture.Responses[$next]=@{value=@(@{type='Other'})}
    $rows=@(Get-FabricCollection $initial);Assert-FabricTest ($rows.Count -eq 3) 'Valid paged collection was nested or lost.'
}
Test-FabricCase 'Repeated and partial pagination fail closed' {
    $next='https://api.fabric.microsoft.com/v1/workspaces?continuationToken=x';$script:FabricFixture.Responses.workspaces.continuationUri=$next;$script:FabricFixture.Responses[$next]=@{value=@();continuationUri=$next}
    Assert-FabricRejected {Get-FabricCollection 'workspaces'} 'repeated'
    $script:FabricFixture.Responses.workspaces=@{continuationUri=$next};Assert-FabricRejected {Get-FabricCollection 'workspaces'} 'Incomplete'
}
Test-FabricCase 'Collection page limit never returns partial evidence as complete' {
    $script:FabricFixture.Handler={param($Method,$Path,$Body) $script:FabricFixture.Page++;@{value=@();continuationUri="https://api.fabric.microsoft.com/v1/workspaces?continuationToken=$($script:FabricFixture.Page)"}}
    Assert-FabricRejected {Get-FabricCollection 'workspaces'} 'partial';Assert-FabricTest ($script:FabricFixture.Calls.Count -eq 100) 'Unbounded pagination.'
}
Test-FabricCase 'Canonical empty arrays and map key order preserve drift bindings' {
    Assert-FabricTest ((ConvertTo-FabricCanonical @()) -ceq '[]') 'Empty array lost.'
    Assert-FabricTest ((ConvertTo-FabricCanonical @{z=@();a=1}) -ceq (ConvertTo-FabricCanonical @{a=1;z=@()})) 'Map order changed binding.'
    Assert-FabricTest ((Get-FabricHash 'a') -cne (Get-FabricHash 'b')) 'Hash binding failed.'
}
Test-FabricCase 'API rejects malicious LRO location before following it' {
    $script:FabricFixture.Web.Enqueue(@{StatusCode=202;Content='';Headers=@{Location=@('https://evil.example/v1/operations/'+$i.Other)}})
    Assert-FabricRejected {& $script:ActualFabricApi POST 'workspaces' @{displayName='New'}} 'Untrusted'
    Assert-FabricTest ($script:FabricFixture.WebCalls.Count -eq 1) 'Untrusted LRO was followed.'
}
Test-FabricCase 'API reports unknown LRO outcome without retrying the write' {
    $script:FabricFixture.Web.Enqueue(@{StatusCode=202;Content='';Headers=@{Location=@('https://api.fabric.microsoft.com/v1/operations/'+$i.Other)}})
    $script:FabricFixture.Web.Enqueue(@{StatusCode=503;Content='';Headers=@{}})
    Assert-FabricRejected {& $script:ActualFabricApi POST 'workspaces' @{displayName='New'}} 'outcome unknown'
    Assert-FabricTest (@($script:FabricFixture.WebCalls | Where-Object method -ceq 'POST').Count -eq 1) 'Write was retried.'
}
Test-FabricCase 'API polling completes with identifier-only operation evidence' {
    $operationUri='https://api.fabric.microsoft.com/v1/operations/'+$i.Other
    $script:FabricFixture.Web.Enqueue(@{StatusCode=202;Content='';Headers=@{Location=@($operationUri)}})
    $script:FabricFixture.Web.Enqueue(@{StatusCode=200;Content='{"status":"Succeeded"}';Headers=@{}})
    $result=& $script:ActualFabricApi POST 'workspaces' @{displayName='New'}
    Assert-FabricTest ($result.status -ceq 'Succeeded' -and $result.operationUri -ceq $operationUri -and $result.Count -eq 2) 'LRO result contains wrong evidence.'
}
Test-FabricCase 'Private workspace planning selects the reviewed workspace API host' {
    $manifest=New-FabricPrivateFixtureManifest @{kind='CreateLakehouse';workspaceId=$i.Workspace;displayName='Private lake'}
    $operation=New-FabricOperation $manifest
    Assert-FabricTest ((Get-FabricApiBaseUrl) -ceq $manifest.execution.privateWorkspace.apiBaseUrl -and $operation.path -ceq "workspaces/$($i.Workspace)/lakehouses") 'Private context was not bound.'
}
Test-FabricCase 'Private context resets before a later global operation' {
    Set-FabricApiContext (New-FabricPrivateFixtureManifest @{kind='CreateLakehouse';workspaceId=$i.Workspace;displayName='Private lake'})
    $null=New-FabricOperation (New-FabricFixtureManifest @{kind='CreateWorkspace';displayName='New';capacityId=$i.Capacity;capacityDisplayName='Reviewed capacity'})
    Assert-FabricTest ((Get-FabricApiBaseUrl) -ceq 'https://api.fabric.microsoft.com') 'Previous private workspace leaked into global planning.'
}
Test-FabricCase 'Private execution cannot supply a custom API host or base path' {
    foreach ($bad in @('https://api.fabric.microsoft.com','https://evil.example','http://30000000000000000000000000000001.z30.w.api.fabric.microsoft.com','https://30000000000000000000000000000001.z30.w.api.fabric.microsoft.com/v1','https://30000000000000000000000000000001.z30.w.api.fabric.microsoft.com/')) {
        $manifest=New-FabricPrivateFixtureManifest @{kind='CreateLakehouse';workspaceId=$i.Workspace;displayName='Private lake'}
        $manifest.execution.privateWorkspace.apiBaseUrl=$bad
        Assert-FabricRejected {New-FabricOperation $manifest} 'workspace-specific FQDN'
    }
    Assert-FabricTest ($script:FabricFixture.Calls.Count -eq 0) 'Untrusted private context caused an API read.'
}
Test-FabricCase 'Private context rejects another workspace before API access' {
    $manifest=New-FabricPrivateFixtureManifest @{kind='CreateLakehouse';workspaceId=$i.TargetWorkspace;displayName='Private lake'}
    Assert-FabricRejected {New-FabricOperation $manifest} 'change workspace scope'
    Assert-FabricTest ($script:FabricFixture.Calls.Count -eq 0) 'Cross-workspace planning caused a read.'
}
Test-FabricCase 'Private profile blocks global connections and native deployment pipelines' {
    foreach ($operation in @(
        @{kind='CreateWorkspace';displayName='New';capacityId=$i.Capacity;capacityDisplayName='Reviewed capacity'},
        @{kind='CreateConnection';workspaceId=$i.Workspace;displayName='Private source';connectionType='SQL';creationMethod='SQL';parameters=@(@{name='server';dataType='Text';value='source.database.windows.net'})},
        @{kind='CreateDeploymentPipeline';displayName='New';stages=@(@{displayName='Dev'},@{displayName='Prod'})},
        @{kind='AssignWorkspaceToStage';workspaceId=$i.Workspace;deploymentPipelineId=$i.Pipeline;stageId=$i.TargetStage},
        @{kind='PromoteItems';deploymentPipelineId=$i.Pipeline;sourceStageId=$i.SourceStage;targetStageId=$i.TargetStage;items=@(@{sourceItemId=$i.Item;itemType='Lakehouse'})}
    )) { Assert-FabricRejected {New-FabricOperation (New-FabricPrivateFixtureManifest $operation)} 'only qualified workspace-scoped' }
    Assert-FabricTest ($script:FabricFixture.Calls.Count -eq 0) 'Unsupported private operation caused a read.'
}
Test-FabricCase 'Private HTTP uses the reviewed workspace host with no public fallback' {
    $manifest=New-FabricPrivateFixtureManifest @{kind='CreateLakehouse';workspaceId=$i.Workspace;displayName='Private lake'}
    Set-FabricApiContext $manifest
    $script:FabricFixture.Web.Enqueue(@{StatusCode=200;Content='{"value":[]}';Headers=@{}})
    $null=& $script:ActualFabricApi GET "workspaces/$($i.Workspace)/lakehouses"
    Assert-FabricTest ($script:FabricFixture.WebCalls[0].uri -ceq ($manifest.execution.privateWorkspace.apiBaseUrl+"/v1/workspaces/$($i.Workspace)/lakehouses")) 'Private request was sent to a different host.'
}
Test-FabricCase 'Private URI guard rejects global hosts and unrelated private routes' {
    $manifest=New-FabricPrivateFixtureManifest @{kind='CreateLakehouse';workspaceId=$i.Workspace;displayName='Private lake'}
    Set-FabricApiContext $manifest;$base=$manifest.execution.privateWorkspace.apiBaseUrl
    foreach ($bad in @("https://api.fabric.microsoft.com/v1/workspaces/$($i.Workspace)","$base/v1/workspaces/$($i.TargetWorkspace)/items","$base/v1/connections","$base/v1/admin/workspaces/$($i.Workspace)","$base/v1/workspaces/$($i.Workspace)/networking/communicationPolicy","$base/v1/workspaces/$($i.Workspace)/%2fconnections")) {
        Assert-FabricRejected {& $script:ActualFabricApi GET $bad} 'Untrusted|workspace scope|unsupported private route'
    }
    Assert-FabricTest ($script:FabricFixture.WebCalls.Count -eq 0) 'Disallowed private URI received a token.'
}
Test-FabricCase 'Private pagination keeps the same host and workspace collection' {
    $manifest=New-FabricPrivateFixtureManifest @{kind='CreateLakehouse';workspaceId=$i.Workspace;displayName='Private lake'}
    Set-FabricApiContext $manifest
    $path="workspaces/$($i.Workspace)/lakehouses";$next=$manifest.execution.privateWorkspace.apiBaseUrl+"/v1/$path`?continuationToken=x"
    $script:FabricFixture.Responses[$path].continuationUri=$next;$script:FabricFixture.Responses[$next]=@{value=@(@{id=$i.Item})}
    $rows=@(Get-FabricCollection $path)
    Assert-FabricTest ($rows.Count -eq 1 -and $script:FabricFixture.Calls.Count -eq 2) 'Qualified private continuation was not preserved.'
}
Test-FabricCase 'Private pagination rejects a global continuation before a second read' {
    Set-FabricApiContext (New-FabricPrivateFixtureManifest @{kind='CreateLakehouse';workspaceId=$i.Workspace;displayName='Private lake'})
    $path="workspaces/$($i.Workspace)/lakehouses"
    $script:FabricFixture.Responses[$path].continuationUri="https://api.fabric.microsoft.com/v1/$path`?continuationToken=x"
    Assert-FabricRejected {Get-FabricCollection $path} 'Untrusted'
    Assert-FabricTest ($script:FabricFixture.Calls.Count -eq 1) 'Global continuation was followed.'
}
Test-FabricCase 'Private pagination rejects another workspace on the same private host' {
    $manifest=New-FabricPrivateFixtureManifest @{kind='CreateLakehouse';workspaceId=$i.Workspace;displayName='Private lake'}
    Set-FabricApiContext $manifest;$path="workspaces/$($i.Workspace)/lakehouses"
    $script:FabricFixture.Responses[$path].continuationUri=$manifest.execution.privateWorkspace.apiBaseUrl+"/v1/workspaces/$($i.TargetWorkspace)/lakehouses?continuationToken=x"
    Assert-FabricRejected {Get-FabricCollection $path} 'workspace scope'
    Assert-FabricTest ($script:FabricFixture.Calls.Count -eq 1) 'Cross-workspace continuation was followed.'
}
Test-FabricCase 'Private LRO remains on the reviewed private host' {
    $manifest=New-FabricPrivateFixtureManifest @{kind='CreateLakehouse';workspaceId=$i.Workspace;displayName='Private lake'}
    Set-FabricApiContext $manifest;$operationUri=$manifest.execution.privateWorkspace.apiBaseUrl+'/v1/operations/'+$i.Other
    $script:FabricFixture.Web.Enqueue(@{StatusCode=202;Content='';Headers=@{Location=@($operationUri)}})
    $script:FabricFixture.Web.Enqueue(@{StatusCode=200;Content='{"status":"Succeeded"}';Headers=@{}})
    $result=& $script:ActualFabricApi POST "workspaces/$($i.Workspace)/lakehouses" @{displayName='Private lake'}
    Assert-FabricTest ($result.operationUri -ceq $operationUri -and $script:FabricFixture.WebCalls.Count -eq 2) 'Private operation polling escaped the selected endpoint.'
}
Test-FabricCase 'Private LRO rejects a global operation location without replaying the write' {
    Set-FabricApiContext (New-FabricPrivateFixtureManifest @{kind='CreateLakehouse';workspaceId=$i.Workspace;displayName='Private lake'})
    $script:FabricFixture.Web.Enqueue(@{StatusCode=202;Content='';Headers=@{Location=@('https://api.fabric.microsoft.com/v1/operations/'+$i.Other)}})
    Assert-FabricRejected {& $script:ActualFabricApi POST "workspaces/$($i.Workspace)/lakehouses" @{displayName='Private lake'}} 'Untrusted'
    Assert-FabricTest ($script:FabricFixture.WebCalls.Count -eq 1) 'Global LRO was followed or the write was replayed.'
}
$fixtureRoot=Join-Path (Split-Path $PSScriptRoot -Parent) ('artifacts/fabric-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $fixtureRoot -Force | Out-Null
$failed=@($script:FabricTestResults | Where-Object { !$_.passed }).Count
$receipt=[ordered]@{dateUtc=[DateTimeOffset]::UtcNow.ToString('o');passed=$script:FabricTestResults.Count-$failed;failed=$failed;scope='Synthetic typed Fabric planner/API tests only; network, Azure, Fabric and native deployment pipeline acceptance not exercised';scenarios=$script:FabricTestResults.ToArray()}
$receipt | ConvertTo-Json -Depth 20 | Set-Content -LiteralPath (Join-Path $fixtureRoot 'receipt.json')
if ($failed) { throw "$failed Fabric workspace scenarios failed. Receipt: $fixtureRoot/receipt.json" }
Write-Host "PASS: $($receipt.passed) Fabric workspace scenarios. Receipt: $fixtureRoot/receipt.json"
