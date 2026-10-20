import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { fabricReadinessView, setupFabricReadiness } from '../../src/SelfService.Portal/wwwroot/fabric.mjs';

const report = (environment = 'dev', region = 'eastus2') => ({
  schemaVersion: 1, environment, region, checkedUtc: '2026-10-20T16:00:00Z', scope: 'ReviewedSourceOnly',
  status: 'NeedsConfiguration', canDeploy: false, liveVerified: false,
  poolName: `fabric-${environment}-agents`, workspaceMode: 'Bootstrap', privateProfile: 'Same VNet / Azure DNS / IPv4',
  pipeline: 'azure-pipelines-fabric-workspace.yml', sources: [{ path: `self-service/fabric/workspace.${environment}.json`, sha256: 'a'.repeat(64) }],
  checks: [
    { id: 'source-onboarding', label: 'Deployment onboarding', status: 'NeedsConfiguration', detail: 'Reviewed targets are disabled.' },
    ...['identity', 'operation', 'pool-binding', 'pool-profile', 'region', 'private-workspace'].map(id => ({ id: `source-${id}`, label: 'Reviewed source ' + id, status: 'Configured', detail: 'Source values are configured; live state remains unverified.' })),
    ...['managed-pool', 'agent-subnet', 'agent-job', 'private-endpoint', 'private-reachability', 'public-policy', 'plan-verification'].map(id => ({ id: `runtime-${id}`, label: 'Live check ' + id, status: 'NotVerified', detail: 'The protected pipeline checks live state and private reachability.' }))
  ], limitations: ['Native Fabric deployment pipelines cannot promote into deny-public workspaces.']
});

class Element {
  constructor(tagName, ownerDocument) { this.tagName = tagName; this.ownerDocument = ownerDocument; this.children = []; this.attributes = {}; this.listeners = {}; this.dataset = {}; this.textContent = ''; }
  append(...nodes) { this.children.push(...nodes); }
  replaceChildren(...nodes) { this.children = [...nodes]; }
  setAttribute(name, value) { this.attributes[name] = value; }
  addEventListener(name, callback) { this.listeners[name] = callback; }
}
function surface() {
  const document = { createElement(tag) { return new Element(tag, document); } };
  return document.createElement('section');
}
const descendants = node => [node, ...node.children.flatMap(descendants)];
const content = node => descendants(node).map(n => n.textContent).join('\n');
const button = (node, label) => descendants(node).find(n => n.tagName === 'button' && n.textContent === label);
const settle = async () => { await new Promise(resolve => setImmediate(resolve)); };
function pending() { let resolve, reject; const promise = new Promise((yes, no) => { resolve = yes; reject = no; }); return { promise, resolve, reject }; }

test('source checks and live checks stay distinct, with no deployment authority', () => {
  const view = fabricReadinessView(report(), 'dev', 'eastus2');
  assert.equal(view.sourceChecks.length, 7); assert.equal(view.pipelineChecks.length, 7);
  assert.equal(view.heading, 'Configuration needs attention');
  assert(view.pipelineChecks.every(c => c.statusLabel === 'Awaiting pipeline check'));
  const configured = report(); configured.status = 'AwaitingPipelineVerification'; configured.checks[0].status = 'Configured';
  assert.match(fabricReadinessView(configured, 'dev', 'eastus2').heading, /pipeline verification pending/);
});

test('invalid, cross-scope or execution-enabled evidence is rejected', () => {
  for (const change of [
    r => { r.schemaVersion = 2; }, r => { r.scope = 'LiveAzure'; }, r => { r.environment = 'prod'; }, r => { r.region = 'westus'; },
    r => { r.canDeploy = true; }, r => { r.liveVerified = true; }, r => { r.status = 'Ready'; }, r => { r.workspaceMode = 'constructor'; },
    r => { r.checks[0].status = 'Verified'; }, r => { r.checks[0].status = 'toString'; }, r => { r.checks = []; }, r => { r.checkedUtc = 'invalid'; },
    r => { r.pipeline = 'unreviewed.yml'; }, r => { r.sources[0].sha256 = 'not-a-hash'; }, r => { r.poolName = {}; }, r => { r.limitations = ['x'.repeat(1601)]; },
    r => { r.checks.find(c => c.id === 'runtime-private-reachability').status = 'Configured'; },
    r => { r.checks = r.checks.filter(c => c.id !== 'runtime-public-policy'); },
    r => { r.checks.push({ ...r.checks[0] }); }, r => { r.status = 'AwaitingPipelineVerification'; }
  ]) { const candidate = report(); change(candidate); assert.equal(fabricReadinessView(candidate, 'dev', 'eastus2'), null); }
  assert.equal(fabricReadinessView(null, 'dev', 'eastus2'), null);
});

