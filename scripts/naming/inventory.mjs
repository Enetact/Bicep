import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { digest } from './core.mjs';

const root = fileURLToPath(new URL('../../', import.meta.url));
function walk(directory) { return fs.readdirSync(directory, { withFileTypes: true }).flatMap(e => e.isDirectory() ? walk(path.join(directory, e.name)) : e.name.endsWith('.bicep') ? [path.join(directory, e.name)] : []); }
// Source inventory only, not a Bicep evaluator or proof that conditional resources are created.
export function inventory(policy) {
  const declarations = [];
  for (const file of [...walk(path.join(root, 'modules')), ...walk(path.join(root, 'workloads'))].sort()) {
    const source = fs.readFileSync(file, 'utf8');
    for (const match of source.matchAll(/^\s*resource\s+(\w+)\s+'([^'@]+)@([^']+)'\s*(existing\s*)?=/gm)) {
      const [, symbol, type, apiVersion, existing] = match;
      if (!Object.hasOwn(policy.rules, type)) throw new Error(`Unclassified ARM type: ${type}`);
      declarations.push({ file: path.relative(root, file).replaceAll('\\', '/'), line: source.slice(0, match.index).split('\n').length, symbol, type, apiVersion, existing: Boolean(existing), classification: policy.rules[type].classification });
    }
  }
  return { schema: 'platform.naming-source-inventory/v1', kind: 'source-classification-only', policySha256: digest(policy), types: [...new Set(declarations.map(r => r.type))].sort(), declarations,
    inlineAndServiceContracts: [
      { file: 'modules/network/workload-vnet/main.bicep', field: 'properties.subnets', type: 'Microsoft.Network/virtualNetworks/subnets', classification: 'convention' },
      { file: 'workloads/blob-transfer/modules/network.bicep', field: 'properties.subnets', type: 'Microsoft.Network/virtualNetworks/subnets', classification: 'convention' },
      { file: 'modules/compute/private-aks/main.bicep', field: 'properties.agentPoolProfiles', type: 'Microsoft.ContainerService/managedClusters/agentPools', classification: 'applicationContract' },
      { file: 'modules/compute/private-aks/main.bicep', field: 'properties.dnsPrefix', classification: 'applicationContract' },
      { file: 'modules/analytics/databricks-workspace/main.bicep', field: 'properties.managedResourceGroupId', classification: 'explicitManagedScope' },
      { file: 'modules/network/private-endpoint/main.bicep', field: 'properties.networkInterfaces', classification: 'providerAssigned' },
      { file: 'modules/compute/private-aks/main.bicep', field: 'properties.nodeResourceGroup', classification: 'explicitManagedScope' },
      { file: 'modules/compute/private-aks/main.bicep', field: 'service-generated child resources inside the explicitly named node resource group', classification: 'providerAssigned' }
    ], deploymentAuthorized: false, limitations: ['Declarations include existing references, conditionals and repeated module source; not a deployment resource count.', 'Inline/service contracts are a reviewed supplemental inventory; source role bindings and runtime references still need N3 qualification.'] };
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  const policy = JSON.parse(fs.readFileSync(path.join(root, 'config/naming-policy.json'), 'utf8'));
  process.stdout.write(JSON.stringify(inventory(policy), null, 2) + '\n');
}
