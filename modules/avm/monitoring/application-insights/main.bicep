targetScope = 'resourceGroup'

metadata name = 'Reviewed AVM private Application Insights'
metadata description = 'Workspace-based Entra-only telemetry with masked IPs and private ingestion/query; requires qualified Azure Monitor Private Link.'

@description('Explicit reviewed component name; no naming policy is inferred.')
@minLength(1)
@maxLength(260)
param name string
@description('Approved Azure region.')
@minLength(1)
param location string
@description('Reviewed ownership and classification tags.')
param tags object
@description('Approved existing or composition-owned Log Analytics workspace ID. Observation alone does not authorize reuse.')
@minLength(1)
param workspaceId string
@description('Reviewed application telemetry profile.')
@allowed(['web', 'other'])
param applicationType string = 'web'
@description('Reviewed retention profile in days; workspace retention and cost must also be qualified.')
@allowed([30, 60, 90, 120, 180, 270, 365, 550, 730])
param retentionInDays int = 90
@description('Percentage of application telemetry sampled, 1-100. Confirm that the instrumentation honors this setting.')
@minValue(1)
@maxValue(100)
param samplingPercentage int = 100

module avm '../../../../vendor/azure-verified-modules/monitoring-application-insights/main.json' = {
  name: '${uniqueString(deployment().name, name)}-avm'
  params: {
    name: name
    location: location
    tags: tags
    workspaceResourceId: workspaceId
    applicationType: applicationType
    disableLocalAuth: true
    disableIpMasking: false
    publicNetworkAccessForIngestion: 'Disabled'
    publicNetworkAccessForQuery: 'Disabled'
    retentionInDays: retentionInDays
    samplingPercentage: samplingPercentage
    enableTelemetry: false
  }
}

output id string = avm.outputs.resourceId
output name string = avm.outputs.name
output applicationId string = avm.outputs.applicationId
