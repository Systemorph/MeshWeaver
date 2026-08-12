using System.Collections.Immutable;
using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Overview and Thumbnail views for individual Activity nodes.
/// Registered via ActivityNodeType's AddActivityViews().
/// </summary>
public static class ActivityLayoutAreas
{
    /// <summary>Area name for the Overview layout area.</summary>
    public const string OverviewArea = "Overview";
    /// <summary>Area name for the Thumbnail layout area.</summary>
    public const string ThumbnailArea = "Thumbnail";
    /// <summary>Area name for the Cancel layout area.</summary>
    public const string CancelArea = "Cancel";

    /// <summary>
    /// The Cancel button is visible if-and-only-if the activity is currently
    /// running AND no cancel request is already in flight. Centralised so the
    /// three layout-area sites (Overview, Progress, CancelButton) share one
    /// rule, and so tests can pin the contract without spinning up a full
    /// layout-area host.
    /// </summary>
    public static bool IsCancelButtonVisible(ActivityLog log) =>
        log.Status == ActivityStatus.Running
        && log.RequestedStatus != ActivityStatus.Cancelled;

    /// <summary>
    /// Registers the Activity-specific views (Overview, Thumbnail, Progress, Cancel).
    /// </summary>
    public static MessageHubConfiguration AddActivityViews(this MessageHubConfiguration configuration)
        => configuration
            .AddLayout(layout => layout
                .WithDefaultArea(OverviewArea)
                .WithView(OverviewArea, Overview)
                .WithView(ThumbnailArea, Thumbnail)
                .WithView(ProgressArea, Progress)
                .WithView(CancelArea, CancelButton));

    /// <summary>Area name for the Progress layout area.</summary>
    public const string ProgressArea = "Progress";

    /// <summary>
    /// Area id of the script RESULT inside <see cref="Progress"/> / <see cref="Overview"/> —
    /// the control a script returned, rendered live. Named (not auto-numbered) so the
    /// indicator and log keep their positions and so tests address it by name.
    /// </summary>
    public const string ResultArea = "Result";

    /// <summary>
    /// The control a script RETURNED, as a live stream — the missing half of #915.
    /// <para>A script's return value reaches the reader by exactly one route: the kernel
    /// publishes it into the hub's area dictionary (<c>KernelExecutor.UpdateView</c>, keyed by
    /// the submission id = this activity node's id) and a layout host renders it from there.
    /// It CANNOT be recovered from <see cref="ActivityLog.ReturnValue"/>: a container control
    /// serializes as bare <c>NamedAreaControl</c> references — its children live in the
    /// non-serialized <c>Views</c>/<c>Renderers</c> — so the stored JSON is hollow.</para>
    /// <para>Only an <see cref="IUiControl"/> is rendered. Everything else the kernel already
    /// logs as a text line (<c>1 + 1</c> ⇒ "2"), and rendering it here as well would print
    /// every scalar result twice.</para>
    /// <para>Emits <c>null</c> first and whenever the dictionary has nothing for this activity —
    /// the hub is re-activated empty after its idle disconnect, so an OLD run degrades to the
    /// status line + log it renders today, never to a pane that waits forever.</para>
    /// </summary>
    private static IObservable<UiControl?> RenderedResult(LayoutAreaHost host)
    {
        // The kernel's area dictionary is a hub-scoped service registered by KernelContainer.
        // Absent ⇒ this activity's hub hosts no kernel (a compile, an import, a sync) ⇒ no result.
        var areas = host.Hub.ServiceProvider
            .GetService<ISynchronizationStream<ImmutableDictionary<string, object>>>();
        if (areas is null) return Observable.Return<UiControl?>(null);

        // The submission id IS the activity node's id (CodeNodeType.HandleExecuteScript stamps
        // `activityId = submissionId`), and the hub's address is that node's path.
        var submissionId = host.Hub.Address.Segments[^1];
        var controls = host.Hub.ServiceProvider.GetRequiredService<IUiControlService>();
        // Project to THIS submission's entry and dedupe before converting: the dictionary is
        // hub-wide, so an unrelated submission's update must not re-render this pane (or pay for
        // another Convert). The seed makes the dedupe cover the initial null too, so a hub whose
        // dictionary is simply empty emits exactly one.
        return areas
            .Select(change => change.Value?.GetValueOrDefault(submissionId))
            .StartWith((object?)null)
            .DistinctUntilChanged()
            .Select(value => value is IUiControl ? controls.Convert(value!) : null);
    }

