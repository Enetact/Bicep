#requires -Version 7.4
[CmdletBinding()]
param([string]$PublishedPackageDirectory = '', [string]$DotNetPath = 'dotnet')
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
$repositoryRoot = [IO.Path]::GetFullPath((Join-Path $PSScriptRoot '../..'))
$publisher = Join-Path $repositoryRoot 'scripts/Publish-Foundation.ps1'
$tokens = $null; $parseErrors = $null
$ast = [Management.Automation.Language.Parser]::ParseFile($publisher, [ref]$tokens, [ref]$parseErrors)
if ($parseErrors) { throw ($parseErrors | Out-String) }
# Load trusted local helper definitions only. The test never invokes publish or adds a production bypass.
foreach ($function in $ast.FindAll({param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst]}, $true)) {
    . ([scriptblock]::Create($function.Extent.Text))
}
$fixtureRoot = Join-Path $repositoryRoot ('artifacts/foundation-package-tests/' + [Guid]::NewGuid().ToString('N'))
$null = New-Item -ItemType Directory -Path $fixtureRoot
$script:checks = 0
function Assert-PackageCheck([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }; $script:checks++
}
function Assert-PackageRejected([scriptblock]$Action, [string]$Expected) {
    $caught = $false
    try { & $Action | Out-Null } catch { $caught = $true; if ($_.Exception.Message -notlike $Expected) { throw } }
    Assert-PackageCheck $caught "Expected rejection: $Expected"
}
function New-PackageFixture([string]$Name, [switch]$NoOwner) {
    $parent = Join-Path $fixtureRoot $Name; $package = Join-Path $parent 'package'
    $null = New-Item -ItemType Directory -Path (Join-Path $package 'runtime'),(Join-Path $package 'config'),(Join-Path $package 'docs') -Force
    [IO.File]::WriteAllBytes((Join-Path $package 'runtime/SelfService.Foundation.Host.dll'), [Text.Encoding]::UTF8.GetBytes('synthetic fixture runtime bytes'))
    [IO.File]::WriteAllText((Join-Path $package 'runtime/SelfService.Foundation.Host.runtimeconfig.json'), '{"runtimeOptions":{"tfm":"net10.0"}}')
    [IO.File]::WriteAllText((Join-Path $package 'runtime/SelfService.Foundation.Host.deps.json'), '{}')
    [IO.File]::WriteAllBytes((Join-Path $package 'runtime/SelfService.Foundation.Worker.dll'), [Text.Encoding]::UTF8.GetBytes('synthetic fixture worker bytes'))
    [IO.File]::WriteAllText((Join-Path $package 'runtime/SelfService.Foundation.Worker.runtimeconfig.json'), '{"runtimeOptions":{"tfm":"net10.0"}}')
    [IO.File]::WriteAllText((Join-Path $package 'runtime/SelfService.Foundation.Worker.deps.json'), '{}')
    [IO.File]::WriteAllBytes((Join-Path $package 'runtime/SelfService.Foundation.Owner.dll'), [Text.Encoding]::UTF8.GetBytes('synthetic fixture owner bytes'))
    [IO.File]::WriteAllText((Join-Path $package 'runtime/SelfService.Foundation.Owner.runtimeconfig.json'), '{"runtimeOptions":{"tfm":"net10.0"}}')
    [IO.File]::WriteAllText((Join-Path $package 'runtime/SelfService.Foundation.Owner.deps.json'), '{}')
    [IO.File]::WriteAllText((Join-Path $package 'config/foundation-cell.example.json'), '{"publicKeys":[],"mockEndpoint":null}')
    [IO.File]::WriteAllText((Join-Path $package 'docs/foundation-service.md'), 'Synthetic local package guide.')
    [IO.File]::WriteAllText((Join-Path $package 'config/foundation-entra-onboarding.example.json'), '{"customerId":"fixture-customer"}')
    [IO.File]::WriteAllText((Join-Path $package 'docs/foundation-owner-cli.md'), 'Synthetic trusted-owner guide.')
    [IO.File]::WriteAllText((Join-Path $package 'docs/foundation-entra-onboarding.md'), 'Synthetic public identity proposal guide.')
    foreach ($publicFile in @(Get-FoundationPublicFiles | Where-Object { $_.StartsWith('docs/', [StringComparison]::Ordinal) })) {
        [IO.File]::WriteAllText((Join-Path $package $publicFile), 'Synthetic public local-preview guide.')
    }
    [IO.File]::WriteAllText((Join-Path $package 'README.md'), 'Local preview; source features need the matching full checkout.')
    Write-PackageDependencyFixture $package
    if (!$NoOwner) {
        [ordered]@{schema='platform-studio.foundation-package-owner.v1';runId=[Guid]::NewGuid().ToString('N');destination=$parent;localPreview=$true} |
            ConvertTo-Json | Set-Content -LiteralPath (Join-Path $parent '.package-owner.json') -Encoding utf8NoBOM
    }
    [pscustomobject]@{Parent=$parent;Package=$package;Runtime=(Join-Path $package 'runtime');Zip=(Join-Path $parent 'fixture.zip')}
}
function Write-PackageDependencyFixture([string]$Package) {
    $folder=Join-Path $Package 'dependency-evidence'; $null=New-Item -ItemType Directory -Path $folder -Force
    $runtimeFiles=@(Get-FoundationPackageFiles (Join-Path $Package 'runtime') | ForEach-Object {
        [ordered]@{path=$_.Path;bytes=$_.Length;sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
    })
    [ordered]@{schema='platform-studio.foundation-dependency-evidence.v1';technicalStatus='Blocked';packageEvidenceComplete=$true;
        binaryLicenseCoverageVerified=$false;signatureTrustVerified=$false;legalClearance=$false;signed=$false;sourceFreezeVerified=$false;productionReady=$false;
        runtimeFiles=$runtimeFiles;issues=@(@{code='SyntheticFixtureNotCommercialEvidence'})} | ConvertTo-Json -Depth 8 |
        Set-Content -LiteralPath (Join-Path $folder 'dependency-evidence.json') -Encoding utf8NoBOM
    [IO.File]::WriteAllText((Join-Path $folder 'sbom.spdx.json'), '{"spdxVersion":"SPDX-2.3","syntheticFixture":true}')
    [IO.File]::WriteAllText((Join-Path $folder 'THIRD-PARTY-NOTICES.txt'), 'Synthetic fixture; no commercial rights or legal clearance.')
    $lines=foreach($name in @('dependency-evidence.json','sbom.spdx.json','THIRD-PARTY-NOTICES.txt')) {
        (Get-FileHash -LiteralPath (Join-Path $folder $name)).Hash.ToLowerInvariant()+'  '+$name
    }
    [IO.File]::WriteAllText((Join-Path $folder 'MANIFEST.sha256'),($lines -join "`n")+"`n")
}
Assert-PackageRejected { & $publisher -DotNetPath 'unavailable-test-runtime' } '*requires explicit -LocalPreview*'
Assert-PackageRejected { & $publisher -LocalPreview -ProductionReady -DotNetPath 'unavailable-test-runtime' } '*cannot claim production readiness*'
$selection = Resolve-FoundationPackageDestination $repositoryRoot
Assert-PackageCheck ($selection.Destination.StartsWith((Join-Path $repositoryRoot 'artifacts/foundation-packages'))) 'Default output escaped its owned root.'
Assert-PackageRejected { Resolve-FoundationPackageDestination $repositoryRoot $fixtureRoot } '*new direct child*'
$null = New-Item -ItemType Directory -Path $selection.Destination -Force
Assert-PackageRejected { Resolve-FoundationPackageDestination $repositoryRoot $selection.Destination } '*already exists*'
$fixture = New-PackageFixture 'valid'
$report = New-FoundationPackageArchive $fixture.Package $fixture.Zip
$verified = Test-FoundationArchive $fixture.Zip
Assert-PackageCheck ($report.Sha256 -ceq (Get-FileHash -LiteralPath $fixture.Zip -Algorithm SHA256).Hash.ToLowerInvariant()) 'ZIP hash is incorrect.'
Assert-PackageCheck ($report.EntryCount -eq $verified.Entries -and $report.HashesVerified -eq $verified.Entries - 1) 'ZIP inventory coverage mismatch.'
$inventory = Get-Content -LiteralPath (Join-Path $fixture.Package 'inventory.json') -Raw | ConvertFrom-Json
Assert-PackageCheck ($inventory.localPreview -and !$inventory.productionReady -and $inventory.files.Count -eq (14 + @(Get-FoundationPublicFiles).Count)) 'Inventory readiness or file count is incorrect.'
$badInner=New-PackageFixture 'stale-inner-manifest'; $null=New-FoundationPackageArchive $badInner.Package $badInner.Zip
$zip=[IO.Compression.ZipFile]::Open($badInner.Zip,[IO.Compression.ZipArchiveMode]::Update)
try {
    $zip.GetEntry('dependency-evidence/MANIFEST.sha256').Delete()
    $writer=[IO.StreamWriter]::new($zip.CreateEntry('dependency-evidence/MANIFEST.sha256').Open())
    try { foreach($name in @('dependency-evidence.json','sbom.spdx.json','THIRD-PARTY-NOTICES.txt')) { $writer.Write((('0'*64)+'  '+$name)+"`n") } } finally { $writer.Dispose() }
    $zip.GetEntry('MANIFEST.sha256').Delete()
    $outerLines=foreach($entry in @($zip.Entries)) {
        $stream=$entry.Open()
        try { [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant()+'  '+$entry.FullName } finally { $stream.Dispose() }
    }
    $writer=[IO.StreamWriter]::new($zip.CreateEntry('MANIFEST.sha256').Open())
    try { $writer.Write(($outerLines -join "`n")+"`n") } finally { $writer.Dispose() }
} finally { $zip.Dispose() }
Assert-PackageRejected { Test-FoundationArchive $badInner.Zip } '*Archive dependency evidence manifest hash mismatch*'
$badSize=Join-Path $fixtureRoot 'false-zip-length.zip'
foreach($name in @('runtime/./ambiguous.dll','runtime//ambiguous.dll','runtime/alias./ambiguous.dll','runtime/CON.dll','runtime/../escaped.dll','../ignored-directory/')){
    $unsafeZip=Join-Path $fixtureRoot ('unsafe-path-'+[Guid]::NewGuid().ToString('N')+'.zip')
    $unsafeArchive=[IO.Compression.ZipFile]::Open($unsafeZip,[IO.Compression.ZipArchiveMode]::Create)
    try{$null=$unsafeArchive.CreateEntry($name)}finally{$unsafeArchive.Dispose()}
    Assert-PackageRejected {Test-FoundationArchive $unsafeZip} '*Unsafe*archive*path*'
}
$directoryPayload=Join-Path $fixtureRoot 'directory-with-payload.zip'
$unsafeArchive=[IO.Compression.ZipFile]::Open($directoryPayload,[IO.Compression.ZipArchiveMode]::Create)
try{$stream=$unsafeArchive.CreateEntry('runtime/').Open();try{$stream.Write([byte[]]@(1))}finally{$stream.Dispose()}}finally{$unsafeArchive.Dispose()}
Assert-PackageRejected {Test-FoundationArchive $directoryPayload} '*Unsafe archive directory*'
$tooManyDirectories=Join-Path $fixtureRoot 'directory-entry-flood.zip'
$unsafeArchive=[IO.Compression.ZipFile]::Open($tooManyDirectories,[IO.Compression.ZipArchiveMode]::Create)
try{foreach($index in 1..5001){$null=$unsafeArchive.CreateEntry('d'+$index+'/')}}finally{$unsafeArchive.Dispose()}
Assert-PackageRejected {Test-FoundationArchive $tooManyDirectories} '*Archive entry limit*'
$zip=[IO.Compression.ZipFile]::Open($badSize,[IO.Compression.ZipArchiveMode]::Create)
try {
    $stream=$zip.CreateEntry('bounded.txt',[IO.Compression.CompressionLevel]::NoCompression).Open()
    try { $stream.Write([Text.Encoding]::ASCII.GetBytes(('x'*8192))) } finally { $stream.Dispose() }
} finally { $zip.Dispose() }
$zipBytes=[IO.File]::ReadAllBytes($badSize); $central=-1
for($index=0;$index -lt $zipBytes.Length-28;$index++) {
    if($zipBytes[$index] -eq 0x50 -and $zipBytes[$index+1] -eq 0x4b -and $zipBytes[$index+2] -eq 1 -and $zipBytes[$index+3] -eq 2) { $central=$index;break }
}
if($central -lt 0){throw 'Synthetic ZIP central directory was not found.'}
[Array]::Copy([BitConverter]::GetBytes([uint32]1),0,$zipBytes,$central+24,4); [IO.File]::WriteAllBytes($badSize,$zipBytes)
$zip=[IO.Compression.ZipFile]::OpenRead($badSize)
try { Assert-PackageRejected { Read-FoundationArchiveEntry $zip.GetEntry('bounded.txt') 64 } '*Archive entry actual size limit exceeded*' }
finally { $zip.Dispose() }
foreach ($name in @(Get-FoundationDependencyFiles)) {
    $missingEvidence=New-PackageFixture ('missing-evidence-' + $script:checks)
    Remove-Item -LiteralPath (Join-Path $missingEvidence.Package $name)
    Assert-PackageRejected { Test-FoundationPackageTree $missingEvidence.Package } '*Required dependency evidence*'
}
foreach ($name in @(Get-FoundationCorePublicFiles)) {
    $missingPublic=New-PackageFixture ('missing-public-' + $script:checks)
    Remove-Item -LiteralPath (Join-Path $missingPublic.Package $name)
    Assert-PackageRejected { Test-FoundationPackageTree $missingPublic.Package } '*Required package content*'
}
$previousGuides=New-PackageFixture 'previous-public-guide-set'
foreach ($name in @(Get-FoundationPublicFiles | Where-Object { $_ -cnotin @(Get-FoundationCorePublicFiles) })) {
    Remove-Item -LiteralPath (Join-Path $previousGuides.Package $name)
}
$previousArchive=New-FoundationPackageArchive $previousGuides.Package $previousGuides.Zip
Assert-PackageCheck ($previousArchive.HashesVerified -eq $previousArchive.EntryCount - 1) 'Existing preview guide sets must remain verifiable.'
$wrongBinding=New-PackageFixture 'wrong-dependency-binding'
[IO.File]::WriteAllText((Join-Path $wrongBinding.Runtime 'SelfService.Foundation.Worker.dll'), 'different runtime bytes')
Assert-PackageRejected { Test-FoundationPackageTree $wrongBinding.Package } '*Dependency evidence runtime binding mismatch*'
$unsupportedClearance=New-PackageFixture 'unsupported-clearance'
$unsafeReport=Get-Content -LiteralPath (Join-Path $unsupportedClearance.Package 'dependency-evidence/dependency-evidence.json') -Raw | ConvertFrom-Json -AsHashtable
$unsafeReport.legalClearance=$true
$unsafeBytes=[Text.Encoding]::UTF8.GetBytes(($unsafeReport | ConvertTo-Json -Depth 8))
$unsafeRuntime=@(Get-FoundationPackageFiles $unsupportedClearance.Runtime | ForEach-Object {
    [pscustomobject]@{Path=$_.Path;Bytes=$_.Length;Sha256=(Get-FileHash -LiteralPath $_.FullName).Hash.ToLowerInvariant()}
})
Assert-PackageRejected { Assert-FoundationDependencyReport $unsafeBytes $unsafeRuntime } '*unsupported clearance claim*'
Assert-PackageRejected { New-FoundationPackageArchive $fixture.Package $fixture.Zip } '*overwrite*'
$sqlite = New-PackageFixture 'legitimate-sqlite-library'
[IO.File]::WriteAllText((Join-Path $sqlite.Runtime 'Microsoft.Data.Sqlite.dll'), 'synthetic managed dependency bytes')
Assert-PackageCheck ((Test-FoundationRuntimeTree $sqlite.Runtime).FileCount -eq 10) 'A SQLite library was confused with a customer database.'
foreach ($name in @('SelfService.Foundation.Host.dll','SelfService.Foundation.Host.deps.json','SelfService.Foundation.Host.runtimeconfig.json',
    'SelfService.Foundation.Worker.dll','SelfService.Foundation.Worker.deps.json','SelfService.Foundation.Worker.runtimeconfig.json',
    'SelfService.Foundation.Owner.dll','SelfService.Foundation.Owner.deps.json','SelfService.Foundation.Owner.runtimeconfig.json')) {
    $missing=New-PackageFixture ('missing-' + $script:checks)
    Remove-Item -LiteralPath (Join-Path $missing.Runtime $name)
    Assert-PackageRejected { Test-FoundationPackageTree $missing.Package } '*Required package content*'
}
$mergePrimary=New-PackageFixture 'merge-primary'; $mergeWorker=New-PackageFixture 'merge-worker'
[IO.File]::WriteAllText((Join-Path $mergeWorker.Runtime 'worker-only.dll'), 'synthetic worker closure')
$merged=Merge-FoundationRuntimeClosures $mergePrimary.Runtime $mergeWorker.Runtime
Assert-PackageCheck ($merged.FileCount -eq 10 -and (Test-Path -LiteralPath (Join-Path $mergePrimary.Runtime 'worker-only.dll'))) 'Worker closure was not copied and revalidated.'
[IO.File]::WriteAllText((Join-Path $mergeWorker.Runtime 'SelfService.Foundation.Host.dll'), 'conflicting bytes')
[IO.File]::WriteAllText((Join-Path $mergeWorker.Runtime 'never-copy.dll'), 'must stay out')
Assert-PackageRejected { Merge-FoundationRuntimeClosures $mergePrimary.Runtime $mergeWorker.Runtime } '*dependency closures conflict*'
Assert-PackageCheck (!(Test-Path -LiteralPath (Join-Path $mergePrimary.Runtime 'never-copy.dll'))) 'Conflict caused a partial merge.'
$stampRoot=Join-Path $fixtureRoot 'publish-source-stamp'
$null=New-Item -ItemType Directory -Path (Join-Path $stampRoot 'src'),(Join-Path $stampRoot 'scripts'),(Join-Path $stampRoot 'config'),(Join-Path $stampRoot 'docs') -Force
& git init --quiet $stampRoot
if ($LASTEXITCODE -ne 0) { throw 'Synthetic source-stamp Git initialization failed.' }
foreach ($file in (@('src/main.cs','scripts/Publish-Foundation.ps1','scripts/New-FoundationDependencyEvidence.ps1','scripts/Test-FoundationPackageNoticePins.ps1') + @(Get-FoundationPublicFiles))) {
    [IO.File]::WriteAllText((Join-Path $stampRoot $file), 'synthetic source input')
}
$before=Get-FoundationPublishSourceStamp $stampRoot
Assert-PackageCheck ($before -ceq (Get-FoundationPublishSourceStamp $stampRoot)) 'Unchanged publish inputs were unstable.'
[IO.File]::WriteAllText((Join-Path $stampRoot 'src/main.cs'), 'changed compiler input')
$changed=Get-FoundationPublishSourceStamp $stampRoot
Assert-PackageCheck ($before -cne $changed) 'Compiler input changes were missed.'
[IO.File]::WriteAllText((Join-Path $stampRoot 'src/new.cs'), 'new implicit compiler input')
Assert-PackageCheck ($changed -cne (Get-FoundationPublishSourceStamp $stampRoot)) 'New implicit compiler inputs were missed.'
$unowned = New-PackageFixture 'unowned' -NoOwner
Assert-PackageRejected { New-FoundationPackageArchive $unowned.Package $unowned.Zip } '*ownership marker*'
$wrongOwner = New-PackageFixture 'wrong-owner'
$owner = Get-Content -LiteralPath (Join-Path $wrongOwner.Parent '.package-owner.json') -Raw | ConvertFrom-Json
$owner.destination = $fixtureRoot
$owner | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $wrongOwner.Parent '.package-owner.json') -Encoding utf8NoBOM
Assert-PackageRejected { New-FoundationPackageArchive $wrongOwner.Package $wrongOwner.Zip } '*ownership mismatch*'
foreach ($name in @('auth.json','customer.sqlite','portal.local.json','key.pem','source.cs','appsettings.Production.json')) {
    $unsafe = New-PackageFixture ('unsafe-' + $script:checks)
    [IO.File]::WriteAllText((Join-Path $unsafe.Runtime $name), '{"synthetic":"fixture"}')
    Assert-PackageRejected { Test-FoundationRuntimeTree $unsafe.Runtime } '*forbidden*'
}
$state = New-PackageFixture 'nested-state'
$null = New-Item -ItemType Directory -Path (Join-Path $state.Runtime '.local/azure-cli') -Force
[IO.File]::WriteAllText((Join-Path $state.Runtime '.local/azure-cli/accessTokens.json'), '{"synthetic":"fixture"}')
Assert-PackageRejected { Test-FoundationRuntimeTree $state.Runtime } '*forbidden*'
$secret = New-PackageFixture 'secret-bytes'
[IO.File]::WriteAllText((Join-Path $secret.Runtime 'synthetic.dll'), 'Bearer abcdefghijklmnopqrstuvwxyz0123456789')
Assert-PackageRejected { Test-FoundationRuntimeTree $secret.Runtime } '*Credential-shaped*'
$jsonSecret = New-PackageFixture 'secret-json'
[IO.File]::WriteAllText((Join-Path $jsonSecret.Runtime 'synthetic.runtimeconfig.json'), '{"accessToken":"synthetic-not-a-real-token"}')
Assert-PackageRejected { Test-FoundationRuntimeTree $jsonSecret.Runtime } '*Credential fields*'
foreach ($key in @('client_secret','access_token','refresh_token','private_key','connection_string','client-secret','api_key','personal_access_token')) {
    $unsafeJson=New-PackageFixture ('secret-key-' + $script:checks)
    [IO.File]::WriteAllText((Join-Path $unsafeJson.Runtime 'synthetic.runtimeconfig.json'), (@{$key='synthetic-not-a-real-credential'} | ConvertTo-Json))
    Assert-PackageRejected { Test-FoundationRuntimeTree $unsafeJson.Runtime } '*Credential fields*'
}
$source = New-PackageFixture 'extra-source'
[IO.File]::WriteAllText((Join-Path $source.Package 'private-customer-export.json'), '{"synthetic":"fixture"}')
Assert-PackageRejected { New-FoundationPackageArchive $source.Package $source.Zip } '*Unreviewed source/runtime state*'
$tampered = New-PackageFixture 'tampered'
$null = New-FoundationPackageArchive $tampered.Package $tampered.Zip
$zip = [IO.Compression.ZipFile]::Open($tampered.Zip,[IO.Compression.ZipArchiveMode]::Update)
try {
    $zip.GetEntry('runtime/SelfService.Foundation.Host.dll').Delete()
    $stream = [IO.StreamWriter]::new($zip.CreateEntry('runtime/SelfService.Foundation.Host.dll').Open())
    try { $stream.Write('tampered fixture bytes') } finally { $stream.Dispose() }
} finally { $zip.Dispose() }
Assert-PackageRejected { Test-FoundationArchive $tampered.Zip } '*hash mismatch*'
$traversal = New-PackageFixture 'traversal'
$null = New-FoundationPackageArchive $traversal.Package $traversal.Zip
$zip = [IO.Compression.ZipFile]::Open($traversal.Zip,[IO.Compression.ZipArchiveMode]::Update)
try { $stream = $zip.CreateEntry('../outside.dll').Open(); $stream.Dispose() } finally { $zip.Dispose() }
Assert-PackageRejected { Test-FoundationArchive $traversal.Zip } '*Unsafe*'
$link = Join-Path $fixtureRoot 'linked-runtime'
if ($IsWindows) { $null = New-Item -ItemType Junction -Path $link -Target $fixture.Runtime }
else { $null = New-Item -ItemType SymbolicLink -Path $link -Target $fixture.Runtime }
Assert-PackageRejected { Test-FoundationRuntimeTree $link } '*Linked*'
$smoke = $null
if ($PublishedPackageDirectory) {
    $published = [IO.Path]::GetFullPath($PublishedPackageDirectory)
    $null = Test-FoundationPackageTree $published
    $parent = [IO.Path]::GetDirectoryName($published)
    $owner = Get-Content -LiteralPath (Join-Path $parent '.package-owner.json') -Raw | ConvertFrom-Json
    $receipt = Get-Content -LiteralPath (Join-Path $parent 'receipt.json') -Raw | ConvertFrom-Json
    Assert-PackageCheck ($owner.destination -ceq $parent -and $owner.localPreview -and $receipt.localPreview -and !$receipt.productionReady) 'Unowned or non-preview runtime smoke is forbidden.'
    $null = Test-FoundationArchive $receipt.archive.Archive
    Assert-PackageCheck ((Get-FileHash -LiteralPath $receipt.archive.Archive -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $receipt.archive.Sha256) 'Published archive differs from its receipt.'
    foreach ($line in Get-Content -LiteralPath (Join-Path $published 'MANIFEST.sha256')) {
        if ($line -cnotmatch '\A([a-f0-9]{64})  (.+)\z') { throw 'Invalid published manifest.' }
        Assert-PackageCheck ((Get-FileHash -LiteralPath (Join-Path $published $Matches[2]) -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $Matches[1]) 'Extracted preview differs from the verified ZIP.'
    }
    $smokeDirectory = Join-Path $fixtureRoot 'owned-host-smoke'
    . (Join-Path $repositoryRoot 'scripts/FoundationTestExecutionEvidence.ps1')
    $null = New-FTEPrivateDirectory $smokeDirectory
    $listener = [Net.Sockets.TcpListener]::new([Net.IPAddress]::Loopback, 0)
    $listener.Start(); $port = $listener.LocalEndpoint.Port; $listener.Stop()
    $rsa = [Security.Cryptography.RSA]::Create(2048)
    $child = $null; $ownership = $null; $client = $null; $stdout = $null; $stderr = $null; $workerChild = $null; $workerOwnership = $null
    $ownerChild = $null; $ownerProcessIdentity = $null
    try {
        $config = Get-Content -LiteralPath (Join-Path $published 'config/foundation-cell.example.json') -Raw | ConvertFrom-Json
        $config.publicKeys = @([ordered]@{keyId='synthetic-package-smoke';pem=$rsa.ExportSubjectPublicKeyInfoPem()})
        $config.databasePath = Join-Path $smokeDirectory 'synthetic-cell.sqlite'
        $config.repositoryRoot = $repositoryRoot
        $config.listenPort = $port; $config.enableEntraDiscovery = $false; $config.mockEndpoint = $null
        $configurationPath = Join-Path $smokeDirectory 'public-synthetic-cell.json'
        $config | ConvertTo-Json -Depth 32 | Set-Content -LiteralPath $configurationPath -Encoding utf8NoBOM
        function New-SmokeToken([string]$Subject, [string]$Role, [string]$Audience) {
            $now = [DateTimeOffset]::UtcNow
            $tenant = $config.bootstrapActorKey.Split(':')[1]
            $caller = @($config.allowedClients | Where-Object {$_.kind -ceq 'User' -and $_.tenantId -ceq $tenant})[0].clientId
            $issuer = @($config.trustedIssuersByTenant.$tenant)[0]
            $claims = [ordered]@{aud=$Audience;iss=$issuer;iat=$now.AddSeconds(-30).ToUnixTimeSeconds();nbf=$now.AddSeconds(-30).ToUnixTimeSeconds();
                exp=$now.AddMinutes(5).ToUnixTimeSeconds();tid=$tenant;oid=$Subject;azp=$caller;ver='2.0';idtyp='user';scp='platform.access';roles=@($Role)}
            $header = [Text.Encoding]::UTF8.GetBytes('{"alg":"RS256","typ":"JWT","kid":"synthetic-package-smoke"}')
            $body = [Text.Encoding]::UTF8.GetBytes(($claims | ConvertTo-Json -Depth 8 -Compress))
            $signingInput = [Convert]::ToBase64String($header).TrimEnd('=').Replace('+','-').Replace('/','_') + '.' +
                [Convert]::ToBase64String($body).TrimEnd('=').Replace('+','-').Replace('/','_')
            $signature = $rsa.SignData([Text.Encoding]::ASCII.GetBytes($signingInput), [Security.Cryptography.HashAlgorithmName]::SHA256, [Security.Cryptography.RSASignaturePadding]::Pkcs1)
            $signingInput + '.' + [Convert]::ToBase64String($signature).TrimEnd('=').Replace('+','-').Replace('/','_')
        }
        $executable = if (Test-Path -LiteralPath $DotNetPath -PathType Leaf) {(Get-Item -LiteralPath $DotNetPath).FullName} else {(Get-Command $DotNetPath -CommandType Application | Select-Object -First 1).Source}
        $start = [Diagnostics.ProcessStartInfo]::new($executable)
        $start.ArgumentList.Add((Join-Path $published 'runtime/SelfService.Foundation.Host.dll'))
        $start.WorkingDirectory = $published; $start.UseShellExecute = $false; $start.CreateNoWindow = $true
        $start.WindowStyle = [Diagnostics.ProcessWindowStyle]::Hidden
        $start.RedirectStandardOutput = $true; $start.RedirectStandardError = $true
        # The fresh child receives no inherited Azure/GitHub/Codex credentials or startup hooks.
        $start.Environment.Clear()
        if ($IsWindows) { $start.Environment['SystemRoot'] = $env:SystemRoot; $start.Environment['WINDIR'] = $env:SystemRoot }
        $start.Environment['PATH'] = [IO.Path]::GetDirectoryName($executable)
        $start.Environment['TEMP'] = $smokeDirectory; $start.Environment['TMP'] = $smokeDirectory
        $start.Environment['PLATFORM_FOUNDATION_CONFIG'] = $configurationPath
        $start.Environment['ASPNETCORE_ENVIRONMENT'] = 'Production'; $start.Environment['DOTNET_ENVIRONMENT'] = 'Production'
        $start.Environment['DOTNET_CLI_TELEMETRY_OPTOUT'] = '1'; $start.Environment['OTEL_SDK_DISABLED'] = 'true'
        $child = [Diagnostics.Process]::Start($start)
        $stdout = $child.StandardOutput.ReadToEndAsync(); $stderr = $child.StandardError.ReadToEndAsync()
        # The executable passed to Start is the expected image; immediate MainModule inspection can race the Windows loader.
        $ownership = [pscustomobject]@{Id=$child.Id;StartedUtc=$child.StartTime.ToUniversalTime();Path=$executable}
        $handler = [Net.Http.HttpClientHandler]::new(); $handler.UseProxy = $false; $handler.AllowAutoRedirect = $false
        $client = [Net.Http.HttpClient]::new($handler); $client.Timeout = [TimeSpan]::FromSeconds(2)
        $origin = "http://127.0.0.1:$port"; $watch = [Diagnostics.Stopwatch]::StartNew(); $health = $null
        while ($watch.Elapsed -lt [TimeSpan]::FromSeconds(20)) {
            if ($child.HasExited) { throw 'Owned packaged host exited before readiness.' }
            try { $response = $client.GetAsync("$origin/health").GetAwaiter().GetResult(); if ($response.IsSuccessStatusCode) {$health=$response.Content.ReadAsStringAsync().GetAwaiter().GetResult() | ConvertFrom-Json;break} } catch { }
            Start-Sleep -Milliseconds 100
        }
        Assert-PackageCheck ($null -ne $health -and $health.ready -and !$health.externalDispatch) 'Packaged host did not become ready with external dispatch disabled.'
        $ownerRequestPath = Join-Path $smokeDirectory 'owner-inspect.json'; $ownerResultPath = Join-Path $smokeDirectory 'inspect.owner-result.json'
        @{operation='inspect';customerId=$config.customerId;cellId=$config.cellId;databasePath=$config.databasePath} |
            ConvertTo-Json | Set-Content -LiteralPath $ownerRequestPath -Encoding utf8NoBOM
        $ownerStart = [Diagnostics.ProcessStartInfo]::new($executable)
        $ownerStart.UseShellExecute=$false; $ownerStart.CreateNoWindow=$true; $ownerStart.WindowStyle=[Diagnostics.ProcessWindowStyle]::Hidden
        $ownerStart.RedirectStandardOutput=$true; $ownerStart.RedirectStandardError=$true; $ownerStart.WorkingDirectory=$smokeDirectory
        foreach($argument in @((Join-Path $published 'runtime/SelfService.Foundation.Owner.dll'),'--request',$ownerRequestPath,'--output',$ownerResultPath)){$ownerStart.ArgumentList.Add($argument)}
        $ownerStart.Environment.Clear()
        if($IsWindows){$ownerStart.Environment['SystemRoot']=$env:SystemRoot;$ownerStart.Environment['WINDIR']=$env:SystemRoot}
        $ownerStart.Environment['PATH']=[IO.Path]::GetDirectoryName($executable);$ownerStart.Environment['TEMP']=$smokeDirectory;$ownerStart.Environment['TMP']=$smokeDirectory
        $ownerStart.Environment['DOTNET_CLI_TELEMETRY_OPTOUT']='1';$ownerStart.Environment['OTEL_SDK_DISABLED']='true'
        $ownerChild=[Diagnostics.Process]::Start($ownerStart)
        $ownerProcessIdentity=@{Id=$ownerChild.Id;StartedUtc=$ownerChild.StartTime.ToUniversalTime();Path=$executable}
        $ownerOutput=$ownerChild.StandardOutput.ReadToEndAsync();$ownerErrors=$ownerChild.StandardError.ReadToEndAsync()
        if(!$ownerChild.WaitForExit(10000)){throw 'Packaged owner inspector exceeded its deadline.'}
        Assert-PackageCheck ($ownerChild.ExitCode -eq 0 -and !$ownerErrors.GetAwaiter().GetResult()) 'Packaged owner inspection failed.'
        $inspection=Get-Content -LiteralPath $ownerResultPath -Raw|ConvertFrom-Json
        Assert-PackageCheck ($inspection.state -ceq 'Succeeded' -and !$inspection.externalExecutionAuthorized -and
            $inspection.result.state -ceq 'CurrentSchema' -and $inspection.result.sqliteQuickCheckVerified -and !$inspection.result.stateMutated -and
            !$ownerOutput.GetAwaiter().GetResult().Contains($config.databasePath)) 'Packaged owner inspection changed state or leaked its private path.'
        $root = "$origin/api/foundation/customers/$($config.customerId)/projects/$($config.projectId)/environments/$($config.environmentId)"
        Assert-PackageCheck ($client.GetAsync("$root/jobs").GetAwaiter().GetResult().StatusCode -eq 401) 'Anonymous private reads were accepted.'
        $administratorToken = New-SmokeToken $config.bootstrapActorKey.Split(':')[2] 'CustomerAdmin' $config.audience
        $viewerSubject = 'eeeeeeee-eeee-eeee-eeee-eeeeeeeeeeee'
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Put,"$root/memberships")
        $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',$administratorToken)
        $body = @{actorKey="user:$($config.bootstrapActorKey.Split(':')[1]):$viewerSubject";roles=@('Viewer');enabled=$true} | ConvertTo-Json -Compress
        $request.Content = [Net.Http.StringContent]::new($body,[Text.Encoding]::UTF8,'application/json')
        Assert-PackageCheck ($client.SendAsync($request).GetAwaiter().GetResult().IsSuccessStatusCode) 'Synthetic scoped viewer setup failed.'
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,"$root/jobs")
        $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',(New-SmokeToken $viewerSubject 'Viewer' $config.audience))
        $response = $client.SendAsync($request).GetAwaiter().GetResult()
        Assert-PackageCheck ($response.IsSuccessStatusCode -and $response.Content.ReadAsStringAsync().GetAwaiter().GetResult() -ceq '[]') 'Signed scoped viewer could not read the empty synthetic queue.'
        $request = [Net.Http.HttpRequestMessage]::new([Net.Http.HttpMethod]::Get,"$root/jobs")
        $request.Headers.Authorization = [Net.Http.Headers.AuthenticationHeaderValue]::new('Bearer',(New-SmokeToken $viewerSubject 'Viewer' 'wrong-audience'))
        Assert-PackageCheck ($client.SendAsync($request).GetAwaiter().GetResult().StatusCode -eq 401) 'Wrong-audience synthetic token was accepted.'
        $workerConfigurationPath = Join-Path $smokeDirectory 'disabled-worker.json'
        $workerReceiptPath = Join-Path $smokeDirectory 'disabled-worker-receipt.json'
        @{ customerId=$config.customerId;cellId=$config.cellId;databasePath=$config.databasePath;runSeconds=1;receiptPath=$workerReceiptPath } |
            ConvertTo-Json | Set-Content -LiteralPath $workerConfigurationPath -Encoding utf8NoBOM
        $preflightStart = [Diagnostics.ProcessStartInfo]::new($ownership.Path)
        $preflightStart.UseShellExecute=$false; $preflightStart.CreateNoWindow=$true; $preflightStart.RedirectStandardOutput=$true; $preflightStart.RedirectStandardError=$true
        foreach ($argument in @((Join-Path $published 'runtime/SelfService.Foundation.Worker.dll'),'--validate-config',$workerConfigurationPath)) { $preflightStart.ArgumentList.Add($argument) }
        $preflightStart.Environment.Clear()
        foreach ($name in @('SystemRoot','WINDIR')) { if ([Environment]::GetEnvironmentVariable($name)) { $preflightStart.Environment[$name]=[Environment]::GetEnvironmentVariable($name) } }
        $preflightStart.Environment['DOTNET_ROOT']=[IO.Path]::GetDirectoryName($ownership.Path)
        $preflightStart.Environment['TEMP']=$smokeDirectory; $preflightStart.Environment['TMP']=$smokeDirectory
        $preflightChild=[Diagnostics.Process]::Start($preflightStart)
        try {
            $preflightOutput=$preflightChild.StandardOutput.ReadToEndAsync(); $preflightError=$preflightChild.StandardError.ReadToEndAsync()
            if (!$preflightChild.WaitForExit(10000)) { throw 'Packaged worker preflight exceeded its deadline.' }
            if ($preflightChild.ExitCode -ne 0 -or $preflightError.GetAwaiter().GetResult().Length -ne 0) { throw 'Packaged worker preflight rejected the synthetic configuration.' }
            $preflightBody=$preflightOutput.GetAwaiter().GetResult()
            if ($preflightBody.Length -gt 8192) { throw 'Packaged worker preflight output exceeded its contract.' }
            $preflight=$preflightBody | ConvertFrom-Json
            Assert-PackageCheck ($preflight.schema -ceq 'platform-studio.foundation-worker-preflight.v1' -and $preflight.receiptPathValidated -and !$preflight.workerStartupPerformed -and !$preflight.externalDispatch) 'Packaged preflight claimed startup or external effects.'
            Assert-PackageCheck ($preflight.configurationSha256 -ceq (Get-FileHash -LiteralPath $workerConfigurationPath -Algorithm SHA256).Hash.ToLowerInvariant()) 'Packaged preflight did not bind the exact config bytes.'
            Assert-PackageCheck (!(Test-Path -LiteralPath $workerReceiptPath) -and !(Test-Path -LiteralPath ($workerReceiptPath+'.reserve'))) 'Read-only packaged preflight created a receipt/reservation.'
        } finally {
            if (!$preflightChild.HasExited) { $preflightChild.Kill(); if (!$preflightChild.WaitForExit(5000)) { throw 'Owned packaged preflight did not stop.' } }
            $preflightChild.Dispose()
        }
        $workerStart = [Diagnostics.ProcessStartInfo]::new($ownership.Path)
        $workerStart.UseShellExecute=$false; $workerStart.CreateNoWindow=$true; $workerStart.RedirectStandardOutput=$true; $workerStart.RedirectStandardError=$true
        $workerStart.ArgumentList.Add((Join-Path $published 'runtime/SelfService.Foundation.Worker.dll'))
        $workerStart.ArgumentList.Add('--config'); $workerStart.ArgumentList.Add($workerConfigurationPath)
        foreach ($key in @($workerStart.Environment.Keys)) {
            if ($key -notmatch '\A(?:SystemRoot|WINDIR|TEMP|TMP|PATH|DOTNET_ROOT|PROCESSOR_ARCHITECTURE|NUMBER_OF_PROCESSORS)\z') { $workerStart.Environment.Remove($key) | Out-Null }
        }
        $workerChild=[Diagnostics.Process]::Start($workerStart)
        $workerOwnership=@{Id=$workerChild.Id;StartedUtc=$workerChild.StartTime.ToUniversalTime();Path=$workerStart.FileName}
        $workerOutput=$workerChild.StandardOutput.ReadToEndAsync(); $workerErrors=$workerChild.StandardError.ReadToEndAsync()
        if (!$workerChild.WaitForExit(10000)) { throw 'Packaged independent worker did not exit within its deadline.' }
        Assert-PackageCheck ($workerChild.ExitCode -eq 0) ("Packaged independent disabled worker failed: " + $workerErrors.GetAwaiter().GetResult())
        $workerReceipt=$workerOutput.GetAwaiter().GetResult().Trim() | ConvertFrom-Json
        Assert-PackageCheck ($workerReceipt.mode -ceq 'Disabled' -and $workerReceipt.processId -eq $workerOwnership.Id -and !$workerErrors.GetAwaiter().GetResult()) 'Packaged worker did not run in disabled mode.'
        $writtenReceipt=Get-Content -LiteralPath $workerReceiptPath -Raw | ConvertFrom-Json
        $receiptAcl=Get-Acl -LiteralPath $workerReceiptPath; $ownerSid=[Security.Principal.WindowsIdentity]::GetCurrent().User.Value
        $receiptOwner=$receiptAcl.GetOwner([Security.Principal.SecurityIdentifier]).Value
        Assert-PackageCheck ($writtenReceipt.processId -eq $workerOwnership.Id -and $receiptAcl.AreAccessRulesProtected -and $receiptOwner -ceq $ownerSid -and @($receiptAcl.Access | Where-Object { $_.IdentityReference.Translate([Security.Principal.SecurityIdentifier]).Value -cne $ownerSid }).Count -eq 0) 'Packaged worker receipt is not private to its owner.'
        $smoke = [ordered]@{performed=$true;loopback=$true;port=$port;syntheticSignedIdentities=$true;privateKeyWritten=$false;externalDispatch=$false;stopped=$false;packagedIndependentWorkerDisabled=$true;packagedOwnerInspection=$true;packagedWorkerReadOnlyPreflight=$true;packagedWorkerPrivateReceipt=$true}
    } finally {
        if ($null -ne $client) { $client.Dispose() }
        if($null -ne $ownerChild){
            if(!$ownerChild.HasExited){
                if($ownerChild.StartTime.ToUniversalTime() -ne $ownerProcessIdentity.StartedUtc -or $ownerChild.MainModule.FileName -cne $ownerProcessIdentity.Path){throw 'Packaged owner process identity changed; stop refused.'}
                $ownerChild.Kill();if(!$ownerChild.WaitForExit(5000)){throw 'Owned owner-inspector process did not stop.'}
            }
            $ownerChild.Dispose()
        }
        if ($null -ne $workerChild) {
            if (!$workerChild.HasExited) {
                $current=[Diagnostics.Process]::GetProcessById($workerOwnership.Id)
                try {
                    if ($current.StartTime.ToUniversalTime() -ne $workerOwnership.StartedUtc -or $current.MainModule.FileName -cne $workerOwnership.Path) { throw 'Packaged worker ownership changed; stop refused.' }
                    $current.Kill(); if (!$current.WaitForExit(5000)) { throw 'Owned packaged worker did not stop.' }
                } finally { $current.Dispose() }
            }
            $workerChild.Dispose()
        }
        if ($null -ne $child) {
            if (!$child.HasExited -and $null -ne $ownership) {
                $current = [Diagnostics.Process]::GetProcessById($ownership.Id)
                try {
                    if ($current.StartTime.ToUniversalTime() -ne $ownership.StartedUtc -or $current.MainModule.FileName -cne $ownership.Path) {
                        throw 'Packaged-host ownership changed; refusal to stop an unverified process.'
                    }
                    $current.Kill(); if (!$current.WaitForExit(10000)) { throw 'Owned packaged host did not stop.' }
                } finally { $current.Dispose() }
            }
            elseif (!$child.HasExited) {
                # Start returned this original owned Process, but metadata collection failed before a receipt existed.
                $child.Kill(); if (!$child.WaitForExit(5000)) { throw 'Owned packaged host did not stop after startup metadata failure.' }
            }
            if ($null -ne $stdout) {[IO.File]::WriteAllText((Join-Path $smokeDirectory 'stdout.txt'),$stdout.GetAwaiter().GetResult())}
            if ($null -ne $stderr) {[IO.File]::WriteAllText((Join-Path $smokeDirectory 'stderr.txt'),$stderr.GetAwaiter().GetResult())}
            if ($null -ne $smoke) {$smoke.stopped=$child.HasExited}
            $child.Dispose()
        }
        $rsa.Dispose()
    }
    Assert-PackageCheck ($null -ne $smoke -and $smoke.stopped) 'Owned packaged smoke host remained running.'
}
[ordered]@{schema='platform-studio.foundation-package-tests.v1';passed=$script:checks;failed=0;localPreview=$true;actualPublishPerformed=$false;
    azureOperationsPerformed=$false;livePipelinesSubmitted=$false;fixtureRoot=$fixtureRoot;packagedHostSmoke=$smoke} |
    ConvertTo-Json | Set-Content -LiteralPath (Join-Path $fixtureRoot 'results.json') -Encoding utf8NoBOM
Write-Output "Foundation package fixture checks passed: $script:checks. $fixtureRoot"
