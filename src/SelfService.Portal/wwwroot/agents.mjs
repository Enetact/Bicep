import { renderTopology } from './topology.mjs';
import { renderModuleOptions } from './avm-modules.mjs';
// AHP coordinates a browser-owned session. Typed HTTP requests execute bounded workflows.
export async function connectAgentHost(csrf) {
  const socket = new WebSocket(`ws://${location.host}/api/agent/ahp`, ['platform-studio', csrf]);
  const pending = new Map(); let id = 0;
  socket.addEventListener('message', event => {
    const message = JSON.parse(event.data), task = pending.get(message.id);
    if (!task) return; pending.delete(message.id); clearTimeout(task.timer);
    message.error ? task.reject(new Error(message.error.message)) : task.resolve(message.result);
  });
  socket.addEventListener('close', () => { for (const task of pending.values()) { clearTimeout(task.timer); task.reject(new Error('Agent host disconnected.')); } pending.clear(); });
  await new Promise((resolve, reject) => { socket.addEventListener('open', resolve, { once: true }); socket.addEventListener('error', () => reject(new Error('Agent host unavailable.')), { once: true }); });
  const call = (method, params) => new Promise((resolve, reject) => {
    const n = ++id, timer = setTimeout(() => { pending.delete(n); reject(new Error('Agent host timed out. Check the saved receipt before retrying.')); socket.close(); }, method === 'dispatchAction' ? 330000 : 50000);
    pending.set(n, { resolve, reject, timer }); socket.send(JSON.stringify({ jsonrpc: '2.0', id: n, method, params }));
  });
  await call('initialize', { channel: 'ahp-root://', protocolVersions: ['0.9.0'], clientId: crypto.randomUUID() });
  const channel = `ahp-session:/${crypto.randomUUID()}`;
  await call('createSession', { channel, provider: 'codex' });
  return { status: async () => (await call('subscribe', { channel })).snapshot.state.platformStudio, run: request => call('dispatchAction', { channel, action: { type: 'platform/workflowRun', request } }), close: () => socket.close() };
}

