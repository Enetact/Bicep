// Physical convention names consumed by the six additional compositions.
// Application-contract children and provider-assigned resources are not renamed here.
const rg = 'Microsoft.Resources/resourceGroups', storage = 'Microsoft.Storage/storageAccounts', pe = 'Microsoft.Network/privateEndpoints';
const app = 'Microsoft.Web/sites', plan = 'Microsoft.Web/serverfarms', identity = 'Microsoft.ManagedIdentity/userAssignedIdentities';
const sqr = 'Microsoft.Insights/scheduledQueryRules', alert = 'Microsoft.Insights/metricAlerts';
const entry = (field, type, role = 'primary', parent = 'group') => ({field,type,role,parent});
const endpoints = roles => roles.map(role => entry(role.replace(/-([a-z])/g,(_,c)=>c.toUpperCase())+'Endpoint',pe,role));
const networkChildren = () => [entry('integrationSubnet','Microsoft.Network/virtualNetworks/subnets','integration','vnet'),entry('endpointSubnet','Microsoft.Network/virtualNetworks/subnets','endpoints','vnet'),entry('dnsLink','Microsoft.Network/privateDnsZones/virtualNetworkLinks','primary','dnsblob')];
export const extraBindings = {
  'http-functions': [entry('storage',storage),entry('identity',identity),entry('app',app),entry('plan',plan),...endpoints(['blob','queue','table','sites'])],
  'service-bus-worker': [entry('storage',storage),entry('identity',identity),entry('app',app),entry('plan',plan),entry('bus','Microsoft.ServiceBus/namespaces'),...endpoints(['blob','queue','table','sites','servicebus'])],
  'private-aks': [entry('cluster','Microsoft.ContainerService/managedClusters'),entry('applicationIdentity',identity),entry('nodeGroup',rg,'nodes',null)],
  'private-data-platform': [entry('storage',storage,'lake'),entry('managedGroup',rg,'databricks',null),entry('rootStorage',storage,'root','managedGroup'),
    entry('databricks','Microsoft.Databricks/workspaces'),entry('accessConnector','Microsoft.Databricks/accessConnectors','lake'),entry('rootAccessConnector','Microsoft.Databricks/accessConnectors','root'),
    entry('factory','Microsoft.DataFactory/factories'),...endpoints(['root-blob','root-dfs','blob','dfs','databricks','datafactory','adfportal'])],
  'logic-app-event-grid': [entry('runtimeStorage',storage,'host'),entry('eventStorage',storage,'events'),entry('app',app),entry('plan',plan),entry('topic','Microsoft.EventGrid/topics'),
    entry('vnet','Microsoft.Network/virtualNetworks'),entry('integrationNsg','Microsoft.Network/networkSecurityGroups','integration'),entry('workspace','Microsoft.OperationalInsights/workspaces'),...networkChildren(),
    entry('insights','Microsoft.Insights/components'),entry('workflowFailures',sqr,'failures'),entry('quarantine',sqr,'quarantine'),entry('deliveryFailures',alert,'delivery'),entry('deadletters',alert,'deadletter'),entry('backlog',alert,'backlog'),
    ...endpoints(['runtime-blob','runtime-queue','runtime-table','runtime-file','event-blob','event-queue','sites','topic'])],
  'blob-transfer': [entry('hostStorage',storage,'host'),entry('uploadStorage',storage,'upload'),entry('identity',identity),entry('app',app),entry('plan',plan),
    entry('workspace','Microsoft.OperationalInsights/workspaces'),entry('insights','Microsoft.Insights/components'),entry('functionErrors',sqr,'failures'),entry('recoveryReview',sqr,'review'),entry('recoveryHeartbeat',sqr,'heartbeat'),
    entry('vnet','Microsoft.Network/virtualNetworks'),entry('integrationNsg','Microsoft.Network/networkSecurityGroups','integration'),...networkChildren(),
    ...endpoints(['host-blob','host-queue','host-table','upload-blob','upload-queue','destination-blob','destination-dfs','sites'])]
};
