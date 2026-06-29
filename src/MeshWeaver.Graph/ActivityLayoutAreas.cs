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
    /// followed by the structured message log (per-message rows with log-level
    /// colour coding), terminal-status badge, and Re-run button (when the activity
    /// originated from an executable hub and isn't currently running).
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        return host.Workspace.GetMeshNodeStream()
            .Select(node =>
            {
                if (node?.Content is not ActivityLog log)
                    return (UiControl?)Controls.Html("<div>No activity data.</div>");

                var stack = Controls.Stack
                    .WithStyle("padding: 16px; gap: 12px;")
                    .WithView(Controls.Html(BuildHeaderHtml(log)))
                    .WithView(Controls.Html(BuildMessagesHtml(log)));

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
    /// only the messages + inline Cancel button (while running) + status badge.
    /// No header, no Re-run.
    /// </summary>
    public static IObservable<UiControl?> Progress(LayoutAreaHost host, RenderingContext _)
    {
        return host.Workspace.GetMeshNodeStream()
            .Select(node =>
            {
                if (node?.Content is not ActivityLog log)
                    return (UiControl?)Controls.Html("<em>No activity yet.</em>");

                var stack = Controls.Stack
                    .WithStyle("gap: 8px;")
                    .WithView(Controls.Html(BuildMessagesHtml(log)));

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

    private static string BuildHeaderHtml(ActivityLog log)
    {
        var userName = log.User?.DisplayName ?? log.User?.Email ?? "System";
        var startStr = log.Start.ToString("g");
        var endStr = log.End is { } end ? end.ToString("g") : "—";
        var statusColor = log.Status switch
        {
            ActivityStatus.Failed => "var(--error)",
            ActivityStatus.Warning => "var(--warning)",
            ActivityStatus.Running => "var(--neutral-foreground-hint)",
            _ => "var(--accent-fill-rest)",
        };
        var statusLabel = log.Status.ToString();
        Func<string, string> enc = s => System.Net.WebUtility.HtmlEncode(s);
        return
            "<div style=\"display: flex; align-items: baseline; gap: 12px; flex-wrap: wrap;\">" +
                $"<span style=\"font-weight: 600; font-size: 1rem;\">{enc(userName)}</span>" +
                $"<span style=\"font-size: 0.85rem; padding: 2px 8px; border-radius: 10px; background: {statusColor}20; color: {statusColor};\">{enc(log.Category)} · {enc(statusLabel)}</span>" +
                $"<span style=\"font-size: 0.8rem; color: var(--neutral-foreground-hint); margin-left: auto;\">started {enc(startStr)} · ended {enc(endStr)}</span>" +
            "</div>";
    }

    private static string BuildMessagesHtml(ActivityLog log)
    {
        if (log.Messages.Count == 0 && log.Status == ActivityStatus.Running)
            return "<div style=\"font-style: italic; color: var(--neutral-foreground-hint);\">Running…</div>";

        Func<string, string> enc = s => System.Net.WebUtility.HtmlEncode(s);
        var sb = new System.Text.StringBuilder();
        sb.Append("<div style=\"font-family: var(--font-monospace, ui-monospace, SFMono-Regular, monospace); font-size: 0.85rem; line-height: 1.5;\">");
        foreach (var msg in log.Messages)
        {
            var (color, prefix) = msg.LogLevel switch
            {
                Microsoft.Extensions.Logging.LogLevel.Critical or Microsoft.Extensions.Logging.LogLevel.Error =>
                    ("var(--error)", "ERROR"),
                Microsoft.Extensions.Logging.LogLevel.Warning =>
                    ("var(--warning)", "WARN"),
                Microsoft.Extensions.Logging.LogLevel.Debug or Microsoft.Extensions.Logging.LogLevel.Trace =>
                    ("var(--neutral-foreground-hint)", "DBG"),
                _ => ("inherit", "INFO"),
            };
            sb.Append($"<div style=\"display: flex; gap: 8px; padding: 2px 0;\">");
            sb.Append($"<span style=\"color: {color}; font-weight: 600; min-width: 40px;\">{prefix}</span>");
            sb.Append($"<span style=\"color: {color}; flex: 1; white-space: pre-wrap;\">{enc(msg.Message)}</span>");
            sb.Append("</div>");
        }
        // Terminal-state badge.
        var statusBadge = log.Status switch
        {
            ActivityStatus.Succeeded => "<div style=\"margin-top: 8px; color: var(--accent-fill-rest);\">✓ Done</div>",
            ActivityStatus.Failed    => "<div style=\"margin-top: 8px; color: var(--error);\">✗ Failed</div>",
            ActivityStatus.Warning   => "<div style=\"margin-top: 8px; color: var(--warning);\">⚠ Completed with warnings</div>",
            _ => "<div style=\"margin-top: 8px; font-style: italic; color: var(--neutral-foreground-hint);\">Running…</div>",
        };
        sb.Append(statusBadge);
        sb.Append("</div>");
        return sb.ToString();
    }

    /// <summary>
    /// Thumbnail view — compact label for activity entries.
    /// </summary>
    public static UiControl Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        return Controls.Html("<div>Activity</div>");
    }
}