export function setupAgents({ api, data, notice }) {
  const $ = id => document.getElementById(id);
  let status, host, busy = false, timer, lastResult, networkReportId, tagEvidence, operationsAssessment;
  const selected = () => status?.workflows.find(w => w.id === $('agent-workflow').value);
  const opts = (id, values) => { $(id).replaceChildren(...values.map(v => { const o = document.createElement('option'); o.value = v.id; o.textContent = v.name; return o; })); };
  function fields() {
    const w = selected(); if (!w) return;
    $('agent-network-fields').hidden = w.scope !== 'resource-group' || !!networkReportId; $('agent-workload-fields').hidden = !['workload', 'preview', 'composition', 'iac'].includes(w.scope);
    $('agent-network-snapshot').hidden = !networkReportId || !['resource-group', 'composition'].includes(w.scope); $('agent-clear-snapshot').hidden = $('agent-network-snapshot').hidden;
    $('agent-draft-fields').hidden = w.scope !== 'composition';
    $('agent-mcp-fields').hidden = w.scope !== 'mcp';
    $('agent-preview-field').hidden = !['preview','iac'].includes(w.scope); $('agent-iac-phase-field').hidden=w.scope!=='iac'; $('agent-iac-artifact-field').hidden=w.scope!=='iac'; $('agent-limits').textContent = w.limits;
    $('agent-tag-snapshot').hidden = w.scope !== 'tag-evidence';
    $('agent-operations-snapshot').hidden = w.scope !== 'operations';
    $('agent-run').textContent = w.scope === 'mcp' ? 'Run orchestration task →' : 'Collect evidence and run review →';
    $('agent-run').disabled = busy || !w.ready || (w.scope === 'tag-evidence' && !tagEvidence) || (w.scope === 'composition' && (!networkReportId || !$('agent-draft-goal').value.trim()));
    if(w.scope==='operations' && (!operationsAssessment || operationsAssessment.workflow!==w.id)) $('agent-run').disabled=true;
    if(w.scope==='mcp' && !$('agent-mcp-goal').value.trim()) $('agent-run').disabled=true;
    $('agent-connect').disabled = busy || !status.installed || status.provider.authenticated || status.provider.ready || String(status.provider.state).includes('Waiting');
    $('agent-groups').disabled = busy || !w.evidenceReady;
    for (const id of ['agent-workflow','agent-subscription','agent-group','agent-product','agent-environment','agent-region','agent-preview','agent-draft-goal','agent-mcp-goal','agent-iac-phase','agent-iac-artifact']) $(id).disabled = busy;
    $('agent-requirements').textContent = `${w.requires} · ${w.ready ? 'Ready to collect evidence and review' : 'Connect the required services to enable this workflow'}`;
  }
  function environments() {
    const p = data.products.find(p => p.id === $('agent-product').value);
    opts('agent-environment', p.targets.map(t => ({ id: t.environment, name: t.environment }))); fields();
  }
  async function refresh() {
    try {
      host ??= await connectAgentHost(data.csrf);
      status = await host.status();
    } catch { host?.close(); host = null; status = await api('agent/status'); }
    const old = $('agent-workflow').value;
    opts('agent-workflow', status.workflows.map(w => ({ id: w.id, name: w.name })));
    if (old) $('agent-workflow').value = old;
    const policy = status.policy;
    $('codex-state').textContent = `${status.provider.state} · ${policy.displayName} · ${policy.effortDisplayName} · ${policy.speed}${status.provider.runtimeVersion ? ` · Codex ${status.provider.runtimeVersion}` : ''}`;
    $('agent-policy').textContent = `${policy.displayName} · ${policy.effortDisplayName} reasoning · ${policy.speed} speed`;
    $('agent-status').textContent = status.state;
    $('agent-connect').disabled = busy || !status.installed || status.provider.ready;
    $('codex-install').hidden = status.installed;
    if (status.provider.authenticated) $('codex-login-link').hidden = true;
    if (status.provider.ready || !String(status.provider.state).includes('Waiting')) { clearInterval(timer); timer = null; }
    fields();
  }
  function action(id, fn) { $(id).addEventListener('click', async () => { $(id).disabled = true; try { await fn(); } catch (e) { notice(e.message, true); } finally { $(id).disabled = false; fields(); } }); }
  opts('agent-subscription', data.discoveryScopes); opts('agent-product', data.products); opts('agent-region', data.regions.map(r => ({ id: r, name: r })));
  environments();
  $('agent-workflow').addEventListener('change', fields); $('agent-product').addEventListener('change', environments);
  $('agent-draft-goal').addEventListener('input', fields);
  $('agent-mcp-goal').addEventListener('input', fields);
  $('agent-subscription').addEventListener('change', () => opts('agent-group', [{ id: '', name: 'Refresh resource groups' }]));
  action('agent-refresh', refresh);
  action('agent-browse-modules', async () => {
    const catalog = await api('bicep/modules');
    renderModuleOptions($('agent-module-options'), catalog);
  });
  action('agent-connect', async () => {
    const result = await api('agent/connect', {});
    // A visible link survives popup blockers. The backend validates the provider URL.
    $('codex-login-link').href = result.url; $('codex-login-link').hidden = false;
    window.open(result.url, '_blank', 'noopener,noreferrer');
    await refresh(); clearInterval(timer); timer = setInterval(() => refresh().catch(e => { clearInterval(timer); notice(e.message, true); }), 3000);
  });
  action('agent-cancel-login', async () => { await api('agent/cancel-login', {}); $('codex-login-link').hidden = true; await refresh(); });
  action('agent-disconnect', async () => { await api('agent/disconnect', {}); $('codex-login-link').hidden = true; await refresh(); });
  action('agent-groups', async () => {
    const report = await api(`agent/resource-groups/${$('agent-subscription').value}`);
    if (!['Succeeded', 'SucceededEmpty'].includes(report.status)) throw new Error(report.issue ?? 'Resource-group coverage is incomplete.');
    opts('agent-group', [{ id: '', name: 'Choose a resource group' }, ...report.resources.map(g => ({ id: g.name, name: g.name }))]);
  });
  action('agent-run', async () => {
    busy = true; fields(); $('agent-result').hidden = true; $('agent-status').textContent = selected()?.scope === 'mcp' ? 'Running the task through connected MCP providers. Requested changes will appear as plans for approval…' : 'Collecting evidence and running Codex. This can take several minutes…'; $('agent-cancel').hidden = false;
    try {
      const request = { workflow: $('agent-workflow').value, subscriptionId: $('agent-subscription').value,
        resourceGroup: $('agent-group').value, product: $('agent-product').value, environment: $('agent-environment').value,
        region: $('agent-region').value, iacArtifact:selected()?.scope==='iac'?$('agent-iac-artifact').value:null, iacPhase: selected()?.scope==='iac'?$('agent-iac-phase').value:null, previewRunId: Number($('agent-preview').value) || null,
        networkReportId: ['resource-group','composition'].includes(selected()?.scope) ? networkReportId : null, evidenceId: selected()?.scope === 'operations' ? operationsAssessment?.id : selected()?.scope === 'tag-evidence' ? tagEvidence?.id : null, resourceIds: selected()?.scope === 'tag-evidence' ? tagEvidence?.resourceIds : null,
        draftGoal: selected()?.scope === 'mcp' ? $('agent-mcp-goal').value.trim() : selected()?.scope === 'composition' ? $('agent-draft-goal').value.trim() : null };
      const result = host ? await host.run(request) : await api('agent/run', request);
      $('agent-review').textContent = result.review; $('agent-receipt').textContent = JSON.stringify(result.receipt, null, 2);
      lastResult = result; $('agent-tag-advice').replaceChildren(); $('agent-use-tags').hidden = !result.structuredReview;
      $('agent-operations-advice').replaceChildren();
      for(const r of result.operationsAdvice?.recommendations ?? []) { const row=document.createElement('p'),button=document.createElement('button');row.textContent=`${r.operation} · ${r.resourceId} · ${r.rationale} `;button.textContent='Use as editable proposal';button.addEventListener('click',()=>window.dispatchEvent(new CustomEvent('platform-operations-advice',{detail:{assessmentDigest:result.operationsAdvice.assessmentDigest,recommendation:r}})));row.append(button);$('agent-operations-advice').append(row); }
      $('agent-draft-result').hidden = !result.draft;
      $('agent-draft-source').textContent = result.draft?.files?.['main.bicep'] ?? '';
      $('agent-draft-summary').textContent = result.draft ? `${result.draft.summary}\n${result.draft.requiredInputs.length} required main inputs plus three stack settings remain. Compile, review and qualify this source before registering a workload.\n${result.draft.gaps.join('\n')}` : '';
      for(const r of result.structuredReview?.recommendations ?? []) { const label=document.createElement('label'), check=document.createElement('input'); check.type='checkbox'; check.value=r.id; label.append(check,document.createTextNode(`${r.key} = ${r.value} · ${r.resourceId} · ${r.rationale}`)); $('agent-tag-advice').append(label); }
      $('agent-diagram').replaceChildren();
      $('agent-diagram-status').textContent = result.diagram ? `${result.diagram.status}: ${result.diagram.issue}` : 'This advisory workflow does not produce an observed-resource diagram.';
      if (result.diagram?.status === 'Validated') renderTopology($('agent-diagram'), result.diagram);
      $('agent-mermaid').disabled = !result.diagram?.source;
      $('agent-saved').textContent = `Saved: ${result.path}. Advisory output; no deployment approval.`; $('agent-result').hidden = false;
    } finally { busy = false; $('agent-cancel').hidden = true; window.dispatchEvent(new Event('platform-mcp-plans-changed')); await refresh(); }
  });
  action('agent-cancel', () => api('agent/cancel', {}));
  action('agent-download', () => { const url = URL.createObjectURL(new Blob([$('agent-review').textContent], { type: 'text/markdown' })); const a = document.createElement('a'); a.href = url; a.download = 'agent-review.md'; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000); });
  const save = (value, name, type) => { const url = URL.createObjectURL(new Blob([value], {type})); const a = document.createElement('a'); a.href = url; a.download = name; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000); };
  $('agent-mermaid').addEventListener('click', () => { if (lastResult?.diagram?.source) save(lastResult.diagram.source, 'agent-diagram.mmd', 'text/plain'); });
  $('agent-receipt-download').addEventListener('click', () => { if (lastResult) save(JSON.stringify(lastResult.receipt, null, 2), 'agent-receipt.json', 'application/json'); });
  $('agent-draft-download').addEventListener('click', () => { if(lastResult?.draft?.archiveBase64) save(Uint8Array.from(atob(lastResult.draft.archiveBase64), c => c.charCodeAt(0)), 'bicep-source-draft.zip', 'application/zip'); });
  window.addEventListener('platform-iac-review', event => {
    if(busy){notice('Wait for the current agent review before changing its evidence.',true);return;}
    const r=event.detail;$('agent-workflow').value='iac';$('agent-product').value=r.product;environments();$('agent-environment').value=r.environment;
    $('agent-region').value=r.region;$('agent-preview').value=r.runId;$('agent-iac-phase').value=r.phase;$('agent-iac-artifact').value=r.artifact;fields();
    notice('IaC phase selected. Click Run to send the saved projected evidence to Codex for advisory review.');
  });
  window.addEventListener('platform-network-report', event => { networkReportId = event.detail.id; $('agent-network-snapshot').textContent = 'Using your latest network discovery snapshot. Review sends this projected evidence to Codex; it does not scan again or authorize changes.'; $('agent-workflow').value = 'visualize'; fields(); });
  $('agent-clear-snapshot').addEventListener('click', () => {networkReportId = null; fields();});
  window.addEventListener('platform-tag-evidence', event => { tagEvidence=event.detail; $('agent-workflow').value='tagging'; $('agent-tag-snapshot').textContent=`Using saved tag evidence for ${tagEvidence.resourceIds.length} selected resources. Click Run to send the approved projection to Codex.`; fields(); });
  $('agent-use-tags').addEventListener('click', () => { const review=lastResult?.structuredReview; if(!review)return; const ids=[...$('agent-tag-advice').querySelectorAll('input:checked')].map(n=>n.value); window.dispatchEvent(new CustomEvent('platform-tag-recommendations',{detail:{evidenceDigest:review.evidenceDigest,edits:review.recommendations.filter(r=>ids.includes(r.id)).map(r=>({resourceId:r.resourceId,key:r.key,value:r.value,operation:r.operation}))}})); });
  window.addEventListener('pagehide', () => { clearInterval(timer); host?.close(); });
  window.addEventListener('platform-operations-review', async event => { if(busy){notice('Wait for the current agent review.',true);return;}operationsAssessment=event.detail;await refresh();$('agent-workflow').value=operationsAssessment.workflow;$('agent-operations-snapshot').textContent='Using your selected saved operations assessment. Click Run to send its bounded configuration evidence through the existing AHP/Codex/MCP workflow.';fields(); });
  window.addEventListener('platform-disconnected', () => { operationsAssessment=null;$('agent-operations-advice').replaceChildren(); });
  window.addEventListener('platform-disconnected', () => { tagEvidence=null; lastResult=null; networkReportId=null; $('agent-result').hidden=true; $('agent-review').textContent=''; $('agent-receipt').textContent=''; $('agent-tag-advice').replaceChildren(); $('agent-tag-snapshot').textContent='Open Tag governance and select evidence for this review.'; });
  document.addEventListener('platform-mcp-status-changed', () => { void refresh().catch(e => notice(e.message, true)); });
  return refresh;
}
