#requires -Version 7.4
# Trusted local evidence helpers. These are observations by the executing OS owner, not independent attestation.
function Initialize-FTEPlatform {
    if('FoundationTestEvidencePlatform' -as [type]){return}
    Add-Type -TypeDefinition @'
using System; using System.Diagnostics; using System.IO; using System.Runtime.InteropServices;
using System.Text; using System.Threading; using System.Threading.Tasks;
public sealed record FoundationTestProcessObservation(int ProcessId,long StartedUnixMilliseconds,long FinishedUnixMilliseconds,int ExitCode,string Stdout,string Stderr);
public static class FoundationTestEvidencePlatform {
 [StructLayout(LayoutKind.Sequential)] struct Attributes {public int Length;public IntPtr Descriptor;[MarshalAs(UnmanagedType.Bool)]public bool Inherit;}
 [DllImport("kernel32.dll",CharSet=CharSet.Unicode,SetLastError=true)] [return:MarshalAs(UnmanagedType.Bool)]static extern bool CreateDirectoryW(string path,ref Attributes attributes);
 public static bool CreatePrivateDirectory(string path,byte[] descriptor){var pinned=GCHandle.Alloc(descriptor,GCHandleType.Pinned);try{var attributes=new Attributes{Length=Marshal.SizeOf<Attributes>(),Descriptor=pinned.AddrOfPinnedObject()};return CreateDirectoryW(path,ref attributes);}finally{pinned.Free();}}
 static async Task<string> Read(StreamReader reader,CancellationTokenSource deadline){var text=new StringBuilder();var buffer=new char[4096];int count;while((count=await reader.ReadAsync(buffer.AsMemory(),deadline.Token))>0){if(text.Length+count>4194304){deadline.Cancel();throw new IOException("TestExecutionOutputLimit");}text.Append(buffer,0,count);}return text.ToString();}
 public static FoundationTestProcessObservation Run(string executable,string[] arguments,string directory,int timeoutMilliseconds){
  if(timeoutMilliseconds<50||timeoutMilliseconds>900000)throw new ArgumentOutOfRangeException(nameof(timeoutMilliseconds));
  using var process=new Process();var start=new ProcessStartInfo(executable){UseShellExecute=false,CreateNoWindow=true,RedirectStandardOutput=true,RedirectStandardError=true,WorkingDirectory=directory};
  foreach(var argument in arguments)start.ArgumentList.Add(argument);process.StartInfo=start;
  using var deadline=new CancellationTokenSource(timeoutMilliseconds);var began=DateTimeOffset.UtcNow.ToUnixTimeMilliseconds();
  try{if(!process.Start())throw new IOException("TestExecutionProcessStartFailed");var stdout=Read(process.StandardOutput,deadline);var stderr=Read(process.StandardError,deadline);
   Task.WhenAll(stdout,stderr,process.WaitForExitAsync(deadline.Token)).GetAwaiter().GetResult();return new(process.Id,began,DateTimeOffset.UtcNow.ToUnixTimeMilliseconds(),process.ExitCode,stdout.Result,stderr.Result);
  }finally{try{if(process.Id>0&&!process.HasExited){process.Kill(false);if(!process.WaitForExit(5000))throw new IOException("TestExecutionProcessDidNotStop");}}catch(InvalidOperationException){}}
 }
}
'@
}
function Assert-FTEUnlinkedPath([string]$Path) {
    if(!$IsWindows -or !$Path -or $Path -cnotmatch '\A[A-Za-z]:\\' -or $Path.Substring(2).Contains(':') -or $Path.Contains('/') -or $Path -match '[\x00-\x1f\x7f]') {throw 'TestExecutionLocalWindowsPathRequired'}
    foreach($segment in $Path.Substring(3).Split('\')){if(!$segment -or $segment -in @('.','..') -or $segment.EndsWith('.') -or $segment.EndsWith(' ') -or $segment -match '(?i)\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)'){throw 'TestExecutionCanonicalPathRequired'}}
    $full=[IO.Path]::GetFullPath($Path)
    if([IO.DriveInfo]::new([IO.Path]::GetPathRoot($full)).DriveType -ne [IO.DriveType]::Fixed){throw 'TestExecutionLocalWindowsPathRequired'}
    for($current=$full;$current -and $current.Length -gt 3;$current=Split-Path $current -Parent){if((Test-Path -LiteralPath $current) -and ((Get-Item -LiteralPath $current -Force).Attributes -band [IO.FileAttributes]::ReparsePoint)){throw 'TestExecutionLinkedPathRejected'}}
    $full
}
function New-FTEPrivateDirectory([string]$Path) {
    $full=Assert-FTEUnlinkedPath $Path
    if(!(Test-Path -LiteralPath (Split-Path $full -Parent) -PathType Container)){throw 'TestExecutionParentMissing'}
    Initialize-FTEPlatform;$sid=[Security.Principal.WindowsIdentity]::GetCurrent().User;$acl=[Security.AccessControl.DirectorySecurity]::new();$acl.SetOwner($sid);$acl.SetAccessRuleProtection($true,$false)
    $acl.AddAccessRule([Security.AccessControl.FileSystemAccessRule]::new($sid,[Security.AccessControl.FileSystemRights]::FullControl,[Security.AccessControl.InheritanceFlags]'ContainerInherit,ObjectInherit',[Security.AccessControl.PropagationFlags]::None,[Security.AccessControl.AccessControlType]::Allow))
    if(![FoundationTestEvidencePlatform]::CreatePrivateDirectory($full,$acl.GetSecurityDescriptorBinaryForm())){throw 'TestExecutionFreshDirectoryRequired'}
    $full
}
function Get-FTEHash([byte[]]$Bytes) {[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($Bytes)).ToLowerInvariant()}
function Write-FTEFile([string]$Path,[byte[]]$Bytes,[int]$Maximum=16777216) {
    $null=Assert-FTEUnlinkedPath $Path;if($Bytes.Length -gt $Maximum){throw 'TestExecutionArtifactLimit'}
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::CreateNew,[IO.FileAccess]::Write,[IO.FileShare]::Read,8192,[IO.FileOptions]::WriteThrough)
    try{$stream.Write($Bytes,0,$Bytes.Length);$stream.Flush($true)}finally{$stream.Dispose()}
    [ordered]@{path=[IO.Path]::GetFileName($Path);bytes=$Bytes.Length;sha256=(Get-FTEHash $Bytes)}
}
function Write-FTEJson([string]$Path,$Value) {Write-FTEFile $Path ([Text.UTF8Encoding]::new($false).GetBytes((ConvertTo-Json -InputObject $Value -Depth 24)))}
function Resolve-FTEExecutable([string]$Path) {
    # Match PowerShell's first native-command resolution, then record and pin that absolute file.
    $command=@(Get-Command -Name $Path -CommandType Application -ErrorAction Stop)[0]
    $full=Assert-FTEUnlinkedPath $command.Source
    if(!(Test-Path -LiteralPath $full -PathType Leaf)){throw 'TestExecutionExecutableMissing'}
    $full
}
function Invoke-FTEProcess([string]$Executable,[string[]]$Arguments,[string]$Directory,[int]$TimeoutMilliseconds=30000) {
    $null=Assert-FTEUnlinkedPath $Executable;$null=Assert-FTEUnlinkedPath $Directory
    $handle=[IO.FileStream]::new($Executable,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{if($handle.Length -gt 67108864){throw 'TestExecutionExecutableByteLimit'};$sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($handle)).ToLowerInvariant();Initialize-FTEPlatform
        $observed=[FoundationTestEvidencePlatform]::Run($Executable,$Arguments,$Directory,$TimeoutMilliseconds)
        [pscustomobject]@{executable=$Executable;executableSha256=$sha256;arguments=$Arguments;processId=$observed.ProcessId;startedUnixMilliseconds=$observed.StartedUnixMilliseconds;finishedUnixMilliseconds=$observed.FinishedUnixMilliseconds;exitCode=$observed.ExitCode;stdout=$observed.Stdout;stderr=$observed.Stderr}
    }finally{$handle.Dispose()}
}
function Get-FTEGit([string]$Root,[string[]]$Arguments) {
    $git=Resolve-FTEExecutable 'git';$observed=Invoke-FTEProcess $git (@('--no-optional-locks','-C',$Root,'-c','core.quotepath=false','-c','core.fsmonitor=false')+$Arguments) $Root
    if($observed.exitCode -ne 0){throw 'TestExecutionGitFailed'}
    $observed.stdout
}
function Get-FTESourceSnapshot([string]$Root) {
    $rootPath=Assert-FTEUnlinkedPath $Root
    $actualRoot=(Get-FTEGit $rootPath @('rev-parse','--show-toplevel')).Trim().Replace('/','\')
    if(![string]::Equals($actualRoot,$rootPath,[StringComparison]::OrdinalIgnoreCase)){throw 'TestExecutionRepositoryRootRequired'}
    $head=(Get-FTEGit $rootPath @('rev-parse','HEAD')).Trim()
    if($head -cnotmatch '\A[a-f0-9]{40}(?:[a-f0-9]{24})?\z'){throw 'TestExecutionSourceRevisionRequired'}
    $catalog=Get-FTEGit $rootPath @('ls-files','--cached','--others','--exclude-standard','--eol','-z')
    $entries=@($catalog.Split([char]0,[StringSplitOptions]::RemoveEmptyEntries));if($entries.Count -lt 1 -or $entries.Count -gt 30000){throw 'TestExecutionSourceCountLimit'}
    $files=[Collections.Generic.SortedDictionary[string,object]]::new([StringComparer]::Ordinal);$seen=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);$total=[long]0
    foreach($entry in $entries){
        if($entry -cnotmatch '\A(?<metadata>[^\t\r\n]+)\t(?<path>[^\x00-\x1f\x7f]+)\z'){throw 'TestExecutionSourceCatalogInvalid'}
        $metadata=$Matches.metadata;$relative=$Matches.path
        if($relative.Length -gt 1024 -or $relative.StartsWith('/') -or $relative.Contains('\') -or $relative.Contains(':') -or !$seen.Add($relative)){throw 'TestExecutionSourcePathInvalid'}
        foreach($segment in $relative.Split('/')){if(!$segment -or $segment -in @('.','..') -or $segment.EndsWith('.') -or $segment.EndsWith(' ') -or $segment -match '(?i)\A(?:CON|PRN|AUX|NUL|COM[1-9]|LPT[1-9])(?:\.|\z)'){throw 'TestExecutionSourcePathInvalid'}}
        $path=Assert-FTEUnlinkedPath (Join-Path $rootPath $relative)
        if(!(Test-Path -LiteralPath $path -PathType Leaf)){throw 'TestExecutionSourceInputMissing'}
        $stream=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read);$memory=[IO.MemoryStream]::new()
        try{$buffer=[byte[]]::new(8192);while(($count=$stream.Read($buffer,0,$buffer.Length)) -gt 0){if($memory.Length+$count -gt 16777216 -or $total+$count -gt 134217728){throw 'TestExecutionSourceByteLimit'};$memory.Write($buffer,0,$count);$total+=$count};$bytes=$memory.ToArray()}
        finally{$memory.Dispose();$stream.Dispose()}
        $governed=$bytes;$normalized=$metadata -match '\bw/(crlf|mixed)\b' -and $metadata -match '\beol=lf\b' -and $metadata -notmatch '\battr/-text\b'
        if($normalized){$governed=[Text.Encoding]::Latin1.GetBytes([Text.Encoding]::Latin1.GetString($bytes).Replace("`r`n","`n"))}
        $attribute=if($metadata -match '\battr/(?<attribute>.*)\z'){$Matches.attribute.Trim()}else{throw 'TestExecutionSourceAttributesMissing'}
        $files.Add($relative,[ordered]@{path=$relative;bytes=$bytes.Length;rawSha256=(Get-FTEHash $bytes);governedBytes=$governed.Length;governedSha256=(Get-FTEHash $governed);attributes=$attribute;crlfNormalized=[bool]$normalized})
    }
    if(!$files.Count){throw 'TestExecutionSourceCountLimit'}
    [ordered]@{schema='platform-studio.foundation-test-source.v1';head=$head;excludedPaths=@();fileCount=$files.Count;rawBytes=$total;files=@($files.Values)}
}
function Read-FTETrx([string]$Path,[long]$StartedUnixMilliseconds,[long]$FinishedUnixMilliseconds) {
    $null=Assert-FTEUnlinkedPath $Path;if(!(Test-Path -LiteralPath $Path -PathType Leaf)){throw 'TestExecutionTrxMissing'}
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{
        if($stream.Length -lt 1 -or $stream.Length -gt 33554432){throw 'TestExecutionTrxByteLimit'}
        $sha256=[Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant();$length=$stream.Length;$stream.Position=0
        $settings=[Xml.XmlReaderSettings]::new();$settings.DtdProcessing=[Xml.DtdProcessing]::Prohibit;$settings.XmlResolver=$null;$settings.MaxCharactersInDocument=33554432;$settings.MaxCharactersFromEntities=0
        $reader=[Xml.XmlReader]::Create($stream,$settings);$document=[Xml.XmlDocument]::new();$document.XmlResolver=$null
        try{$document.Load($reader)}finally{$reader.Dispose()}
    }finally{$stream.Dispose()}
    $ns=[Xml.XmlNamespaceManager]::new($document.NameTable);$ns.AddNamespace('t','http://microsoft.com/schemas/VisualStudio/TeamTest/2010')
    if($document.DocumentElement.LocalName -cne 'TestRun' -or $document.DocumentElement.NamespaceURI -cne $ns.LookupNamespace('t')){throw 'TestExecutionTrxContractInvalid'}
    $runId=[Guid]::Empty;if(![Guid]::TryParseExact($document.DocumentElement.GetAttribute('id'),'D',[ref]$runId) -or $runId -eq [Guid]::Empty){throw 'TestExecutionTrxContractInvalid'}
    $times=$document.SelectNodes('/t:TestRun/t:Times',$ns);$summaries=$document.SelectNodes('/t:TestRun/t:ResultSummary',$ns);$counters=$document.SelectNodes('/t:TestRun/t:ResultSummary/t:Counters',$ns)
    if($times.Count -ne 1 -or $summaries.Count -ne 1 -or $counters.Count -ne 1){throw 'TestExecutionTrxContractInvalid'}
    $start=$null;$finish=$null
    try{foreach($key in @('start','finish')){$value=$times[0].GetAttribute($key);if($value -cnotmatch '(?:Z|[+-][0-9]{2}:[0-9]{2})\z'){throw 'invalid time'};$parsed=[DateTimeOffset]::Parse($value,[Globalization.CultureInfo]::InvariantCulture,[Globalization.DateTimeStyles]::RoundtripKind);if($key -ceq 'start'){$start=$parsed}else{$finish=$parsed}}}catch{throw 'TestExecutionTrxTimeInvalid'}
    if($finish -lt $start -or $start.ToUnixTimeMilliseconds() -lt $StartedUnixMilliseconds-2000 -or $finish.ToUnixTimeMilliseconds() -gt $FinishedUnixMilliseconds+2000){throw 'TestExecutionTrxOutsideObservedProcess'}
    $results=$document.SelectNodes('/t:TestRun/t:Results/t:UnitTestResult',$ns);$definitions=$document.SelectNodes('/t:TestRun/t:TestDefinitions/t:UnitTest',$ns);$entries=$document.SelectNodes('/t:TestRun/t:TestEntries/t:TestEntry',$ns)
    foreach($container in @('Results','TestDefinitions','TestEntries')){if($document.SelectNodes('/t:TestRun/t:'+$container,$ns).Count -ne 1){throw 'TestExecutionTrxContractInvalid'}}
    if($results.Count -lt 1 -or $results.Count -gt 100000 -or $definitions.Count -ne $results.Count -or $entries.Count -ne $results.Count){throw 'TestExecutionTrxResultCountInvalid'}
    $executionIds=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);$testIds=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);$definitionIds=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);$entryIds=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase)
    $executionTests=[Collections.Generic.Dictionary[string,string]]::new([StringComparer]::OrdinalIgnoreCase)
    $passed=0;$failed=0;$skipped=0
    foreach($result in $results){
        $test=$result.GetAttribute('testId');$execution=$result.GetAttribute('executionId');$id=[Guid]::Empty
        if(![Guid]::TryParseExact($test,'D',[ref]$id) -or $id -eq [Guid]::Empty -or ![Guid]::TryParseExact($execution,'D',[ref]$id) -or $id -eq [Guid]::Empty -or !$testIds.Add($test) -or !$executionIds.Add($execution)){throw 'TestExecutionTrxDuplicateOrInvalidResult'}
        $executionTests.Add($execution,$test)
        switch -CaseSensitive ($result.GetAttribute('outcome')){'Passed'{$passed++}'Failed'{$failed++}'NotExecuted'{$skipped++}default{throw 'TestExecutionTrxOutcomeInvalid'}}
    }
    foreach($definition in $definitions){$id=$definition.GetAttribute('id');if(!$testIds.Contains($id) -or !$definitionIds.Add($id)){throw 'TestExecutionTrxDefinitionInvalid'}}
    foreach($entry in $entries){$id=$entry.GetAttribute('executionId');if(!$executionIds.Contains($id) -or ![string]::Equals($executionTests[$id],$entry.GetAttribute('testId'),[StringComparison]::OrdinalIgnoreCase) -or !$entryIds.Add($id)){throw 'TestExecutionTrxEntryInvalid'}}
    foreach($name in @('total','executed','passed','failed','notExecuted')){if($counters[0].GetAttribute($name) -cnotmatch '\A(?:0|[1-9][0-9]{0,5})\z'){throw 'TestExecutionTrxCounterInvalid'}}
    if([int]$counters[0].GetAttribute('total') -ne $results.Count -or [int]$counters[0].GetAttribute('passed') -ne $passed -or [int]$counters[0].GetAttribute('failed') -ne $failed -or
        [int]$counters[0].GetAttribute('executed') -ne $passed+$failed -or [int]$counters[0].GetAttribute('notExecuted') -gt $skipped){throw 'TestExecutionTrxCounterInvalid'}
    foreach($name in @('error','timeout','aborted','inconclusive','passedButRunAborted','notRunnable','disconnected','warning','inProgress','pending')){if($counters[0].HasAttribute($name) -and $counters[0].GetAttribute($name) -cne '0'){throw 'TestExecutionTrxCounterInvalid'}}
    [ordered]@{path='results/foundation.trx';bytes=$length;sha256=$sha256;runId=$runId.ToString('D');passed=$passed;failed=$failed;notExecuted=$skipped;total=$results.Count;summaryOutcome=$summaries[0].GetAttribute('outcome')}
}
function Read-FTEJsonArtifact([string]$Path,[string]$ExpectedSha256) {
    $null=Assert-FTEUnlinkedPath $Path
    if($ExpectedSha256 -isnot [string] -or $ExpectedSha256 -cnotmatch '\A[a-f0-9]{64}\z'){throw 'TestExecutionArtifactPinRequired'}
    $stream=[IO.FileStream]::new($Path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
    try{if($stream.Length -gt 16777216){throw 'TestExecutionArtifactLimit'};$bytes=[byte[]]::new($stream.Length);$stream.ReadExactly($bytes)}finally{$stream.Dispose()}
    if((Get-FTEHash $bytes) -cne $ExpectedSha256){throw 'TestExecutionArtifactPinMismatch'}
    $text=[Text.UTF8Encoding]::new($false,$true).GetString($bytes);$options=[Text.Json.JsonDocumentOptions]::new();$options.MaxDepth=24;$document=[Text.Json.JsonDocument]::Parse($text,$options)
    try{if($document.RootElement.ValueKind -ne [Text.Json.JsonValueKind]::Object){throw 'TestExecutionJsonObjectRequired'};$stack=[Collections.Generic.Stack[Text.Json.JsonElement]]::new();$stack.Push($document.RootElement);while($stack.Count){$node=$stack.Pop();if($node.ValueKind -eq [Text.Json.JsonValueKind]::Object){$names=[Collections.Generic.HashSet[string]]::new([StringComparer]::OrdinalIgnoreCase);foreach($property in $node.EnumerateObject()){if(!$names.Add($property.Name)){throw 'TestExecutionAmbiguousJson'};$stack.Push($property.Value)}}elseif($node.ValueKind -eq [Text.Json.JsonValueKind]::Array){foreach($child in $node.EnumerateArray()){$stack.Push($child)}}}}finally{$document.Dispose()}
    $text|ConvertFrom-Json -AsHashtable -Depth 24
}
function Assert-FTEFields($Record,[string[]]$Fields) {
    if($Record -isnot [Collections.IDictionary] -or $Record.Count -ne $Fields.Count -or @($Record.Keys|Where-Object {$_ -cnotin $Fields}).Count){throw 'TestExecutionJsonContractInvalid'}
}
function Assert-FTELimits($Limits) {
    $fields=@('independentAttestation','completeDependencyClosureVerified','environmentAttested','noNetworkAttested','descendantProcessCleanupVerified','signed','productionReady','publicationAuthorized')
    Assert-FTEFields $Limits $fields
    foreach($flag in $fields){if($Limits[$flag] -isnot [bool] -or $Limits[$flag]){throw 'TestExecutionUnsupportedClaim'}}
}
function Test-FTEExecutionReceipt([string]$RunDirectory,[string]$ExpectedReceiptSha256) {
    $run=Assert-FTEUnlinkedPath $RunDirectory;$completed=Join-Path $run 'completed.json'
    if(!(Test-Path -LiteralPath $completed -PathType Leaf) -or (Test-Path -LiteralPath (Join-Path $run 'rejected.json'))){throw 'TestExecutionCompletedReceiptRequired'}
    $receipt=Read-FTEJsonArtifact $completed $ExpectedReceiptSha256
    Assert-FTEFields $receipt @('schema','state','passed','startedUnixMilliseconds','finishedUnixMilliseconds','profile','started','sourceBefore','sourceAfter','sourceInputsUnchanged','commands','trx','summary','limits')
    foreach($time in @($receipt.startedUnixMilliseconds,$receipt.finishedUnixMilliseconds)){if(($time -isnot [long] -and $time -isnot [int]) -or $time -le 0){throw 'TestExecutionTimestampInvalid'}}
    if($receipt.schema -isnot [string] -or $receipt.schema -cne 'platform-studio.foundation-test-execution.v1' -or $receipt.state -isnot [string] -or $receipt.state -cne 'Completed' -or
        $receipt.passed -isnot [bool] -or !$receipt.passed -or $receipt.sourceInputsUnchanged -isnot [bool] -or !$receipt.sourceInputsUnchanged -or
        $receipt.commands -isnot [Collections.IList] -or $receipt.commands.Count -ne 2){throw 'TestExecutionCompletedReceiptInvalid'}
    Assert-FTELimits $receipt.limits
    $started=Read-FTEJsonArtifact (Join-Path $run 'started.json') $receipt.started.sha256
    Assert-FTEFields $started @('schema','state','startedUnixMilliseconds','repositoryRoot','profile','executable','executableSha256','plannedArguments','limits')
    Assert-FTELimits $started.limits
    if($receipt.started.path -cne 'started.json' -or $started.schema -cne 'platform-studio.foundation-test-execution-started.v1' -or $started.state -cne 'Started' -or
        $started.profile -cne 'disconnected-signed-identity-sqlite-mock-pipelines' -or $receipt.profile -cne $started.profile -or
        $started.executable -isnot [string] -or $started.executableSha256 -isnot [string] -or $started.executableSha256 -cnotmatch '\A[a-f0-9]{64}\z' -or
        $started.repositoryRoot -isnot [string]){throw 'TestExecutionStartedReceiptInvalid'}
    $root=Assert-FTEUnlinkedPath $started.repositoryRoot
    if(!$run.StartsWith((Join-Path $root 'artifacts/foundation-test-executions')+'\',[StringComparison]::OrdinalIgnoreCase)){throw 'TestExecutionRunBindingInvalid'}
    $project=Join-Path $root 'tests/SelfService.Foundation.Tests/SelfService.Foundation.Tests.csproj';$output=Join-Path $run 'build';$results=Join-Path $run 'results'
    $expectedArguments=@(@('restore',$project,'--locked-mode','--artifacts-path',$output,'-m:1','-nodeReuse:false'),@('test',$project,'--no-restore','--artifacts-path',$output,'-c','Release','-m:1','-nodeReuse:false','-p:UseSharedCompilation=false','--logger','trx;LogFileName=foundation.trx','--results-directory',$results))
    if($started.plannedArguments -isnot [Collections.IList] -or $started.plannedArguments.Count -ne 2 -or $receipt.startedUnixMilliseconds -ne $started.startedUnixMilliseconds){throw 'TestExecutionCommandReceiptInvalid'}
    for($index=0;$index -lt 2;$index++){
        $command=$receipt.commands[$index]
        Assert-FTEFields $command @('step','executable','executableSha256','arguments','processId','startedUnixMilliseconds','finishedUnixMilliseconds','exitCode','stdout','stderr')
        foreach($time in @($command.startedUnixMilliseconds,$command.finishedUnixMilliseconds)){if(($time -isnot [long] -and $time -isnot [int]) -or $time -le 0){throw 'TestExecutionTimestampInvalid'}}
        if($command.step -cne @('restore','test')[$index] -or $command.executable -cne $started.executable -or $command.executableSha256 -cne $started.executableSha256 -or
            (ConvertTo-Json -InputObject $command.arguments -Compress) -cne (ConvertTo-Json -InputObject $expectedArguments[$index] -Compress) -or
            (ConvertTo-Json -InputObject $started.plannedArguments[$index] -Compress) -cne (ConvertTo-Json -InputObject $expectedArguments[$index] -Compress) -or
            $command.startedUnixMilliseconds -lt $started.startedUnixMilliseconds -or $command.finishedUnixMilliseconds -lt $command.startedUnixMilliseconds -or $command.finishedUnixMilliseconds -gt $receipt.finishedUnixMilliseconds){throw 'TestExecutionCommandReceiptInvalid'}
    }
    if($receipt.commands[1].startedUnixMilliseconds -lt $receipt.commands[0].finishedUnixMilliseconds){throw 'TestExecutionCommandReceiptInvalid'}
    $before=Read-FTEJsonArtifact (Join-Path $run 'source-before.json') $receipt.sourceBefore.sha256
    $null=Read-FTEJsonArtifact (Join-Path $run 'source-after.json') $receipt.sourceAfter.sha256
    if($receipt.sourceBefore.path -cne 'source-before.json' -or $receipt.sourceAfter.path -cne 'source-after.json' -or $receipt.sourceBefore.sha256 -cne $receipt.sourceAfter.sha256 -or
        $before.schema -cne 'platform-studio.foundation-test-source.v1' -or $before.fileCount -lt 1 -or $before.fileCount -gt 30000 -or $before.fileCount -ne $before.files.Count){throw 'TestExecutionSourceReceiptInvalid'}
    foreach($step in @('restore','test')){
        $command=@($receipt.commands|Where-Object {$_.step -ceq $step})
        if($command.Count -ne 1 -or ($command[0].exitCode -isnot [long] -and $command[0].exitCode -isnot [int]) -or $command[0].exitCode -ne 0 -or $command[0].executableSha256 -cnotmatch '\A[a-f0-9]{64}\z'){throw 'TestExecutionCommandReceiptInvalid'}
        foreach($channel in @('stdout','stderr')){
            $binding=$command[0][$channel];$name=$step+'.'+$channel+'.txt'
            if($binding.path -cne $name -or $binding.sha256 -cnotmatch '\A[a-f0-9]{64}\z'){throw 'TestExecutionCommandReceiptInvalid'}
            $path=Assert-FTEUnlinkedPath (Join-Path $run $name);$stream=[IO.FileStream]::new($path,[IO.FileMode]::Open,[IO.FileAccess]::Read,[IO.FileShare]::Read)
            try{if($stream.Length -gt 16777216 -or $stream.Length -ne $binding.bytes -or [Convert]::ToHexString([Security.Cryptography.SHA256]::HashData($stream)).ToLowerInvariant() -cne $binding.sha256){throw 'TestExecutionCommandArtifactMismatch'}}finally{$stream.Dispose()}
        }
    }
    $test=@($receipt.commands|Where-Object {$_.step -ceq 'test'})[0]
    $trx=Read-FTETrx (Join-Path $run 'results/foundation.trx') $test.startedUnixMilliseconds $test.finishedUnixMilliseconds
    if($trx.sha256 -cne $receipt.trx.sha256 -or $trx.failed -ne 0 -or $trx.notExecuted -ne 0 -or $trx.summaryOutcome -cne 'Completed' -or $trx.total -ne $receipt.trx.total -or $trx.passed -ne $receipt.trx.passed){throw 'TestExecutionTrxReceiptMismatch'}
    $summary=Read-FTEJsonArtifact (Join-Path $run 'results/summary.json') $receipt.summary.sha256
    Assert-FTEFields $summary @('schema','observedUtc','passed','failed','total','notExecuted','profile','externalDispatch','deploymentVerified')
    if($summary.schema -isnot [string] -or $summary.schema -cne 'platform-studio.foundation-tests.v1' -or $summary.profile -isnot [string] -or $summary.profile -cne $receipt.profile -or
        $summary.externalDispatch -isnot [bool] -or $summary.externalDispatch -or $summary.deploymentVerified -isnot [bool] -or $summary.deploymentVerified -or
        $receipt.summary.path -cne 'results/summary.json' -or $summary.passed -ne $trx.passed -or $summary.total -ne $trx.total -or $summary.failed -ne 0 -or $summary.notExecuted -ne 0){throw 'TestExecutionSummaryReceiptMismatch'}
    [pscustomobject]@{state='Completed';passed=$true;sourceInputsUnchanged=$true;sourceFileCount=$before.fileCount;testsPassed=$trx.passed;receiptSha256=$ExpectedReceiptSha256;independentAttestation=$false;completeDependencyClosureVerified=$false;noNetworkAttested=$false;productionReady=$false}
}
