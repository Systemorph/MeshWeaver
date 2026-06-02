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
using Microsoft.Extensions.Logging;

namespace MeshWeaver.Graph;

/// <summary>
/// Registers the "Activity" area on User nodes.
/// Shows a personal dashboard to the node owner, or a public profile to visitors.
/// </summary>
public static class UserActivityLayoutAreas
{
    public const string ActivityArea = "Activity";

    private const string ThinScrollbar = "scrollbar-width: thin; scrollbar-color: rgba(128,128,128,0.3) transparent;";

    // Catalog tab state — the selected tab name is held in the host data stream
    // (same local-UI-state pattern as CommentLayoutAreas). Clicking a tab button
    // writes the name; the content pane observes the key and swaps the MeshSearch.
    private const string CatalogTabStateKey = "ownerCatalogTab";
    private const string TabThreads = "Threads";
    private const string TabSpaces = "Spaces";
    private const string TabMyItems = "My Items";
    private const string TabLastRead = "Last Read";
    private const string TabLastEdited = "Last Edited";
    // Threads first → it is the default tab shown on load. Spaces second so the user can see
    // (and explicitly create) the spaces they belong to. Pinned is NOT a tab; it has its own
    // always-visible section above the tab bar (see BuildOwnerDashboard).
    private static readonly string[] CatalogTabs = { TabThreads, TabSpaces, TabMyItems, TabLastRead, TabLastEdited };

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

        // CAPTURE the viewer's AccessContext at area-handler entry. The
        // LayoutAreaHost restores the per-subscription AccessContext during
        // its WithInitialization hook (line ~75 of LayoutAreaHost.cs), so
        // `accessService.Context` IS set when the `Activity(host, ctx)`
        // method runs. But the `IObservable<UiControl?>` we return is
        // subscribed AFTER initialization completes — by the time the
        // Select lambda fires for each workspace-stream emission, the
        // Context AsyncLocal has been cleared and reading it again returns
        // null. Capturing here, before constructing the observable, locks
        // the identity to this specific user's subscription.
        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var capturedAccessContext = accessService?.Context ?? accessService?.CircuitContext;
        var isOwner = IsViewerOwner(capturedAccessContext, nodeOwnerId);

        var areaLogger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.UserActivityLayoutAreas");
        areaLogger?.LogDebug(
            "[UserActivity.Activity] hubAddress={HubAddress} nodePath={NodePath} nodeOwnerId={OwnerId} " +
            "viewer.ObjectId={ViewerObjectId} viewer.Email={ViewerEmail} viewer.IsVirtual={IsVirtual} " +
            "isOwner={IsOwner} (Context={HasCtx}, CircuitContext={HasCircuit})",
            host.Hub.Address, nodePath, nodeOwnerId,
            capturedAccessContext?.ObjectId ?? "(null)",
            capturedAccessContext?.Email ?? "(null)",
            capturedAccessContext?.IsVirtual ?? false,
            isOwner,
            accessService?.Context != null,
            accessService?.CircuitContext != null);

        var syncStream = host.Workspace.GetStream(new MeshNodeReference());

