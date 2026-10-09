using MeshWeaver.Layout.Composition;
using MeshWeaver.Messaging;

namespace MeshWeaver.Layout;

/// <summary>
/// Represents the context for a UI action, including the area, payload, message hub, and layout area host.
/// </summary>
/// <param name="Area">The area where the UI action is performed.</param>
/// <param name="Payload">The payload associated with the UI action.</param>
/// <param name="Hub">The message hub for handling messages related to the UI action.</param>
/// <param name="Host">The layout area host associated with the UI action.</param>
public record UiActionContext(string Area, object? Payload, IMessageHub Hub, LayoutAreaHost Host)
{
    /// <summary>
    /// The row this action was raised from, when its control is declared inside a bound row template
    /// (a <c>BindMany</c> list, a data grid's template column) — the row as the client rendered it.
    /// Null for a control outside any row. Read the value with <c>RowAs&lt;T&gt;()</c> and a node
    /// row's path with <see cref="UiActionContextExtensions.RowPath"/>; see <see cref="RowContext"/>
    /// for why it is the rendered row and never a re-resolved index.
    /// </summary>
    public RowContext? Row { get; init; }

    /// <summary>
    /// The running click this action belongs to, when it was raised by a click (null for a blur, a
    /// dialog close, or a context built by hand). Carries the busy state the framework shows for every
    /// click — see <c>Doc/GUI/ButtonPendingState</c>.
    /// </summary>
    internal ClickSession? Session { get; init; }

    /// <summary>
    /// Trips when the viewer presses Cancel on this click. Pass it to work that honours a token; work
    /// that is cancelled another way registers that way with <see cref="UiActionContextExtensions.OnCancel"/>.
    /// Settling the click, or the viewer leaving the page, does NOT trip it. <see cref="System.Threading.CancellationToken.None"/>
    /// when the action was not raised by a click.
    /// </summary>
    public CancellationToken CancellationToken => Session?.Token ?? CancellationToken.None;
}

/// <summary>
/// Extension methods for UiActionContext.
/// </summary>
public static class UiActionContextExtensions
{
    /// <summary>
    /// The mesh path of the row this action was raised from (<see cref="RowContext.NodePath"/>), or
    /// null when the control is not in a row or the row is not a node.
    /// </summary>
    /// <param name="context">The UI action context.</param>
    /// <returns>The row's node path, or null.</returns>
    public static string? RowPath(this UiActionContext context) => context.Row?.NodePath();

    /// <summary>
    /// Updates the status line (and, when known, the completed fraction) the clicked control shows
    /// while this click runs — "what is happening". Text is shown as given: pass it localized
    /// (<c>ctx.Host.Localize(…)</c>). Safe from any thread; a no-op once the click settled or when the
    /// action was not raised by a click.
    /// </summary>
    /// <param name="context">The click's action context.</param>
    /// <param name="status">The status line; null keeps the current one.</param>
    /// <param name="fraction">Completed fraction in [0, 1]; null keeps the current one (indeterminate when never set).</param>
    public static void ReportProgress(this UiActionContext context, string? status, double? fraction = null)
        => context.Session?.ReportProgress(status, fraction);

    /// <summary>
    /// States the click's outcome line (e.g. "Approved 5 of 6 — 1 failed: …"), shown next to the control
    /// once the click settled. Pass it localized.
    /// </summary>
    /// <param name="context">The click's action context.</param>
    /// <param name="summary">The outcome line.</param>
    public static void ReportSummary(this UiActionContext context, string summary)
        => context.Session?.ReportSummary(summary);

    /// <summary>
    /// Registers what Cancel must do beyond what the framework already does (dispose the action's own
    /// pipeline, trip <see cref="UiActionContext.CancellationToken"/>) — e.g. patch an activity's
    /// <c>RequestedStatus</c>. Runs on the owner's hub as the clicking user.
    /// </summary>
    /// <param name="context">The click's action context.</param>
    /// <param name="onCancel">The cancel handler.</param>
    public static void OnCancel(this UiActionContext context, Action onCancel)
        => context.Session?.OnCancel(onCancel);

    /// <summary>
    /// Keeps the clicked control busy over <paramref name="progress"/> — work the click STARTED and
    /// that outlives the action itself. Each emission updates the status line, fraction, summary or
    /// activity link (null fields keep the current value); an emission with <see cref="ClickProgress.Running"/>
    /// false, or completion, ends the tracked work; an emission carrying <see cref="ClickProgress.Error"/>,
    /// or an error, fails the click and shows the reason. The click settles — and the control re-enables —
    /// when the action AND every tracked source settled. A duplicate click meanwhile never re-runs the action.
    /// </summary>
    /// <param name="context">The click's action context.</param>
    /// <param name="progress">The work's progress, as the click state to show.</param>
    /// <param name="followThroughCancel">True when the source itself reports the end of a cancel (an activity
    /// reaching Cancelled), so Cancel keeps watching it; false (default) disposes it on Cancel.</param>
    public static void Track(this UiActionContext context, IObservable<ClickProgress> progress, bool followThroughCancel = false)
        => context.Session?.Track(progress, followThroughCancel);

    /// <summary>
    /// Navigates to the specified URI by posting a NavigationRequest to the portal.
    /// Safe to call from click handlers and other UI action contexts.
    /// </summary>
    /// <param name="context">The UI action context.</param>
    /// <param name="uri">The URI to navigate to.</param>
    /// <param name="forceLoad">Whether to force a full page reload.</param>
    /// <param name="replace">Whether to replace the current history entry instead of adding a new one.</param>
    public static void NavigateTo(this UiActionContext context, string uri, bool forceLoad = false, bool replace = false)
    {
        context.Host.NavigateTo(uri, forceLoad, replace);
    }

    /// <summary>
    /// Opens the URI in the portal's side panel, leaving the page the viewer is on in place —
    /// see <see cref="Composition.LayoutAreaHost.NavigateToSidePanel"/> for when to prefer this
    /// over <see cref="NavigateTo"/>.
    /// </summary>
    /// <param name="context">The action context of the click.</param>
    /// <param name="uri">The URI to open in the side panel.</param>
    public static void NavigateToSidePanel(this UiActionContext context, string uri)
    {
        context.Host.NavigateToSidePanel(uri);
    }
}
