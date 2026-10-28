namespace PerplexityLocalBridge;

public static class SettingsCommit
{
    public static void Save(BridgeState state,ProfileStore profiles,BridgeSettings settings,string? profileId,string? profileName,bool enabled)
    {
        BridgeState.Validate(settings);
        if(profileId is not null)profiles.ValidateDetails(profileId,profileName);
        var before=state.Settings;
        state.Update(settings);
        try{if(profileId is not null)profiles.UpdateDetails(profileId,profileName,enabled);}
        catch{state.Update(before);throw;}
    }
}
