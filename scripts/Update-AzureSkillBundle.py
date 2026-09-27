"""Index the vendored Azure Skills snapshot. Requires PyYAML 6.0.3; no Azure calls."""
import hashlib
import json
from pathlib import Path
import yaml

ROOT = Path(__file__).resolve().parents[1] / 'vendor' / 'azure-skills'
COMMIT = '117b038edfef5d7af09848b8ffcd355f28f19956'
GROUPS = {
    'network': ['azure-enterprise-infra-planner', 'azure-resource-visualizer', 'azure-resource-lookup'],
    'compute': ['azure-compute', 'python-appservice-deploy'],
    'kubernetes': ['azure-kubernetes', 'airunway-aks-setup'],
    'storage': ['azure-storage'], 'ai': ['azure-ai', 'microsoft-foundry', 'azure-aigateway'],
    'messaging': ['azure-messaging'],
    'monitoring': ['appinsights-instrumentation', 'azure-diagnostics', 'azure-reliability'],
    'data': ['azure-kusto'],
}
entries = []
for file in sorted(ROOT.rglob('SKILL.md')):
    rel = file.relative_to(ROOT).as_posix()
    text = file.read_text(encoding='utf-8-sig')
    if text.startswith('---'):
        front = yaml.safe_load(text.split('---', 2)[1])
        name, description = str(front['name']), str(front['description'])
    else:
        # Three upstream onboarding sub-workflows intentionally have no front matter.
        name = 'azure-app-onboard-' + file.parent.name
        description = 'Nested Azure app onboarding workflow: ' + file.parent.name + '. Read its bundled guidance for prerequisites and execution boundaries.'
    family = rel.split('/')[1] if rel.startswith('skills/') else 'azure-cost'
    stem = rel.removeprefix('skills/').removesuffix('/SKILL.md') if rel.startswith('skills/') else 'azure-cost/' + file.parent.name
    entries.append(dict(id='azure--' + stem.replace('/', '--'), name=name, description=description,
                        path=rel, family=family, discoveryProfile=next((p for p, names in GROUPS.items() if family in names), 'inventory'),
                        pipelineStatus='No pipeline associated yet'))
files = {f.relative_to(ROOT).as_posix(): hashlib.sha256(f.read_bytes()).hexdigest()
         for f in sorted(ROOT.rglob('*')) if f.is_file() and f.name != 'bundle.json'}
manifest = dict(schemaVersion=1, repository='https://github.com/microsoft/azure-skills', commit=COMMIT,
                bundledUtc='2026-09-27', topLevelSkillDirectories=28, skillDefinitions=len(entries), skills=entries, files=files)
(ROOT / 'bundle.json').write_text(json.dumps(manifest, indent=2) + '\n', encoding='utf-8')
print(f'Indexed {len(entries)} skills and {len(files)} source files.')
