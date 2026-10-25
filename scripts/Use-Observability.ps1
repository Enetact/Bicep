#requires -Version 7.4
[CmdletBinding()]
param()
. (Join-Path $PSScriptRoot 'Observability.Common.ps1')
$status = Get-ObservabilityStatus
if ($status.State -ne 'Running' -or !$status.OwnershipVerified -or !$status.Healthy) {
    throw "Telemetry environment import blocked: $($status.State). Start the owned dashboard before the application."
}
$private = Get-ObservabilityPrivateConfig
# Set these only in the caller's process. Children inherit them; no credential is written to command arguments.
try {
    $env:OTEL_EXPORTER_OTLP_ENDPOINT = $private.otlpHttpUrl
    $env:OTEL_EXPORTER_OTLP_PROTOCOL = 'http/protobuf'
    $env:OTEL_EXPORTER_OTLP_HEADERS = 'x-otlp-api-key=' + $private.otlpApiKey
} finally { $private = $null }
