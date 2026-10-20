const statuses = {
  Configured: 'Configured in source', NeedsConfiguration: 'Needs setup',
  NotVerified: 'Awaiting pipeline check', Unsupported: 'Unsupported'
};
const modes = { Bootstrap: 'Create the workspace first', Private: 'Private profile selected', Unconfigured: 'Private profile needs setup' };
const bounded = (value, max) => typeof value === 'string' && value.length <= max;
const sourceIds = ['source-onboarding', 'source-identity', 'source-operation', 'source-pool-binding', 'source-pool-profile', 'source-region', 'source-private-workspace'];
const runtimeIds = ['runtime-managed-pool', 'runtime-agent-subnet', 'runtime-agent-job', 'runtime-private-endpoint', 'runtime-private-reachability', 'runtime-public-policy', 'runtime-plan-verification'];

// Source assessment cannot establish live pool, network or workspace readiness.
export function fabricReadinessView(report, environment, region) {
  if (report?.schemaVersion !== 1 || report.scope !== 'ReviewedSourceOnly' || report.canDeploy !== false || report.liveVerified !== false ||
      report.environment !== environment || report.region !== region ||
      !['NeedsConfiguration', 'AwaitingPipelineVerification', 'Unavailable'].includes(report.status) ||
      !Object.hasOwn(modes, report.workspaceMode) || !bounded(report.poolName, 100) ||
      report.pipeline !== 'azure-pipelines-fabric-workspace.yml' || report.privateProfile !== 'Same VNet / Azure DNS / IPv4' ||
      !bounded(report.checkedUtc, 60) || !Number.isFinite(Date.parse(report.checkedUtc)) ||
      !Array.isArray(report.checks) || !report.checks.length || report.checks.length > 40 ||
      !report.checks.every(c => c && bounded(c.id, 80) && bounded(c.label, 160) && bounded(c.detail, 1600) && Object.hasOwn(statuses, c.status)) ||
      !Array.isArray(report.limitations) || report.limitations.length > 12 || !report.limitations.every(x => bounded(x, 1600)) ||
      !Array.isArray(report.sources) || report.sources.length > 20 || !report.sources.every(s => s && bounded(s.path, 260) && /^[a-f0-9]{64}$/.test(s.sha256))) return null;
  const sourceChecks = report.checks.filter(c => c.id.startsWith('source-'));
  const pipelineChecks = report.checks.filter(c => c.id.startsWith('runtime-'));
  const expectedSourceIds = report.status === 'Unavailable' ? ['source-unavailable'] : sourceIds;
  if (new Set(report.checks.map(c => c.id)).size !== report.checks.length ||
      sourceChecks.length + pipelineChecks.length !== report.checks.length ||
      sourceChecks.length !== expectedSourceIds.length || !sourceChecks.every(c => expectedSourceIds.includes(c.id) && c.status !== 'NotVerified') ||
      pipelineChecks.length !== runtimeIds.length || !pipelineChecks.every(c => runtimeIds.includes(c.id) && c.status === 'NotVerified') ||
      (report.status === 'AwaitingPipelineVerification' && sourceChecks.some(c => c.status !== 'Configured'))) return null;
  return {
    heading: report.status === 'Unavailable' ? 'Source checks unavailable' : report.status === 'NeedsConfiguration' ? 'Configuration needs attention' : 'Source configured · pipeline verification pending',
    poolName: report.poolName || 'Managed pool not configured', mode: modes[report.workspaceMode],
    sourceChecks: sourceChecks.map(c => ({ ...c, statusLabel: statuses[c.status] })),
    pipelineChecks: pipelineChecks.map(c => ({ ...c, statusLabel: statuses[c.status] })),
    limitations: [...report.limitations], sources: [...report.sources], checkedUtc: report.checkedUtc
  };
}

