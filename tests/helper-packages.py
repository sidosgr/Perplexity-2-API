"""Verify built archives and run helper installers in isolated temporary folders."""
from pathlib import Path
import hashlib, json, subprocess, sys, tempfile, zipfile, tarfile

folder=Path(sys.argv[1])
for line in (folder/'SHA256SUMS.txt').read_text().splitlines():
 expected,name=line.split('  ',1)
 assert hashlib.sha256((folder/name).read_bytes()).hexdigest()==expected,name
with tempfile.TemporaryDirectory(prefix='perplexity-helper-test-') as temp:
 for harness in ['pi','opencode']:
  source=Path(temp)/harness;source.mkdir()
  with zipfile.ZipFile(folder/f'LocalPerplexityBridge-{harness}-helper-0.4.0.zip') as z:
   assert z.testzip() is None;z.extractall(source)
  config=json.loads((source/'provider-example.json').read_text())
  assert 'perplexity-2-api' in config.get('providers',config.get('provider',{}))
  target=source/'isolated-target'
  for _ in range(2):subprocess.run(['node',str(source/'Install.mjs'),'--target-dir',str(target)],check=True,capture_output=True)
  filename='perplexity-bridge.ts' if harness=='pi' else 'perplexity-sessions.ts'
  original='perplexity-bridge.mjs' if harness=='pi' else 'perplexity-sessions.mjs'
  assert (target/filename).read_bytes()==(source/original).read_bytes()
  backups=list(target.glob('*.backup-*'));assert len(backups)==1
  assert backups[0].read_bytes()==(source/original).read_bytes()
  assert len(list(target.iterdir()))==2
  print('PASS:',harness,'archive, isolated installation and previous-file backup')
 for path in folder.glob('*deepseek*.zip'):
  with zipfile.ZipFile(path) as z:
   assert z.testzip() is None
   assert {'package.json','cordis.patch.yml','index.js','transport.js','README.md','LICENSE'}<=set(z.namelist())
 for path in folder.glob('*.tgz'):
  with tarfile.open(path) as tar:
   names=set(tar.getnames());assert 'package/index.js' in names
   assert not any('node_modules' in n for n in names)
 print('PASS: DeepSeek ZIP/native tarball layout and all SHA-256 checksums')
