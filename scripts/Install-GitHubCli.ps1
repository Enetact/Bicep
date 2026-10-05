#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference='Stop'
$root=Split-Path $PSScriptRoot -Parent
$arch=[Runtime.InteropServices.RuntimeInformation]::OSArchitecture.ToString().ToLowerInvariant()
if(!$IsWindows -or $arch -notin @('arm64','x64')){throw 'Supported systems: Windows ARM64 or x64.'}
$assetArch=if($arch -eq 'x64'){'amd64'}else{'arm64'}
$version='2.102.0'
$name="gh_${version}_windows_${assetArch}.zip"
$destination=Join-Path $root '.tools/github-cli'
if(Test-Path -LiteralPath (Join-Path $destination 'bin/gh.exe')){ & (Join-Path $destination 'bin/gh.exe') --version; return }
$download=Join-Path $root '.tools/downloads'
New-Item -ItemType Directory -Force -Path $download|Out-Null
$base="https://github.com/cli/cli/releases/download/v$version"
$archive=Join-Path $download $name
Invoke-WebRequest "$base/$name" -OutFile $archive
$checksums=(Invoke-WebRequest "$base/gh_${version}_checksums.txt").Content
if($checksums -is [byte[]]){$checksums=[Text.Encoding]::UTF8.GetString($checksums)}
$line=@($checksums -split "`n"|Where-Object { $_.Trim() -match ('^[a-fA-F0-9]{64}\s+'+[regex]::Escape($name)+'$') })
if($line.Count -ne 1){throw 'Official GitHub CLI checksum entry missing or ambiguous.'}
$expected=($line[0].Trim() -split '\s+')[0]
if((Get-FileHash -LiteralPath $archive -Algorithm SHA256).Hash -ine $expected){throw 'GitHub CLI checksum mismatch; archive was not extracted.'}
Expand-Archive -LiteralPath $archive -DestinationPath $destination
$exe=Join-Path $destination 'bin/gh.exe'
if(!(Test-Path -LiteralPath $exe)){throw 'GitHub CLI archive layout was unexpected.'}
& $exe --version
if($LASTEXITCODE -ne 0){throw 'GitHub CLI executable validation failed.'}
Write-Host 'GitHub CLI installed in ignored .tools/github-cli. In the portal: Connections > Sign in to GitHub.'
