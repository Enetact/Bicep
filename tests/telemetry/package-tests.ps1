#requires -Version 7.4
$ErrorActionPreference='Stop'
$root=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$snapshot=Join-Path $root ('artifacts/telemetry-package-tests/'+[guid]::NewGuid().ToString('N'))
$null=New-Item -ItemType Directory -Path (Join-Path $snapshot 'scripts') -Force
$tokens=$null;$errors=$null
$ast=[Management.Automation.Language.Parser]::ParseFile((Join-Path $root 'scripts/Publish-Portal.ps1'),[ref]$tokens,[ref]$errors)
if($errors.Count){throw 'Publish script contains syntax errors.'}
$copyGroup=$ast.FindAll({param($node) $node -is [Management.Automation.Language.ForEachStatementAst] -and $node.Condition.Extent.Text.Contains("@('PlatformTrace.ps1'")},$true)
if($copyGroup.Count -ne 1){throw 'A single explicit observability package source group is required.'}
# Exercise the real copy allowlist without publishing binaries or invoking external provider setup.
. ([scriptblock]::Create($copyGroup[0].Extent.Text))
$files=@(Get-ChildItem (Join-Path $snapshot 'scripts') -File)
foreach($file in $files){
    if((Get-FileHash -LiteralPath $file.FullName).Hash -cne (Get-FileHash -LiteralPath (Join-Path $root ('scripts/'+$file.Name))).Hash){throw 'Packaged telemetry source differs.'}
    $references=[regex]::Matches((Get-Content -LiteralPath $file.FullName -Raw),'Join-Path \$PSScriptRoot ''([^'']+\.ps1)''')
    foreach($reference in $references){if(!(Test-Path -LiteralPath (Join-Path $file.DirectoryName $reference.Groups[1].Value))){throw 'Packaged telemetry launcher has a missing script dependency.'}}
}
if($files.Count -ne 6 -or (Test-Path -LiteralPath (Join-Path $snapshot '.local'))){throw 'Unexpected telemetry package contents.'}
Write-Host 'PASS: portable telemetry source copy group, hashes, and launcher dependency closure; private state excluded.'
