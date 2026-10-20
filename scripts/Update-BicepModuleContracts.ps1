#requires -Version 7.4
[CmdletBinding()]
param([switch]$Check)
. "$PSScriptRoot/common.ps1"
$root=Get-ProjectRoot
$output=Join-Path $root 'artifacts/module-audit'
New-Item -ItemType Directory -Path $output -Force|Out-Null
# Fabric and Managed DevOps Pools use reviewed product adapters, with separate
# service/network ownership gates. They are not generic leaf-module draft inputs.
$contracts=@(foreach($file in Get-ChildItem (Join-Path $root modules) -Filter '*.bicep' -Recurse|Where-Object {$modulePath=[IO.Path]::GetRelativePath((Join-Path $root modules),$_.FullName).Replace('\','/');!$modulePath.StartsWith('avm/') -and !$modulePath.StartsWith('fabric/') -and !$modulePath.StartsWith('devops/managed-devops-pool/')}|Sort-Object FullName){
    $path=[IO.Path]::GetRelativePath($root,$file.FullName).Replace('\','/')
    $id=$path.Replace('modules/','').Replace('/main.bicep','').Replace('.bicep','').Replace('/','-')
    $compiled=Join-Path $output "$id.json"
    Invoke-Bicep -Arguments @('build',$file.FullName,'--outfile',$compiled)
    $arm=Get-Content -LiteralPath $compiled -Raw|ConvertFrom-Json -AsHashtable
    $source=[IO.File]::ReadAllText($file.FullName).Replace("`r`n","`n")
    # V1 emits self-contained leaf modules only. A newly nested module requires adapter review.
    if($source -match '(?m)^\s*(module |import |extension )' -or $source -match '\bload(TextContent|JsonContent|YamlContent|FileAsBase64)\s*\('){throw "Review non-leaf module before cataloging: $path"}
    $parameters=[ordered]@{};foreach($key in $arm.parameters.Keys|Sort-Object){$parameters[$key]=$arm.parameters[$key]}
    $outputs=[ordered]@{};foreach($key in $arm.outputs.Keys|Sort-Object){$outputs[$key]=@{type=$arm.outputs[$key].type}}
    $resources=if($arm.resources -is [Collections.IDictionary]){@($arm.resources.Values)}else{@($arm.resources)}
    [ordered]@{id=$id;path=$path;sourceSha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($source))).ToLowerInvariant();draftSelectable=($id -notin @('network-ipam-reservation','analytics-databricks-workspace','analytics-data-factory','storage-data-lake','compute-private-aks'));parameters=$parameters;outputs=$outputs;resourceTypes=@($resources|ForEach-Object {$_.type}|Sort-Object -Unique)}
})
$json=ConvertTo-Json -InputObject ([ordered]@{schemaVersion=1;purpose='Source draft contracts, not deployment authorization';modules=$contracts}) -Depth 100
$path=Join-Path $root 'config/bicep-module-contracts.json'
if($Check){
    if(!(Test-Path -LiteralPath $path) -or ([IO.File]::ReadAllText($path).Replace("`r`n","`n").Trim() -cne $json.Replace("`r`n","`n").Trim())){throw 'Bicep module contracts changed. Regenerate, review and commit the contract with its source.'}
    Write-Host "PASS: $($contracts.Count) compiled module contracts match source. No Azure calls."
}else{[IO.File]::WriteAllText($path,$json+"`n",[Text.UTF8Encoding]::new($false));Write-Host "Updated $($contracts.Count) module contracts. No Azure calls."}
& "$PSScriptRoot/Update-AvmModuleContracts.ps1" -Check:$Check
