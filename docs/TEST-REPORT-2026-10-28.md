# Verification — 28 October 2026

## Exact model and environment

**Every live check in the latest expanded run used `perplexity-claude55sonnetthinking` (Claude Sonnet Thinking), on the connected account reporting Pro, against installed bridge v0.3.4.** This is not a comparison across models. Tests selected this ID explicitly; they did not rely on the configured-default alias.

Pi CLI reported version 1.4.0. The account baseline was reported by the user: conditional custom instructions, uploaded bridge skill, Default response length, search-history memory and Notes disabled, unrelated Content creation skill disabled. We did not verify skill activation or isolate causality between these changes and successful responses.

## Live results

| Suite / command | Outcome |
| --- | --- |
| `node tests/live-pi-rpc.mjs --file-cycle --model perplexity-claude55sonnetthinking` | PASS: actual write/read/edit tools, independently verified local contents, real missing-file tool error, successful subsequent `node --version`, same upstream thread |
| `node tests/live-pi-rpc.mjs --with-tool --long-context --model perplexity-claude55sonnetthinking` | PASS: real read/result loop, appended long-context reference retrieval, same-thread continuation and process resume, distinct thread for new session |
| `node tests/live-sdk.mjs --pi-credentials --model perplexity-claude55sonnetthinking` | PASS: OpenAI text, history, buffered SSE, OpenCode-compatible SDK streamed tool call and result/final answer |

Raw local records were written around 01:15–01:16 UTC (03:15–03:16 Europe/Athens). They remain in ignored `artifacts/`; public documentation omits thread IDs, personal paths and account identifiers. Repeated runs overwrite the corresponding local latest-result files.

## Automated source checks

- C# suite: 152 assertions passed, including API authentication, profile routing, encrypted conversation persistence, bounded context and response recovery.
- Stream parser: 255 split positions passed, plus Unicode, incremental/replacement answers, errors, completion and truncation checks.
- Transport/model/Pi/OpenCode fixtures passed: request echo, isolation, unavailable models, verification/limits, tier discovery, stable session and project metadata.

The response-recovery source changes are **not installed in the v0.3.4 binary used above**. Their synthetic coverage does not establish live recovery. This repository includes that pending source; a new build must be labeled and tested before treating it as a verified replacement for the installed binary.

## Historical model evidence

Earlier v0.3.0 long-context tests used `perplexity-gpt6_1_sol_thinking`: 71,601 system characters plus 25 tools, fragmented transfer, echo-tool round trip, and incremental follow-up passed. Other GPT runs showed inconsistent caller-tool behavior. Do not generalize the latest Claude successes to GPT, automatic selection, or every available model.

## Remaining gaps

### DeepSeek Harness plugin follow-up

Plugin 0.1.0 was checked against upstream source revision 22ddee3792402c3e70ec5f86a63c3e3e895a2f60 and the published dsh-llm 0.0.1-rc.1 runtime. Transport fixtures, real Cordis activation/LLM registration, model resolution, and prepared streaming passed. Live checks used **perplexity-claude55sonnetthinking**, Pro, installed bridge v0.3.4: text and a tool-call/local QA write/read/result/final cycle passed. Same-session response thread headers were checked. The test supplies one harmless QA tool dispatcher; full Harness UI installation and built-in tools remain unverified. No local DeepSeek API key or separately billed fallback was used.

Free and Max tiers; full OpenCode application workflow; long soak sessions; large code/quote-heavy tool arguments; live multi-account quota failover; live malformed-response recovery; skill activation in Search; native MCP connector transport; all discovered models; Windows 10/ARM64. No claims of unlimited allowance, provider support, or native structured-tool guarantees are made.

## Reproducing

Live Pi scripts require the installed Pi CLI, helper and a configured local provider. They use isolated test directories and remove their temporary key config in `finally`. They consume web-account allowance. Live SDK tests additionally require optional dependencies in `artifacts/sdk`: `openai`, `ai`, `@ai-sdk/openai-compatible`, `jsonc-parser`; the script currently also reads an existing OpenCode configuration. Do not fabricate or commit personal provider configuration to run CI. Automated CI uses synthetic backends and requires no Perplexity login.
