// Real localhost API checks. Public provider discovery/reads are explicitly opt-in;
// this never signs in, invokes a model, or executes cloud/source-control changes.
import assert from 'node:assert/strict';
import fs from 'node:fs';
const base = process.env.PORTAL_TEST_URL ?? 'http://localhost:5087';
if (!/^http:\/\/localhost:\d+$/.test(base)) throw Error('Only localhost is permitted.');
const initial = await fetch(base + '/api/bootstrap');
assert.equal(initial.status, 200);
const data = await initial.json();
const cookie = initial.headers.get('set-cookie').split(';')[0];
const headers = { cookie, origin: base, 'x-portal-csrf': data.csrf, 'content-type': 'application/json' };
const checks = [], inventories = [];
async function call(path, body, extra = {}) {
  const response = await fetch(base + '/api/' + path, { method: body === undefined ? 'GET' : 'POST', headers: { ...headers, ...extra }, body: body === undefined ? undefined : JSON.stringify(body), signal: AbortSignal.timeout(120000) });
  const result = await response.json();
  return { response, result };
}
async function check(name, action) { await action(); checks.push({ name, passed: true }); }
await check('Restart resume rejects cross-origin and missing CSRF before opening a checkpoint', async () => {
  for (const extra of [{ origin: 'http://example.invalid' }, { 'x-portal-csrf': '' }]) {
    const { response } = await call('auth/resume', { proof: 'invalid' }, extra); assert.equal(response.status, 403);
  }
});
await check('Invalid restart capability cannot attach credentials or create plans', async () => {
  const { response } = await call('auth/resume', { proof: 'invalid' }); assert.equal(response.status, 403);
  const { result } = await call('auth'); assert.deepEqual(result.connected, []);
  const plans = await call('mcp/plans'); assert.deepEqual(plans.result.plans, []);
});
await check('Five provider definitions and no ambient credential attachment', async () => {
  const { response, result } = await call('mcp/status');
  assert.equal(response.status, 200);
  assert.deepEqual(result.providers.map(p => p.id), ['bicep', 'azure', 'ado', 'github', 'learn']);
  assert.ok(result.providers.every(p => p.connected === false && p.state === 'stopped'));
});
await check('Provider connection requires the browser CSRF token', async () => assert.equal((await call('mcp/learn/connect', {}, { 'x-portal-csrf': '' })).response.status, 403));
await check('Unknown providers and plans fail without leaking a session gate', async () => {
  assert.equal((await call('mcp/unknown/connect', {})).response.status, 404);
  assert.equal((await call('mcp/plans/not-this-browser/run', { digest: '0'.repeat(64) })).response.status, 404);
  assert.equal((await call('mcp/plans')).response.status, 200);
});
await check('Disconnected providers cannot produce executable plans', async () => assert.equal((await call('mcp/plans', { steps: [{ provider: 'azure', tool: 'group_list', arguments: {} }] })).response.status, 401));
if (process.env.PLATFORM_MCP_LIVE_PUBLIC === '1') {
  try {
    for (const provider of ['bicep', 'learn']) {
      await check(`${provider}: real MCP initialization and full discovery`, async () => {
        const { result } = await call(`mcp/${provider}/connect`, {});
        const p = result.providers.find(p => p.id === provider);
        assert.equal(p.state, 'ready', p.error ?? 'Provider did not become ready.');
        assert.ok(p.toolCount > 0); assert.equal(p.tools.length, p.toolCount);
        inventories.push({ provider, toolCount: p.toolCount, catalogDigest: p.catalogDigest, tools: p.tools.map(t => t.name) });
      });
    }
    for (const [provider, tool, args] of [['bicep', 'get_bicep_best_practices', {}], ['learn', 'microsoft_docs_search', { query: 'Azure Bicep parameters' }]]) {
      await check(`${provider}: bounded real read through reviewed plan`, async () => {
        const { response, result: plan } = await call('mcp/plans', { steps: [{ provider, tool, arguments: args }] });
        assert.equal(response.status, 200); assert.equal(plan.requiresApproval, false);
        const { result } = await call(`mcp/plans/${plan.id}/run`, { digest: plan.digest });
        assert.equal(result.state, 'Completed'); assert.equal(result.receipts.length, 1); assert.equal(result.receipts[0].status, 'Succeeded');
        assert.equal((await call(`mcp/plans/${plan.id}/run`, { digest: plan.digest })).response.status, 409);
      });
    }
  } finally { for (const id of ['bicep', 'learn']) await call(`mcp/${id}/disconnect`, {}); }
}
const directory = process.env.PORTAL_MCP_TEST_RESULTS ?? 'artifacts/mcp-integration'; fs.mkdirSync(directory, { recursive: true });
fs.writeFileSync(`${directory}/http.json`, JSON.stringify({ observedUtc: new Date().toISOString(), architecture: data.architecture, publicProvidersTested: process.env.PLATFORM_MCP_LIVE_PUBLIC === '1', loginStarted: false, inferenceStarted: false, externalChanges: false, checks, inventories }, null, 2));
console.log(`PASS: ${checks.length} real MCP HTTP checks; ${inventories.map(p => `${p.provider}=${p.toolCount}`).join(', ') || 'no provider calls'}. No sign-in, inference or external changes.`);
