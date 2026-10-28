import {LlmAdapter,attributionHeaders} from '@deepseek-ai/dsh-llm';
import {settings,headers,requestBody,jsonRequest,chunks} from './transport.js';
export const inject=['llm'];
export const name='local-perplexity-bridge';
export const PROVIDER='perplexity-bridge';
export function apply(ctx,config={}){
 const cfg=settings(config);
 async function authenticated(){
  // The desktop Models page stores a reference rather than a process env var.
  // Resolve through the host service; never read or copy its credential files.
  const ref=config.apiKeyEnv??'LOCAL_PERPLEXITY_KEY';
  const stored=await ctx.get?.('credentials')?.resolve(ref);
  return stored?.value?settings(config,{[ref]:stored.value}):cfg;
 }
 class BridgeAdapter extends LlmAdapter{
  providerInfo(){return {id:PROVIDER,name:'Local Perplexity Bridge'};}
  providerRetryPolicy(){return {mode:'normal',maxRetries:0,retryableCodes:[],initialDelayMs:500,maxDelayMs:1000,jitterRatio:0};}
  async listModels(_provider,signal){
   const auth=await authenticated();
   const body=await jsonRequest(auth,'/models',{signal,headers:{...attributionHeaders(),Authorization:'Bearer '+auth.key()}});
   if(!Array.isArray(body.data))throw Error('Invalid bridge model catalogue');
   return body.data.filter(m=>typeof m.id==='string'&&/^perplexity-[\w-]+$/.test(m.id)).map(m=>({provider:PROVIDER,id:m.id,name:m.name||m.id,inputModalities:['text']}));
  }
  async resolveModel(provider,model,signal){signal?.throwIfAborted();const found=(await this.listModels(provider,signal)).find(m=>m.id===model);signal?.throwIfAborted();if(!found)throw Error('Model unavailable in the local bridge catalogue');return found;}
  async *stream(options){
   try{
    const auth=await authenticated();
    const body=requestBody(options);
    const result=await jsonRequest(auth,'/chat/completions',{method:'POST',headers:headers(auth,options,attributionHeaders()),body:JSON.stringify(body),signal:options.signal});
    yield* chunks(result,options.tools);
   }catch(error){yield {type:'finish',reason:{kind:options.signal?.aborted?'aborted':'error',failure:{code:options.signal?.aborted?'ABORTED':'PERPLEXITY_BRIDGE',message:error.message,...(error.status?{status:error.status}:{})}}};}
  }
 }
 // registerAdapter owns its Cordis disposal effect.
 ctx.llm.registerAdapter([PROVIDER],new BridgeAdapter());
}
