#requires -Version 7.4
Set-StrictMode -Version Latest
$ErrorActionPreference = 'Stop'

function Get-SuiteRoot { [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent)) }

function Get-SuiteProjects {
    # Deliberately fixed entry points. Recursive discovery would also build generated WorkerExtensions projects.
    @(
        @{ Name='portal'; Project='src/SelfService.Portal/SelfService.Portal.csproj'; Assembly='SelfService.Portal.dll'; Runtime='Studio' }
        @{ Name='blob-transfer'; Project='src/BlobTransfer/BlobTransfer.csproj'; Assembly='BlobTransfer.dll'; Runtime='Full' }
        @{ Name='product-functions'; Project='src/ProductFunctions/ProductFunctions.csproj'; Assembly='ProductFunctions.dll'; Runtime='BuildOnly' }
        @{ Name='transfer-tool'; Project='src/TransferTool/TransferTool.csproj'; Assembly='TransferTool.dll'; Runtime='OnDemand' }
        @{ Name='tagging-tool'; Project='src/SelfService.Tagging.Tool/SelfService.Tagging.Tool.csproj'; Assembly='SelfService.Tagging.Tool.dll'; Runtime='OnDemand' }
        @{ Name='operations-cli'; Project='src/SelfService.Operations.Cli/SelfService.Operations.Cli.csproj'; Assembly='SelfService.Operations.Cli.dll'; Runtime='OnDemand' }
    )
}

function Assert-SuitePath([string]$Path) {
    $root = Get-SuiteRoot
    $full = [IO.Path]::GetFullPath($Path)
    if (!$full.StartsWith($root + [IO.Path]::DirectorySeparatorChar, [StringComparison]::OrdinalIgnoreCase)) { throw 'Suite state must remain inside this checkout.' }
    for ($node=$full; $node.Length -gt $root.Length; $node=[IO.Path]::GetDirectoryName($node)) {
        if ((Test-Path -LiteralPath $node) -and ((Get-Item -LiteralPath $node -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)) { throw 'Suite state cannot traverse a link or junction.' }
    }
    $full
}

function New-SuitePrivateDirectory([string]$Path) {
    $full = Assert-SuitePath $Path
    $null = New-Item -ItemType Directory -Path $full -Force
    $security = [Security.AccessControl.DirectorySecurity]::new()
    $security.SetAccessRuleProtection($true,$false)
    foreach ($sid in @([Security.Principal.WindowsIdentity]::GetCurrent().User, [Security.Principal.SecurityIdentifier]::new('S-1-5-18'))) {
        $security.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,'FullControl','ContainerInherit,ObjectInherit','None','Allow'))
    }
    Set-Acl -LiteralPath $full -AclObject $security
    $full
}

function Get-SuiteSourceHash {
    $root = Get-SuiteRoot
    $paths = @(& git -C $root -c core.quotepath=false ls-files --cached --others --exclude-standard)
    $nativeExit = $LASTEXITCODE
    if ($nativeExit -ne 0 -or !$paths.Count) { throw 'Suite builds require the source Git checkout.' }
    $text = [Text.StringBuilder]::new()
    foreach ($relative in $paths | Sort-Object -Unique) {
        if ([IO.Path]::IsPathFullyQualified($relative) -or $relative -match '(^|/)\.\.(/|$)|[\r\n]') { throw 'Invalid suite source path.' }
        $path = Assert-SuitePath (Join-Path $root $relative)
        $digest = if (Test-Path -LiteralPath $path -PathType Leaf) { (Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash } else { 'MISSING' }
        [void]$text.Append($relative).Append([char]0).Append($digest).Append("`n")
    }
    [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($text.ToString()))).ToLowerInvariant()
}

function Get-SuiteCheckpoint {
    $root = Get-SuiteRoot
    $port = 5087
    $settings = Join-Path $root 'src/SelfService.Portal/portal.local.json'
    if (Test-Path -LiteralPath $settings) {
        $configuration = Get-Content -LiteralPath $settings -Raw | ConvertFrom-Json
        if ($configuration.Portal.Port) { $port = [int]$configuration.Portal.Port }
    }
    if ($env:STUDIO_Portal__Port) { $port = [int]$env:STUDIO_Portal__Port }
    if ($port -lt 1024 -or $port -gt 65535) { throw 'Invalid portal port.' }
    Assert-SuitePath (Join-Path $root ".local/portal/credential-cache/$port/restart.bin")
}

