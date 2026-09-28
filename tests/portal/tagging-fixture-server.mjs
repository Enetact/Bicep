// Explicit UI fixture; serves production assets with synthetic API responses.
// Never used by Start-Portal or Publish-Portal. No Azure/ADO/model calls or writes.
import {createServer} from 'node:http';
import {readFile} from 'node:fs/promises';
const bootstrap=await fetch('http://localhost:5087/api/bootstrap').then(r=>r.json());bootstrap.csrf='fixture-only';
const status=await fetch('http://localhost:5087/api/agent/status').then(r=>r.json());
const catalog=await fetch('http://localhost:5087/api/pipeline-setup/catalog').then(r=>r.json());
const sub='11111111-1111-1111-1111-111111111111';bootstrap.discoveryScopes=[{id:sub,name:'SYNTHETIC subscription'}];
const resources=Array.from({length:27},(_,i)=>({id:`/subscriptions/${sub}/resourcegroups/test/providers/microsoft.storage/storageaccounts/fixture${i}`,name:`SYNTHETIC storage ${i}`,type:'microsoft.storage/storageaccounts',resourceGroup:'UI fixture only',location:'eastus',tags:i===0?{}:{'custom.team':i===1?'<script>test only</script>':'Example','environment':'dev'},ownership:'Unknown',tagState:i===0?'Untagged':'Observed',supported:true}));
const report={inventory:{id:'fixture-evidence',digest:'fixture-digest',subscriptionId:sub,observedUtc:new Date().toISOString(),source:'SYNTHETIC UI FIXTURE — no cloud collection',resources,coverage:[{collector:'Fixture',state:'Partial',detail:'Synthetic test only; no real discovery or authorization.'}]},findings:[{resourceId:resources[0].id,key:'owner',state:'NeedsInput',summary:'Provide a reviewed value'}]};
createServer(async(req,res)=>{
 res.setHeader('Cache-Control','no-store');res.setHeader('Content-Security-Policy',"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self'");const route=req.url.split('?')[0];
 const basic={'/api/bootstrap':bootstrap,'/api/auth':{state:'Connected',account:'SYNTHETIC UI FIXTURE',connected:['azure']},'/api/agent/status':status,'/api/network/allocation':{enabled:false,note:'Test fixture only'},'/api/pipeline-setup/catalog':catalog,'/api/tags/config':{profile:'SYNTHETIC UI FIXTURE',enabled:false,note:'No real cloud identity, pipeline or resources. UI testing only.'},'/api/tags/discover':report};
 let value=basic[route];if(route==='/api/tags/drafts'){let text='';for await(const b of req)text+=b;const request=JSON.parse(text);value={plan:{changes:request.edits,blockers:['Fixture ownership unknown; no writes.']},advisoryOnly:true};}
 if(value){res.setHeader('Content-Type','application/json');res.end(JSON.stringify(value));return;}
 const allowed=['app.js','styles.css','agents.mjs','topology.mjs','network.mjs','pipeline-setup.mjs','tagging.mjs'];const file=route==='/'?'index.html':route.slice(1);if(file!=='index.html'&&!allowed.includes(file)){res.writeHead(404);res.end('No fixture route');return;}
 res.setHeader('Content-Type',file.endsWith('.html')?'text/html':file.endsWith('.css')?'text/css':'text/javascript');res.end(await readFile(new URL('../../src/SelfService.Portal/wwwroot/'+file,import.meta.url)));
}).listen(5099,'127.0.0.1',()=>console.log('SYNTHETIC TAG UI ONLY: http://127.0.0.1:5099'));
