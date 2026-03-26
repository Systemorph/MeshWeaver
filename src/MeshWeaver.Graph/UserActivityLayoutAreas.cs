using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.Extensions.DependencyInjection;

namespace MeshWeaver.Graph;

/// <summary>
/// Registers the "Activity" area on User nodes.
/// Shows a personal dashboard to the node owner, or a public profile to visitors.
/// </summary>
public static class UserActivityLayoutAreas
{
    public const string ActivityArea = "Activity";

    private const string ThinScrollbar = "scrollbar-width: thin; scrollbar-color: rgba(128,128,128,0.3) transparent;";

    /// <summary>
    /// Adds the Activity view to the User node's layout.
    /// </summary>
    public static MessageHubConfiguration AddUserActivityLayoutAreas(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout.WithView(ActivityArea, Activity));

    /// <summary>
    /// Renders the user's page. Shows a personal dashboard to the owner,
    /// or a public profile to visitors.
    /// </summary>
    public static IObservable<UiControl?> Activity(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        // Extract the owner ID from the hub address (e.g., "User/Alice" → "Alice")
        var nodeOwnerId = nodePath.StartsWith("User/") ? nodePath[5..] : nodePath;

        // Get the node from the workspace stream to derive the owner's display name
        var nodeStream = host.Workspace.GetStream<MeshNode>()?.Select(nodes => nodes ?? Array.Empty<MeshNode>())
            ?? Observable.Return(Array.Empty<MeshNode>());

        return nodeStream.SelectMany(async nodes =>
        {
            var ownerNode = nodes.FirstOrDefault(n => n.Path == nodePath);
            var ownerName = ownerNode?.Name ?? nodeOwnerId;

            // Determine if the viewer is the node owner
            var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
            var viewerId = accessService?.Context?.ObjectId ?? "";
            var isOwner = string.Equals(viewerId, nodeOwnerId, StringComparison.OrdinalIgnoreCase);

            if (isOwner)
                return (UiControl?)BuildOwnerDashboard(host, nodePath, ownerName, nodeOwnerId);
            else
                return (UiControl?)BuildVisitorProfile(nodePath, ownerName, ownerNode);
        });
    }

    /// <summary>
    /// Personal dashboard shown to the node owner — welcome banner, chat, threads,
    /// activity feed, recently viewed, and child items.
    /// </summary>
    private static UiControl BuildOwnerDashboard(LayoutAreaHost host, string nodePath, string ownerName, string nodeOwnerId)
    {
        // Outer shell: flex column, fills the available main area (height managed by CSS grid)
        var dashboard = Controls.Stack
            .WithWidth("100%")
            .WithStyle("display: flex; flex-direction: column; height: 100%; min-height: 0; overflow: hidden;");

        // Welcome banner
        dashboard = dashboard.WithView(Controls.Html(
            $"<div style=\"flex-shrink: 0; padding: 20px 24px 12px 24px;\">" +
            $"<div style=\"font-size: 1.6rem; font-weight: 700; letter-spacing: -0.02em;\">" +
            $"Welcome back, {EscapeHtml(ownerName)}</div>" +
            $"<div style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-top: 2px;\">Here's what's happening across your workspace</div>" +
            "</div>"));

        // Chat — full width
        dashboard = dashboard.WithView(BuildChatSection(host, nodePath));

        // Scrollable content area — full-width layout grid
        var content = Controls.LayoutGrid
            .WithStyle("padding: 0 24px; flex: 1; min-height: 0; overflow-y: auto; gap: 24px; width: 100%; " + ThinScrollbar);

        // Latest Threads — full width, above My Items
        content = content.WithView(BuildLatestThreads(nodePath, nodeOwnerId),
            skin => skin.WithXs(12));

        // My Items — full width, below Latest Threads
        content = content.WithView(BuildChildren(nodePath),
            skin => skin.WithXs(12));

        // Activity Feed — 2/3 width on desktop, full on mobile
        content = content.WithView(
            BuildActivityFeed(),
            skin => skin.WithXs(12).WithSm(8));

        // Recently Viewed — 1/3 width on desktop, full on mobile
        content = content.WithView(
            BuildRecentActivity(nodePath),
            skin => skin.WithXs(12).WithSm(4));

        dashboard = dashboard.WithView(content);

        return dashboard;
    }

