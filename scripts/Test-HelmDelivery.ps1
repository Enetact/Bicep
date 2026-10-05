#requires -Version 7.4
param([string]$HelmDirectory='')
if($HelmDirectory){$env:PATH=[IO.Path]::GetFullPath($HelmDirectory)+[IO.Path]::PathSeparator+$env:PATH}
. "$PSScriptRoot/helm-common.ps1"
$root=Get-ProjectRoot;$dir=Join-Path $root ('artifacts/helm-tests/'+[guid]::NewGuid().ToString('N'));New-Item -ItemType Directory $dir -Force|Out-Null
$profile=(Get-Content (Join-Path $root config/helm-targets.json) -Raw|ConvertFrom-Json -AsHashtable).profiles.dev
$profile.registryResourceId="/subscriptions/$($profile.subscriptionId)/resourceGroups/shared/providers/Microsoft.ContainerRegistry/registries/fixtureacr"
$profile.values.imageRepository='fixtureacr.azurecr.io/platform/app';$profile.values.imageDigest='sha256:'+('a'*64);$profile.values.workloadIdentityClientId='11111111-1111-1111-1111-111111111111'
$count=0
function Check([bool]$Value){if(!$Value){throw 'Helm assertion failed.'};$script:count++}
function Reject([scriptblock]$Body){$failed=$false;try{& $Body|Out-Null}catch{$failed=$true};Check $failed}
function Clone($Value){$Value|ConvertTo-Json -Depth 100|ConvertFrom-Json -AsHashtable}
Reject {Read-HelmProfile dev};Assert-HelmProfile $profile dev
Assert-HelmKubernetesVersions @{serverVersion=@{gitVersion='v1.35.3'};clientVersion=@{gitVersion='v1.36.0'}}
Reject {Assert-HelmKubernetesVersions @{serverVersion=@{gitVersion='v1.35.3'};clientVersion=@{gitVersion='v1.32.0'}}}
Reject {Assert-HelmKubernetesVersions @{serverVersion=@{gitVersion='v1.38.0'};clientVersion=@{gitVersion='v1.38.0'}}}
foreach($key in @('namespace','release')){$bad=Clone $profile;$bad[$key]='kube-system';Reject {Assert-HelmProfile $bad dev}}
$bad=Clone $profile;$bad.values.imageDigest='latest';Reject {Assert-HelmProfile $bad dev}
$bad=Clone $profile;$bad.values.egressCidrs=@('0.0.0.0/0');Reject {Assert-HelmProfile $bad dev}
$chart=Join-Path $root charts/platform-app;$values=Join-Path $dir values.json;Write-HelmJson $profile.values $values
$null=Invoke-HelmTool helm @('lint',$chart,'--strict','-f',$values)
$null=Invoke-HelmTool helm @('package',$chart,'-d',$dir)
$package=Join-Path $dir platform-app-0.1.0.tgz;Assert-HelmChart $package $chart
$rendered=(Invoke-HelmTool helm @('template',$profile.release,$package,'-n',$profile.namespace,'-f',$values)).text
$items=Read-HelmRendered $rendered $profile;Check ($items.Count -eq 5)
$originalTool=${function:Invoke-HelmTool}
try {
    $scenario='install'
    function Invoke-HelmTool([string]$Tool,[string[]]$Arguments,[int[]]$AllowedCodes=@(0)) {
        if($scenario -eq 'denied'){throw 'Fixture authorization failure'}
        if($Tool -eq 'helm' -and $Arguments[0] -eq 'list'){
            $releases=@();if($scenario -ne 'install'){$releases=@(@{name=$profile.release;status=$(if($scenario -eq 'pending'){'pending-upgrade'}else{'deployed'});revision=1})}
            return @{text=(ConvertTo-Json -InputObject $releases -Compress);code=0}
        }
        if($Tool -eq 'helm'){return @{text=$rendered;code=0}}
        if($Arguments[1] -eq 'namespace'){return @{text=(@{metadata=@{uid='namespace-uid';labels=@{'pod-security.kubernetes.io/enforce'=$(if($scenario -eq 'unrestricted'){'baseline'}else{'restricted'})}}}|ConvertTo-Json -Depth 10);code=0}}
        if($scenario -eq 'install'){return @{text='';code=0}}
        $item=Clone ($items|Where-Object kind -CEQ $Arguments[1]);$item.metadata.uid='object-uid'
        $item.metadata.labels=@{'app.kubernetes.io/managed-by'='Helm'}
        $item.metadata.annotations=@{'meta.helm.sh/release-name'=$(if($scenario -eq 'foreign'){'other'}else{$profile.release});'meta.helm.sh/release-namespace'=$profile.namespace}
        return @{text=($item|ConvertTo-Json -Depth 100);code=0}
    }
    Check ((Get-HelmBefore $profile $items).objects.Count -eq 5)
    $scenario='upgrade';$state=Get-HelmBefore $profile $items;Check ($state.objects[0].exists)
    foreach($scenario in @('denied','pending','unrestricted','foreign')){Reject {Get-HelmBefore $profile $items}}
}finally{Set-Item Function:Invoke-HelmTool $originalTool}
Reject {Read-HelmRendered ($rendered.Replace('"ClusterIP"','"LoadBalancer"')) $profile}
Reject {Read-HelmRendered ($rendered.Replace('"readOnlyRootFilesystem":true','"readOnlyRootFilesystem":false')) $profile}
Reject {Read-HelmRendered ($rendered+"`n---`n"+'{"apiVersion":"v1","kind":"Secret","metadata":{"name":"bad"}}') $profile}
[IO.File]::WriteAllText((Join-Path $dir rendered.yaml),$rendered,[Text.UTF8Encoding]::new($false))
foreach($name in @('before.json','changes.diff','README.md')){[IO.File]::WriteAllText((Join-Path $dir $name),'fixture')}
$files=@{};foreach($name in @('platform-app-0.1.0.tgz','values.json','rendered.yaml','before.json','changes.diff','README.md')){$files[$name]=(Get-FileHash (Join-Path $dir $name)).Hash.ToLowerInvariant()}
$plan=@{schemaVersion=1;sourceCommit=('a'*40);runId='12';createdUtc=[DateTimeOffset]::UtcNow.ToString('O');profileHash=(Get-HelmHash (ConvertTo-HelmCanonical $profile));files=$files}
Assert-HelmPlan $plan $profile ('a'*40) '12' $dir
foreach($key in @('sourceCommit','runId','profileHash','createdUtc')){$bad=Clone $plan;$bad[$key]=if($key -eq 'createdUtc'){[DateTimeOffset]::UtcNow.AddHours(-3).ToString('O')}else{'wrong'};Reject {Assert-HelmPlan $bad $profile ('a'*40) '12' $dir}}
Add-Content (Join-Path $dir values.json) 'tampered';Reject {Assert-HelmPlan $plan $profile ('a'*40) '12' $dir}
Write-HelmJson @{passed=$count;azureCalls=$false;clusterCalls=$false;helmVersion=(Invoke-HelmTool helm @('version','--short')).text} (Join-Path $dir results.json)
Write-Host "PASS: $count Helm render/contract checks. Evidence: $dir"
