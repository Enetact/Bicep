import { validateInstances } from '../naming/instances.mjs';
import { workloadNames, supportedWorkloads } from '../naming/workload.mjs';

// Select only source-reviewed drafts matching this discovery scope. Never adopt observed resources by name.
export function discoveryNamingDrafts(report, manifest, registry, policy, catalog) {
  const validation = validateInstances(registry, policy, catalog);
  const selected = registry.instances.filter(e => e.workloadType === report.context.workloadType && e.intent.environment === report.context.environment &&
    e.intent.subscriptionId.toLowerCase() === report.context.subscriptionId.toLowerCase() && e.intent.region === manifest.selection?.region);
  if (selected.length > 32) throw new Error('Discovery naming report exceeds 32 matching drafts; narrow the reviewed registry scope.');
  const drafts = [], pending = [];
  for (const entry of selected) {
    if (supportedWorkloads.includes(entry.workloadType)) drafts.push(workloadNames({workloadType:entry.workloadType,intent:entry.intent},policy,catalog));
    else pending.push({ instanceId:entry.intent.instanceId, workloadType:entry.workloadType, reason:'Workload naming bindings are not qualified.' });
  }
  return { schema:'platform.discovery-naming-drafts/v1', context:report.context, registrySha256:validation.registrySha256,
    policySha256:validation.policySha256, catalogSha256:validation.catalogSha256, drafts, pending,
    deploymentAuthorized:false, coverage:'Calculated source drafts matching this saved workload/environment/subscription/region. They are not observed resources, approved targets or Azure What-If. Empty means no matching qualified draft; it does not mean no resources exist.' };
}

const md = value => String(value).replace(/[\r\n\t]/g,' ').replace(/[|<>\[\]`]/g,c=>`&#${c.charCodeAt(0)};`);
export function draftSummary(report) {
  const lines = ['','## Proposed names generated from the reviewed draft registry','',report.coverage,''];
  if (!report.drafts.length) lines.push('No qualified source naming draft matches this discovery. No proposed names were invented.','');
  for (const d of report.drafts) {
    lines.push(`### ${md(d.plan.intent.stackDisplayName)} (${d.plan.intent.instanceId})`,'',`Draft SHA-256: ${d.draftSha256}`,'','| Calculated name | Type | State |','|---|---|---|',
      ...d.plan.resources.map(r=>`| ${md(r.fullName ?? 'Unresolved')} | ${md(r.armType)} | ${md(r.status)} |`),'');
  }
  for(const p of report.pending)lines.push(`- ${p.instanceId}: ${p.reason}`);
  return lines.join('\n')+'\n';
}
