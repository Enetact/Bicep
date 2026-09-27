#requires -Version 7.4
[CmdletBinding()]
param([int]$DiscoveryRunId=0)
$ErrorActionPreference='Stop'
if($env:BUILD_SOURCEBRANCH -cne 'refs/heads/main' -or $env:BUILD_REPOSITORY_NAME -cne 'Enetact/Bicep'){throw 'Tags require the registered GitHub main source.'}
if(!$env:SYSTEM_ACCESSTOKEN -or $env:SYSTEM_COLLECTIONURI -notmatch '^https://(dev\.azure\.com/enetactgames/|enetactgames\.visualstudio\.com/)$' -or $env:SYSTEM_TEAMPROJECTID -notmatch '^[0-9a-fA-F-]{36}$'){throw 'Missing scoped ADO context.'}
$base=$env:SYSTEM_COLLECTIONURI.TrimEnd('/')
$headers=@{Authorization="Bearer $env:SYSTEM_ACCESSTOKEN"}
$project=Invoke-RestMethod -Uri "$base/_apis/projects/$($env:SYSTEM_TEAMPROJECTID)?api-version=7.1" -Headers $headers
if($project.visibility -cne 'private'){throw 'Tag evidence is restricted to a verified private ADO project.'}
if($DiscoveryRunId -gt 0){
    $api="$base/$($env:SYSTEM_TEAMPROJECTID)/_apis"
    $run=Invoke-RestMethod -Uri "$api/build/builds/${DiscoveryRunId}?api-version=7.1" -Headers $headers
    $definition=Invoke-RestMethod -Uri "$api/build/definitions/$($run.definition.id)?api-version=7.1" -Headers $headers
    if($run.result -cne 'succeeded' -or $run.sourceBranch -cne 'refs/heads/main' -or $run.sourceVersion -cne $env:BUILD_SOURCEVERSION -or $run.repository.id -cne 'Enetact/Bicep' -or $definition.name -cne 'Discover - Tags' -or $definition.process.yamlFilename.TrimStart('/') -cne 'azure-pipelines-tags-discover.yml'){throw 'Discovery producer/source binding failed. Run a fresh Tags discovery on this main commit.'}
    Write-Host "##vso[task.setvariable variable=TagDiscoveryDefinitionId]$([int]$run.definition.id)"
}
Write-Host 'Private project and source context verified. No credentials or tag values printed.'
