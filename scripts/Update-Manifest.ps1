[CmdletBinding()]
param([switch]$Check,[string]$RepositoryRoot='')
. "$PSScriptRoot/common.ps1"
$root=if($RepositoryRoot){[IO.Path]::GetFullPath($RepositoryRoot)}else{Get-ProjectRoot}
# Git identifies text and the effective checkout EOL rule. Hash LF checkout bytes
# for LF-governed text, even if an editor left CRLF/mixed bytes locally. Binary
# files and files with other EOL rules retain exact byte hashing.
$entries=@(& git -C $root -c core.quotepath=false ls-files --cached --others --exclude-standard --eol)
if($LASTEXITCODE -ne 0){throw 'Manifest generation requires a Git checkout.'}
$hashes=[Collections.Generic.SortedDictionary[string,string]]::new([StringComparer]::Ordinal)
foreach($entry in $entries){
    if($entry -notmatch '^(?<metadata>.*?)\t(?<path>.+)$'){throw "Cannot parse Git source entry: $entry"}
    $name=$Matches.path;$metadata=$Matches.metadata
    $file=Join-Path $root $name
    if($name -eq 'MANIFEST.sha256' -or !(Test-Path -LiteralPath $file -PathType Leaf)){continue}
    $bytes=[IO.File]::ReadAllBytes($file)
    if($metadata -match '\bw/(crlf|mixed)\b' -and $metadata -match '\beol=lf\b' -and $metadata -notmatch '\battr/-text\b'){
        # Latin1 preserves every byte; only the CRLF byte pair is replaced.
        $bytes=[Text.Encoding]::Latin1.GetBytes([Text.Encoding]::Latin1.GetString($bytes).Replace("`r`n","`n"))
    }
    $hashes[$name]=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($bytes)).ToLowerInvariant()
}
$lines=@(foreach($name in $hashes.Keys){"$($hashes[$name])  $name"})
$manifest=Join-Path $root MANIFEST.sha256
if($Check){
    if(!(Test-Path -LiteralPath $manifest)){throw 'MANIFEST.sha256 is missing. Generate and commit it with scripts/Update-Manifest.ps1.'}
    $expected=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::Ordinal)
    foreach($line in Get-Content -LiteralPath $manifest){
        if($line -cnotmatch '^([0-9a-f]{64})  (.+)$'){throw 'Malformed source manifest entry.'}
        if(!$expected.TryAdd($Matches[2],$Matches[1])){throw "Duplicate manifest path: $($Matches[2])"}
    }
    $differences=@(
        foreach($name in $hashes.Keys){
            if(!$expected.ContainsKey($name)){"Unlisted source: $name"}
            elseif($expected[$name] -cne $hashes[$name]){"Changed content: $name"}
        }
        foreach($name in $expected.Keys){if(!$hashes.ContainsKey($name)){"Missing source: $name"}}
    )
    if($differences.Count){throw ("Source manifest differs:`n"+($differences -join "`n")+"`nRegenerate with scripts/Update-Manifest.ps1, review, and commit MANIFEST.sha256 with the source changes. Do not regenerate in CI.")}
    Write-Host "PASS: all $($lines.Count) source manifest entries match Git checkout line endings; no unlisted source files."
}else{
    [IO.File]::WriteAllText($manifest,($lines -join "`n")+"`n",[Text.UTF8Encoding]::new($false))
    Write-Host "Updated $($lines.Count) source manifest entries."
}
