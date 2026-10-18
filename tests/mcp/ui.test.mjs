import assert from 'node:assert/strict';
import { readFile } from 'node:fs/promises';
import { test } from 'node:test';
import { parseToolArguments, planControls, setupMcp } from '../../src/SelfService.Portal/wwwroot/mcp.mjs';

test('tool arguments require an object and execution refuses unknown or terminal states', () => {
  assert.deepEqual(parseToolArguments('{"scope":"fixture"}'), { scope: 'fixture' });
  for (const value of ['null', '[]', '1', 'true', '"text"', '{bad}']) assert.throws(() => parseToolArguments(value));
  for (const state of ['Running', 'Completed', 'Failed', 'UnknownOutcome', 'Invented'])
    assert.deepEqual(planControls({ id: 'p', digest: 'd', state }), { approve: false, run: false });
  assert.deepEqual(planControls({ id: 'p', digest: 'd', state: 'AwaitingApproval' }), { approve: true, run: false });
  assert.deepEqual(planControls({ id: 'p', digest: 'd', state: 'Approved' }), { approve: false, run: true });
  assert.deepEqual(planControls({ id: 'p', state: 'Approved' }), { approve: false, run: false });
});

class Element {
  constructor(tag = 'div') { this.tag = tag; this.children = []; this.dataset = {}; this.value = ''; this.textContent = ''; this.listeners = {}; this.disabled = false; this.hidden = false; }
  append(...children) { this.children.push(...children); }
  replaceChildren(...children) { this.children = children; if (this.tag === 'select') this.value = children[0]?.value ?? ''; }
  addEventListener(name, fn) { (this.listeners[name] ??= []).push(fn); }
  querySelectorAll(selector) { return this.children.flatMap(c => [c, ...c.querySelectorAll(selector)]).filter(c => c.tag === selector); }
  async emit(name) { for (const fn of this.listeners[name] ?? []) await fn({}); }
  scrollIntoView() {} focus() {} click() {}
  set innerHTML(_) { throw new Error('Untrusted markup must not be used'); }
}

test('actual MCP UI binds ordered steps and exact approval, renders metadata as text and invalidates changed drafts', async () => {
  const source = await readFile(new URL('../../src/SelfService.Portal/wwwroot/index.html', import.meta.url), 'utf8');
  const module = await readFile(new URL('../../src/SelfService.Portal/wwwroot/mcp.mjs', import.meta.url), 'utf8');
  const htmlIds = [...source.matchAll(/\bid="([^"]+)"/g)].map(m => m[1]);
  assert.equal(new Set(htmlIds).size, htmlIds.length, 'HTML identifiers are unique');
  const ids = [...new Set([...module.matchAll(/\$\('([^']+)'\)/g)].map(m => m[1]))];
  for (const id of ids) assert.ok(htmlIds.includes(id), `Missing UI element ${id}`);
  const nodes = Object.fromEntries(ids.map(id => [id, new Element(['mcp-step-provider', 'mcp-step-tool', 'mcp-tool-filter'].includes(id) ? 'select' : 'div')]));
  const events = {};
  globalThis.document = {
    getElementById: id => nodes[id], createElement: tag => new Element(tag),
    addEventListener: (name, fn) => { (events[name] ??= []).push(fn); },
    dispatchEvent: event => { for (const fn of events[event.type] ?? []) fn(event); }
  };
  globalThis.window = { addEventListener() {} };
  const calls = [], notices = []; let current;
  const api = async (path, body) => {
    calls.push({ path, body });
    if (path === 'mcp/status') return { providers: [{ id: 'azure', name: '<script>hostile</script>', connected: true, state: 'ready', tools: [{ name: 'read_fixture', description: '<img src=x>', effect: 'Read', inputSchema: { type: 'object' } }] }] };
    if (path === 'mcp/plans' && body) { current = { id: 'plan1', digest: 'exact-digest', state: 'AwaitingApproval', steps: structuredClone(body.steps) }; return structuredClone(current); }
    if (path === 'mcp/plans') return { plans: current ? [structuredClone(current)] : [] };
    if (path === 'mcp/plans/plan1/approve') { assert.equal(body.digest, 'exact-digest'); current.state = 'Approved'; return structuredClone(current); }
    if (path === 'mcp/plans/plan1/run') { assert.equal(body.digest, 'exact-digest'); current.state = 'Completed'; current.receipts = [{ state: 'Completed' }]; return structuredClone(current); }
    if (path === 'mcp/plans/plan1') return structuredClone(current);
    throw new Error(path);
  };
  try {
    setupMcp({ api, notice: (...args) => notices.push(args) });
    for (let i = 0; i < 4; i++) await new Promise(setImmediate);
    assert.equal(nodes['mcp-step-provider'].value, 'azure'); assert.equal(nodes['mcp-step-tool'].value, 'read_fixture');
    assert.ok(nodes['mcp-providers'].children[0].children.some(n => n.textContent === '<script>hostile</script>'));
    assert.ok(calls.every(c => c.body === undefined), 'Readiness must not start tool execution');
    nodes['mcp-step-arguments'].value = '{"target":"fixture-a"}'; await nodes['mcp-add-step'].emit('click');
    nodes['mcp-step-arguments'].value = '{"target":"fixture-b"}'; await nodes['mcp-add-step'].emit('click');
    await nodes['mcp-steps'].children[1].children.find(n => n.textContent === 'Move up').emit('click');
    await nodes['mcp-preview'].emit('click');
    assert.equal(current.steps[0].arguments.target, 'fixture-b'); assert.equal(nodes['mcp-approve'].hidden, false); assert.equal(nodes['mcp-run'].hidden, true);
    await nodes['mcp-approve'].emit('click'); assert.equal(nodes['mcp-run'].hidden, false);
    await nodes['mcp-run'].emit('click'); assert.equal(current.state, 'Completed'); assert.equal(nodes['mcp-run'].hidden, true);
    await nodes['mcp-add-step'].emit('click');
    assert.equal(nodes['mcp-plan-result'].hidden, true); assert.equal(nodes['mcp-approve'].hidden, true); assert.equal(nodes['mcp-run'].hidden, true);
    assert.equal(notices.length, 0); assert.equal(calls.filter(c => c.path.endsWith('/run')).length, 1); assert.equal(calls.filter(c => c.path.endsWith('/approve')).length, 1);
  } finally { delete globalThis.document; delete globalThis.window; }
});
