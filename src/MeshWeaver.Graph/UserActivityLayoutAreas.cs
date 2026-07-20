using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Reactive.Linq;
using System.Text.Json;
using System.Threading.Tasks;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Application.Styles;
using MeshWeaver.Data;
using MeshWeaver.Graph.Configuration;
using MeshWeaver.Layout;
using MeshWeaver.Layout.Composition;
using MeshWeaver.Markdown;
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

    /// <summary>Area that clears the owner's <see cref="User.Body"/> override so the default welcome home returns.</summary>
    public const string ResetHomeArea = "ResetHome";

    /// <summary>
    /// Area name for the public profile page (<c>/{user}/Profile</c>) — the polished, read-only
    /// showcase every visitor sees, and the owner's preview + entry point to the editor.
    /// </summary>
    public const string ProfileArea = "Profile";

    /// <summary>
    /// Area name for the owner-only profile editor (bio, links, showcase) — node-bound editors that
    /// auto-persist to the User node. Access-gated on <see cref="Permission.Update"/> (self-edit only).
    /// </summary>
    public const string EditProfileArea = "EditProfile";

    /// <summary>Link to the doc page that explains the configurable Body-page + <c>@@</c>-region model.</summary>
    internal const string ConfigGuideLink = "/Doc/GUI/ConfigurablePages";

    private const string ThinScrollbar = "scrollbar-width: thin; scrollbar-color: rgba(128,128,128,0.3) transparent;";

    /// <summary>
    /// Section title for the additive "Shared with me" band (#385) — the cross-partition modules the
    /// caller was specifically invited into, which the unified <c>is:main</c> catalog query can't
    /// reach on its own (they're readable by URL but invisible to a scope search). See BuildCatalog.
    /// </summary>
    private const string SharedWithMeTitle = "Shared with me";

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
            .WithView(ChatArea, ComposerAreaView)
            // Override the generic Edit area with the SAFE per-field Body editor. Editing a
            // partition-root node generically is suppressed in the default node menu (it could
            // rewrite the whole partition); this edits THIS page only — User.Body — 1:1 with the
            // Space Body editor. See EditHome / BuildHomeBodyEditor.
            .WithView(MeshNodeLayoutAreas.EditArea, EditHome)
            // Clears User.Body → the welcome template returns; reached from the Reset menu item.
            .WithView(ResetHomeArea, ResetHome)
            // The polished public profile (read-only showcase) and its owner-only, node-bound editor.
            .WithView(ProfileArea, ProfileAreaView)
            .WithView(EditProfileArea, EditProfile))
            // Re-enable Edit on the user home (the default node menu HIDES generic Edit on a
            // protected partition root) and add a Reset-to-default item once the owner has
            // authored a Body override.
            .AddNodeMenuItems(HomePageMenuItems);

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
        var options = host.Hub.JsonSerializerOptions;
        // Email is PII on the world-readable User node (#471): the visitor profile reveals it ONLY to
        // the subject or a global admin. The owner never lands here (they get the dashboard); every
        // other viewer starts REDACTED and the email is revealed only once global-admin is confirmed.
        var canSeeEmail = CanSeeEmailStream(host.Hub, capturedAccessContext, isOwner);

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
            .CombineLatest(canSeeEmail, (change, showEmail) => (change, showEmail))
            .Select(t =>
            {
                var ownerNode = t.change.Value;
                var ownerName = ownerNode?.Name ?? nodeOwnerId;

                if (isOwner)
                    return (UiControl?)BuildOwnerHome(nodePath, ownerName, ownerNode, options);
                return (UiControl?)BuildProfile(nodePath, nodeOwnerId, ownerName, ownerNode,
                    isOwner: false, canSeeEmail: t.showEmail, options);
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
    /// <see cref="BuildProfile"/>. Accepts either:
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

        @@("area/Threads")

        @@("area/Catalog")

        @@("area/Pinned")

        _This home is yours to shape. [It's fully configurable]({{ConfigGuideLink}}): tell the assistant in the chat above what you'd like to see, or edit this page's **Body** directly._
        """;

    // ── Editable home: Edit (this page's Body) + Reset-to-default ─────────────────────────────────

    /// <summary>
    /// The User node's <c>Edit</c> area override — the SAFE, per-field editor for the owner's home
    /// <see cref="User.Body"/> markdown page, mirroring <c>SpaceLayoutAreas.Edit</c>. It replaces the
    /// generic property/content Edit so "Edit" on a user home edits THIS page, never rewrites the
    /// partition root. Gated on <see cref="Permission.Update"/> (self-edit → the owner only).
    /// </summary>
    public static IObservable<UiControl?> EditHome(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var options = host.Hub.JsonSerializerOptions;
        return host.Workspace.GetMeshNodeStream().CombineLatest(
            host.Hub.GetEffectivePermissions(hubPath),
            (node, permissions) => !permissions.HasFlag(Permission.Update)
                ? (UiControl?)MeshNodeLayoutAreas.BuildAccessDenied(hubPath)
                : (UiControl?)BuildHomeBodyEditor(node, hubPath, options));
    }

    /// <summary>
    /// The home-page body editor: a back link, a "Reset to default" action shown only when the Body is
    /// set, and the SAME <see cref="MarkdownEditorControl"/> the Markdown node uses — bound to the
    /// <c>body</c> content field via a node-bound <c>DataContext</c> so each edit is a per-field
    /// read-modify-write to <see cref="User.Body"/> (never a whole-content replace). An empty Body ⇒
    /// the <see cref="UserWelcomeMarkdown"/> default renders (see <see cref="BuildOwnerHome"/>).
    /// </summary>
    private static UiControl BuildHomeBodyEditor(MeshNode? node, string hubPath, JsonSerializerOptions options)
    {
        if (node is null)
            return Controls.Markdown("*Home page not found.*");

        var userPath = node.Path ?? hubPath;
        var contentCtx = LayoutAreaReference.GetMeshNodeDataContext(userPath, bindContent: true);
        var hasBody = !string.IsNullOrWhiteSpace(node.ContentAs<User>(options)?.Body);

        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("height: calc(100vh - 100px); display: flex; flex-direction: column;");

        var headerRow = Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithWidth("100%")
            .WithVerticalAlignment(VerticalAlignment.Center)
            .WithHorizontalGap(12)
            .WithStyle("padding: 8px 0; border-bottom: 1px solid var(--neutral-stroke-rest); flex-shrink: 0;");

        headerRow = headerRow.WithView(Controls.Button("")
            .WithIconStart(FluentIcons.ArrowLeft())
            .WithAppearance(Appearance.Stealth)
            .WithNavigateToHref($"/{userPath}"));

        headerRow = headerRow.WithView(Controls.Html(
            "<span style=\"flex: 1; font-size: 1.25rem; font-weight: 600;\">Edit your home page</span>"));

        // "Reset to default" — the in-editor twin of the Reset menu item, shown only when the owner has
        // overridden the home. Clears User.Body → the welcome template returns.
        if (hasBody)
            headerRow = headerRow.WithView(Controls.Button("Reset to default")
                .WithAppearance(Appearance.Stealth)
                .WithClickAction(ClearBodyAction(userPath)));

        headerRow = headerRow.WithView(Controls.Html(
            "<span style=\"color: var(--neutral-foreground-hint); font-size: 0.85rem;\">Changes are saved automatically</span>"));

        container = container.WithView(headerRow);

        var editor = new MarkdownEditorControl
        {
            Value = new JsonPointerReference("body"),
            DataContext = contentCtx,
            Height = "100%",
            MaxHeight = "none",
            Placeholder = "Write your home page in markdown… leave it empty to use the default."
        };

        container = container.WithView(Controls.Stack
            .WithWidth("100%")
            .WithStyle("flex: 1; width: 100%; min-height: 0; overflow: hidden; margin-top: 8px;")
            .WithView(editor));

        return container;
    }

    /// <summary>
    /// The <see cref="ResetHomeArea"/> handler — a menu-reachable action area that clears the owner's
    /// <see cref="User.Body"/> (one-shot read → transform → <see cref="DataChangeRequest"/> on the user
    /// hub, the pin/unpin write pattern), then renders a confirmation linking back to the (now default)
    /// home. No-op when the Body is already empty.
    /// </summary>
    public static IObservable<UiControl?> ResetHome(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var backHref = MeshNodeLayoutAreas.BuildUrl(hubPath, ActivityArea);
        var userAddress = host.Hub.Address;

        host.Hub.GetMeshNode(hubPath, TimeSpan.FromSeconds(10))
            .Subscribe(node =>
            {
                if (node?.Content is not User user || string.IsNullOrWhiteSpace(user.Body))
                    return;
                var newNode = node with { Content = user with { Body = null } };
                host.Hub.Post(new DataChangeRequest { Updates = [newNode] }, o => o.WithTarget(userAddress));
            });

        return Observable.Return<UiControl?>(Controls.Markdown(
            $"### Home reset to default\n\nYour home page now shows the default welcome layout. [Back to your home]({backHref})"));
    }

    /// <summary>
    /// A <c>WithClickAction</c> that clears <see cref="User.Body"/> on the user node at
    /// <paramref name="userPath"/> — one-shot read, null the Body, post a <see cref="DataChangeRequest"/>
    /// to the owning hub (which echoes to subscribers, so the editor / home re-renders to the default).
    /// </summary>
    private static Func<UiActionContext, Task> ClearBodyAction(string userPath) => ctx =>
    {
        var userAddress = new Address(userPath);
        ctx.Host.Hub.GetMeshNode(userPath, TimeSpan.FromSeconds(10))
            .Subscribe(node =>
            {
                if (node?.Content is not User user) return;
                var newNode = node with { Content = user with { Body = null } };
                ctx.Host.Hub.Post(new DataChangeRequest { Updates = [newNode] }, o => o.WithTarget(userAddress));
            });
        return Task.CompletedTask;
    };

    /// <summary>
    /// Node-menu items for the user home: re-adds <b>Edit</b> (the default provider suppresses generic
    /// Edit on a protected partition root, but our Edit override is the safe Body editor) and, once the
    /// owner has authored a <see cref="User.Body"/>, a <b>Reset to default</b> item. Both require
    /// <see cref="Permission.Update"/> (self-edit → owner only), so visitors see neither.
    /// </summary>
    private static IObservable<IReadOnlyCollection<NodeMenuItemDefinition>> HomePageMenuItems(
        LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var options = host.Hub.JsonSerializerOptions;
        return host.Workspace.GetMeshNodeStream().CombineLatest(
            host.Hub.GetEffectivePermissions(hubPath),
            (node, permissions) =>
            {
                var items = new List<NodeMenuItemDefinition>();
                if (permissions.HasFlag(Permission.Update))
                {
                    items.Add(new NodeMenuItemDefinition(
                        "View public profile", ProfileArea,
                        Icon: "👤", RequiredPermission: Permission.Update, Order: 5,
                        Href: MeshNodeLayoutAreas.BuildUrl(hubPath, ProfileArea),
                        Tooltip: "Preview your public profile as visitors see it"));
                    items.Add(new NodeMenuItemDefinition(
                        "Edit profile", EditProfileArea,
                        Icon: "🪪", RequiredPermission: Permission.Update, Order: 6,
                        Href: MeshNodeLayoutAreas.BuildUrl(hubPath, EditProfileArea),
                        Tooltip: "Edit your bio, links, and showcase"));
                    items.Add(new NodeMenuItemDefinition(
                        "Edit home page", MeshNodeLayoutAreas.EditArea,
                        Icon: "✏️", RequiredPermission: Permission.Update, Order: 10,
                        Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.EditArea),
                        Tooltip: "Edit this home page's markdown"));

                    if (!string.IsNullOrWhiteSpace(node.ContentAs<User>(options)?.Body))
                        items.Add(new NodeMenuItemDefinition(
                            "Reset home to default", ResetHomeArea,
                            Icon: "↩️", RequiredPermission: Permission.Update, Order: 11,
                            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, ResetHomeArea),
                            Tooltip: "Discard your custom home and use the default layout"));
                }
                return (IReadOnlyCollection<NodeMenuItemDefinition>)items;
            });
    }

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

    /// <summary>The catalog region — ONE unified, grouped "everything" <see cref="MeshSearchControl"/>
    /// (see <see cref="BuildCatalog"/>), plus an additive "Shared with me" band for the caller's
    /// cross-partition grants (#385) that a broad query can't reach — see <see cref="ObserveSharedTargets"/>.</summary>
    internal static IObservable<UiControl?> CatalogAreaView(LayoutAreaHost host, RenderingContext _)
    {
        var ownerId = OwnerIdOf(host.Hub.Address.ToString());
        var options = host.Hub.JsonSerializerOptions;
        // The home's DISPLAY CONFIG is DATA-DRIVEN: read the admin-editable Admin/HomeConfig platform
        // node reactively (shipped defaults when absent), so an admin's edit updates every open home
        // LIVE — no code change, no image roll. Combined with the caller's cross-partition grants
        // (#385, the one thing a first-level query can't reach). Both start with a value so the home
        // paints instantly.
        return HomeConfigNodeType.Observe(host.Workspace, options)
            .CombineLatest(ObserveSharedTargets(host, ownerId),
                (config, shared) => (UiControl?)BuildCatalog(ownerId, config, shared));
    }

    /// <summary>
    /// The cross-partition scopes the owner has been granted access to — an invited module living in
    /// ANOTHER partition, reachable by URL but otherwise invisible in nav (the #385 symptom). Sourced
    /// from the owner's <c>AccessAssignment</c> satellites (<c>content.accessObject == ownerId</c>),
    /// fanned out cross-partition and access-filtered, each resolved to its governed target scope
    /// (<see cref="MeshNode.MainNode"/>). Starts empty so the home paints instantly; grants land
    /// reactively. No security surface changes — it only READS the caller's own readable grants.
    /// </summary>
    private static IObservable<IReadOnlyList<string>> ObserveSharedTargets(LayoutAreaHost host, string ownerId)
    {
        var mesh = host.Hub.ServiceProvider.GetService<IMeshService>();
        if (mesh is null || string.IsNullOrEmpty(ownerId))
            return Observable.Return<IReadOnlyList<string>>([]);
        return mesh
            .Query<MeshNode>(MeshQueryRequest.FromQuery(
                $"nodeType:AccessAssignment content.accessObject:{ownerId}"))
            .Scan(ImmutableDictionary<string, MeshNode>.Empty,
                (map, change) =>
                {
                    if (change.ChangeType is QueryChangeType.Initial or QueryChangeType.Reset)
                        return change.Items.ToImmutableDictionary(n => n.Path);
                    foreach (var item in change.Items)
                        map = change.ChangeType switch
                        {
                            QueryChangeType.Added or QueryChangeType.Updated => map.SetItem(item.Path, item),
                            QueryChangeType.Removed => map.Remove(item.Path),
                            _ => map
                        };
                    return map;
                })
            .Select(map => SharedTargetPaths(map.Values, ownerId))
            .StartWith((IReadOnlyList<string>)[]);
    }

    /// <summary>
    /// Pure projection: the distinct CROSS-PARTITION target scopes from a set of the owner's
    /// <c>AccessAssignment</c> nodes — each assignment's <see cref="MeshNode.MainNode"/> (the governed
    /// scope; falling back to the scope derived from the node path), keeping only targets that live
    /// OUTSIDE the owner's own partition and are non-empty.
    /// </summary>
    internal static IReadOnlyList<string> SharedTargetPaths(IEnumerable<MeshNode> assignments, string ownerId)
        => assignments
            // Normalise via ScopeOfAssignment: MainNode may hold the governed scope directly OR the
            // satellite path (MeshNode.MainNode defaults to the node's own path). ScopeOfAssignment
            // strips a trailing …/_Access/… segment and returns a plain scope unchanged; fall back to
            // the node path when MainNode is unset.
            .Select(a => AccessSubjectQueries.ScopeOfAssignment(
                string.IsNullOrEmpty(a.MainNode) ? a.Path : a.MainNode)?.Trim('/'))
            .Where(scope => !string.IsNullOrEmpty(scope))
            .Select(scope => scope!)
            .Where(scope => !string.Equals(
                AccessSubjectQueries.Partition(scope), ownerId, StringComparison.OrdinalIgnoreCase))
            .Distinct(StringComparer.OrdinalIgnoreCase)
            .OrderBy(s => s, StringComparer.OrdinalIgnoreCase)
            .ToList();

    /// <summary>
    /// NodeType of a legacy home-catalog EXTENSION TAB. The extension-tab row was folded into the ONE
    /// unified, grouped catalog search (see <see cref="BuildCatalog"/>): a tab node's CONTENT (e.g. a
    /// plugin's courses) already surfaces in the cross-partition <c>is:main</c> list under its type
    /// section, so a plugin no longer needs a tab. The node type stays REGISTERED
    /// (<c>HomeTabNodeType.AddHomeTabType</c>) so existing HomeTab nodes remain valid and don't orphan;
    /// they simply no longer render a separate tab.
    /// </summary>
    public const string HomeTabNodeType = "HomeTab";

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

    // ── First-level catalog queries ────────────────────────────────────────────────────────────────
    // The home is a SHALLOW, first-level index — NOT a full-tree dump. Each sort order is a UNION of two
    // sub-queries (newline-joined; MeshSearchView issues them as a MeshQueryRequest union, sort/limit
    // taken from the FIRST):
    //   1. `namespace:` (empty) → the root-level partition nodes the reader can see — spaces,
    //      courses/plugins, their own home root (default scope is children-of-root = the roots).
    //   2. `namespace:{ownerId}` → the user's OWN top-level home items (default scope children = the
    //      DIRECT children of their home partition root).
    // Neither spans a subtree, so no deep `…/Introduction/Exercise/…` nodes leak in.
    /// <summary>Builds the two-query first-level UNION for a given sort/source suffix (newline-joined).</summary>
    private static string FirstLevelUnion(string ownerId, string sortSuffix) =>
        $"namespace: is:main context:search {sortSuffix}\n" +
        $"namespace:{ownerId} is:main context:search {sortSuffix}";

    /// <summary>The catalog query for a scope + sort suffix: the first-level union (partition roots + the
    /// user's home children), or a cross-partition SUBTREE query (everything the viewer can read at every
    /// depth) when <see cref="HomeConfig.Scope"/> selects <see cref="HomeCatalogScope.Subtree"/>.</summary>
    private static string CatalogQuery(HomeCatalogScope scope, string ownerId, string sortSuffix) =>
        scope == HomeCatalogScope.Subtree
            ? $"is:main context:search {sortSuffix}"
            : FirstLevelUnion(ownerId, sortSuffix);

    // The three user-selectable sort orders (the view-options "Sort by" dropdown). LAST ACCESSED:
    // source:accessed JOINs the user's UserActivity satellite and projects its timestamp into
    // last_modified, so the list is ordered by the user's own access recency (it supersedes the old
    // "Last Read" tab). LAST MODIFIED / ALPHABETICAL drop source:accessed, ordering by edit-recency /
    // name. Immutable constant lookup — enum · label · query suffix.
    private const string SortSuffixLastAccessed = "source:accessed sort:LastModified-desc";
    private const string SortSuffixLastModified = "sort:LastModified-desc";
    private const string SortSuffixAlphabetical = "sort:Name-asc";
    private static readonly (HomeCatalogSort Sort, string Label, string Suffix)[] CatalogSorts =
    {
        (HomeCatalogSort.LastAccessed, "Last accessed", SortSuffixLastAccessed),
        (HomeCatalogSort.LastModified, "Last modified", SortSuffixLastModified),
        (HomeCatalogSort.Alphabetical, "Alphabetical", SortSuffixAlphabetical),
    };

    /// <summary>
    /// The catalog region — ONE tab-less list, whose shape is DATA-DRIVEN by <paramref name="config"/>
    /// (the admin-editable <c>Admin/HomeConfig</c> platform node; <c>null</c> ⇒
    /// <see cref="HomeConfigNodeType.Defaults"/> = <b>FirstLevel + Flat + LastAccessed</b>). The config
    /// drives the depth (first-level top-level entries vs the full subtree), the render (flat list vs
    /// grouped-by-type sections), and the default sort — and a view-options "Sort by" control still lets
    /// the user pick <b>Last accessed</b> / <b>Last modified</b> / <b>Alphabetical</b> at will. FIRST-LEVEL
    /// shows only the partition roots (spaces, courses, plugins) the viewer can read plus their own
    /// top-level home items — NOT the whole tree. This REPLACES the former Spaces / My Items / Last Read /
    /// Last Edited tab row AND the data-driven extension tabs.
    /// <para>The one thing a first-level query can't reach is a module in ANOTHER partition the caller was
    /// specifically invited into (#385): those are resolved from the caller's own readable
    /// <c>AccessAssignment</c> grants (<paramref name="sharedTargets"/>) and appended as an additive
    /// "Shared with me" band, present ONLY when the caller actually has such grants.</para>
    /// <para>Pure (no hub) so the catalog shape is unit-testable without standing up a hub.</para>
    /// </summary>
    internal static UiControl BuildCatalog(
        string nodeOwnerId, HomeConfig? config = null, IReadOnlyList<string>? sharedTargets = null)
    {
        var cfg = config ?? HomeConfigNodeType.Defaults;

        // Sort options, DEFAULT first (so the dropdown's default selection == HiddenQuery). Each option
        // carries its full catalog query (first-level union or subtree, per cfg.Scope); the query itself
        // carries the sort/source, so no client-side WithSortBy (that would override the query order).
        var sortOptions = CatalogSorts
            .OrderByDescending(s => s.Sort == cfg.DefaultSort)
            .Select(s => new MeshSearchSortOption(s.Label, CatalogQuery(cfg.Scope, nodeOwnerId, s.Suffix)))
            .ToArray();

        var everything = Controls.MeshSearch
            .WithHiddenQuery(sortOptions[0].Query)
            .WithSortOptions(sortOptions)
            .WithShowSearchBox(true)
            .WithViewOptions(true)
            .WithShowEmptyMessage(true)
            .WithRenderMode(cfg.Render == HomeCatalogRender.Grouped
                ? MeshSearchRenderMode.Grouped
                : MeshSearchRenderMode.Flat)
            .WithItemLimit(50)
            .WithMaxRows(6)
            .WithMaxColumns(4)
            .WithReactiveMode(true)
            .WithCreateHref("/create");

        // Grouped render → collapsible per-type sections with counts (flat is the default, no grouping).
        if (cfg.Render == HomeCatalogRender.Grouped)
            everything = everything.WithSectionCounts(true).WithCollapsibleSections(true);

        // No cross-partition invitations → the catalog IS the single list.
        if (sharedTargets is not { Count: > 0 })
            return everything;

        // Additive #385 band: modules in OTHER partitions the caller was invited into, which the broad
        // is:main query can't reach (readable by URL but invisible to a scope search). The `path:a|b|c`
        // alternation resolves each target node, access-filtered by the mesh.
        var pathList = string.Join("|", sharedTargets);
        var shared = Controls.MeshSearch
            .WithTitle(SharedWithMeTitle)
            .WithHiddenQuery($"path:{pathList} is:main sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithMaxColumns(4)
            .WithItemLimit(50)
            .WithMaxRows(3)
            .WithReactiveMode(true);

        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("gap: 24px; width: 100%;")
            .WithView(everything)
            .WithView(shared);
    }

    // ── Public profile + owner-editable showcase ───────────────────────────────────────────────────

    /// <summary>
    /// The public profile area (<see cref="ProfileArea"/>, <c>/{user}/Profile</c>) — the polished,
    /// read-only showcase every viewer sees, and the owner's preview + entry point to the editor.
    /// Reacts to the owner node so edits appear live; email is revealed only to the owner or a global
    /// admin (fail-closed: hidden until admin status resolves).
    /// </summary>
    public static IObservable<UiControl?> ProfileAreaView(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        var nodeOwnerId = OwnerIdOf(nodePath);
        var options = host.Hub.JsonSerializerOptions;

        var accessService = host.Hub.ServiceProvider.GetService<AccessService>();
        var captured = accessService?.Context ?? accessService?.CircuitContext;
        var isOwner = IsViewerOwner(captured, nodeOwnerId);
        // Email PII gate (#471): owner or global admin only — fail-closed until admin is confirmed.
        var canSeeEmail = CanSeeEmailStream(host.Hub, captured, isOwner);
        var areaLogger = host.Hub.ServiceProvider.GetService<ILoggerFactory>()
            ?.CreateLogger("MeshWeaver.Graph.UserActivityLayoutAreas");

        var syncStream = host.Workspace.GetStream(new MeshNodeReference());
        return syncStream!
            .CombineLatest(canSeeEmail, (change, showEmail) =>
            {
                var ownerNode = change.Value;
                var ownerName = ownerNode?.Name ?? nodeOwnerId;
                return (UiControl?)BuildProfile(nodePath, nodeOwnerId, ownerName, ownerNode,
                    isOwner, canSeeEmail: showEmail, options);
            })
            // Same narrow guard as Activity: arm the timeout for the FIRST emission only (surface an
            // unreachable owner hub) and disarm it thereafter (an idle data-bound view must not tear
            // itself down between edits). A first-snapshot timeout / read denial throws, never swallows.
            .Timeout(Observable.Timer(TimeSpan.FromSeconds(30)), _ => Observable.Never<long>())
            .Catch<UiControl?, Exception>(ex =>
            {
                areaLogger?.LogWarning(ex,
                    "[UserActivity.Profile] profile unavailable for {NodePath}", nodePath);
                return Observable.Throw<UiControl?>(
                    new InvalidOperationException($"Profile unavailable for '{nodePath}'.", ex));
            });
    }

    /// <summary>
    /// The owner-only profile editor (<see cref="EditProfileArea"/>) — node-bound markdown editors for
    /// the bio and links plus inline showcase curation, gated on <see cref="Permission.Update"/>
    /// (self-edit → the owner only; visitors get access-denied). Mirrors <see cref="EditHome"/>.
    /// </summary>
    public static IObservable<UiControl?> EditProfile(LayoutAreaHost host, RenderingContext _)
    {
        var hubPath = host.Hub.Address.ToString();
        var options = host.Hub.JsonSerializerOptions;
        return host.Workspace.GetMeshNodeStream().CombineLatest(
            host.Hub.GetEffectivePermissions(hubPath),
            (node, permissions) => !permissions.HasFlag(Permission.Update)
                ? (UiControl?)MeshNodeLayoutAreas.BuildAccessDenied(hubPath)
                : (UiControl?)BuildProfileEditor(node, hubPath, options));
    }

    /// <summary>
    /// The profile editor body: a back link, node-bound <see cref="MarkdownEditorControl"/>s for the
    /// bio and links (each edit is a per-field read-modify-write straight to the User node — the same
    /// node-bound DataContext pattern as <see cref="BuildHomeBodyEditor"/>: ONE source of truth, no
    /// <c>/data</c> replica, no save subscription), and the showcase rendered with the inline unpin
    /// overlay so the owner curates pins in place. Built from layout-area controls only.
    /// </summary>
    internal static UiControl BuildProfileEditor(MeshNode? node, string hubPath, JsonSerializerOptions options)
    {
        if (node is null)
            return Controls.Markdown("*Profile not found.*");

        var userPath = node.Path ?? hubPath;
        var ownerId = OwnerIdOf(userPath);
        var contentCtx = LayoutAreaReference.GetMeshNodeDataContext(userPath, bindContent: true);
        var pins = node.ContentAs<User>(options)?.PinnedPaths;

        var container = Controls.Stack
            .WithWidth("100%")
            .WithStyle("gap: 20px; width: 100%; padding: 0 4px 24px;");

        // Header: back to the profile + auto-save hint (Label controls, never raw HTML).
        container = container.WithView(Controls.Stack
            .WithOrientation(Orientation.Horizontal)
            .WithVerticalAlignment(VerticalAlignment.Center)
            .WithHorizontalGap(12)
            .WithStyle("padding: 8px 0; border-bottom: 1px solid var(--neutral-stroke-rest);")
            .WithView(Controls.Button("")
                .WithIconStart(FluentIcons.ArrowLeft())
                .WithAppearance(Appearance.Stealth)
                .WithNavigateToHref($"/{userPath}/{ProfileArea}"))
            .WithView(Controls.H3("Edit your profile").WithStyle("margin: 0; flex: 1;"))
            .WithView(Controls.Label("Changes are saved automatically")
                .WithStyle("color: var(--neutral-foreground-hint); font-size: 0.85rem;")));

        // Bio — node-bound markdown editor (JsonPointer "bio" against the User content context).
        container = container.WithView(BuildProfileSection("Bio", new MarkdownEditorControl
        {
            Value = new JsonPointerReference("bio"),
            DataContext = contentCtx,
            Height = "160px",
            MaxHeight = "none",
            Placeholder = "A sentence or two about what you do…"
        }));

        // Links — node-bound markdown editor (one markdown link per line).
        container = container.WithView(BuildProfileSection("Links", new MarkdownEditorControl
        {
            Value = new JsonPointerReference("links"),
            DataContext = contentCtx,
            Height = "140px",
            MaxHeight = "none",
            Placeholder = "One link per line, e.g. [GitHub](https://github.com/you)"
        }));

        // Showcase — pinned cards with the inline unpin overlay; a note on how to add more.
        container = container.WithView(BuildProfileSection("Showcase",
            Controls.Stack
                .WithStyle("gap: 8px; width: 100%;")
                .WithView(Controls.Markdown(
                    "Pin any space, doc, agent, or example from its menu to feature it here — " +
                    "hover a card to unpin it."))
                .WithView(BuildShowcase(ownerId, pins, ownerView: true))));

        return container;
    }

    /// <summary>
    /// The reactive "may this viewer see the profile's email?" gate (#471 PII). True for the subject
    /// (<paramref name="isOwner"/>); for anyone else it is a global-admin check on the live
    /// AccessAssignment stream. Anonymous / virtual / non-owner viewers start REDACTED (secure default);
    /// <c>StartWith(false)</c> renders the profile immediately with the email hidden and reveals it only
    /// once admin status is confirmed, and <c>DistinctUntilChanged</c> drops the duplicate initial false.
    /// </summary>
    private static IObservable<bool> CanSeeEmailStream(IMessageHub hub, AccessContext? viewer, bool isOwner)
    {
        var viewerId = viewer?.ObjectId;
        return isOwner || string.IsNullOrEmpty(viewerId) || viewer?.IsVirtual == true
            ? Observable.Return(isOwner)
            : hub.IsGlobalAdmin(viewerId).StartWith(false).DistinctUntilChanged();
    }

    /// <summary>
    /// The polished public profile — cover/avatar + display name, an opt-in bio and links block, and a
    /// curated "Showcase" of the owner's pinned content with a recent-public-content fallback. Rendered
    /// read-only for everyone via layout-area controls (no hand-rolled HTML); the owner reaches the
    /// node-bound editors via <see cref="EditProfileArea"/>. Email is shown ONLY when
    /// <paramref name="canSeeEmail"/> (owner or global admin) — visitors never see it (#471 PII). An
    /// empty profile (no bio, links, or pins) renders the getting-started card instead of empty sections.
    /// Kept <c>internal</c> so the owner/visitor + empty/populated behaviour is unit-testable.
    /// </summary>
    internal static UiControl BuildProfile(
        string nodePath, string ownerId, string ownerName, MeshNode? ownerNode,
        bool isOwner, bool canSeeEmail, JsonSerializerOptions options)
    {
        // ContentAs (not `as User`): the owner-node stream alternates typed↔JsonElement↔null frames.
        var user = ownerNode.ContentAs<User>(options);
        var bio = user?.Bio;
        var links = user?.Links;
        var pins = user?.PinnedPaths;
        var email = canSeeEmail ? user?.Email : null;
        var isEmpty = string.IsNullOrWhiteSpace(bio)
                      && string.IsNullOrWhiteSpace(links)
                      && (pins is null || pins.Count == 0);

        var profile = Controls.Stack
            .WithWidth("100%")
            .WithStyle("display: flex; flex-direction: column; height: 100%; min-height: 0; overflow: hidden;");

        // Header card (avatar + name; email only for owner/admin). Bio renders as its own markdown
        // section below, so it is NOT passed to the header — no duplication.
        profile = profile.WithView(new UserProfileControl()
            .WithNodePath(nodePath)
            .WithDisplayName(ownerName)
            .WithIcon(ownerNode?.Icon)
            .WithEmail(email)
            .WithBio(null));

        var content = Controls.Stack
            .WithStyle("padding: 0 24px 24px; flex: 1; min-height: 0; overflow-y: auto; " + ThinScrollbar);

        // Owner-only inline entry to the node-bound editors.
        if (isOwner)
            content = content.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(8)
                .WithStyle("padding: 8px 0;")
                .WithView(Controls.Button("Edit profile")
                    .WithIconStart(FluentIcons.Edit())
                    .WithAppearance(Appearance.Lightweight)
                    .WithNavigateToHref($"/{nodePath}/{EditProfileArea}")));

        if (isEmpty)
        {
            content = content.WithView(BuildGettingStarted(nodePath, ownerId, ownerName, isOwner));
        }
        else
        {
            if (!string.IsNullOrWhiteSpace(bio))
                content = content.WithView(BuildProfileSection("About", Controls.Markdown(bio!)));
            if (!string.IsNullOrWhiteSpace(links))
                content = content.WithView(BuildProfileSection("Links", Controls.Markdown(links!)));
            content = content.WithView(BuildProfileSection("Showcase",
                BuildShowcase(ownerId, pins, ownerView: false)));

            // Recent activity + items — visibility-filtered to the viewer (only public nodes for visitors).
            content = content.WithView(BuildRecentActivity(ownerId));
            content = content.WithView(BuildProfileItems(ownerId));
        }

        profile = profile.WithView(content);
        return profile;
    }

    /// <summary>A titled profile section — an <c>H3</c> heading (Label control, not HTML) over its body.</summary>
    private static UiControl BuildProfileSection(string title, UiControl body) =>
        Controls.Stack
            .WithStyle("gap: 8px; width: 100%; padding-top: 16px;")
            .WithView(Controls.H3(title).WithStyle("margin: 0; font-size: 1.15rem;"))
            .WithView(body);

    /// <summary>Recent activity by the owner — visibility-filtered so a visitor sees only public nodes.</summary>
    private static UiControl BuildRecentActivity(string ownerId) =>
        Controls.MeshSearch
            .WithTitle("Recent Activity")
            .WithHiddenQuery($"source:activity namespace:{ownerId} scope:subtree is:main sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithMaxColumns(4)
            .WithItemLimit(50)
            .WithMaxRows(2)
            .WithReactiveMode(true);

    /// <summary>The owner's visible child nodes — the security service filters to viewer-visible nodes.</summary>
    private static UiControl BuildProfileItems(string ownerId) =>
        Controls.MeshSearch
            .WithTitle("Items")
            .WithHiddenQuery($"namespace:{ownerId} is:main context:search scope:descendants sort:LastModified-desc")
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Grouped)
            .WithSectionCounts(true)
            .WithItemLimit(50)
            .WithMaxRows(3)
            .WithMaxColumns(4)
            .WithCollapsibleSections(true)
            .WithReactiveMode(true);

    /// <summary>
    /// The Showcase band: the owner's curated pins (<see cref="User.PinnedPaths"/>) rendered as cards,
    /// or — when nothing is pinned — a fallback of the owner's recent public content so the section is
    /// never empty. Visibility is enforced by the search itself (a visitor only ever sees pins they may
    /// read). In the owner's editor (<paramref name="ownerView"/>) each pinned card carries the inline
    /// unpin overlay (<see cref="PinLayoutArea.PinnedThumbnailArea"/>) so the owner can curate in place.
    /// </summary>
    internal static UiControl BuildShowcase(string ownerId, IReadOnlyList<string>? pins, bool ownerView)
    {
        if (pins is { Count: > 0 })
        {
            var pathsClause = string.Join(" OR ", pins);
            var search = Controls.MeshSearch
                .WithHiddenQuery($"path:({pathsClause}) sort:LastModified-desc")
                .WithShowSearchBox(false)
                .WithShowEmptyMessage(false)
                .WithRenderMode(MeshSearchRenderMode.Flat)
                .WithCollapsibleSections(false)
                .WithSectionCounts(false)
                .WithMaxColumns(4)
                .WithGridSpacing(20)
                .WithItemLimit(24)
                .WithMaxRows(2)
                .WithReactiveMode(true);
            if (ownerView)
                search = search.WithItemArea(PinLayoutArea.PinnedThumbnailArea);
            return search;
        }

        // Fallback — the owner's recent public content (visibility-filtered), so the band is never bare.
        return Controls.MeshSearch
            .WithHiddenQuery($"namespace:{ownerId} is:main context:search scope:descendants sort:LastModified-desc")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithMaxColumns(4)
            .WithItemLimit(12)
            .WithMaxRows(2)
            .WithReactiveMode(true);
    }

    /// <summary>Stable control id for the getting-started card — asserted by tests, stable across renders.</summary>
    internal const string GettingStartedId = "profile-getting-started";

    /// <summary>
    /// The friendly getting-started card shown on an EMPTY profile (no bio, links, or pins) — a
    /// persistent, self-documenting starter that renders automatically for EVERY user until they fill
    /// their profile in. There is nothing to seed: the behaviour is inherent in the render path, so it
    /// shows for all users, always, not as a one-time step. For the owner it explains how to add a bio,
    /// links, and pin content, links straight to the editor, and previews their recent work as
    /// inspiration; for a visitor it is a gentle "nothing here yet" plus the owner's recent public work.
    /// Built entirely from layout-area controls (Stack / Markdown / Button / MeshSearch) — no HTML.
    /// </summary>
    internal static UiControl BuildGettingStarted(string nodePath, string ownerId, string ownerName, bool isOwner)
    {
        var card = Controls.Stack
            .WithId(GettingStartedId)
            .WithWidth("100%")
            .WithStyle("gap: 16px; width: 100%; padding: 24px; margin-top: 8px; " +
                       "border: 1px solid var(--neutral-stroke-rest); border-radius: 8px; " +
                       "background: var(--neutral-fill-rest);");

        var intro = isOwner
            ? $$"""
                ### 👋 Welcome, {{ownerName}} — let's set up your profile

                Your profile is how others discover your work. Make it yours:

                - **Bio** — a sentence or two about what you do.
                - **Links** — your GitHub, site, or socials (one markdown link per line).
                - **Showcase** — **pin** your best spaces, docs, agents, or examples to feature them here.

                Use **Edit profile** to add your bio and links. Pin any node from its menu to add it to your Showcase.
                """
            : $"### {ownerName}\n\n{ownerName} hasn't set up their profile yet. Explore their recent public work below.";

        card = card.WithView(Controls.Markdown(intro));

        if (isOwner)
            card = card.WithView(Controls.Stack
                .WithOrientation(Orientation.Horizontal)
                .WithHorizontalGap(8)
                .WithView(Controls.Button("Edit profile")
                    .WithIconStart(FluentIcons.Edit())
                    .WithAppearance(Appearance.Accent)
                    .WithNavigateToHref($"/{nodePath}/{EditProfileArea}")));

        // Recent public content — doubles as "showcase examples" so the card is never bare.
        card = card.WithView(BuildProfileSection(
            isOwner ? "Your recent work" : "Recent public work",
            BuildShowcase(ownerId, null, ownerView: false)));

        return card;
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
