import { renderTopology } from './topology.mjs';

export function operationsGraph(evidence) {
  const resources = evidence.resources.filter(r => !r.properties.recordType);
  const nodes = resources.map(r => ({id:r.id,label:r.name,type:r.type,status:'Observed configuration',level:r.parentId?2:1,detail:r.id}));
  const ids = new Set(nodes.map(n=>n.id)), edges=[];
  for(const r of resources) {
    if(ids.has(r.parentId)) edges.push({from:r.parentId,to:r.id,label:'contains'});
    for(const target of [r.properties.virtualNetworkId,...JSON.parse(r.properties.zoneIds || '[]')])
      if(target && ids.has(target.toLowerCase())) edges.push({from:r.id,to:target.toLowerCase(),label:'configured reference'});
  }
  return {title:'Observed DNS configuration',subtitle:evidence.completedUtc,nodes,edges,
    note:'Configured relationships within selected visible scopes. Records are shown in the matrix. Consumer completeness, DNS resolution and private reachability remain unknown.',
    coverage:evidence.coverage.map(c=>`${c.kind}: ${c.status} (${c.observed})${c.issue?' · '+c.issue:''}`)};
}

export function setupOperations({api,data,notice,page}) {
  const $=id=>document.getElementById(id), root=$('operations-workspace');
  root.innerHTML=`<div class="eyebrow">OBSERVE · COMPARE · REVIEW</div><h1>DNS and drift analysis.</h1>
    <p class="lead">Collect Private DNS evidence, inspect deterministic findings and prepare a reviewable proposal.</p>
    <p class="callout">Analysis-only milestone O0–O1. No Azure writes, consolidation, accepted deployment baseline or remediation executor. Identity, connectivity diagnostics, pipeline analysis, promotion and AKS maintenance remain planned.</p>
    <div class="panel"><button id="ops-scopes" class="secondary">Refresh subscriptions from Azure</button><p id="ops-scope-status" role="status">Connect Azure, then refresh. No subscription is hardcoded or preselected.</p><div class="fields"><label>Visible subscriptions (up to eight)<select id="ops-subscriptions" multiple></select></label><label>Analysis<select id="ops-workflow"><option value="private-dns">Private DNS analysis</option><option value="drift">DNS drift and desired changes</option></select></label></div>
    <p>Uses Azure browser authentication. Collects DNS configuration and selected network dependencies; excludes TXT values and secrets. ARM read access does not prove complete tenant visibility.</p>
    <button id="ops-collect" class="primary">Collect new DNS observation</button><button id="ops-cancel" class="secondary" hidden>Cancel collection</button><button id="ops-refresh" class="secondary">Refresh saved observations</button><p id="ops-status" role="status"></p>
    <div class="fields"><label>Observed snapshot<select id="ops-observed"></select></label><label>Earlier local reference (optional)<select id="ops-reference"></select></label></div>
    <p class="muted">Local references are not verified deployment baselines. Missing resources stay Unknown. The first drift profile covers record values/TTL and link registration; desired edits support TTL and registration only.</p>
    <div class="fields"><label>Desired resource<select id="ops-edit-resource"></select></label><label>Property<select id="ops-edit-property"><option value="ttl">TTL</option><option value="registrationEnabled">Auto-registration</option></select></label><label>Desired value<input id="ops-edit-value" placeholder="3600 or true/false"></label></div><button id="ops-edit-add" class="secondary">Add desired edit</button><button id="ops-edit-clear" class="quiet">Clear desired edits</button><pre id="ops-edits" class="document"></pre>
    <button id="ops-assess" class="primary">Assess selected observation</button><button id="ops-export" class="secondary" disabled>Export evidence and assessment</button><button id="ops-agent" class="secondary" disabled>Review assessment with Codex →</button></div>
    <div class="panel" id="ops-result" hidden><h2>Observed resources and records</h2><p>Select exact targets for a proposal. Nothing is preselected.</p><div id="ops-resources"></div><h2>Deterministic findings</h2><div id="ops-findings"></div><h2>Reference / observed / desired</h2><p id="ops-authority"></p><div id="ops-comparison"></div><div id="ops-graph" class="topology"></div><details><summary>Collection coverage</summary><pre id="ops-coverage" class="document"></pre></details></div>
    <div class="panel"><h2>Editable proposal</h2><div class="fields"><label>Proposal type<select id="ops-operation"><option>Investigate</option><option>SourceAmendment</option><option>MigrationReview</option></select></label><label>Observed destination zone (migration only)<select id="ops-destination"></select></label><label>Source/platform owner<input id="ops-owner" maxlength="200"></label></div><label>Rationale and required review<textarea id="ops-rationale" maxlength="4000"></textarea></label><p class="muted">Editing creates a new proposal and invalidates the displayed preview. This preview describes a proposal; it is not ARM What-If or an executable DNS migration.</p><button id="ops-propose" class="primary">Save proposal and preview</button><button id="ops-proposal-export" class="secondary" disabled>Export proposal and preview</button><pre id="ops-preview" class="document"></pre></div>`;
  let evidence,assessment,proposal,preview,edits=[],controller,busy=false;
  const element=(tag,text)=>{const e=document.createElement(tag);e.textContent=text;return e;};
  const options=(id,items)=>$(id).replaceChildren(...items.map(([value,label])=>{const e=element('option',label);e.value=value;return e;}));
  options('ops-subscriptions',[]);
  const invalidate=()=>{assessment=null;proposal=null;preview=null;$('ops-agent').disabled=true;$('ops-export').disabled=true;$('ops-proposal-export').disabled=true;$('ops-preview').textContent='';};
  function table(id,head,rows){const t=document.createElement('table'),tr=document.createElement('tr');head.forEach(h=>tr.append(element('th',h)));t.append(tr);for(const row of rows){const tr=document.createElement('tr');for(const value of row){const td=document.createElement('td');if(value instanceof Node)td.append(value);else td.textContent=value??'Unknown';tr.append(td);}t.append(tr);}$(id).replaceChildren(t);}
  async function history(){const rows=await api('operations/evidence');const items=rows.map(e=>[e.id,`${e.completedUtc} · ${e.resources} resources · ${e.status}`]);options('ops-observed',items);options('ops-reference',[['','No accepted baseline; observed versus desired only'],...items]);}
  async function load(){invalidate();if(!$('ops-observed').value)return;evidence=await api(`operations/evidence/${$('ops-observed').value}`);options('ops-edit-resource',evidence.resources.filter(r=>r.properties.ttl!==undefined||r.properties.registrationEnabled!==undefined).map(r=>[r.id,`${r.name} · ${r.id}`]));options('ops-destination',[['','Choose a destination for migration'],...evidence.resources.filter(r=>r.type==='microsoft.network/privatednszones').map(r=>[r.id,`${r.name} · ${r.id}`])]);edits=[];$('ops-edits').textContent='';}
  function draw(){
    $('ops-result').hidden=false;
    table('ops-resources',['Select','Resource','Type / ownership','Observed properties'],evidence.resources.map(r=>{const input=document.createElement('input');input.type='checkbox';input.value=r.id;input.setAttribute('aria-label',`Select ${r.id}`);input.addEventListener('change',clearPreview);return[input,r.id,`${r.type} · ${r.properties.ownership||'Not established'}`,JSON.stringify(r.properties)];}));
    table('ops-findings',['Rule','State','Resource','Finding'],assessment.findings.map(f=>[f.rule,f.state,f.resourceId,f.message]));
    table('ops-comparison',['Resource / property','Classification','Reference','Observed','Desired'],assessment.comparisons.map(r=>[`${r.resourceId} · ${r.property}`,r.state,r.baseline,r.observed,r.desired]));
    $('ops-authority').textContent=assessment.baselineAuthority;$('ops-coverage').textContent=JSON.stringify(evidence.coverage,null,2);
    const graph=operationsGraph(evidence);if(graph.nodes.length<=200)renderTopology($('ops-graph'),graph);else $('ops-graph').textContent='Diagram exceeds 200 nodes; inspect the full resource matrix. No graph was silently truncated.';
    $('ops-export').disabled=false;$('ops-agent').disabled=false;
  }
  function clearPreview(){proposal=null;preview=null;$('ops-preview').textContent='';$('ops-proposal-export').disabled=true;}
  function action(id,fn){$(id).addEventListener('click',async()=>{if(busy)return;busy=true;$(id).disabled=true;try{await fn();}catch(e){notice(e.message,true);}finally{busy=false;$(id).disabled=false;}});}
  action('ops-refresh',async()=>{await history();await load();});
  action('ops-scopes',async()=>{options('ops-subscriptions',[]);const scopes=await api('operations/scopes');options('ops-subscriptions',scopes.subscriptions.map(s=>[s.id,`${s.name} · ${s.id}`]));$('ops-scope-status').textContent=`Authenticated tenant ${scopes.tenantId} · ${scopes.coverage.status}. ${scopes.exclusions.join(' ')} Visible subscriptions only; this is not complete tenant coverage.`;});
  action('ops-collect',async()=>{invalidate();controller=new AbortController();$('ops-cancel').hidden=false;$('ops-status').textContent='Collecting configuration; each failed/denied collection remains explicit…';try{evidence=await api('operations/collect',{subscriptionIds:[...$('ops-subscriptions').selectedOptions].map(o=>o.value)},controller.signal);await history();$('ops-observed').value=evidence.id;await load();$('ops-status').textContent='Observation saved. Select Assess to inspect findings.';}finally{$('ops-cancel').hidden=true;controller=null;}});
  $('ops-cancel').addEventListener('click',()=>controller?.abort());
  $('ops-observed').addEventListener('change',()=>load().catch(e=>notice(e.message,true)));
  for(const id of ['ops-reference','ops-workflow'])$(id).addEventListener('change',invalidate);
  action('ops-edit-add',async()=>{invalidate();const e={resourceId:$('ops-edit-resource').value,property:$('ops-edit-property').value,value:$('ops-edit-value').value};edits=edits.filter(x=>x.resourceId!==e.resourceId||x.property!==e.property);edits.push(e);$('ops-edits').textContent=JSON.stringify(edits,null,2);});
  action('ops-edit-clear',async()=>{invalidate();edits=[];$('ops-edits').textContent='';});
  action('ops-assess',async()=>{invalidate();if(!evidence)throw new Error('Collect or select an observation first.');assessment=await api('operations/assess',{workflow:$('ops-workflow').value,evidenceId:evidence.id,referenceId:$('ops-reference').value||null,edits});draw();});
  const save=(value,name)=>{const url=URL.createObjectURL(new Blob([JSON.stringify(value,null,2)],{type:'application/json'}));const a=document.createElement('a');a.href=url;a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);};
  $('ops-export').addEventListener('click',()=>save({evidence,assessment,desiredEdits:edits},'operations-assessment.json'));
  $('ops-agent').addEventListener('click',()=>{window.dispatchEvent(new CustomEvent('platform-operations-review',{detail:{id:assessment.id,workflow:assessment.workflow}}));page('agents');});
  for(const id of ['ops-owner','ops-rationale','ops-destination','ops-operation'])$(id).addEventListener('input',clearPreview);
  action('ops-propose',async()=>{clearPreview();if(!assessment)throw new Error('Assess the current inputs first.');proposal=await api('operations/proposals',{assessmentId:assessment.id,operation:$('ops-operation').value,resourceIds:[...$('ops-resources').querySelectorAll('input:checked')].map(i=>i.value),destinationId:$('ops-operation').value==='MigrationReview'?$('ops-destination').value:null,owner:$('ops-owner').value,rationale:$('ops-rationale').value});preview=await api(`operations/proposals/${proposal.id}/preview`,{});$('ops-preview').textContent=JSON.stringify({proposal,preview},null,2);$('ops-proposal-export').disabled=false;});
  $('ops-proposal-export').addEventListener('click',()=>save({proposal,preview},'operations-proposal.json'));
  window.addEventListener('platform-operations-advice',event=>{const a=event.detail;if(!assessment||a.assessmentDigest!==assessment.digest){notice('Return to the matching saved assessment before using this advice.',true);return;}clearPreview();for(const input of $('ops-resources').querySelectorAll('input'))input.checked=input.value===a.recommendation.resourceId;$('ops-operation').value=a.recommendation.operation;$('ops-rationale').value=a.recommendation.rationale;page('operations');});
  window.addEventListener('platform-disconnected',()=>{controller?.abort();invalidate();evidence=null;edits=[];options('ops-subscriptions',[]);options('ops-observed',[]);options('ops-reference',[]);$('ops-result').hidden=true;});
  history().then(load).catch(e=>notice(e.message,true));
}