export function setupFabricReadiness({ container, api, openManagedPool }) {
  let selection, generation = 0, controller;
  const make = (tag, text, cls) => {
    const node = container.ownerDocument.createElement(tag);
    if (text != null) node.textContent = text;
    if (cls) node.className = cls;
    return node;
  };
  function clear() {
    generation++; controller?.abort(); controller = null; selection = null;
    container.replaceChildren(); container.hidden = true; container.setAttribute('aria-busy', 'false');
  }
  async function load() {
    if (!selection) return;
    controller?.abort(); controller = new AbortController();
    const version = ++generation, { environment, region } = selection;
    const heading = make('div', null, 'section-heading'), title = make('div');
    const h = make('h2', 'Fabric private workspace readiness'); h.id = 'fabric-readiness-title';
    title.append(make('div', 'MANAGED AGENTS + PRIVATE WORKSPACE', 'eyebrow'), h);
    const refresh = make('button', 'Refresh source checks', 'secondary'); refresh.type = 'button'; refresh.disabled = true;
    refresh.addEventListener('click', load); heading.append(title, refresh);
    const scope = make('p', `${environment} · ${region} · Local configuration only`, 'muted');
    const progress = make('p', 'Reading the reviewed Fabric and managed pool configuration…', 'callout');
    progress.setAttribute('role', 'status'); progress.setAttribute('aria-live', 'polite');
    const content = make('div');
    container.hidden = false; container.setAttribute('aria-busy', 'true'); container.setAttribute('aria-labelledby', h.id);
    container.replaceChildren(heading, scope, progress, content);
    try {
      const report = await api(`fabric/readiness/${encodeURIComponent(environment)}/${encodeURIComponent(region)}`, undefined, controller.signal);
      if (version !== generation) return;
      const view = fabricReadinessView(report, environment, region);
      if (!view) throw new Error('Invalid readiness response.');
      progress.textContent = view.heading;
      content.append(make('p', 'These checks read saved configuration. The protected pipeline verifies Azure resources, the assigned agent and private connectivity before execution.', 'muted'));
      const summary = make('div', null, 'fabric-summary');
      const pool = make('div'), workspace = make('div');
      pool.append(make('h3', 'Managed DevOps Pool'), make('p', view.poolName), make('p', 'Created separately with the Managed DevOps Pool workload.', 'muted'));
      workspace.append(make('h3', 'Private workspace'), make('p', view.mode), make('p', 'Supported profile: same VNet, Azure DNS and private IPv4.', 'muted'));
      summary.append(pool, workspace); content.append(summary);
      const poolButton = make('button', 'Configure Managed DevOps Pool →', 'secondary'); poolButton.type = 'button';
      poolButton.addEventListener('click', () => openManagedPool(environment, region)); content.append(poolButton);
      function checks(titleText, values) {
        if (!values.length) return;
        content.append(make('h3', titleText));
        const list = make('ul', null, 'fabric-checks');
        for (const check of values) {
          const row = make('li'); row.dataset.status = check.status;
          const top = make('div', null, 'fabric-check-heading');
          top.append(make('strong', check.label), make('span', check.statusLabel, 'tag'));
          row.append(top, make('p', check.detail, 'muted')); list.append(row);
        }
        content.append(list);
      }
      checks('Configuration to review', view.sourceChecks);
      checks('Private readiness checks at execution', view.pipelineChecks);
      if (view.limitations.length) {
        const limit = make('div', null, 'callout'); limit.append(make('h3', 'Deployment limits'));
        for (const line of view.limitations) limit.append(make('p', line));
        content.append(limit);
      }
      const provenance = make('details'); provenance.append(make('summary', 'Reviewed sources and workflow'));
      provenance.append(make('p', 'Workspace workflow: azure-pipelines-fabric-workspace.yml', 'muted'));
      provenance.append(make('p', `Source checked at ${view.checkedUtc}. Source configuration does not grant deployment approval.`, 'muted'));
      const sourceList = make('ul', null, 'fabric-sources');
      for (const source of view.sources) sourceList.append(make('li', `${source.path} · SHA-256 ${source.sha256}`));
      provenance.append(sourceList); content.append(provenance);
    } catch {
      if (version !== generation) return;
      progress.textContent = 'Source checks unavailable. PowerShell 7.4 or later and the reviewed source files are required. Refresh to retry. Pool and private workspace readiness remain unverified.';
      content.replaceChildren();
    } finally {
      if (version === generation) { refresh.disabled = false; container.setAttribute('aria-busy', 'false'); }
    }
  }
  function select(product, environment, region) {
    if (product !== 'fabric') { clear(); return; }
    if (selection?.environment === environment && selection?.region === region) return;
    selection = { environment, region }; void load();
  }
  return { select, clear };
}
