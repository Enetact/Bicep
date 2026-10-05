#requires -Version 7.4
[CmdletBinding()]
param([ValidateSet('Preview','Deploy')][string]$Mode='Preview',[ValidateSet('dev','qa','uat','prod')][string]$EnvironmentName='dev',[Parameter(Mandatory)][string]$OutputDirectory,[string]$PreviewDirectory)
. "$PSScriptRoot/helm-common.ps1"
$root=Get-ProjectRoot
$OutputDirectory=[IO.Path]::GetFullPath($OutputDirectory);New-Item -ItemType Directory $OutputDirectory -Force|Out-Null
Write-HelmJson @{schemaVersion=1;mode=$Mode;status='Blocked';mutationAttempted=$false;note='Preflight did not complete. See the pipeline task error; no deployment started.'} (Join-Path $OutputDirectory receipt.json)
[IO.File]::WriteAllText((Join-Path $OutputDirectory README.md),"# Helm delivery`n`nBlocked during preflight. No deployment started. Inspect the task error and complete the source-owned profile, tools, identities and network prerequisites.",[Text.UTF8Encoding]::new($false))
$profile=Read-HelmProfile $EnvironmentName -AllowDisabled:($Mode -eq 'Preview')
if($env:BUILD_SOURCEBRANCH -cne 'refs/heads/main' -or $env:BUILD_BUILDID -cnotmatch '^\d+$'){throw 'Helm delivery requires a manual trusted main-branch ADO run.'}
$commit=(Invoke-HelmTool git @('-C',$root,'rev-parse','HEAD')).text.Trim()
if($commit -cne $env:BUILD_SOURCEVERSION -or (Invoke-HelmTool git @('-C',$root,'status','--porcelain')).text.Trim()){throw 'Use the clean exact pipeline source commit.'}
foreach($tool in @('az','helm','kubectl','kubelogin')){if(!(Get-Command $tool -ErrorAction SilentlyContinue)){throw "Private agent prerequisite missing: $tool"}}
if((Invoke-HelmTool helm @('version','--template','{{.Version}}')).text.Trim() -cne 'v4.3.0'){throw 'Qualify Helm v4.3.0 on the private agent.'}
$temp=Join-Path ([IO.Path]::GetTempPath()) ('platform-helm-'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $temp|Out-Null
$oldKube=$env:KUBECONFIG;$oldPlugins=$env:HELM_PLUGINS;$oldDiff=$env:KUBECTL_EXTERNAL_DIFF
$helmEnvironment=@{}
foreach($key in @('HELM_KUBEAPISERVER','HELM_KUBECAFILE','HELM_KUBECONTEXT','HELM_KUBEINSECURE_SKIP_TLS_VERIFY','HELM_KUBETLS_SERVER_NAME','HELM_KUBETOKEN','HELM_DRIVER','HELM_DRIVER_SQL_CONNECTION_STRING')){
    $helmEnvironment[$key]=[Environment]::GetEnvironmentVariable($key)
    [Environment]::SetEnvironmentVariable($key,$null)
}
$env:HELM_DRIVER='secret'
$env:KUBECONFIG=Join-Path $temp config;$env:HELM_PLUGINS=Join-Path $temp plugins;$env:KUBECTL_EXTERNAL_DIFF=$null
$receipt=@{schemaVersion=1;mode=$Mode;status='Blocked';sourceCommit=$commit;runId=$env:BUILD_BUILDID;clusterId=$profile.clusterResourceId;namespace=$profile.namespace;release=$profile.release;mutationAttempted=$false}
try {
    $account=(Invoke-HelmTool az @('account','show','-o','json')).text|ConvertFrom-Json -AsHashtable
    if($account.id -ine $profile.subscriptionId){throw 'Wrong service-connection subscription.'}
    $cluster=(Invoke-HelmTool az @('resource','show','--ids',$profile.clusterResourceId,'--api-version','2026-02-01','-o','json')).text|ConvertFrom-Json -AsHashtable
    if($cluster.id -ine $profile.clusterResourceId -or $cluster.properties.provisioningState -cne 'Succeeded' -or $cluster.properties.disableLocalAccounts -ne $true -or $cluster.properties.apiServerAccessProfile.enablePrivateCluster -ne $true -or $cluster.properties.aadProfile.enableAzureRBAC -ne $true -or $cluster.properties.securityProfile.workloadIdentity.enabled -ne $true){throw 'Cluster private/Entra/workload-identity readiness failed.'}
    $clusterBinding=@{id=$cluster.id;privateFqdn=$cluster.properties.privateFQDN;oidcIssuer=$cluster.properties.oidcIssuerProfile.issuerURL}
    $identityId=($profile.clusterResourceId -replace '/providers/Microsoft.ContainerService/managedClusters/[^/]+$','')+'/providers/Microsoft.ManagedIdentity/userAssignedIdentities/id-'+$cluster.name+'-app'
    $identity=(Invoke-HelmTool az @('resource','show','--ids',$identityId,'--api-version','2024-11-30','-o','json')).text|ConvertFrom-Json -AsHashtable
    if($identity.id -ine $identityId -or $identity.properties.clientId -ine $profile.values.workloadIdentityClientId){throw 'Application identity differs from the AKS foundation.'}
    $federation=(Invoke-HelmTool az @('resource','show','--ids',($identityId+'/federatedIdentityCredentials/kubernetes'),'--api-version','2024-11-30','-o','json')).text|ConvertFrom-Json -AsHashtable
    if($federation.properties.issuer -cne $clusterBinding.oidcIssuer -or $federation.properties.subject -cne "system:serviceaccount:$($profile.namespace):$($profile.values.serviceAccountName)" -or @($federation.properties.audiences).Count -ne 1 -or $federation.properties.audiences[0] -cne 'api://AzureADTokenExchange'){throw 'Application federation does not match this cluster/namespace/service account.'}
    $clusterBinding.applicationIdentity=$identity.properties.clientId
    $registry=(Invoke-HelmTool az @('resource','show','--ids',$profile.registryResourceId,'--api-version','2023-07-01','-o','json')).text|ConvertFrom-Json -AsHashtable
    if($registry.id -ine $profile.registryResourceId -or $registry.sku.name -cne 'Premium' -or $registry.properties.publicNetworkAccess -cne 'Disabled' -or $registry.properties.adminUserEnabled -ne $false -or !$profile.values.imageRepository.StartsWith($registry.properties.loginServer+'/',[StringComparison]::Ordinal)){throw 'Image must use the reviewed private Premium ACR.'}
    $imagePath=$profile.values.imageRepository.Substring($registry.properties.loginServer.Length+1)+'@'+$profile.values.imageDigest
    $null=Invoke-HelmTool az @('acr','manifest','show-metadata','--registry',$registry.name,'--name',$imagePath,'--only-show-errors','-o','json')
    $clusterBinding.registryId=$registry.id
    $parts=$profile.clusterResourceId.Split('/');$null=Invoke-HelmTool az @('aks','get-credentials','--resource-group',$parts[4],'--name',$parts[8],'--file',$env:KUBECONFIG,'--overwrite-existing','--format','exec')
    $null=Invoke-HelmTool kubelogin @('convert-kubeconfig','--login','azurecli','--kubeconfig',$env:KUBECONFIG)
    $server=(Invoke-HelmTool kubectl @('config','view','--minify','-o','json')).text|ConvertFrom-Json -AsHashtable
    if($server.clusters.Count -ne 1 -or ([uri]$server.clusters[0].cluster.server).Host -ine $clusterBinding.privateFqdn){throw 'Kubernetes endpoint differs from the ARM private API.'}
    $versions=(Invoke-HelmTool kubectl @('version','-o','json')).text|ConvertFrom-Json -AsHashtable
    Assert-HelmKubernetesVersions $versions
    $clusterBinding.kubernetesVersion=$versions.serverVersion.gitVersion
    $chart=Join-Path $root charts/platform-app
    if($Mode -eq 'Preview'){
        Write-HelmJson $profile.values (Join-Path $OutputDirectory values.json)
        $null=Invoke-HelmTool helm @('lint',$chart,'--strict','--values',(Join-Path $OutputDirectory values.json))
        $null=Invoke-HelmTool helm @('package',$chart,'--destination',$OutputDirectory)
        $package=Join-Path $OutputDirectory platform-app-0.1.0.tgz;Assert-HelmChart $package $chart
        $rendered=(Invoke-HelmTool helm @('template',$profile.release,$package,'--namespace',$profile.namespace,'--values',(Join-Path $OutputDirectory values.json))).text
        $items=Read-HelmRendered $rendered $profile
        [IO.File]::WriteAllText((Join-Path $OutputDirectory rendered.yaml),$rendered,[Text.UTF8Encoding]::new($false))
        $before=Get-HelmBefore $profile $items
        $null=Invoke-HelmTool kubectl @('apply','--dry-run=server','--validate=strict','-n',$profile.namespace,'-f',(Join-Path $OutputDirectory rendered.yaml))
        $diff=Invoke-HelmTool kubectl @('diff','-n',$profile.namespace,'-f',(Join-Path $OutputDirectory rendered.yaml)) @(0,1)
        [IO.File]::WriteAllText((Join-Path $OutputDirectory changes.diff),$diff.text,[Text.UTF8Encoding]::new($false))
        Write-HelmJson $before (Join-Path $OutputDirectory before.json)
        $files=@{};foreach($name in @('platform-app-0.1.0.tgz','values.json','rendered.yaml')){$files[$name]=(Get-FileHash (Join-Path $OutputDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant()}
        $plan=@{schemaVersion=1;sourceCommit=$commit;runId=$env:BUILD_BUILDID;createdUtc=[DateTimeOffset]::UtcNow.ToString('O');profileHash=(Get-HelmHash (ConvertTo-HelmCanonical $profile));cluster=$clusterBinding;beforeHash=(Get-HelmHash (ConvertTo-HelmCanonical $before));files=$files}
        $rows=@($items|ForEach-Object { $item=$_;$exists=@($before.objects|Where-Object {$_.kind -ceq $item.kind -and $_.exists}).Count -gt 0;"| $($item.kind) | $($item.metadata.name) | $(if($exists){'Compare existing (see diff)'}else{'Create'}) |"})
        $summary=@('# Helm application Preview','','This is Kubernetes server validation and kubectl diff, not ARM What-If or an exact Helm three-way patch. No deployment has run.',"Target: $($profile.clusterResourceId); namespace: $($profile.namespace); release: $($profile.release).",'', '| Kind | Name | Proposed action |','|---|---|---|')+$rows+@('', 'See rendered.yaml, changes.diff, before.json and plan.json. Deploy requires the same run/source, fresh state, a protected environment approval and an exclusive lock. No PVCs, Secrets, hooks, CRDs, namespaces or public Services are managed. No automatic rollback.')
        [IO.File]::WriteAllText((Join-Path $OutputDirectory README.md),($summary -join "`n"),[Text.UTF8Encoding]::new($false));$receipt.status='Previewed'
        foreach($name in @('before.json','changes.diff','README.md')){$plan.files[$name]=(Get-FileHash (Join-Path $OutputDirectory $name) -Algorithm SHA256).Hash.ToLowerInvariant()}
        Write-HelmJson $plan (Join-Path $OutputDirectory plan.json)
    }else{
        if(!$PreviewDirectory){throw 'Same-run Preview artifact required.'}
        $plan=Get-Content (Join-Path $PreviewDirectory plan.json) -Raw|ConvertFrom-Json -AsHashtable
        Assert-HelmPlan $plan $profile $commit $env:BUILD_BUILDID $PreviewDirectory
        if((ConvertTo-HelmCanonical $plan.cluster) -cne (ConvertTo-HelmCanonical $clusterBinding)){throw 'Cluster binding changed.'}
        $package=Join-Path $PreviewDirectory platform-app-0.1.0.tgz;Assert-HelmChart $package $chart
        $values=Join-Path $PreviewDirectory values.json
        if((ConvertTo-HelmCanonical (Get-Content $values -Raw|ConvertFrom-Json -AsHashtable)) -cne (ConvertTo-HelmCanonical $profile.values)){throw 'Preview values differ from source.'}
        $rendered=(Invoke-HelmTool helm @('template',$profile.release,$package,'-n',$profile.namespace,'-f',$values)).text
        if((Get-HelmHash $rendered) -cne $plan.files['rendered.yaml']){throw 'Chart rendering changed.'}
        $items=Read-HelmRendered $rendered $profile;$before=Get-HelmBefore $profile $items
        if((Get-HelmHash (ConvertTo-HelmCanonical $before)) -cne $plan.beforeHash){throw 'Kubernetes state/release revision changed; run a new Preview.'}
        $null=Invoke-HelmTool kubectl @('apply','--dry-run=server','--validate=strict','-n',$profile.namespace,'-f',(Join-Path $PreviewDirectory rendered.yaml))
        $receipt.mutationAttempted=$true;$receipt.status='OutcomeUnknown';Write-HelmJson $receipt (Join-Path $OutputDirectory receipt.json)
        $null=Invoke-HelmTool helm @('upgrade','--install',$profile.release,$package,'-n',$profile.namespace,'-f',$values,'--wait=watcher','--timeout','10m','--history-max','10','--no-hooks')
        $receipt.status='Applied'
        $null=Invoke-HelmTool kubectl @('rollout','status',"deployment/$($profile.release)",'-n',$profile.namespace,'--timeout=300s')
        $after=Get-HelmBefore $profile $items;Write-HelmJson $after (Join-Path $OutputDirectory after.json)
        $deployment=(Invoke-HelmTool kubectl @('get','deployment',$profile.release,'-n',$profile.namespace,'-o','json')).text|ConvertFrom-Json -AsHashtable
        if($deployment.spec.template.spec.containers[0].image -cne "$($profile.values.imageRepository)@$($profile.values.imageDigest)" -or $deployment.status.availableReplicas -ne $profile.values.replicas -or $deployment.status.updatedReplicas -ne $profile.values.replicas){throw 'Image/replica read-back failed.'}
        $receipt.status='Verified';$receipt.verification='Helm wait, rollout, ready replicas and image digest. Probe paths are defined by the chart; external business effects are not verified.'
    }
}catch{if(!$receipt.mutationAttempted){$receipt.status='Blocked'}elseif($receipt.status -eq 'Applied'){$receipt.status='VerificationFailed'};$receipt.failure=$_.Exception.Message;throw}
finally{
    Write-HelmJson $receipt (Join-Path $OutputDirectory receipt.json)
    if($Mode -eq 'Deploy' -or $receipt.status -ne 'Previewed'){
        [IO.File]::WriteAllText((Join-Path $OutputDirectory README.md),("# Helm $Mode`n`nStatus: $($receipt.status). Mutation attempted: $($receipt.mutationAttempted).`n`nInspect receipt.json and any retained before/after evidence. Reconcile uncertain or failed writes before another run; no automatic rollback is performed."),[Text.UTF8Encoding]::new($false))
    }
    $env:KUBECONFIG=$oldKube;$env:HELM_PLUGINS=$oldPlugins;$env:KUBECTL_EXTERNAL_DIFF=$oldDiff
    foreach($key in $helmEnvironment.Keys){[Environment]::SetEnvironmentVariable($key,$helmEnvironment[$key])}
    $resolved=[IO.Path]::GetFullPath($temp);if(!$resolved.StartsWith([IO.Path]::GetFullPath([IO.Path]::GetTempPath()),[StringComparison]::OrdinalIgnoreCase) -or [IO.Path]::GetFileName($resolved) -notlike 'platform-helm-*'){throw 'Refusing unsafe temporary cleanup.'}
    Remove-Item -LiteralPath $resolved -Recurse -Force
}
