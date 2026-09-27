import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import assert from 'node:assert/strict';
const root = path.resolve('vendor/azure-skills');
const bundle = JSON.parse(fs.readFileSync(path.join(root, 'bundle.json')));
assert.equal(bundle.commit, '117b038edfef5d7af09848b8ffcd355f28f19956');
assert.equal(bundle.skills.length, 42); assert.equal(new Set(bundle.skills.map(s => s.id)).size, 42);
const walked = [];
function walk(dir) { for (const f of fs.readdirSync(dir, { withFileTypes: true })) { assert.ok(!f.isSymbolicLink()); const file = path.join(dir, f.name); if (f.isDirectory()) walk(file); else walked.push(path.relative(root, file).split(path.sep).join('/')); } }
walk(root);
assert.deepEqual(walked.filter(p => p !== 'bundle.json').sort(), Object.keys(bundle.files).sort());
for (const [name, hash] of Object.entries(bundle.files)) {
  assert.ok(!name.split('/').includes('..')); assert.ok(!path.isAbsolute(name));
  assert.equal(crypto.createHash('sha256').update(fs.readFileSync(path.join(root, name))).digest('hex'), hash, name);
}
assert.deepEqual(walked.filter(p => p.endsWith('/SKILL.md')).sort(), bundle.skills.map(s => s.path).sort());
for (const s of bundle.skills) { assert.equal(s.pipelineStatus, 'No pipeline associated yet'); assert.ok(['network','inventory','compute','kubernetes','storage','ai','messaging','monitoring','data'].includes(s.discoveryProfile)); }
console.log(`PASS: all ${bundle.skills.length} upstream skill definitions indexed; ${Object.keys(bundle.files).length} source files match pinned hashes.`);
