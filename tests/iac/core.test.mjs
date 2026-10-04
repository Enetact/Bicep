import { test } from 'node:test';
import assert from 'node:assert/strict';
import { mkdir,writeFile } from 'node:fs/promises';
import { project, planned, compare, versions, bicep, assertId, json } from '../../scripts/iac/core.mjs';
import { collect, completeBefore } from '../../scripts/iac/azure.mjs';
import { iacTopology } from '../../src/SelfService.Portal/wwwroot/iac.mjs';
const target={subscriptionId:'11111111-1111-4111-8111-111111111111',resourceGroup:'rg-test'};
const scope=`/subscriptions/${target.subscriptionId}/resourceGroups/${target.resourceGroup}`;
const id=scope+'/providers/Microsoft.Storage/storageAccounts/data';
const apiVersions={'microsoft.storage/storageaccounts':'2023-05-01','microsoft.resources/resourcegroups':'2022-09-01'};
const input={target,tenantId:'22222222-2222-4222-8222-222222222222',token:'test',apiVersions};
const raw=(action='Create',config={properties:{publicNetworkAccess:'Disabled'}})=>({properties:{provisioningState:'Succeeded',changes:{resourceChanges:[{id,changeType:action,changeCertainty:'definite',resourceConfigurationChanges:{after:config}}]}}});
const observed=(config={})=>({resources:[{id,type:'Microsoft.Storage/storageAccounts',apiVersion:apiVersions['microsoft.storage/storageaccounts'],status:'Observed',config}]});
function transports({missing=false,denied=false,mcpError=false,resourceError=false,foreign=false}={}){
  const calls=[];
  return {calls,mcp:{async callTool(request){calls.push(request);if(mcpError)return {isError:true,content:[{type:'text',text:'PRIVATE ERROR'}]};const rows=request.name==='group_list'?{groups:missing?[]:[{name:'rg-test'}]}:{resources:[{id:foreign?id.replace('rg-test','rg-other'):id}]};return {content:[{type:'text',text:JSON.stringify({status:200,results:rows})}]};}},
    read:async(_,resource)=>{
      if(resource===scope)return {status:denied?'Denied':missing?'Absent':'Observed',value:{location:'eastus2'}};
      if(resource===id&&resourceError)throw Error('PRIVATE ERROR');
      return {status:'Observed',value:{id:resource,location:'eastus2',properties:{publicNetworkAccess:'Disabled',password:'SECRET'}}};
    }};
}
test('projection drops credentials, tags, expressions and arbitrary property bodies',()=>{
  const p=project({location:'eastus2',tags:{password:'SECRET'},properties:{publicNetworkAccess:'Disabled',connectionString:'SECRET',minimumTlsVersion:'[parameters(secret)]'},identity:{type:'SystemAssigned',principalId:'not requested'}});
  assert.deepEqual(p,{location:'eastus2','identity.type':'SystemAssigned','properties.publicNetworkAccess':'Disabled'});assert.ok(!JSON.stringify(p).includes('SECRET'));
});
for(const bad of [id.replace('rg-test','rg-other'),id+'?secret',id+'#fragment',id+'/../else',id+'%2fother'])test('scope rejects '+bad,()=>assert.throws(()=>assertId(bad,target)));
test('real adapter contract uses only scoped read tools and direct reads',async()=>{
  const t=transports(),r=await collect(input,t);assert.equal(r.inventoryStatus,'Listed');assert.equal(r.resources.length,2);assert.deepEqual(t.calls.map(c=>c.name),['group_list','group_resource_list']);
  assert.ok(t.calls.every(c=>c.arguments.subscription===target.subscriptionId&&c.arguments.tenant===input.tenantId));assert.ok(!JSON.stringify(r).includes('SECRET'));
});
test('confirmed absent group is not confused with permission denial',async()=>{
  const missing=await collect(input,transports({missing:true}));assert.equal(missing.inventoryStatus,'GroupAbsent');assert.equal(missing.resources.length,0);
  const denied=await collect(input,transports({denied:true}));assert.equal(denied.inventoryStatus,'Partial');assert.ok(denied.issues.length);
});
test('MCP failure is retained as failed tool audit, never empty success',async()=>{
  const r=await collect(input,transports({mcpError:true}));assert.equal(r.inventoryStatus,'Partial');assert.equal(r.audit[0].status,'Failed');assert.ok(!JSON.stringify(r).includes('PRIVATE ERROR'));
});
test('foreign resource stops scope expansion',async()=>assert.equal((await collect(input,transports({foreign:true}))).inventoryStatus,'Partial'));
test('individual failed read retains identity and unknown state',async()=>{
  const r=await collect(input,transports({resourceError:true}));assert.equal(r.resources.find(r=>r.id===id).status,'Unknown');assert.ok(!JSON.stringify(r).includes('PRIVATE ERROR'));
});
test('after explicitly reads a child missing from generic inventory',async()=>{
  const child=id+'/blobServices/default',t=transports();const r=await collect({...input,apiVersions:{...apiVersions,'microsoft.storage/storageaccounts/blobservices':'2023-05-01'},expected:[{id:child}]},t);
  assert.equal(r.resources.find(r=>r.id===child).status,'Observed');
});
test('unsupported API version is visible, not silently interpreted as absent',async()=>{
  const r=await collect({...input,apiVersions:{}},transports());assert.equal(r.resources.find(r=>r.id===id).status,'NotAssessed');
});
test('plan rejects missing, duplicate or foreign resources and preserves uncertainty',()=>{
  assert.throws(()=>planned({},target));const p=raw();p.properties.changes.resourceChanges.push(p.properties.changes.resourceChanges[0]);assert.throws(()=>planned(p,target));
  const foreign=raw();foreign.properties.changes.resourceChanges[0].id=id.replace(target.subscriptionId,'other-subscription');assert.throws(()=>planned(foreign,target));
  assert.equal(planned(raw('Ignore'),target).resources[0].status,'Unresolved');
});
test('comparison separates mismatch, partial match, existence, absence and unknown',()=>{
  const p=planned(raw(),target);assert.equal(compare(p,observed({'properties.publicNetworkAccess':'Disabled'}))[0].status,'Projected properties match');
  assert.equal(compare(p,observed({'properties.publicNetworkAccess':'Enabled'}))[0].status,'Mismatch');
  assert.equal(compare(p,observed())[0].status,'Observed existence only');
  const multi=planned(raw('Modify',{location:'eastus2',properties:{publicNetworkAccess:'Disabled'}}),target);
  assert.equal(compare(multi,observed({location:'eastus2'}))[0].status,'Partial projected match');
  assert.equal(compare(planned(raw('Delete'),target),{resources:[{id,status:'Absent'}]})[0].status,'Observed absent');
  assert.equal(compare(p,{resources:[{id,status:'Denied'}]})[0].status,'Unknown');
  p.status='Incomplete';assert.equal(compare(p,observed({'properties.publicNetworkAccess':'Disabled'}))[0].status,'Unknown');
});
test('API versions come from nested compiled deployments; expressions are excluded',()=>assert.deepEqual(versions({resources:[{type:'Microsoft.Storage/storageAccounts',apiVersion:'2023-05-01'},{type:'[x]',apiVersion:'latest'}]}),apiVersions));
test('Bicep snapshot is existing-only and cannot assert a restore',async()=>{
  const source=bicep(observed(),target);assert.match(source,/existing =/);assert.match(source,/NOT a restore template/);assert.doesNotMatch(source,/properties:|SECRET/);
  await mkdir(new URL('../../artifacts/iac/',import.meta.url),{recursive:true});await writeFile(new URL('../../artifacts/iac/before-test.bicep',import.meta.url),source);
});
test('diagrams distinguish prediction and observation, with no invented connectivity',()=>{
  const report={kind:'portal-iac-analysis',provenance:{runId:'1'},updatedUtc:'now',limitations:['unknown data plane'],before:observed(),planned:planned(raw(),target),after:null};
  assert.equal(iacTopology(report,'planned').nodes[0].status,'Create');assert.equal(iacTopology(report,'before').nodes[0].status,'Observed');
  assert.equal(iacTopology(report,'planned').edges.length,0);assert.throws(()=>iacTopology(report,'after'));
});

test('before completion captures explicit planned shared-group IDs without scanning another group',async()=>{
  const snapshot=await collect(input,transports());const shared=id.replace('rg-test','rg-shared');const p=raw();p.properties.changes.resourceChanges[0].id=shared;
  await completeBefore(snapshot,planned(p,target),target,apiVersions,'test',transports().read);
  assert.equal(snapshot.resources.find(r=>r.id===shared).status,'Observed');assert.match(bicep(snapshot,target),/rg-shared/);
});
test('extension resource types resolve using the last provider segment',()=>{
  const extension=id+'/providers/Microsoft.Authorization/roleAssignments/assignment';const p=raw();p.properties.changes.resourceChanges[0].id=extension;
  assert.equal(planned(p,target).resources[0].type,'Microsoft.Authorization/roleAssignments');
});
