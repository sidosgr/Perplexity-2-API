"""Build the offline HTML guide using only Python's standard library."""
from pathlib import Path
import html, re

ROOT = Path(__file__).resolve().parent.parent

def inline(value):
    value = html.escape(value)
    value = re.sub(r'`([^`]+)`', r'<code>\1</code>', value)
    value = re.sub(r'\*\*([^*]+)\*\*', r'<strong>\1</strong>', value)
    return re.sub(r'\[([^\]]+)\]\(([^)]+)\)', r'<a href="\2">\1</a>', value)

def render(text, prefix):
    out, code, table, listing = [], None, False, False
    for line in text.splitlines():
        if line.startswith('```'):
            if code is None: code = []
            else:
                out.append('<div class="code"><button class="copy" type="button">Copy</button><pre><code>'+html.escape('\n'.join(code))+'</code></pre></div>')
                code = None
            continue
        if code is not None:
            code.append(line); continue
        is_table = line.startswith('|')
        is_list = bool(re.match(r'^(?:- |\d+\. )', line))
        if table and not is_table: out.append('</tbody></table></div>'); table=False
        if listing and not is_list: out.append('</ul>'); listing=False
        if is_table:
            cells = [x.strip() for x in line.strip('|').split('|')]
            if all(re.fullmatch(r'[:\- ]+',x) for x in cells): continue
            if not table: out.append('<div class="table"><table><tbody>'); table=True
            out.append('<tr>'+''.join('<td>'+inline(x)+'</td>' for x in cells)+'</tr>'); continue
        if is_list:
            if not listing: out.append('<ul>'); listing=True
            out.append('<li>'+inline(re.sub(r'^(?:- |\d+\. )','',line))+'</li>');continue
        if line.startswith('#'):
            level=min(len(line)-len(line.lstrip('#'))+1,4)
            title=line.lstrip('# ').strip()
            out.append(f'<h{level}>'+inline(title)+f'</h{level}>')
        elif line.startswith('>'): out.append('<blockquote>'+inline(line.lstrip('> '))+'</blockquote>')
        elif line.strip(): out.append('<p>'+inline(line)+'</p>')
    if table:out.append('</tbody></table></div>')
    if listing:out.append('</ul>')
    result='\n'.join(out)
    # Markdown sections originally in docs/ use paths relative to that folder.
    if prefix: result=result.replace('href="../','href="').replace('href="ACCOUNT-', 'href="docs/ACCOUNT-')
    return result

