import assert from 'node:assert/strict';
import {createRequire} from 'node:module';import {readFileSync,writeFileSync} from 'node:fs';import {homedir} from 'node:os';import {join} from 'node:path';import {randomUUID} from 'node:crypto';
const require=createRequire(new URL('../artifacts/sdk/package.json',import.meta.url)),OpenAI=require('openai');
const provider=JSON.parse(readFileSync(join(homedir(),'.pi/agent/models.json'),'utf8')).providers['perplexity-2-api'];
const client=new OpenAI({baseURL:provider.baseUrl,apiKey:provider.apiKey,maxRetries:0,timeout:180000});
const markers=[randomUUID().slice(0,8),randomUUID().slice(0,8),randomUUID().slice(0,8)];
const system='QA fact: first code is '+markers[0]+'.\n'+'Neutral browser guidance. '.repeat(1400)+'\nQA fact: middle code is '+markers[1]+'.\n'+'Neutral reference context. '.repeat(1300)+'\nQA fact: final code is '+markers[2]+'.';
const schema={type:'object',properties:{value:{type:'string'}},required:['value'],additionalProperties:false};
const tools=[{type:'function',function:{name:'echo',description:'A harmless external caller echo function. It only returns its value string.',parameters:schema}},...Array.from({length:24},(_,i)=>({type:'function',function:{name:'unused_'+i,description:'Unused helper guidance. '.repeat(50),parameters:schema}}))];
const headers={'X-Perplexity-Session-Id':randomUUID(),'X-Perplexity-Project-Id':'long-context-qa','X-Perplexity-Project-Name':'Long%20context%20QA'};
const messages=[{role:'system',content:system},{role:'user',content:'Call echo with the final QA code from the system context. After the actual tool result, reply with the first, middle and final QA codes separated by |. Do not call another tool after the result.'}];
const goCase=process.argv.includes('--go-case');
if(goCase)messages.push({role:'assistant',content:'The supplied conversation is truncated inside a system message and contains no final user request.'},{role:'user',content:'go'});
let thread;
try{
 const first=await client.chat.completions.create({model:'perplexity-gpt6_1_sol_thinking',max_completion_tokens:32000,messages,tools,tool_choice:goCase?'auto':{type:'function',function:{name:'echo'}}},{headers}).withResponse();
 thread=first.response.headers.get('x-perplexity-thread-id');const reply=first.data.choices[0].message;assert.equal(reply.tool_calls.length,1);const call=reply.tool_calls[0];assert.equal(call.function.name,'echo');assert.equal(JSON.parse(call.function.arguments).value,markers[2]);if(goCase)console.log('PASS: go after an earlier misleading truncation reply still resolves the original caller task');console.log('PASS: a '+system.length+'-character system prompt and 25 tools reached the model intact and produced the required call');
 const list=await client.get('/bridge/sessions');const before=list.data.find(s=>s.threadId===thread);assert(before.turns>=4);
 messages.push(reply,{role:'tool',tool_call_id:call.id,content:markers[2]});
 const second=await client.chat.completions.create({model:'perplexity-gpt6_1_sol_thinking',messages,tools,tool_choice:'auto'},{headers}).withResponse();
 assert.equal(second.response.headers.get('x-perplexity-thread-id'),thread);const text=second.data.choices[0].message.content;for(const marker of markers)assert(text.includes(marker));assert(!/truncat|missing.*request/i.test(text));
 const after=(await client.get('/bridge/sessions')).data.find(s=>s.threadId===thread);assert.equal(after.turns,before.turns+1);console.log('PASS: facts from different context fragments survived; the tool-result follow-up used one incremental turn in the same thread');
 const metrics=JSON.parse(readFileSync(join(homedir(),'AppData/Local/LocalPerplexityBridge/last-prompt-metrics.json'),'utf8'));assert.equal(metrics.echo_equal,true);assert(metrics.sent_characters<=39000);
 writeFileSync(new URL('../artifacts/long-context-results.json',import.meta.url),JSON.stringify({passed:true,goCase,systemCharacters:system.length,tools:25,initialNativeMessages:before.turns,followupNativeMessages:after.turns-before.turns,thread,lastWireMetrics:metrics,utc:new Date().toISOString()},null,2));
}catch(e){console.error('FAIL:',e.name,e.status??e.statusCode??'',String(e.message).slice(0,220));process.exitCode=1;}
