using PerplexityLocalBridge;
internal static class ProfileTests
{
    public static async Task Run(Action<bool,string> check)
    {
        var root=Path.Combine(Path.GetTempPath(),"BridgeProfiles-"+Guid.NewGuid().ToString("N"));var path=Path.Combine(root,"profiles.json");
        var store=new ProfileStore(path);
        check(store.ActiveId=="default"&&store.Profiles.Count==1,"Existing login must map to Default");
        check(store.Profiles[0].NeedsName&&store.Profiles[0].DisplayName=="Name this profile","First profile must invite a custom name");
        check(store.BrowserFolder("default")==Path.Combine(root,"BrowserProfile"),"Default browser data must not move");
        var work=store.Add("Work");var backup=store.Add("Backup");
        check(store.BrowserFolder(work.Id)!=store.BrowserFolder(backup.Id)&&store.BrowserFolder(work.Id).StartsWith(Path.Combine(root,"Profiles")),"Named sessions must have isolated browser folders");
        store.Rename(work.Id,"Personal account");store.Activate(work.Id);
        var restored=new ProfileStore(path);
        check(restored.ActiveId==work.Id&&restored.Profiles[1].Name=="Personal account","Names and active profile must persist");
        try{store.Add("PERSONAL ACCOUNT");throw new Exception("Duplicate profile name accepted");}catch(ArgumentException){check(true,"Duplicate names rejected");}
        try{store.BrowserFolder("../escape");throw new Exception("Unsafe profile ID accepted");}catch(ArgumentException){check(true,"Unsafe profile ID rejected");}
        store.Move(backup.Id,-2);check(new ProfileStore(path).Profiles[0].Id==backup.Id,"Profile order must persist");
        store.Move(backup.Id,2);store.Activate("default");
        var router=new ProfileRouter(store);var calls=new List<string>();
        var answer=await router.CompleteAsync(true,60,(_,_)=>Task.FromResult(true),(profile,_)=>
        {
            calls.Add(profile.Id);
            if(profile.Id=="default")throw new BridgeException(429,"perplexity_limit","limited");
            return Task.FromResult(new Completion("OK","perplexity-sonnet-5-5"));
        },null,CancellationToken.None);
        check(answer.Text=="OK"&&calls.SequenceEqual(new[]{"default",work.Id}),"A quota response must try the next profile once");
        check(store.ActiveId==work.Id&&new ProfileStore(path).ActiveId==work.Id,"Successful failover must remain active across restart");
        check(new ProfileStore(path).Profiles.First(p=>p.Id=="default").LimitedUntil>DateTimeOffset.UtcNow,"Quota cooldown must persist");
        calls.Clear();
        await router.CompleteAsync(true,60,(_,_)=>Task.FromResult(true),(profile,_)=>{calls.Add(profile.Id);return Task.FromResult(new Completion("OK","test"));},null,CancellationToken.None);
        check(calls.SequenceEqual(new[]{work.Id}),"Healthy active profile should be reused");
        calls.Clear();
        try{await router.CompleteAsync(true,60,(_,_)=>Task.FromResult(true),(profile,_)=>{calls.Add(profile.Id);throw new BridgeException(502,"bad_response","bad");},null,CancellationToken.None);throw new Exception("Expected upstream error");}
        catch(BridgeException ex){check(ex.Status==502&&calls.Count==1,"Non-quota generation errors must not rotate");}
        calls.Clear();
        try{await router.CompleteAsync(true,60,(_,_)=>Task.FromResult(true),(profile,_)=>{calls.Add(profile.Id);throw new BridgeException(503,"session_expired","expired");},null,CancellationToken.None);throw new Exception("Expected auth error");}
        catch(BridgeException ex){check(ex.Code=="session_expired"&&calls.Count==1,"Authentication errors must not rotate");}
        store.Enable(backup.Id,false);calls.Clear();
        try{await router.CompleteAsync(true,60,(_,_)=>Task.FromResult(true),(profile,_)=>{calls.Add(profile.Id);throw new BridgeException(429,"perplexity_limit","limit");},null,CancellationToken.None);throw new Exception("Expected exhaustion");}
        catch(BridgeException ex){check(ex.Code=="profiles_unavailable"&&calls.SequenceEqual(new[]{work.Id}),"Disabled and cooling profiles must be skipped, without loops");}
        store.Reset("default");store.Reset(work.Id);store.Activate("default");calls.Clear();
        try{await router.CompleteAsync(false,60,(_,_)=>Task.FromResult(true),(profile,_)=>{calls.Add(profile.Id);throw new BridgeException(429,"perplexity_limit","limit");},null,CancellationToken.None);throw new Exception("Expected single limit");}
        catch(BridgeException ex){check(ex.Code=="perplexity_limit"&&calls.SequenceEqual(new[]{"default"}),"Failover-off must keep the original quota response");}
        store.Reset("default");store.Reset(work.Id);store.Enable(backup.Id,true);store.Activate("default");calls.Clear();
        await router.CompleteAsync(true,60,(profile,_)=>Task.FromResult(profile.Id!=work.Id),(profile,_)=>
        {calls.Add(profile.Id);if(profile.Id=="default")throw new BridgeException(429,"perplexity_limit","limit");return Task.FromResult(new Completion("OK","test"));},null,CancellationToken.None);
        check(calls.SequenceEqual(new[]{"default",backup.Id}),"A signed-out fallback should be skipped");
        store.Reset("default");store.Activate("default");
        using var cancel=new CancellationTokenSource();cancel.Cancel();
        try{await router.CompleteAsync(true,60,(_,_)=>Task.FromResult(true),(_,_)=>throw new Exception("Cancelled request sent"),null,cancel.Token);throw new Exception("Expected cancellation");}
        catch(OperationCanceledException){check(true,"Cancelled request must not be sent");}
        check(!File.ReadAllText(path).Contains("sessionKey"),"Profile metadata must not store cookies");
        var statePath=Path.Combine(root,"settings.json");var state=new BridgeState(statePath);
        check(state.Settings.ProfileCooldownMinutes==300,"New settings must default to five hours");
        SettingsCommit.Save(state,store,state.Settings with{Port=8765,ThinkingLevel="off",ProfileCooldownMinutes=300},"default","Primary personal account",true);
        check(new BridgeState(statePath).Settings.Port==8765&&new ProfileStore(path).Profiles.First(p=>p.Id=="default").Name=="Primary personal account","Global save must persist settings and the first profile's custom name");
        check(!new ProfileStore(path).Profiles.First(p=>p.Id=="default").NeedsName,"Naming the first profile must clear its naming prompt");
        var hash=File.ReadAllText(statePath);
        try{SettingsCommit.Save(state,store,state.Settings with{Port=8766},"default","Personal account",true);throw new Exception("Duplicate profile saved");}catch(ArgumentException){check(File.ReadAllText(statePath)==hash,"Invalid profile edits must not partially save endpoint changes");}
        try{SettingsCommit.Save(state,store,state.Settings with{Port=8766},"default","",true);throw new Exception("Empty name saved");}catch(ArgumentException){check(File.ReadAllText(statePath)==hash,"Clearing a named profile must fail without saving other settings");}
        File.WriteAllText(statePath,System.Text.Json.JsonSerializer.Serialize(new{Settings=new BridgeSettings(ProfileCooldownMinutes:60),Keys=Array.Empty<LocalKey>()}));
        check(new BridgeState(statePath).Settings.ProfileCooldownMinutes==300,"Old 60-minute default must migrate to five hours");
        var named=new BridgeState(statePath);named.Update(named.Settings with{ProfileCooldownMinutes=90});
        check(new BridgeState(statePath).Settings.ProfileCooldownMinutes==90,"An explicit custom pause must survive reload");
        var legacyPath=Path.Combine(root,"legacy-profiles.json");
        File.WriteAllText(legacyPath,System.Text.Json.JsonSerializer.Serialize(new{ActiveId="default",Profiles=new[]{new AccountProfile("default","Default"),new AccountProfile(work.Id,"Existing custom name")}}));
        var migrated=new ProfileStore(legacyPath);
        check(migrated.Profiles[0].NeedsName&&migrated.Profiles[0].DisplayName=="Name this profile","Old autogenerated Default label must become a naming prompt");
        check(migrated.Profiles[1].Name=="Existing custom name"&&migrated.BrowserFolder("default")==Path.Combine(root,"BrowserProfile"),"Custom names and original login folder must survive migration");
        migrated.Rename("default","Default");
        check(new ProfileStore(legacyPath).Profiles[0].Name=="Default"&&!new ProfileStore(legacyPath).Profiles[0].NeedsName,"An explicitly chosen profile name must remain user-controlled after migration");
        var removedStore=new ProfileStore(Path.Combine(root,"remove-profiles.json"));
        var nextProfile=removedStore.Add("Next");var disabled=removedStore.Add("Disabled");removedStore.Enable(disabled.Id,false);
        removedStore.Remove(disabled.Id);
        check(removedStore.ActiveId=="default"&&removedStore.Profiles.Count==2,"Removing an inactive profile must preserve the active one");
        var nextActive=removedStore.Remove("default");
        check(nextActive.Id==nextProfile.Id&&new ProfileStore(Path.Combine(root,"remove-profiles.json")).ActiveId==nextProfile.Id,"Removing the active profile must select and persist a remaining profile");
        var empty=removedStore.Remove(nextProfile.Id);
        check(empty.NeedsName&&empty.Id!=nextProfile.Id&&empty.Id!="default"&&removedStore.Profiles.Count==1,"Removing the last profile must create a fresh unnamed session");
        check(removedStore.BrowserFolder(empty.Id)!=Path.Combine(root,"BrowserProfile"),"Fresh session must not reuse the removed login folder");
        try{removedStore.Remove(nextProfile.Id);throw new Exception("Removed profile found");}catch(ArgumentException){check(removedStore.Profiles.Count==1,"Duplicate removal must not change the profile store");}

        var routingStore=new ProfileStore(Path.Combine(root,"routing-v3.json"));var fallback=routingStore.Add("Second account");
        routingStore.SetAccount("default","11111111-1111-1111-1111-111111111111");
        routingStore.SetAccount(fallback.Id,"22222222-2222-2222-2222-222222222222");
        check(new ProfileStore(Path.Combine(root,"routing-v3.json")).Profiles.Select(p=>p.AccountId).Distinct().Count()==2,"Account identity preferences must persist separately for each login");
        var routing=new ProfileRouter(routingStore);calls.Clear();
        await routing.CompleteAsync(true,300,(_,_)=>Task.FromResult(true),(p,_)=>{calls.Add(p.Id);if(p.Id=="default")throw new BridgeException(429,"perplexity_rate_limit","temporary");return Task.FromResult(new Completion("OK","test"));},null,CancellationToken.None);
        var paused=routingStore.Profiles[0];
        check(calls.Count==2&&paused.PauseKind=="rate"&&paused.LimitedUntil<DateTimeOffset.UtcNow.AddMinutes(2),"Unspecified rate limits must fail over with a one-minute pause, not five hours");
        routingStore.Reset("default");routingStore.Activate("default");
        await routing.CompleteAsync(true,300,(_,_)=>Task.FromResult(true),(p,_)=>{if(p.Id=="default")throw new BridgeException(429,"perplexity_limit","quota");return Task.FromResult(new Completion("OK","test"));},null,CancellationToken.None);
        check(routingStore.Profiles[0].PauseKind=="usage"&&routingStore.Profiles[0].LimitedUntil>DateTimeOffset.UtcNow.AddMinutes(299),"Confirmed usage limits must retain the configured five-hour pause");
        routingStore.Reset("default");routingStore.Activate("default");
        var providerRetry=DateTimeOffset.UtcNow.AddMinutes(7);
        await routing.CompleteAsync(true,300,(_,_)=>Task.FromResult(true),(p,_)=>{if(p.Id=="default")throw new BridgeException(429,"perplexity_rate_limit","temporary",providerRetry);return Task.FromResult(new Completion("OK","test"));},null,CancellationToken.None);
        check(routingStore.Profiles[0].LimitedUntil==providerRetry,"Provider Retry-After must take precedence over local rate-limit defaults");
        var oldPath=Path.Combine(root,"old-pauses.json");
        File.WriteAllText(oldPath,System.Text.Json.JsonSerializer.Serialize(new{Version=2,ActiveId="default",Profiles=new[]{new AccountProfile("default","Keep me",LimitedUntil:DateTimeOffset.UtcNow.AddHours(5)),new AccountProfile(fallback.Id,"Keep me too")}}));
        var old=new ProfileStore(oldPath);
        check(old.RecheckedLegacyPauses&&old.Profiles.Count==2&&old.Profiles[0].LimitedUntil is null,"Legacy unclassified pauses must be rechecked once without removing accounts");
        old.Limit("default",DateTimeOffset.UtcNow.AddHours(5));
        check(!new ProfileStore(oldPath).RecheckedLegacyPauses&&new ProfileStore(oldPath).Profiles[0].LimitedUntil is not null,"New classified pauses must survive subsequent restarts");
        routingStore.Reset("default");routingStore.Activate("default");
        try{await routing.CompleteAsync(true,300,(_,_)=>Task.FromResult(false),(_,_)=>throw new Exception("Unverified request"),null,CancellationToken.None);throw new Exception("Expected readiness error");}
        catch(BridgeException ex){check(ex.Status==503&&ex.Code=="profiles_not_ready","Failed session checks must be distinguished from exhausted quota");}
    }
}
