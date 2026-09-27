// Optional browser acceptance fixture. No cloud calls, auth tokens, or queue operations.
// Run while the real portal is on 5087: node tests/portal/diagram-fixture-server.mjs
import { createServer } from 'node:http';
import { readFile } from 'node:fs/promises';
const bootstrap = await fetch('http://localhost:5087/api/bootstrap').then(r=>r.json());
bootstrap.csrf='local-test-only';bootstrap.configured=true;
const subscription=bootstrap.products[0].targets[0].subscriptionId;
const vnet=`/subscriptions/${subscription}/resourceGroups/fixture-network/providers/Microsoft.Network/virtualNetworks/fixture-vnet`;
const subnet=vnet+'/subnets/fixture-apps';
const storage=`/subscriptions/${subscription}/resourceGroups/fixture-storage/providers/Microsoft.Storage/storageAccounts/fixturedata`;
const report={kind:'portal-skill-discovery',id:'fixture',subscriptionId:subscription,status:'Partial',generatedUtc:'2026-09-27T00:00:00Z',coverage:'LOCAL TEST FIXTURE: no live Azure inventory.',limitations:['Fixture includes inaccessible DNS to test partial coverage.'],collections:[
  {name:'Virtual networks',status:'Succeeded',pages:1,resources:[{id:vnet,name:'fixture-vnet',type:'Microsoft.Network/virtualNetworks',subnets:[{id:subnet,name:'fixture-apps',occupancy:'Unknown'}],peerings:[{remoteVnetId:'/subscriptions/unqueried/resourceGroups/hub/providers/Microsoft.Network/virtualNetworks/hub'}]}]},
  {name:'Resources',status:'Succeeded',pages:1,resources:[{id:storage,name:'fixturedata',type:'Microsoft.Storage/storageAccounts'}]},
  {name:'DNS',status:'Failed',pages:0,resources:[],issue:'Fixture HTTP 403; remaining coverage unknown.'}]};
const preview={kind:'portal-preview-diagram',product:'storage',environment:'dev',region:'eastus2',subscriptionId:subscription,runId:44,sourceCommit:'LOCAL-TEST-FIXTURE',createdUtc:'2026-09-27T00:00:00Z',status:'Blocked or incomplete preview',previewSucceeded:false,coverage:'LOCAL TEST FIXTURE. Saved actions only; no deployment authorization.',resources:[{id:storage,action:'Create',certainty:'definite'},{id:vnet,action:'Delete',certainty:'potential'}]};
const network={id:'fixture',mode:'registered',generatedUtc:'2026-09-27T00:00:00Z',status:'Partial',membershipCoverage:'LOCAL TEST FIXTURE: one selected subscription; not tenant-complete.',issues:['DNS read denied in fixture.'],resolvedSubscriptions:[subscription],reports:[report],assessment:{findings:[{state:'Unknown',rule:'capacity',resourceId:subnet,summary:'Configured capacity is not proof of free addresses.'}]}};
const diagram={status:'Validated',issue:'Fixture of a server-validated graph; not live model output.',source:'flowchart TB\nR001["fixture-vnet"]\nR002["fixture-apps"]\nR001 -->|configured subnet| R002',title:'Agent interpretation',subtitle:'LOCAL TEST FIXTURE',note:'Evidence-backed configuration; not effective connectivity.',coverage:['Partial fixture coverage'],nodes:[{id:'R001',resourceId:vnet,label:'fixture-vnet',type:'Virtual network',status:'Observed configuration',level:0,detail:'Fixture only'},{id:'R002',resourceId:subnet,label:'fixture-apps',type:'Subnet',status:'Observed configuration',level:1,detail:'Free capacity unknown'}],edges:[{from:'R001',to:'R002',label:'configured subnet'}]};
let reviews=0;
const server=createServer(async(req,res)=>{
  res.setHeader('Content-Security-Policy',"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self'");
  res.setHeader('Cache-Control','no-store');
  const route=req.url.split('?')[0];
  const values={'/api/bootstrap':bootstrap,'/api/auth':{state:'Connected',account:'LOCAL TEST FIXTURE — no cloud connection',connected:['ado','azure']},'/api/skill-discovery':report,'/api/preview/storage/44':preview};
  values['/api/network/allocation']={configured:false,enabled:false,note:'LOCAL TEST FIXTURE: no pool or queue.'};
  values['/api/network/discover']=network;
  values['/api/agent/status']={installed:true,state:'LOCAL TEST FIXTURE',provider:{state:'Fixture connected',ready:true},workflows:[{id:'visualize',name:'Azure resource visualizer (fixture)',scope:'resource-group',ready:true,evidenceReady:true,requires:'Synthetic fixture evidence',limits:'No cloud/model calls; second review demonstrates rejected output.'}]};
  if(route==='/api/agent/run'){
    const accepted=++reviews%2===1;
    values[route]={review:accepted?'LOCAL TEST FIXTURE: approved grammar result.':'LOCAL TEST FIXTURE: unsafe Mermaid was rejected. click R001 href https://invalid.test',receipt:{fixture:true,diagramStatus:accepted?'Validated':'Rejected'},path:'fixture-only',diagram:accepted?diagram:{status:'Rejected',issue:'Unsupported or unsafe Mermaid statement.',source:null,nodes:[],edges:[]}};
  }
  if(route in values){res.setHeader('Content-Type','application/json');res.end(JSON.stringify(values[route]));return;}
  const paths={'/':'index.html','/app.js':'app.js','/topology.mjs':'topology.mjs','/agents.mjs':'agents.mjs','/network.mjs':'network.mjs','/styles.css':'styles.css'};
  if(!(route in paths)){res.writeHead(404);res.end('Fixture route not enabled; no external calls.');return;}
  try {const path=paths[route];res.setHeader('Content-Type',path.endsWith('.html')?'text/html':path.endsWith('.css')?'text/css':'text/javascript');res.end(await readFile(new URL('../../src/SelfService.Portal/wwwroot/'+path,import.meta.url)));}
  catch{res.writeHead(500);res.end('Fixture file unavailable.');}
});
server.listen(5098,'127.0.0.1',()=>console.log('LOCAL TEST FIXTURE ONLY: http://127.0.0.1:5098. Stop with Ctrl+C.'));
