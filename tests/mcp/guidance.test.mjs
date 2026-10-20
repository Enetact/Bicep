import assert from 'node:assert/strict';
import crypto from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { test } from 'node:test';
import { composeGuidance } from '../../scripts/mcp/update-guidance.mjs';

const compiler = fileURLToPath(new URL('../../scripts/mcp/update-guidance.mjs', import.meta.url));
const providers = ['bicep', 'azure', 'ado', 'github', 'learn', 'fabric', 'aspire'];
const sha256 = value => crypto.createHash('sha256').update(value).digest('hex');

function fixture(t) {
  const root = fs.mkdtempSync(path.join(os.tmpdir(), 'mcp-guidance-tests-'));
  t.after(() => fs.rmSync(root, { recursive: true, force: true }));
  const manifest = {
    schemaVersion: 1,
    skill: { name: 'platform-mcp-orchestration', description: 'Synthetic complete provider guidance.' },
    providers: [...providers],
    sources: [{ id: 'common', title: 'Shared instructions', path: 'instruction.md', kind: 'markdown' }]
  };
  function write(relative, content) {
    const target = path.join(root, relative);
    fs.mkdirSync(path.dirname(target), { recursive: true });
    fs.writeFileSync(target, content);
  }
  write('instruction.md', '# Shared instructions\n\nCOMMON-GUIDANCE-MARKER.\n');
  for (const id of providers) {
    for (const [name, kind, content] of [
      ['SKILL.md', 'markdown', `---\nname: fixture-${id}\ndescription: Synthetic ${id}.\n---\n\n# ${id} skill\n\nPROVIDER-${id}-SKILL-MARKER.\n`],
      ['instruction.md', 'markdown', `# ${id} instructions\n\nPROVIDER-${id}-INSTRUCTION-MARKER.\n`],
      ['references.md', 'markdown', `# ${id} reference\n\nPROVIDER-${id}-REFERENCE-MARKER.\n`],
      ['examples.json', 'json', JSON.stringify({ provider: id, example: `PROVIDER-${id}-EXAMPLE-MARKER`, steps: [] }, null, 2) + '\n']
    ]) {
      const relative = `providers/${id}/${name}`;
      manifest.sources.push({ id: `${id}-${name.replace(/\./g, '-').toLowerCase()}`, title: `${id} ${name}`, path: relative, kind });
      write(relative, content);
    }
  }
  function save() { write('guidance.json', JSON.stringify(manifest, null, 2) + '\n'); }
  function extra(relative, content, kind = 'markdown') {
    manifest.sources.push({ id: 'extra-' + manifest.sources.length, title: 'Synthetic extra source', path: relative, kind });
    write(relative, content); save();
  }
  save();
  return { root, manifest, write, save, extra };
}

function cli(root, check = false) {
  return spawnSync(process.execPath, [compiler, '--root', root, ...(check ? ['--check'] : [])], { encoding: 'utf8', timeout: 15_000 });
}

test('complete provider guidance is deterministic, content-addressed and independent of checkout location', async t => {
  const first = fixture(t); const second = fixture(t);
  const a = await composeGuidance(first.root); const b = await composeGuidance(second.root);
  assert.equal(a.text, b.text);
  assert.deepEqual(a.lock, b.lock);
  assert.deepEqual(await composeGuidance(first.root), a);
  assert.equal(a.lock.schemaVersion, 1);
  assert.equal(a.lock.outputSha256, sha256(a.text));
  for (const field of ['manifestSha256', 'bundleSha256']) assert.match(a.lock[field], /^[a-f0-9]{64}$/);
  assert.deepEqual(a.lock.sources.map(s => s.path).sort(), first.manifest.sources.map(s => s.path).sort());
  for (const source of a.lock.sources) {
    const bytes = fs.readFileSync(path.join(first.root, source.path));
    assert.equal(source.sha256, sha256(bytes), source.path);
    assert.equal(source.bytes, bytes.length, source.path);
  }
  assert.ok(a.text.includes('COMMON-GUIDANCE-MARKER'));
  for (const id of providers)
    for (const part of ['SKILL', 'INSTRUCTION', 'REFERENCE', 'EXAMPLE'])
      assert.equal(a.text.split(`PROVIDER-${id}-${part}-MARKER`).length - 1, 1, `${id} ${part} appears exactly once`);
  first.write('providers/azure/instruction.md', '# Azure instructions\n\nCHANGED-AZURE-INSTRUCTION.\n');
  const changed = await composeGuidance(first.root);
  assert.notEqual(changed.lock.bundleSha256, a.lock.bundleSha256);
  assert.notEqual(changed.lock.outputSha256, a.lock.outputSha256);
  assert.equal(a.text.includes('CHANGED-AZURE-INSTRUCTION'), false, 'an already composed snapshot stays unchanged');
});