    /// <summary>
    /// Overview for an Activity node. Header (user / category / status / timestamps),
    /// followed by the live progress indicator (indeterminate bar while running, a
    /// status line once terminal), the structured message log (per-message rows
    /// with log-level colour coding) and the script's rendered result, plus a Cancel
    /// button (while running) and a Re-run button (once terminal, when the activity
    /// originated from an executable hub). Built entirely from framework controls —
    /// no hand-rolled HTML.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var zoneId = host.Hub.ServiceProvider.GetService<AccessService>().ViewerZoneId();
        return host.Workspace.GetMeshNodeStream()
            .CombineLatest(RenderedResult(host), (node, result) =>
            {
                if (node?.Content is not ActivityLog log)
                    return (UiControl?)Controls.Label(host.Localize("ui.noActivityData"))
                        .WithStyle("font-style: italic; color: var(--neutral-foreground-hint);");

                var stack = Controls.Stack
                    .WithStyle("padding: 16px; gap: 12px;")
                    .WithView(BuildHeader(log, zoneId))
                    .WithView(BuildProgressIndicator(log))
                    .WithView(BuildLog(log, locale: host.ViewerLocale(), hasResult: result is not null));
                if (result is not null)
                    stack = stack.WithView(result, ResultArea);

                // While running: Cancel button. Per the Activity Control Plane
                // pattern (Doc/Architecture/ActivityControlPlane.md), cancellation
                // is a property patch on the activity's content — NOT a separate
                // CancelXRequest message. The activity hub's own watcher
                // (KernelContainer.StartActivityControlPlane) translates the
                // RequestedStatus = Cancelled patch into the internal cancel.
                if (IsCancelButtonVisible(log))
                {
                    stack = stack.WithView(Controls.Button(host.Localize("common.cancel"))
                        .WithIconStart(FluentIcons.Dismiss())
                        .WithClickAction(ctx =>
                        {
                            ctx.Host.Hub.CancelActivity(ctx.Host.Hub.Address.ToString());
                            return Task.CompletedTask;
                        }));
                }
                // Re-run button when the activity is finished and the originating
                // hub is known. Click posts ExecuteScriptRequest back to the
                // originating hub, which creates a fresh sibling Activity.
                else if (!string.IsNullOrEmpty(log.HubPath) && log.Status != ActivityStatus.Running)
                {
                    var originAddress = new Address(log.HubPath);
                    stack = stack.WithView(Controls.Button(host.Localize("ui.rerun"))
                        .WithIconStart(FluentIcons.ArrowRotateClockwise())
                        .WithAppearance(Appearance.Accent)
                        .WithClickAction(ctx =>
                        {
                            ctx.Host.Hub.Post(new ExecuteScriptRequest(),
                                o => o.WithTarget(originAddress));
                            return Task.CompletedTask;
                        }));
                }

                return (UiControl?)stack;
            });
    }

    /// <summary>
    /// Standalone Cancel-button view. Renders just the button (no log, no header).
    /// While the activity is running, click patches <c>RequestedStatus = Cancelled</c>
    /// per the <see href="xref:Architecture/ActivityControlPlane">Activity Control Plane</see>
    /// pattern; once terminal, renders nothing.
    ///
    /// <para>Embed in interactive markdown via <c>--render Cancel</c> (when
    /// rendered within an activity's own layout) or as
    /// <c>Controls.NamedArea(activityAddress, ActivityLayoutAreas.CancelArea)</c>
    /// when embedding from another hub's layout.</para>
    /// </summary>
    public static IObservable<UiControl?> CancelButton(LayoutAreaHost host, RenderingContext _)
    {
        return host.Workspace.GetMeshNodeStream()
            .Select(node =>
            {
                if (node?.Content is not ActivityLog log) return null;
                if (log.Status != ActivityStatus.Running) return null;
                var disabled = log.RequestedStatus == ActivityStatus.Cancelled;
                var button = Controls.Button(host.Localize("common.cancel"))
                    .WithIconStart(FluentIcons.Dismiss())
                    .WithStyle(disabled ? "opacity: 0.5;" : "");
                if (!disabled)
                {
                    button = button.WithClickAction(ctx =>
                    {
                        ctx.Host.Hub.CancelActivity(ctx.Host.Hub.Address.ToString());
                        return Task.CompletedTask;
                    });
                }
                return (UiControl?)button;
            });
    }

    /// <summary>
    /// Compact running-progress view for embedding next to an executable Code
    /// node (or anywhere a caller wants live script feedback). Streams the same
    /// ActivityLog content as <see cref="Overview"/> but trims chrome and shows
    /// only the live progress indicator + message log + the script's rendered
    /// result + inline Cancel button (while running). No header, no Re-run.
    /// Built from framework controls.
    /// <para>This IS the code cell's output pane (<c>CodeLayoutAreas.BuildContent</c>
    /// embeds it), so a script whose result is a control renders that control HERE —
    /// see <see cref="RenderedResult"/>. Order follows a notebook cell: status,
    /// printed lines, then the result.</para>
    /// </summary>
    public static IObservable<UiControl?> Progress(LayoutAreaHost host, RenderingContext _)
    {
        return host.Workspace.GetMeshNodeStream()
            .CombineLatest(RenderedResult(host), (node, result) =>
            {
                if (node?.Content is not ActivityLog log)
                    return (UiControl?)Controls.Label(host.Localize("ui.noActivityYet"))
                        .WithStyle("font-style: italic; color: var(--neutral-foreground-hint);");

                var stack = Controls.Stack.WithStyle("gap: 8px;");
                // The status line renders only while it carries information the pane doesn't
                // already show: progress while running, the failure while failed, the explicit
                // "done, no output" when there is nothing else. A successful run WITH a result
                // shows the result alone — its presence IS the success, and the cell toolbar
                // carries the idle/done state (2026-08-12 UX feedback: "✓ Done" as a heading
                // above the rendered result read as chrome on top of the actual output).
                if (ShowsStatusLine(log, hasResult: result is not null))
                    stack = stack.WithView(BuildProgressIndicator(log, host.ViewerLocale()));
                stack = stack.WithView(BuildLog(log, locale: host.ViewerLocale(), hasResult: result is not null));
                if (result is not null)
                    stack = stack.WithView(result, ResultArea);

                // Inline Cancel: same content-patch pattern as the Overview's
                // button. Only rendered while the activity is actually running
                // and not already cancelling.
                if (IsCancelButtonVisible(log))
                {
                    stack = stack.WithView(Controls.Button(host.Localize("common.cancel"))
                        .WithIconStart(FluentIcons.Dismiss())
                        .WithClickAction(ctx =>
                        {
                            ctx.Host.Hub.CancelActivity(ctx.Host.Hub.Address.ToString());
                            return Task.CompletedTask;
                        }));
                }
                return (UiControl?)stack;
            });
    }

    /// <summary>
    /// The activity header row: the triggering user (bold), a status badge
    /// coloured by <see cref="ActivityStatus"/>, and a right-aligned timestamp
    /// hint (started / ended). Control-based — replaces the former hand-rolled
    /// header HTML.
    /// </summary>
    public static StackControl BuildHeader(ActivityLog log, string? zoneId = null)
    {
        var userName = log.User?.DisplayName ?? log.User?.Email ?? "System";
        // Activity timestamps are stored UTC; the header is the one place a user reads
        // "when did this run", so it renders in THEIR zone.
        var startStr = DisplayTimeExtensions.ToDisplayTime(log.Start, zoneId).ToString("g");
        var endStr = log.End is { } end ? DisplayTimeExtensions.ToDisplayTime(end, zoneId).ToString("g") : "—";

        return Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(12)
            .WithStyle("align-items: baseline; flex-wrap: wrap;")
            .WithView(Controls.Label(userName)
                .WithStyle("font-weight: 600; font-size: 1rem;"))
            .WithView(Controls.Label($"{log.Category} · {log.Status}")
                .WithStyle(
                    "font-size: 0.85rem; padding: 2px 8px; border-radius: 10px; "
                    + $"color: {StatusColor(log.Status)}; "
                    + $"background: color-mix(in srgb, {StatusColor(log.Status)} 12%, transparent);"))
            .WithView(Controls.Label($"started {startStr} · ended {endStr}")
                .WithStyle("font-size: 0.8rem; color: var(--neutral-foreground-hint); margin-left: auto;"));
    }

    /// <summary>
    /// Whether the output pane renders a status line for this run. True while the line carries
    /// information the pane does not otherwise show: any RUNNING activity (the live progress),
    /// any non-success (the failure must stay loud, result or not), and a success WITHOUT a
    /// rendered result (the explicit "done" is then the only feedback). False for the one case
    /// the line was chrome: a SUCCEEDED run whose result renders right below — the result is the
    /// success, and the cell toolbar carries the idle state (<c>CodeLayoutAreas.CellStatusChip</c>).
    /// Pure — this is the contract, so it is pinned without a layout host.
    /// </summary>
    /// <param name="log">The activity log the pane renders.</param>
    /// <param name="hasResult">Whether the run's rendered result is present beside this log.</param>
    public static bool ShowsStatusLine(ActivityLog log, bool hasResult)
        => log.Status != ActivityStatus.Succeeded || !hasResult;

    /// <summary>
    /// The activity's progress indicator — the "progress" of the generic activity GUI.
    /// While <see cref="ActivityStatus.Running"/> it is an INDETERMINATE (animated)
    /// <see cref="ProgressControl"/> whose message is the latest log line (or
    /// "Running…" if none yet). Once terminal it is a coloured status line
    /// (✓ Done / ✗ Failed / ⚠ Completed with warnings / Cancelled) preceded by the
    /// final message. Passing <c>null</c> as the progress value is what drives the
    /// indeterminate FluentProgress in <c>ProgressView.razor</c>.
    /// </summary>
    /// <param name="log">The activity log to summarize.</param>
    /// <param name="locale">Viewer locale for the status words; null falls back to English.</param>
    public static UiControl BuildProgressIndicator(ActivityLog log, string? locale = null)
    {
        var latest = log.Messages.Count > 0 ? log.Messages[^1].Message : null;

        if (log.Status == ActivityStatus.Running)
        {
            // Indeterminate bar: progress == null → animated FluentProgress.
            return Controls.Progress((object?)latest ?? LocalizationCatalog.Get("ui.running", locale), null!)
                .WithWidth("100%")
                .WithHideNumber(true)
                .WithMessagePosition(MessagePosition.Top);
        }

        var (glyph, label) = StatusGlyph(log.Status, locale);

        var text = string.IsNullOrEmpty(latest) ? $"{glyph} {label}" : $"{latest}\n{glyph} {label}";
        return Controls.H4(text)
            .WithStyle($"color: {StatusColor(log.Status)}; white-space: pre-wrap; margin: 0;");
    }

    /// <summary>
    /// The glyph + localized word for a TERMINAL activity status — the one vocabulary every
    /// surface (output pane, cell toolbar) uses, so "Done" cannot drift from "✓". Pure.
    /// </summary>
    /// <param name="status">The activity status to name.</param>
    /// <param name="locale">Viewer locale; null falls back to English.</param>
    public static (string Glyph, string Label) StatusGlyph(ActivityStatus status, string? locale = null)
        => status switch
        {
            ActivityStatus.Succeeded => ("✓", LocalizationCatalog.Get("ui.statusDone", locale)),
            ActivityStatus.Failed    => ("✗", LocalizationCatalog.Get("ui.statusFailed", locale)),
            ActivityStatus.Warning   => ("⚠", LocalizationCatalog.Get("ui.statusWarnings", locale)),
            ActivityStatus.Cancelled => ("⊘", LocalizationCatalog.Get("ui.statusCancelled", locale)),
            _                        => ("", status.ToString()),
        };

    /// <summary>
    /// The activity log — one row per <see cref="LogMessage"/>: a fixed-width
    /// level tag (INFO / WARN / ERROR / DBG, coloured by severity) beside the
    /// message text. An empty log on a RUNNING activity renders a single
    /// "Running…" row; an empty log on a TERMINAL activity says explicitly that
    /// the run produced no output (#915 — a script whose result is a control
    /// logs nothing, and "Running…" beside a "✓ Done" header read as a
    /// contradiction the reader had to decode as failure). Control-based
    /// (a vertical <see cref="StackControl"/> of horizontal rows) — replaces
    /// the former hand-rolled messages HTML and is unit-testable without a
    /// layout host.
    /// </summary>
    /// <param name="log">The activity log to render.</param>
    /// <param name="locale">Viewer locale for the empty-log line.</param>
    /// <param name="hasResult">
    /// True when the caller is rendering the script's returned control beside this log
    /// (<see cref="ResultArea"/>). The log is then empty because the run's output IS that
    /// control — printing "this run produced no output" above it would contradict it.
    /// </param>
    public static StackControl BuildLog(ActivityLog log, string? locale = null, bool hasResult = false)
    {
        var stack = Controls.Stack
            .WithStyle(
                "font-family: var(--font-monospace, ui-monospace, monospace); "
                + "font-size: .85rem; gap: 2px; max-height: 320px; overflow: auto;");

        if (log.Messages.Count == 0)
            return hasResult ? stack : stack.WithView(BuildEmptyLogLabel(log, locale));

        foreach (var msg in log.Messages)
        {
            var (color, tag) = LevelTag(msg.LogLevel);
            stack = stack.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(8)
                .WithView(Controls.Label(tag)
                    .WithStyle($"min-width: 44px; font-weight: 600; color: {color};"))
                .WithView(Controls.Label(msg.Message)
                    .WithStyle($"flex: 1; white-space: pre-wrap; color: {color};")));
        }

        return stack;
    }

    /// <summary>
    /// The single row an EMPTY log renders: "Running…" while the activity is still
    /// <see cref="ActivityStatus.Running"/>, and an explicit "this run produced no output"
    /// statement once it is terminal — never a running label on a finished activity
    /// (#915: a code cell whose result is a control logs nothing, so "✓ Done" sat beside
    /// "Running…" and read as a failure). Public pure builder like its siblings so the
    /// status dependence is unit-testable without a layout host.
    /// </summary>
    public static LabelControl BuildEmptyLogLabel(ActivityLog log, string? locale = null) =>
        Controls.Label(LocalizationCatalog.Get(
                log.Status == ActivityStatus.Running ? "ui.running" : "ui.activityNoOutput", locale))
            .WithStyle("font-style: italic; color: var(--neutral-foreground-hint);");

    // Shared with the code cell's toolbar chip (CodeLayoutAreas.CellStatusChip) so the two
    // surfaces cannot disagree on what a status looks like.
    internal static string StatusColor(ActivityStatus status) => status switch
    {
        ActivityStatus.Failed    => "var(--error)",
        ActivityStatus.Warning   => "var(--warning)",
        ActivityStatus.Running   => "var(--neutral-foreground-hint)",
        ActivityStatus.Cancelled => "var(--neutral-foreground-hint)",
        _                        => "var(--accent-fill-rest)",
    };

    private static (string Color, string Tag) LevelTag(LogLevel level) => level switch
    {
        LogLevel.Critical or LogLevel.Error => ("var(--error)", "ERROR"),
        LogLevel.Warning                    => ("var(--warning)", "WARN"),
        LogLevel.Debug or LogLevel.Trace    => ("var(--neutral-foreground-hint)", "DBG"),
        _                                   => ("inherit", "INFO"),
    };

    /// <summary>
    /// Thumbnail view — compact label for activity entries.
    /// </summary>
    public static UiControl Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        return Controls.Label(host.Localize("ui.activity"));
    }
}
