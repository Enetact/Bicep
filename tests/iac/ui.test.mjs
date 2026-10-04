import {test} from 'node:test';
import assert from 'node:assert/strict';
import {renderIac} from '../../src/SelfService.Portal/wwwroot/iac.mjs';
class Element {
 constructor(tag){this.tag=tag;this.children=[];this.dataset={};this.value='';this.textContent='';this.listeners={};}
 append(...children){this.children.push(...children)}
 replaceChildren(...children){this.children=children}
 setAttribute(){}
 addEventListener(name,fn){this.listeners[name]=fn}
 set innerHTML(_){throw Error('Untrusted markup must not be used')}
}
const walk=e=>[e,...e.children.flatMap(walk)];
test('actual IaC renderer presents nested blockers, child coverage and unknown values as text',()=>{
 globalThis.document={createElement:tag=>new Element(tag)};
 const id='/subscriptions/fixture/resourceGroups/test/providers/Microsoft.Network/virtualNetworks/net';
 const state={status:'Succeeded',resources:[{id,type:'Microsoft.Network/virtualNetworks',action:'Modify',status:'Unresolved',certainty:'definite',config:{'properties.subnets.$count':0},propertyCoverage:[{path:'properties.subnets.*.name',status:'UnresolvedExpression'}]}],coverage:'Unknown nested values'};
 const report={kind:'portal-iac-analysis',collector:{version:'2.0.5'},provenance:{runId:'1',sourceCommit:'fixture'},qualification:{status:'Blocked',profile:'nested-arm/v1',blockers:[{code:'DesiredStateUnresolved',resourceId:id}]},probes:[{phase:'before',id:id+'/subnets',apiVersion:'2025-09-01',status:'Denied',observedUtc:'fixture'}],planned:state,before:null,after:null,comparison:[],limitations:['<script>untrusted</script>'],receipt:{},updatedUtc:'fixture'};
 const container=new Element('div');renderIac(container,report);const text=walk(container).map(e=>e.textContent).join('\n');
 assert.match(text,/Nested evidence: Blocked/);assert.match(text,/DesiredStateUnresolved/);assert.match(text,/Denied/);assert.match(text,/UnresolvedExpression/);assert.match(text,/Unknown \/ omitted/);
 assert.ok(walk(container).filter(e=>e.tag==='button').some(e=>e.textContent==='Review this phase with Codex'));
 const after=walk(container).find(e=>e.tag==='button'&&e.dataset.phase==='after');after.listeners.click();assert.ok(walk(container).some(e=>/after evidence is unavailable/.test(e.textContent)));
 delete globalThis.document;
});
