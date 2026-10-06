import fs from 'node:fs';
import { workloadNames } from './workload.mjs';
try {
  if (process.argv.length !== 2) throw new Error('Supply one naming draft on stdin; no arguments.');
  let size = 0; const chunks = [];
  for await (const chunk of process.stdin) { size += chunk.length; if (size > 65536) throw new Error('Draft exceeds 64 KiB.'); chunks.push(chunk); }
  const read = file => JSON.parse(fs.readFileSync(new URL(file, import.meta.url), 'utf8'));
  const result = workloadNames(JSON.parse(Buffer.concat(chunks).toString('utf8').replace(/^\uFEFF/, '')), read('../../config/naming-policy.json'), read('../../config/workloads.json'));
  process.stdout.write(JSON.stringify(result, null, 2) + '\n');
} catch (error) { process.stderr.write(`Naming draft rejected: ${error instanceof SyntaxError ? 'Invalid JSON.' : error.message}\n`); process.exitCode = 1; }
