import assert from 'node:assert/strict';
import test from 'node:test';
import { createNamingDrafts, renderNamingEditor } from '../../src/SelfService.Portal/wwwroot/naming.mjs';

const product = { id:'storage', type:'private-storage' }, target = { subscriptionId:'SUB-A', environment:'dev', workload:'storage', network:'private' };
class Element {
  constructor(tag) { this.tag = tag; this.children = []; this.handlers = {}; this.isConnected = true; this.textContent = ''; this.classList = { add(){} }; }
  append(...nodes) { this.children.push(...nodes); }
  replaceChildren(...nodes) { this.children = nodes; }
  setAttribute() {}
  addEventListener(event, fn) { this.handlers[event] = fn; }
}
globalThis.document = { createElement: tag => new Element(tag) };
const input = (host, name) => host.children.flatMap(n => n.children).find(n => n.name === name);
const button = (host, text) => host.children.find(n => n.tag === 'button' && n.textContent === text);
const message = host => host.children.find(n => n.tag === 'p' && !n.textContent);
const edit = (host, name, value) => { const field = input(host, name); field.value = value; field.handlers.input(); };

test('typed naming intent and instance survive operation redraws; a new draft is explicit', () => {
  let number = 0; const drafts = createNamingDrafts(() => `uuid-${++number}`), host = new Element('section');
  const api = () => { throw new Error('No automatic API calls'); };
  renderNamingEditor(host, api, product, target, 'eastus2', drafts);
  edit(host, 'stackDisplayName', 'Claims service'); edit(host, 'stackAcronym', 'clm'); edit(host, 'namespaceSalt', 'permanent');
  const id = input(host, 'instanceId').value;
  renderNamingEditor(host, api, product, target, 'eastus2', drafts);
  assert.equal(input(host, 'stackDisplayName').value, 'Claims service'); assert.equal(input(host, 'instanceId').value, id);
  assert.equal(input(host, 'namespaceSalt').value, 'permanent');
  button(host, 'Start new naming draft').onclick();
  assert.equal(input(host, 'instanceId').value, 'uuid-2'); assert.equal(input(host, 'stackDisplayName').value, '');
});

test('scope drafts remain separate and session reset discards every scope', () => {
  let number = 0; const drafts = createNamingDrafts(() => `uuid-${++number}`);
  const first = drafts.select(product, target, 'eastus2'); first.stackAcronym = 'clm'; first.previewInstance = 'registered-a';
  assert.notEqual(drafts.select(product, target, 'westus2'), first);
  assert.notEqual(drafts.select(product, {...target, environment:'prod'}, 'eastus2'), first);
  assert.notEqual(drafts.select(product, {...target, subscriptionId:'sub-b'}, 'eastus2'), first);
  assert.equal(drafts.select(product, {...target, subscriptionId:'sub-a'}, 'eastus2'), first);
  assert.equal(drafts.select(product, target, 'eastus2').previewInstance, 'registered-a');
  drafts.reset(); const fresh = drafts.select(product, target, 'eastus2');
  assert.equal(fresh.stackAcronym, ''); assert.equal(fresh.previewInstance, 'legacy'); assert.notEqual(fresh.instanceId, first.instanceId);
});

test('late calculations cannot populate a redrawn scope or a reset identity', async () => {
  for (const reset of [false, true]) {
    let resolve; const pending = new Promise(r => { resolve = r; }), drafts = createNamingDrafts(() => 'uuid');
    const host = new Element('section'); renderNamingEditor(host, () => pending, product, target, 'eastus2', drafts);
    const oldStatus = message(host), running = button(host, 'Calculate names').onclick();
    if (reset) drafts.reset();
    renderNamingEditor(host, () => pending, product, target, 'westus2', drafts);
    resolve({}); await running; // Invalid payload would throw/render if a stale response were accepted.
    assert.equal(oldStatus.textContent, 'Calculating locally…'); assert.equal(message(host).textContent, '');
    assert.equal(host.children.at(-1).children.length, 0);
  }
});

test('editing intent invalidates a pending calculation and errors from old views stay hidden', async () => {
  let reject; const pending = new Promise((_, r) => { reject = r; }), drafts = createNamingDrafts(() => 'uuid'), host = new Element('section');
  renderNamingEditor(host, () => pending, product, target, 'eastus2', drafts);
  const running = button(host, 'Calculate names').onclick(); edit(host, 'stackDisplayName', 'Changed');
  reject(new Error('stale failure')); await running;
  assert.equal(host.children.find(n => n.textContent === 'Settings changed; calculate a new draft.')?.tag, 'p');
  assert.equal(drafts.select(product, target, 'eastus2').stackDisplayName, 'Changed');
});
