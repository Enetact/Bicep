import assert from 'node:assert/strict';
import test from 'node:test';
import fs from 'node:fs';
import { createRequire } from 'node:module';
const YAML = createRequire(new URL('../infrastructure/package.json', import.meta.url))('yaml');
import { csvCell } from '../../src/SelfService.Portal/wwwroot/tagging.mjs';
test('Tag CSV prevents formula execution and quotes values',()=>{for(const v of ['=1+1','+SUM(A1)','-2+3','@cmd','\t=1'])assert.ok(csvCell(v).startsWith('"\''));assert.equal(csvCell('quoted "tag"'),'"quoted ""tag"""');});
test('Tag roots have manual defaults and an approval environment',()=>{
 const discover=YAML.parse(fs.readFileSync('azure-pipelines-tags-discover.yml','utf8'));
 const changes=YAML.parse(fs.readFileSync('azure-pipelines-tags.yml','utf8'));
 for(const p of [discover,changes]){assert.equal(p.trigger,'none');assert.equal(p.pr,'none');assert.equal(p.pool.vmImage,'windows-latest');assert.equal(p.schedules,undefined);}
 assert.equal(changes.parameters.find(p=>p.name==='operation').default,'Preview only');assert.equal(changes.stages[0].stage,'Preview');
 const conditional=changes.stages[1]["${{ if eq(parameters.operation, 'Preview and apply') }}"];
 assert.deepEqual(conditional.map(s=>s.stage),['Apply','Verify']);assert.equal(conditional[0].jobs[0].environment,'platform-tag-governance');assert.equal(changes.lockBehavior,'sequential');
});
test('All compositions and wrappers carry source custom tags',()=>{
 for(const dir of fs.readdirSync('workloads',{withFileTypes:true}).filter(x=>x.isDirectory())){
  const base=`workloads/${dir.name}`;if(!fs.existsSync(`${base}/stack.bicep`))continue;
  for(const f of ['main.bicep','stack.bicep']){const s=fs.readFileSync(`${base}/${f}`,'utf8');assert.match(s,/param customTags object = \{\}/);assert.match(s,/union\(customTags, \{/);if(f==='stack.bicep')assert.match(s,/customTags: customTags/);}
 }
});
