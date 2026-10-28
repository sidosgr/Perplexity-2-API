globalThis.LocalPerplexityShape=function(v,depth=0){
 if(depth>7)return '<depth-limit>';
 if(v===null)return null;
 if(Array.isArray(v))return v.slice(0,4).map(x=>LocalPerplexityShape(x,depth+1));
 if(typeof v==='object')return Object.fromEntries(Object.keys(v).slice(0,45).filter(k=>/^[a-zA-Z_][a-zA-Z0-9_]{0,60}$/.test(k)).map(k=>[k,LocalPerplexityShape(v[k],depth+1)]));
 return typeof v==='string'?'<string:'+v.length+'>':typeof v;
};
// Experimental same-origin transport. Credentials stay in the WebView2 profile.
globalThis.LocalPerplexityLimitKind = function(e) {
 const code=String(e?.code||e?.type||'').toLowerCase(), msg=typeof e?.message==='string'?e.message.toLowerCase():'';
 if(/^(usage_limit_error|quota_exceeded|usage_limit_reached|daily_limit_reached)$/.test(code)||/(?:usage|daily|weekly|monthly) (?:limit|quota).{0,40}(?:reached|exceeded|exhausted)/.test(msg))return 'usage';
 if(/^(rate_limit_error|too_many_requests|rate_limited)$/.test(code))return 'rate';
 return null;
};
globalThis.LocalPerplexitySseParser=class {
 constructor(expectedPrompt=null){this.expectedPrompt=expectedPrompt;this.echoLength=null;this.echoEqual=null;this.buffer='';this.text='';this.chunks=[];this.stopped=false;this.sources=[];this.shapes=[];this.markdownSeen=false;this.planTier=null;this.thread={};}
 feed(chunk,eof=false){
  this.buffer+=chunk;if(this.buffer.length>1024*1024)throw Error('event_too_large');let match;
  while(!this.stopped&&(match=/\r?\n\r?\n/.exec(this.buffer))){
   const frame=this.buffer.slice(0,match.index);this.buffer=this.buffer.slice(match.index+match[0].length);
   const lines=frame.split(/\r?\n/), data=lines.filter(l=>l.startsWith('data:')).map(l=>l.slice(5).replace(/^ /,'')).join('\n'), event=lines.find(l=>l.startsWith('event:'))?.slice(6).trim();
   if(!data)continue;if(data==='[DONE]'){this.stopped=true;continue;}
   let v;try{v=JSON.parse(data);}catch{throw Error('invalid_stream_json');}
   if(typeof v.query_str==='string'){this.echoLength=this.echoLength===null?v.query_str.length:Math.min(this.echoLength,v.query_str.length);if(this.expectedPrompt!==null)this.echoEqual=this.echoEqual!==false&&v.query_str===this.expectedPrompt;}
   for(const key of ['backend_uuid','context_uuid','read_write_token','thread_url_slug'])if(typeof v[key]==='string'&&v[key].length<=1024)this.thread[key]=v[key];
   if(['free','pro','max'].includes(v._extras?.subscription_tier))this.planTier=v._extras.subscription_tier;
   if(this.shapes.length<6)this.shapes.push(globalThis.LocalPerplexityShape(v));
   if(v.error||event==='error'||v.status==='failed'){const k=LocalPerplexityLimitKind(v.error||v);throw Error(k==='usage'?'usage_limited':k==='rate'?'rate_limited':'upstream_stream_error');}
   if(typeof v.text==='string'){try{v={...v,...JSON.parse(v.text)};}catch{}}
   if(typeof v.answer==='string')this.text=v.answer;
   const blocks=Array.isArray(v.blocks)?v.blocks:[];
   if(blocks.some(b=>b.intended_usage==='ask_text_0_markdown'))this.markdownSeen=true;
   for(const b of blocks){
    if(b.intended_usage==='ask_text_0_markdown'||(b.intended_usage==='ask_text'&&!this.markdownSeen)){
     const md=b.markdown_block;
     if(typeof md?.answer==='string'){this.text=md.answer;this.chunks=[md.answer];}
     else if(Array.isArray(md?.chunks)&&md.chunks.every(x=>typeof x==='string')){this.snapshot(md);}
     for(const p of b.diff_block?.patches||[])this.patch(p);
    }
    if(b.intended_usage==='web_results'&&Array.isArray(b.web_result_block?.web_results))this.sources=b.web_result_block.web_results.filter(s=>typeof s.url==='string'&&/^https?:\/\//.test(s.url)).map(s=>s.url);
   }
   if(v.final_sse_message===true||(!blocks.length&&v.status==='completed'&&typeof v.answer==='string')||event==='end_of_stream')this.stopped=true;
  }
  if(eof&&!this.stopped)throw Error('incomplete_stream');
 }
 snapshot(md){
  const offset=md.chunk_starting_offset??0;
  if(!Number.isInteger(offset)||offset<0||offset>this.chunks.length)throw Error('unsupported_stream_patch');
  if(offset===0)this.chunks=[...md.chunks];else this.chunks.splice(offset,md.chunks.length,...md.chunks);
  this.text=this.chunks.join('');
 }
 patch(p){
  const path=p.path||'';if(!['add','replace','remove'].includes(p.op))throw Error('unsupported_stream_patch');
  if((path===''||path==='/'||path==='/markdown_block')&&p.value&&typeof p.value==='object'){
   if(Array.isArray(p.value.chunks)&&p.value.chunks.every(x=>typeof x==='string'))this.snapshot(p.value);
   else if(typeof p.value.answer==='string')this.chunks=[p.value.answer];else throw Error('unsupported_stream_patch');
  }else if(path==='/chunks'&&Array.isArray(p.value)&&p.value.every(x=>typeof x==='string'))this.chunks=[...p.value];
  else if((path==='/answer'||path==='/markdown_block/answer')&&typeof p.value==='string')this.chunks=[p.value];
  else if(/^\/chunks\/(\d+|-)$/.test(path)){
   const k=path.split('/')[2],i=k==='-'?this.chunks.length:Number(k);
   if(i>this.chunks.length||(p.op!=='add'&&i>=this.chunks.length))throw Error('unsupported_stream_patch');
   if(p.op==='remove')this.chunks.splice(i,1);else{if(typeof p.value!=='string')throw Error('unsupported_stream_patch');if(p.op==='add')this.chunks.splice(i,0,p.value);else this.chunks[i]=p.value;}
  }else if(path==='/progress'||path==='/chunk_starting_offset')return;
  else throw Error('unsupported_stream_patch');this.text=this.chunks.join('');
 }
};
globalThis.LocalPerplexityExtractModels=function(root){
 const found=new Map([['perplexity-auto',{id:'perplexity-auto',name:'Automatic — website default'}]]);
 for(const row of root.querySelectorAll('[data-model-id]')){
  const id=row.getAttribute('data-model-id'),label=(row.textContent||'').replace(/\s+/g,' ').trim();
  if(!/^[a-zA-Z0-9_-]{1,80}$/.test(id||'')||!label||row.disabled||row.getAttribute('aria-disabled')==='true'||row.hasAttribute('data-disabled')||/upgrade|unlock|research|labs/i.test(label))continue;
  if(typeof row.getBoundingClientRect==='function'&&row.getBoundingClientRect().width===0)continue;
  found.set('perplexity-'+id,{id:'perplexity-'+id,name:label.slice(0,120)});
 }return [...found.values()];
};
globalThis.LocalPerplexityModelsFromConfig=function(config,tier){
 const rank={free:0,pro:1,max:2}, level=rank[tier]??0;
 const choices=[{id:'perplexity-auto',name:'Automatic — website default',reasoning:false}];
 for(const row of Array.isArray(config.config)?config.config:[]){
  if(row.audience||row.disabled||!(row.subscription_tier in rank)||rank[row.subscription_tier]>level)continue;
  for(const field of ['non_reasoning_model','reasoning_model']){
   const id=row[field],info=config.models?.[id];
   if(typeof id!=='string'||!/^[a-zA-Z0-9_-]{1,80}$/.test(id)||!info||info.mode!=='search'||info.replaced_by||typeof info.label!=='string')continue;
   choices.push({id:'perplexity-'+id,name:info.label,reasoning:field==='reasoning_model'});
  }
 }
 return [...new Map(choices.map(m=>[m.id,m])).values()];
};
globalThis.LocalPerplexityDiscoverModels=async function(signal,force=false){
 const cached=globalThis.localPerplexityModelCache;
 if(!force&&cached&&Date.now()-cached.time<30000)return cached;
 const auth=await fetch('/api/auth/session',{credentials:'same-origin',redirect:'error',signal});
 if(!auth.ok)throw Error('http_'+auth.status);
 const session=await auth.json();if(!session.user?.id)throw Error('http_401');
 const tier=['free','pro','max'].includes(session.user.subscription_tier)?session.user.subscription_tier:'free';
 const r=await fetch('/rest/models/config?config_schema=v1&version=2.18&source=default',{credentials:'same-origin',redirect:'error',signal});
 if(!r.ok)throw Error('http_'+r.status);
 const data=await r.json();if(!data.models||!Array.isArray(data.config))throw Error('no_available_models');
 const result={time:Date.now(),account:session.user.id,tier,models:LocalPerplexityModelsFromConfig(data,tier),automatic:tier==='free'?'default':data.default_models?.search||'default'};
 globalThis.localPerplexityModelCache=result;return result;
};
globalThis.localPerplexityBridgeRun=async function(input,signal){
 if(location.origin!=='https://www.perplexity.ai')throw Error('wrong_origin');
 const failure=async r=>{
  let kind='unknown',reason='unspecified';
  if(r.headers.get('cf-mitigated')==='challenge')kind='browser_challenge';
  else if((r.headers.get('content-type')||'').includes('text/html'))kind='html_response';
  else{try{const body=await r.text();if(body.length<=32768){const v=JSON.parse(body),e=v.error||v,k=LocalPerplexityLimitKind(e);if(k){const e2=Error(k==='usage'?'usage_limited':'rate_limited');const retry=Number(r.headers.get('retry-after'));if(Number.isFinite(retry)&&retry>=1&&retry<=86400)e2.retryAfterSeconds=retry;return e2;}if(/model/i.test(e.message||''))reason='model_restriction';}}catch{}}
  const error=Error('http_'+r.status+'|'+kind+'|'+reason);
  const retry=r.headers.get('retry-after');
  const seconds=/^\d+$/.test(retry||'')?Number(retry):Math.ceil((Date.parse(retry)-Date.now())/1000);
  if(Number.isFinite(seconds)&&seconds>=1&&seconds<=86400)error.retryAfterSeconds=seconds;
  return error;
 };
 if(input.operation==='session'){
  const r=await fetch('/api/auth/session',{credentials:'same-origin',redirect:'error',signal});if(!r.ok)throw await failure(r);
  if(!(r.headers.get('content-type')||'').includes('application/json'))throw Error('browser_verification');
  const data=await r.json(),identity=data?.user?.id||data?.user?.email;if(typeof identity!=='string'||!identity)throw Error('http_401');
  const bytes=new Uint8Array(await crypto.subtle.digest('SHA-256',new TextEncoder().encode(identity))),h=[...bytes.slice(0,16)].map(x=>x.toString(16).padStart(2,'0')).join('');
  if(globalThis.localPerplexityModelCache?.account!==data.user.id)globalThis.localPerplexityModelCache=null;
  return [{id:`${h.slice(0,8)}-${h.slice(8,12)}-${h.slice(12,16)}-${h.slice(16,20)}-${h.slice(20)}`,name:'Signed-in Perplexity account',plan_tier:['free','pro','max'].includes(data.user.subscription_tier)?data.user.subscription_tier:null}];
 }
 if(input.operation==='inspect_models'){
  const response=await fetch('/rest/models/config?config_schema=v1&version=2.18&source=default',{credentials:'same-origin',redirect:'error',signal});
  if(!response.ok)throw await failure(response);
  const config=await response.json();
  const triggers=[...document.querySelectorAll('button')].filter(b=>/^(Model|Best)$|select model|choose model/i.test((b.textContent||'').trim()+' '+(b.getAttribute('aria-label')||'')));
  let trigger=triggers.find(b=>b.getBoundingClientRect().width>0);const opened=trigger&&trigger.getAttribute('aria-expanded')!=='true';
  if(opened){trigger.click();await new Promise(r=>setTimeout(r,700));}
  const rows=[...document.querySelectorAll('[role="menuitem"],[role="menuitemradio"],[role="option"]')].filter(r=>r.getBoundingClientRect().width>0).map(r=>({label:r.textContent,disabled:r.getAttribute('aria-disabled'),attributes:[...r.attributes].filter(a=>/^(data-|aria-)/.test(a.name)).map(a=>[a.name,a.value])}));
  if(opened&&trigger.getAttribute('aria-expanded')==='true')trigger.click();
  const sessionResponse=await fetch('/api/auth/session',{credentials:'same-origin',signal});
  return {config,rows,sessionShape:LocalPerplexityShape(await sessionResponse.json())};
 }
 if(input.operation==='models')return (await LocalPerplexityDiscoverModels(signal,input.force===true)).models;
 if(input.operation!=='complete')throw Error('unsupported_operation');
 if(input.prompt.length>39000)throw Error('prompt_too_large');
 const catalog=await LocalPerplexityDiscoverModels(signal),available=catalog.models;
 if(input.model!=='perplexity-auto'&&!available.some(m=>m.id===input.model))throw Error('http_403|model_unavailable|model_restriction');
 const payload={query_str:input.prompt,params:{dsl_query:input.prompt,attachments:[],language:navigator.language||'en-US',timezone:Intl.DateTimeFormat().resolvedOptions().timeZone||'UTC',search_focus:'internet',sources:['web'],frontend_uuid:crypto.randomUUID(),frontend_context_uuid:input.frontend_context_uuid||crypto.randomUUID(),mode:input.model==='perplexity-auto'&&catalog.automatic==='default'?'concise':'copilot',model_preference:input.model==='perplexity-auto'?catalog.automatic:input.model.slice(11),is_related_query:false,is_sponsored:false,prompt_source:'user',query_source:'home',is_incognito:false,use_schematized_api:true,send_back_text_in_streaming_api:false,supported_block_use_cases:['answer_modes','media_items','knowledge_cards','search_result_widgets','inline_images','inline_assets','pending_followups'],client_coordinates:null,mentions:[],skip_search_enabled:true,source:'default',version:'2.18'}};
 if(input.cursor){payload.params.query_source='followup';payload.params.followup_source='link';payload.params.last_backend_uuid=input.cursor.backend_uuid;payload.params.read_write_token=input.cursor.read_write_token;payload.params.frontend_context_uuid=null;payload.params.dsl_query=null;}
 const r=await fetch('/rest/sse/perplexity_ask',{method:'POST',credentials:'same-origin',redirect:'error',signal,headers:{'Content-Type':'application/json',Accept:'text/event-stream','X-Request-ID':crypto.randomUUID(),'X-App-ApiVersion':'2.18','X-App-ApiClient':'default','X-Perplexity-Request-Reason':'perplexity-query-state-provider'},body:JSON.stringify(payload)});
 if(!r.ok)throw await failure(r);if(!(r.headers.get('content-type')||'').includes('text/event-stream')||!r.body)throw Error('browser_verification');
 const parser=new LocalPerplexitySseParser(input.prompt),reader=r.body.getReader(),decoder=new TextDecoder();let size=0;
 try{while(!parser.stopped){const {value,done}=await reader.read();if(done){parser.feed(decoder.decode(),true);break;}size+=value.length;if(size>4*1024*1024)throw Error('response_too_large');parser.feed(decoder.decode(value,{stream:true}));}if(!parser.text)throw Error('no_text_output');return {text:parser.text,model:input.model,sources:parser.sources,plan_tier:parser.planTier,thread:parser.thread,transport:{sent_characters:input.prompt.length,echoed_characters:parser.echoLength,echo_equal:parser.echoEqual}};}
 catch(e){e.diagnostic=parser.shapes;throw e;}
 finally{await reader.cancel().catch(()=>{});}
};
if(typeof module!=='undefined')module.exports={LocalPerplexitySseParser:globalThis.LocalPerplexitySseParser,LocalPerplexityExtractModels:globalThis.LocalPerplexityExtractModels,LocalPerplexityModelsFromConfig:globalThis.LocalPerplexityModelsFromConfig};
