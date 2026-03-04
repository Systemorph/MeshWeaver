using System.Collections.Immutable;

namespace MeshWeaver.Layout;

/// <summary>
/// Layout control for thread-based chat views that combine markdown editing
/// with reference collection, agent/model selection, and streaming responses.
/// </summary>
public record ThreadChatControl() : UiControl<ThreadChatControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// The path to the thread being viewed/edited.
    /// </summary>
    public string? ThreadPath { get; init; }

    /// <summary>
    /// The initial context path for reference chips.
    /// This is typically the node where the chat was initiated from.
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
    /// Data-bound list of message cells. Each cell is a LayoutAreaControl
    /// pointing to the message node's OverviewArea. Populated server-side
    /// by the thread's layout function.
    /// </summary>
    public ImmutableList<LayoutAreaControl> Cells { get; init; } = ImmutableList<LayoutAreaControl>.Empty;

    public ThreadChatControl WithThreadPath(string threadPath) => this with { ThreadPath = threadPath };
    public ThreadChatControl WithInitialContext(string context) => this with { InitialContext = context };
    public ThreadChatControl WithInitialContextDisplayName(string displayName) => this with { InitialContextDisplayName = displayName };
    public ThreadChatControl WithHideEmptyState(bool hide = true) => this with { HideEmptyState = hide };
    public ThreadChatControl WithCells(ImmutableList<LayoutAreaControl> cells) => this with { Cells = cells };
}
