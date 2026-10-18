import assert from 'node:assert/strict';
import test from 'node:test';
import fs from 'node:fs';
import { moduleOptions, renderModuleOptions } from '../../src/SelfService.Portal/wwwroot/avm-modules.mjs';

function surface() {
  const document = { createElement: tag => new Element(tag) };
  class Element {
    constructor(tag) { this.tagName = tag; this.ownerDocument = document; this.children = []; this.textContent = ''; }
    append(...nodes) { this.children.push(...nodes); }
    replaceChildren(...nodes) { this.children = nodes; }
    set innerHTML(_) { throw new Error('HTML parsing is not permitted for catalog content.'); }
  }
  return new Element('div');
}

test('Module browser excludes unavailable modules and identifies required settings and optional typed children', () => {
  const catalog = { modules: [
    { id: 'private-module', draftSelectable: true, parameters: {
      name: { type: 'string' }, enabled: { type: 'bool', defaultValue: false },
      names: { type: 'array', items: { type: 'string' }, defaultValue: [] },
      children: { type: 'array', items: { type: 'object', properties: { name: { type: 'string' } }, additionalProperties: false }, defaultValue: [] }
    } },
    { id: 'unqualified-module', draftSelectable: false, parameters: { unavailable: { type: 'string' } } }
  ] };
  const options = moduleOptions(catalog);
  assert.equal(options.length, 1);
  assert.deepEqual(options[0].required, ['name']);
  assert.deepEqual(options[0].children, ['children']);
  assert.equal(options[0].origin, 'Local module');
});

test('Module browser renders untrusted catalog strings as text and replaces a previous result', () => {
  const container = surface(), payload = '<img src=x onerror=alert(1)>';
  renderModuleOptions(container, { modules: [{ id: payload, draftSelectable: true, parameters: { name: { type: 'string', metadata: { description: payload } } } }] });
  const nodes = [];
  const visit = node => { nodes.push(node); for (const child of node.children) visit(child); };
  visit(container);
  assert.ok(nodes.some(node => node.tagName === 'pre' && node.textContent.includes(payload)));
  assert.ok(nodes.every(node => ['div', 'p', 'details', 'summary', 'pre'].includes(node.tagName)));
  assert.match(container.children[0].textContent, /1 modules available/);
  renderModuleOptions(container, { modules: [] });
  assert.equal(container.children.length, 1);
  assert.match(container.children[0].textContent, /0 modules available/);
});

test('All six pinned AVM modules expose reviewed names, versions and typed child options in the browser', () => {
  const catalog = JSON.parse(fs.readFileSync('config/avm-module-contracts.json', 'utf8'));
  const options = moduleOptions(catalog);
  assert.equal(options.length, 6);
  for (const option of options) {
    assert.match(option.origin, /^Azure Verified Module · \d+\.\d+\.\d+$/);
    for (const input of ['name', 'location', 'tags']) assert.ok(option.required.includes(input), `${option.id} lacks required ${input}`);
    assert.ok(!option.name.startsWith('avm-'));
  }
  const children = id => options.find(option => option.id === id).children;
  assert.deepEqual(children('avm-identity-user-assigned-identity'), ['federatedIdentityCredentials']);
  assert.deepEqual(children('avm-containers-container-registry'), ['replications']);
  assert.deepEqual(children('avm-configuration-app-configuration'), ['keyValues', 'replicaLocations']);
  assert.deepEqual(children('avm-network-network-security-group'), ['securityRules']);
  assert.deepEqual(children('avm-network-route-table'), ['applianceRoutes', 'routes']);
});
