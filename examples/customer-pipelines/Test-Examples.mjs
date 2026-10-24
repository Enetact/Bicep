import assert from 'node:assert/strict';
import { readFileSync } from 'node:fs';
import { createRequire } from 'node:module';
import { dirname, join } from 'node:path';
import { fileURLToPath } from 'node:url';

const require = createRequire(import.meta.url);
const yaml = require(process.argv[2] || 'yaml');
const root = dirname(fileURLToPath(import.meta.url));
const load = name => {
  const doc = yaml.parseDocument(readFileSync(join(root, name), 'utf8'), { uniqueKeys: true });
  assert.deepEqual(doc.errors, [], `Invalid YAML: ${name}`);
  return doc.toJS();
};
const reserved = ['studio_request_id', 'studio_plan_digest', 'studio_source_revision', 'studio_environment'];
const gh = load('github-actions.validate.yml');
assert.deepEqual(Object.keys(gh.on), ['workflow_dispatch']);
assert.deepEqual(gh.permissions, { contents: 'read' });
for (const name of reserved) assert.equal(gh.on.workflow_dispatch.inputs[name].required, true);
assert.deepEqual(Object.keys(gh.jobs), ['validate']);
assert.ok(gh.jobs.validate['runs-on'].includes('platform-studio-validation'));
const gs = gh.jobs.validate.steps;
assert.equal(gs.length, 3);
assert.ok(gs[0].run.includes('$env:STUDIO_TRIGGER_REVISION -cne $env:STUDIO_SOURCE_REVISION'));
assert.ok(gs[0].run.includes('$env:STUDIO_WORKFLOW_REVISION -cne $env:STUDIO_SOURCE_REVISION'));
assert.equal(gs[1].uses, 'actions/checkout@34e114876b0b11c390a56381ad16ebd13914f8d5');
assert.equal(gs[1].with.ref, '${{ inputs.studio_source_revision }}');
assert.equal(gs[1].with['persist-credentials'], false);
assert.equal(gs[1].with.submodules, false);
assert.equal(gs[1].with.lfs, false);
assert.equal(gs[2].run, '& ./examples/customer-pipelines/Validate-Bicep.ps1');
assert.equal(gh.jobs.validate.environment, undefined);

const ado = load('azure-devops.validate.yml');
assert.equal(ado.trigger, 'none');
assert.equal(ado.pr, 'none');
assert.equal(ado.schedules, undefined);
assert.equal(ado.resources, undefined);
assert.equal(ado.pool.name, 'PlatformStudioValidation');
for (const name of reserved) assert.equal(ado.parameters.filter(p => p.name === name).length, 1);
assert.equal(ado.jobs.length, 1);
const as = ado.jobs[0].steps;
assert.equal(as.length, 3);
assert.ok(as[0].pwsh.includes('$env:STUDIO_TRIGGER_REVISION -cne $env:STUDIO_SOURCE_REVISION'));
assert.equal(as[1].checkout, 'self');
assert.equal(as[1].persistCredentials, false);
assert.equal(as[1].submodules, false);
assert.equal(as[1].lfs, false);
assert.equal(as[2].pwsh, '& ./examples/customer-pipelines/Validate-Bicep.ps1');

const forbidden = /\baz\s+|AzureCLI@|AzurePowerShell@|azure\/login@|\bInvoke-(?:RestMethod|WebRequest)\b|\bcurl\s|\bterraform\s+apply\b|\bkubectl\s|id-token\s*:/i;
for (const step of [...gs, ...as]) {
  assert.equal(forbidden.test(step.run || step.pwsh || ''), false, 'Unexpected external command');
  assert.equal(step.task, undefined, 'Unexpected task capability');
}
const script = readFileSync(join(root, 'Validate-Bicep.ps1'), 'utf8');
assert.ok(script.includes('build (Join-Path $expectedRoot \'main.bicep\') --no-restore'));
assert.ok(script.includes('build-params (Join-Path $expectedRoot \'main.bicepparam\') --no-restore'));
assert.ok(script.includes('ls-files --error-unmatch'));
assert.ok(script.includes('ReparsePoint'));
assert.ok(script.includes('mock = $true'));
assert.ok(script.includes('deploymentVerified = $false'));
assert.equal(forbidden.test(script), false);
assert.equal(/\b(?:br|ts):/.test(readFileSync(join(root, 'main.bicep'), 'utf8')), false);
console.log('Customer pipeline examples: YAML, source guards, parameter contract and offline-only commands passed.');
