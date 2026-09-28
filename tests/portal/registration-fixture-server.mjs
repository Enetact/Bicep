// Explicit browser-only fixture. No ADO/GitHub calls, real tokens or persistent definitions.
// Run with the real portal on 5087; stop this test server after UI verification.
import {createServer} from 'node:http';
import {readFile} from 'node:fs/promises';
const bootstrap=await fetch('http://localhost:5087/api/bootstrap').then(r=>r.json());
const catalog=await fetch('http://localhost:5087/api/pipeline-setup/catalog').then(r=>r.json());
bootstrap.csrf='fixture-only';
const seed={id:1,name:'LOCAL TEST FIXTURE — not a real ADO definition',connectionId:'aaaaaaaa-aaaa-aaaa-aaaa-aaaaaaaaaaaa',queueId:9};
const rows=catalog.entries.map((e,i)=>({...e,status:i===0?'Existing':i===1?'Conflict':i===2?'Source not published':'Missing',existingId:i===0?1:null,detail:'Synthetic browser test; no ADO operation.'}));
let pending;
const server=createServer(async(req,res)=>{
  res.setHeader('Cache-Control','no-store');res.setHeader('Content-Security-Policy',"default-src 'self'; script-src 'self'; style-src 'self'; img-src 'self' blob:; connect-src 'self'");
  const route=req.url.split('?')[0];let result;
  const basic={'/api/bootstrap':bootstrap,'/api/auth':{state:'Connected',account:'LOCAL TEST FIXTURE — no cloud connection',connected:['ado']},
    '/api/tags/config':{profile:'Fixture',enabled:false,note:'Fixture only'},
    '/api/agent/status':{installed:false,state:'Fixture',provider:{ready:false,state:'Not connected'},workflows:[]},
    '/api/network/allocation':{configured:false,enabled:false,note:'Fixture only'},'/api/pipeline-setup/catalog':catalog};
  if(route in basic)result=basic[route];
  if(route==='/api/pipeline-setup/inventory')result={...catalog,commit:'LOCAL TEST FIXTURE',catalogHash:catalog.hash,sources:[seed],pipelines:rows};
  if(route==='/api/pipeline-setup/review'){
    let input='';for await(const part of req)input+=part;const selected=JSON.parse(input);
    pending=rows.filter(r=>r.status==='Missing'&&selected.pipelines.includes(r.yaml));
    result={ticket:'fixture-ticket',organization:bootstrap.organization,project:bootstrap.project,repository:catalog.repository,commit:'LOCAL TEST FIXTURE',seed,
      warning:'LOCAL TEST FIXTURE: only in-memory simulation; no Azure DevOps or GitHub mutation.',payloads:pending.map(r=>({name:r.name,process:{yamlFilename:r.yaml}}))};
  }
  if(route==='/api/pipeline-setup/apply/fixture-ticket'&&pending){
    result={id:'fixture',status:'Completed',runsQueued:false,path:'fixture-only',results:pending.map((r,i)=>({yaml:r.yaml,name:r.name,status:'Created',id:100+i,detail:'Synthetic receipt; no cloud call.'}))};
    for(const row of pending)row.status='Existing';pending=null;
  }
  if(result){res.setHeader('Content-Type','application/json');res.end(JSON.stringify(result));return;}
  const files={'/':'index.html','/app.js':'app.js','/styles.css':'styles.css','/agents.mjs':'agents.mjs','/topology.mjs':'topology.mjs','/network.mjs':'network.mjs','/pipeline-setup.mjs':'pipeline-setup.mjs','/tagging.mjs':'tagging.mjs'};
  if(!(route in files)){res.writeHead(404);res.end('Fixture route unavailable.');return;}
  const file=files[route];res.setHeader('Content-Type',file.endsWith('.html')?'text/html':file.endsWith('.css')?'text/css':'text/javascript');res.end(await readFile(new URL('../../src/SelfService.Portal/wwwroot/'+file,import.meta.url)));
});
server.listen(5098,'127.0.0.1',()=>console.log('LOCAL REGISTRATION FIXTURE ONLY: http://127.0.0.1:5098'));
