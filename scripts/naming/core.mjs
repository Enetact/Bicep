import { createHash } from 'node:crypto';

// Pure offline calculations. Callers supply reviewed component descriptors, not executable expressions.
export const canonical = value => {
  if (Array.isArray(value)) return '[' + value.map(canonical).join(',') + ']';
  if (value !== null && typeof value === 'object') return '{' + Object.keys(value).sort().map(k => JSON.stringify(k) + ':' + canonical(value[k])).join(',') + '}';
  return JSON.stringify(value);
};
export const digest = value => createHash('sha256').update(canonical(value), 'utf8').digest('hex');
const fail = message => { throw new Error(message); };
const uuid = /^[0-9a-f]{8}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{4}-[0-9a-f]{12}$/;
const keyPattern = /^[a-z][a-z0-9.-]{0,79}$/;
function shape(value, required, optional, label) {
  if (!value || Array.isArray(value) || typeof value !== 'object') fail(`${label}: object required.`);
  for (const key of Object.keys(value)) if (![...required, ...optional].includes(key)) fail(`${label}: unsupported field ${key}.`);
  for (const key of required) if (!Object.hasOwn(value, key)) fail(`${label}: missing ${key}.`);
}
function token(value, pattern, label) { if (typeof value !== 'string' || !pattern.test(value)) fail(`${label}: invalid value.`); }
function printable(value, max, label) { if (typeof value !== 'string' || !value.trim() || value.length > max || /[\x00-\x1f\x7f]/.test(value)) fail(`${label}: invalid text.`); }
function suffix(value, policy) {
  const bytes = createHash('sha256').update(canonical(value), 'utf8').digest();
  let bits = 0, accumulated = 0, result = '';
  for (const byte of bytes) {
    accumulated = (accumulated << 8) | byte; bits += 8;
    while (bits >= 5 && result.length < policy.hash.length) { bits -= 5; result += policy.hash.alphabet[(accumulated >>> bits) & 31]; }
    if (result.length === policy.hash.length) break;
  }
  return result;
}

