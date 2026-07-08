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
    /// Overview for an Activity node. Header (user / category / status / timestamps),
    /// followed by the live progress indicator (indeterminate bar while running, a
    /// status line once terminal) and the structured message log (per-message rows
    /// with log-level colour coding), plus a Cancel button (while running) and a
    /// Re-run button (once terminal, when the activity originated from an
    /// executable hub). Built entirely from framework controls — no hand-rolled HTML.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        return host.Workspace.GetMeshNodeStream()
            .Select(node =>
            {
                if (node?.Content is not ActivityLog log)
                    return (UiControl?)Controls.Label("No activity data.")
                        .WithStyle("font-style: italic; color: var(--neutral-foreground-hint);");

                var stack = Controls.Stack
                    .WithStyle("padding: 16px; gap: 12px;")
                    .WithView(BuildHeader(log))
                    .WithView(BuildProgressIndicator(log))
                    .WithView(BuildLog(log));

                // While running: Cancel button. Per the Activity Control Plane
                // pattern (Doc/Architecture/ActivityControlPlane.md), cancellation
                // is a property patch on the activity's content — NOT a separate
                // CancelXRequest message. The activity hub's own watcher
                // (KernelContainer.StartActivityControlPlane) translates the
                // RequestedStatus = Cancelled patch into the internal cancel.
                if (IsCancelButtonVisible(log))
                {
                    stack = stack.WithView(Controls.Button("Cancel")
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
                    stack = stack.WithView(Controls.Button("Re-run")
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
                var button = Controls.Button("Cancel")
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
    /// only the live progress indicator + message log + inline Cancel button
    /// (while running). No header, no Re-run. Built from framework controls.
    /// </summary>
    public static IObservable<UiControl?> Progress(LayoutAreaHost host, RenderingContext _)
    {
        return host.Workspace.GetMeshNodeStream()
            .Select(node =>
            {
                if (node?.Content is not ActivityLog log)
                    return (UiControl?)Controls.Label("No activity yet.")
                        .WithStyle("font-style: italic; color: var(--neutral-foreground-hint);");

                var stack = Controls.Stack
                    .WithStyle("gap: 8px;")
                    .WithView(BuildProgressIndicator(log))
                    .WithView(BuildLog(log));

                // Inline Cancel: same content-patch pattern as the Overview's
                // button. Only rendered while the activity is actually running
                // and not already cancelling.
                if (IsCancelButtonVisible(log))
                {
                    stack = stack.WithView(Controls.Button("Cancel")
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
    public static StackControl BuildHeader(ActivityLog log)
    {
        var userName = log.User?.DisplayName ?? log.User?.Email ?? "System";
        var startStr = log.Start.ToString("g");
        var endStr = log.End is { } end ? end.ToString("g") : "—";

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
    /// The activity's progress indicator — the "progress" of the generic activity GUI.
    /// While <see cref="ActivityStatus.Running"/> it is an INDETERMINATE (animated)
    /// <see cref="ProgressControl"/> whose message is the latest log line (or
    /// "Running…" if none yet). Once terminal it is a coloured status line
    /// (✓ Done / ✗ Failed / ⚠ Completed with warnings / Cancelled) preceded by the
    /// final message. Passing <c>null</c> as the progress value is what drives the
    /// indeterminate FluentProgress in <c>ProgressView.razor</c>.
    /// </summary>
    public static UiControl BuildProgressIndicator(ActivityLog log)
    {
        var latest = log.Messages.Count > 0 ? log.Messages[^1].Message : null;

        if (log.Status == ActivityStatus.Running)
        {
            // Indeterminate bar: progress == null → animated FluentProgress.
            return Controls.Progress((object?)latest ?? "Running…", null!)
                .WithWidth("100%")
                .WithHideNumber(true)
                .WithMessagePosition(MessagePosition.Top);
        }

        var (glyph, label) = log.Status switch
        {
            ActivityStatus.Succeeded => ("✓", "Done"),
            ActivityStatus.Failed    => ("✗", "Failed"),
            ActivityStatus.Warning   => ("⚠", "Completed with warnings"),
            ActivityStatus.Cancelled => ("⊘", "Cancelled"),
            _                        => ("", log.Status.ToString()),
        };

        var text = string.IsNullOrEmpty(latest) ? $"{glyph} {label}" : $"{latest}\n{glyph} {label}";
        return Controls.H4(text)
            .WithStyle($"color: {StatusColor(log.Status)}; white-space: pre-wrap; margin: 0;");
    }

    /// <summary>
    /// The activity log — one row per <see cref="LogMessage"/>: a fixed-width
    /// level tag (INFO / WARN / ERROR / DBG, coloured by severity) beside the
    /// message text. An empty log on a running activity renders a single
    /// "Running…" row. Control-based (a vertical <see cref="StackControl"/> of
    /// horizontal rows) — replaces the former hand-rolled messages HTML and is
    /// unit-testable without a layout host.
    /// </summary>
    public static StackControl BuildLog(ActivityLog log)
    {
        var stack = Controls.Stack
            .WithStyle(
                "font-family: var(--font-monospace, ui-monospace, monospace); "
                + "font-size: .85rem; gap: 2px; max-height: 320px; overflow: auto;");

        if (log.Messages.Count == 0)
        {
            return stack.WithView(Controls.Label("Running…")
                .WithStyle("font-style: italic; color: var(--neutral-foreground-hint);"));
        }

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

    private static string StatusColor(ActivityStatus status) => status switch
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
        return Controls.Label("Activity");
    }
}
