namespace MeshWeaver.Layout;

/// <summary>
/// Static, paths-only <see cref="UiControl"/> that represents ONE item in the
/// chat history's <see cref="ItemTemplateControl"/>. The control itself carries
/// no concrete message content — the Blazor view subscribes to
/// <c>IMeshNodeStreamCache.GetStream(MessagePath)</c> and renders text +
/// tool calls + status directly from the live <c>MeshNode.Content</c>.
///
/// <para>This is the canonical "list of MeshNodes" rendering shape:</para>
/// <list type="bullet">
///   <item>Backend layout area emits ONE <see cref="ItemTemplateControl"/>
///   whose <c>Data</c> is the message-id list and whose <c>View</c> is a
///   <see cref="ThreadMessageItemControl"/> with the per-item path bound via
///   <c>JsonPointerReference</c>.</item>
///   <item>Per-item Blazor view holds ONE cache subscription and dispatches
///   in-Razor to either an input or output sub-render based on the bound
///   <c>ThreadMessage.Role</c>. No per-message round-trip through the
///   per-node hub.</item>
/// </list>
///
/// <para>Pattern reference: <c>Doc/GUI/ItemTemplateMeshNodeStreamBinding</c>.</para>
///
/// <para><b>Pending fallback.</b> If the user has just submitted and the
/// satellite cell hasn't materialised yet, the backend layout area can ship
/// the typed text in <see cref="PendingText"/>. The view renders that
/// immediately and swaps to the live cache emission when the cell appears —
/// the same Blazor component handles both states.</para>
/// </summary>
public record ThreadMessageItemControl() : UiControl<ThreadMessageItemControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// Path of the <c>ThreadMessage</c> MeshNode this item renders. The Blazor
    /// view opens <c>IMeshNodeStreamCache.GetStream(MessagePath)</c> on this
    /// path and re-renders on every emission. <see cref="object"/>-typed so the
    /// backend can pass either a concrete <c>string</c> (direct constructor)
    /// or a <c>JsonPointerReference</c> (inside an <see cref="ItemTemplateControl"/>
    /// where each item resolves its own path from the data section).
    /// </summary>
    public object? MessagePath { get; init; }

    /// <summary>
    /// Fallback text shown before the satellite cell at <see cref="MessagePath"/>
    /// materialises — used for "instant render of pending user input" so the
    /// user sees their typed text immediately without waiting for cell creation.
    /// The Blazor view renders <c>PendingText</c> as a placeholder; the first
    /// cache emission with non-null <c>Content</c> swaps it for the live
    /// <c>ThreadMessage.Text</c> without a flicker.
    /// </summary>
    public object? PendingText { get; init; }

    public ThreadMessageItemControl WithMessagePath(object? path) => this with { MessagePath = path };
    public ThreadMessageItemControl WithPendingText(object? text) => this with { PendingText = text };
}
