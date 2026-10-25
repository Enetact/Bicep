import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';
import assert from 'node:assert/strict';

const root = fileURLToPath(new URL('../../', import.meta.url));
let skills = 0, files = 0;
for (const [directory, repository, commit, count] of [
  ['azure-devops-skills', 'https://github.com/microsoft/azure-devops-skills', '844a1f6f5202aa920ce5b1be95c056d529d63ded', 7],
  ['microsoft-learn-skills', 'https://github.com/MicrosoftDocs/mcp', 'f8ffde185dfd232dbf5d187c22ce299eadc3d583', 3]
]) {
  const folder = path.join(root, 'vendor', directory);
  const bundle = JSON.parse(fs.readFileSync(path.join(folder, 'bundle.json'), 'utf8'));
  assert.equal(bundle.schemaVersion, 1); assert.equal(bundle.repository, repository); assert.equal(bundle.commit, commit);
  assert.equal(bundle.skills.length, count); assert.equal(bundle.skillDefinitions, count);
  const expected = new Set(['bundle.json']), walked = [];
  for (const entry of bundle.files) {
    assert.equal(typeof entry.path, 'string');
    assert(!path.isAbsolute(entry.path) && !entry.path.includes('\\') && !entry.path.split('/').some(p => ['', '.', '..'].includes(p)));
    assert(!expected.has(entry.path)); expected.add(entry.path);
    const content = fs.readFileSync(path.join(folder, entry.path));
    assert.equal(content.length, entry.bytes, entry.path);
    assert.equal(crypto.createHash('sha256').update(content).digest('hex'), entry.sha256, entry.path);
    assert.equal(crypto.createHash('sha1').update(Buffer.from(`blob ${content.length}\0`)).update(content).digest('hex'), entry.gitBlobSha, entry.path);
    assert(entry.sourceUrl.includes(`/${commit}/`) && entry.sourceUrl.startsWith('https://raw.githubusercontent.com/'));
    files++;
  }
  const walk = relative => {
    for (const entry of fs.readdirSync(path.join(folder, relative), { withFileTypes: true })) {
      assert(!entry.isSymbolicLink(), 'Linked upstream source');
      const next = path.posix.join(relative, entry.name);
      if (entry.isDirectory()) walk(next); else walked.push(next);
    }
  };
  walk(''); assert.deepEqual(walked.sort(), [...expected].sort());
  assert.deepEqual(bundle.skills.map(s => s.path).sort(), walked.filter(p => p.endsWith('/SKILL.md')).sort());
  for (const file of bundle.licenseFiles) assert(expected.has(file));
  skills += count;
}
console.log(`PASS: ${skills} complete official ADO/Learn skills; ${files} upstream files match pinned bytes, SHA256, Git blobs and inventories.`);
