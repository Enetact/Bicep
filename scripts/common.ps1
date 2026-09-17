Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Invoke-Az {
    param([Parameter(Mandatory)][string[]]$Arguments)
    $result = & az @Arguments --only-show-errors
    if ($LASTEXITCODE -ne 0) { throw "Azure CLI failed: $($Arguments[0..([Math]::Min(2, $Arguments.Length - 1))] -join ' ')" }
    return $result
}

function Invoke-Bicep {
    param([Parameter(Mandatory)][string[]]$Arguments)
    if (Get-Command bicep -ErrorAction SilentlyContinue) { & bicep @Arguments }
    else { & az bicep @Arguments }
    if ($LASTEXITCODE -ne 0) { throw 'Bicep compilation failed.' }
}

function Get-ProjectRoot { Split-Path -Parent $PSScriptRoot }

function Export-Templates {
    param([ValidateSet('dev','qa','uat','prod')][string]$EnvironmentName)
    $root = Get-ProjectRoot
    $output = Join-Path $root "artifacts/$EnvironmentName"
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Invoke-Bicep -Arguments @('build', (Join-Path $root 'main.bicep'), '--outfile', (Join-Path $output 'main.json'))
    Invoke-Bicep -Arguments @('build-params', (Join-Path $root "environments/$EnvironmentName.bicepparam"), '--outfile', (Join-Path $output 'parameters.json'))
    return $output
}
