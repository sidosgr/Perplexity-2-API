using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
namespace PerplexityLocalBridge;

public sealed record BridgeSettings(int Port = 8082, bool OpenAi = true, string Model = "perplexity-auto", int TimeoutSeconds = 120, string? AccountSessionId = null, bool RequireApiKey = true, bool DarkMode = true, bool ToolCalling = true, bool CheckUpdatesOnStartup = false, bool AutoStartEndpoint = true, bool EndpointEnabled = false, bool ShowBrowser = true, string ThinkingLevel = "off", bool AutomaticProfileFailover = true, int ProfileCooldownMinutes = 300, bool LanMode = false, string? LanAddress = null);
public sealed record LocalKey(string Id, string Name, string Prefix, string Hash, string? ProtectedSecret = null) { public string Label => $"{Name} · {Prefix}…"; }
public sealed record AccountIdentity(string Id, string Name);
public sealed record FunctionCall(string Id, string Name, string Arguments);
public sealed record Completion(string Text, string Model, IReadOnlyList<FunctionCall>? ToolCalls = null, string FinishReason = "stop", IReadOnlyList<string>? Sources = null, ThreadCursor? Cursor = null, string? ProfileId = null);
public sealed record WebModel(string Id, string Name, bool Reasoning = false);
public sealed record ThinkingOptions(string Mode = "off", string Effort = "low")
{
    public static ThinkingOptions FromLevel(string level)=>level=="off"?new():new("extended",level);
}
public sealed class ModelCatalog
{
    private readonly object gate = new();
    private WebModel[] models = [];
    public IReadOnlyList<WebModel> Snapshot { get { lock (gate) return models.ToArray(); } }
    public void Replace(IEnumerable<WebModel> available) { lock (gate) models = available.Where(m => System.Text.RegularExpressions.Regex.IsMatch(m.Id, @"^perplexity-[a-zA-Z0-9_-]+$")).DistinctBy(m => m.Id).Take(80).ToArray(); }
    public void Clear() { lock (gate) models = []; }
    public string Resolve(string requested, string fallback)
    {
        if (requested == "perplexity-web") return fallback;
        if (requested == "perplexity-auto" || Snapshot.Any(m => m.Id == requested)) return requested;
        throw new BridgeException(404, "model_not_found", "This model has not been discovered in the Perplexity picker. Refresh models in the app or use perplexity-web for the configured default.");
    }
}
public sealed class BridgeException(int status, string code, string message, DateTimeOffset? retryAt = null, ThreadCursor? cursor = null) : Exception(message)
{
    public ThreadCursor? Cursor { get; } = cursor;
    public DateTimeOffset? RetryAt { get; } = retryAt;
    public int Status { get; } = status;
    public string Code { get; } = code;
}
public sealed class BridgeState
{
    public static readonly string DataFolder = Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "LocalPerplexityBridge");
    private readonly string path;
    private readonly object gate = new();
    public BridgeSettings Settings { get; private set; } = new();
    private List<LocalKey> keys = [];
    public IReadOnlyList<LocalKey> Keys { get { lock (gate) return keys.ToArray(); } }
    public BridgeState(string? storagePath = null)
    {
        path = storagePath ?? Path.Combine(DataFolder, "settings.json");
        if (!File.Exists(path)) return;
        var data = JsonSerializer.Deserialize<Stored>(File.ReadAllText(path)) ?? throw new InvalidDataException("Settings file is empty.");
        Settings = data.Settings with { Model = NormalizeModel(data.Settings.Model) };
        if(data.Version<2&&Settings.ProfileCooldownMinutes==60)Settings=Settings with{ProfileCooldownMinutes=300};
        Validate(Settings); keys = data.Keys;
        if (Settings != data.Settings||data.Version<2) Save();
    }
    public static string NormalizeModel(string model) => model.Trim();
    public static void Validate(BridgeSettings value)
    {
        if(value.LanMode && (!value.RequireApiKey || !NetworkAccess.IsLocalAddress(value.LanAddress)))throw new ArgumentException("LAN mode requires an API key and an assigned private IPv4 address.");
        if(value.ProfileCooldownMinutes is <1 or >1440)throw new ArgumentException("Profile cooldown must be between 1 and 1440 minutes.");
        if(value.ThinkingLevel != "off")throw new ArgumentException("Choose a supported thinking level.");
        if (value.Port is < 1024 or > 65535) throw new ArgumentException("Port must be between 1024 and 65535.");
        if (value.TimeoutSeconds is < 15 or > 600) throw new ArgumentException("Timeout must be between 15 and 600 seconds.");
        if (!Regex.IsMatch(value.Model, @"^perplexity-[a-zA-Z0-9_-]{1,100}$")) throw new ArgumentException("Choose a model returned by /v1/models, or perplexity-auto.");
        if (value.AccountSessionId is not null && !Guid.TryParse(value.AccountSessionId, out _)) throw new ArgumentException("Invalid saved account identity identifier.");
    }
    public void Update(BridgeSettings value)
    {
        Validate(value);
        lock (gate) { var old = Settings; Settings = value; try { Save(); } catch { Settings = old; throw; } }
    }
    public (LocalKey Key, string Secret) CreateKey(string name)
    {
        name = name.Trim();
        if (name.Length is < 1 or > 60 || name.Any(char.IsControl)) throw new ArgumentException("Key name must contain 1–60 printable characters.");
        var secret = "lpb_" + Convert.ToHexString(RandomNumberGenerator.GetBytes(32)).ToLowerInvariant();
        var key = new LocalKey(Guid.NewGuid().ToString("N"), name, secret[..12], Digest(secret), Convert.ToBase64String(ProtectedData.Protect(Encoding.UTF8.GetBytes(secret), null, DataProtectionScope.CurrentUser)));
        lock (gate) { keys.Add(key); try { Save(); } catch { keys.Remove(key); throw; } }
        return (key, secret);
    }
    public void Revoke(string id)
    {
        lock (gate) { var old = keys.ToList(); keys.RemoveAll(k => k.Id == id); try { Save(); } catch { keys = old; throw; } }
    }
    public string CopyableSecret(string id)
    {
        lock(gate)
        {
            var key=keys.SingleOrDefault(k=>k.Id==id)??throw new ArgumentException("Select an active key.");
            if(key.ProtectedSecret is null)throw new ArgumentException("This older key was stored as a hash only. Create a replacement key to enable copying after restart.");
            return Encoding.UTF8.GetString(ProtectedData.Unprotect(Convert.FromBase64String(key.ProtectedSecret),null,DataProtectionScope.CurrentUser));
        }
    }
    public bool Authenticate(string secret)
    {
        if (secret.Length is < 10 or > 200) return false;
        var candidate = Encoding.ASCII.GetBytes(Digest(secret));
        lock (gate) return keys.Any(k => CryptographicOperations.FixedTimeEquals(candidate, Encoding.ASCII.GetBytes(k.Hash)));
    }
    private static string Digest(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text)));
    private void Save()
    {
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllText(path + ".tmp", JsonSerializer.Serialize(new Stored(Settings, keys,2), new JsonSerializerOptions { WriteIndented = true }));
        File.Move(path + ".tmp", path, true);
    }
    private sealed record Stored(BridgeSettings Settings, List<LocalKey> Keys,int Version=1);
}

public static class NetworkAccess
{
    public static bool IsPrivate(System.Net.IPAddress address)
    {
        var b=address.GetAddressBytes();return b.Length==4&&(b[0]==10||b[0]==172&&b[1]>=16&&b[1]<=31||b[0]==192&&b[1]==168);
    }
    public static string[] Addresses()=>System.Net.NetworkInformation.NetworkInterface.GetAllNetworkInterfaces()
        .Where(n=>n.OperationalStatus==System.Net.NetworkInformation.OperationalStatus.Up)
        .SelectMany(n=>n.GetIPProperties().UnicastAddresses).Select(a=>a.Address)
        .Where(IsPrivate).Select(a=>a.ToString()).Distinct().ToArray();
    public static bool IsLocalAddress(string? address)=>address is not null&&Addresses().Contains(address);
    public static bool Allowed(System.Net.IPAddress? remote,string host,bool browser,BridgeSettings settings)
    {
        if(remote is null||browser)return false;
        if(System.Net.IPAddress.IsLoopback(remote))return host is "127.0.0.1" or "localhost"||settings.LanMode&&host==settings.LanAddress;
        return settings.LanMode&&IsPrivate(remote)&&host==settings.LanAddress;
    }
}
