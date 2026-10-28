using System.IO;
using System.Text.Json;
using System.Text.RegularExpressions;
using System.Text.Json.Serialization;
namespace PerplexityLocalBridge;

public sealed record AccountProfile(string Id,string Name,bool Enabled=true,DateTimeOffset? LimitedUntil=null,bool NeedsName=false,string? PauseKind=null,string Status="Not checked",string? AccountId=null)
{
    [JsonIgnore] public bool IsApiActive{get;init;}
    public string DisplayName=>NeedsName?"Name this profile":Name;
    public string Label=>(IsApiActive?"API → ":"")+DisplayName+(!Enabled?" · disabled":LimitedUntil>DateTimeOffset.UtcNow?" · "+(PauseKind=="rate"?"temporary limit":"usage limit")+" until "+LimitedUntil.Value.ToLocalTime().ToString("HH:mm"):" · "+(LimitedUntil is not null?"Pause ended · recheck on next request":Status));
}
public sealed class ProfileStore
{
    private readonly string path;
    private List<AccountProfile> profiles;
    public string ActiveId{get;private set;}
    public bool RecheckedLegacyPauses{get;private set;}
    public IReadOnlyList<AccountProfile> Profiles=>profiles.ToArray();
    public ProfileStore(string? storagePath=null)
    {
        path=storagePath??Path.Combine(BridgeState.DataFolder,"profiles.json");
        var saved=File.Exists(path)?JsonSerializer.Deserialize<Saved>(File.ReadAllText(path)):null;
        profiles=saved?.Profiles??[new("default","Unnamed profile",NeedsName:true)];ActiveId=saved?.ActiveId??"default";
        var legacy=saved?.Version<2?profiles.FindIndex(p=>p.Id=="default"&&p.Name=="Default"):-1;
        if(legacy>=0)
        {
            var name="Unnamed profile";var suffix=2;
            while(profiles.Any(p=>p.Id!="default"&&p.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))name="Unnamed profile "+suffix++;
            profiles[legacy]=profiles[legacy] with{Name=name,NeedsName=true};
        }
        if(profiles.Count is <1 or >20||profiles.Any(p=>!ValidId(p.Id)||!ValidName(p.Name))||profiles.Select(p=>p.Id).Distinct().Count()!=profiles.Count||profiles.Select(p=>p.Name.ToUpperInvariant()).Distinct().Count()!=profiles.Count||!profiles.Any(p=>p.Id==ActiveId))throw new InvalidDataException("Invalid saved profiles.");
        if(saved?.Version<3&&profiles.Any(p=>p.LimitedUntil is not null&&p.PauseKind is null))
        {
            profiles=profiles.Select(p=>p.PauseKind is null?p with{LimitedUntil=null,Status="Needs availability recheck"}:p).ToList();RecheckedLegacyPauses=true;
        }
        if(legacy>=0||RecheckedLegacyPauses)Change(()=>{});
    }
    public static bool ValidId(string id)=>id=="default"||Regex.IsMatch(id,@"^[a-f0-9]{32}$");
    public void CheckName(string name,string? id=null)=>ValidateName(name.Trim(),id);
    private static bool ValidName(string name)=>name.Length is >=1 and <=60&&!name.Any(char.IsControl)&&name==name.Trim();
    public AccountProfile Add(string name)
    {
        if(profiles.Count>=20)throw new ArgumentException("Up to 20 profiles can be saved.");
        ValidateName(name=name.Trim(),null);var profile=new AccountProfile(Guid.NewGuid().ToString("N"),name);
        Change(()=>profiles.Add(profile));return profile;
    }
    public void Rename(string id,string name){ValidateName(name=name.Trim(),id);Replace(id,p=>p with{Name=name,NeedsName=false});}
    public void ValidateDetails(string id,string? name)
    {
        var profile=profiles.FirstOrDefault(p=>p.Id==id)??throw new ArgumentException("Unknown profile.");
        if(string.IsNullOrWhiteSpace(name)&&!profile.NeedsName)throw new ArgumentException("Enter a profile name with 1–60 printable characters.");
        if(!string.IsNullOrWhiteSpace(name))ValidateName(name.Trim(),id);
    }
    public void UpdateDetails(string id,string? name,bool enabled)
    {
        ValidateDetails(id,name);
        Replace(id,p=>string.IsNullOrWhiteSpace(name)?p with{Enabled=enabled}:p with{Name=name.Trim(),Enabled=enabled,NeedsName=false});
    }
    public void Enable(string id,bool enabled)=>Replace(id,p=>p with{Enabled=enabled});
    public void Reset(string id)=>Replace(id,p=>p with{LimitedUntil=null,PauseKind=null,Status="Needs availability recheck"});
    public void Limit(string id,DateTimeOffset until,string kind="usage")=>Replace(id,p=>p with{LimitedUntil=until,PauseKind=kind,Status=kind=="rate"?"Temporary limit":"Usage limit"});
    public void SetStatus(string id,string status)=>Replace(id,p=>p.Status==status?p:p with{Status=status});
    public void SetAccount(string id,string? accountIdentity)=>Replace(id,p=>p with{AccountId=accountIdentity});
    public void Activate(string id)
    {
        if(!profiles.Any(p=>p.Id==id))throw new ArgumentException("Unknown profile.");
        if(ActiveId==id&&File.Exists(path))return;
        Change(()=>ActiveId=id);
    }
    public void Move(string id,int offset)
    {
        var index=profiles.FindIndex(p=>p.Id==id);if(index<0)throw new ArgumentException("Unknown profile.");
        var target=Math.Clamp(index+offset,0,profiles.Count-1);
        Change(()=>{var item=profiles[index];profiles.RemoveAt(index);profiles.Insert(target,item);});
    }
    public AccountProfile Remove(string id)
    {
        var index=profiles.FindIndex(p=>p.Id==id);if(index<0)throw new ArgumentException("Unknown profile.");
        Change(()=>
        {
            profiles.RemoveAt(index);
            if(profiles.Count==0)profiles.Add(new AccountProfile(Guid.NewGuid().ToString("N"),"Unnamed profile",NeedsName:true));
            if(ActiveId==id)
            {
                var ordered=Enumerable.Range(0,profiles.Count).Select(i=>profiles[(index+i)%profiles.Count]).ToArray();
                ActiveId=(ordered.FirstOrDefault(p=>p.Enabled&&(p.LimitedUntil is null||p.LimitedUntil<=DateTimeOffset.UtcNow))??
                    ordered.FirstOrDefault(p=>p.Enabled)??ordered[0]).Id;
            }
        });
        return profiles.First(p=>p.Id==ActiveId);
    }
    public string BrowserFolder(string id)
    {
        if(!ValidId(id)||!profiles.Any(p=>p.Id==id))throw new ArgumentException("Unknown profile.");
        var root=Path.GetDirectoryName(Path.GetFullPath(path))!;
        return id=="default"?Path.Combine(root,"BrowserProfile"):Path.Combine(root,"Profiles",id,"BrowserProfile");
    }
    private void ValidateName(string name,string? id)
    {
        if(!ValidName(name))throw new ArgumentException("Use a profile name with 1–60 printable characters.");
        if(profiles.Any(p=>p.Id!=id&&p.Name.Equals(name,StringComparison.OrdinalIgnoreCase)))throw new ArgumentException("Choose a unique profile name.");
    }
    private void Replace(string id,Func<AccountProfile,AccountProfile> transform)
    {
        var index=profiles.FindIndex(p=>p.Id==id);if(index<0)throw new ArgumentException("Unknown profile.");
        var next=transform(profiles[index]);if(next==profiles[index])return;
        Change(()=>profiles[index]=next);
    }
    private void Change(Action action)
    {
        var before=profiles.ToList();var active=ActiveId;
        try{action();Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);File.WriteAllText(path+".tmp",JsonSerializer.Serialize(new Saved(ActiveId,profiles,3),new JsonSerializerOptions{WriteIndented=true}));File.Move(path+".tmp",path,true);}
        catch{profiles=before;ActiveId=active;throw;}
    }
    private sealed record Saved(string ActiveId,List<AccountProfile> Profiles,int Version=1);
}

