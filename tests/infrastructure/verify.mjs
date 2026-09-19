import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import { spawnSync } from 'node:child_process';
import YAML from 'yaml';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const read=name=>fs.readFileSync(path.join(root,name),'utf8');
const json=name=>JSON.parse(read(name));
const documents=new Map();
const cases=[];
function check(name, action){action();cases.push({name,passed:true});}
function inside(file){const relative=path.relative(root,file);assert(!relative.startsWith('..')&&!path.isAbsolute(relative),'Path escaped repository: '+file);return relative.replaceAll('\\','/');}
function document(name){
  if(!documents.has(name)){
    const parsed=YAML.parseDocument(read(name),{uniqueKeys:true});
    assert.equal(parsed.errors.length,0,name+': '+parsed.errors.join('; '));
    documents.set(name,parsed.toJS());
  }
  return documents.get(name);
}
function walk(value, action){if(!value||typeof value!=='object')return;action(value);Object.values(value).forEach(x=>walk(x,action));}
function files(directory){return fs.readdirSync(path.join(root,directory),{withFileTypes:true}).flatMap(x=>x.isDirectory()?files(path.join(directory,x.name)):[path.join(directory,x.name).replaceAll('\\','/')]);}
const yamlFiles=[...fs.readdirSync(root).filter(x=>/\.ya?ml$/.test(x)),...files('pipelines').filter(x=>/\.ya?ml$/.test(x))];

