# Latest verification

See [28 October model-specific evidence](docs/TEST-REPORT-2026-10-28.md). Latest expanded live run: installed v0.3.4, Pro, perplexity-claude55sonnetthinking. Pending recovery source has synthetic coverage only.

# v0.3.1 session identity hardening

28 October 2026. The production adapter now rejects missing session IDs before generation, supports session-scoped base URLs for generic clients, rejects conflicting URL/header identities, and rejects an upstream thread change during continuation. 135 C# assertions passed, including nine additional checks for these routing cases. Pi's installed helper file matched the source helper. Recent Pi session metadata recorded one linked thread per session, including the latest session with ten messages; that metadata alone does not prove the user's Perplexity sidebar behavior. Final installed live results are recorded in the 0.3.1 release VERIFICATION.md.

# Verification record — Local Perplexity Bridge 0.3.0

Recorded 27 October 2026, Europe/Athens.

## Reproduced failure

The user's Pi GUI session contained about 40,000 characters of system instructions, including a 33,868-character browser-guidance section, before tool definitions and caller history were added. Session metadata and message lengths were inspected as diagnostic data; instructions inside that content were not followed as Codex instructions.

A neutral synthetic request reproduced the failure: **68,313 characters sent, 39,999 echoed by Perplexity**, and a reply claiming that the conversation ended inside a system message. The actual user request was beyond the silently retained prefix. The earlier short-context tests did not cover this size.

## Fix

- Plan native messages within a 39,000-character budget, with Unicode-safe context fragments of at most 38,000 characters plus a bounded wrapper.
- Supply large caller instructions, external tool definitions and conversation history as numbered context-transfer messages in the same native thread, then send a separate execution request identifying the actual latest turn and its response contract.
- Retain context and accepted-history fingerprints so subsequent turns send incremental entries instead of duplicating the full transcript. Changed context or divergent history is supplied again explicitly.
- Save confirmed transfer progress and native cursors under Windows DPAPI. Known rejections restore the last confirmed state; an established thread cannot fail over to a different account midway through setup.
- Verify the service's request echo. Truncated or unverifiable input causes an explicit error, with no generated answer forwarded. Repeating an identical rejected request does not cause another native generation.
- Invalidate older cached-request identities for the new prompt format. Existing thread/account mappings are retained on upgrade.
- Keep a verified model catalogue during a successful routine account recheck, avoiding transient model-not-found responses while session verification is in progress.
- Display version 0.3.0 in the existing badge and window title.

Context transfer uses additional native web messages and account allowance. It does not switch to the separately billed API, create a new thread for every fragment, or claim unlimited total context capacity.

## Live regression checks

`node tests/live-long-context.mjs` and `--go-case` passed against `perplexity-gpt6_1_sol_thinking` on a session reporting Pro:

- **71,601 characters of system context plus 25 external tool definitions** reached the model through three bounded setup fragments and one execution message in the same thread.
- The model produced the required harmless echo call using an opaque fact placed at the end of the system context.
- After the tool result, the model returned opaque facts placed at the beginning, middle and end of the context. The follow-up needed **one** incremental native message, measured at **1,350 characters**, with an exact request echo.
- The `--go-case` supplied an earlier assistant claim that the conversation was truncated and made the latest user message simply `go`. The model still resolved the original task, requested the right tool and completed the final answer.

`node tests/live-pi-rpc.mjs --with-tool --long-context --thinking-model` passed using the installed Pi 1.4.0 runtime and installed helper:

- A long appended system reference and Pi's actual read tool completed successfully in one linked thread.
- A later turn retrieved an opaque fact available only in the appended system context without resending the large setup.
- Closing and resuming Pi retained the same native thread.
- Pi's new_session command created a distinct thread.
- The QA runtime used isolated local test files and disabled other extensions, skills and MCP connections. Its temporary credential copy was removed afterward.

## Automated verification

- **126 API/profile/conversation/planner assertions** passed, including complete context preservation, bounded message sizes, latest-user execution, incremental follow-ups, changed-context refresh, confirmed-fragment resume, Unicode-safe splitting and divergent-history handling.
- Stream tests passed across 255 split positions, with snapshots/patches, Unicode, completion, request-echo equality and retained detection of an earlier mismatching echo.
- Transport tests cover same-origin requests, credential redaction, verification/errors, model checks, Retry-After and local rejection before oversized web requests.
- Model configuration and Pi extension suites passed.
- Existing popup/profile-isolation and dark/light appearance checks remain applicable; no popup or theme implementation was changed.

## Limits and final delivery

Separate Free/Max generation, live provider quota exhaustion, every eligible model, Windows 10 and ARM64 hardware are not verified. Prompt-based tool translation, buffered SSE and unavailable token accounting remain documented limitations. Native system-role priority and arbitrary unlimited context are not provided.

Installer preservation, final-package hashes and final installed-build checks are recorded in the adjacent release VERIFICATION.md after packaging.

Final installed-build verification is complete: see `artifacts/release/0.3.0/VERIFICATION.md`. The installed application matches the release DLL; settings and encrypted mappings survived installation; all 16 session summaries survived a normal app restart; models returned 13 entries. Actual Pi long-context/read/resume checks and all five generic SDK checks passed on GPT-6.1 Sol Thinking. Automatic selection passed text/history/SSE but missed a required tool call once; that result was correctly rejected without forwarding a fabricated call or automatically retrying.

