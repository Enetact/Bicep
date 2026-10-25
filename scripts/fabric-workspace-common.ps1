# Fabric API operations are constructed here; manifests cannot supply arbitrary URLs,
# executable code, credentials, deletion options or arbitrary HTTP bodies.
$script:FabricApiContext=@{baseUrl='https://api.fabric.microsoft.com';workspaceId=''}
function Get-FabricHash([string]$Text) {
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant()
}
function ConvertTo-FabricCanonical($Value) {
    if ($null -eq $Value) { return 'null' }
    if ($Value -is [Collections.IDictionary]) {
        return '{'+((@($Value.Keys | Sort-Object -CaseSensitive) | ForEach-Object { ($_ | ConvertTo-Json -Compress)+':'+(ConvertTo-FabricCanonical $Value[$_]) }) -join ',')+'}'
    }
    if ($Value -is [array]) { return '['+(($Value | ForEach-Object { ConvertTo-FabricCanonical $_ }) -join ',')+']' }
    return ConvertTo-Json -InputObject $Value -Compress -Depth 80
}
function Assert-FabricKeys($Value,[string[]]$Keys) {
    if ($Value -isnot [Collections.IDictionary] -or @($Value.Keys | Where-Object { $_ -cnotin $Keys }).Count) { throw 'Unsupported Fabric manifest field.' }
}
function Assert-FabricGuid($Value) {
    $parsed=[guid]::Empty
    if ($Value -isnot [string] -or ![guid]::TryParseExact($Value,'D',[ref]$parsed) -or $parsed -eq [guid]::Empty) { throw 'A reviewed nonzero Fabric/Entra GUID is required.' }
}
function Assert-FabricName($Value) {
    if ($Value -isnot [string] -or [string]::IsNullOrWhiteSpace($Value) -or $Value.Length -gt 200 -or $Value -match '[\x00-\x1f]' -or $Value -match 'REPLACE_') { throw 'Invalid Fabric display name.' }
}
function Assert-FabricManifest($Manifest) {
    Assert-FabricKeys $Manifest @('schemaVersion','enabled','tenantId','subscriptionId','deploymentPrincipalObjectId','environmentName','operation','execution')
    foreach ($key in @('schemaVersion','enabled','tenantId','subscriptionId','deploymentPrincipalObjectId','environmentName','operation')) { if (!$Manifest.Contains($key)) { throw 'Required Fabric manifest field missing.' } }
    if (($Manifest.schemaVersion -isnot [int] -and $Manifest.schemaVersion -isnot [long]) -or $Manifest.schemaVersion -ne 1 -or $Manifest.enabled -isnot [bool] -or $Manifest.environmentName -cnotin @('dev','qa','uat','prod')) { throw 'Invalid Fabric manifest version/environment.' }
    foreach ($key in @('tenantId','subscriptionId','deploymentPrincipalObjectId')) { Assert-FabricGuid $Manifest[$key] }
    $o=$Manifest.operation
    Assert-FabricKeys $o @('kind','displayName','workspaceId','capacityId','capacityDisplayName','groupObjectId','connectionType','creationMethod','parameters','name','targetPrivateLinkResourceId','targetSubresourceType','deploymentPipelineId','stageId','stages','sourceStageId','targetStageId','items')
    if ($o.kind -cnotin @('CreateWorkspace','CreateLakehouse','ProvisionIdentity','AddWorkspaceAdminGroup','CreateConnection','CreateManagedPrivateEndpoint','CreateDeploymentPipeline','AssignWorkspaceToStage','PromoteItems')) { throw 'Unsupported Fabric operation.' }
    $fields=switch -CaseSensitive ($o.kind) {
        'CreateWorkspace' { @('kind','displayName','capacityId','capacityDisplayName') }
        'CreateLakehouse' { @('kind','workspaceId','displayName') }
        'ProvisionIdentity' { @('kind','workspaceId') }
        'AddWorkspaceAdminGroup' { @('kind','workspaceId','groupObjectId') }
        'CreateConnection' { @('kind','workspaceId','displayName','connectionType','creationMethod','parameters') }
        'CreateManagedPrivateEndpoint' { @('kind','workspaceId','name','targetPrivateLinkResourceId','targetSubresourceType') }
        'CreateDeploymentPipeline' { @('kind','displayName','stages') }
        'AssignWorkspaceToStage' { @('kind','workspaceId','deploymentPipelineId','stageId') }
        'PromoteItems' { @('kind','deploymentPipelineId','sourceStageId','targetStageId','items') }
    }
    Assert-FabricKeys $o $fields
    foreach ($key in $fields) { if (!$o.Contains($key) -or $null -eq $o[$key]) { throw 'Required Fabric operation field missing.' } }
    if ($o.Contains('workspaceId')) { Assert-FabricGuid $o.workspaceId }
    foreach ($key in @('capacityId','groupObjectId','deploymentPipelineId','stageId','sourceStageId','targetStageId') | Where-Object { $o.Contains($_) }) { Assert-FabricGuid $o[$key] }
    foreach ($key in @('displayName','capacityDisplayName','name') | Where-Object { $o.Contains($_) }) { Assert-FabricName $o[$key] }
    # Reject secrets even in a field that would otherwise be ignored.
    if ((ConvertTo-FabricCanonical $Manifest) -match '(?i)AccountKey=|SharedAccessSignature=|Bearer |[?&]sig=|-----BEGIN .*PRIVATE KEY') { throw 'Credentials are forbidden in Fabric source manifests.' }
}
function Set-FabricApiContext($Manifest) {
    # Reset on every operation so a previous private workspace cannot affect a later plan.
    $script:FabricApiContext=@{baseUrl='https://api.fabric.microsoft.com';workspaceId=''}
    if (!$Manifest.Contains('execution')) { return }
    if ($Manifest.execution -isnot [Collections.IDictionary]) { throw 'Invalid Fabric execution profile.' }
    if (!$Manifest.execution.Contains('privateWorkspace')) { return }
    $private=$Manifest.execution.privateWorkspace
    Assert-FabricKeys $private @('workspaceId','apiBaseUrl','privateEndpointResourceId','virtualNetworkResourceId','privateDnsZoneResourceId')
    Assert-FabricGuid $private.workspaceId
    $workspace=([guid]$private.workspaceId).ToString('D')
    $compact=([guid]$private.workspaceId).ToString('N')
    # Microsoft documents this exact FQDN; arbitrary hosts and URL paths are forbidden.
    # https://learn.microsoft.com/fabric/security/security-workspace-level-private-links-set-up
    $baseUrl="https://$compact.z$($compact.Substring(0,2)).w.api.fabric.microsoft.com"
    if ($private.apiBaseUrl -isnot [string] -or $private.apiBaseUrl -cne $baseUrl) { throw 'Private Fabric API base URL must match the reviewed workspace-specific FQDN.' }
    if ($Manifest.operation.kind -cnotin @('CreateLakehouse','ProvisionIdentity','AddWorkspaceAdminGroup','CreateManagedPrivateEndpoint')) { throw 'Private Fabric workspace execution supports only qualified workspace-scoped operations. Global connections and native deployment pipelines are unsupported in this profile.' }
    if ($Manifest.operation.workspaceId -ine $workspace) { throw 'Private Fabric execution cannot change workspace scope.' }
    $script:FabricApiContext=@{baseUrl=$baseUrl;workspaceId=$workspace}
}
function Get-FabricApiBaseUrl {
    if ($script:FabricApiContext) { return $script:FabricApiContext.baseUrl }
    return 'https://api.fabric.microsoft.com'
}
function Assert-FabricApiUri([string]$Uri) {
    $u=[uri]$Uri
    $hostName=([uri](Get-FabricApiBaseUrl)).Host
    if (!$u.IsAbsoluteUri -or $u.Scheme -cne 'https' -or $u.Host -cne $hostName -or !$u.IsDefaultPort -or $u.UserInfo -or $u.Fragment -or !$u.AbsolutePath.StartsWith('/v1/')) { throw 'Untrusted Fabric API/continuation/operation URI.' }
    if ($script:FabricApiContext.workspaceId) {
        $workspacePath='/v1/workspaces/'+$script:FabricApiContext.workspaceId
        $isWorkspace=$u.AbsolutePath -ieq $workspacePath -or $u.AbsolutePath.StartsWith($workspacePath+'/',[StringComparison]::OrdinalIgnoreCase)
        $isOperation=$u.AbsolutePath -cmatch '^/v1/operations/[0-9a-fA-F]{8}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{4}-[0-9a-fA-F]{12}(/result)?$'
        # Communication-policy APIs require the global endpoint with workspace-level PL.
        # This private execution path never falls back to a public/global endpoint.
        if ((!$isWorkspace -and !$isOperation) -or $u.AbsolutePath.Contains('%') -or ($isWorkspace -and $u.AbsolutePath -imatch '/networking(?:/|$)')) { throw 'Private Fabric API URI changed workspace scope or uses an unsupported private route.' }
    }
}
function Invoke-FabricApi([string]$Method,[string]$Path,$Body=$null) {
    $uri=if ($Path.StartsWith('https://')) { $Path } else { (Get-FabricApiBaseUrl)+'/v1/'+$Path }
    Assert-FabricApiUri $uri
    $parameters=@{Uri=$uri;Method=$Method;Headers=@{Authorization='Bearer '+$script:FabricToken};MaximumRedirection=0;TimeoutSec=60;SkipHttpErrorCheck=$true}
    if ($null -ne $Body) { $parameters.ContentType='application/json';$parameters.Body=ConvertTo-Json -InputObject $Body -Depth 80 -Compress }
    $response=Invoke-WebRequest @parameters
    # Never print upstream bodies (connection/identity responses may be sensitive).
    if ([int]$response.StatusCode -lt 200 -or [int]$response.StatusCode -ge 300) { throw "Fabric API returned HTTP $([int]$response.StatusCode). Reconcile state before retrying a write." }
    $data=if ($response.Content) { $response.Content | ConvertFrom-Json -AsHashtable } else { @{} }
    if ([int]$response.StatusCode -eq 202) {
        $location=[string]($response.Headers.Location | Select-Object -First 1)
        Assert-FabricApiUri $location
        if (([uri]$location).AbsolutePath -notmatch '^/v1/operations/[0-9a-fA-F-]+$') { throw 'Unrecognized Fabric operation location.' }
        for ($attempt=0;$attempt -lt 40;$attempt++) {
            $delay=5
            $retry=[string]($response.Headers.'Retry-After' | Select-Object -First 1)
            if ($retry -match '^\d+$') { $delay=[Math]::Min(30,[Math]::Max(1,[int]$retry)) }
            Start-Sleep -Seconds $delay
            $response=Invoke-WebRequest -Uri $location -Method Get -Headers $parameters.Headers -MaximumRedirection 0 -TimeoutSec 60 -SkipHttpErrorCheck
            if ([int]$response.StatusCode -ne 200) { throw 'Fabric operation status unavailable; outcome unknown.' }
            $status=$response.Content | ConvertFrom-Json -AsHashtable
            if ($status.status -eq 'Succeeded') { return @{operationUri=$location;status='Succeeded'} }
            if ($status.status -in @('Failed','Cancelled','Canceled')) { throw 'Fabric operation failed; inspect Fabric operation history.' }
        }
        throw 'Fabric operation polling timed out; outcome unknown. Do not replay.'
    }
    return $data
}
function Get-FabricCollection([string]$Path) {
    $all=[Collections.Generic.List[object]]::new();$seen=[Collections.Generic.HashSet[string]]::new()
    $collectionPath=if ($Path.StartsWith('https://')) { ([uri]$Path).AbsolutePath } else { ([uri]((Get-FabricApiBaseUrl)+'/v1/'+$Path)).AbsolutePath }
    for ($page=0;$page -lt 100;$page++) {
        if (!$seen.Add($Path)) { throw 'Fabric pagination repeated a page.' }
        $result=Invoke-FabricApi GET $Path
        if ($result -isnot [Collections.IDictionary] -or !$result.Contains('value') -or $result.value -isnot [array]) { throw 'Incomplete Fabric collection evidence.' }
        foreach ($item in $result.value) { $all.Add($item) }
        if (!$result.continuationUri) { return $all.ToArray() }
        $Path=[string]$result.continuationUri;Assert-FabricApiUri $Path
        # A provider-supplied continuation may page the same collection only.
        # Microsoft's supported-type example uses a documented route alias.
        $nextPath=([uri]$Path).AbsolutePath
        if ($nextPath -cne $collectionPath -and !($collectionPath -ceq '/v1/connections/supportedConnectionTypes' -and $nextPath -ceq '/v1/connections/supportedConnections')) { throw 'Fabric pagination changed collection scope.' }
    }
    throw 'Fabric collection exceeded page limit; evidence is partial.'
}
function Get-FabricItemDefinitionHash([string]$WorkspaceId,[string]$ItemId) {
    Assert-FabricGuid $WorkspaceId;Assert-FabricGuid $ItemId
    # This narrowly constructed POST retrieves source; it is not a deployment.
    # https://learn.microsoft.com/rest/api/fabric/core/items/get-item-definition
    # Unsupported definitions, permissions or encrypted labels block promotion.
    $result=Invoke-FabricApi POST "workspaces/$WorkspaceId/items/$ItemId/getDefinition"
    if ($result.operationUri) {
        Assert-FabricApiUri $result.operationUri
        if (([uri]$result.operationUri).AbsolutePath -notmatch '^/v1/operations/[0-9a-fA-F-]+$' -or $result.status -cne 'Succeeded') { throw 'Item definition operation evidence is incomplete.' }
        $result=Invoke-FabricApi GET ($result.operationUri+'/result')
    }
    if ($result -isnot [Collections.IDictionary] -or !$result.Contains('definition')) { throw 'Item definition evidence is unavailable; promotion is blocked.' }
    $definition=$result.definition
    Assert-FabricKeys $definition @('format','parts')
    if ($definition.parts -isnot [array] -or $definition.parts.Count -lt 1 -or $definition.parts.Count -gt 256) { throw 'Item definition parts are missing or exceed the evidence limit.' }
    $paths=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($part in $definition.parts) {
        Assert-FabricKeys $part @('path','payload','payloadType')
        if ($part.path -isnot [string] -or [string]::IsNullOrWhiteSpace($part.path) -or $part.path.Length -gt 1024 -or !$paths.Add($part.path) -or $part.payloadType -cne 'InlineBase64' -or $part.payload -isnot [string] -or $part.payload.Length -gt 8388608) { throw 'Item definition part is incomplete, duplicated or unsupported.' }
        try { $null=[Convert]::FromBase64String($part.payload) } catch { throw 'Item definition payload is not valid InlineBase64 evidence.' }
    }
    $canonical=ConvertTo-FabricCanonical $definition
    if ([Text.Encoding]::UTF8.GetByteCount($canonical) -gt 8388608) { throw 'Item definition exceeds the 8 MiB evidence limit.' }
    # Never retain definition text/base64 payloads in a Preview plan or receipt.
    $hash=Get-FabricHash $canonical
    $result=$null;$definition=$null;$canonical=$null
    return $hash
}
function New-FabricOperation($Manifest) {
    Assert-FabricManifest $Manifest
    Set-FabricApiContext $Manifest
    $o=$Manifest.operation;$evidence=@{};$path='';$body=@{};$verify=''
    if ($o.Contains('workspaceId')) {
        $w=Invoke-FabricApi GET "workspaces/$($o.workspaceId)"
        if ($w.id -ine $o.workspaceId -or !$w.capacityId) { throw 'Workspace identity/capacity evidence is incomplete.' }
        $evidence.workspace=$w
    }
    switch -CaseSensitive ($o.kind) {
        'CreateWorkspace' {
            Assert-FabricGuid $o.capacityId;Assert-FabricName $o.displayName;Assert-FabricName $o.capacityDisplayName
            $capacities=@(Get-FabricCollection 'capacities' | Where-Object { $_.id -ieq $o.capacityId })
            if ($capacities.Count -ne 1 -or $capacities[0].displayName -cne $o.capacityDisplayName -or $capacities[0].state -ne 'Active' -or $capacities[0].sku -cnotin @('F64','F128','F256','F512','F1024','F2048')) { throw 'Reviewed active F64+ Fabric capacity not found. ARM resource IDs are not Fabric capacity GUIDs.' }
            $evidence.capacity=$capacities[0];$evidence.existing=@(Get-FabricCollection 'workspaces' | Where-Object { $_.displayName -ieq $o.displayName })
            if ($evidence.existing.Count) { throw 'Workspace name exists. Record its exact ID and review the next operation; never adopt by name.' }
            $path='workspaces';$body=@{displayName=$o.displayName;capacityId=$o.capacityId}
        }
        'CreateLakehouse' {
            Assert-FabricGuid $o.workspaceId;Assert-FabricName $o.displayName
            $evidence.existing=@(Get-FabricCollection "workspaces/$($o.workspaceId)/lakehouses" | Where-Object { $_.displayName -ieq $o.displayName })
            if ($evidence.existing.Count) { throw 'Lakehouse already exists; review its ID instead of replaying creation.' }
            $path="workspaces/$($o.workspaceId)/lakehouses";$body=@{displayName=$o.displayName}
        }
        'ProvisionIdentity' {
            Assert-FabricGuid $o.workspaceId
            if ($evidence.workspace.workspaceIdentity) { throw 'Workspace identity already provisioned.' }
            $path="workspaces/$($o.workspaceId)/provisionIdentity"
        }
        'AddWorkspaceAdminGroup' {
            Assert-FabricGuid $o.workspaceId;Assert-FabricGuid $o.groupObjectId
            $evidence.roles=@(Get-FabricCollection "workspaces/$($o.workspaceId)/roleAssignments")
            if (@($evidence.roles | Where-Object { $_.principal.id -ieq $o.groupObjectId }).Count) { throw 'Group already has a workspace role. Role modification requires separate review.' }
            $path="workspaces/$($o.workspaceId)/roleAssignments";$body=@{principal=@{id=$o.groupObjectId;type='Group'};role='Admin'}
        }
        'CreateConnection' {
            Assert-FabricGuid $o.workspaceId;Assert-FabricName $o.displayName
            if (!$evidence.workspace.workspaceIdentity) { throw 'Provision workspace identity and grant source data permissions first.' }
            if ($o.connectionType -cnotin @('AzureDataLakeStorage','SQL') -or $o.creationMethod -cne $o.connectionType) { throw 'Only matching reviewed ADLS/SQL connection examples are supported.' }
            if ($o.parameters -isnot [array] -or !$o.parameters.Count) { throw 'Connector parameters required.' }
            foreach ($p in $o.parameters) {
                Assert-FabricKeys $p @('name','dataType','value')
                if ($p.name -cnotin @('url','server','database') -or $p.dataType -cne 'Text' -or $p.value -isnot [string] -or [string]::IsNullOrWhiteSpace($p.value) -or $p.value -match '[?;@\x00-\x1f]') { throw 'Only non-secret connector locations are allowed.' }
                if ($o.connectionType -ceq 'AzureDataLakeStorage') {
                    $sourceUri=$null
                    if ($p.name -cne 'url' -or ![uri]::TryCreate($p.value,[UriKind]::Absolute,[ref]$sourceUri) -or $sourceUri.Scheme -cne 'https' -or !$sourceUri.IsDefaultPort -or $sourceUri.UserInfo -or $sourceUri.Query -or $sourceUri.Fragment -or $sourceUri.Host -cnotmatch '^[a-z0-9]{3,24}\.dfs\.core\.windows\.net$') { throw 'ADLS source must be an HTTPS Azure Data Lake endpoint without credentials, query or fragment.' }
                } elseif (($p.name -ceq 'server' -and $p.value -cnotmatch '^[a-z0-9][a-z0-9-]{0,62}\.database\.windows\.net$') -or ($p.name -ceq 'database' -and $p.value -notmatch '^[\p{L}\p{N}_ -]{1,128}$') -or $p.name -ceq 'url') { throw 'SQL example requires an Azure SQL server host and a plain database name.' }
            }
            if (@($o.parameters.name | Sort-Object -Unique).Count -ne $o.parameters.Count) { throw 'Duplicate connector parameters are not allowed.' }
            $evidence.supported=@(Get-FabricCollection 'connections/supportedConnectionTypes?showAllCreationMethods=true' | Where-Object { $_.type -ceq $o.connectionType })
            $methods=@($evidence.supported | ForEach-Object { $_.creationMethods } | Where-Object { $_.name -ceq $o.creationMethod })
            if ($evidence.supported.Count -ne 1 -or $methods.Count -ne 1 -or 'WorkspaceIdentity' -cnotin $evidence.supported[0].supportedCredentialTypes -or 'Encrypted' -cnotin $evidence.supported[0].supportedConnectionEncryptionTypes) { throw 'Connector/WorkspaceIdentity/encryption support must be confirmed by the live supported-type schema.' }
            foreach ($p in $o.parameters) { if (@($methods[0].parameters | Where-Object { $_.name -ceq $p.name -and $_.dataType -ceq $p.dataType }).Count -ne 1) { throw 'Connector parameter absent from live schema.' } }
            foreach ($p in @($methods[0].parameters | Where-Object { $_.required })) { if ($p.name -cnotin $o.parameters.name) { throw 'A required connector parameter is missing.' } }
            $evidence.existing=@(Get-FabricCollection 'connections' | Where-Object { $_.displayName -ieq $o.displayName })
            if ($evidence.existing.Count) { throw 'Connection name already exists.' }
            $path='connections';$body=@{displayName=$o.displayName;connectivityType='ShareableCloud';privacyLevel='Organizational';connectionDetails=@{type=$o.connectionType;creationMethod=$o.creationMethod;parameters=$o.parameters};credentialDetails=@{singleSignOnType='None';connectionEncryption='Encrypted';skipTestConnection=$false;credentials=@{credentialType='WorkspaceIdentity'}}}
        }
        'CreateManagedPrivateEndpoint' {
            Assert-FabricGuid $o.workspaceId;Assert-FabricName $o.name
            $prefix='^/subscriptions/'+[regex]::Escape($Manifest.subscriptionId)+'/resourceGroups/[^/]+/providers/'
            if ($o.targetPrivateLinkResourceId -notmatch ($prefix+'(Microsoft.Storage/storageAccounts|Microsoft.Sql/servers)/[a-zA-Z0-9-]+$') -or $o.targetSubresourceType -cnotin @('blob','dfs','sqlServer') -or $o.name.Length -gt 64) { throw 'Managed endpoint must target a reviewed same-subscription Storage or SQL resource.' }
            if (($o.targetPrivateLinkResourceId -match '/Microsoft.Storage/storageAccounts/' -and $o.targetSubresourceType -cnotin @('blob','dfs')) -or ($o.targetPrivateLinkResourceId -match '/Microsoft.Sql/servers/' -and $o.targetSubresourceType -cne 'sqlServer')) { throw 'Managed endpoint subresource does not match its source resource type.' }
            $evidence.existing=@(Get-FabricCollection "workspaces/$($o.workspaceId)/managedPrivateEndpoints" | Where-Object { $_.name -ieq $o.name })
            if ($evidence.existing.Count) { throw 'Managed endpoint already exists. Reconcile approval state.' }
            $path="workspaces/$($o.workspaceId)/managedPrivateEndpoints";$body=@{name=$o.name;targetPrivateLinkResourceId=$o.targetPrivateLinkResourceId;targetSubresourceType=$o.targetSubresourceType}
        }
        'CreateDeploymentPipeline' {
            Assert-FabricName $o.displayName
            if ($o.stages -isnot [array] -or $o.stages.Count -lt 2 -or $o.stages.Count -gt 10) { throw 'Configure two to ten explicit deployment stages.' }
            foreach ($s in $o.stages) { Assert-FabricKeys $s @('displayName');Assert-FabricName $s.displayName }
            if (@($o.stages.displayName | Sort-Object -Unique).Count -ne $o.stages.Count) { throw 'Duplicate deployment stages.' }
            $evidence.existing=@(Get-FabricCollection 'deploymentPipelines' | Where-Object { $_.displayName -ieq $o.displayName })
            if ($evidence.existing.Count) { throw 'Deployment pipeline already exists; use its exact ID.' }
            $path='deploymentPipelines';$body=@{displayName=$o.displayName;stages=@($o.stages | ForEach-Object { @{displayName=$_.displayName;isPublic=$false} })}
        }
        { $_ -cin @('AssignWorkspaceToStage','PromoteItems') } {
            Assert-FabricGuid $o.deploymentPipelineId
            $evidence.stages=@(Get-FabricCollection "deploymentPipelines/$($o.deploymentPipelineId)/stages")
            if ($o.kind -ceq 'AssignWorkspaceToStage') {
                Assert-FabricGuid $o.stageId;Assert-FabricGuid $o.workspaceId
                $stage=@($evidence.stages | Where-Object { $_.id -ieq $o.stageId })
                if ($stage.Count -ne 1 -or $stage[0].workspaceId) { throw 'Stage missing or already assigned. No automatic replacement.' }
                $policy=Invoke-FabricApi GET "workspaces/$($o.workspaceId)/networking/communicationPolicy"
                if ($policy.inbound.publicAccessRules.defaultAction -cne 'Allow') { throw 'Native deployment pipelines require public access Allow; restricted workspaces need a separately qualified release mechanism.' }
                $evidence.policy=$policy;$path="deploymentPipelines/$($o.deploymentPipelineId)/stages/$($o.stageId)/assignWorkspace";$body=@{workspaceId=$o.workspaceId}
            } else {
                Assert-FabricGuid $o.sourceStageId;Assert-FabricGuid $o.targetStageId
                $source=@($evidence.stages | Where-Object { $_.id -ieq $o.sourceStageId });$target=@($evidence.stages | Where-Object { $_.id -ieq $o.targetStageId })
                if ($source.Count -ne 1 -or $target.Count -ne 1 -or !$source[0].workspaceId -or !$target[0].workspaceId -or $target[0].order -ne ($source[0].order+1)) { throw 'Promotion requires existing consecutive assigned stages.' }
                foreach ($s in @($source[0],$target[0])) {
                    Assert-FabricGuid $s.workspaceId
                    if (($s.order -isnot [int] -and $s.order -isnot [long]) -or $s.order -lt 0 -or $s.order -gt 9) { throw 'Deployment stage order evidence is incomplete.' }
                }
                foreach ($s in @($source[0],$target[0])) {
                    $policy=Invoke-FabricApi GET "workspaces/$($s.workspaceId)/networking/communicationPolicy"
                    if ($policy.inbound.publicAccessRules.defaultAction -cne 'Allow') { throw 'Native promotion is unsupported for a deny-public workspace. Firewall rules will not be changed.' }
                    $evidence[$s.id]=$policy
                }
                if ($o.items -isnot [array] -or !$o.items.Count -or $o.items.Count -gt 300) { throw 'Explicit promotion items required (1-300); deploy-all is not supported.' }
                $evidence.sourceItems=@(Get-FabricCollection "workspaces/$($source[0].workspaceId)/items")
                foreach ($item in $o.items) {
                    Assert-FabricKeys $item @('sourceItemId','itemType');Assert-FabricGuid $item.sourceItemId
                    if (@($evidence.sourceItems | Where-Object { $_.id -ieq $item.sourceItemId -and $_.type -ceq $item.itemType }).Count -ne 1) { throw 'Promotion item does not match source evidence.' }
                }
                if (@($o.items.sourceItemId | Sort-Object -Unique).Count -ne $o.items.Count) { throw 'Duplicate promotion items.' }
                $evidence.stageItems=@(Get-FabricCollection "deploymentPipelines/$($o.deploymentPipelineId)/stages/$($o.sourceStageId)/items")
                $evidence.targetItems=@(Get-FabricCollection "workspaces/$($target[0].workspaceId)/items")
                $evidence.sourceDefinitions=@{};$evidence.targetDefinitions=@{};$evidence.promotionTargets=@{}
                foreach ($item in $o.items) {
                    $sourceItem=@($evidence.sourceItems | Where-Object { $_.id -ieq $item.sourceItemId })
                    $pair=@($evidence.stageItems | Where-Object { $_.itemId -ieq $item.sourceItemId -and $_.itemType -ceq $item.itemType })
                    if ($pair.Count -ne 1 -or $pair[0].itemDisplayName -cne $sourceItem[0].displayName) { throw 'Explicit promotion item is absent from supported stage/pairing evidence.' }
                    $evidence.sourceDefinitions[$item.sourceItemId]=Get-FabricItemDefinitionHash $source[0].workspaceId $item.sourceItemId
                    $targetItemId=[string]$pair[0].targetItemId
                    $evidence.promotionTargets[$item.sourceItemId]=$targetItemId
                    if ($targetItemId) {
                        Assert-FabricGuid $targetItemId
                        if (@($evidence.targetItems | Where-Object { $_.id -ieq $targetItemId -and $_.type -ceq $item.itemType }).Count -ne 1) { throw 'Paired target item is absent from target workspace evidence.' }
                        $evidence.targetDefinitions[$targetItemId]=Get-FabricItemDefinitionHash $target[0].workspaceId $targetItemId
                    } elseif (@($evidence.targetItems | Where-Object { $_.displayName -ceq $sourceItem[0].displayName -and $_.type -ceq $item.itemType }).Count) { throw 'Unpaired target name collision requires separate pairing review.' }
                }
                $path="deploymentPipelines/$($o.deploymentPipelineId)/deploy";$body=@{sourceStageId=$o.sourceStageId;targetStageId=$o.targetStageId;items=$o.items;options=@{allowCrossRegionDeployment=$false}}
            }
        }
    }
    return @{kind=$o.kind;method='POST';path=$path;body=$body;before=$evidence}
}
