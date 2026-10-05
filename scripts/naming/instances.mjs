import { canonical, digest, resolveNames } from './core.mjs';

const fail = message => { throw new Error(message); };
const ordered = items => [...items].sort((a, b) => a.intent.instanceId < b.intent.instanceId ? -1 : a.intent.instanceId > b.intent.instanceId ? 1 : 0);
function shape(value, keys, label) {
  if (!value || Array.isArray(value) || typeof value !== 'object' || Object.keys(value).some(k => !keys.includes(k)) || keys.some(k => !Object.hasOwn(value, k))) fail(`${label}: exact contract fields required.`);
}

// A draft namespace reservation in source, not an Azure registration or deployable target.
export function validateInstances(registry, policy, catalog, previousRegistry = null) {
  function validate(source) {
    shape(source, ['schemaVersion', 'namingPolicySha256', 'instances'], 'registry');
    if (source.schemaVersion !== 1 || source.namingPolicySha256 !== digest(policy)) fail('Registry policy/version mismatch; explicit requalification required.');
    if (catalog.schemaVersion !== 1 || !catalog.workloads) fail('Unsupported workload catalog.');
    if (!Array.isArray(source.instances) || source.instances.length > 1000) fail('Registry requires at most 1000 draft instances.');
    const ids = new Set(), resourceIds = new Set(), results = [];
    for (const entry of source.instances) {
      shape(entry, ['workloadType', 'state', 'intent'], 'instance');
      if (typeof entry.workloadType !== 'string' || !Object.hasOwn(catalog.workloads, entry.workloadType)) fail('Unregistered workload type.');
      if (entry.state !== 'draft') fail('Only non-executable draft instances are supported.');
      const plan = resolveNames({ schemaVersion: 1, intent: entry.intent, components: [
        { key: 'resource-group', armType: 'Microsoft.Resources/resourceGroups', mode: 'convention', condition: 'included', role: 'primary' },
        { key: 'deployment-stack', armType: 'Microsoft.Resources/deploymentStacks', mode: 'convention', condition: 'included', role: 'primary' }
      ] }, policy);
      // Current offerings are private; a naming token must not invent an external topology.
      if (entry.intent.exposure !== 'internal') fail('Registered workloads currently support internal exposure only.');
      if (ids.has(entry.intent.instanceId)) fail('Duplicate instance ID.');
      ids.add(entry.intent.instanceId);
      for (const resource of plan.resources) {
        const id = resource.resourceId.toLowerCase();
        if (resourceIds.has(id)) fail('Duplicate stack/group identity across instances; choose a distinct acronym or ordinal.');
        resourceIds.add(id);
      }
      results.push({ instanceId: entry.intent.instanceId, workloadType: entry.workloadType, state: 'draft', identityPlan: plan });
    }
    return results.sort((a, b) => a.instanceId < b.instanceId ? -1 : a.instanceId > b.instanceId ? 1 : 0);
  }
  const instances = validate(registry);
  let previousRegistrySha256 = null;
  if (previousRegistry !== null) {
    validate(previousRegistry);
    const next = new Map(registry.instances.map(e => [e.intent.instanceId, e]));
    for (const prior of previousRegistry.instances) {
      const current = next.get(prior.intent.instanceId);
      if (!current) fail('Existing instance removal or ID replacement requires a separate retirement/migration workflow.');
      const identity = e => ({ workloadType: e.workloadType, intent: Object.fromEntries(Object.entries(e.intent).filter(([k]) => k !== 'stackDisplayName')) });
      if (canonical(identity(prior)) !== canonical(identity(current))) fail('Immutable instance identity changed; migration required.');
    }
    previousRegistrySha256 = digest({ ...previousRegistry, instances: ordered(previousRegistry.instances) });
  }
  const result = {
    schema: 'platform.stack-instance-validation/v1',
    registrySha256: digest({ ...registry, instances: ordered(registry.instances) }),
    policySha256: digest(policy), catalogSha256: digest(catalog), previousRegistrySha256,
    transitionChecked: previousRegistry !== null, instances,
    deploymentAuthorized: false, allocationAuthorized: false,
    limitations: [
      'Offline draft identity check only; no target, service connection or existing Azure ownership binding.',
      'Only resource-group and subscription Deployment Stack identities are calculated here, not the full workload resource inventory.',
      'Source reservations are not transactional; reconcile the complete registry and its reviewed previous version before merging concurrent drafts.',
      'Existing legacy targets are not migrated or compared for cloud collisions; Azure name availability and deployment qualification remain outstanding.'
    ]
  };
  return { ...result, receiptSha256: digest(result) };
}
