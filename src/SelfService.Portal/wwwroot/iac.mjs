import { renderTopology } from './topology.mjs';

export function iacTopology(report, phase) {
  if (report.kind !== 'portal-iac-analysis' || !['before','planned','after'].includes(phase)) throw new Error('Invalid IaC phase.');
  const state = report[phase];
  if (!state) throw new Error(`${phase} evidence has not been captured. No final state is inferred.`);
  const nodes = state.resources.map(r => ({id:r.id.toLowerCase(),label:r.id.split('/').at(-1),type:r.type,
    status:phase==='planned'?r.action:r.status,level:1,detail:phase==='planned'?`Predicted · ${r.certainty}`:`ARM read: ${r.status}`}));
  const ids = new Set(nodes.map(n=>n.id)), edges=[];
  for(const r of state.resources)for(const [key,value]of Object.entries(r.config)) {
    if((key.endsWith('.id')||key==='properties.virtualNetworkSubnetId')&&typeof value==='string'&&ids.has(value.toLowerCase()))
      edges.push({from:r.id.toLowerCase(),to:value.toLowerCase(),label:phase==='planned'?'predicted reference':'observed reference'});
  }
  return {title:{before:'Observed before',planned:'Azure What-If prediction',after:'Observed after'}[phase],
    subtitle:`Run ${report.provenance.runId} · ${state.inventoryStatus ?? state.status} · ${state.completedUtc ?? report.updatedUtc}`,
    nodes,edges,note:state.coverage,coverage:[...(state.issues??[]),...report.limitations]};
}
const element=(name,text)=>{const e=document.createElement(name);if(text!=null)e.textContent=text;return e;};
const download=(text,name)=>{const url=URL.createObjectURL(new Blob([text],{type:'text/plain'})),a=element('a');a.href=url;a.download=name;a.click();setTimeout(()=>URL.revokeObjectURL(url),1000);};
export function renderIac(container,report) {
  container.replaceChildren();
  container.append(element('h3','Before → What-If → observed after'));
  container.append(element('p',`Microsoft Azure MCP ${report.collector.version} · pipeline identity · run ${report.provenance.runId} · ${report.provenance.sourceCommit}. Hash-checked evidence; no deployment authorization.`));
  const controls=element('div'),diagram=element('div'),stateText=element('p'),review=element('button','Review this phase with Codex'),body=element('div');
  diagram.className='topology';stateText.setAttribute('role','status');let selected='planned';
  function show(phase){
    selected=phase;diagram.replaceChildren();
    for(const button of controls.children)button.setAttribute('aria-pressed',String(button.dataset.phase===phase));
    if(!report[phase]){stateText.textContent=`${phase} evidence is unavailable. Use Load observed after once Deploy publishes its artifact.`;review.disabled=true;return;}
    review.disabled=false;stateText.textContent=`${phase}: ${report[phase].inventoryStatus??report[phase].status}. Missing or denied reads remain unknown.`;
    renderTopology(diagram,iacTopology(report,phase));
  }
  for(const [phase,label]of [['before','Before'],['planned','Prepare / What-If'],['after','Observed after']]){
    const button=element('button',label);button.className='secondary';button.dataset.phase=phase;button.addEventListener('click',()=>show(phase));controls.append(button);
  }
  review.addEventListener('click',()=>window.dispatchEvent(new CustomEvent('platform-iac-review',{detail:{product:report.provenance.product,environment:report.provenance.environment,
    region:report.provenance.region,runId:Number(report.provenance.runId),phase:selected,artifact:report.artifactPhase}})));
  container.append(controls,stateText,diagram,review);
  const downloads=element('div');
  for(const [label,text,name]of [['Download receipt',JSON.stringify(report.receipt,null,2),'iac-receipt.json'],['Download projected evidence',JSON.stringify(report,null,2),'iac-evidence.json'],
    ['Download before Bicep',report.beforeBicep,'before.bicep'],['Download after Bicep',report.afterBicep,'after.bicep']]){
    const button=element('button',label);button.className='secondary';button.disabled=!text;button.addEventListener('click',()=>download(text,name));downloads.append(button);
  }
  container.append(downloads,element('p','Bicep downloads contain existing resource identities only. They are not restore templates. Receipt hashes refer to the original pipeline files; the portal JSON is a display projection.'));
  const table=element('table'),head=element('tr');for(const text of ['Resource / projected property','Before','What-If','After / comparison'])head.append(element('th',text));table.append(head);
  const ids=new Set(['before','planned','after'].flatMap(p=>(report[p]?.resources??[]).map(r=>r.id.toLowerCase())));
  for(const id of [...ids].sort()){
    const b=report.before?.resources.find(r=>r.id.toLowerCase()===id),p=report.planned?.resources.find(r=>r.id.toLowerCase()===id),a=report.after?.resources.find(r=>r.id.toLowerCase()===id),c=report.comparison.find(r=>r.id.toLowerCase()===id);
    const row=element('tr');for(const text of [id,b?.status??'Not observed',p?.action??'Not assessed',`${a?.status??'Not collected'} · ${c?.status??'Unverified'}`])row.append(element('td',text));table.append(row);
    const keys=new Set([b,p,a].flatMap(r=>Object.keys(r?.config??{})));
    for(const key of keys){const property=element('tr');for(const value of [key,b?.config[key],p?.config[key],a?.config[key]])property.append(element('td',value===undefined?'Unknown / omitted':String(value)));table.append(property);}
  }
  const details=element('details'),summary=element('summary','Resource and property comparison');details.append(summary,table);body.append(details);container.append(body);show(selected);
}
