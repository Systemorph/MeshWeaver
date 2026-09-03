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
using MeshWeaver.Utils;
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
            // links, the Threads app tile). Since ChatNodeType was removed there is NO {user}/Chat
            // node any more: the URL resolves to prefix={user} + remainder="Chat", which AreaPage
            // renders as area "Chat" on this hub. Without this registration that's "no renderer
            // for area Chat", and a LEGACY {user}/Chat node from an older deployment resolves as
            // invalid-NodeType ("No node found at '{user}/Chat'… remainder='Chat'" — the prod
            // memex report, 2026-07-02). The page is the node-less THREADS APP (vertical rail of
            // open threads with ✕-close + the composer) — see ThreadsAppView.
            .WithView(ChatArea, ThreadsAppView)
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
    /// catalog embedded as <c>@@("area/…")</c> blocks (the same mechanism as the Space welcome's
    /// <c>@@("area/Search")</c>), and a small "it's configurable" note at the bottom linking to the
    /// config guide. No open-threads band: the THREADS APP (an ordinary <c>{owner}/_App</c> record
    /// on the Apps grid, opening <c>/{owner}/Chat</c>) replaced it — the <c>area/Threads</c> area
    /// stays registered so authored bodies embedding it keep working. This is the single source of
    /// truth for "the default", shared by the render path and the unit tests.
    /// </summary>
    internal static string UserWelcomeMarkdown(string ownerName) =>
        $$"""
        ### Welcome back, {{ownerName}}

        @@("area/Composer")

        @@("area/Catalog")

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
                : (UiControl?)BuildHomeBodyEditor(node, hubPath, options, locale: host.ViewerLocale()));
    }

    /// <summary>
    /// The home-page body editor: a back link, a "Reset to default" action shown only when the Body is
    /// set, and the SAME <see cref="MarkdownEditorControl"/> the Markdown node uses — bound to the
    /// <c>body</c> content field via a node-bound <c>DataContext</c> so each edit is a per-field
    /// read-modify-write to <see cref="User.Body"/> (never a whole-content replace). An empty Body ⇒
    /// the <see cref="UserWelcomeMarkdown"/> default renders (see <see cref="BuildOwnerHome"/>).
    /// </summary>
    private static UiControl BuildHomeBodyEditor(MeshNode? node, string hubPath, JsonSerializerOptions options, string? locale = null)
    {
        if (node is null)
            return Controls.Markdown(LocalizationCatalog.Get("ui.mdHomeNotFound", locale));

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
            headerRow = headerRow.WithView(Controls.Button(LocalizationCatalog.Get("ui.resetToDefault", locale))
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

        // EmitNull: fire-and-forget click action with no error sink — an OnError would
        // rethrow on the timeout's timer thread. Stall behaviour unchanged (the reset does
        // not happen); the read logs the timeout + hub diagnostics at Warning.
        host.Hub.GetMeshNode(hubPath, TimeSpan.FromSeconds(10), ReadTimeoutBehavior.EmitNull)
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
        // EmitNull — same reason as ResetHomeAction above: no error sink on this
        // fire-and-forget subscription.
        ctx.Host.Hub.GetMeshNode(userPath, TimeSpan.FromSeconds(10), ReadTimeoutBehavior.EmitNull)
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
                        Tooltip: "Preview your public profile as visitors see it")
                        { LabelKey = "menu.viewPublicProfile", TooltipKey = "menu.viewPublicProfileTooltip" });
                    items.Add(new NodeMenuItemDefinition(
                        "Edit profile", EditProfileArea,
                        Icon: "🪪", RequiredPermission: Permission.Update, Order: 6,
                        Href: MeshNodeLayoutAreas.BuildUrl(hubPath, EditProfileArea),
                        Tooltip: "Edit your bio, links, and showcase")
                        { LabelKey = "menu.editProfile", TooltipKey = "menu.editProfileTooltip" });
                    items.Add(new NodeMenuItemDefinition(
                        "Edit home page", MeshNodeLayoutAreas.EditArea,
                        Icon: "✏️", RequiredPermission: Permission.Update, Order: 10,
                        Href: MeshNodeLayoutAreas.BuildUrl(hubPath, MeshNodeLayoutAreas.EditArea),
                        Tooltip: "Edit this home page's markdown")
                        { LabelKey = "menu.editHomePage", TooltipKey = "menu.editHomePageTooltip" });

                    if (!string.IsNullOrWhiteSpace(node.ContentAs<User>(options)?.Body))
                        items.Add(new NodeMenuItemDefinition(
                            "Reset home to default", ResetHomeArea,
                            Icon: "↩️", RequiredPermission: Permission.Update, Order: 11,
                            Href: MeshNodeLayoutAreas.BuildUrl(hubPath, ResetHomeArea),
                            Tooltip: "Discard your custom home and use the default layout")
                            { LabelKey = "menu.resetHome", TooltipKey = "menu.resetHomeTooltip" });
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
        // The VIEWER's presentation screen (#1803), resolved ONCE here on the render turn and
        // combined in as a value — never re-read from an ambient context inside the projection
        // below, which would resolve to "nobody" (and therefore hide nothing) on a later emission.
        // 🚨 NOT .Seeded() — deliberately. This is a tile surface: painting a pinned card before
        // the screen is known is the flash the mode exists to prevent, so it GATES. The node menu
        // seeds instead because there the screen only picks a label. See Doc/GUI/PresentationMode
        // rule 2 before "fixing" this to match the menu.
        var screen = host.ViewerScreen();
        return syncStream!.CombineLatest(screen,
            (change, viewerScreen) => BuildPinnedItems(change.Value.ContentAs<User>(options), screen: viewerScreen));
    }

    /// <summary>The open-threads region — the owner's own threads that aren't Done yet, newest first.</summary>
    internal static IObservable<UiControl?> ThreadsAreaView(LayoutAreaHost host, RenderingContext _)
    {
        var nodePath = host.Hub.Address.ToString();
        return Observable.Return<UiControl?>(BuildOpenThreads(nodePath, OwnerIdOf(nodePath)));
    }

    /// <summary>The catalog region — the TABBED home surface (see <see cref="BuildHome"/>):
    /// <b>Pinned</b> · <b>Apps</b> (the viewer's OWN <c>{owner}/_App</c> records — a pure READ) ·
    /// <b>Spaces</b> (the deduplicated catalog) · <b>All</b>, with the <b>Shared with me</b> band
    /// (the caller's cross-partition grants, #385 — see <see cref="ObserveSharedTargets"/>)
    /// below. The admin-editable <c>Admin/HomeConfig</c> node drives the shape and can switch back
    /// to the legacy single-list catalog (<see cref="HomeStyle.Catalog"/>).
    /// <para>🚨 The render path performs NO app writes, with no exception. Everything install-shaped
    /// — creating a record when an app is installed, removing it on uninstall, stamping its real
    /// name/icon and refreshing them — belongs to the STORE, which owns the app lifecycle; core
    /// only reads what the Store wrote. Seeding the platform defaults for a new user is a run-once
    /// LOGON action (<c>SeedDefaultAppsLogonAction</c>), not an empty-grid trigger on render.
    /// </para></summary>
    internal static IObservable<UiControl?> CatalogAreaView(LayoutAreaHost host, RenderingContext _)
    {
        var ownerId = OwnerIdOf(host.Hub.Address.ToString());
        var options = host.Hub.JsonSerializerOptions;
        var locale = host.ViewerLocale();
        // The viewer's presentation screen (#1803), resolved ONCE on the render turn.
        // 🚨 NOT .Seeded() — deliberately. This is a tile surface: painting a pinned card before
        // the screen is known is the flash the mode exists to prevent, so it GATES. The node menu
        // seeds instead because there the screen only picks a label. See Doc/GUI/PresentationMode
        // rule 2 before "fixing" this to match the menu.
        var screen = host.ViewerScreen();
        // The home's DISPLAY CONFIG is DATA-DRIVEN: read the admin-editable Admin/HomeConfig platform
        // node reactively (shipped defaults when absent), so an admin's edit updates every open home
        // LIVE — no code change, no image roll. Combined with the caller's cross-partition grants
        // (#385) and the owner node (pins). Every leg starts with a value so the home paints
        // instantly. The Apps grid needs NOTHING here: the tiles are rendered from their own
        // single-partition query inside the search control.
        var syncStream = host.Workspace.GetStream(new MeshNodeReference());
        return HomeConfigNodeType.Observe(host.Workspace, options)
            .CombineLatest(
                ObserveSharedTargets(host, ownerId),
                syncStream!.Select(change => change.Value.ContentAs<User>(options)).StartWith((User?)null),
                screen,
                // 🚨 The render path performs NO app writes at all now. Seeding the platform
                // defaults used to happen HERE, gated on the viewer's grid coming back EMPTY —
                // emptiness standing in for "this user has not been set up yet". That proxy was
                // only valid while nothing else could write an app record, and the Store now writes
                // one at install time: a user who acquires a package before first opening their
                // home arrives with a non-empty grid, the seeding never fires, and they lose the
                // defaults permanently — including the Store tile the seeding exists to guarantee.
                // It is a run-once LOGON action now (SeedDefaultAppsLogonAction), which says what
                // the proxy was reaching for: once per user because the ledger says so.
                (config, shared, user, viewerScreen) =>
                    (UiControl?)BuildHome(ownerId, config, shared, user, locale, viewerScreen));
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
    /// The THREADS APP page (<c>/{user}/Chat</c>, the ChatArea) — the agentic-app default view:
    /// the chat surface with its collapsible THREADS side menu (new chat · searchable list of the
    /// viewer's open threads with live evaluating/queued/awaiting status, all <c>GetQuery</c>-bound
    /// inside the Blazor chat view) beside the node-less composer. Sending starts a proper thread
    /// via <c>StartThread</c> and opens it full-screen — where the same side menu renders again,
    /// so the navigation never collapses. See <see cref="BuildThreadsApp"/>.
    /// </summary>
    internal static IObservable<UiControl?> ThreadsAppView(LayoutAreaHost host, RenderingContext _)
        => Observable.Return<UiControl?>(BuildThreadsApp());

    /// <summary>
    /// The Threads-app composition — pure (no hub) so the shape is unit-testable: ONE
    /// <see cref="ThreadChatControl"/> in compact (node-less) mode with the threads side menu
    /// turned on. The thread list, its live status (evaluating / queued / awaiting input), the
    /// search box, and the collapse behaviour are all NATIVE to the chat view and bound through
    /// the synced <c>GetQuery</c> cache — full thread nodes, content included. 🚨 Never
    /// reintroduce a search-result <c>ItemArea</c> rail here: rows that delegated to a
    /// <c>RailItem</c> area on each THREAD's own hub activated one hub PER RESULT and resolved
    /// an area on a hub this page does not own — "area cannot be found" in the distributed
    /// portal while passing in a monolith (the AppTile failure shape). And never stretch the
    /// composer: the old shell's <c>height: 100%</c> turned the compact input into a
    /// viewport-height empty box.
    /// </summary>
    internal static UiControl BuildThreadsApp() =>
        Controls.Stack
            .WithWidth("100%")
            .WithStyle("flex: 1; min-height: 0; display: flex; flex-direction: column;")
            .WithView(ThreadsAppComposer());

    /// <summary>The one control on the Threads app page — the node-less compact composer with the
    /// threads side menu on. Factored out so the shape is directly assertable.</summary>
    internal static ThreadChatControl ThreadsAppComposer() =>
        new ThreadChatControl()
            .WithHideEmptyState(true)
            .WithShowThreadNav()
            .WithStyle("flex: 1; min-height: 0; overflow: hidden;");

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
    //   1. `namespace:` (empty) → the root-level partition nodes the reader can see — and of those,
    //      only SPACES (see RootTypeFilter): the workspaces, nothing else.
    //   2. `namespace:{ownerId}` → the user's OWN top-level home items (default scope children = the
    //      DIRECT children of their home partition root).
    // Neither spans a subtree, so no deep `…/Introduction/Exercise/…` nodes leak in.
    /// <summary>Builds the two-query first-level UNION for a given sort suffix (newline-joined).
    /// The root leg is an ALLOW-list (<see cref="RootTypeFilter"/>), which is why it carries no
    /// <c>-nodeType:User</c>: a User root is simply not a Space. <paramref name="exclusions"/>
    /// (leading-space <c>-nodeType:…</c> clauses) applies to the OWN leg only — the root leg has
    /// nothing left to exclude.</summary>
    private static string FirstLevelUnion(string ownerId, string sortSuffix, string exclusions = "") =>
        $"namespace: is:main is:content{RootTypeFilter} {sortSuffix}\n" +
        $"namespace:{ownerId} is:main is:content{exclusions} {sortSuffix}";

    /// <summary>The catalog query for a scope + sort suffix: the first-level union (the SPACES the viewer
    /// can reach + the user's home children), or a cross-partition SUBTREE query (everything the viewer
    /// can read at every depth) when <see cref="HomeConfig.Scope"/> selects
    /// <see cref="HomeCatalogScope.Subtree"/>. The user's own root never lists on their home page: the
    /// first-level shape drops it because a User is not a Space, the subtree shape excludes it by type.
    /// <c>Subtree</c> is the admin's explicit "show me everything" and keeps the deny-list shape.</summary>
    private static string CatalogQuery(HomeCatalogScope scope, string ownerId, string sortSuffix, string exclusions = "") =>
        scope == HomeCatalogScope.Subtree
            ? $"is:main is:content -nodeType:User{exclusions} {sortSuffix}"
            : FirstLevelUnion(ownerId, sortSuffix, exclusions);

    // The three user-selectable sort orders (the view-options "Sort by" dropdown). LAST ACCESSED:
    // source:accessed JOINs the user's UserActivity satellite and projects its timestamp into
    // last_modified, so the list is ordered by the user's own access recency. The scope clauses
    // still apply to accessed queries — the empty-namespace roots leg pushes `namespace = ''`
    // (the 2026-07-21 fix; previously the fan-out dropped it and the "list" became the user's
    // whole access history) and `namespace:X` never matches the node at path X. LAST MODIFIED /
    // ALPHABETICAL are pure order-bys. Immutable constant lookup — enum · label key · query suffix
    // (labels resolve through the localization catalog; null locale = English, which is what the
    // pure-builder unit tests exercise).
    private const string SortSuffixLastAccessed = "source:accessed sort:LastModified-desc";
    private const string SortSuffixLastModified = "sort:LastModified-desc";
    private const string SortSuffixAlphabetical = "sort:Name-asc";
    private static readonly (HomeCatalogSort Sort, string LabelKey, string Suffix)[] CatalogSorts =
    {
        (HomeCatalogSort.LastAccessed, "home.sortLastAccessed", SortSuffixLastAccessed),
        (HomeCatalogSort.LastModified, "home.sortLastModified", SortSuffixLastModified),
        (HomeCatalogSort.Alphabetical, "home.sortAlphabetical", SortSuffixAlphabetical),
    };

    /// <summary>
    /// The home surface — ONE search control whose SCOPE TABS are the phone-home tabs:
    /// <b>Pinned</b> (only with pins) · <b>Apps</b> (the viewer's OWN <c>{owner}/_App</c> RECORDS,
    /// materialized from config defaults + install manifests — a single-partition query, so it
    /// loads fast, with Threads a NORMAL app record like every other — rendered as the phone-home
    /// ICON grid straight from the query rows) · <b>Spaces</b> (the catalog without store items) ·
    /// <b>All</b> (everything the viewer can read, at every depth). Because the scopes live INSIDE
    /// one <see cref="MeshSearchControl"/>, the search bar is shared: the typed term survives tab
    /// switches and every tab is searchable — including All. The search input renders on desktop
    /// and hides on mobile (the view's responsive rule). <b>Shared with me</b> is its OWN titled
    /// band BELOW the search surface (only with cross-partition grants; store items excluded — the
    /// auto-entitlement grants made every plugin partition read as "shared", but an app belongs on
    /// Apps). <see cref="HomeStyle.Catalog"/> switches back to the legacy single-list
    /// <see cref="BuildCatalog"/>. Pure (no hub) so the shape is unit-testable without standing up
    /// a hub.
    /// </summary>
    internal static UiControl BuildHome(
        string nodeOwnerId, HomeConfig? config = null, IReadOnlyList<string>? sharedTargets = null,
        User? user = null, string? locale = null,
        PresentationScreen? screen = null)
    {
        var cfg = config ?? HomeConfigNodeType.Defaults;
        // 🚨 The viewer's presentation screen (#1803) is applied to the Shared-with-me band and the
        // Pinned scope HERE, before their queries are built — not only where the resulting cards
        // are painted. Both interpolate the viewer's PATHS into the control's query string, which
        // the search view exposes in its options editor and carries in the `hq=` parameter of
        // "open in search". A marked name reaching the address bar mid-presentation is the leak,
        // whether or not a card for it is ever drawn. The Apps records are the viewer's own
        // (filtered where painted, like Spaces/All whose queries are generic).
        var privacy = screen ?? PresentationScreen.Off;
        if (cfg.Style == HomeStyle.Catalog)
            return BuildCatalog(nodeOwnerId, cfg, sharedTargets, locale, privacy);

        // TWO SECTIONS, apps first: the icon grid you launch things from, then the content you
        // search through. They are different acts — one is "open my app", the other is "find that
        // thing" — and mixing them into one tab strip made the apps just another lens on a search.
        //
        // 🚨 No separate "Shared with me" band any more. It existed because the catalog's scope
        // queries cannot reach a module living in ANOTHER partition that the viewer was invited
        // into (#385) — but that is a reason to put those items IN the list, not beside it. The
        // grants are folded into All as an extra union leg (see BuildContentSection), so a shared
        // module is simply content the viewer can reach, grouped by its type like everything else.
        // Deleting the band without folding them in would have silently dropped every invitation.
        return Controls.Stack
            .WithWidth("100%")
            .WithStyle("gap: 24px; width: 100%;")
            .WithView(BuildAppsBand(nodeOwnerId, locale))
            .WithView(BuildContentSection(
                nodeOwnerId, config, user, locale, screen, privacy.Retain(sharedTargets)));
    }

    /// <summary>
    /// The <b>Content</b> section — ONE category of content plus, when the viewer has any, a
    /// <b>Pinned</b> tab. The category is simply <b>All</b>: the SPACES the viewer can reach plus
    /// their own home items. Slicing one list into three tabs was navigation the reader had to
    /// think about before they could look at anything.
    /// <para>🚨 At the root level the list is Spaces and only Spaces
    /// (<see cref="RootTypeFilter"/>). Everything else that can sit at a partition root — a
    /// publishing hub, an event hub, a plugin cover, a course — is something you LAUNCH, and it
    /// appears exactly once, in the Apps band above.</para>
    /// <para>Store items are excluded — those are apps, and an app appears exactly once, up in the
    /// Apps section. The sort dropdown offers last accessed / last modified / alphabetical, and the
    /// search box searches the active tab. <see cref="HomeConfig.Scope"/> can widen the list to the
    /// full subtree. Pure, for tests.</para>
    /// </summary>
    internal static MeshSearchControl BuildContentSection(
        string nodeOwnerId, HomeConfig? config, User? user, string? locale, PresentationScreen? screen,
        IReadOnlyList<string>? sharedTargets = null)
    {
        var cfg = config ?? HomeConfigNodeType.Defaults;
        var privacy = screen ?? PresentationScreen.Off;
        var scopes = new List<MeshSearchScopeTab>();

        // All FIRST, and therefore the DEFAULT tab: the view activates the first scope, and the
        // control-level fallback query is scopes[0] — so "everything I can reach" is what the home
        // opens on. Pinned is the narrower, opt-in lens and follows it.
        //
        // Cross-partition invitations (#385) are an extra UNION LEG here rather than a band of
        // their own: a module someone shared with you is content you can reach, and the scope
        // queries structurally cannot see it (they walk the viewer's own partition and the roots).
        // Folding it in is what let the separate "Shared with me" section go without losing it.
        var sharedLeg = sharedTargets is { Count: > 0 }
            ? "\n" + $"path:{string.Join("|", sharedTargets)} is:main is:content -nodeType:User{SpacesDedupExclusions}"
            : string.Empty;
        scopes.Add(ContentScope("home.all", locale, cfg.DefaultSort,
            (_, suffix) => CatalogQuery(cfg.Scope, nodeOwnerId, suffix, SpacesDedupExclusions) + sharedLeg));

        // Pinned — the owner's shortcuts, present only when there are pins.
        // 🚨 The pins are INTERPOLATED INTO the query string, so the presentation screen (#1803)
        // drops a marked one HERE, before the query exists — not only where its card is painted.
        // No ItemArea: the inline unpin overlay used to resolve an area on each pinned node's OWN
        // hub, the per-result foreign-area shape that failed in the distributed portal. Unpinning
        // lives in the node menu, and the card comes from the query row.
        var pins = privacy.Retain(user?.PinnedPaths);
        if (pins.Count > 0)
        {
            var pinnedBase = $"path:({string.Join(" OR ", pins)})";
            scopes.Add(ContentScope("home.pinned", locale, HomeCatalogSort.LastModified,
                (sort, suffix) => sort == HomeCatalogSort.LastAccessed
                    // source:accessed is an INNER JOIN — alone it would hide a never-opened pin.
                    ? $"{pinnedBase} {suffix}\n{pinnedBase} {SortSuffixLastModified}"
                    : $"{pinnedBase} {suffix}"));
        }

        // Mine — the viewer's OWN partition, which is the second leg of the All union standing on
        // its own. Unconditional: your own space is the one tab that is never empty for you, and a
        // tab that appears and disappears is worse than one that is occasionally short.
        scopes.Add(ContentScope("home.mine", locale, cfg.DefaultSort,
            (_, suffix) => $"namespace:{nodeOwnerId} is:main is:content{SpacesDedupExclusions} {suffix}"));

        // Spaces — the cross-partition invitations (#385), which All folds in as a union leg. As a
        // TAB it answers a different question, and "Spaces" is what those things ARE from the
        // viewer's side: the workspaces they can reach but do not own. Present only when there is
        // something in it, since the query is a path list and an empty list matches nothing at all.
        if (sharedTargets is { Count: > 0 })
            scopes.Add(ContentScope("home.spaces", locale, HomeCatalogSort.LastModified,
                (_, suffix) =>
                    $"path:{string.Join("|", sharedTargets)} is:main is:content -nodeType:User{SpacesDedupExclusions} {suffix}"));

        // ONE list that FANS OUT by the node's own type — Spaces, Clients, Courses, … — with the
        // biggest group first (GroupByFrequency), so the page opens on what the viewer actually
        // works with. That is what "Spaces" was reaching for and failing to say: the categories
        // are not a fixed taxonomy the home invents, they are whatever types the viewer's
        // top-level nodes happen to have.
        var content = Controls.MeshSearch
            .WithTitle(LocalizationCatalog.Get("home.content", locale))
            .WithScopeTabs(scopes.ToArray())
            // Fallback for clients without scope support (react renders these): the first scope.
            .WithHiddenQuery(scopes[0].Query)
            .WithSortOptions([.. scopes[0].SortOptions!])
            .WithShowSearchBox(true)
            .WithViewOptions(true)
            .WithShowEmptyMessage(true)
            .WithRenderMode(MeshSearchRenderMode.Grouped)
            .WithGroupBy("NodeType")
            .WithGroupByFrequency()
            .WithSectionCounts(true)
            .WithCollapsibleSections(true)
            .WithMaxColumns(4)
            // Fill the page. The home is where a viewer looks for something they can already see;
            // a short list makes them search for what should have been on screen. 200 bounds the
            // query, and MaxRows is a per-GROUP cap — with the type fan-out that sets the section's
            // height rather than a hard total.
            .WithItemLimit(200)
            .WithMaxRows(24)
            .WithReactiveMode(true)
            .WithCreateHref("/create");
        return content;
    }

    /// <summary>
    /// The <b>Apps</b> section — the phone-home ICON grid over the viewer's OWN
    /// <c>{owner}/_App</c> records. A SINGLE-PARTITION query (which is why it loads fast — the old
    /// cover-path alternation fanned out across every partition schema, the multi-second home lag)
    /// painted ENTIRELY from the query rows: record Name/Icon, no per-tile hub, no content read.
    /// <c>NavigateToMainNode</c> makes a tile open the APP it points at, never the record.
    /// <para>ORDER: most recently used first, like a phone (<c>SortByAccess</c>, applied at paint
    /// from the viewer's own access log). NOT <c>source:accessed</c> — that is an INNER JOIN keyed
    /// by the row's own path, so it would drop every never-opened app AND match nothing anyway,
    /// since opening an app records a visit to the APP, never to the record pointing at it.</para>
    /// <para>No search box and no view options: this is a launcher, not a search surface — the
    /// content section below is where you search. Pure, exposed for tests.</para>
    /// </summary>
    internal static MeshSearchControl BuildAppsBand(string nodeOwnerId, string? locale)
    {
        var appsQuery =
            $"path:{nodeOwnerId}/{AppNodeType.UserNamespace} scope:children " +
            $"nodeType:{AppNodeType.NodeType} {SortSuffixAlphabetical}";
        return Controls.MeshSearch
            .WithTitle(LocalizationCatalog.Get("home.apps", locale))
            .WithHiddenQuery(appsQuery)
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(false)
            .WithRenderMode(MeshSearchRenderMode.Icons)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithItemLimit(50)
            .WithReactiveMode(true)
            // GROUPS + DRAG AND DROP, iPhone-style: the sections are the records' own
            // App.Group (per viewer — the Store stamps the package's category, the viewer
            // regroups by dragging), the order inside a section is App.Order, and a drop writes
            // both back onto the moved records through the node stream. Nothing else stores the
            // arrangement: a group exists exactly while a tile carries its name.
            .WithGroupBy(AppGroupProperty)
            .WithSortable()
            with
            {
                NavigateToMainNode = true,
                // One scope, so the grid's own paint order is the phone order. The scope carries it
                // because SortByAccess/RenderMode are per-scope settings the view reads there.
                ScopeTabs =
                [
                    new MeshSearchScopeTab(LocalizationCatalog.Get("home.apps", locale), appsQuery)
                    {
                        RenderMode = nameof(MeshSearchRenderMode.Icons),
                        NavigateToMainNode = true,
                        SortByAccess = true,
                        Sortable = true,
                    },
                ],
            };
    }

    /// <summary>The record content property the Apps grid groups by — <see cref="App.Group"/>.</summary>
    internal const string AppGroupProperty = nameof(App.Group);

    /// <summary>
    /// The <b>Shared with me</b> band — #385: modules in OTHER partitions the caller was invited
    /// into, unreachable by the catalog's scope queries. <c>null</c> when the caller has no such
    /// grants (the band simply isn't there). <c>source:accessed</c> is an INNER join on the
    /// caller's access log, so the query is a path-keyed UNION with a plain completeness fallback
    /// (a fresh, never-opened invitation must not hide). Store items are EXCLUDED — the silent
    /// per-viewer entitlement grants (StandardPacks) made every plugin partition read as "shared
    /// with me", but an app belongs on Apps, exactly once. USER roots are excluded too — a grant
    /// resolving to another user's home partition must not list that person's space as "shared"
    /// content. Pure, exposed for tests.
    /// </summary>
    internal static MeshSearchControl? BuildSharedBand(
        IReadOnlyList<string> visibleShared, string? locale)
    {
        if (visibleShared.Count == 0)
            return null;
        var sharedBase = $"path:{string.Join("|", visibleShared)} is:main is:content -nodeType:User{SpacesDedupExclusions}";
        return Controls.MeshSearch
            .WithTitle(LocalizationCatalog.Get("home.sharedWithMe", locale))
            .WithHiddenQuery($"{sharedBase} {SortSuffixLastAccessed}\n{sharedBase} {SortSuffixLastModified}")
            .WithShowSearchBox(false)
            .WithShowEmptyMessage(false)
            .WithRenderMode(MeshSearchRenderMode.Flat)
            .WithCollapsibleSections(false)
            .WithSectionCounts(false)
            .WithMaxColumns(4)
            .WithItemLimit(50)
            .WithMaxRows(2)
            .WithReactiveMode(true);
    }

    /// <summary>One content tab: localized label + the three catalog sorts (default first), each
    /// option's full query produced by <paramref name="query"/>(sort, suffix).</summary>
    private static MeshSearchScopeTab ContentScope(
        string labelKey, string? locale, HomeCatalogSort defaultSort,
        Func<HomeCatalogSort, string, string> query)
    {
        var sorts = CatalogSorts
            .OrderByDescending(s => s.Sort == defaultSort)
            .Select(s => new MeshSearchSortOption(
                LocalizationCatalog.Get(s.LabelKey, locale), query(s.Sort, s.Suffix)))
            .ToArray();
        return new MeshSearchScopeTab(LocalizationCatalog.Get(labelKey, locale), sorts[0].Query)
        {
            SortOptions = sorts,
        };
    }

    /// <summary>
    /// Product name + icon for the PLATFORM DEFAULT apps (<see cref="HomeConfig.DefaultApps"/>): a
    /// <c>~/</c> entry is not a node path but an AREA on the viewer's own hub (<c>~/Chat</c> → the
    /// Threads app at <c>/{owner}/Chat</c>); anything not listed falls back to its path leaf + the
    /// generic app icon. Names here are deliberately GLOSSARY/product terms (Store, Documentation,
    /// Threads — English in every locale), so no localization key is involved. Immutable constant
    /// lookup, never written at runtime.
    /// <para>This table covers the DEFAULTS only, and deliberately so: every other app's name and
    /// icon are written by the STORE when the app is installed. Core does not know, and must not
    /// guess, what a third-party app is called.</para>
    /// </summary>
    private static readonly ImmutableDictionary<string, (string Name, string Icon)> KnownApps =
        new Dictionary<string, (string Name, string Icon)>
        {
            ["Store"] = ("Store", "/static/NodeTypeIcons/shopping-bag.svg"),
            ["Doc"] = ("Documentation", "/static/NodeTypeIcons/book.svg"),
            ["~/" + ChatArea] = ("Threads", ThreadsIcon),
        }.ToImmutableDictionary(StringComparer.OrdinalIgnoreCase);

    /// <summary>
    /// The Threads tile's artwork — full-bleed, so it reads as a product icon rather than a small
    /// grey glyph floating in a box, which is what <c>chat.svg</c> looked like next to the Store's.
    ///
    /// <para>Three constraints it is built to, all learned the hard way:</para>
    /// <list type="bullet">
    /// <item><b>No <c>width</c>/<c>height</c> on the root tag.</b> Those render at literal pixels
    /// inside the tile — an authored <c>width="24"</c> is a 24px icon in a 64px box, which was the
    /// whole "icons render tiny" report. viewBox only; the surface decides the size.</item>
    /// <item><b>Attribute styling only — no <c>&lt;style&gt;</c>, no <c>class</c>.</b> React Native
    /// renders neither, so a class-driven fill is invisible on the phone and fine on the web.</item>
    /// <item><b>A gradient needs a unique id.</b> Several inline SVGs land in one document, and a
    /// duplicated <c>linearGradient</c> id means the first one wins for everybody.</item>
    /// </list>
    /// </summary>
    internal const string ThreadsIcon =
        "<svg viewBox='0 0 48 48' xmlns='http://www.w3.org/2000/svg'>"
        + "<defs><linearGradient id='mw-threads-grad' x1='0%' y1='0%' x2='100%' y2='100%'>"
        + "<stop offset='0%' stop-color='#4f46e5'/><stop offset='100%' stop-color='#0ea5e9'/>"
        + "</linearGradient></defs>"
        + "<rect width='48' height='48' rx='10' fill='url(#mw-threads-grad)'/>"
        + "<path d='M11 18a5 5 0 0 1 5-5h11a5 5 0 0 1 5 5v5a5 5 0 0 1-5 5h-8l-6 4v-4a5 5 0 0 1-2-4z' "
        + "fill='#ffffff' fill-opacity='0.95'/>"
        + "<path d='M23 27a5 5 0 0 1 5-5h6a5 5 0 0 1 5 5v3a5 5 0 0 1-5 5v3l-5-3h-1a5 5 0 0 1-5-5z' "
        + "fill='#c7d2fe' fill-opacity='0.92'/>"
        + "<circle cx='18' cy='20' r='1.6' fill='#4f46e5'/>"
        + "<circle cx='23' cy='20' r='1.6' fill='#4f46e5'/>"
        + "<circle cx='28' cy='20' r='1.6' fill='#4f46e5'/>"
        + "</svg>";

    internal const string GenericAppIcon = "/static/NodeTypeIcons/puzzlepiece.svg";

    private static string LeafOf(string path) =>
        path.Contains('/') ? path[(path.LastIndexOf('/') + 1)..] : path;

    /// <summary>The blueprint of one platform-default app record.</summary>
    internal sealed record AppRecordSpec(
        string Id, string Name, string Icon, string? Plugin, string? OpenPath, string Source)
    {
        /// <summary>The record's navigation target — the app's path, or the owner-hub area path
        /// for a <c>~/</c> app. Stamped as the record's <see cref="MeshNode.MainNode"/> so an icon
        /// tile navigates STRAIGHT to the app with nothing else to read.</summary>
        public string? Target => Plugin is { Length: > 0 } ? Plugin : OpenPath;
    }

    /// <summary>
    /// The PLATFORM DEFAULT app records (<see cref="HomeConfig.DefaultApps"/>; a <c>~/</c> entry is
    /// an AREA on the viewer's own hub, e.g. <c>~/Chat</c> → the Threads app, an ORDINARY record
    /// like every other app), deduped by record id (a path's <c>/</c> becomes <c>-</c>).
    /// <see cref="KnownApps"/> gives them their product name + icon. Pure, exposed for tests.
    /// <para>Installed apps are NOT here: the Store writes their records when it installs them.</para>
    /// </summary>
    internal static IReadOnlyList<AppRecordSpec> AppRecordSpecs(HomeConfig? config, string ownerId)
    {
        var cfg = config ?? HomeConfigNodeType.Defaults;
        var specs = new List<AppRecordSpec>();
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var raw in cfg.DefaultApps ?? [])
        {
            if (string.IsNullOrWhiteSpace(raw))
                continue;
            if (raw.StartsWith("~/", StringComparison.Ordinal))
            {
                var segment = raw[2..].Trim('/');
                if (segment.Length == 0)
                    continue;
                var id = segment.Replace('/', '-');
                if (!seen.Add(id))
                    continue;
                var (name, icon) = KnownApps.TryGetValue("~/" + segment, out var known)
                    ? known
                    : (segment, GenericAppIcon);
                specs.Add(new AppRecordSpec(id, name, icon,
                    Plugin: null, OpenPath: $"{ownerId}/{segment}", Source: "default"));
            }
            else
            {
                var path = raw.Trim('/');
                if (path.Length == 0)
                    continue;
                var id = path.Replace('/', '-');
                if (!seen.Add(id))
                    continue;
                var (name, icon) = KnownApps.TryGetValue(path, out var known)
                    ? known
                    : (LeafOf(path), GenericAppIcon);
                specs.Add(new AppRecordSpec(id, name, icon,
                    Plugin: path, OpenPath: null, Source: "default"));
            }
        }
        return specs;
    }


    /// <summary>One default app record: the tile's whole identity lives on the NODE (Name, Icon,
    /// MainNode = the app it opens), so the grid paints from query rows alone. Pure, for tests.</summary>
    internal static MeshNode BuildAppRecord(string ownerId, AppRecordSpec spec) =>
        new(spec.Id, $"{ownerId}/{AppNodeType.UserNamespace}")
        {
            NodeType = AppNodeType.NodeType,
            Name = spec.Name,
            Icon = spec.Icon,
            MainNode = spec.Target ?? $"{ownerId}/{AppNodeType.UserNamespace}/{spec.Id}",
            State = MeshNodeState.Active,
            Content = new App
            {
                Plugin = spec.Plugin ?? "",
                OpenPath = spec.OpenPath,
                Source = spec.Source,
            },
        };

    /// <summary>
    /// What the ROOT leg of the home list is allowed to be: a <b>Space</b>, and nothing else.
    /// <para>🚨 An ALLOW-list, deliberately — this used to be a deny-list
    /// (<see cref="SpacesDedupExclusions"/> plus <c>-nodeType:User</c>) and a deny-list is only
    /// ever as complete as the last person who remembered to extend it. It was not: on
    /// memex.meshweaver.cloud the home listed <c>Posts</c> under a "Posts Hubs" heading (a
    /// <c>SocialMedia/PostsHub</c> partition root) and <c>Event</c> under "Event Hubs" — every
    /// root type that was not a store item leaked in and minted its own type group. The home's
    /// content list answers "which workspaces can I reach", so it lists workspaces; a hub, a
    /// plugin cover, a course — anything you LAUNCH — belongs in the Apps band above it, exactly
    /// once. A new root NodeType now has to opt IN to the list rather than remember to opt out.
    /// </para>
    /// </summary>
    private const string RootTypeFilter = " nodeType:Space";

    /// <summary>Dedup for the OWN-partition leg: anything living in the Store (and therefore
    /// representable as an installed app — plugin covers, the store root) is EXCLUDED, so an app
    /// appears exactly once, on the Apps scope. The root leg no longer needs it —
    /// <see cref="RootTypeFilter"/> subsumes it.
    /// <para>🚨 This is the ONE list left, and it should not be here either: these belong on their
    /// own NodeType nodes as <c>ExcludeFromContext: ["content"]</c> — which is one
    /// <c>.HideFromContent()</c> call — but <c>Store/Plugin</c> and <c>Store/Catalog</c> are
    /// DYNAMIC node types living in the Store partition, so marking them is a MeshWeaver.Plugins
    /// change, landing after this. Deleting the terms first would put every installed plugin's root
    /// back on the content list, where it duplicates its own app tile.</para></summary>
    private const string SpacesDedupExclusions = " -nodeType:Store/Plugin -nodeType:Store/Catalog";

    /// <summary>
    /// The LEGACY catalog region (<see cref="HomeStyle.Catalog"/>) — ONE tab-less list, whose shape
    /// is DATA-DRIVEN by <paramref name="config"/> (the admin-editable <c>Admin/HomeConfig</c>
    /// platform node; <c>null</c> ⇒ <see cref="HomeConfigNodeType.Defaults"/>). The config drives
    /// the depth (first-level top-level entries vs the full subtree), the render (flat list vs
    /// grouped-by-type sections), and the default sort — and a view-options "Sort by" control still
    /// lets the user pick <b>Last accessed</b> / <b>Last modified</b> / <b>Alphabetical</b> at will.
    /// <para>The one thing a first-level query can't reach is a module in ANOTHER partition the
    /// caller was specifically invited into (#385): those are resolved from the caller's own
    /// readable <c>AccessAssignment</c> grants (<paramref name="sharedTargets"/>) and appended as an
    /// additive "Shared with me" band, present ONLY when the caller actually has such grants.</para>
    /// <para>Pure (no hub) so the catalog shape is unit-testable without standing up a hub.</para>
    /// </summary>
    internal static UiControl BuildCatalog(
        string nodeOwnerId, HomeConfig? config = null, IReadOnlyList<string>? sharedTargets = null,
        string? locale = null, PresentationScreen? screen = null)
    {
        var cfg = config ?? HomeConfigNodeType.Defaults;
        var everything = BuildCatalogList(nodeOwnerId, cfg, exclusions: "", locale);
        // Same rule as BuildHome: the shared band's targets are query-string content, so the
        // presentation screen (#1803) applies before the query is built. The catalog list itself is
        // a generic query and is deliberately NOT narrowed — a `-path:` clause would put the marked
        // name in the same URL and start turning the screen into a second permission system.
        sharedTargets = (screen ?? PresentationScreen.Off).Retain(sharedTargets);

        // No cross-partition invitations → the catalog IS the single list.
        if (sharedTargets is not { Count: > 0 })
            return everything;

        // Additive #385 band: modules in OTHER partitions the caller was invited into, which the broad
        // is:main query can't reach (readable by URL but invisible to a scope search). The `path:a|b|c`
        // alternation resolves each target node, access-filtered by the mesh.
        var pathList = string.Join("|", sharedTargets);
        var shared = Controls.MeshSearch
            .WithTitle(LocalizationCatalog.Get("home.sharedWithMe", locale))
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

    /// <summary>
    /// The shared catalog-list core (the "everything" search) used by both the legacy
    /// <see cref="BuildCatalog"/> (no exclusions) and the Spaces tab
    /// (<see cref="SpacesDedupExclusions"/>). Sort options DEFAULT first (so the dropdown's default
    /// selection == HiddenQuery); each option carries its full catalog query (first-level union or
    /// subtree, per <see cref="HomeConfig.Scope"/>) — the query itself carries the sort/source, so
    /// no client-side WithSortBy (that would override the query order).
    /// </summary>
    private static MeshSearchControl BuildCatalogList(
        string nodeOwnerId, HomeConfig cfg, string exclusions, string? locale)
    {
        var sortOptions = CatalogSorts
            .OrderByDescending(s => s.Sort == cfg.DefaultSort)
            .Select(s => new MeshSearchSortOption(
                LocalizationCatalog.Get(s.LabelKey, locale),
                CatalogQuery(cfg.Scope, nodeOwnerId, s.Suffix, exclusions)))
            .ToArray();

        var everything = Controls.MeshSearch
            .WithHiddenQuery(sortOptions[0].Query)
            .WithSortOptions(sortOptions)
            // No embedded search box: every shell already carries the top-bar search, and a second
            // input on the home reads as a duplicate (maintainer, 2026-08-21). Sort/view stay.
            .WithShowSearchBox(false)
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

        return everything;
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
                : (UiControl?)BuildProfileEditor(node, hubPath, options, locale: host.ViewerLocale()));
    }

    /// <summary>
    /// The profile editor body: a back link, node-bound <see cref="MarkdownEditorControl"/>s for the
    /// bio and links (each edit is a per-field read-modify-write straight to the User node — the same
    /// node-bound DataContext pattern as <see cref="BuildHomeBodyEditor"/>: ONE source of truth, no
    /// <c>/data</c> replica, no save subscription), and the showcase rendered with the inline unpin
    /// overlay so the owner curates pins in place. Built from layout-area controls only.
    /// </summary>
    internal static UiControl BuildProfileEditor(MeshNode? node, string hubPath, JsonSerializerOptions options, string? locale = null)
    {
        if (node is null)
            return Controls.Markdown(LocalizationCatalog.Get("ui.mdProfileNotFound", locale));

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
            .WithView(Controls.H3(LocalizationCatalog.Get("ui.editYourProfile", locale)).WithStyle("margin: 0; flex: 1;"))
            .WithView(Controls.Label(LocalizationCatalog.Get("ui.autoSaved", locale))
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

        // Language — the UI language, editable HERE on the profile and not only buried in
        // Settings → Preferences, which is where a user actually looks for "my language".
        // Node-bound like every other field on this page: MeshNodeContentEditorControl reads and
        // writes User.Locale straight on the node stream (IMeshNodeStreamCache), so there is ONE
        // source of truth and no /data replica + save-subscription. The same control and the same
        // field back the Preferences tab, so the two can never drift apart.
        container = container.WithView(BuildProfileSection(
            LocalizationCatalog.Get("settings.language", locale),
            new MeshNodeContentEditorControl(userPath)
            {
                CanEdit = true,
                Fields = ImmutableList.Create(
                    new MeshNodeEditorField(
                        nameof(User.Locale).ToCamelCase()!,
                        LocalizationCatalog.Get("settings.language", locale),
                        MeshNodeEditorFieldKind.Enum)
                    {
                        // Stores the BCP-47 tag ("de") but shows the endonym ("Deutsch") — a German
                        // speaker looks for "Deutsch", not "German" or a raw tag.
                        Options = Locales.Supported,
                        OptionLabels = Locales.DisplayNames
                    })
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
        bool isOwner, bool canSeeEmail, JsonSerializerOptions options, string? locale = null)
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
                .WithView(Controls.Button(LocalizationCatalog.Get("menu.editProfile", locale))
                    .WithIconStart(FluentIcons.Edit())
                    .WithAppearance(Appearance.Lightweight)
                    .WithNavigateToHref($"/{nodePath}/{EditProfileArea}")));

        if (isEmpty)
        {
            content = content.WithView(BuildGettingStarted(nodePath, ownerId, ownerName, isOwner, locale: locale));
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
            .WithHiddenQuery($"namespace:{ownerId} is:main is:content scope:descendants sort:LastModified-desc")
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
            .WithHiddenQuery($"namespace:{ownerId} is:main is:content scope:descendants sort:LastModified-desc")
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
    internal static UiControl BuildGettingStarted(string nodePath, string ownerId, string ownerName, bool isOwner, string? locale = null)
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
                .WithView(Controls.Button(LocalizationCatalog.Get("menu.editProfile", locale))
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
    internal static UiControl? BuildPinnedItems(
        User? user, bool withTitle = true, PresentationScreen? screen = null)
    {
        // 🚨 Same reason as the shared band and the Apps tab: a pinned path is INTERPOLATED INTO THE
        // QUERY STRING this control carries, so a marked one is dropped here rather than only where
        // its card would be painted — otherwise the marked name still travels into the search box's
        // options editor and the `hq=` URL. Display-only: the pin itself is untouched and comes
        // straight back when presentation mode is turned off (#1803).
        var pinnedPaths = (screen ?? PresentationScreen.Off).Retain(user?.PinnedPaths);
        if (pinnedPaths.Count == 0)
            return null;

        var pathsClause = string.Join(" OR ", pinnedPaths);
        var search = Controls.MeshSearch
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
        return withTitle ? search.WithTitle("Pinned") : search;
    }

}
