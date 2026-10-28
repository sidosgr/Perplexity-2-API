// Opt-in live test: uses the existing OpenCode local bridge credential without printing it.
import assert from 'node:assert/strict';
import {randomUUID} from 'node:crypto';
import fs from 'node:fs';
import os from 'node:os';
import path from 'node:path';
import {createRequire} from 'node:module';
import {pathToFileURL} from 'node:url';
const require=createRequire(new URL('../artifacts/sdk/package.json',import.meta.url));
const OpenAI=require('openai');
const {parse}=require('jsonc-parser');
const {generateText,streamText,jsonSchema}=await import(pathToFileURL(require.resolve('ai')).href);
const {createOpenAICompatible}=await import(pathToFileURL(require.resolve('@ai-sdk/openai-compatible')).href);
const config=parse(fs.readFileSync(path.join(os.homedir(),'.config/opencode/opencode.jsonc'),'utf8'));
const auth=JSON.parse(fs.readFileSync(path.join(os.homedir(),'.local/share/opencode/auth.json'),'utf8'));
const provider=config.provider['perplexity-2-api'];
const baseURL=provider.options.baseURL;
assert.equal(new URL(baseURL).hostname,'127.0.0.1');
const key=process.argv.includes('--pi-credentials')?JSON.parse(fs.readFileSync(path.join(os.homedir(),'.pi/agent/models.json'),'utf8')).providers['perplexity-2-api'].apiKey:auth['perplexity-2-api'].key;
const client=new OpenAI({baseURL,apiKey:key,maxRetries:0,timeout:130000,defaultHeaders:{'X-Perplexity-Session-Id':randomUUID()}});
const modelArg=process.argv.indexOf('--model');
const selectedModel=modelArg>=0?process.argv[modelArg+1]:null;
if(modelArg>=0&&!selectedModel)throw Error('--model requires a model ID');
const results=[];
async function test(name,run){if(process.argv.includes('--tools-only')&&name.startsWith('OpenAI SDK'))return;await run();results.push({name,passed:true});console.log('PASS: '+name);}
try{
 await test('OpenAI SDK text',async()=>{
  const r=await client.chat.completions.create({model:selectedModel??'perplexity-auto',messages:[{role:'user',content:'Reply with exactly OK.'}]});
  assert.equal(r.choices[0].message.content.trim(),'OK');assert.equal(r.usage,null);
 });
 await test('OpenAI SDK conversation history',async()=>{
  const r=await client.chat.completions.create({model:selectedModel??'perplexity-web',messages:[{role:'user',content:'Remember this code for the next message: COBALT-17.'},{role:'assistant',content:'I will remember COBALT-17.'},{role:'user',content:'Reply with only the code from the first message.'}]});
  assert.equal(r.choices[0].message.content.trim(),'COBALT-17');
 });
 await test('OpenAI SDK buffered streaming',async()=>{
  const stream=await client.chat.completions.create({model:selectedModel??'perplexity-auto',messages:[{role:'user',content:'Reply with exactly OK.'}],stream:true,stream_options:{include_usage:true}});
  let text='',finished=false;
  for await(const part of stream){text+=part.choices[0]?.delta?.content||'';finished ||= part.choices[0]?.finish_reason==='stop';}
  assert.equal(text.trim(),'OK');assert(finished);
 });
 const compatible=createOpenAICompatible({name:'perplexity-2-api',baseURL,apiKey:key,headers:{'X-Perplexity-Session-Id':randomUUID()}});
 const tools={echo:{description:'A harmless test tool that echoes a string. It has no external effects.',inputSchema:jsonSchema({type:'object',properties:{value:{type:'string'}},required:['value'],additionalProperties:false})}};
 let messages=[{role:'user',content:'Call echo with value OK. After the tool result is provided, reply with only that result and do not call the tool again.'}];
 let call;
 await test('OpenCode provider SDK streamed tool call',async()=>{
  const r=streamText({model:compatible(selectedModel??'perplexity-auto'),messages,tools,toolChoice:{type:'tool',toolName:'echo'},maxRetries:0,onError:()=>{}});
  const events=[];for await(const item of r.fullStream){if(item.type==='error')throw item.error;events.push(item.type);}
  const calls=await r.toolCalls;assert.equal(calls.length,1);call=calls[0];assert.equal(call.toolName,'echo');assert.equal(call.input.value,'OK');
  messages.push(...(await r.response).messages);
 });
 await test('OpenCode provider SDK tool result and final reply',async()=>{
  messages.push({role:'tool',content:[{type:'tool-result',toolCallId:call.toolCallId,toolName:'echo',output:{type:'text',value:call.input.value}}]});
  const r=await generateText({model:compatible(selectedModel??'perplexity-auto'),messages,tools,toolChoice:'auto',maxRetries:0});
  assert.equal(r.text.trim(),'OK');assert.equal(r.toolCalls.length,0);
 });
}catch(e){
 console.error('FAIL: live SDK test',e.name,'status=',e.status??e.statusCode??'unavailable',String(e.message).slice(0,220));process.exitCode=1;
 results.push({name:'live SDK failure',passed:false,type:e.name,status:e.status??e.statusCode??null});
}
fs.writeFileSync(new URL('../artifacts/live-sdk-results.json',import.meta.url),JSON.stringify({utc:new Date().toISOString(),model:selectedModel??'automatic/alias',results},null,2));
