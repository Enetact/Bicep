#requires -Version 7.4
[CmdletBinding()]
param()
$ErrorActionPreference = 'Stop'
Set-StrictMode -Version Latest
if (!$IsWindows) { throw 'Native Portal private-directory tests require Windows.' }
$repository = [IO.Path]::GetFullPath((Split-Path $PSScriptRoot -Parent))
$user = [Security.Principal.WindowsIdentity]::GetCurrent().User
$system = [Security.Principal.SecurityIdentifier]::new('S-1-5-18')
$sections = [Security.AccessControl.AccessControlSections]'Access,Owner,Group'
$family = Join-Path $repository 'artifacts/portal-private-directory-tests'
$null = [IO.Directory]::CreateDirectory($family)
$evidence = Join-Path $family ([guid]::NewGuid().ToString('N'))
$private = [Security.AccessControl.DirectorySecurity]::new()
$private.SetAccessRuleProtection($true, $false)
foreach ($identity in @($user, $system)) {
    $private.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($identity, 'FullControl', 'ContainerInherit,ObjectInherit', 'None', 'Allow'))
}
[IO.FileSystemAclExtensions]::Create([IO.DirectoryInfo]::new($evidence), $private)
$cases = [Collections.Generic.List[string]]::new()
$sourcePins = [Collections.Generic.List[object]]::new()
$log = Join-Path $evidence 'native-test.log'

