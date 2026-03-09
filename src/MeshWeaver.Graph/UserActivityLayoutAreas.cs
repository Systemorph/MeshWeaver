using System.Reactive.Linq;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Registers the "Activity" area on User nodes for the personal dashboard start page.
/// Modern social-media-inspired layout with activity timeline, quick-access cards, and chat.
/// </summary>
public static class UserActivityLayoutAreas
{
    public const string ActivityArea = "Activity";

    private const string ThinScrollbar = "scrollbar-width: thin; scrollbar-color: rgba(128,128,128,0.3) transparent;";
    private const string ColumnHeight = "calc(100vh - 280px)";

    /// <summary>
    /// Adds the Activity view to the User node's layout.
    /// </summary>
    public static MessageHubConfiguration AddUserActivityLayoutAreas(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout.WithView(ActivityArea, Activity));

    /// <summary>
    /// Renders the user's personal dashboard with a modern social-media look.
    /// </summary>
    public static IObservable<UiControl?> Activity(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var userId = accessService?.Context?.ObjectId ?? "";

        return Observable.FromAsync(async () =>
        {
            var userName = accessService?.Context?.Name ?? "User";

            // Outer shell: flex column, full viewport height minus portal header
            var dashboard = Controls.Stack
                .WithWidth("100%")
                .WithStyle("display: flex; flex-direction: column; height: calc(100vh - 64px); min-height: 0; overflow: hidden;");

            // Welcome banner
            dashboard = dashboard.WithView(Controls.Html(
                $"<div style=\"flex-shrink: 0; padding: 20px 24px 12px 24px;\">" +
                $"<div style=\"font-size: 1.6rem; font-weight: 700; letter-spacing: -0.02em;\">" +
                $"Welcome back, {EscapeHtml(userName)}</div>" +
                $"<div style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-top: 2px;\">Here's what's happening across your workspace</div>" +
                "</div>"));

            // Content area: two-column grid
            var topPanel = Controls.LayoutGrid
                .WithStyle("padding: 0 24px; gap: 24px;");

            topPanel = topPanel.WithView(
                await BuildActivityFeed(host),
                skin => skin.WithXs(12).WithSm(8));

            topPanel = topPanel.WithView(
                await BuildRecentActivity(host, userId),
                skin => skin.WithXs(12).WithSm(4));

            dashboard = dashboard.WithView(topPanel);

            // Chat — full width, pinned to bottom, no title
            dashboard = dashboard.WithView(BuildChatSection(host, nodePath));

            return (UiControl?)dashboard;
        });
    }

    /// <summary>
    /// Chat input pinned to the very bottom — no header, full width, aligned with content above.
    /// </summary>
    private static UiControl BuildChatSection(LayoutAreaHost host, string nodePath)
    {
        var section = Controls.Stack
            .WithStyle("flex-shrink: 0; width: 100%; padding: 8px 24px 12px 24px;");

        var chatControl = new ThreadChatControl()
            .WithInitialContext(nodePath)
            .WithInitialContextDisplayName("Home");

        section = section.WithView(chatControl);
        return section;
    }

    /// <summary>
    /// Activity timeline — social-media style feed with rich cards, fixed height with scroll.
    /// Always shows a pinned documentation welcome card as the first item.
    /// </summary>
    private static async Task<UiControl> BuildActivityFeed(LayoutAreaHost host)
    {
        // Fixed-height scrollable section
        var section = Controls.Stack
            .WithHeight(ColumnHeight)
            .WithStyle($"overflow-y: auto; {ThinScrollbar}");

        // Section header
        section = section.WithView(Controls.Html(
            "<div style=\"font-size: 1.05rem; font-weight: 600; padding-bottom: 12px;\">Activity Feed</div>"));

        var feed = Controls.Stack.WithVerticalGap(12);

        // Pinned documentation card — always first
        feed = feed.WithView(BuildDocumentationCard());

        var meshQuery = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        var activityLogNodes = new List<MeshNode>();
        await foreach (var n in meshQuery.QueryAsync<MeshNode>("nodeType:ActivityLog sort:Start-desc limit:30 scope:subtree"))
            activityLogNodes.Add(n);
        var activityLogs = activityLogNodes.Select(n => n.Content).OfType<ActivityLog>().ToList();

        foreach (var log in activityLogs)
        {
            feed = feed.WithView(BuildActivityCard(log));
        }

        section = section.WithView(feed);
        return section;
    }

