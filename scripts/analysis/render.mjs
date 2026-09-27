// Output contains projected labels and static markup only; never raw Azure properties.
import { canonical } from './core.mjs';
const xml = s => String(s).replace(/[&<>"']/g, c => ({ '&': '&amp;', '<': '&lt;', '>': '&gt;', '"': '&quot;', "'": '&apos;' }[c]));
const md = s => String(s).replace(/[\r\n\t]/g, ' ').replace(/[|<>\[\]`]/g, c => `&#${c.charCodeAt(0)};`);
const label = n => `${n.label} | ${n.states.join('+')} | ${n.action}`;

export function renderAnalysis(report) {
  const files = new Map();
  files.set('analysis.json', canonical(report) + '\n');
  const nodes = report.graph.nodes;
  const lines = ['# Self-service saved-evidence analysis', '', `Workload: **${md(report.context.workload)} / ${md(report.context.environment)}** (${report.context.workloadType}).`, '',
    `Assessment: **${report.status}**. Provenance: **UnverifiedOffline**. **This report does not authorize deployment.**`, '',
    `Collected: ${report.context.generatedUtc}. Evaluated: ${report.context.evaluatedUtc}.`, '',
    '## Coverage', '', '| Collection | Saved query evidence | Count |', '|---|---|---|',
    ...report.coverage.map(c => `| ${md(c.collection)} | ${c.state} | ${c.count} |`), '',
    'A successful empty collection means none reported in that collection and scope. Unknown means absence cannot be established.', '',
    '## Resources and reported prerequisite actions', '', '| Resource | Evidence view | Reported action | Ownership | Runtime |', '|---|---|---|---|---|',
    ...nodes.map(n => `| ${md(n.resourceId)} | ${n.states.join(', ')} | ${n.action} | ${n.ownership} | ${n.runtime} |`), '',
    '## Topology', '', 'Solid lines mean observed containment; dashed lines mean proposed containment. Neither means network connectivity. Proposed resources are not deployed. Diagrams are split into pages; cross-page relationships remain in analysis.json.', ''];
  if (!nodes.length) lines.push('No resource nodes were available. Read collection status before interpreting this as an empty environment.', '');
  for (let start = 0; start < nodes.length; start += 30) {
    const page = nodes.slice(start, start + 30); const number = start / 30 + 1;
    const ids = new Set(page.map(n => n.id));
    const edges = report.graph.edges.filter(e => ids.has(e.from) && ids.has(e.to));
    const mermaid = ['flowchart TB', ...page.map(n => `    ${n.id}["${xml(label(n))}"]`), ...edges.map(e => `    ${e.from} ${e.state === 'Observed' ? '-->' : '-.->'}|contains| ${e.to}`)].join('\n') + '\n';
    files.set(`topology-${number}.mmd`, mermaid);
    lines.push(`### Topology page ${number}`, '', `![Static topology page ${number}](topology-${number}.svg)`, '', '```mermaid', mermaid.trimEnd(), '```', '');
    const height = 115 + page.length * 76;
    const position = new Map(page.map((n, i) => [n.id, 100 + i * 76]));
    const svg = [`<svg xmlns="http://www.w3.org/2000/svg" width="1100" height="${height}" viewBox="0 0 1100 ${height}" role="img" aria-label="Resource containment; runtime unverified">`, '<rect width="100%" height="100%" fill="#f1f5f9"/>', '<text x="30" y="35" font-family="sans-serif" font-size="22" fill="#0f172a">Saved topology / no runtime verification</text>', `<text x="30" y="62" font-family="sans-serif" font-size="14">${xml(report.context.workload)} / ${xml(report.context.environment)} / page ${number}</text>`];
    for (const e of edges) svg.push(`<path d="M 66 ${position.get(e.from) + 25} H 25 V ${position.get(e.to) + 25} H 66" fill="none" stroke="#64748b" stroke-width="2" ${e.state === 'Proposed' ? 'stroke-dasharray="5 4"' : ''}/>`);
    for (const n of page) {
      const y = position.get(n.id); const color = n.action === 'Blocked' ? '#fee2e2' : n.states.includes('Proposed') ? '#fef3c7' : '#dbeafe';
      svg.push(`<rect x="66" y="${y}" width="1000" height="60" rx="8" fill="${color}" stroke="#94a3b8"/><text x="82" y="${y + 23}" font-family="sans-serif" font-size="16">${xml(label(n).slice(0, 105))}</text><text x="82" y="${y + 45}" font-family="sans-serif" font-size="12" fill="#334155">${xml(n.type.slice(0, 115))} / Runtime: Unverified</text>`);
    }
    svg.push('</svg>'); files.set(`topology-${number}.svg`, svg.join('\n') + '\n');
  }
  lines.push('## Findings', '', '| Rule | Outcome | Required | Explanation |', '|---|---|---|---|', ...report.findings.map(f => `| ${md(f.ruleId)} | ${f.outcome} | ${f.required} | ${md(f.message)} |`), '', '## Limits and next step', '', ...report.limitations.map(l => `- ${l}`), '', 'Use the existing Deploy Preview and authenticated discovery handoff for deployment decisions. Correct blockers and collect missing evidence; do not use this report as a substitute for approval.', '');
  files.set('README.md', lines.join('\n'));
  return files;
}