function Assert-PrivateTest([bool]$Condition, [string]$Message) {
    if (!$Condition) { throw $Message }
}
function Complete-PrivateTest([string]$Name) {
    $script:cases.Add($Name)
    [IO.File]::AppendAllText($script:log, "PASS: $Name`n", [Text.UTF8Encoding]::new($false))
}
function Read-PrivateSecurity([string]$Path) {
    if ([IO.Directory]::Exists($Path)) { return [IO.FileSystemAclExtensions]::GetAccessControl([IO.DirectoryInfo]::new($Path), $script:sections) }
    [IO.FileSystemAclExtensions]::GetAccessControl([IO.FileInfo]::new($Path), $script:sections)
}
function Assert-PrivateSecurity([string]$Path, [bool]$ProtectedDirectory) {
    $acl = Read-PrivateSecurity $Path
    Assert-PrivateTest ($acl.GetOwner([Security.Principal.SecurityIdentifier]) -eq $script:user) 'Fixture owner changed.'
    if ($ProtectedDirectory) { Assert-PrivateTest $acl.AreAccessRulesProtected 'Private directory still inherits access.' }
    $rules = @($acl.GetAccessRules($true, $true, [Security.Principal.SecurityIdentifier]))
    Assert-PrivateTest ($rules.Count -eq 2) 'Private entry has unexpected access rules.'
    foreach ($identity in @($script:user, $script:system)) {
        $matches = @($rules | Where-Object { $_.IdentityReference -eq $identity })
        Assert-PrivateTest ($matches.Count -eq 1) 'Required private identity is absent or ambiguous.'
        $rule = $matches[0]
        Assert-PrivateTest ($rule.AccessControlType -eq 'Allow' -and $rule.FileSystemRights -eq [Security.AccessControl.FileSystemRights]::FullControl) 'Private identity lacks FullControl.'
        Assert-PrivateTest ($rule.PropagationFlags -eq [Security.AccessControl.PropagationFlags]::None) 'Private permission is inherit-only.'
        if ($ProtectedDirectory) {
            Assert-PrivateTest (!$rule.IsInherited -and $rule.InheritanceFlags -eq [Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit') 'Private directory rules do not protect descendants.'
        }
        else { Assert-PrivateTest $rule.IsInherited 'New child did not inherit private access.' }
    }
}
function Get-PrivateFunctions([string]$Relative, [string[]]$Names) {
    $path = Join-Path $script:repository $Relative
    $tokens = $null; $errors = $null
    $ast = [Management.Automation.Language.Parser]::ParseFile($path, [ref]$tokens, [ref]$errors)
    Assert-PrivateTest ($errors.Count -eq 0) 'A production private-writer source did not parse.'
    $script:sourcePins.Add([ordered]@{ path=$Relative; sha256=(Get-FileHash -LiteralPath $path -Algorithm SHA256).Hash.ToLowerInvariant() })
    $definitions = foreach ($name in $Names) {
        $matches = @($ast.FindAll({ param($node) $node -is [Management.Automation.Language.FunctionDefinitionAst] -and $node.Name -eq $name }, $true))
        Assert-PrivateTest ($matches.Count -eq 1) 'Production private-writer function was not unique.'
        $matches[0].Extent.Text
    }
    $definitions -join "`n"
}
function Invoke-PrivateWriter($Writer, [string]$Path) {
    & $Writer.Module { param($Name, $Target) & $Name $Target } $Writer.Name $Path | Out-Null
}
function Assert-PrivateRefusal([scriptblock]$Action) {
    $failure = $null
    try { & $Action | Out-Null } catch { $failure = $_ }
    Assert-PrivateTest ($null -ne $failure) 'Unsafe fixture path was accepted.'
}

try {
    $writers = @(
        @{ Key='suite'; Name='New-SuitePrivateDirectory'; Source='scripts/Suite.Common.ps1'; Functions=@('Assert-SuitePath','New-SuitePrivateDirectory') },
        @{ Key='observability'; Name='Protect-ObservabilityDirectory'; Source='scripts/Observability.Common.ps1'; Functions=@('Assert-ObservabilityPath','Protect-ObservabilityDirectory') },
        @{ Key='fresh-restart'; Name='Set-FreshPrivateDirectory'; Source='scripts/Restart-PortalFresh.ps1'; Functions=@('Assert-FreshPath','Set-FreshPrivateDirectory') }
    )
    foreach ($writer in $writers) {
        $fixture = Join-Path $evidence $writer.Key
        $null = [IO.Directory]::CreateDirectory($fixture)
        $literalRoot = $fixture.Replace("'", "''")
        $prelude = switch ($writer.Key) {
            'suite' { "function Get-SuiteRoot { '$literalRoot' }" }
            'observability' { "function Get-ObservabilityPaths { @{ Root = '$literalRoot' } }" }
            default { "`$script:root = '$literalRoot'" }
        }
        $definitions = Get-PrivateFunctions $writer.Source $writer.Functions
        $writer.Module = New-Module -Name ('PortalPrivateFixture'+[guid]::NewGuid().ToString('N')) -ScriptBlock ([scriptblock]::Create("Set-StrictMode -Version Latest`n`$ErrorActionPreference='Stop'`n$prelude`n$definitions"))
        $path = Join-Path $fixture 'private'
        Assert-PrivateTest (!(Test-Path -LiteralPath $path)) 'Fresh fixture unexpectedly exists.'
        Invoke-PrivateWriter $writer $path
        Assert-PrivateSecurity $path $true
        Complete-PrivateTest ($writer.Key+': fresh private directory under private inherited parent')

        $child = Join-Path $path 'child'
        $null = [IO.Directory]::CreateDirectory($child)
        $file = Join-Path $child 'preserved.txt'
        [IO.File]::WriteAllText($file, 'disposable native ACL fixture; no credentials', [Text.UTF8Encoding]::new($false))
        Assert-PrivateSecurity $child $false
        Assert-PrivateSecurity $file $false
        Complete-PrivateTest ($writer.Key+': actual descendant inheritance')
        $before = @(foreach ($entry in @($path,$child,$file)) {
            $security = Read-PrivateSecurity $entry
            [ordered]@{ owner=$security.GetOwner([Security.Principal.SecurityIdentifier]).Value; group=$security.GetGroup([Security.Principal.SecurityIdentifier]).Value; access=$security.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access) }
        }) | ConvertTo-Json -Depth 4 -Compress
        $content = (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash
        Invoke-PrivateWriter $writer $path
        Invoke-PrivateWriter $writer $path
        $after = @(foreach ($entry in @($path,$child,$file)) {
            $security = Read-PrivateSecurity $entry
            [ordered]@{ owner=$security.GetOwner([Security.Principal.SecurityIdentifier]).Value; group=$security.GetGroup([Security.Principal.SecurityIdentifier]).Value; access=$security.GetSecurityDescriptorSddlForm([Security.AccessControl.AccessControlSections]::Access) }
        }) | ConvertTo-Json -Depth 4 -Compress
        Assert-PrivateTest ($before -ceq $after -and (Get-FileHash -LiteralPath $file -Algorithm SHA256).Hash -ceq $content) 'Repeated protection changed content, owner, group or private access.'
        Assert-PrivateSecurity $path $true
        Complete-PrivateTest ($writer.Key+': already-protected repeated calls preserve content and descriptors')

        $acl = Read-PrivateSecurity $path
        $acl.SetAccessRuleProtection($false,$true)
        [IO.FileSystemAclExtensions]::SetAccessControl([IO.DirectoryInfo]::new($path),$acl)
        Invoke-PrivateWriter $writer $path
        Assert-PrivateSecurity $path $true
        Complete-PrivateTest ($writer.Key+': inherited owner-and-SYSTEM-only access becomes protected')

        $escape = Join-Path $evidence ($writer.Key+'-outside')
        Assert-PrivateRefusal { Invoke-PrivateWriter $writer $escape }
        Assert-PrivateTest (!(Test-Path -LiteralPath $escape)) 'Escape refusal created a destination.'
        Complete-PrivateTest ($writer.Key+': outside-root refusal before creation')

        $target = Join-Path $evidence ($writer.Key+'-junction-target')
        $null = [IO.Directory]::CreateDirectory($target)
        $link = Join-Path $fixture 'junction'
        $null = New-Item -ItemType Junction -Path $link -Target $target
        try {
            Assert-PrivateRefusal { Invoke-PrivateWriter $writer (Join-Path $link 'unexpected') }
            Assert-PrivateTest (!(Test-Path -LiteralPath (Join-Path $target 'unexpected'))) 'Linked-path refusal changed its target.'
            Complete-PrivateTest ($writer.Key+': actual no-admin junction refusal before creation')
        }
        finally {
            # Delete only the fixture junction itself; never recurse into its target.
            Assert-PrivateTest ((Get-Item -LiteralPath $link -Force).Attributes.HasFlag([IO.FileAttributes]::ReparsePoint)) 'Fixture junction identity changed.'
            [IO.Directory]::Delete($link)
        }
    }
    foreach ($pin in $sourcePins) {
        Assert-PrivateTest ((Get-FileHash -LiteralPath (Join-Path $repository $pin.path) -Algorithm SHA256).Hash.ToLowerInvariant() -ceq $pin.sha256) 'Production writer changed during native qualification.'
    }
    $receipt = [ordered]@{ state='Passed'; passed=$cases.Count; failed=0; cases=@($cases); productionSources=@($sourcePins); powerShell=$PSVersionTable.PSVersion.ToString(); generatedUtc=[DateTimeOffset]::UtcNow.ToString('O'); scope='Actual AST-extracted production private-directory writers against native disposable NTFS directories; no service, process, sign-in cache, SACL, elevation or network operations.' }
    $receipt | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'receipt.json') -Encoding utf8NoBOM
    Write-Host "PASS: $($cases.Count) native Portal private-directory scenarios. Evidence: $evidence/receipt.json"
}
catch {
    [IO.File]::AppendAllText($log, "FAIL: $($_.Exception.GetType().Name): $($_.Exception.Message)`n", [Text.UTF8Encoding]::new($false))
    [ordered]@{ state='Failed'; passed=$cases.Count; failed=1; cases=@($cases); productionSources=@($sourcePins); generatedUtc=[DateTimeOffset]::UtcNow.ToString('O'); failure=$_.Exception.Message } | ConvertTo-Json -Depth 6 | Set-Content -LiteralPath (Join-Path $evidence 'receipt.json') -Encoding utf8NoBOM
    throw
}
