export function setupPipelineRegistration({ api, data, notice }) {
  const $ = id => document.getElementById(id);
  const make = (tag, text, cls) => { const e = document.createElement(tag); if (text) e.textContent = text; if (cls) e.className = cls; return e; };
  let inventory, ticket, busy = false;
  const selected = () => [...$('pipeline-setup-list').querySelectorAll('input:checked')].map(e => e.value);
  function gates() { $('pipeline-setup-review').disabled = busy || !selected().length || !$('pipeline-setup-source').value; $('pipeline-setup-refresh').disabled = busy; $('pipeline-setup-all').disabled = busy || !inventory; }
  function draw(rows) {
    $('pipeline-setup-list').replaceChildren(...rows.map(row => {
      const card = make('article', null, 'panel'); const label = make('label'); const box = make('input'); box.type = 'checkbox'; box.value = row.yaml; box.disabled = row.status !== 'Missing'; box.checked = row.status === 'Missing'; box.addEventListener('change', gates);
      label.append(box, document.createTextNode(' ' + row.name)); card.append(label, make('p', `${row.category} · ${row.yaml}`, 'muted'), make('strong', row.status || 'Not checked'), make('p', row.detail || 'Connect Azure DevOps, then check registration.'));
      if (row.existingId) { const link = make('a', `View existing definition ${row.existingId} ↗`); link.href = `https://dev.azure.com/${encodeURIComponent(data.organization)}/${encodeURIComponent(data.project)}/_build?definitionId=${row.existingId}`; link.target = '_blank'; link.rel = 'noopener noreferrer'; card.append(link); }
      return card;
    })); gates();
  }
  api('pipeline-setup/catalog').then(c => { $('pipeline-setup-scope').textContent = `${data.organization} / ${data.project} · ${c.repository} · ${c.branch} · ${c.entries.length} pipeline entry files`; draw(c.entries); }).catch(e => notice(e.message, true));
  $('pipeline-setup-source').addEventListener('change', gates);
  $('pipeline-setup-all').addEventListener('click', () => { for (const box of $('pipeline-setup-list').querySelectorAll('input')) box.checked = !box.disabled; gates(); });
  $('pipeline-setup-refresh').addEventListener('click', async () => {
    busy = true; gates(); $('pipeline-setup-status').textContent = 'Reading ADO definitions and the public GitHub main tree…';
    inventory = null; ticket = null;
    try {
      const found = await api('pipeline-setup/inventory'); inventory = found;
      $('pipeline-setup-source').replaceChildren(make('option', 'Choose an existing GitHub-connected pipeline'), ...found.sources.map(s => { const o = make('option', `${s.name} · ID ${s.id} · queue ${s.queueId}`); o.value = s.id; return o; }));
      $('pipeline-setup-source').options[0].value = '';
      if (found.sources.length === 1) $('pipeline-setup-source').value = found.sources[0].id;
      $('pipeline-setup-status').textContent = `Checked GitHub commit ${found.commit}. ${found.pipelines.filter(p => p.status === 'Missing').length} missing; ${found.pipelines.filter(p => p.status === 'Existing').length} existing. ${found.sources.length ? 'Select missing entries, then review.' : 'A platform administrator must first connect this GitHub repository to one ADO pipeline. No connection or access is granted automatically.'}`;
      draw(found.pipelines);
    } catch (e) { draw([]); $('pipeline-setup-status').textContent = e.message; notice(e.message, true); }
    finally { busy = false; gates(); }
  });
  $('pipeline-setup-review').addEventListener('click', async () => {
    busy = true; gates();
    try {
      const result = await api('pipeline-setup/review', {sourcePipelineId: Number($('pipeline-setup-source').value), pipelines: selected()});
      ticket = result.ticket; $('pipeline-setup-warning').textContent = result.warning;
      $('pipeline-setup-selected').replaceChildren(...result.payloads.map(p => make('li', `${p.name} — ${p.process.yamlFilename}`)));
      $('pipeline-setup-payload').textContent = JSON.stringify({organization:result.organization,project:result.project,repository:result.repository,commit:result.commit,source:result.seed,definitions:result.payloads},null,2);
      $('pipeline-setup-confirmation').showModal();
    } catch (e) { notice(e.message, true); }
    finally { busy = false; gates(); }
  });
  const cancel = () => { ticket = null; $('pipeline-setup-confirmation').close(); };
  $('pipeline-setup-back').addEventListener('click', cancel);
  $('pipeline-setup-confirmation').addEventListener('cancel', () => {ticket = null;});
  $('pipeline-setup-confirm').addEventListener('click', async () => {
    if (!ticket || busy) return;
    const submitted = ticket; ticket = null; busy = true; gates(); $('pipeline-setup-confirm').disabled = true;
    $('pipeline-setup-status').textContent = 'Registering reviewed definitions. No runs are queued. Keep this page open for the receipt.';
    try {
      const result = await api('pipeline-setup/apply/' + submitted, {});
      $('pipeline-setup-receipt').textContent = JSON.stringify(result,null,2); $('pipeline-setup-result').hidden = false;
      $('pipeline-setup-status').textContent = `${result.status}. Receipt: ${result.path}. Refresh registration before another request. Pipeline resource permissions and approvals remain separately configured.`;
    } catch (e) { $('pipeline-setup-status').textContent = `${e.message} Refresh ADO inventory before retrying; some definitions may already exist. Receipts are under artifacts/pipeline-registration.`; notice($('pipeline-setup-status').textContent, true); }
    finally { inventory = null; draw([]); $('pipeline-setup-confirmation').close(); $('pipeline-setup-confirm').disabled = false; busy = false; gates(); }
  });
  $('pipeline-setup-download').addEventListener('click', () => { const url = URL.createObjectURL(new Blob([$('pipeline-setup-receipt').textContent],{type:'application/json'})); const a = make('a'); a.href = url; a.download = 'pipeline-registration-receipt.json'; a.click(); setTimeout(()=>URL.revokeObjectURL(url),1000); });
  gates();
}
