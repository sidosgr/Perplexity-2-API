using PerplexityLocalBridge;
using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Net.Sockets;
using System.Text.Json;

var count = 0;
void Check(bool value, string message) { if (!value) throw new Exception(message); count++; }
var folder = Path.Combine(Path.GetTempPath(), "LocalPerplexityBridge-Tests-" + Guid.NewGuid().ToString("N"));
var path = Path.Combine(folder, "settings.json");
var state = new BridgeState(path);
BridgeRequest ParseThinking(string fields)
{
    using var document=JsonDocument.Parse("{\"messages\":[{\"role\":\"user\",\"content\":\"OK\"}],"+fields+"}");
    return ChatProtocol.Parse(document.RootElement,true,true);
}
foreach(var field in new[]{"reasoning_effort","reasoning","thinking","enable_thinking","output_config"})
{
 try{ParseThinking("\""+field+"\":true");throw new Exception("Unsupported reasoning accepted");}
 catch(BridgeException ex){Check(ex.Code=="unsupported_reasoning","Reasoning must be rejected explicitly");}
}
Check(state.Settings.AutoStartEndpoint&&!state.Settings.EndpointEnabled&&state.Settings.ShowBrowser,"First launch must show sign-in and wait for connection");
Check(state.Settings.Model=="perplexity-auto"&&!state.Settings.CheckUpdatesOnStartup,"Defaults must use automatic model and no update network checks");
Check(state.Settings.DarkMode && state.Settings.RequireApiKey && state.Settings.ToolCalling,"New settings must default to dark mode, authenticated local access and tool translation");
var key = state.CreateKey("Test client");
Check(state.Authenticate(key.Secret), "Created key must authenticate");
Check(!state.Authenticate(key.Secret + "wrong"), "Wrong key must fail");
Check(!File.ReadAllText(path).Contains(key.Secret), "Plaintext key must not be saved");
Check(new BridgeState(path).Authenticate(key.Secret), "Key hash must persist");
var rememberedAccountIdentity = Guid.NewGuid().ToString();
state.Update(state.Settings with { AccountSessionId = rememberedAccountIdentity });
Check(new BridgeState(path).Settings.AccountSessionId == rememberedAccountIdentity, "Account identity preference must persist");
var used = new TcpListener(IPAddress.Loopback, 0); used.Start();
var port = ((IPEndPoint)used.LocalEndpoint).Port; used.Stop();
state.Update(new BridgeSettings(port));
var invocations = 0;
var wait = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
var catalog = new ModelCatalog();
await using var server = new LocalServer(state, async (prompt, model, seconds, cancellation) =>
{
    Interlocked.Increment(ref invocations);
    if (prompt == "block") { blocked.TrySetResult(); await wait.Task.WaitAsync(cancellation); }
    var protocol = System.Text.RegularExpressions.Regex.Match(prompt,"bridge_protocol must equal \\\"([a-f0-9]{32})\\\"");
    if(protocol.Success)
    {
        if(prompt.Contains("RETURN_FINAL_TOOL_TEST"))return new Completion(JsonSerializer.Serialize(new{bridge_protocol=protocol.Groups[1].Value,text="done",tool_calls=Array.Empty<object>()}),model);
        var tool=prompt.Contains("MALFORMED_TOOL_TEST")?"invented_tool":"echo";
        return new Completion(JsonSerializer.Serialize(new{bridge_protocol=protocol.Groups[1].Value,text="",tool_calls=new[]{new{name=tool,arguments=new{value="hello"}}}}),model);
    }
    return new Completion(prompt == "Reply with exactly OK." ? "OK" : prompt, model);
},catalog);
await server.StartAsync();
using var client = new HttpClient { BaseAddress = new Uri($"http://127.0.0.1:{port}"), Timeout = TimeSpan.FromSeconds(10) };
Check((await client.GetAsync("/health")).IsSuccessStatusCode, "Health must work");
Check((await client.GetAsync("/v1/models")).StatusCode == HttpStatusCode.Unauthorized, "Models need authentication");
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", key.Secret);
Check((await client.GetAsync("/v1/models")).IsSuccessStatusCode, "Valid key must work");
using (var request = new HttpRequestMessage(HttpMethod.Get, "/v1/models"))
{
    request.Headers.Add("Origin", "https://example.com");
    Check((await client.SendAsync(request)).StatusCode == HttpStatusCode.Forbidden, "Browser origins must be rejected");
}
using (var request = new HttpRequestMessage(HttpMethod.Get, "/health"))
{
    request.Headers.Host = "evil.example";
    Check((await client.SendAsync(request)).StatusCode == HttpStatusCode.Forbidden, "Foreign Host must be rejected");
}
object Body(string prompt, bool stream = false) => new { model = "perplexity-web", messages = new[] { new { role = "user", content = prompt } }, stream };
using (var answer = await client.PostAsJsonAsync("/v1/chat/completions", Body("Reply with exactly OK.")))
{
    using var json = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
    Check(answer.IsSuccessStatusCode && json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString() == "OK", "Chat response translation failed");
    Check(json.RootElement.GetProperty("usage").ValueKind == JsonValueKind.Null, "Must not invent token counts");
}
using (var answer = await client.PostAsJsonAsync("/v1/chat/completions", Body("hello", true)))
{
    var text = await answer.Content.ReadAsStringAsync();
    Check(answer.Content.Headers.ContentType?.MediaType == "text/event-stream" && text.EndsWith("data: [DONE]\n\n"), "SSE must terminate");
    Check(text.Split("\"finish_reason\":\"stop\"").Length == 2, "SSE must finish once");
}
using (var answer = await client.PostAsJsonAsync("/v1/messages", Body("hello")))
{
    using var json = JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
    Check(json.RootElement.GetProperty("content")[0].GetProperty("text").GetString() == "hello", "Native response failed");
}
var before = invocations;
using(var answer=await client.PostAsJsonAsync("/v1/chat/completions",new{model="perplexity-web",messages=new[]{new{role="user",content="hello"}},max_tokens=16,temperature=0.7,store=false,stream_options=new{include_usage=true}}))
{
 Check(answer.IsSuccessStatusCode,"Pi compatibility fields must be accepted");
 Check(answer.Headers.GetValues("X-Local-Perplexity-Compatibility").Single().Contains("token_budget_as_prompt_hint") && answer.Headers.GetValues("X-Local-Perplexity-Compatibility").Single().Contains("temperature_not_enforced"),"Compatibility caveats must be explicit");
}
before=invocations;
Check((await client.PostAsJsonAsync("/v1/chat/completions", new { messages = new[] { new { role = "user", content = new[] { new { type = "image_url", image_url = "hidden" } } } } })).StatusCode == HttpStatusCode.BadRequest, "Images must not silently drop");
Check(invocations == before, "Invalid requests must not reach Perplexity");
using(var answer=await client.PostAsJsonAsync("/v1/messages",Body("hello",true)))
 Check(answer.IsSuccessStatusCode && (await answer.Content.ReadAsStringAsync()).Contains("event: message_stop"),"Buffered native SSE must finish");
