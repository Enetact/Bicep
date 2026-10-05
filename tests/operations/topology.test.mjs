import {test} from 'node:test';
import assert from 'node:assert/strict';
import {operationsGraph} from '../../src/SelfService.Portal/wwwroot/operations.mjs';
test('DNS graph includes only observed references and leaves unknown paths unasserted',()=>{
  const e={completedUtc:'test',coverage:[],resources:[
    {id:'zone',name:'zone',type:'zone',properties:{}},
    {id:'link',name:'link',type:'link',parentId:'zone',properties:{virtualNetworkId:'unobserved-vnet'}},
    {id:'record',name:'record',type:'a',parentId:'zone',properties:{recordType:'A'}},
    {id:'group',name:'group',type:'zonegroup',properties:{zoneIds:'["zone"]'}}]};
  const graph=operationsGraph(e);
  assert.equal(graph.nodes.length,3);
  assert.deepEqual(graph.edges,[{from:'zone',to:'link',label:'contains'},{from:'group',to:'zone',label:'configured reference'}]);
  assert.match(graph.note,/unknown/);
});
