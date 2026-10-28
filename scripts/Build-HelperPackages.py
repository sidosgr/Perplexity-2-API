"""Build self-contained helper ZIPs and the native DeepSeek bundle tarball."""
from pathlib import Path
import argparse, hashlib, json, shutil, subprocess, zipfile

ROOT=Path(__file__).resolve().parent.parent
parser=argparse.ArgumentParser()
parser.add_argument('--output-dir',type=Path)
args=parser.parse_args()
version='0.4.0'
output=(args.output_dir or ROOT/'artifacts/helper-packages'/version).resolve()
output.mkdir(parents=True,exist_ok=True)
installer=(ROOT/'scripts/helper-installer.mjs').read_bytes()
license=(ROOT/'LICENSE').read_bytes()

guides={
'pi':'''# Pi helper · package 0.4.0

Extract the entire ZIP, then double-click Install.cmd (Node.js must be installed),
or run `node Install.mjs` from a terminal. The installer copies the helper into
`~/.pi/agent/extensions/perplexity-bridge.ts`, backing up an existing helper.
Restart Pi or run `/reload`. It does not edit models.json, auth.json or keys.

Recommended initial provider setup: in Local Perplexity Bridge use App → Set up
Pi session tracking. Alternatively merge the supplied provider-example.json into
your existing ~/.pi/agent/models.json; do not replace other providers. Set the
LOCAL_PERPLEXITY_KEY environment variable to your generated local bridge key
before launching Pi. The key is local to the bridge, not a Perplexity API key.

Keep the bridge connected on port 8082. Select perplexity-2-api/perplexity-web.
The helper attaches Pi's real session/project identity to every request.
/perplexity-thread shows the linked thread. /perplexity-models refreshes choices;
run /reload afterwards. Verify a new chat, follow-up and restart/resume in the
bridge Sessions tab. Enable Pi's read/write/edit/shell tools for local work.

To remove: close Pi and remove only the installed perplexity-bridge.ts helper;
restore the .backup file if replacing an earlier helper was unintended.

Tested helper lifecycle: Pi CLI 1.4.0. Latest live provider model:
perplexity-claude55sonnetthinking, Pro; availability depends on account discovery.
''',
'opencode':'''# OpenCode helper · package 0.4.0

Extract the entire ZIP, then double-click Install.cmd (Node.js required),
or run `node Install.mjs`. The installer copies the plugin into
~/.config/opencode/plugins/perplexity-sessions.ts (honors XDG_CONFIG_HOME),
backing up an existing helper. Restart OpenCode after installation.
Provider configuration and credentials are not changed by this installer.

Merge provider-example.json into your existing OpenCode config, preserving
other providers. Use the provider connection/authentication flow to store a
generated local bridge key. Keep the bridge connected on port 8082 and select
perplexity-2-api/perplexity-web. Refresh/populate explicit model IDs from the app.

The chat.headers hook attaches OpenCode's actual session ID and workspace hash.
Verify same-chat continuation and new-chat isolation in the bridge Sessions tab.
Harness tools/permissions control local execution; enable them as appropriate.

To remove: close OpenCode and remove only perplexity-sessions.ts, restoring its
.backup file if needed. Hook fixtures and OpenCode-compatible SDK tool round trips
passed; full live OpenCode UI execution remains unverified.
'''}

packages=[]
for harness in ['pi','opencode']:
 source='perplexity-bridge.mjs' if harness=='pi' else 'perplexity-sessions.mjs'
 provider={'providers':{'perplexity-2-api':{'baseUrl':'http://127.0.0.1:8082/v1','api':'openai-completions','apiKey':'LOCAL_PERPLEXITY_KEY','authHeader':True,'models':[{'id':'perplexity-web','name':'Perplexity configured default','reasoning':False,'input':['text']}]}}} if harness=='pi' else {'provider':{'perplexity-2-api':{'npm':'@ai-sdk/openai-compatible','name':'Local Perplexity Bridge','options':{'baseURL':'http://127.0.0.1:8082/v1'},'models':{'perplexity-web':{'name':'Perplexity configured default'}}}}}
 files={source:(ROOT/'integrations'/harness/source).read_bytes(),'Install.mjs':installer,
 'Install.cmd':b'@echo off\r\nnode "%~dp0Install.mjs"\r\nif errorlevel 1 echo Installation failed. Read the error above.\r\npause\r\n',
 'README.md':guides[harness].encode(),'LICENSE':license,
 'helper-package.json':json.dumps({'harness':harness,'version':version},indent=2).encode(),
 'provider-example.json':json.dumps(provider,indent=2).encode()}
 archive=output/f'LocalPerplexityBridge-{harness}-helper-{version}.zip'
 with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as z:
  for name,data in files.items():z.writestr(name,data)
 packages.append(archive)

deepseek=ROOT/'integrations/deepseek-harness'
manifest=json.loads((deepseek/'package.json').read_text())
dversion=manifest['version']
archive=output/f'LocalPerplexityBridge-deepseek-harness-{dversion}.zip'
with zipfile.ZipFile(archive,'w',zipfile.ZIP_DEFLATED) as z:
 for path in deepseek.rglob('*'):
  if path.is_file() and 'node_modules' not in path.parts:z.write(path,path.relative_to(deepseek))
 z.writestr('LICENSE',license)
packages.append(archive)
run=subprocess.run([shutil.which('npm.cmd') or shutil.which('npm'),'pack',str(deepseek),'--pack-destination',str(output),'--json'],capture_output=True,text=True,check=True)
pack=json.loads(run.stdout)[0]
packages.append(output/pack['filename'])
(output/'SHA256SUMS.txt').write_text(''.join(hashlib.sha256(p.read_bytes()).hexdigest()+'  '+p.name+'\n' for p in packages))
(output/'README.txt').write_text('Local Perplexity Bridge helper packages\n\nPi and OpenCode: extract ZIP, run Install.cmd or node Install.mjs, then configure the provider using README.md.\nDeepSeek Harness: extract ZIP and use Plugins > Add plugin with the absolute extracted folder path. Install and Enable now. The .tgz is the native npm-format alternative. Read its README before configuring the key reference. No AI is required for installation.\n\nThese packages contain helper source, documentation and installers, never accounts or keys. The bridge app must be installed and running separately.\n')
print('\n'.join(str(p) for p in packages))
combined=output/f'LocalPerplexityBridge-All-Helpers-{version}.zip'
with zipfile.ZipFile(combined,'w',zipfile.ZIP_DEFLATED) as z:
 for path in [*packages,output/'SHA256SUMS.txt',output/'README.txt']:z.write(path,path.name)
with (output/'SHA256SUMS.txt').open('a') as sums:sums.write(hashlib.sha256(combined.read_bytes()).hexdigest()+'  '+combined.name+'\n')
print(combined)
