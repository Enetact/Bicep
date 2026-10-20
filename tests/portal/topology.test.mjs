import { test } from 'node:test';
import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { observedTopology, proposedTopology, previewTopology, networkTopology, graphSvg } from '../../src/SelfService.Portal/wwwroot/topology.mjs';
const root = '/subscriptions/test/resourceGroups/rg/providers/Microsoft.Network';
const vnet = `${root}/virtualNetworks/vnet`, subnet = `${vnet}/subnets/apps`, pe = `${root}/privateEndpoints/pe`;
test('multi-subscription topology reconciles references without making visibility complete',()=>{
  const remote='/subscriptions/other/resourceGroups/rg/providers/Microsoft.Network/virtualNetworks/remote';
  const g=networkTopology({status:'Partial',membershipCoverage:'Visible tenant only',issues:['denied scope'],reports:[
    {subscriptionId:'test',status:'Collected',collections:[{resources:[{id:vnet,name:'local',peerings:[{remoteVnetId:remote}]}]}]},
    {subscriptionId:'other',status:'Collected',collections:[{resources:[{id:remote,name:'remote'}]}]}
  ]});
  assert.equal(g.nodes.filter(n=>n.id===remote.toLowerCase()).length,1);assert.equal(g.nodes.find(n=>n.id===remote.toLowerCase()).status,'Observed');
  assert.match(g.note,/Visible tenant only/);assert.ok(g.coverage.includes('denied scope'));assert.ok(g.edges.some(e=>e.to===remote.toLowerCase()));
});
test('observed inventory merges duplicate ARM IDs and preserves configured references, partial coverage and unknown remote networks', () => {
  const g = observedTopology({subscriptionId:'test',status:'Partial',collections:[
    {name:'Resources',status:'Succeeded',resources:[{id:vnet.toUpperCase(),name:'vnet'}]},
    {name:'Virtual networks',status:'Succeeded',resources:[{id:vnet,name:'vnet',addressPrefixes:['10.1.0.0/16'],subnets:[{id:subnet,name:'apps',occupancy:'Unknown',nsgId:`${root}/networkSecurityGroups/nsg`}],peerings:[{remoteVnetId:'/subscriptions/remote/resourceGroups/rg/providers/Microsoft.Network/virtualNetworks/hub'}]}]},
    {name:'Private endpoints',status:'Succeeded',resources:[{id:pe,name:'pe',subnetId:subnet,connections:[{configured:{privateLinkServiceId:'/subscriptions/test/resourceGroups/rg/providers/Microsoft.Storage/storageAccounts/data'},state:'Approved'}]}]},
    {name:'DNS',status:'Failed',issue:'HTTP 403',resources:[]} ]});
  assert.equal(g.nodes.filter(n=>n.id===vnet.toLowerCase()).length,1);
  assert.match(g.nodes.find(n=>n.id===vnet.toLowerCase()).detail,/10.1/);
  assert.equal(g.nodes.find(n=>n.id===subnet.toLowerCase()).status,'Observed');
  assert.equal(g.nodes.find(n=>n.id.startsWith('/subscriptions/remote')).status,'Referenced only');
  assert.ok(g.edges.some(e=>e.label==='configured NSG'));
  assert.match(g.coverage.at(-1),/Failed.*403/); assert.match(g.note,/unknown, never empty/);
});
test('empty discovery has only scope; failed inventory does not imply creation',()=>{
  const g=observedTopology({subscriptionId:'test',status:'Partial',collections:[{name:'Resources',status:'Failed',resources:[]}]});
  assert.equal(g.nodes.length,1); assert.equal(g.nodes[0].status,'Scope'); assert.ok(!g.nodes.some(n=>n.status==='Create'));
});
const definitions=JSON.parse(readFileSync(new URL('../../config/portal-topologies.json',import.meta.url)));
const workloads=JSON.parse(readFileSync(new URL('../../config/workloads.json',import.meta.url))).workloads;
test('every registered product has a reviewed topology tied to actual source symbols',()=>{
  assert.deepEqual(Object.keys(definitions.products).sort(),Object.values(workloads).map(w=>w.menuSlug).sort());
  for(const [id,d] of Object.entries(definitions.products)){
    const type=Object.values(workloads).find(w=>w.menuSlug===id); assert.equal(d.source,type.composition);
    const ids=new Set(d.nodes.map(n=>n[0])); assert.equal(ids.size,d.nodes.length);
    for(const n of d.nodes){
      const reference=n[4]||d.source;
      assert.match(reference,/^(?:[A-Za-z0-9_.-]+\/)*[A-Za-z0-9_.-]+\.(?:bicep|ps1)$/);
      assert(reference.split('/').every(segment=>segment!=='.'&&segment!=='..'),'Topology sources must stay inside the repository');
      const source=readFileSync(new URL('../../'+reference,import.meta.url),'utf8');
      const symbol=n[3].replace(/[.*+?^${}()|[\]\\]/g,'\\$&');
      const declaration=reference.endsWith('.ps1')?`function ${symbol}\\(`:`(?:module|resource${n[4]?'|param':''}) ${symbol} `;
      assert.match(source,new RegExp(declaration));
    }
    for(const [from,to] of d.edges){assert.ok(ids.has(from));assert.ok(ids.has(to));}
    const g=proposedTopology({id,name:type.displayName},{environment:'dev',subscriptionId:'test',network:'central-private'},'eastus2',d);
    assert.equal(g.nodes.length,d.nodes.length+1);assert.ok(g.nodes.every(n=>n.status==='Proposed'));
    assert.match(g.note,/Names, counts, conditions/);
  }
});

