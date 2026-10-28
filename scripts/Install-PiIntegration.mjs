import {homedir} from 'node:os';
import {join,resolve} from 'node:path';
import {mkdirSync,copyFileSync} from 'node:fs';
import {refreshLocalModels} from '../integrations/pi/perplexity-bridge.mjs';
const agentDir=join(homedir(),'.pi','agent');
mkdirSync(join(agentDir,'extensions'),{recursive:true});
copyFileSync(resolve('integrations/pi/perplexity-bridge.mjs'),join(agentDir,'extensions','perplexity-bridge.ts'));
console.log('Installed the local Pi session helper.');
console.log('Refreshed '+await refreshLocalModels(agentDir)+' model selections. Reload Pi to apply.');
