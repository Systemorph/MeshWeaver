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
/// Layout: Tabbed content (Feed, Notifications, Actions) + sidebar (Recently Viewed, Chat).
/// Height constrained to viewport with individual areas scrollable.
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
    /// Renders the user's personal dashboard: welcome header,
    /// tabbed main content (Feed, Notifications, Actions) + sidebar (Recently Viewed, Chat).
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
                .WithStyle("display: flex; flex-direction: column; height: calc(100vh - 64px); min-height: 0; gap: 12px; padding: 16px;");

            // Welcome header (compact, no scroll)
            dashboard = dashboard.WithView(Controls.H2($"Welcome back, {userName}"));

            // 2-column responsive grid: tabs (9 cols) + sidebar (3 cols)
            var grid = Controls.LayoutGrid.WithStyle("width: 100%; flex: 1; min-height: 0;");

            // LEFT COLUMN: Tabbed content
            var tabs = Controls.Tabs
                .WithSkin(skin => skin
                    .WithActiveTabId("1")
                    .WithHeight("100%"))
                .WithView(
                    await BuildActivityFeed(host, userId),
                    s => s.WithLabel("Feed"))
                .WithView(
                    BuildNotifications(nodePath, userId),
                    s => s.WithLabel("Notifications"))
                .WithView(
                    BuildPendingActions(userId),
                    s => s.WithLabel("Actions"));

            grid = grid.WithView(tabs, skin => skin.WithXs(12).WithSm(9));

            // RIGHT COLUMN: Recently Viewed + Chat
            var sidebar = Controls.Stack
                .WithStyle("display: flex; flex-direction: column; height: 100%; min-height: 0; gap: 16px;");

            sidebar = sidebar.WithView(await BuildRecentActivity(host, userId));
            sidebar = sidebar.WithView(BuildChatSection(host, nodePath));

            grid = grid.WithView(sidebar, skin => skin.WithXs(12).WithSm(3));

            dashboard = dashboard.WithView(grid);

            return (UiControl?)dashboard;
        });
    }

    /// <summary>
    /// Chat section in the sidebar — narrower than the previous full-width sticky footer.
    /// </summary>
    private static UiControl BuildChatSection(LayoutAreaHost host, string nodePath)
    {
        var section = Controls.Stack
            .WithStyle("flex-shrink: 0; max-height: 40%; border-top: 1px solid var(--neutral-stroke-divider-rest); padding-top: 8px;");

        section = section.WithView(Controls.PaneHeader("Chat"));

        var chatControl = new ThreadChatControl()
            .WithInitialContext(nodePath)
            .WithInitialContextDisplayName("Home")
            .WithHideEmptyState();

        section = section.WithView(chatControl);
        return section;
    }

    /// <summary>
    /// Builds the unified activity feed combining UserActivityRecords and ActivityLogs.
    /// Modern card styling with hover effects.
    /// </summary>
    private static async Task<UiControl> BuildActivityFeed(LayoutAreaHost host, string userId)
    {
        var section = Controls.Stack
            .WithVerticalGap(8)
            .WithStyle("overflow-y: auto; padding: 4px;");

        section = section.WithView(Controls.PaneHeader("Activity Feed"));

        var activityStore = host.Hub.ServiceProvider.GetService<IActivityStore>();
        var activityLogStore = host.Hub.ServiceProvider.GetService<IActivityLogStore>();

        // Fetch both sources in parallel
        var userActivitiesTask = activityStore != null && !string.IsNullOrEmpty(userId)
            ? activityStore.GetActivitiesAsync(userId)
            : Task.FromResult<IReadOnlyList<UserActivityRecord>>([]);

        var activityLogsTask = activityLogStore != null
            ? activityLogStore.GetRecentActivityLogsAsync(limit: 20)
            : Task.FromResult<IReadOnlyList<ActivityLog>>([]);

        await Task.WhenAll(userActivitiesTask, activityLogsTask);

        var userActivities = await userActivitiesTask;
        var activityLogs = await activityLogsTask;

        // Build unified feed items sorted by timestamp
        var feedItems = new List<(DateTime Timestamp, string Html)>();

        // Add ActivityLog entries (system-wide edits)
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

            feedItems.Add((log.Start,
                $"<div style=\"display: flex; gap: 12px; padding: 12px 16px; border-radius: 8px; " +
                $"border: 1px solid var(--neutral-stroke-divider-rest); background: var(--neutral-layer-2); " +
                $"transition: box-shadow 0.15s ease, border-color 0.15s ease;\" " +
                $"onmouseenter=\"this.style.boxShadow='0 2px 8px rgba(0,0,0,0.08)'; this.style.borderColor='var(--accent-stroke-control-rest)'\" " +
                $"onmouseleave=\"this.style.boxShadow='none'; this.style.borderColor='var(--neutral-stroke-divider-rest)'\">" +
                $"<div style=\"flex-shrink: 0; width: 36px; height: 36px; border-radius: 50%; background: {statusColor}; " +
                $"display: flex; align-items: center; justify-content: center; color: white; font-size: 15px;\">&#9998;</div>" +
                $"<div style=\"flex: 1; min-width: 0;\">" +
                $"<div style=\"font-weight: 600; font-size: 0.9rem; line-height: 1.4;\">{EscapeHtml(userName)} " +
                $"<span style=\"font-weight: 400; color: var(--neutral-foreground-hint);\">edited</span> " +
                $"<a href=\"/{EscapeHtml(hubPath)}\" style=\"text-decoration: none; color: var(--accent-foreground-rest); font-weight: 500;\">{EscapeHtml(hubName)}</a></div>" +
                $"<div style=\"font-size: 0.8rem; color: var(--neutral-foreground-hint); margin-top: 4px; display: flex; align-items: center; gap: 8px;\">" +
                $"<span>{EscapeHtml(changeCount)}</span>" +
                $"<span style=\"opacity: 0.4;\">&#183;</span>" +
                $"<span>{timeAgo}</span>" +
                $"</div></div></div>"));
        }

        // Add recent user activity (personal views) — show top 10
        foreach (var activity in userActivities.Take(10))
        {
            var timeAgo = GetRelativeTime(activity.LastAccessedAt);
            var nodeTypeBadge = !string.IsNullOrEmpty(activity.NodeType)
                ? $"<span style=\"font-size: 0.7rem; padding: 1px 5px; border-radius: 3px; background: var(--neutral-layer-3); color: var(--neutral-foreground-hint);\">{EscapeHtml(activity.NodeType)}</span> "
                : "";

            feedItems.Add((activity.LastAccessedAt.UtcDateTime,
                $"<div style=\"display: flex; gap: 12px; padding: 12px 16px; border-radius: 8px; " +
                $"border: 1px solid var(--neutral-stroke-divider-rest); " +
                $"transition: box-shadow 0.15s ease, border-color 0.15s ease;\" " +
                $"onmouseenter=\"this.style.boxShadow='0 2px 8px rgba(0,0,0,0.08)'; this.style.borderColor='var(--accent-stroke-control-rest)'\" " +
                $"onmouseleave=\"this.style.boxShadow='none'; this.style.borderColor='var(--neutral-stroke-divider-rest)'\">" +
                $"<div style=\"flex-shrink: 0; width: 36px; height: 36px; border-radius: 50%; background: var(--neutral-layer-3); " +
                $"display: flex; align-items: center; justify-content: center; color: var(--neutral-foreground-hint); font-size: 15px;\">&#128065;</div>" +
                $"<div style=\"flex: 1; min-width: 0;\">" +
                $"<div style=\"font-size: 0.9rem; line-height: 1.4;\">{nodeTypeBadge}You viewed " +
                $"<a href=\"/{EscapeHtml(activity.NodePath)}\" style=\"text-decoration: none; color: var(--accent-foreground-rest); font-weight: 500;\">" +
                $"{EscapeHtml(activity.NodeName ?? activity.NodePath)}</a></div>" +
                $"<div style=\"font-size: 0.8rem; color: var(--neutral-foreground-hint); margin-top: 4px;\">{timeAgo}</div>" +
                $"</div></div>"));
        }

        if (feedItems.Count == 0)
        {
            section = section.WithView(Controls.Html(
                "<div style=\"padding: 32px; text-align: center; border: 1px dashed var(--neutral-stroke-divider-rest); border-radius: 8px;\">" +
                "<div style=\"font-weight: 600; margin-bottom: 4px;\">No activity yet</div>" +
                "<div style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint);\">Start exploring content \u2014 your views and edits will appear here.</div>" +
                "</div>"));
            return section;
        }

        // Sort by timestamp descending and render
        var feed = Controls.Stack.WithVerticalGap(8);
        foreach (var item in feedItems.OrderByDescending(f => f.Timestamp).Take(30))
        {
            feed = feed.WithView(Controls.Html(item.Html));
        }

        section = section.WithView(feed);
        return section;
    }

    /// <summary>
    /// Builds the "Recently Viewed" sidebar section. Shows up to 5 items,
    /// filling remaining slots from top-level Doc namespace nodes when fewer than 5 real activities exist.
    /// </summary>
    private static async Task<UiControl> BuildRecentActivity(LayoutAreaHost host, string userId)
    {
        const int maxItems = 5;

        var section = Controls.Stack
            .WithStyle("flex: 1; overflow-y: auto; min-height: 0;");

        section = section.WithView(Controls.PaneHeader("Recently Viewed"));

        var activityStore = host.Hub.ServiceProvider.GetService<IActivityStore>();
        var recentActivities = new List<UserActivityRecord>();

        if (activityStore != null && !string.IsNullOrEmpty(userId))
        {
            var activities = await activityStore.GetActivitiesAsync(userId);
            recentActivities = activities
                .OrderByDescending(a => a.LastAccessedAt)
                .Take(maxItems)
                .ToList();
        }

        // Render actual activity items
        var list = Controls.Stack.WithVerticalGap(4);
        foreach (var activity in recentActivities)
        {
            var nodeTypeBadge = !string.IsNullOrEmpty(activity.NodeType)
                ? $"<span style=\"font-size: 0.75rem; padding: 1px 6px; border-radius: 3px; background: var(--neutral-layer-3); color: var(--neutral-foreground-hint);\">{EscapeHtml(activity.NodeType)}</span>"
                : "";

            var timeAgo = GetRelativeTime(activity.LastAccessedAt);

            list = list.WithView(Controls.Html(
                $"<div style=\"display: flex; align-items: center; gap: 8px; padding: 6px 8px; border-radius: 4px; " +
                $"transition: background 0.1s ease;\" " +
                $"onmouseenter=\"this.style.background='var(--neutral-layer-3)'\" " +
                $"onmouseleave=\"this.style.background='transparent'\">" +
                $"{nodeTypeBadge}" +
                $"<a href=\"/{EscapeHtml(activity.NodePath)}\" style=\"flex: 1; text-decoration: none; color: var(--neutral-foreground-rest); font-size: 0.85rem;\">{EscapeHtml(activity.NodeName ?? activity.NodePath)}</a>" +
                $"<span style=\"font-size: 0.75rem; color: var(--neutral-foreground-hint);\">{timeAgo}</span>" +
                "</div>"));
        }

        // Fill remaining slots from Doc namespace top-level items
        var remaining = maxItems - recentActivities.Count;
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
