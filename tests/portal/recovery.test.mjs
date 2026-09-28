import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { recoveryView } from '../../src/SelfService.Portal/wwwroot/recovery.mjs';
const catalog = JSON.parse(readFileSync(new URL('../../config/recovery-capabilities.json', import.meta.url)));
test('all fourteen workflow policies display without execution controls', () => {
  assert.equal(catalog.policies.length, 14);
  for (const policy of catalog.policies) { const view = recoveryView(catalog, policy.id); assert.equal(view.canExecute, false); assert.ok(view.exclusions.length); }
});
test('policy selection isolates application and tag prerequisites', () => {
  const app = recoveryView(catalog, 'blob-transfer'), tags = recoveryView(catalog, 'tags-external'), storage = recoveryView(catalog, 'private-storage');
  assert.ok(app.checks.includes(catalog.checks.packageCompatible)); assert.ok(!app.checks.includes(catalog.checks.tagKeysOwned));
  assert.ok(tags.checks.includes(catalog.checks.tagCurrentMatchesApplied)); assert.ok(!tags.checks.includes(catalog.checks.packageCompatible));
  assert.equal(storage.checks.length, 14);
});
test('unrecognized schema, policy or execution-enabled catalog fails closed', () => {
  assert.equal(recoveryView(catalog, 'invented'), null); assert.equal(recoveryView({ ...catalog, executionEnabled: true }, 'blob-transfer'), null);
  assert.equal(recoveryView({ ...catalog, schemaVersion: 2 }, 'blob-transfer'), null); assert.equal(recoveryView(undefined, 'blob-transfer'), null);
});
test('manual and source paths never present deployment eligibility', () => {
  assert.match(recoveryView(catalog, 'network-ipam').status, /review required/);
  assert.match(recoveryView(catalog, 'tags-source').status, /source change/);
  assert.equal(recoveryView(catalog, 'discovery').checks.length, 0);
});
