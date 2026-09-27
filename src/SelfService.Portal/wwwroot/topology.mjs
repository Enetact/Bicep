// Pure evidence-to-graph functions shared with tests; no AI, remote script or HTML evaluation.
const text = v => typeof v === 'string' ? v : '';
const key = v => text(v).toLowerCase();
const name = id => text(id).split('/').filter(Boolean).at(-1) || id;
export function observedTopology(report) {
  const nodes = new Map(), edges = new Map();
  const add = (id, label, type, status = 'Observed', level = 2, detail = '') => {
    if (!id) return;
    const k = key(id), old = nodes.get(k);
    if (!old || old.status === 'Referenced only' || (status === 'Observed' && detail)) nodes.set(k, { id: k, label, type, status, level, detail });
  };
  const edge = (from, to, label) => { if (from && to) edges.set(`${key(from)}|${key(to)}|${label}`, { from: key(from), to: key(to), label }); };
  const scope = `/subscriptions/${report.subscriptionId}`;
  add(scope, report.subscriptionId, 'Subscription scope', 'Scope', 0);
  const resource = (r, fallback = 'Azure resource', level = 2) => {
    if (!r?.id) return;
    const match = r.id.match(/^(\/subscriptions\/[^/]+\/resourceGroups\/[^/]+)/i);
    if (match) { add(match[1], name(match[1]), 'Resource group', 'Scope', 1); edge(scope, match[1], 'contains'); edge(match[1], r.id, 'contains'); }
    add(r.id, r.name || name(r.id), r.type || fallback, 'Observed', level, [r.location, ...(r.addressPrefixes || []), r.occupancy].filter(Boolean).join(' · '));
  };
  const ref = (from, to, label) => { if (!to) return; add(to, name(to), 'Referenced resource', 'Referenced only', 3, 'Visibility and connectivity unverified'); edge(from, to, label); };
  // Deduplicate generic and specialized network records by case-insensitive ARM ID.
  for (const c of report.collections || []) for (const r of c.resources || []) resource(r);
  for (const c of report.collections || []) for (const r of c.resources || []) {
    for (const s of r.subnets || []) {
      resource(s, 'Subnet', 3); edge(r.id, s.id, 'contains subnet');
      ref(s.id, s.nsgId, 'configured NSG'); ref(s.id, s.routeTableId, 'configured route table');
    }
    for (const p of r.peerings || []) ref(r.id, p.remoteVnetId, 'configured peering; reachability unknown');
    ref(r.id, r.subnetId, 'configured subnet');
    ref(r.id, r.virtualNetworkId, 'configured DNS link');
    for (const p of r.connections || []) ref(r.id, p.configured?.privateLinkServiceId, `configured private link (${p.state || 'unknown state'})`);
  }
  return { title: 'Observed configuration', subtitle: `${report.status} · ${report.generatedUtc || ''}`, nodes: [...nodes.values()], edges: [...edges.values()],
    note: `${report.coverage || ''} Configured relationships only; effective connectivity and DNS resolution are unverified. Failed or partial collections are unknown, never empty.`,
    coverage: (report.collections || []).map(c => `${c.name}: ${c.status} (${c.resources?.length || 0} records)${c.issue ? ' · ' + c.issue : ''}`) };
}
export function networkTopology(report) {
  const nodes = new Map(), edges = new Map();
  for (const item of report.reports || []) {
    const graph = observedTopology(item);
    for (const n of graph.nodes) if (!nodes.has(n.id) || nodes.get(n.id).status === 'Referenced only') nodes.set(n.id, n);
    for (const e of graph.edges) edges.set(`${e.from}|${e.to}|${e.label}`, e);
  }
  return {title: 'Observed network configuration', subtitle: `${report.status} · ${report.generatedUtc}`, nodes: [...nodes.values()], edges: [...edges.values()],
    note: report.membershipCoverage + ' Configured relationships are not proof of reachability or allocation rights.',
    coverage: [...(report.issues || []), ...(report.reports || []).map(r => `${r.subscriptionId}: ${r.status}`)]};
}
export function proposedTopology(product, target, region, definition) {
  const root = `proposal:${product.id}:${target.environment}:${region}`;
  const nodes = [{ id: root, label: product.name, type: `${target.environment} · ${region}`, status: 'Proposed', level: 0, detail: `Subscription ${target.subscriptionId} · network ${target.network}` }];
  for (const [id, label, detail, symbol] of definition.nodes) nodes.push({ id, label, detail: `${detail} · ${definition.source} (${symbol})`, type: 'Component group', status: 'Proposed', level: 1 });
  return { title: 'Proposed workload', subtitle: `${product.name} · ${target.environment} · ${region}`, nodes,
    edges: [...definition.nodes.map(n => ({ from: root, to: n[0], label: 'includes / requires' })), ...definition.edges.map(([from, to, label]) => ({ from, to, label }))],
    note: 'Conceptual component groups from the registered workload. Names, counts, conditions and create/reuse decisions require discovery and Azure Preview. No resources have been created by this diagram.', coverage: [] };
}
export function previewTopology(report) {
  if (report.kind !== 'portal-preview-diagram' || !Array.isArray(report.resources)) throw new Error('Invalid Preview diagram report.');
  const observed = observedTopology({ subscriptionId: report.subscriptionId, collections: [{ resources: report.resources.map(r => ({ id: r.id, name: name(r.id) })) }] });
  const actions = new Map(report.resources.map(r => [key(r.id), r.action]));
  const certainty = new Map(report.resources.map(r => [key(r.id), r.certainty]));
  for (const n of observed.nodes) if (actions.has(n.id)) {
    const confidence = certainty.get(n.id) || 'unknown';
    n.status = actions.get(n.id) + (confidence === 'definite' ? '' : ` · ${confidence}`);
    n.detail = `Change certainty: ${confidence}`;
  }
  return { ...observed, title: 'Azure Preview changes', subtitle: `Run ${report.runId} · ${report.environment} · ${report.status} · ${report.createdUtc}`, note: report.coverage,
    coverage: [`Source commit: ${report.sourceCommit}`, report.previewSucceeded ? 'Preview succeeded at the saved run. Current Azure state may have changed.' : 'Blocked or incomplete: displayed changes are partial evidence. No deployment approval.'] };
}

