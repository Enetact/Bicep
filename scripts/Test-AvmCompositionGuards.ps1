#requires -Version 7.4
<#
.SYNOPSIS
Verify the combined-composition acceptance guard rejects deliberately malformed copies.
.DESCRIPTION
First validates the original application-generated draft. All subsequent changes are
synthetic fixtures in a new artifacts folder; the original source remains untouched.
#>
[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$DraftDirectory,
    [string]$RepositoryRoot=''
)
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$RepositoryRoot) { $RepositoryRoot = Split-Path $PSScriptRoot -Parent }
$root = [IO.Path]::GetFullPath($RepositoryRoot)
$source = [IO.Path]::GetFullPath($DraftDirectory)
$run = Join-Path $root ('artifacts/avm-composition-guards/' + [guid]::NewGuid().ToString('N'))
New-Item -ItemType Directory -Path $run | Out-Null
& (Join-Path $root 'scripts/Test-AvmComposition.ps1') -DraftDirectory $source -RepositoryRoot $root -OutputDirectory (Join-Path $run 'baseline')
$originalHashes = @{}
Get-ChildItem -LiteralPath $source -Recurse -File -Force | ForEach-Object { $originalHashes[$_.FullName] = (Get-FileHash -LiteralPath $_.FullName -Algorithm SHA256).Hash }
$sourceProposal = Get-Content -LiteralPath (Join-Path $source 'proposal.json') -Raw | ConvertFrom-Json -AsHashtable
$identityNode = @($sourceProposal.modules | Where-Object moduleId -CEQ 'avm-identity-user-assigned-identity')[0]
$identityParameter = "p_$($identityNode.id)_federatedIdentityCredentials"
$identityHeader = "module m_$($identityNode.id) './modules/avm/identity/user-assigned-identity/main.bicep' = {"
$results = [Collections.Generic.List[object]]::new()
function Expect-Rejection([string]$Name, [scriptblock]$Run, [string]$Diagnostic) {
    try { & $Run; throw 'The malformed fixture was accepted.' }
    catch {
        if ($_.Exception.Message -cnotlike $Diagnostic) { throw }
        $results.Add([ordered]@{ name = $Name; rejected = $true; expectedDiagnostic = $Diagnostic })
    }
}
function Copy-Fixture([string]$Name) {
    $target = Join-Path $run $Name
    New-Item -ItemType Directory -Path $target | Out-Null
    Get-ChildItem -LiteralPath $source -Force | Copy-Item -Destination $target -Recurse
    return $target
}
function Write-Proposal([string]$Path, [object]$Proposal) {
    $proposalPath = Join-Path $Path 'proposal.json'
    $Proposal | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $proposalPath -Encoding utf8NoBOM
    $receiptPath = Join-Path $Path 'draft-receipt.json'
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -AsHashtable
    $text = [IO.File]::ReadAllText($proposalPath).Replace("`r`n", "`n")
    $receipt.files['proposal.json'] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant()
    $receipt | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
}
function Update-SourceReceipt([string]$Path, [string]$Name) {
    $receiptPath = Join-Path $Path 'draft-receipt.json'
    $receipt = Get-Content -LiteralPath $receiptPath -Raw | ConvertFrom-Json -AsHashtable
    $text = [IO.File]::ReadAllText((Join-Path $Path $Name)).Replace("`r`n", "`n")
    $receipt.files[$Name] = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text))).ToLowerInvariant()
    $receipt | ConvertTo-Json -Depth 30 | Set-Content -LiteralPath $receiptPath -Encoding utf8NoBOM
}
$badChild = Copy-Fixture 'child-not-parameterized'
$proposal = Get-Content -LiteralPath (Join-Path $badChild 'proposal.json') -Raw | ConvertFrom-Json -AsHashtable
$identity = @($proposal.modules | Where-Object moduleId -CEQ 'avm-identity-user-assigned-identity')[0]
$identity.bindings.federatedIdentityCredentials = @{ kind = 'Setting'; reference = 'arbitrary' }
Write-Proposal $badChild $proposal
Expect-Rejection 'child binding must be Input' { & (Join-Path $root 'scripts/Test-AvmComposition.ps1') -DraftDirectory $badChild } 'Missing explicit typed child input: federatedIdentityCredentials'

$badEdge = Copy-Fixture 'wrong-workspace-edge'
$proposal = Get-Content -LiteralPath (Join-Path $badEdge 'proposal.json') -Raw | ConvertFrom-Json -AsHashtable
$telemetry = @($proposal.modules | Where-Object moduleId -CEQ 'avm-monitoring-application-insights')[0]
$telemetry.bindings.workspaceId.module = $identityNode.id
Write-Proposal $badEdge $proposal
Expect-Rejection 'workspace edge must bind workspace' { & (Join-Path $root 'scripts/Test-AvmComposition.ps1') -DraftDirectory $badEdge } 'Telemetry must bind to the dedicated workspace output.'

$tampered = Copy-Fixture 'tampered-source'
[IO.File]::AppendAllText((Join-Path $tampered 'main.bicep'), "`n// deliberately changed after source receipt`n")
Expect-Rejection 'source receipt detects modified template' { & (Join-Path $root 'scripts/Test-AvmComposition.ps1') -DraftDirectory $tampered } 'Source receipt differs: main.bicep'