test('Windows line endings and a UTF-8 BOM do not change the compiled guidance identity', async t => {
  const lf = fixture(t); const windows = fixture(t);
  for (const relative of ['guidance.json', ...windows.manifest.sources.map(s => s.path)]) {
    const content = fs.readFileSync(path.join(windows.root, relative), 'utf8');
    windows.write(relative, '\uFEFF' + content.replaceAll('\n', '\r\n'));
  }
  assert.deepEqual(await composeGuidance(lf.root), await composeGuidance(windows.root));
});

test('check mode detects stale generated skill, stale lock and changed normative inputs without rewriting them', t => {
  const f = fixture(t);
  let result = cli(f.root);
  assert.equal(result.status, 0, result.stderr || result.stdout);
  result = cli(f.root, true);
  assert.equal(result.status, 0, result.stderr || result.stdout);
  for (const relative of ['SKILL.md', 'guidance.lock.json', 'providers/ado/instruction.md']) {
    const file = path.join(f.root, relative);
    const changed = fs.readFileSync(file, 'utf8') + '\nSTALE-FIXTURE-MARKER\n';
    fs.writeFileSync(file, changed);
    result = cli(f.root, true);
    assert.notEqual(result.status, 0, `${relative} must invalidate check mode`);
    assert.equal(fs.readFileSync(file, 'utf8'), changed, '--check is read-only');
    if (relative === 'guidance.lock.json') fs.unlinkSync(file);
    result = cli(f.root);
    assert.equal(result.status, 0, result.stderr || result.stdout);
  }
});

test('the exact seven providers and their skill and instruction entry points are required', async t => {
  for (const fault of ['missing-provider', 'duplicate-provider', 'unknown-provider', 'common', 'skill', 'instruction']) {
    const f = fixture(t);
    if (fault === 'missing-provider') f.manifest.providers.pop();
    else if (fault === 'duplicate-provider') f.manifest.providers[4] = 'azure';
    else if (fault === 'unknown-provider') f.manifest.providers[4] = 'other';
    else {
      const removed = fault === 'common' ? 'instruction.md' : `providers/learn/${fault === 'skill' ? 'SKILL.md' : 'instruction.md'}`;
      f.manifest.sources = f.manifest.sources.filter(s => s.path !== removed);
    }
    f.save();
    await assert.rejects(async () => composeGuidance(f.root), fault);
  }
});

test('duplicate source paths, duplicate IDs, missing files and invalid JSON are rejected', async t => {
  for (const fault of ['duplicate-path', 'duplicate-case-path', 'duplicate-resource-case', 'resource-source-case', 'generated-source-case', 'duplicate-id', 'missing-file', 'invalid-json']) {
    const f = fixture(t);
    if (fault === 'duplicate-path') f.manifest.sources.push({ ...f.manifest.sources[0], id: 'different-id' });
    if (fault === 'duplicate-case-path') f.manifest.sources.push({ ...f.manifest.sources[0], id: 'different-id', path: 'Instruction.md' });
    if (fault === 'duplicate-resource-case') {
      f.write('agents/openai.yaml', 'display_name: Synthetic metadata\n');
      f.manifest.resources = [{ path: 'agents/openai.yaml' }, { path: 'agents/OpenAI.yaml' }];
    }
    if (fault === 'resource-source-case') f.manifest.resources = [{ path: 'Instruction.md' }];
    if (fault === 'generated-source-case') {
      f.write('SKILL.md', '# Generated output\n\nGENERATED-SOURCE-MARKER.\n');
      f.manifest.sources.push({ id: 'generated-alias', title: 'Generated alias', path: 'skill.md', kind: 'markdown' });
    }
    if (fault === 'duplicate-id') f.manifest.sources[1].id = f.manifest.sources[0].id;
    if (fault === 'missing-file') fs.unlinkSync(path.join(f.root, 'providers/bicep/instruction.md'));
    if (fault === 'invalid-json') f.write('providers/github/examples.json', '{ invalid JSON }');
    f.save();
    await assert.rejects(async () => composeGuidance(f.root), fault);
  }
});

test('input paths cannot traverse outside the pack or use absolute or ambiguous separators', async t => {
  for (const relative of ['../outside.md', '/absolute.md', 'C:/absolute.md', 'providers\\azure\\instruction.md', 'providers//azure/instruction.md', './instruction.md']) {
    const f = fixture(t);
    f.manifest.sources.push({ id: 'unsafe', title: 'Unsafe path', path: relative, kind: 'markdown' });
    f.save();
    await assert.rejects(async () => composeGuidance(f.root), relative);
  }
});