// Preserve display casing; only comparisons are case-insensitive. Extension providers start a new type chain.
export function parseArmId(id) {
  if (typeof id !== 'string' || id.length > 2048 || /[\s?#%\\\x00-\x1f]/.test(id) || id.endsWith('/')) fail('Invalid ARM ID.');
  const p = id.split('/');
  if (p[0] !== '' || p[1]?.toLowerCase() !== 'subscriptions' || !uuid.test(p[2]?.toLowerCase() ?? '')) fail('Subscription-qualified ARM ID required.');
  let i = 3, type = 'Microsoft.Resources/subscriptions', names = [p[2]], parentId = null;
  if (p[i]?.toLowerCase() === 'resourcegroups') {
    if (!p[i + 1] || p[i + 1] === '.' || p[i + 1] === '..') fail('Invalid resource group ID.');
    parentId = '/' + p.slice(1, i).join('/'); names = [p[i + 1]]; type = 'Microsoft.Resources/resourceGroups'; i += 2;
  }
  while (i < p.length) {
    if (p[i]?.toLowerCase() !== 'providers' || !/^[a-z][a-z0-9.]+$/i.test(p[i + 1] ?? '')) fail('Invalid ARM provider segment.');
    let provider = p[i + 1], types = []; names = []; i += 2;
    while (i < p.length && p[i].toLowerCase() !== 'providers') {
      if (!/^[a-z][a-z0-9]*$/i.test(p[i]) || !p[i + 1] || ['.', '..'].includes(p[i + 1])) fail('Invalid ARM child segment.');
      parentId = '/' + p.slice(1, types.length ? i : i - 2).join('/');
      types.push(p[i]); names.push(p[i + 1]); i += 2;
    }
    if (!types.length) fail('Empty ARM provider path.');
    type = provider + '/' + types.join('/');
  }
  return { id, subscriptionId: p[2], type, name: names.at(-1), fullName: names.join('/'), parentId };
}
function fixedName(name, rule) {
  printable(name, 260, 'explicitName');
  if (/[\\/?#%]/.test(name) || name === '.' || name === '..') fail('Explicit child name cannot contain path syntax.');
  if (rule.exception === 'guid' && !uuid.test(name)) fail('Explicit GUID name required; preserve the existing GUID algorithm.');
  if (rule.exception === 'default' && name !== 'default') fail('This service child must be named default.');
  if (rule.exception === 'dns' && (!/^(?:[a-z0-9](?:[a-z0-9-]*[a-z0-9])?\.)+[a-z][a-z0-9-]*$/.test(name) || name.length > 253)) fail('Explicit DNS zone FQDN required.');
  if (rule.exception === 'webConfig' && !['web', 'appsettings', 'authsettingsV2', 'connectionstrings', 'logs'].includes(name)) fail('Unqualified Web configuration child.');
  if (rule.exception === 'publishing' && !['ftp', 'scm'].includes(name)) fail('Unqualified publishing policy child.');
  if (rule.exception === 'storageChild' && (name.length < 3 || name.length > 63 || !/^[a-z0-9]+(?:-[a-z0-9]+)*$/.test(name))) fail('Invalid storage contract name.');
}
function qualifiedId(type, name, scope, rule) {
  const parent = parseArmId(scope);
  if (type === 'Microsoft.Resources/resourceGroups') {
    if (parent.type !== 'Microsoft.Resources/subscriptions') fail('Resource group requires subscription scope.');
    return `${scope}/resourceGroups/${name}`;
  }
  if (rule.scope === 'child') {
    if (type.slice(0, type.lastIndexOf('/')).toLowerCase() !== parent.type.toLowerCase()) fail('Child type differs from its parent scope.');
    return `${scope}/${type.split('/').at(-1)}/${name}`;
  }
  if (rule.scope === 'resourceGroup' && parent.type !== 'Microsoft.Resources/resourceGroups') fail('Resource requires resource-group scope.');
  if (rule.scope === 'subscription' && parent.type !== 'Microsoft.Resources/subscriptions') fail('Resource requires subscription scope.');
  return `${scope}/providers/${type}/${name}`;
}

export function resolveNames(input, policy) {
  shape(input, ['schemaVersion', 'intent', 'components'], [], 'request');
  if (input.schemaVersion !== 1 || policy.schemaVersion !== 1 || policy.id !== 'standard-v2' || policy.hash.algorithm !== 'sha256-base32' || policy.hash.length !== 6 || policy.hash.alphabet !== 'abcdefghijklmnopqrstuvwxyz234567') fail('Unsupported naming contract/policy.');
  const intent = input.intent;
  shape(intent, ['namingProfile', 'stackDisplayName', 'stackAcronym', 'instanceId', 'instanceOrdinal', 'environment', 'exposure', 'region', 'subscriptionId', 'namespaceSalt'], [], 'intent');
  if (intent.namingProfile !== policy.id) fail('Legacy naming remains owned by existing adapters; no implicit migration.');
  printable(intent.stackDisplayName, 80, 'stackDisplayName');
  token(intent.stackAcronym, /^[a-z][a-z0-9]{1,3}$/, 'stackAcronym');
  token(intent.instanceId, uuid, 'instanceId'); token(intent.subscriptionId, uuid, 'subscriptionId');
  token(intent.instanceOrdinal, /^(?:0[1-9]|[1-9][0-9])$/, 'instanceOrdinal');
  token(intent.namespaceSalt, /^[a-z0-9]{0,16}$/, 'namespaceSalt');
  if (!policy.environments.includes(intent.environment) || !Object.hasOwn(policy.regions, intent.region) || !Object.hasOwn(policy.exposures, intent.exposure)) fail('Unsupported environment, region or exposure.');
  if (!Array.isArray(input.components) || input.components.length < 1 || input.components.length > 1000) fail('Supply 1–1000 component descriptors.');
  const components = new Map(), rows = new Map(), visiting = new Set(), identities = new Set();
  for (const c of input.components) {
    shape(c, ['key', 'armType', 'mode', 'condition'], ['role', 'parentKey', 'scope', 'explicitName', 'existingResourceId', 'reason'], 'component');
    token(c.key, keyPattern, 'component key');
    if (components.has(c.key)) fail('Duplicate component key.');
    if (typeof c.armType !== 'string' || !Object.hasOwn(policy.rules, c.armType)) fail('Unsupported ARM type; add a reviewed rule.');
    if (!['convention', 'fixed', 'reference', 'providerAssigned', 'unresolved'].includes(c.mode) || !['included', 'excluded', 'unknown'].includes(c.condition)) fail('Unsupported component mode/condition.');
    if (c.parentKey !== undefined) token(c.parentKey, keyPattern, 'parentKey');
    if (c.parentKey && c.scope) fail('Supply parentKey or explicit scope, not both.');
    if (c.scope !== undefined) parseArmId(c.scope);
    if (c.reason !== undefined) printable(c.reason, 512, 'reason');
    if (c.mode === 'reference' && (c.scope || c.parentKey || c.explicitName || c.role)) fail('Reference accepts only the exact existing ID.');
    if (c.mode !== 'reference' && c.existingResourceId !== undefined) fail('Existing ID is only valid for reference mode.');
    if (c.mode !== 'fixed' && c.explicitName !== undefined) fail('Explicit name is only valid for fixed exceptions.');
    if (c.mode !== 'convention' && c.role !== undefined) fail('Role is only valid for convention mode.');
    const rule = policy.rules[c.armType];
    if (c.mode === 'convention' && (rule.classification !== 'convention' || !Object.hasOwn(rule.roles, c.role))) fail('Unqualified convention role or resource classification.');
    if (c.mode === 'fixed') {
      if (rule.classification === 'convention' || rule.classification === 'shared') fail('No explicit override for convention or shared resources.');
      fixedName(c.explicitName, rule);
    }
    if (c.mode === 'reference' && parseArmId(c.existingResourceId).type.toLowerCase() !== c.armType.toLowerCase()) fail('Referenced resource type mismatch.');
    components.set(c.key, c);
  }
  function resolve(key) {
    if (rows.has(key)) return rows.get(key);
    const c = components.get(key); if (!c) fail('Unknown parent component.');
    if (visiting.has(key)) fail('Component parent cycle.'); visiting.add(key);
    const rule = policy.rules[c.armType];
    const parent = c.parentKey ? resolve(c.parentKey) : null;
    if (c.condition === 'included' && parent?.status === 'Excluded') fail('Included child cannot use an excluded parent.');
    let scope = parent?.resourceId ?? c.scope ?? (rule.scope === 'subscription' ? `/subscriptions/${intent.subscriptionId}` : null);
    let name = null, resourceId = null, format = null, status = 'Unresolved', reason = c.reason ?? rule.reason ?? '';
    if (scope && parseArmId(scope).subscriptionId.toLowerCase() !== intent.subscriptionId) fail('Owned component scope differs from selected subscription.');
    if (c.condition === 'excluded') status = 'Excluded';
    else if (c.condition === 'unknown') reason = 'Condition is unresolved; no resource name asserted.';
    else if (c.mode === 'providerAssigned') { status = 'ProviderAssigned'; reason ||= 'Service assigns the final name; observe it after deployment.'; }
    else if (c.mode === 'unresolved') { if (!reason) fail('Unresolved component requires a reason.'); }
    else if (c.mode === 'reference') {
      const parsed = parseArmId(c.existingResourceId);
      if (parsed.type.toLowerCase() !== c.armType.toLowerCase()) fail('Referenced resource type mismatch.');
      name = parsed.name; resourceId = parsed.id; scope = parsed.parentId; status = 'Referenced'; format = 'preserved';
      reason = 'Caller-supplied reference; existence, access and ownership are unverified.';
    } else if (c.mode === 'fixed') {
      if (rule.classification === 'convention' || rule.classification === 'shared') fail('No explicit override for convention or shared resources.');
      fixedName(c.explicitName, rule); name = c.explicitName; status = 'ExplicitException'; format = 'preserved';
    } else {
      if (rule.classification !== 'convention' || !Object.hasOwn(rule.roles, c.role)) fail('Unqualified convention role or resource classification.');
      const abbreviation = rule.roleAbbreviations?.[c.role] ?? rule.abbreviation;
      const base = [intent.environment, abbreviation, intent.stackAcronym, policy.exposures[intent.exposure], policy.regions[intent.region]];
      // Global suffix carries instance/ordinal/role identity. Local names retain explicit ordinal.
      const hash = suffix({ profile: policy.id, subscriptionId: intent.subscriptionId, instanceId: intent.instanceId, instanceOrdinal: intent.instanceOrdinal, environment: intent.environment, acronym: intent.stackAcronym, exposure: intent.exposure, region: intent.region, type: c.armType, role: c.role, key: c.key, salt: intent.namespaceSalt }, policy);
      const end = rule.globalNamespace ? [rule.roles[c.role], hash] : [...(c.role === 'primary' ? [] : [c.role]), intent.instanceOrdinal];
      format = rule.format; name = [...base, ...end].join(format === 'compact' ? '' : '-');
      if (name.length > rule.maxLength && rule.compactFallback) { format = 'compact'; name = [...base, ...end].join(''); reason = 'Compact format selected to preserve required tokens within the resource length limit.'; }
      if (name.length < rule.minLength || name.length > rule.maxLength || !new RegExp(rule.pattern).test(name)) fail(`${c.key}: calculated name exceeds resource rules (${name.length} characters; allowed ${rule.minLength}–${rule.maxLength}). No tokens truncated.`);
      status = 'Calculated';
    }
    if (name && !resourceId && scope) {
      if (parent && ['Excluded', 'Unresolved', 'ProviderAssigned'].includes(parent.status)) fail('Included child cannot use an unresolved/excluded parent.');
      resourceId = qualifiedId(c.armType, name, scope, rule);
    }
    if (name && !resourceId) reason = [reason, 'Scope unresolved; no ARM ID asserted.'].filter(Boolean).join(' ');
    const identity = name ? (rule.globalNamespace ? `global|${c.armType}|${name}` : scope ? `${c.armType}|${scope}|${name}` : null) : null;
    if (identity && identities.has(identity.toLowerCase())) fail('Duplicate resource identity in the calculated plan.');
    if (identity) identities.add(identity.toLowerCase());
    const row = { componentKey: key, armType: c.armType, parentKey: c.parentKey ?? null, name, fullName: resourceId ? parseArmId(resourceId).fullName : name, resourceId, scope, role: c.role ?? null, condition: c.condition, status, format, classification: rule.classification, ownership: c.mode === 'reference' ? 'UnverifiedReference' : 'ProposedOnly', availability: 'Unknown', reason };
    visiting.delete(key); rows.set(key, row); return row;
  }
  for (const key of [...components.keys()].sort()) resolve(key);
  const sortedRows = [...rows.values()].sort((a, b) => a.componentKey < b.componentKey ? -1 : a.componentKey > b.componentKey ? 1 : 0);
  const normalized = { ...input, components: [...components.values()].sort((a, b) => a.key < b.key ? -1 : a.key > b.key ? 1 : 0) };
  const result = { schema: 'platform.naming-plan/v1', profile: policy.id, intent: structuredClone(intent), policySha256: digest(policy), inputSha256: digest(normalized), resources: sortedRows, deploymentAuthorized: false, allocationAuthorized: false, limitations: policy.limitations };
  return { ...result, planSha256: digest(result) };
}
