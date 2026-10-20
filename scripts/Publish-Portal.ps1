#requires -Version 7.4
[CmdletBinding()]
param([ValidateSet('win-arm64','win-x64')][string[]]$Runtime=@('win-arm64','win-x64'))
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
& (Join-Path $PSScriptRoot 'Update-McpGuidance.ps1') -Check
& node (Join-Path $PSScriptRoot 'mcp/verify-skill-sources.mjs')
if($LASTEXITCODE -ne 0){throw 'Official skill source verification failed.'}
& (Join-Path $PSScriptRoot 'Update-BicepModuleContracts.ps1') -Check
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
foreach($rid in $Runtime){
    $dest=Join-Path $root "artifacts/portal-packages/$stamp/$rid"
    if(Test-Path -LiteralPath $dest){throw 'Package output already exists; retry with a new timestamp.'}
    & dotnet publish (Join-Path $root 'src/SelfService.Portal/SelfService.Portal.csproj') -c Release -r $rid --self-contained true -p:RestoreLockedMode=true -p:PublishSingleFile=false -o $dest
    if($LASTEXITCODE -ne 0){throw "Publish failed for $rid."}
    # Ship only reviewed catalog, skills, analyzer and documentation; never local configuration or artifacts.
    $snapshot=Join-Path $dest 'repository';New-Item -ItemType Directory -Path $snapshot -Force|Out-Null
    foreach($folder in @('config','self-service','docs','.agents','vendor','modules')){Copy-Item -LiteralPath (Join-Path $root $folder) -Destination $snapshot -Recurse}
    Copy-Item -LiteralPath (Join-Path $root 'bicepconfig.json') -Destination $snapshot
    New-Item -ItemType Directory -Path (Join-Path $snapshot 'scripts') -Force|Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'scripts/analysis') -Destination (Join-Path $snapshot 'scripts') -Recurse
    Copy-Item -LiteralPath (Join-Path $root 'scripts/naming') -Destination (Join-Path $snapshot 'scripts') -Recurse
    $mcpScripts=Join-Path $snapshot 'scripts/mcp';New-Item -ItemType Directory -Path $mcpScripts -Force|Out-Null
    foreach($file in @('package.json','package-lock.json','update-guidance.mjs','verify-skill-sources.mjs')){Copy-Item -LiteralPath (Join-Path $root "scripts/mcp/$file") -Destination $mcpScripts}
    Copy-Item -LiteralPath (Join-Path $root 'scripts/Setup-McpProviders.ps1') -Destination (Join-Path $snapshot 'scripts')
    Copy-Item -LiteralPath (Join-Path $root 'scripts/Update-McpGuidance.ps1') -Destination (Join-Path $snapshot 'scripts')
    foreach($file in @('common.ps1','Setup-AvmTooling.ps1','Update-BicepModuleContracts.ps1','Update-AvmModuleContracts.ps1','Verify-AvmSources.ps1')){Copy-Item -LiteralPath (Join-Path $root "scripts/$file") -Destination (Join-Path $snapshot 'scripts')}
    # Ship source launchers and the pinned public settings; credentials, CLI downloads and telemetry stay local.
    foreach($file in @('PlatformTrace.ps1','Observability.Common.ps1','Start-Observability.ps1','Stop-Observability.ps1','Get-Observability.ps1','Use-Observability.ps1')){Copy-Item -LiteralPath (Join-Path $root "scripts/$file") -Destination (Join-Path $snapshot 'scripts')}
    # Read-only Fabric readiness uses the same validators and reviewed bindings as the pipeline.
    foreach($file in @('Get-FabricReadiness.ps1','fabric-readiness-common.ps1','fabric-workspace-common.ps1','fabric-execution-common.ps1','managed-devops-pool-common.ps1')){Copy-Item -LiteralPath (Join-Path $root "scripts/$file") -Destination (Join-Path $snapshot 'scripts')}
    $fabricBindings=Join-Path $snapshot 'pipelines';New-Item -ItemType Directory -Path $fabricBindings -Force|Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'pipelines/fabric-execution-bindings.yml') -Destination $fabricBindings
    $poolParameters=Join-Path $snapshot 'workloads/managed-devops-pool/environments';New-Item -ItemType Directory -Path $poolParameters -Force|Out-Null
    foreach($environmentName in @('dev','qa','uat','prod')){Copy-Item -LiteralPath (Join-Path $root "workloads/managed-devops-pool/environments/main.$environmentName.bicepparam") -Destination $poolParameters}
    Copy-Item -LiteralPath (Join-Path $root 'AGENTS.md') -Destination $snapshot
    Copy-Item -LiteralPath (Join-Path $root 'scripts/Install-GitHubCli.ps1') -Destination (Join-Path $snapshot 'scripts')
    Get-ChildItem $root -Filter 'azure-pipelines*.yml'|Copy-Item -Destination $snapshot
    Copy-Item -LiteralPath (Join-Path $root 'docs/local-portal.md') -Destination (Join-Path $dest 'README.md')
    Copy-Item -LiteralPath (Join-Path $root 'src/SelfService.Portal/THIRD-PARTY-NOTICES.md') -Destination $dest
    # portal.local.json must never be copied by publish defaults.
    if(Test-Path -LiteralPath (Join-Path $dest 'portal.local.json')){throw 'Unexpected local configuration in package.'}
    Compress-Archive -Path (Join-Path $dest '*') -DestinationPath "$dest.zip"
    Get-FileHash -LiteralPath "$dest.zip" -Algorithm SHA256|Format-List
    Write-Host "Created $dest.zip"
}
