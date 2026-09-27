import { networkTopology, renderTopology } from './topology.mjs';

export function setupNetwork({ api, data, notice, page }) {
  const $ = id => document.getElementById(id);
  const make = (tag, text) => { const n = document.createElement(tag); n.textContent = text; return n; };
  let scopes, report, controller, allocationTicket;
  $('allocation-workload').replaceChildren(...data.products.filter(p=>p.id!=='observe').map(p=>{const o=make('option',p.name);o.value=p.id;return o;}));
  api('network/allocation').then(status=>{
    $('allocation-status').textContent=`${status.configured?'Pool configured':'Platform setup required: configure an existing AVNM pool'} · ${status.enabled?'Reservation profile enabled':'Reservation disabled'}. ${status.note}`;
    for(const o of $('allocation-operation').options)if(o.value.startsWith('Reserve'))o.disabled=!status.enabled;
  }).catch(e=>{$('allocation-status').textContent=e.message;});
  $('allocation-review').addEventListener('click',async()=>{
    $('allocation-review').disabled=true;
    try{const result=await api('network/review',{workload:$('allocation-workload').value,environment:$('allocation-environment').value,operation:$('allocation-operation').value,profile:'avnm-private-web'});
      allocationTicket=result.ticket;$('allocation-warning').textContent=result.warning;$('allocation-payload').textContent=JSON.stringify(result.payload,null,2);$('allocation-confirmation').showModal();}
    catch(e){notice(e.message,true);}finally{$('allocation-review').disabled=false;}
  });
  $('allocation-back').addEventListener('click',()=>{$('allocation-confirmation').close();allocationTicket=null;});
  $('allocation-confirm').addEventListener('click',async()=>{
    if(!allocationTicket)return;const ticket=allocationTicket;allocationTicket=null;$('allocation-confirm').disabled=true;
    try{const run=await api('network/queue/'+ticket,{});$('allocation-run-status').textContent=`Queued allocation run ${run.id}. Follow approvals, summaries and receipts in ADO.`;$('allocation-run-link').href=run.url;$('allocation-run-link').hidden=false;}
    catch(e){notice(`${e.message} Check ADO before retrying; a queue response can be lost after acceptance.`,true);}
    finally{$('allocation-confirmation').close();$('allocation-confirm').disabled=false;}
  });
  function choices() {
    const mode = $('network-mode').value;
    $('network-subscriptions').hidden = !['registered', 'selected'].includes(mode);
    $('network-group').hidden = mode !== 'managementGroup';
    $('network-subscriptions').multiple = mode === 'selected';
    const list = mode === 'registered' ? data.discoveryScopes : scopes?.subscriptions || [];
    $('network-subscriptions').replaceChildren(...list.map(s => {const o = make('option', `${s.name} · ${s.id}`); o.value = s.id; return o;}));
    $('network-group').replaceChildren(...(scopes?.managementGroups || []).map(g => {const o = make('option', `${g.name} · ${g.id}`); o.value = g.id; return o;}));
  }
  $('network-mode').addEventListener('change', choices); choices();
  $('network-scopes').addEventListener('click', async () => {
    $('network-scopes').disabled = true;
    try { scopes = await api('network/scopes'); $('network-scope-status').textContent = `${scopes.status} · Tenant ${scopes.tenantId}. ${(scopes.issues || []).join(' ')}`; choices(); }
    catch (e) { notice(e.message, true); } finally { $('network-scopes').disabled = false; }
  });
  $('network-run').addEventListener('click', async () => {
    controller = new AbortController(); $('network-run').disabled = true; $('network-cancel').hidden = false; $('network-result').hidden = true;
    for (const id of ['network-mode','network-subscriptions','network-group','network-scopes']) $(id).disabled = true;
    const start = Date.now(); const tick = setInterval(() => {$('network-progress').textContent = `Collecting scoped Azure evidence · ${Math.floor((Date.now()-start)/1000)}s · up to 10 minutes. Missing reads remain unknown.`;}, 1000);
    try {
      const body = {mode: $('network-mode').value, subscriptionIds: [...$('network-subscriptions').selectedOptions].map(o => o.value), managementGroupId: $('network-group').value || null};
      const response = await fetch('/api/network/discover', {method:'POST', signal:controller.signal, headers:{'Content-Type':'application/json','X-Portal-CSRF':data.csrf}, body:JSON.stringify(body)});
      const value = await response.json(); if (!response.ok) throw new Error(value.error || 'Network discovery failed.'); report = value;
      $('network-result').hidden = false; renderTopology($('network-diagram'), networkTopology(report));
      $('network-findings').replaceChildren(...report.assessment.findings.map(f => make('p', `${f.state} · ${f.rule} · ${f.resourceId}\n${f.summary}`)));
      $('network-coverage').textContent = `${report.membershipCoverage}\n${report.issues.join('\n')}\n` + report.reports.flatMap(r => r.collections.map(c => `${r.subscriptionId} · ${c.name}: ${c.status} (${c.observed} records) ${c.issue || ''}`)).join('\n');
      $('network-progress').textContent = `${report.status} · ${report.reports.length}/${report.resolvedSubscriptions.length} subscriptions collected. Saved: artifacts/portal-network/${report.id}/report.json. No allocation or deployment authorized.`;
    } catch (e) { $('network-progress').textContent = e.name === 'AbortError' ? 'Scan cancelled; no empty scope or allocation approval inferred.' : e.message; notice($('network-progress').textContent, true); }
    finally {clearInterval(tick); controller = null; $('network-run').disabled = false; $('network-cancel').hidden = true; for (const id of ['network-mode','network-subscriptions','network-group','network-scopes']) $(id).disabled = false;}
  });
  $('network-cancel').addEventListener('click', () => controller?.abort());
  $('network-download').addEventListener('click', () => { if (!report) return; const url = URL.createObjectURL(new Blob([JSON.stringify(report,null,2)],{type:'application/json'})); const a=make('a',''); a.href=url; a.download=`network-${report.id}.json`; a.click(); setTimeout(()=>URL.revokeObjectURL(url),1000); });
  $('network-agent').addEventListener('click', () => {if (!report) return; window.dispatchEvent(new CustomEvent('platform-network-report',{detail:{id:report.id}})); page('agents');});
}
