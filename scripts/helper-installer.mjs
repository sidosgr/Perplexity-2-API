// Distributed with Pi/OpenCode helper ZIPs. No credential/config-file edits.
import {readFileSync,existsSync,mkdirSync,copyFileSync} from 'node:fs';
import {dirname,join,resolve} from 'node:path';
import {homedir} from 'node:os';
import {fileURLToPath} from 'node:url';
const root=dirname(fileURLToPath(import.meta.url));
const manifest=JSON.parse(readFileSync(join(root,'helper-package.json'),'utf8'));
if(!['pi','opencode'].includes(manifest.harness))throw Error('Unknown helper package');
const flag=process.argv.indexOf('--target-dir');
if(flag>=0&&!process.argv[flag+1])throw Error('--target-dir needs a folder');
const folder=flag>=0?resolve(process.argv[flag+1]):manifest.harness==='pi'?join(homedir(),'.pi','agent','extensions'):join(process.env.XDG_CONFIG_HOME||join(homedir(),'.config'),'opencode','plugins');
const filename=manifest.harness==='pi'?'perplexity-bridge.ts':'perplexity-sessions.ts';
const source=join(root,manifest.harness==='pi'?'perplexity-bridge.mjs':'perplexity-sessions.mjs');
const target=join(folder,filename);
mkdirSync(folder,{recursive:true});
if(existsSync(target)){
 const backup=target+'.backup-'+Date.now();copyFileSync(target,backup);console.log('Previous helper backed up: '+backup);
}
copyFileSync(source,target);
console.log('Installed '+manifest.harness+' helper '+manifest.version+': '+target);
console.log('Restart the harness (or /reload in Pi). See README.md for provider/key setup.');
console.log('Existing provider settings and credentials were not changed.');
