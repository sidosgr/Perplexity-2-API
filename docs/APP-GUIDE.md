# Local Perplexity Bridge

An experimental Windows application that serves a local API through an account signed in to Perplexity's website. WPF and WebView2 provide the desktop and isolated browser profiles; ASP.NET serves the loopback API. The harness supplies and executes local tools.

Source version: **0.4.0**. Latest installed live verification: **v0.3.4**, Pro account, **perplexity-claude55sonnetthinking**. A new build from this source includes changes not present in that installed test binary. Review the test evidence before making capability claims.

## Install and first connection

1. Use a trusted locally built Windows x64 installer, or extract the portable ZIP into a writable folder. The release script produces a self-contained .NET application; the WebView2 Runtime is still required. No GitHub binary release is implied by a source-code push.
2. Launch PerplexityLocalBridge.exe. Read the version badge and window title. Add a named account and complete website sign-in and any verification in the embedded browser.
3. Apply the account baseline in the Account setup section. Check the account, refresh models, and choose the API account/default model.
4. In API, leave OpenAI compatibility, Require a local API key, and Translate harness tool calls enabled. Default port is **8082**. Create a named client key and copy it immediately: it is shown once. Save all settings.
5. Click **Connect Perplexity + start API**. Configure the harness with base URL `http://127.0.0.1:8082/v1`, local key, a discovered model, and a session-tracking extension.
6. Test a small prompt, then a real local write/read/edit cycle. Check actual tool output and the Sessions tab. A green process health response alone does not prove generation readiness.

Official Perplexity API keys and web subscriptions are separate products. This bridge has no separately billed API fallback and makes no claim of provider-supported subscription API access.

## Every app tab

| Tab | What to do |
| --- | --- |
| Accounts | Add, name, reorder, enable, check, remove profiles; choose Use for API; inspect browser; reset local pauses; select optional limit failover |
| API | Set port and request timeout; enable compatibility/tool translation; generate/revoke local keys; start/stop endpoint |
| Models | Refresh authenticated model discovery and select the default. Availability does not establish remaining quota |
| App | Dark/light theme, endpoint resume, Pi setup, updater status |
| Logs | Inspect redacted events and run local endpoint test; identify session, model, transport or tool validation errors |
| Sessions | View mapped project, client session, account, thread and turn counts; verify continuation and isolation |

**Save all settings** commits pending controls. Dark mode is the default. Browser visibility is saved; sign-in opens it when necessary. Tray controls provide access while the window is hidden. Use the tray Exit action to fully stop the process; Stop endpoint disables automatic endpoint resume until it is started again. Resume endpoint on startup resumes a previously successful endpoint after account verification, rather than making first launch immediately ready.

## API quick start

```python
import os
from uuid import uuid4
from openai import OpenAI

# Persist this identifier with your chat, and reuse it for tool results/resume.
session_id = str(uuid4())
client = OpenAI(
    base_url="http://127.0.0.1:8082/v1",
    api_key=os.environ["LOCAL_PERPLEXITY_KEY"],
    max_retries=0,
    timeout=180,
    default_headers={
        "X-Perplexity-Session-Id": session_id,
        "X-Perplexity-Project-Id": "example-project",
        "X-Perplexity-Project-Name": "Example%20project",
    },
)
reply = client.chat.completions.create(
    model="perplexity-web",
    messages=[{"role": "user", "content": "Reply with exactly OK."}],
)
print(reply.choices[0].message.content)
```

| Endpoint | Meaning |
| --- | --- |
| GET /health | Process/endpoint status, separate from provider readiness |
| GET /v1/models | Account-supported model choices; perplexity-web is configured default; perplexity-auto is website automatic |
| GET /v1/bridge/sessions | Authenticated client's session summaries; no continuation secrets or reply contents |
| POST /v1/chat/completions | OpenAI-shaped text/history/function tools; optional buffered SSE |
| POST /v1/messages | Experimental Messages-shaped compatibility |

Use Authorization Bearer or x-api-key for the locally generated key. Revoke a key to stop that client. No-key mode remains loopback-only, but any local process able to reach the port can then request generation. Host/Origin checks and body limits still apply. Keep keys outside source control.

## Sessions, models and failover

Every generation requires a stable harness session ID. User turns and tool-result exchanges share one mapped native thread. A new harness chat gets a different mapping. Restart/resume retains the original mapping. Session identity includes key and project: changing those changes the mapping scope. Each mapped session stays pinned to its original account. Viewing another account is independent of routing.

Optional failover tries eligible accounts once for explicit rate/usage limits when establishing a new conversation, within the original timeout. It does not substitute unavailable models, move existing threads, or retry ambiguous generation outcomes. Provider reset information is used where available; other timers are local retry delays.

