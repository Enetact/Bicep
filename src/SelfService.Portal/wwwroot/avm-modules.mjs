const names = {
  'avm-identity-user-assigned-identity': 'User-assigned identity',
  'avm-containers-container-registry': 'Private container registry',
  'avm-configuration-app-configuration': 'Private App Configuration',
  'avm-monitoring-application-insights': 'Private Application Insights',
  'avm-network-network-security-group': 'Network security group',
  'avm-network-route-table': 'Route table'
};

// This view describes curated local source contracts. It neither resolves values nor invokes an agent.
export function moduleOptions(catalog) {
  return (catalog?.modules ?? []).filter(module => module.draftSelectable).map(module => {
    const parameters = Object.entries(module.parameters ?? {});
    return {
      id: module.id,
      name: names[module.id] ?? String(module.id).split('-').map(word => word.charAt(0).toUpperCase() + word.slice(1)).join(' '),
      origin: module.origin?.kind === 'AzureVerifiedModule' ? `Azure Verified Module · ${module.origin.version}` : 'Local module',
      required: parameters.filter(([, schema]) => !Object.hasOwn(schema, 'defaultValue')).map(([name]) => name),
      children: parameters.filter(([, schema]) => Object.hasOwn(schema, 'defaultValue') &&
        (schema.type === 'array' && schema.items?.type === 'object' || schema.type === 'object' && schema.properties)).map(([name]) => name),
      schema: JSON.stringify(module.parameters ?? {}, null, 2)
    };
  }).sort((left, right) => left.name.localeCompare(right.name));
}

export function renderModuleOptions(container, catalog) {
  const document = container.ownerDocument, modules = moduleOptions(catalog);
  const element = (tag, text) => { const node = document.createElement(tag); if (text !== undefined) node.textContent = text; return node; };
  const summary = element('p', `${modules.length} modules available for source drafts. Complete names and settings in the downloaded parameter files. These options do not create resources.`);
  const cards = modules.map(module => {
    const details = element('details');
    details.append(element('summary', `${module.name} · ${module.origin}`));
    details.append(element('p', `Required settings: ${module.required.join(', ') || 'None'}.`));
    details.append(element('p', `Optional child options: ${module.children.join(', ') || 'None'}.`));
    const schema = element('details'); schema.append(element('summary', 'Parameter rules and defaults'));
    const source = element('pre', module.schema); source.className = 'document'; schema.append(source);
    details.append(schema); return details;
  });
  container.replaceChildren(summary, ...cards);
  return modules.length;
}