sections=[('overview','Start here','docs/APP-GUIDE.md'),('accounts','Account setup','docs/ACCOUNT-SETUP.md'),('harnesses','Harness development','docs/HARNESS-DEVELOPMENT.md'),('tests','Test evidence','docs/TEST-REPORT-2026-10-28.md')]
nav=''.join(f'<a href="#{key}"><span>0{i+1}</span>{label}</a>' for i,(key,label,_) in enumerate(sections))
body=''.join(f'<section id="{key}"><div class="eyebrow">0{i+1} / {label}</div>'+render((ROOT/file).read_text(encoding='utf-8-sig'),True)+'</section>' for i,(key,label,file) in enumerate(sections))
page='''<!doctype html><html lang="en" data-theme="dark"><head><meta charset="utf-8"><meta name="viewport" content="width=device-width, initial-scale=1"><title>Local Perplexity Bridge · Handbook</title>
<style>
:root{color-scheme:dark;--bg:#0b1017;--panel:#121b26;--text:#e4edf7;--muted:#9eafc3;--line:#293647;--accent:#63dec9;--code:#080e15}html[data-theme=light]{color-scheme:light;--bg:#f2f6fa;--panel:#fff;--text:#172638;--muted:#526579;--line:#ccdae5;--accent:#087b70;--code:#eaf0f6}*{box-sizing:border-box}html{scroll-behavior:smooth;scroll-padding-top:30px}body{margin:0;background:var(--bg);color:var(--text);font:16px/1.75 system-ui,-apple-system,Segoe UI,sans-serif}a{color:var(--accent);text-underline-offset:4px}aside{position:fixed;inset:0 auto 0 0;width:260px;border-right:1px solid var(--line);padding:36px 25px;background:var(--panel)}.brand{font-size:20px;font-weight:750;line-height:1.4}.small{color:var(--muted);font-size:13px}nav{display:grid;gap:12px;margin-top:45px}nav a{color:var(--text);text-decoration:none;padding:10px;border-radius:8px}nav a:hover{background:var(--bg)}nav span{color:var(--accent);margin-right:14px;font-size:12px}main{max-width:1190px;margin-left:260px;padding:55px 65px 100px}.hero{padding:30px 0 55px;border-bottom:1px solid var(--line)}.eyebrow{color:var(--accent);text-transform:uppercase;letter-spacing:2px;font-size:12px;font-weight:750}h1{font-size:clamp(38px,5vw,66px);line-height:1.08;letter-spacing:-2px;margin:22px 0}h2{font-size:32px;line-height:1.3}h3{font-size:23px;margin-top:35px}h4{font-size:19px}.lead{font-size:20px;color:var(--muted);max-width:660px}.badge{display:inline-block;padding:5px 12px;border:1px solid var(--line);border-radius:30px;margin:10px 5px 0 0;font-size:12px}section{padding:55px 0;border-bottom:1px solid var(--line)}p,li{max-width:850px}code{font:0.88em/1.7 Consolas,monospace;background:var(--code);padding:3px 6px;border-radius:5px;overflow-wrap:anywhere}pre{overflow:auto;padding:23px;margin:0}pre code{padding:0;overflow-wrap:normal}.code{background:var(--code);border:1px solid var(--line);border-radius:12px;position:relative;margin:24px 0}.copy{position:absolute;right:8px;top:8px;font-size:11px}button{background:var(--panel);color:var(--text);border:1px solid var(--line);border-radius:7px;padding:8px 12px;cursor:pointer}button:focus-visible,a:focus-visible{outline:3px solid var(--accent);outline-offset:4px}#theme{margin-top:30px}.table{overflow:auto;margin:25px 0}table{border-collapse:collapse;width:100%;font-size:14px}td{border-bottom:1px solid var(--line);padding:14px 16px;vertical-align:top}tr:first-child{font-weight:700;background:var(--panel)}blockquote{border-left:3px solid var(--accent);padding:10px 20px;margin:12px 0;background:var(--panel);color:var(--muted)}footer{padding-top:35px;color:var(--muted);font-size:13px}@media(max-width:850px){aside{position:static;width:auto;border-right:0;border-bottom:1px solid var(--line);padding:24px}nav{display:flex;flex-wrap:wrap;margin-top:20px;gap:3px}main{margin:0;padding:25px}#theme{margin-top:10px}.hero{padding-top:10px}h1{letter-spacing:-1px}}@media print{aside,.copy{display:none}main{margin:0;padding:0}body{background:white;color:black}section{break-inside:auto}a{color:#146b64}pre{white-space:pre-wrap}html{color-scheme:light}}
</style></head><body><aside><div class="brand">Local Perplexity<br>Bridge <span style="color:var(--accent)">↗</span></div><p class="small">The Windows bridge handbook<br>Source 0.3.4 + pending recovery</p><nav>'''+nav+'''</nav><button id="theme" type="button">Switch to light</button><p class="small">Offline · No external fonts<br>No analytics · 28 Oct 2026</p></aside><main><header class="hero"><div class="eyebrow">Your account. Your local workflow.</div><h1>A clear path from<br>chat to local tools.</h1><p class="lead">Configure accounts, connect your coding agent, understand session routing, and verify what actually ran on your computer.</p><span class="badge">Windows x64</span><span class="badge">OpenAI-compatible</span><span class="badge">Experimental web adapter</span></header>'''+body+'''<footer>Independent project. Original MIT attribution retained. <a href="LICENSE">License</a> · <a href="README.md">Markdown README</a> · <a href="TESTING.md">Historical testing</a></footer></main><script>
const root=document.documentElement,theme=document.getElementById('theme');let stored;try{stored=localStorage.getItem('bridge-guide-theme')}catch{}if(stored==='light')root.dataset.theme='light';function label(){theme.textContent=root.dataset.theme==='dark'?'Switch to light':'Switch to dark'}label();theme.onclick=()=>{root.dataset.theme=root.dataset.theme==='dark'?'light':'dark';try{localStorage.setItem('bridge-guide-theme',root.dataset.theme)}catch{}label()};document.querySelectorAll('.copy').forEach(b=>b.onclick=async()=>{try{await navigator.clipboard.writeText(b.parentElement.querySelector('code').textContent);b.textContent='Copied'}catch{b.textContent='Select text to copy'}setTimeout(()=>b.textContent='Copy',1800)});
</script></body></html>'''
(ROOT/'README.html').write_text(page,encoding='utf-8')
print('Built README.html')
