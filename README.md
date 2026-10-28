# Local Perplexity Bridge

Experimental Windows desktop app that serves a local text/tool API through your own signed-in Perplexity web session. Source version **0.3.4 plus pending response recovery**. Latest installed live tests used v0.3.4 with **perplexity-claude55sonnetthinking** on Pro. See [the HTML handbook](README.html), [account setup](docs/ACCOUNT-SETUP.md), [harness development](docs/HARNESS-DEVELOPMENT.md), and [model-specific test report](docs/TEST-REPORT-2026-10-28.md).

Sign in directly on `https://www.perplexity.ai/` inside the app. No Perplexity API key is needed for the web adapter. The official Perplexity API is a separate product with separate billing. This adapter uses undocumented web interfaces and can break when the website changes.

## Local tools (v0.3.4)

The harness executes local tools. The bridge translates requests for the tools supplied in each API call; it does not itself grant desktop or filesystem permissions. Enable the harness's write/edit, read, and shell tools for local coding. The prompt explicitly identifies the caller's computer as the execution environment and forbids substituting Perplexity website artifacts or its sandbox for local work. Successful local work must be confirmed by actual caller tool results. This is prompt-based translation, so website/model compliance is not guaranteed; confirm important actions from tool events and files on disk.

## One Perplexity thread per harness session (v0.3.1)

A stable session identity is mandatory for generation. Requests without one return HTTP 400 `session_id_required` before contacting Perplexity. The bridge never silently falls back to a new conversation per request. If Perplexity returns a different context ID during a continuation, the bridge withholds the answer and retains the original mapping for recovery.

Pi's installed helper supplies its real session ID automatically. Run `/reload` or restart Pi if the helper was installed while Pi was running. A new Pi session gets a new thread; user turns, tool results and resumed turns retain that session's thread.

Other harnesses must send `X-Perplexity-Session-Id` or `metadata.session_id` with a stable unique session ID. If they only support a configurable base URL, use `http://127.0.0.1:8082/v1/sessions/YOUR-UNIQUE-SESSION-ID`. Keep this URL for that session, and generate a different ID for each new session. Models, chat completions and Messages routes work below this base URL. A fixed provider-wide URL shares a thread across all sessions using it, so it must not be presented as automatic session tracking. The standard OpenAI request format does not guarantee a session ID; the bridge cannot infer distinct sessions reliably from message text.

## Large agent context in v0.3.0

A live test found that Perplexity’s web service echoed only 39,999 characters from a 68,313-character request. Earlier versions could therefore deliver the start of a large system prompt while losing the actual user task at the end.

The app now plans each native message within a **39,000-character budget**. Large system instructions, tool definitions and conversation data are transferred as numbered setup fragments in the same Perplexity thread, followed by a distinct execution request. No context is silently dropped. Setup fragments consume additional web-account requests and are visible as context-transfer progress in the app logs.

For a linked session, unchanged system/tool context is retained and later requests send only new history entries. Changed instructions or a changed history prefix are supplied again as the current authoritative context. Confirmed transfer progress survives known rejections and restarts. Every native response must echo the complete supplied message; a mismatch or unverifiable echo withholds the generated reply and returns an explicit error.

Total provider context limits still apply. The local API retains its 1 MiB body and 400,000-character assembled-input limits. Increase the request timeout for lengthy setup or coding work; all setup and execution messages share the original request timeout.

## Install and connect

1. Run `LocalPerplexityBridge-Setup-0.3.4-x64.exe`. It installs per user, creates a Start-menu shortcut, and opens the app. The release includes .NET; Microsoft Edge WebView2 Evergreen Runtime must be installed.
2. Sign in inside the embedded Perplexity browser, including any verification. Each named account has its own isolated browser profile.
3. Click **Check selected account**, then **Use for API** or **Start endpoint**. Separate indicators show sign-in and local server status.
4. Create a local key on the **API** tab and copy it. Only its hash is saved. The optional no-key setting requires saving settings and restarting the endpoint.
5. Use `http://127.0.0.1:8082/v1` in your client. Choose `perplexity-auto`, or `perplexity-web` to use the app's saved default.
6. **Logs → Test local endpoint → Perplexity** sends one small prompt and checks the returned text.

The portable ZIP can be extracted and run directly. Keep all files together. The installer has no registered Add/Remove Programs uninstaller. Do not delete the app-data directory if you want to retain logins and keys.

## Pi Agent: one thread per session

The app shows its version beside the name and in the window title. **v0.3.0** retains persistent conversation routing and authenticated model discovery and adds bounded context transfer.

