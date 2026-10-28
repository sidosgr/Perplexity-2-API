using System.Net;
using System.Net.Http.Json;
using System.Security.Cryptography;
using System.Text;
using PerplexityLocalBridge;
public static class ConversationTests
{
 public static async Task Run(Action<bool,string> check)
 {
  var folder=Path.Combine(Path.GetTempPath(),"PerplexityConversationTests-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  var storePath=Path.Combine(folder,"conversations.dpapi");var key=new string('a',64);
  var cursor=new ThreadCursor(Guid.NewGuid().ToString(),"private-thread-access-token",Guid.NewGuid().ToString());
  var store=new ConversationStore(storePath);
  var pending=new ConversationState(key,"client-a","profile-a","Project A",Guid.NewGuid().ToString(),Pending:true,RequestHash:"request-a");
  store.Put(pending);check(new ConversationStore(storePath).Find(key)?.Pending==true,"Interrupted turns must remain marked across restarts");
  store.Put(pending with{Pending=false,Cursor=cursor,Cached=new Completion("private synthetic reply","perplexity-auto",Cursor:cursor)});
  var restored=new ConversationStore(storePath).Find(key)!;
  check(restored.Cursor==cursor&&restored.Cached?.Text=="private synthetic reply","Thread continuation and cached response must survive restart");
  var disk=Encoding.UTF8.GetString(File.ReadAllBytes(storePath));
  check(!disk.Contains("private-thread-access-token")&&!disk.Contains("private synthetic reply"),"Thread access and cached output must be encrypted at rest");
  store.Restore(key,pending);check(store.Find(key)?.Pending==true,"A rejected request can restore its previous mapping");
  var secondKey=new string('b',64);store.Put(pending with{Key=secondKey,Project="Project B"});
  store.Restore(key,null);check(store.Find(key)is null&&store.Find(secondKey)is not null,"Removing a new rejected request must preserve other sessions");
  var thirdKey=new string('c',64);store.Put(pending with{Key=secondKey,Pending=false,Cursor=cursor,Cached=new Completion("erase this reply","perplexity-auto")});store.Put(pending with{Key=thirdKey,ProfileId="profile-b",Pending=false,Cursor=cursor});
  store.ForgetAccount("profile-a");check(store.Find(secondKey)?.Cursor is null&&store.Find(secondKey)?.Cached is null&&store.Find(secondKey)?.ThreadId==cursor.ContextId&&store.Find(thirdKey)?.Cursor==cursor,"Removing an account must clear its access tokens and cached output while keeping other accounts intact");
  File.WriteAllText(storePath,"corrupt");
  try{_ = new ConversationStore(storePath);throw new Exception("Corrupt mappings were silently discarded");}catch(BridgeException e){check(e.Code=="session_store_unavailable","Corrupt stores must fail without creating replacement threads");}
  var settings=new BridgeState(Path.Combine(folder,"settings.json"));
  var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();
  settings.Update(new BridgeSettings(Port:port));var keyA=settings.CreateKey("Client A");var keyB=settings.CreateKey("Client B");
  var calls=new List<ConversationCall>();
  var catalogue=new ModelCatalog();var failRefresh=false;var refreshes=0;
  await using var server=new LocalServer(settings,(_,m,_,_)=>Task.FromResult(new Completion("stateless",m)),catalog:catalogue,refreshModelCatalog:_=>{refreshes++;if(failRefresh)throw new BridgeException(503,"discovery_unavailable","Synthetic connection failure");catalogue.Replace([new("perplexity-test-model","Test")]);return Task.CompletedTask;},completeConversation:(call,_)=>{calls.Add(call);return Task.FromResult(new Completion("OK",call.Model,Cursor:cursor));});
  await server.StartAsync();using var client=new HttpClient{BaseAddress=new Uri($"http://127.0.0.1:{port}")};
  async Task Send(string session,string project,string secret,bool metadata=false)
  {
   using var req=new HttpRequestMessage(HttpMethod.Post,"/v1/chat/completions");req.Headers.Authorization=new("Bearer",secret);
   if(!metadata){req.Headers.Add("X-Perplexity-Session-Id",session);req.Headers.Add("X-Perplexity-Project-Id",project);}
   req.Content=JsonContent.Create(new{model="perplexity-auto",messages=new[]{new{role="user",content="OK"}},metadata=metadata?new{session_id=session,project_id=project}:null});
   using var answer=await client.SendAsync(req);check(answer.IsSuccessStatusCode,"Session request must succeed");check(answer.Headers.GetValues("X-Perplexity-Thread-Id").Single()==cursor.ContextId,"Clients must receive the stable upstream thread identity");
  }
  await Send("session-a","project-a",keyA.Secret);await Send("session-a","project-a",keyA.Secret);
  check(calls[0].Key==calls[1].Key&&calls[0].RequestHash==calls[1].RequestHash,"Same agent session and identical request must retain their identities");
  await Send("session-b","project-a",keyA.Secret);await Send("session-a","project-b",keyA.Secret);await Send("session-a","project-a",keyB.Secret);
  check(calls.Select(c=>c.Key).Distinct().Count()==4,"Separate agent sessions, projects and client credentials must never share mappings");
  await Send("session-a","project-a",keyA.Secret,true);
  check(calls[0].Key==calls[^1].Key,"Metadata fallback must route to the same session as explicit headers");
  var previousCount=calls.Count;
  using(var missing=new HttpRequestMessage(HttpMethod.Post,"/v1/chat/completions")){
   missing.Headers.Authorization=new("Bearer",keyA.Secret);missing.Content=JsonContent.Create(new{messages=new[]{new{role="user",content="OK"}}});
   using var response=await client.SendAsync(missing);check(response.StatusCode==HttpStatusCode.BadRequest&&(await response.Content.ReadAsStringAsync()).Contains("session_id_required")&&calls.Count==previousCount,"Missing identity must fail before creating an upstream conversation");
  }
  async Task Scoped(string id,string? header=null){
   using var req=new HttpRequestMessage(HttpMethod.Post,"/v1/sessions/"+id+"/chat/completions");req.Headers.Authorization=new("Bearer",keyA.Secret);
   if(header is not null)req.Headers.Add("X-Perplexity-Session-Id",header);
   req.Content=JsonContent.Create(new{model="perplexity-auto",messages=new[]{new{role="user",content="OK"}}});
   using var response=await client.SendAsync(req);check(response.StatusCode==(header is null?HttpStatusCode.OK:HttpStatusCode.BadRequest),"Scoped base URL must route or reject conflicting identities");
  }
  await Scoped("scoped-a");await Scoped("scoped-a");check(calls[^1].Key==calls[^2].Key&&calls[^1].Persistent,"Scoped clients must retain one persistent mapping across turns");
  await Scoped("scoped-b");check(calls[^1].Key!=calls[^2].Key,"A fresh scoped session must receive a distinct mapping");
  previousCount=calls.Count;await Scoped("scoped-a","different");check(calls.Count==previousCount,"Identity conflict must never generate");
  using(var req=new HttpRequestMessage(HttpMethod.Get,"/v1/sessions/scoped-a/models")){req.Headers.Authorization=new("Bearer",keyA.Secret);using var response=await client.SendAsync(req);check(response.IsSuccessStatusCode,"Scoped clients must discover models from their base URL");}
  async Task ModelTurn(HttpStatusCode expected){
   using var req=new HttpRequestMessage(HttpMethod.Post,"/v1/sessions/model-refresh/chat/completions");req.Headers.Authorization=new("Bearer",keyA.Secret);
   req.Content=JsonContent.Create(new{model="perplexity-test-model",messages=new[]{new{role="user",content="OK"}}});
   using var response=await client.SendAsync(req);check(response.StatusCode==expected,"Generation must hydrate a cleared model catalogue or preserve discovery errors");
  }
  catalogue.Clear();await ModelTurn(HttpStatusCode.OK);var modelKey=calls[^1].Key;
  catalogue.Clear();await ModelTurn(HttpStatusCode.OK);check(calls[^1].Key==modelKey,"Hydrating models between turns must preserve session identity");
  previousCount=calls.Count;failRefresh=true;await ModelTurn(HttpStatusCode.ServiceUnavailable);check(calls.Count==previousCount,"Failed discovery must not generate or masquerade as an unavailable model");failRefresh=false;
  using var bad=new HttpRequestMessage(HttpMethod.Post,"/v1/chat/completions");bad.Headers.Authorization=new("Bearer",keyA.Secret);bad.Headers.TryAddWithoutValidation("X-Perplexity-Session-Id"," ");bad.Content=JsonContent.Create(new{messages=new[]{new{role="user",content="OK"}}});
  using var invalid=await client.SendAsync(bad);check(invalid.StatusCode==HttpStatusCode.BadRequest,"Empty session headers must be rejected");
 }
}
