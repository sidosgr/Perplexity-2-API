import {spawn} from 'node:child_process';
import {createInterface} from 'node:readline';
import {readFileSync,writeFileSync,mkdirSync,unlinkSync,existsSync} from 'node:fs';
import {homedir} from 'node:os';
import {join,resolve} from 'node:path';
import {createHash,randomUUID} from 'node:crypto';
import assert from 'node:assert/strict';
const appData=process.env.LOCALAPPDATA;
const root=join(appData,'LocalPerplexityBridge','PiTests',randomUUID());mkdirSync(root,{recursive:true});
const projectArg=process.argv.indexOf('--project-dir');
const cwd=projectArg>=0?resolve(process.argv[projectArg+1]):join(root,'PiBridgeQA');
if(projectArg>=0&&!process.argv.includes('--existing-file')&&existsSync(join(cwd,'tetris.html')))throw Error('Refusing to overwrite an existing tetris.html');
mkdirSync(cwd,{recursive:true});
const provider=JSON.parse(readFileSync(join(homedir(),'.pi/agent/models.json'),'utf8')).providers['perplexity-2-api'];
writeFileSync(join(root,'models.json'),JSON.stringify({providers:{'perplexity-2-api':provider}}));
writeFileSync(join(root,'settings.json'),JSON.stringify({defaultProvider:'perplexity-2-api',defaultModel:'perplexity-auto'}));
const cli=join(process.env.APPDATA,'npm/node_modules/@earendil-works/pi-coding-agent/dist/bundle/cli.js');
const ext=join(homedir(),'.pi/agent/extensions/perplexity-bridge.ts');
const sessionName='bridge-qa-'+randomUUID();
const longContext=process.argv.includes('--long-context'),thinkingModel=process.argv.includes('--thinking-model');
const modelArg=process.argv.indexOf('--model');
const qaModel=modelArg>=0?process.argv[modelArg+1]:thinkingModel?'perplexity-gpt6_1_sol_thinking':'perplexity-auto';
const qaContext='QA_REFERENCE_'+randomUUID().slice(0,8);
const contextFile=join(root,'long-system.md');writeFileSync(contextFile,'QA_REFERENCE_CODE is '+qaContext+'.\n'+'Neutral browser API reference text. '.repeat(1900));
const fileCycle=process.argv.includes('--file-cycle');
const localWork=fileCycle||process.argv.includes('--local-work');
const withTool=localWork||process.argv.includes('--with-tool'),sentinel='PI_TOOL_'+randomUUID();if(!localWork)writeFileSync(join(cwd,'sentinel.txt'),sentinel);
const sha=s=>createHash('sha256').update(s).digest('hex');
const projectId=sha(process.platform==='win32'?resolve(cwd).toLowerCase():resolve(cwd));
const expectedKey=id=>sha(sha(provider.apiKey)+'\n'+projectId+'\n'+id);
async function sessions(){const r=await fetch(provider.baseUrl+'/bridge/sessions',{headers:{Authorization:'Bearer '+provider.apiKey}});assert(r.ok);return (await r.json()).data;}
function start(){
 const child=spawn(process.execPath,[cli,'--mode','rpc','--session-id',sessionName,...(withTool?['--tools',localWork?'read,write,edit,bash':'read']:['--no-tools']),'--no-extensions','--extension',ext,'--no-mcp','--no-skills','--no-prompt-templates','--provider','perplexity-2-api','--model',qaModel,...(longContext?['--append-system-prompt',contextFile]:[]),'--thinking','off'],{cwd,env:{...process.env,PI_CODING_AGENT_DIR:root},windowsHide:true,stdio:['pipe','pipe','pipe']});
 const events=[],waiting=new Set();let diagnostic='';
 child.stderr.on('data',s=>{diagnostic+=String(s).slice(0,2000)});
 createInterface({input:child.stdout}).on('line',line=>{try{const event=JSON.parse(line);events.push(event);for(const fn of waiting)fn();}catch{}});
 child.on('exit',()=>{for(const fn of waiting)fn();});
 const wait=(predicate,from=0)=>new Promise((resolve,reject)=>{
  const timer=setTimeout(()=>{waiting.delete(check);reject(Error('Pi RPC timed out: '+diagnostic.slice(-300)));},150000);
  function check(){const found=events.slice(from).find(predicate);if(found){clearTimeout(timer);waiting.delete(check);resolve(found);}else if(child.exitCode!==null){clearTimeout(timer);waiting.delete(check);reject(Error('Pi exited '+child.exitCode+': '+diagnostic.slice(-300)));}}
  waiting.add(check);check();
 });
 async function command(type,extra={}){const id=randomUUID(),from=events.length;child.stdin.write(JSON.stringify({id,type,...extra})+'\n');const e=await wait(e=>e.type==='response'&&e.id===id,from);if(!e.success)throw Error('Pi command failed: '+type+' '+String(e.error).slice(0,180));return e.data;}
 async function prompt(message){const from=events.length;const id=randomUUID();child.stdin.write(JSON.stringify({id,type:'prompt',message})+'\n');const end=await wait(e=>e.type==='agent_end'||e.type==='response'&&e.id===id&&!e.success,from);if(end.type==='response')throw Error(String(end.error));const assistant=[...(end.messages||[])].reverse().find(m=>m.role==='assistant');if(assistant?.stopReason==='error')throw Error(assistant.errorMessage||'Pi assistant failed');return (assistant?.content||[]).filter(p=>p.type==='text').map(p=>p.text).join('');}
 return {child,command,prompt,events};
}
let rpc=start();
try{
 let state=await rpc.command('get_state');const id=state.sessionId;assert(id);
 if(fileCycle){
 const filename='bridge-cycle.txt', initial='INITIAL_'+randomUUID(), updated='UPDATED_'+randomUUID();
 const checkTool=(from,name)=>assert(rpc.events.slice(from).some(e=>e.type==='tool_execution_end'&&e.toolName===name&&!e.isError),'Expected successful '+name+' tool');
 let from=rpc.events.length;
 await rpc.prompt('Use write to create '+filename+' in the current directory containing exactly: '+initial);
 checkTool(from,'write');assert.equal(readFileSync(join(cwd,filename),'utf8').trim(),initial);
 let row=(await sessions()).find(s=>s.id===expectedKey(id));assert(row);const thread=row.threadId;
 console.log('PASS: actual local write and disk verification');
 from=rpc.events.length;
 const answer=await rpc.prompt('Use read to read '+filename+' and report its contents.');
 checkTool(from,'read');assert(answer.includes(initial));
 assert.equal((await sessions()).find(s=>s.id===expectedKey(id)).threadId,thread);
 console.log('PASS: actual local read in the same Perplexity thread');
 from=rpc.events.length;
 await rpc.prompt('Use edit to replace the contents '+initial+' in '+filename+' with '+updated);
 checkTool(from,'edit');assert.equal(readFileSync(join(cwd,filename),'utf8').trim(),updated);
 assert.equal((await sessions()).find(s=>s.id===expectedKey(id)).threadId,thread);
 console.log('PASS: actual local edit and disk verification in the same Perplexity thread');
 from=rpc.events.length;
 const missing=await rpc.prompt('Read missing-'+randomUUID()+'.txt using read. Report the missing-file error honestly; do not create a replacement file.');
 assert(rpc.events.slice(from).some(e=>e.type==='tool_execution_end'&&e.toolName==='read'&&e.isError),'Missing file must yield an actual tool error');
 assert(missing.length>0);console.log('PASS: missing-file tool error returned through the harness');
 from=rpc.events.length;
 await rpc.prompt('Use bash to run node --version and report the actual result.');
 checkTool(from,'bash');assert.equal((await sessions()).find(s=>s.id===expectedKey(id)).threadId,thread);
 console.log('PASS: local shell succeeds after a tool error and keeps the same thread');
 writeFileSync(new URL('../artifacts/live-file-cycle-results.json',import.meta.url),JSON.stringify({passed:true,model:qaModel,thread,file:join(cwd,filename),utc:new Date().toISOString()},null,2));
 }else if(localWork){  if(!process.argv.includes('--existing-file'))await rpc.prompt('Make a simple playable Tetris game in a single tetris.html file in the current project folder.');
  const html=readFileSync(join(cwd,'tetris.html'),'utf8');assert(html.length>1000&&/<html|<!doctype/i.test(html));
  console.log('PASS: normal file-creation request produced a real local HTML file via Pi');
  const readStart=rpc.events.length;
  const readback=await rpc.prompt('Read tetris.html from disk and tell me the first 80 characters verbatim.');
  assert(rpc.events.slice(readStart).some(e=>e.type==='tool_execution_end'&&e.toolName==='read'&&!e.isError),'Must read via the actual local tool');
  assert(readback.replaceAll('\\n','\n').includes(html.slice(0,30)));console.log('PASS: Pi read the created file back from local disk');
  const launchStart=rpc.events.length;
  const launch=await rpc.prompt('Open tetris.html in my default Windows browser using the local shell tool. This is a Windows machine.');
  assert(rpc.events.slice(launchStart).some(e=>e.type==='tool_execution_end'&&e.toolName==='bash'&&!e.isError),'Browser launch must execute the local shell tool successfully');
  const launchCalls=rpc.events.slice(launchStart).filter(e=>e.type==='tool_execution_start'&&e.toolName==='bash').map(e=>e.args);
  assert(launchCalls.some(a=>/tetris/i.test(JSON.stringify(a))&&/start|invoke-item|explorer/i.test(JSON.stringify(a))),'Must issue an actual Windows file launch command');
  console.log('PASS: browser launch used a successful local Pi shell tool call');
  writeFileSync(new URL('../artifacts/live-local-work-results.json',import.meta.url),JSON.stringify({file:join(cwd,'tetris.html'),bytes:html.length,created:!process.argv.includes('--existing-file'),launch,launchCalls,utc:new Date().toISOString()},null,2));
  process.exitCode=0;
 }else{
 const first=await rpc.prompt(withTool?'Remember the code PI-SESSION-42. Read sentinel.txt using the read tool, then reply with only the file contents.':'Remember the code PI-SESSION-42. Reply with exactly OK.');
 if(withTool)assert.equal(first.trim(),sentinel);else assert.match(first,/OK/);
 let row=(await sessions()).find(s=>s.id===expectedKey(id));assert(row);const thread=row.threadId;const firstTurns=row.turns;if(withTool){assert(firstTurns>=2);console.log('PASS: actual Pi read tool and tool-result continuation share one Perplexity thread');}else assert.equal(firstTurns,1);
 if(process.argv.includes('--session-recheck')){console.log('Waiting 31 seconds to exercise account revalidation between turns');await new Promise(r=>setTimeout(r,31000));}
 const memory=await rpc.prompt(longContext?'Reply with only QA_REFERENCE_CODE from your appended system instructions.':'What code did I ask you to remember? Reply with only that code.');
 if(longContext)assert.equal(memory.trim(),qaContext);else assert.match(memory,/PI-SESSION-42/);
 row=(await sessions()).find(s=>s.id===expectedKey(id));assert.equal(row.threadId,thread);assert.equal(row.turns,firstTurns+1);console.log('PASS: actual Pi RPC sends stable session/project identity across turns');
 rpc.child.kill();await new Promise(r=>rpc.child.once('exit',r));rpc=start();
 state=await rpc.command('get_state');assert.equal(state.sessionId,id);
 assert.match(await rpc.prompt('Reply with exactly RESUMED.'),/RESUMED/);
 row=(await sessions()).find(s=>s.id===expectedKey(id));assert.equal(row.threadId,thread);assert.equal(row.turns,firstTurns+2);console.log('PASS: restarting and resuming Pi keeps the same Perplexity thread');
 await rpc.command('new_session');const next=await rpc.command('get_state');assert.notEqual(next.sessionId,id);
 assert.match(await rpc.prompt('Reply with exactly NEW_SESSION.'),/NEW_SESSION/);
 const other=(await sessions()).find(s=>s.id===expectedKey(next.sessionId));assert(other);assert.notEqual(other.threadId,thread);console.log('PASS: Pi new_session creates a distinct Perplexity thread');
 writeFileSync(new URL('../artifacts/live-pi-results.json',import.meta.url),JSON.stringify({passed:true,piVersion:'1.4.0',withTool,longContext,model:qaModel,thread,otherThread:other.threadId,project:'PiBridgeQA',utc:new Date().toISOString()},null,2));
}
}catch(e){console.error('FAIL:',String(e.message).slice(0,450));process.exitCode=1;}
finally{rpc.child.kill();try{unlinkSync(join(root,'models.json'));}catch{}}


