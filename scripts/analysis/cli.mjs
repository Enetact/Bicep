import fs from 'node:fs';
import path from 'node:path';
import { analyzeDiscovery, canonical, digest } from './core.mjs';
import { renderAnalysis } from './render.mjs';

const MAX_BYTES = 16 * 1024 * 1024;
function read(file) {
  const info = fs.lstatSync(file);
  if (!info.isFile() || info.isSymbolicLink() || info.size > MAX_BYTES) throw new Error('Input must be a regular JSON file no larger than 16 MiB.');
  const bytes = fs.readFileSync(file);
  return { bytes, value: JSON.parse(bytes.toString('utf8').replace(/^\uFEFF/, '')) };
}
try {
  if (Number(process.versions.node.split('.')[0]) < 22) throw new Error('Node.js 22+ is required.');
  const args = process.argv.slice(2); const options = {};
  for (let i = 0; i < args.length; i += 2) {
    if (!['--input', '--output', '--workload', '--environment', '--at'].includes(args[i]) || !args[i + 1] || args[i + 1].startsWith('--') || Object.hasOwn(options, args[i])) throw new Error('Use --input DIR --output NEW_DIR [--workload NAME --environment NAME --at ISO_TIME].');
    options[args[i]] = args[i + 1];
  }
  if (!options['--input'] || !options['--output']) throw new Error('Supply --input DIR and --output NEW_DIR.');
  const input = fs.realpathSync(options['--input']); const output = path.resolve(options['--output']);
  if (fs.existsSync(output)) throw new Error('Output must be a new directory; existing evidence is never overwritten.');
  const inv = read(path.join(input, 'inventory.json')); const man = read(path.join(input, 'manifest.json'));
  const report = analyzeDiscovery({ manifest: man.value, inventory: inv.value, inventoryBytes: inv.bytes, manifestBytes: man.bytes, workload: options['--workload'], environment: options['--environment'], evaluatedUtc: options['--at'] ?? new Date().toISOString() });
  const files = renderAnalysis(report);
  const hashes = Object.fromEntries([...files].map(([name, content]) => [name, digest(content)]));
  // Reserve the output directory only after input validation; mkdir is exclusive.
  fs.mkdirSync(path.dirname(output), { recursive: true }); fs.mkdirSync(output);
  for (const [name, content] of files) fs.writeFileSync(path.join(output, name), content, { flag: 'wx', encoding: 'utf8' });
  fs.writeFileSync(path.join(output, 'report-manifest.json'), canonical({ schemaVersion: 1, kind: 'platform-analysis-files', files: hashes }) + '\n', { flag: 'wx' });
  console.log(`Saved offline report: ${output}. Assessment: ${report.status}; deployment authorization: false.`);
} catch (error) {
  // JSON parser errors can contain source snippets. Never echo untrusted values.
  console.error(error instanceof SyntaxError ? 'Analysis failed: malformed JSON.' : `Analysis failed: ${error.code ? 'file operation failed (' + error.code + ')' : error.message}`);
  process.exitCode = 1;
}
