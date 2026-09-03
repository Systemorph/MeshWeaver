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
    /// When true, renders the collapsible THREADS side menu (new chat · searchable list of the
    /// viewer's open threads with live evaluating/queued/awaiting status) beside the chat even
    /// when no thread is open yet — the Threads app page (<c>/{user}/Chat</c>) sets this so the
    /// node-less composer carries the same default navigation as every thread page. Thread pages
    /// show the menu implicitly (<see cref="ShowFullHeader"/> + a real thread); the side panel
    /// and the home composer leave both off.
    /// </summary>
    public bool ShowThreadNav { get; init; }

    /// <summary>
    /// A draft the SERVER declares for the composer — text the reader finds already typed in, and
    /// is free to edit or clear before sending.
    ///
    /// <para>The first caller is the markdown <c>```prompt</c> fence (#2511): a page author's
    /// suggested prompt becomes the initial draft, the reader edits it in place, and Submit starts
    /// a real thread. It exists because that composer is RENDERED rather than opened by a click, so
    /// there is no interaction to carry a draft in on.</para>
    ///
    /// <para>🚨 Seeding it is the CLIENT's decision and must be one-shot: a composer the user has
    /// deliberately emptied must not find the prompt typed back in on the next render. The control
    /// only declares the text; it says nothing about when it is applied.</para>
    /// </summary>
    public string? InitialDraft { get; init; }

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
    /// <summary>Returns a copy with <paramref name="show"/> controlling whether the collapsible threads side menu renders even without an open thread.</summary>
    /// <param name="show">When <c>true</c>, the threads side menu renders beside the chat.</param>
    public ThreadChatControl WithShowThreadNav(bool show = true) => this with { ShowThreadNav = show };
    /// <summary>Returns a copy with <paramref name="draft"/> as the text the composer opens pre-filled with.</summary>
    /// <param name="draft">The draft text; <c>null</c> or empty leaves the composer empty.</param>
    public ThreadChatControl WithInitialDraft(string? draft) => this with { InitialDraft = draft };
    /// <summary>Returns a copy with <paramref name="threadViewModel"/> as the data-bound thread view model.</summary>
    /// <param name="threadViewModel">A data-bound thread view model or pointer reference; <c>null</c> for direct-path mode.</param>
    public ThreadChatControl WithThreadViewModel(object? threadViewModel) => this with { ThreadViewModel = threadViewModel };
}
