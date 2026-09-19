# Stack/Template Spec backend. Loaded by self-service-common.ps1; Azure calls share its mockable boundary.
function Assert-StackTooling {
    $version=Invoke-ServiceJson @('version')
    if ([version]$version['azure-cli'] -lt [version]'2.89.1') { throw 'Stack pipeline requires Azure CLI 2.89.1 or newer (the locally verified baseline).' }
    $help=Invoke-Az -Arguments @('stack-whatif','sub','create','--help')
    foreach($flag in @('--template-spec','--validation-level','--no-pretty-print','--retention-interval')) { if (($help -join "`n") -notmatch [regex]::Escape($flag)) { throw "CLI lacks required stack preview support: $flag" } }
}
function Read-StackConfiguration {
    $c=Get-Content (Join-Path (Get-ProjectRoot) config/deployment-stack.json) -Raw | ConvertFrom-Json -AsHashtable
    if ($c.schemaVersion -ne 1 -or $c.actionOnUnmanage -cne 'detachAll' -or $c.denySettingsMode -cnotin @('none','denyDelete')) { throw 'Unsupported stack lifecycle policy; destructive and deny-write modes require a separate migration.' }
    $s=$c.templateSpec
    if ([guid]::Parse($s.subscriptionId) -eq [guid]::Empty) { throw 'Template Spec subscription is required.' }
    foreach($key in @('resourceGroup','name','publisherServiceConnection','publisherEnvironment','publisherAgentPool')) { if ($s[$key] -cnotmatch '^[a-zA-Z0-9][a-zA-Z0-9._-]{0,79}$') { throw "Invalid Template Spec binding: $key" } }
    if ($s.location -cnotmatch '^[a-z0-9]+$') { throw 'Invalid publication region.' }
    return $c
}
function Assert-StackTemplate($Template) {
    # Linked deployments hide resources from preview. Published compositions must be self-contained.
    $pending=[Collections.Generic.Queue[object]]::new(); $pending.Enqueue($Template)
    while($pending.Count) {
        $node=$pending.Dequeue()
        if($node -is [Collections.IDictionary]) {
            if($node.Contains('templateLink')) { throw 'Nested templateLink is not allowed in a qualified stack template.' }
            foreach($value in $node.Values) { if($null -ne $value) {$pending.Enqueue($value)} }
        } elseif($node -is [array]) { foreach($value in $node) { if($null -ne $value) {$pending.Enqueue($value)} } }
    }
    if ($Template['$schema'] -notlike '*subscriptionDeploymentTemplate.json#') { throw 'Stack template must own a subscription-scoped workload composition.' }
}
function New-StackContract($Target,$Template,$Configuration=(Read-StackConfiguration)) {
    Assert-StackTemplate $Template
    $s=$Configuration.templateSpec; $hash=Get-ValueHash $Template
    $name="stack-$($Target.workload)-$($Target.environmentName)"
    $parent="/subscriptions/$($s.subscriptionId)/resourceGroups/$($s.resourceGroup)/providers/Microsoft.Resources/templateSpecs/$($s.name)"
    return @{schemaVersion=1;engine='deploymentStack';stackName=$name;stackId="/subscriptions/$($Target.subscriptionId)/providers/Microsoft.Resources/deploymentStacks/$name";templateHash=$hash;templateSpecId="$parent/versions/sha256-$hash";publication=$s;actionOnUnmanage=$Configuration.actionOnUnmanage;denySettingsMode=$Configuration.denySettingsMode;targetKey=(Get-ValueHash @{subscription=$Target.subscriptionId;group=$Target.resourceGroup;workload=$Target.workload;environment=$Target.environmentName})}
}
function Assert-StackContract($Contract,$Target,$Template) {
    $expected=New-StackContract $Target $Template
    if ((Get-ValueHash $Contract) -cne (Get-ValueHash $expected)) { throw 'Frozen stack contract differs from reviewed platform configuration or compiled content.' }
}
function Get-PublishedStackTemplate($Bundle) {
    $spec=Invoke-ServiceJson @('ts','show','--id',$Bundle.stack.templateSpecId)
    if ($spec.id -ine $Bundle.stack.templateSpecId -or !$spec.properties.Contains('mainTemplate')) { throw 'Unexpected Template Spec identity/content.' }
    if ($spec.properties.Contains('linkedTemplates') -and @($spec.properties.linkedTemplates).Count) { throw 'Linked Template Spec content is not permitted.' }
    Assert-StackTemplate $spec.properties.mainTemplate
    if ((Get-ValueHash $spec.properties.mainTemplate) -cne $Bundle.stack.templateHash) { throw 'Published Template Spec content hash mismatch.' }
    return $spec
}
function Get-StackRestCollection([string]$Url) {
    $items=@(); $visited=@{}
    while($Url) {
        if ($Url -notmatch '^https://management\.azure\.com/' -or $visited.ContainsKey($Url)) { throw 'Unexpected ARM pagination URL.' }
        $visited[$Url]=$true
        $page=Invoke-ServiceJson @('rest','--method','get','--url',$Url)
        if (!$page.Contains('value')) { throw 'Incomplete ARM collection.' }
        $items+=@($page.value); $Url=if($page.Contains('nextLink')){$page.nextLink}else{''}
    }
    return ,$items
}
function Publish-StackTemplate($Bundle,[string]$Directory) {
    $s=$Bundle.stack.publication; $version='sha256-'+$Bundle.stack.templateHash
    $parent=$Bundle.stack.templateSpecId -replace '/versions/[^/]+$',''
    $specs=@(Invoke-ServiceJson @('ts','list','--subscription',$s.subscriptionId,'--resource-group',$s.resourceGroup))
    $exists=$false
    if (@($specs | Where-Object {$_.id -ieq $parent}).Count) {
        $versions=Get-StackRestCollection "https://management.azure.com$parent/versions?api-version=2022-02-01"
        $exists=@($versions | Where-Object {$_.id -ieq $Bundle.stack.templateSpecId}).Count -gt 0
    }
    if (!$exists) {
        $null=Invoke-ServiceJson @('ts','create','--subscription',$s.subscriptionId,'--resource-group',$s.resourceGroup,'--name',$s.name,'--version',$version,'--location',$s.location,'--template-file',(Join-Path $Bundle.directory stack-template.json))
    }
    # Never update a found version: reject content drift, including a concurrent conflicting publisher.
    $null=Get-PublishedStackTemplate $Bundle
    $receipt=@{schemaVersion=1;templateSpecId=$Bundle.stack.templateSpecId;templateHash=$Bundle.stack.templateHash;bundleHash=$Bundle.hash;sourceCommit=$Bundle.receipt.sourceCommit;reused=$exists;verifiedUtc=[DateTimeOffset]::UtcNow.ToString('O')}
    Write-ServiceJson $receipt (Join-Path $Directory publication.json)
    return $receipt
}
function Get-WorkloadStack($Bundle) {
    $stacks=@(Invoke-ServiceJson @('stack','sub','list','--subscription',$Bundle.target.subscriptionId))
    $matches=@($stacks | Where-Object {$_.id -ieq $Bundle.stack.stackId})
    if ($matches.Count -gt 1) { throw 'Ambiguous stack identity.' }
    if (!$matches.Count) { return $null }
    $stack=Invoke-ServiceJson @('stack','sub','show','--subscription',$Bundle.target.subscriptionId,'--name',$Bundle.stack.stackName)
    if ($stack.id -ine $Bundle.stack.stackId -or !$stack.Contains('tags') -or !$stack.tags.Contains('targetKey') -or $stack.tags.targetKey -cne $Bundle.stack.targetKey) { throw 'Existing stack is not owned by this target; explicit adoption review required.' }
    if ($stack.properties.provisioningState -ne 'Succeeded') { throw 'Stack is not in a successful state; platform recovery required.' }
    if ($stack.properties.denySettings.mode -ine $Bundle.stack.denySettingsMode) { throw 'Deny settings drift requires platform review.' }
    foreach($key in @('excludedPrincipals','excludedActions')) { if ($stack.properties.denySettings.Contains($key) -and @($stack.properties.denySettings[$key]).Count) { throw 'Unapproved deny exclusions require platform review.' } }
    foreach($key in @('resources','resourceGroups')) { if (!$stack.properties.actionOnUnmanage.Contains($key) -or $stack.properties.actionOnUnmanage[$key] -ine 'detach') { throw 'Stack unmanage policy drift requires platform review.' } }
    return $stack
}
function Assert-StackManagedId($Bundle,[string]$Id) {
    $rg="/subscriptions/$($Bundle.target.subscriptionId)/resourceGroups/$($Bundle.target.resourceGroup)"
    if ($Id -ieq $rg -or $Id.StartsWith($rg+'/providers/',[StringComparison]::OrdinalIgnoreCase)) { return }
    # Only workload-created grants (not destination storage itself) cross this lifecycle boundary.
    $p=$Bundle.parameters.parameters
    $destination="/subscriptions/$($p.destinationSubscriptionId.value)/resourceGroups/$($p.destinationResourceGroupName.value)"
    $container="$destination/providers/Microsoft.Storage/storageAccounts/$($p.destinationStorageAccountName.value)/blobServices/default/containers/$($p.destinationContainerName.value)"
    if ($Id -match ('^'+[regex]::Escape($container)+'/providers/Microsoft.Authorization/roleAssignments/[0-9a-f-]{36}$')) { return }
    if ($Id -match ('^'+[regex]::Escape($destination)+'/providers/Microsoft.Resources/deployments/destination-access-'+[regex]::Escape($Bundle.target.environmentName)+'-[a-z0-9]+$')) { return }
    throw "Resource is outside workload lifecycle ownership: $Id"
}
function Get-WorkloadStackState($Bundle) {
    $stack=Get-WorkloadStack $Bundle
    if (!$stack) {
        $groups=@(Invoke-ServiceJson @('group','list','--subscription',$Bundle.target.subscriptionId))
        if (@($groups | Where-Object {$_.name -ieq $Bundle.target.resourceGroup}).Count) {
            throw 'Workload resource group already exists without this stack. Explicit adoption review required.'
        }
        return @{hasApp=$false;appIds=@();stackExists=$false;managedResources=@()}
    }
    $p=$stack.properties
    if (!$p.Contains('parameters') -or !$p.parameters.Contains('deployFunctionApp') -or !$p.parameters.deployFunctionApp.Contains('value') -or $p.parameters.deployFunctionApp.value -isnot [bool]) { throw 'Stack phase parameter is missing or invalid; platform recovery required.' }
    $released=$p.parameters.deployFunctionApp.value
    foreach($resource in $p.resources) { if($resource.Contains('status') -and $resource.status -ine 'managed') { throw 'Stack has an unhealthy managed resource; platform recovery required.' } }
    $ids=@($p.resources | ForEach-Object {$_.id.ToLowerInvariant()} | Sort-Object)
    if (!$ids.Count) { throw 'Stack managed-resource inventory is empty.' }
    foreach($id in $ids){Assert-StackManagedId $Bundle $id}
    return @{hasApp=$released;appIds=@();stackExists=$true;managedResources=$ids;stackStateHash=(Get-ValueHash @{resources=@($p.resources | Sort-Object id);parameters=$p.parameters;outputs=$p.outputs;denySettings=$p.denySettings;actionOnUnmanage=$p.actionOnUnmanage});stackId=$stack.id}
}
function Convert-StackPropertyChanges($Deltas) {
    $result=@()
    foreach($delta in $Deltas) {
        $n=@{path=$delta.path;propertyChangeType=$delta.changeType}
        foreach($key in @('before','after')) { if($delta.Contains($key)){$n[$key]=$delta[$key]} }
        if($delta.Contains('children')) {$n.children=@(Convert-StackPropertyChanges $delta.children)}
        $result+=$n
    }
    return $result
}
function Convert-StackPreview($Report,$Bundle,$State) {
    $p=$Report.properties
    if ($p.provisioningState -ne 'Succeeded' -or $p.deploymentStackResourceId -ine $Bundle.stack.stackId -or ($p.Contains('error') -and $p.error)) { throw 'Stack preview failed or identifies another stack.' }
    if ($p.Contains('diagnostics') -and @($p.diagnostics).Count) { throw 'Stack preview diagnostics require review; incomplete analysis is not approved.' }
    if (!$p.Contains('changes') -or !$p.changes.Contains('resourceChanges') -or !$p.changes.Contains('denySettingsChange')) { throw 'Stack preview lacks analyzed changes.' }
    $c=$p.changes
    if ($c.denySettingsChange.Contains('after') -and $c.denySettingsChange.after.mode -ine $Bundle.stack.denySettingsMode) { throw 'Preview deny settings do not match the approved mode.' }
    if ($c.Contains('deploymentScopeChange') -and $c.deploymentScopeChange.before -and $c.deploymentScopeChange.before -ine $c.deploymentScopeChange.after) { throw 'Stack scope change is forbidden.' }
    if ($State.stackExists -and $c.denySettingsChange.Contains('delta') -and @($c.denySettingsChange.delta).Count) { throw 'Deny settings changes require platform review.' }
    $changes=@(); $seen=@{}
    foreach($r in $c.resourceChanges) {
        if (!$r.id -or $r.changeCertainty -cne 'definite' -or $seen.ContainsKey($r.id)) { throw 'Ambiguous or potential stack resource change.' }
        $seen[$r.id]=$true
        Assert-StackManagedId $Bundle $r.id
        if ($r.changeType -notin @('create','modify','noChange')) { throw "Forbidden stack lifecycle change: $($r.changeType)" }
        if($r.Contains('managementStatusChange') -and $r.managementStatusChange.after -ne 'managed') { throw 'Stack would lose resource ownership.' }
        if($r.Contains('denyStatusChange') -and $r.denyStatusChange.before -in @('denyDelete','denyWriteAndDelete') -and $r.denyStatusChange.before -ine $r.denyStatusChange.after) { throw 'Stack would weaken resource deny protection.' }
        $change=@{resourceId=$r.id;changeType=$r.changeType}
        if($r.Contains('resourceConfigurationChanges') -and $r.resourceConfigurationChanges) {
            $config=$r.resourceConfigurationChanges
            foreach($key in @('before','after')) { if($config.Contains($key)) {$change[$key]=$config[$key]} }
            if($config.Contains('delta')) { $change.delta=@(Convert-StackPropertyChanges $config.delta) }
        }
        # Membership-only Modify must still have explicit unchanged configuration evidence.
        if ($r.changeType -eq 'modify' -and (!$change.Contains('delta') -or !$change.delta.Count)) { throw 'Stack Modify has no property-level evidence.' }
        $changes+=$change
    }
    foreach($id in $State.managedResources) { if(!$seen.ContainsKey($id)) { throw 'Stack preview omitted an existing managed resource.' } }
    if (!$changes.Count) { throw 'Empty stack preview cannot establish coverage.' }
    $rg="/subscriptions/$($Bundle.target.subscriptionId)/resourceGroups/$($Bundle.target.resourceGroup)"
    if(!$seen.ContainsKey($rg)) { throw 'Stack preview must include ownership of the workload resource group.' }
    $null=Get-ServiceChanges @{status='Succeeded';changes=$changes}
    return @{status='Succeeded';changes=@($changes | Sort-Object resourceId)}
}
function Get-StackDeploymentArguments($Bundle,[string]$ParametersPath) {
    return @('--subscription',$Bundle.target.subscriptionId,'--name',$Bundle.stack.stackName,'--location',$Bundle.parameters.parameters.location.value,'--template-spec',$Bundle.stack.templateSpecId,'--parameters',"@$ParametersPath",'--action-on-unmanage',$Bundle.stack.actionOnUnmanage,'--deny-settings-mode',$Bundle.stack.denySettingsMode,'--validation-level','Provider')
}
function New-StackPreview($Bundle,$State,[string]$ParametersPath,[string]$Directory) {
    $null=Get-PublishedStackTemplate $Bundle
    $deploymentArguments=Get-StackDeploymentArguments $Bundle $ParametersPath
    $validation=Invoke-ServiceJson (@('stack','sub','validate')+$deploymentArguments)
    Write-ServiceJson $validation (Join-Path $Directory arm-validation.json)
    $name='preview-'+[guid]::NewGuid().ToString('N')
    $previewArgs=@('--subscription',$Bundle.target.subscriptionId,'--name',$name,'--location',$Bundle.parameters.parameters.location.value,'--stack-id',$Bundle.stack.stackId,'--template-spec',$Bundle.stack.templateSpecId,'--parameters',"@$ParametersPath",'--action-on-unmanage',$Bundle.stack.actionOnUnmanage,'--deny-settings-mode',$Bundle.stack.denySettingsMode,'--validation-level','Provider','--retention-interval','P1D','--no-pretty-print')
    try {
        $raw=Invoke-ServiceJson (@('stack-whatif','sub','create')+$previewArgs)
        Write-ServiceJson $raw (Join-Path $Directory stack-what-if.json)
        return Convert-StackPreview $raw $Bundle $State
    } finally {
        # Removes only this invocation's metadata resource; never the stack/workload.
        $null=Invoke-Az -Arguments @('stack-whatif','sub','delete','--subscription',$Bundle.target.subscriptionId,'--name',$name,'--yes')
    }
}
function Invoke-WorkloadStackApply($Bundle,[string]$ParametersPath,[string]$Directory,$Approved) {
    $null=Get-PublishedStackTemplate $Bundle
    $deploymentArguments=Get-StackDeploymentArguments $Bundle $ParametersPath
    $stack=Invoke-ServiceJson (@('stack','sub','create')+$deploymentArguments+@('--tags',"targetKey=$($Bundle.stack.targetKey)",'managedBy=blob-transfer-platform','--yes'))
    Write-ServiceJson $stack (Join-Path $Directory stack-result.json)
    if ($stack.id -ine $Bundle.stack.stackId -or $stack.properties.provisioningState -ne 'Succeeded') { throw 'Stack deployment did not succeed.' }
    $managed=@($stack.properties.resources | ForEach-Object {$_.id.ToLowerInvariant()} | Sort-Object)
    foreach($id in $managed){Assert-StackManagedId $Bundle $id}
    foreach($change in $Approved.changes) { if($change.resourceId -notin $managed) { throw 'Stack inventory does not contain an approved resource.' } }
    foreach($id in $Approved.state.managedResources) { if($id -notin $managed) { throw 'Stack lost a previously managed resource.' } }
    if(!$managed.Count) { throw 'Deployment returned no managed resources.' }
    Write-ServiceJson @{stackId=$stack.id;templateSpecId=$Bundle.stack.templateSpecId;templateHash=$Bundle.stack.templateHash;managedResourceIds=$managed;denySettings=$stack.properties.denySettings;actionOnUnmanage=$stack.properties.actionOnUnmanage} (Join-Path $Directory lifecycle.json)
    return $stack
}