// Deliberately bounded ADO expression subset. Unknown expressions fail rather than being evaluated as JavaScript.
function expression(source, parameters){
  const displayId=source.trim().match(/^split\(parameters\.([A-Za-z0-9_]+), ' \| '\)\[1\]$/);
  if(displayId){assert(Object.hasOwn(parameters,displayId[1]));return parameters[displayId[1]].split(' | ')[1];}
  let remaining=source.trim();
  function parse(){
    remaining=remaining.trimStart();let match;
    if((match=remaining.match(/^'((?:[^']|'')*)'/))){remaining=remaining.slice(match[0].length);return match[1].replaceAll("''","'");}
    if((match=remaining.match(/^parameters\.([A-Za-z0-9_]+)/))){remaining=remaining.slice(match[0].length);assert(Object.hasOwn(parameters,match[1]),'Unknown parameter '+match[1]);return parameters[match[1]];}
    if((match=remaining.match(/^(true|false)\b/))){remaining=remaining.slice(match[0].length);return match[1]==='true';}
    match=remaining.match(/^(eq|and|or|not)\(/);assert(match,'Unsupported expression: '+remaining);
    remaining=remaining.slice(match[0].length);const args=[parse()];
    while(remaining.trimStart().startsWith(',')){remaining=remaining.trimStart().slice(1);args.push(parse());}
    remaining=remaining.trimStart();assert(remaining.startsWith(')'),'Unclosed expression');remaining=remaining.slice(1);
    switch(match[1]){case 'eq':assert.equal(args.length,2);return args[0]===args[1];case 'and':return args.every(Boolean);case 'or':return args.some(Boolean);case 'not':assert.equal(args.length,1);return !args[0];}
  }
  const result=parse();assert.equal(remaining.trim(),'','Trailing expression');return result;
}
function substitute(value, parameters){
  if(typeof value==='string'){
    const exact=value.match(/^\$\{\{\s*(.*?)\s*\}\}$/);if(exact)return expression(exact[1],parameters);
    return value.replace(/\$\{\{\s*(.*?)\s*\}\}/g,(_,e)=>String(expression(e,parameters)));
  }
  if(Array.isArray(value))return value.map(x=>substitute(x,parameters));
  if(value&&typeof value==='object')return Object.fromEntries(Object.entries(value).map(([k,v])=>[k,substitute(v,parameters)]));
  return value;
}
function bind(doc, supplied){
  const declared=doc.parameters||[];const result={};
  for(const key of Object.keys(supplied))assert(declared.some(p=>p.name===key),'Undeclared template parameter '+key);
  for(const p of declared){assert(Object.hasOwn(supplied,p.name)||Object.hasOwn(p,'default'),'Missing template parameter '+p.name);const value=Object.hasOwn(supplied,p.name)?supplied[p.name]:p.default;assert.equal(typeof value,p.type==='boolean'?'boolean':'string',p.name);if(p.values)assert(p.values.includes(value),'Disallowed value '+p.name);result[p.name]=value;}
  return result;
}
function expand(name, supplied={}, ancestry=[], section='stages'){
  assert(!ancestry.includes(name),'Template cycle: '+name);const doc=document(name);const parameters=bind(doc,supplied);
  const referenced=(item,kind)=>expand(inside(path.resolve(root,path.dirname(name),item.template)),substitute(item.parameters||{},parameters),[...ancestry,name],kind);
  if(doc.extends)return referenced(doc.extends,section);
  function content(value){
    if(Array.isArray(value))return value.map(content);
    if(value&&typeof value==='object')return Object.fromEntries(Object.entries(value).map(([key,item])=>[key,['steps','jobs'].includes(key)?items(item,key):content(item)]));
    return substitute(value,parameters);
  }
  function items(values,kind){return values.flatMap(item=>{
    const keys=Object.keys(item);if(keys.length===1&&keys[0].startsWith('${{')){
      const condition=keys[0].match(/^\$\{\{\s*if\s+(.*?)\s*\}\}$/);assert(condition,'Unsupported template directive');return expression(condition[1],parameters)?items(item[keys[0]],kind):[];
    }
    return item.template?referenced(item,kind):[content(item)];
  });}
  assert(Array.isArray(doc[section]),name+': missing '+section);
  return items(doc[section],section);
}
function artifacts(stages){
  const available=new Set();
  for(const stage of stages){
    const emitted=[];
    walk(stage,node=>{
      if(node.download==='current')assert(available.has(node.artifact),'Artifact not published by prior stage: '+node.artifact);
      if(node.publish)emitted.push(node.artifact);
      if(node.task==='PublishPipelineArtifact@1')emitted.push(node.inputs.artifact);
    });
    emitted.forEach(x=>available.add(x));
  }
}
check('YAML paths, required parameters and script entry points resolve',()=>{
  for(const name of yamlFiles){const doc=document(name);walk(doc,node=>{
    if(node.template){const destination=inside(path.resolve(root,path.dirname(name),node.template));const declarations=document(destination).parameters||[];for(const key of Object.keys(node.parameters||{}))assert(declarations.some(p=>p.name===key),name+': unknown '+key);for(const p of declarations.filter(p=>!Object.hasOwn(p,'default')))assert(Object.hasOwn(node.parameters||{},p.name),name+': missing '+p.name);}
    if(node.inputs?.scriptPath)assert(fs.existsSync(path.join(root,node.inputs.scriptPath)),node.inputs.scriptPath);
    if(node.pwsh)for(const match of node.pwsh.matchAll(/(?:\.\/|\s)scripts\/([A-Za-z0-9-]+\.ps1)/g))assert(fs.existsSync(path.join(root,'scripts',match[1])),match[1]);
  });}
});
const deploy=document('azure-pipelines-self-service-deploy.yml');
function menuType(file,id){const parameter=document(file).parameters.find(p=>p.name==='workloadType');const value=parameter.values.find(v=>v.endsWith(' | '+id));assert(value,'Missing workload menu choice '+id);return value;}

check('Manual roots, developer fields and discovery run picker remain intact',()=>{
  for(const file of ['azure-pipelines.yml','azure-pipelines-self-service.yml','azure-pipelines-self-service-deploy.yml']){const doc=document(file);assert.equal(doc.trigger,'none');assert.equal(doc.pr,'none');}
  assert.deepEqual(deploy.parameters.slice(0,4).map(p=>p.name),['workloadType','workloadName','environment','region']);
  assert(deploy.parameters.slice(4).every(p=>p.type==='string'&&p.values.length===1));
  assert.equal(deploy.extends.template,'pipelines/deploy-entry.yml');
  assert.equal(deploy.extends.parameters.discoveryRunId,'$(resources.pipeline.discovery.runID)');
  assert.equal(deploy.extends.parameters.discoveryPipelineId,'$(resources.pipeline.discovery.pipelineID)');
  assert.equal(deploy.resources.pipelines[0].source,json('self-service/pipeline-settings.json').discoveryPipelineName);assert.equal(deploy.resources.pipelines[0].trigger,'none');
});
check('Workload menus explain separate blueprints and normalize IDs before routing',()=>{
  for(const file of ['azure-pipelines-self-service.yml','azure-pipelines-self-service-deploy.yml']){
    const doc=document(file);const choices=doc.parameters.find(p=>p.name==='workloadType').values;
    assert(choices.some(v=>v.includes('Function App + 2 Storage accounts')));assert(choices.some(v=>v.includes('Logic App + Event Grid + 2 Storage accounts')));
    const values=bind(doc,{});const supplied=substitute((doc.extends||doc.stages[0]).parameters,values);assert.equal(supplied.workloadType,'blob-transfer');
    assert(!Object.keys(supplied).some(k=>/Creates|Requirements|Blueprint|Estimate/.test(k)),'Help fields must never become resource options');
  }
  assert(!deploy.parameters.some(p=>/Creates|Requirements/.test(p.name)),'Generic menu must not show both workload summaries');
  assert(!document('azure-pipelines-self-service.yml').parameters.some(p=>p.name.endsWith('Blueprint')));

});
const platform=json('config/platform.json');const publication=json('config/deployment-stack.json').templateSpec;
const sharedQualification=expand('pipelines/templates/steps/qualify-application.yml',{},[],'steps');
check('Build and Deploy share ordered qualification, cleanup and packaging',()=>{
  const [build]=expand('azure-pipelines.yml',{},[],'jobs');
  assert.deepEqual(build.steps.slice(1,1+sharedQualification.length),sharedQualification);
  assert.equal(build.pool.vmImage,'windows-latest');assert.equal(build.cancelTimeoutInMinutes,5);
  const index=script=>sharedQualification.findIndex(x=>x.pwsh?.includes(script));
  const sequence=['Test-Project.ps1','Test-Recovery.ps1','Test-LocalTooling.ps1','Run-Local.ps1','Stop-Local.ps1','Build-Package.ps1'].map(index);
  assert(sequence.every((value,i)=>value>=0&&(i===0||value>sequence[i-1])));
  assert.equal(sharedQualification[index('Stop-Local.ps1')].condition,'always()');
  assert(!sharedQualification[index('Build-Package.ps1')].condition,'Packaging must retain the default success condition');
  walk(build,n=>{assert(!n.task?.startsWith('AzureCLI'));assert(!n.continueOnError);});
});
check('Qualification retains every suite and avoids misleading missing-file publication failures',()=>{
  const steps=expand('pipelines/templates/steps/publish-qualification.yml',{artifactName:'test-evidence'},[],'steps');
  assert.equal(steps[0].condition,'always()');assert.equal(steps[0].inputs.failTaskOnFailedTests,true);assert.equal(steps[0].inputs.failTaskOnMissingResultsFile,false);
  for(const name of ['test-results','tooling-tests','self-service-tests','discovery-tests','cost-tests','platform-tests','stack-tests','pipeline-tests','recovery-tests','.local/logs'])assert(steps[1].pwsh.includes(name));
  assert.equal(steps[1].condition,'always()');assert.equal(steps[2].inputs.artifact,'test-evidence');assert.equal(steps[2].condition,"and(always(), eq(variables['qualificationEvidencePrepared'], 'true'))");
});
// Execute the small inline evidence scripts in isolated fixture directories. No
// application lifecycle, deployment scripts, Azure calls or ADO commands run.
function runEvidence(script,directory,env={}){
  const result=spawnSync('pwsh',['-NoLogo','-NoProfile','-NonInteractive','-Command',"$ErrorActionPreference='Stop';\n"+script],{cwd:directory,env:{...process.env,...env},encoding:'utf8'});
  assert.equal(result.status,0,result.error?.message||result.stderr||result.stdout);return result.stdout;
}
check('Early Azure task failure retains context without manufacturing a readiness receipt',()=>{
  const parent=path.join(root,'artifacts/pipeline-tests');fs.mkdirSync(parent,{recursive:true});const fixture=fs.mkdtempSync(path.join(parent,'stage-'));
  const [step]=expand('pipelines/templates/steps/prepare-stage-evidence.yml',{directory:fixture},[],'steps');
  const output=runEvidence(step.pwsh,fixture,{EVIDENCE_DIRECTORY:fixture,BUILD_BUILDID:'42',SYSTEM_STAGENAME:'ApplyRelease',SYSTEM_STAGEATTEMPT:'1',SYSTEM_JOBATTEMPT:'1',BUILD_SOURCEVERSION:'fixture-commit'});
  const context=JSON.parse(fs.readFileSync(path.join(fixture,'pipeline-context.json'),'utf8'));
  assert.equal(context.runId,'42');assert.equal(context.stage,'ApplyRelease');assert.equal(context.evidenceKind,'StageEntered');assert(!Object.hasOwn(context,'ready'));
  assert(!fs.existsSync(path.join(fixture,'receipt.json')));assert(output.includes('variable=stageEvidencePrepared]true'));
});
check('Qualification evidence works before tests start and retains later partial results',()=>{
  const parent=path.join(root,'artifacts/pipeline-tests');fs.mkdirSync(parent,{recursive:true});const fixture=fs.mkdtempSync(path.join(parent,'qualification-'));
  const destination=path.join(fixture,'staging');const steps=expand('pipelines/templates/steps/publish-qualification.yml',{artifactName:'test-evidence'},[],'steps');
  const script=steps[1].pwsh.replaceAll('$(Build.ArtifactStagingDirectory)',destination.replaceAll('\\','/').replaceAll("'","''"));
  runEvidence(script,fixture);assert(fs.existsSync(path.join(destination,'qualification/README.txt')));
  for(const suite of ['platform-tests','stack-tests','recovery-tests']){fs.mkdirSync(path.join(fixture,'artifacts',suite),{recursive:true});fs.writeFileSync(path.join(fixture,'artifacts',suite,'partial.json'),'{}');}
  runEvidence(script,fixture);
  for(const suite of ['platform','stacks','recovery'])assert(fs.existsSync(path.join(destination,'qualification',suite,'partial.json')));
});
const targets=files('self-service/targets').filter(x=>x.endsWith('.json')).map(json);
for(const target of targets){
  const workloadType=target.workloadType||'blob-transfer';
  const common={workloadType,workload:target.workload,environment:target.environmentName,subscription:target.subscriptionAlias,network:target.networkProfile};
  check(target.environmentName+' discovery remains hosted, scoped and artifact-producing',()=>{
    const stages=expand('azure-pipelines-self-service.yml',{...common,workloadType:menuType('azure-pipelines-self-service.yml',workloadType)});assert.deepEqual(stages.map(s=>s.stage),['Discover']);const job=stages[0].jobs[0];assert.equal(job.pool.vmImage,'windows-latest');
    const azure=job.steps.find(x=>x.task==='AzureCLI@2');assert.equal(azure.inputs.azureSubscription,target.serviceConnection);assert.equal(azure.inputs.scriptPath,'scripts/Export-DeploymentInventory.ps1');assert(azure.inputs.arguments.includes('-UseServiceConnectionSubscription'));
    assert(job.steps.some(x=>x.publish&&x.artifact==='subscription-discovery'&&x.condition==='succeededOrFailed()'));
  });
  const intent={workloadType,workloadName:target.workload,environment:target.environmentName,region:target.parameterOverrides.location||platform.defaultRegion};
  check(target.environmentName+' checked-in Deploy route matches its enabled flag',()=>{
    const stages=expand('azure-pipelines-self-service-deploy.yml',{...intent,workloadType:menuType('azure-pipelines-self-service-deploy.yml',workloadType)});
    if(!target.enabled){assert.deepEqual(stages.map(s=>s.stage),['SetupOnly']);walk(stages,n=>{assert(!n.deployment&&!n.environment&&!n.task?.startsWith('AzureCLI'));if(n.pool)assert.equal(n.pool.vmImage,'windows-latest');});assert(stages[0].jobs[0].steps.some(x=>x.artifact==='setup-guidance'));}
    else assert.equal(stages.length,6);
  });
  check(target.environmentName+' enabled-stage contract, protected bindings and artifact chain',()=>{
    // Exercise the reusable enabled contract without enabling or deploying any real profile.
    const stages=expand('pipelines/templates/self-service-stages.yml',{...common,serviceConnection:target.serviceConnection,agentPool:target.agentPool,deploymentEnvironment:target.deploymentEnvironment,publisherServiceConnection:publication.publisherServiceConnection,publisherEnvironment:publication.publisherEnvironment,publisherAgentPool:publication.publisherAgentPool,discoveryPipelineId:'1',discoveryRunId:'42',createDestinationPrivateEndpoints:platform.createDestinationPrivateEndpoints,enableLogAlerts:platform.enableLogAlerts});
    assert.deepEqual(stages.map(s=>s.stage),['Qualify','PublishTemplate','PlanFoundation','ApplyFoundation','PlanRelease','ApplyRelease']);
    for(let i=1;i<stages.length;i++)assert.equal(stages[i].dependsOn,stages[i-1].stage);
    for(const stage of stages.slice(1)){const publishing=stage.stage==='PublishTemplate';for(const job of stage.jobs){assert.equal(job.pool.name,publishing?publication.publisherAgentPool:target.agentPool);if(job.deployment){assert.equal(job.environment,publishing?publication.publisherEnvironment:target.deploymentEnvironment);assert.equal(stage.lockBehavior,'sequential');}walk(job,n=>{if(n.task==='AzureCLI@2')assert.equal(n.inputs.azureSubscription,publishing?publication.publisherServiceConnection:target.serviceConnection);});}}
    const download=stages[0].jobs[0].steps.find(x=>x.task==='DownloadPipelineArtifact@2');assert.equal(download.inputs.pipelineId,'42');assert.equal(download.inputs.definition,'1');assert.equal(download.inputs.artifactName,'subscription-discovery');artifacts(stages);
    const qualify=stages[0].jobs[0].steps;const handoff=qualify.findIndex(x=>x.pwsh?.includes('Test-DiscoveryHandoff.ps1'));
    const selected=workloadType==='blob-transfer'?sharedQualification:expand('pipelines/templates/steps/qualify-logic-app.yml',{},[],'steps');
    assert.deepEqual(qualify.slice(handoff+1,handoff+1+selected.length),selected);
    assert(qualify.findIndex(x=>x.pwsh?.includes('New-SelfServiceBundle.ps1'))>handoff+selected.length);
    if(workloadType!=='blob-transfer')assert(!selected.some(x=>x.pwsh?.includes('Run-Local.ps1')));
    for(const stage of stages.slice(1)){
      const job=stage.jobs[0];const steps=job.steps||job.strategy.runOnce.deploy.steps;
      assert.equal(job.workspace.clean,'all');assert.equal(job.cancelTimeoutInMinutes,5);
      const prepare=steps.findIndex(x=>x.pwsh?.includes('pipeline-context.json'));
      assert(prepare>=0&&prepare<steps.findIndex(x=>x.download==='current'));
      assert(prepare<steps.findIndex(x=>x.task==='AzureCLI@2'));
      assert.equal(steps.at(-1).condition,"and(always(), eq(variables['stageEvidencePrepared'], 'true'))");
    }
    assert(fs.existsSync(path.join(root,target.parameterFile)),target.parameterFile);
    assert.equal(json('artifacts/'+(workloadType==='blob-transfer'?'':'logic-app-event-grid/')+target.environmentName+'/parameters.json').parameters.environmentName.value,target.environmentName);
  });
}
check('Unregistered developer intent is rejected by the platform entry',()=>{
  const stages=expand('pipelines/deploy-entry.yml',{workloadType:'unknown',workloadName:'blobcopy',environment:'dev',region:'eastus2',discoveryPipelineId:'1',discoveryRunId:'42'});assert.deepEqual(stages.map(s=>s.stage),['InvalidIntent']);
});
check('Subscription wrapper forwards the complete original resource-group composition',()=>{
  const main=json('artifacts/dev/main.json');const wrapper=json('artifacts/test-results/stack-template.json');const resources=Object.values(wrapper.resources);const composition=resources.find(x=>x.type==='Microsoft.Resources/deployments');
  assert.equal(resources.length,2);assert(resources.some(x=>x.type==='Microsoft.Resources/resourceGroups'));
  assert.deepEqual(Object.keys(wrapper.parameters).sort(),[...Object.keys(main.parameters),'workloadResourceGroupName'].sort());
  assert.deepEqual(Object.keys(composition.properties.parameters).sort(),Object.keys(main.parameters).sort());
  for(const [name,value] of Object.entries(composition.properties.parameters))assert.equal(value.value,"[parameters('"+name+"')]");
  assert.deepEqual(composition.properties.template,main);assert.deepEqual(Object.keys(wrapper.outputs).sort(),Object.keys(main.outputs).sort());
});
check('Logic App wrapper embeds and forwards its complete composition',()=>{
  const main=json('artifacts/logic-app-event-grid/dev/main.json');const wrapper=json('artifacts/test-results/logic-stack-template.json');const resources=Object.values(wrapper.resources);const composition=resources.find(x=>x.type==='Microsoft.Resources/deployments');
  assert.equal(resources.length,2);assert.deepEqual(Object.keys(wrapper.parameters).sort(),[...Object.keys(main.parameters),'workloadResourceGroupName'].sort());
  for(const [name,value] of Object.entries(composition.properties.parameters))assert.equal(value.value,"[parameters('"+name+"')]");
  assert.deepEqual(Object.keys(composition.properties.parameters).sort(),Object.keys(main.parameters).sort());assert.deepEqual(composition.properties.template,main);assert.deepEqual(Object.keys(wrapper.outputs).sort(),Object.keys(main.outputs).sort());
});
check('Default menus select valid routes and cross-workload intent fails',()=>{
  assert.deepEqual(expand('azure-pipelines-self-service.yml',{}).map(x=>x.stage),['Discover']);
  assert.deepEqual(expand('azure-pipelines-self-service-deploy.yml',{}).map(x=>x.stage),['SetupOnly']);
  assert.deepEqual(expand('pipelines/deploy-entry.yml',{workloadType:'logic-app-event-grid',workloadName:'blobcopy',environment:'dev',region:'eastus2',discoveryPipelineId:'1',discoveryRunId:'42'}).map(x=>x.stage),['InvalidIntent']);
});
for(const [type,slug,title] of [['blob-transfer','blobcopy','Blob copy'],['logic-app-event-grid','eventflow','Event flow']]){
  const discoverFile=`azure-pipelines-${slug}-discover.yml`;const deployFile=`azure-pipelines-${slug}-deploy.yml`;
  const discover=document(discoverFile);const specific=document(deployFile);
  check(title+' menu contains only its own blueprint and fixed workload binding',()=>{
    for(const doc of [discover,specific]){
      assert.equal(doc.trigger,'none');assert.equal(doc.pr,'none');assert(!doc.parameters.some(p=>p.name==='workloadType'));
      const menu=JSON.stringify(doc.parameters);const foreign=type==='blob-transfer'?/Event flow|eventflow|Logic App|Event Grid/:/Blob copy|blobcopy|Function App/;
      assert(!foreign.test(menu),'Unrelated workload leaked into the menu');
      assert(doc.parameters.some(p=>p.name==='workloadSummary'));
    }
    assert.equal(discover.stages[0].parameters.workloadType,type);
    assert.equal(specific.extends.template,'pipelines/deploy-entry.yml');assert.equal(specific.extends.parameters.workloadType,type);
    assert.equal(specific.resources.pipelines[0].source,json('self-service/pipeline-settings.json').workloadDiscoveryPipelineNames[type]);
    assert.equal(specific.resources.pipelines[0].trigger,'none');assert.equal(specific.resources.pipelines[0].branch,'refs/heads/main');
    const supplied=substitute(specific.extends.parameters,bind(specific,{}));assert.deepEqual(Object.keys(supplied).sort(),['workloadType','workloadName','environment','region','discoveryPipelineId','discoveryRunId','executionMode'].sort());
    assert.throws(()=>expand(deployFile,{workloadName:type==='blob-transfer'?'eventflow':'blobcopy'}),/Disallowed value/);
  });
  for(const target of targets.filter(t=>(t.workloadType||'blob-transfer')===type)){
    check(title+' '+target.environmentName+' dedicated routes preserve existing protected templates',()=>{
      const common={workload:target.workload,environment:target.environmentName,subscription:target.subscriptionAlias,network:target.networkProfile};
      assert.deepEqual(expand(discoverFile,common),expand('azure-pipelines-self-service.yml',{...common,workloadType:menuType('azure-pipelines-self-service.yml',type)}));
      const request={workloadName:target.workload,environment:target.environmentName,region:target.parameterOverrides.location||platform.defaultRegion};
      const [preview,apply]=expand(deployFile,request);
      assert.deepEqual([preview.stage,apply.stage],['Preview','Deploy']);assert.equal(apply.dependsOn,'Preview');assert(apply.condition.includes('Preview only'));
      walk([preview,apply],n=>{if(n.pool)assert.equal(n.pool.vmImage,'windows-latest');assert(!n.deployment&&!n.environment);});
      const steps=preview.jobs[0].steps;
      assert(steps.some(n=>n.inputs?.artifactName==='subscription-discovery'));
      assert(steps.some(n=>n.pwsh?.includes('Test-DiscoveryHandoff.ps1')&&n.pwsh.includes('-AllowDisabled')));
      assert(steps.some(n=>n.pwsh?.includes('task.uploadsummary')&&n.condition.includes('always()')));
      assert(steps.some(n=>n.artifact==='deployment-preview'&&n.condition.includes('always()')));
      assert(!JSON.stringify(preview).includes('qualify-application'));
      const enabled=expand('pipelines/templates/self-service-two-stage.yml',{...common,workloadType:type,serviceConnection:target.serviceConnection,agentPool:target.agentPool,deploymentEnvironment:target.deploymentEnvironment,publisherServiceConnection:publication.publisherServiceConnection,publisherEnvironment:publication.publisherEnvironment,publisherAgentPool:publication.publisherAgentPool,discoveryPipelineId:'1',discoveryRunId:'42',executionMode:'Preview and deploy',deploymentEnabled:true});
      assert.deepEqual(enabled.map(x=>x.stage),['Preview','Deploy']);
      const jobs=enabled[1].jobs;assert.deepEqual(jobs.map(j=>j.job||j.deployment),['BuildBundle','PublishTemplateSpec','ApplyStack']);
      assert.equal(jobs[1].dependsOn,'BuildBundle');assert.equal(jobs[2].dependsOn,'PublishTemplateSpec');
      assert(jobs[0].steps.some(n=>n.pwsh?.includes('-Action VerifyBundle')));
      assert.equal(jobs[1].environment,publication.publisherEnvironment);assert.equal(jobs[2].environment,target.deploymentEnvironment);
      assert.equal(jobs[1].pool.name,publication.publisherAgentPool);assert.equal(jobs[2].pool.name,target.agentPool);
      const artifactsSeen=new Set(['deployment-preview']);
      for(const job of jobs){const emitted=[];walk(job,n=>{if(n.download==='current')assert(artifactsSeen.has(n.artifact));if(n.publish)emitted.push(n.artifact);if(n.task==='AzureCLI@2')assert.equal(n.inputs.azureSubscription,job.deployment==='PublishTemplateSpec'?publication.publisherServiceConnection:target.serviceConnection);});emitted.forEach(x=>artifactsSeen.add(x));}
      const disabled=expand(deployFile,{...request,executionMode:'Preview and deploy'});
      if(!target.enabled){assert.equal(disabled[1].jobs[0].job,'DeploymentUnavailable');walk(disabled,n=>{if(n.pool)assert.equal(n.pool.vmImage,'windows-latest');});}

    });
  }
}
const report={passed:cases.length,failed:0,yamlFilesParsed:yamlFiles.length,cases,azureCalls:false,adoServerExpansion:false,scope:'Local validation of the expression subset used in this repository; not an ADO compiler or resource authorization check.'};
fs.mkdirSync(path.join(root,'artifacts/test-results'),{recursive:true});fs.writeFileSync(path.join(root,'artifacts/test-results/pipeline-structure.json'),JSON.stringify(report,null,2)+'\n');
console.log(`PASS: ${cases.length} pipeline/infrastructure contracts; ${yamlFiles.length} YAML files. No Azure calls or ADO server expansion.`);
