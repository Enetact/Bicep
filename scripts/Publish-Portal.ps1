#requires -Version 7.4
[CmdletBinding()]
param([ValidateSet('win-arm64','win-x64')][string[]]$Runtime=@('win-arm64','win-x64'))
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$stamp=Get-Date -Format 'yyyyMMdd-HHmmss'
foreach($rid in $Runtime){
    $dest=Join-Path $root "artifacts/portal-packages/$stamp/$rid"
    if(Test-Path -LiteralPath $dest){throw 'Package output already exists; retry with a new timestamp.'}
    & dotnet publish (Join-Path $root 'src/SelfService.Portal/SelfService.Portal.csproj') -c Release -r $rid --self-contained true -p:RestoreLockedMode=true -p:PublishSingleFile=false -o $dest
    if($LASTEXITCODE -ne 0){throw "Publish failed for $rid."}
    # Ship only reviewed catalog, skills, analyzer and documentation; never local configuration or artifacts.
    $snapshot=Join-Path $dest 'repository';New-Item -ItemType Directory -Path $snapshot -Force|Out-Null
    foreach($folder in @('config','self-service','docs','.agents')){Copy-Item -LiteralPath (Join-Path $root $folder) -Destination $snapshot -Recurse}
    New-Item -ItemType Directory -Path (Join-Path $snapshot 'scripts') -Force|Out-Null
    Copy-Item -LiteralPath (Join-Path $root 'scripts/analysis') -Destination (Join-Path $snapshot 'scripts') -Recurse
    Get-ChildItem $root -Filter 'azure-pipelines-*.yml'|Copy-Item -Destination $snapshot
    Copy-Item -LiteralPath (Join-Path $root 'docs/local-portal.md') -Destination (Join-Path $dest 'README.md')
    Copy-Item -LiteralPath (Join-Path $root 'src/SelfService.Portal/THIRD-PARTY-NOTICES.md') -Destination $dest
    # portal.local.json must never be copied by publish defaults.
    if(Test-Path -LiteralPath (Join-Path $dest 'portal.local.json')){throw 'Unexpected local configuration in package.'}
    Compress-Archive -Path (Join-Path $dest '*') -DestinationPath "$dest.zip"
    Get-FileHash -LiteralPath "$dest.zip" -Algorithm SHA256|Format-List
    Write-Host "Created $dest.zip"
}
