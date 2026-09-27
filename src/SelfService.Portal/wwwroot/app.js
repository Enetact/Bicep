const $ = id => document.getElementById(id);
const make = (tag, text, cls) => { const node = document.createElement(tag); if (text != null) node.textContent = text; if (cls) node.className = cls; return node; };
let data, chosen, auth = { connected: [] }, ticket, reviewedProduct, loginTimer, runTimer;
const runs = [], diagramUrls = [];
async function api(path, body) {
  const res = await fetch('/api/' + path, { method: body === undefined ? 'GET' : 'POST', headers: { 'Content-Type': 'application/json', 'X-Portal-CSRF': data?.csrf ?? '' }, body: body === undefined ? undefined : JSON.stringify(body) });
  const text = await res.text(); const result = text ? JSON.parse(text) : {};
  if (!res.ok) throw new Error(result.error ?? `Request failed (${res.status}).`); return result;
}
function notice(text, error = false) { $('notice').textContent = text; $('notice').classList.toggle('error', error); $('notice').hidden = false; }
function action(id, fn) { $(id).addEventListener('click', async () => { const b = $(id); b.disabled = true; try { await fn(); } catch (e) { notice(e.message, true); } finally { b.disabled = false; updateGates(); } }); }
function page(name) { document.querySelectorAll('[data-view]').forEach(el => el.hidden = el.dataset.view !== name); document.querySelectorAll('[data-page]').forEach(el => el.classList.toggle('active', el.dataset.page === name)); $('page-title').textContent = document.querySelector(`[data-page="${name}"] span`).textContent; }
document.querySelectorAll('[data-page]').forEach(el => el.addEventListener('click', () => page(el.dataset.page)));
$('connection-shortcut').addEventListener('click', () => page('connections'));
$('theme').addEventListener('click', () => { const theme = document.documentElement.dataset.theme === 'dark' ? 'light' : 'dark'; document.documentElement.dataset.theme = theme; localStorage.setItem('studio-theme', theme); });
document.documentElement.dataset.theme = localStorage.getItem('studio-theme') === 'dark' ? 'dark' : 'light';
function options(select, values, label = v => v) { select.replaceChildren(...values.map(v => { const o = make('option', label(v)); o.value = typeof v === 'string' ? v : v.id; return o; })); }
function showProduct(product) {
  chosen = product; $('request').hidden = false; $('request-title').textContent = product.name;
  options($('environment'), product.targets.map(t => t.environment)); options($('region'), data.regions);
  $('operation').value = 'discover'; options($('discovery-run'), [{ id: '', name: 'Refresh runs to select saved discovery' }], v => v.name);
  selectionChanged(); $('request').scrollIntoView({ behavior: 'smooth', block: 'start' });
}
function selectionChanged() {
  if (!chosen) return;
  const t = chosen.targets.find(t => t.environment === $('environment').value);
  $('target-status').textContent = t.enabled ? 'Deployment enabled' : 'Preview available · deployment disabled';
  $('target-details').textContent = `${t.subscription} · ${t.network} · ${t.workload} / ${t.environment}`;
  $('operation').querySelector('[value="deploy"]').disabled = !t.enabled;
  if (!t.enabled && $('operation').value === 'deploy') $('operation').value = 'preview';
  $('handoff').hidden = $('operation').value === 'discover';
  $('action-note').textContent = $('operation').value === 'discover' ? 'Discover inventories the approved subscription and publishes a saved manifest and analysis report.' : $('operation').value === 'preview' ? 'Preview validates Bicep and reports planned changes. It does not deploy the workload. Temporary Azure What-If metadata may be written.' : 'This runs Preview followed by Deploy, using the selected discovery. ADO environment approvals and deployment guards still apply.';
  updateGates();
}
function updateGates() {
  if (!data) return;
  const waiting = auth.state === 'Waiting for Microsoft';
  $('connect-ado').disabled = !data.configured || waiting; $('connect-azure').disabled = !data.configured || waiting;
  $('subscriptions').disabled = !auth.connected.includes('azure');
  $('review').disabled = !chosen || !auth.connected.includes('ado') || ($('operation').value !== 'discover' && !$('discovery-run').value);
  $('refresh-discovery').disabled = !auth.connected.includes('ado'); $('cancel-login').hidden = !waiting;
}
async function refreshAuth() {
  auth = await api('auth'); $('account-name').textContent = auth.account ?? 'Not connected'; $('auth-state').textContent = auth.state;
  $('ado-state').textContent = auth.connected.includes('ado') ? 'Connected · delegated access' : 'Not connected';
  $('azure-state').textContent = auth.connected.includes('azure') ? 'Connected · delegated access' : 'Not connected';
  $('auth-detail').textContent = auth.error ?? (auth.state === 'Waiting for Microsoft' ? 'Your default browser is opening Microsoft sign-in. Complete account selection, consent and MFA there. This page will update automatically.' : auth.state === 'Connected' ? 'Connection complete. You can return to Workloads.' : 'Click Connect to open Microsoft sign-in. Cancelled or failed sign-in can be retried.');
  if (auth.state !== 'Waiting for Microsoft') { clearInterval(loginTimer); loginTimer = null; }
  updateGates();
}
for (const audience of ['ado', 'azure']) action('connect-' + audience, async () => { await api('auth/' + audience, {}); await refreshAuth(); if (!loginTimer && auth.state === 'Waiting for Microsoft') loginTimer = setInterval(() => refreshAuth().catch(e => { clearInterval(loginTimer); notice(e.message, true); }), 2000); });
action('cancel-login', async () => { await api('cancel-login', {}); await refreshAuth(); });
action('disconnect', async () => { await api('disconnect', {}); await refreshAuth(); });
action('subscriptions', async () => { const subscriptions = await api('subscriptions'); $('subscription-list').replaceChildren(...subscriptions.map(s => make('p', `${s.name} · ${s.state}`))); if (!subscriptions.length) $('subscription-list').textContent = 'No registered subscriptions are visible to this account.'; });
for (const id of ['environment', 'region', 'operation', 'discovery-run']) $(id).addEventListener('change', selectionChanged);
action('refresh-discovery', async () => { const found = await api('discovery/' + chosen.id); options($('discovery-run'), [{ id: '', name: 'Choose a matching discovery run' }, ...found], v => v.name + (v.finished ? ` · ${new Date(v.finished).toLocaleString()}` : '')); if (!found.length) notice('No successful main discovery runs from the last seven days were found. Run Discover first.'); });
action('review', async () => {
  const request = { product: chosen.id, environment: $('environment').value, region: $('region').value, operation: $('operation').value, discoveryRunId: Number($('discovery-run').value) || null };
  const review = await api('review', request); ticket = review.ticket; reviewedProduct = chosen.id;
  $('review-warning').textContent = review.warning; $('review-payload').textContent = JSON.stringify(review.payload, null, 2); $('confirmation').showModal();
});
$('dismiss').addEventListener('click', () => $('confirmation').close());
action('confirm', async () => {
  const current = ticket; ticket = null; $('confirmation').close();
  const run = await api('queue/' + current, {}); runs.unshift({ ...run, product: reviewedProduct, state: 'Queued' }); drawRuns(); page('activity');
  notice(`Run ${run.id} submitted to Azure DevOps.`); if (!runTimer) runTimer = setInterval(pollRuns, 15000);
});
function drawRuns() {
  $('runs').replaceChildren(...runs.map(run => { const row = make('div', null, 'run-row'); row.append(make('strong', `#${run.id}`), make('span', `${run.product} · ${run.state}${run.result ? ' · ' + run.result : ''}`)); const a = make('a', 'Open stages, reports & approvals ↗'); a.href = run.url; a.target = '_blank'; a.rel = 'noopener noreferrer'; row.append(a); return row; }));
}
let polling = false;
async function pollRuns() {
  if (polling) return; polling = true;
  try { for (const r of runs.filter(r => r.state !== 'completed')) Object.assign(r, await api(`runs/${r.product}/${r.id}`)); drawRuns(); if (runs.every(r => r.state === 'completed')) { clearInterval(runTimer); runTimer = null; } }
  catch (e) { clearInterval(runTimer); runTimer = null; notice(`${e.message} Open ADO to follow the run.`, true); }
  finally { polling = false; }
}
async function fileBase64(input) { const f = input.files[0]; if (!f || f.size > 16 * 1024 * 1024) throw new Error('Choose both JSON files, at most 16 MiB each.'); const bytes = new Uint8Array(await f.arrayBuffer()); let text = ''; for (let i = 0; i < bytes.length; i += 8192) text += String.fromCharCode(...bytes.subarray(i, i + 8192)); return btoa(text); }
action('analyze', async () => {
  const result = await api('analysis', { manifest: await fileBase64($('manifest')), inventory: await fileBase64($('inventory')) });
  $('analysis-result').hidden = false; $('analysis-text').textContent = result.markdown; $('analysis-location').textContent = `Saved to artifacts/portal-analysis/${result.id}/report. Offline evidence only. Showing ${result.diagrams.length} of ${result.diagramCount} diagram pages; all pages are in the saved report.`;
  diagramUrls.splice(0).forEach(URL.revokeObjectURL); $('diagrams').replaceChildren(...result.diagrams.map((svg, i) => { const img = make('img'); img.alt = `Observed and proposed resource containment, page ${i + 1}. Network reachability is unverified.`; const url = URL.createObjectURL(new Blob([svg], { type: 'image/svg+xml' })); diagramUrls.push(url); img.src = url; return img; }));
});
try {
  data = await api('bootstrap'); $('architecture').textContent = `Windows ${data.architecture} · ${data.packagedCatalog ? 'Packaged catalog' : 'Local checkout'}`; $('setup').hidden = data.configured; $('ado-scope').textContent = `${data.organization} / ${data.project}`;
  $('product-count').textContent = data.products.length; const all = data.products.flatMap(p => p.targets); $('target-count').textContent = `${all.filter(t => t.enabled).length} / ${all.length}`;
  $('products').replaceChildren(...data.products.map((p, i) => { const card = make('article', null, 'product-card'); const top = make('div', null, 'card-top'); top.append(make('div', i ? '⌁' : '⇄', 'card-icon'), make('span', i ? 'EVENT DRIVEN' : 'FILE TRANSFER', 'tag')); card.append(top, make('h2', p.name), make('p', p.summary, 'summary')); const details = make('details'); details.append(make('summary', 'Dependencies & estimated costs'), make('p', p.requirements), make('p', p.costs)); const button = make('button', 'Configure workload →', 'primary'); button.addEventListener('click', () => showProduct(p)); card.append(details, button); return card; }));
  $('skills').replaceChildren(...data.skills.map(s => { const card = make('article', null, 'panel'); const button = make('button', 'Read skill →', 'secondary'); button.addEventListener('click', async () => { try { const skill = await api('skills/' + s.id); $('skill-detail').hidden = false; $('skill-title').textContent = skill.id; $('skill-content').textContent = skill.content; $('skill-detail').scrollIntoView({ behavior: 'smooth' }); } catch (e) { notice(e.message, true); } }); card.append(make('h2', s.id), make('p', s.description), button); return card; }));
  await refreshAuth(); if (auth.state === 'Waiting for Microsoft') loginTimer = setInterval(() => refreshAuth().catch(e => notice(e.message, true)), 2000);
} catch (e) { notice(e.message, true); }
