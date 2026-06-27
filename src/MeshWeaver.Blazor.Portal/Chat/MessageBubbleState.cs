using MeshWeaver.Layout;

namespace MeshWeaver.Blazor.Portal.Chat;

/// <summary>
/// The rendered state of one chat bubble, projected from a ThreadMessage cell's content in
/// <c>ThreadChatView.UpdateMessageState</c>. The per-message <c>.Subscribe()</c> dedups with
/// <c>Equals(prev, newState)</c> so an unchanged re-emission of the message stream does NOT
/// re-render.
///
/// <para>🚨 Value equality MUST compare <see cref="ToolCalls"/> and <see cref="UpdatedNodes"/> by
/// SEQUENCE. The synthesized record equality compares them by REFERENCE, and
/// <c>UpdateMessageState</c> deserializes a FRESH list on every emission — so for the agent's
/// OUTPUT message (which carries tool calls / node changes) <c>Equals</c> was perpetually false,
/// the dedup never fired, and every re-emission of the (JsonElement) message stream fired
/// <c>StateHasChanged</c>. That is the render storm that vanished the chat "when pushing the
/// output message". Pinned by <c>MessageBubbleStateEqualityTest</c>.</para>
/// </summary>
internal record MessageBubbleState(
    string Role,
    string AuthorName,
    string? ModelName,
    DateTime? Timestamp,
    string? Text,
    IReadOnlyList<ToolCallEntry>? ToolCalls,
    IReadOnlyList<NodeChangeEntry>? UpdatedNodes,
    string? Status = null,
    DateTime? CompletedAt = null,
    string? Harness = null,
    int? InputTokens = null,
    int? OutputTokens = null)
{
    /// <summary>
    /// Value equality over all bound bubble state, comparing the tool-call and updated-node lists
    /// by SEQUENCE (their elements are records) so a re-emission carrying structurally-identical
    /// but reference-distinct lists dedups instead of forcing a redundant re-render.
    /// </summary>
    public virtual bool Equals(MessageBubbleState? other)
    {
        if (other is null) return false;
        if (ReferenceEquals(this, other)) return true;
        return Role == other.Role
               && AuthorName == other.AuthorName
               && ModelName == other.ModelName
               && Timestamp == other.Timestamp
               && Text == other.Text
               && Status == other.Status
               && CompletedAt == other.CompletedAt
               && Harness == other.Harness
               && InputTokens == other.InputTokens
               && OutputTokens == other.OutputTokens
               && SequenceEqualOrBothEmpty(ToolCalls, other.ToolCalls)
               && SequenceEqualOrBothEmpty(UpdatedNodes, other.UpdatedNodes);
    }

    /// <summary>Hash consistent with <see cref="Equals(MessageBubbleState?)"/> — folds the list
    /// ELEMENTS, never the list reference.</summary>
    public override int GetHashCode()
    {
        var hash = new HashCode();
        hash.Add(Role);
        hash.Add(AuthorName);
        hash.Add(ModelName);
        hash.Add(Timestamp);
        hash.Add(Text);
        hash.Add(Status);
        hash.Add(CompletedAt);
        hash.Add(Harness);
        hash.Add(InputTokens);
        hash.Add(OutputTokens);
        foreach (var tc in ToolCalls ?? [])
            hash.Add(tc);
        foreach (var un in UpdatedNodes ?? [])
            hash.Add(un);
        return hash.ToHashCode();
    }

    private static bool SequenceEqualOrBothEmpty<T>(IReadOnlyList<T>? a, IReadOnlyList<T>? b)
    {
        if (ReferenceEquals(a, b)) return true;
        var ca = a?.Count ?? 0;
        var cb = b?.Count ?? 0;
        if (ca != cb) return false;
        return ca == 0 || a!.SequenceEqual(b!);
    }
}
