using System.Reactive.Linq;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh.Activity;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Registers the "Activity" area on User nodes for the personal dashboard start page.
/// Sections: Chat Entry, Recent Activity, Notifications, Pending Actions.
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
    /// Renders the user's personal dashboard with 4 sections.
    /// </summary>
    public static IObservable<UiControl?> Activity(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var userId = accessService?.Context?.ObjectId ?? "";

        return Observable.FromAsync(async () =>
        {
            var dashboard = Controls.Stack.WithWidth("100%")
                .WithStyle("max-width: 960px; margin: 0 auto; padding: 24px;");

            // Welcome header
            var userName = accessService?.Context?.Name ?? "User";
            dashboard = dashboard.WithView(Controls.Html(
                $"<h1 style=\"margin: 0 0 24px 0;\">Welcome back, {EscapeHtml(userName)}</h1>"));

            // 1. Chat Entry
            dashboard = dashboard.WithView(BuildChatEntry(host, nodePath));

            // 2. Recent Activity
            dashboard = dashboard.WithView(await BuildRecentActivity(host, userId));

            // 3. Notifications
            dashboard = dashboard.WithView(BuildNotifications(nodePath, userId));

            // 4. Pending Actions (approvals assigned to this user)
            dashboard = dashboard.WithView(BuildPendingActions(userId));

            return (UiControl?)dashboard;
        });
    }

    private static UiControl BuildChatEntry(LayoutAreaHost host, string nodePath)
    {
        var section = Controls.Stack.WithWidth("100%")
            .WithStyle("margin-bottom: 32px; padding: 16px; border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; background: var(--neutral-layer-2);");

        section = section.WithView(Controls.Html(
            "<h3 style=\"margin: 0 0 12px 0;\">Ask anything</h3>"));

        // ThreadChatControl for new threads
        var chatControl = new ThreadChatControl()
            .WithInitialContext(nodePath)
            .WithInitialContextDisplayName("Home");

        section = section.WithView(chatControl);
        return section;
    }

    private static async Task<UiControl> BuildRecentActivity(LayoutAreaHost host, string userId)
    {
        var section = Controls.Stack.WithWidth("100%")
            .WithStyle("margin-bottom: 32px;");

        section = section.WithView(Controls.Html(
            "<h2 style=\"margin: 0 0 16px 0;\">Recent Activity</h2>"));

        var activityStore = host.Hub.ServiceProvider.GetService<IActivityStore>();
        if (activityStore == null || string.IsNullOrEmpty(userId))
        {
            section = section.WithView(Controls.Html(
                "<p style=\"color: var(--neutral-foreground-hint); font-style: italic;\">No recent activity.</p>"));
            return section;
        }

        var activities = await activityStore.GetActivitiesAsync(userId);
        var recent = activities
            .OrderByDescending(a => a.LastAccessedAt)
            .Take(10)
            .ToList();

        if (recent.Count == 0)
        {
            section = section.WithView(Controls.Html(
                "<p style=\"color: var(--neutral-foreground-hint); font-style: italic;\">No recent activity.</p>"));
            return section;
        }

        var list = Controls.Stack.WithWidth("100%").WithStyle("gap: 4px;");
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
        var section = Controls.Stack.WithWidth("100%")
            .WithStyle("margin-bottom: 32px;");

        section = section.WithView(Controls.Html(
            "<h2 style=\"margin: 0 0 16px 0;\">Notifications</h2>"));

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
        var section = Controls.Stack.WithWidth("100%")
            .WithStyle("margin-bottom: 32px;");

        section = section.WithView(Controls.Html(
            "<h2 style=\"margin: 0 0 16px 0;\">Pending Actions</h2>"));

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
