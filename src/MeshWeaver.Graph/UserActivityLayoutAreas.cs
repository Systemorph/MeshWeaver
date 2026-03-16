using System.Reactive.Linq;
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

            // Outer shell: flex column, fills the available main area (height managed by CSS grid)
            var dashboard = Controls.Stack
                .WithWidth("100%")
                .WithStyle("display: flex; flex-direction: column; height: 100%; min-height: 0; overflow: hidden;");

            // Welcome banner
            dashboard = dashboard.WithView(Controls.Html(
                $"<div style=\"flex-shrink: 0; padding: 20px 24px 12px 24px;\">" +
                $"<div style=\"font-size: 1.6rem; font-weight: 700; letter-spacing: -0.02em;\">" +
                $"Welcome back, {EscapeHtml(userName)}</div>" +
                $"<div style=\"font-size: 0.85rem; color: var(--neutral-foreground-hint); margin-top: 2px;\">Here's what's happening across your workspace</div>" +
                "</div>"));

            // Chat — full width
            dashboard = dashboard.WithView(BuildChatSection(host, nodePath));

            // Scrollable content area — full-width layout grid
            var content = Controls.LayoutGrid
                .WithStyle("padding: 0 24px; flex: 1; min-height: 0; overflow-y: auto; gap: 24px; width: 100%; " + ThinScrollbar);

            // Latest Threads — full width
            content = content.WithView(BuildLatestThreads(nodePath),
                skin => skin.WithXs(12));

            // Children section — full width
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

            return (UiControl?)dashboard;
        });
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

        section = section.WithView(Controls.Html(
            "<div style=\"font-size: 1.05rem; font-weight: 600; padding-bottom: 12px;\">Activity Feed</div>"));

        // Pinned documentation card
        section = section.WithView(BuildDocumentationCard());

        // Activity feed — 2 items per row, ~4 rows visible, scrollable
        section = section.WithView(Controls.Stack
            .WithStyle("max-height: 480px; overflow-y: auto;")
            .WithView(Controls.MeshSearch
                .WithHiddenQuery("source:activity scope:subtree is:main sort:LastModified-desc")
                .WithShowSearchBox(false)
                .WithRenderMode(MeshSearchRenderMode.Flat)
                .WithCollapsibleSections(false)
                .WithSectionCounts(false)
                .WithMaxColumns(2)
                .WithItemLimit(8)));

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
        var section = Controls.Stack.WithWidth("100%");

        section = section.WithView(Controls.Html(
            "<div style=\"font-size: 1.05rem; font-weight: 600; padding-bottom: 12px;\">Recently Viewed</div>"));

        // Recently viewed — 1 item per row (full width for readability), ~4 rows, scrollable
        // Exclude the user's own node from results (we're already on their page)
        section = section.WithView(Controls.Stack
            .WithWidth("100%")
            .WithStyle("max-height: 400px; overflow-y: auto;")
            .WithView(Controls.MeshSearch
                .WithHiddenQuery($"source:accessed scope:subtree is:main sort:LastModified-desc -path:{nodePath}")
                .WithShowSearchBox(false)
                .WithShowEmptyMessage(true)
                .WithRenderMode(MeshSearchRenderMode.Flat)
                .WithCollapsibleSections(false)
                .WithSectionCounts(false)
                .WithMaxColumns(1)
                .WithGridBreakpoints(12, 12, 12, 12)
                .WithItemLimit(4)));

        return section;
    }

    /// <summary>
    /// Latest threads — shows the user's most recently accessed threads.
    /// </summary>
    private static UiControl BuildLatestThreads(string nodePath)
    {
        var section = Controls.Stack.WithStyle("margin-top: 16px;");

        section = section.WithView(Controls.Html(
            "<div style=\"font-size: 1.05rem; font-weight: 600; padding-bottom: 12px;\">Latest Threads</div>"));

        section = section.WithView(Controls.MeshSearch
            .WithHiddenQuery($"nodeType:Thread namespace:{nodePath} scope:descendants sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithItemLimit(6)
            .WithCreateNodeType("Thread")
            .WithCreateNamespace(nodePath));

        return section;
    }

    /// <summary>
    /// Child nodes — shows sub-nodes grouped by type, like the standard Children view.
    /// </summary>
    private static UiControl BuildChildren(string nodePath)
    {
        var section = Controls.Stack.WithStyle("margin-top: 24px;");

        section = section.WithView(Controls.Html(
            "<div style=\"font-size: 1.05rem; font-weight: 600; padding-bottom: 12px;\">My Items</div>"));

        section = section.WithView(Controls.MeshSearch
            .WithHiddenQuery($"namespace:{nodePath} is:main context:search scope:descendants sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Grouped)
            .WithSectionCounts(true)
            .WithItemLimit(10)
            .WithCollapsibleSections(true)
            .WithCreateHref($"/{nodePath}/{MeshNodeLayoutAreas.CreateNodeArea}"));

        return section;
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
