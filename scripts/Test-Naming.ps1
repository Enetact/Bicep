#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
& node --test (Join-Path $root 'tests/naming/core.test.mjs') (Join-Path $root 'tests/naming/instances.test.mjs') (Join-Path $root 'tests/naming/workload.test.mjs') (Join-Path $root 'tests/naming/discovery-drafts.test.mjs')
if($LASTEXITCODE -ne 0){throw 'Naming core/CLI tests failed.'}
. (Join-Path $PSScriptRoot 'naming-common.ps1')
$request=Get-Content (Join-Path $root 'tests/naming/request.example.json') -Raw|ConvertFrom-Json -AsHashtable
$plan=Resolve-ResourceNames $request
if(@($plan.resources|Where-Object componentKey -eq 'host')[0].name -cne 'devstpayinteus2hcsr5ju'){throw 'PowerShell entry point differs from golden name.'}
if(!(Test-Json -Json ($request|ConvertTo-Json -Depth 30) -SchemaFile (Join-Path $root 'schemas/naming/intent.schema.json'))){throw 'Intent schema validation failed.'}
if(!(Test-Json -Json ($plan|ConvertTo-Json -Depth 30) -SchemaFile (Join-Path $root 'schemas/naming/plan.schema.json'))){throw 'Plan schema validation failed.'}
$bad=$request|ConvertTo-Json -Depth 30|ConvertFrom-Json -AsHashtable
$bad.intent.stackAcronym='INVALID'
$rejected=$false
try{$null=Resolve-ResourceNames $bad}catch{if($_.Exception.Message -notmatch 'stackAcronym: invalid'){throw};$rejected=$true}
if(!$rejected){throw 'PowerShell adapter accepted invalid intent.'}
$emptyRegistry=Get-Content (Join-Path $root 'self-service/instances/registry.json') -Raw|ConvertFrom-Json -AsHashtable
$registry=$emptyRegistry|ConvertTo-Json -Depth 30|ConvertFrom-Json -AsHashtable
$first=@{workloadType='private-storage';state='draft';intent=$request.intent}
$second=$first|ConvertTo-Json -Depth 30|ConvertFrom-Json -AsHashtable
$second.intent.instanceId='33333333-3333-4333-8333-333333333333';$second.intent.instanceOrdinal='02'
$registry.instances=@($first,$second)
if(!(Test-Json -Json ($registry|ConvertTo-Json -Depth 30) -SchemaFile (Join-Path $root 'schemas/naming/instances.schema.json'))){throw 'Instance registry schema failed.'}
$instanceReceipt=Test-StackInstanceRegistry -Registry $registry -PreviousRegistry $emptyRegistry
if(!$instanceReceipt.transitionChecked -or $instanceReceipt.instances.Count -ne 2 -or $instanceReceipt.deploymentAuthorized){throw 'Instance adapter failed checked draft transition.'}
if($instanceReceipt.instances[1].identityPlan.resources[0].name -cne 'dev-stack-pay-int-eus2-02'){throw 'Instance adapter differs from golden stack name.'}
$changed=$registry|ConvertTo-Json -Depth 30|ConvertFrom-Json -AsHashtable
$changed.instances[0].intent.namespaceSalt='changed'
$rejected=$false
try{$null=Test-StackInstanceRegistry -Registry $changed -PreviousRegistry $registry}catch{if($_.Exception.Message -notmatch 'Immutable instance identity'){throw};$rejected=$true}
if(!$rejected){throw 'Instance adapter accepted identity mutation.'}
$inventory=& node (Join-Path $PSScriptRoot 'naming/inventory.mjs')
if($LASTEXITCODE -ne 0){throw 'Naming source inventory failed.'}
$output=Join-Path $root 'artifacts/naming-tests'
New-Item -ItemType Directory -Force $output|Out-Null
$plan|ConvertTo-Json -Depth 30|Set-Content (Join-Path $output 'sample-plan.json')
$inventory|Set-Content (Join-Path $output 'source-inventory.json')
$instanceReceipt|ConvertTo-Json -Depth 30|Set-Content (Join-Path $output 'instance-receipt.json')
@{schema='platform.naming-validation/v1';createdUtc=[DateTimeOffset]::UtcNow.ToString('O');coreAndCli='Passed';instanceCoreAndCli='Passed';powerShellAdapter='Passed';instanceAdapter='Passed';schemaChecks=3;goldenName='devstpayinteus2hcsr5ju';invalidInputRejected=$true;identityMutationRejected=$true;azureCalls=$false;appRestarted=$false}|ConvertTo-Json|Set-Content (Join-Path $output 'receipt.json')
Write-Host 'PASS: Naming and instance adapters, golden vectors, invalid input/identity rejection and three schema checks. No Azure calls or portal restart.'
