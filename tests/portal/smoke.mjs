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
await test('Catalog and complete skill API', async () => { assert.equal(bootstrap.products.length, 2); assert.equal(bootstrap.skills.length, 47); assert.equal(bootstrap.skills.filter(s => s.origin === 'Microsoft Azure Skills').length, 42); const skill = await fetch(base + '/api/skills/azure--azure-resource-visualizer', { headers }).then(r => r.json()); assert.equal(skill.pipelineStatus,'No pipeline associated yet'); assert.equal(skill.discoveryProfile,'network'); });
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
await test('Disconnect returns clean state', async () => { assert.equal((await post('disconnect', {})).status, 200); const s = await fetch(base + '/api/auth', { headers }).then(r => r.json()); assert.deepEqual(s.connected, []); });
fs.mkdirSync('artifacts/portal-tests', { recursive: true });
fs.writeFileSync('artifacts/portal-tests/http-smoke.json', JSON.stringify({ timestamp: new Date().toISOString(), architecture: bootstrap.architecture, liveAzure: false, results }, null, 2));
console.log(`PASS: ${results.length} real local HTTP checks (${bootstrap.architecture}); no Azure/ADO calls.`);
