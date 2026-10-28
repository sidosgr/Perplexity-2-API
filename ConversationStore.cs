using System.IO;
using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PerplexityLocalBridge;

public sealed record ThreadCursor(string BackendId, string ReadWriteToken, string ContextId, string? Slug = null);
public sealed record ConversationCall(string Prompt, string Model, int TimeoutSeconds, ThinkingOptions Thinking,
    string Key, string ClientNamespace, string Project, string RequestHash, BridgeRequest? Request = null, bool Persistent = true, bool Repair = false);
public sealed record ConversationSummary(string Id,string Project,string ThreadId,int Turns,bool Pending,DateTimeOffset UpdatedAt);
public sealed record ConversationState(string Key, string ClientNamespace, string ProfileId, string Project,
    string FrontendContextId, ThreadCursor? Cursor = null, string? RequestHash = null, Completion? Cached = null,
    bool Pending = false, DateTimeOffset UpdatedAt = default, string? AccountIdentity = null, int CompletedTurns = 0, string? PublicThreadId = null, string? ContextHash = null, string[]? HistoryHashes = null, string? StagedInputHash = null, int StagedInputPart = 0, string? RejectedRequestHash = null, int RejectedStatus = 413, string? RejectedCode = null, string? RejectedMessage = null, string? RepairAttemptedRequestHash = null)
{
    public string ThreadId => Cursor?.ContextId ?? PublicThreadId ?? FrontendContextId;
    public string Label => $"{Project} · session {Key[..10]} · thread {ThreadId} · {CompletedTurns} turns · {(Pending ? "needs recovery" : "ready")} · {UpdatedAt.ToLocalTime():g}";
}

// Thread access tokens and the single cached reply stay encrypted for this Windows user.
public sealed class ConversationStore
{
    private readonly string path;
    private Dictionary<string, ConversationState> entries = [];
    private static readonly byte[] Entropy = Encoding.UTF8.GetBytes("LocalPerplexityBridge.Conversations.v1");
    public IReadOnlyList<ConversationState> Snapshot => entries.Values.OrderByDescending(e => e.UpdatedAt).ToArray();
    public ConversationStore(string? storagePath = null)
    {
        path = storagePath ?? Path.Combine(BridgeState.DataFolder, "conversations.dpapi");
        if (!File.Exists(path)) return;
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Conversation storage requires Windows DPAPI.");
        try
        {
            var plain = ProtectedData.Unprotect(File.ReadAllBytes(path), Entropy, DataProtectionScope.CurrentUser);
            var saved = JsonSerializer.Deserialize<ConversationState[]>(plain) ?? throw new InvalidDataException();
            if (saved.Length > 500 || saved.Any(e => e.Key.Length != 64 || !e.Key.All(Uri.IsHexDigit))) throw new InvalidDataException();
            entries = saved.ToDictionary(e => e.Key);
        }
        catch (Exception ex) when (ex is CryptographicException or JsonException or InvalidDataException or ArgumentException)
        { throw new BridgeException(503, "session_store_unavailable", "Saved thread mappings could not be read. They were kept; no replacement conversations were created."); }
    }
    public ConversationState? Find(string key) => entries.GetValueOrDefault(key);
    public void Put(ConversationState entry)
    {
        if (!entries.ContainsKey(entry.Key) && entries.Count >= 500)
            throw new BridgeException(503, "session_store_full", "The saved conversation limit was reached. Existing mappings were retained.");
        var previous = entries.GetValueOrDefault(entry.Key);
        entries[entry.Key] = entry with { UpdatedAt = DateTimeOffset.UtcNow };
        try { Save(); }
        catch { if (previous is null) entries.Remove(entry.Key); else entries[entry.Key] = previous; throw; }
    }
    public ConversationState BeginRepair(ConversationState saved,string requestHash)
    {
        if(saved.Pending||saved.Cursor is null)throw new BridgeException(409,"repair_unavailable","A response correction requires a completed reply and its original thread cursor.");
        if(saved.RepairAttemptedRequestHash==requestHash)throw new BridgeException(502,"repair_exhausted","The single response correction for this request has already been attempted. No further generation or local tool execution was performed.");
        var updated=saved with{RepairAttemptedRequestHash=requestHash};Put(updated);return updated;
    }
    public void ForgetAccount(string profileId)
    {
        foreach(var entry in entries.Values.Where(e=>e.ProfileId==profileId).ToArray())
            Put(entry with{PublicThreadId=entry.ThreadId,Cursor=null,Cached=null,Pending=true});
    }
    public void Restore(string key, ConversationState? previous)
    {
        if (previous is null) entries.Remove(key); else entries[key] = previous;
        Save();
    }
    private void Save()
    {
        if (!OperatingSystem.IsWindows()) throw new PlatformNotSupportedException("Conversation storage requires Windows DPAPI.");
        var plain = JsonSerializer.SerializeToUtf8Bytes(entries.Values.ToArray());
        if (plain.Length > 64 * 1024 * 1024) throw new BridgeException(503, "session_store_full", "Conversation storage reached its size limit. Existing thread mappings were retained.");
        var encrypted = ProtectedData.Protect(plain, Entropy, DataProtectionScope.CurrentUser);
        Directory.CreateDirectory(Path.GetDirectoryName(Path.GetFullPath(path))!);
        File.WriteAllBytes(path + ".tmp", encrypted);
        File.Move(path + ".tmp", path, true);
    }
}
