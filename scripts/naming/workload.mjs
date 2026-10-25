import { digest, resolveNames } from './core.mjs';
import { validateInstances } from './instances.mjs';
import { extraBindings } from './bindings.mjs';

export const supportedWorkloads = ['private-storage', 'key-vault', 'observability', ...Object.keys(extraBindings)];
// Reviewed bindings for all registered workloads. Unsupported workloads never receive a partial map.
export function workloadNames(request, policy, catalog) {
  if (!request || typeof request !== 'object' || Array.isArray(request) || Object.keys(request).sort().join() !== 'intent,workloadType' || !supportedWorkloads.includes(request.workloadType)) throw new Error('Unsupported naming request or workload.');
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
  } else if (workloadType === 'observability') {
    // One workbook per instance. Preserve its GUID across display-name edits; legacy still uses Bicep guid().
    fixed('workbook', 'Microsoft.Insights/workbooks', 'group', intent.instanceId);
    convention('alert', 'Microsoft.Insights/scheduledQueryRules');
  } else {
    if (['blob-transfer','logic-app-event-grid'].includes(workloadType)) fixed('dnsblob','Microsoft.Network/privateDnsZones','group','privatelink.blob.core.windows.net');
    for (const b of extraBindings[workloadType]) convention(b.field.toLowerCase(),b.type,b.role,b.parent === null ? null : b.parent.toLowerCase());
  }
  const plan = resolveNames({ schemaVersion: 1, intent, components }, policy);
  const name = key => plan.resources.find(r => r.componentKey === key).name;
  const resourceNames = workloadType === 'private-storage' ? { profile: 'standard-v2', storage: name('storage'), blobEndpoint: name('endpoint.blob'), queueEndpoint: name('endpoint.queue') }
    : workloadType === 'key-vault' ? { profile: 'standard-v2', vault: name('vault'), vaultEndpoint: name('endpoint.vault') }
    : workloadType === 'observability' ? { profile: 'standard-v2', workbook: name('workbook'), displayName: intent.stackDisplayName, alert: name('alert') }
    : {profile:'standard-v2', ...Object.fromEntries(extraBindings[workloadType].map(b=>[b.field,name(b.field.toLowerCase())]))};
  const result = { schema: 'platform.workload-naming-draft/v1', workloadType, plan,
    parameterPatch: { parameters: { resourceNames: { value: resourceNames }, workloadResourceGroupName: { value: name('group') }, environmentName: { value: intent.environment }, location: { value: intent.region } } },
    deploymentAuthorized: false, allocationAuthorized: false,
    coverage: 'Calculated name candidates, not an expanded deployment inventory. Conditional components may be excluded by effective Bicep settings; only fresh What-If establishes actions. Application-contract children keep their existing names. Principal-dependent role IDs, shared prerequisites and provider-generated resources are not resolved by this draft. This partial parameter patch is not a deployment request.' };
  return { ...result, draftSha256: digest(result) };
}
