using System.Windows;
using System.Windows.Media;
using System.Windows.Controls;
using Forms = System.Windows.Forms;
using Wpf = System.Windows.Controls;
namespace PerplexityLocalBridge;

public partial class MainWindow
{
    private Forms.NotifyIcon? tray;
    private static readonly WebModel[] ThinkingLevels=[new("off","Website default")];
    private sealed record QuickCommand(string Label,Func<Task>? Run=null,bool? Checked=null,IReadOnlyList<QuickCommand>? Children=null);
    private void InitializeTray()
    {
        tray=new Forms.NotifyIcon { Text="Local Perplexity Bridge · disconnected", Icon=System.Drawing.Icon.ExtractAssociatedIcon(Environment.ProcessPath!), Visible=true };
        var menu=new Forms.ContextMenuStrip();
        menu.Opening+=(_,_)=>
        {
            foreach(Forms.ToolStripItem item in menu.Items.Cast<Forms.ToolStripItem>().ToArray())item.Dispose();menu.Items.Clear();
            foreach(var command in QuickCommands())menu.Items.Add(TrayItem(command));
            RefreshQuickMenus();
        };
        tray.ContextMenuStrip=menu;
        tray.DoubleClick+=(_,_)=>Dispatcher.InvokeAsync(ShowApp);
        ContextMenu=new Wpf.ContextMenu();
        ContextMenu.Opened+=(_,_)=>{ContextMenu.Items.Clear();foreach(var command in QuickCommands())ContextMenu.Items.Add(WindowItem(command));};
        RefreshQuickMenus();
    }
    private IReadOnlyList<QuickCommand> QuickCommands()
    {
        Task Immediate(Action action){action();return Task.CompletedTask;}
        QuickCommand Toggle(string label,Func<BridgeSettings,bool> read,Func<BridgeSettings,bool,BridgeSettings> set)=>new(label,()=>Guard(()=>ApplyQuickSettingsAsync(s=>set(s,!read(s)))),read(state.Settings));
        return [
            new("Connect Perplexity + start API",()=>Guard(ConnectAsync)),new("Stop local API",StopEndpointAsync),new("Copy API base URL",()=>Immediate(CopyEndpoint)),
            new("Show Local Perplexity Bridge",()=>Immediate(ShowApp)),new("Check Perplexity session",()=>Guard(CheckSession)),new("Refresh available models",()=>Guard(RefreshModels)),
            new("Profiles",Children:profiles.Profiles.Select(p=>new QuickCommand(p.DisplayName,()=>Guard(()=>UseSavedProfileAsync(p.Id)),p.Id==profiles.ActiveId)).ToArray()),
            new("Default model",Children:models.Snapshot.Select(m=>new QuickCommand(m.Name,()=>Guard(()=>ApplyQuickSettingsAsync(s=>s with{Model=m.Id})),state.Settings.Model==m.Id)).ToArray()),
            new("-"),Toggle("Dark mode",s=>s.DarkMode,(s,v)=>s with{DarkMode=v}),Toggle("Show internal browser",s=>s.ShowBrowser,(s,v)=>s with{ShowBrowser=v}),
            Toggle("Automatic profile failover",s=>s.AutomaticProfileFailover,(s,v)=>s with{AutomaticProfileFailover=v}),
            Toggle("Resume API on startup",s=>s.AutoStartEndpoint,(s,v)=>s with{AutoStartEndpoint=v}),
            new("API options",Children:[Toggle("OpenAI compatibility",s=>s.OpenAi,(s,v)=>s with{OpenAi=v}),Toggle("Require local API key",s=>s.RequireApiKey,(s,v)=>s with{RequireApiKey=v}),Toggle("Translate tool calls",s=>s.ToolCalling,(s,v)=>s with{ToolCalling=v})]),
            new("Settings",Children:new[]{"Accounts","API","Models","App","Logs","Sessions"}.Select((label,index)=>new QuickCommand(label,()=>Immediate(()=>{SettingsTabs.SelectedIndex=index;ShowApp();}))).ToArray()),
            new("Check for updates",CheckUpdatesAsync),new("-"),new("Exit",()=>Immediate(Close))
        ];
    }
    private Forms.ToolStripItem TrayItem(QuickCommand command)
    {
        if(command.Label=="-")return new Forms.ToolStripSeparator();
        var item=new Forms.ToolStripMenuItem(command.Label){Checked=command.Checked==true};
        if(command.Children is not null){foreach(var child in command.Children)item.DropDownItems.Add(TrayItem(child));item.Enabled=command.Children.Count>0;}
        else if(command.Run is not null)item.Click+=async(_,_)=>await command.Run();
        return item;
    }
    private object WindowItem(QuickCommand command)
    {
        if(command.Label=="-")return new Wpf.Separator();
        var item=new Wpf.MenuItem{Header=command.Label,IsCheckable=command.Checked is not null,IsChecked=command.Checked==true};
        if(command.Children is not null){foreach(var child in command.Children)item.Items.Add(WindowItem(child));item.IsEnabled=command.Children.Count>0;}
        else if(command.Run is not null)item.Click+=async(_,e)=>{e.Handled=true;await command.Run();};
        return item;
    }
    private void RefreshQuickMenus()
    {
        if(tray?.ContextMenuStrip is not {} menu||state is null)return;
        var dark=DarkModeBox.IsChecked??state.Settings.DarkMode;
        menu.Renderer=new Forms.ToolStripProfessionalRenderer(new MenuColors(dark)){RoundedEdges=false};
        void Colors(Forms.ToolStripItemCollection items)
        {
            foreach(Forms.ToolStripItem item in items)
            {
                item.BackColor=dark?System.Drawing.Color.FromArgb(26,32,41):System.Drawing.SystemColors.Window;
                item.ForeColor=dark?System.Drawing.Color.FromArgb(237,241,247):System.Drawing.SystemColors.WindowText;
                if(item is Forms.ToolStripMenuItem child)Colors(child.DropDownItems);
            }
        }
        menu.BackColor=dark?System.Drawing.Color.FromArgb(26,32,41):System.Drawing.SystemColors.Window;Colors(menu.Items);
    }
    private sealed class MenuColors(bool dark):Forms.ProfessionalColorTable
    {
        private System.Drawing.Color Panel=>dark?System.Drawing.Color.FromArgb(26,32,41):System.Drawing.SystemColors.Window;
        private System.Drawing.Color Hover=>dark?System.Drawing.Color.FromArgb(53,73,102):System.Drawing.SystemColors.Highlight;
        public override System.Drawing.Color ToolStripDropDownBackground=>Panel;
        public override System.Drawing.Color ImageMarginGradientBegin=>Panel;
        public override System.Drawing.Color ImageMarginGradientMiddle=>Panel;
        public override System.Drawing.Color ImageMarginGradientEnd=>Panel;
        public override System.Drawing.Color ImageMarginRevealedGradientBegin=>Panel;
        public override System.Drawing.Color ImageMarginRevealedGradientMiddle=>Panel;
        public override System.Drawing.Color ImageMarginRevealedGradientEnd=>Panel;
        public override System.Drawing.Color MenuItemSelected=>Hover;
        public override System.Drawing.Color MenuItemSelectedGradientBegin=>Hover;
        public override System.Drawing.Color MenuItemSelectedGradientEnd=>Hover;
        public override System.Drawing.Color MenuItemPressedGradientBegin=>Hover;
        public override System.Drawing.Color MenuItemPressedGradientMiddle=>Hover;
        public override System.Drawing.Color MenuItemPressedGradientEnd=>Hover;
        public override System.Drawing.Color CheckBackground=>Hover;
        public override System.Drawing.Color CheckSelectedBackground=>Hover;
    }
    private async Task ApplyQuickSettingsAsync(Func<BridgeSettings,BridgeSettings> change)
    {
        var before=state.Settings;var next=change(before);state.Update(next);
        DarkModeBox.IsChecked=next.DarkMode;ShowBrowserBox.IsChecked=next.ShowBrowser;AutoStartBox.IsChecked=next.AutoStartEndpoint;AutoUpdateBox.IsChecked=next.CheckUpdatesOnStartup;
        OpenAiBox.IsChecked=next.OpenAi;RequireKeyBox.IsChecked=next.RequireApiKey;ToolCallingBox.IsChecked=next.ToolCalling;
        if(before.Model!=next.Model){ModelBox.Text=next.Model;ModelPicker.SelectedItem=models.Snapshot.FirstOrDefault(m=>m.Id==next.Model);}
        ThinkingPicker.SelectedValue=next.ThinkingLevel;
        Theme.Apply(this,next.DarkMode);foreach(var entry in profileSessions.Values)entry.Session.SetDarkMode(next.DarkMode);SetBrowserVisible(next.ShowBrowser);RefreshQuickMenus();
        ProfileFailoverBox.IsChecked=next.AutomaticProfileFailover;
        if(server.Running&&(before.Model!=next.Model||before.ThinkingLevel!=next.ThinkingLevel||before.OpenAi!=next.OpenAi||before.RequireApiKey!=next.RequireApiKey||before.ToolCalling!=next.ToolCalling))
        {await server.DisposeAsync();await server.StartAsync();}
        UpdateConnectionStatus();SavedFeedback("Quick setting saved");Log("settings","Quick setting saved and applied.");
    }
    private void Thinking_Changed(object sender,Wpf.SelectionChangedEventArgs e)=>MarkSettingsDirty();
    private void CopyEndpoint_Click(object sender,RoutedEventArgs e)=>CopyEndpoint();
    private void CopyEndpoint()
    {
        if(state is null)return;
        try{Clipboard.SetText($"http://127.0.0.1:{(server?.Running==true?server.Port:state.Settings.Port)}/v1");Log("clipboard","API base URL copied to the clipboard.");}
        catch(Exception){Log("clipboard","Clipboard is unavailable. Copy the endpoint URL manually.");}
    }
    private void ShowApp(){Show();WindowState=WindowState.Normal;Activate();}
    private async Task ConnectAsync()
    {
        SelectProfile(await EnsureProfileAsync(profiles.ActiveId,CancellationToken.None),false);
        if(!session.Ready)
        {
            try{await CheckSession();}
            catch(BridgeException ex){RevealInternalBrowser();ShowApp();Log(ex.Code,ex.Message);return;}
        }
        if(!session.Ready){RevealInternalBrowser();ShowApp();Log("connection","Sign in to Perplexity to finish connecting.");return;}
        await StartEndpoint();
    }
    private async void Connect_Click(object sender,RoutedEventArgs e)=>await Guard(ConnectAsync);
    private void BrowserVisibility_Click(object sender,RoutedEventArgs e)
    {
        if(state is null)return;
        var show=ShowBrowserBox.IsChecked==true;
        MarkSettingsDirty();
        SetBrowserVisible(show);
    }
    private void StartupPreference_Click(object sender,RoutedEventArgs e)
    {
        MarkSettingsDirty();
    }
    private void SetBrowserVisible(bool visible)
    {
        Grid.SetColumn(AccountDetailsPanel,visible?0:2);Grid.SetRow(AccountDetailsPanel,visible?1:0);
        Grid.SetColumnSpan(AccountListPanel,visible?3:1);Grid.SetColumnSpan(AccountDetailsPanel,visible?3:1);Grid.SetRow(AccountRoutingPanel,visible?2:1);
        BrowserArea.Visibility=visible?Visibility.Visible:Visibility.Hidden;
        BrowserArea.Width=visible?double.NaN:700;
        SettingsColumn.Width=visible?new GridLength(470):new GridLength(1,GridUnitType.Star);
        BrowserGap.Width=new GridLength(visible?16:0);
        BrowserColumn.Width=visible?new GridLength(1,GridUnitType.Star):new GridLength(0);
        Grid.SetColumnSpan(SettingsTabs,visible?1:3);
        ConnectionDashboard.Visibility=Visibility.Collapsed;
    }
    private void UpdateConnectionStatus()
    {
        var connected=profiles is not null&&profileSessions.TryGetValue(profiles.ActiveId,out var apiEntry)&&apiEntry.Session.Ready;var running=server?.Running==true;
        ConnectionDot.Fill=connected?Brushes.MediumSeaGreen:Brushes.DarkOrange;
        EndpointDot.Fill=running?Brushes.MediumSeaGreen:Brushes.Gray;
        ConnectionLabel.Text=connected?"Perplexity · Connected":"Perplexity · Sign-in required";
        EndpointLabel.Text=running?"Local API · Running":"Local API · Stopped";
        if(state is not null)EndpointText.Text=$"http://127.0.0.1:{(running?server!.Port:state.Settings.Port)}/v1";
        DashboardStatus.Text=connected?running?"Connected and ready for API requests":"Perplexity connected · API stopped":"Sign in to connect Perplexity";
        if(tray is not null)tray.Text=connected&&running?"Local Perplexity Bridge · Connected · API running":connected?"Local Perplexity Bridge · Connected · API stopped":"Local Perplexity Bridge · Sign-in required";
    }
}