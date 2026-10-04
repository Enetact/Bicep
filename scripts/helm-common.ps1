. "$PSScriptRoot/common.ps1"
. "$PSScriptRoot/aks-common.ps1"
function Invoke-HelmTool([string]$Tool,[string[]]$Arguments,[int[]]$AllowedCodes=@(0)) {
    $output=& $Tool @Arguments 2>&1;$code=$LASTEXITCODE
    if($code -notin $AllowedCodes){throw "$Tool $($Arguments[0]) failed (exit $code); no write retry was attempted."}
    return @{text=($output -join "`n");code=$code}
}
function Get-HelmHash([string]$Text) { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($Text))).ToLowerInvariant() }
function ConvertTo-HelmCanonical($Value) {
    if($null -eq $Value){return 'null'}
    if($Value -is [Collections.IDictionary]){return '{'+(@($Value.Keys|Sort-Object -CaseSensitive|ForEach-Object {($_|ConvertTo-Json -Compress)+':'+(ConvertTo-HelmCanonical $Value[$_])}) -join ',')+'}'}
    if($Value -is [array]){return '['+(@($Value|ForEach-Object {ConvertTo-HelmCanonical $_}) -join ',')+']'}
    return ConvertTo-Json -InputObject $Value -Compress -Depth 100
}
function Write-HelmJson($Value,[string]$Path) { [IO.File]::WriteAllText($Path,($Value|ConvertTo-Json -Depth 100),[Text.UTF8Encoding]::new($false)) }
function Assert-HelmKubernetesVersions($Versions) {
    $server=[version]($Versions.serverVersion.gitVersion -replace '^v','' -replace '-.*$','')
    $client=[version]($Versions.clientVersion.gitVersion -replace '^v','' -replace '-.*$','')
    if($server.Major -ne 1 -or $server.Minor -lt 33 -or $server.Minor -gt 37 -or $client.Major -ne 1 -or [math]::Abs($server.Minor-$client.Minor) -gt 1){throw 'Kubernetes version is outside the chart profile or supported kubectl version skew.'}
}
function Read-HelmProfile([string]$EnvironmentName,[switch]$AllowDisabled) {
    $catalog=Get-Content (Join-Path (Get-ProjectRoot) config/helm-targets.json) -Raw|ConvertFrom-Json -AsHashtable
    if($catalog.schemaVersion -ne 1 -or !$catalog.profiles.Contains($EnvironmentName)){throw 'Unknown Helm profile.'}
    $p=$catalog.profiles[$EnvironmentName]
    if($p.enabled -isnot [bool] -or (!$p.enabled -and !$AllowDisabled)){throw 'Helm target disabled: complete platform onboarding and reviewed source enablement.'}
    Assert-HelmProfile $p $EnvironmentName
    return $p
}
function Assert-HelmProfile($P,[string]$EnvironmentName) {
    $target=Get-Content (Join-Path (Get-ProjectRoot) "self-service/targets/aksapps.$EnvironmentName.json") -Raw|ConvertFrom-Json -AsHashtable
    if($P.Keys.Count -ne 9 -or $P.subscriptionId -ine $target.subscriptionId -or $P.clusterResourceId -ine "/subscriptions/$($target.subscriptionId)/resourceGroups/$($target.resourceGroup)/providers/Microsoft.ContainerService/managedClusters/aks-aksapps-$EnvironmentName" -or $P.protectedEnvironment -cne $target.deploymentEnvironment -or $P.serviceConnection -cne $target.serviceConnection){throw 'Helm target must match the registered AKS profile.'}
    if($P.registryResourceId -notmatch ('^/subscriptions/'+[regex]::Escape($P.subscriptionId)+'/resourceGroups/[^/]+/providers/Microsoft.ContainerRegistry/registries/[a-zA-Z0-9]+$')){throw 'Approved same-subscription ACR resource ID required.'}
    foreach($key in @('namespace','release')){if($P[$key] -cnotmatch '^[a-z][a-z0-9-]{2,39}$' -or $P[$key].StartsWith('kube-')){throw 'Invalid Helm namespace/release.'}}
    $schema=Get-Content (Join-Path (Get-ProjectRoot) charts/platform-app/values.schema.json) -Raw
    if(!($P.values|ConvertTo-Json -Depth 20|Test-Json -Schema $schema -ErrorAction SilentlyContinue)){throw 'Invalid Helm values; complete digest-pinned image and application identity.'}
    if($P.values.imageDigest -eq ('sha256:'+('0'*64)) -or [guid]::Parse($P.values.workloadIdentityClientId) -eq [guid]::Empty){throw 'Placeholder image/identity forbidden.'}
    foreach($cidr in $P.values.egressCidrs){$null=Get-AksRange $cidr}
}
function Read-HelmRendered([string]$Text,$Profile,[switch]$Previous) {
    $items=@(foreach($document in ($Text -split '(?m)^---\s*$')){
        $body=($document -split "`n"|Where-Object {$_ -notmatch '^\s*#'}) -join "`n"
        if($body.Trim()){$body|ConvertFrom-Json -AsHashtable}
    })
    $expected=@{Deployment='apps/v1';Service='v1';ServiceAccount='v1';PodDisruptionBudget='policy/v1';NetworkPolicy='networking.k8s.io/v1'}
    if($items.Count -ne 5 -or @($items.kind|Sort-Object -Unique).Count -ne 5){throw 'Chart must contain exactly five reviewed namespaced objects.'}
    foreach($item in $items){
        $name=if($item.kind -ceq 'ServiceAccount'){$Profile.values.serviceAccountName}else{$Profile.release}
        if(!$expected.Contains($item.kind) -or $item.apiVersion -cne $expected[$item.kind] -or $item.metadata.name -cne $name -or ($item.metadata.Contains('namespace') -and $item.metadata.namespace -cne $Profile.namespace) -or ($item.metadata.Contains('annotations') -and $item.metadata.annotations.Contains('helm.sh/hook'))){throw 'Unexpected Kubernetes object, namespace or hook.'}
    }
    $deployment=($items|Where-Object kind -CEQ Deployment).spec
    $pod=$deployment.template.spec;$container=$pod.containers[0]
    if($pod.containers.Count -ne 1 -or $deployment.replicas -lt 3 -or $pod.securityContext.runAsNonRoot -ne $true -or $pod.securityContext.seccompProfile.type -cne 'RuntimeDefault' -or $container.securityContext.readOnlyRootFilesystem -ne $true -or $container.securityContext.allowPrivilegeEscalation -ne $false -or $container.securityContext.capabilities.drop[0] -cne 'ALL' -or (!$Previous -and $container.image -cne "$($Profile.values.imageRepository)@$($Profile.values.imageDigest)") -or $pod.serviceAccountName -cne $Profile.values.serviceAccountName){throw 'Rendered application security/image contract failed.'}
    if(($items|Where-Object kind -CEQ Service).spec.type -cne 'ClusterIP'){throw 'Public Kubernetes service forbidden.'}
    return $items
}
function Get-HelmBefore($Profile,$Items) {
    $namespace=(Invoke-HelmTool kubectl @('get','namespace',$Profile.namespace,'-o','json')).text|ConvertFrom-Json -AsHashtable
    if($namespace.metadata.labels['pod-security.kubernetes.io/enforce'] -cne 'restricted'){throw 'Platform must provision a restricted namespace first.'}
    $releases=@((Invoke-HelmTool helm @('list','--namespace',$Profile.namespace,'--all','--filter',"^$($Profile.release)$",'-o','json')).text|ConvertFrom-Json -AsHashtable)
    if(@($releases).Count -gt 1 -or (@($releases).Count -and $releases[0].status -cne 'deployed')){throw 'Release is failed/pending/unknown; reconcile manually before a new Preview.'}
    $objects=@()
    foreach($item in $Items){
        $result=Invoke-HelmTool kubectl @('get',$item.kind,$item.metadata.name,'--namespace',$Profile.namespace,'--ignore-not-found=true','-o','json')
        if(!$result.text.Trim()){$objects+=@{kind=$item.kind;name=$item.metadata.name;exists=$false};continue}
        $r=$result.text|ConvertFrom-Json -AsHashtable
        if(!@($releases).Count -or $r.metadata.annotations['meta.helm.sh/release-name'] -cne $Profile.release -or $r.metadata.annotations['meta.helm.sh/release-namespace'] -cne $Profile.namespace -or $r.metadata.labels['app.kubernetes.io/managed-by'] -cne 'Helm'){throw 'Existing object is not owned by this Helm release; adoption forbidden.'}
        # Status/controller timestamps are volatile. UID, actual spec, labels and annotations are material.
        $annotations=@{};foreach($k in $r.metadata.annotations.Keys|Where-Object {$_ -cne 'deployment.kubernetes.io/revision'}){$annotations[$k]=$r.metadata.annotations[$k]}
        $objects+=@{kind=$r.kind;name=$r.metadata.name;exists=$true;uid=$r.metadata.uid;spec=$(if($r.Contains('spec')){$r.spec}else{@{automountServiceAccountToken=$r.automountServiceAccountToken}});labels=$r.metadata.labels;annotations=$annotations}
    }
    if(@($releases).Count){
        # Reject upgrades of an unrelated/older chart with hidden resources that Helm could delete.
        $old=(Invoke-HelmTool helm @('get','manifest',$Profile.release,'-n',$Profile.namespace)).text
        $null=Read-HelmRendered $old $Profile -Previous
    }
    return @{namespaceUid=$namespace.metadata.uid;namespaceLabels=$namespace.metadata.labels;releases=@($releases);objects=$objects}
}
function Assert-HelmChart([string]$Package,[string]$SourceDirectory) {
    $expected=@{};foreach($file in Get-ChildItem $SourceDirectory -File -Recurse){$relative=[IO.Path]::GetRelativePath($SourceDirectory,$file.FullName).Replace('\','/');$expected['platform-app/'+$relative]=(Get-FileHash $file.FullName -Algorithm SHA256).Hash}
    $seen=@{};$stream=[IO.File]::OpenRead($Package);$gzip=[IO.Compression.GZipStream]::new($stream,[IO.Compression.CompressionMode]::Decompress);$reader=[System.Formats.Tar.TarReader]::new($gzip)
    try{while($entry=$reader.GetNextEntry()){
        if($entry.EntryType -eq [System.Formats.Tar.TarEntryType]::Directory){continue}
        if($entry.EntryType -notin @([System.Formats.Tar.TarEntryType]::RegularFile,[System.Formats.Tar.TarEntryType]::V7RegularFile) -or !$expected.Contains($entry.Name) -or $seen.Contains($entry.Name) -or $entry.Length -gt 1048576){throw 'Unexpected chart archive member.'}
        $hash=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($entry.DataStream));if($entry.Name -cne 'platform-app/Chart.yaml' -and $hash -cne $expected[$entry.Name]){throw 'Chart package differs from the reviewed source commit.'};$seen[$entry.Name]=$true
    };if($seen.Count -ne $expected.Count){throw 'Incomplete chart archive.'}
    if((Invoke-HelmTool helm @('show','chart',$Package)).text -cne (Invoke-HelmTool helm @('show','chart',$SourceDirectory)).text){throw 'Chart metadata differs from reviewed source.'}}finally{$reader.Dispose();$gzip.Dispose();$stream.Dispose()}
}
function Assert-HelmPlan($Plan,$Profile,[string]$Commit,[string]$RunId,[string]$Directory) {
    if($Plan.schemaVersion -ne 1 -or $Plan.sourceCommit -cne $Commit -or $Plan.runId -cne $RunId -or $Plan.profileHash -cne (Get-HelmHash (ConvertTo-HelmCanonical $Profile)) -or [DateTimeOffset]::Parse($Plan.createdUtc) -lt [DateTimeOffset]::UtcNow.AddHours(-2) -or [DateTimeOffset]::Parse($Plan.createdUtc) -gt [DateTimeOffset]::UtcNow.AddMinutes(2)){throw 'Stale or cross-target/source/run Helm Preview.'}
    if($Plan.files.Count -ne 6){throw 'Unexpected Helm artifact file set.'}
    foreach($name in @('platform-app-0.1.0.tgz','values.json','rendered.yaml','before.json','changes.diff','README.md')){if(!$Plan.files.Contains($name) -or (Get-FileHash (Join-Path $Directory $name) -Algorithm SHA256).Hash.ToLowerInvariant() -cne $Plan.files[$name]){throw 'Helm artifact integrity failure.'}}
}
