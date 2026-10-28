# Configure every Perplexity account

Repeat this checklist for each distinct account. Recommended baseline: a dedicated bridge account. Account-wide personalization changes also affect ordinary website use.

1. In **Accounts**, choose **Add account**, assign a useful local label, and open **Sign in / open Perplexity**. Complete login and verification yourself in that profile's embedded browser. A login in Codex, Chrome, or another profile does not sign this profile in.
2. Verify the intended email and subscription in the website account menu. Never infer remaining allowance from a Pro badge or model list.
3. Open website **Settings → Personalization**. Set response length and headers/lists to **Default**. Paste the conditional instructions below. Occupation and company fields can remain empty; they are unnecessary for the bridge.
4. For dedicated bridge accounts, turn **Use search history** and **Notes** off under **Memory**. Do not delete existing conversations or stored memories as part of setup.
5. Disable unrelated custom skills, such as Content creation, for this baseline. Upload [the bridge skill](../integrations/perplexity/SKILL.md) through **Plugins → Add custom → Upload plugin or skill**, or the older **Customize → Skills → Create skill → Upload skill** interface. Confirm its name and description on Installed.
6. Back in the app, click **Check selected account**, then **Models → Refresh models from Perplexity**. Choose only a discovered model, or the automatic option if manual selection is unavailable.
7. Enable **Include in API routing** and choose **Use for API** when this should be the default account. Viewing another profile does not change API routing. Save all settings.
8. Run the local endpoint test, then a harmless harness write/read/edit cycle. Verify real local tool events and disk contents. Record app version, explicit model ID, account tier, date, and results.

## Conditional custom instructions

> When a request explicitly identifies Local Perplexity Bridge and supplies a bridge_protocol response contract, follow that contract exactly. Return only the requested JSON object, with no Markdown fences or additional commentary. Use the current request's protocol identifier and only its declared caller tools.
>
> The caller's tools execute on my local computer. Perplexity website artifacts and Computer tools are a separate environment. Request local file operations, commands, and application launches through the declared caller tools. Never substitute a website artifact for a local file.
>
> A requested tool call is not a completed action. Claim success only after receiving a successful caller tool result. Do not invent results or claim local tools are unavailable merely because Perplexity cannot directly access Windows.
>
> For these bridge requests, use the supplied conversation and current instructions for project state, rather than memories or other conversations. Acknowledge context-transfer fragments exactly as instructed and wait for the execution request. For all other conversations, respond normally.

## Skills and connectors: verified boundaries

Skills are documented for **Perplexity Computer**, with task-based activation. Uploading this skill does not prove activation in the bridge's Search-mode transport, enforce JSON syntax, or grant local permissions. The latest successful tests were performed after the user reported applying this baseline; they do not isolate which setting helped.

Custom remote MCP connectors require HTTPS and support authentication. A cloud service cannot reach this PC's `127.0.0.1` endpoint. No remote connector or public tool relay is implemented by this app. Do not enter the local API URL as a remote connector expecting local shell access.

Sources checked 28 October 2026: [Computer skills](https://www.perplexity.ai/help-center/en/articles/13914413-how-to-use-computer-skills), [remote connectors](https://www.perplexity.ai/help-center/en/articles/13915507-adding-custom-remote-connectors).

## Multiple accounts

Keep one isolated browser profile per account and repeat the baseline in each account. Reordering changes new-request candidate order. Failover applies to recognized usage/rate limits for new conversations; existing sessions remain pinned to their original account. A local retry delay is not a provider quota-reset estimate. Reset pause clears the local timer only. Removing a profile invalidates access to its mapped sessions; it does not migrate them or delete website conversations. Free and Max generation remain unverified separately.
