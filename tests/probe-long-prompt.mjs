import assert from 'node:assert/strict';
import {readFileSync} from 'node:fs';import {homedir} from 'node:os';import {join} from 'node:path';import {createRequire} from 'node:module';
const require=createRequire(new URL('../artifacts/sdk/package.json',import.meta.url)),OpenAI=require('openai');const p=JSON.parse(readFileSync(join(homedir(),'.pi/agent/models.json'),'utf8')).providers['perplexity-2-api'];const client=new OpenAI({baseURL:p.baseUrl,apiKey:p.apiKey,maxRetries:0,timeout:180000});
try{const result=await client.chat.completions.create({model:'perplexity-gpt6_1_sol_thinking',messages:[{role:'system',content:'Neutral padding follows. '+'neutral context. '.repeat(4000)},{role:'user',content:'Reply with exactly LONG_OK.'}]});console.log('Synthetic long-prompt reply:',result.choices[0].message.content.slice(0,160));}
catch(e){console.log('Synthetic long-prompt status:',e.status,String(e.message).slice(0,180));}
console.log('Transport metrics:',readFileSync(join(homedir(),'AppData/Local/LocalPerplexityBridge/last-prompt-metrics.json'),'utf8'));