foreach ($variant in @('indented-module', 'comment-prefix-module', 'comment-prefix-resource', 'comment-prefix-read', 'directory-metadata-read', 'using', 'test', 'provider')) {
    $fixture = Copy-Fixture $variant
    $addition = switch ($variant) {
        'indented-module' { "`n  module extra 'br:unresolved.example.test/fake/module:0.0.0' = {}`n" }
        'comment-prefix-module' { "`n/*synthetic comment*/ module extra '../outside.bicep' = {}`n" }
        'comment-prefix-resource' { "`n/*synthetic comment*/ resource extra 'Microsoft.Storage/storageAccounts@2023-05-01' = {}`n" }
        'comment-prefix-read' { "`n/*synthetic comment*/ var extra = loadTextContent('../outside.txt')`n" }
        'directory-metadata-read' { "`noutput fileInfo array = loadDirectoryFileInfo('../')`n" }
        'using' { "`nusing '../outside.bicep'`n" }
        'test' { "`ntest extra '../outside.bicep' = {}`n" }
        'provider' { "`nprovider extra`n" }
    }
    [IO.File]::AppendAllText((Join-Path $fixture 'main.bicep'), $addition)
    Update-SourceReceipt $fixture 'main.bicep'
    $diagnostic = switch ($variant) {
        'comment-prefix-resource' { 'Source has a resource outside the generated group wrapper.' }
        { $_ -in @('indented-module', 'comment-prefix-module') } { 'Source has a module outside the exact reviewed local closure.' }
        default { 'Source contains a dependency/read outside the offline generated profile.' }
    }
    Expect-Rejection "$variant declaration rejected before compiler" { & (Join-Path $root 'scripts/Test-AvmComposition.ps1') -DraftDirectory $fixture } $diagnostic
}

$droppedChild = Copy-Fixture 'child-not-forwarded'
$mainPath = Join-Path $droppedChild 'main.bicep'
$main = [IO.File]::ReadAllText($mainPath).Replace("federatedIdentityCredentials: $identityParameter", "federatedIdentityCredentials: empty($identityParameter) ? $identityParameter : []")
[IO.File]::WriteAllText($mainPath, $main)
Update-SourceReceipt $droppedChild 'main.bicep'
Expect-Rejection 'typed child must reach the module deployment' { & (Join-Path $root 'scripts/Test-AvmComposition.ps1') -DraftDirectory $droppedChild } "Compiled module does not forward its exact binding: $($identityNode.id).federatedIdentityCredentials"

foreach ($variant in @('conditional-module', 'alternate-resource-group')) {
    $fixture = Copy-Fixture $variant
    $mainPath = Join-Path $fixture 'main.bicep'
    $main = [IO.File]::ReadAllText($mainPath)
    if (!$main.Contains($identityHeader, [StringComparison]::Ordinal)) { throw 'The valid fixture lacks its generated identity module header.' }
    $replacement = if ($variant -eq 'conditional-module') { $identityHeader.Replace('= {', '= if (false) {') } else { $identityHeader + "`n  scope: resourceGroup('synthetic-other-rg')" }
    [IO.File]::WriteAllText($mainPath, $main.Replace($identityHeader, $replacement))
    Update-SourceReceipt $fixture 'main.bicep'
    Expect-Rejection "$variant changes deployment ownership" { & (Join-Path $root 'scripts/Test-AvmComposition.ps1') -DraftDirectory $fixture } 'Compiled module changed the unconditional same-group deployment profile.'
}

foreach ($variant in @('traversal', 'duplicate')) {
    $archivePath = Join-Path $run "$variant.zip"
    $archive = [IO.Compression.ZipFile]::Open($archivePath, [IO.Compression.ZipArchiveMode]::Create)
    try {
        if ($variant -eq 'traversal') { $names = @('../escape.txt') } else { $names = @('main.bicep', 'main.bicep') }
        foreach ($name in $names) {
            $entry = $archive.CreateEntry($name)
            $writer = [IO.StreamWriter]::new($entry.Open())
            try { $writer.Write('synthetic') } finally { $writer.Dispose() }
        }
    } finally { $archive.Dispose() }
    Expect-Rejection "$variant ZIP rejected" { & (Join-Path $root 'scripts/Test-AvmComposition.ps1') -ArchivePath $archivePath } 'Archive has an unknown or duplicate entry.'
}
if (Test-Path -LiteralPath (Join-Path $run 'escape.txt')) { throw 'Unexpected archive extraction outside the draft directory.' }
foreach ($path in $originalHashes.Keys) { if ((Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash -cne $originalHashes[$path]) { throw 'An original draft file changed while exercising copied malformed fixtures.' } }
[ordered]@{ schema = 'platform.avm-composition-guard-tests/v1'; cases = $results.Count; passed = $results.Count; originalFilesPreserved = $true; azureCalls = $false; deploymentAuthorized = $false; results = $results } | ConvertTo-Json -Depth 10 | Set-Content -LiteralPath (Join-Path $run 'results.json') -Encoding utf8NoBOM
Write-Host "PASS: $($results.Count) composition acceptance guards. Evidence: $run/results.json"
