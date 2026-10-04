import fs from 'node:fs/promises';
import path from 'node:path';
import { schema, sha, json, planned, compare, bicep, versions, scopeOf } from './core.mjs';
import { connectMcp, collect, completeBefore } from './azure.mjs';

const [action, directory, source, whatIf] = process.argv.slice(2);
const read = async file => JSON.parse(await fs.readFile(file,'utf8'));
if(action === 'qualify') {
  const {client,tools}=await connectMcp();try{console.log(json({server:'@azure/mcp',version:'2.0.5',tools:tools.map(t=>t.name),azureCalls:0}));}finally{await client.close();}
} else {
  if(!['before','plan','after'].includes(action) || !directory)throw Error('Expected before, plan or after and output directory.');
  await fs.mkdir(directory,{recursive:true});
  let report;
  if(action === 'before') {
    const target=await read(path.join(source,'target.json')), inputs=await read(path.join(source,'preview-inputs.json'));
    scopeOf(target);
    report={schema,scope:{subscriptionId:target.subscriptionId,resourceGroup:target.resourceGroup},
      provenance:{product:target.workload,environment:target.environmentName,runId:inputs.runId,sourceCommit:inputs.sourceCommit,
        region:(await read(path.join(source,'effective.parameters.json'))).parameters.location.value,
        pipelineId:process.env.SYSTEM_DEFINITIONID ?? '',repositoryId:process.env.BUILD_REPOSITORY_ID ?? '',
        inputSha256:sha(await fs.readFile(path.join(source,'preview-inputs.json'))),templateSha256:sha(await fs.readFile(path.join(source,'stack-template.json')))},
      collector:{name:'Microsoft Azure MCP',version:'2.0.5',credential:'AzureCliCredential',readOnly:true},
      apiVersions:versions(await read(path.join(source,'stack-template.json'))),before:null,planned:null,after:null,comparison:[],deploymentAuthorized:false,
      limitations:['Not a restore plan. Existing-only Bicep is an identity inventory, not reconstructed configuration.', 'After is a fresh observation, never synthesized from What-If. Projected matches are not full resource or application acceptance.']};
  }else report=await read(path.join(directory,'analysis.json'));
  if(report.schema!==schema)throw Error('Unsupported IaC evidence schema.');
  for(const [key,env]of Object.entries({runId:'BUILD_BUILDID',sourceCommit:'BUILD_SOURCEVERSION',pipelineId:'SYSTEM_DEFINITIONID',repositoryId:'BUILD_REPOSITORY_ID'}))
    if(process.env[env]&&String(report.provenance[key])!==process.env[env])throw Error('IaC evidence belongs to another pipeline run or source.');
  // A failed repeat must never publish an older successful phase as the new result.
  if(action==='plan'){report.planned=null;report.after=null;report.comparison=[];delete report.whatIfSha256;}
  if(action==='after'){report.after=null;report.comparison=[];}
  let failure;
  try {
    let input='';for await(const chunk of process.stdin){input+=chunk;if(input.length>65536)throw Error('Credential input too large.');}
    const auth=JSON.parse(input);
    if(auth.subscriptionId.toLowerCase()!==report.scope.subscriptionId.toLowerCase() || !/^[a-f0-9-]{36}$/i.test(auth.tenantId) || !auth.accessToken)throw Error('Azure task credential scope mismatch.');
    if(report.provenance.tenantId&&report.provenance.tenantId!==auth.tenantId)throw Error('Tenant changed between captures.');
    report.provenance.tenantId=auth.tenantId;
    if(action==='plan') {
      const bytes=await fs.readFile(whatIf);report.planned=planned(JSON.parse(bytes),report.scope);report.whatIfSha256=sha(bytes);
      try { await completeBefore(report.before,report.planned,report.scope,report.apiVersions,auth.accessToken); }
      catch { if(report.before){report.before.inventoryStatus='Partial';(report.before.issues??=[]).push('Planned resource-ID reads did not complete.');}throw Error('Incomplete baseline.'); }
      auth.accessToken='';
    }else {
      // Token is supplied only via stdin by the owning AzureCLI task, never argv, logs or disk.
      let client;
      try {
        ({client}=await connectMcp());
        report[action]=await collect({target:report.scope,tenantId:auth.tenantId,token:auth.accessToken,apiVersions:report.apiVersions,
          expected:action==='after' ? report.planned?.resources ?? [] : []},{mcp:client});
      }finally{auth.accessToken='';if(client)await client.close();}
      if(action==='after' && report.planned)report.comparison=compare(report.planned,report.after);
      if(report.comparison.some(c=>c.status==='Mismatch'))failure='Observed projected state differs from the reviewed What-If. Reconcile before retrying.';
      if(report[action].inventoryStatus==='Partial')failure='Live inventory was partial. Review analysis.json; no absence or final success inferred.';
    }
  }catch{failure='IaC analysis failed. Review pipeline diagnostics and tool qualification; absent resources are not inferred.';}
  report.updatedUtc=new Date().toISOString();
  if(failure)report.issue=failure;else delete report.issue;
  const files={'analysis.json':json(report)};
  for(const phase of ['before','after'])if(report[phase])files[phase+'.bicep']=bicep(report[phase],report.scope);
  files['README.md']=['# Agentic IaC evidence','',`Run ${report.provenance.runId} · ${report.provenance.product} / ${report.provenance.environment}`,'',
    `Before: ${report.before?.inventoryStatus ?? 'Unavailable'}. What-If: ${report.planned?.status ?? 'Not captured'}. After: ${report.after?.inventoryStatus ?? 'Not collected; deployment is not confirmed'}.`,
    '',failure ?? '', '', '| Resource | Action | Observed comparison |','|---|---|---|',
    ...(report.planned?.resources ?? []).map(r=>`| ${r.id.replace(/[|<>\r\n]/g,'_')} | ${r.action} | ${report.comparison.find(c=>c.id===r.id)?.status ?? 'Awaiting observation'} |`),'',...report.limitations.map(l=>'- '+l),''].join('\n');
  for(const [file,content]of Object.entries(files))await fs.writeFile(path.join(directory,file),content);
  await fs.writeFile(path.join(directory,'receipt.json'),json({schema:'platform.iac-receipt/v1',provenance:report.provenance,
    files:Object.fromEntries(Object.entries(files).map(([name,value])=>[name,sha(value)])),createdUtc:report.updatedUtc,executionAuthorized:false}));
  if(failure){console.error(failure);process.exitCode=1;}
}