    /// <summary>
    /// Public profile shown to visitors — UserProfileControl (rendered by Blazor)
    /// with child nodes and recent activity below.
    /// </summary>
    private static UiControl BuildVisitorProfile(string nodePath, string ownerName, MeshNode? ownerNode)
    {
        // Extract User content fields (bio, email) if available
        string? email = null;
        string? bio = null;
        if (ownerNode?.Content is User userContent)
        {
            email = userContent.Email;
            bio = userContent.Bio;
        }
        else if (ownerNode?.Content is JsonElement je && je.ValueKind == JsonValueKind.Object)
        {
            if (je.TryGetProperty("Email", out var emailProp) || je.TryGetProperty("email", out emailProp))
                email = emailProp.GetString();
            if (je.TryGetProperty("Bio", out var bioProp) || je.TryGetProperty("bio", out bioProp))
                bio = bioProp.GetString();
        }

        var profile = Controls.Stack
            .WithWidth("100%")
            .WithStyle("display: flex; flex-direction: column; height: 100%; min-height: 0; overflow: hidden;");

        // User profile card (rendered by Blazor UserProfilePageView)
        profile = profile.WithView(new UserProfileControl()
            .WithNodePath(nodePath)
            .WithDisplayName(ownerName)
            .WithIcon(ownerNode?.Icon)
            .WithEmail(email)
            .WithBio(bio));

        // Scrollable content area
        var content = Controls.Stack
            .WithStyle("padding: 0 24px; flex: 1; min-height: 0; overflow-y: auto; " + ThinScrollbar);

        // Recent activity by this user
        content = content.WithView(Controls.MeshSearch
            .WithTitle("Recent Activity")
            .WithHiddenQuery($"source:activity namespace:{nodePath} scope:subtree is:main sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithMaxColumns(4)
            .WithGridBreakpoints(12, 6, 3, 3)
            .WithItemLimit(8));

        // Visible child nodes — security service automatically filters to viewer-visible nodes
        content = content.WithView(Controls.MeshSearch
            .WithTitle("Items")
            .WithHiddenQuery($"namespace:{nodePath} is:main context:search scope:descendants sort:LastModified-desc")
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Grouped)
            .WithSectionCounts(true)
            .WithItemLimit(20)
            .WithMaxColumns(4)
            .WithGridBreakpoints(12, 6, 3, 3)
            .WithCollapsibleSections(true));

        profile = profile.WithView(content);
        return profile;
    }

    /// <summary>
    /// Chat input pinned to the very bottom — no header, full width, aligned with content above.
    /// Hides the empty-state placeholder; shows only the input bar with agent/model selectors.
    /// </summary>
    private static UiControl BuildChatSection(LayoutAreaHost host, string nodePath)
    {
        var section = Controls.Stack
            .WithStyle("flex-shrink: 0; width: 100%; padding: 8px 24px 12px 24px;");

        var chatControl = new ThreadChatControl()
            .WithInitialContext(nodePath)
            .WithInitialContextDisplayName("Home")
            .WithHideEmptyState();

        section = section.WithView(chatControl);
        return section;
    }

    /// <summary>
    /// Activity timeline — shows main content nodes with recent changes, plus a pinned docs card.
    /// source:activity JOINs with Activity satellites and orders by most recent activity.
    /// </summary>
    private static UiControl BuildActivityFeed()
    {
        var section = Controls.Stack;

        // Pinned documentation card
        section = section.WithView(BuildDocumentationCard());

        // Activity feed
        section = section.WithView(Controls.MeshSearch
            .WithTitle("Activity Feed")
            .WithHiddenQuery("source:activity scope:subtree is:main sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithMaxColumns(2)
            .WithItemLimit(8));

        return section;
    }

    /// <summary>
    /// Pinned welcome card linking to the documentation — styled like a social feed post.
    /// </summary>
    private static UiControl BuildDocumentationCard()
    {
        return Controls.Html(
            "<a href=\"/Doc\" style=\"text-decoration: none; color: inherit; display: block;\">" +
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
    /// Recently Viewed panel — compact card grid, max 10 items, fixed height with scroll.
    /// Resolves full MeshNode for each item to get proper icon/thumbnail.
    /// </summary>
    private static UiControl BuildRecentActivity(string nodePath)
    {
        return Controls.MeshSearch
            .WithTitle("Recently Viewed")
            .WithHiddenQuery($"source:accessed scope:subtree is:main sort:LastModified-desc -path:{nodePath}")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithMaxColumns(1)
            .WithGridBreakpoints(12, 12, 12, 12)
            .WithItemLimit(4);
    }

    /// <summary>
    /// Latest threads — shows the current user's threads across all partitions.
    /// Filters by content.CreatedBy to find only threads created by this user.
    /// </summary>
    private static UiControl BuildLatestThreads(string nodePath, string nodeOwnerId)
    {
        return Controls.MeshSearch
            .WithTitle("Latest Threads")
            .WithHiddenQuery($"nodeType:Thread content.createdBy:{nodeOwnerId} scope:descendants sort:LastModified-desc")
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithItemLimit(8)
            .WithMaxColumns(4)
            .WithGridBreakpoints(12, 6, 3, 3)
            .WithCreateNodeType("Thread")
            .WithCreateNamespace(nodePath);
    }

    /// <summary>
    /// Child nodes — shows sub-nodes grouped by type, like the standard Children view.
    /// </summary>
    private static UiControl BuildChildren(string nodePath)
    {
        return Controls.MeshSearch
            .WithTitle("My Items")
            .WithHiddenQuery($"namespace:{nodePath} is:main context:search scope:descendants sort:LastModified-desc")
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Grouped)
            .WithSectionCounts(true)
            .WithItemLimit(10)
            .WithMaxColumns(4)
            .WithGridBreakpoints(12, 6, 3, 3)
            .WithCollapsibleSections(true)
            .WithCreateHref($"/create?type=Markdown&namespace={Uri.EscapeDataString(nodePath)}");
    }

    private static UiControl BuildNotifications(string _, string userId)
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

    private static UiControl BuildPendingActions(string _)
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

    private static string EscapeHtml(string? text)
        => System.Net.WebUtility.HtmlEncode(text ?? "");
}
