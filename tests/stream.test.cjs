const assert=require('node:assert/strict');
const {LocalPerplexitySseParser:Parser}=require('../WebBridge.js');
const frame=v=>'data: '+JSON.stringify(v)+'\r\n\r\n';
const block=md=>({blocks:[{intended_usage:'ask_text_0_markdown',markdown_block:md}]});
const stream=frame(block({chunks:['Hello ']}))+frame(block({chunks:['Hello ','Ελληνικά 🌍']}))+frame({final_sse_message:true});
for(let i=0;i<=stream.length;i++){const p=new Parser();p.feed(stream.slice(0,i));p.feed(stream.slice(i),true);assert.equal(p.text,'Hello Ελληνικά 🌍');assert(p.stopped);}
const patch=p=>frame({blocks:[{intended_usage:'ask_text_0_markdown',diff_block:{patches:p}}]});
const p=new Parser();p.feed(patch([{op:'replace',path:'',value:{chunks:['wrong']}}]));p.feed(patch([{op:'replace',path:'/chunks/0',value:'OK'},{op:'add',path:'/chunks/-',value:'!'},{op:'remove',path:'/chunks/1'}]));p.feed(frame({final_sse_message:true}));assert.equal(p.text,'OK');
assert.throws(()=>new Parser().feed(frame(block({chunks:['partial']})),true),/incomplete_stream/);
assert.throws(()=>new Parser().feed('data: {bad}\n\n'),/invalid_stream_json/);
assert.throws(()=>new Parser().feed(frame({error:{type:'quota_exceeded'}})),/usage_limited/);
assert.throws(()=>new Parser().feed(frame({error:{type:'rate_limit_error'}})),/rate_limited/);
assert.throws(()=>new Parser().feed(patch([{op:'add',path:'/chunks/9',value:'wrong'}])),/unsupported_stream_patch/);
const status=new Parser();status.feed(frame({text:'Searching...'}));assert.equal(status.text,'');
const legacy=new Parser();legacy.feed(frame({text:JSON.stringify({answer:'OK',status:'completed'})}),true);assert.equal(legacy.text,'OK');
console.log(`PASS: ${stream.length+1} split positions, Unicode, replacement snapshots, incremental patches, errors and truncated streams`);

// Live transport regression: concise answers use ask_text without a markdown variant.
const concise=new Parser();
concise.feed(frame({blocks:[{intended_usage:'ask_text',markdown_block:{chunks:['O'],chunk_starting_offset:0}}],final:true}));
assert.equal(concise.stopped,false);
concise.feed(frame({blocks:[{intended_usage:'ask_text',markdown_block:{chunks:['O','K'],chunk_starting_offset:0,answer:'OK'}}],final_sse_message:true}));
assert.equal(concise.text,'OK');assert(concise.stopped);
for(const reversed of [false,true]){
 const both=new Parser(), blocks=[{intended_usage:'ask_text',markdown_block:{chunks:['plain']}},{intended_usage:'ask_text_0_markdown',markdown_block:{chunks:['**markdown**']}}];
 both.feed(frame({blocks:reversed?blocks.reverse():blocks,final_sse_message:true}));assert.equal(both.text,'**markdown**');
}
const offsets=new Parser();offsets.feed(frame(block({chunks:['A','B'],chunk_starting_offset:0})));offsets.feed(frame(block({chunks:['C'],chunk_starting_offset:2})));assert.equal(offsets.text,'ABC');
console.log('PASS: live ask_text schema, authoritative completion, dual-block deduplication and chunk offsets');

const tier=new Parser();tier.feed(frame({answer:'OK',final_sse_message:true,_extras:{subscription_tier:'pro'}}));assert.equal(tier.planTier,'pro');

const delivery=new Parser('complete input');delivery.feed(frame({query_str:'complete input',answer:'OK',final_sse_message:true}));assert.equal(delivery.echoLength,14);assert.equal(delivery.echoEqual,true);
const cut=new Parser('complete input');cut.feed(frame({query_str:'complete',answer:'bad reply',final_sse_message:true}));assert.equal(cut.echoEqual,false);
console.log('PASS: upstream request echo detects input truncation');

const changedEcho=new Parser('complete input');changedEcho.feed(frame({query_str:'complete'}));changedEcho.feed(frame({query_str:'complete input',answer:'OK',final_sse_message:true}));assert.equal(changedEcho.echoEqual,false);
