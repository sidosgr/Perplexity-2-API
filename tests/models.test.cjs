const assert=require('node:assert/strict');const {LocalPerplexityExtractModels:extract}=require('../WebBridge.js');
function row(id,label,disabled=false){return {textContent:label,disabled,getAttribute:k=>k==='data-model-id'?id:null,hasAttribute:()=>false,getBoundingClientRect:()=>({width:100})};}
const models=extract({querySelectorAll:()=>[row('fixture_a','Available'),row('fixture_b','Disabled',true),row('fixture_c','Upgrade to unlock'),row('fixture_d','Research'),row('bad/id','Bad'),row('fixture_a','Duplicate')]});
assert.deepEqual(models.map(m=>m.id),['perplexity-auto','perplexity-fixture_a']);assert.equal(extract({querySelectorAll:()=>[]})[0].id,'perplexity-auto');console.log('PASS: automatic selection, explicit DOM model IDs, deduplication, disabled/upgrade/research filtering');

const {LocalPerplexityModelsFromConfig:fromConfig}=require('../WebBridge.js');
const config={models:{normal:{label:'Normal',mode:'search'},thinking:{label:'Normal Thinking',mode:'search'},premium:{label:'Premium',mode:'search'},computer:{label:'Computer',mode:'asi'},retired:{label:'Retired',mode:'search',replaced_by:'normal'}},config:[{subscription_tier:'pro',non_reasoning_model:'normal',reasoning_model:'thinking'},{subscription_tier:'max',non_reasoning_model:'premium'},{subscription_tier:'pro',non_reasoning_model:'computer'},{subscription_tier:'pro',non_reasoning_model:'retired'},{subscription_tier:'pro',audience:'gated',non_reasoning_model:'premium'}]};
assert.deepEqual(fromConfig(config,'free').map(m=>m.id),['perplexity-auto']);
assert.deepEqual(fromConfig(config,'pro').map(m=>m.id),['perplexity-auto','perplexity-normal','perplexity-thinking']);
assert(fromConfig(config,'pro').find(m=>m.reasoning));assert.equal(fromConfig(config,'max').length,4);
console.log('PASS: authenticated config discovery, Free/Pro/Max gates, reasoning variants, retired and Computer model exclusion');
