// Recovery display never queues, approves or executes a workflow.
export function recoveryView(catalog, id) {
  if (catalog?.schemaVersion !== 1 || catalog.executionEnabled !== false) return null;
  const policy = catalog.policies?.find(p => p.id === id);
  if (!policy || !['ConfigurationRestore','ApplicationRestore','TagCompensation','SourceRevert','ManualRecovery','NotApplicable'].includes(policy.mode)) return null;
  const actionModes = ['ConfigurationRestore','ApplicationRestore','TagCompensation'];
  const checks = actionModes.includes(policy.mode) ? Object.entries(catalog.checks).filter(([key]) =>
    (!['packageCompatible','processingImpactReviewed'].includes(key) || policy.mode === 'ApplicationRestore') &&
    (!['tagKeysOwned','tagCurrentMatchesApplied'].includes(key) || policy.mode === 'TagCompensation')).map(([, value]) => value) : [];
  return { ...policy, checks, canExecute: false, status: policy.mode === 'NotApplicable' ? 'No cloud change to restore' :
    policy.mode === 'ManualRecovery' ? 'Platform review required' : policy.mode === 'SourceRevert' ? 'Use the source change workflow' : 'Rules defined · restore execution unavailable' };
}
export function renderRecovery(container, catalog, id) {
  container.replaceChildren();
  const add = (tag, text, parent = container) => { const el = document.createElement(tag); el.textContent = text; parent.append(el); return el; };
  const p = recoveryView(catalog, id);
  if (!p) { add('p', 'Recovery rules unavailable. This does not authorize restoration.'); return; }
  add('h3', `${p.name} · ${p.status}`); add('p', p.summary);
  add('p', 'This is a policy, not an assessment of a saved run. No recovery executor is registered.');
  add('h4', 'Outside this recovery policy'); const exclusions = add('ul', ''); p.exclusions.forEach(x => add('li', x, exclusions));
  if (p.checks.length) { const details = add('details', ''); add('summary', 'Required evidence and checks', details); const list = add('ul', '', details); p.checks.forEach(x => add('li', x, list)); }
}
