using MeshWeaver.Data;

namespace MeshWeaver.Layout;

/// <summary>
/// What a clicked control is doing RIGHT NOW, as the owner states it — the server half of every
/// framework button's busy state (<c>Doc/GUI/ButtonPendingState</c>). The owner's
/// <c>LayoutAreaHost</c> writes one of these into the stream's data section under
/// <see cref="DataId(string)"/> the moment a click starts running, updates it as the action reports
/// progress (or as a tracked activity / node moves), and writes the terminal state when the click
/// settles. A client view binds it by <see cref="PointerFor(string)"/>: while
/// <see cref="Running"/> is true the control stays disabled, shows <see cref="Status"/> and
/// <see cref="Fraction"/>, and offers Cancel; on settle it shows <see cref="Error"/> or
/// <see cref="Summary"/> and re-enables.
/// <para>No author code is needed for any of this: every click on a control with a click action
/// gets it. An action that has more to say calls <c>ctx.ReportProgress</c>,
/// <c>ctx.ReportSummary</c>, <c>ctx.Track</c> or (Mesh.Contract) <c>ctx.TrackActivity</c>.</para>
/// </summary>
public record ClickProgress
{
    /// <summary>True from the click until the action — and everything it handed to <c>ctx.Track</c> — settled.</summary>
    public bool Running { get; init; }

    /// <summary>The status line: what is happening now, in the viewer's language. Null means the client shows its own localized "Working…".</summary>
    public string? Status { get; init; }

    /// <summary>Completed fraction in [0, 1] when the work knows it; null renders an indeterminate indicator.</summary>
    public double? Fraction { get; init; }

    /// <summary>Whether a Cancel affordance is offered. True while running: cancelling always disposes the action's own pipeline, trips <c>ctx.CancellationToken</c> and runs every <c>ctx.OnCancel</c> handler.</summary>
    public bool Cancellable { get; init; }

    /// <summary>True once a cancel was requested and is being honoured.</summary>
    public bool Cancelling { get; init; }

    /// <summary>The reason the click failed — shown next to the re-enabled control, never swallowed. Null on success.</summary>
    public string? Error { get; init; }

    /// <summary>The outcome line once settled (e.g. "Approved 5 of 6"), when the action stated one.</summary>
    public string? Summary { get; init; }

    /// <summary>The activity the click is bound to, when it called <c>ctx.TrackActivity</c> — a client may link to it.</summary>
    public string? ActivityPath { get; init; }

    /// <summary>
    /// The area a row-scoped control's click state is keyed by: <paramref name="area"/> qualified by the
    /// row's node path, pointer or index (in that order), so one row's busy state never disables every row
    /// of the template. A row known only by its value (a data grid row) — and a control outside any row —
    /// uses <paramref name="area"/> itself. Clients compute the same with the row they render.
    /// </summary>
    /// <param name="area">The clicked control's area.</param>
    /// <param name="row">The row the control was rendered in, or null.</param>
    public static string RowArea(string area, RowContext? row)
        => row switch
        {
            { Path: { Length: > 0 } path } => area + "#" + path,
            { Pointer: { Length: > 0 } pointer } => area + "#" + pointer,
            { Index: { } index } => area + "#" + index.ToString(System.Globalization.CultureInfo.InvariantCulture),
            _ => area,
        };

    /// <summary>
    /// The data id the click state of <paramref name="area"/> is written under. The area's '/' separators
    /// are folded to '|': a client write (the Cancel request) into a data id that contains '/' does not
    /// reach the owner's value today (measured by ClickBusyStateTest), so the ids carry none.
    /// </summary>
    /// <param name="area">The clicked control's area.</param>
    public static string DataId(string area) => "click_" + area.Replace("/", "|");

    /// <summary>The data id a client writes a cancel request for <paramref name="area"/> to (a <see cref="ClickCancellation"/>).</summary>
    /// <param name="area">The clicked control's area.</param>
    public static string CancelDataId(string area) => "clickCancel_" + area.Replace("/", "|");

    /// <summary>The JSON pointer a client binds the click state of <paramref name="area"/> by.</summary>
    /// <param name="area">The clicked control's area.</param>
    public static string PointerFor(string area) => LayoutAreaReference.GetDataPointer(DataId(area));

    /// <summary>The JSON pointer a client writes <c>requested: true</c> under to cancel the click on <paramref name="area"/>.</summary>
    /// <param name="area">The clicked control's area.</param>
    public static string CancelPointerFor(string area) => LayoutAreaReference.GetDataPointer(CancelDataId(area));
}

/// <summary>
/// The client-written half of a click's control plane: the owner seeds it with
/// <see cref="Requested"/> = false when a click starts, a client flips it to true (Cancel), and the
/// owner watches it — a property write, never a verb message (<c>Doc/Architecture/ActivityControlPlane</c>).
/// </summary>
public record ClickCancellation
{
    /// <summary>Which click this request belongs to — seeded by the owner, never written by the client, so a stale request cannot cancel the next click.</summary>
    public int Session { get; init; }

    /// <summary>True once the viewer asked to cancel the running click.</summary>
    public bool Requested { get; init; }
}
