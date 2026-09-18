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
    else {
        $cliArguments = ConvertTo-AzBicepArguments -Arguments $Arguments
        & az bicep @cliArguments
    }
    if ($LASTEXITCODE -ne 0) { throw 'Bicep compilation failed.' }
}

function ConvertTo-AzBicepArguments {
    param([Parameter(Mandatory)][string[]]$Arguments)
    if ($Arguments.Count -lt 2 -or $Arguments[0] -notin @('build', 'build-params') -or $Arguments[1].StartsWith('-')) {
        throw 'Expected Bicep build/build-params followed by a source file.'
    }
    @($Arguments[0], '--file', $Arguments[1]) + @($Arguments | Select-Object -Skip 2)
}

function Test-FunctionMetadata {
    param([Parameter(Mandatory)][string]$Path)
    $functions = @(Get-Content -LiteralPath $Path -Raw | ConvertFrom-Json)
    $expected = @{
        DispatchUploadedBlob = 'blobTrigger'
        CopyUploadedBlob = 'queueTrigger'
        ReconcileTransfers = 'timerTrigger'
        MonitorTransferPoison = 'timerTrigger'
        AuditTransferLedger = 'timerTrigger'
    }
    if ($functions.Count -ne $expected.Count) { throw 'Package must contain exactly five Functions.' }
    $names = [System.Collections.Generic.HashSet[string]]::new([StringComparer]::Ordinal)
    foreach ($function in $functions) {
        if (!$names.Add($function.name) -or $function.name -cnotin $expected.Keys) {
            throw "Unexpected or duplicate Function: $($function.name)"
        }
        $triggers = @($function.bindings | Where-Object { $_.type -match 'Trigger$' })
        if ($triggers.Count -ne 1 -or $triggers[0].type -cne $expected[$function.name]) {
            throw "Unexpected trigger for Function: $($function.name)"
        }
    }
}

function Resolve-SourceScope {
    param([Parameter(Mandatory)][string]$SourceName, [Parameter(Mandatory)][System.Collections.IDictionary]$ScopePrefixes)
    $matching = @($ScopePrefixes.Keys | Where-Object { $SourceName.StartsWith($_, [StringComparison]::Ordinal) } |
        Sort-Object { $_.Length } -Descending)
    if ($matching.Count -eq 0) { throw "Synthetic source prefix is not mapped: $SourceName" }
    $scope = [string]$ScopePrefixes[$matching[0]]
    if ($scope -cnotmatch '^[a-z0-9][a-z0-9-]{0,62}$') { throw 'Invalid source scope.' }
    return $scope
}

function Assert-NoVulnerablePackages {
    param([Parameter(Mandatory)][System.Collections.IDictionary]$Report)
    if (!$Report.Contains('projects') -or @($Report.projects).Count -eq 0) { throw 'Advisory report has no projects.' }
    if ($Report.Contains('problems') -and @($Report.problems).Count -gt 0) { throw 'Advisory report contains problems.' }
    foreach ($project in $Report.projects) {
        # dotnet list --vulnerable emits only the path when no advisories match.
        if (!$project.Contains('path') -or !$project.path) { throw 'Advisory project path is missing.' }
        if (!$project.Contains('frameworks')) { continue }
        foreach ($framework in $project.frameworks) {
            foreach ($kind in @('topLevelPackages', 'transitivePackages')) {
                if ($framework.Contains($kind)) {
                    foreach ($package in $framework[$kind]) {
                        if ($package.Contains('vulnerabilities') -and @($package.vulnerabilities).Count -gt 0) {
                            throw "Vulnerable dependency: $($package.id)"
                        }
                    }
                }
            }
        }
    }
}

function Assert-FirstBootstrap {
    param([AllowEmptyCollection()][object[]]$Apps, [string]$Workload, [string]$EnvironmentName)
    $prefix = "func-$Workload-$EnvironmentName-"
    if (@($Apps | Where-Object { $_.name.StartsWith($prefix, [StringComparison]::OrdinalIgnoreCase) }).Count -gt 0) {
        throw 'Bootstrap is not permitted for an existing Function instance because it would disable runtime alerts. Use Release.'
    }
}

function Get-ProjectRoot { Split-Path -Parent $PSScriptRoot }

function Export-Templates {
    param(
        [ValidateSet('dev','qa','uat','prod')][string]$EnvironmentName,
        [string]$ParameterPath = '',
        [string]$OutputPath = ''
    )
    $root = Get-ProjectRoot
    $output = if ($OutputPath) { $OutputPath } else { Join-Path $root "artifacts/$EnvironmentName" }
    if (!$ParameterPath) { $ParameterPath = Join-Path $root "environments/$EnvironmentName.bicepparam" }
    New-Item -ItemType Directory -Path $output -Force | Out-Null
    Invoke-Bicep -Arguments @('build', (Join-Path $root 'main.bicep'), '--outfile', (Join-Path $output 'main.json'))
    Invoke-Bicep -Arguments @('build-params', $ParameterPath, '--outfile', (Join-Path $output 'parameters.json'))
    return $output
}
