// Identity projection only. Called after the evidence pair and resource scope have been checked.
export function observedNames(inventory, validateId) {
  const rows = new Map();
  const add = (r, source) => {
    const key = validateId(r.id);
    if (rows.has(key)) { rows.get(key).evidenceRefs.push(source); return; }
    const parts = r.id.split('/'); let type = 'Microsoft.Resources/resourceGroups', names = [parts[4]], parent = '/' + parts.slice(1, 3).join('/');
    for (let i = 5; i < parts.length;) {
      if (parts[i].toLowerCase() !== 'providers') throw new Error('Unsupported resource identity in names evidence.');
      type = parts[i + 1]; names = []; i += 2;
      while (i < parts.length && parts[i].toLowerCase() !== 'providers') {
        parent = '/' + parts.slice(1, names.length ? i : i - 2).join('/');
        type += '/' + parts[i]; names.push(parts[i + 1]); i += 2;
      }
    }
    rows.set(key, { id: r.id, name: names.at(-1), fullName: names.join('/'), type, parentId: parent, status: 'Observed in saved discovery', evidenceRefs: [source] });
  };
  for (const [i, n] of (inventory.networks ?? []).entries()) {
    add(n, `inventory.json#/networks/${i}`);
    for (const [j, s] of (n.subnets ?? []).entries()) add(s, `inventory.json#/networks/${i}/subnets/${j}`);
  }
  for (const [i, r] of (inventory.privateDnsZones ?? []).entries()) add(r, `inventory.json#/privateDnsZones/${i}`);
  for (const [i, r] of (inventory.resources ?? []).entries()) add(r, `inventory.json#/resources/${i}`);
  if (rows.size > 5000) throw new Error('Resource names exceed the 5000-row evidence limit.');
  return [...rows.values()].sort((a, b) => a.id.toLowerCase() < b.id.toLowerCase() ? -1 : 1);
}
