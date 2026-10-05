import fs from 'node:fs';
import { validateInstances } from './instances.mjs';

try {
  if (process.argv.length !== 2) throw new Error('This command takes a JSON envelope on stdin; no arguments.');
  const chunks = []; let size = 0;
  for await (const chunk of process.stdin) { size += chunk.length; if (size > 1024 * 1024) throw new Error('Instance input exceeds 1 MiB.'); chunks.push(chunk); }
  const envelope = JSON.parse(Buffer.concat(chunks).toString('utf8').replace(/^\uFEFF/, ''));
  if (!envelope || Array.isArray(envelope) || typeof envelope !== 'object' || !Object.hasOwn(envelope, 'registry') || Object.keys(envelope).some(k => !['registry', 'previousRegistry'].includes(k)) || (Object.hasOwn(envelope, 'previousRegistry') && envelope.previousRegistry === null)) throw new Error('Supply registry and optional non-null previousRegistry.');
  const read = relative => JSON.parse(fs.readFileSync(new URL(relative, import.meta.url), 'utf8'));
  const result = validateInstances(envelope.registry, read('../../config/naming-policy.json'), read('../../config/workloads.json'), envelope.previousRegistry);
  process.stdout.write(JSON.stringify(result, null, 2) + '\n');
} catch (error) { process.stderr.write(`Instance registry rejected: ${error instanceof SyntaxError ? 'Invalid JSON.' : error.message}\n`); process.exitCode = 1; }
