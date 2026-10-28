using System.IO;
using System.Reflection;
using System.Text.Json;
using System.Windows.Threading;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.Wpf;
namespace PerplexityLocalBridge;

public sealed class WebSession : IDisposable
{
    private readonly WebView2 browser;
    private readonly Dispatcher dispatcher;
    private readonly Dictionary<string, TaskCompletionSource<JsonElement>> pending = [];
    private readonly string script;
    private readonly ModelCatalog models;
    private string? organizationId;
    private WebPopupHost? popups;
    public event Action? ConnectionChanged;
    public event Action? LoginWindowClosed;
    public event Action? LoginWindowFailed;
    public string? AccountSessionId { get => organizationId; set { if (organizationId == value) return; models.Clear(); organizationId = value; ConnectionChanged?.Invoke(); } }
    public string? ReportedPlanTier { get; private set; }
    public bool Ready => AccountSessionId is not null;
    public WebSession(WebView2 browser, ModelCatalog? catalog = null)
    {
        this.browser = browser; dispatcher = browser.Dispatcher; models = catalog ?? new();
        using var stream = Assembly.GetExecutingAssembly().GetManifestResourceStream("PerplexityLocalBridge.WebBridge.js")!;
        using var reader = new StreamReader(stream); script = reader.ReadToEnd();
    }
    public async Task InitializeAsync(string? browserFolder=null)
    {
        var environment = await CoreWebView2Environment.CreateAsync(userDataFolder: browserFolder??Path.Combine(BridgeState.DataFolder, "BrowserProfile"));
        await browser.EnsureCoreWebView2Async(environment);
        browser.CoreWebView2.WebMessageReceived += Receive;
        browser.CoreWebView2.NavigationStarting += (_, _) =>
        {
            AccountSessionId = null;
            foreach (var item in pending.Values) item.TrySetException(new BridgeException(503, "page_changed", "Perplexity page changed during the request. Check the session and try again."));
            pending.Clear();
        };
        popups=new WebPopupHost(environment,System.Windows.Window.GetWindow(browser));
        popups.PopupClosed+=()=>LoginWindowClosed?.Invoke();
        popups.PopupFailed+=()=>LoginWindowFailed?.Invoke();
        popups.Attach(browser.CoreWebView2);
        browser.CoreWebView2.Navigate("https://www.perplexity.ai/");
    }
    public async Task<IReadOnlyList<AccountIdentity>> CheckAsync(CancellationToken cancellation = default)
    {
        JsonElement result;
        try { result=await RpcAsync(new { operation = "session" },30,cancellation); }
        catch(BridgeException ex) when(ex.Status==403)
        {
            throw new BridgeException(403,"session_check_forbidden","Perplexity rejected the session check (HTTP 403). Complete sign-in in the browser, including any verification, then click Connect. No generation request was sent.");
        }
        if(result.GetArrayLength()>0&&result[0].TryGetProperty("plan_tier",out var plan)&&plan.ValueKind==JsonValueKind.String)ReportedPlanTier=plan.GetString();
        return result.EnumerateArray().Select(o => new AccountIdentity(o.GetProperty("id").GetString()!, o.GetProperty("name").GetString()!))
            .Where(o => Guid.TryParse(o.Id, out _)).ToArray();
    }
    public Task<JsonElement> InspectModelsAsync()=>RpcAsync(new {operation="inspect_models"},30,CancellationToken.None);
    public async Task<IReadOnlyList<WebModel>> DiscoverModelsAsync(CancellationToken cancellation = default, bool force = false)
    {
        if (!Ready) throw new BridgeException(503,"session_not_ready","Verify your session before refreshing models.");
        var result = await RpcAsync(new { operation = "models",force }, 10, cancellation);
        models.Replace(result.EnumerateArray().Select(m => new WebModel(m.GetProperty("id").GetString()!, m.GetProperty("name").GetString()!,m.TryGetProperty("reasoning",out var reasoning)&&reasoning.GetBoolean())));
        return models.Snapshot;
    }
    public void SetDarkMode(bool dark)
    {
        if (browser.CoreWebView2 is not null) browser.CoreWebView2.Profile.PreferredColorScheme = dark ? CoreWebView2PreferredColorScheme.Dark : CoreWebView2PreferredColorScheme.Light;
        browser.DefaultBackgroundColor = dark ? System.Drawing.Color.FromArgb(17,21,27) : System.Drawing.Color.FromArgb(244,244,241);
    }
    public async Task ClearSavedLoginAsync()
    {
        popups?.CloseAll();AccountSessionId=null;
        if(browser.CoreWebView2 is not null)await browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
    }
    public Task<Completion> CompleteAsync(string prompt, string model, int timeoutSeconds, CancellationToken cancellation)
        =>CompleteWithThinkingAsync(prompt,model,timeoutSeconds,new(),cancellation);
    public Task<Completion> CompleteWithThinkingAsync(string prompt,string model,int timeoutSeconds,ThinkingOptions thinking,CancellationToken cancellation)
        =>CompleteInThreadAsync(prompt,model,timeoutSeconds,thinking,null,null,cancellation);
    public Task<Completion> CompleteInThreadAsync(string prompt,string model,int timeoutSeconds,ThinkingOptions thinking,ThreadCursor? cursor,string? frontendContextId,CancellationToken cancellation)
    {
        if(dispatcher.CheckAccess())return CompleteOnUiAsync(prompt,model,timeoutSeconds,thinking,cursor,frontendContextId,cancellation);
        return dispatcher.InvokeAsync(()=>CompleteOnUiAsync(prompt,model,timeoutSeconds,thinking,cursor,frontendContextId,cancellation)).Task.Unwrap();
    }
    private async Task<Completion> CompleteOnUiAsync(string prompt,string model,int timeoutSeconds,ThinkingOptions thinking,ThreadCursor? cursor,string? frontendContextId,CancellationToken cancellation)
    {
        if(AccountSessionId is null)throw new BridgeException(503,"session_not_ready","Sign in to Perplexity and check the account in the app.");
        var result=await RpcAsync(new{operation="complete",prompt,model,frontend_context_uuid=frontendContextId,
            cursor=cursor is null?null:new{backend_uuid=cursor.BackendId,read_write_token=cursor.ReadWriteToken}},timeoutSeconds,cancellation);
        if(result.TryGetProperty("transport",out var transport))
        {
            try{File.WriteAllText(Path.Combine(BridgeState.DataFolder,"last-prompt-metrics.json"),transport.GetRawText());}catch(IOException){}
        }
        ReportedPlanTier=result.TryGetProperty("plan_tier",out var tier)&&tier.GetString() is "free" or "pro" or "max"?tier.GetString():null;
        ThreadCursor? next=null;
        if(result.TryGetProperty("thread",out var thread)&&thread.ValueKind==JsonValueKind.Object&&
           thread.TryGetProperty("backend_uuid",out var backend)&&Guid.TryParse(backend.GetString(),out _)&&
           thread.TryGetProperty("context_uuid",out var context)&&Guid.TryParse(context.GetString(),out _)&&
           thread.TryGetProperty("read_write_token",out var access)&&access.ValueKind==JsonValueKind.String&&access.GetString() is string token&&token.Length is >0 and <=1024&&!token.Any(char.IsControl))
            next=new(backend.GetString()!,token,context.GetString()!,thread.TryGetProperty("thread_url_slug",out var slug)?slug.GetString():null);
        if(cursor is not null&&next is not null&&cursor.ContextId!=next.ContextId)
            throw new BridgeException(502,"upstream_thread_changed","Perplexity returned a different thread during a continuation. The original mapping was retained for recovery; no answer was forwarded and no automatic retry was made.");
        if(!result.TryGetProperty("transport",out var proof)||!proof.TryGetProperty("echo_equal",out var exact)||exact.ValueKind is not (JsonValueKind.True or JsonValueKind.False))
            throw new BridgeException(502,"upstream_input_unverified","Perplexity did not provide a verifiable request echo. The generated reply was withheld and its thread mapping was retained.",cursor:next);
        if(result.TryGetProperty("transport",out var delivery)&&delivery.TryGetProperty("echo_equal",out var same)&&same.ValueKind==JsonValueKind.False)
            throw new BridgeException(413,"upstream_prompt_truncated","Perplexity changed or truncated the supplied prompt. No generated reply was forwarded. Reduce the request size before retrying.",cursor:next);
        return new Completion(result.GetProperty("text").GetString()!,result.GetProperty("model").GetString()!,Sources:result.TryGetProperty("sources",out var sources)?sources.EnumerateArray().Select(s=>s.GetString()!).ToArray():null,Cursor:next);
    }
    private async Task<JsonElement> RpcAsync(object input, int timeoutSeconds, CancellationToken cancellation)
    {
        if (browser.CoreWebView2 is null || !Uri.TryCreate(browser.Source?.AbsoluteUri, UriKind.Absolute, out var source) || source.Scheme != "https" || source.Host != "www.perplexity.ai")
            throw new BridgeException(503, "wrong_page", "Open perplexity.ai and finish signing in before checking the session.");
        var id = Guid.NewGuid().ToString("N");
        var completion = new TaskCompletionSource<JsonElement>(TaskCreationOptions.RunContinuationsAsynchronously);
        pending[id] = completion;
        var data = JsonSerializer.Serialize(input);
        var wrapper = script + "\n(async()=>{const id='" + id + "';const ctl=new AbortController();" +
            "globalThis.localPerplexityBridgeControllers??={};globalThis.localPerplexityBridgeControllers[id]=ctl;" +
            "try{const result=await localPerplexityBridgeRun(" + data + ",ctl.signal);chrome.webview.postMessage({channel:'local-perplexity-bridge',id,result});}" +
            "catch(e){chrome.webview.postMessage({channel:'local-perplexity-bridge',id,error:String(e.message),diagnostic:e.diagnostic,retry_after:e.retryAfterSeconds});}" +
            "finally{delete globalThis.localPerplexityBridgeControllers[id];}})();";
        try
        {
            await browser.CoreWebView2.ExecuteScriptAsync(wrapper);
            return await completion.Task.WaitAsync(TimeSpan.FromSeconds(timeoutSeconds), cancellation);
        }
        catch (TimeoutException) { throw new BridgeException(504, "upstream_timeout", "Perplexity did not finish before the timeout. The request was not retried."); }
        finally
        {
            pending.Remove(id);
            try { await browser.CoreWebView2.ExecuteScriptAsync("globalThis.localPerplexityBridgeControllers?.['" + id + "']?.abort()"); }
            catch (Exception) { /* The page may have closed or navigated. */ }
        }
    }
    private void Receive(object? sender, CoreWebView2WebMessageReceivedEventArgs e)
    {
        if (!Uri.TryCreate(e.Source, UriKind.Absolute, out var source) || source.Scheme != "https" || source.Host != "www.perplexity.ai") return;
        try
        {
            using var json = JsonDocument.Parse(e.WebMessageAsJson); var root = json.RootElement;
            if (!root.TryGetProperty("channel", out var channel) || channel.GetString() != "local-perplexity-bridge" || !root.TryGetProperty("id", out var id)) return;
            if (!pending.TryGetValue(id.GetString() ?? "", out var completion)) return;
            if (root.TryGetProperty("error", out var error))
            {
                if(Environment.GetCommandLineArgs().Contains("--transport-diagnostics")&&root.TryGetProperty("diagnostic",out var diagnostic)&&diagnostic.GetRawText().Length<20000)
                {
                    try{File.WriteAllText(Path.Combine(BridgeState.DataFolder,"transport-shape.json"),diagnostic.GetRawText());}catch(IOException){}
                }
                var fields = (error.GetString() ?? "browser_error").Split('|', 3);
                var code = fields[0];
                var kind = fields.Length > 1 ? fields[1] : "unknown";
                var reason = fields.Length > 2 ? fields[2] : "unspecified";
                if (!System.Text.RegularExpressions.Regex.IsMatch(kind, @"^[a-z_]{1,64}$")) kind = "unknown";
                if (reason is not ("plan_restriction" or "model_restriction" or "csrf_validation" or "verification" or "access_restriction")) reason = "unspecified";
                var mapped = code switch
                {
                    "http_401" => new BridgeException(503, "session_expired", "Perplexity session is signed out or expired. Sign in and check the session again."),
                    "http_403" when kind is "browser_challenge" or "html_response" => new BridgeException(503, "browser_verification", "Perplexity returned HTTP 403 with a browser verification/HTML response. Inspect the visible Perplexity page. No automatic retry was made."),
                    "http_403" when reason=="model_restriction" => new BridgeException(404,"profile_model_unavailable","The requested model is unavailable for this profile."),
                    "http_403" => new BridgeException(403, "perplexity_web_forbidden", $"Perplexity returned HTTP 403. Error type: {kind}; category: {reason}. This generation request was denied."),
                    "browser_verification" => new BridgeException(503, "unexpected_web_response", "Perplexity returned an unexpected page instead of API data. Inspect the visible browser for verification or a login redirect."),
                    "usage_limited" => new BridgeException(429,"perplexity_limit","Perplexity reported an exhausted usage allowance for this profile."),
                    "http_429" or "rate_limited" => new BridgeException(429,"perplexity_rate_limit","Perplexity temporarily rate-limited this profile. It will be rechecked shortly."),
                    "http_400" => new BridgeException(502, "web_request_rejected", "Perplexity rejected the web request format or model (HTTP 400). The adapter needs a compatibility update."),
                    "http_404" => new BridgeException(502, "web_route_not_found", "Perplexity web route or selected model was not found (HTTP 404)."),
                    "prompt_too_large" => new BridgeException(413,"prompt_too_large","The prompt exceeds the verified 39,000-character web-message budget. No generation was sent."),
                    "no_text_output" => new BridgeException(502,"no_text_output","Perplexity completed the response, but the bridge could not extract answer text."),
                    "unsupported_stream_patch" => new BridgeException(502,"unsupported_stream_patch","Perplexity returned an answer update format the bridge does not yet recognize."),
                    "invalid_stream_json" => new BridgeException(502,"invalid_stream_json","Perplexity returned an unreadable stream event."),
                    "upstream_stream_error" => new BridgeException(502,"upstream_stream_error","Perplexity reported a generation error in its response stream."),
                    "incomplete_stream" => new BridgeException(502, "incomplete_stream", "Perplexity disconnected before the response finished."),
                    "model_picker_missing" => new BridgeException(503,"model_picker_missing","Open a new chat in the visible Perplexity page, then refresh models. The model-picker control was not found."),
                    "no_available_models" => new BridgeException(503,"model_discovery_failed","The model picker did not expose available entries. No model list was invented; open the picker and refresh again."),
                    _ => new BridgeException(502, "web_request_failed", "Perplexity web request could not complete. Check the visible browser and model setting.")
                };
                if(root.TryGetProperty("retry_after",out var retry)&&retry.TryGetInt32(out var seconds)&&seconds is >=1 and <=86400)
                    mapped=new BridgeException(mapped.Status,mapped.Code,mapped.Message,DateTimeOffset.UtcNow.AddSeconds(seconds));
                if (code == "http_401") AccountSessionId = null;
                completion.TrySetException(mapped);
            }
            else if (root.TryGetProperty("result", out var result)) completion.TrySetResult(result.Clone());
        }
        catch (JsonException) { }
        catch (InvalidOperationException) { }
    }
    public void Dispose()
    {
        popups?.Dispose();
        foreach (var item in pending.Values) item.TrySetCanceled(); pending.Clear(); browser.Dispose();
    }
}