const escape = value => String(value ?? '').replace(/[&<>"']/g, c => ({ '&':'&amp;', '<':'&lt;', '>':'&gt;', '"':'&quot;', "'":'&apos;' }[c]));
const colors = { Create:'#137547', Modify:'#975400', Delete:'#b42318', Detach:'#b42318', NoChange:'#526270', Observed:'#006cbe', 'Observed configuration':'#006cbe', Proposed:'#7050ad', 'Referenced only':'#796538' };
export function graphSvg(nodes, edges, title, evidence = '') {
  const width = 16 + (Math.max(1, ...nodes.map(n => Math.min(n.level || 0, 3))) + 1) * 278, positions = new Map();
  // Bounded pages keep large subscriptions navigable and SVG size predictable.
  const rows = [0,0,0,0];
  for (const n of nodes) { const col = Math.min(n.level || 0, 3); positions.set(n.id, { x: 16 + col * 278, y: 75 + rows[col]++ * 94 }); }
  const height = Math.max(190, 90 + Math.max(...rows) * 94);
  const paths = edges.filter(e => positions.has(e.from) && positions.has(e.to)).map(e => {
    const a = positions.get(e.from), b = positions.get(e.to);
    return `<path d="M${a.x+125},${a.y+66} L${b.x+125},${b.y}" stroke="#9babbc" stroke-width="1.3" fill="none"><title>${escape(e.label)}</title></path>`;
  }).join('');
  const boxes = nodes.map(n => { const p = positions.get(n.id); const color = colors[n.status] || '#526270';
    return `<g><title>${escape(`${n.label} | ${n.status} | ${n.id} | ${n.detail || ''}`)}</title><rect x="${p.x}" y="${p.y}" width="254" height="70" rx="9" fill="#fff" stroke="${color}" stroke-width="2"/><text x="${p.x+10}" y="${p.y+20}" font-size="12" font-weight="600">${escape(n.label.slice(0,32))}</text><text x="${p.x+10}" y="${p.y+40}" font-size="10" fill="#526270">${escape(n.type.slice(0,40))}</text><text x="${p.x+10}" y="${p.y+58}" font-size="11" fill="${color}">${escape(n.status)}</text></g>`;
  }).join('');
  return `<svg xmlns="http://www.w3.org/2000/svg" role="img" aria-label="${escape(title)}" viewBox="0 0 ${width} ${height}" width="${width}" height="${height}"><desc>${escape(evidence)}</desc><rect width="100%" height="100%" fill="#f4f7fb"/><g font-family="Segoe UI, sans-serif" fill="#152c45"><text x="16" y="28" font-size="16" font-weight="600">${escape(title)}</text><text x="16" y="49" font-size="10">${escape(evidence.slice(0, Math.floor(width / 6)))}</text>${paths}${boxes}</g></svg>`;
}

export function renderTopology(host, graph) {
  host.replaceChildren();
  const make = (tag, value, cls) => { const e = document.createElement(tag); if (value != null) e.textContent = value; if (cls) e.className = cls; return e; };
  const header = make('div', null, 'section-heading'); header.append(make('h3', graph.title), make('span', graph.subtitle, 'muted'));
  const note = make('p', graph.note, 'callout'); const coverage = make('div', null, 'muted'); graph.coverage.forEach(c => coverage.append(make('p', c)));
  const controls = make('div', null, 'topology-controls'); const filter = make('input'); filter.type = 'search'; filter.placeholder = 'Find a resource or action…'; filter.setAttribute('aria-label', `Search ${graph.title}`);
  const previous = make('button', 'Previous', 'secondary'), next = make('button', 'Next', 'secondary'), count = make('span', '', 'muted'), download = make('button', 'Save diagram SVG', 'secondary');
  const visual = make('div', null, 'topology-canvas'); const details = make('details'); details.append(make('summary', 'Resources and relationships (full names)'));
  const list = make('div', null, 'topology-details'); details.append(list); controls.append(filter, previous, count, next, download);
  host.append(header, note, coverage, controls, visual, details);
  let page = 0, svg = '', currentUrl;
  const draw = () => {
    const q = filter.value.toLowerCase(); const found = graph.nodes.filter(n => `${n.label} ${n.id} ${n.type} ${n.status}`.toLowerCase().includes(q));
    const pages = Math.max(1, Math.ceil(found.length / 40)); page = Math.min(page, pages - 1); const slice = found.slice(page * 40, (page + 1) * 40);
    previous.disabled = page === 0; next.disabled = page + 1 >= pages;
    count.textContent = `${found.length} nodes · page ${page + 1}/${pages}. Lines connect nodes on this page.`;
    svg = graphSvg(slice, graph.edges, graph.title, `${graph.subtitle} · ${graph.note} · Filter: ${q || 'all'} · Page ${page+1}/${pages}`); if (currentUrl) URL.revokeObjectURL(currentUrl);
    const img = make('img'); img.alt = `${graph.title}. ${graph.note}`; currentUrl = URL.createObjectURL(new Blob([svg], {type:'image/svg+xml'})); img.src = currentUrl;
    img.onload = img.onerror = () => URL.revokeObjectURL(img.src); visual.replaceChildren(img);
    list.replaceChildren(); if (!slice.length) list.append(make('p', 'No matching nodes. Check collection coverage before interpreting absence.'));
    const ids = new Set(slice.map(n => n.id)); const labels = new Map(graph.nodes.map(n => [n.id, n.label]));
    for (const n of slice) list.append(make('p', `${n.status} · ${n.label} · ${n.type}\n${n.resourceId || n.id}\n${n.detail || ''}`));
    for (const e of graph.edges.filter(e => ids.has(e.from) || ids.has(e.to))) list.append(make('p', `${labels.get(e.from) || e.from} → ${labels.get(e.to) || e.to}: ${e.label}`, 'muted'));
  };
  filter.addEventListener('input', () => { page = 0; draw(); }); previous.addEventListener('click', () => { page--; draw(); }); next.addEventListener('click', () => { page++; draw(); });
  download.addEventListener('click', () => { const a = make('a'); const url = URL.createObjectURL(new Blob([svg], {type:'image/svg+xml'})); a.href = url; a.download = `topology-page-${page+1}.svg`; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000); });
  draw();
}