using (var answer = await client.PostAsync("/v1/messages", new StringContent("not json", System.Text.Encoding.UTF8, "application/json")))
    Check(answer.StatusCode == HttpStatusCode.BadRequest, "Malformed JSON must reject");
using (var answer = await client.PostAsJsonAsync("/v1/messages", Body("x" + new string('x', 1200000))))
    Check(answer.StatusCode == HttpStatusCode.RequestEntityTooLarge, "Large body must reject");
var first = client.PostAsJsonAsync("/v1/messages", Body("block"));
await blocked.Task.WaitAsync(TimeSpan.FromSeconds(5));
Check((await client.PostAsJsonAsync("/v1/messages", Body("second"))).StatusCode == HttpStatusCode.TooManyRequests, "Concurrent generation must reject");
wait.TrySetResult(); using (var answer = await first) Check(answer.IsSuccessStatusCode, "Original request must finish");
catalog.Replace(new[]{new WebModel("perplexity-fixture-a","Fixture A"),new WebModel("perplexity-fixture-b","Fixture B")});
using(var answer=await client.GetAsync("/v1/models"))
{
 using var json=JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
 Check(json.RootElement.GetProperty("data").GetArrayLength()==3,"Discovered models must retain the configured-default alias");
}
using(var answer=await client.PostAsJsonAsync("/v1/chat/completions",new{model="perplexity-fixture-b",messages=new[]{new{role="user",content="hello"}}}))
{
 using var json=JsonDocument.Parse(await answer.Content.ReadAsStringAsync());Check(json.RootElement.GetProperty("model").GetString()=="perplexity-fixture-b","Requests must route the discovered model");
}
Check((await client.PostAsJsonAsync("/v1/chat/completions",new{model="perplexity-unknown-9",messages=new[]{new{role="user",content="hi"}}})).StatusCode==HttpStatusCode.NotFound,"Unknown models must reject");
var toolDefinition=new{type="function",function=new{name="echo",description="Echo a value",parameters=new{type="object",properties=new{value=new{type="string"}},required=new[]{"value"}}}};
object ToolBody(string text,bool stream=false)=>new{model="perplexity-web",messages=new[]{new{role="user",content=text}},tools=new[]{toolDefinition},tool_choice="required",parallel_tool_calls=false,stream,stream_options=new{include_usage=true},max_completion_tokens=2048};
string callId;
using(var answer=await client.PostAsJsonAsync("/v1/chat/completions",ToolBody("CALL_TEST_TOOL")))
{
 using var json=JsonDocument.Parse(await answer.Content.ReadAsStringAsync());
 Check(answer.IsSuccessStatusCode && json.RootElement.GetProperty("choices")[0].GetProperty("finish_reason").GetString()=="tool_calls","Tool turn must finish with tool_calls");
 var call=json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("tool_calls")[0];callId=call.GetProperty("id").GetString()!;
 Check(call.GetProperty("function").GetProperty("name").GetString()=="echo" && JsonDocument.Parse(call.GetProperty("function").GetProperty("arguments").GetString()!).RootElement.GetProperty("value").GetString()=="hello","Tool arguments must remain JSON");
}
using(var answer=await client.PostAsJsonAsync("/v1/chat/completions",ToolBody("CALL_TEST_TOOL",true)))
{
 var text=await answer.Content.ReadAsStringAsync();Check(text.Contains("\"tool_calls\"")&&text.Contains("\"usage\":null")&&text.EndsWith("data: [DONE]\n\n"),"Tool SSE and include_usage must be SDK compatible");
}
var history=new object[]{new{role="user",content="CALL_TEST_TOOL"},new{role="assistant",content=(string?)null,tool_calls=new[]{new{id=callId,type="function",function=new{name="echo",arguments="{\"value\":\"hello\"}"}}}},new{role="tool",tool_call_id=callId,content="RETURN_FINAL_TOOL_TEST: hello"}};
using(var answer=await client.PostAsJsonAsync("/v1/chat/completions",new{model="perplexity-web",messages=history,tools=new[]{toolDefinition}}))
{
 using var json=JsonDocument.Parse(await answer.Content.ReadAsStringAsync());Check(answer.IsSuccessStatusCode&&json.RootElement.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()=="done","Tool result history must complete another turn");
}
Check((await client.PostAsJsonAsync("/v1/chat/completions",ToolBody("MALFORMED_TOOL_TEST"))).StatusCode==HttpStatusCode.BadGateway,"Invented tools must not be forwarded");
using(var nativeAuth=new HttpRequestMessage(HttpMethod.Get,"/v1/models"))
{
 nativeAuth.Headers.Add("x-api-key",key.Secret);nativeAuth.Headers.Authorization=null;
 // Default request auth is overridden explicitly so only the native key is present.
 client.DefaultRequestHeaders.Authorization=null;
 Check((await client.SendAsync(nativeAuth)).IsSuccessStatusCode,"Native x-api-key must authenticate");
 client.DefaultRequestHeaders.Authorization=new AuthenticationHeaderValue("Bearer",key.Secret);
}
state.Revoke(key.Key.Id);
Check((await client.GetAsync("/v1/models")).StatusCode == HttpStatusCode.Unauthorized, "Revoked key must stop working");
await server.DisposeAsync();
state.Update(new BridgeSettings(port, false));
var secondKey = state.CreateKey("Second client");
client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", secondKey.Secret);
await server.StartAsync();
Check((await client.PostAsJsonAsync("/v1/chat/completions", Body("hello"))).StatusCode == HttpStatusCode.NotFound, "OpenAI toggle must remove route");
await server.DisposeAsync();
state.Update(new BridgeSettings(port,RequireApiKey:false));
client.DefaultRequestHeaders.Authorization=null;
await server.StartAsync();
Check((await client.GetAsync("/v1/models")).IsSuccessStatusCode,"No-key mode must allow missing credentials");
using(var browser=new HttpRequestMessage(HttpMethod.Get,"/v1/models"))
{
 browser.Headers.Add("Origin","https://example.com");Check((await client.SendAsync(browser)).StatusCode==HttpStatusCode.Forbidden,"No-key mode must preserve browser-origin restrictions");
}
await server.DisposeAsync();
await using(var reasoningServer=new LocalServer(state,(_,model,_,_)=>Task.FromResult(new Completion("OK",model)),catalog))
{
 await reasoningServer.StartAsync();
 foreach(var level in new[]{"none","low","medium","high","xhigh","max"})
 {
  using var response=await client.PostAsJsonAsync("/v1/chat/completions",new{messages=new[]{new{role="user",content="OK"}},reasoning_effort=level});
  Check(response.StatusCode==HttpStatusCode.BadRequest,"Unsupported reasoning rejected over HTTP: "+level);
 }
}
var failoverStore=new ProfileStore(Path.Combine(folder,"http-profiles.json"));var httpBackup=failoverStore.Add("HTTP backup");var httpRouter=new ProfileRouter(failoverStore);var attempted=new List<string>();
await using(var failoverServer=new LocalServer(state,(prompt,model,seconds,token)=>httpRouter.CompleteAsync(true,300,(_,_)=>Task.FromResult(true),(profile,_)=>
{
 attempted.Add(profile.Id);if(profile.Id=="default")throw new BridgeException(429,"perplexity_limit","Allowance exhausted");return Task.FromResult(new Completion("OK from backup",model));
},null,token),catalog))
{
 await failoverServer.StartAsync();
 using var response=await client.PostAsJsonAsync("/v1/chat/completions",Body("OK"));
 var json=await response.Content.ReadFromJsonAsync<JsonElement>();
 Check(response.IsSuccessStatusCode&&json.GetProperty("choices")[0].GetProperty("message").GetProperty("content").GetString()=="OK from backup","OpenAI HTTP request must return the second account's response after first account quota failure");
 Check(attempted.SequenceEqual(new[]{"default",httpBackup.Id})&&failoverStore.ActiveId==httpBackup.Id,"HTTP failover must attempt each account once and retain the successful account");
}
Console.WriteLine($"PASS: {count} integration assertions. Synthetic backend only; no Perplexity account requests.");
await ProfileTests.Run(Check);
Console.WriteLine($"PASS: {count} total API/profile assertions. Failover is tested with synthetic quota responses.");


