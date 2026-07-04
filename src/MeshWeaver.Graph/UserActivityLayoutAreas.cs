using System.Reactive.Linq;
using System.Text.Json;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
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
    /// <summary>Area name for the Activity layout area — the owner's home page (or visitor profile).</summary>
    public const string ActivityArea = "Activity";

    // Home regions. The owner home is a SINGLE editable markdown page (User.Body, 1:1 with Space.Body)
    // that embeds regions with @@("area/<Name>"). Areas are registered with the standard fluent layout
    // builder below, which is flexible enough to embed ANY view — the Body can @@-embed any area or
    // node. Only genuinely USER-SPECIFIC regions are registered here; GENERIC areas (e.g. the node
    // "Search" catalog from MeshNodeLayoutAreas.AddDefaultLayoutAreas) are reused as-is — a Body can
    // @@("area/Search") without us re-declaring it.
    /// <summary>Area name for the pinned-items region embedded via <c>@@("area/Pinned")</c>.</summary>
    public const string PinnedArea = "Pinned";
    /// <summary>Area name for the open-threads region embedded via <c>@@("area/Threads")</c>.</summary>
    public const string ThreadsArea = "Threads";
    /// <summary>Area name for the catalog region embedded via <c>@@("area/Catalog")</c>.</summary>
    public const string CatalogArea = "Catalog";
    /// <summary>Area name for the chat composer region embedded via <c>@@("area/Composer")</c>.</summary>
    public const string ComposerArea = "Composer";

    /// <summary>
    /// The user-facing chat URL contract: <c>/{user}/Chat</c> — one string serving as BOTH the URL
    /// segment links navigate to (<c>WithCreateHref</c>, chat menu) AND the layout-area name that
    /// URL resolves to (AreaPage renders prefix=<c>{user}</c>, remainder=<c>Chat</c> as area "Chat"
    /// on the user hub). Kept separate from <see cref="ComposerArea"/>, whose name is persisted in
    /// user home markdown (<c>@@("area/Composer")</c>) and cannot be renamed.
    /// </summary>
    public const string ChatArea = "Chat";

    /// <summary>Link to the doc page that explains the configurable Body-page + <c>@@</c>-region model.</summary>
    internal const string ConfigGuideLink = "/Doc/GUI/ConfigurablePages";

    private const string ThinScrollbar = "scrollbar-width: thin; scrollbar-color: rgba(128,128,128,0.3) transparent;";

    // Catalog tab labels. Each maps to a labelled MeshSearch in the fluent catalog (see BuildCatalog);
    // the declaration order there is the tab order. Open threads are NOT a tab here — they have their
    // own region above the catalog (see ThreadsAreaView); the catalog is the broader "browse" surface.
    private const string TabSpaces = "Spaces";
    private const string TabMyItems = "My Items";
    private const string TabLastRead = "Last Read";
    private const string TabLastEdited = "Last Edited";

    /// <summary>
    /// Adds the Activity view (the owner home / visitor profile) to the User node's layout, plus the
    /// user-specific home regions the owner page embeds with <c>@@("area/…")</c> (Pinned, Threads,
    /// Catalog, Composer). This is the standard fluent layout builder — flexible enough to embed any
    /// view — and registering the regions as real areas is what lets the home be ONE editable markdown
    /// page, exactly the Space Overview model. Generic areas (e.g. <c>Search</c>) come from
    /// <c>AddDefaultLayoutAreas</c> and are reused, not re-declared here.
    /// </summary>
    public static MessageHubConfiguration AddUserActivityLayoutAreas(this MessageHubConfiguration configuration)
        => configuration.AddLayout(layout => layout
            .WithView(ActivityArea, Activity)
            .WithView(PinnedArea, PinnedAreaView)
            .WithView(ThreadsArea, ThreadsAreaView)
            .WithView(CatalogArea, CatalogAreaView)
            .WithView(ComposerArea, ComposerAreaView)
            // "/{user}/Chat" (ChatArea) is a well-known URL (thread-catalog Create-New, chat menu
            // links). Since ChatNodeType was removed there is NO {user}/Chat node any more: the URL
            // resolves to prefix={user} + remainder="Chat", which AreaPage renders as area "Chat"
            // on this hub. Without this registration that's "no renderer for area Chat", and a
            // LEGACY {user}/Chat node from an older deployment resolves as invalid-NodeType
            // ("No node found at '{user}/Chat'… remainder='Chat'" — the prod memex report,
            // 2026-07-02). The composer is node-less — serve it directly.
            .WithView(ChatArea, ComposerAreaView));

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

        // The composer region (@@("area/Composer")) renders its ThreadChatControl INLINE — a pure
        // layout area with no backing node (see ComposerAreaView) — so the dashboard no longer has to
        // ensure-create a {owner}/Chat node before rendering. Nothing to gate on: bind straight to the
        // owner-node sync stream.
        return syncStream!
            .Select(change =>
            {
                var ownerNode = change.Value;
                var ownerName = ownerNode?.Name ?? nodeOwnerId;

                if (isOwner)
                    return (UiControl?)BuildOwnerHome(nodePath, ownerName, ownerNode, host.Hub.JsonSerializerOptions);
                else
                    return (UiControl?)BuildVisitorProfile(nodePath, ownerName, ownerNode);
            })
            // The area must NEVER spin forever, but it must ALSO never tear itself down
            // while idle. Two distinct failure modes, ONE narrow guard:
            //   • NOT REACHABLE — the owner hub never returns its FIRST snapshot. No
            //     OnError fires, so .Select never runs and the area spins. We arm a
            //     timeout for the FIRST emission ONLY.
            //   • NO ACCESS — the read is denied; the stream OnErrors (handled by Catch).
            // CRITICAL: the timeout is armed for the first element ONLY (Observable.Timer
            // as the first-timeout) and DISARMED thereafter (the per-element selector
            // returns Observable.Never). A bare .Timeout(30s) fires on every inter-emission
            // gap — so an idle, healthy data-bound view (no changes for 30s) would trip it
            // and the rendered area would be torn down mid-session. Idle ≠ unreachable.
            // On a real first-snapshot timeout or a denial we THROW a clear, attributed
            // error (do NOT swallow) — surfaced, logged loud, root chased separately.
            .Timeout(Observable.Timer(TimeSpan.FromSeconds(30)), _ => Observable.Never<long>())
            .Catch<UiControl?, Exception>(ex =>
            {
                var reason = ex is TimeoutException
                    ? $"user node '{nodePath}' did not return a snapshot (owner hub not reachable)"
                    : $"could not read user node '{nodePath}' ({ex.GetType().Name}: {ex.Message})";
                areaLogger?.LogWarning(ex,
                    "[UserActivity.Activity] area unavailable for {NodePath} — {Reason}", nodePath, reason);
                return Observable.Throw<UiControl?>(
                    new InvalidOperationException($"Activity dashboard unavailable — {reason}.", ex));
            });
    }

    /// <summary>
    /// True when the viewer's <see cref="AccessContext"/> represents the same
    /// principal as the per-user partition key <paramref name="nodeOwnerId"/>
    /// — the rule that gates <see cref="BuildOwnerHome"/> vs
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

    /// <summary>Owner id from a user hub path (<c>"User/Alice" → "Alice"</c>, else the path verbatim).</summary>
    private static string OwnerIdOf(string nodePath) => nodePath.StartsWith("User/") ? nodePath[5..] : nodePath;

    /// <summary>
    /// The owner's home page — ONE editable markdown page, 1:1 with the Space Overview body
    /// (<c>SpaceLayoutAreas.BuildBodyContent</c>): render the user's <see cref="User.Body"/> when set,
    /// else the <see cref="UserWelcomeMarkdown"/> template. <see cref="MarkdownControl.NodePath"/> is
    /// the user hub path so the page's relative <c>@@("area/…")</c> embeds resolve to this hub's
    /// region areas (Pinned / Threads / Catalog / Composer). There is no bespoke control stack and no
    /// per-segment override — the page IS the override surface. Kept <c>internal</c> so the
    /// default-vs-override behaviour is unit-testable without standing up a hub.
    /// </summary>
    internal static UiControl BuildOwnerHome(string nodePath, string ownerName, MeshNode? ownerNode, JsonSerializerOptions options)
    {
        // ContentAs (not `as User`): the owner-node stream alternates typed↔JsonElement↔null frames.
        var body = ownerNode.ContentAs<User>(options)?.Body;
        var markdown = string.IsNullOrWhiteSpace(body) ? UserWelcomeMarkdown(ownerName) : body!;
        return Controls.Markdown(markdown) with { NodePath = nodePath };
    }

    /// <summary>
    /// The default home page shown until the owner authors their own <see cref="User.Body"/> — a
    /// "Welcome back" heading on top, then the chat composer (start a thread right away) and the home
    /// regions embedded as <c>@@("area/…")</c> blocks (the same mechanism as the Space welcome's
    /// <c>@@("area/Search")</c>), and a small "it's configurable" note at the bottom linking to the
    /// config guide. This is the single source of truth for "the default", shared by the render path
    /// and the unit tests.
    /// </summary>
    internal static string UserWelcomeMarkdown(string ownerName) =>
        $$"""
        ### Welcome back, {{ownerName}}

        @@("area/Composer")

        @@("area/Pinned")

        @@("area/Threads")

        @@("area/Catalog")

        _This home is yours to shape. [It's fully configurable]({{ConfigGuideLink}}): tell the assistant in the chat above what you'd like to see, or edit this page's **Body** directly._
        """;

    // ── Home region areas ────────────────────────────────────────────────────────────────────────
    // Each is embedded by the home page via @@("area/<Name>"). They are registered on the User hub in
    // AddUserActivityLayoutAreas. A null control collapses the region (e.g. Pinned with no pins).

    /// <summary>The pinned-items region — reacts to the owner node so pins appear/disappear live.</summary>
    internal static IObservable<UiControl?> PinnedAreaView(LayoutAreaHost host, RenderingContext _)
    {
        var options = host.Hub.JsonSerializerOptions;
        var syncStream = host.Workspace.GetStream(new MeshNodeReference());
        return syncStream!.Select(change => BuildPinnedItems(change.Value.ContentAs<User>(options)));
    }

    /// <summary>The open-threads region — the owner's own threads that aren't Done yet, newest first.</summary>
    internal static IObservable<UiControl?> ThreadsAreaView(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        return Observable.Return<UiControl?>(BuildOpenThreads(nodePath, OwnerIdOf(nodePath)));
    }

    /// <summary>The catalog region — the declarative TabsControl + MeshSearches.</summary>
    internal static IObservable<UiControl?> CatalogAreaView(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        return Observable.Return<UiControl?>(BuildCatalog(nodePath, OwnerIdOf(nodePath)));
    }

    /// <summary>
    /// The chat composer region — the SAME <see cref="ThreadChatControl"/> the side panel mounts for a
    /// new chat (Monaco editor, harness/agent/model selectors, attachments, Send). Rendered INLINE as a
    /// pure layout area on the already-alive user hub — it is NOT routed through a backing
    /// <c>{owner}/Chat</c> mesh node. The control is self-contained: <c>ThreadChatView</c> resolves the
    /// signed-in user from the circuit and anchors a new thread under the current page / the user's home
    /// (never the hosting hub's address), so hosting it here is 1:1 with the side-panel composer.
    /// <para>Rendering inline (rather than <c>Controls.LayoutArea("{owner}/Chat", "Overview")</c>)
    /// removes the entire "the node must exist or the embedded area 404s (<c>No node found at
    /// '{owner}/Chat'</c>)" failure class the previous on-demand-create design carried — there is
    /// nothing left to create, race, or fail to provision.</para>
    /// </summary>
    internal static IObservable<UiControl?> ComposerAreaView(LayoutAreaHost host, RenderingContext _)
        // HideEmptyState = the compact/dashboard composer: renders just the input (no inline
        // message-history area) AND, on submit, opens the new thread FULL-SCREEN in the main pane —
        // ThreadChatView reads HideEmptyState as `isCompact` → NavigateTo("/{path}") — instead of the
        // side panel. The home composer is exactly the dashboard case the flag was designed for;
        // without it the home submit opened the thread in the side pane.
        => Observable.Return<UiControl?>(new ThreadChatControl().WithHideEmptyState(true));

    /// <summary>
    /// The owner's OPEN threads — their own partition only (<c>{owner}/*_Thread</c>, no cross-partition
    /// fan-out), excluding finished ones (<c>-content.status:Done</c>), newest first; "New thread"
    /// creates under the user node. Mirrors what used to be the catalog's first tab, promoted to its own
    /// region so active conversations sit right at the top of the home.
    /// </summary>
    private static UiControl BuildOpenThreads(string nodePath, string nodeOwnerId) =>
        Controls.MeshSearch
            .WithTitle("Open threads")
            .WithHiddenQuery($"namespace:{nodeOwnerId}/*_Thread nodeType:Thread -content.status:Done sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithItemLimit(50)
            .WithMaxRows(2)
            .WithMaxColumns(4)
            .WithReactiveMode(true)
            // "Create New" must NOT raw-create a Thread node — CreateNodeType="Thread" does a bare CreateNode
            // that BYPASSES StartThread (AGENTS.md-forbidden: a hand-assembled Thread has no submission wiring
            // / composer, so it renders as an empty message box). Instead navigate to the per-user new-chat
            // composer at /{owner}/Chat (the ChatArea layout area — node-less ThreadChatControl); sending
            // there starts a proper thread via StartThread.
            .WithCreateHref($"/{nodeOwnerId}/{ChatArea}");

    /// <summary>
    /// The fluent catalog: a skinned <see cref="TabsControl"/> whose tabs are labelled
    /// <see cref="MeshSearchControl"/>s, declared via <c>WithMeshSearch</c>. Each tab maps a
    /// label + scoped query (and create/empty-state) from what used to be the per-tab
    /// <c>Build*</c> helpers — now folded into the declarative builder. The TabsControl owns tab
    /// switching, so there is no host-data tab-state and no CSS-flex toggling.
    /// </summary>
    internal static UiControl BuildCatalog(string nodePath, string nodeOwnerId)
        => Controls.Tabs
            // 100% width so the tabs + their search grids fill the home page instead of shrinking to
            // content width (the TabsControl had no Width skin before — FluentTabs defaulted to fit).
            .WithSkin(s => s.WithWidth("100%"))
            // Spaces the user can read — a FULL mesh search control scoped to nodeType:Space: the
            // search bar is shown and the view-options ("settings") bar is enabled so the user can
            // group/adjust and reveal the otherwise-hidden search. New space → the standard top-level
            // create form (Space's DefaultNamespace is "" → top-level, the only sanctioned way to make
            // a new partition).
            .WithMeshSearch(TabSpaces,
                nodeType: "Space",
                query: "is:main sort:LastModified-desc",
                placeholder: "Search spaces…",
                configure: s => s
                    .WithShowSearchBox(true).WithViewOptions(true).WithShowEmptyMessage(true)
                    .WithRenderMode(MeshSearchRenderMode.Flat)
                    .WithCollapsibleSections(false).WithSectionCounts(false)
                    .WithMaxColumns(4).WithItemLimit(50).WithMaxRows(3).WithReactiveMode(true)
                    .WithCreateHref("/create?type=Space"))
            // My Items — the owner's own partition (rbuergi.* post-v10), grouped by type.
            .WithMeshSearch(TabMyItems,
                @namespace: nodeOwnerId,
                query: "is:main context:search sort:LastModified-desc",
                configure: s => s
                    .WithShowEmptyMessage(true).WithRenderMode(MeshSearchRenderMode.Grouped)
                    .WithSortBy("LastModified", ascending: false).WithSectionCounts(true)
                    .WithItemLimit(50).WithMaxRows(3).WithMaxColumns(4)
                    .WithCollapsibleSections(true).WithReactiveMode(true)
                    .WithCreateHref($"/create?type=Markdown&namespace={Uri.EscapeDataString(nodeOwnerId)}"))
            // Last Read — recently accessed nodes (excluding this dashboard node itself).
            .WithMeshSearch(TabLastRead,
                query: $"source:accessed scope:subtree is:main sort:LastModified-desc -path:{nodePath}",
                configure: s => s
                    .WithShowSearchBox(false).WithShowEmptyMessage(true)
                    .WithRenderMode(MeshSearchRenderMode.Flat)
                    .WithCollapsibleSections(false).WithSectionCounts(false)
                    .WithMaxColumns(1).WithItemLimit(20).WithMaxRows(4).WithReactiveMode(true))
            // Last Edited — the activity feed (main content nodes with recent changes).
            .WithMeshSearch(TabLastEdited,
                query: "source:activity scope:subtree is:main sort:LastModified-desc",
                configure: s => s
                    .WithShowSearchBox(false).WithRenderMode(MeshSearchRenderMode.Flat)
                    .WithCollapsibleSections(false).WithSectionCounts(false)
                    .WithMaxColumns(2).WithItemLimit(50).WithMaxRows(4).WithReactiveMode(true));

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
    /// Pinned items — compact cards of everything in the owner's <see cref="User.PinnedPaths"/>.
    /// Each card is rendered via <see cref="PinLayoutArea.PinnedThumbnailArea"/>, which overlays
    /// an unpin icon so owners can remove items inline. Returns <c>null</c> when nothing is pinned.
    /// <para>Takes the already-deserialized <see cref="User"/> (the caller reads it via
    /// <c>ContentAs&lt;User&gt;</c>, never <c>as User</c> — the owner-node stream alternates
    /// typed↔JsonElement frames, and <c>as</c> → null on JsonElement frames flips the band in/out,
    /// the render storm that vanished the home on chat launch).</para>
    /// </summary>
    internal static UiControl? BuildPinnedItems(User? user)
    {
        var pinnedPaths = user?.PinnedPaths;
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

}
