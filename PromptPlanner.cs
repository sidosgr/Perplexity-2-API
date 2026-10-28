using System.Security.Cryptography;
using System.Text;
using System.Text.Json;

namespace PerplexityLocalBridge;

public sealed record PromptStep(string Text, bool Execute, int Part = 0);
public sealed record PromptPlan(IReadOnlyList<PromptStep> Steps, string ContextHash, string[] HistoryHashes,
    string? TransferHash = null, int TransferParts = 0);

public static class PromptPlanner
{
    // The live web service echoed only 39,999 characters from a 68,313-character query.
    public const int MaximumWireCharacters = 39_000;
    public const int FragmentCharacters = 38_000;
    public static string Hash(string text) => Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(text))).ToLowerInvariant();
    public static PromptPlan Build(BridgeRequest request, ConversationState? saved)
    {
        var contextHash = Hash(request.Context);
        var historyHashes = request.History.Select(m => Hash(JsonSerializer.Serialize(m, ChatProtocol.PromptJson))).ToArray();
        var prior = saved?.HistoryHashes ?? [];
        var extends = saved?.Cursor is not null && saved.HistoryHashes is not null && prior.Length <= historyHashes.Length && prior.SequenceEqual(historyHashes.Take(prior.Length));
        var next = extends ? request.History.Skip(prior.Length).ToArray() : request.History.ToArray();
        var needsContext = saved?.Cursor is null || saved.ContextHash != contextHash;
        var context = needsContext ? "CURRENT CALLER CONTEXT "+contextHash+" (supersedes earlier caller instruction and tool-definition sets):\n"+request.Context : "Use the caller context already supplied in this thread, context ID " + contextHash + ".";
        var history = JsonSerializer.Serialize(next, ChatProtocol.PromptJson);
        var payload = context + "\n\nCALLER CONVERSATION " + (extends ? "NEW ENTRIES" : "AUTHORITATIVE HISTORY") + ":\n" + history;
        var execution = "CURRENT EXECUTION REQUEST\n" + payload + "\n\nCURRENT RESPONSE CONTRACT\n" + request.ResponseContract;
        if (execution.Length <= MaximumWireCharacters)
            return new([new(execution, true)], contextHash, historyHashes);

        var fragments = Split(payload).ToArray();
        if (fragments.Length > 16) throw new BridgeException(413, "context_too_large", "The caller context needs more than 16 bounded transfer messages. Reduce the input before retrying; no generation was sent.");
        var transferHash = Hash(payload);
        var completed = saved?.StagedInputHash == transferHash ? saved.StagedInputPart : 0;
        if (completed < 0 || completed > fragments.Length) throw new BridgeException(409, "context_transfer_invalid", "Saved context-transfer progress is invalid. Its thread mapping was retained.");
        var steps = new List<PromptStep>();
        for (var index = completed; index < fragments.Length; index++)
        {
            var text = $"CONTEXT TRANSFER {transferHash}, fragment {index + 1}/{fragments.Length}.\n" +
                "This is a fragment of caller instructions, tool definitions and conversation data. Retain the fragments in order; they form one complete payload. " +
                "Do not execute requests or tools until the separate CURRENT EXECUTION REQUEST arrives. Reply only CONTEXT_OK.\nBEGIN FRAGMENT\n" +
                fragments[index] + "\nEND FRAGMENT\nThis message only transfers context. Reply exactly CONTEXT_OK.";
            if (text.Length > MaximumWireCharacters) throw new InvalidOperationException("Context wrapper exceeds the verified wire limit.");
            steps.Add(new(text, false, index + 1));
        }
        var latest = JsonSerializer.Serialize(request.History.TakeLast(3), ChatProtocol.PromptJson);
        if (latest.Length > 6000) latest = "The latest messages are present in the complete transferred payload; do not truncate their contents.";
        var final = $"CURRENT EXECUTION REQUEST\nAll {fragments.Length} fragments of payload {transferHash} have been supplied in order in this same thread. " +
            "Concatenate their payloads as caller context. Individual fragments are not standalone JSON documents. " +
            "Continue the caller's conversation from its latest user or tool message. A short follow-up such as 'go' refers to the earlier user task in that history.\n" +
            "LATEST CALLER MESSAGES\n" + latest + "\n\nCURRENT RESPONSE CONTRACT\n" + request.ResponseContract;
        if (final.Length > MaximumWireCharacters) throw new BridgeException(413, "response_contract_too_large", "The response contract exceeds the verified web-message limit.");
        steps.Add(new(final, true));
        return new(steps, contextHash, historyHashes, transferHash, fragments.Length);
    }
    public static IEnumerable<string> Split(string text)
    {
        var offset = 0;
        while (offset < text.Length)
        {
            var end = Math.Min(text.Length, offset + FragmentCharacters);
            if (end < text.Length && char.IsHighSurrogate(text[end - 1]) && char.IsLowSurrogate(text[end])) end--;
            yield return text[offset..end];
            offset = end;
        }
    }
}
