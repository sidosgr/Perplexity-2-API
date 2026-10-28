using System.IO;
using System.Net.Http;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using System.Windows;
using System.Windows.Controls;
using Microsoft.Web.WebView2.Core;
namespace PerplexityLocalBridge;

public partial class MainWindow : Window
{
    private BridgeState state = null!;
    private WebSession session = null!;
    private LocalServer server = null!;
    private readonly ModelCatalog models = new();
    private bool initialized, busy, closing;
    private bool startupTestPending = Environment.GetCommandLineArgs().Contains("--quick-test");
    private readonly List<object> report = [];

    public MainWindow()
    {
        InitializeComponent();
        try
        {
            var historyPath = Path.Combine(BridgeState.DataFolder, "web-session-test.json");
            if (File.Exists(historyPath))
            {
                using var history = JsonDocument.Parse(File.ReadAllText(historyPath));
                if (history.RootElement.TryGetProperty("events", out var previous) && previous.ValueKind == JsonValueKind.Array)
                    report.AddRange(previous.EnumerateArray().TakeLast(100).Select(item => (object)item.Clone()));
            }
        }
        catch (Exception) { /* Diagnostic history must not prevent startup. */ }
        Loaded += Initialize;
        Closing += async (_, e) =>
        {
            if (closing) return;
            e.Cancel = true; closing = true;
            updateCancellation?.Cancel();
            try { if (server is not null) await server.DisposeAsync(); }
            catch(Exception){ }
            finally { tray?.Dispose(); foreach(var entry in profileSessions.Values)entry.Session.Dispose(); updater.Dispose(); Close(); }
        };
    }
    private async void Initialize(object sender, RoutedEventArgs e)
    {
        try
        {
            state = new BridgeState();
            conversations = new ConversationStore();
            if(Environment.GetCommandLineArgs().Contains("--configure-pi"))ConfigurePiClient();
            InitializeUpdates();
            DarkModeBox.IsChecked = state.Settings.DarkMode;
            RequireKeyBox.IsChecked = state.Settings.RequireApiKey;
            LanModeBox.IsChecked=state.Settings.LanMode;
            LanAddressBox.ItemsSource=NetworkAccess.Addresses();
            LanAddressBox.SelectedItem=state.Settings.LanAddress??NetworkAccess.Addresses().FirstOrDefault();
            LanAddressBox.SelectionChanged+=(_,_)=>UpdateLanUrl();
            PortBox.TextChanged+=(_,_)=>UpdateLanUrl();
            LanModeBox.Click+=(_,_)=>{if(LanModeBox.IsChecked==true)RequireKeyBox.IsChecked=true;UpdateLanUrl();};
            KeysBox.SelectionChanged+=(_,_)=>NewKeyBox.Clear();
            UpdateLanUrl();
            ToolCallingBox.IsChecked = state.Settings.ToolCalling;
            AutoStartBox.IsChecked = state.Settings.AutoStartEndpoint;
            ShowBrowserBox.IsChecked = state.Settings.ShowBrowser;
            ThinkingPicker.ItemsSource=ThinkingLevels;
            ThinkingPicker.SelectedValue=state.Settings.ThinkingLevel;
            Theme.Apply(this,state.Settings.DarkMode);
            PortBox.Text = state.Settings.Port.ToString();
            OpenAiBox.IsChecked = state.Settings.OpenAi;
            ModelBox.Text = state.Settings.Model;
            TimeoutBox.Text = state.Settings.TimeoutSeconds.ToString();
            RefreshKeys();
            profiles=new ProfileStore();profileRouter=new ProfileRouter(profiles);
            ProfileFailoverBox.IsChecked=state.Settings.AutomaticProfileFailover;
            ProfileCooldownBox.Text=state.Settings.ProfileCooldownMinutes.ToString();
            busy=true;
            var initial=await EnsureProfileAsync(profiles.ActiveId,CancellationToken.None,false);SelectProfile(initial);
            server=new LocalServer(state,(prompt,model,seconds,token)=>CompleteProfilesAsync(prompt,model,seconds,new(),token),models,CompleteProfilesAsync,CompleteConversationAsync,RefreshApiModelsAsync,ConversationSummaries);
            if(profiles.RecheckedLegacyPauses)Log("profiles","Old unclassified pause timers were cleared once so saved accounts can be checked again. All accounts were kept.");
            initialized=true;InitializeTray();InitializeSettingsFeedback();RefreshSessions();ConfirmUpdateStartup();
            try{await initial.Loaded.Task.WaitAsync(TimeSpan.FromSeconds(30));}catch(TimeoutException){ }
            busy=false;
            await Guard(async()=>{await CheckSession();await ResumeEndpointAsync();});
            if(Environment.GetCommandLineArgs().Contains("--configure-pi")&&session.Ready)ConfigurePiIntegration();
            if(Environment.GetCommandLineArgs().Contains("--inspect-models")&&session.Ready)
                File.WriteAllText(Path.Combine(BridgeState.DataFolder,"model-inspection.json"),(await session.InspectModelsAsync()).GetRawText());
            if(Environment.GetCommandLineArgs().Contains("--check-profiles"))await Guard(CheckAllProfilesAsync);
            if(Environment.GetCommandLineArgs().Contains("--install-update"))_=RunUpdateCommandAsync();
            Log("browser", session.Ready ? "Perplexity session is connected." : "Sign in to Perplexity in the internal browser.");
        }
        catch (WebView2RuntimeNotFoundException) { Log("startup_error", "Microsoft Edge WebView2 Runtime is required. Install the Evergreen runtime from Microsoft, then reopen the app."); }
        catch (Exception ex) { Log("startup_error", "Could not initialize the app: " + ex.GetType().Name + ". Check the local settings file and WebView2 installation."); }
    }
    private void Log(string stage, string message)
    {
        if (!Dispatcher.CheckAccess()) { Dispatcher.Invoke(() => Log(stage, message)); return; }
        StatusText.Text = message;
        LogBox.AppendText($"{DateTime.Now:HH:mm:ss} [{stage}] {message}\n"); LogBox.ScrollToEnd();
        report.Add(new { utc = DateTimeOffset.UtcNow, stage, message });
        if (report.Count > 200) report.RemoveRange(0, report.Count - 200);
        try
        {
            Directory.CreateDirectory(BridgeState.DataFolder);
            File.WriteAllText(Path.Combine(BridgeState.DataFolder, "web-session-test.json"), JsonSerializer.Serialize(new { version = UpdatePaths.CurrentVersion.ToString(3), events = report }, new JsonSerializerOptions { WriteIndented = true }));
        }
        catch (IOException) { }
        catch (UnauthorizedAccessException) { }
    }
    private async Task Guard(Func<Task> action)
    {
        if (!initialized) { Log("app", "Browser is still initializing or could not start."); return; }
        if (busy||routing) { Log("app", "Wait for the current app operation or API request to finish."); return; }
        busy = true; CheckButton.IsEnabled = TestButton.IsEnabled = StartButton.IsEnabled = ModelsButton.IsEnabled = false;
        try { await action(); }
        catch (BridgeException ex) { Log(ex.Code, ex.Message); }
        catch (ArgumentException ex) { Log("settings", ex.Message); }
        catch (Exception ex) { Log("operation_error", "Operation failed: " + ex.GetType().Name + ". If starting the endpoint, check whether the port is already in use."); }
        finally { busy = false; CheckButton.IsEnabled = TestButton.IsEnabled = StartButton.IsEnabled = ModelsButton.IsEnabled = true; UpdateConnectionStatus(); }
    }
    private async void Login_Click(object sender, RoutedEventArgs e)=>await Guard(async()=>
    {
        await EnsureActiveBrowserAsync();
        RevealInternalBrowser();
        Browser.CoreWebView2.Navigate("https://www.perplexity.ai/");
    });
    private async void Check_Click(object sender, RoutedEventArgs e) => await Guard(async()=>{await CheckSession();await ResumeEndpointAsync();});
    private async Task CheckSession()
    {
        await EnsureActiveBrowserAsync();
        Log("session", "Checking the signed-in Perplexity web session…");
        await VerifyProfileAsync(profileSessions[viewedProfileId!],CancellationToken.None);
        var organizations=profileSessions[viewedProfileId!].AccountIdentities;
        SetBrowserVisible(ShowBrowserBox.IsChecked==true);
        UpdateConnectionStatus();
        Log("session", session.Ready ? "Perplexity web session verified. Account is ready. Account plan type remains unverified." : organizations.Count == 0 ? "No Perplexity account identity found. Complete the login in the browser." : "Perplexity web session verified. Choose the account identity to use.");
        if(session.Ready)
        {
            try { await RefreshModels(); }
            catch(BridgeException ex) { Log(ex.Code,ex.Message); }
        }
    }
    private void Org_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (session is null||changingProfile) return;
        session.AccountSessionId = (OrgBox.SelectedItem as AccountIdentity)?.Id;
        ModelPicker.ItemsSource = null;
        if (session.AccountSessionId is not null)
        {
            try { if(viewedProfileId is not null)profiles.SetAccount(viewedProfileId,session.AccountSessionId); }
            catch (Exception) { Log("settings", "Account identity is selected for this session, but its preference could not be saved."); }
        }
    }
    private async void SignOut_Click(object sender, RoutedEventArgs e) => await Guard(async () =>
    {
        await server.DisposeAsync(); EndpointText.Text = "Endpoint stopped";
        session.AccountSessionId = null; OrgBox.ItemsSource = null;
        if(viewedProfileId is not null){profileSessions[viewedProfileId].AccountIdentities=[];profiles.SetAccount(viewedProfileId,null);profiles.SetStatus(viewedProfileId,"Sign-in required");RefreshProfiles();}
        state.Update(state.Settings with { AccountSessionId = null, EndpointEnabled = false });
        SetBrowserVisible(true); UpdateConnectionStatus();
        await Browser.CoreWebView2.Profile.ClearBrowsingDataAsync(CoreWebView2BrowsingDataKinds.AllProfile);
        Browser.CoreWebView2.Navigate("https://www.perplexity.ai/");
        Log("session", "This app’s browser session was cleared.");
    });
    private BridgeSettings ReadSettings()
    {
        if (!int.TryParse(PortBox.Text, out var port) || !int.TryParse(TimeoutBox.Text, out var timeout)) throw new ArgumentException("Port and timeout must be whole numbers.");
        var settings = state.Settings with { Port=port, LanMode=LanModeBox.IsChecked==true, LanAddress=LanAddressBox.SelectedItem as string, OpenAi=OpenAiBox.IsChecked==true, Model=BridgeState.NormalizeModel(ModelBox.Text), TimeoutSeconds=timeout, RequireApiKey=RequireKeyBox.IsChecked==true, DarkMode=DarkModeBox.IsChecked==true, ToolCalling=ToolCallingBox.IsChecked==true, CheckUpdatesOnStartup=AutoUpdateBox.IsChecked==true, AutoStartEndpoint=AutoStartBox.IsChecked==true };
        if(!int.TryParse(ProfileCooldownBox.Text,out var cooldown))throw new ArgumentException("Profile cooldown must be a whole number of minutes.");
        settings=settings with{ThinkingLevel=ThinkingPicker.SelectedValue as string??state.Settings.ThinkingLevel,ShowBrowser=ShowBrowserBox.IsChecked==true,AutomaticProfileFailover=ProfileFailoverBox.IsChecked==true,ProfileCooldownMinutes=cooldown};
        BridgeState.Validate(settings); return settings;
    }
    private async Task StartEndpoint()
    {
        if (server.Running) return;
        if (!session.Ready) throw new BridgeException(503,"session_not_ready","Connect to Perplexity before starting the endpoint.");
        await server.StartAsync(); EndpointText.Text = $"http://127.0.0.1:{server.Port}/v1";
        state.Update(state.Settings with { EndpointEnabled=true }); UpdateConnectionStatus();
        Log("endpoint", state.Settings.RequireApiKey ? "Local endpoint is running with API-key authentication." : "Local endpoint is running without an API key. Access remains limited to this PC.");
    }
    private async void Start_Click(object sender, RoutedEventArgs e) => await Guard(ConnectAsync);
    private async void Stop_Click(object sender, RoutedEventArgs e)=>await StopEndpointAsync();
    private async Task StopEndpointAsync()
    {
        if (!initialized||routing) return;
        try { await server.DisposeAsync(); state.Update(state.Settings with { EndpointEnabled=false }); EndpointText.Text = "Endpoint stopped"; UpdateConnectionStatus(); Log("endpoint", "Endpoint stopped. Automatic endpoint restart is disabled until you connect again."); }
        catch (Exception) { Log("endpoint", "Endpoint shutdown could not complete cleanly."); }
    }
    private void RefreshKeys() => KeysBox.ItemsSource = state.Keys;
    private async void Models_Click(object sender,RoutedEventArgs e)=>await Guard(RefreshModels);
    private async Task RefreshModels()
    {
        var available=await session.DiscoverModelsAsync(force:true);
        PublishModels();
        ModelPicker.ItemsSource=available;
        ModelPicker.SelectedItem=available.FirstOrDefault(m=>m.Id==BridgeState.NormalizeModel(ModelBox.Text));
        Log("models",$"Discovered {available.Count} selectable models from Perplexity’s authenticated model configuration: "+string.Join(", ",available.Select(m=>m.Name))+". Remaining quota is not reported.");
        RefreshQuickMenus();
    }
    private void Model_Changed(object sender,SelectionChangedEventArgs e)
    {
        if(ModelPicker.SelectedItem is WebModel model)ModelBox.Text=model.Id;
    }
    private void Theme_Click(object sender,RoutedEventArgs e)
    {
        if(!initialized)return;
        var dark=DarkModeBox.IsChecked==true;Theme.Apply(this,dark);session?.SetDarkMode(dark);
        MarkSettingsDirty();
        RefreshQuickMenus();
    }
    private async void CreateKey_Click(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        var created = state.CreateKey(KeyNameBox.Text); NewKeyBox.Text = created.Secret;
        RefreshKeys(); KeysBox.SelectedItem=created.Key; NewKeyBox.Text=created.Secret; Log("keys", "Local API key created and encrypted for repeat copying."); return Task.CompletedTask;
    });
    private async void RevokeKey_Click(object sender, RoutedEventArgs e) => await Guard(() =>
    {
        if (KeysBox.SelectedItem is not LocalKey key) throw new ArgumentException("Select a key to revoke.");
        state.Revoke(key.Id); NewKeyBox.Clear(); RefreshKeys(); Log("keys", "Selected local API key revoked."); return Task.CompletedTask;
    });
    private void CopyKey_Click(object sender, RoutedEventArgs e)
    {
        try {
            if(KeysBox.SelectedItem is not LocalKey key)throw new ArgumentException("Select a key to copy.");
            var secret=state.CopyableSecret(key.Id);NewKeyBox.Text=secret;Clipboard.SetText(secret);
            Log("keys","Selected API key copied to clipboard.");
        }catch(Exception ex){Log("keys",ex is ArgumentException?ex.Message:"Key decryption or clipboard unavailable on this Windows account.");}
    }
    private void UpdateLanUrl()
    {
        if(LanUrlBox is null)return;
        LanUrlBox.Text=LanAddressBox.SelectedItem is string ip?$"http://{ip}:{PortBox.Text}/v1":"No private IPv4 address detected";
    }
    private async void Test_Click(object sender, RoutedEventArgs e) => await Guard(RunQuickTest);
    private async Task RunQuickTest()
    {
        if (!session.Ready) await CheckSession();
        if (!session.Ready) throw new BridgeException(503, "session_not_ready", "Finish signing in before running the test.");
        await StartEndpoint();
        var temporary = state.Settings.RequireApiKey ? state.CreateKey("Temporary quick test") : ((LocalKey Key, string Secret)?)null;
        try
        {
            using var client = new HttpClient { Timeout = TimeSpan.FromSeconds(state.Settings.TimeoutSeconds + 10) };
            internalTest=true;
            client.DefaultRequestHeaders.Add("X-Perplexity-Session-Id","selftest-"+Guid.NewGuid().ToString("N"));
            if(temporary is not null) client.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue("Bearer", temporary.Value.Secret);
            Log("test", "Sending one small prompt through the local endpoint to the Perplexity website…");
            using var response = await client.PostAsJsonAsync($"http://127.0.0.1:{server.Port}/v1/messages", new { model = "perplexity-web", messages = new[] { new { role = "user", content = "Reply with exactly OK." } } });
            using var result = JsonDocument.Parse(await response.Content.ReadAsStringAsync());
            if (!response.IsSuccessStatusCode)
            {
                var error = result.RootElement.GetProperty("error");
                Log("test_failed", $"HTTP {(int)response.StatusCode}: {error.GetProperty("message").GetString()}"); return;
            }
            var text = result.RootElement.GetProperty("content")[0].GetProperty("text").GetString() ?? "";
            Log("test_passed", text.Trim() == "OK" ? "PASS: local endpoint returned OK from the Perplexity web session. Free-plan identity and allowance attribution are not independently verified." : "PASS: Perplexity web session returned a text response through the local endpoint. Reply differed from OK; free allowance remains unverified.");
        }
        finally { internalTest=false; if(temporary is not null)state.Revoke(temporary.Value.Key.Id); RefreshKeys(); }
    }
}
