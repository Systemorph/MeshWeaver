namespace MeshWeaver.Layout;

/// <summary>
/// Static, paths-only <see cref="UiControl"/> for ONE delegation
/// <see cref="ToolCallEntry"/> card on a parent <c>ThreadMessage</c>.
///
/// The Blazor view opens TWO concurrent <c>IMeshNodeStreamCache.GetStream</c>
/// subscriptions:
/// <list type="number">
///   <item><c>cache.GetStream(MessagePath)</c> — the parent's response cell.
///     Provides the matching <see cref="ToolCallEntry"/> (filtered by
///     <see cref="DelegationPath"/>), which carries the LIVE
///     <see cref="ToolCallStatus"/> and the last-10-lines projection in
///     <c>Result</c>. Updated by the parent's delegation watcher in
///     <c>ChatClientAgentFactory.ExecuteDelegationAsync</c>.</item>
///   <item><c>cache.GetStream(DelegationPath)</c> — the sub-thread itself.
///     Provides the sub-thread's <c>Name</c> for the card title. Live: a
///     rename on the sub-thread propagates to the card without a refresh.</item>
/// </list>
///
/// Both subscriptions share the process-wide <c>IMeshNodeStreamCache</c> upstream
/// handles — no extra cost beyond two downstream subscribers on already-cached
/// streams. The whole card is wrapped in an <c>&lt;a href="/{DelegationPath}"&gt;</c>
/// so clicking navigates to the sub-thread URL.
///
/// <para>Used inline by the output branch of <see cref="ThreadMessageItemControl"/>'s
/// Blazor view when iterating <c>msg.ToolCalls</c>: entries with non-null
/// <see cref="ToolCallEntry.DelegationPath"/> render as cards; non-delegation
/// tool calls keep their existing chip rendering.</para>
///
/// <para>Pattern reference: <c>Doc/GUI/ItemTemplateMeshNodeStreamBinding</c>.</para>
/// </summary>
public record DelegationToolCallCardControl()
    : UiControl<DelegationToolCallCardControl>(ModuleSetup.ModuleName, ModuleSetup.ApiVersion)
{
    /// <summary>
    /// Path of the parent <c>ThreadMessage</c> response cell. The Blazor view
    /// subscribes here to find the matching <see cref="ToolCallEntry"/>
    /// (the one whose <see cref="ToolCallEntry.DelegationPath"/> equals this
    /// control's <see cref="DelegationPath"/>) and reads its live
    /// <see cref="ToolCallStatus"/> / <see cref="ToolCallEntry.Result"/>.
    /// </summary>
    public object? MessagePath { get; init; }

    /// <summary>
    /// Path of the sub-thread this delegation dispatched. Used by the Blazor
    /// view for two purposes: (1) the second cache subscription that reads
    /// the sub-thread's <c>Name</c> for the card title, and (2) the click
    /// target — the whole card is wrapped in <c>&lt;a href="/{DelegationPath}"&gt;</c>.
    /// </summary>
    public object? DelegationPath { get; init; }

    public DelegationToolCallCardControl WithMessagePath(object? path) => this with { MessagePath = path };
    public DelegationToolCallCardControl WithDelegationPath(object? path) => this with { DelegationPath = path };
}
