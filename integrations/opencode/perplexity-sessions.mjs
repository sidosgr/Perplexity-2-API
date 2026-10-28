function trustedHost(host) {
 const b=host.split('.').map(Number);
 return ['127.0.0.1','localhost','[::1]'].includes(host)||(b.length===4&&b.every(n=>Number.isInteger(n)&&n>=0&&n<=255)&&(b[0]===10||b[0]===172&&b[1]>=16&&b[1]<=31||b[0]===192&&b[1]===168));
}
import { createHash } from "node:crypto";
import { resolve, basename } from "node:path";
export const PerplexitySessions = async ({directory}) => ({
  "chat.headers": async (input, output) => {
    if (input.model.providerID !== "perplexity-2-api") return;
    const endpoint = input.provider.options?.baseURL;
    if (!endpoint) throw new Error("Perplexity session tracking requires the local bridge baseURL.");
    const url = new URL(endpoint);
    if (url.protocol !== "http:" || !trustedHost(url.hostname)) return;
    if (!input.sessionID) throw new Error("OpenCode did not supply a session ID; no Perplexity request was sent.");
    const cwd=resolve(directory);
    output.headers["X-Perplexity-Session-Id"]="opencode-"+input.sessionID;
    output.headers["X-Perplexity-Project-Id"]=createHash("sha256").update(process.platform==="win32"?cwd.toLowerCase():cwd).digest("hex");
    output.headers["X-Perplexity-Project-Name"]=encodeURIComponent([...basename(cwd)].slice(0,48).join(""));
  }
});
