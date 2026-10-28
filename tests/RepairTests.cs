using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using PerplexityLocalBridge;
public static class RepairTests
{
 public static async Task Run(Action<bool,string> check)
 {
  using var body=JsonDocument.Parse("""{"messages":[{"role":"user","content":"Echo a command string"}],"tools":[{"type":"function","function":{"name":"echo","parameters":{"type":"object","properties":{"value":{"type":"string"}},"required":["value"],"additionalProperties":false}}}]}""");
  var request=ChatProtocol.Parse(body.RootElement,true,true,"nonce");
  string Envelope(object args)=>JsonSerializer.Serialize(new{bridge_protocol="nonce",text="",tool_calls=new[]{new{name="echo",arguments=args}}});
  var command="powershell -Command \"Write-Output '{literal}'\"\nUnicode: Δ";
  var envelope=Envelope(new{value=command});
  var decoded=ChatProtocol.Decode(request,new("Here is the result:\n"+envelope+"\nDone.","perplexity-auto"));
  using(var args=JsonDocument.Parse(decoded.ToolCalls![0].Arguments))check(args.RootElement.GetProperty("value").GetString()==command,"Extraction must preserve commands, quoting, Unicode and newlines exactly");
  void Reject(string text){try{ChatProtocol.Decode(request,new(text,"perplexity-auto"));throw new Exception("Expected rejection");}catch(BridgeException ex){check(ex.Code=="invalid_tool_response","Invalid/ambiguous tool responses must be rejected");}}
  Reject(envelope+"\n"+envelope);Reject(envelope+"\n{");Reject(Envelope(new{value=123}));Reject(Envelope(new{value="x",extra=true}));
  Reject(envelope.Replace("\"text\":\"\"","\"text\":\"\",\"text\":\"duplicate\""));
  Reject(envelope.Replace("nonce","wrong-nonce"));Reject(envelope[..^3]);
  var folder=Path.Combine(Path.GetTempPath(),"BridgeRepair-"+Guid.NewGuid().ToString("N"));Directory.CreateDirectory(folder);
  var storePath=Path.Combine(folder,"conversations.dpapi");var store=new ConversationStore(storePath);
  var cursor=new ThreadCursor(Guid.NewGuid().ToString(),"token",Guid.NewGuid().ToString());
  var entry=new ConversationState(new string('d',64),"client","default","QA",cursor.ContextId,Cursor:cursor,RequestHash:"hash");store.Put(entry);
  store.BeginRepair(entry,"hash");var restarted=new ConversationStore(storePath);
  try{restarted.BeginRepair(restarted.Find(entry.Key)!,"hash");throw new Exception("Repeated repair allowed");}catch(BridgeException ex){check(ex.Code=="repair_exhausted","Repair budget must survive process restarts");}
  var state=new BridgeState(Path.Combine(folder,"settings.json"));
  var listener=new System.Net.Sockets.TcpListener(IPAddress.Loopback,0);listener.Start();var port=((IPEndPoint)listener.LocalEndpoint).Port;listener.Stop();state.Update(new BridgeSettings(Port:port));var key=state.CreateKey("QA");
  var calls=new List<ConversationCall>();bool transportFailure=false;
  await using var server=new LocalServer(state,(_,m,_,_)=>Task.FromResult(new Completion("",m)),completeConversation:(call,token)=>{
   calls.Add(call);if(transportFailure)throw new BridgeException(502,"incomplete_stream","Synthetic interrupted transport");
   var reply=call.Repair?JsonSerializer.Serialize(new{bridge_protocol=call.Request!.ProtocolId,text="",tool_calls=new[]{new{name="echo",arguments=new{value=command}}}}):"Malformed initial response";
   return Task.FromResult(new Completion(reply,call.Model,Cursor:cursor));
  });await server.StartAsync();
  using var client=new HttpClient{BaseAddress=new Uri($"http://127.0.0.1:{port}")};client.DefaultRequestHeaders.Authorization=new("Bearer",key.Secret);client.DefaultRequestHeaders.Add("X-Perplexity-Session-Id","repair-test");
  using(var response=await client.PostAsJsonAsync("/v1/chat/completions",body.RootElement)){
   check(response.IsSuccessStatusCode&&response.Headers.GetValues("X-Perplexity-Response-Repair").Single()=="1","One completed malformed response must receive a bounded correction");
   check(calls.Count==2&&!calls[0].Repair&&calls[1].Repair&&calls[0].Key==calls[1].Key&&calls[0].RequestHash==calls[1].RequestHash,"Correction must retain session, request identity and nonce");
  }
  calls.Clear();transportFailure=true;
  using(var response=await client.PostAsJsonAsync("/v1/chat/completions",body.RootElement))check(response.StatusCode==HttpStatusCode.BadGateway&&calls.Count==1,"Ambiguous transport failures must never trigger correction");
 }
}