function Get-SuiteStatus([string]$Profile='Studio') {
    $portal = & (Join-Path $PSScriptRoot 'Get-Portal.ps1')
    $telemetry = & (Join-Path $PSScriptRoot 'Get-Observability.ps1')
    $local = & (Join-Path $PSScriptRoot 'Get-Local.ps1')
    $localRegistered = Test-Path -LiteralPath (Assert-SuitePath (Join-Path (Get-SuiteRoot) '.local/run.json')) -PathType Leaf
    $healthy = $portal.State -eq 'Ready' -and $portal.TelemetryConfigured -and $telemetry.State -eq 'Running' -and $telemetry.Healthy
    if ($Profile -eq 'Full') { $healthy = $healthy -and $local.State -eq 'Ready' }
    $states = @($portal.State,$telemetry.State)
    if ($Profile -eq 'Full' -or $localRegistered) { $states += $local.State }
    $blocked = $states | Where-Object { $_ -in @('Unknown','OwnershipMismatch','UnownedListener') }
    $state = if ($blocked) { 'Blocked' } elseif ($healthy) { 'Ready' } elseif ($portal.State -in @('Stopped','StaleReceipt') -and $telemetry.State -in @('Stopped','StaleReceipt') -and (!$localRegistered -or $local.State -in @('Stopped','StaleReceipt'))) { 'Stopped' } else { 'Partial' }
    [pscustomobject]@{ State=$state; Profile=$Profile; Healthy=[bool]$healthy; Portal=$portal; Telemetry=$telemetry; LocalRuntime=$local; LocalRegistered=[bool]$localRegistered;
        CredentialCheckpointPending=(Test-Path -LiteralPath (Get-SuiteCheckpoint) -PathType Leaf);
        Components=@(Get-SuiteProjects | ForEach-Object { [pscustomobject]@{ Name=$_.Name; Project=$_.Project; Runtime=$_.Runtime } });
        Providers='Browser-owned MCP and Codex helpers start on demand; sign-in and tool readiness are separate checks.' }
}

function Assert-SuiteControllable($Status) {
    $components = @($Status.Portal,$Status.Telemetry)
    if ($Status.Profile -eq 'Full' -or $Status.LocalRegistered) { $components += $Status.LocalRuntime }
    foreach ($component in $components) {
        if ($component.State -in @('Unknown','OwnershipMismatch','UnownedListener')) { throw "Suite lifecycle blocked by $($component.State). No unknown process or listener may be adopted." }
    }
}

function Get-SuiteTelemetryEnvironment {
    $previous = @{}
    foreach ($name in @('OTEL_EXPORTER_OTLP_ENDPOINT','OTEL_EXPORTER_OTLP_PROTOCOL','OTEL_EXPORTER_OTLP_HEADERS','PATH','DOTNET_ROOT','DOTNET_CLI_TELEMETRY_OPTOUT','FUNCTIONS_CORE_TOOLS_TELEMETRY_OPTOUT','DOTNET_SKIP_FIRST_TIME_EXPERIENCE','DOTNET_GENERATE_ASPNET_CERTIFICATE')) {
        $previous[$name] = [Environment]::GetEnvironmentVariable($name,'Process')
    }
    $previous
}

function Restore-SuiteTelemetryEnvironment([hashtable]$Previous) {
    foreach ($name in $Previous.Keys) {
        # PowerShell can bind a null string as empty; delete the entry to restore absence exactly.
        if ($null -eq $Previous[$name]) {
            if (Test-Path -LiteralPath "Env:\$name") { Remove-Item -LiteralPath "Env:\$name" -ErrorAction Stop }
        }
        else { [Environment]::SetEnvironmentVariable($name,$Previous[$name],'Process') }
    }
}
