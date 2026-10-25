#requires -Version 7.4
[CmdletBinding()]
param([string]$RepositoryRoot='', [switch]$Refetch)
$ErrorActionPreference='Stop'
function Read-FoundationPinnedNoticeBody {
    param([Parameter(Mandatory)][IO.Stream]$Stream,[Parameter(Mandatory)][Threading.CancellationToken]$CancellationToken,[int]$MaximumBytes=524288)
    $memory=[IO.MemoryStream]::new()
    try{
        $buffer=[byte[]]::new(8192)
        while(($n=$Stream.ReadAsync($buffer,0,$buffer.Length,$CancellationToken).GetAwaiter().GetResult()) -gt 0){
            $CancellationToken.ThrowIfCancellationRequested()
            if($memory.Length+$n -gt $MaximumBytes){throw 'Upstream notice exceeds the byte cap.'};$memory.Write($buffer,0,$n)
        }
        $CancellationToken.ThrowIfCancellationRequested();return ,$memory.ToArray()
    }finally{$memory.Dispose()}
}
function Get-FoundationPinnedNoticeRequests {
    param([Parameter(Mandatory)]$ValidatedSource)
    $bindings=@($ValidatedSource.packages.Values|ForEach-Object {$_.binding})
    $files=@{}
    foreach($package in $bindings){foreach($file in $package.files){$files[$file.path]=$file}}
    [pscustomobject]@{bindings=$bindings;files=$files}
}
if(!$RepositoryRoot){$RepositoryRoot=[IO.Path]::GetFullPath((Join-Path $PSScriptRoot '..'))}
$generator=Join-Path $RepositoryRoot 'scripts/New-FoundationDependencyEvidence.ps1'
$tokens=$null;$errors=$null;$ast=[Management.Automation.Language.Parser]::ParseFile($generator,[ref]$tokens,[ref]$errors)
if($errors){throw($errors|Out-String)}
# Definitions are trusted checkout code. No publishing, package restore/build or package code runs.
foreach($function in $ast.FindAll({param($n)$n -is [Management.Automation.Language.FunctionDefinitionAst]},$true)){. ([scriptblock]::Create($function.Extent.Text))}
$source=Read-FDNoticeSupplements $RepositoryRoot
if(!$source.manifestSha256){throw 'Pinned notice source is missing.'}
$requests=Get-FoundationPinnedNoticeRequests $source
$sources=$requests.files
$output=$null
if($Refetch){
    $output=Join-Path $RepositoryRoot ('artifacts/foundation-notice-checks/'+[Guid]::NewGuid().ToString('N'))
    $null=Assert-FDPlainPath $output;$null=New-Item -ItemType Directory -Path $output
    $handler=[Net.Http.HttpClientHandler]::new();$handler.AllowAutoRedirect=$false;$handler.UseCookies=$false;$handler.UseDefaultCredentials=$false
    $client=[Net.Http.HttpClient]::new($handler);$client.Timeout=[TimeSpan]::FromSeconds(30)
    try{
        foreach($path in @($sources.Keys|Sort-Object -CaseSensitive)){
            $file=$sources[$path]
            # This checks the reviewed source manifest's repository/commit URL. Actual nuspec binding belongs to the main generator.
            $deadline=[Threading.CancellationTokenSource]::new([TimeSpan]::FromSeconds(30));$response=$null
            try{
                $response=$client.GetAsync($file.sourceUrl,[Net.Http.HttpCompletionOption]::ResponseHeadersRead,$deadline.Token).GetAwaiter().GetResult()
                if([int]$response.StatusCode -ne 200){throw 'Pinned upstream file unavailable; no branch fallback is permitted.'}
                $stream=$response.Content.ReadAsStreamAsync($deadline.Token).GetAwaiter().GetResult()
                try{
                    $bytes=Read-FoundationPinnedNoticeBody $stream $deadline.Token
                }finally{$stream.Dispose()}
                $sha=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
                if($sha -cne $file.sha256 -or (Get-FDGitBlobHash $bytes) -cne $file.gitBlobSha -or $bytes.Length -ne $file.bytes){throw 'Pinned upstream notice bytes changed.'}
                $target=Join-Path $output $path;$null=New-Item -ItemType Directory -Path (Split-Path $target -Parent) -Force
                $fileStream=[IO.File]::Open($target,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::None)
                try{$fileStream.Write($bytes)}finally{$fileStream.Dispose()}
            }finally{if($null -ne $response){$response.Dispose()};$deadline.Dispose()}
        }
    }finally{$client.Dispose();$handler.Dispose()}
}
[pscustomobject]@{schema='platform-studio.foundation-package-notice-check.v1';manifestSha256=$source.manifestSha256;
    packageBindings=$source.packages.Count;pinnedPackages=@($requests.bindings|Where-Object status -CEQ 'Pinned').Count;
    missingRepositoryCommits=@($requests.bindings|Where-Object status -CEQ 'MissingRepositoryCommit').Count;
    allPackageTextResolved=(@($requests.bindings|Where-Object status -CNE 'Pinned').Count -eq 0);uniqueFiles=$sources.Count;refetched=[bool]$Refetch;outputDirectory=$output;
    actualPackageBindingsVerified=$false;
    legalClearance=$false;distributionRightsGranted=$false;productionReady=$false}
