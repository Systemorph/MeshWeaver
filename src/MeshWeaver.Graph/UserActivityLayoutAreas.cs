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
/// Layout: Activity Feed (main) + sidebar (Recently Viewed, Notifications, Pending Actions) + Chat pinned to bottom.
/// </summary>
public static class UserActivityLayoutAreas
{
    public const string ActivityArea = "Activity";

    /// <summary>
    /// Adds the Activity view to the User node's layout.
    /// </summary>
    public static MessageHubConfiguration AddUserActivityLayoutAreas(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout.WithView(ActivityArea, Activity));

    /// <summary>
    /// Renders the user's personal dashboard: welcome header,
    /// 2-column layout (Activity Feed + sidebar), and chat pinned to bottom.
    /// </summary>
    public static IObservable<UiControl?> Activity(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var userId = accessService?.Context?.ObjectId ?? "";

        return Observable.FromAsync(async () =>
        {
            // Outer container: flex column filling available height
            var dashboard = Controls.Stack
                .WithWidth("100%")
                .WithStyle("display: flex; flex-direction: column; height: 100%; min-height: 0;");

            // Scrollable content area
            var contentArea = Controls.Stack
                .WithWidth("100%")
                .WithStyle("flex: 1; overflow-y: auto; min-height: 0;")
                .WithVerticalGap(16);

            // Welcome header
            var userName = accessService?.Context?.Name ?? "User";
            contentArea = contentArea.WithView(Controls.H2($"Welcome back, {userName}"));

            // 2-column responsive grid: feed (8 cols) + sidebar (4 cols)
            var grid = Controls.LayoutGrid.WithStyle("width: 100%;");

            // Activity Feed (main area)
            grid = grid.WithView(
                await BuildActivityFeed(host, userId),
                skin => skin.WithXs(12).WithSm(8)
            );

            // Sidebar: Recently Viewed + Notifications + Pending Actions
            var sidebar = Controls.Stack.WithVerticalGap(16);
            sidebar = sidebar.WithView(await BuildRecentActivity(host, userId));
            sidebar = sidebar.WithView(BuildNotifications(nodePath, userId));
            sidebar = sidebar.WithView(BuildPendingActions(userId));

            grid = grid.WithView(sidebar, skin => skin.WithXs(12).WithSm(4));
            contentArea = contentArea.WithView(grid);

            // Chat pinned to bottom of scroll area
            contentArea = contentArea.WithView(
                Controls.Stack
                    .WithWidth("100%")
                    .WithStyle("position: sticky; bottom: 0; background: var(--neutral-layer-1); border-top: 1px solid var(--neutral-stroke-divider-rest); padding-top: 8px; z-index: 1;")
                    .WithView(BuildChatEntry(host, nodePath))
            );

            dashboard = dashboard.WithView(contentArea);

            return (UiControl?)dashboard;
        });
    }

    private static UiControl BuildChatEntry(LayoutAreaHost host, string nodePath)
    {
        var chatControl = new ThreadChatControl()
            .WithInitialContext(nodePath)
            .WithInitialContextDisplayName("Home")
            .WithHideEmptyState();

        return Controls.Stack.WithWidth("100%").WithView(chatControl);
    }

