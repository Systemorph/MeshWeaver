using System.Text.Json;

namespace MeshWeaver.Maui.Abstractions;

/// <summary>The bound state the native ThreadChatView renders: which message bubbles to show + status.</summary>
/// <param name="ThreadPath">The thread node path (bubbles bind to <c>{ThreadPath}/{id}</c>).</param>
/// <param name="MessageIds">Ordered message-node IDs to render as bubbles.</param>
/// <param name="PendingTexts">Queued user-message texts not yet drained into Messages (shown immediately).</param>
/// <param name="IsExecuting">True while the agent is generating a response.</param>
/// <param name="ExecutionStatus">Human-readable execution status (shown while executing).</param>
public sealed record ThreadChatState(
    string? ThreadPath,
    IReadOnlyList<string> MessageIds,
    IReadOnlyList<string> PendingTexts,
    bool IsExecuting,
    string? ExecutionStatus);

/// <summary>The fields a single message bubble renders, read from the message node's content.</summary>
/// <param name="Role">"user" / "assistant" / "system".</param>
/// <param name="AuthorName">Display name (falls back to agentName).</param>
/// <param name="Text">Message body (streams in place while <see cref="IsStreaming"/>).</param>
/// <param name="IsStreaming">True while the cell's Status is "Streaming".</param>
/// <param name="ModelName">Model used for the response (footer chip).</param>
/// <param name="Timestamp">Message timestamp (footer chip).</param>
public sealed record MessageBubbleState(
    string Role,
    string? AuthorName,
    string Text,
    bool IsStreaming,
    string? ModelName,
    DateTime? Timestamp)
{
    /// <summary>True for the local user's messages (right-aligned bubble).</summary>
    public bool IsUser => string.Equals(Role, "user", StringComparison.OrdinalIgnoreCase);
}

/// <summary>
/// Pure projection of the thread/message JSON the native chat views bind to — the same shapes
/// produced by <c>ThreadLayoutAreas.BuildThreadViewModel</c> (the data-section ThreadViewModel) and a
/// <c>ThreadMessage</c> node's content. Kept MAUI-free so the chat rendering logic is unit-testable.
/// </summary>
public static class MauiChatProjection
{
    /// <summary>
    /// Reads the bubble-list + status state from EITHER the data-section <c>ThreadViewModel</c> (which
    /// carries an explicit <c>isExecuting</c>) OR a raw <c>Thread</c> node (whose <c>IsExecuting</c> is a
    /// computed <c>[JsonIgnore]</c> getter, so only <c>status</c> is present) — the direct-path mode reads
    /// the raw node. Execution is therefore derived from <c>isExecuting</c> OR an executing <c>status</c>.
    /// </summary>
    public static ThreadChatState ReadThreadViewModel(JsonElement vm) => new(
        ThreadPath: GetString(vm, "threadPath"),
        MessageIds: GetStringArray(vm, "messages"),
        PendingTexts: GetStringArray(vm, "pendingMessageTexts"),
        IsExecuting: GetBool(vm, "isExecuting") || IsExecutingStatus(GetString(vm, "status")),
        ExecutionStatus: GetString(vm, "executionStatus"));

    // The raw Thread node has no isExecuting (it's [JsonIgnore]); these Status values mean "executing".
    private static bool IsExecutingStatus(string? status) =>
        status is "StartingExecution" or "Executing";

    /// <summary>Reads a <c>ThreadMessage</c> node's content into the fields a bubble renders.</summary>
    public static MessageBubbleState ReadMessage(JsonElement content) => new(
        Role: GetString(content, "role") ?? "assistant",
        AuthorName: GetString(content, "authorName") ?? GetString(content, "agentName"),
        Text: GetString(content, "text") ?? "",
        IsStreaming: string.Equals(GetString(content, "status"), "Streaming", StringComparison.OrdinalIgnoreCase),
        ModelName: GetString(content, "modelName"),
        Timestamp: GetDateTime(content, "timestamp"));

    // ---- pure JSON readers (camelCase, tolerant of missing / wrong-kind) -------------------------

    /// <summary>The string value of property <paramref name="name"/>, or null.</summary>
    public static string? GetString(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.String
            ? p.GetString() : null;

    /// <summary>True only when property <paramref name="name"/> is JSON <c>true</c>.</summary>
    public static bool GetBool(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p) && p.ValueKind == JsonValueKind.True;

    /// <summary>The string elements of array property <paramref name="name"/>, or an empty list.</summary>
    public static List<string> GetStringArray(JsonElement e, string name)
    {
        var list = new List<string>();
        if (e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var arr) && arr.ValueKind == JsonValueKind.Array)
            foreach (var item in arr.EnumerateArray())
                if (item.ValueKind == JsonValueKind.String) list.Add(item.GetString()!);
        return list;
    }

    /// <summary>The DateTime value of property <paramref name="name"/>, or null.</summary>
    public static DateTime? GetDateTime(JsonElement e, string name) =>
        e.ValueKind == JsonValueKind.Object && e.TryGetProperty(name, out var p)
        && p.ValueKind == JsonValueKind.String && p.TryGetDateTime(out var dt) ? dt : null;
}
