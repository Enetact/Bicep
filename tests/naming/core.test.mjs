import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { resolveNames, digest, parseArmId } from '../../scripts/naming/core.mjs';
import { inventory } from '../../scripts/naming/inventory.mjs';

const policy = JSON.parse(fs.readFileSync(new URL('../../config/naming-policy.json', import.meta.url)));
const sample = JSON.parse(fs.readFileSync(new URL('./request.example.json', import.meta.url)));
const copy = () => structuredClone(sample);
const resolve = input => resolveNames(input, policy);
const row = (plan, key) => plan.resources.find(r => r.componentKey === key);
const reject = (mutate, pattern) => { const input = copy(); mutate(input); assert.throws(() => resolve(input), pattern); };

test('resource names, nested parent identity and explicit contracts', () => {
  const plan = resolve(copy());
  assert.equal(row(plan, 'group').name, 'dev-rg-pay-int-eus2-01');
  assert.match(row(plan, 'host').name, /^devstpayinteus2h[a-z2-7]{6}$/);
  assert.equal(row(plan, 'host').name, 'devstpayinteus2hcsr5ju'); // Published cross-entry golden vector.
  assert.equal(row(plan, 'blob').fullName, `${row(plan, 'host').name}/default`);
  assert.equal(row(plan, 'incoming').resourceId, `${row(plan, 'host').resourceId}/blobServices/default/containers/incoming`);
  assert.equal(plan.deploymentAuthorized, false);
  assert(plan.resources.every(r => r.availability === 'Unknown'));
});
test('canonical plan is invariant under input property/component order', () => {
  const input = copy(); input.components.reverse(); input.intent = Object.fromEntries(Object.entries(input.intent).reverse());
  assert.deepEqual(resolve(input), resolve(copy()));
});
test('policy change changes receipt digest', () => { const p = structuredClone(policy); p.reviewedDate = '2026-10-05'; assert.notEqual(resolveNames(copy(), p).planSha256, resolve(copy()).planSha256); });
test('plan hash binds output excluding its own hash', () => { const { planSha256, ...body } = resolve(copy()); assert.equal(planSha256, digest(body)); body.resources[0].name = 'changed'; assert.notEqual(planSha256, digest(body)); });
test('display name changes receipt but not names', () => { const input = copy(); input.intent.stackDisplayName = '<script>untrusted title</script>'; const p = resolve(input); assert.deepEqual(p.resources, resolve(copy()).resources); assert.notEqual(p.inputSha256, resolve(copy()).inputSha256); });
for (const [field, value] of [['stackAcronym', 'PAY'], ['stackAcronym', 'ab_cd'], ['stackAcronym', 'åb'], ['stackAcronym', 'toolong'], ['instanceOrdinal', '00'], ['environment', 'stage'], ['region', 'eastus'], ['exposure', 'public'], ['instanceId', 'run-25'], ['subscriptionId', 'not-guid'], ['namespaceSalt', 'new salt'], ['namingProfile', 'legacy-v1']]) {
  test(`reject invalid intent ${field}=${value}`, () => reject(i => { i.intent[field] = value; }, /invalid|Unsupported|Legacy/));
}
test('unknown request/intent fields rejected', () => { reject(i => { i.execute = true; }, /unsupported field/); reject(i => { i.intent.regionCode = 'xx'; }, /unsupported field/); });
test('excluded components still reject malformed descriptors', () => { reject(i => { i.components[1].condition = 'excluded'; i.components[1].role = 'invented'; }, /Unqualified/); });
test('null and non-string optional fields cannot bypass validation', () => { reject(i => { delete i.components[1].parentKey; i.components[1].scope = 0; }, /Invalid ARM/); reject(i => { i.components[1].reason = null; }, /invalid text/); });
test('unknown ARM types/roles fail closed', () => { reject(i => { i.components[1].armType = 'Example.Unknown/resources'; }, /Unsupported ARM type/); reject(i => { i.components[1].role = 'invented'; }, /Unqualified/); });
test('global identity stable on repetition and distinct for another instance/salt/role', () => {
  const original = row(resolve(copy()), 'host').name;
  for (const change of [i => i.intent.instanceId = '33333333-3333-4333-8333-333333333333', i => i.intent.namespaceSalt = 'b', i => i.components[1].role = 'events']) { const i = copy(); change(i); assert.notEqual(row(resolve(i), 'host').name, original); }
});
test('prod storage with longest acronym fits 24 without truncation', () => { const i = copy(); i.intent.environment = 'prod'; i.intent.stackAcronym = 'abcd'; assert.equal(row(resolve(i), 'host').name.length, 24); });
test('Key Vault selects approved compact fallback', () => { const i = copy(); i.components = [i.components[0], { key: 'vault', armType: 'Microsoft.KeyVault/vaults', mode: 'convention', condition: 'included', role: 'primary', parentKey: 'group' }]; const r = row(resolve(i), 'vault'); assert.equal(r.format, 'compact'); assert(r.name.length <= 24); });
test('impossible length rejects instead of truncating', () => { const p = structuredClone(policy); p.rules['Microsoft.Storage/storageAccounts'].maxLength = 10; assert.throws(() => resolveNames(copy(), p), /No tokens truncated/); });
test('duplicate component keys and identities reject', () => { reject(i => i.components.push(i.components[0]), /Duplicate component key/); reject(i => i.components.push({ ...i.components[0], key: 'second-group' }), /Duplicate resource identity/); });
test('parent cycles, missing parent and wrong parent type reject', () => {
  reject(i => { i.components[0].parentKey = 'host'; }, /cycle/);
  reject(i => { i.components[1].parentKey = 'missing'; }, /Unknown parent/);
  reject(i => { i.components[2].parentKey = 'group'; }, /Child type/);
});
test('included child cannot sit under excluded parent', () => reject(i => { i.components[0].condition = 'excluded'; }, /excluded parent/));
test('unknown condition does not assert a name', () => { const i = copy(); i.components = [i.components[0]]; i.components[0].condition = 'unknown'; assert.equal(resolve(i).resources[0].name, null); });
test('missing scope is disclosed, never invented', () => { const i = copy(); i.components = [{ ...i.components[1], parentKey: undefined }]; delete i.components[0].parentKey; const r = resolve(i).resources[0]; assert(r.name); assert.equal(r.resourceId, null); assert.match(r.reason, /Scope unresolved/); });
test('fixed default and storage contract validators', () => { reject(i => { i.components[2].explicitName = 'renamed'; }, /must be named default/); reject(i => { i.components[3].explicitName = 'wrong--name'; }, /storage contract/); });
test('explicit overrides cannot bypass convention', () => reject(i => { i.components[1] = { key: 'host', armType: i.components[1].armType, mode: 'fixed', condition: 'included', explicitName: 'anything' }; }, /No explicit override/));
test('reused resources preserve case and cross-subscription ID without proving access', () => {
  const i = copy(); const id = '/subscriptions/33333333-3333-4333-8333-333333333333/resourceGroups/Shared/providers/Microsoft.Network/virtualNetworks/ExistingVnet';
  i.components = [{ key: 'network', armType: 'Microsoft.Network/virtualNetworks', mode: 'reference', condition: 'included', existingResourceId: id }];
  const r = resolve(i).resources[0]; assert.equal(r.resourceId, id); assert.equal(r.name, 'ExistingVnet'); assert.equal(r.ownership, 'UnverifiedReference');
});
test('reference type mismatch rejects', () => reject(i => { i.components = [{ key: 'ref', armType: 'Microsoft.Storage/storageAccounts', mode: 'reference', condition: 'included', existingResourceId: `/subscriptions/${i.intent.subscriptionId}/resourceGroups/test` }]; }, /type mismatch/));
test('owned resource scope cannot change subscription', () => reject(i => { i.components[0].scope = '/subscriptions/33333333-3333-4333-8333-333333333333'; }, /differs from selected subscription/));
test('provider assigned names stay unknown', () => { const i = copy(); i.components = [{ key: 'managed', armType: 'Microsoft.Resources/resourceGroups', mode: 'providerAssigned', condition: 'included' }]; assert.equal(resolve(i).resources[0].name, null); });
test('extension resource ID parsing preserves full scope', () => { const id = `/subscriptions/${sample.intent.subscriptionId}/resourceGroups/shared/providers/Microsoft.Storage/storageAccounts/test/providers/Microsoft.Authorization/roleAssignments/11111111-1111-4111-8111-111111111111`; const r = parseArmId(id); assert.equal(r.type, 'Microsoft.Authorization/roleAssignments'); assert(r.parentId.endsWith('/storageAccounts/test')); });
for (const bad of ['https://example.com', '/subscriptions/nope', `/subscriptions/${sample.intent.subscriptionId}/providers/X.Y`, `/subscriptions/${sample.intent.subscriptionId}/resourceGroups/test?sig=a`, `/subscriptions/${sample.intent.subscriptionId}/resourceGroups/../`]) test(`reject malformed ARM ID ${bad}`, () => assert.throws(() => parseArmId(bad)));
test('source inventory covers declared ARM types and inline review locations exist', () => {
  const data = inventory(policy); assert.equal(data.types.length, 47); assert(data.declarations.length > 47);
  for (const x of data.inlineAndServiceContracts) assert(fs.existsSync(new URL('../../' + x.file, import.meta.url)));
  const nodeGroup = data.inlineAndServiceContracts.find(x => x.field === 'properties.nodeResourceGroup');
  assert.equal(nodeGroup.classification, 'explicitManagedScope');
  const module = fs.readFileSync(new URL('../../' + nodeGroup.file, import.meta.url), 'utf8');
  assert.match(module, /param nodeResourceGroupName string = '\$\{resourceGroup\(\)\.name\}-aks-managed'/);
  assert.match(module, /nodeResourceGroup:\s*nodeResourceGroupName/);
});
test('every convention profile resolves a legal representative name', () => {
  for (const [type, rule] of Object.entries(policy.rules).filter(([, r]) => r.classification === 'convention')) {
    const i = copy(); i.components = [{ key: 'item', armType: type, mode: 'convention', role: 'primary', condition: 'included' }];
    const r = resolve(i).resources[0]; assert(r.name.length >= rule.minLength && r.name.length <= rule.maxLength, type); assert(new RegExp(rule.pattern).test(r.name), type);
  }
});
test('stdin CLI matches pure core and rejects command-line overrides', () => {
  const cli = fileURLToPath(new URL('../../scripts/naming/cli.mjs', import.meta.url));
  const good = spawnSync(process.execPath, [cli], { input: JSON.stringify(copy()), encoding: 'utf8' }); assert.equal(good.status, 0, good.stderr); assert.deepEqual(JSON.parse(good.stdout), resolve(copy()));
  const bad = spawnSync(process.execPath, [cli, '--deploy'], { input: '{}', encoding: 'utf8' }); assert.notEqual(bad.status, 0); assert.equal(bad.stdout, '');
});
test('CLI rejects oversized input with no partial plan', () => { const r = spawnSync(process.execPath, [fileURLToPath(new URL('../../scripts/naming/cli.mjs', import.meta.url))], { input: ' '.repeat(1024 * 1024 + 1), encoding: 'utf8' }); assert.notEqual(r.status, 0); assert.match(r.stderr, /exceeds 1 MiB/); assert.equal(r.stdout, ''); });
