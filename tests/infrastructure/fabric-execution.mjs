import fs from 'node:fs';
import path from 'node:path';
import assert from 'node:assert/strict';
import { fileURLToPath } from 'node:url';
import YAML from 'yaml';

const root=path.resolve(path.dirname(fileURLToPath(import.meta.url)),'../..');
const read=name=>fs.readFileSync(path.join(root,name),'utf8');
const json=name=>JSON.parse(read(name));
const yaml=name=>{
  const document=YAML.parseDocument(read(name),{uniqueKeys:true});
  assert.deepEqual(document.errors,[],name);
  return document.toJS();
};
const environments=['dev','qa','uat','prod'];
const pipeline=yaml('azure-pipelines-fabric-workspace.yml');
const bindings=yaml('pipelines/fabric-execution-bindings.yml');
const cases=[];
const check=(name,action)=>{action();cases.push(name);};
function walk(value, action){if(!value||typeof value!=='object')return;action(value);Object.values(value).forEach(child=>walk(child,action));}

check('Fabric runner selection comes only from the reviewed environment binding',()=>{
  assert.deepEqual(pipeline.parameters.map(p=>p.name),['operation','environment']);
  assert.deepEqual(pipeline.parameters.find(p=>p.name==='environment').values,environments);
  assert.deepEqual(pipeline.variables,[{template:'pipelines/fabric-execution-bindings.yml',parameters:{environment:'${{ parameters.environment }}'}}]);
  assert.equal(pipeline.pool.name,'${{ variables.fabricAgentPool }}');
  assert.deepEqual(pipeline.pool.demands,['ImageOverride -equals platform-windows']);
  assert.deepEqual(bindings.parameters,[{name:'environment',type:'string',values:environments}]);
  assert.equal(bindings.variables.length,environments.length);
});
for(const environment of environments){
  check(`Fabric ${environment} runner alias agrees with its managed-pool profile`,()=>{
    const condition=`\${{ if eq(parameters.environment, '${environment}') }}`;
    const conditional=bindings.variables.filter(entry=>Object.hasOwn(entry,condition));
    assert.equal(conditional.length,1);
    const variables=conditional[0][condition];
    assert.equal(variables.length,1);
    const source=json(`self-service/fabric/workspace.${environment}.json`);
    const armTarget=json(`self-service/targets/fabric.${environment}.json`);
    const poolParameters=read(`workloads/managed-devops-pool/environments/main.${environment}.bicepparam`);
    assert.deepEqual(variables,[{name:'fabricAgentPool',value:source.execution.adoPoolName,readonly:true}]);
    const poolAlias=poolParameters.match(/^param adoPoolName = '([^']+)'$/m);
    assert(poolAlias,'Managed-pool source alias must be explicit');
    assert.equal(source.execution.adoPoolName,poolAlias[1]);
    assert.equal(source.execution.adoPoolName,`fabric-${environment}-agents`);
    assert.equal(armTarget.agentPool,source.execution.adoPoolName);
    assert.equal(armTarget.enabled,false,'ARM deployment target onboarding remains disabled');
    assert.equal(source.environmentName,environment);
    assert.equal(source.execution.kind,'ManagedDevOpsPool');
    assert(source.execution.poolResourceId.includes(`/resourceGroups/rg-devopspool-${environment}/`));
    assert(source.execution.poolResourceId.includes('/providers/Microsoft.DevOpsInfrastructure/pools/'));
    assert(source.execution.agentSubnetId.includes('/subnets/'));
    assert.equal(source.enabled,false,'Onboarding remains disabled');
    assert.equal(source.operation.kind,'CreateWorkspace');
    assert(!source.execution.privateWorkspace,'Bootstrap has no existing private workspace');
  });
}
check('All three Fabric phases prove runner identity with a scoped job token',()=>{
  const tasks=[];const checkouts=[];
  walk(pipeline,node=>{if(node.task==='AzureCLI@2')tasks.push(node);if(node.checkout)checkouts.push(node);});
  assert.equal(tasks.length,3);
  for(const task of tasks){
    assert.equal(task.inputs.visibleAzLogin,false);
    assert.deepEqual(task.env,{BOUND_FABRIC_AGENT_POOL:'${{ variables.fabricAgentPool }}',SYSTEM_ACCESSTOKEN:'$(System.AccessToken)'});
    assert(!task.inputs.inlineScript.includes('System.AccessToken'),'Tokens stay out of command text');
    assert(task.inputs.inlineScript.includes('./scripts/Invoke-FabricWorkspace.ps1'));
  }
  assert.equal(checkouts.length,3);
  assert(checkouts.every(checkout=>checkout.clean===true&&checkout.persistCredentials===false));
  assert(pipeline.stages[0].condition.includes("refs/heads/main"));
  assert(pipeline.stages[0].condition.includes("variables['Build.Reason'], 'Manual'"));
  assert(pipeline.stages[1]["${{ if eq(parameters.operation, 'Preview and deploy') }}"][0].condition.includes("variables['Build.Reason'], 'Manual'"));
});
check('Private workspace example binds the reviewed agent VNet, endpoint and scoped API',()=>{
  const source=json('workloads/fabric/examples/private-workspace.manifest.json');
  const profile=source.execution;const workspace=profile.privateWorkspace;
  assert.equal(source.enabled,false);
  assert.equal(profile.kind,'ManagedDevOpsPool');
  assert.equal(source.operation.kind,'CreateLakehouse');
  assert.equal(source.operation.workspaceId,workspace.workspaceId);
  assert.equal(workspace.virtualNetworkResourceId,profile.agentSubnetId.replace(/\/subnets\/[^/]+$/,''));
  assert(workspace.privateEndpointResourceId.includes('/providers/Microsoft.Network/privateEndpoints/'));
  assert(workspace.privateDnsZoneResourceId.endsWith('/privateDnsZones/privatelink.fabric.microsoft.com'));
  const compact=workspace.workspaceId.replaceAll('-','').toLowerCase();
  assert.equal(workspace.apiBaseUrl,`https://${compact}.z${compact.slice(0,2)}.w.api.fabric.microsoft.com`);
});
const report={passed:cases.length,cases,azureCalls:0,adoCalls:0,scope:'Source configuration and YAML contracts only; no pool, DNS or Fabric acceptance.'};
fs.mkdirSync(path.join(root,'artifacts/test-results'),{recursive:true});
fs.writeFileSync(path.join(root,'artifacts/test-results/fabric-execution-pipeline.json'),JSON.stringify(report,null,2)+'\n');
console.log(`PASS: ${cases.length} Fabric managed-pool pipeline/source contracts. No Azure or ADO calls.`);
