// Exercise the real local HTTP boundary. No Microsoft sign-in or ADO requests.
import assert from 'node:assert/strict';
import fs from 'node:fs';
import http from 'node:http';
import { fixture, input } from '../analysis/fixtures.mjs';
const base = process.env.PORTAL_TEST_URL ?? 'http://localhost:5087';
if (!/^http:\/\/localhost:\d+$/.test(base)) throw new Error('Tests only target localhost.');
const results = [];
async function test(name, f) { await f(); results.push({ name, passed: true }); }
const response = await fetch(base + '/api/bootstrap');
assert.equal(response.status, 200);
const cookie = response.headers.get('set-cookie').split(';')[0]; const bootstrap = await response.json();
assert.equal(bootstrap.configured, false, 'Run smoke tests against an unconfigured local portal.');
const headers = { cookie, origin: base, 'x-portal-csrf': bootstrap.csrf, 'content-type': 'application/json' };
const post = (path, data, extra = {}) => fetch(base + '/api/' + path, { method: 'POST', headers: { ...headers, ...extra }, body: JSON.stringify(data) });
await test('Session cookie and restrictive response headers', () => { assert.match(response.headers.get('set-cookie'), /httponly/i); assert.match(response.headers.get('set-cookie'), /samesite=strict/i); assert.match(response.headers.get('content-security-policy'), /frame-ancestors 'none'/); assert.equal(response.headers.get('access-control-allow-origin'), null); });
await test('Catalog and complete skill API', async () => { assert.equal(bootstrap.products.length, 9); assert.equal(bootstrap.skills.length, 52); assert.equal(bootstrap.skills.filter(s => s.origin === 'Microsoft Azure Skills').length, 42); const skill = await fetch(base + '/api/skills/azure--azure-resource-visualizer', { headers }).then(r => r.json()); assert.equal(skill.pipelineStatus,'No pipeline associated yet'); assert.equal(skill.discoveryProfile,'network'); });
await test('Every product has a served conceptual topology', () => assert.deepEqual(Object.keys(bootstrap.topologies.products).sort(), bootstrap.products.map(p => p.id).sort()));
await test('Diagram module and connected panels are served', async () => {
  const module = await fetch(base + '/topology.mjs'); assert.equal(module.status, 200); assert.match(module.headers.get('content-type'), /javascript/);
  const html = await fetch(base + '/').then(r => r.text()); for (const id of ['observed-diagram','workload-diagram','preview-diagram','load-preview','iac-analysis','load-iac-after','agent-iac-phase','agent-iac-artifact']) assert.ok(html.includes(`id="${id}"`));
});
await test('Unauthenticated Preview artifact read fails closed', async () => assert.equal((await fetch(base + '/api/preview/storage/44', { headers })).status, 401));
await test('Unauthenticated IaC artifact read fails closed', async () => assert.equal((await fetch(base + '/api/iac/storage/44/dev/preview', { headers })).status, 401));
await test('Recovery policy UI and read-only API cover all workflows without execution', async () => {
  const rules = await fetch(base + '/api/recovery/policies', { headers }).then(r => r.json());
  assert.equal(rules.executionEnabled, false); assert.equal(rules.policies.length, 17);
  const html = await fetch(base + '/').then(r => r.text());
  for (const id of ['workload-recovery','recovery-workflow','recovery-rules']) assert.ok(html.includes(`id="${id}"`));
  assert.equal((await fetch(base + '/recovery.mjs')).status, 200);
});
await test('Network workspace and validated-agent diagram controls are real app assets', async () => {
  const html=await fetch(base+'/').then(r=>r.text()); for(const id of ['network-mode','network-run','network-agent','agent-diagram','agent-mermaid','agent-receipt-download']) assert.ok(html.includes(`id="${id}"`));
  assert.equal((await fetch(base+'/network.mjs')).status,200);
});
await test('Expanded discovery cannot run without configured identity', async () => {
  assert.equal((await post('network/discover',{mode:'tenant'})).status,409);
  assert.equal((await post('network/discover',{mode:'registered',subscriptionIds:[bootstrap.discoveryScopes[0].id]})).status,401);
});
await test('Agent readiness is explicit and no workflow starts on authentication checks', async () => {
  const s = await fetch(base + '/api/agent/status', { headers }).then(r => r.json());
  assert.equal(s.provider.ready, false); assert.equal(s.provider.model, 'gpt-6.1-sol'); assert.equal(s.provider.effort, 'ultra'); assert.equal(s.provider.speed, 'Standard');
  assert.deepEqual(s.policy, { model: 'gpt-6.1-sol', displayName: 'GPT-6.1 Sol', effort: 'ultra', effortDisplayName: 'Ultra', serviceTier: 'default', speed: 'Standard' });
  assert.equal(s.workflows.length, 9); assert.ok(s.workflows.every(w => !w.ready));
});
await test('Agent menu and module are served', async () => { assert.equal((await fetch(base + '/agents.mjs')).status, 200); assert.match(await fetch(base + '/').then(r => r.text()), /id="agent-run"/); });
await test('DNS/drift analysis exposes implemented actions without execution',async()=>{
  const catalog=await fetch(base+'/api/operations/catalog',{headers}).then(r=>r.json());
  assert.deepEqual(catalog.implemented.map(w=>w.id),['private-dns','drift']);assert.ok(catalog.implemented.every(w=>w.execute===false));
  assert.equal((await fetch(base+'/operations.mjs')).status,200);assert.match(await fetch(base+'/').then(r=>r.text()),/id="operations-workspace"/);
});
await test('Operations collection and agent review require Azure connection',async()=>{
  assert.equal((await fetch(base+'/api/operations/scopes',{headers})).status,401);
  assert.equal((await post('operations/collect',{subscriptionIds:[bootstrap.discoveryScopes[0].id]})).status,401);
  assert.equal((await post('agent/run',{workflow:'private-dns',evidenceId:'unknown'})).status,401);
  assert.equal((await post('agent/run',{workflow:'drift',evidenceId:'unknown'})).status,401);
});
await test('Operations has no hidden apply route or cross-session evidence fallback',async()=>{
  assert.equal((await post('operations/apply',{})).status,404);
  assert.equal((await fetch(base+'/api/operations/evidence/unknown',{headers})).status,401);
  assert.equal((await post('operations/proposals/unknown/preview',{})).status,404);
});
await test('Agent connection requires CSRF', async () => assert.equal((await post('agent/connect', {}, { 'x-portal-csrf': '' })).status, 403));
await test('Agent workflow requires its separate Azure audience', async () => assert.equal((await post('agent/run', { workflow: 'visualize' })).status, 401));
await test('Unknown agent workflow is rejected', async () => assert.equal((await post('agent/run', { workflow: 'deploy' })).status, 400));
await test('Resource-group menu requires Azure identity', async () => assert.equal((await fetch(base + '/api/agent/resource-groups/' + bootstrap.discoveryScopes[0].id, { headers })).status, 401));
await test('AHP endpoint rejects non-WebSocket requests', async () => assert.equal((await fetch(base + '/api/agent/ahp', { headers })).status, 403));
await test('Cross-site POST rejected', async () => assert.equal((await post('disconnect', {}, { origin: 'https://outside.example' })).status, 403));
await test('Missing CSRF rejected', async () => assert.equal((await post('disconnect', {}, { 'x-portal-csrf': '' })).status, 403));
await test('Cross-site GET rejected', async () => assert.equal((await fetch(base + '/api/bootstrap', { headers: { 'sec-fetch-site': 'cross-site' } })).status, 403));
await test('DNS rebinding Host rejected', async () => { const status = await new Promise((resolve, reject) => { http.get(base + '/api/bootstrap', { headers: { host: 'outside.example' } }, r => { r.resume(); resolve(r.statusCode); }).on('error', reject); }); assert.equal(status, 403); });
await test('Unconfigured login fails closed', async () => assert.equal((await post('auth/ado', {})).status, 409));
await test('Unauthenticated ADO read fails closed', async () => assert.equal((await fetch(base + '/api/discovery/blobcopy', { headers })).status, 401));
await test('Unauthenticated Azure skill discovery fails closed', async () => assert.equal((await post('skill-discovery', { skillId: 'azure--azure-resource-visualizer', subscriptionId: bootstrap.discoveryScopes[0].id })).status, 401));
await test('Disabled deployment rejected server-side', async () => assert.equal((await post('review', { product: 'blobcopy', environment: 'dev', region: 'eastus2', operation: 'deploy', discoveryRunId: 91 })).status, 409));
await test('Unissued queue ticket rejected', async () => assert.equal((await post('queue/not-issued', {})).status, 409));
await test('Invalid upload rejected', async () => assert.equal((await post('analysis', { manifest: '!invalid', inventory: 'bad' })).status, 400));
await test('Existing analyzer executes and returns bounded synthetic report', async () => {
  const f = fixture('logic-app-event-grid', true); f.manifest.generatedUtc = f.inventory.generatedUtc = new Date().toISOString(); const pair = input(f);
  const r = await post('analysis', { manifest: pair.manifestBytes.toString('base64'), inventory: pair.inventoryBytes.toString('base64') });
  const body = await r.json(); assert.equal(r.status, 200, JSON.stringify(body)); assert.equal(body.report.deploymentAuthorized, false); assert.match(body.markdown, /Offline|offline/); assert.ok(body.diagrams.length > 0);
});
await test('Registration catalog covers all 25 root pipelines', async () => { const r = await fetch(base + '/api/pipeline-setup/catalog', {headers}); assert.equal(r.status,200); const c = await r.json(); assert.equal(c.entries.length,25); assert.ok(c.entries.some(e=>e.yaml==='azure-pipelines.yml')); });
await test('Registration inventory requires ADO identity', async () => assert.equal((await fetch(base + '/api/pipeline-setup/inventory', {headers})).status,401));
await test('Unissued registration ticket cannot create', async () => assert.equal((await post('pipeline-setup/apply/not-issued',{})).status,409));
await test('Disconnect returns clean state', async () => { assert.equal((await post('disconnect', {})).status, 200); const s = await fetch(base + '/api/auth', { headers }).then(r => r.json()); assert.deepEqual(s.connected, []); });
await test('Local authentication UI and status expose no credentials', async () => {
  const s=await fetch(base+'/api/auth',{headers}).then(r=>r.json());
  assert.equal(typeof s.localCliAvailable,'boolean'); assert.equal(s.mode,null); assert.equal(s.tenantId,null);
  assert.ok(!('accessToken' in s)); assert.ok(!('cachePath' in s));
  const html=await fetch(base+'/').then(r=>r.text());
  for(const id of ['auth-mode','local-auth-settings','local-auth-tenant','connect-local','local-cli-status','auth-context']) assert.ok(html.includes(`id="${id}"`));
  assert.ok(html.includes('Local authentication — Azure CLI')); assert.ok(html.includes('Disconnect Microsoft'));
});
await test('Local authentication rejects CSRF before any CLI invocation', async () => assert.equal((await post('local-auth/azure',{}, {'x-portal-csrf':'wrong'})).status,403));
await test('Local authentication rejects arbitrary audiences and tenant arguments', async () => {
  assert.equal((await post('local-auth/untrusted',{})).status,400);
  assert.equal((await post('local-auth/azure',{tenantId:'invalid; shell text'})).status,400);
  const s=await fetch(base+'/api/auth',{headers}).then(r=>r.json()); assert.equal(s.mode,null); assert.deepEqual(s.connected,[]);
});
await test('Tagging configuration does not enable writes', async () => { const c=await fetch(base+'/api/tags/config',{headers}).then(r=>r.json()); assert.equal(c.enabled,false);assert.equal(c.workflow,'tagging'); });
await test('Tag evidence is isolated from unauthenticated sessions', async () => {const r=await fetch(base+'/api/tags/evidence/unknown',{headers});assert.equal(r.status,401);});
await test('Tag drafts require a connected evidence owner', async () => {const r=await post('tags/drafts',{evidenceId:'unknown',edits:[]});assert.equal(r.status,401);});
await test('Tag queue rejects unissued tickets', async () => {const r=await post('tags/pipeline/queue/unissued',{});assert.equal(r.status,409);});
await test('GitHub controls and status expose no credential and require CSRF', async () => {
  const status=await fetch(base+'/api/github/status',{headers}).then(r=>r.json());
  assert.equal(typeof status.available,'boolean'); assert.equal(status.connected,false); assert.equal(status.user,null); assert.equal(status.code,null);
  assert.equal(status.verificationUrl,'https://github.com/login/device'); assert.ok(!('token' in status));
  const html=await fetch(base+'/').then(r=>r.text()); for(const id of ['github-connect','github-attach','github-code','github-disconnect']) assert.ok(html.includes(`id="${id}"`));
  assert.equal((await fetch(base+'/github.mjs')).status,200);
  for(const route of ['connect','attach','disconnect']) assert.equal((await post('github/'+route,{}, {'x-portal-csrf':''})).status,403);
  assert.equal((await post('github/disconnect',{})).status,200);
});
fs.mkdirSync('artifacts/portal-tests', { recursive: true });
fs.writeFileSync('artifacts/portal-tests/http-smoke.json', JSON.stringify({ timestamp: new Date().toISOString(), architecture: bootstrap.architecture, liveAzure: false, results }, null, 2));
console.log(`PASS: ${results.length} real local HTTP checks (${bootstrap.architecture}); no Azure/ADO calls.`);
