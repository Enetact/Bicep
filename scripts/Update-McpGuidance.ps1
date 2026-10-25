#requires -Version 7.4
[CmdletBinding()]
param([switch]$Check)
$ErrorActionPreference='Stop'
$arguments=@((Join-Path $PSScriptRoot 'mcp/update-guidance.mjs'))
if($Check){$arguments+='--check'}
& node @arguments
if($LASTEXITCODE -ne 0){throw 'MCP guidance generation or verification failed.'}