    /// <summary>
    /// Builds the system-wide activity feed combining UserActivityRecords and ActivityLogs.
    /// </summary>
    private static async Task<UiControl> BuildActivityFeed(LayoutAreaHost host, string userId)
    {
        var section = Controls.Stack.WithVerticalGap(8);
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
                $"<div style=\"display: flex; gap: 12px; padding: 10px 12px; border-radius: 6px; border: 1px solid var(--neutral-stroke-divider-rest); background: var(--neutral-layer-2);\">" +
                $"<div style=\"flex-shrink: 0; width: 32px; height: 32px; border-radius: 50%; background: {statusColor}; display: flex; align-items: center; justify-content: center; color: white; font-size: 14px;\">&#9998;</div>" +
                $"<div style=\"flex: 1; min-width: 0;\">" +
                $"<div style=\"font-weight: 500;\">{EscapeHtml(userName)} <span style=\"font-weight: 400; color: var(--neutral-foreground-hint);\">edited</span> <a href=\"/{EscapeHtml(hubPath)}\" style=\"text-decoration: none; color: var(--accent-foreground-rest);\">{EscapeHtml(hubName)}</a></div>" +
                $"<div style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-top: 2px;\">{EscapeHtml(changeCount)}</div>" +
                $"<div style=\"font-size: 0.8rem; color: var(--neutral-foreground-hint); margin-top: 4px;\">{timeAgo}</div>" +
                "</div></div>"));
        }

        // Add recent user activity (personal views) — show top 10
        foreach (var activity in userActivities.Take(10))
        {
            var timeAgo = GetRelativeTime(activity.LastAccessedAt);
            var nodeTypeBadge = !string.IsNullOrEmpty(activity.NodeType)
                ? $"<span style=\"font-size: 0.7rem; padding: 1px 5px; border-radius: 3px; background: var(--neutral-layer-3); color: var(--neutral-foreground-hint);\">{EscapeHtml(activity.NodeType)}</span> "
                : "";

            feedItems.Add((activity.LastAccessedAt.UtcDateTime,
                $"<div style=\"display: flex; gap: 12px; padding: 10px 12px; border-radius: 6px; border: 1px solid var(--neutral-stroke-divider-rest);\">" +
                $"<div style=\"flex-shrink: 0; width: 32px; height: 32px; border-radius: 50%; background: var(--neutral-layer-3); display: flex; align-items: center; justify-content: center; color: var(--neutral-foreground-hint); font-size: 14px;\">&#128065;</div>" +
                $"<div style=\"flex: 1; min-width: 0;\">" +
                $"<div>{nodeTypeBadge}You viewed <a href=\"/{EscapeHtml(activity.NodePath)}\" style=\"text-decoration: none; color: var(--accent-foreground-rest);\">{EscapeHtml(activity.NodeName ?? activity.NodePath)}</a></div>" +
                $"<div style=\"font-size: 0.8rem; color: var(--neutral-foreground-hint); margin-top: 4px;\">{timeAgo}</div>" +
                "</div></div>"));
        }

        if (feedItems.Count == 0)
        {
            section = section.WithView(Controls.Html(
                "<div style=\"padding: 24px; border: 1px dashed var(--neutral-stroke-divider-rest); border-radius: 8px;\">" +
                "<div style=\"font-weight: 500; margin-bottom: 8px;\">Your activity feed is empty</div>" +
                "<div style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-bottom: 16px;\">Start exploring — your recent views and edits will appear here.</div>" +
                "</div>"));

            section = section.WithView(await BuildSuggestedContent(host));
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

    private static async Task<UiControl> BuildRecentActivity(LayoutAreaHost host, string userId)
    {
        var section = Controls.Stack;

        section = section.WithView(Controls.PaneHeader("Recently Viewed"));

        var activityStore = host.Hub.ServiceProvider.GetService<IActivityStore>();
        if (activityStore == null || string.IsNullOrEmpty(userId))
        {
            section = section.WithView(BuildQuickLinks(host));
            return section;
        }

        var activities = await activityStore.GetActivitiesAsync(userId);
        var recent = activities
            .OrderByDescending(a => a.LastAccessedAt)
            .Take(5)
            .ToList();

        if (recent.Count == 0)
        {
            section = section.WithView(BuildQuickLinks(host));
            return section;
        }

        var list = Controls.Stack.WithVerticalGap(4);
        foreach (var activity in recent)
        {
            var nodeTypeBadge = !string.IsNullOrEmpty(activity.NodeType)
                ? $"<span style=\"font-size: 0.75rem; padding: 1px 6px; border-radius: 3px; background: var(--neutral-layer-3); color: var(--neutral-foreground-hint);\">{EscapeHtml(activity.NodeType)}</span>"
                : "";

            var timeAgo = GetRelativeTime(activity.LastAccessedAt);

            list = list.WithView(Controls.Html(
                $"<div style=\"display: flex; align-items: center; gap: 8px; padding: 6px 8px; border-radius: 4px;\">" +
                $"{nodeTypeBadge}" +
                $"<a href=\"/{EscapeHtml(activity.NodePath)}\" style=\"flex: 1; text-decoration: none; color: var(--neutral-foreground-rest);\">{EscapeHtml(activity.NodeName ?? activity.NodePath)}</a>" +
                $"<span style=\"font-size: 0.8rem; color: var(--neutral-foreground-hint);\">{timeAgo}</span>" +
                "</div>"));
        }

        section = section.WithView(list);
        return section;
    }

    private static UiControl BuildNotifications(string nodePath, string userId)
    {
        var section = Controls.Stack;

        section = section.WithView(Controls.PaneHeader("Notifications"));

        // Use MeshSearchControl to query notification nodes under this user
        var searchControl = Controls.MeshSearch
            .WithHiddenQuery($"path:User/{userId} nodeType:{NotificationNodeType.NodeType} scope:descendants")
            .WithShowSearchBox(false)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithSortBy("CreatedAt", ascending: false)
            .WithReactiveMode(true)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false);

        section = section.WithView(searchControl);
        return section;
    }

    private static UiControl BuildPendingActions(string userId)
    {
        var section = Controls.Stack;

        section = section.WithView(Controls.PaneHeader("Pending Actions"));

        // Query approval nodes where this user is the approver
        var searchControl = Controls.MeshSearch
            .WithHiddenQuery($"nodeType:{ApprovalNodeType.NodeType}")
            .WithShowSearchBox(false)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithSortBy("CreatedAt", ascending: false)
            .WithReactiveMode(true)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false);

        section = section.WithView(searchControl);
        return section;
    }

    /// <summary>
    /// Builds a "Discover Content" section showing doc sections for new users.
    /// Only shows direct children (sections) — not all nested pages.
    /// </summary>
    private static async Task<UiControl> BuildSuggestedContent(LayoutAreaHost host)
    {
        var section = Controls.Stack.WithVerticalGap(8);
        section = section.WithView(Controls.PaneHeader("Discover"));

        var searchControl = Controls.MeshSearch
            .WithPlaceholder("Search for content to explore...")
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithMaxColumns(2)
            .WithItemLimit(6)
            .WithShowEmptyMessage(false)
            .WithShowLoadingIndicator(false);

        // Try to find documentation namespace and show its direct sections
        var meshCatalog = host.Hub.ServiceProvider.GetService<IMeshCatalog>();
        if (meshCatalog != null)
        {
            var rootNodes = new List<MeshNode>();
            await foreach (var node in meshCatalog.QueryAsync(null, maxResults: 20))
                rootNodes.Add(node);

            var docNode = rootNodes.FirstOrDefault(n =>
                n.Category?.Contains("Documentation", StringComparison.OrdinalIgnoreCase) == true
                || n.Name?.Contains("Documentation", StringComparison.OrdinalIgnoreCase) == true);

            if (docNode != null)
            {
                // scope:children — only direct sections, not every nested page
                searchControl = searchControl
                    .WithHiddenQuery($"path:{docNode.Path} scope:children")
                    .WithShowSearchBox(true);
            }
        }

        section = section.WithView(searchControl);
        return section;
    }

    /// <summary>
    /// Builds quick links to root-level content as a replacement for empty "Recently Viewed".
    /// Excludes system node types (AccessAssignment, NodeType, etc.).
    /// </summary>
    private static UiControl BuildQuickLinks(LayoutAreaHost host)
    {
        var section = Controls.Stack.WithVerticalGap(4);
        section = section.WithView(Controls.Html(
            "<div style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-bottom: 4px;\">Suggested pages:</div>"));

        var searchControl = Controls.MeshSearch
            .WithHiddenQuery("-nodeType:(AccessAssignment OR NodeType OR User OR Role OR Group) scope:children")
            .WithShowSearchBox(false)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithItemLimit(5)
            .WithMaxColumns(1)
            .WithShowEmptyMessage(false)
            .WithShowLoadingIndicator(false)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false);

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
