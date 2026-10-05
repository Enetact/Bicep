import { Client } from '@modelcontextprotocol/sdk/client/index.js';
import { StdioClientTransport } from '@modelcontextprotocol/sdk/client/stdio.js';
import { fileURLToPath } from 'node:url';
import { assertId, assertPlannedId, scopeOf, resourceType, project, limits } from './core.mjs';
import { projection, forType } from './projection.mjs';
import { probeChildren } from './probes.mjs';

export async function connectMcp() {
  const transport = new StdioClientTransport({ command: process.execPath,
    args: [fileURLToPath(new URL('./node_modules/@azure/mcp/index.js', import.meta.url)), 'server', 'start', '--read-only', '--tool', 'group_list', '--tool', 'group_resource_list'],
    // AzureCLI@2 establishes this isolated credential. Never try another developer/managed identity.
    env: { ...process.env, AZURE_TOKEN_CREDENTIALS: 'AzureCliCredential', AZURE_MCP_COLLECT_TELEMETRY: 'false' }, stderr: 'ignore' });
  const client = new Client({ name: 'platform-iac-analysis', version: '1.0.0' });
  try {
    await client.connect(transport);
    const { tools } = await client.listTools();
    for (const name of ['group_list','group_resource_list']) {
      const tool = tools.find(t => t.name === name);
      if (!tool?.annotations?.readOnlyHint || tool.annotations.destructiveHint || !tool.inputSchema.properties.subscription) throw Error('Pinned Azure MCP schema mismatch.');
    }
    if (tools.length !== 2) throw Error('Unexpected Azure MCP tools exposed.');
    return { client, tools };
  } catch (e) { await client.close(); throw e; }
}
function payload(result, key) {
  if (result.isError) throw Error('Azure MCP read failed; resource absence is unknown.');
  const candidates = [result.structuredContent, ...(result.content ?? []).filter(c => c.type === 'text').map(c => { try { return JSON.parse(c.text); } catch { return null; } })];
  for (const c of candidates) {
    if (c?.status && c.status !== 200 && c.status !== '200') throw Error('Azure MCP returned unsuccessful status.');
    const rows = c?.results?.[key] ?? c?.[key];
    if (Array.isArray(rows)) return rows;
  }
  throw Error('Azure MCP returned an unsupported response; no empty inventory inferred.');
}
export async function armRead(token, id, version, continuation=null) {
  if (!/^\d{4}-\d{2}-\d{2}(-preview)?$/.test(version)) throw Error('Invalid API version.');
  const url=new URL(continuation??`https://management.azure.com${id}?api-version=${version}`);
  if(url.origin!=='https://management.azure.com'||url.pathname.toLowerCase()!==id.toLowerCase()||url.username||url.password||url.hash||url.searchParams.get('api-version')!==version)throw Error('Invalid ARM GET route.');
  const response = await fetch(url, {
    method: 'GET', headers: { Authorization: `Bearer ${token}` }, redirect: 'error', signal: AbortSignal.timeout(30000) });
  if (response.status !== 200) return { status: response.status === 404 ? 'Absent' : response.status === 403 ? 'Denied' : 'Unknown', httpStatus: response.status };
  if (Number(response.headers.get('content-length')) > limits.bytes) throw Error('ARM response exceeds limit.');
  const reader = response.body.getReader(); const chunks = []; let size = 0;
  try { while (true) { const {done,value}=await reader.read(); if(done)break; size+=value.length;if(size>limits.bytes)throw Error('ARM response exceeds limit.');chunks.push(value); } }
  finally { await reader.cancel(); }
  return { status: 'Observed', value: JSON.parse(Buffer.concat(chunks).toString('utf8')) };
}
export async function readProjected(id,target,apiVersions,token,read=armRead) {
  assertPlannedId(id,target);const type=resourceType(id),apiVersion=apiVersions[type.toLowerCase()]??forType(type)?.apiVersion;
  if(!apiVersion)return{id,type,status:'NotAssessed',config:{},issue:'No API version in the compiled deployment; metadata identity only.'};
  try {
    const r=await read(token,id,apiVersion);
    if(r.status==='Observed'&&r.value.id&&r.value.id.toLowerCase()!==id.toLowerCase())throw Error('Response identity mismatch.');
    return{id,type,apiVersion,status:r.status,...(r.status==='Observed'?projection(r.value,type):{config:{},propertyCoverage:[]}),httpStatus:r.httpStatus,observedUtc:new Date().toISOString(),source:'Resource ARM GET'};
  }catch{return{id,type,apiVersion,status:'Unknown',config:{},issue:'Read failed or timed out; absence not inferred.'};}
}
export async function completeBefore(snapshot,plan,target,apiVersions,token,read=armRead) {
  if(!snapshot||snapshot.inventoryStatus==='Partial')throw Error('A baseline is required before reading planned IDs.');
  const deadline=Date.now()+300000;
  for(const r of plan.resources) {
    if(Date.now()>deadline)throw Error('Before-state completion exceeded five minutes; remaining reads are unknown.');
    assertPlannedId(r.id,target);
    if(!snapshot.resources.some(x=>x.id.toLowerCase()===r.id.toLowerCase())) {
      if(snapshot.resources.length>=limits.resources)throw Error('Combined before evidence exceeds 500 resources.');
      snapshot.resources.push(await readProjected(r.id,target,apiVersions,token,read));
    }
  }
  snapshot.completedUtc=new Date().toISOString();
  await probeChildren(snapshot,target,apiVersions,token,read);
  snapshot.coverage+=' Missing planned IDs within the selected subscription were read after What-If and before any apply; this includes explicit shared-group and child IDs, not a scan of those groups.';
}
// Injectable transports permit testing denied, missing, partial and cancellation behavior without Azure.
export async function collect({ target, tenantId, token, apiVersions, expected = [] }, { mcp, read = armRead }) {
  const startedUtc = new Date().toISOString(), scope = scopeOf(target), audit = [], issues = [], resources = [];
  const deadline=Date.now()+300000;
  function budget(){if(Date.now()>deadline)throw Error('Collection exceeded five minutes; remaining reads are unknown.');}
  let inventoryStatus = 'Unknown';
  const observed = id => readProjected(id,target,apiVersions,token,read);
  try {
    const subscription = await read(token,`/subscriptions/${target.subscriptionId}`,'2022-12-01');
    if (subscription.status !== 'Observed') throw Error('Subscription visibility not established.');
    const common = {subscription:target.subscriptionId,tenant:tenantId,'retry-max-retries':1,'retry-network-timeout':30};
    async function call(name, args, key) {
      const started = new Date().toISOString();
      try { const rows=payload(await mcp.callTool({name,arguments:args},undefined,{timeout:60000}),key); audit.push({tool:name,startedUtc:started,status:'Succeeded',count:rows.length});return rows; }
      catch { audit.push({tool:name,startedUtc:started,status:'Failed'});throw Error('Azure MCP inventory unavailable.'); }
    }
    const groups = await call('group_list',common,'groups');
    const group = await read(token,scope,'2022-09-01');
    if (group.status === 'Absent' && !groups.some(g => String(g.name ?? g.id?.split('/').at(-1)).toLowerCase() === target.resourceGroup.toLowerCase())) inventoryStatus='GroupAbsent';
    else if (group.status !== 'Observed') throw Error('Resource group visibility is denied or inconsistent.');
    else {
      resources.push({id:scope,type:'Microsoft.Resources/resourceGroups',apiVersion:'2022-09-01',status:'Observed',config:project(group.value)});
      const rows = await call('group_resource_list',{...common,'resource-group':target.resourceGroup},'resources');
      if (rows.length > limits.resources) throw Error('Resource-group inventory exceeds 500 resources. Narrow the workflow scope.');
      const ids=new Set();
      for (const row of rows) {budget();const id=assertId(row.id,target),key=id.toLowerCase();if(ids.has(key))throw Error('Duplicate resource inventory.');ids.add(key);resources.push(await observed(id,resourceType(id)));}
      inventoryStatus='Listed';
    }
    for (const row of expected) {
      budget();
      const id=assertPlannedId(row.id,target);
      if (!resources.some(r=>r.id.toLowerCase()===id.toLowerCase())) resources.push(await observed(id,resourceType(id)));
    }
    if(resources.length>limits.resources)throw Error('Combined evidence exceeds 500 resources.');
  } catch(e) {issues.push(e.message);inventoryStatus='Partial';}
  const snapshot={startedUtc,completedUtc:new Date().toISOString(),inventoryStatus,resources,audit,issues,
    coverage:'Scoped MCP inventory, ARM GETs and fixed profile child probes. Nested allowlisted values only; omitted properties, unsupported child types and effective connectivity remain unknown.'};
  await probeChildren(snapshot,target,apiVersions,token,read);
  return snapshot;
}
