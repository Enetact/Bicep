import { renderTopology } from './topology.mjs';
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
    const n = ++id, timer = setTimeout(() => { pending.delete(n); reject(new Error('Agent host timed out.')); socket.close(); }, 50000);
    pending.set(n, { resolve, reject, timer }); socket.send(JSON.stringify({ jsonrpc: '2.0', id: n, method, params }));
  });
  await call('initialize', { channel: 'ahp-root://', protocolVersions: ['0.9.0'], clientId: crypto.randomUUID() });
  const channel = `ahp-session:/${crypto.randomUUID()}`;
  await call('createSession', { channel, provider: 'codex' });
  return { status: async () => (await call('subscribe', { channel })).snapshot.state.platformStudio, close: () => socket.close() };
}

export function setupAgents({ api, data, notice }) {
  const $ = id => document.getElementById(id);
  let status, host, busy = false, timer, lastResult, networkReportId;
  const selected = () => status?.workflows.find(w => w.id === $('agent-workflow').value);
  const opts = (id, values) => { $(id).replaceChildren(...values.map(v => { const o = document.createElement('option'); o.value = v.id; o.textContent = v.name; return o; })); };
  function fields() {
    const w = selected(); if (!w) return;
    $('agent-network-fields').hidden = w.scope !== 'resource-group' || !!networkReportId; $('agent-workload-fields').hidden = w.scope === 'resource-group';
    $('agent-network-snapshot').hidden = !networkReportId || w.scope !== 'resource-group'; $('agent-clear-snapshot').hidden = $('agent-network-snapshot').hidden;
    $('agent-preview-field').hidden = w.scope !== 'preview'; $('agent-limits').textContent = w.limits;
    $('agent-run').disabled = busy || !w.ready;
    $('agent-connect').disabled = busy || !status.installed || status.provider.ready || String(status.provider.state).includes('Waiting');
    $('agent-groups').disabled = busy || !w.evidenceReady;
    for (const id of ['agent-workflow','agent-subscription','agent-group','agent-product','agent-environment','agent-region','agent-preview']) $(id).disabled = busy;
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
    $('codex-state').textContent = `${status.provider.state} · GPT-6 Astra · High · Standard`;
    $('agent-status').textContent = status.state;
    $('agent-connect').disabled = busy || !status.installed || status.provider.ready;
    $('codex-install').hidden = status.installed;
    if (status.provider.ready || !String(status.provider.state).includes('Waiting')) { clearInterval(timer); timer = null; }
    fields();
  }
  function action(id, fn) { $(id).addEventListener('click', async () => { $(id).disabled = true; try { await fn(); } catch (e) { notice(e.message, true); } finally { $(id).disabled = false; fields(); } }); }
  opts('agent-subscription', data.discoveryScopes); opts('agent-product', data.products); opts('agent-region', data.regions.map(r => ({ id: r, name: r })));
  environments();
  $('agent-workflow').addEventListener('change', fields); $('agent-product').addEventListener('change', environments);
  $('agent-subscription').addEventListener('change', () => opts('agent-group', [{ id: '', name: 'Refresh resource groups' }]));
  action('agent-refresh', refresh);
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
    busy = true; fields(); $('agent-result').hidden = true; $('agent-status').textContent = 'Collecting evidence and running Codex. This can take several minutes…'; $('agent-cancel').hidden = false;
    try {
      const result = await api('agent/run', { workflow: $('agent-workflow').value, subscriptionId: $('agent-subscription').value,
        resourceGroup: $('agent-group').value, product: $('agent-product').value, environment: $('agent-environment').value,
        region: $('agent-region').value, previewRunId: Number($('agent-preview').value) || null,
        networkReportId: selected()?.scope === 'resource-group' ? networkReportId : null });
      $('agent-review').textContent = result.review; $('agent-receipt').textContent = JSON.stringify(result.receipt, null, 2);
      lastResult = result; $('agent-diagram').replaceChildren();
      $('agent-diagram-status').textContent = result.diagram ? `${result.diagram.status}: ${result.diagram.issue}` : 'This advisory workflow does not produce an observed-resource diagram.';
      if (result.diagram?.status === 'Validated') renderTopology($('agent-diagram'), result.diagram);
      $('agent-mermaid').disabled = !result.diagram?.source;
      $('agent-saved').textContent = `Saved: ${result.path}. Advisory output; no deployment approval.`; $('agent-result').hidden = false;
    } finally { busy = false; $('agent-cancel').hidden = true; await refresh(); }
  });
  action('agent-cancel', () => api('agent/cancel', {}));
  action('agent-download', () => { const url = URL.createObjectURL(new Blob([$('agent-review').textContent], { type: 'text/markdown' })); const a = document.createElement('a'); a.href = url; a.download = 'agent-review.md'; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000); });
  const save = (value, name, type) => { const url = URL.createObjectURL(new Blob([value], {type})); const a = document.createElement('a'); a.href = url; a.download = name; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000); };
  $('agent-mermaid').addEventListener('click', () => { if (lastResult?.diagram?.source) save(lastResult.diagram.source, 'agent-diagram.mmd', 'text/plain'); });
  $('agent-receipt-download').addEventListener('click', () => { if (lastResult) save(JSON.stringify(lastResult.receipt, null, 2), 'agent-receipt.json', 'application/json'); });
  window.addEventListener('platform-network-report', event => { networkReportId = event.detail.id; $('agent-network-snapshot').textContent = 'Using your latest network discovery snapshot. Review sends this projected evidence to Codex; it does not scan again or authorize changes.'; $('agent-workflow').value = 'visualize'; fields(); });
  $('agent-clear-snapshot').addEventListener('click', () => {networkReportId = null; fields();});
  window.addEventListener('pagehide', () => { clearInterval(timer); host?.close(); });
  return refresh;
}
