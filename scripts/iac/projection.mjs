import fs from 'node:fs';
import { createHash } from 'node:crypto';

// One source allowlist is also embedded into the portal validator. No recursive export of ARM bodies.
const bytes = fs.readFileSync(new URL('./profiles.json', import.meta.url));
export const profile = JSON.parse(bytes);
export const profileSha256 = createHash('sha256').update(bytes).digest('hex');
export const forType = type => profile.types[String(type).toLowerCase()];
const patterns = [...new Set([...profile.scalars, ...Object.values(profile.types).flatMap(p => p.paths)])];
const counts = patterns.flatMap(p => p.split('.').flatMap((v,i,a) => v==='*' ? [a.slice(0,i).join('.')+'.$count'] : []));
export function isProjectedPath(path) {
  const normalized = String(path).replace(/\[([0-9]+)\]/g, '.$1').replace(/\.([0-9]+)(?=\.|$)/g, '.*');
  return patterns.includes(normalized) || counts.includes(normalized);
}
const safe = v => typeof v==='boolean' || typeof v==='number' && Number.isFinite(v) ||
  typeof v==='string' && v.length<=2048 && !/[\[\]${}\r\n]/.test(v);
const expression = v => typeof v==='string' && (/^\s*\[(?!\[)/.test(v) || /\$\{|<unevaluated>|<unresolved>/i.test(v));
// Count unresolved values even outside the public allowlist without disclosing their paths/contents.
export function unresolvedCount(value, depth=0) {
  if(depth>64) throw Error('Configuration nesting exceeds the analysis limit.');
  if(expression(value)) return 1;
  return value && typeof value==='object' ? Object.values(value).reduce((n,v)=>n+unresolvedCount(v,depth+1),0) : 0;
}
function ordered(array) {
  const ordinal=(a,b)=>a<b?-1:a>b?1:0;
  if(array.every(v=>v===null||typeof v!=='object')) return [...array].sort((a,b)=>ordinal(JSON.stringify(a),JSON.stringify(b)));
  for(const key of ['name','id']) if(array.every(v=>typeof v?.[key]==='string') && new Set(array.map(v=>v[key])).size===array.length)
    return [...array].sort((a,b)=>ordinal(a[key],b[key]));
  return array; // Without an identity key, retain order; never join unrelated values as a set.
}
export function projection(resource, type=resource?.type) {
  const config={}, propertyCoverage=[];
  for(const pattern of [...new Set([...profile.scalars,...(forType(type)?.paths??[])])]) {
    const parts=pattern.split('.'); let status='Observed',seen=false;
    const mark = s => { if(status==='Observed'||s==='LimitExceeded')status=s; };
    function walk(value,index,path) {
      if(Object.keys(config).length>=4096){mark('LimitExceeded');return;}
      if(index===parts.length) {
        seen=true;
        if(safe(value))config[path.join('.')]=value;
        else mark(expression(value)?'UnresolvedExpression':'NotReturned');
        return;
      }
      if(expression(value)){mark('UnresolvedExpression');return;}
      const key=parts[index];
      if(key==='*') {
        if(!Array.isArray(value)){mark('NotReturned');return;}
        if(value.length>256){mark('LimitExceeded');return;}
        config[path.join('.')+'.$count']=value.length;seen=true;
        ordered(value).forEach((v,i)=>walk(v,index+1,[...path,String(i)]));
      } else if(value && typeof value==='object' && Object.hasOwn(value,key))walk(value[key],index+1,[...path,key]);
      else mark('NotReturned');
    }
    walk(resource,0,[]);
    propertyCoverage.push({path:pattern,status:seen||status!=='Observed'?status:'NotReturned'});
  }
  return {config,propertyCoverage};
}
export const project = (resource,type) => projection(resource,type).config;
