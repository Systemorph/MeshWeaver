namespace MeshWeaver.Layout;

/// <summary>
/// Layout control for thread-based chat views that combine markdown editing
/// with reference collection, agent/model selection, and streaming responses.
/// </summary>
public record ThreadChatControl() : UiControl<ThreadChatControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// The path to the thread being viewed/edited.
    /// Set directly when creating the control outside layout areas (side panel, dashboard).
    /// </summary>
    public string? ThreadPath { get; init; }

    /// <summary>
    /// The initial context path for reference chips.
    /// </summary>
    public string? InitialContext { get; init; }

    /// <summary>
    /// The display name for the initial context (for the context chip).
    /// </summary>
    public string? InitialContextDisplayName { get; init; }

    /// <summary>
    /// When true, hides the empty-state placeholder (icon + text) shown when there are no messages.
    /// Useful for compact/embedded chat (e.g., dashboard).
    /// </summary>
    public bool HideEmptyState { get; init; }

    /// <summary>
    /// When true, renders the full-page thread hero header (title, context back-link,
    /// modified-nodes summary, Mark Done) as the FIRST item inside the scrollable
    /// message area, so it scrolls away with the conversation instead of being pinned
    /// above it. Set by the full-page <c>ThreadView</c>; left false for the side panel
    /// (which shows the title in its own chrome).
    /// </summary>
    public bool ShowFullHeader { get; init; }

    /// <summary>
    /// Data-bound thread view model (via JsonPointerReference).
    /// Contains ThreadPath, InitialContext, Messages — all thread state.
    /// Null when control is created directly (side panel, dashboard).
    /// </summary>
    public object? ThreadViewModel { get; init; }

    /// <summary>Returns a copy with <paramref name="threadPath"/> as the mesh path of the thread to display.</summary>
    /// <param name="threadPath">Mesh path of the thread node.</param>
    public ThreadChatControl WithThreadPath(string threadPath) => this with { ThreadPath = threadPath };
    /// <summary>Returns a copy with <paramref name="context"/> as the initial context path for reference chips.</summary>
    /// <param name="context">Mesh path of the initial context node.</param>
    public ThreadChatControl WithInitialContext(string context) => this with { InitialContext = context };
    /// <summary>Returns a copy with <paramref name="displayName"/> as the label shown on the initial context chip.</summary>
    /// <param name="displayName">Human-readable name for the context chip.</param>
    public ThreadChatControl WithInitialContextDisplayName(string displayName) => this with { InitialContextDisplayName = displayName };
    /// <summary>Returns a copy with <paramref name="hide"/> controlling whether the empty-state placeholder is hidden.</summary>
    /// <param name="hide">When <c>true</c>, the icon and text shown for an empty thread are suppressed.</param>
    public ThreadChatControl WithHideEmptyState(bool hide = true) => this with { HideEmptyState = hide };
    /// <summary>Returns a copy with <paramref name="show"/> controlling whether the full-page thread header is rendered inside the scrollable area.</summary>
    /// <param name="show">When <c>true</c>, the hero header scrolls with the conversation.</param>
    public ThreadChatControl WithShowFullHeader(bool show = true) => this with { ShowFullHeader = show };
    /// <summary>Returns a copy with <paramref name="threadViewModel"/> as the data-bound thread view model.</summary>
    /// <param name="threadViewModel">A data-bound thread view model or pointer reference; <c>null</c> for direct-path mode.</param>
    public ThreadChatControl WithThreadViewModel(object? threadViewModel) => this with { ThreadViewModel = threadViewModel };
}
