import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { validateInstances } from '../../scripts/naming/instances.mjs';
import { digest } from '../../scripts/naming/core.mjs';

const read = path => JSON.parse(fs.readFileSync(new URL(path, import.meta.url)));
const policy = read('../../config/naming-policy.json'), catalog = read('../../config/workloads.json');
const intent = read('./request.example.json').intent;
const entry = () => ({ workloadType: 'private-storage', state: 'draft', intent: structuredClone(intent) });
const registry = (...instances) => ({ schemaVersion: 1, namingPolicySha256: digest(policy), instances });
const validate = (value, prior) => validateInstances(value, policy, catalog, prior);
const second = () => { const e = entry(); e.intent.instanceId = '33333333-3333-4333-8333-333333333333'; e.intent.instanceOrdinal = '02'; return e; };
const invoke = (input, args = []) => spawnSync(process.execPath, [fileURLToPath(new URL('../../scripts/naming/instances-cli.mjs', import.meta.url)), ...args], { input: typeof input === 'string' ? input : JSON.stringify(input), encoding: 'utf8', timeout: 15000 });

test('two instances of one product/environment have different group and stack IDs', () => {
  const r = validate(registry(entry(), second()));
  assert.equal(r.instances.length, 2);
  assert.equal(new Set(r.instances.flatMap(i => i.identityPlan.resources.map(v => v.resourceId))).size, 4);
  assert.equal(r.instances[0].identityPlan.resources[0].name, 'dev-stack-pay-int-eus2-01');
  assert.equal(r.instances[1].identityPlan.resources[0].name, 'dev-stack-pay-int-eus2-02');
  assert.equal(r.deploymentAuthorized, false); assert.equal(r.allocationAuthorized, false);
  assert.equal(r.transitionChecked, false); assert.equal(r.previousRegistrySha256, null);
});
test('all registered workloads and environments accept explicit private drafts', () => {
  for (const type of Object.keys(catalog.workloads)) for (const env of policy.environments) {
    const e = entry(); e.workloadType = type; e.intent.environment = env;
    assert.equal(validate(registry(e)).instances[0].workloadType, type);
  }
});
test('empty checked-in registry contains no user subscriptions or active targets', () => {
  const r = read('../../self-service/instances/registry.json');
  assert.deepEqual(r.instances, []); assert.equal(validate(r).instances.length, 0);
});
test('schema intent and workload enum remain synchronized with authoritative contracts', () => {
  const schema = read('../../schemas/naming/instances.schema.json');
  const fields = schema.properties.instances.items.properties;
  assert.deepEqual(fields.intent, read('../../schemas/naming/intent.schema.json').properties.intent);
  assert.deepEqual(fields.workloadType.enum, Object.keys(catalog.workloads));
});
test('registry digest and output independent of array/property order', () => {
  const a = registry(entry(), second()), b = structuredClone(a); b.instances.reverse();
  b.instances[0].intent = Object.fromEntries(Object.entries(b.instances[0].intent).reverse());
  assert.deepEqual(validate(a), validate(b));
});
test('receipt binds catalog, policy and result', () => {
  const { receiptSha256, ...body } = validate(registry(entry()));
  assert.equal(receiptSha256, digest(body)); assert.equal(body.catalogSha256, digest(catalog));
  body.instances[0].identityPlan.resources[0].name = 'tampered'; assert.notEqual(receiptSha256, digest(body));
});
test('display name changes preserve identities but invalidate registry receipt', () => {
  const a = registry(entry()), b = structuredClone(a); b.instances[0].intent.stackDisplayName = 'New label';
  const r = validate(b, a), old = validate(a);
  assert(r.transitionChecked); assert.equal(r.previousRegistrySha256, old.registrySha256);
  assert.notEqual(r.registrySha256, old.registrySha256);
  assert.deepEqual(r.instances[0].identityPlan.resources, old.instances[0].identityPlan.resources);
});
test('append-only new draft permitted with checked previous registry', () => {
  assert.equal(validate(registry(entry(), second()), registry(entry())).instances.length, 2);
});
for (const [field, value] of [['instanceOrdinal','02'], ['stackAcronym','ops'], ['namespaceSalt','new'], ['environment','qa'], ['region','westus'], ['exposure','external'], ['subscriptionId','44444444-4444-4444-8444-444444444444'], ['instanceId','33333333-3333-4333-8333-333333333333'], ['namingProfile','legacy-v1']]) {
  test(`identity edit rejected: ${field}`, () => {
    const a = registry(entry()), b = structuredClone(a); b.instances[0].intent[field] = value;
    assert.throws(() => validate(b, a), /migration|Unsupported|Legacy|internal exposure/);
  });
}
test('workload type change requires migration', () => {
  const a = registry(entry()), b = structuredClone(a); b.instances[0].workloadType = 'key-vault';
  assert.throws(() => validate(b, a), /migration/);
});
test('removing a prior instance is rejected', () => assert.throws(() => validate(registry(), registry(entry())), /retirement/));
test('policy changes cannot silently recompute registered names', () => {
  const r = registry(entry()); r.namingPolicySha256 = '0'.repeat(64); assert.throws(() => validate(r), /policy/);
  assert.throws(() => validate(registry(entry()), r), /policy/);
});
test('duplicate IDs rejected even if ordinal differs', () => { const b = second(); b.intent.instanceId = intent.instanceId; assert.throws(() => validate(registry(entry(), b)), /Duplicate instance/); });
test('same ordinal/acronym cannot collide across product types or salts', () => {
  const b = second(); b.intent.instanceOrdinal = '01'; b.workloadType = 'key-vault'; b.intent.namespaceSalt = 'salt';
  assert.throws(() => validate(registry(entry(), b)), /Duplicate stack\/group/);
});
test('same naming tuple can be separately reserved in different subscriptions', () => {
  const b = second(); b.intent.instanceOrdinal = '01'; b.intent.subscriptionId = '44444444-4444-4444-8444-444444444444';
  assert.equal(validate(registry(entry(), b)).instances.length, 2);
});
test('only catalog types accepted including prototype names', () => {
  for (const name of ['other','toString','__proto__']) { const e = entry(); e.workloadType = name; assert.throws(() => validate(registry(e)), /Unregistered/); }
});
test('draft cannot enable deployment or inject a target override', () => {
  for (const key of ['enabled','deploymentAuthorized','target','serviceConnection']) { const e = entry(); e[key] = true; assert.throws(() => validate(registry(e)), /exact contract/); }
  const e = entry(); e.state = 'registered'; assert.throws(() => validate(registry(e)), /non-executable/);
});
test('bounded collection and malformed nested inputs rejected', () => {
  assert.throws(() => validate(registry(...Array(1001).fill(entry()))), /at most/);
  const e = entry(); e.intent = null; assert.throws(() => validate(registry(e)), /object required/);
  assert.throws(() => validate({ schemaVersion: 1 }), /exact contract/);
});
test('CLI matches pure validation including checked transition', () => {
  const r = registry(entry(), second()), prior = registry(entry()); const result = invoke({ registry: r, previousRegistry: prior });
  assert.equal(result.status, 0, result.stderr); assert.deepEqual(JSON.parse(result.stdout), validate(r, prior));
});
test('CLI rejects malformed envelope, explicit null prior and oversized input without partial JSON', () => {
  for (const v of [{ registry: registry(), previousRegistry: null }, { registry: registry(), command: 'deploy' }, [], 'not JSON', ' '.repeat(1024*1024+1)]) {
    const r = invoke(v); assert.equal(r.status, 1); assert.equal(r.stdout, ''); assert.match(r.stderr, /rejected/);
  }
  assert.equal(invoke({ registry: registry() }, ['--deploy']).status, 1);
});
