[CmdletBinding()]
param(
    [Parameter(Mandatory)][string]$SubscriptionId,
    [Parameter(Mandatory)][string]$UploadAccount,
    [string]$UploadContainer = 'incoming',
    [string]$LedgerContainer = 'transfer-ledger',
    [string]$ScopeId = '',
    [ValidatePattern('^[^\x00-\x1f\x7f]{1,800}$')][string]$SourcePrefix = 'smoke/',
    [string]$ScopePrefixesJson = '{"":"default"}',
    [Parameter(Mandatory)][string]$DestinationSubscriptionId,
    [Parameter(Mandatory)][string]$DestinationAccount,
    [Parameter(Mandatory)][string]$DestinationContainer,
    [ValidateRange(60,7200)][int]$TimeoutSeconds = 1800,
    [string]$EvidencePath = ''
)
. "$PSScriptRoot/common.ps1"
$root = Get-ProjectRoot
$id = [guid]::NewGuid().ToString('N')
$scopePrefixes = ConvertFrom-Json -InputObject $ScopePrefixesJson -AsHashtable
$baseName = $SourcePrefix.TrimEnd('/') + "/$id"
$sourceNames = @("$baseName/report.txt", "$baseName/report-copy.txt", "$baseName/report.txt")
$resolvedScope = Resolve-SourceScope -SourceName $sourceNames[0] -ScopePrefixes $scopePrefixes
foreach ($sourceName in $sourceNames) {
    if ((Resolve-SourceScope -SourceName $sourceName -ScopePrefixes $scopePrefixes) -cne $resolvedScope) { throw 'Synthetic names must resolve to one scope.' }
}
if ($ScopeId -and $ScopeId -cne $resolvedScope) { throw 'ScopeId does not match the configured source prefix mapping.' }
$ScopeId = $resolvedScope
$folder = Join-Path $root "artifacts/smoke/$id"
New-Item -ItemType Directory -Path $folder -Force | Out-Null
$source = Join-Path $folder 'source.txt'
$download = Join-Path $folder 'destination.txt'
[IO.File]::WriteAllText($source, "Synthetic external upload smoke test $id")
$hash = (Get-FileHash -LiteralPath $source -Algorithm SHA256).Hash.ToLowerInvariant()
$name = "v1/$ScopeId/$hash/payload"
$requests = @()
# These are ordinary external-style uploads: no custom metadata and no queue send.
# The third write overwrites the first name with identical bytes; source version scanning must recover all revisions.
foreach ($sourceName in $sourceNames) {
    $null = Invoke-Az -Arguments @('storage','blob','upload','--account-name',$UploadAccount,'--container-name',$UploadContainer,'--name',$sourceName,'--file',$source,'--auth-mode','login','--overwrite','true','--subscription',$SubscriptionId,'--output','json')
    $props = Invoke-Az -Arguments @('storage','blob','show','--account-name',$UploadAccount,'--container-name',$UploadContainer,'--name',$sourceName,'--auth-mode','login','--subscription',$SubscriptionId,'--output','json') | ConvertFrom-Json
    $identity = "https://$UploadAccount.blob.core.windows.net/$UploadContainer" + "`n" + $sourceName + "`n" + $props.properties.etag.Trim('"')
    $request = [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData([Text.Encoding]::UTF8.GetBytes($identity))).ToLowerInvariant()
    $requests += [pscustomobject]@{ Id=$request; Source=$sourceName }
}
$requests | ConvertTo-Json | Set-Content -LiteralPath (Join-Path $folder 'requests.json')
if (@($requests.Id | Select-Object -Unique).Count -ne 3) { throw 'Smoke uploads did not produce three distinct source revisions.' }
$deadline = [DateTimeOffset]::UtcNow.AddSeconds($TimeoutSeconds)
$complete = $false
while ([DateTimeOffset]::UtcNow -lt $deadline) {
    $allComplete = $true
    foreach ($request in $requests) {
        $ledgerName = "requests/$ScopeId/$($request.Id).json"
        $exists = Invoke-Az -Arguments @('storage','blob','exists','--account-name',$UploadAccount,'--container-name',$LedgerContainer,'--name',$ledgerName,'--auth-mode','login','--subscription',$SubscriptionId,'--output','json') | ConvertFrom-Json
        if (!$exists.exists) { $allComplete=$false; continue }
        $recordFile = Join-Path $folder "$($request.Id).json"
        $null = Invoke-Az -Arguments @('storage','blob','download','--account-name',$UploadAccount,'--container-name',$LedgerContainer,'--name',$ledgerName,'--file',$recordFile,'--overwrite','true','--auth-mode','login','--subscription',$SubscriptionId,'--output','json')
        $record = Get-Content -LiteralPath $recordFile -Raw | ConvertFrom-Json
        if ($record.Status -eq 'Quarantined') { throw "Smoke request quarantined: $($record.ErrorCode)" }
        if ($record.Status -ne 'Completed') { $allComplete=$false }
        elseif ($record.DestinationName -ne $name) { throw 'Duplicate sources did not converge on expected destination.' }
    }
    if ($allComplete) { $complete=$true; break }
    Start-Sleep -Seconds 10
}
if (!$complete) { throw 'Not all revisions completed. Inspect dispatcher, source-version reconciliation, ledger, and both poison queues.' }
$null = Invoke-Az -Arguments @('storage','blob','download','--account-name',$DestinationAccount,'--container-name',$DestinationContainer,'--name',$name,'--file',$download,'--auth-mode','login','--subscription',$DestinationSubscriptionId,'--output','json')
if ((Get-FileHash -LiteralPath $download).Hash.ToLowerInvariant() -ne $hash) { throw 'Content hashes do not match.' }
foreach ($sourceName in @($requests.Source | Select-Object -Unique)) {
    $exists = Invoke-Az -Arguments @('storage','blob','exists','--account-name',$UploadAccount,'--container-name',$UploadContainer,'--name',$sourceName,'--auth-mode','login','--subscription',$SubscriptionId,'--output','json') | ConvertFrom-Json
    if (!$exists.exists) { throw 'A source blob was unexpectedly removed.' }
}
if ($EvidencePath) {
    New-Item -ItemType Directory -Path (Split-Path -Parent ([IO.Path]::GetFullPath($EvidencePath))) -Force | Out-Null
    @{passed=$true; requestIds=@($requests.Id); sourceNames=@($requests.Source); destination=$name; sha256=$hash; sourceVersionsTested=$true; completedUtc=[DateTimeOffset]::UtcNow.ToString('O'); evidenceFolder=$folder} |
        ConvertTo-Json -Depth 5 | Set-Content -LiteralPath $EvidencePath -Encoding utf8
}
Write-Host "PASS: three external-style uploads/revisions completed to one byte-identical destination; sources retained. Evidence: $folder"
