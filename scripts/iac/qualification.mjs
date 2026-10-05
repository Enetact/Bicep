import { sha,json } from './core.mjs';
import { profile,profileSha256 } from './projection.mjs';

export function inspectTemplate(template) {
  let linkedTemplates=0,inlineTemplates=0;
  function walk(value,depth=0){
    if(depth>64)throw Error('Compiled template nesting exceeds limit.');
    if(!value||typeof value!=='object')return;
    if(String(value.type).toLowerCase()==='microsoft.resources/deployments'){
      if(value.properties?.templateLink)linkedTemplates++;
      if(value.properties?.template)inlineTemplates++;
    }
    for(const v of Object.values(value))walk(v,depth+1);
  }
  walk(template);return {linkedTemplates,inlineTemplates,coverage:'Structural inspection only; ARM diagnostics remain authoritative for expansion failures.'};
}
export function qualify(report) {
  const blockers=[];const add=(code,resourceId=null)=>blockers.push({code,resourceId});
  const plan=report.planned;
  if(!plan||plan.status!=='Succeeded')add('WhatIfIncomplete');
  if(report.templateInspection?.linkedTemplates)add('LinkedTemplatesNotExpanded');
  for(const r of plan?.resources??[]){
    if(r.status==='Unresolved'||r.certainty!=='definite')add('DesiredStateUnresolved',r.id);
    const before=report.before?.resources.find(b=>b.id.toLowerCase()===r.id.toLowerCase());
    if(!before||!['Observed','Absent'].includes(before.status))add('BeforeStateUnknown',r.id);
    function beforeKnown(path) {
      if(Object.hasOwn(before.config,path))return true;
      const parts=path.split('.');
      // A returned array count can prove an element was absent; a missing scalar cannot prove its default.
      return parts.some((part,i)=>/^\d+$/.test(part)&&Object.hasOwn(before.config,parts.slice(0,i).join('.')+'.$count')&&Number(part)>=before.config[parts.slice(0,i).join('.')+'.$count']);
    }
    if(before?.status==='Observed'&&['Modify','NoChange'].includes(r.action)&&Object.keys(r.config).some(path=>!beforeKnown(path)))add('BeforePropertyUnknown',r.id);
  }
  for(const phase of ['before','after']){
    const state=report[phase];if(!state)continue;
    if(state.inventoryStatus==='Partial')add('InventoryIncomplete');
    for(const probe of state.probes??[])if(probe.status!=='Succeeded')add('ChildProbeIncomplete',probe.id);
    for(const r of state.resources)if((r.propertyCoverage??[]).some(p=>p.status==='LimitExceeded'||p.status==='UnresolvedExpression'))add('PropertyProjectionIncomplete',r.id);
  }
  if(report.after)for(const c of report.comparison??[])if(['Mismatch','Unknown','Partial projected match'].includes(c.status)||c.unknownPaths?.length)add('AfterStateUnverified',c.id);
  // Exclude collection times and ETags from drift identity; include all retained properties and probe coverage.
  const fingerprint=sha(json({profile:profileSha256,resources:(report.before?.resources??[]).map(r=>({id:r.id.toLowerCase(),status:r.status,apiVersion:r.apiVersion,config:r.config,propertyCoverage:r.propertyCoverage??[]})).sort((a,b)=>a.id.localeCompare(b.id,'en')),
    probes:(report.before?.probes??[]).map(p=>({id:p.id.toLowerCase(),apiVersion:p.apiVersion,status:p.status})).sort((a,b)=>a.id.localeCompare(b.id,'en'))}));
  return {profile:profile.version,profileSha256,beforeFingerprint:fingerprint,status:blockers.length?'Blocked':'QualifiedProjection',blockers,
    authority:'Observed properties never substitute for unresolved desired expressions. Qualification covers this profile only, not full template expansion or runtime acceptance.'};
}
