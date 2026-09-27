// Pure saved-evidence analysis. No filesystem, Azure, network or process access.
import { createHash } from 'node:crypto';

export const RULE_VERSION = '1.0.0';
export const MAX_RESOURCES = 5000;
const kinds = new Map([[1, ['blob-transfer-discovery', 'blob-transfer']], [2, ['workload-discovery', 'logic-app-event-grid']]]);
const guid = /^[a-f0-9]{8}-(?:[a-f0-9]{4}-){3}[a-f0-9]{12}$/i;
const own = (v, k) => Object.hasOwn(v ?? {}, k);
const object = v => v !== null && typeof v === 'object' && !Array.isArray(v);
function requireValue(ok, message) { if (!ok) throw new Error(message); }
export function digest(bytes) { return createHash('sha256').update(bytes).digest('hex'); }
export function canonical(value) {
  if (Array.isArray(value)) return `[${value.map(canonical).join(',')}]`;
  if (object(value)) return `{${Object.keys(value).sort().map(k => `${JSON.stringify(k)}:${canonical(value[k])}`).join(',')}}`;
  return JSON.stringify(value);
}
function array(v, name) { requireValue(Array.isArray(v) && v.length <= MAX_RESOURCES, `Invalid or oversized ${name}.`); return v; }
function date(v) { requireValue(typeof v === 'string' && /^\d{4}-\d{2}-\d{2}T/.test(v) && Number.isFinite(Date.parse(v)), 'Invalid evidence timestamp.'); return new Date(v).toISOString(); }
function identifier(v, name) { requireValue(typeof v === 'string' && /^[a-zA-Z0-9][a-zA-Z0-9_.-]{0,79}$/.test(v), `Invalid ${name}.`); return v; }
function resource(v) {
  requireValue(typeof v === 'string' && v.length <= 1500, 'Invalid resource identifier.');
  const parts = v.split('/');
  requireValue(parts[0] === '' && parts[1]?.toLowerCase() === 'subscriptions' && guid.test(parts[2]) && parts[3]?.toLowerCase() === 'resourcegroups' && parts[4] &&
    (parts.length === 5 || (parts[5]?.toLowerCase() === 'providers' && parts.length >= 9 && parts.length % 2 === 1)) &&
    parts.slice(4).every(p => /^[a-zA-Z0-9_.() -]+$/.test(p)), 'Unsupported or unsafe resource identifier.');
  return v.toLowerCase();
}
function resourceType(id) { const p = id.split('/'); return p.length === 5 ? 'microsoft.resources/resourcegroups' : [p[6], ...p.slice(7).filter((_, i) => i % 2 === 0)].join('/'); }

