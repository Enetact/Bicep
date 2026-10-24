import { renderTopology } from './topology.mjs';
const el = (tag, text) => { const node = document.createElement(tag); if (text != null) node.textContent = text; return node; };
const save = (value, file) => { const url = URL.createObjectURL(new Blob([JSON.stringify(value, null, 2)], { type: 'application/json' })); const a = el('a'); a.href = url; a.download = file; a.click(); setTimeout(() => URL.revokeObjectURL(url), 1000); };

export function namingGraph(draft) {
  if (draft?.schema !== 'platform.workload-naming-draft/v1' || draft.deploymentAuthorized !== false) throw new Error('Invalid local naming draft.');
  return { title: 'Calculated naming draft', subtitle: `${draft.workloadType} · ${draft.plan.intent.stackDisplayName}`, note: draft.coverage,
    coverage: ['Not discovery or Azure What-If. Availability, ownership and shared prerequisites are unverified.'],
    nodes: draft.plan.resources.map(r => ({ id: r.componentKey, resourceId: r.resourceId, label: r.name ?? 'Unresolved', type: r.armType, status: r.status, detail: r.reason, level: r.parentKey ? 1 : 0 })),
    edges: draft.plan.resources.filter(r => r.parentKey).map(r => ({ from: r.parentKey, to: r.componentKey, label: 'proposed scope' })) };
}

export function renderResourceNames(host, rows, title, evidence) {
  host.replaceChildren(); host.classList.add('resource-names'); host.append(el('h3', title), el('p', evidence));
  const search = el('input'); search.type = 'search'; search.placeholder = 'Filter resource names, types or scopes'; search.setAttribute('aria-label', 'Filter resource names');
  const count = el('p'), table = el('table'), head = el('thead'), header = el('tr'), body = el('tbody');
  for (const label of ['Name / full child name', 'Resource type', 'State', 'Resource ID / parent scope']) { const th = el('th', label); th.scope = 'col'; header.append(th); }
  head.append(header); table.append(head, body); const download = el('button', 'Download resource names JSON'); download.type = 'button'; download.className = 'secondary'; download.onclick = () => save({ title, evidence, resources: rows }, 'resource-names.json');
  const scroll = el('div'); scroll.className = 'resource-names-scroll'; scroll.tabIndex = 0; scroll.setAttribute('role','region'); scroll.setAttribute('aria-label',title); scroll.append(table);
  host.append(search, download, count, scroll);
  const draw = () => {
    const query = search.value.toLowerCase(), found = rows.filter(r => [r.fullName, r.name, r.armType ?? r.type, r.resourceId ?? r.id, r.scope ?? r.parentId, r.status].join(' ').toLowerCase().includes(query));
    body.replaceChildren(); count.textContent = `${found.length} matching records. Showing up to 200; download retains all records. Empty or unresolved evidence does not mean a resource should be created.`;
    for (const r of found.slice(0, 200)) { const tr = el('tr'); for (const value of [r.fullName ?? r.name ?? 'Unresolved', r.armType ?? r.type, r.status, [r.resourceId ?? r.id, r.scope ?? r.parentId].filter(Boolean).join('\n')]) tr.append(el('td', value)); body.append(tr); }
  };
  search.oninput = draw; draw();
}