1. Keep the existing `perplexity-2-api` provider pointed at this app.
2. Open **App → Set up Pi session tracking**. This installs the helper in `~/.pi/agent/extensions/perplexity-bridge.ts`, verifies/configures Pi's local key, and refreshes that provider's model list. Existing configuration is backed up locally before edits.
3. Run **`/reload` in Pi** to load the extension and updated catalogue. A Pi GUI that does not expose reload can be restarted.
4. Continue using your Pi session. Its subsequent user turns and tool-result turns share one Perplexity thread. Resuming Pi or restarting the bridge retains the mapping. A new Pi session gets a distinct thread.

The helper scopes requests to the local Perplexity provider and attaches Pi's real session ID plus a hash of its working-directory identity. The **Sessions** tab lists the project label, agent identifier, thread ID, turn count and account. Pi also displays a linked-thread status.

- `/perplexity-thread` shows the thread attached to the current Pi session.
- `/perplexity-models` refreshes model choices from the local app; run `/reload` afterwards to apply the new list.

Each conversation is pinned to its original account. An existing thread pauses when that account is unavailable or limited. New conversations can use the configured account failover. Changing the signed-in identity inside an existing profile does not silently redirect its old conversations.

Identical request retries reuse the last completed reply. If the upstream outcome is uncertain, the mapping is kept and the app reports a recovery error instead of creating a replacement thread. Removing an account clears its stored thread-access data and cached replies while retaining a tombstone that prevents accidental rerouting of old sessions.

The first request from a Pi session after upgrading creates its initial mapping. Project labels come from Pi's working directory. Existing Perplexity conversations are retained.

For development, `node scripts/Install-PiIntegration.mjs` installs the same helper and refreshes an existing provider using its current local key.

## DeepSeek Harness

Install the [DeepSeek Harness provider plugin](integrations/deepseek-harness/README.md) through its Plugin Manager. The bundle supplies authenticated model discovery and persistent session identity directly through the host LLM adapter API. Source is in `integrations/deepseek-harness`; no package-registry publication is required. The plugin README records live Claude Sonnet Thinking runtime/tool-loop evidence and full-application verification limits.

## OpenCode setup

Configure an OpenAI-compatible custom provider:

```json
{
  "provider": {
    "perplexity-2-api": {
      "npm": "@ai-sdk/openai-compatible",
      "name": "Local Perplexity Bridge",
      "options": { "baseURL": "http://127.0.0.1:8082/v1" },
      "models": { "perplexity-auto": { "name": "Perplexity automatic" } }
    }
  }
}
```

Store the generated local key through OpenCode's provider connection flow. Keep the bridge running. Select `perplexity-2-api/perplexity-auto` and start a fresh prompt after updating from the initial milestone build.

## Python OpenAI client

```python
import os
from openai import OpenAI

client = OpenAI(
    base_url="http://127.0.0.1:8082/v1",
    api_key=os.environ["LOCAL_PERPLEXITY_KEY"],
    max_retries=0,
    default_headers={"X-Perplexity-Session-Id": "persist-a-unique-id-per-chat"},
)
reply = client.chat.completions.create(
    model="perplexity-auto",
    messages=[{"role": "user", "content": "Reply with exactly OK."}],
)
print(reply.choices[0].message.content)
```

## API behavior

| Route | Behavior |
| --- | --- |
| `GET /health` | Process status and authentication mode; does not prove upstream generation works |
| `GET /v1/models` | Configured-default alias plus Search models available to the verified subscription tier |
| `GET /v1/bridge/sessions` | Session/project/thread summaries scoped to the authenticated local client; no access tokens or cached reply content |
| `POST /v1/chat/completions` | OpenAI-shaped text, serialized history, translated function tools, buffered SSE |
| `POST /v1/messages` | Experimental Messages-shaped text/tools and buffered SSE |

Local keys use `Authorization: Bearer <local-key>` or `x-api-key: <local-key>`. Keys are unrelated to Perplexity credentials. The endpoint binds only to IPv4 loopback and rejects browser-origin requests and unapproved Host values even in no-key mode.

