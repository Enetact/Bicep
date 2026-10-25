#requires -Version 7.4
[CmdletBinding()]
param([switch]$VerifyOnly)
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
if(![OperatingSystem]::IsWindows()){throw 'This portal tooling profile supports Windows ARM64/x64.'}
$architecture=[Runtime.InteropServices.RuntimeInformation]::ProcessArchitecture.ToString().ToLowerInvariant()
$hashes=@{arm64='613a0afb0ffed4dca106e6466688d780b7db0eac7cf3576f62ae9eab11554b05';x64='398af294cf16ac4becdbd008a77d148d0e8e91492309a6b60ec911b7e1cab082'}
if(!$hashes.ContainsKey($architecture)){throw 'Use native Windows ARM64 or x64 PowerShell.'}
$destination=Join-Path $root '.tools/bicep/0.48.1'
$compiler=Join-Path $destination 'bicep.exe'
if(!(Test-Path -LiteralPath $compiler -PathType Leaf)){
    if($VerifyOnly){throw 'Run scripts/Setup-AvmTooling.ps1 to prepare the pinned checkout-local compiler.'}
    New-Item -ItemType Directory -Path $destination -Force|Out-Null
    $download=Join-Path $destination ('download-'+[guid]::NewGuid().ToString('N')+'.exe')
    Invoke-WebRequest "https://github.com/Azure/bicep/releases/download/v0.48.1/bicep-win-$architecture.exe" -OutFile $download
    if((Get-FileHash -LiteralPath $download -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hashes[$architecture]){throw 'Official Bicep release SHA-256 differs from the reviewed pin; download preserved for review.'}
    Move-Item -LiteralPath $download -Destination $compiler
}
if((Get-FileHash -LiteralPath $compiler -Algorithm SHA256).Hash.ToLowerInvariant() -cne $hashes[$architecture]){throw 'Existing Bicep compiler differs from the reviewed release. Review it before replacement.'}
$previousExtraction=$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR
try{
    $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR=Join-Path $root '.local/avm-bicep-bundle'
    New-Item -ItemType Directory -Path $env:DOTNET_BUNDLE_EXTRACT_BASE_DIR -Force|Out-Null
    & $compiler --version
    if($LASTEXITCODE -ne 0){throw 'Pinned Bicep compiler could not run.'}
    & (Join-Path $PSScriptRoot 'Verify-AvmSources.ps1')
}finally{$env:DOTNET_BUNDLE_EXTRACT_BASE_DIR=$previousExtraction}
Write-Host 'PASS: checkout-local Bicep 0.48.1 and pinned AVM sources verified. No global installation, sign-in or deployment.'