// This editor never modifies the deployment form/request. It calculates explicit local drafts only.
// Drafts live only in this browser view. A session/identity reset discards all input and invalidates pending responses.
export function createNamingDrafts(uuid = () => crypto.randomUUID()) {
  const drafts = new Map(); let generation = 0;
  const key = (product, target, region) => JSON.stringify([product.id, product.type, target.environment, target.subscriptionId.toLowerCase(), target.workload, target.network, region]);
  return {
    get generation() { return generation; },
    select(product, target, region) {
      const id = key(product, target, region);
      if (!drafts.has(id)) {
        if (drafts.size >= 32) drafts.delete(drafts.keys().next().value);
        drafts.set(id, { stackDisplayName:'', stackAcronym:'', instanceId:uuid(), instanceOrdinal:'01', subscriptionId:target.subscriptionId, namespaceSalt:'', previewInstance:'legacy' });
      }
      return drafts.get(id);
    },
    newDraft(product, target, region) { drafts.delete(key(product, target, region)); generation++; },
    reset() { drafts.clear(); generation++; }
  };
}
const hostDrafts = new WeakMap(), hostVersions = new WeakMap();
export function renderNamingEditor(host, api, product, target, region, drafts, redraw) {
  if (!drafts) { if (!hostDrafts.has(host)) hostDrafts.set(host, createNamingDrafts()); drafts = hostDrafts.get(host); }
  const view = (hostVersions.get(host) ?? 0) + 1; hostVersions.set(host, view);
  host.replaceChildren(); const title = el('h3', 'New stack naming draft'); host.append(title);
  if (!['managed-devops-pool','fabric','private-storage','key-vault','observability','http-functions','service-bus-worker','private-aks','private-data-platform','logic-app-event-grid','blob-transfer'].includes(product.type)) { host.append(el('p', 'Concrete naming maps for this workload are still being qualified. Existing deployment naming is unchanged.')); return; }
  host.append(el('p', 'Local calculation only. Discovery supplies observed names; a fresh Azure Preview is required for planned changes. Save this draft for source review. Only source-registered instances can be selected for ADO Preview.'));
  const values = drafts.select(product, target, region), generation = drafts.generation, fields = {};
  for (const [key, label] of [['stackDisplayName','Stack display name'], ['stackAcronym','Stack acronym (2–4 lowercase letters/numbers)'], ['instanceId','New instance UUID'], ['instanceOrdinal','Instance ordinal (01–99)'], ['subscriptionId','Subscription ID for this draft'], ['namespaceSalt','Persistent collision salt (optional)']]) {
    const wrapper = el('label', label), input = el('input'); input.value = values[key]; input.name = key; input.autocomplete = 'off'; fields[key] = input; wrapper.append(input); host.append(wrapper);
  }
  host.append(el('p', `Environment: ${target.environment} · Region: ${region} · Exposure: internal`));
  const button = el('button', 'Calculate names'), message = el('p'), result = el('div'), diagram = el('div'); button.type = 'button'; message.setAttribute('role','status');
  const reset = el('button', 'Start new naming draft'); reset.type = 'button'; reset.className = 'secondary';
  reset.onclick = () => { drafts.newDraft(product, target, region); if (redraw) redraw(); else renderNamingEditor(host, api, product, target, region, drafts); };
  host.append(button, reset, message, result, diagram); let version = 0;
  const currentView = () => drafts.generation === generation && hostVersions.get(host) === view && host.isConnected;
  const clear = () => { version++; result.replaceChildren(); diagram.replaceChildren(); message.textContent = 'Settings changed; calculate a new draft.'; };
  for (const [key, input] of Object.entries(fields)) input.addEventListener('input', () => { if (!currentView()) return; values[key] = input.value; clear(); });
  button.onclick = async () => {
    const current = ++version; result.replaceChildren(); diagram.replaceChildren(); button.disabled = true; message.textContent = 'Calculating locally…';
    try {
      const draft = await api('naming/draft', { workloadType: product.type, intent: { namingProfile:'standard-v2', environment:target.environment, region, exposure:'internal', ...Object.fromEntries(Object.entries(fields).map(([k,v]) => [k,v.value])) } });
      if (current !== version || !currentView()) return;
      renderResourceNames(result, draft.plan.resources, 'Calculated names — not observed', draft.coverage); renderTopology(diagram, namingGraph(draft));
      const download = el('button','Download draft and parameter patch'); download.type='button'; download.className='secondary'; download.onclick=()=>save(draft,'workload-naming-draft.json'); result.prepend(download);
      message.textContent = `Draft ${draft.draftSha256}. No registration, discovery, What-If or deployment was started.`;
    } catch (e) { if (current === version && currentView()) message.textContent = e.message; }
    finally { button.disabled = false; }
  };
}