    /// <summary>
    /// Pinned welcome card linking to the documentation — styled like a social feed post.
    /// </summary>
    private static UiControl BuildDocumentationCard()
    {
        return Controls.Html(
            "<a href=\"/MeshWeaver/Documentation\" style=\"text-decoration: none; color: inherit; display: block;\">" +
            "<div style=\"display: flex; gap: 14px; padding: 14px 16px; border-radius: 12px; " +
            "background: var(--neutral-layer-2); border-left: 3px solid var(--accent-fill-rest); " +
            "transition: transform 0.15s ease, box-shadow 0.15s ease;\" " +
            "onmouseenter=\"this.style.transform='translateY(-1px)'; this.style.boxShadow='0 4px 16px rgba(0,0,0,0.12)'\" " +
            "onmouseleave=\"this.style.transform='none'; this.style.boxShadow='none'\">" +

            // Logo avatar
            "<div style=\"flex-shrink: 0; width: 40px; height: 40px; border-radius: 50%; " +
            "background: var(--neutral-layer-3); " +
            "display: flex; align-items: center; justify-content: center;\">" +
            "<img src=\"/static/storage/content/MeshWeaver/logo.svg\" alt=\"\" style=\"width: 24px; height: 24px;\" />" +
            "</div>" +

            // Content
            "<div style=\"flex: 1; min-width: 0;\">" +
            "<div style=\"display: flex; align-items: center; gap: 8px;\">" +
            "<span style=\"font-weight: 600; font-size: 0.9rem;\">MeshWeaver</span>" +
            "<span style=\"font-size: 0.7rem; padding: 2px 8px; border-radius: 10px; " +
            "background: rgba(100,100,100,0.12); color: var(--accent-fill-rest); font-weight: 500;\">Pinned</span>" +
            "</div>" +
            "<div style=\"font-size: 0.9rem; margin-top: 6px; line-height: 1.5;\">" +
            "Explore the documentation, try the use cases, or just <strong>open the chat below</strong> and ask anything.</div>" +
            "<div style=\"display: inline-flex; align-items: center; gap: 4px; " +
            "margin-top: 8px; padding: 4px 12px; border-radius: 6px; " +
            "background: var(--neutral-layer-3); " +
            "color: var(--accent-foreground-rest); font-size: 0.8rem; font-weight: 500;\">" +
            "&#8594; Documentation</div>" +
            "</div></div></a>");
    }

