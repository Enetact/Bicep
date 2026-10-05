#requires -Version 7.4
[CmdletBinding()]
param()
# Reuse the domain fixtures; Azure readers are restored before local export checks.
. "$PSScriptRoot/Test-DataPlatform.ps1"
$start=$cases.Count
$testDir=Join-Path $root ("artifacts/data-platform/recipe-tests/"+[guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $testDir -Force|Out-Null
$parameterFile=Join-Path $testDir parameters.json;$outputsFile=Join-Path $testDir outputs.json
Write-ServiceJson @{parameters=$p} $parameterFile
$o.storageAccountName=@{value='stexample'};$o.storageResourceId.value="$owned/Microsoft.Storage/storageAccounts/stexample"
Write-ServiceJson $o $outputsFile
$exportArgs=@{ParametersFile=$parameterFile;OutputsFile=$outputsFile;AdfApplicationId='22222222-2222-2222-2222-222222222222';RunAsApplicationId='33333333-3333-3333-3333-333333333333';ComputePolicyId='abcd1234abcd1234';ExportId=[guid]::NewGuid().ToString('N')}
Case 'export binds deployment connections and preserves manual execution' {
    & "$PSScriptRoot/Export-DataPlatformRecipes.ps1" @exportArgs
    $export=Join-Path $root "artifacts/data-platform/recipes/$($exportArgs.ExportId)"
    $config=Get-Content (Join-Path $export databricks.yml) -Raw|ConvertFrom-Json -AsHashtable
    Check ($config.variables.lake_account.default -ceq 'stexample')
    Check ($config.variables.workspace_host.default -ceq "https://$($o.databricksUrl.value)")
    Check ($config.resources.jobs.reviewed_batch.tasks.Count -eq 3)
    Check (!$config.resources.jobs.reviewed_batch.Contains('schedule'))
    $receipt=Get-Content (Join-Path $export receipt.json) -Raw|ConvertFrom-Json -AsHashtable
    Check (!$receipt.deployed -and !$receipt.executed -and !$receipt.qualified -and !$receipt.cloudCalls)
    foreach($file in $receipt.files){Check ((Get-FileHash (Join-Path $export $file.path) -Algorithm SHA256).Hash -ieq $file.sha256)}
}
Case 'export never overwrites an existing setup' {Reject {& "$PSScriptRoot/Export-DataPlatformRecipes.ps1" @exportArgs}}
Case 'export rejects foreign output scope' {$exportArgs.ExportId=[guid]::NewGuid().ToString('N');$bad=Clone $o;$bad.storageResourceId.value='foreign';Write-ServiceJson $bad $outputsFile;Reject {& "$PSScriptRoot/Export-DataPlatformRecipes.ps1" @exportArgs}}
Case 'export rejects an arbitrary host' {$exportArgs.ExportId=[guid]::NewGuid().ToString('N');$bad=Clone $o;$bad.databricksUrl.value='example.com';Write-ServiceJson $bad $outputsFile;Reject {& "$PSScriptRoot/Export-DataPlatformRecipes.ps1" @exportArgs}}
if(!(Get-Command python -ErrorAction SilentlyContinue)){throw 'Python 3.10+ is required for offline data-recipe tests.'}
& python -B -m unittest discover -s (Join-Path $root tests/data-platform) -p 'test_*.py' -v
if($LASTEXITCODE -ne 0){throw 'Offline recipe data contract tests failed.'}
& node (Join-Path $root tests/infrastructure/data-recipes.mjs)
if($LASTEXITCODE -ne 0){throw 'Manual pipeline recipe contracts failed.'}
Write-ServiceJson @{exportContracts=$cases.Count-$start;pythonTests=10;cloudCalls=$false;nativeBundleValidation=$false;cases=@($cases|Select-Object -Skip $start)} (Join-Path $testDir results.json)
Write-Host "PASS: 4 recipe export contracts plus Python/compiled-template checks. Evidence: $testDir"
