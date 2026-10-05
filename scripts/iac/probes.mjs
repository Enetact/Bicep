import { forType, projection, profile } from './projection.mjs';
import { assertId, inScope, resourceType, limits } from './core.mjs';

// Fixed child GET/list routes. No provider links, model-authored URLs, POST listKeys or data-plane probes.
export async function probeChildren(snapshot,target,apiVersions,token,read) {
  snapshot.probes??=[];const deadline=Date.now()+120000;let requests=0;
  for(const parent of [...snapshot.resources]) {
    if(parent.status!=='Observed'||!inScope(parent.id,target))continue;
    for(const child of forType(parent.type)?.children??[]) {
      const id=parent.id+'/'+child.suffix;
      if(snapshot.probes.some(p=>p.id.toLowerCase()===id.toLowerCase()))continue;
      assertId(id,target);
      const apiVersion=apiVersions[child.type]??forType(child.type).apiVersion;
      const receipt={id,parentId:parent.id,apiVersion,profile:profile.version,method:'GET',status:'Unknown',pages:0,observed:0,observedUtc:new Date().toISOString()};
      snapshot.probes.push(receipt);
      try {
        let next=null;const visited=new Set();
        do {
          if(++requests>100||Date.now()>deadline||receipt.pages>=10){receipt.status='LimitExceeded';break;}
          const result=await read(token,id,apiVersion,next);receipt.pages++;
          if(result.status!=='Observed'){receipt.status=result.status==='Absent'?'NotAvailable':result.status;break;}
          const rows=child.list?result.value?.value:[result.value];
          if(!Array.isArray(rows)||rows.length>500)throw Error('Invalid child listing.');
          for(const r of rows) {
            const resourceId=child.list?r?.id:id;assertId(resourceId,target);
            if((child.list&&!resourceId.toLowerCase().startsWith(id.toLowerCase()+'/'))||resourceType(resourceId).toLowerCase()!==child.type ||
              (r.id&&r.id.toLowerCase()!==resourceId.toLowerCase()))throw Error('Unexpected child identity.');
            const existing=snapshot.resources.find(x=>x.id.toLowerCase()===resourceId.toLowerCase());
            if(existing)continue;
            if(snapshot.resources.length>=limits.resources){receipt.status='LimitExceeded';break;}
            snapshot.resources.push({id:resourceId,type:resourceType(resourceId),apiVersion,status:'Observed',...projection(r,child.type),observedUtc:new Date().toISOString(),source:'Child ARM GET'});receipt.observed++;
          }
          if(receipt.status==='LimitExceeded')break;
          next=child.list?result.value.nextLink:null;
          if(next){const url=new URL(next);if(url.origin!=='https://management.azure.com'||url.pathname.toLowerCase()!==id.toLowerCase()||url.username||url.password||url.hash||url.searchParams.get('api-version')!==apiVersion||visited.has(next))throw Error('Unsafe continuation.');visited.add(next);}
          receipt.status=next?'Partial':'Succeeded';
        }while(next);
      }catch{receipt.status='Unknown';}
    }
  }
  snapshot.completedUtc=new Date().toISOString();
}
