using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Registers the "Activity" area on User nodes for the personal dashboard start page.
/// Layout: Vertical stack — top panel (grid: Activities + Recently Viewed, scrollable),
/// bottom panel (Chat, full width, glued to bottom).
/// </summary>
public static class UserActivityLayoutAreas
{
    public const string ActivityArea = "Activity";

    private static readonly HashSet<string> SystemNodeTypes =
    [
        "AccessAssignment", "NodeType", "User", "Role", "Group", "PartitionAccessPolicy"
    ];

    /// <summary>
    /// Adds the Activity view to the User node's layout.
    /// </summary>
    public static MessageHubConfiguration AddUserActivityLayoutAreas(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout.WithView(ActivityArea, Activity));

    /// <summary>
    /// Renders the user's personal dashboard.
    /// Top: grid with Activities (bigger) + Recently Viewed (smaller), scrollable.
    /// Bottom: Chat (full width, glued to bottom).
    /// </summary>
    public static IObservable<UiControl?> Activity(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var userId = accessService?.Context?.ObjectId ?? "";

        return Observable.FromAsync(async () =>
        {
            var userName = accessService?.Context?.Name ?? "User";

            // Outer container: flex column filling viewport minus portal header
            var dashboard = Controls.Stack
                .WithWidth("100%")
                .WithStyle("display: flex; flex-direction: column; height: calc(100vh - 64px); min-height: 0; padding: 16px; gap: 0;");

            // Welcome header (compact, no scroll)
            dashboard = dashboard.WithView(Controls.H2($"Welcome back, {userName}"));

            // TOP PANEL: Grid with Activities + Recently Viewed (scrollable, takes remaining space)
            var topPanel = Controls.LayoutGrid
                .WithStyle("width: 100%; flex: 1; min-height: 0; overflow-y: auto; gap: 16px;");

            topPanel = topPanel.WithView(
                await BuildActivityFeed(host),
                skin => skin.WithXs(12).WithSm(8));

            topPanel = topPanel.WithView(
                await BuildRecentActivity(host, userId),
                skin => skin.WithXs(12).WithSm(4));

            dashboard = dashboard.WithView(topPanel);

            // BOTTOM PANEL: Chat (full width, glued to bottom)
            dashboard = dashboard.WithView(BuildChatSection(host, nodePath));

            return (UiControl?)dashboard;
        });
    }

    /// <summary>
    /// Chat section — full width, glued to bottom of the page.
    /// </summary>
    private static UiControl BuildChatSection(LayoutAreaHost host, string nodePath)
    {
        var section = Controls.Stack
            .WithStyle("flex-shrink: 0; height: 280px; border-top: 1px solid var(--neutral-stroke-divider-rest); padding-top: 8px;");

        section = section.WithView(Controls.PaneHeader("Chat"));

        var chatControl = new ThreadChatControl()
            .WithInitialContext(nodePath)
            .WithInitialContextDisplayName("Home")
            .WithHideEmptyState();

        section = section.WithView(chatControl);
        return section;
    }