    /// <summary>
    /// Single activity card — social-media post style with avatar, action, link, and timestamp.
    /// </summary>
    private static UiControl BuildActivityCard(ActivityLog log)
    {
        var userName = log.User?.DisplayName ?? log.User?.Email ?? "System";
        var initials = GetInitials(userName);
        var hubPath = log.HubPath ?? "unknown";
        var hubName = hubPath.Contains('/') ? hubPath.Substring(hubPath.LastIndexOf('/') + 1) : hubPath;
        var message = log.Messages.Count > 0 ? log.Messages[0].Message : "data changes";
        var timeAgo = GetRelativeTime(new DateTimeOffset(log.Start, TimeSpan.Zero));

        // Category styling
        var (categoryIcon, categoryColor, categoryBg) = log.Category switch
        {
            "Approval" => ("&#10003;", "#2e7d32", "rgba(46,125,50,0.12)"),
            "DataUpdate" => ("&#9998;", "#1565c0", "rgba(21,101,192,0.12)"),
            _ => ("&#9679;", "var(--accent-fill-rest)", "rgba(100,100,100,0.12)")
        };

        var statusBorderLeft = log.Status switch
        {
            ActivityStatus.Failed => "border-left: 3px solid var(--error);",
            ActivityStatus.Warning => "border-left: 3px solid var(--warning);",
            _ => $"border-left: 3px solid {categoryColor};"
        };

        // Avatar color based on user name hash
        var avatarHue = Math.Abs(userName.GetHashCode()) % 360;

        return Controls.Html(
            $"<div style=\"display: flex; gap: 14px; padding: 14px 16px; border-radius: 12px; " +
            $"background: var(--neutral-layer-2); {statusBorderLeft} " +
            $"transition: transform 0.15s ease, box-shadow 0.15s ease;\" " +
            $"onmouseenter=\"this.style.transform='translateY(-1px)'; this.style.boxShadow='0 4px 16px rgba(0,0,0,0.12)'\" " +
            $"onmouseleave=\"this.style.transform='none'; this.style.boxShadow='none'\">" +

            // User avatar
            $"<div style=\"flex-shrink: 0; width: 40px; height: 40px; border-radius: 50%; " +
            $"background: hsl({avatarHue}, 55%, 55%); " +
            $"display: flex; align-items: center; justify-content: center; " +
            $"color: white; font-weight: 700; font-size: 0.85rem; letter-spacing: 0.5px;\">{EscapeHtml(initials)}</div>" +

            // Content
            $"<div style=\"flex: 1; min-width: 0;\">" +

            // Top row: user + category badge + time
            $"<div style=\"display: flex; align-items: center; gap: 8px; flex-wrap: wrap;\">" +
            $"<span style=\"font-weight: 600; font-size: 0.9rem;\">{EscapeHtml(userName)}</span>" +
            $"<span style=\"font-size: 0.7rem; padding: 2px 8px; border-radius: 10px; " +
            $"background: {categoryBg}; color: {categoryColor}; font-weight: 500;\">" +
            $"{categoryIcon} {EscapeHtml(log.Category)}</span>" +
            $"<span style=\"font-size: 0.75rem; color: var(--neutral-foreground-hint); margin-left: auto;\">{timeAgo}</span>" +
            $"</div>" +

            // Action description
            $"<div style=\"font-size: 0.85rem; color: var(--neutral-foreground-rest); margin-top: 6px; line-height: 1.5;\">" +
            $"{EscapeHtml(message)}</div>" +

            // Target link
            $"<a href=\"/{EscapeHtml(hubPath)}\" style=\"display: inline-flex; align-items: center; gap: 4px; " +
            $"margin-top: 8px; padding: 4px 12px; border-radius: 6px; " +
            $"background: var(--neutral-layer-3); text-decoration: none; " +
            $"color: var(--accent-foreground-rest); font-size: 0.8rem; font-weight: 500; " +
            $"transition: background 0.1s ease;\" " +
            $"onmouseenter=\"this.style.background='var(--accent-fill-rest)'; this.style.color='white'\" " +
            $"onmouseleave=\"this.style.background='var(--neutral-layer-3)'; this.style.color='var(--accent-foreground-rest)'\">" +
            $"&#8594; {EscapeHtml(hubName)}</a>" +

            "</div></div>");
    }

    /// <summary>
    /// Recently Viewed panel — compact card grid, max 10 items, fixed height with scroll.
    /// Resolves full MeshNode for each item to get proper icon/thumbnail.
    /// </summary>
    private static async Task<UiControl> BuildRecentActivity(LayoutAreaHost host, string userId)
    {
        // Fixed-height scrollable section
        var section = Controls.Stack
            .WithHeight(ColumnHeight)
            .WithStyle($"overflow-y: auto; {ThinScrollbar}");

        // Section header
        section = section.WithView(Controls.Html(
            "<div style=\"font-size: 1.05rem; font-weight: 600; padding-bottom: 12px;\">Recently Viewed</div>"));

        var meshQuery = host.Hub.ServiceProvider.GetRequiredService<IMeshService>();
        var recentNodes = new List<MeshNode>();
        await foreach (var n in meshQuery.QueryAsync<MeshNode>("source:activity sort:lastAccessedAt-desc limit:10 scope:subtree"))
            recentNodes.Add(n);

        if (recentNodes.Count == 0)
        {
            section = section.WithView(Controls.Html(
                "<div style=\"padding: 48px 24px; text-align: center; border: 1px dashed var(--neutral-stroke-divider-rest); border-radius: 12px; margin-top: 8px;\">" +
                "<div style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint);\">Items you visit will appear here.</div>" +
                "</div>"));
            return section;
        }

        var grid = Controls.LayoutGrid.WithStyle("gap: 8px;");

        foreach (var node in recentNodes)
        {
            grid = grid.WithView(
                MeshNodeCardControl.FromNode(node, node.Path ?? ""),
                skin => skin.WithXs(12));
        }

        section = section.WithView(grid);
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

    private static string GetInitials(string name)
    {
        var parts = name.Split(' ', StringSplitOptions.RemoveEmptyEntries);
        if (parts.Length >= 2)
            return $"{parts[0][0]}{parts[parts.Length - 1][0]}".ToUpperInvariant();
        return parts.Length > 0 ? parts[0][..Math.Min(2, parts[0].Length)].ToUpperInvariant() : "?";
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
