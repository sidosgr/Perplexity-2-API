using System.Windows;
namespace PerplexityLocalBridge;
public partial class MainWindow
{
    private ConversationStore conversations=null!;
    private IReadOnlyList<ConversationSummary> ConversationSummaries(string clientNamespace)
    {
        if(!Dispatcher.CheckAccess())return Dispatcher.Invoke(()=>ConversationSummaries(clientNamespace));
        return conversations.Snapshot.Where(c=>c.ClientNamespace==clientNamespace).Select(c=>new ConversationSummary(c.Key,c.Project,c.ThreadId,c.CompletedTurns,c.Pending,c.UpdatedAt)).ToArray();
    }
    private void RefreshSessions(){if(conversations is not null)SessionsBox.ItemsSource=conversations.Snapshot.Select(s=>new{Project=s.Project,Details="Agent "+s.Key[..10]+" · "+s.CompletedTurns+" turns · "+(s.Pending?"needs recovery":"ready")+"\nAccount: "+(profiles?.Profiles.FirstOrDefault(p=>p.Id==s.ProfileId)?.DisplayName??"removed")+" · "+s.UpdatedAt.ToLocalTime().ToString("g"),Thread="Perplexity thread: "+s.ThreadId}).ToArray();}
    private Task RefreshApiModelsAsync(CancellationToken cancellation)
    {
        if(!Dispatcher.CheckAccess())return Dispatcher.InvokeAsync(()=>RefreshApiModelsAsync(cancellation)).Task.Unwrap();
        return Refresh();
        async Task Refresh()
        {
            while((busy&&!internalTest)||routing)await Task.Delay(100,cancellation);
            var previouslyBusy=busy;busy=true;
            try
            {
                foreach(var profile in profiles.Profiles.Where(p=>p.Enabled))
                {
                    var entry=await EnsureProfileAsync(profile.Id,cancellation);
                    if(!entry.Session.Ready)await VerifyProfileAsync(entry,cancellation);
                    if(!entry.Session.Ready)throw new BridgeException(503,"session_not_ready","Sign in to the account before discovering its models.");
                    // A refresh failure is a connection failure, not proof that models disappeared.
                    await entry.Session.DiscoverModelsAsync(cancellation);
                }
                PublishModels();
                if(viewedProfileId is not null&&profileSessions.TryGetValue(viewedProfileId,out var viewed))ModelPicker.ItemsSource=viewed.Catalog.Snapshot;
            }
            finally{busy=previouslyBusy;}
        }
    }
    private void RefreshSessions_Click(object sender,RoutedEventArgs e)=>RefreshSessions();
}
