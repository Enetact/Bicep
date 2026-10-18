#requires -Version 7.4
<#
.SYNOPSIS
Verify an application-generated seven-node AVM draft and compile synthetic parameter fixtures offline.
#>
[CmdletBinding(DefaultParameterSetName='Directory')]
param(
    [Parameter(Mandatory,ParameterSetName='Directory')][string]$DraftDirectory,
    [Parameter(Mandatory,ParameterSetName='Archive')][string]$ArchivePath,
    [string]$RepositoryRoot='',
    [string]$OutputDirectory=''
)
$ErrorActionPreference='Stop'
Set-StrictMode -Version Latest
if(!$RepositoryRoot){$directory=[IO.DirectoryInfo]::new($PSScriptRoot);while($directory -and !(Test-Path -LiteralPath (Join-Path $directory.FullName 'config/workloads.json'))){$directory=$directory.Parent};if(!$directory){throw 'Select the source repository.'};$RepositoryRoot=$directory.FullName}
$root=[IO.Path]::GetFullPath($RepositoryRoot)
if(!$OutputDirectory){$OutputDirectory=Join-Path $root ('artifacts/avm-composition-acceptance/'+[guid]::NewGuid().ToString('N'))}
$output=[IO.Path]::GetFullPath($OutputDirectory)
if(!$output.StartsWith((Join-Path $root 'artifacts')+[IO.Path]::DirectorySeparatorChar,[StringComparison]::OrdinalIgnoreCase) -or (Test-Path -LiteralPath $output)){throw 'Use a new output directory inside this checkout artifacts folder.'}
function Assert-Composition([bool]$Condition,[string]$Message){if(!$Condition){throw $Message}}
function Assert-CompositionPath([string]$Path){
    $absolute=[IO.Path]::GetFullPath($Path)
    for($item=$absolute;$item;$item=Split-Path $item -Parent){if(Test-Path -LiteralPath $item){Assert-Composition (!((Get-Item -LiteralPath $item -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) 'Linked draft/source paths are not accepted.'};if($item -eq [IO.Path]::GetPathRoot($item)){break}}
}
function Get-CompositionTextHash([string]$Path){
    $text=[IO.File]::ReadAllText($Path).Replace("`r`n","`n")
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant()
}
function Get-CompositionFiles([string]$Path,[string]$Base){
    Assert-CompositionPath $Path
    foreach($item in Get-ChildItem -LiteralPath $Path -Force){Assert-CompositionPath $item.FullName;if($item.PSIsContainer){Get-CompositionFiles $item.FullName $Base}else{[IO.Path]::GetRelativePath($Base,$item.FullName).Replace('\','/')}}
}
function Get-CompositionTokens([string]$Text){
    # The emitter generates ordinary literals only. Read comments and literals before
    # inspecting declarations so spacing/comment prefixes cannot hide dependencies.
    $tokens=[Collections.Generic.List[object]]::new();$index=0
    while($index -lt $Text.Length){
        Assert-Composition ($tokens.Count -le 50000) 'Source token count exceeds the generated composition profile.'
        $character=$Text[$index]
        if([char]::IsWhiteSpace($character)){$index++;continue}
        if($index+1 -lt $Text.Length -and $Text.Substring($index,2) -ceq '//'){$end=$Text.IndexOf("`n",$index+2);$index=if($end -lt 0){$Text.Length}else{$end+1};continue}
        if($index+1 -lt $Text.Length -and $Text.Substring($index,2) -ceq '/*'){$end=$Text.IndexOf('*/',$index+2,[StringComparison]::Ordinal);Assert-Composition ($end -ge 0) 'Unclosed source comment.';$index=$end+2;continue}
        if($character -ceq "'"){
            Assert-Composition (!($index+2 -lt $Text.Length -and $Text.Substring($index,3) -ceq "'''")) 'Multiline literals are outside the generated source profile.'
            $start=++$index;$closed=$false
            while($index -lt $Text.Length){if($Text[$index] -ceq '\'){$index+=2;continue};if($Text[$index] -ceq "'"){$closed=$true;break};$index++}
            Assert-Composition $closed 'Unclosed source literal.'
            $literal=$Text.Substring($start,$index-$start)
            Assert-Composition (!$literal.Contains('${',[StringComparison]::Ordinal)) 'Interpolated literals are outside the generated source profile.'
            $tokens.Add(@{kind='string';value=$literal});$index++;continue
        }
        if($character -cmatch '[a-zA-Z_]'){$start=$index++;while($index -lt $Text.Length -and $Text[$index] -cmatch '[a-zA-Z0-9_]'){$index++};$tokens.Add(@{kind='identifier';value=$Text.Substring($start,$index-$start)});continue}
        $tokens.Add(@{kind='symbol';value=[string]$character});$index++
    }
    return ,$tokens.ToArray()
}
function Assert-CompositionDependencies([string]$Text,[Collections.IDictionary]$AllowedModules,[bool]$Stack){
    $tokens=Get-CompositionTokens $Text;$modules=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal);$resources=0
    for($index=0;$index -lt $tokens.Count;$index++){
        $token=$tokens[$index];if($token.kind -cne 'identifier'){continue}
        Assert-Composition ($token.value -cnotin @('import','extension','using','test','provider','loadDirectoryFileInfo','loadTextContent','loadJsonContent','loadYamlContent','loadFileAsBase64','readEnvironmentVariable')) 'Source contains a dependency/read outside the offline generated profile.'
        if($token.value -ceq 'module'){
            Assert-Composition ($index+2 -lt $tokens.Count -and $tokens[$index+1].kind -ceq 'identifier' -and $tokens[$index+2].kind -ceq 'string') 'Unrecognized module declaration.'
            $name=$tokens[$index+1].value;$path=$tokens[$index+2].value
            Assert-Composition ($AllowedModules.Contains($name) -and $modules.Add($name) -and $path -ceq $AllowedModules[$name]) 'Source has a module outside the exact reviewed local closure.'
        }
        if($token.value -ceq 'resource'){
            $resources++
            Assert-Composition ($Stack -and $index+2 -lt $tokens.Count -and $tokens[$index+1].value -ceq 'group' -and $tokens[$index+2].kind -ceq 'string' -and $tokens[$index+2].value -ceq 'Microsoft.Resources/resourceGroups@2024-03-01') 'Source has a resource outside the generated group wrapper.'
        }
    }
    Assert-Composition ($modules.Count -eq $AllowedModules.Count -and $resources -eq $(if($Stack){1}else{0})) 'Generated module/resource declarations are incomplete.'
}
Assert-CompositionPath $output
$avm=(Get-Content -LiteralPath (Join-Path $root 'config/avm-module-contracts.json') -Raw|ConvertFrom-Json -AsHashtable).modules
$native=(Get-Content -LiteralPath (Join-Path $root 'config/bicep-module-contracts.json') -Raw|ConvertFrom-Json -AsHashtable).modules
$ids=@('avm-identity-user-assigned-identity','avm-containers-container-registry','avm-configuration-app-configuration','avm-monitoring-application-insights','avm-network-network-security-group','avm-network-route-table','monitoring-log-analytics')
$contracts=@{}
foreach($id in $ids){$selectedContracts=@(@($avm)+@($native)|Where-Object id -CEQ $id);Assert-Composition ($selectedContracts.Count -eq 1) "Missing exact module contract: $id";$contracts[$id]=$selectedContracts[0]}
$fixed=@('main.bicep','stack.bicep','main.bicepparam','stack.bicepparam','resolved-values.json','proposal.json','parameter-provenance.json','bicepconfig.json','README.md','draft-receipt.json')
$expected=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach($name in $fixed){[void]$expected.Add($name)}
foreach($contract in $contracts.Values){if($contract.Contains('sources')){foreach($path in $contract.sources.Keys){[void]$expected.Add($path)}}else{[void]$expected.Add($contract.path)}}
New-Item -ItemType Directory -Path $output|Out-Null
if($PSCmdlet.ParameterSetName -eq 'Archive'){
    Assert-CompositionPath $ArchivePath
    $DraftDirectory=Join-Path $output 'original-archive'
    New-Item -ItemType Directory -Path $DraftDirectory|Out-Null
    $zip=[IO.Compression.ZipFile]::OpenRead([IO.Path]::GetFullPath($ArchivePath))
    try{
        $seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal);$total=0L
        foreach($entry in $zip.Entries){
            $name=$entry.FullName
            Assert-Composition ($expected.Contains($name) -and $seen.Add($name)) 'Archive has an unknown or duplicate entry.'
            Assert-Composition (($entry.ExternalAttributes -shr 16 -band 61440) -ne 40960 -and $entry.Length -le 8MB) 'Archive has a linked or oversized entry.'
            $total+=$entry.Length;Assert-Composition ($total -le 32MB) 'Archive exceeds the offline source limit.'
            $target=Join-Path $DraftDirectory $name
            New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force|Out-Null
            $input=$entry.Open();$destination=[IO.File]::Open($target,[IO.FileMode]::CreateNew)
            try{
                $buffer=[byte[]]::new(65536);$actualLength=0L
                while(($read=$input.Read($buffer,0,$buffer.Length)) -gt 0){$actualLength+=$read;Assert-Composition ($actualLength -le $entry.Length -and $actualLength -le 8MB -and ($total-$entry.Length+$actualLength) -le 32MB) 'Archive decompression exceeds its declared source limits.';$destination.Write($buffer,0,$read)}
                Assert-Composition ($actualLength -eq $entry.Length) 'Archive decompression differs from its declared source size.'
            }finally{$destination.Dispose();$input.Dispose()}
        }
    }finally{$zip.Dispose()}
}
$original=[IO.Path]::GetFullPath($DraftDirectory)
Assert-CompositionPath $original
$actual=@(Get-CompositionFiles $original $original)
Assert-Composition ($actual.Count -eq $expected.Count -and @($actual|Where-Object {!$expected.Contains($_)}).Count -eq 0) 'Draft file inventory differs from the exact reviewed dependency closure.'
$sourceLength=0L;foreach($name in $actual){$length=(Get-Item -LiteralPath (Join-Path $original $name)).Length;$sourceLength+=$length;Assert-Composition ($length -le 8MB -and $sourceLength -le 32MB) 'Draft source exceeds the offline source limits.'}
$originalHashes=@{};foreach($name in $actual){$originalHashes[$name]=(Get-FileHash -LiteralPath (Join-Path $original $name) -Algorithm SHA256).Hash}
$proposal=Get-Content -LiteralPath (Join-Path $original 'proposal.json') -Raw|ConvertFrom-Json -AsHashtable
$receipt=Get-Content -LiteralPath (Join-Path $original 'draft-receipt.json') -Raw|ConvertFrom-Json -AsHashtable
$provenance=Get-Content -LiteralPath (Join-Path $original 'parameter-provenance.json') -Raw|ConvertFrom-Json -AsHashtable
Assert-Composition ($proposal.schema -ceq 'platform.bicep-proposal/v1' -and $proposal.modules.Count -eq 7) 'Expected a seven-node application-generated composition proposal.'
Assert-Composition (@(Compare-Object ($ids|Sort-Object) ($proposal.modules.moduleId|Sort-Object) -CaseSensitive).Count -eq 0) 'Expected all six exact AVM modules plus the dedicated Log Analytics module.'
Assert-Composition ($receipt.schema -ceq 'platform.bicep-draft/v1' -and $receipt.status -ceq 'UnqualifiedSourceDraft' -and $receipt.deploymentAuthorized -eq $false -and $receipt.compilation -ceq 'Required') 'Draft status/authorization boundary differs.'
Assert-Composition ($receipt.evidenceDigest -ceq $proposal.evidenceDigest) 'Proposal and draft receipt evidence bindings differ.'
Assert-Composition ($receipt.modules.Count -eq 7) 'Draft receipt module inventory differs.'
Assert-Composition ($receipt.files.Count -eq ($expected.Count-1)) 'Draft file receipt is incomplete.'
foreach($path in $actual|Where-Object {$_ -ne 'draft-receipt.json'}){Assert-Composition ($receipt.files.Contains($path) -and (Get-CompositionTextHash (Join-Path $original $path)) -ceq $receipt.files[$path]) "Source receipt differs: $path"}
$children=@{
    'avm-identity-user-assigned-identity'=@('federatedIdentityCredentials')
    'avm-containers-container-registry'=@('replications')
    'avm-configuration-app-configuration'=@('keyValues','replicaLocations')
    'avm-network-network-security-group'=@('securityRules')
    'avm-network-route-table'=@('routes','applianceRoutes')
}
$nodeIds=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$childCount=0
foreach($node in $proposal.modules){
    Assert-Composition ($node.id -cmatch '^[a-z][a-zA-Z0-9]{0,31}$' -and $nodeIds.Add($node.id)) 'Invalid or duplicate node identifier.'
    $contract=$contracts[$node.moduleId]
    $record=@($receipt.modules|Where-Object id -CEQ $node.id)
    Assert-Composition ($record.Count -eq 1 -and $record[0].moduleId -ceq $node.moduleId -and $record[0].sourceSha256 -ceq $contract.sourceSha256) 'Module receipt differs from the selected contract.'
    foreach($input in @('name','tags')){Assert-Composition ($node.bindings.Contains($input) -and $node.bindings[$input].kind -ceq 'Input' -and $receipt.requiredInputs -ccontains "p_$($node.id)_$input") 'Names and ownership tags must stay explicit required inputs.'}
    Assert-Composition ($node.bindings.location.kind -ceq 'Setting' -and $node.bindings.location.reference -ceq 'location') 'Location must come from the approved context setting.'
    if($children.Contains($node.moduleId)){
        foreach($input in $children[$node.moduleId]){
            $schema=$contract.parameters[$input]
            Assert-Composition ($node.bindings.Contains($input) -and $node.bindings[$input].kind -ceq 'Input' -and $receipt.requiredInputs -ccontains "p_$($node.id)_$input") "Missing explicit typed child input: $input"
            Assert-Composition ($schema.type -ceq 'array' -and $schema.items.type -ceq 'object' -and $schema.items.additionalProperties -eq $false -and $schema.defaultValue.Count -eq 0) 'Child collection lost its closed typed/default-empty contract.'
            $childCount++
        }
    }
    if($contract.Contains('sources')){
        Assert-Composition ($record[0].sources.Count -eq $contract.sources.Count -and $record[0].origin.version -ceq $contract.origin.version -and $record[0].origin.sourceCommit -ceq $contract.origin.sourceCommit -and $record[0].origin.manifestDigest -ceq $contract.origin.manifestDigest) 'AVM provenance differs from the reviewed release.'
        foreach($path in $contract.sources.Keys){Assert-Composition ($record[0].sources[$path] -ceq $contract.sources[$path] -and (Get-FileHash -LiteralPath (Join-Path $original $path) -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $contract.sources[$path]) "Pinned source closure differs: $path"}
    }else{Assert-Composition ((Get-CompositionTextHash (Join-Path $original $contract.path)) -ceq $contract.sourceSha256) 'Native workspace source differs.'}
}
Assert-Composition ($childCount -eq 7) 'Not all seven child collections were selected.'
$workspace=@($proposal.modules|Where-Object moduleId -CEQ 'monitoring-log-analytics')[0]
$telemetry=@($proposal.modules|Where-Object moduleId -CEQ 'avm-monitoring-application-insights')[0]
$edge=$telemetry.bindings.workspaceId
Assert-Composition ($edge.kind -ceq 'Output' -and $edge.module -ceq $workspace.id -and $edge.output -ceq 'id') 'Telemetry must bind to the dedicated workspace output.'
$resolved=Get-Content -LiteralPath (Join-Path $original 'resolved-values.json') -Raw|ConvertFrom-Json -AsHashtable
foreach($key in $resolved.Keys){Assert-Composition ($key -cmatch '^p_[a-z][a-zA-Z0-9]{0,31}_location$' -and $resolved[$key] -is [string] -and $resolved[$key] -cmatch '^[a-z][a-z0-9]{1,30}$') 'Unexpected resolved data needs manual review; this acceptance fixture only permits approved location settings.'}
$main=[IO.File]::ReadAllText((Join-Path $original 'main.bicep'))
$allowedMain=[ordered]@{};foreach($node in $proposal.modules){$allowedMain["m_$($node.id)"]='./'+$contracts[$node.moduleId].path}
Assert-CompositionDependencies $main $allowedMain $false
$stack=[IO.File]::ReadAllText((Join-Path $original 'stack.bicep'))
Assert-CompositionDependencies $stack ([ordered]@{composition='./main.bicep'}) $true
Assert-Composition ((Get-CompositionTextHash (Join-Path $original 'bicepconfig.json')) -ceq (Get-CompositionTextHash (Join-Path $root 'bicepconfig.json'))) 'Draft compiler configuration differs from the reviewed source.'
foreach($entry in @('main','stack')){
    $lines=@([IO.File]::ReadAllLines((Join-Path $original "$entry.bicepparam"))|Where-Object {$_.Trim().Length -and !$_.TrimStart().StartsWith('//')})
    Assert-Composition ($lines.Count -ge 1 -and $lines[0] -ceq "using './$entry.bicep'") 'Original parameter file has an unexpected source reference.'
    $assignments=[Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach($line in $lines|Select-Object -Skip 1){
        $match=[regex]::Match($line,"^param (?<left>p_[a-z][a-zA-Z0-9]{0,31}_[a-z][a-zA-Z0-9]{0,31}) = loadJsonContent\('\./resolved-values\.json'\)\.(?<right>p_[a-z][a-zA-Z0-9]{0,31}_[a-z][a-zA-Z0-9]{0,31})$")
        Assert-Composition ($match.Success -and $match.Groups['left'].Value -ceq $match.Groups['right'].Value -and $resolved.Contains($match.Groups['left'].Value) -and $assignments.Add($match.Groups['left'].Value)) 'Original parameter file contains an unreviewed value/file read.'
    }
    Assert-Composition ($assignments.Count -eq $resolved.Count) 'Original resolved parameter assignments are incomplete.'
}

$source=Join-Path $output 'source';New-Item -ItemType Directory -Path $source|Out-Null
foreach($path in $actual){$target=Join-Path $source $path;New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force|Out-Null;Copy-Item -LiteralPath (Join-Path $original $path) -Destination $target}
$names=@{'avm-identity-user-assigned-identity'='synthetic-identity';'avm-containers-container-registry'='syntheticavmregistry';'avm-configuration-app-configuration'='synthetic-avm-config';'avm-monitoring-application-insights'='synthetic-app-insights';'avm-network-network-security-group'='synthetic-nsg';'avm-network-route-table'='synthetic-routes';'monitoring-log-analytics'='synthetic-log-workspace'}
$childNames=@{federatedIdentityCredentials='synthetic-federation';replications='westus3';keyValues='synthetic-setting';replicaLocations='syntheticreplica';securityRules='deny-inbound';routes='drop-review-range';applianceRoutes='reviewed-appliance'}
function New-CompositionValue([Collections.IDictionary]$Schema,[string]$Field,[string]$Collection=''){
    # Property names, requiredness, enums and bounds come from actual compiler contracts.
    # These explicit semantic values are synthetic fixtures, never discovery/default claims.
    $semantic=@{issuer='https://issuer.example.test/';subject='repo:example/repo:environment:reviewed';location='westus3';replicaLocation='westus3';value='{"synthetic":true}';sourceAddressPrefix='VirtualNetwork';destinationAddressPrefix='VirtualNetwork';sourcePortRange='*';destinationPortRange='443';nextHopIpAddress='10.250.0.4';access='Deny';direction='Inbound';protocol='Tcp';nextHopType='None'}
    if($Schema.Contains('allowedValues')){
        if($Schema.type -eq 'array'){return ,@($Schema.allowedValues[0])}
        if($semantic.Contains($Field) -and $Schema.allowedValues -ccontains $semantic[$Field]){return $semantic[$Field]}
        return $Schema.allowedValues[0]
    }
    switch($Schema.type){
        array {Assert-Composition ($Schema.Contains('items')) 'Untyped child fixture is unsupported.';return ,@((New-CompositionValue $Schema.items $Field $Collection))}
        object {
            if(!$Schema.Contains('properties')){return @{owner='synthetic-acceptance';environment='synthetic'}}
            $result=[ordered]@{}
            foreach($property in $Schema.properties.Keys){$definition=$Schema.properties[$property];if($definition.Contains('nullable') -and $definition.nullable){continue};$result[$property]=New-CompositionValue $definition $property $Collection}
            return $result
        }
        int {if($Schema.Contains('defaultValue')){return $Schema.defaultValue};if($Schema.Contains('minValue')){return $Schema.minValue};return 1}
        bool {if($Schema.Contains('defaultValue')){return $Schema.defaultValue};return $false}
        string {
            $value=if($Field -eq 'name' -and $Collection){$childNames[$Collection]}elseif($Field -eq 'addressPrefix'){if($Collection -eq 'applianceRoutes'){'10.251.0.0/16'}else{'10.250.0.0/16'}}elseif($semantic.Contains($Field)){$semantic[$Field]}elseif($Schema.Contains('defaultValue')){$Schema.defaultValue}else{'synthetic'}
            Assert-Composition ($value -is [string] -and $value -notmatch '^\[') 'An unresolved expression cannot become a synthetic fixture value.'
            if($Schema.Contains('minLength')){$value=$value.PadRight($Schema.minLength,'x')}
            if($Schema.Contains('maxLength')){Assert-Composition ($value.Length -le $Schema.maxLength) 'Synthetic value exceeds its actual contract.'}
            return $value
        }
        default {throw 'Unsupported actual parameter type.'}
    }
}
$values=[ordered]@{}
foreach($node in $proposal.modules){
    $contract=$contracts[$node.moduleId]
    foreach($key in $contract.parameters.Keys){
        $parameter="p_$($node.id)_$key"
        if(!$provenance.Contains($parameter) -or $provenance[$parameter].kind -ceq 'Output'){continue}
        $schema=$contract.parameters[$key]
        $value=switch($key){name {$names[$node.moduleId]} location {'eastus2'} tags {@{owner='synthetic-acceptance';environment='synthetic'}} default {New-CompositionValue $schema $key $(if($children.Contains($node.moduleId) -and $children[$node.moduleId] -ccontains $key){$key}else{''})}}
        $values[$parameter]=$value
    }
}
foreach($node in $proposal.modules){if($children.Contains($node.moduleId)){foreach($key in $children[$node.moduleId]){Assert-Composition ($values["p_$($node.id)_$key"].Count -gt 0) 'A requested child fixture is empty.'}}}
$values|ConvertTo-Json -Depth 30|Set-Content -LiteralPath (Join-Path $output 'acceptance-values.json') -Encoding utf8NoBOM
function Write-CompositionParams([string]$Name,[string]$Entry,[string]$ValueFile,[bool]$Stack){
    $lines=@("using './source/$Entry.bicep'")+@(foreach($key in $values.Keys){"param $key = loadJsonContent('./$ValueFile').$key"})
    if($Stack){$lines+=@("param workloadResourceGroupName = 'synthetic-avm-review-rg'","param deploymentLocation = 'eastus2'","param workloadTags = { owner: 'synthetic-acceptance', environment: 'synthetic' }")}
    [IO.File]::WriteAllText((Join-Path $output "$Name.bicepparam"),($lines -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
}
Write-CompositionParams 'positive-main' 'main' 'acceptance-values.json' $false
Write-CompositionParams 'positive-stack' 'stack' 'acceptance-values.json' $true
$federation=@($proposal.modules|Where-Object moduleId -CEQ 'avm-identity-user-assigned-identity')[0]
$federationParameter="p_$($federation.id)_federatedIdentityCredentials"
$invalid=ConvertTo-Json -InputObject $values -Depth 30|ConvertFrom-Json -AsHashtable
$invalid[$federationParameter][0].unreviewed=$true
$invalid|ConvertTo-Json -Depth 30|Set-Content -LiteralPath (Join-Path $output 'invalid-child-values.json') -Encoding utf8NoBOM
Write-CompositionParams 'negative-child' 'main' 'invalid-child-values.json' $false
$invalid=ConvertTo-Json -InputObject $values -Depth 30|ConvertFrom-Json -AsHashtable
$invalid[$federationParameter][0].audiences=@('invented-audience')
$invalid|ConvertTo-Json -Depth 30|Set-Content -LiteralPath (Join-Path $output 'invalid-audience-values.json') -Encoding utf8NoBOM
Write-CompositionParams 'negative-audience' 'main' 'invalid-audience-values.json' $false
$compiler=Join-Path $root '.tools/bicep/0.48.1/bicep.exe'
Assert-Composition (Test-Path -LiteralPath $compiler) 'Prepare the pinned checkout-local Bicep 0.48.1 compiler.'
& (Join-Path $root 'scripts/Setup-AvmTooling.ps1') -VerifyOnly
$previousExtraction=$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR=Join-Path $root '.local/avm-bicep-bundle-composition'
$build=Join-Path $output 'build';New-Item -ItemType Directory -Path $build|Out-Null
function Invoke-CompositionCompiler([string]$Command,[string]$InputPath,[string]$Label,[bool]$ExpectedPass){
    $start=[Diagnostics.ProcessStartInfo]::new($compiler);$start.UseShellExecute=$false;$start.CreateNoWindow=$true;$start.RedirectStandardOutput=$true;$start.RedirectStandardError=$true
    foreach($argument in @($Command,$InputPath,'--no-restore','--outfile',(Join-Path $build "$Label.json"))){$start.ArgumentList.Add($argument)}
    $process=[Diagnostics.Process]::Start($start)
    try{$stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync();$process.WaitForExit();$log=$stdout.GetAwaiter().GetResult()+$stderr.GetAwaiter().GetResult();[IO.File]::WriteAllText((Join-Path $build "$Label.log"),$log,[Text.UTF8Encoding]::new($false));Assert-Composition (($process.ExitCode -eq 0) -eq $ExpectedPass) "Unexpected compilation outcome: $Label. Inspect its local log.";if(!$ExpectedPass){Assert-Composition ($log -match 'Error BCP') 'Expected a Bicep parameter-contract diagnostic.'}}finally{$process.Dispose()}
}
try{
    foreach($entry in @('main','stack')){
        Invoke-CompositionCompiler 'build' (Join-Path $source "$entry.bicep") $entry $true
        Invoke-CompositionCompiler 'build-params' (Join-Path $output "positive-$entry.bicepparam") "positive-$entry.parameters" $true
        Invoke-CompositionCompiler 'build-params' (Join-Path $source "$entry.bicepparam") "negative-unresolved-$entry" $false
    }
    Invoke-CompositionCompiler 'build-params' (Join-Path $output 'negative-child.bicepparam') 'negative-child' $false
    Invoke-CompositionCompiler 'build-params' (Join-Path $output 'negative-audience.bicepparam') 'negative-audience' $false
    $compiled=Get-Content -LiteralPath (Join-Path $build 'main.json') -Raw|ConvertFrom-Json -AsHashtable
    Assert-Composition ($compiled.languageVersion -ceq '2.0' -and $compiled.resources -is [Collections.IDictionary] -and $compiled.resources.Count -eq 7) 'Compiled main resource inventory differs from the composed modules.'
    Assert-Composition ($compiled.parameters.Count -eq $values.Count -and @('outputs','variables','functions','imports','extensions'|Where-Object {$compiled.Contains($_)}).Count -eq 0) 'Compiled main contains state outside the generated input/module profile.'
    foreach($key in $values.Keys){Assert-Composition ($compiled.parameters.Contains($key) -and !$compiled.parameters[$key].Contains('defaultValue')) 'A composed parameter lost its explicit required-input boundary.'}
    foreach($node in $proposal.modules){
        $symbol="m_$($node.id)";$deployment=$compiled.resources[$symbol]
        Assert-Composition ($null -ne $deployment -and $deployment.type -ceq 'Microsoft.Resources/deployments' -and $deployment.properties.parameters.Count -eq $node.bindings.Count) 'Compiled module deployment bindings differ.'
        Assert-Composition (@('condition','copy','scope','subscriptionId','resourceGroup'|Where-Object {$deployment.Contains($_)}).Count -eq 0 -and $deployment.name -ceq "[format('$symbol-{0}', uniqueString('$symbol', deployment().name))]" -and $deployment.properties.mode -ceq 'Incremental' -and $deployment.properties.expressionEvaluationOptions.scope -ceq 'inner') 'Compiled module changed the unconditional same-group deployment profile.'
        $expectedDependencies=@($node.bindings.Values|Where-Object kind -CEQ 'Output'|ForEach-Object {"m_$($_.module)"}|Sort-Object -Unique)
        $actualDependencies=@();if($deployment.Contains('dependsOn')){$actualDependencies=@($deployment.dependsOn)}
        Assert-Composition (@(Compare-Object $expectedDependencies $actualDependencies -CaseSensitive).Count -eq 0) 'Compiled module dependency ownership differs.'
        foreach($key in $node.bindings.Keys){
            $binding=$node.bindings[$key]
            $expectedValue=switch -CaseSensitive ($binding.kind){
                Input {"[parameters('p_$($node.id)_$key')]"}
                Setting {"[parameters('p_$($node.id)_$key')]"}
                Output {"[reference('m_$($binding.module)').outputs.$($binding.output).value]"}
                default {throw 'Unexpected binding kind in the acceptance profile.'}
            }
            $emittedBinding=$deployment.properties.parameters[$key]
            Assert-Composition ($emittedBinding -is [Collections.IDictionary] -and $emittedBinding.Contains('value') -and $emittedBinding.value -is [string] -and $emittedBinding.value -ceq $expectedValue) "Compiled module does not forward its exact binding: $($node.id).$key"
            if($binding.kind -ceq 'Output'){Assert-Composition ($deployment.dependsOn -ccontains "m_$($binding.module)") 'Compiled output binding lost its deployment dependency.'}
        }
    }
    $compiledStack=Get-Content -LiteralPath (Join-Path $build 'stack.json') -Raw|ConvertFrom-Json -AsHashtable
    Assert-Composition ($compiledStack.resources.Count -eq 2 -and $compiledStack.resources.composition.properties.parameters.Count -eq $values.Count -and $compiledStack.parameters.Count -eq ($values.Count+3)) 'Compiled stack module inventory differs.'
    $group=$compiledStack.resources.group;$composition=$compiledStack.resources.composition
    Assert-Composition ($group.type -ceq 'Microsoft.Resources/resourceGroups' -and $group.apiVersion -ceq '2024-03-01' -and $group.name -ceq "[parameters('workloadResourceGroupName')]" -and $group.location -ceq "[parameters('deploymentLocation')]" -and $group.tags -ceq "[parameters('workloadTags')]" -and @('condition','copy','scope','subscriptionId','resourceGroup'|Where-Object {$group.Contains($_)}).Count -eq 0) 'Compiled resource group changed its explicit ownership inputs.'
    Assert-Composition ($composition.type -ceq 'Microsoft.Resources/deployments' -and $composition.name -ceq "[format('composition-{0}', uniqueString('composition', deployment().name))]" -and $composition.resourceGroup -ceq "[parameters('workloadResourceGroupName')]" -and $composition.dependsOn.Count -eq 1 -and $composition.dependsOn[0] -ceq 'group' -and $composition.properties.mode -ceq 'Incremental' -and $composition.properties.expressionEvaluationOptions.scope -ceq 'inner' -and @('condition','copy','scope','subscriptionId'|Where-Object {$composition.Contains($_)}).Count -eq 0) 'Compiled stack changed its unconditional owned-group deployment profile.'
    foreach($key in $compiledStack.parameters.Keys){Assert-Composition (!$compiledStack.parameters[$key].Contains('defaultValue')) 'Stack wrapper introduced an implicit input default.'}
    foreach($key in $values.Keys){Assert-Composition ($composition.properties.parameters[$key].value -ceq "[parameters('$key')]") "Stack wrapper does not forward its exact input: $key"}
}finally{$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR=$previousExtraction}
foreach($path in $actual){Assert-Composition ((Get-FileHash -LiteralPath (Join-Path $original $path) -Algorithm SHA256).Hash -ceq $originalHashes[$path]) "Original model draft changed during acceptance: $path"}
$result=[ordered]@{schema='platform.avm-composition-acceptance/v1';generatedUtc=[DateTimeOffset]::UtcNow.ToString('o');originalDraft=$original;proposalSha256=(Get-CompositionTextHash (Join-Path $original 'proposal.json'));evidenceDigest=$receipt.evidenceDigest;catalogDigest=$receipt.catalogDigest;nodes=7;avmModules=6;typedChildCollections=7;workspaceOutputEdges=1;compiledBindingWiringVerified=$true;compiledOwnershipProfileVerified=$true;sourceClosureFiles=$expected.Count;sourceTemplatesCompiled=2;filledParameterFilesCompiled=2;unresolvedParameterFilesRejected=2;invalidChildParameterFilesRejected=2;originalFilesPreserved=$true;parameterValues='Synthetic acceptance fixtures only; not model/discovery values or deployment-ready defaults';azureCalls=$false;modelInferencePerformedByThisVerifier=$false;deploymentAuthorized=$false;compilerVersion='0.48.1'}
$result|ConvertTo-Json -Depth 10|Set-Content -LiteralPath (Join-Path $output 'results.json') -Encoding utf8NoBOM
Write-Host "PASS: seven-node draft; six pinned AVM modules; seven nonempty typed child fixtures; workspace output edge; four positive compilations and four required failures. Original draft preserved. Evidence: $output/results.json"
