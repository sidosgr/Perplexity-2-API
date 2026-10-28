using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
namespace PerplexityLocalBridge;

public partial class MainWindow
{
    private void ProfileList_RightClick(object sender,MouseButtonEventArgs e)
    {
        var item=ItemsControl.ContainerFromElement(ProfilesBox,e.OriginalSource as DependencyObject) as ListBoxItem;
        if(item is not null)item.IsSelected=true;else ProfilesBox.SelectedIndex=-1;
    }
    private void ProfileMenu_Opened(object sender,RoutedEventArgs e)
    {
        if(sender is ContextMenu menu)
            foreach(var item in menu.Items.OfType<MenuItem>())item.IsEnabled=ProfilesBox.SelectedItem is AccountProfile&&!busy&&!routing;
    }
    private async void RemoveProfile_Click(object sender,RoutedEventArgs e)=>await Guard(async()=>
    {
        if(ProfilesBox.SelectedItem is not AccountProfile profile)throw new ArgumentException("Select a profile.");
        if(!ProfileDialogs.ConfirmRemoval(this,profile,profiles.Profiles.Count==1))return;
        var active=profiles.ActiveId==profile.Id;var resume=active&&server.Running;
        if(active)await server.DisposeAsync();
        var entry=await EnsureProfileAsync(profile.Id,CancellationToken.None,false);
        await entry.Session.ClearSavedLoginAsync();
        AccountProfile next;
        try{next=profiles.Remove(profile.Id);}
        catch(Exception)
        {
            RefreshProfiles();UpdateConnectionStatus();
            Log("profiles","The saved login was cleared, but the profile list could not be updated. Retry removal or check the profile metadata file permissions.");return;
        }
        conversations.ForgetAccount(profile.Id);RefreshSessions();
        profileSessions.Remove(profile.Id);BrowserHost.Children.Remove(entry.View);entry.Session.Dispose();
        if(active||viewedProfileId==profile.Id)
        {
            models.Clear();session=null!;Browser=null!;
            var replacement=await EnsureProfileAsync(next.Id,CancellationToken.None);SelectProfile(replacement);
            try{await CheckSession();if(resume&&session.Ready)await StartEndpoint();}
            catch(BridgeException){Log("profiles","Profile removed. Sign in to the selected profile to connect Perplexity.");}
        }
        RefreshProfiles();RefreshQuickMenus();UpdateConnectionStatus();SavedFeedback("Profile removed");
        Log("profiles","Profile removed and its saved browser login cleared. Other profiles were kept.");
    });
}
