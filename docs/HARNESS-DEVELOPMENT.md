# Build a harness integration

The extension's job is stable identity and provider configuration. The harness retains its tools, execution permissions, approvals, and tool-result loop. A skill alone cannot provide reliable session identity.

## Wire contract

Send these headers on **every generation request**, including tool-result continuations, retries, and resumed sessions:

| Header | Value |
| --- | --- |
| `Authorization` | `Bearer <local bridge key>` |
| `X-Perplexity-Session-Id` | Persistent unique harness conversation ID |
| `X-Perplexity-Project-Id` | Stable opaque project ID, e.g. SHA-256 of canonical workspace path |
| `X-Perplexity-Project-Name` | URI-encoded display label |

Session mappings are scoped to local key + project + session. Persist the session ID with the harness conversation. Generate a new ID for new chats and forks. Reuse it when resuming. Never derive identity from prompt text, generate it per HTTP call, or use a single provider-wide ID. Rotating the local key changes the mapping scope.

Equivalent body metadata fields are `session_id`, `project_id`, `project_name`. Explicit headers take precedence. Clients limited to base URLs can use `/v1/sessions/<unique-session-id>`; change that URL for every new chat. Conflicting scoped URL/header IDs are rejected. Missing identity returns `session_id_required`.

## Minimal reusable adapter

```js
import { createHash } from "node:crypto";
import { resolve, basename } from "node:path";

export function bridgeHeaders({ baseURL, apiKey, sessionId, workspace }) {
  const url = new URL(baseURL);
  if (url.protocol !== "http:" || !["127.0.0.1", "localhost", "[::1]"].includes(url.hostname))
    throw new Error("Expected local bridge URL");
  if (!sessionId) throw new Error("Persist a conversation ID before generation");
  const cwd = resolve(workspace);
  const identity = process.platform === "win32" ? cwd.toLowerCase() : cwd;
  return {
    Authorization: `Bearer ${apiKey}`,
    "X-Perplexity-Session-Id": sessionId,
    "X-Perplexity-Project-Id": createHash("sha256").update(identity).digest("hex"),
    "X-Perplexity-Project-Name": encodeURIComponent([...basename(cwd)].slice(0, 48).join(""))
  };
}
```

Hook this into the harness's request builder, restricted to its configured local Perplexity provider. Resolve workspace identity consistently across restarts; symlinks, path case, or relocating a project can change it. Do not send bridge credentials to arbitrary remote URLs. Fetch `/v1/models` with the local key and preserve IDs exactly. `perplexity-web` selects the app default; `perplexity-auto` requests website automatic selection. Do not hardcode paid models for Free profiles.

## Tool loop

1. Send standard OpenAI messages and declared function tools, including valid JSON schemas. Enable Translate harness tool calls in the app.
2. Read returned `tool_calls`; arguments are JSON strings. Execute them using the harness's existing tool dispatcher and permission checks.
3. Append the assistant message containing those calls, then matching `role: tool` messages with each `tool_call_id`. Send the continuation with the **same session/project headers**.
4. Continue until the assistant produces a final response. Confirm file changes and command outcomes from actual tool results.

The bridge supplies its internal protocol nonce and validates the returned envelope. Do not implement your own fixed nonce or pre-wrap normal SDK messages. Streaming is buffered: a long pause before SSE is expected. Usage is `null`. Select a discovered thinking model by ID if desired; omit explicit reasoning controls. Disable automatic retries for ambiguous generation failures. Honor cancellation and surface errors instead of creating replacement conversations.

## Pi reference

Source: [perplexity-bridge.mjs](../integrations/pi/perplexity-bridge.mjs). The app's **App → Set up Pi session tracking** installs it as `~/.pi/agent/extensions/perplexity-bridge.ts` and configures the provider. Restart Pi or run `/reload`.

Developer install: `node scripts/Install-PiIntegration.mjs` from the repository root. This requires an existing `perplexity-2-api` provider in `~/.pi/agent/models.json`; it installs the helper and refreshes models using the existing local key. The app setup is the easier initial configuration route.

The helper uses `before_provider_headers`, metadata fallback in `before_provider_request`, and `after_provider_response` for thread status. `/perplexity-thread` shows the linked thread; `/perplexity-models` refreshes choices. It uses Pi's session manager ID and workspace hash. Hook availability must be checked for the target Pi build. Latest live checks used Pi CLI 1.4.0; isolated tests disable unrelated Pi extensions, skills and MCP.

## OpenCode reference

Copy [perplexity-sessions.mjs](../integrations/opencode/perplexity-sessions.mjs) to `~/.config/opencode/plugins/perplexity-sessions.ts`, preserving its named export. Restart OpenCode. Configure provider ID `perplexity-2-api`, npm provider `@ai-sdk/openai-compatible`, and `options.baseURL` as `http://127.0.0.1:8082/v1`; store a generated local key through the harness's authentication flow. Populate model IDs from the bridge.

The plugin uses `chat.headers`, `input.sessionID`, `input.model.providerID`, and the project directory. Test hook compatibility against the installed OpenCode version. Hook fixtures and the compatible provider SDK passed; the latest suite did not run a full live OpenCode UI workflow.

## Acceptance tests for every new harness

- Same chat: user → tool call → tool result → follow-up retains one `X-Perplexity-Thread-Id`.
- Restart/resume retains that thread; new chat and fork get distinct threads.
- Concurrent chats and two workspaces remain isolated. Cancellation does not silently start a replacement request.
- Missing identity and unavailable model fail clearly; no automatic model substitution.
- Real write/read/edit/shell actions succeed locally; missing-file errors remain errors.
- Model refresh uses authenticated discovery; revoked local keys are rejected.
- Inspect `/v1/bridge/sessions` and actual local files. A plausible assistant claim alone is not a passing test.

Use the existing Pi/OpenCode hook tests and live SDK scripts as references. Record model ID, tier, app version, harness version, prompt sizes, settings baseline, time, and whether the check was synthetic or live. Keep raw account/thread/credential artifacts out of commits.
