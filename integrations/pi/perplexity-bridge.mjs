import { createHash } from "node:crypto";
import { basename, resolve, join } from "node:path";
import { homedir } from "node:os";
import { readFileSync, writeFileSync, renameSync, copyFileSync, mkdirSync } from "node:fs";

function localPerplexity(ctx) {
  if (ctx.model?.provider !== "perplexity-2-api") return false;
  try { const url = new URL(ctx.model.baseUrl); return url.protocol === "http:" && ["127.0.0.1", "localhost", "[::1]"].includes(url.hostname); }
  catch { return false; }
}
function identity(ctx) {
  const cwd = resolve(ctx.sessionManager.getCwd() || ctx.cwd);
  return { session: ctx.sessionManager.getSessionId(), project: createHash("sha256").update(process.platform === "win32" ? cwd.toLowerCase() : cwd).digest("hex"), name: encodeURIComponent([...(basename(cwd) || "Project")].slice(0,48).join("")) };
}
export async function refreshLocalModels(agentDir = join(homedir(), ".pi", "agent")) {
  const file = join(agentDir, "models.json"), original = readFileSync(file, "utf8"), config = JSON.parse(original);
  const provider = config.providers?.["perplexity-2-api"];
  if (!provider) throw new Error("Configure the perplexity-2-api provider first.");
  const url = new URL(provider.baseUrl);
  if (url.protocol !== "http:" || !["127.0.0.1", "localhost", "[::1]"].includes(url.hostname)) throw new Error("Model refresh only connects to the local bridge.");
  let key = provider.apiKey;
  if (typeof key === "string" && process.env[key]) key = process.env[key];
  if (typeof key !== "string" || !/^(lpb_|lcb_)/.test(key)) {
    try { const auth = JSON.parse(readFileSync(join(agentDir, "auth.json"), "utf8")); key = auth["perplexity-2-api"]?.key; } catch { }
  }
  const response = await fetch(provider.baseUrl.replace(/\/$/, "") + "/models", { headers: key ? { Authorization: "Bearer " + key } : {}, signal: AbortSignal.timeout(30000) });
  if (!response.ok) throw new Error("Local model refresh failed (HTTP " + response.status + ").");
  const body = await response.json();
  const old = new Map((provider.models || []).map(model => [model.id, model]));
  const available = (body.data || []).filter(model => typeof model.id === "string" && /^perplexity-[a-zA-Z0-9_-]+$/.test(model.id));
  if (!available.length) throw new Error("The bridge returned no model selections.");
  provider.models = available.map(model => ({ ...old.get(model.id), id: model.id, name: model.name || model.id, reasoning: false }));
  provider.authHeader = true;
  if (readFileSync(file, "utf8") !== original) throw new Error("Pi's model settings changed during refresh. Retry the command.");
  const backupDir = join(agentDir, "perplexity-bridge-backups"); mkdirSync(backupDir, { recursive: true });
  copyFileSync(file, join(backupDir, "models-" + Date.now() + ".json"));
  writeFileSync(file + ".perplexity-tmp", JSON.stringify(config, null, 2) + "\n"); renameSync(file + ".perplexity-tmp", file);
  return available.length;
}
export default function (pi) {
  const lastThreads = new Map();
  pi.on("before_provider_headers", (event, ctx) => {
    if (!localPerplexity(ctx)) return;
    const id = identity(ctx);
    event.headers["X-Perplexity-Session-Id"] = id.session;
    event.headers["X-Perplexity-Project-Id"] = id.project;
    event.headers["X-Perplexity-Project-Name"] = id.name;
  });
  // Payload metadata also supports Pi transports that do not expose the header hook.
  pi.on("before_provider_request", (event, ctx) => {
    if (!localPerplexity(ctx) || !event.payload || typeof event.payload !== "object") return;
    const id = identity(ctx);
    return { ...event.payload, metadata: { ...event.payload.metadata, session_id: id.session, project_id: id.project, project_name: id.name } };
  });
  pi.on("after_provider_response", (event, ctx) => {
    if (!localPerplexity(ctx)) return;
    const thread = event.headers["x-perplexity-thread-id"];
    if (!thread) return;
    const session = ctx.sessionManager.getSessionId();
    ctx.ui.setStatus("perplexity-thread", "Perplexity · " + thread.slice(0, 8));
    if (lastThreads.get(session) !== thread) {
      lastThreads.set(session, thread);
      pi.appendEntry("perplexity-bridge-thread", { thread, project: decodeURIComponent(identity(ctx).name) });
    }
  });
  pi.on("session_start", (_event, ctx) => {
    const entry = [...ctx.sessionManager.getEntries()].reverse().find(e => e.type === "custom" && e.customType === "perplexity-bridge-thread");
    if (entry?.data?.thread) { lastThreads.set(ctx.sessionManager.getSessionId(), entry.data.thread); ctx.ui.setStatus("perplexity-thread", "Perplexity · " + entry.data.thread.slice(0, 8)); }
    else ctx.ui.setStatus("perplexity-thread", undefined);
  });
  pi.registerCommand("perplexity-thread", { description: "Show the Perplexity thread attached to this Pi session", handler: async (_args, ctx) => {
    const thread = lastThreads.get(ctx.sessionManager.getSessionId());
    ctx.ui.notify(thread ? "Perplexity thread: " + thread : "This Pi session has not received a Perplexity reply yet.", "info");
  }});
  pi.registerCommand("perplexity-models", { description: "Refresh local Perplexity models, then reload Pi to apply the list", handler: async (_args, ctx) => {
    try { const count = await refreshLocalModels(); ctx.ui.notify("Saved " + count + " Perplexity models. Run /reload to apply the list.", "info"); }
    catch (error) { ctx.ui.notify(String(error.message), "error"); }
  }});
}
