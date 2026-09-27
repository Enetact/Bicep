import assert from 'node:assert/strict';
import fs from 'node:fs';
import path from 'node:path';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import { analyzeDiscovery, canonical } from '../../scripts/analysis/core.mjs';
import { renderAnalysis } from '../../scripts/analysis/render.mjs';
import { fixture, input, at, vnet, subnet, dns, rg } from './fixtures.mjs';
const root = path.resolve(path.dirname(fileURLToPath(import.meta.url)), '../..');
const outputRoot = path.join(root, 'artifacts/analysis-tests'); fs.mkdirSync(outputRoot, { recursive: true });
const directory = fs.mkdtempSync(path.join(outputRoot, 'run-')); const results = [];
const test = (name, run) => { run(); results.push({ name, passed: true }); };
const analyze = f => analyzeDiscovery(input(f));
for (const type of ['blob-transfer', 'logic-app-event-grid']) for (const populated of [false, true]) {
  test(`${type} ${populated ? 'populated' : 'empty'} saved compatibility and containment`, () => {
    const report = analyze(fixture(type, populated));
    assert.equal(report.context.workloadType, type); assert.equal(report.deploymentAuthorized, false); assert.equal(report.status, 'partial');
    assert.equal(report.graph.edges.length, populated || type === 'logic-app-event-grid' ? 1 : 0);
    if (!populated && type === 'logic-app-event-grid') assert(report.graph.nodes.every(n => n.action === 'Create' && !n.states.includes('Observed')));
    for (const n of report.graph.nodes) assert.equal(n.runtime, 'Unverified');
    const out = path.join(directory, `${type}-${populated}`); fs.mkdirSync(out);
    for (const [name, value] of renderAnalysis(report)) fs.writeFileSync(path.join(out, name), value);
  });
}
test('deterministic output for fixed inputs and evaluation time', () => { const f = fixture('logic-app-event-grid', true); assert.equal(canonical(analyze(f)), canonical(analyze(f))); });
test('hash tampering rejected', () => { const x = input(fixture()); x.inventoryBytes = Buffer.from('changed'); assert.throws(() => analyzeDiscovery(x), /bytes/); });
test('unknown manifest major rejected', () => { const f = fixture(); f.manifest.schemaVersion = 9; assert.throws(() => analyze(f), /schema/); });
test('cross-workload inventory cannot use a Blob copy manifest', () => { const f = fixture(); f.inventory.workloadType = 'logic-app-event-grid'; assert.throws(() => analyze(f), /contract/); });
test('wrong workload selection rejected', () => { assert.throws(() => analyzeDiscovery({ ...input(fixture()), workload: 'eventflow' }), /workload/); });
test('wrong environment selection rejected', () => { assert.throws(() => analyzeDiscovery({ ...input(fixture()), environment: 'prod' }), /environment/); });
test('subscription mismatch rejected', () => { const f = fixture(); f.inventory.subscription.id = '22222222-2222-2222-2222-222222222222'; assert.throws(() => analyze(f), /subscription/); });
test('timestamp mismatch rejected', () => { const f = fixture(); f.inventory.generatedUtc = '2026-09-25T12:00:00Z'; assert.throws(() => analyze(f), /time/); });
for (const [name, mutation] of [
  ['failed DNS', f => { f.inventory.privateDnsQuery.status = 'Failed'; }],
  ['truncated DNS', f => { f.inventory.privateDnsQuery.truncated = true; }],
  ['count mismatch', f => { f.inventory.networkQuery.count = 20; }],
  ['pending page', f => { f.inventory.networkQuery.nextLink = 'opaque-page'; }],
  ['failed subnet query', f => { f.inventory.networks[0].subnetQuery.status = 'Failed'; }],
  ['unknown ownership', f => { f.inventory.prerequisiteOwnership.status = 'Failed'; }]
]) test(`${name} never authorizes reported prerequisite actions`, () => { const f = fixture('logic-app-event-grid', true); mutation(f); const r = analyze(f); assert.equal(r.status, 'blocked'); assert(r.graph.nodes.filter(n => n.states.includes('Proposed')).every(n => n.action === 'Blocked')); });
test('successful empty DNS remains distinct from unknown', () => { const r = analyze(fixture()); assert.equal(r.coverage.find(c => c.collection === 'privateDnsZones').state, 'ReportedComplete'); assert.equal(r.coverage[1].count, 0); });
test('stale evidence blocked', () => { const r = analyzeDiscovery({ ...input(fixture()), evaluatedUtc: '2026-10-05T12:00:00Z' }); assert.equal(r.status, 'blocked'); });
test('future evidence blocked', () => { const r = analyzeDiscovery({ ...input(fixture()), evaluatedUtc: '2026-09-26T10:00:00Z' }); assert.equal(r.status, 'blocked'); });
test('saved main metadata never authenticates ADO', () => { const r = analyze(fixture()); assert.equal(r.context.provenance, 'UnverifiedOffline'); assert.equal(r.findings.find(f => f.ruleId === 'provenance.authenticated').outcome, 'unknown'); });
test('managed resource cannot silently become external reuse', () => { const f = fixture('logic-app-event-grid', true); f.inventory.prerequisiteOwnership.managedResourceIds = [vnet]; const r = analyze(f); assert.equal(r.graph.nodes.find(n => n.resourceId === vnet.toLowerCase()).action, 'Blocked'); });
test('owned Manage preserved', () => { const f = fixture('logic-app-event-grid', true); f.inventory.prerequisiteOwnership.managedResourceIds = [vnet, subnet]; f.inventory.prerequisitePlan.resources.forEach(r => r.action = 'Manage'); const r = analyze(f); assert(r.graph.nodes.filter(n => n.states.includes('Proposed')).every(n => n.action === 'Manage' && n.ownership === 'ReportedOwned')); });
test('Create contradicting observed existence blocked', () => { const f = fixture('logic-app-event-grid', true); f.inventory.prerequisitePlan.resources[0].action = 'Create'; assert.equal(analyze(f).status, 'blocked'); });
test('Manage without ownership blocked', () => { const f = fixture('logic-app-event-grid', true); f.inventory.prerequisitePlan.resources[0].action = 'Manage'; assert.equal(analyze(f).status, 'blocked'); });
test('unrelated ARM resources excluded from workload graph', () => { const f = fixture('logic-app-event-grid'); f.inventory.resources = [{ id: `${rg}/providers/Microsoft.Web/sites/unrelated-app` }]; assert(!canonical(analyze(f)).includes('unrelated-app')); });
test('raw errors, credentials and hostile names never copied', () => { const f = fixture('blob-transfer', true); f.inventory.warnings = ['SECRET-CANARY']; f.inventory.privateDnsQuery.error = 'SECRET-CANARY'; f.inventory.networks[0].name = '<script>SECRET-CANARY</script>'; f.inventory.networks[0].connectionString = 'SECRET-CANARY'; const values = [...renderAnalysis(analyze(f)).values()].join(''); assert(!values.includes('SECRET-CANARY')); assert(!values.includes('<script')); });
test('unsafe resource identifiers rejected', () => { const f = fixture('blob-transfer', true); f.inventory.networks[0].id += '?token=SECRET-CANARY'; assert.throws(() => analyze(f), /identifier/); });
test('cross-scope resource rejected', () => { const f = fixture('blob-transfer', true); f.inventory.privateDnsZones[0].id = dns.replace('11111111', '22222222'); assert.throws(() => analyze(f), /scope/); });
test('duplicate DNS rejected ignoring case', () => { const f = fixture('blob-transfer', true); f.inventory.privateDnsZones.push({ id: dns.toUpperCase() }); assert.throws(() => analyze(f), /Duplicate/); });
test('invalid subnet parent rejected', () => { const f = fixture('blob-transfer', true); f.inventory.networks[0].subnets[0].id = subnet.replace('vnet-example', 'another'); assert.throws(() => analyze(f), /containment/); });
test('oversized collection rejected', () => { const f = fixture(); f.inventory.networks = Array(5001).fill({}); assert.throws(() => analyze(f), /oversized/); });
test('large graphs paginate without losing nodes', () => { const f = fixture(); f.inventory.privateDnsZones = Array.from({ length: 65 }, (_, i) => ({ id: `${rg}/providers/Microsoft.Network/privateDnsZones/zone-${i}.test` })); f.inventory.privateDnsQuery.count = 65; const r = analyze(f); const files = renderAnalysis(r); assert.equal(r.graph.nodes.length, 65); assert.equal([...files.keys()].filter(k => k.endsWith('.svg')).length, 3); });
test('CLI writes receipt and refuses evidence overwrite', () => {
  const f = input(fixture('logic-app-event-grid')); const inp = path.join(directory, 'cli-input'); const out = path.join(directory, 'cli-output'); fs.mkdirSync(inp);
  fs.writeFileSync(path.join(inp, 'inventory.json'), f.inventoryBytes); fs.writeFileSync(path.join(inp, 'manifest.json'), f.manifestBytes);
  const args = [path.join(root, 'scripts/analysis/cli.mjs'), '--input', inp, '--output', out, '--at', at];
  const run = spawnSync(process.execPath, args, { encoding: 'utf8' }); assert.equal(run.status, 0, run.stderr); assert(fs.existsSync(path.join(out, 'report-manifest.json')));
  const again = spawnSync(process.execPath, args, { encoding: 'utf8' }); assert.equal(again.status, 1); assert(again.stderr.includes('never overwritten'));
});
test('CLI hides parser source snippets', () => { const inp = path.join(directory, 'bad-input'); fs.mkdirSync(inp); fs.writeFileSync(path.join(inp, 'inventory.json'), '{SECRET-CANARY'); const run = spawnSync(process.execPath, [path.join(root, 'scripts/analysis/cli.mjs'), '--input', inp, '--output', path.join(directory, 'bad-output')], { encoding: 'utf8' }); assert.equal(run.status, 1); assert(!run.stderr.includes('SECRET-CANARY')); assert(!fs.existsSync(path.join(directory, 'bad-output'))); });
fs.writeFileSync(path.join(directory, 'results.json'), canonical({ passed: results.length, failed: 0, azureCalls: false, adoCalls: false, cases: results }) + '\n');
console.log(`PASS: ${results.length} offline analysis cases. Evidence: ${directory}`);