test('directory links cannot supply guidance, even when their target is inside the pack', async t => {
  const f = fixture(t);
  f.write('linked-source/guide.md', '# Linked instructions\n\nLINKED-MARKER.\n');
  fs.symlinkSync(path.join(f.root, 'linked-source'), path.join(f.root, 'linked'), process.platform === 'win32' ? 'junction' : 'dir');
  f.manifest.sources.push({ id: 'linked', title: 'Linked source', path: 'linked/guide.md', kind: 'markdown' });
  f.save();
  await assert.rejects(async () => composeGuidance(f.root));
});

test('the complete pack has a hard byte budget instead of silently truncating instructions', async t => {
  const f = fixture(t);
  f.extra('references/too-large.md', '# Large source\n\n' + 'x'.repeat(192 * 1024));
  await assert.rejects(async () => composeGuidance(f.root));
});

test('compiled links resolve to embedded sections and metadata resources are hashed without becoming model instructions', async t => {
  const f = fixture(t);
  f.write('instruction.md', '# Shared instructions\n\nCOMMON-GUIDANCE-MARKER. Read [Bicep](providers/bicep/SKILL.md) and [Azure](providers/azure/instruction.md#details).\n');
  f.manifest.resources = [{ path: 'agents/openai.yaml' }];
  f.write('agents/openai.yaml', 'display_name: AUTHORING-METADATA-MARKER\n'); f.save();
  const result = await composeGuidance(f.root);
  for (const relative of ['providers/bicep/SKILL.md', 'providers/azure/instruction.md']) {
    const source = f.manifest.sources.find(s => s.path === relative);
    assert.ok(result.text.includes(`](#${source.id})`));
    assert.ok(result.text.includes(`<a id="${source.id}"></a>`));
  }
  assert.equal(result.text.includes('AUTHORING-METADATA-MARKER'), false);
  assert.deepEqual(result.lock.resources.map(s => s.path), ['agents/openai.yaml']);
  assert.equal(result.lock.resources[0].sha256, sha256('display_name: AUTHORING-METADATA-MARKER\n'));
  f.write('agents/openai.yaml', 'display_name: CHANGED-AUTHORING-METADATA\n');
  assert.notEqual((await composeGuidance(f.root)).lock.bundleSha256, result.lock.bundleSha256);
});

test('nested authoring links are rebased to the generated root while embedded links use section anchors', async t => {
  const f = fixture(t);
  f.extra('references/nested/workflows.md', '# Nested references\n\n' +
    '[Bundle](../../../../../vendor/example/bundle.json#source-index)\n' +
    '[License](../../../../../vendor/example/LICENSE)\n' +
    '[Metadata](../../agents/openai.yaml#interface)\n' +
    '[Bicep](../../providers/bicep/SKILL.md#how-to)\n' +
    '[Public](https://learn.microsoft.com/en-us/azure/#overview)\n' +
    '[Local anchor](#same-section)\n');
  const result = await composeGuidance(f.root);
  assert.ok(result.text.includes('[Bundle](../../../vendor/example/bundle.json#source-index)'));
  assert.ok(result.text.includes('[License](../../../vendor/example/LICENSE)'));
  assert.ok(result.text.includes('[Metadata](agents/openai.yaml#interface)'));
  const bicep = f.manifest.sources.find(s => s.path === 'providers/bicep/SKILL.md');
  assert.ok(result.text.includes(`[Bicep](#${bicep.id})`));
  assert.ok(result.text.includes('[Public](https://learn.microsoft.com/en-us/azure/#overview)'));
  assert.ok(result.text.includes('[Local anchor](#same-section)'));
  assert.equal(result.text.includes('](../../../../../vendor/example/'), false);
});

test('descriptions with YAML punctuation remain one safely quoted scalar with an exact round trip', async t => {
  for (const description of [
    'Scope: tenant and subscription # keep the complete description',
    'Use "quoted" targets, apostrophes\' and C:\\synthetic\\paths: carefully # intact'
  ]) {
    const f = fixture(t);
    f.manifest.skill.description = description; f.save();
    const result = await composeGuidance(f.root);
    const match = /^description: (.+)$/m.exec(result.text);
    assert.ok(match, 'generated frontmatter includes a single description line');
    assert.ok(match[1].startsWith('"') && match[1].endsWith('"'), 'JSON quoted strings are valid YAML scalars');
    assert.equal(JSON.parse(match[1]), description);
    assert.equal(result.text.split('\n').filter(line => line.startsWith('description: ')).length, 1);
  }
});