test('non-Fabric workloads make no readiness calls', () => {
  const container = surface(); let calls = 0;
  const ui = setupFabricReadiness({ container, api: () => { calls++; }, openManagedPool: () => {} });
  ui.select('devopspool', 'dev', 'eastus2'); assert.equal(calls, 0); assert.equal(container.hidden, true);
});

test('Fabric loads source checks without a write and exposes a separate pool configuration shortcut', async () => {
  const container = surface(), calls = [], opened = [], request = pending();
  const ui = setupFabricReadiness({ container, api: (...args) => { calls.push(args); return request.promise; }, openManagedPool: (...args) => opened.push(args) });
  ui.select('fabric', 'qa', 'eastus2');
  assert.equal(container.attributes['aria-busy'], 'true'); assert.equal(button(container, 'Refresh source checks').disabled, true);
  assert.equal(calls[0][0], 'fabric/readiness/qa/eastus2'); assert.equal(calls[0][1], undefined);
  request.resolve(report('qa')); await settle();
  assert.equal(container.attributes['aria-busy'], 'false');
  assert.match(content(container), /fabric-qa-agents/); assert.match(content(container), /Awaiting pipeline check/);
  assert.match(content(container), /Native Fabric deployment pipelines/); assert.match(content(container), /Local configuration only/);
  assert.equal(button(container, 'Refresh source checks').disabled, false);
  button(container, 'Configure Managed DevOps Pool →').listeners.click(); assert.deepEqual(opened, [['qa', 'eastus2']]);
  ui.select('fabric', 'qa', 'eastus2'); assert.equal(calls.length, 1);
});

test('scope changes abort and discard stale results and clear other products', async () => {
  const container = surface(), requests = [], calls = [];
  const ui = setupFabricReadiness({ container, api: (...args) => { const request = pending(); requests.push(request); calls.push(args); return request.promise; }, openManagedPool: () => {} });
  ui.select('fabric', 'dev', 'eastus2'); ui.select('fabric', 'prod', 'centralus');
  assert.equal(calls[0][2].aborted, true);
  requests[1].resolve(report('prod', 'centralus')); await settle();
  requests[0].resolve(report('dev')); await settle();
  assert.match(content(container), /fabric-prod-agents/); assert.doesNotMatch(content(container), /fabric-dev-agents/);
  ui.select('fabric', 'qa', 'eastus2'); ui.select('storage', 'dev', 'eastus2');
  requests[2].resolve(report('qa')); await settle();
  assert.equal(container.hidden, true); assert.equal(content(container), ''); assert.equal(calls[2][2].aborted, true);
});

test('failed or invalid responses remove prior evidence and keep a safe refresh path', async () => {
  const container = surface(); let count = 0;
  const ui = setupFabricReadiness({ container, api: async () => { if (++count === 1) return report(); throw new Error('Bearer synthetic-secret-body'); }, openManagedPool: () => {} });
  ui.select('fabric', 'dev', 'eastus2'); await settle();
  button(container, 'Refresh source checks').listeners.click(); await settle();
  assert.match(content(container), /Source checks unavailable/); assert.doesNotMatch(content(container), /fabric-dev-agents|synthetic-secret|Bearer/);
  assert.equal(button(container, 'Refresh source checks').disabled, false);
  const other = surface(); const invalid = setupFabricReadiness({ container: other, api: async () => ({ ...report(), liveVerified: true }), openManagedPool: () => {} });
  invalid.select('fabric', 'dev', 'eastus2'); await settle(); assert.match(content(other), /readiness remain unverified/);
});

test('source-controlled strings are rendered as text without HTML or link evaluation', async () => {
  const payload = '<img src=x onerror=alert(1)>'; const candidate = report(); candidate.poolName = payload; candidate.checks[0].detail = payload;
  const container = surface(); const ui = setupFabricReadiness({ container, api: async () => candidate, openManagedPool: () => {} });
  ui.select('fabric', 'dev', 'eastus2'); await settle();
  assert(content(container).includes(payload)); assert(!descendants(container).some(n => n.tagName === 'img' || n.tagName === 'a'));
  const source = readFileSync(new URL('../../src/SelfService.Portal/wwwroot/fabric.mjs', import.meta.url), 'utf8');
  assert.doesNotMatch(source, /innerHTML|insertAdjacentHTML|eval\(/);
});