        return syncStream!.Select(change =>
        {
            var ownerNode = change.Value;
            var ownerName = ownerNode?.Name ?? nodeOwnerId;

            if (isOwner)
                return (UiControl?)BuildOwnerDashboard(host, nodePath, ownerName, nodeOwnerId, ownerNode);
            else
                return (UiControl?)BuildVisitorProfile(nodePath, ownerName, ownerNode);
        });
    }

    /// <summary>
    /// True when the viewer's <see cref="AccessContext"/> represents the same
    /// principal as the per-user partition key <paramref name="nodeOwnerId"/>
    /// — the rule that gates <see cref="BuildOwnerDashboard"/> vs
    /// <see cref="BuildVisitorProfile"/>. Accepts either:
    /// <list type="bullet">
    ///   <item><see cref="AccessContext.ObjectId"/> equal to the partition key
    ///     — the canonical match when <c>CircuitAccessHandler</c> seeds
    ///     ObjectId from the email's local part (the same rule
    ///     <c>UserOnboardingService</c> uses to name the partition).</item>
    ///   <item><see cref="AccessContext.Email"/>'s local part equal to the
    ///     partition key — fallback for auth backends that leave ObjectId as
    ///     the UPN or an Entra GUID. Mirrors
    ///     <c>CircuitAccessHandler.UsernameFromEmail</c>.</item>
    /// </list>
    /// </summary>
    internal static bool IsViewerOwner(AccessContext? viewer, string nodeOwnerId)
    {
        if (viewer is null || string.IsNullOrEmpty(nodeOwnerId))
            return false;
        if (string.Equals(viewer.ObjectId, nodeOwnerId, StringComparison.OrdinalIgnoreCase))
            return true;
        if (!string.IsNullOrEmpty(viewer.Email) && viewer.Email.Contains('@'))
        {
            var alias = viewer.Email.Split('@')[0].ToLowerInvariant();
            if (string.Equals(alias, nodeOwnerId, StringComparison.OrdinalIgnoreCase))
                return true;
        }
        return false;
    }

    /// <summary>
    /// Personal dashboard shown to the node owner — a welcome banner, an always-visible
    /// Pinned section, a catalog with tab toggles (Threads / My Items / Last Read /
    /// Last Edited, Threads default), and the chat input pinned to the bottom.
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

        // Pinned section — its own band ABOVE the tabs, always visible (when the owner has pins).
        var pinned = BuildPinnedItems(ownerNode);
        if (pinned != null)
            dashboard = dashboard.WithView(Controls.Stack
                .WithWidth("100%")
                .WithStyle("flex-shrink: 0; padding: 0 24px 8px 24px;")
                .WithView(pinned));

        // Catalog region: a fixed tab bar over a swappable content pane. The
        // selected tab lives in the host data stream so a button click re-emits
        // both the bar (active styling) and the content (which MeshSearch shows).
        var catalog = Controls.Stack
            .WithWidth("100%")
            .WithStyle("display: flex; flex-direction: column; flex: 1; min-height: 0; overflow: hidden; padding: 0 24px;");

        // Init-once seed of the default tab — heap-captured so it survives the
        // per-subscription re-evaluation of the view-definition lambda.
        var initialized = new[] { false };

        catalog = catalog.WithView((h, _) =>
        {
            if (!initialized[0])
            {
                h.UpdateData(CatalogTabStateKey, TabThreads);
                initialized[0] = true;
            }
            return h.Stream.GetDataStream<string>(CatalogTabStateKey)
                .DistinctUntilChanged()
                .Select(selected => BuildCatalogTabBar(selected ?? TabThreads));
        });

        catalog = catalog.WithView((h, _) =>
            h.Stream.GetDataStream<string>(CatalogTabStateKey)
                .DistinctUntilChanged()
                .Select(selected => BuildCatalogContent(selected ?? TabThreads, nodePath, nodeOwnerId)));

        dashboard = dashboard.WithView(catalog);

        // Chat input — pinned to the bottom of the dashboard column
        dashboard = dashboard.WithView(BuildChatSection(host, nodePath));

        return dashboard;
    }

    /// <summary>
    /// Horizontal row of tab toggle buttons. The active tab is rendered with an
    /// accent appearance; clicking any tab writes its name to the catalog state key.
    /// </summary>
    private static UiControl BuildCatalogTabBar(string selected)
    {
        var bar = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithHorizontalGap(4)
            .WithStyle("flex-shrink: 0; align-items: center; padding: 4px 0 12px 0; border-bottom: 1px solid var(--neutral-stroke-rest);");

        foreach (var tab in CatalogTabs)
        {
            var captured = tab; // bind per-iteration for the click closure
            var isActive = string.Equals(tab, selected, StringComparison.Ordinal);
            bar = bar.WithView(Controls.Button(captured)
                .WithAppearance(isActive ? Appearance.Accent : Appearance.Stealth)
                .WithStyle(isActive
                    ? "font-weight: 600;"
                    : "font-weight: 400; color: var(--neutral-foreground-hint);")
                .WithClickAction(ctx =>
                {
                    ctx.Host.UpdateData(CatalogTabStateKey, captured);
                    return Task.CompletedTask;
                }));
        }

        return bar;
    }

    /// <summary>
    /// Maps the selected tab to its existing MeshSearch builder, wrapped in a scrolling pane.
    /// Threads is the default (shown on load). Pinned is not a tab — it has its own section.
    /// </summary>
    private static UiControl BuildCatalogContent(string selected, string nodePath, string nodeOwnerId)
    {
        var pane = Controls.Stack
            .WithWidth("100%")
            .WithStyle("flex: 1; min-height: 0; overflow-y: auto; padding-top: 12px; " + ThinScrollbar);

        UiControl content = selected switch
        {
            TabSpaces => BuildSpaces(),
            // My Items live in the user's own partition (rbuergi.* post-v10), not
            // under the User/-prefixed dashboard path — pass nodeOwnerId.
            TabMyItems => BuildChildren(nodeOwnerId),
            TabLastRead => BuildRecentActivity(nodePath),
            TabLastEdited => BuildActivityFeed(),
            _ => BuildLatestThreads(nodePath, nodeOwnerId), // Threads + default
        };

        return pane.WithView(content);
    }

    /// <summary>
    /// Public profile shown to visitors — UserProfileControl (rendered by Blazor)
    /// with child nodes and recent activity below.
    /// </summary>
    private static UiControl BuildVisitorProfile(string nodePath, string ownerName, MeshNode? ownerNode)
    {
        // Compute owner partition path (post-v10 each user has their own
        // partition; legacy /User/{userid}/... maps to {userid}/... content)
        var nodeOwnerId = nodePath.StartsWith("User/") ? nodePath[5..] : nodePath;

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

        // Recent activity by this user. Per-user content lives at the user's
        // own partition path (post-v10: nodeOwnerId = "rbuergi"), not under
        // the User-prefixed dashboard path. Both Items and Activity queries
        // scope to nodeOwnerId so the legacy User/ path doesn't leak.
        content = content.WithView(Controls.MeshSearch
            .WithTitle("Recent Activity")
            .WithHiddenQuery($"source:activity namespace:{nodeOwnerId} scope:subtree is:main sort:LastModified-desc")
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
            .WithHiddenQuery($"namespace:{nodeOwnerId} is:main context:search scope:descendants sort:LastModified-desc")
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
            .WithMaxColumns(4)
            .WithGridSpacing(20)
            .WithItemLimit(24)
            .WithMaxRows(2)
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
            // -content.status:Done hides threads the user explicitly marked
            // finished. Type `content.status:Done` in the search box to surface them.
            .WithHiddenQuery($"nodeType:Thread namespace:*/_Thread content.createdBy:{nodeOwnerId} -content.status:Done sort:LastModified-desc")
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
    /// Spaces the user belongs to — every <c>Space</c> node the viewer can read (the
    /// SecurityService filters the query to the partitions they have access to). The
    /// "New space" affordance routes to the standard create form pre-set to
    /// <c>type=Space</c>; because <c>Space</c>'s <c>NodeTypeDefinition.DefaultNamespace</c>
    /// is empty, the node is created top-level, which is the ONLY sanctioned way to make a
    /// new partition — <see cref="MeshWeaver.Mesh.Services.IPartitionStorageProvider"/>
    /// schemas are never created implicitly by an arbitrary write (see
    /// <c>PartitionWriteGuardValidator</c>). Creating the Space runs
    /// <c>SpaceTopLevelValidator</c> (provisions the schema) + <c>SpacePostCreationHandler</c>
    /// (registers the partition, grants the creator Admin).
    /// </summary>
    private static UiControl BuildSpaces()
    {
        return Controls.MeshSearch
            .WithTitle("Spaces")
            .WithHiddenQuery("nodeType:Space is:main sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithMaxColumns(4)
            .WithItemLimit(50)
            .WithMaxRows(3)
            .WithReactiveMode(true)
            // Explicit, top-level Space creation only — never implicit. type=Space resolves
            // DefaultNamespace="" → the create form submits a top-level node.
            .WithCreateHref("/create?type=Space");
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

    private static string EscapeHtml(string? text)
        => System.Net.WebUtility.HtmlEncode(text ?? "");
}
