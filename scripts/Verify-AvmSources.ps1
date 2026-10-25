#requires -Version 7.4
[CmdletBinding()]
param(
    [string]$BundleRoot = (Join-Path $PSScriptRoot '../vendor/azure-verified-modules'),
    [switch]$AsJson
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest

# This allowlist is reviewed together with the application wrappers. All checks are offline.
$pins = @{
    'identity-user-assigned-identity' = @('managed-identity/user-assigned-identity', '0.6.0', 'a9370ad48e31b2de5a679dd630da28612ad95471', 'af3108d4973ce5252f08442e2e97b1fc6c67e19a7143ea58ba21ac1a58dc807e')
    'containers-container-registry' = @('container-registry/registry', '0.13.1', 'f1f4ba28698437d9a683d68bf181341ad3547e48', '2ee6ba8e32e9392ed7a5c0eee39910ce5222c35d4c463b087d0f6928139789bd')
    'configuration-app-configuration' = @('app-configuration/configuration-store', '0.10.1', 'f1f4ba28698437d9a683d68bf181341ad3547e48', '9405d1dc0ea6801538fea96ffb78350249adfcdd6d59b8acab12aefb57af7a11')
    'monitoring-application-insights' = @('insights/component', '0.8.0', '4f750c70f333b2df6170e3b56ae90faa852361d2', 'ab732413f1131a8915500e3b9ec12e21964b015cf7dfcdf9ce5c8ed95deba8b3')
    'network-network-security-group' = @('network/network-security-group', '0.5.4', '99f7bd7e64b6067691330b63744a999269431d2c', 'c5d7a58038a524309b5a392a2f72540eaa93466cd14659fcc8ff3ccc62b1008a')
    'network-route-table' = @('network/route-table', '0.5.0', '38e14b5cc5a386be8620c26c25d368e8aeaa0bf2', '59067d0b5aed599eb7b1e778cab5961630d82115e64ffba22c7afe72f194a590')
}
$repository = 'https://github.com/Azure/bicep-registry-modules'
$licenseSha256 = 'c2cfccb812fe482101a8f04597dfc5a9991a6b2748266c47ac91b6a5aae15383'
$root = [IO.Path]::GetFullPath($BundleRoot)
if (!(Test-Path -LiteralPath $root -PathType Container)) { throw 'AVM bundle directory is missing.' }
if ((Get-Item -LiteralPath $root -Force).Attributes -band [IO.FileAttributes]::ReparsePoint) { throw 'AVM bundle root must not be a link.' }

function Assert-Avm([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw "AVM verification failed: $Message" }
}
function Resolve-AvmFile([string]$RelativePath) {
    Assert-Avm (![string]::IsNullOrWhiteSpace($RelativePath)) 'empty file path'
    Assert-Avm ($RelativePath -notmatch '\\|^/|:|(^|/)(\.|\.\.)(/|$)|//') "unsafe file path: $RelativePath"
    $resolved = [IO.Path]::GetFullPath((Join-Path $root $RelativePath))
    Assert-Avm ($resolved.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) "file escapes bundle: $RelativePath"
    Assert-Avm (Test-Path -LiteralPath $resolved -PathType Leaf) "missing file: $RelativePath"
    $cursor = Get-Item -LiteralPath $resolved -Force
    while ($cursor.FullName -ne $root) {
        Assert-Avm (!($cursor.Attributes -band [IO.FileAttributes]::ReparsePoint)) "linked file or directory: $RelativePath"
        $cursor = if ($cursor -is [IO.FileInfo]) { $cursor.Directory } else { $cursor.Parent }
    }
    return $resolved
}
function Get-AvmSha([string]$Path) {
    return (Get-FileHash -LiteralPath $Path -Algorithm SHA256).Hash.ToLowerInvariant()
}
function Read-AvmJson([string]$RelativePath) {
    return Get-Content -LiteralPath (Resolve-AvmFile $RelativePath) -Raw | ConvertFrom-Json -AsHashtable -Depth 100
}
function Measure-AvmTemplate([System.Collections.IDictionary]$Template) {
    Assert-Avm ($Template.Contains('$schema') -and $Template['$schema'] -eq 'https://schema.management.azure.com/schemas/2019-04-01/deploymentTemplate.json#') 'unrecognized ARM template schema'
    Assert-Avm ($Template.Contains('resources')) 'ARM template has no resource collection'
    $resources = $Template.resources
    $definitions = if ($resources -is [System.Collections.IDictionary]) { @($resources.Values) } else { @($resources) }
    $templateCount = 1
    $resourceCount = 0
    $types = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    foreach ($resource in $definitions) {
        Assert-Avm ($resource -is [System.Collections.IDictionary] -and $resource.Contains('type')) 'invalid resource definition'
        $null = $types.Add([string]$resource.type)
        $resourceCount++
        if ($resource.type -eq 'Microsoft.Resources/deployments') {
            Assert-Avm ($resource.Contains('properties') -and $resource.properties -is [System.Collections.IDictionary]) 'deployment has no properties'
            Assert-Avm (!$resource.properties.Contains('templateLink')) 'external linked template is forbidden'
            Assert-Avm ($resource.properties.Contains('template') -and $resource.properties.template -is [System.Collections.IDictionary]) 'deployment must contain its complete inline template'
            $nested = Measure-AvmTemplate $resource.properties.template
            $templateCount += $nested.templateCount
            $resourceCount += $nested.resourceCount
            foreach ($type in $nested.resourceTypes) { $null = $types.Add($type) }
        }
    }
    return @{ templateCount = $templateCount; resourceCount = $resourceCount; resourceTypes = @($types | Sort-Object) }
}

$bundle = Read-AvmJson 'bundle.json'
Assert-Avm ($bundle.schemaVersion -eq 1 -and $bundle.sourceRepository -ceq $repository) 'bundle schema or repository differs'
Assert-Avm (@($bundle.modules).Count -eq $pins.Count) 'module count differs'
$expectedFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$null = $expectedFiles.Add('LICENSE')
$seenModules = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
$results = foreach ($module in $bundle.modules) {
    Assert-Avm ($pins.ContainsKey($module.id) -and $seenModules.Add($module.id)) 'unknown or duplicate module'
    $pin = $pins[$module.id]
    Assert-Avm ($module.version -ceq $pin[1] -and $module.sourceCommit -ceq $pin[2]) "source version/commit differs: $($module.id)"
    Assert-Avm ($module.registry -ceq "mcr.microsoft.com/bicep/avm/res/$($pin[0]):$($pin[1])") "registry differs: $($module.id)"
    Assert-Avm ($module.sourceRepository -ceq $repository -and $module.sourceUrl -ceq "$repository/blob/$($pin[2])/avm/res/$($pin[0])/main.bicep") "source URL differs: $($module.id)"
    Assert-Avm ($module.sourceTag -ceq "avm/res/$($pin[0])/$($pin[1])" -and $module.license -ceq 'MIT') "source tag/license differs: $($module.id)"
    Assert-Avm ($module.templatePath -ceq "$($module.id)/main.json" -and $module.manifestPath -ceq "$($module.id)/manifest.json") "artifact paths differ: $($module.id)"
    Assert-Avm ($module.manifestDigest -ceq "sha256:$($pin[3])" -and $module.manifestSha256 -ceq $pin[3]) "manifest pin differs: $($module.id)"
    $apiRoot = "https://mcr.microsoft.com/v2/bicep/avm/res/$($pin[0])"
    Assert-Avm ($module.manifestUrl -ceq "$apiRoot/manifests/sha256:$($pin[3])" -and $module.versionManifestUrl -ceq "$apiRoot/manifests/$($pin[1])") "manifest URL differs: $($module.id)"
    $manifestPath = Resolve-AvmFile $module.manifestPath
    Assert-Avm ((Get-AvmSha $manifestPath) -ceq $pin[3]) "manifest bytes changed: $($module.id)"
    $manifest = Read-AvmJson $module.manifestPath
    Assert-Avm ($manifest.schemaVersion -eq 2 -and $manifest.mediaType -ceq 'application/vnd.oci.image.manifest.v1+json' -and $manifest.artifactType -ceq 'application/vnd.ms.bicep.module.artifact') "unexpected OCI manifest: $($module.id)"
    $layers = @($manifest.layers | Where-Object { $_.mediaType -ceq 'application/vnd.ms.bicep.module.layer.v1+json' })
    Assert-Avm ($layers.Count -eq 1) "expected one ARM layer: $($module.id)"
    $layer = $layers[0]
    $templatePath = Resolve-AvmFile $module.templatePath
    $templateSha = Get-AvmSha $templatePath
    Assert-Avm ($layer.digest -ceq "sha256:$templateSha" -and $module.templateSha256 -ceq $templateSha) "template digest differs: $($module.id)"
    Assert-Avm ($layer.size -eq (Get-Item -LiteralPath $templatePath).Length -and $module.templateBytes -eq $layer.size) "template size differs: $($module.id)"
    Assert-Avm ($module.templateBlobUrl -ceq "$apiRoot/blobs/$($layer.digest)") "template URL differs: $($module.id)"
    $summary = Measure-AvmTemplate (Read-AvmJson $module.templatePath)
    $null = $expectedFiles.Add($module.templatePath)
    $null = $expectedFiles.Add($module.manifestPath)
    [ordered]@{ id = $module.id; version = $module.version; templateSha256 = $templateSha; templateCount = $summary.templateCount; resourceCount = $summary.resourceCount; resourceTypes = $summary.resourceTypes }
}
Assert-Avm (@($bundle.files).Count -eq $expectedFiles.Count) 'file inventory count differs'
$seenFiles = [Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
foreach ($entry in $bundle.files) {
    Assert-Avm ($expectedFiles.Contains($entry.path) -and $seenFiles.Add($entry.path)) 'unexpected or duplicate file inventory entry'
    $path = Resolve-AvmFile $entry.path
    Assert-Avm ((Get-AvmSha $path) -ceq $entry.sha256 -and (Get-Item -LiteralPath $path).Length -eq $entry.bytes) "file integrity differs: $($entry.path)"
}
$null = $expectedFiles.Add('bundle.json')
$actualFiles = @(Get-ChildItem -LiteralPath $root -Recurse -Force -File)
Assert-Avm ($actualFiles.Count -eq $expectedFiles.Count) 'untracked or missing vendor file'
foreach ($file in $actualFiles) {
    $relative = [IO.Path]::GetRelativePath($root, $file.FullName).Replace('\', '/')
    Assert-Avm ($expectedFiles.Contains($relative)) "untracked vendor file: $relative"
}
Assert-Avm ((Get-AvmSha (Resolve-AvmFile 'LICENSE')) -ceq $licenseSha256) 'upstream MIT license changed'
Assert-Avm (@($bundle.licenses).Count -eq 1) 'license inventory differs'
$license = $bundle.licenses[0]
Assert-Avm ($license.path -ceq 'LICENSE' -and $license.spdx -ceq 'MIT' -and $license.sha256 -ceq $licenseSha256) 'license provenance differs'
$sourceCommits = @($pins.Values | ForEach-Object { $_[2] } | Sort-Object -Unique)
Assert-Avm ($license.sourceCommit -cin $sourceCommits -and $license.sourceUrl -ceq "https://raw.githubusercontent.com/Azure/bicep-registry-modules/$($license.sourceCommit)/LICENSE") 'license source differs'
Assert-Avm (@($license.verifiedSourceCopies).Count -eq $sourceCommits.Count) 'license source coverage differs'
$seenLicenseCommits = [Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
foreach ($copy in $license.verifiedSourceCopies) {
    Assert-Avm ($copy.sourceCommit -cin $sourceCommits -and $seenLicenseCommits.Add($copy.sourceCommit)) 'unknown or duplicate license source'
    Assert-Avm ($copy.sha256 -ceq $licenseSha256 -and $copy.sourceUrl -ceq "https://raw.githubusercontent.com/Azure/bicep-registry-modules/$($copy.sourceCommit)/LICENSE") 'license source hash/URL differs'
}
$report = [ordered]@{ passed = $true; offline = $true; moduleCount = $pins.Count; fileCount = $actualFiles.Count; licenseCount = 1; modules = @($results) }
if ($AsJson) { $report | ConvertTo-Json -Depth 10 } else { Write-Host "PASS: six pinned AVM modules; $($actualFiles.Count) complete vendor files; hashes, OCI layers, inline children and MIT license verified offline." }