test('Fabric dependencies retain their own source and do not become created or verified resources',()=>{
  const definition=definitions.products.fabric;
  const graph=proposedTopology({id:'fabric',name:workloads.fabric.displayName},{environment:'dev',subscriptionId:'test',network:'central-private'},'eastus2',definition);
  const pool=graph.nodes.find(n=>n.id==='pool'),agents=graph.nodes.find(n=>n.id==='agents'),dns=graph.nodes.find(n=>n.id==='dns'),delivery=graph.nodes.find(n=>n.id==='delivery');
  assert.equal(pool.type,'Required dependency');assert.match(pool.detail,/Provisioned separately.*modules\/devops\/managed-devops-pool\/main.bicep/);
  assert.equal(agents.type,'Required dependency');assert.match(agents.detail,/never used for Fabric private endpoints/);
  assert.match(dns.detail,/Same-VNet direct Azure DNS and private IPv4/);
  assert.match(delivery.detail,/never inferred from this diagram.*scripts\/fabric-execution-common.ps1/);
  assert(graph.edges.some(e=>e.from==='agents'&&e.to==='private'&&e.label.includes('separate')));
  assert(graph.edges.some(e=>e.from==='delivery'&&e.to==='private'&&e.label.includes('deny-public promotion is blocked')));
  assert(graph.nodes.every(n=>n.status==='Proposed'));assert.match(graph.note,/No resources have been created/);
});
test('Preview preserves destructive, unchanged and uncertain evidence without authorizing deployment',()=>{
  const g=previewTopology({kind:'portal-preview-diagram',subscriptionId:'test',resources:[{id:vnet,action:'Delete',certainty:'potential'},{id:pe,action:'NoChange'}],status:'Blocked',previewSucceeded:false});
  assert.equal(g.nodes.find(n=>n.id===vnet.toLowerCase()).status,'Delete · potential');
  assert.match(g.nodes.find(n=>n.id===vnet.toLowerCase()).detail,/potential/);
  assert.match(g.coverage[1],/Blocked or incomplete/);
});
test('SVG encodes all untrusted labels and identifiers; no external code or image loading',()=>{
  const svg=graphSvg([{id:'<bad>',label:'<script>alert(1)</script>',type:'" onload="evil',detail:'<img src=x>',status:'Proposed',level:1}],[],'<svg onload="evil">');
  assert.ok(!svg.includes('<script>'));assert.ok(!svg.includes('<img'));
  assert.ok(svg.includes('&lt;script&gt;'));assert.ok(svg.includes('&quot;'));
});
test('large discovered collections are retained in the model for paginated rendering',()=>{
  const g=observedTopology({subscriptionId:'test',collections:[{resources:Array.from({length:5001},(_,i)=>({id:`${root}/virtualNetworks/v${i}`,name:`v${i}`}))}]});
  assert.equal(g.nodes.length,5003);
});