- Model discovery reads Perplexity’s authenticated model configuration, checks the account tier, and excludes retired, Computer/browser-agent, audience-gated and upgrade-only selections. Thinking variants appear as separate real model IDs. The automatic option uses the website default for that tier. A selectable model does not establish remaining quota.
- Requests with a session identifier continue their mapped Perplexity thread using native follow-up data. Clients without a session identifier are rejected before generation. Caller instructions and history are supplied as context; unchanged context is reused and follow-up history is incremental. Native system-message priority is unavailable. Existing unrelated conversations are not read or deleted.
- Tools use a structured prompt protocol. If the model omits a required call or returns an invalid envelope, the request fails explicitly; the bridge never invents a call. Pending source recovery permits one same-thread correction for a completed invalid response within the original timeout; installed v0.3.4 does not include that correction. Declared names, object arguments, required properties and call counts are checked. The client executes tools; this is not native Perplexity tool execution or full JSON Schema validation.
- The parser supports `ask_text` and `ask_text_0_markdown` answer blocks, snapshots and patches. Markdown takes precedence when both are present. Status and follow-up suggestions are excluded from the answer.
- SSE is **buffered**: generation and tool validation finish before the local response is emitted. It is not real-time token delivery.
- Source links from web-result blocks are retained in ordinary text answers. Structured tool and JSON-object responses are not contaminated with an appended source list.
- Web search follows the website behavior. Search-free generation is not guaranteed.
- Sampling controls cannot be enforced. Token limits become approximate prompt guidance. Compatibility caveats are returned in `X-Local-Perplexity-Compatibility`. Token accounting is unavailable, so usage is `null`.
- Explicit reasoning/thinking controls are rejected until a real Perplexity mapping is verified. Images, uploads, audio, Responses API, strict JSON Schema output, and dedicated Research/Labs workflows are unsupported.
- One generation at a time; 1 MiB request-body limit; 400,000-character assembled prompt limit. Requests have a configurable timeout and support cancellation. Ambiguous failures are not retried by the bridge.

## Session identifiers for other clients

Supply `X-Perplexity-Session-Id` (or `X-Session-Id`) with a stable identifier for one agent conversation. Optional `X-Perplexity-Project-Id` and URI-encoded `X-Perplexity-Project-Name` keep project identity and display labels distinct. OpenAI request `metadata.session_id`, `metadata.project_id` and `metadata.project_name` provide a fallback; explicit headers take precedence.

Mappings are scoped to the authenticated local key, project ID and session ID. Responses include `X-Perplexity-Thread-Id`, the stable upstream thread identity. `GET /v1/bridge/sessions` provides current-client summaries without exposing thread access tokens.

For example, OpenAI SDK calls can add:

```python
extra_headers={
    "X-Perplexity-Session-Id": "a-unique-agent-conversation-id",
    "X-Perplexity-Project-Id": "my-project",
    "X-Perplexity-Project-Name": "My%20project",
}
```

## Accounts and local storage

Accounts support naming, manual routing, enabled flags, reordering, removal, checking all sessions, and optional failover for recognized quota/rate-limit failures. Viewing an account does not change API routing. Each candidate is tried once within the original request timeout. A model unavailable on a candidate is never silently replaced by another model.

A valid provider `Retry-After` header takes precedence when supplied. Otherwise local usage-limit pauses default to 300 minutes and temporary rate-limit pauses default to one minute. These are retry delays, not claims about provider quota resets. Reset a pause manually when appropriate. Live multi-account quota failover remains unverified. Existing mapped conversations remain pinned and never fail over to another account automatically.

Data lives in `%LOCALAPPDATA%\LocalPerplexityBridge`: settings and key hashes, profile metadata, isolated WebView2 browser data, and redacted diagnostic events. `last-prompt-metrics.json` contains character counts and an echo-equality flag, never prompt text. `conversations.dpapi` stores thread continuation data context/history fingerprints, transfer progress, and the single most recent reply per session encrypted with Windows DPAPI for this user; full caller transcripts are not persisted there. Cookies are not extracted, copied from another browser, exported, or stored in profile metadata. A pseudonymous account fingerprint is used to recognize duplicate sign-ins. Never share this data folder.

Dark/light themes, tray controls, saved browser visibility, endpoint resume, and unified **Save all settings** are retained from the reference app. Update status appears inline. Network update checks are disabled with **Update source not configured** until a release source is deliberately configured.

## Build and test

Install .NET 8 SDK and Node.js for development:

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
.\scripts\Build-Release.ps1
```

`tests/live-sdk.mjs` is an opt-in developer smoke test for the existing local OpenCode provider. It reads the provider's local credential without printing it. Its optional SDK dependencies are installed under `artifacts/sdk`; they are not application dependencies. See `TESTING.md` for current evidence and limits.

## Attribution

Based on the current Local Claude Bridge source supplied by the user. Original MIT copyright and license are retained in `LICENSE`.

Protocol references: [Perplexity official SDK](https://github.com/perplexityai/perplexity-py), [edusu/perplexity-cli](https://github.com/edusu/perplexity-cli), and [ccproxy web adapter](https://github.com/starbaser/ccproxy/blob/dev/src/ccproxy/lightllm/pplx.py). Desktop runtime: [Microsoft WebView2](https://learn.microsoft.com/en-us/microsoft-edge/webview2/).

Independent experimental project, not affiliated with Perplexity, Anthropic, OpenAI, or OpenCode. A working technical test does not establish provider support or authorization.


