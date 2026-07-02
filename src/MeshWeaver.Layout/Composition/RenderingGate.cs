#nullable enable
using MeshWeaver.Data;

namespace MeshWeaver.Layout.Composition;

/// <summary>
/// The status source for STATUS-GATED EMERGENCY-MODE RENDERING (see
/// <see cref="ActivityStatusExtensions"/> for the underlying law). Configured once per hub via
/// <see cref="LayoutDefinition.WithRenderingGate"/>; the <see cref="LayoutAreaHost"/> subscribes it
/// on initialization and routes EVERY area of that host through the emitted state:
///
/// <list type="bullet">
///   <item>ONLY a SUCCESS status (<see cref="ActivityStatusExtensions.IsSuccess"/>) invokes the
///         registered renderers — the code paths that read the real typed content
///         (<c>ContentAs&lt;T&gt;</c> / <c>Content is X</c>). A failed or still-loading node's content
///         may be absent, partial, or untyped; typing it yields <c>null</c> and the area renders
///         empty or a reactive wait times out (the "renders empty / secretly-errors-as-timeout"
///         wedge class).</item>
///   <item>An ERROR status (<see cref="ActivityStatusExtensions.IsError"/> — failed or cancelled)
///         short-circuits to EMERGENCY MODE: the area renders a visible error control carrying
///         <see cref="RenderingGateState.ErrorMessage"/>. The error IS the rendered output —
///         never a hang, never an empty render.</item>
///   <item>Missing configuration (<see cref="RenderingGateState.NoConfig"/> — content absent or
///         not type-resolvable where the layout requires it) is emergency mode too.</item>
///   <item>A still-<see cref="ActivityStatus.Running"/> status renders nothing yet — the
///         progress channel stays up and the terminal transition (which the status law
///         guarantees) re-fires the gate into one of the two branches above.</item>
/// </list>
/// </summary>
/// <param name="host">The layout-area host whose rendering is being gated.</param>
/// <returns>The live status stream; each emission re-evaluates the gate.</returns>
public delegate IObservable<RenderingGateState> RenderingGate(LayoutAreaHost host);

/// <summary>
/// One emission of a <see cref="RenderingGate"/>: the content status driving the render decision,
/// per the total success-XOR-error law pinned by <see cref="ActivityStatusExtensions"/>.
/// </summary>
public sealed record RenderingGateState
{
    /// <summary>
    /// The content status. <see cref="ActivityStatusExtensions.IsSuccess"/> → normal (typed) render;
    /// <see cref="ActivityStatusExtensions.IsError"/> → emergency mode; <see cref="ActivityStatus.Running"/>
    /// → not rendered yet (progress stays up until the terminal transition).
    /// </summary>
    public ActivityStatus Status { get; init; } = ActivityStatus.Running;

    /// <summary>The error/cancellation detail rendered by the emergency frame; null when none.</summary>
    public string? ErrorMessage { get; init; }

    /// <summary>
    /// True when the emergency is "no configuration available" (content missing or untyped where
    /// the layout requires it) rather than an error-status content.
    /// </summary>
    public bool IsConfigMissing { get; init; }

    /// <summary>The content is usable — renderers run and may read the typed content.</summary>
    public static RenderingGateState Success() => new() { Status = ActivityStatus.Succeeded };

    /// <summary>The content is not ready yet — nothing renders, the progress channel stays up.</summary>
    public static RenderingGateState Pending() => new() { Status = ActivityStatus.Running };

    /// <summary>The content is in an error state — emergency mode with <paramref name="errorMessage"/>.</summary>
    /// <param name="errorMessage">The error detail shown in the emergency frame.</param>
    public static RenderingGateState Failed(string errorMessage) =>
        new() { Status = ActivityStatus.Failed, ErrorMessage = errorMessage };

    /// <summary>The producing operation was cancelled — emergency mode.</summary>
    /// <param name="reason">Optional cancellation detail shown in the emergency frame.</param>
    public static RenderingGateState Cancelled(string? reason = null) =>
        new() { Status = ActivityStatus.Cancelled, ErrorMessage = reason };

    /// <summary>
    /// No configuration is available (content missing or untyped where the layout requires it) —
    /// emergency mode. Modeled as an error-class status so the success-XOR-error law stays total.
    /// </summary>
    /// <param name="detail">What was expected and not found (shown in the emergency frame).</param>
    public static RenderingGateState NoConfig(string detail) =>
        new() { Status = ActivityStatus.Failed, ErrorMessage = detail, IsConfigMissing = true };

    /// <summary>Maps a raw content status (e.g. an <see cref="ActivityLog.Status"/>) to a gate state.</summary>
    /// <param name="status">The content status.</param>
    /// <param name="errorMessage">Optional error detail for the emergency frame.</param>
    public static RenderingGateState FromStatus(ActivityStatus status, string? errorMessage = null) =>
        new() { Status = status, ErrorMessage = errorMessage };
}
