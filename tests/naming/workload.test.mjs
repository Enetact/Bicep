import test from 'node:test';
import assert from 'node:assert/strict';
import fs from 'node:fs';
import { spawnSync } from 'node:child_process';
import { fileURLToPath } from 'node:url';
import { workloadNames, supportedWorkloads } from '../../scripts/naming/workload.mjs';
import { digest } from '../../scripts/naming/core.mjs';
import { namingGraph } from '../../src/SelfService.Portal/wwwroot/naming.mjs';
const read = path => JSON.parse(fs.readFileSync(new URL(path, import.meta.url)));
const policy=read('../../config/naming-policy.json'), catalog=read('../../config/workloads.json'), intent=read('./request.example.json').intent;
const run=(type, edit={})=>workloadNames({workloadType:type,intent:{...intent,...edit}},policy,catalog);
for(const type of supportedWorkloads) for(const environment of policy.environments) test(`${type}/${environment} concrete map, typed profile and graph agree`,()=>{
  const draft=run(type,{environment}), map=draft.parameterPatch.parameters.resourceNames.value;
  assert.equal(map.profile,'standard-v2'); assert.equal(draft.deploymentAuthorized,false);
  assert.equal(draft.parameterPatch.parameters.workloadResourceGroupName.value,`${environment}-rg-pay-int-eus2-01`);
  for(const [key,value]of Object.entries(map)) if(!['profile','displayName'].includes(key)) assert(draft.plan.resources.some(r=>r.name===value),key);
  const graph=namingGraph(draft); assert.equal(graph.nodes.length,draft.plan.resources.length); assert(graph.nodes.some(n=>n.resourceId===draft.plan.resources.find(r=>r.componentKey==='group').resourceId));
  const {draftSha256,...body}=draft; assert.equal(draftSha256,digest(body));
  if(environment==='prod')assert.doesNotThrow(()=>run(type,{environment,stackAcronym:'abcd'}),'longest environment/acronym must fit every role without truncation');
});
test('storage child contracts survive while principal-dependent IDs remain unresolved',()=>{
  const r=run('private-storage').plan.resources; assert(r.some(x=>x.fullName?.endsWith('/default/workspace'))); assert(r.some(x=>x.fullName?.endsWith('/default/work')));
  assert.equal(r.filter(x=>x.armType==='Microsoft.Authorization/roleAssignments'&&x.name===null&&x.status==='Unresolved').length,2);
});
test('workbook GUID stays stable when display label changes',()=>{
  const a=run('observability'),b=run('observability',{stackDisplayName:'New display'}); assert.deepEqual(a.plan.resources,b.plan.resources); assert.equal(b.parameterPatch.parameters.resourceNames.value.workbook,intent.instanceId); assert.notEqual(a.draftSha256,b.draftSha256);
});
test('unsupported product, placeholder subscription and deployment flags are rejected',()=>{
  assert.throws(()=>run('unknown-product'),/support/);assert.throws(()=>run('key-vault',{subscriptionId:'00000000-0000-0000-0000-000000000000'}),/Select a subscription/);
  assert.throws(()=>workloadNames({workloadType:'key-vault',intent,deploy:true},policy,catalog),/support/);
});
test('workload CLI returns the same frozen draft with no path or command injection',()=>{
  const cli=fileURLToPath(new URL('../../scripts/naming/workload-cli.mjs',import.meta.url));
  const r=spawnSync(process.execPath,[cli],{encoding:'utf8',input:JSON.stringify({workloadType:'key-vault',intent})});assert.equal(r.status,0,r.stderr);assert.deepEqual(JSON.parse(r.stdout),run('key-vault'));
  for(const [args,input]of [[['--deploy'],'{}'],[[],' '.repeat(65537)]]){const bad=spawnSync(process.execPath,[cli,...args],{encoding:'utf8',input});assert.equal(bad.status,1);assert.equal(bad.stdout,'');}
});
