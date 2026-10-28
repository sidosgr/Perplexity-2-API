using System.IO;
using System.Text.Json.Nodes;
namespace PerplexityLocalBridge;
public partial class MainWindow
{
    private async void ConfigurePi_Click(object sender,System.Windows.RoutedEventArgs e)=>await Guard(async()=>
    {
        if(!session.Ready)await CheckSession();
        if(!session.Ready)throw new BridgeException(503,"session_not_ready","Connect your Perplexity account before configuring Pi.");
        await RefreshModels();ConfigurePiIntegration();
    });
    private void ConfigurePiIntegration()
    {
        ConfigurePiClient();
        var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".pi","agent");
        var path=Path.Combine(folder,"models.json");var text=File.ReadAllText(path);var root=JsonNode.Parse(text)!;
        var provider=root["providers"]!["perplexity-2-api"]!;
        var old=(provider["models"] as JsonArray)?.OfType<JsonObject>().ToArray()??[];
        var choices=models.Snapshot.Prepend(new WebModel("perplexity-web","Configured default")).DistinctBy(m=>m.Id).ToArray();
        var updated=new JsonArray();
        foreach(var model in choices)
        {
            var row=old.FirstOrDefault(m=>m["id"]?.GetValue<string>()==model.Id)?.DeepClone() as JsonObject??new JsonObject();
            row["id"]=model.Id;row["name"]=model.Name;row["reasoning"]=false;updated.Add(row);
        }
        provider["models"]=updated;provider["authHeader"]=true;
        if(File.ReadAllText(path)!=text)throw new IOException("Pi model settings changed during configuration. Retry setup.");
        var backup=Path.Combine(folder,"perplexity-bridge-backups");Directory.CreateDirectory(backup);
        File.WriteAllText(Path.Combine(backup,"models-before-refresh-"+DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+".json"),text);
        File.WriteAllText(path+".bridge-tmp",root.ToJsonString(new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));File.Move(path+".bridge-tmp",path,true);
        var extensionFolder=Path.Combine(folder,"extensions");Directory.CreateDirectory(extensionFolder);
        using var input=System.Reflection.Assembly.GetExecutingAssembly().GetManifestResourceStream("PerplexityLocalBridge.PiBridge.mjs")!;
        using var reader=new StreamReader(input);File.WriteAllText(Path.Combine(extensionFolder,"perplexity-bridge.ts"),reader.ReadToEnd());
        Log("pi_integration","Pi session tracking is installed with "+choices.Length+" model selections. Run /reload in Pi to apply it.");
    }
    private void ConfigurePiClient()
    {
        var folder=Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.UserProfile),".pi","agent");
        var path=Path.Combine(folder,"models.json");
        if(!File.Exists(path))throw new ArgumentException("Pi models.json was not found.");
        var text=File.ReadAllText(path);var document=JsonNode.Parse(text)??throw new ArgumentException("Pi model settings are empty.");
        var provider=document["providers"]?["perplexity-2-api"]??throw new ArgumentException("The Pi Perplexity provider is not configured.");
        if(!Uri.TryCreate(provider["baseUrl"]?.GetValue<string>(),UriKind.Absolute,out var url)||url.Scheme!="http"||url.Host is not ("127.0.0.1" or "localhost")||url.Port!=state.Settings.Port)
            throw new ArgumentException("Pi must point to this bridge's local endpoint before its key is configured.");
        var oldKey=provider["apiKey"]?.GetValue<string>();
        if(oldKey is not null&&state.Authenticate(oldKey))return;
        var key=state.CreateKey("Pi Agent integration");
        try
        {
            var backup=Path.Combine(folder,"perplexity-bridge-backups");Directory.CreateDirectory(backup);
            File.WriteAllText(Path.Combine(backup,"models-before-key-"+DateTimeOffset.UtcNow.ToUnixTimeMilliseconds()+".json"),text);
            provider["apiKey"]=key.Secret;provider["authHeader"]=true;
            File.WriteAllText(path+".bridge-tmp",document.ToJsonString(new System.Text.Json.JsonSerializerOptions{WriteIndented=true}));File.Move(path+".bridge-tmp",path,true);
        }
        catch{state.Revoke(key.Key.Id);throw;}
        Log("pi_integration","Pi's dedicated local API key was configured. Its full value was not logged.");
    }
}
