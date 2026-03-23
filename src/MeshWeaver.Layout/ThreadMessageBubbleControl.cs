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

    public ThreadMessageBubbleControl WithRole(string role) => this with { Role = role };
    public ThreadMessageBubbleControl WithAuthorName(string name) => this with { AuthorName = name };
    public ThreadMessageBubbleControl WithText(object? text) => this with { Text = text };
    public ThreadMessageBubbleControl WithIsExecuting(object? isExecuting) => this with { IsExecuting = isExecuting };
    public ThreadMessageBubbleControl WithExecutionStatus(object? status) => this with { ExecutionStatus = status };
    public ThreadMessageBubbleControl WithThreadPath(string? path) => this with { ThreadPath = path };
}