await ConversationTests.Run(Check);
Console.WriteLine($"PASS: {count} total assertions including encrypted conversation storage and HTTP session isolation.");

PromptPlannerTests.Run(Check);
Console.WriteLine($"PASS: {count} total assertions including bounded context transfers and incremental history.");
await RepairTests.Run(Check); Console.WriteLine($"PASS: {count} total assertions including bounded response recovery.");

{
 var folderLanQa=Path.Combine(Path.GetTempPath(),"lan-keyLanQa-qa-"+Guid.NewGuid());Directory.CreateDirectory(folderLanQa);
 try{
 var pathLanQa=Path.Combine(folderLanQa,"settingsLanQa.json");var storeLanQa=new BridgeState(pathLanQa);var createdLanQa=storeLanQa.CreateKey("Repeat copy");
 Check(storeLanQa.CopyableSecret(createdLanQa.Key.Id)==createdLanQa.Secret,"New keyLanQa must be copyable");
 var restoredLanQa=new BridgeState(pathLanQa);Check(restoredLanQa.CopyableSecret(createdLanQa.Key.Id)==createdLanQa.Secret,"Key must remain copyable after restart");
 Check(!File.ReadAllText(pathLanQa).Contains(createdLanQa.Secret),"Saved keyLanQa must not be plaintext");
 restoredLanQa.Revoke(createdLanQa.Key.Id);Check(!restoredLanQa.Authenticate(createdLanQa.Secret),"Revoked keyLanQa must fail authentication");
 try{restoredLanQa.CopyableSecret(createdLanQa.Key.Id);Check(false,"Revoked keyLanQa must not be copyable");}catch(ArgumentException){Check(true,"Revoked keyLanQa copy rejected");}
 var settingsLanQa=new BridgeSettings(LanMode:true,LanAddress:"192.168.1.20");
 Check(NetworkAccess.Allowed(System.Net.IPAddress.Parse("192.168.1.2"),"192.168.1.20",false,settingsLanQa),"LAN client on configured host accepted");
 Check(!NetworkAccess.Allowed(System.Net.IPAddress.Parse("8.8.8.8"),"192.168.1.20",false,settingsLanQa),"Public remote rejected");
 Check(!NetworkAccess.Allowed(System.Net.IPAddress.Parse("192.168.1.2"),"evil.example",false,settingsLanQa),"Wrong Host rejected");
 Check(!NetworkAccess.Allowed(System.Net.IPAddress.Parse("192.168.1.2"),"192.168.1.20",true,settingsLanQa),"Browser Origin rejected");
 Check(!NetworkAccess.Allowed(System.Net.IPAddress.Parse("192.168.1.2"),"192.168.1.20",false,new()),"LAN disabled blocks remote");
 try{BridgeState.Validate(settingsLanQa with{RequireApiKey=false});Check(false,"LAN no-keyLanQa forbidden");}catch(ArgumentException){Check(true,"LAN no-keyLanQa rejected");}
 var ips=NetworkAccess.Addresses();
 if(ips.Length>0){
 var listenerLanQa=new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback,0);listenerLanQa.Start();var portLanQa=((System.Net.IPEndPoint)listenerLanQa.LocalEndpoint).Port;listenerLanQa.Stop();
 storeLanQa.Update(new BridgeSettings(Port:portLanQa,LanMode:true,LanAddress:ips[0]));var keyLanQa=storeLanQa.CreateKey("LAN test");
 await using var serverLanQa=new LocalServer(storeLanQa,(_,m,_,_)=>Task.FromResult(new Completion("OK",m)));await serverLanQa.StartAsync();
 using var httpLanQa=new HttpClient(new HttpClientHandler{UseProxy=false});
 var uriLanQa=$"http://{ips[0]}:{portLanQa}";
 Check((await httpLanQa.GetAsync(uriLanQa+"/health")).StatusCode==System.Net.HttpStatusCode.Unauthorized,"LAN health requires keyLanQa");
 httpLanQa.DefaultRequestHeaders.Authorization=new("Bearer",keyLanQa.Secret);
 Check((await httpLanQa.GetAsync(uriLanQa+"/health")).IsSuccessStatusCode,"Bound LAN interface accepts authenticated request");
 storeLanQa.Revoke(keyLanQa.Key.Id);Check((await httpLanQa.GetAsync(uriLanQa+"/v1/models")).StatusCode==System.Net.HttpStatusCode.Unauthorized,"Revocation applies immediately on LAN");
 }
 }finally{Directory.Delete(folderLanQa,true);}
}
Console.WriteLine($"PASS: {count} total assertions including LAN binding/policy and encrypted repeat-copy keys.");