    /// <summary>
    /// Builds the activity feed from ActivityLog entries only (system-wide edits/approvals).
    /// Personal views are shown separately in Recently Viewed.
    /// </summary>
    private static async Task<UiControl> BuildActivityFeed(LayoutAreaHost host)
    {
        var section = Controls.Stack
            .WithVerticalGap(8)
            .WithStyle("overflow-y: auto; padding: 4px;");

        section = section.WithView(Controls.PaneHeader("Activity Feed"));

        var activityLogStore = host.Hub.ServiceProvider.GetService<IActivityLogStore>();

        var activityLogs = activityLogStore != null
            ? await activityLogStore.GetRecentActivityLogsAsync(limit: 30)
            : [];

        if (activityLogs.Count == 0)
        {
            section = section.WithView(Controls.Html(
                "<div style=\"padding: 32px; text-align: center; border: 1px dashed var(--neutral-stroke-divider-rest); border-radius: 8px;\">" +
                "<div style=\"font-weight: 600; margin-bottom: 4px;\">No activity yet</div>" +
                "<div style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint);\">Edits, approvals and other events will appear here.</div>" +
                "</div>"));
            return section;
        }

        var feed = Controls.Stack.WithVerticalGap(8);
        foreach (var log in activityLogs)
        {
            var userName = log.User?.DisplayName ?? log.User?.Email ?? "System";
            var hubPath = log.HubPath ?? "unknown";
            var hubName = hubPath.Contains('/') ? hubPath.Substring(hubPath.LastIndexOf('/') + 1) : hubPath;
            var changeCount = log.Messages.Count > 0 ? log.Messages[0].Message : "data changes";
            var timeAgo = GetRelativeTime(new DateTimeOffset(log.Start, TimeSpan.Zero));
            var statusColor = log.Status switch
            {
                ActivityStatus.Failed => "var(--error)",
                ActivityStatus.Warning => "var(--warning)",
                _ => "var(--accent-fill-rest)"
            };

            feed = feed.WithView(Controls.Html(
                $"<div style=\"display: flex; gap: 12px; padding: 12px 16px; border-radius: 8px; " +
                $"border: 1px solid var(--neutral-stroke-divider-rest); background: var(--neutral-layer-2); " +
                $"transition: box-shadow 0.15s ease, border-color 0.15s ease;\" " +
                $"onmouseenter=\"this.style.boxShadow='0 2px 8px rgba(0,0,0,0.08)'; this.style.borderColor='var(--accent-stroke-control-rest)'\" " +
                $"onmouseleave=\"this.style.boxShadow='none'; this.style.borderColor='var(--neutral-stroke-divider-rest)'\">" +
                $"<div style=\"flex-shrink: 0; width: 36px; height: 36px; border-radius: 50%; background: {statusColor}; " +
                $"display: flex; align-items: center; justify-content: center; color: white; font-size: 15px;\">&#9998;</div>" +
                $"<div style=\"flex: 1; min-width: 0;\">" +
                $"<div style=\"font-weight: 600; font-size: 0.9rem; line-height: 1.4;\">{EscapeHtml(userName)} " +
                $"<span style=\"font-weight: 400; color: var(--neutral-foreground-hint);\">{EscapeHtml(log.Category)}</span> " +
                $"<a href=\"/{EscapeHtml(hubPath)}\" style=\"text-decoration: none; color: var(--accent-foreground-rest); font-weight: 500;\">{EscapeHtml(hubName)}</a></div>" +
                $"<div style=\"font-size: 0.8rem; color: var(--neutral-foreground-hint); margin-top: 4px; display: flex; align-items: center; gap: 8px;\">" +
                $"<span>{EscapeHtml(changeCount)}</span>" +
                $"<span style=\"opacity: 0.4;\">&#183;</span>" +
                $"<span>{timeAgo}</span>" +
                $"</div></div></div>"));
        }

        section = section.WithView(feed);
        return section;
    }

