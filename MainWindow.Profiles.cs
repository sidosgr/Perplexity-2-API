using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Wpf;
namespace PerplexityLocalBridge;

public partial class MainWindow
{
    private WebView2 Browser=null!;
    private ProfileStore profiles=null!;
    private ProfileRouter profileRouter=null!;
    private readonly Dictionary<string,ProfileSession> profileSessions=[];
    private bool routing,changingProfile,internalTest;
    private string? viewedProfileId;
    private sealed class ProfileSession(string id,WebView2 view,WebSession session,ModelCatalog catalog)
    {
        public string Id{get;}=id;
        public WebView2 View{get;}=view;
        public WebSession Session{get;}=session;
        public ModelCatalog Catalog{get;}=catalog;
        public IReadOnlyList<AccountIdentity> AccountIdentities{get;set;}=[];
        public DateTimeOffset VerifiedAt{get;set;}
        public TaskCompletionSource Loaded{get;}=new(TaskCreationOptions.RunContinuationsAsynchronously);
    }
    private async Task<ProfileSession> EnsureProfileAsync(string id,CancellationToken cancellation,bool waitForPage=true)
    {
        if(profileSessions.TryGetValue(id,out var existing))return existing;
        var view=new WebView2{Visibility=Visibility.Hidden};BrowserHost.Children.Add(view);
        var catalog=new ModelCatalog();var web=new WebSession(view,catalog);var entry=new ProfileSession(id,view,web,catalog);
        profileSessions.Add(id,entry);
        web.ConnectionChanged+=()=>{if(!web.Ready&&viewedProfileId==id){changingProfile=true;OrgBox.ItemsSource=null;changingProfile=false;}PublishModels();UpdateConnectionStatus();};
        web.LoginWindowClosed+=async()=>
        {
            await Task.Delay(750);
            if(closing||web.Ready||!profileSessions.ContainsKey(id))return;
            Log("login","Sign-in window closed. Checking the profile's saved session…");
            view.CoreWebView2.Navigate("https://www.perplexity.ai/");
        };
        web.LoginWindowFailed+=()=>Log("login","The sign-in window could not open. Try email sign-in in the internal browser.");
        view.NavigationCompleted+=async(_,args)=>
        {
            if(args.IsSuccess&&view.Source?.Host=="www.perplexity.ai")entry.Loaded.TrySetResult();
            if(!initialized||closing||busy||routing||Browser!=view)return;
            if(!args.IsSuccess){Log("browser","The page did not load. Use Open Perplexity to retry.");return;}
            if(view.Source?.Host!="www.perplexity.ai")return;
            if(view.Source.AbsolutePath != "/")return;
            await Guard(async()=>{await CheckSession();await ResumeEndpointAsync();});
        };
        try
        {
            await web.InitializeAsync(profiles.BrowserFolder(id));web.SetDarkMode(state.Settings.DarkMode);
            view.CoreWebView2.SourceChanged+=(_,_)=>{if(Browser==view)BrowserLocationText.Text=view.Source?.GetLeftPart(UriPartial.Path)??"Internal browser";};
            if(waitForPage)
            {
                try{await entry.Loaded.Task.WaitAsync(TimeSpan.FromSeconds(30),cancellation);}
                catch(TimeoutException){ /* Keep the browser usable for a manual retry. */ }
            }
            return entry;
        }
        catch{profileSessions.Remove(id);BrowserHost.Children.Remove(view);web.Dispose();throw;}
    }
    private void SelectProfile(ProfileSession entry,bool makeActive=true)
    {
        foreach(var other in profileSessions.Values)other.View.Visibility=other==entry?Visibility.Visible:Visibility.Hidden;
        Browser=entry.View;session=entry.Session;viewedProfileId=entry.Id;if(makeActive)profiles.Activate(entry.Id);PublishModels();
        ModelPicker.ItemsSource=entry.Catalog.Snapshot;
        ModelPicker.SelectedItem=entry.Catalog.Snapshot.FirstOrDefault(m=>m.Id==ModelBox.Text);
        changingProfile=true;
        try{OrgBox.ItemsSource=entry.AccountIdentities;OrgBox.SelectedItem=entry.AccountIdentities.FirstOrDefault(o=>o.Id==session.AccountSessionId);}
        finally{changingProfile=false;}
        BrowserLocationText.Text=Browser.Source?.GetLeftPart(UriPartial.Path)??"Internal browser";
        RefreshProfiles();UpdateConnectionStatus();SetBrowserVisible(ShowBrowserBox.IsChecked==true);
    }
    private async Task ResumeEndpointAsync()
    {
        if(startupTestPending&&session.Ready){startupTestPending=false;await RunQuickTest();}
        else if(session.Ready&&(Environment.GetCommandLineArgs().Contains("--start-endpoint")||state.Settings.AutoStartEndpoint&&state.Settings.EndpointEnabled))await StartEndpoint();
    }
    private Task<Completion> CompleteProfilesAsync(string prompt,string model,int seconds,ThinkingOptions thinking,CancellationToken cancellation)
        =>CompleteProfilesCoreAsync(prompt,model,seconds,thinking,cancellation,null);
    private Task<Completion> CompleteConversationAsync(ConversationCall call,CancellationToken cancellation)
        =>CompleteProfilesCoreAsync(call.Prompt,call.Model,call.TimeoutSeconds,call.Thinking,cancellation,call);
    private Task<Completion> CompleteProfilesCoreAsync(string prompt,string model,int seconds,ThinkingOptions thinking,CancellationToken cancellation,ConversationCall? call)
    {
        if(!Dispatcher.CheckAccess())return Dispatcher.InvokeAsync(()=>CompleteProfilesCoreAsync(prompt,model,seconds,thinking,cancellation,call)).Task.Unwrap();
        return RouteOnUiAsync();
        async Task<Completion> RouteOnUiAsync()
        {
            while(busy&&!internalTest)await Task.Delay(100,cancellation);
            routing=true;
            var saved=call?.Persistent==true?conversations.Find(call.Key):null;
            try
            {
                if(saved is not null)
                {
                    if(!profiles.Profiles.Any(p=>p.Id==saved.ProfileId))throw new BridgeException(409,"session_account_removed","This session belongs to a removed account. Start a new agent session to use another account.");
                    if(saved.Pending)throw new BridgeException(409,"session_needs_recovery","The previous turn did not finish safely. Its thread mapping was retained; start a new agent session rather than silently duplicating the conversation.");
                    if(saved.RejectedRequestHash==call!.RequestHash)throw new BridgeException(saved.RejectedStatus,saved.RejectedCode??"upstream_prompt_truncated",saved.RejectedMessage??"Perplexity did not confirm receipt of the complete input. The generated reply was not forwarded or retransmitted.");
                    if(call!.Repair)
                    {
                        saved=conversations.BeginRepair(saved,call.RequestHash);
                        Log("response_repair","Correcting one completed invalid response in its original thread; no local tool call has been forwarded.");
                    }
                    if(!call.Repair&&saved.RequestHash==call.RequestHash)
                    {
                        if(saved.Cached is null)throw new BridgeException(409,"reply_not_cached","This request already completed but its reply was too large to cache. Do not resend the same turn.");
                        Log("session_cached","Returning the completed reply for session "+call.Key[..10]+" without another Perplexity turn.");
                        return saved.Cached;
                    }
                    var pinned=profiles.Profiles.First(p=>p.Id==saved.ProfileId);
                    if(!pinned.Enabled)throw new BridgeException(409,"session_account_disabled","The account for this session is disabled. Enable it to continue the same thread.");
                    if(pinned.LimitedUntil>DateTimeOffset.UtcNow)throw new BridgeException(429,"session_account_limited","This session's account is paused. Its thread remains pinned to that account.",pinned.LimitedUntil);
                    if(!await Prepare(pinned,cancellation))throw new BridgeException(503,"session_account_not_ready","Sign in to this session's original account to continue its Perplexity thread.");
                    if(saved.AccountIdentity!=profileSessions[pinned.Id].Session.AccountSessionId)throw new BridgeException(409,"session_account_changed","This profile is signed in to a different Perplexity account. Restore the original sign-in to continue this thread.");
                    try{return await Complete(pinned,cancellation);}
                    catch(BridgeException ex)when(ex.Code is "perplexity_limit" or "perplexity_rate_limit")
                    {
                        var temporary=ex.Code=="perplexity_rate_limit";
                        profiles.Limit(pinned.Id,ex.RetryAt??DateTimeOffset.UtcNow.AddMinutes(temporary?1:state.Settings.ProfileCooldownMinutes),temporary?"rate":"usage");
                        throw;
                    }
                }
                return await profileRouter.CompleteAsync(state.Settings.AutomaticProfileFailover,state.Settings.ProfileCooldownMinutes,
                    Prepare,Complete,p=>Log("profile_limit","Perplexity reported a limit. The local retry pause was saved; trying the next eligible account for this new conversation."),cancellation);
            }
            finally{routing=false;RefreshProfiles();RefreshSessions();UpdateConnectionStatus();}
            async Task<bool> Prepare(AccountProfile profile,CancellationToken token)
            {
                try
                {
                    var entry=await EnsureProfileAsync(profile.Id,token);
                    if(!entry.Session.Ready||DateTimeOffset.UtcNow-entry.VerifiedAt>TimeSpan.FromSeconds(30))await VerifyProfileAsync(entry,token);
                    if(!entry.Session.Ready)return false;
                    profiles.SetStatus(profile.Id,"Signed in");return true;
                }
                catch(BridgeException ex){profiles.SetStatus(profile.Id,"Check failed: "+ex.Code);Log("profile_skip","Account check failed: "+ex.Code);return false;}
                catch(TimeoutException){profiles.SetStatus(profile.Id,"Connection timed out");return false;}
            }
            async Task<Completion> Complete(AccountProfile profile,CancellationToken token)
            {
                var progress=saved??new ConversationState(call?.Key??Guid.NewGuid().ToString("N"),call?.ClientNamespace??"ephemeral",profile.Id,call?.Project??"Local client",Guid.NewGuid().ToString(),AccountIdentity:profileSessions[profile.Id].Session.AccountSessionId);
                var plan=call?.Request is BridgeRequest request?PromptPlanner.Build(request,progress):new PromptPlan([new(prompt,true)],"",[]);
                if(plan.TransferParts>0)Log("context_transfer","Large caller context uses "+plan.TransferParts+" fragments; "+plan.Steps.Count(s=>!s.Execute)+" remain to transfer in this same thread. Progress is saved.");
                Completion? final=null;
                foreach(var step in plan.Steps)
                {
                    var before=progress;
                    if(call?.Persistent==true)conversations.Put(progress with{Pending=true});
                    try
                    {
                        Log(step.Execute?"profile_request":"context_transfer",step.Execute?"Executing the caller turn in its linked Perplexity thread ("+step.Text.Length+" characters).":"Transferring context fragment "+step.Part+"/"+plan.TransferParts+" ("+step.Text.Length+" characters).");
                        var result=await profileSessions[profile.Id].Session.CompleteInThreadAsync(step.Text,model,seconds,thinking,progress.Cursor,progress.FrontendContextId,token);
                        result=result with{ProfileId=profile.Id};
                        if(result.Cursor is null)throw new BridgeException(502,"session_cursor_missing","Perplexity returned a reply without complete continuation data. The mapping was retained for recovery.");
                        progress=progress with{Cursor=result.Cursor,PublicThreadId=result.Cursor.ContextId,Pending=false,CompletedTurns=progress.CompletedTurns+1};
                        if(step.Execute)
                        {
                            final=result;
                            progress=progress with{ContextHash=plan.ContextHash,HistoryHashes=plan.HistoryHashes,StagedInputHash=null,StagedInputPart=0,RejectedRequestHash=null,RejectedCode=null,RejectedMessage=null,RequestHash=call?.RequestHash,Cached=result.Text.Length<1024*1024?result:null};
                        }
                        else progress=progress with{StagedInputHash=plan.TransferHash,StagedInputPart=step.Part};
                        if(call?.Persistent==true)conversations.Put(progress);
                    }
                    catch(BridgeException ex)when(ex.Code is "upstream_prompt_truncated" or "upstream_input_unverified"&&ex.Cursor is not null)
                    {
                        progress=before with{Cursor=ex.Cursor,PublicThreadId=ex.Cursor.ContextId,Pending=false,CompletedTurns=before.CompletedTurns+1,RejectedRequestHash=call?.RequestHash,RejectedStatus=ex.Status,RejectedCode=ex.Code,RejectedMessage=ex.Message};
                        if(call?.Persistent==true)conversations.Put(progress);
                        throw;
                    }
                    catch(BridgeException ex)when(ex.Code is "perplexity_limit" or "perplexity_rate_limit" or "profile_model_unavailable" or "session_expired" or "wrong_page" or "web_request_rejected" or "perplexity_web_forbidden" or "browser_verification" or "prompt_too_large")
                    {
                        if(call?.Persistent==true)
                        {
                            if(before.Cursor is null&&saved is null)conversations.Restore(call.Key,null);else conversations.Put(before);
                        }
                        if(before.Cursor is not null&&ex.Code is "perplexity_limit" or "perplexity_rate_limit")
                        {
                            var temporary=ex.Code=="perplexity_rate_limit";var until=ex.RetryAt??DateTimeOffset.UtcNow.AddMinutes(temporary?1:state.Settings.ProfileCooldownMinutes);
                            profiles.Limit(profile.Id,until,temporary?"rate":"usage");
                            throw new BridgeException(429,"session_account_limited","This thread's account reached a limit during the request. Its context progress was retained; it was not moved to another account.",until);
                        }
                        if(before.Cursor is not null&&ex.Code=="profile_model_unavailable")throw new BridgeException(404,"session_model_unavailable","This model is unavailable for the thread's original account. Select an available model to continue the same thread.");
                        throw;
                    }
                }
                profiles.Reset(profile.Id);profiles.SetStatus(profile.Id,"Last API request succeeded");
                Log("profile_success","API request completed with intact input."+(profileSessions[profile.Id].Session.ReportedPlanTier is string tier?" Provider reported plan tier: "+tier+".":""));
                return final??throw new BridgeException(502,"execution_missing","Context transferred, but the execution request did not complete.");
            }

        }
    }
    private void RefreshProfiles()
    {
        if(profiles is null)return;
        var selected=viewedProfileId??profiles.ActiveId;
        changingProfile=true;
        try
        {
            var items=profiles.Profiles.Select(p=>p with{IsApiActive=p.Id==profiles.ActiveId}).ToArray();
            ProfilesBox.ItemsSource=items;
            ProfilesBox.SelectedItem=items.FirstOrDefault(p=>p.Id==selected)??items.First(p=>p.Id==profiles.ActiveId);
            ProfileStatusText.Text=$"{profiles.Profiles.Count} saved accounts · API account: "+profiles.Profiles.First(p=>p.Id==profiles.ActiveId).DisplayName;
            HeaderProfileText.Text="Profile · "+profiles.Profiles.First(p=>p.Id==profiles.ActiveId).DisplayName;
            ProfileList_Changed(this,new SelectionChangedEventArgs(SelectorEvent,Array.Empty<object>(),Array.Empty<object>()));
        }
        finally{changingProfile=false;}
    }
    private static readonly RoutedEvent SelectorEvent=System.Windows.Controls.Primitives.Selector.SelectionChangedEvent;
    private async void ProfileList_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(!changingProfile&&initialized&&ProfilesBox.SelectedItem is AccountProfile selected&&selected.Id!=viewedProfileId)
        {
            if(busy||routing){RefreshProfiles();return;}
            changingProfile=true;OrgBox.ItemsSource=null;changingProfile=false;
            SelectedAccountText.Text="Opening selected account…";
            await Guard(async()=>{SelectProfile(await EnsureProfileAsync(selected.Id,CancellationToken.None),false);});
            if(viewedProfileId!=selected.Id)RefreshProfiles();return;
        }
        RemoveProfileButton.IsEnabled=ProfilesBox.SelectedItem is AccountProfile;
        if(ProfilesBox.SelectedItem is not AccountProfile profile)return;
        var before=changingProfile;changingProfile=true;
        try
        {
            ProfileEnabledBox.IsChecked=profile.Enabled;
            var duplicate=profile.AccountId is not null&&profiles.Profiles.Any(p=>p.Id!=profile.Id&&p.AccountId==profile.AccountId);
            SelectedAccountText.Text="Viewing: "+profile.DisplayName+" · "+profile.Status+(duplicate?"\nThis account identity is also saved under another account profile.":"");
            ProfileNameHint.Visibility=profile.NeedsName?Visibility.Visible:Visibility.Collapsed;
        }
        finally{changingProfile=before;}
    }
    private async void AddProfile_Click(object sender,RoutedEventArgs e)=>await Guard(async()=>
    {
        var name=ProfileDialogs.Name(this,"Add profile","",value=>profiles.CheckName(value));
        if(name is null)return;
        var profile=profiles.Add(name);RevealInternalBrowser();
        var entry=await EnsureProfileAsync(profile.Id,CancellationToken.None);SelectProfile(entry);await CheckSession();
    });
    private async void RenameProfile_Click(object sender,RoutedEventArgs e)=>await Guard(()=>
    {
        if(ProfilesBox.SelectedItem is not AccountProfile profile)throw new ArgumentException("Select a profile.");
        var name=ProfileDialogs.Name(this,"Rename profile",profile.NeedsName?"":profile.Name,value=>profiles.CheckName(value,profile.Id));
        if(name is null)return Task.CompletedTask;
        profiles.Rename(profile.Id,name);RefreshProfiles();SavedFeedback("Profile renamed");return Task.CompletedTask;
    });
    private async Task UseSavedProfileAsync(string id)
    {
        var entry=await EnsureProfileAsync(id,CancellationToken.None);SelectProfile(entry);
        try{await CheckSession();}finally{if(!session.Ready)RevealInternalBrowser();}
        await ResumeEndpointAsync();
    }
    private async Task EnsureActiveBrowserAsync()
    {
        if(session is not null&&viewedProfileId is not null&&profileSessions.ContainsKey(viewedProfileId))return;
        SelectProfile(await EnsureProfileAsync(profiles.ActiveId,CancellationToken.None),false);
    }
    private async void UseProfile_Click(object sender,RoutedEventArgs e)=>await Guard(async()=>
    {
        if(ProfilesBox.SelectedItem is not AccountProfile profile)throw new ArgumentException("Select a profile.");
        await UseSavedProfileAsync(profile.Id);
    });
    private async void EnableProfile_Click(object sender,RoutedEventArgs e)=>await Guard(()=>
    {
        if(ProfilesBox.SelectedItem is AccountProfile profile)profiles.Enable(profile.Id,ProfileEnabledBox.IsChecked==true);
        RefreshProfiles();return Task.CompletedTask;
    });
    private async void ResetProfile_Click(object sender,RoutedEventArgs e)=>await Guard(()=>
    {
        if(ProfilesBox.SelectedItem is AccountProfile profile)profiles.Reset(profile.Id);
        RefreshProfiles();return Task.CompletedTask;
    });
    private void MoveProfile(int offset)
    {
        if(busy||routing)return;
        try{if(ProfilesBox.SelectedItem is AccountProfile profile)profiles.Move(profile.Id,offset);RefreshProfiles();}
        catch(Exception){Log("profiles","The profile order could not be saved.");}
    }
    private void MoveProfileUp_Click(object sender,RoutedEventArgs e)=>MoveProfile(-1);
    private void MoveProfileDown_Click(object sender,RoutedEventArgs e)=>MoveProfile(1);
    private void PublishModels()
    {
        models.Replace(profileSessions.Values.Where(e=>e.Session.Ready&&profiles.Profiles.Any(p=>p.Id==e.Id&&p.Enabled)).SelectMany(e=>e.Catalog.Snapshot).DistinctBy(m=>m.Id));
    }
    private async Task VerifyProfileAsync(ProfileSession entry,CancellationToken token)
    {
        var saved=profiles.Profiles.First(p=>p.Id==entry.Id).AccountId;
        var previous=entry.Session.AccountSessionId;var catalog=entry.Catalog.Snapshot.ToArray();
        entry.AccountIdentities=[];
        if(viewedProfileId==entry.Id){changingProfile=true;OrgBox.ItemsSource=null;changingProfile=false;}
        try
        {
            entry.AccountIdentities=await entry.Session.CheckAsync(token);
            entry.Session.AccountSessionId=(entry.AccountIdentities.FirstOrDefault(o=>o.Id==saved)??entry.AccountIdentities.FirstOrDefault())?.Id;
            profiles.SetAccount(entry.Id,entry.Session.AccountSessionId);
            profiles.SetStatus(entry.Id,entry.Session.Ready?"Signed in":"Sign-in required");
            entry.VerifiedAt=DateTimeOffset.UtcNow;
            if(previous==entry.Session.AccountSessionId)entry.Catalog.Replace(catalog);
        }
        catch
        {
            entry.Session.AccountSessionId=null;entry.AccountIdentities=[];entry.VerifiedAt=default;
            profiles.SetStatus(entry.Id,"Sign-in / connection check required");throw;
        }
        finally
        {
            if(viewedProfileId==entry.Id)
            {
                changingProfile=true;OrgBox.ItemsSource=entry.AccountIdentities;
                OrgBox.SelectedItem=entry.AccountIdentities.FirstOrDefault(o=>o.Id==entry.Session.AccountSessionId);changingProfile=false;
            }
            PublishModels();RefreshProfiles();UpdateConnectionStatus();
        }
    }
    private async void CheckAllProfiles_Click(object sender,RoutedEventArgs e)=>await Guard(CheckAllProfilesAsync);
    private async Task CheckAllProfilesAsync()
    {
        foreach(var profile in profiles.Profiles)
        {
            try{await VerifyProfileAsync(await EnsureProfileAsync(profile.Id,CancellationToken.None),CancellationToken.None);}
            catch(BridgeException ex){Log("profile_check","Account check: "+ex.Code+". Select the account to sign in again.");}
            catch(TimeoutException){profiles.SetStatus(profile.Id,"Connection timed out");}
        }
        RefreshProfiles();Log("profiles","Finished checking all saved accounts. Each account identity belongs to its selected login.");
    }
    private void RevealInternalBrowser()
    {
        ShowBrowserBox.IsChecked=true;state.Update(state.Settings with{ShowBrowser=true});SetBrowserVisible(true);
    }
}
