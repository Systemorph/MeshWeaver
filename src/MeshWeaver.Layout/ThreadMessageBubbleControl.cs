namespace MeshWeaver.Layout;

/// <summary>
/// Layout control for rendering a single thread message as a chat bubble.
/// The Blazor view renders the bubble with role-based styling (user/assistant)
/// and data-binds the text content so only text updates during streaming.
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

    public ThreadMessageBubbleControl WithRole(string role) => this with { Role = role };
    public ThreadMessageBubbleControl WithAuthorName(string name) => this with { AuthorName = name };
    public ThreadMessageBubbleControl WithText(object? text) => this with { Text = text };
}
