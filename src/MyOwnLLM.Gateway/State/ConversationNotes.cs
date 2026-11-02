using System.Collections.Concurrent;

namespace MyOwnLLM.Gateway.State;

/// <summary>
/// The project note as it was when the conversation began. The note sits near the prompt's start, so a note update in
/// the middle of a conversation would make the model read everything after it again; history cuts don't cost that. A
/// conversation is new when its history is empty, which is also what /clear leaves. After a gateway restart nothing is
/// held, so the conversation takes the current note, at the cost of one re-read.
/// </summary>
public sealed class ConversationNotes
{
    readonly ConcurrentDictionary<string, string?> _notes = new();

    /// <returns><paramref name="current"/>, kept, for a new conversation or a project with nothing kept; otherwise what was kept.</returns>
    public string? For(string project, bool newConversation, string? current)
    {
        var key = ProjectStateStore.Key(project);
        if (newConversation || !_notes.TryGetValue(key, out var kept)) return _notes[key] = current;
        return kept;
    }
}