export function analyzeDiscovery({ manifest, inventory, inventoryBytes, manifestBytes, workload, environment, evaluatedUtc }) {
  requireValue(object(manifest) && object(inventory), 'Expected manifest and inventory objects.');
  const kind = kinds.get(manifest.schemaVersion);
  requireValue(kind && manifest.kind === kind[0] && (manifest.schemaVersion !== 2 || manifest.workloadType === kind[1]), 'Unsupported discovery schema or workload.');
  const type = kind[1];
  requireValue(inventory.schemaVersion === 1 && inventory.readOnly === true && (!own(inventory, 'workloadType') || inventory.workloadType === type) && (type !== 'logic-app-event-grid' || inventory.workloadType === type), 'Unsupported inventory contract.');
  requireValue(typeof manifest.inventorySha256 === 'string' && manifest.inventorySha256 === digest(inventoryBytes), 'Inventory bytes do not match the manifest.');
  requireValue(guid.test(manifest.subscriptionId) && manifest.subscriptionId.toLowerCase() === inventory.subscription?.id?.toLowerCase(), 'Discovery subscription mismatch.');
  const instance = identifier(manifest.selection?.workload, 'workload selection');
  const env = identifier(manifest.selection?.environment, 'environment selection');
  requireValue(!workload || workload === instance, 'Requested workload differs from discovery.');
  requireValue(!environment || environment === env, 'Requested environment differs from discovery.');
  const generatedUtc = date(manifest.generatedUtc);
  requireValue(generatedUtc === date(inventory.generatedUtc) && manifest.discoveryStatus === inventory.discoveryStatus && ['Complete', 'Partial'].includes(inventory.discoveryStatus), 'Discovery status/time mismatch.');
  const now = date(evaluatedUtc);
  const findings = [];
  const addFinding = (ruleId, outcome, message, evidenceRefs, required = true) => findings.push({ ruleId, outcome, message, evidenceRefs, required });
  const coverage = [];
  const nodes = new Map();
  const edges = [];
  const subscriptionId = manifest.subscriptionId.toLowerCase();
  const observedIds = new Set();
  const idFor = id => 'r' + digest(id).slice(0, 20);
  const addNode = (rawId, state, ref, action = 'Unspecified', ownership = 'Unknown') => {
    const id = resource(rawId);
    requireValue(id.split('/')[2] === subscriptionId, 'Resource is outside the saved subscription scope.');
    if (!nodes.has(id)) {
      requireValue(nodes.size < MAX_RESOURCES, 'Analysis resource limit exceeded.');
      nodes.set(id, { id: idFor(id), resourceId: id, label: id.split('/').at(-1), type: resourceType(id), states: [], action: 'Unspecified', ownership: 'Unknown', runtime: 'Unverified', evidenceRefs: [] });
    }
    const n = nodes.get(id);
    if (!n.states.includes(state)) n.states.push(state);
    if (!n.evidenceRefs.includes(ref)) n.evidenceRefs.push(ref);
    if (action !== 'Unspecified') n.action = action;
    if (ownership !== 'Unknown') n.ownership = ownership;
    if (state === 'Observed') observedIds.add(id);
    return n;
  };
  const query = (name, raw, count, ref, required = true) => {
    const state = raw?.status === 'Succeeded' && !raw?.truncated && !raw?.resultTruncated && !raw?.nextLink && !raw?.skipToken && (!own(raw, 'count') || raw.count === count) ? 'ReportedComplete' : 'Unknown';
    coverage.push({ collection: name, state, count, evidenceRef: ref, required });
    addFinding(`coverage.${name}`, state === 'Unknown' ? 'unknown' : 'pass', state === 'Unknown' ? 'Collection is failed, incomplete, unavailable or inconsistent; empty does not mean absent.' : 'Saved query reports success; completeness was not independently verified.', [ref], required);
    return state === 'ReportedComplete';
  };
  const age = Date.parse(now) - Date.parse(generatedUtc);
  addFinding('evidence.freshness', age > 7 * 86400000 || age < -300000 ? 'fail' : 'pass', 'Saved discovery must be no older than seven days and no more than five minutes in the future.', ['manifest.json#/generatedUtc']);
  addFinding('discovery.status', inventory.discoveryStatus === 'Complete' ? 'pass' : 'fail', 'Partial discovery cannot establish deployment prerequisites.', ['inventory.json#/discoveryStatus']);
  const source = manifest.source;
  const provenanceClaim = object(source) && /^\d+$/.test(source.runId ?? '') && /^\d+$/.test(source.pipelineId ?? '') && source.branch === 'refs/heads/main' && /^[a-f0-9]{40}$/i.test(source.commit ?? '') && !!source.projectId && !!source.repositoryId;
  addFinding('provenance.saved-claim', provenanceClaim ? 'pass' : 'unknown', 'Saved metadata is a claim; this offline tool does not authenticate ADO producer, run success or protected branch.', ['manifest.json#/source']);
  addFinding('provenance.authenticated', 'unknown', 'Use the existing authenticated deployment handoff checks; an offline report never authorizes deployment.', ['manifest.json#/source']);
  const networks = array(inventory.networks, 'network collection');
  const zones = array(inventory.privateDnsZones, 'DNS collection');
  let complete = query('networks', inventory.networkQuery, networks.length, 'inventory.json#/networkQuery');
  const seenNetworks = new Set();
  for (const [i, net] of networks.entries()) {
    requireValue(object(net), 'Invalid network record.');
    const netId = resource(net.id); requireValue(!seenNetworks.has(netId), 'Duplicate network resource.'); seenNetworks.add(netId);
    const parent = addNode(net.id, 'Observed', `inventory.json#/networks/${i}`);
    const subnets = array(net.subnets, 'subnet collection');
    complete = query(`subnets.${parent.id}`, net.subnetQuery, subnets.length, `inventory.json#/networks/${i}/subnetQuery`) && complete;
    const seen = new Set();
    for (const [j, subnet] of subnets.entries()) {
      const childId = resource(subnet.id);
      requireValue(childId.startsWith(netId + '/subnets/') && childId.split('/').length === netId.split('/').length + 2 && !seen.has(childId), 'Invalid or duplicate subnet containment.'); seen.add(childId);
      addNode(subnet.id, 'Observed', `inventory.json#/networks/${i}/subnets/${j}`);
    }
  }
  complete = query('privateDnsZones', inventory.privateDnsQuery, zones.length, 'inventory.json#/privateDnsQuery') && complete;
  const seenZones = new Set();
  zones.forEach((z, i) => { const id = resource(z.id); requireValue(!seenZones.has(id), 'Duplicate DNS resource.'); seenZones.add(id); addNode(id, 'Observed', `inventory.json#/privateDnsZones/${i}`); });
  if (type === 'logic-app-event-grid') {
    const resources = array(inventory.resources, 'resource collection');
    complete = query('resources', inventory.resourceQuery, resources.length, 'inventory.json#/resourceQuery') && complete;
    // A subscription catalog contains unrelated workloads. Only selected prerequisites
    // and already inventoried network/DNS nodes belong in this workload report.
    const referenced = new Set(array(inventory.prerequisitePlan?.resources ?? [], 'prerequisite resources').map(r => resource(r.id)));
    const seen = new Set();
    resources.forEach((r, i) => { const id = resource(r.id); requireValue(!seen.has(id), 'Duplicate resource catalog entry.'); seen.add(id); if (referenced.has(id) || nodes.has(id)) addNode(id, 'Observed', `inventory.json#/resources/${i}`); });
    const providers = array(inventory.providers, 'provider collection');
    const required = ['Microsoft.Web', 'Microsoft.Storage', 'Microsoft.EventGrid', 'Microsoft.Insights', 'Microsoft.OperationalInsights'];
    for (const ns of required) {
      const matches = providers.filter(p => p.namespace === ns);
      addFinding(`provider.${ns}`, matches.length === 1 && matches[0].registrationState === 'Registered' ? 'pass' : 'unknown', 'Saved provider registration is required but does not establish permissions or capacity.', ['inventory.json#/providers']);
    }
  }
  const plan = inventory.prerequisitePlan;
  if (plan !== undefined) {
    requireValue(type === 'logic-app-event-grid' && object(plan) && plan.schemaVersion === 1 && plan.subscriptionId?.toLowerCase() === subscriptionId && ['Ready', 'Blocked'].includes(plan.status), 'Unsupported prerequisite plan.');
    const ownership = inventory.prerequisiteOwnership;
    const ownerKnown = ownership?.status === 'Succeeded';
    const managed = new Set(ownerKnown ? array(ownership.managedResourceIds, 'owned resources').map(resource) : []);
    const planReady = complete && inventory.discoveryStatus === 'Complete' && ownerKnown && plan.status === 'Ready';
    addFinding('prerequisites.saved-plan', planReady ? 'pass' : 'fail', 'Reported actions are informational; current target/policy and Azure state must be revalidated by the deployment resolver.', ['inventory.json#/prerequisitePlan']);
    const seen = new Set();
    array(plan.resources, 'prerequisite resources').forEach((r, i) => {
      const id = resource(r.id); requireValue(!seen.has(id) && ['Create', 'Reuse', 'Manage', 'Blocked'].includes(r.action), 'Ambiguous prerequisite action.'); seen.add(id);
      let action = planReady ? r.action : 'Blocked';
      const inconsistent = (action === 'Create' && (observedIds.has(id) || managed.has(id))) || (action === 'Manage' && !managed.has(id)) || (action === 'Reuse' && (!observedIds.has(id) || managed.has(id)));
      if (inconsistent) { action = 'Blocked'; addFinding(`ownership.${idFor(id)}`, 'fail', 'Reported prerequisite action conflicts with saved existence or ownership evidence.', [`inventory.json#/prerequisitePlan/resources/${i}`]); }
      addNode(id, 'Proposed', `inventory.json#/prerequisitePlan/resources/${i}`, action, managed.has(id) ? 'ReportedOwned' : action === 'Reuse' ? 'ReportedExternal' : 'Unknown');
    });
  } else addFinding('prerequisites.not-supplied', 'unknown', 'This saved artifact contains no resource-level prerequisite plan; missing resources are not automatically proposed for creation.', ['inventory.json'], false);
  // Resource-ID parentage is containment, never a claim of packet reachability.
  for (const n of nodes.values()) {
    const p = n.resourceId.split('/');
    if (p.length > 9) {
      const parent = nodes.get(p.slice(0, -2).join('/'));
      if (parent) edges.push({ from: parent.id, to: n.id, relation: 'Contains', state: parent.states.includes('Observed') && n.states.includes('Observed') ? 'Observed' : 'Proposed', evidenceRefs: [...n.evidenceRefs] });
    }
  }
  const requiredFailures = findings.some(f => f.required && f.outcome === 'fail') || [...nodes.values()].some(n => n.action === 'Blocked');
  return {
    schemaVersion: 1, kind: 'platform-offline-analysis', ruleSetVersion: RULE_VERSION,
    context: { workloadType: type, workload: instance, environment: env, subscriptionId, generatedUtc, evaluatedUtc: now, manifestSha256: digest(manifestBytes), inventorySha256: digest(inventoryBytes), provenance: 'UnverifiedOffline', scope: 'SavedSubscriptionOnly' },
    status: requiredFailures ? 'blocked' : 'partial', deploymentAuthorized: false,
    coverage, findings,
    graph: { nodes: [...nodes.values()].map(n => ({ ...n, states: n.states.sort(), evidenceRefs: n.evidenceRefs.sort() })).sort((a, b) => a.resourceId < b.resourceId ? -1 : a.resourceId > b.resourceId ? 1 : 0), edges: edges.sort((a, b) => a.from + a.to < b.from + b.to ? -1 : a.from + a.to > b.from + b.to ? 1 : 0) },
    limitations: ['Offline consistency only; no authenticated ADO or live Azure checks.', 'Query completeness and ownership are saved claims, not independent proof.', 'Network/DNS inventory may include shared resources; unrelated ARM catalog resources are omitted.', 'Only resource containment is drawn; DNS resolution, routing, private connectivity and runtime health are unverified.', 'Proposed actions come from saved prerequisites, not a new allocation or permission decision.']
  };
}
