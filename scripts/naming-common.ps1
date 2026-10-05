# Shared adapter: keep naming algorithms in scripts/naming/core.mjs.
function Resolve-ResourceNames {
    [CmdletBinding()]
    param([Parameter(Mandatory)][Collections.IDictionary]$Request)
    Invoke-NamingContract -Request $Request -EntryPoint 'cli.mjs'
}

function Test-StackInstanceRegistry {
    [CmdletBinding()]
    param([Parameter(Mandatory)][Collections.IDictionary]$Registry, [Collections.IDictionary]$PreviousRegistry)
    $envelope=@{registry=$Registry}
    if($PSBoundParameters.ContainsKey('PreviousRegistry')){
        if($null -eq $PreviousRegistry){throw 'PreviousRegistry cannot be null when supplied.'}
        $envelope.previousRegistry=$PreviousRegistry
    }
    Invoke-NamingContract -Request $envelope -EntryPoint 'instances-cli.mjs'
}

function Invoke-NamingContract {
    [CmdletBinding()]
    param([Parameter(Mandatory)][Collections.IDictionary]$Request,
          [Parameter(Mandatory)][ValidateSet('cli.mjs','instances-cli.mjs')][string]$EntryPoint)
    $node=(Get-Command node -ErrorAction Stop).Source
    $start=[Diagnostics.ProcessStartInfo]::new($node)
    $start.ArgumentList.Add((Join-Path $PSScriptRoot "naming/$EntryPoint"))
    $start.UseShellExecute=$false
    $start.CreateNoWindow=$true
    $start.RedirectStandardInput=$true
    $start.RedirectStandardOutput=$true
    $start.RedirectStandardError=$true
    $start.StandardInputEncoding=[Text.UTF8Encoding]::new($false)
    $start.StandardOutputEncoding=[Text.UTF8Encoding]::new($false)
    $payload=ConvertTo-Json -InputObject $Request -Depth 30 -Compress
    if([Text.Encoding]::UTF8.GetByteCount($payload) -gt 1MB){throw 'Naming request exceeds 1 MiB.'}
    $process=[Diagnostics.Process]::new();$process.StartInfo=$start
    try {
        if(!$process.Start()){throw 'Naming process could not start.'}
        $stdout=$process.StandardOutput.ReadToEndAsync();$stderr=$process.StandardError.ReadToEndAsync()
        $process.StandardInput.Write($payload);$process.StandardInput.Close()
        if(!$process.WaitForExit(15000)){$process.Kill($true);throw 'Naming resolver timed out.'}
        $result=$stdout.GetAwaiter().GetResult();$errorText=$stderr.GetAwaiter().GetResult()
        if($process.ExitCode -ne 0){throw $errorText.Trim()}
        if($result.Length -gt 8MB){throw 'Naming response exceeds 8 MiB.'}
        return ($result|ConvertFrom-Json -AsHashtable -Depth 30)
    } finally {$process.Dispose()}
}
