import {createHash} from 'node:crypto';
import {resolve,basename} from 'node:path';

export function settings(config={},env=process.env){
 const url=new URL(config.baseURL??'http://127.0.0.1:8082/v1');
 // Match the bridge's IPv4 binding; no userinfo, query, fragment or alternate path.
 if(url.protocol!=='http:'||url.hostname!=='127.0.0.1'||url.username||url.password||url.search||url.hash||url.pathname.replace(/\/$/,'')!=='/v1')throw Error('Bridge baseURL must be http://127.0.0.1:<port>/v1');
 const ref=config.apiKeyEnv??'LOCAL_PERPLEXITY_KEY';
 const timeoutMs=config.timeoutMs??180000;
 if(!Number.isInteger(timeoutMs)||timeoutMs<1000||timeoutMs>1800000)throw Error('timeoutMs must be 1000–1800000');
 const cwd=resolve(config.projectPath??process.cwd());
 return {baseURL:url.href.replace(/\/$/,''),timeoutMs,cwd,key:()=>{
  const key=env[ref]?.trim();if(!key||!/^lpb_[\x21-\x7e]+$/.test(key))throw Error('Set the configured environment variable to a local bridge key');return key;
 }};
}
export function headers(cfg,options,attribution={}){
 if(typeof options.sessionId!=='string'||!options.sessionId.trim())throw Error('DeepSeek Harness did not supply a sessionId; no generation sent');
 if(options.purpose)throw Error('Auxiliary title/compaction requests require another provider; refusing to pollute the bridge conversation');
 return {...attribution,Authorization:'Bearer '+cfg.key(),'Content-Type':'application/json',
  'X-Perplexity-Session-Id':'dsh-'+options.sessionId,
  'X-Perplexity-Project-Id':createHash('sha256').update(process.platform==='win32'?cfg.cwd.toLowerCase():cfg.cwd).digest('hex'),
  'X-Perplexity-Project-Name':encodeURIComponent([...basename(cfg.cwd)].slice(0,48).join(''))};
}
function text(blocks){
 if(!Array.isArray(blocks))throw Error('Expected harness content blocks');
 return blocks.map(b=>{if(b.type==='text')return b.text;if(b.type==='reasoning')return '';throw Error('Unsupported harness content block: '+b.type)}).join('\n');
}
export function requestBody(options){
 if(options.reasoningEffort!==undefined)throw Error('Explicit reasoning controls are unsupported; select a discovered thinking model ID');
 const messages=[];
 if(options.system)messages.push({role:'system',content:options.system});
 for(const m of options.messages){
  if(m.role==='assistant'){
   const calls=m.content.filter(b=>b.type==='tool-call');
   messages.push({role:'assistant',content:text(m.content.filter(b=>b.type!=='tool-call'))||null,
    ...(calls.length?{tool_calls:calls.map(b=>({id:b.id,type:'function',function:{name:b.name,arguments:b.arguments}}))}:{})});
  }else if(m.role==='user'&&m.content.some(b=>b.type==='tool-result')){
   // Published rc.1 represents tool results inside user content; newer source
   // uses first-class tool-role messages. Preserve block order for both.
   for(const block of m.content){
    if(block.type==='tool-result'){
     if(!block.toolCallId)throw Error('Tool result is missing toolCallId');
     messages.push({role:'tool',tool_call_id:block.toolCallId,content:text(block.content)});
    }else messages.push({role:'user',content:text([block])});
   }
  }else if(m.role==='tool'){
   if(!m.toolCallId)throw Error('Tool result is missing toolCallId');
   messages.push({role:'tool',tool_call_id:m.toolCallId,content:text(m.content)});
  }else if(['system','developer','user'].includes(m.role))messages.push({role:m.role,content:text(m.content)});
  else throw Error('Unsupported harness message role');
 }
 return {model:options.model,messages,stream:false,
  ...(options.tools?.length?{tools:options.tools.map(t=>({type:'function',function:{name:t.name,description:t.description,parameters:t.parameters}}))}:{}),
  ...(options.maxTokens===undefined?{}:{max_tokens:options.maxTokens})};
}
export async function jsonRequest(cfg,path,init={},fetchImpl=fetch){
 const signal=AbortSignal.any([AbortSignal.timeout(cfg.timeoutMs),...(init.signal?[init.signal]:[])]);
 const response=await fetchImpl(cfg.baseURL+path,{...init,signal,redirect:'error'});
 if(!response.ok){const error=Error('Local Perplexity Bridge HTTP '+response.status);error.status=response.status;throw error;}
 return response.json();
}
export function chunks(body,tools=[]){
 const choice=body.choices?.[0],message=choice?.message;
 if(!message||!['stop','tool_calls','length'].includes(choice.finish_reason))throw Error('Incomplete bridge response');
 const allowed=new Set(tools.map(t=>t.name)),blocks=[];
 if(message.content!==null&&message.content!==undefined){if(typeof message.content!=='string')throw Error('Invalid text response');if(message.content)blocks.push({type:'text',text:message.content});}
 const ids=new Set();
 for(const call of message.tool_calls??[]){
  const fn=call.function;if(!call.id||ids.has(call.id)||!allowed.has(fn?.name)||typeof fn.arguments!=='string')throw Error('Invalid or undeclared bridge tool call');
  const args=JSON.parse(fn.arguments);if(!args||Array.isArray(args)||typeof args!=='object')throw Error('Tool arguments must be an object');ids.add(call.id);
  blocks.push({type:'tool-call',id:call.id,name:fn.name,arguments:fn.arguments});
 }
 if(!blocks.length)throw Error('Empty bridge response');
 return [...blocks.flatMap((block,index)=>[{type:'block-start',index,blockType:block.type},
  block.type==='text'?{type:'text-delta',index,text:block.text}:{type:'tool-call-delta',index,id:block.id,name:block.name,argumentsDelta:block.arguments},
  {type:'block-end',index,block}]),{type:'finish',reason:{kind:choice.finish_reason==='length'?'max-tokens':ids.size?'tool-calls':'stop'}}];
 // Deliberately omit usage: the bridge reports null; zero would invent accounting.
}
