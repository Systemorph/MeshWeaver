using System.Reactive.Linq;
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
                .WithView(ThumbnailArea, Thumbnail));

    /// <summary>
    /// Overview for an Activity node. Shows user, category, status, timestamps, and messages.
    /// </summary>
    public static IObservable<UiControl?> Overview(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        return host.Workspace.GetStream<MeshNode>()
            .Select(nodes =>
            {
                var node = nodes?.FirstOrDefault(n => n.Path == hubPath);
                if (node?.Content is not ActivityLog log)
                    return (UiControl?)Controls.Html("<div>No activity data.</div>");

                var userName = log.User?.DisplayName ?? log.User?.Email ?? "System";
                var timeStr = log.Start.ToString("g");
                var statusColor = log.Status switch
                {
                    ActivityStatus.Failed => "var(--error)",
                    ActivityStatus.Warning => "var(--warning)",
                    _ => "var(--accent-fill-rest)"
                };

                var html = $"<div style=\"padding: 16px;\">" +
                    $"<div style=\"display: flex; align-items: center; gap: 8px; margin-bottom: 12px;\">" +
                    $"<span style=\"font-weight: 600;\">{System.Net.WebUtility.HtmlEncode(userName)}</span>" +
                    $"<span style=\"font-size: 0.8rem; padding: 2px 8px; border-radius: 10px; background: {statusColor}20; color: {statusColor};\">{System.Net.WebUtility.HtmlEncode(log.Category)}</span>" +
                    $"<span style=\"font-size: 0.8rem; color: var(--neutral-foreground-hint); margin-left: auto;\">{timeStr}</span>" +
                    $"</div>";

                foreach (var msg in log.Messages)
                {
                    html += $"<div style=\"font-size: 0.9rem; margin-bottom: 4px;\">{System.Net.WebUtility.HtmlEncode(msg.Message)}</div>";
                }

                html += "</div>";
                return (UiControl?)Controls.Html(html);
            });
    }

    /// <summary>
    /// Thumbnail view — compact label for activity entries.
    /// </summary>
    public static UiControl Thumbnail(LayoutAreaHost host, RenderingContext _)
    {
        return Controls.Html("<div>Activity</div>");
    }
}
