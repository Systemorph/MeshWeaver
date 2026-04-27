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

        var syncStream = host.Workspace.GetStream(new MeshNodeReference());

        return syncStream!.Select(change =>
        {
            var ownerNode = change.Value;
            var ownerName = ownerNode?.Name ?? nodeOwnerId;

            // Determine if the viewer is the node owner
            var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
            var viewerId = accessService?.Context?.ObjectId ?? "";
            var isOwner = string.Equals(viewerId, nodeOwnerId, StringComparison.OrdinalIgnoreCase);

            if (isOwner)
                return (UiControl?)BuildOwnerDashboard(host, nodePath, ownerName, nodeOwnerId, ownerNode);
            else
                return (UiControl?)BuildVisitorProfile(nodePath, ownerName, ownerNode);
        });
    }

    /// <summary>
    /// Personal dashboard shown to the node owner — welcome banner, pinned items, threads,
    /// child items, activity feed, recently viewed, and the chat input pinned to the bottom.
    /// </summary>
    private static UiControl BuildOwnerDashboard(LayoutAreaHost host, string nodePath, string ownerName, string nodeOwnerId, MeshNode? ownerNode)
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

        // Scrollable content area — full-width layout grid
        var content = Controls.LayoutGrid
            .WithStyle("padding: 0 24px; flex: 1; min-height: 0; overflow-y: auto; gap: 24px; width: 100%; " + ThinScrollbar);

        // Pinned items — compact, first section
        var pinnedSection = BuildPinnedItems(ownerNode);
        if (pinnedSection != null)
            content = content.WithView(pinnedSection, skin => skin.WithXs(12));

        // Latest Threads — full width
        content = content.WithView(BuildLatestThreads(nodePath, nodeOwnerId),
            skin => skin.WithXs(12));

        // My Items — full width
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

        // Chat input — pinned to the bottom of the dashboard column
        dashboard = dashboard.WithView(BuildChatSection(host, nodePath));

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
            .WithItemLimit(50)
            .WithMaxRows(2)
            .WithReactiveMode(true));

        // Visible child nodes — security service automatically filters to viewer-visible nodes
        content = content.WithView(Controls.MeshSearch
            .WithTitle("Items")
            .WithHiddenQuery($"namespace:{nodePath} is:main context:search scope:descendants sort:LastModified-desc")
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Grouped)
            .WithSectionCounts(true)
            .WithItemLimit(50)
            .WithMaxRows(3)
            .WithMaxColumns(4)
            .WithCollapsibleSections(true)
            .WithReactiveMode(true));

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
            .WithHideEmptyState()
            .WithStyle("width: 100%;");

        section = section.WithView(chatControl);
        return section;
    }

    /// <summary>
    /// Activity timeline — shows main content nodes with recent changes.
    /// source:activity JOINs with Activity satellites and orders by most recent activity.
    /// </summary>
    private static UiControl BuildActivityFeed()
    {
        return Controls.MeshSearch
            .WithTitle("Activity Feed")
            .WithHiddenQuery("source:activity scope:subtree is:main sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithMaxColumns(2)
            .WithItemLimit(50)
            .WithMaxRows(4)
            .WithReactiveMode(true);
    }

    /// <summary>
    /// Pinned items — compact cards of everything in the owner's <see cref="User.PinnedPaths"/>.
    /// Each card is rendered via <see cref="PinLayoutArea.PinnedThumbnailArea"/>, which overlays
    /// an unpin icon so owners can remove items inline. Returns <c>null</c> when nothing is pinned.
    /// </summary>
    private static UiControl? BuildPinnedItems(MeshNode? ownerNode)
    {
        var pinnedPaths = (ownerNode?.Content as User)?.PinnedPaths;
        if (pinnedPaths == null || pinnedPaths.Count == 0)
            return null;

        var pathsClause = string.Join(" OR ", pinnedPaths);
        return Controls.MeshSearch
            .WithTitle("Pinned")
            .WithHiddenQuery($"path:({pathsClause}) sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(false)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithItemArea(PinLayoutArea.PinnedThumbnailArea)
            .WithMaxColumns(6)
            .WithItemLimit(24)
            .WithMaxRows(1)
            .WithReactiveMode(true);
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
            .WithItemLimit(20)
            .WithMaxRows(4)
            .WithReactiveMode(true);
    }

    /// <summary>
    /// Latest threads — shows the current user's threads across all partitions.
    /// Filters by content.CreatedBy to find only threads created by this user.
    /// </summary>
    private static UiControl BuildLatestThreads(string nodePath, string nodeOwnerId)
    {
        return Controls.MeshSearch
            .WithTitle("Latest Threads")
            .WithHiddenQuery($"nodeType:Thread namespace:*/_Thread content.createdBy:{nodeOwnerId} sort:LastModified-desc")
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithItemLimit(50)
            .WithMaxRows(2)
            .WithMaxColumns(4)
            .WithReactiveMode(true)
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
            .WithHiddenQuery($"namespace:{nodePath} is:main context:search sort:LastModified-desc")
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Grouped)
            .WithSortBy("LastModified", ascending: false)
            .WithSectionCounts(true)
            .WithItemLimit(50)
            .WithMaxRows(3)
            .WithMaxColumns(4)
            .WithCollapsibleSections(true)
            .WithReactiveMode(true)
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
