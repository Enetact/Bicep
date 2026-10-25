// Explicit local browser fixture. No portal session, identity, provider, or deployment calls.
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
const html = `<!doctype html><html lang="en"><meta charset="utf-8"><title>Naming continuity qualification</title><link rel="stylesheet" href="/styles.css"><body><main style="max-width:960px;margin:2rem auto;padding:1rem"><h1>Naming continuity qualification</h1><p>Synthetic browser fixture. No cloud connection or deployment.</p><label>Environment<select id="environment"><option>dev</option><option>prod</option></select></label><label>Region<select id="region"><option>eastus2</option><option>westus2</option></select></label><label>Operation<select id="operation"><option>discover</option><option>preview</option></select></label><button id="redraw">Redraw workload</button><button id="disconnect">Reset identity</button><section id="editor"></section></main><script type="module" src="/fixture.mjs"></script></body></html>`;
const fixture = `import {createNamingDrafts,renderNamingEditor} from '/naming.mjs';
const drafts=createNamingDrafts(), host=document.getElementById('editor');
function draw(){renderNamingEditor(host,async()=>{throw new Error('Fixture: no provider calls.');},{id:'storage',type:'private-storage'},{environment:document.getElementById('environment').value,subscriptionId:'00000000-0000-0000-0000-000000000005',workload:'storage',network:'private'},document.getElementById('region').value,drafts,draw);}
for(const id of ['environment','region','operation'])document.getElementById(id).onchange=draw;
document.getElementById('redraw').onclick=draw;document.getElementById('disconnect').onclick=()=>{drafts.reset();draw();};draw();`;
const source = new URL('../../src/SelfService.Portal/wwwroot/', import.meta.url);
const server = createServer(async (req, res) => {
  res.setHeader('Cache-Control', 'no-store'); res.setHeader('Content-Security-Policy', "default-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self'; connect-src 'none'");
  const route = req.url;
  if (route === '/') { res.setHeader('Content-Type', 'text/html'); res.end(html); return; }
  if (route === '/fixture.mjs') { res.setHeader('Content-Type', 'text/javascript'); res.end(fixture); return; }
  if (!['/naming.mjs','/topology.mjs','/styles.css'].includes(route)) { res.writeHead(404); res.end(); return; }
  try { res.setHeader('Content-Type', route.endsWith('.css') ? 'text/css' : 'text/javascript'); res.end(await readFile(new URL(route.slice(1), source))); }
  catch { res.writeHead(500); res.end(); }
});
server.listen(5138, '127.0.0.1', () => console.log('NAMING FIXTURE ONLY http://127.0.0.1:5138'));
