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
const server=createServer(async(req,res)=>{
  res.setHeader('Content-Security-Policy',"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self'");
  res.setHeader('Cache-Control','no-store');
  const route=req.url.split('?')[0];
  const values={'/api/bootstrap':bootstrap,'/api/auth':{state:'Connected',account:'LOCAL TEST FIXTURE — no cloud connection',connected:['ado','azure']},'/api/skill-discovery':report,'/api/preview/storage/44':preview};
  if(route in values){res.setHeader('Content-Type','application/json');res.end(JSON.stringify(values[route]));return;}
  const paths={'/':'index.html','/app.js':'app.js','/topology.mjs':'topology.mjs','/styles.css':'styles.css'};
  if(!(route in paths)){res.writeHead(404);res.end('Fixture route not enabled; no external calls.');return;}
  try {const path=paths[route];res.setHeader('Content-Type',path.endsWith('.html')?'text/html':path.endsWith('.css')?'text/css':'text/javascript');res.end(await readFile(new URL('../../src/SelfService.Portal/wwwroot/'+path,import.meta.url)));}
  catch{res.writeHead(500);res.end('Fixture file unavailable.');}
});
server.listen(5098,'127.0.0.1',()=>console.log('LOCAL TEST FIXTURE ONLY: http://127.0.0.1:5098. Stop with Ctrl+C.'));