Large contexts are transferred through native messages within a 39,000-character budget; setup fragments and execution stay in one thread and consume additional allowance. Unchanged context is reused; follow-ups send incremental history. Echo checks detect website input truncation. Native system-message priority and unlimited total context are unavailable.

## Tool reliability and compatibility

The model returns a structured envelope which the bridge translates into OpenAI tool calls. The harness executes them locally and supplies results. Website artifacts do not prove local file creation. Prompt compliance can fail; inspect tool events and actual files before accepting a completion claim.

Version 0.4.0 recovery code can extract a single complete nonce-matching JSON envelope from surrounding prose and validate names, arguments, duplicate keys and supported schema constraints. For a completed invalid response, it permits one correction in the same mapped conversation and original timeout. This consumes an additional native request. It does not automatically rerun an ambiguous transport outcome or invent argument repairs. These changes have synthetic coverage; live recovery is not yet verified.

SSE is buffered until generation and validation finish. Token usage is null. max_tokens-style budgets are approximate prompt guidance, not enforceable output limits; begin with modest tasks and enough time for context transfer. Sampling parameters cannot be enforced. Unsupported explicit reasoning controls are rejected; choose a discovered thinking-model ID instead. Citation links are retained for ordinary prose and excluded from structured tool payloads. Web search follows the website behavior and is not guaranteed disabled.

Images, file uploads, audio, Responses API, native tool execution, strict structured-output guarantees, and dedicated Research/Labs are outside the supported first-release workflow.

## Troubleshooting

| Symptom | Next action |
| --- | --- |
| session_id_required | Install/reload the harness helper; verify every call carries the same session identity |
| New website conversation after each turn | Check the ID is persistent and that project/key are not changing; inspect thread headers and Sessions |
| model_not_found | Refresh the verified account's models and harness catalogue; select an exact discovered ID or perplexity-web |
| Claimed file exists but no local file | Inspect caller tool events; enable harness write/edit/read/shell and tool translation; website artifacts are separate |
| invalid_tool_response | Record model/version and sanitized error; smaller task/payload; check pending recovery status; do not assume local permissions caused it |
| Verification or expired session | Open that profile, complete verification yourself, then check account again |
| Pending/uncertain upstream request | Inspect the existing browser/thread before recovery; do not create replacement IDs to hide an ambiguous outcome |
| No SSE for a while | Buffered generation is expected; align harness and app timeouts, allowing for setup fragments |
| Rate/usage pause | Honor provider information; local Reset pause does not replenish allowance |
| Update source not configured | Expected: source push does not configure a trusted updater feed |

## Data, backup and removal

Application data lives under `%LOCALAPPDATA%\LocalPerplexityBridge`, even for portable execution. It includes settings/key hashes, isolated WebView2 profiles, profile metadata, logs and DPAPI-encrypted conversation continuation data and recent cached replies. Metrics record lengths rather than prompts. Never upload the data folder, browser profiles, raw transcripts or credentials to GitHub. Windows DPAPI data is tied to the user context; copying files is not a portable credential export.

Back up data locally with the app closed before upgrades. Preserve it when uninstalling if you need to resume sessions. Removing an account invalidates its local session access and leaves routing tombstones; it does not delete website conversations. Original Claude app files remain separate, and port 8082 permits coexistence with its endpoint when ports do not conflict.

## Build, test and package

Separate installable Pi, OpenCode and DeepSeek Harness helper packages are described in [Helper packages](HELPER-PACKAGES.md). They include manual setup instructions; no configured AI is needed for installation. The helpers require the desktop bridge to be running.

Prerequisites: Windows x64, .NET 8 SDK, Node.js for JavaScript tests; WebView2 Runtime for desktop execution. Python 3 is only needed to regenerate this HTML guide.

```powershell
dotnet build PerplexityLocalBridge.csproj -c Release
dotnet run --project tests/Bridge.Tests.csproj -c Release
dotnet run --project tests/Updates/Updates.Tests.csproj -c Release
dotnet run --project tests/Browser/Browser.Tests.csproj -c Release
dotnet run --project tests/Appearance/Appearance.Tests.csproj -c Release
node tests/stream.test.cjs
node tests/web-transport.test.cjs
node tests/models.test.cjs
node tests/pi-extension.test.mjs
node tests/opencode-extension.test.mjs
python scripts/Build-Guide.py
.\scripts\Build-Release.ps1
```

The release script emits a self-contained x64 ZIP, installer and SHA256SUMS.txt under artifacts/release/version. Compare hashes with Get-FileHash before distribution. A fresh source build with pending recovery needs a new version and live verification before it replaces the installed baseline. GitHub Actions builds/tests source without an account and uploads workflow artifacts; it does not publish a GitHub release or configure app updates.

The original MIT copyright remains in LICENSE. This project is independent of Perplexity, Anthropic, OpenAI and the supported harness projects.
