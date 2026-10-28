using System.Text.Json;
using PerplexityLocalBridge;
public static class PromptPlannerTests
{
 public static void Run(Action<bool,string> check)
 {
  var padding="browser neutral guidance "+new string('x',65000);
  var toolDescription="external caller tool "+new string('d',25000);
  BridgeRequest Parse(string system,object[] history)=>ChatProtocol.Parse(JsonSerializer.SerializeToElement(new{messages=new[]{new{role="system",content=system}}.Cast<object>().Concat(history).ToArray(),tools=new[]{new{type="function",function=new{name="echo",description=toolDescription,parameters=new{type="object",properties=new{value=new{type="string"}},required=new[]{"value"}}}}}}),true,true,"00112233445566778899aabbccddeeff");
  var first=Parse(padding,[new{role="user",content="LATEST_USER_TASK_MUST_SURVIVE"}]);
  var plan=PromptPlanner.Build(first,null);
  check(plan.TransferParts>=3,"Large system and tool context must be transferred without dropping content");
  check(plan.Steps.All(s=>s.Text.Length<=PromptPlanner.MaximumWireCharacters),"Every native web message must fit the measured transport budget");
  var recovered=string.Concat(plan.Steps.Where(s=>!s.Execute).Select(s=>{var start=s.Text.IndexOf("BEGIN FRAGMENT\n",StringComparison.Ordinal)+"BEGIN FRAGMENT\n".Length;var end=s.Text.LastIndexOf("\nEND FRAGMENT",StringComparison.Ordinal);return s.Text[start..end];}));
  check(recovered.Contains(padding)&&recovered.Contains(toolDescription)&&recovered.Contains("LATEST_USER_TASK_MUST_SURVIVE"),"Transferred payload must preserve the full system instructions, tool definitions and final user request");
  check(plan.Steps[^1].Execute&&plan.Steps[^1].Text.Contains("LATEST_USER_TASK_MUST_SURVIVE")&&plan.Steps[^1].Text.Contains(first.ProtocolId!),"Execution must identify the actual latest request and current tool contract");
  var saved=new ConversationState(new string('a',64),"qa","account","Project",Guid.NewGuid().ToString(),Cursor:new(Guid.NewGuid().ToString(),"thread-access",Guid.NewGuid().ToString()),ContextHash:plan.ContextHash,HistoryHashes:plan.HistoryHashes);
  var next=Parse(padding,[new{role="user",content="LATEST_USER_TASK_MUST_SURVIVE"},new{role="assistant",content="OK"},new{role="user",content="NEXT_TURN_ONLY"}]);
  var delta=PromptPlanner.Build(next,saved);
  check(delta.TransferParts==0&&delta.Steps.Count==1&&!delta.Steps[0].Text.Contains(padding)&&!delta.Steps[0].Text.Contains(toolDescription)&&delta.Steps[0].Text.Contains("NEXT_TURN_ONLY"),"Follow-ups must use retained context and send only new history entries");
  var changed=PromptPlanner.Build(Parse(padding+" updated",[new{role="user",content="LATEST_USER_TASK_MUST_SURVIVE"}]),saved);
  check(changed.TransferParts>0&&changed.ContextHash!=plan.ContextHash,"Changed caller instructions must be transferred again");
  var partial=saved with{ContextHash=null,HistoryHashes=null,StagedInputHash=plan.TransferHash,StagedInputPart=1};
  var resumed=PromptPlanner.Build(first,partial);
  check(resumed.TransferHash==plan.TransferHash&&resumed.Steps[0].Part==2&&resumed.Steps.Count==plan.Steps.Count-1,"Confirmed context fragments must not be retransmitted after a known rejection or restart");
  var unicode=new string('u',PromptPlanner.FragmentCharacters-1)+"😀"+new string('v',PromptPlanner.FragmentCharacters);
  var split=PromptPlanner.Split(unicode).ToArray();check(string.Concat(split)==unicode&&split.All(s=>!char.IsHighSurrogate(s[^1])&&!char.IsLowSurrogate(s[0])),"Fragment boundaries must preserve Unicode surrogate pairs");
  var branch=PromptPlanner.Build(Parse(padding,[new{role="user",content="DIFFERENT_BRANCH"}]),saved);
  check(branch.Steps[^1].Text.Contains("AUTHORITATIVE HISTORY")&&branch.Steps[^1].Text.Contains("DIFFERENT_BRANCH"),"A changed history prefix must be supplied as the authoritative current history");
 }
}
