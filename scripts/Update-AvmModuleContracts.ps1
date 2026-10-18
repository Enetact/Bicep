#requires -Version 7.4
[CmdletBinding()]
param([switch]$Check)
. "$PSScriptRoot/common.ps1"
$root=Get-ProjectRoot
& "$PSScriptRoot/Verify-AvmSources.ps1"
$vendor='vendor/azure-verified-modules'
$bundle=Get-Content -LiteralPath (Join-Path $root "$vendor/bundle.json") -Raw|ConvertFrom-Json -AsHashtable
$output=Join-Path $root 'artifacts/module-audit/avm'
New-Item -ItemType Directory -Path $output -Force|Out-Null
$expected=@(
    'identity/user-assigned-identity', 'containers/container-registry', 'configuration/app-configuration',
    'monitoring/application-insights', 'network/network-security-group', 'network/route-table'
)
$actual=@(Get-ChildItem (Join-Path $root 'modules/avm') -Recurse -File -Filter '*.bicep'|ForEach-Object {[IO.Path]::GetRelativePath((Join-Path $root 'modules/avm'),$_.FullName).Replace('\','/')})
if(@(Compare-Object ($expected|ForEach-Object {"$_/main.bicep"}|Sort-Object) ($actual|Sort-Object)).Count){throw 'Review the exact six AVM wrapper paths before cataloging new sources.'}
function Get-AvmFileHash([string]$Relative){
    $file=Join-Path $root $Relative
    $walk=$root
    foreach($part in $Relative.Split('/')){
        if($part -in @('','.','..') -or $part.Contains('\')){throw 'Invalid dependency path.'}
        $walk=Join-Path $walk $part
        if((Get-Item -LiteralPath $walk -Force).Attributes -band [IO.FileAttributes]::ReparsePoint){throw 'Linked AVM dependency is not allowed.'}
    }
    (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Expand-AvmSchema([Collections.IDictionary]$Value,[Collections.IDictionary]$Definitions,[string[]]$Trail=@(),[int]$Depth=0){
    if($Depth -gt 12){throw 'AVM wrapper schema exceeds the supported nesting bound.'}
    $result=[ordered]@{}
    if($Value.Contains('$ref')){
        $reference=[string]$Value['$ref']
        if($reference -notmatch '^#/definitions/([A-Za-z0-9_.-]+)$' -or !$Definitions.Contains($Matches[1]) -or $reference -in $Trail){throw 'Unresolved or cyclic AVM wrapper type.'}
        $expanded=Expand-AvmSchema $Definitions[$Matches[1]] $Definitions (@($Trail)+$reference) ($Depth+1)
        foreach($key in $expanded.Keys){$result[$key]=$expanded[$key]}
    }
    foreach($key in $Value.Keys){
        if($key -eq '$ref'){continue}
        $item=$Value[$key]
        if($key -eq 'properties'){
            $properties=[ordered]@{}
            foreach($name in $item.Keys|Sort-Object){$properties[$name]=Expand-AvmSchema $item[$name] $Definitions $Trail ($Depth+1)}
            $result[$key]=$properties
        }elseif($key -in @('items','additionalProperties') -and $item -is [Collections.IDictionary]){
            $result[$key]=Expand-AvmSchema $item $Definitions $Trail ($Depth+1)
        }else{$result[$key]=$item}
    }
    return $result
}
function Get-AvmResourceTypes([Collections.IDictionary]$Template){
    if(!$Template.Contains('resources')){return}
    $resources=if($Template.resources -is [Collections.IDictionary]){@($Template.resources.Values)}else{@($Template.resources)}
    foreach($resource in $resources){
        if($resource.type -eq 'Microsoft.Resources/deployments'){
            if(!$resource.properties.Contains('template') -or $resource.properties.Contains('templateLink')){throw 'Only self-contained nested ARM templates are allowed.'}
            Get-AvmResourceTypes $resource.properties.template
        }else{
            [string]$resource.type
            if($resource.Contains('resources')){Get-AvmResourceTypes @{resources=$resource.resources}}
        }
    }
}
$contracts=@(foreach($relative in $expected|Sort-Object){
    $id=$relative.Replace('/','-')
    $release=@($bundle.modules|Where-Object id -CEQ $id)
    if($release.Count -ne 1){throw "Missing pinned AVM release: $id"}
    $release=$release[0]
    $path="modules/avm/$relative/main.bicep"
    $file=Join-Path $root $path
    $source=[IO.File]::ReadAllText($file).Replace("`r`n","`n")
    $moduleReferences=@([regex]::Matches($source,"(?m)^\s*module\s+[A-Za-z][A-Za-z0-9_]*\s+'([^']+)'\s*="))
    $approvedReference="../../../../$vendor/$id/main.json"
    if($moduleReferences.Count -ne 1 -or $moduleReferences[0].Groups[1].Value -cne $approvedReference -or
        $source -match '(?m)^\s*(import |extension )' -or $source -match '\bload(TextContent|JsonContent|YamlContent|FileAsBase64)\s*\('){throw "Only the pinned local AVM artifact may be referenced: $path"}
    $compiled=Join-Path $output "$id.json"
    Invoke-Bicep -Arguments @('build',$file,'--outfile',$compiled)
    $arm=Get-Content -LiteralPath $compiled -Raw|ConvertFrom-Json -AsHashtable
    $definitions=if($arm.Contains('definitions')){$arm.definitions}else{@{}}
    $parameters=[ordered]@{}
    foreach($key in $arm.parameters.Keys|Sort-Object){
        $definition=Expand-AvmSchema $arm.parameters[$key] $definitions
        if($definition.type -notin @('string','int','bool','array','object')){throw 'Secure or unsupported wrapper inputs require a separate adapter.'}
        $parameters[$key]=$definition
    }
    $outputs=[ordered]@{}
    foreach($key in $arm.outputs.Keys|Sort-Object){$outputs[$key]=@{type=$arm.outputs[$key].type}}
    $sources=[ordered]@{}
    foreach($dependency in @($path,"$vendor/$($release.templatePath)","$vendor/$($release.manifestPath)","$vendor/LICENSE","$vendor/bundle.json")|Sort-Object){$sources[$dependency]=Get-AvmFileHash $dependency}
    [ordered]@{
        id="avm-$id";path=$path
        sourceSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($source))).ToLowerInvariant()
        draftSelectable=$true;parameters=$parameters;outputs=$outputs
        resourceTypes=@(Get-AvmResourceTypes $arm|Sort-Object -Unique)
        definitions=$definitions;sources=$sources
        origin=[ordered]@{kind='AzureVerifiedModule';version=$release.version;registry=$release.registry;sourceCommit=$release.sourceCommit;sourceUrl=$release.sourceUrl;manifestDigest=$release.manifestDigest;templateSha256=$release.templateSha256}
    }
})
$json=ConvertTo-Json -InputObject ([ordered]@{schemaVersion=1;purpose='Pinned AVM source draft contracts, not deployment authorization';modules=$contracts}) -Depth 100
$destination=Join-Path $root 'config/avm-module-contracts.json'
if($Check){
    if(!(Test-Path -LiteralPath $destination) -or ([IO.File]::ReadAllText($destination).Replace("`r`n","`n").Trim() -cne $json.Replace("`r`n","`n").Trim())){throw 'AVM contracts differ from reviewed source/dependencies. Regenerate and review the separate AVM catalog.'}
    Write-Host "PASS: $($contracts.Count) pinned AVM contracts and dependency closures match source. No Azure calls."
}else{
    [IO.File]::WriteAllText($destination,$json+"`n",[Text.UTF8Encoding]::new($false))
    Write-Host "Updated $($contracts.Count) separate AVM module contracts. Legacy catalog unchanged."
}
