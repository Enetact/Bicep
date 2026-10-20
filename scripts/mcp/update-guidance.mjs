import fs from 'node:fs';
import path from 'node:path';
import crypto from 'node:crypto';
import { fileURLToPath } from 'node:url';

const providers = ['bicep', 'azure', 'ado', 'github', 'learn', 'fabric', 'aspire'];
const maximumBytes = 192 * 1024;
const normalize = value => value.replaceAll('\r\n', '\n').replace(/^\uFEFF/, '');
const hash = value => crypto.createHash('sha256').update(value).digest('hex');
const fail = message => { throw Error(`MCP guidance: ${message}`); };
function safeFile(root, relative) {
  if (typeof relative !== 'string' || !relative || path.isAbsolute(relative) || relative.includes('\\') ||
      relative.split('/').some(part => !part || part === '.' || part === '..' || part.startsWith('.')) ||
      !/\.(md|json|yaml)$/i.test(relative)) fail('invalid source path');
  let file = root;
  for (const part of relative.split('/')) {
    file = path.join(file, part);
    if (!fs.existsSync(file) || fs.lstatSync(file).isSymbolicLink()) fail('missing or linked source');
  }
  if (!fs.statSync(file).isFile() || fs.statSync(file).size > maximumBytes) fail('invalid or oversized source');
  return file;
}
function body(text) {
  if (!text.startsWith('---\n')) return text.trim();
  const end = text.indexOf('\n---\n', 4);
  if (end < 0) fail('unterminated skill frontmatter');
  return text.slice(end + 5).trim();
}
export function composeGuidance(packRoot) {
  const root = path.resolve(packRoot);
  if (!fs.existsSync(root) || fs.lstatSync(root).isSymbolicLink()) fail('invalid bundle root');
  const manifestText = normalize(fs.readFileSync(safeFile(root, 'guidance.json'), 'utf8'));
  const manifest = JSON.parse(manifestText);
  if (manifest.schemaVersion !== 1 || !manifest.skill || !/^[a-z0-9-]{1,64}$/.test(manifest.skill.name) ||
      typeof manifest.skill.description !== 'string' || !manifest.skill.description.trim() ||
      manifest.skill.description.length > 1024 || /[\r\n]/.test(manifest.skill.description) ||
      !Array.isArray(manifest.providers) || manifest.providers.length !== providers.length ||
      providers.some(p => !manifest.providers.includes(p)) || !Array.isArray(manifest.sources) ||
      manifest.sources.length > 64 || manifest.sources.length === 0) fail('invalid bundle manifest');
  const seen = new Set(), ids = new Set(), sections = [], sources = [];
  let bytes = Buffer.byteLength(manifestText);
  for (const source of manifest.sources) {
    const key = typeof source?.path === 'string' ? source.path.toLowerCase() : '';
    if (!source || !key || !/^[a-z][a-z0-9-]{0,100}$/.test(source.id) || ids.has(source.id) || seen.has(key) ||
        typeof source.title !== 'string' || !source.title.trim() || /[\r\n]/.test(source.title) ||
        !['markdown', 'json'].includes(source.kind) || ['skill.md', 'guidance.lock.json', 'guidance.json'].includes(key)) fail('invalid or duplicate source');
    if (source.kind === 'markdown' && !source.path.endsWith('.md') || source.kind === 'json' && !source.path.endsWith('.json')) fail('source kind mismatch');
    const content = normalize(fs.readFileSync(safeFile(root, source.path), 'utf8'));
    if (!content.trim()) fail('empty source');
    const length = Buffer.byteLength(content); bytes += length;
    if (bytes > maximumBytes) fail('bundle exceeds 192 KiB');
    if (source.kind === 'json') JSON.parse(content);
    ids.add(source.id); seen.add(key);
    sources.push({ path: source.path, sha256: hash(content), bytes: length });
    sections.push({ ...source, content });
  }
  for (const required of ['instruction.md', ...providers.flatMap(p => [`providers/${p}/SKILL.md`, `providers/${p}/instruction.md`])])
    if (!seen.has(required.toLowerCase())) fail('bundle omits required provider guidance');
  const resources = [];
  for (const resource of manifest.resources ?? []) {
    const key = typeof resource?.path === 'string' ? resource.path.toLowerCase() : '';
    if (!resource || !key || seen.has(key)) fail('duplicate resource');
    const content = normalize(fs.readFileSync(safeFile(root, resource.path), 'utf8'));
    const length = Buffer.byteLength(content); bytes += length;
    if (bytes > maximumBytes) fail('bundle exceeds 192 KiB');
    seen.add(key); resources.push({ path: resource.path, sha256: hash(content), bytes: length });
  }
  const anchors = new Map(sections.map(s => [s.path, s.id]));
  const rewrite = (text, sourcePath) => text.replace(/\]\(([^)\s]+)\)/g, (match, href) => {
    if (/^(?:[a-z]+:|#)/i.test(href)) return match;
    const [target, fragment] = href.split('#');
    const relative = path.posix.normalize(path.posix.join(path.posix.dirname(sourcePath), target));
    return anchors.has(relative) ? `](#${anchors.get(relative)})` : `](${relative}${fragment === undefined ? '' : '#' + fragment})`;
  });
  const identity = { manifestSha256: hash(manifestText), sources, resources };
  const bundleSha256 = hash(JSON.stringify(identity));
  const text = `---\nname: ${manifest.skill.name}\ndescription: ${JSON.stringify(manifest.skill.description)}\n---\n\n` +
    `<!-- Generated by scripts/Update-McpGuidance.ps1. Edit the indexed companion files. -->\n` +
    `<!-- Bundle SHA-256: ${bundleSha256} -->\n\n# Connected MCP workflow guidance\n\n` +
    `This self-contained skill embeds every normative instruction and example listed below. The isolated workflow only needs this generated file. Follow the sections relevant to the requested providers; repository links to upstream bundles are authoring references. Instructions do not grant service roles or plan approval.\n\n` +
    sections.map(s => `- [${s.title}](#${s.id})`).join('\n') + '\n\n' +
    sections.map(s => `<a id="${s.id}"></a>\n\n## ${s.title}\n\n` +
      (s.kind === 'markdown' ? rewrite(body(s.content), s.path) : '```json\n' + JSON.stringify(JSON.parse(s.content), null, 2) + '\n```')).join('\n\n') + '\n';
  if (Buffer.byteLength(text) > maximumBytes) fail('compiled skill exceeds 192 KiB');
  return { text, lock: { schemaVersion: 1, skill: manifest.skill.name, ...identity, bundleSha256, outputSha256: hash(text), outputBytes: Buffer.byteLength(text) } };
}
function main(args) {
  let root = fileURLToPath(new URL('../../.agents/skills/platform-mcp-orchestration/', import.meta.url));
  let check = false;
  for (let i = 0; i < args.length; i++) {
    if (args[i] === '--check') check = true;
    else if (args[i] === '--root' && args[i + 1]) root = path.resolve(args[++i]);
    else fail('unknown command option');
  }
  const result = composeGuidance(root), output = path.join(root, 'SKILL.md'), lock = path.join(root, 'guidance.lock.json');
  const lockText = JSON.stringify(result.lock, null, 2) + '\n';
  for (const file of [output, lock]) if (fs.existsSync(file) && fs.lstatSync(file).isSymbolicLink()) fail('linked generated output');
  if (check) {
    if (!fs.existsSync(output) || normalize(fs.readFileSync(output, 'utf8')) !== result.text ||
        !fs.existsSync(lock) || normalize(fs.readFileSync(lock, 'utf8')) !== lockText) fail('generated skill or lock is stale; regenerate before review');
  } else {
    fs.writeFileSync(output, result.text); fs.writeFileSync(lock, lockText);
  }
  console.log(`${check ? 'PASS' : 'Updated'}: ${providers.length}-provider guidance; ${result.lock.sources.length} sources; ${result.lock.outputBytes} bytes; ${result.lock.bundleSha256}`);
}
if (process.argv[1] && path.resolve(process.argv[1]) === fileURLToPath(import.meta.url)) {
  try { main(process.argv.slice(2)); } catch (error) { console.error(error.message); process.exitCode = 1; }
}
