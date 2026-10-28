import assert from 'node:assert/strict';
import {createRequire} from 'node:module';
import {readFileSync,writeFileSync} from 'node:fs';
import {homedir} from 'node:os';
import {join} from 'node:path';
import {randomUUID} from 'node:crypto';
const require=createRequire(new URL('../artifacts/sdk/package.json',import.meta.url));const OpenAI=require('openai');
const p=JSON.parse(readFileSync(join(homedir(),'.pi/agent/models.json'),'utf8')).providers['perplexity-2-api'];
const client=new OpenAI({baseURL:p.baseUrl,apiKey:p.apiKey,maxRetries:0,timeout:150000});
const stateFile=new URL('../artifacts/live-session-state.json',import.meta.url);
let saved;
const resume=process.argv.includes('--resume');
if(resume)saved=JSON.parse(readFileSync(stateFile,'utf8'));else saved={session:randomUUID(),otherSession:randomUUID(),project:'bridge-session-qa',messages:[]};
const headers=session=>({'X-Perplexity-Session-Id':session,'X-Perplexity-Project-Id':saved.project,'X-Perplexity-Project-Name':'Bridge%20session%20QA'});
async function call(body,session=saved.session){const {data,response}=await client.chat.completions.create(body,{headers:headers(session)}).withResponse();return {text:data.choices[0].message.content,thread:response.headers.get('x-perplexity-thread-id')};}
async function summary(){return (await client.get('/bridge/sessions')).data;}
try{
 if(!resume){
  const models=(await client.models.list()).data;assert(models.length>2);assert(!models.some(m=>m.id==='perplexity-gpt6_astra'));console.log('PASS: authenticated model catalogue contains '+models.length+' eligible selections');
  saved.messages=[{role:'user',content:'Remember the code AMBER-42. Reply with exactly OK.'}];
  const first=await call({model:'perplexity-auto',messages:saved.messages});assert.equal(first.text.trim(),'OK');assert(first.thread);saved.thread=first.thread;saved.messages.push({role:'assistant',content:first.text},{role:'user',content:'What code did I ask you to remember? Reply with only that code.'});
  const body={model:'perplexity-auto',messages:saved.messages};const second=await call(body);assert.match(second.text,/AMBER-42/);assert.equal(second.thread,saved.thread);console.log('PASS: two turns remain in one Perplexity thread');
  const before=(await summary()).find(s=>s.threadId===saved.thread);assert.equal(before.turns,2);
  const retry=await call(body);assert.equal(retry.text,second.text);assert.equal((await summary()).find(s=>s.threadId===saved.thread).turns,2);console.log('PASS: identical request replay returns cached reply without another upstream turn');
  const other=await call({model:'perplexity-auto',messages:[{role:'user',content:'Reply with exactly OTHER.'}]},saved.otherSession);assert.notEqual(other.thread,saved.thread);saved.otherThread=other.thread;console.log('PASS: a different agent session gets a separate thread');
  saved.model=models.some(m=>m.id==='perplexity-claude55sonnet')?'perplexity-claude55sonnet':'perplexity-auto';assert.notEqual(saved.model,'perplexity-auto');
  saved.messages.push({role:'assistant',content:second.text},{role:'user',content:'Reply with exactly MODEL_OK.'});
  const explicit=await call({model:saved.model,messages:saved.messages});assert.equal(explicit.text.trim(),'MODEL_OK');assert.equal(explicit.thread,saved.thread);saved.messages.push({role:'assistant',content:explicit.text});console.log('PASS: explicit discovered model selection continues the same thread');
  saved.turns=(await summary()).find(s=>s.threadId===saved.thread).turns;
  writeFileSync(stateFile,JSON.stringify(saved,null,2));
 }else{
  saved.messages.push({role:'user',content:'Reply with exactly RESUMED.'});
  const next=await call({model:saved.model,messages:saved.messages});assert.equal(next.text.trim(),'RESUMED');assert.equal(next.thread,saved.thread);assert.equal((await summary()).find(s=>s.threadId===saved.thread).turns,saved.turns+1);console.log('PASS: bridge restart resumes the original thread and next turn');
 }
 writeFileSync(new URL('../artifacts/live-session-results'+(resume?'-resume':'')+'.json',import.meta.url),JSON.stringify({passed:true,thread:saved.thread,otherThread:saved.otherThread,model:saved.model,utc:new Date().toISOString()},null,2));
}catch(e){console.error('FAIL:',e.name,'status',e.status??e.statusCode??'',String(e.message).slice(0,180));process.exitCode=1;}
