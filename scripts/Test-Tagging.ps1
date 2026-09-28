#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
. "$PSScriptRoot/self-service-common.ps1"
$root=Get-ProjectRoot
$directory=Join-Path $root ('artifacts/tagging-tests/'+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $directory -Force|Out-Null
Assert-CustomTags @{'custom.team'='platform';criticality='standard'}
foreach($tags in @(@{Owner='override'},@{managedBy='override'},@{'custom.team'='Bearer fixture'},@{'custom.password'='fixture'})){
    $rejected=$false;try{Assert-CustomTags $tags}catch{$rejected=$true}
    if(!$rejected){throw 'Unsafe custom tags accepted.'}
}
$count=0
foreach($workload in (Get-Content (Join-Path $root config/workloads.json) -Raw|ConvertFrom-Json -AsHashtable).workloads.Keys){
    foreach($entry in @('main','stack')){
        $file=Join-Path $directory "$workload-$entry.json"
        Invoke-Bicep -Arguments @('build',(Join-Path $root "workloads/$workload/$entry.bicep"),'--outfile',$file)
        $template=Get-Content -LiteralPath $file -Raw|ConvertFrom-Json -AsHashtable
        if($template.parameters.customTags.type -ne 'object' -or $template.parameters.customTags.defaultValue.Count -ne 0){throw "customTags interface missing: $workload/$entry"}
        if((Get-Content -LiteralPath $file -Raw) -notmatch "parameters\('customTags'\)"){throw 'Compiled custom-tag propagation missing.'}
        $count++
    }
}
& dotnet build (Join-Path $root src/SelfService.Tagging.Tool) -c Release -p:RestoreLockedMode=true
if($LASTEXITCODE -ne 0){throw 'Tagging tool build failed.'}
@{date=[datetimeoffset]::UtcNow.ToString('O');compiledTemplates=$count;customTagChecks=5;cloudCalls=$false;redeploymentQualified=$false}|ConvertTo-Json|Set-Content (Join-Path $directory results.json)
Write-Host "PASS: $count Bicep compositions/wrappers, 5 custom-tag checks and pipeline tool build. No Azure calls. Evidence: $directory"
