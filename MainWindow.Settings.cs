using System.Windows;
using System.Windows.Controls;
using System.Windows.Media;
namespace PerplexityLocalBridge;

public partial class MainWindow
{
    private void InitializeSettingsFeedback()
    {
        foreach(var field in new[]{PortBox,TimeoutBox,ModelBox,ProfileCooldownBox})field.TextChanged+=(_,_)=>MarkSettingsDirty();
        foreach(var box in new[]{OpenAiBox,RequireKeyBox,ToolCallingBox,ProfileFailoverBox})box.Click+=(_,_)=>MarkSettingsDirty();
    }
    private void MarkSettingsDirty()
    {
        if(!initialized||busy||changingProfile)return;
        SaveFeedbackText.Text="Unsaved changes — click Save all settings.";SaveFeedbackText.Foreground=Brushes.DarkOrange;
    }
    private void SavedFeedback(string message="All settings saved")
    {
        SaveFeedbackText.Text="✓ "+message+" at "+DateTime.Now.ToString("HH:mm:ss");SaveFeedbackText.Foreground=Brushes.MediumSeaGreen;
    }
    private void PersistAllSettings()
    {
        var selected=ProfilesBox.SelectedItem as AccountProfile;
        SettingsCommit.Save(state,profiles,ReadSettings(),selected?.Id,selected is {NeedsName:false}?selected.Name:null,ProfileEnabledBox.IsChecked==true);
    }
    private async void Save_Click(object sender,RoutedEventArgs e)=>await Guard(async()=>
    {
        SaveAllButton.IsEnabled=false;SaveAllButton.Content="Saving…";SaveFeedbackText.Text="Saving all settings…";
        try
        {
            PersistAllSettings();
            Theme.Apply(this,state.Settings.DarkMode);
            foreach(var entry in profileSessions.Values)entry.Session.SetDarkMode(state.Settings.DarkMode);
            SetBrowserVisible(state.Settings.ShowBrowser);RefreshProfiles();RefreshQuickMenus();
            SavedFeedback();Log("settings","All settings saved successfully.");
            if(server.Running)
            {
                try{await server.DisposeAsync();await server.StartAsync();}
                catch(Exception){SaveFeedbackText.Text+=" · API restart failed";SaveFeedbackText.Foreground=Brushes.DarkOrange;Log("endpoint","Settings were saved, but the endpoint could not restart. Check the port and try Connect.");}
            }
            UpdateConnectionStatus();
        }
        catch(Exception)
        {
            SaveFeedbackText.Text="Not saved — see the validation message below.";SaveFeedbackText.Foreground=Brushes.IndianRed;throw;
        }
        finally{SaveAllButton.IsEnabled=true;SaveAllButton.Content="Save all settings";}
    });
}
