namespace MeshWeaver.AI;

/// <summary>
/// View model for thread data binding in the ThreadChatControl.
/// Wraps all thread state needed by the Blazor view:
/// - Messages: ordered list of child message node IDs
/// - ThreadPath: the thread node's full path
/// - InitialContext: the context path for agent initialization
/// - InitialContextDisplayName: display name for the context chip
///
/// Custom equality: uses SequenceEqual for Messages to prevent redundant UI updates
/// when the stream re-emits with a new list instance containing the same elements.
/// </summary>
public record ThreadViewModel
{
    public IReadOnlyList<string> Messages { get; init; } = [];
    public string? ThreadPath { get; init; }
    public string? InitialContext { get; init; }
    public string? InitialContextDisplayName { get; init; }

    public virtual bool Equals(ThreadViewModel? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return ThreadPath == other.ThreadPath
               && InitialContext == other.InitialContext
               && InitialContextDisplayName == other.InitialContextDisplayName
               && Messages.SequenceEqual(other.Messages);
    }

    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(ThreadPath);
        hash.Add(InitialContext);
        hash.Add(InitialContextDisplayName);
        foreach (var msg in Messages)
            hash.Add(msg);
        return hash.ToHashCode();
    }
}