public sealed class ProfileRouter(ProfileStore store)
{
    public async Task<Completion> CompleteAsync(bool failover,int cooldownMinutes,Func<AccountProfile,CancellationToken,Task<bool>> prepare,Func<AccountProfile,CancellationToken,Task<Completion>> complete,Action<AccountProfile>? limited,CancellationToken cancellation)
    {
        var all=store.Profiles;var active=all.ToList().FindIndex(p=>p.Id==store.ActiveId);
        var ordered=Enumerable.Range(0,all.Count).Select(i=>all[(active+i)%all.Count]).ToArray();
        var eligible=0;var limitedCount=0;var modelUnavailable=false;
        foreach(var profile in failover?ordered:ordered.Take(1))
        {
            cancellation.ThrowIfCancellationRequested();
            if(!profile.Enabled)continue;
            if(profile.LimitedUntil>DateTimeOffset.UtcNow){limitedCount++;continue;}
            eligible++;
            if(!await prepare(profile,cancellation))continue;
            store.Activate(profile.Id);
            try{return await complete(profile,cancellation);}
            catch(BridgeException ex)when(ex.Status==429&&ex.Code is "perplexity_limit" or "perplexity_rate_limit")
            {
                limitedCount++;var temporary=ex.Code=="perplexity_rate_limit";
                store.Limit(profile.Id,ex.RetryAt??DateTimeOffset.UtcNow.AddMinutes(temporary?1:cooldownMinutes),temporary?"rate":"usage");limited?.Invoke(profile);
                if(!failover)throw;
            }
            catch(BridgeException ex)when(ex.Code=="profile_model_unavailable"&&failover)
            {modelUnavailable=true;store.SetStatus(profile.Id,"Requested model unavailable");}
        }
        if(modelUnavailable&&limitedCount==0)throw new BridgeException(404,"model_unavailable","The requested model is unavailable on the checked accounts.");
        if(eligible>0&&limitedCount==0)throw new BridgeException(503,"profiles_not_ready","Enabled accounts could not be verified. Open Accounts and Check all accounts, then sign in where needed.");
        throw new BridgeException(429,"profiles_unavailable","No enabled, signed-in profile is available for this model. Profiles at a usage limit are paused until their cooldown ends. Check Accounts settings or reset a pause after Perplexity's allowance resets.");
    }
}

