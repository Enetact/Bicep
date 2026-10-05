#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$ParametersFile,
    [Parameter(Mandatory)][string]$OutputsFile,
    [Parameter(Mandatory)][guid]$AdfApplicationId,
    [Parameter(Mandatory)][guid]$RunAsApplicationId,
    [Parameter(Mandatory)][ValidatePattern('^[a-zA-Z0-9]{8,64}$')][string]$ComputePolicyId,
    [ValidatePattern('^[a-zA-Z0-9][a-zA-Z0-9_-]{0,63}$')][string]$ExportId=([guid]::NewGuid().ToString('N'))
)
. "$PSScriptRoot/workload-preview-common.ps1"
$root=Get-ProjectRoot
$p=(Get-Content -LiteralPath $ParametersFile -Raw|ConvertFrom-Json -AsHashtable).parameters
if($p.workload.value -cnotmatch '^[a-z][a-z0-9]{2,9}$' -or $p.environmentName.value -cnotin @('dev','qa','uat','prod')){throw 'Invalid registered target selector.'}
$t=Get-Content (Join-Path $root "self-service/targets/$($p.workload.value).$($p.environmentName.value).json") -Raw|ConvertFrom-Json -AsHashtable
if($t.workloadType -cne 'private-data-platform'){throw 'Recipes require a registered data-platform target.'}
Assert-ProductParameters $t $p
if($AdfApplicationId -eq [guid]::Empty -or $RunAsApplicationId -eq [guid]::Empty){throw 'Provide reviewed application/client IDs, not blank or object IDs.'}
$o=Get-Content -LiteralPath $OutputsFile -Raw|ConvertFrom-Json -AsHashtable
if($o.databricksUrl.value -cnotmatch '^adb-[0-9]+\.[0-9]+\.azuredatabricks\.net$' -or $o.storageAccountName.value -cnotmatch '^[a-z0-9]{3,24}$'){throw 'Invalid deployment output host/account.'}
$scope="/subscriptions/$($t.subscriptionId)/resourceGroups/$($t.resourceGroup)/providers"
if($o.storageResourceId.value -ine "$scope/Microsoft.Storage/storageAccounts/$($o.storageAccountName.value)" -or $o.databricksResourceId.value -notmatch ('^'+[regex]::Escape($scope)+'/Microsoft.Databricks/workspaces/[^/]+$') -or $o.factoryResourceId.value -notmatch ('^'+[regex]::Escape($scope)+'/Microsoft.DataFactory/factories/[^/]+$')){throw 'Outputs do not match the registered target scope.'}
$source=Join-Path $root workloads/private-data-platform/databricks
$config=Get-Content (Join-Path $source bundle.template.json) -Raw|ConvertFrom-Json -AsHashtable
$bindings=@{workspace_host="https://$($o.databricksUrl.value)";lake_account=$o.storageAccountName.value;spark_version=$p.sparkVersion.value;node_type=$p.nodeType.value;max_workers=$p.maxWorkers.value;run_as_application_id=$RunAsApplicationId.ToString();adf_application_id=$AdfApplicationId.ToString();compute_policy_id=$ComputePolicyId;instance="$($t.workload)-$($t.environmentName)"}
foreach($key in $bindings.Keys){$config.variables[$key].default=$bindings[$key]}
$config.resources.jobs.reviewed_batch.parameters[1].default="inbox/$($p.inputFileName.value)"
$out=Join-Path $root "artifacts/data-platform/recipes/$ExportId"
if(Test-Path -LiteralPath $out){throw 'Export directory exists; use a new ExportId. Existing files are preserved.'}
New-Item -ItemType Directory -Path $out -ErrorAction Stop|Out-Null
Copy-Item -LiteralPath (Join-Path $source src) -Destination $out -Recurse
Copy-Item -LiteralPath (Join-Path $source sample.csv) -Destination $out
# JSON is valid YAML and avoids executable interpolation of user-controlled settings.
Write-ServiceJson $config (Join-Path $out databricks.yml)
$files=@(Get-ChildItem $out -File -Recurse|ForEach-Object {@{path=[IO.Path]::GetRelativePath($out,$_.FullName).Replace('\','/');sha256=(Get-FileHash $_.FullName -Algorithm SHA256).Hash.ToLowerInvariant()}})
Write-ServiceJson @{schemaVersion=1;kind='data-platform-recipes';target="$($t.workload).$($t.environmentName)";createdUtc=[DateTime]::UtcNow.ToString('o');parametersSha256=(Get-FileHash $ParametersFile -Algorithm SHA256).Hash;outputsSha256=(Get-FileHash $OutputsFile -Algorithm SHA256).Hash;files=$files;cloudCalls=$false;deployed=$false;executed=$false;qualified=$false;note='Local setup export only. Validate live identities, job policy and UC grants before explicit bundle deployment.'} (Join-Path $out receipt.json)
Write-Host "Exported reviewable manual recipes: $out. No cloud calls, deployment or job execution."
