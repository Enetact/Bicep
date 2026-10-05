import { digest, resolveNames } from './core.mjs';
import { validateInstances } from './instances.mjs';

export const supportedWorkloads = ['private-storage', 'key-vault', 'observability'];
// Reviewed bindings for the first N3 group. Unsupported workloads never receive a partial map.
export function workloadNames(request, policy, catalog) {
  if (!request || typeof request !== 'object' || Array.isArray(request) || Object.keys(request).sort().join() !== 'intent,workloadType' || !supportedWorkloads.includes(request.workloadType)) throw new Error('Naming maps currently support Private Storage, Key Vault and Observability only.');
  const { intent, workloadType } = request;
  validateInstances({ schemaVersion: 1, namingPolicySha256: digest(policy), instances: [{ workloadType, state: 'draft', intent }] }, policy, catalog);
  if (intent.subscriptionId === '00000000-0000-0000-0000-000000000000') throw new Error('Select a subscription; an unconfigured target cannot establish naming scope.');
  const components = [];
  const add = (key, armType, mode, extra = {}) => components.push({ key, armType, mode, condition: 'included', ...extra });
  const convention = (key, type, role = 'primary', parentKey = 'group') => add(key, type, 'convention', { role, ...(parentKey ? { parentKey } : {}) });
  const fixed = (key, type, parentKey, explicitName) => add(key, type, 'fixed', { parentKey, explicitName });
  const role = (key, parentKey) => add(key, 'Microsoft.Authorization/roleAssignments', 'unresolved', { parentKey, reason: 'GUID depends on the reviewed principal and role binding; no principal is selected by this naming draft.' });
  const endpoint = (key, purpose) => { convention(key, 'Microsoft.Network/privateEndpoints', purpose); fixed(`${key}.dns`, 'Microsoft.Network/privateEndpoints/privateDnsZoneGroups', key, 'default'); };
  convention('group', 'Microsoft.Resources/resourceGroups', 'primary', null);
  convention('stack', 'Microsoft.Resources/deploymentStacks', 'primary', null);
  if (workloadType === 'private-storage') {
    convention('storage', 'Microsoft.Storage/storageAccounts');
    for (const service of ['blob', 'queue', 'table']) fixed(service, `Microsoft.Storage/storageAccounts/${service}Services`, 'storage', 'default');
    fixed('container', 'Microsoft.Storage/storageAccounts/blobServices/containers', 'blob', 'workspace');
    fixed('queue.work', 'Microsoft.Storage/storageAccounts/queueServices/queues', 'queue', 'work');
    for (const service of ['blob', 'queue']) {
      fixed(`${service}.diagnostics`, 'Microsoft.Insights/diagnosticSettings', service, 'storage-audit');
      endpoint(`endpoint.${service}`, service);
      role(`access.${service}`, 'storage');
    }
  } else if (workloadType === 'key-vault') {
    convention('vault', 'Microsoft.KeyVault/vaults'); endpoint('endpoint.vault', 'vault');
    fixed('vault.diagnostics', 'Microsoft.Insights/diagnosticSettings', 'vault', 'vault-audit'); role('access.reader', 'vault');
  } else {
    // One workbook per instance. Preserve its GUID across display-name edits; legacy still uses Bicep guid().
    fixed('workbook', 'Microsoft.Insights/workbooks', 'group', intent.instanceId);
    convention('alert', 'Microsoft.Insights/scheduledQueryRules');
  }
  const plan = resolveNames({ schemaVersion: 1, intent, components }, policy);
  const name = key => plan.resources.find(r => r.componentKey === key).name;
  const resourceNames = workloadType === 'private-storage' ? { profile: 'standard-v2', storage: name('storage'), blobEndpoint: name('endpoint.blob'), queueEndpoint: name('endpoint.queue') }
    : workloadType === 'key-vault' ? { profile: 'standard-v2', vault: name('vault'), vaultEndpoint: name('endpoint.vault') }
    : { profile: 'standard-v2', workbook: name('workbook'), displayName: intent.stackDisplayName, alert: name('alert') };
  const result = { schema: 'platform.workload-naming-draft/v1', workloadType, plan,
    parameterPatch: { parameters: { resourceNames: { value: resourceNames }, workloadResourceGroupName: { value: name('group') }, environmentName: { value: intent.environment }, location: { value: intent.region } } },
    deploymentAuthorized: false, allocationAuthorized: false,
    coverage: 'Calculated names and explicit children only. Principal-dependent role IDs remain unresolved. Shared prerequisites and service-generated NICs are not bound or inventoried. This partial parameter patch is not a deployment request.' };
  return { ...result, draftSha256: digest(result) };
}
