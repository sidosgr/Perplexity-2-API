// Optional integration test against actual published Cordis/LLM packages.
// npm install --prefix artifacts/dsh-runtime --ignore-scripts @deepseek-ai/dsh-llm @deepseek-ai/cordis
import {createRequire} from 'node:module';
import {pathToFileURL} from 'node:url';
import {cpSync,mkdirSync,readFileSync,writeFileSync} from 'node:fs';
import {resolve} from 'node:path';
import assert from 'node:assert/strict';
const root=resolve('artifacts/dsh-runtime');mkdirSync(root,{recursive:true});
cpSync('integrations/deepseek-harness',root+'/plugin',{recursive:true});
const require=createRequire(root+'/package.json');
const {Context}=await import(pathToFileURL(require.resolve('@deepseek-ai/cordis')));
const {default:Runtime,createToolResultMessage,createAssistantMessage}=await import(pathToFileURL(require.resolve('@deepseek-ai/dsh-llm')));
const plugin=await import(pathToFileURL(root+'/plugin/index.js'));
const ctx=new Context();
const oldFetch=globalThis.fetch,oldKey=process.env.LOCAL_PERPLEXITY_KEY;
const live=process.argv.includes('--live');
let requests=[];const linkedThreads=new Map();
if(live){const config=JSON.parse(readFileSync(resolve(process.env.USERPROFILE,'.pi/agent/models.json'),'utf8'));process.env.LOCAL_PERPLEXITY_KEY=config.providers['perplexity-2-api'].apiKey;
 globalThis.fetch=async(url,init)=>{const response=await oldFetch(url,init);const id=init.headers?.['X-Perplexity-Session-Id'],thread=response.headers.get('x-perplexity-thread-id');if(id&&thread){const prior=linkedThreads.get(id);if(prior)assert.equal(thread,prior);linkedThreads.set(id,thread);}return response;};
}
else{process.env.LOCAL_PERPLEXITY_KEY='lpb_test-only';globalThis.fetch=async(url,init)=>{
 requests.push({url,init});
 return new Response(JSON.stringify(url.endsWith('/models')?{data:[{id:'perplexity-web'}]}:{choices:[{finish_reason:'stop',message:{content:'OK'}}],usage:null}));
};}
try{
 await ctx.plugin(Runtime);await ctx.plugin(plugin);
 assert((await ctx.llm.listProviders()).some(p=>p.id===plugin.PROVIDER));
 const model=live?'perplexity-claude55sonnetthinking':'perplexity-web';
 const prepared=await ctx.llm.prepareCall({provider:plugin.PROVIDER,model});
 const options={provider:plugin.PROVIDER,model,sessionId:'dsh-plugin-qa-'+Date.now(),messages:[{role:'user',content:[{type:'text',text:'Reply with exactly OK.'}]}]};
 const events=[];for await(const e of prepared.stream(options))events.push(e);
 assert.equal(events.at(-1).reason.kind,'stop',JSON.stringify(events.at(-1)));
 assert.equal(events.find(e=>e.type==='block-end').block.text.trim(),'OK');
 const {requestBody}=await import('../integrations/deepseek-harness/transport.js');
 const toolResult=createToolResultMessage({callId:'call_1',content:[{type:'text',text:'Actual local result'}],isError:false});
 assert.equal(requestBody({...options,messages:[toolResult]}).messages[0].tool_call_id,'call_1');
 if(live){
  const session='dsh-plugin-tool-qa-'+Date.now(),marker='DSH_LOCAL_'+Date.now();
  const messages=[{role:'user',content:[{type:'text',text:'Call save_test with value '+marker+'. Wait for the tool result, then reply with that exact value. Do not claim the file is written before the tool result.'}]}];
  const tools=[{name:'save_test',description:'Save a value to an isolated local QA file and read it back.',parameters:{type:'object',properties:{value:{type:'string'}},required:['value'],additionalProperties:false}}];
  const first=[];for await(const e of ctx.llm.stream({provider:plugin.PROVIDER,model,sessionId:session,messages,tools}))first.push(e);
  assert.equal(first.at(-1).reason.kind,'tool-calls',JSON.stringify(first.at(-1)));
  const block=first.find(e=>e.type==='block-end'&&e.block.type==='tool-call').block;
  assert.equal(block.name,'save_test');const args=JSON.parse(block.arguments);assert.equal(args.value,marker);
  // This QA dispatcher owns exactly one harmless tool, not the full Harness tool service.
  const file=root+'/tool-proof-'+session+'.txt';writeFileSync(file,args.value);const actual=readFileSync(file,'utf8');assert.equal(actual,marker);
  messages.push(createAssistantMessage({content:[block],source:{provider:plugin.PROVIDER,model}}));
  messages.push(createToolResultMessage({callId:block.id,content:[{type:'text',text:actual}],isError:false}));
  const final=[];for await(const e of ctx.llm.stream({provider:plugin.PROVIDER,model,sessionId:session,messages,tools}))final.push(e);
  assert.equal(final.at(-1).reason.kind,'stop',JSON.stringify(final.at(-1)));
  assert(final.some(e=>e.type==='block-end'&&e.block.type==='text'&&e.block.text.includes(marker)));
  assert(linkedThreads.has('dsh-'+session));
  console.log('PASS: live tool call, isolated local QA file write/read, and actual tool-result continuation through published Harness LLM runtime');
 }
 if(!live){assert(requests.every(r=>r.init.headers['user-agent'].startsWith('deepseek-harness/')));assert.equal(requests.at(-1).init.headers['X-Perplexity-Session-Id'],'dsh-'+options.sessionId);}
 if(!live){
  let adapter,reads=0;
  plugin.apply({get:(service)=>service==='credentials'?{resolve:async(ref)=>{assert.equal(ref,'SAVED_BRIDGE_REF');reads++;return {value:'lpb_saved-test-only'};}}:undefined,llm:{registerAdapter:(_routes,value)=>{adapter=value;}}},{apiKeyEnv:'SAVED_BRIDGE_REF'});
  await adapter.listModels();assert(reads>0);assert.equal(requests.at(-1).init.headers.Authorization,'Bearer lpb_saved-test-only');
  console.log('PASS: desktop saved credential reference resolves without an environment variable');
 }
 console.log('PASS: actual published Cordis activation, LLM registration/model resolution, prepared stream, tool-result representation and '+(live?'live '+model+' text generation':'wire attribution/session headers'));
}finally{await ctx.fiber.dispose();globalThis.fetch=oldFetch;if(oldKey===undefined)delete process.env.LOCAL_PERPLEXITY_KEY;else process.env.LOCAL_PERPLEXITY_KEY=oldKey;}
