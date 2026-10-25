// Provider credentials stay in the host. This UI handles catalog metadata and reviewed plans.
export function parseToolArguments(value) {
  let args;
  try { args = JSON.parse(value); } catch { throw new Error('Enter valid JSON for the tool arguments.'); }
  if (args === null || typeof args !== 'object' || Array.isArray(args)) throw new Error('Tool arguments must be a JSON object.');
  return args;
}

export function planControls(plan) {
  return {
    approve: !!plan?.id && !!plan?.digest && plan.state === 'AwaitingApproval',
    run: !!plan?.id && !!plan?.digest && ['Ready', 'Approved'].includes(plan.state)
  };
}

export function setupMcp({ api, notice }) {
  const $ = id => document.getElementById(id);
  const make = (tag, text, cls) => { const n = document.createElement(tag); if (text !== undefined) n.textContent = text; if (cls) n.className = cls; return n; };
  let providers = [], steps = [], plan = null, savedPlans = [], busy = false;
  const selectedProvider = () => providers.find(p => p.id === $('mcp-step-provider').value);
  const selectedTool = () => selectedProvider()?.tools?.find(t => t.name === $('mcp-step-tool').value);
  const fillOptions = (node, values, empty) => {
    node.replaceChildren(...(values.length ? values : [{ value: '', label: empty }]).map(v => { const option = make('option', v.label); option.value = v.value; return option; }));
  };
  function controls() {
    const permitted = planControls(plan);
    $('mcp-preview').disabled = busy || !steps.length;
    $('mcp-approve').hidden = !permitted.approve; $('mcp-approve').disabled = busy;
    $('mcp-run').hidden = !permitted.run; $('mcp-run').disabled = busy;
    $('mcp-save-receipt').disabled = !plan;
    $('mcp-add-step').disabled = busy || !selectedTool();
    for (const id of ['mcp-step-provider', 'mcp-step-tool', 'mcp-step-arguments', 'mcp-clear', 'mcp-refresh', 'mcp-plans-refresh']) $(id).disabled = busy;
    for (const button of $('mcp-steps').querySelectorAll('button')) button.disabled = busy || button.dataset.unavailable === 'true';
    for (const button of $('mcp-providers').querySelectorAll('button')) button.disabled = busy || button.dataset.unavailable === 'true';
    for (const button of $('mcp-plans').querySelectorAll('button')) button.disabled = busy;
  }
  function invalidatePlan() {
    plan = null; $('mcp-plan-result').hidden = true; $('mcp-plan-json').textContent = ''; controls();
  }
  function drawPlan(value) {
    plan = value; $('mcp-plan-result').hidden = false;
    $('mcp-plan-state').textContent = `${plan.state} · ${plan.id}`;
    $('mcp-plan-digest').textContent = `Exact plan digest: ${plan.digest}`;
    $('mcp-plan-json').textContent = JSON.stringify(plan, null, 2);
    $('mcp-plan-approval-note').textContent = plan.state === 'AwaitingApproval'
      ? 'Review every tool, argument and target below. Aspire telemetry can include private runtime content. Approve authorizes this exact plan and its returned results. Execute starts the ordered calls.'
      : ['Failed', 'UnknownOutcome'].includes(plan.state)
        ? 'Review the saved receipts and verify the external state before preparing another plan. A partial run may already have changed its target.'
        : plan.state === 'Completed' ? 'Execution finished. Review each tool result and receipt.' : 'Execution uses the connected identities and their current service permissions.';
    controls();
  }
  async function withBusy(fn) {
    if (busy) return; busy = true; controls();
    try { await fn(); } catch (e) { notice(e.message, true); }
    finally { busy = false; controls(); }
  }
  function chooseTool(providerId, name) {
    $('mcp-step-provider').value = providerId; drawToolOptions(name);
    $('mcp-step-arguments').value = '{}';
    $('mcp-step-arguments').focus();
    $('mcp-builder').scrollIntoView({ behavior: 'smooth', block: 'start' });
  }
  function drawProviders() {
    $('mcp-providers').replaceChildren(...providers.map(provider => {
      const card = make('article', undefined, 'panel');
      card.append(make('h3', provider.name), make('p', `${provider.state} · ${provider.toolCount ?? provider.tools?.length ?? 0} discovered tools`));
      if (provider.id === 'aspire') card.append(make('p', 'Reads logs and traces from this checkout’s authenticated local dashboard. Each telemetry plan needs your review before private results are returned.'));
      if (provider.error) card.append(make('p', provider.error, 'mcp-error'));
      const connect = make('button', provider.connected ? 'Rediscover full catalog' : 'Connect provider', 'primary');
      connect.addEventListener('click', () => withBusy(async () => { await api(`mcp/${encodeURIComponent(provider.id)}/connect`, {}); await refresh(); }));
      const disconnect = make('button', 'Disconnect provider', 'quiet'); disconnect.dataset.unavailable = String(!provider.connected);
      disconnect.addEventListener('click', () => withBusy(async () => { await api(`mcp/${encodeURIComponent(provider.id)}/disconnect`, {}); await refresh(); }));
      card.append(connect, disconnect); return card;
    }));
    if (!providers.length) $('mcp-providers').append(make('p', 'No MCP providers were reported by the host.'));
    const previous = $('mcp-step-provider').value;
    fillOptions($('mcp-step-provider'), providers.filter(p => p.connected).map(p => ({ value: p.id, label: p.name })), 'Connect a provider first');
    if (providers.some(p => p.connected && p.id === previous)) $('mcp-step-provider').value = previous;
    drawToolOptions(); drawCatalog();
  }
  function drawToolOptions(name = $('mcp-step-tool').value) {
    fillOptions($('mcp-step-tool'), (selectedProvider()?.tools ?? []).map(t => ({ value: t.name, label: `${t.name} · ${t.effect ?? 'Approval required'}` })), 'No discovered tools');
    if (selectedProvider()?.tools?.some(t => t.name === name)) $('mcp-step-tool').value = name;
    const tool = selectedTool();
    $('mcp-step-description').textContent = tool?.description ?? '';
    $('mcp-step-schema').textContent = tool ? JSON.stringify(tool.inputSchema ?? {}, null, 2) : 'Connect a provider to inspect its tool arguments.';
    controls();
  }
  function drawCatalog() {
    const query = $('mcp-tool-search').value.trim().toLowerCase();
    const providerFilter = $('mcp-tool-filter').value;
    const tools = providers.filter(p => !providerFilter || p.id === providerFilter).flatMap(p => (p.tools ?? []).map(tool => ({ provider: p, tool })))
      .filter(({ provider, tool }) => `${provider.name} ${tool.name} ${tool.description ?? ''}`.toLowerCase().includes(query));
    $('mcp-tool-count').textContent = `${tools.length} tools shown. Catalogs reflect the connected servers and their granted access.`;
    $('mcp-tool-list').replaceChildren(...tools.map(({ provider, tool }) => {
      const row = make('details'); row.append(make('summary', `${provider.name} / ${tool.name} · ${tool.effect ?? 'Approval required'}`));
      row.append(make('p', tool.description ?? ''), make('pre', JSON.stringify(tool.inputSchema ?? {}, null, 2), 'document'));
      const button = make('button', 'Add this tool to a plan', 'secondary'); button.disabled = !provider.connected;
      button.addEventListener('click', () => chooseTool(provider.id, tool.name)); row.append(button); return row;
    }));
  }
  function drawSteps() {
    $('mcp-steps').replaceChildren(...steps.map((step, index) => {
      const row = make('li'); row.append(make('strong', `${index + 1}. ${step.provider} / ${step.tool}`), make('pre', JSON.stringify(step.arguments, null, 2), 'document'));
      for (const [label, offset] of [['Move up', -1], ['Move down', 1]]) {
        const button = make('button', label, 'secondary'); button.dataset.unavailable = String(index + offset < 0 || index + offset >= steps.length);
        button.addEventListener('click', () => { if (busy || button.dataset.unavailable === 'true') return; const target = index + offset; [steps[index], steps[target]] = [steps[target], steps[index]]; invalidatePlan(); drawSteps(); }); row.append(button);
      }
      const remove = make('button', 'Remove', 'quiet'); remove.addEventListener('click', () => { if (busy) return; steps.splice(index, 1); invalidatePlan(); drawSteps(); }); row.append(remove); return row;
    })); controls();
  }
  async function refreshPlans() {
    const value = await api('mcp/plans'); savedPlans = Array.isArray(value) ? value : value.plans ?? [];
    $('mcp-plans').replaceChildren(...savedPlans.map(item => {
      const row = make('div', undefined, 'run-row'); row.append(make('span', `${item.state} · ${item.id} · ${item.steps?.length ?? item.stepCount ?? '?'} steps`));
      const view = make('button', 'Review plan and receipt', 'secondary'); view.addEventListener('click', () => withBusy(async () => { drawPlan(await api(`mcp/plans/${encodeURIComponent(item.id)}`)); $('mcp-plan-result').scrollIntoView({ behavior: 'smooth' }); })); row.append(view); return row;
    }));
    if (!savedPlans.length) $('mcp-plans').append(make('p', 'No saved plans in this browser session.'));
    controls();
  }
  async function refresh() {
    const value = await api('mcp/status'); providers = value.providers ?? [];
    const filter = $('mcp-tool-filter').value;
    fillOptions($('mcp-tool-filter'), [{ value: '', label: 'All providers' }, ...providers.map(p => ({ value: p.id, label: p.name }))], 'All providers');
    if (providers.some(p => p.id === filter)) $('mcp-tool-filter').value = filter;
    drawProviders(); await refreshPlans();
    if (plan) { try { drawPlan(await api(`mcp/plans/${encodeURIComponent(plan.id)}`)); } catch { invalidatePlan(); } }
    $('mcp-status').textContent = `${providers.filter(p => p.connected).length} of ${providers.length} providers connected. Full discovered catalogs are available below.`;
    document.dispatchEvent(new Event('platform-mcp-status-changed'));
  }
  $('mcp-step-provider').addEventListener('change', () => drawToolOptions());
  $('mcp-step-tool').addEventListener('change', () => drawToolOptions());
  $('mcp-tool-search').addEventListener('input', drawCatalog); $('mcp-tool-filter').addEventListener('change', drawCatalog);
  $('mcp-add-step').addEventListener('click', () => { if (busy) return; try { const tool = selectedTool(); if (!tool) throw new Error('Choose a discovered tool.'); steps.push({ provider: selectedProvider().id, tool: tool.name, arguments: parseToolArguments($('mcp-step-arguments').value) }); invalidatePlan(); drawSteps(); } catch (e) { notice(e.message, true); } });
  $('mcp-clear').addEventListener('click', () => { if (busy) return; steps = []; invalidatePlan(); drawSteps(); });
  $('mcp-refresh').addEventListener('click', () => withBusy(refresh));
  $('mcp-plans-refresh').addEventListener('click', () => withBusy(refreshPlans));
  $('mcp-preview').addEventListener('click', () => withBusy(async () => { drawPlan(await api('mcp/plans', { steps })); await refreshPlans(); }));
  $('mcp-approve').addEventListener('click', () => withBusy(async () => {
    const current = plan; if (!planControls(current).approve) return;
    drawPlan(await api(`mcp/plans/${encodeURIComponent(current.id)}/approve`, { digest: current.digest })); await refreshPlans();
  }));
  $('mcp-run').addEventListener('click', () => withBusy(async () => {
    const current = plan; if (!planControls(current).run) return;
    $('mcp-plan-state').textContent = 'Running ordered tool calls…';
    try { drawPlan(await api(`mcp/plans/${encodeURIComponent(current.id)}/run`, { digest: current.digest })); }
    finally { await refreshPlans(); try { drawPlan(await api(`mcp/plans/${encodeURIComponent(current.id)}`)); } catch { /* Preserve the last known result when receipt refresh is unavailable. */ } }
  }));
  $('mcp-save-receipt').addEventListener('click', () => {
    if (!plan) return;
    const url = URL.createObjectURL(new Blob([JSON.stringify(plan, null, 2)], { type: 'application/json' })); const link = make('a'); link.href = url; link.download = `mcp-plan-${plan.id}.json`; link.click(); setTimeout(() => URL.revokeObjectURL(url), 1000);
  });
  document.addEventListener('github-connection-changed', () => { void withBusy(refresh); });
  window.addEventListener('platform-disconnected', () => { invalidatePlan(); void withBusy(refresh); });
  window.addEventListener('platform-mcp-plans-changed', () => { void withBusy(refreshPlans); });
  void withBusy(refresh);
  return () => withBusy(refresh);
}
