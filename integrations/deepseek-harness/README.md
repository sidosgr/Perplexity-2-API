# DeepSeek Harness · Local Perplexity Bridge

Host provider plugin, version 0.1.0. Registers **perplexity-bridge** directly with `ctx.llm`; no remote relay, extra shell tool, or custom proxy process. Existing Harness tools and permission checks execute local actions.

## Install

1. Keep Local Perplexity Bridge running, signed in and serving port 8082. Enable Translate harness tool calls and create a local client key.
2. Set `LOCAL_PERPLEXITY_KEY` in the environment of the **Harness server process**, then restart that process. For a PowerShell launch, `$env:LOCAL_PERPLEXITY_KEY = '<your-local-key>'`. Avoid committing the key into plugin config. An installed server started elsewhere will not inherit a variable set later in an unrelated terminal.
3. In Harness **Plugin Manager**, install this repository's `integrations/deepseek-harness` directory as a local bundle. If your build exposes the `plugin_manager` tool, use its `install_bundle` action with the local directory, following that version's schema. Do not treat an npm package on disk as proof that the bundle is enabled.
4. Enable the bundle/plugin and inspect its activation status. It must have access to the host's `llm` service and resolve `@deepseek-ai/dsh-llm`. Restart if the loader reports restart-required.
5. Select provider **Local Perplexity Bridge** / ID **perplexity-bridge** and a model from the authenticated bridge catalogue. Latest bridge live tests used `perplexity-claude55sonnetthinking`; select that exact ID only when your account discovers it. `perplexity-web` uses the app default.
6. Start a new chat. Ask the harness to write, read and edit a disposable local file. Verify actual local tool results and disk contents, then check the bridge Sessions tab. Continue and resume that chat: its native Perplexity thread must stay the same. A new chat must create another mapping.

No package registry publication is required. `npm pack ./integrations/deepseek-harness --pack-destination artifacts` creates a redistributable archive for loaders supporting package archives. This package has no install scripts and no build step.

## Configuration

The bundle defaults are in cordis.patch.yml. Config fields accepted by the plugin:

| Field | Default | Meaning |
| --- | --- | --- |
| baseURL | http://127.0.0.1:8082/v1 | IPv4 loopback bridge URL; remote URLs, userinfo, queries and alternate paths refused |
| apiKeyEnv | LOCAL_PERPLEXITY_KEY | Environment-variable reference, never the actual key |
| projectPath | server process working directory | Stable project identity; set an absolute workspace path for a dedicated project deployment |
| timeoutMs | 180000 | Total HTTP timeout, 1000–1800000 ms; align with the app timeout |

The plugin accepts the loop's durable `options.sessionId`; it never guesses from messages or generates one per call. Missing IDs fail before HTTP generation. Threads are scoped by local key, configured project path and `dsh-<sessionId>`. Session IDs separate chats even when several projects share the server's default working directory. Set projectPath to obtain accurate per-project labels, and keep it stable across resume. Separate configured instances cannot claim the same provider ID.

## Behavior and limits

- Models are read from `/v1/models` using the local key. No hardcoded subscription tier or paid model catalogue.
- Standard messages, tools, tool-call IDs, tool results and Unicode are translated to Chat Completions. Images and unknown content blocks fail explicitly. Reasoning blocks from earlier history are omitted; explicit reasoning controls are refused.
- One buffered HTTP reply becomes Harness block/delta/finish chunks. Tool calls are handed to the normal local dispatcher. The plugin never executes a tool itself and never fabricates token accounting when usage is null.
- Automatic provider retries are disabled to avoid duplicating ambiguous upstream work. Cancellation reaches fetch; redirects are refused to prevent credential forwarding.
- Auxiliary `session-title` and `compaction` calls are refused so they cannot alter the agent conversation. Configure those optional features to use another provider or disable them. No successful title/compaction behavior is claimed for this route.
- Complete current tool definitions are used; native deferred-tool and incremental-tool-update semantics are not implemented. The plugin advertises no reasoning levels or image support.
- The adapter uses the host's attributionHeaders and registration/disposal lifecycle. It needs a Harness build with the reviewed LlmAdapter API. Developer-preview APIs can change.

## Compatibility and verification

Reviewed upstream revision: **22ddee3792402c3e70ec5f86a63c3e3e895a2f60**, 28 October 2026. References: `packages/llm/llm/src/index.ts`, `types.ts`, `message.ts`, `retry-policy.ts`, and the shipped host-plugin bundle guide. Source: https://github.com/deepseek-ai/deepseek-harness

Run `node tests/deepseek-plugin.test.mjs` from the bridge repository for transport mappings and defensive checks. The published `@deepseek-ai/dsh-llm` 0.0.1-rc.1 and Cordis runtime also passed actual plugin activation, registration, model resolution and prepared streaming in `tests/deepseek-runtime.mjs`.

On 28 October 2026, `node tests/deepseek-runtime.mjs --live` passed text generation and a tool-call → isolated local QA file write/read → tool-result → final answer cycle using **perplexity-claude55sonnetthinking**, Pro, installed bridge v0.3.4. Continuations retained the same thread header. This uses the real Harness LLM runtime with a narrowly scoped QA tool dispatcher; it does not prove full Plugin Manager UI installation or the full application's built-in tool dispatcher. Both current source tool-role results and published rc.1 user-wrapped results are supported.

To reproduce runtime tests, first run `npm install --prefix artifacts/dsh-runtime --ignore-scripts @deepseek-ai/dsh-llm @deepseek-ai/cordis`, then `node tests/deepseek-runtime.mjs`. Add `--live` only with the existing Pi bridge provider configured; it reads that key locally without printing it and consumes account allowance. Runtime dependencies and QA files stay in ignored artifacts.
