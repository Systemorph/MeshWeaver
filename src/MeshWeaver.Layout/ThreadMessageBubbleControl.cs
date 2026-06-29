namespace MeshWeaver.Layout;

/// <summary>
/// Layout control for rendering a single thread message as a chat bubble.
/// The Blazor view renders the bubble with role-based styling (user/assistant)
/// and data-binds the text content so only text updates during streaming.
/// Shows a spinner with execution status and cancel button while executing.
/// </summary>
public record ThreadMessageBubbleControl() : UiControl<ThreadMessageBubbleControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// The message role: "user" or "assistant". Determines bubble alignment and styling.
    /// </summary>
    public string Role { get; init; } = "user";

    /// <summary>
    /// The display name of the author (e.g., "You", "Navigator").
    /// </summary>
    public string AuthorName { get; init; } = "";

    /// <summary>
    /// Data-bound message text (via JsonPointerReference).
    /// Updates during streaming without rebuilding the control.
    /// </summary>
    public object? Text { get; init; }

    /// <summary>
    /// Data-bound flag indicating whether the agent is currently executing.
    /// When true, the Blazor view shows a spinner and cancel button.
    /// </summary>
    public object? IsExecuting { get; init; }

    /// <summary>
    /// Data-bound execution status text (e.g., "Calling search_nodes...", "Delegating to Navigator...").
    /// Shown alongside the spinner during execution.
    /// </summary>
    public object? ExecutionStatus { get; init; }

    /// <summary>
    /// The thread path used to target cancel requests.
    /// </summary>
    public string? ThreadPath { get; init; }

    /// <summary>
    /// The message ID within the thread. Used for edit/resubmit/delete operations.
    /// </summary>
    public string? MessageId { get; init; }

    /// <summary>
    /// The full message-node path. When set, the Blazor view subscribes directly
    /// to <c>workspace.GetRemoteStream&lt;MeshNode, MeshNodeReference&gt;(NodePath, ...)</c>
    /// and renders Text / ToolCalls / UpdatedNodes from the live node — no layout
    /// data section, no per-chunk hub message. See
    /// <c>Doc/Architecture/ThreadExecutionStreaming.md</c>. Legacy callers that
    /// pass concrete <c>Text</c>/<c>ToolCalls</c>/<c>UpdatedNodes</c> still work.
    /// </summary>
    public string? NodePath { get; init; }

    /// <summary>
    /// Data-bound list of completed tool calls for post-execution inspection.
    /// Rendered as collapsible entries in the Blazor view.
    /// </summary>
    public object? ToolCalls { get; init; }

    /// <summary>
    /// Data-bound list of nodes created/updated/deleted by this message's execution.
    /// The bubble cross-references tool-call target paths against this list to show
    /// inline Diff + Restore links on Create/Update/Delete/Patch tool chips.
    /// </summary>
    public object? UpdatedNodes { get; init; }

    /// <summary>Model name used for this response (e.g., "claude-sonnet-4-6").</summary>
    public string? ModelName { get; init; }

    /// <summary>Timestamp of the message.</summary>
    public DateTime? Timestamp { get; init; }

    /// <summary>Returns a copy with <paramref name="role"/> as its Role.</summary>
    /// <param name="role">The message role, e.g. "user" or "assistant".</param>
    /// <returns>A new instance with the updated Role.</returns>
    public ThreadMessageBubbleControl WithRole(string role) => this with { Role = role };
    /// <summary>Returns a copy with <paramref name="model"/> as its ModelName.</summary>
    /// <param name="model">The model name string, or null to clear it.</param>
    /// <returns>A new instance with the updated ModelName.</returns>
    public ThreadMessageBubbleControl WithModelName(string? model) => this with { ModelName = model };
    /// <summary>Returns a copy with <paramref name="ts"/> as its Timestamp.</summary>
    /// <param name="ts">The message timestamp, or null to clear it.</param>
    /// <returns>A new instance with the updated Timestamp.</returns>
    public ThreadMessageBubbleControl WithTimestamp(DateTime? ts) => this with { Timestamp = ts };
    /// <summary>Returns a copy with <paramref name="name"/> as its AuthorName.</summary>
    /// <param name="name">The display name of the message author.</param>
    /// <returns>A new instance with the updated AuthorName.</returns>
    public ThreadMessageBubbleControl WithAuthorName(string name) => this with { AuthorName = name };
    /// <summary>Returns a copy with <paramref name="text"/> as its Text.</summary>
    /// <param name="text">The message text or a data-bound reference to it.</param>
    /// <returns>A new instance with the updated Text.</returns>
    public ThreadMessageBubbleControl WithText(object? text) => this with { Text = text };
    /// <summary>Returns a copy with <paramref name="isExecuting"/> as its IsExecuting.</summary>
    /// <param name="isExecuting">A boolean or data-bound reference indicating whether the agent is executing.</param>
    /// <returns>A new instance with the updated IsExecuting.</returns>
    public ThreadMessageBubbleControl WithIsExecuting(object? isExecuting) => this with { IsExecuting = isExecuting };
    /// <summary>Returns a copy with <paramref name="status"/> as its ExecutionStatus.</summary>
    /// <param name="status">The execution status text or a data-bound reference to it.</param>
    /// <returns>A new instance with the updated ExecutionStatus.</returns>
    public ThreadMessageBubbleControl WithExecutionStatus(object? status) => this with { ExecutionStatus = status };
    /// <summary>Returns a copy with <paramref name="id"/> as its MessageId.</summary>
    /// <param name="id">The message ID within the thread, or null to clear it.</param>
    /// <returns>A new instance with the updated MessageId.</returns>
    public ThreadMessageBubbleControl WithMessageId(string? id) => this with { MessageId = id };
    /// <summary>Returns a copy with <paramref name="path"/> as its ThreadPath.</summary>
    /// <param name="path">The thread node path used for cancel operations, or null to clear it.</param>
    /// <returns>A new instance with the updated ThreadPath.</returns>
    public ThreadMessageBubbleControl WithThreadPath(string? path) => this with { ThreadPath = path };
    /// <summary>Returns a copy with <paramref name="path"/> as its NodePath.</summary>
    /// <param name="path">The message-node path for live data-binding, or null to use inline data.</param>
    /// <returns>A new instance with the updated NodePath.</returns>
    public ThreadMessageBubbleControl WithNodePath(string? path) => this with { NodePath = path };
    /// <summary>Returns a copy with <paramref name="toolCalls"/> as its ToolCalls.</summary>
    /// <param name="toolCalls">The tool calls list or a data-bound reference to it.</param>
    /// <returns>A new instance with the updated ToolCalls.</returns>
    public ThreadMessageBubbleControl WithToolCalls(object? toolCalls) => this with { ToolCalls = toolCalls };
    /// <summary>Returns a copy with <paramref name="updatedNodes"/> as its UpdatedNodes.</summary>
    /// <param name="updatedNodes">The updated-nodes list or a data-bound reference to it.</param>
    /// <returns>A new instance with the updated UpdatedNodes.</returns>
    public ThreadMessageBubbleControl WithUpdatedNodes(object? updatedNodes) => this with { UpdatedNodes = updatedNodes };
}
