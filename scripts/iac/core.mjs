import { createHash } from 'node:crypto';

// Shared, deterministic evidence rules. No model output, parameter values or write operations.
export const schema = 'platform.iac-analysis/v1';
export const sha = data => createHash('sha256').update(data).digest('hex');
export const json = value => JSON.stringify(value, null, 2) + '\n';
export const limits = { resources: 500, bytes: 4 * 1024 * 1024 };
const paths = ['location','kind','sku.name','sku.tier','sku.capacity','identity.type',
  'properties.publicNetworkAccess','properties.allowBlobPublicAccess','properties.allowSharedKeyAccess',
  'properties.minimumTlsVersion','properties.supportsHttpsTrafficOnly','properties.enableRbacAuthorization',
  'properties.enablePurgeProtection','properties.softDeleteRetentionInDays','properties.httpsOnly',
  'properties.reserved','properties.virtualNetworkSubnetId','properties.subnet.id','properties.networkSecurityGroup.id',
  'properties.routeTable.id','properties.virtualNetwork.id','properties.privateLinkServiceId',
  'properties.apiServerAccessProfile.enablePrivateCluster','properties.disableLocalAccounts',
  'properties.oidcIssuerProfile.enabled','properties.securityProfile.workloadIdentity.enabled'];
export function scopeOf(target) {
  if (!/^[a-f0-9-]{36}$/i.test(target.subscriptionId) || !/^[\w.()-]{1,90}$/.test(target.resourceGroup)) throw Error('Invalid IaC subscription/resource-group scope.');
  return `/subscriptions/${target.subscriptionId}/resourceGroups/${target.resourceGroup}`;
}
export function inScope(id, target) {
  const scope = scopeOf(target).toLowerCase(), value = String(id).toLowerCase();
  return value === scope || value.startsWith(scope + '/providers/');
}
export function assertId(id, target) {
  if (typeof id !== 'string' || id.length > 2048 || /[?#%\\\r\n]/.test(id) || id.includes('..') || !inScope(id, target)) throw Error('Out-of-scope or malformed resource ID.');
  return id;
}
export function assertPlannedId(id,target) {
  scopeOf(target);
  if(typeof id!=='string'||id.length>2048||/[?#%\\\r\n]/.test(id)||id.includes('..')||
    !id.toLowerCase().startsWith(`/subscriptions/${target.subscriptionId}/`.toLowerCase()))throw Error('What-If resource is outside the selected subscription.');
  return id;
}
export function project(resource) {
  const config = {};
  for (const path of paths) {
    const value = path.split('.').reduce((v, p) => v?.[p], resource);
    // Never retain expressions, arbitrary objects, tags, app settings, outputs or credentials.
    if (typeof value === 'boolean' || (typeof value === 'number' && Number.isFinite(value)) ||
        (typeof value === 'string' && value.length <= 2048 && !/[\[\]${}\r\n]/.test(value))) config[path] = value;
  }
  return config;
}
export function resourceType(id) {
  const bits = id.split('/'), provider = bits.findLastIndex(v => v.toLowerCase() === 'providers');
  return provider < 0 ? 'Microsoft.Resources/resourceGroups' : [bits[provider+1], ...bits.slice(provider+2).filter((_, i) => i % 2 === 0)].join('/');
}
export function versions(template) {
  const map = {};
  function walk(v) {
    if (!v || typeof v !== 'object') return;
    if (typeof v.type === 'string' && /^[\w.]+\/[\w/]+$/.test(v.type) && /^\d{4}-\d{2}-\d{2}(-preview)?$/.test(v.apiVersion)) map[v.type.toLowerCase()] = v.apiVersion;
    for (const x of Object.values(v)) walk(x);
  }
  walk(template); map['microsoft.resources/resourcegroups'] = '2022-09-01'; return map;
}
export function planned(raw, target) {
  const p = raw.properties;
  const changes = p?.changes?.resourceChanges ?? raw.changes;
  if (!Array.isArray(changes) || changes.length > limits.resources) throw Error('What-If changes missing or exceed the 500-resource analysis limit.');
  const ids = new Set();
  const resources = changes.map(c => {
    const id = assertPlannedId(c.id ?? c.resourceId, target), key = id.toLowerCase();
    if (ids.has(key)) throw Error('Duplicate What-If resource.'); ids.add(key);
    const action = ['Create','Modify','Delete','NoChange','Ignore','Deploy','Unsupported','Detach'].find(x => x.toLowerCase() === String(c.changeType).toLowerCase()) ?? 'Unknown';
    const configuration = c.resourceConfigurationChanges ?? c;
    return { id, type: resourceType(id), action, certainty: c.changeCertainty ?? 'unknown',
      config: project(configuration.after ?? {}), changedPaths: flatten(configuration.delta ?? []),
      status: ['Ignore','Unsupported','Deploy','Unknown','Detach'].includes(action) ? 'Unresolved' : 'Predicted' };
  });
  return { status: (p?.provisioningState ?? raw.status) === 'Succeeded' ? 'Succeeded' : 'Incomplete', resources,
    coverage: 'Azure What-If prediction, not observed final state. Only allowlisted scalar properties retained; omitted properties and diagnostics require review in the original artifact.' };
}
function flatten(deltas, prefix = '', depth = 0) {
  if (depth > 20 || !Array.isArray(deltas) || deltas.length > 1000) throw Error('What-If property deltas exceed limits.');
  return deltas.flatMap(d => { const p = prefix + String(d.path ?? ''); return [paths.includes(p) ? p : 'Unprojected property', ...flatten(d.children ?? [], p + '.', depth + 1)]; }).filter((v, i, a) => a.indexOf(v) === i);
}
export function compare(plan, after) {
  return plan.resources.map(expected => {
    const found = after.resources.find(r => r.id.toLowerCase() === expected.id.toLowerCase());
    let status = 'Unknown'; const mismatches = [], checked = [], unknown = [];
    if (plan.status === 'Succeeded' && expected.certainty === 'definite' && expected.status !== 'Unresolved' && found) {
      if (expected.action === 'Delete') status = found.status === 'Absent' ? 'Observed absent' : found.status === 'Observed' ? 'Mismatch' : 'Unknown';
      else if (found.status === 'Absent') status = 'Mismatch';
      else if (found.status === 'Observed') {
        for (const [p, v] of Object.entries(expected.config)) {
          if (Object.hasOwn(found.config, p)) { checked.push(p); if (JSON.stringify(v) !== JSON.stringify(found.config[p])) mismatches.push(p); } else unknown.push(p);
        }
        status = mismatches.length ? 'Mismatch' : checked.length ? (unknown.length ? 'Partial projected match' : 'Projected properties match') : 'Observed existence only';
      }
    }
    return { id: expected.id, action: expected.action, status, checkedPaths: checked, mismatchPaths: mismatches, unknownPaths: unknown,
      unverified: 'All omitted properties, data-plane behavior, network reachability and application readiness remain outside this comparison.' };
  });
}
export function bicep(snapshot, target) {
  const lines = ["// Observed identity inventory only. NOT a restore template or a deployment plan.",
    '// Existing declarations do not reconstruct properties, secrets, role grants or resource data.',
    "targetScope = 'subscription'", ''];
  let count = 0;
  for (const r of snapshot.resources) {
    if (r.status !== 'Observed' || !r.apiVersion) continue;
    assertPlannedId(r.id, target); const bits = r.id.split('/');
    if (bits.length < 9 || bits.filter(b => b.toLowerCase() === 'providers').length !== 1) continue;
    const name = bits.slice(8).filter((_, i) => i % 2 === 0).join('/');
    if (bits[3].toLowerCase()!=='resourcegroups'||!/^[\w.()-]{1,90}$/.test(bits[4])||!/^[\w.() /-]+$/.test(name) || !/^[\w.]+\/[\w/]+$/.test(r.type)) continue;
    lines.push(`resource observed${++count} '${r.type}@${r.apiVersion}' existing = {`, `  scope: resourceGroup('${target.subscriptionId}', '${bits[4]}')`, `  name: '${name}'`, '}', '');
  }
  lines.push(`// ${count} representable observed identities. Other resource identities remain in analysis.json.`);
  return lines.join('\n') + '\n';
}
