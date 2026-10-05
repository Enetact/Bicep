import fs from 'node:fs';
import { fileURLToPath } from 'node:url';
import { resolveNames } from './core.mjs';

// Single bounded stdin request, stdout JSON response; no Azure/model calls or shell execution.
try {
  if (process.argv.length !== 2) throw new Error('This command takes a JSON request on stdin; no arguments.');
  const chunks = []; let size = 0;
  for await (const chunk of process.stdin) { size += chunk.length; if (size > 1024 * 1024) throw new Error('Naming input exceeds 1 MiB.'); chunks.push(chunk); }
  const policy = JSON.parse(fs.readFileSync(fileURLToPath(new URL('../../config/naming-policy.json', import.meta.url)), 'utf8'));
  const request = JSON.parse(Buffer.concat(chunks).toString('utf8').replace(/^\uFEFF/, ''));
  process.stdout.write(JSON.stringify(resolveNames(request, policy), null, 2) + '\n');
} catch (error) { process.stderr.write(`Naming rejected: ${error instanceof SyntaxError ? 'Invalid JSON.' : error.message}\n`); process.exitCode = 1; }
