import {test} from 'node:test';
import assert from 'node:assert/strict';
import {projection,project,isProjectedPath} from '../../scripts/iac/projection.mjs';
import {planned,compare} from '../../scripts/iac/core.mjs';
import {qualify,inspectTemplate} from '../../scripts/iac/qualification.mjs';
import {probeChildren} from '../../scripts/iac/probes.mjs';
import {armRead,readProjected} from '../../scripts/iac/azure.mjs';
const scope={subscriptionId:'11111111-1111-4111-8111-111111111111',resourceGroup:'rg-test'};
const id=`/subscriptions/${scope.subscriptionId}/resourceGroups/rg-test/providers/Microsoft.Network/virtualNetworks/net`;
const type='Microsoft.Network/virtualNetworks';
const vnet=subnets=>({type,properties:{subnets}});
const subnet=(name,prefix)=>({name,properties:{addressPrefix:prefix,secret:'DO NOT RETAIN'}});
const whatif=(after,extra={})=>({properties:{provisioningState:'Succeeded',changes:{resourceChanges:[{id,changeType:'Modify',changeCertainty:'definite',resourceConfigurationChanges:{after}}]},...extra}});
const report=(config={})=>({planned:planned(whatif({location:'eastus2'}),scope),before:{inventoryStatus:'Listed',resources:[{id,type,status:'Observed',config}],probes:[]}});
test('nested arrays retain only reviewed leaves, counts and explicit missing-property coverage',()=>{
 const p=projection(vnet([subnet('a','10.0.0.0/24')]));assert.equal(p.config['properties.subnets.0.properties.addressPrefix'],'10.0.0.0/24');assert.equal(p.config['properties.subnets.$count'],1);assert.ok(!JSON.stringify(p).includes('DO NOT RETAIN'));assert.ok(p.propertyCoverage.some(p=>p.path==='properties.subnets.*.id'&&p.status==='NotReturned'));
});
test('named arrays are stable under reorder; changed nested values differ',()=>{
 const a=project(vnet([subnet('b','10.1.0.0/24'),subnet('a','10.0.0.0/24')]));const b=project(vnet([subnet('a','10.0.0.0/24'),subnet('b','10.1.0.0/24')]));assert.deepEqual(a,b);assert.notDeepEqual(a,project(vnet([subnet('a','10.0.0.0/25'),subnet('b','10.1.0.0/24')])));
});
test('empty arrays differ from missing arrays and limits are explicit',()=>{
 assert.equal(project(vnet([]))['properties.subnets.$count'],0);assert.ok(!Object.hasOwn(project(vnet(undefined)),'properties.subnets.$count'));assert.ok(projection(vnet(Array.from({length:257},(_,i)=>subnet('s'+i,'x')))).propertyCoverage.some(p=>p.status==='LimitExceeded'));
});
test('unknown paths and nested credentials are not allowable',()=>{
 assert.equal(isProjectedPath('properties.subnets.0.properties.addressPrefix'),true);assert.equal(isProjectedPath('properties.subnets.0.properties.password'),false);assert.equal(isProjectedPath('properties.subnets.0.properties.tags.secret'),false);
});
test('nested unresolved expressions remain unknown even with observed values',()=>{
 const p=planned(whatif(vnet([subnet('a',"[reference('subnet').addressPrefix]")])),scope);assert.equal(p.resources[0].status,'Unresolved');const r=report();r.planned=p;assert.ok(qualify(r).blockers.some(b=>b.code==='DesiredStateUnresolved'));assert.equal(compare(p,{resources:[{id,status:'Observed',config:project(vnet([subnet('a','10.0.0.0/24')]))}]})[0].status,'Unknown');
});
test('unprojected expression and diagnostics cannot hide behind a successful result',()=>{
 assert.equal(planned(whatif({properties:{unknown:"[reference('x')]"}}),scope).resources[0].status,'Unresolved');const p=planned(whatif({}, {diagnostics:[{message:'sensitive expression'}]}),scope);assert.equal(p.status,'Incomplete');assert.equal(p.diagnosticCount,1);assert.ok(!JSON.stringify(p).includes('sensitive expression'));
});
test('linked nested templates are inspected and block qualification',()=>{
 const r=report();r.templateInspection=inspectTemplate({resources:[{type:'Microsoft.Resources/deployments',properties:{templateLink:{id:'not copied'}}}]});assert.ok(qualify(r).blockers.some(b=>b.code==='LinkedTemplatesNotExpanded'));
});
test('child probe paginates fixed subnets route, strips secrets and records API provenance',async()=>{
 const s={resources:[{id,type,status:'Observed',config:{}}]};let n=0;
 await probeChildren(s,scope,{},'test',async(_,route,version,next)=>{assert.equal(route,id+'/subnets');assert.equal(version,'2025-09-01');n++;return {status:'Observed',value:{value:[{id:id+'/subnets/s'+n,...subnet('s'+n,'10.0.0.0/24')}],nextLink:next?null:`https://management.azure.com${route}?api-version=${version}&$skiptoken=x`}};});
 assert.equal(n,2);assert.equal(s.probes[0].status,'Succeeded');assert.equal(s.probes[0].pages,2);assert.equal(s.resources.length,3);assert.ok(!JSON.stringify(s).includes('DO NOT RETAIN'));
});
for(const next of ['https://evil.test/resource?api-version=2025-09-01',`https://management.azure.com${id}/other?api-version=2025-09-01`])test('unsafe child continuation never sends credentials to '+new URL(next).host,async()=>{
 const s={resources:[{id,type,status:'Observed',config:{}}]};let calls=0;await probeChildren(s,scope,{},'test',async()=>{calls++;return {status:'Observed',value:{value:[],nextLink:next}};});assert.equal(calls,1);assert.equal(s.probes[0].status,'Unknown');
});
test('denied and missing child endpoints are not empty inventories',async()=>{
 for(const status of ['Denied','Absent']){const s={resources:[{id,type,status:'Observed',config:{}}]};await probeChildren(s,scope,{},'test',async()=>({status}));assert.equal(s.probes[0].status,status==='Absent'?'NotAvailable':'Denied');assert.ok(qualify({...report(),before:{...s,inventoryStatus:'Listed'}}).blockers.some(b=>b.code==='ChildProbeIncomplete'));}
});
test('unprofiled resources remain NotAssessed without guessed API calls',async()=>{
 let called=false;const r=await readProjected(id.replace('Microsoft.Network/virtualNetworks','Microsoft.Example/things'),scope,{},'test',async()=>{called=true;});assert.equal(called,false);assert.equal(r.status,'NotAssessed');
});
test('ARM transport rejects a foreign continuation before fetch',async()=>assert.rejects(()=>armRead('not-real',id,'2025-09-01','https://evil.test/')));
test('new nested before-state affects approval fingerprint while timestamps do not',()=>{
 const r=report({'properties.subnets.0.properties.addressPrefix':'10.0.0.0/24'});const a=qualify(r).beforeFingerprint;r.before.completedUtc='later';assert.equal(qualify(r).beforeFingerprint,a);r.before.resources[0].config['properties.subnets.0.properties.addressPrefix']='10.0.1.0/24';assert.notEqual(qualify(r).beforeFingerprint,a);
});
test('nested after mismatch and missing projected values withhold qualification',()=>{
 const r=report();r.planned=planned(whatif(vnet([subnet('a','10.0.0.0/24')])),scope);r.after={inventoryStatus:'Listed',resources:[{id,status:'Observed',config:project(vnet([subnet('a','10.1.0.0/24')]))}]};r.comparison=compare(r.planned,r.after);assert.equal(r.comparison[0].status,'Mismatch');assert.ok(qualify(r).blockers.some(b=>b.code==='AfterStateUnverified'));
});
