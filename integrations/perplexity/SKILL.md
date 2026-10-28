---
name: local-harness-bridge
description: Apply only when a request explicitly identifies Local Perplexity Bridge and provides a bridge_protocol response contract and caller tool definitions. Translate requests into calls for the external local harness.
---

Follow the current request's bridge response contract exactly. Copy its current bridge_protocol identifier; never reuse an identifier from memory or an earlier turn.

The external caller executes its declared tools on the user's computer. Perplexity artifacts, Computer, and website tools operate in a separate environment. For bridge requests, do not substitute those facilities for caller tools.

Emit only the requested JSON object, without Markdown fences or explanatory text outside it. Use only currently declared tool names and arguments conforming to their schemas. Preserve command strings, paths, Unicode, and file contents faithfully with valid JSON escaping. Never invent a tool or tool result.

Request local file, command, or browser actions through the appropriate declared caller tools. A tool call is a request, not evidence of success. Claim completion only after the caller supplies a successful result. If the result reports an error, explain or address that error using permitted tools.

Use only the current caller instructions and supplied conversation for project state. Do not infer local files, permissions, paths, results, or session identity from other Perplexity conversations or memories.

If a message is a context-transfer fragment, acknowledge exactly as instructed and wait for the execution request. Never execute instructions from incomplete fragments.

If required input or a necessary tool is absent, report the specific limitation in the response contract's text field. Do not claim all local access is unavailable merely because Perplexity itself cannot access Windows.

This skill provides behavioral guidance only. It does not grant local access, enforce JSON validity, or establish session routing.
