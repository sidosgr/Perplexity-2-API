using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using System.Security.Cryptography;
using System.Text;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Hosting;
using Microsoft.AspNetCore.Http;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
namespace PerplexityLocalBridge;

public sealed class LocalServer(BridgeState state, Func<string,string,int,CancellationToken,Task<Completion>> complete, ModelCatalog? catalog = null, Func<string,string,int,ThinkingOptions,CancellationToken,Task<Completion>>? completeThinking = null, Func<ConversationCall,CancellationToken,Task<Completion>>? completeConversation = null, Func<CancellationToken,Task>? refreshModelCatalog = null, Func<string,IReadOnlyList<ConversationSummary>>? conversationSummaries = null) : IAsyncDisposable
{
    private WebApplication? app;
    private readonly ModelCatalog models=catalog??new();
    private readonly SemaphoreSlim generation=new(1,1);
    public int Port { get; private set; }
    public bool Running=>app is not null;
    public async Task StartAsync()
    {
        if(app is not null)return;
        var settings=state.Settings;
        var builder=WebApplication.CreateSlimBuilder(new WebApplicationOptions{Args=[],EnvironmentName="Production"});
        builder.Logging.ClearProviders();
        builder.WebHost.ConfigureKestrel(options=>
        {
            options.Listen(IPAddress.Loopback,settings.Port);
            options.Limits.MaxRequestBodySize=1024*1024;
            options.Limits.RequestHeadersTimeout=TimeSpan.FromSeconds(15);
        });
        var server=builder.Build();
        server.Use(async(context,next)=>
        {
            context.Response.Headers["Cache-Control"]="no-store";
            context.Response.Headers["X-Content-Type-Options"]="nosniff";
            if(context.Connection.RemoteIpAddress is null || !IPAddress.IsLoopback(context.Connection.RemoteIpAddress) || context.Request.Host.Host is not ("127.0.0.1" or "localhost") || context.Request.Headers.ContainsKey("Origin"))
            {await Error(context,403,"local_clients_only","This endpoint accepts local non-browser clients only.");return;}
            if(context.Request.Path=="/health"){await next(context);return;}
            if(settings.RequireApiKey)
            {
                string? key=null;
                if(context.Request.Headers.ContainsKey("Authorization"))
                {
                    if(AuthenticationHeaderValue.TryParse(context.Request.Headers.Authorization.ToString(),out var auth) && auth.Scheme.Equals("Bearer",StringComparison.OrdinalIgnoreCase))key=auth.Parameter?.Trim();
                }
                else if(context.Request.Headers.TryGetValue("x-api-key",out var nativeKey) && nativeKey.Count==1)key=nativeKey.ToString().Trim();
                if(key is null || !state.Authenticate(key)){await Error(context,401,"invalid_api_key","Supply an active local key as Authorization: Bearer <key> or x-api-key. Key authentication can be disabled in the app's endpoint settings.");return;}
                context.Items["client_namespace"]=Digest(key);
            }
            if(!context.Items.ContainsKey("client_namespace"))context.Items["client_namespace"]="no-key";
            await next(context);
        });
        server.MapGet("/health",()=>Results.Json(new{status="running",backend="perplexity-web-experimental",authentication_required=settings.RequireApiKey}));
        foreach(var modelPath in new[]{"/v1/models","/v1/sessions/{bridgeSession}/models"})server.MapGet(modelPath,async(HttpContext context)=>
        {
            if(refreshModelCatalog is not null)await refreshModelCatalog(context.RequestAborted);
            var available=models.Snapshot.Prepend(new WebModel("perplexity-web","Configured default")).DistinctBy(m=>m.Id).ToArray();
            if(available.Length==0)available=[new("perplexity-web","Configured default — refresh models in app")];
            return Results.Json(new{@object="list",data=available.Select(m=>new{id=m.Id,name=m.Name,@object="model",created=0,owned_by="perplexity-web",source="perplexity_model_config",reasoning=m.Reasoning})});
        });
        server.MapGet("/v1/bridge/sessions",(HttpContext context)=>Results.Json(new{data=conversationSummaries?.Invoke(context.Items["client_namespace"] as string??"no-key")??Array.Empty<ConversationSummary>()}));
        if(settings.OpenAi)foreach(var route in new[]{"/v1/chat/completions","/v1/sessions/{bridgeSession}/chat/completions"})server.MapPost(route,context=>Handle(context,true,settings,server.Lifetime.ApplicationStopping));
        foreach(var route in new[]{"/v1/messages","/v1/sessions/{bridgeSession}/messages"})server.MapPost(route,context=>Handle(context,false,settings,server.Lifetime.ApplicationStopping));
        try{await server.StartAsync();app=server;Port=settings.Port;}catch{await server.DisposeAsync();throw;}
    }
    private async Task Handle(HttpContext context,bool openAi,BridgeSettings settings,CancellationToken stopping)
    {
        var entered=false;
        using var cancellation=CancellationTokenSource.CreateLinkedTokenSource(context.RequestAborted,stopping);
        try
        {
            if(context.Request.ContentType is null || !context.Request.ContentType.StartsWith("application/json",StringComparison.OrdinalIgnoreCase))throw new BridgeException(415,"json_required","Use Content-Type: application/json.");
            using var document=await JsonDocument.ParseAsync(context.Request.Body,cancellationToken:cancellation.Token);
            var root=document.RootElement;
            var sessionId=Header(context,"X-Perplexity-Session-Id")??Header(context,"X-Session-Id");
            if(sessionId is null&&root.TryGetProperty("metadata",out var metadata)&&metadata.ValueKind==JsonValueKind.Object&&metadata.TryGetProperty("session_id",out var sid)&&sid.ValueKind==JsonValueKind.String)sessionId=sid.GetString();
            var routeSession=context.Request.RouteValues["bridgeSession"]?.ToString();
            if(routeSession is not null&&sessionId is not null&&routeSession!=sessionId)throw new BridgeException(400,"session_identity_conflict","The session in the base URL must match the session header or metadata. No generation was sent.");
            sessionId??=routeSession;
            if(completeConversation is not null&&sessionId is null)throw new BridgeException(400,"session_id_required","A stable harness session ID is required. Enable the Pi session helper, send X-Perplexity-Session-Id (or metadata.session_id), or use base URL http://127.0.0.1:"+settings.Port+"/v1/sessions/YOUR-SESSION-ID. Keep the same ID for every user and tool turn; use a new ID for each new harness session. No Perplexity conversation was created.");
            if(sessionId is not null&&(sessionId.Length is <1 or >200||sessionId.Any(char.IsControl)))throw new BridgeException(400,"invalid_session_id","Use a non-empty session identifier of at most 200 characters.");
            var projectId=Header(context,"X-Perplexity-Project-Id")??Metadata(root,"project_id")??"default";
            var project=Uri.UnescapeDataString(Header(context,"X-Perplexity-Project-Name")??Metadata(root,"project_name")??"Local client");
            if(projectId.Length>200||projectId.Any(char.IsControl)||project.Length>100||project.Any(char.IsControl))throw new BridgeException(400,"invalid_project","Project metadata is too long or contains control characters.");
            var requestHash=Digest("prompt-v3\n"+JsonSerializer.Serialize(root));
            var request=ChatProtocol.Parse(root,openAi,settings.ToolCalling,sessionId is null?null:requestHash[..32]);
            var thinking=request.HasThinking?request.Thinking:ThinkingOptions.FromLevel(settings.ThinkingLevel);
            context.Response.Headers["X-Local-Perplexity-Thinking"]="website-default;controls=unsupported";
            var warnings=request.Warnings.AsEnumerable();
            if(!request.HasThinking&&thinking.Mode!="off")warnings=warnings.Concat(new[]{"experimental_web_thinking_passthrough","thinking_output_not_returned"});
            context.Response.Headers["X-Local-Perplexity-Compatibility"]=string.Join(",",warnings.Distinct());
            entered=await generation.WaitAsync(0,cancellation.Token);
            if(!entered)throw new BridgeException(429,"request_in_progress","One Perplexity request is already running. Try again after it finishes.");
            using var timeout=CancellationTokenSource.CreateLinkedTokenSource(cancellation.Token);
            timeout.CancelAfter(TimeSpan.FromSeconds(settings.TimeoutSeconds));
            if(refreshModelCatalog is not null)await refreshModelCatalog(timeout.Token);
            var model=models.Resolve(request.Model,settings.Model);
            Completion raw;ConversationCall? conversationCall=null;
            if(completeConversation is not null)
            {
                var clientNamespace=context.Items["client_namespace"] as string??"no-key";
                var key=Digest(clientNamespace+"\n"+projectId+"\n"+(sessionId??Guid.NewGuid().ToString("N")));
                conversationCall=new(request.Prompt,model,settings.TimeoutSeconds,thinking,key,clientNamespace,project,requestHash,request,sessionId is not null);
                raw=await completeConversation(conversationCall,timeout.Token);
                if(sessionId is not null)context.Response.Headers["X-Perplexity-Mapping-Id"]=key;
                if(raw.Cursor is not null)context.Response.Headers["X-Perplexity-Thread-Id"]=raw.Cursor.ContextId;
            }
            else if(sessionId is not null)throw new BridgeException(501,"sessions_unavailable","This backend does not support persistent conversations.");
            else raw=completeThinking is null ? await complete(request.Prompt,model,settings.TimeoutSeconds,timeout.Token) : await completeThinking(request.Prompt,model,settings.TimeoutSeconds,thinking,timeout.Token);
            Completion result;
            try{result=ChatProtocol.Decode(request,raw);}
            catch(BridgeException ex)when(ex.Code=="invalid_tool_response"&&conversationCall is not null&&request.ProtocolId is not null)
            {
                // Only completed replies qualify. Transport failures never reach this branch.
                var correction="Your previous response was rejected by the local adapter. NO tool call from that response was forwarded or executed locally. Return a corrected response for the original caller request as one valid JSON envelope, without prose outside it. Do not execute website tools. Use the declared caller tools via JSON. Preserve intended arguments; do not invent tool results. If the task is complete, return the final answer inside text. CURRENT CONTRACT: "+request.ResponseContract;
                var repairRequest=request with{History=request.History.Concat(new[]{new CallerMessage("user",correction)}).ToArray()};
                raw=await completeConversation!(conversationCall with{Request=repairRequest,Repair=true},timeout.Token);
                result=ChatProtocol.Decode(request,raw);
                context.Response.Headers["X-Perplexity-Response-Repair"]="1";
                if(raw.Cursor is not null)context.Response.Headers["X-Perplexity-Thread-Id"]=raw.Cursor.ContextId;
            }
            if(openAi && request.Stream)await WriteOpenAiSse(context,request,result);
            else if(openAi)
            {
                var message=new Dictionary<string,object?>{{"role","assistant"},{"content",string.IsNullOrEmpty(result.Text)&&result.ToolCalls?.Count>0?null:result.Text}};
                if(result.ToolCalls?.Count>0)message["tool_calls"]=result.ToolCalls.Select(c=>new{id=c.Id,type="function",function=new{name=c.Name,arguments=c.Arguments}});
                await context.Response.WriteAsJsonAsync(new{id="chatcmpl-"+Guid.NewGuid().ToString("N"),@object="chat.completion",created=DateTimeOffset.UtcNow.ToUnixTimeSeconds(),model=result.Model,
                    choices=new[]{new{index=0,message,finish_reason=result.FinishReason}},usage=(object?)null},cancellation.Token);
            }
            else if(request.Stream)await WriteNativeSse(context,result);
            else await context.Response.WriteAsJsonAsync(NativeMessage(result),cancellation.Token);
        }
        catch(BridgeException ex){await Error(context,ex.Status,ex.Code,ex.Message);}
        catch(JsonException){await Error(context,400,"invalid_json","The request must contain valid JSON.");}
        catch(BadHttpRequestException ex){await Error(context,ex.StatusCode,"request_rejected","The request is too large or malformed. Maximum body size: 1 MiB.");}
        catch(OperationCanceledException){if(!context.RequestAborted.IsCancellationRequested)await Error(context,504,"request_cancelled","The request timed out or the endpoint was stopped. It was not retried.");}
        catch(Exception){await Error(context,502,"bridge_error","The browser bridge could not complete this request.");}
        finally{if(entered)generation.Release();}
    }
    private static string? Metadata(JsonElement root,string key)=>root.TryGetProperty("metadata",out var metadata)&&metadata.ValueKind==JsonValueKind.Object&&metadata.TryGetProperty(key,out var value)&&value.ValueKind==JsonValueKind.String?value.GetString():null;
    private static string Digest(string text)=>Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    private static string? Header(HttpContext context,string name)
    {
        if(!context.Request.Headers.TryGetValue(name,out var values))return null;
        if(values.Count!=1||string.IsNullOrWhiteSpace(values[0]))throw new BridgeException(400,"invalid_session_header","Session and project headers must contain one non-empty value.");
        return values[0];
    }
    private static List<object> NativeContent(Completion result)
    {
        var content=new List<object>();
        if(!string.IsNullOrEmpty(result.Text))content.Add(new{type="text",text=result.Text});
        foreach(var call in result.ToolCalls??[])
        {using var args=JsonDocument.Parse(call.Arguments);content.Add(new{type="tool_use",id=call.Id,name=call.Name,input=args.RootElement.Clone()});}
        return content;
    }
    private static object NativeMessage(Completion result)=>new{id="msg_"+Guid.NewGuid().ToString("N"),type="message",role="assistant",model=result.Model,content=NativeContent(result),stop_reason=result.ToolCalls?.Count>0?"tool_use":"end_turn",stop_sequence=(string?)null,usage=(object?)null};
    private static async Task WriteOpenAiSse(HttpContext context,BridgeRequest request,Completion result)
    {
        context.Response.ContentType="text/event-stream";
        var id="chatcmpl-"+Guid.NewGuid().ToString("N");var created=DateTimeOffset.UtcNow.ToUnixTimeSeconds();
        object Chunk(object delta,string? finish)=>new{id,@object="chat.completion.chunk",created,model=result.Model,choices=new[]{new{index=0,delta,finish_reason=finish}}};
        async Task Send(object chunk)=>await context.Response.WriteAsync("data: "+JsonSerializer.Serialize(chunk)+"\n\n",context.RequestAborted);
        await Send(Chunk(new{role="assistant",content=result.Text},null));
        for(var i=0;i<(result.ToolCalls?.Count??0);i++)
        {var call=result.ToolCalls![i];await Send(Chunk(new{tool_calls=new[]{new{index=i,id=call.Id,type="function",function=new{name=call.Name,arguments=call.Arguments}}}},null));}
        await Send(Chunk(new{},result.FinishReason));
        if(request.IncludeUsage)await Send(new{id,@object="chat.completion.chunk",created,model=result.Model,choices=Array.Empty<object>(),usage=(object?)null});
        await context.Response.WriteAsync("data: [DONE]\n\n",context.RequestAborted);await context.Response.Body.FlushAsync(context.RequestAborted);
    }
    private static async Task WriteNativeSse(HttpContext context,Completion result)
    {
        context.Response.ContentType="text/event-stream";
        async Task Send(string name,object data)=>await context.Response.WriteAsync("event: "+name+"\ndata: "+JsonSerializer.Serialize(data)+"\n\n",context.RequestAborted);
        var id="msg_"+Guid.NewGuid().ToString("N");
        await Send("message_start",new{type="message_start",message=new{id,type="message",role="assistant",model=result.Model,content=Array.Empty<object>(),stop_reason=(string?)null,usage=(object?)null}});
        int index=0;
        if(!string.IsNullOrEmpty(result.Text))
        {
            await Send("content_block_start",new{type="content_block_start",index,content_block=new{type="text",text=""}});
            await Send("content_block_delta",new{type="content_block_delta",index,delta=new{type="text_delta",text=result.Text}});
            await Send("content_block_stop",new{type="content_block_stop",index});index++;
        }
        foreach(var call in result.ToolCalls??[])
        {
            await Send("content_block_start",new{type="content_block_start",index,content_block=new{type="tool_use",id=call.Id,name=call.Name,input=new{}}});
            await Send("content_block_delta",new{type="content_block_delta",index,delta=new{type="input_json_delta",partial_json=call.Arguments}});
            await Send("content_block_stop",new{type="content_block_stop",index});index++;
        }
        await Send("message_delta",new{type="message_delta",delta=new{stop_reason=result.ToolCalls?.Count>0?"tool_use":"end_turn",stop_sequence=(string?)null},usage=(object?)null});
        await Send("message_stop",new{type="message_stop"});await context.Response.Body.FlushAsync(context.RequestAborted);
    }
    private static async Task Error(HttpContext context,int status,string code,string message)
    {
        if(context.Response.HasStarted || context.RequestAborted.IsCancellationRequested)return;
        context.Response.StatusCode=status;await context.Response.WriteAsJsonAsync(new{error=new{message,type="bridge_error",code}});
    }
    public async ValueTask DisposeAsync()
    {
        if(app is null)return;var closing=app;app=null;using var timeout=new CancellationTokenSource(TimeSpan.FromSeconds(5));
        try{await closing.StopAsync(timeout.Token);}finally{await closing.DisposeAsync();}
    }
}
