// Synthetic fixtures only. No tenant inventory, credentials or Azure calls.
import { canonical, digest } from '../../scripts/analysis/core.mjs';
export const subscription = '11111111-1111-1111-1111-111111111111';
export const rg = `/subscriptions/${subscription}/resourceGroups/rg-example`;
export const vnet = `${rg}/providers/Microsoft.Network/virtualNetworks/vnet-example`;
export const subnet = `${vnet}/subnets/integration`;
export const dns = `${rg}/providers/Microsoft.Network/privateDnsZones/privatelink.blob.core.windows.net`;
export const at = '2026-09-26T12:00:00.000Z';
export function fixture(type = 'blob-transfer', populated = false) {
  const inventory = { schemaVersion: 1, readOnly: true, generatedUtc: at, discoveryStatus: 'Complete', subscription: { id: subscription }, networks: [], privateDnsZones: [], networkQuery: { status: 'Succeeded', count: 0 }, privateDnsQuery: { status: 'Succeeded', count: 0 } };
  if (populated) {
    inventory.networks = [{ id: vnet, name: 'vnet-example', subnetQuery: { status: 'Succeeded', count: 1 }, subnets: [{ id: subnet, name: 'integration' }] }];
    inventory.privateDnsZones = [{ id: dns, name: 'privatelink.blob.core.windows.net' }]; inventory.networkQuery.count = 1; inventory.privateDnsQuery.count = 1;
  }
  if (type === 'logic-app-event-grid') Object.assign(inventory, { workloadType: type, resourceQuery: { status: 'Succeeded' }, resources: [], providers: ['Microsoft.Web', 'Microsoft.Storage', 'Microsoft.EventGrid', 'Microsoft.Insights', 'Microsoft.OperationalInsights'].map(namespace => ({ namespace, registrationState: 'Registered' })), prerequisiteOwnership: { status: 'Succeeded', managedResourceIds: [] }, prerequisitePlan: { schemaVersion: 1, subscriptionId: subscription, status: 'Ready', resources: [{ id: vnet, kind: 'virtualNetwork', action: populated ? 'Reuse' : 'Create' }, { id: subnet, kind: 'integrationSubnet', action: populated ? 'Reuse' : 'Create' }] } });
  const manifest = { schemaVersion: type === 'blob-transfer' ? 1 : 2, kind: type === 'blob-transfer' ? 'blob-transfer-discovery' : 'workload-discovery', ...(type === 'blob-transfer' ? {} : { workloadType: type }), generatedUtc: at, discoveryStatus: 'Complete', subscriptionId: subscription, selection: { workload: type === 'blob-transfer' ? 'blobcopy' : 'eventflow', environment: 'dev' }, source: { runId: '21', pipelineId: '2', projectId: 'synthetic-project', repositoryId: 'synthetic-repository', branch: 'refs/heads/main', commit: 'a'.repeat(40) } };
  return { inventory, manifest };
}
export function input(f) {
  const inventoryBytes = Buffer.from(canonical(f.inventory));
  const manifest = { ...f.manifest, inventorySha256: digest(inventoryBytes) };
  return { manifest, inventory: f.inventory, inventoryBytes, manifestBytes: Buffer.from(canonical(manifest)), evaluatedUtc: at };
}