    /// <summary>
    /// Builds the "Recently Viewed" panel. Takes 50 most recently visited,
    /// then orders by view count (AccessCount) descending.
    /// Fills remaining slots from catalog when fewer than 10 real activities.
    /// </summary>
    private static async Task<UiControl> BuildRecentActivity(LayoutAreaHost host, string userId)
    {
        const int fetchLimit = 50;
        const int displayLimit = 20;

        var section = Controls.Stack
            .WithStyle("overflow-y: auto; padding: 4px;");

        section = section.WithView(Controls.PaneHeader("Recently Viewed"));

        var activityStore = host.Hub.ServiceProvider.GetService<IActivityStore>();
        var recentActivities = new List<UserActivityRecord>();

        if (activityStore != null && !string.IsNullOrEmpty(userId))
        {
            var activities = await activityStore.GetActivitiesAsync(userId);
            // Take 50 most recent, then sort by view count descending
            recentActivities = activities
                .OrderByDescending(a => a.LastAccessedAt)
                .Take(fetchLimit)
                .OrderByDescending(a => a.AccessCount)
                .ThenByDescending(a => a.LastAccessedAt)
                .Take(displayLimit)
                .ToList();
        }

        // Render activity items with view count
        var list = Controls.Stack.WithVerticalGap(4);
        foreach (var activity in recentActivities)
        {
            var nodeTypeBadge = !string.IsNullOrEmpty(activity.NodeType)
                ? $"<span style=\"font-size: 0.75rem; padding: 1px 6px; border-radius: 3px; background: var(--neutral-layer-3); color: var(--neutral-foreground-hint);\">{EscapeHtml(activity.NodeType)}</span>"
                : "";

            var timeAgo = GetRelativeTime(activity.LastAccessedAt);
            var viewCount = activity.AccessCount > 1
                ? $"<span style=\"font-size: 0.7rem; color: var(--neutral-foreground-hint); white-space: nowrap;\">{activity.AccessCount}x</span>"
                : "";

            list = list.WithView(Controls.Html(
                $"<div style=\"display: flex; align-items: center; gap: 8px; padding: 6px 8px; border-radius: 4px; " +
                $"transition: background 0.1s ease;\" " +
                $"onmouseenter=\"this.style.background='var(--neutral-layer-3)'\" " +
                $"onmouseleave=\"this.style.background='transparent'\">" +
                $"{nodeTypeBadge}" +
                $"<a href=\"/{EscapeHtml(activity.NodePath)}\" style=\"flex: 1; text-decoration: none; color: var(--neutral-foreground-rest); font-size: 0.85rem; overflow: hidden; text-overflow: ellipsis; white-space: nowrap;\">{EscapeHtml(activity.NodeName ?? activity.NodePath)}</a>" +
                $"{viewCount}" +
                $"<span style=\"font-size: 0.75rem; color: var(--neutral-foreground-hint); white-space: nowrap;\">{timeAgo}</span>" +
                "</div>"));
        }

        // Fill remaining slots from catalog
        var remaining = displayLimit - recentActivities.Count;
        if (remaining > 0)
        {
            var recentPaths = new HashSet<string>(recentActivities.Select(a => a.NodePath));
            var meshCatalog = host.Hub.ServiceProvider.GetService<IMeshCatalog>();

            if (meshCatalog != null)
            {
                var fillerNodes = new List<MeshNode>();
                await foreach (var node in meshCatalog.QueryAsync(null, maxResults: 20))
                {
                    if (node.NodeType != null && SystemNodeTypes.Contains(node.NodeType))
                        continue;

                    if (node.Path != null && recentPaths.Contains(node.Path))
                        continue;

                    fillerNodes.Add(node);
                    if (fillerNodes.Count >= remaining)
                        break;
                }

                if (fillerNodes.Count > 0 && recentActivities.Count > 0)
                {
                    list = list.WithView(Controls.Html(
                        "<div style=\"border-top: 1px solid var(--neutral-stroke-divider-rest); margin: 4px 0;\"></div>"));
                    list = list.WithView(Controls.Html(
                        "<div style=\"font-size: 0.75rem; color: var(--neutral-foreground-hint); padding: 2px 8px;\">Suggested</div>"));
                }

                foreach (var node in fillerNodes)
                {
                    var nodeTypeBadge = !string.IsNullOrEmpty(node.NodeType)
                        ? $"<span style=\"font-size: 0.75rem; padding: 1px 6px; border-radius: 3px; background: var(--neutral-layer-3); color: var(--neutral-foreground-hint);\">{EscapeHtml(node.NodeType)}</span>"
                        : "";

                    list = list.WithView(Controls.Html(
                        $"<div style=\"display: flex; align-items: center; gap: 8px; padding: 6px 8px; border-radius: 4px; " +
                        $"transition: background 0.1s ease;\" " +
                        $"onmouseenter=\"this.style.background='var(--neutral-layer-3)'\" " +
                        $"onmouseleave=\"this.style.background='transparent'\">" +
                        $"{nodeTypeBadge}" +
                        $"<a href=\"/{EscapeHtml(node.Path ?? "")}\" style=\"flex: 1; text-decoration: none; color: var(--neutral-foreground-rest); font-size: 0.85rem;\">{EscapeHtml(node.Name ?? node.Id ?? "")}</a>" +
                        "</div>"));
                }
            }
        }

        section = section.WithView(list);
        return section;
    }

    private static UiControl BuildNotifications(string nodePath, string userId)
    {
        var section = Controls.Stack
            .WithVerticalGap(8)
            .WithStyle("overflow-y: auto; padding: 4px;");

        section = section.WithView(Controls.PaneHeader("Notifications"));

        var searchControl = Controls.MeshSearch
            .WithHiddenQuery($"path:User/{userId} nodeType:{NotificationNodeType.NodeType} scope:descendants")
            .WithShowSearchBox(false)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithSortBy("CreatedAt", ascending: false)
            .WithReactiveMode(true)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithMaxColumns(2);

        section = section.WithView(searchControl);
        return section;
    }

    private static UiControl BuildPendingActions(string userId)
    {
        var section = Controls.Stack
            .WithVerticalGap(8)
            .WithStyle("overflow-y: auto; padding: 4px;");

        section = section.WithView(Controls.PaneHeader("Pending Actions"));

        var searchControl = Controls.MeshSearch
            .WithHiddenQuery($"nodeType:{ApprovalNodeType.NodeType}")
            .WithShowSearchBox(false)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithSortBy("CreatedAt", ascending: false)
            .WithReactiveMode(true)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithMaxColumns(2);

        section = section.WithView(searchControl);
        return section;
    }

    private static string GetRelativeTime(DateTimeOffset time)
    {
        var diff = DateTimeOffset.UtcNow - time;
        if (diff.TotalMinutes < 1) return "just now";
        if (diff.TotalMinutes < 60) return $"{(int)diff.TotalMinutes}m ago";
        if (diff.TotalHours < 24) return $"{(int)diff.TotalHours}h ago";
        if (diff.TotalDays < 7) return $"{(int)diff.TotalDays}d ago";
        return time.ToString("MMM dd");
    }

    private static string EscapeHtml(string? text)
        => System.Net.WebUtility.HtmlEncode(text ?? "");
}
