#requires -Version 7.4
[CmdletBinding()]
param(
    [Parameter(Mandatory)][ValidateSet('private-dns','drift')][string]$Workflow,
    [Parameter(Mandatory)][string]$EvidencePath,
    [Parameter(Mandatory)][string]$OutputPath,
    [string]$ReferencePath=''
)
$ErrorActionPreference='Stop'
$root=Split-Path (Split-Path $PSScriptRoot -Parent) -Parent
$project=Join-Path $root 'src/SelfService.Operations.Cli/SelfService.Operations.Cli.csproj'
& dotnet restore $project --locked-mode
if($LASTEXITCODE -ne 0){throw 'Operations CLI restore failed.'}
& dotnet build $project -c Release --no-restore
if($LASTEXITCODE -ne 0){throw 'Operations CLI build failed.'}
$arguments=@($Workflow,[IO.Path]::GetFullPath($EvidencePath),[IO.Path]::GetFullPath($OutputPath))
if($ReferencePath){$arguments += [IO.Path]::GetFullPath($ReferencePath)}
& dotnet (Join-Path $root 'src/SelfService.Operations.Cli/bin/Release/net10.0/SelfService.Operations.Cli.dll') @arguments
if($LASTEXITCODE -ne 0){throw 'Operations assessment failed. Existing outputs were not replaced.'}
