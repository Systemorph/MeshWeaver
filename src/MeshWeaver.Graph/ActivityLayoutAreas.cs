using System.Reactive.Linq;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Messaging;

namespace MeshWeaver.Graph;

/// <summary>
/// Overview and Thumbnail views for individual Activity nodes.
/// Registered via ActivityNodeType's AddActivityViews().
/// </summary>
public static class ActivityLayoutAreas
{
    public const string OverviewArea = "Overview";
    public const string ThumbnailArea = "Thumbnail";

    /// <summary>
    /// Registers the Activity-specific views (Overview, Thumbnail).
    /// </summary>
    public static MessageHubConfiguration AddActivityViews(this MessageHubConfiguration configuration)
        => configuration
            .AddLayout(layout => layout
                .WithDefaultArea(OverviewArea)
                .WithView(OverviewArea, Overview)
                .WithView(ThumbnailArea, Thumbnail)
                .WithView(ProgressArea, Progress));

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

                // Re-run button when the activity is finished and the originating
                // hub is known. Click posts ExecuteScriptRequest back to the
                // originating hub, which creates a fresh sibling Activity.
                if (!string.IsNullOrEmpty(log.HubPath) && log.Status != ActivityStatus.Running)
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
    /// Compact running-progress view for embedding next to an executable Code
    /// node (or anywhere a caller wants live script feedback). Streams the same
    /// ActivityLog content as <see cref="Overview"/> but trims chrome and shows
    /// only the messages + status badge — no header, no Re-run button.
    /// </summary>
    public static IObservable<UiControl?> Progress(LayoutAreaHost host, RenderingContext _)
    {
        return host.Workspace.GetMeshNodeStream()
            .Select(node =>
            {
                if (node?.Content is not ActivityLog log)
                    return (UiControl?)Controls.Html("<em>No activity yet.</em>");
                return (UiControl?)Controls.Html(BuildMessagesHtml(log));
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
