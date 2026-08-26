using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Hosting.AspNetCore.Portal;
using MeshWeaver.Blazor.Portal.Resize;
using MeshWeaver.Blazor.Portal.SidePanel;
using MeshWeaver.Blazor.Services;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Security;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Authorization;
using Microsoft.Extensions.Logging;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
using Icons = Microsoft.FluentUI.AspNetCore.Components.Icons;

namespace MeshWeaver.Blazor.Portal.Layout;

/// <summary>
/// Base component for the portal's main layout: hosts the header navigation menus
/// (Node / Mesh / AI), the routed content area, and the resizable, auth-gated side panel
/// that shows chat threads or layout-area content.
/// </summary>
public partial class PortalLayoutBase : LayoutComponentBase, IDisposable
{
    /// <summary>
    /// JS runtime used for side-panel persistence, sizing, and resize dispatch.
    /// </summary>
    [Inject] protected IJSRuntime JSRuntime { get; set; } = null!;

    /// <summary>Logs notable side-panel lifecycle events (e.g. auto-hiding an active chat).</summary>
    [Inject] protected ILogger<PortalLayoutBase> Logger { get; set; } = null!;

    /// <summary>
    /// Manages URL navigation in response to menu clicks and panel actions.
    /// </summary>
    [Inject] protected NavigationManager NavigationManager { get; set; } = null!;

    /// <summary>
    /// Holds and persists the side panel's visibility, position, size, and content.
    /// </summary>
    [Inject] protected SidePanelStateService SidePanelState { get; set; } = null!;

    /// <summary>
    /// Message hub used for mesh queries such as the global-admin check.
    /// </summary>
    [Inject] protected IMessageHub Hub { get; set; } = null!;

    /// <summary>
    /// Provides the reactive navigation context and side-panel navigation requests.
    /// </summary>
    [Inject] protected INavigationService NavigationService { get; set; } = null!;

    /// <summary>
    /// Supplies the dynamic Node / Mesh / AI menu item definitions.
    /// </summary>
    [Inject] protected IMenuItemsProvider MenuItemsProvider { get; set; } = null!;

    /// <summary>
    /// Resolves content paths into layout-area references for the side panel.
    /// </summary>
    [Inject] protected IPathResolver PathResolver { get; set; } = null!;

    /// <summary>
    /// Provides the current user's access context (e.g. their object id).
    /// </summary>
    [Inject] protected AccessService AccessService { get; set; } = null!;

    /// <summary>
    /// Circuit-scoped error surface — a failed page-level action reports here and
    /// <c>PortalErrorModal</c> raises the modal, rather than the failure being swallowed.
    /// </summary>
    [Inject] protected PortalErrorSink ErrorSink { get; set; } = null!;

    /// <summary>
    /// Cascading authentication state; used to gate side-panel content for anonymous circuits.
    /// </summary>
    [CascadingParameter]
    protected Task<AuthenticationState>? AuthStateTask { get; set; }

    // Tracks whether the current circuit's user is signed in. Side panel content
    // (ThreadChatView / LayoutAreaView) accesses the workspace and throws for
    // anonymous users — so we hide it when not authenticated.
    private bool isAuthenticated;

    // ── Light/dark theme toggle (top-bar chrome, matching the mobile client) ──
    // The header hosts a <FluentDesignTheme @bind-Mode="ThemeMode" StorageName="theme"/>; because it
    // shares StorageName with the page's FluentDesignTheme and applies its tokens to the whole
    // document, flipping ThemeMode here re-themes the app and persists — same proven pattern the
    // site-settings panel used before the control moved out here. Binary light↔dark toggle.
    private DesignThemeModes ThemeMode { get; set; }

    private void ToggleTheme()
        => ThemeMode = ThemeMode == DesignThemeModes.Dark ? DesignThemeModes.Light : DesignThemeModes.Dark;

    private string ThemeToggleTitle
        => ThemeMode == DesignThemeModes.Dark ? "Switch to light theme" : "Switch to dark theme";

    // Sun while dark (click → light), moon otherwise (click → dark).
    private Icon ThemeToggleIcon
        => ThemeMode == DesignThemeModes.Dark
            ? new Icons.Regular.Size20.WeatherSunny()
            : new Icons.Regular.Size20.WeatherMoon();

    // Splitter pane sizes - default 3:1 ratio (75% main, 25% side panel)
    private string MainPaneSize => SidePanelState.Width.HasValue ? $"{100 - SidePanelState.Width.Value}%" : "75%";
    private string SidePanelPaneSize => SidePanelState.Width.HasValue ? $"{SidePanelState.Width.Value}%" : "25%";

    /// <summary>
    /// Render fragment for header links (social media icons, etc.)
    /// </summary>
    [Parameter]
    public RenderFragment? HeaderLinks { get; set; }

    /// <summary>
    /// Render fragment for desktop navigation menu
    /// </summary>
    [Parameter]
    public RenderFragment? DesktopNavMenu { get; set; }

    /// <summary>
    /// Render fragment for mobile navigation menu
    /// </summary>
    [Parameter]
    public RenderFragment? MobileNavMenu { get; set; }

    /// <summary>
    /// Name of the message-bar section where top-of-page notifications are rendered.
    /// </summary>
    protected const string MessageBarSection = "MessagesTop";

    private bool isNavMenuOpen;

    /// <summary>
    /// Whether the mobile navigation menu is currently open.
    /// </summary>
    protected bool IsNavMenuOpen => isNavMenuOpen;

    private bool isNodeMenuOpen;
    private bool isMeshMenuOpen;
    private bool isAiMenuOpen;
    private bool isGitHubMenuOpen;

    // Menu context names (must match NodeMenuItemsExtensions.*Context). Instance sync lives in the
    // NODE menu ("Synchronizations"), so there is no separate "Sync" dropdown.
    private const string NodeMenuContext = "Node";
    private const string MeshMenuContext = "Mesh";
    private const string AiMenuContext = "AI";
    private const string GitHubMenuContext = "GitHub";

    // Menu items per context from IMenuItemsProvider (populated by LayoutAreaView from $Menu:{context} streams)
    private IReadOnlyList<NodeMenuItemDefinition> _nodeMenuItems = [];
    private IReadOnlyList<NodeMenuItemDefinition> _meshMenuItems = [];
    private IReadOnlyList<NodeMenuItemDefinition> _aiMenuItems = [];
    private IReadOnlyList<NodeMenuItemDefinition> _gitHubMenuItems = [];
    private IDisposable? _nodeMenuSubscription;
    private IDisposable? _meshMenuSubscription;
    private IDisposable? _aiMenuSubscription;
    private IDisposable? _gitHubMenuSubscription;

    // Data-declared top-bar menus (UiContribution Context="TopBar", design #1645). Each
    // declaration is a NodeMenuItemDefinition whose Area names the menu's own context key; its
    // entries arrive on that key's stream like any compiled context's. One dropdown per
    // declaration renders after the compiled menus, hidden while it has no visible entries.
    private const string TopBarMenuContext = "TopBar";
    private IReadOnlyList<NodeMenuItemDefinition> _topBarMenus = [];
    private IDisposable? _topBarMenusSubscription;
    private readonly Dictionary<string, IReadOnlyList<NodeMenuItemDefinition>> _contributedMenuItems = new(StringComparer.Ordinal);
    private readonly Dictionary<string, IDisposable> _contributedMenuSubscriptions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, bool> _contributedMenuOpen = new(StringComparer.Ordinal);


    // Editable content collections
    /// <summary>
    /// Content collections the current user is permitted to edit.
    /// </summary>
    protected IReadOnlyList<ContentCollectionConfig> EditableCollections { get; private set; } = [];
    private IJSObjectReference? jsModule;
    private DotNetObjectReference<PortalLayoutBase>? dotNetRef;

    private IDisposable? _navContextSubscription;

    /// <summary>
    /// Wires the side-panel, navigation-context, and Node / Mesh / AI menu subscriptions,
    /// re-rendering as each stream emits.
    /// </summary>
    protected override void OnInitialized()
    {
        base.OnInitialized();
        SidePanelState.OnStateChanged += OnSidePanelStateChanged;
        NavigationService.SidePanelNavigationRequested += OnSidePanelNavigation;
        _navContextSubscription = NavigationService.NavigationContext
            .Subscribe(OnNavigationContextChanged);
        // Collapse the side pane whenever the MAIN view NAVIGATES to a thread — a thread lives in
        // EITHER the main view OR the side panel, never both. Keyed on the REAL navigation event
        // (LocationChanged), NOT the nav-context stream, so a background context re-emission to the
        // panel's own thread during a running round can't trip it (the "chat vanishes during
        // execution" bug the nav-context path had to guard against with SameThreadIdentity).
        NavigationManager.LocationChanged += OnLocationChanged;
        CheckChatHint(NavigationManager.Uri);
        _nodeMenuSubscription = MenuItemsProvider.GetMenu(NodeMenuContext).Subscribe(items =>
        {
            _nodeMenuItems = items;
            InvokeAsync(StateHasChanged);
        });
        _meshMenuSubscription = MenuItemsProvider.GetMenu(MeshMenuContext).Subscribe(items =>
        {
            _meshMenuItems = items;
            InvokeAsync(StateHasChanged);
        });
        _aiMenuSubscription = MenuItemsProvider.GetMenu(AiMenuContext).Subscribe(items =>
        {
            _aiMenuItems = items;
            InvokeAsync(StateHasChanged);
        });
        _gitHubMenuSubscription = MenuItemsProvider.GetMenu(GitHubMenuContext).Subscribe(items =>
        {
            _gitHubMenuItems = items;
            InvokeAsync(StateHasChanged);
        });
        _topBarMenusSubscription = MenuItemsProvider.GetMenu(TopBarMenuContext).Subscribe(menus =>
        {
            _topBarMenus = menus;
            SyncContributedMenuSubscriptions(menus);
            InvokeAsync(StateHasChanged);
        });
    }

    /// <summary>
    /// Keeps one entry-stream subscription per declared top-bar menu key: subscribes keys that
    /// appeared, disposes keys whose declaration went away (plugin uninstalled / gated out).
    /// </summary>
    private void SyncContributedMenuSubscriptions(IReadOnlyList<NodeMenuItemDefinition> menus)
    {
        var live = new HashSet<string>(StringComparer.Ordinal);
        foreach (var menu in menus)
            if (menu.Area is { Length: > 0 } key && key != TopBarMenuContext)
                live.Add(key);

        foreach (var stale in _contributedMenuSubscriptions.Keys.Where(k => !live.Contains(k)).ToList())
        {
            _contributedMenuSubscriptions[stale].Dispose();
            _contributedMenuSubscriptions.Remove(stale);
            _contributedMenuItems.Remove(stale);
            _contributedMenuOpen.Remove(stale);
        }

        foreach (var key in live)
            if (!_contributedMenuSubscriptions.ContainsKey(key))
                _contributedMenuSubscriptions[key] = MenuItemsProvider.GetMenu(key).Subscribe(items =>
                {
                    _contributedMenuItems[key] = items;
                    InvokeAsync(StateHasChanged);
                });
    }

    /// <summary>The declared top-bar menus, in declaration Order.</summary>
    private IReadOnlyList<NodeMenuItemDefinition> GetTopBarMenus() => _topBarMenus;

    private IReadOnlyList<NodeMenuItemDefinition> GetContributedMenuItems(string key)
        => _contributedMenuItems.TryGetValue(key, out var items) ? items : [];

    private bool IsContributedMenuOpen(string key)
        => _contributedMenuOpen.TryGetValue(key, out var open) && open;

    private void ToggleContributedMenu(string key)
        => _contributedMenuOpen[key] = !IsContributedMenuOpen(key);

    private void OnContributedMenuOpenChanged(string key, bool open)
        => _contributedMenuOpen[key] = open;

    /// <summary>
    /// Initializes the navigation service, snapshots the authentication state, and forces the
    /// side panel closed (resolving its content only when authenticated and visible).
    /// </summary>
    /// <returns>A task that completes when initialization finishes.</returns>
    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        // Synchronous (no await): Initialize() only wires Rx subscriptions; a Task
        // awaited in OnInitializedAsync would deadlock the circuit's sync-context.
        NavigationService.Initialize();

        // Snapshot auth state. If the user signed out (or arrived anonymous) with a
        // previously-persisted IsVisible=true, force the panel closed before any
        // child component subscribes to a workspace it can't access.
        if (AuthStateTask is not null)
        {
            var authState = await AuthStateTask;
            isAuthenticated = authState.User?.Identity?.IsAuthenticated == true;
        }
        if (!isAuthenticated && SidePanelState.IsVisible)
        {
            SidePanelState.SetVisible(false);
        }

        // Only resolve side panel content if visible AND authenticated.
        if (isAuthenticated && SidePanelState.IsVisible)
            ResolveSidePanelContent();
    }

    /// <summary>
    /// Display name of the currently-focused node — rendered as a header inside the Node and Mesh menus
    /// so the user can see what they're about to act on. Null when there's no node context (home page).
    /// </summary>
    private string? CurrentNodeName
    {
        get
        {
            var node = _currentNavContext?.Node;
            return node?.Name ?? node?.Id;
        }
    }

    /// <summary>A single breadcrumb crumb: its display <paramref name="Label"/>, the
    /// <paramref name="Href"/> to that ancestor's default page, and whether it is the
    /// <paramref name="IsLast"/> (current) segment — rendered as plain bold text, not a link.</summary>
    protected readonly record struct Crumb(string Label, string Href, bool IsLast);

    /// <summary>
    /// Breadcrumb trail for the current node — one crumb per segment of the resolved address, each
    /// linking to that ancestor's default page (<c>/{cumulative}</c>, empty area); the last segment
    /// (the current node) is flagged <see cref="Crumb.IsLast"/> so the view shows it as bold text.
    /// Empty at the mesh root, where the bar still renders the lone ⌂ Home affordance. Mirrors the
    /// React-Native shell's breadcrumb toolbar and the server-side <c>BuildBreadcrumbs</c> trail —
    /// derived reactively from <see cref="_currentNavContext"/>, which re-renders on every nav change.
    /// </summary>
    protected IReadOnlyList<Crumb> Breadcrumbs
    {
        get
        {
            // Address.Path is the host-independent node path — use it rather than Namespace
            // (== Address.ToString()), which appends "~{Host}" for hosted addresses and would
            // split into a bogus trailing crumb.
            var segments = (_currentNavContext?.Address.Path ?? "")
                .Split('/', StringSplitOptions.RemoveEmptyEntries);
            if (segments.Length == 0)
                return Array.Empty<Crumb>();

            var crumbs = new Crumb[segments.Length];
            var cumulative = "";
            for (var i = 0; i < segments.Length; i++)
            {
                // Encode each segment for the href (escaping reserved chars like spaces/#/%),
                // preserving the "/" separators; the Label keeps the raw segment for display.
                var encoded = Uri.EscapeDataString(segments[i]);
                cumulative = i == 0 ? encoded : $"{cumulative}/{encoded}";
                crumbs[i] = new Crumb(segments[i], $"/{cumulative}", i == segments.Length - 1);
            }
            return crumbs;
        }
    }

    /// <summary>
    /// The active area, shown as a trailing "· {area}" after the trail when it isn't the node's
    /// default content page (empty area). Null hides the suffix. Peer of the React-Native shell's
    /// <c>· {nav.area}</c> hint.
    /// </summary>
    protected string? CurrentBreadcrumbArea
    {
        get
        {
            var area = _currentNavContext?.Area;
            return string.IsNullOrEmpty(area) ? null : area;
        }
    }

    private void ToggleNodeMenu()
    {
        isNodeMenuOpen = !isNodeMenuOpen;
    }

    private void OnNodeMenuOpenChanged(bool open)
    {
        isNodeMenuOpen = open;
    }

    private void ToggleMeshMenu()
    {
        isMeshMenuOpen = !isMeshMenuOpen;
    }

    private void OnMeshMenuOpenChanged(bool open)
    {
        isMeshMenuOpen = open;
    }

    private void ToggleAiMenu()
    {
        isAiMenuOpen = !isAiMenuOpen;
    }

    private void OnAiMenuOpenChanged(bool open)
    {
        isAiMenuOpen = open;
    }

    /// <summary>
    /// AI menu items — aggregated reactively from the injectable "AI" menu context (default seed:
    /// Threads / Models / Agents / Skills, each opening mesh search grouped by namespace). NOT a
    /// hardcoded list: modules contribute via an <c>INodeMenuProvider</c> with <c>Context = "AI"</c>.
    /// Populated like the Node / Mesh menus from <see cref="IMenuItemsProvider"/>.
    /// </summary>
    private IReadOnlyList<NodeMenuItemDefinition> GetAiMenuItems() => _aiMenuItems;

    /// <summary>Items for the "GitHub" dropdown (GitHub sync actions) — empty hides the button.</summary>
    private IReadOnlyList<NodeMenuItemDefinition> GetGitHubMenuItems() => _gitHubMenuItems;

    private void ToggleGitHubMenu() => isGitHubMenuOpen = !isGitHubMenuOpen;
    private void OnGitHubMenuOpenChanged(bool open) => isGitHubMenuOpen = open;

    /// <summary>
    /// Navigates to the Settings page — per-node Settings when on a node, Global Settings at the root.
    /// </summary>
    private void NavigateToSettings()
    {
        var ns = NavigationService.CurrentNamespace;
        if (!string.IsNullOrEmpty(ns))
        {
            // Per-node settings — governed by the node's own RLS, not platform-admin gated.
            NavigationManager.NavigateTo($"/{ns}/Settings");
            return;
        }

        // Root → Global Settings is ADMIN-ONLY (Admin-partition Read). A non-admin circuit that
        // subscribes to the GlobalSettings area gets a repeating "Access denied … lacks Read on
        // 'GlobalSettings'" DeliveryFailure → bounded resubscribe storm. Gate the navigation on the
        // canonical IsGlobalAdmin() predicate so a non-admin never issues that subscribe; route them
        // to their own account page instead.
        // Resolve the user BEFORE subscribing: the callback runs on a hub scheduler, where both
        // AccessContext AsyncLocals (Context AND CircuitContext) have been nulled.
        var userId = CircuitUser.ResolveUserId(AccessService);
        Hub.IsGlobalAdmin()
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(5))
            .Catch<bool, Exception>(_ => Observable.Return(false))
            .Subscribe(isAdmin => InvokeAsync(() =>
            {
                if (isAdmin)
                {
                    NavigationManager.NavigateTo($"/{GlobalSettingsLayoutArea.GlobalSettingsArea}");
                    return;
                }
                NavigationManager.NavigateTo(string.IsNullOrEmpty(userId) ? "/" : $"/User/{userId}");
            }));
    }

    /// <summary>
    /// Navigates to the current user's Activity dashboard — the canonical
    /// "all my threads" surface (Latest Threads section already filters out
    /// Done threads by default; type <c>content.status:Done</c> in the search
    /// box to surface them).
    /// </summary>
    private void NavigateToThreads()
    {
        var userId = CircuitUser.ResolveUserId(AccessService);
        if (string.IsNullOrEmpty(userId))
        {
            Logger.LogWarning(
                "NavigateToThreads: no circuit user resolved (CircuitContext and Context both empty) — not navigating.");
            return;
        }
        NavigationManager.NavigateTo($"/User/{userId}/Activity");
    }

    /// <summary>
    /// Sentinel <see cref="NodeMenuItemDefinition.Area"/> for the AI menu's "New thread" item.
    /// <see cref="HandleMenuItemClick"/> matches it BEFORE the Href/Area navigation branches and returns,
    /// opening the composer in the MAIN pane instead. It has to be imperative because the destination is
    /// per-user (<c>/User/{me}/Chat</c>) and only resolvable from the circuit at click time — a static
    /// menu seed cannot name it. Lives here so the seed and the handler agree on the sentinel.
    /// </summary>
    public const string AiNewThreadAction = "ai-new-thread";

    /// <summary>
    /// Handles a click on a dynamic menu item.
    /// Uses Href for absolute navigation when set, otherwise constructs URL from Area.
    /// </summary>
    private async Task HandleMenuItemClick(NodeMenuItemDefinition item)
    {
        isNodeMenuOpen = false;
        isMeshMenuOpen = false;
        isAiMenuOpen = false;
        isGitHubMenuOpen = false;
        // Imperative actions (no Href): the AI menu's "New thread" opens the composer in the MAIN pane.
        if (string.Equals(item.Area, AiNewThreadAction, StringComparison.Ordinal))
        {
            OpenNewThreadInMain();
            return;
        }
        // COMMAND entries run here, on the circuit — BEFORE the Href branch, which for an action is
        // only the landing page / the fallback for a renderer that does not know the id.
        if (item.IsAction && await TryRunMenuActionAsync(item))
            return;
        if (!string.IsNullOrEmpty(item.Href))
            NavigationManager.NavigateTo(item.Href);
        else
            NavigateToArea(item.Area);
    }

    /// <summary>
    /// Runs a <see cref="NodeMenuItemDefinition.Action"/> command on the CIRCUIT. Returns false for
    /// an id this renderer does not know, so the caller falls through to the href — a menu entry
    /// must never become a no-op just because it was authored against a newer portal.
    /// </summary>
    private async Task<bool> TryRunMenuActionAsync(NodeMenuItemDefinition item)
    {
        switch (item.Action)
        {
            case MenuActions.Recycle:
                // The action's Href IS the landing page (see NodeMenuItemDefinition.Action), so the
                // target path is exactly what LandingHref produced. Fall back to the current
                // namespace when a provider emitted no href.
                var target = string.IsNullOrEmpty(item.Href)
                    ? NavigationService.CurrentNamespace ?? ""
                    : item.Href.Trim('/');
                await RunRecycleAsync(target);
                return true;
            default:
                Logger.LogWarning(
                    "Menu entry '{Label}' carries action '{Action}', which this portal does not "
                    + "implement — falling back to navigation.", item.Label, item.Action);
                return false;
        }
    }

    /// <summary>
    /// The user-scoped chat URL for <paramref name="userId"/> — <c>/User/{id}/Chat</c>. That area is
    /// <see cref="UserActivityLayoutAreas.ChatArea"/>, which renders <c>ComposerAreaView</c>: the SAME
    /// <c>ThreadChatControl</c> the side panel mounts for a new chat. Static + internal so the routing
    /// contract is unit-testable without standing up a circuit.
    /// </summary>
    internal static string NewThreadHref(string userId) =>
        $"/User/{userId}/{UserActivityLayoutAreas.ChatArea}";

    /// <summary>
    /// Opens a brand-new conversation in the MAIN pane: navigates to <see cref="NewThreadHref"/> and
    /// CLOSES the side panel.
    /// <para>
    /// 🚨 It closes the panel rather than leaving it be: a conversation lives in EITHER the main view
    /// OR the side panel, never both (the same invariant <c>OnLocationChanged</c> enforces when the main
    /// view navigates to a thread). Leaving the panel open would put a second, independent composer on
    /// screen beside the one we just navigated to — two "new thread" surfaces, only one of which the
    /// user is looking at, each able to start its own thread.
    /// </para>
    /// <para>
    /// This replaces the previous side-panel implementation, which also had a live defect:
    /// <c>RequestAction("New")</c> is a bare event (<c>OnActionRequested?.Invoke</c>), and it fired
    /// BEFORE <c>SetVisible(true)</c> — so with the panel closed no composer was mounted, nothing was
    /// subscribed, and the reset signal was dropped on the floor.
    /// </para>
    /// </summary>
    private void OpenNewThreadInMain()
    {
        // 🚨 CircuitUser.ResolveUserId, never AccessService.Context alone: a menu click is a circuit
        // inbound activity, where CircuitAccessHandler stamps CircuitContext — Context is only set
        // during a hub message delivery, so reading it here is always null and the click silently
        // did nothing (the dead "New thread" menu entry).
        var userId = CircuitUser.ResolveUserId(AccessService);
        if (string.IsNullOrEmpty(userId))
        {
            Logger.LogWarning(
                "OpenNewThreadInMain: no circuit user resolved (CircuitContext and Context both empty) — not navigating.");
            return;
        }

        // Drop any side-panel conversation first, so the composer exists exactly once, in main.
        SidePanelState.SetContentPath(null);
        SidePanelState.SetTitle(null);
        if (SidePanelState.IsVisible)
            SidePanelState.SetVisible(false);

        NavigationManager.NavigateTo(NewThreadHref(userId));
    }

    /// <summary>
    /// Fallback: navigates to the specified area for the current node.
    /// Prefer setting Href on NodeMenuItemDefinition so navigation is independent of client state.
    /// </summary>
    private void NavigateToArea(string area)
    {
        var currentPath = NavigationService.CurrentNamespace ?? "";
        var url = string.IsNullOrEmpty(currentPath)
            ? $"/{area}"
            : $"/{currentPath}/{area}";
        NavigationManager.NavigateTo(url);
    }

    /// <summary>
    /// Returns menu items for the Node context. Permission filtering is done server-side by the providers.
    /// </summary>
    private IReadOnlyList<NodeMenuItemDefinition> GetNodeMenuItems() => _nodeMenuItems;

    /// <summary>
    /// Returns menu items for the Mesh context. Permission filtering is done server-side by the providers.
    /// </summary>
    private IReadOnlyList<NodeMenuItemDefinition> GetMeshMenuItems() => _meshMenuItems;

    // FlattenMenuItems used to live here: it replaced a parent with a divider plus its children
    // inline, DELETING the parent's label, icon and tooltip. NodeMenuItemList now renders the tree
    // as real Fluent sub-menus instead, so nothing flattens any more.

    /// <summary>
    /// Navigates to the Create page for a specific node type (fallback/legacy method).
    /// </summary>
    protected virtual Task NavigateToCreateAsync(string nodeTypePath)
    {
        var currentPath = NavigationService.CurrentNamespace ?? "";

        // Navigate to Create area with type as query parameter
        var createUrl = string.IsNullOrEmpty(currentPath)
            ? $"/Create?type={Uri.EscapeDataString(nodeTypePath)}"
            : $"/{currentPath}/Create?type={Uri.EscapeDataString(nodeTypePath)}";

        NavigationManager.NavigateTo(createUrl);
        return Task.CompletedTask;
    }

    /// <summary>
    /// On first render, imports the layout's JS module, registers the .NET reference, and
    /// restores the persisted side-panel state from local storage.
    /// </summary>
    /// <param name="firstRender">True on the component's first render pass.</param>
    /// <returns>A task that completes when first-render initialization finishes.</returns>
    protected override async Task OnAfterRenderAsync(bool firstRender)
    {
        if (firstRender)
        {
            try
            {
                await EnsureJsModuleAsync();
                dotNetRef = DotNetObjectReference.Create(this);
                await jsModule!.InvokeVoidAsync("initialize", dotNetRef);

                // Restore side panel state from localStorage
                await RestoreSidePanelStateAsync();
            }
            catch (Exception ex) when (ex is OperationCanceledException or JSDisconnectedException)
            {
                // Circuit disconnected during initialization
            }

            // 🚨 First render, not OnInitialized: the recycle flow raises a dialog, and the
            // FluentDialogProvider it needs is a CHILD of this very component — at OnInitialized it
            // has not been rendered yet, so the dialog would have nowhere to go.
            CheckRecycleUrl(NavigationManager.Uri);
        }
    }

    private async Task RestoreSidePanelStateAsync()
    {
        try
        {
            var saved = await jsModule!.InvokeAsync<SidePanelState?>("loadSidePanelState");
            if (saved != null)
            {
                // Anonymous circuits must never restore a visible panel — workspace
                // access fails for them and the panel children throw on render.
                if (!isAuthenticated && saved.IsVisible)
                    saved = saved with { IsVisible = false };
                SidePanelState.State = saved;
                if (isAuthenticated)
                    ResolveSidePanelContent();
                StateHasChanged();
            }
        }
        catch (Exception ex) when (ex is OperationCanceledException or JSDisconnectedException)
        {
            // Circuit disconnected
        }
    }

    private async Task SaveSidePanelStateAsync()
    {
        try
        {
            await EnsureJsModuleAsync();
            await jsModule!.InvokeVoidAsync("saveSidePanelState", SidePanelState.State);
        }
        catch (Exception ex) when (ex is OperationCanceledException or JSDisconnectedException)
        {
            // Circuit disconnected
        }
    }

    private void OnSidePanelNavigation(string path)
    {
        SidePanelState.SetContentPath(path);
        if (!SidePanelState.IsVisible)
            SidePanelState.SetVisible(true);
    }

    private void OnSidePanelStateChanged()
    {
        InvokeAsync(async () =>
        {
            ResolveSidePanelContent();
            await SaveSidePanelStateAsync();
            StateHasChanged();

            // When panel becomes visible, trigger window resize so Monaco editors
            // inside re-layout and re-activate keybindings (e.g., Alt+Enter)
            if (SidePanelState.IsVisible)
            {
                await Task.Delay(50); // Let render complete
                try
                {
                    await JSRuntime.InvokeVoidAsync("eval", "window.dispatchEvent(new Event('resize'))");
                }
                catch (Exception) { /* ignore JS errors */ }
            }
        });
    }

    private NavigationContext? _currentNavContext;

    // ─────────────────────── "Where is the chat?" hint (?hint=chat) ───────────────────────
    // Content pages cannot reach into the portal chrome, so a page that wants to POINT at the
    // ever-present chat entry (course lessons: "the chat is always one click away in the top
    // menu") links its own URL with `?hint=chat`. Landing on such a URL pulses the header's
    // side-panel/chat toggle for a few seconds — a visual "it's THIS one" — and nothing else:
    // no navigation, no panel state change, and clicking the toggle dismisses the pulse.

    /// <summary>True while the header chat toggle is pulsing (see <see cref="CheckChatHint"/>).</summary>
    protected bool ChatHintActive { get; private set; }

    private CancellationTokenSource? _chatHintCts;

    /// <summary>Arms the chat-toggle pulse when <paramref name="uri"/> carries <c>hint=chat</c>.
    /// The pulse self-dismisses after a few seconds so a shared or bookmarked hint link cannot
    /// leave the chrome blinking forever.</summary>
    private void CheckChatHint(string uri)
    {
        var query = new Uri(uri, UriKind.RelativeOrAbsolute).IsAbsoluteUri
            ? new Uri(uri).Query
            : uri.Contains('?') ? uri[uri.IndexOf('?')..] : "";
        // Exact key=value match — `hint=chatty` (or a `foo-hint=chat` key) must not pulse.
        var hinted = query.TrimStart('?').Split('&', StringSplitOptions.RemoveEmptyEntries)
            .Any(pair => string.Equals(pair, "hint=chat", StringComparison.OrdinalIgnoreCase));
        if (!hinted)
            return;
        _chatHintCts?.Cancel();
        var cts = _chatHintCts = new CancellationTokenSource();
        ChatHintActive = true;
        InvokeAsync(StateHasChanged);
        _ = Task.Delay(TimeSpan.FromSeconds(6), cts.Token).ContinueWith(_ =>
            // State flips on the renderer context — never from the timer's background thread.
            InvokeAsync(() =>
            {
                if (cts.IsCancellationRequested) return;
                ChatHintActive = false;
                StateHasChanged();
            }), TaskContinuationOptions.OnlyOnRanToCompletion);
    }

    /// <summary>Stops the pulse — the user found the toggle (clicked it), which is the hint's job done.</summary>
    private void DismissChatHint()
    {
        if (!ChatHintActive) return;
        _chatHintCts?.Cancel();
        ChatHintActive = false;
    }

    // ───────────────────────────── Recycle: a PAGE-level action ─────────────────────────────
    // 🚨 The whole flow lives HERE, on the circuit, because the shell is the one thing that
    // survives the hub it tears down (#2202). Recycle used to be a confirmation layout area
    // HOSTED ON THE TARGET HUB: its confirm button pushed a RedirectControl into the area stream
    // and then posted DisposeRequest to that same hub, so the redirect had to outrun a teardown of
    // the stream carrying it — it did not, and the button read as dead. Afterwards the landing page
    // came back as a per-area refresh mosaic, every module faulting and re-subscribing on its own
    // ("refresh is a matter of the page, not each module").
    //
    // Two doors, ONE implementation:
    //   • the node menu's ♻️ entry, an ACTION (MenuActions.Recycle) — no navigation at all;
    //   • the /{path}/Recycle URL, which the stale-build banner links to and people bookmark.
    // Both land in RunRecycleAsync, which confirms on the circuit, recycles through the CIRCUIT's
    // hub, waits for the address to answer again, and then performs ONE page-level reload.

    /// <summary>The path currently being recycled, or null. Guards against a second confirm while a
    /// recycle is in flight — including the URL door firing again on the navigation we ourselves
    /// perform.</summary>
    private string? _recyclingPath;

    /// <summary>The in-flight recycle's subscription, held so circuit teardown cancels it. It can
    /// legitimately live for the whole recycle budget, and a completion callback marshalled onto a
    /// disposed component is the post-teardown straggler class.</summary>
    private IDisposable? _recycleSubscription;

    /// <summary>
    /// Runs the page-level flow when <paramref name="uri"/> is a <c>/{path}/Recycle</c> URL.
    /// The area behind it renders only a passive "Recycling…" card, so nothing races the teardown.
    /// </summary>
    private void CheckRecycleUrl(string uri)
    {
        var relative = NavigationManager.ToBaseRelativePath(uri);
        var cut = relative.IndexOfAny(['?', '#']);
        if (cut >= 0)
            relative = relative[..cut];
        // Cheap string pre-filter so an ordinary navigation costs nothing.
        if (RecycleLayoutArea.TryGetTargetFromUrl(relative) is not { Length: > 0 })
            return;

        // 🚨 Resolve the viewer HERE, on the circuit thread, and carry the id explicitly through the
        // Rx hops below. AccessService.CircuitContext is an AsyncLocal that resolves ONLY on the
        // circuit's own thread and is documented to be wiped by "a deferred sync write, an Rx
        // continuation" — which is precisely what the resolver's and the permission fold's
        // Subscribe callbacks are. The parameterless CheckPermission(path, permission) overload
        // calls ResolveUserId INTERNALLY, so composing it inside the resolver's callback would have
        // read a wiped AsyncLocal, fallen back to WellKnownUsers.Anonymous, and silently denied the
        // URL door to everyone — a fail-closed that looks identical to "you lack Update".
        var viewerId = CircuitUser.ResolveUserId(AccessService);
        if (string.IsNullOrEmpty(viewerId))
        {
            Logger.LogInformation(
                "Recycle URL '{Url}' ignored — no circuit user resolved (anonymous circuit).", relative);
            return;
        }
        // 🚨 …then ASK THE RESOLVER, because the string is not the decision. A node may itself be
        // called "…/Recycle", and then this URL is that node's own page, not a request to recycle
        // its parent. ResolveNavigationPath is the same resolution AreaPage performs, so the door
        // opens exactly when the page really is rendering a node's Recycle AREA.
        PathResolver.ResolveNavigationPath(relative)
            .Take(1)
            .Subscribe(
                resolution =>
                {
                    if (resolution is null
                        || !string.Equals(resolution.Remainder?.Trim('/'), MeshNodeLayoutAreas.RecycleArea,
                            StringComparison.OrdinalIgnoreCase))
                        return;
                    var target = resolution.Prefix.Trim('/');
                    if (target.Length == 0)
                        return;
                    // The URL is a door into the same command the menu entry runs, so it needs the
                    // same gate. The menu entry is withheld server-side without Permission.Update
                    // (RecycleLayoutArea.GetMenuItem), but a URL is typed, linked and bookmarked —
                    // nothing withholds it. Fail CLOSED: a check that faults, or never answers,
                    // does not open the dialog. The EXPLICIT-userId overload — see above.
                    Hub.CheckPermission(target, viewerId, Permission.Update)
                        .Take(1)
                        .Subscribe(
                            allowed =>
                            {
                                if (allowed)
                                    InvokeAsync(() => RunRecycleAsync(target));
                                else
                                    Logger.LogInformation(
                                        "Recycle URL for '{Path}' ignored — the viewer lacks Update on it.",
                                        target);
                            },
                            ex => Logger.LogWarning(ex,
                                "Recycle URL for '{Path}' ignored — the permission check did not answer.",
                                target));
                },
                ex => Logger.LogWarning(ex,
                    "Recycle URL '{Url}' ignored — path resolution did not answer.", relative));
    }

    /// <summary>
    /// Confirm → recycle → ONE page-level reload. Every step runs on the circuit; nothing here
    /// depends on the hub being torn down.
    /// </summary>
    private async Task RunRecycleAsync(string targetPath)
    {
        if (string.IsNullOrEmpty(targetPath) || _recyclingPath is not null)
            return;

        var landing = RecycleLayoutArea.LandingHref(targetPath);

        // The framework's own confirmation — raised by the page's FluentDialogProvider, so it is
        // owned by the circuit and cannot be torn down by the recycle it is confirming.
        var dialog = await DialogService.ShowMessageBoxAsync(new DialogParameters<MessageBoxContent>
        {
            Content = new MessageBoxContent
            {
                Title = AccessService.Localize("ui.recycleConfirmTitle"),
                MarkupMessage = new MarkupString(System.Net.WebUtility.HtmlEncode(
                    AccessService.Localize("ui.recycleConfirmBody"))),
            },
            PrimaryAction = AccessService.Localize("menu.recycle"),
            SecondaryAction = AccessService.Localize("common.cancel"),
        });
        var result = await dialog.Result;
        if (result.Cancelled)
        {
            // Cancel from the URL door has somewhere to go (the node's own page); from the menu the
            // user is already where they were, and NavigateTo to the same URL is a no-op reload we
            // do not want. Only leave the Recycle URL.
            if (RecycleLayoutArea.TryGetTargetFromUrl(
                    NavigationManager.ToBaseRelativePath(NavigationManager.Uri)) is not null)
                NavigationManager.NavigateTo(landing);
            return;
        }

        _recyclingPath = targetPath;

        // 🚨 Hub is the CIRCUIT's hub — never the target's. Reactive + Subscribe, never awaited:
        // RecycleNode posts the DisposeRequest and then rides the framework's own recycling-aware
        // read until the address answers from a FRESH activation.
        _recycleSubscription?.Dispose();
        _recycleSubscription = Hub.RecycleNode(targetPath).Subscribe(
            _ => InvokeAsync(() =>
            {
                _recyclingPath = null;
                // forceLoad: the page-level refresh the maintainer asked for. One reload against an
                // already-re-activated hub, instead of N modules each discovering the teardown.
                NavigationManager.NavigateTo(landing, forceLoad: true);
            }),
            ex => InvokeAsync(() =>
            {
                _recyclingPath = null;
                Logger.LogWarning(ex, "Recycle of '{Path}' did not settle within the budget", targetPath);
                // Surfaced, never swallowed: the dispose WAS posted, so the node may still be
                // mid-recycle — say so rather than dropping the user on a page we cannot vouch for.
                ErrorSink.Report(string.Format(
                    System.Globalization.CultureInfo.InvariantCulture,
                    AccessService.Localize("ui.recycleFailed"), targetPath, ex.Message));
            }));
    }


    /// <summary>
    /// Collapses the side pane when the MAIN view navigates to a thread node. Fired on the real
    /// <see cref="NavigationManager.LocationChanged"/> event (a genuine URL navigation) — never the
    /// nav-context stream — so a background context re-emission during a running round cannot collapse
    /// the active side-panel chat (the recurring "chat disappears during execution" bug). An unsent
    /// new-chat composer (empty <c>ContentPath</c>) is preserved: it is not an opened thread. This
    /// implements "opening a thread in the main pane collapses the side pane" for EVERY entry point
    /// (composer full-screen submit, Open-Full-Screen, a thread link) since they all navigate.
    /// </summary>
    /// <summary>
    /// Collapses the side pane when the MAIN view navigates to a thread node. Fired on the real
    /// <see cref="NavigationManager.LocationChanged"/> event (a genuine URL navigation) — never the
    /// nav-context stream — so a background context re-emission during a running round cannot collapse
    /// the active side-panel chat (the recurring "chat disappears during execution" bug). An unsent
    /// new-chat composer (empty <c>ContentPath</c>) is preserved: it is not an opened thread. This
    /// implements "opening a thread in the main pane collapses the side pane" for EVERY entry point
    /// (composer full-screen submit, Open-Full-Screen, a thread link) since they all navigate.
    /// </summary>
    private void OnLocationChanged(object? sender, Microsoft.AspNetCore.Components.Routing.LocationChangedEventArgs e)
    {
        CheckChatHint(e.Location);
        CheckRecycleUrl(e.Location);
        if (!isAuthenticated || !SidePanelState.IsVisible || string.IsNullOrEmpty(SidePanelState.ContentPath))
            return;
        var path = NavigationManager.ToBaseRelativePath(e.Location);
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
            path = path[..cut];
        // Only a THREAD in the main view collapses the pane — identified by the stable "/_Thread/"
        // segment (SidePanelChatKeying.ThreadSlug returns null for any non-thread path).
        if (SidePanelChatKeying.ThreadSlug(path) is null)
            return;
        SidePanelState.SetVisible(false);
        InvokeAsync(StateHasChanged);
    }

    private void OnNavigationContextChanged(NavigationContext? ctx)
    {
        _currentNavContext = ctx;

        // New model: while a thread is open in the MAIN view, a VISIBLE side panel peeks that thread's
        // MAIN (context) node. Keep that peek in sync as the user moves between threads — but only when
        // the panel is ALREADY peeking a context (its content is a non-thread node), NEVER replacing a
        // side-panel CHAT the user is in. Visibility is untouched (collapsed stays collapsed); the
        // toggle is what opens the peek (ToggleSidePanel). When this handles the navigation we skip the
        // hide rule below, which only governs a panel holding a chat/thread.
        if (TrySyncContextPeek(ctx))
        {
            InvokeAsync(StateHasChanged);
            return;
        }

        // A thread lives in EITHER the main view OR the side panel, never both — but ONLY
        // close the side panel when the user opened a DIFFERENT thread full-screen than the
        // one already shown in the panel. Closing on the SAME thread (or on a brand-new
        // side-panel chat) is what made the active side-panel conversation vanish during
        // normal chat → submit → navigate use. The decision rule lives in SidePanelChatKeying
        // so it is unit-testable without a render host.
        if (ctx?.Node != null
            && SidePanelChatKeying.ShouldHideSidePanelOnThreadNavigation(
                ctx.Node.NodeType, ctx.Node.Path, SidePanelState.ContentPath, SidePanelState.IsVisible))
        {
            // Notable: collapsing a VISIBLE side panel because the main view opened a DIFFERENT thread.
            // If navNode and ContentPath are actually the SAME thread this is the vanish bug — the log
            // makes it visible instead of silent (SameThreadIdentity above should already prevent it).
            Logger.LogWarning(
                "[SidePanel] Auto-hiding side panel on thread nav: navNode='{NavPath}' (type {NavType}) "
                + "vs contentPath='{ContentPath}'.",
                ctx.Node.Path, ctx.Node.NodeType, SidePanelState.ContentPath);
            SidePanelState.SetVisible(false);
        }

        InvokeAsync(StateHasChanged);
    }

    /// <summary>
    /// Keeps a VISIBLE context-peek panel pointed at the current main thread's context node as the user
    /// navigates between threads. Returns true when it owns the navigation (panel is peeking a context),
    /// so the caller skips the "hide on different thread" rule (which only governs a panel holding a
    /// chat). No-op (false) when not on a thread, or the panel is hidden / empty (new chat) / holding a
    /// thread-chat — those keep their existing behavior.
    /// </summary>
    private bool TrySyncContextPeek(NavigationContext? ctx)
    {
        var contextPath = CurrentThreadContextPath();
        if (contextPath is null)
            return false;
        var current = SidePanelState.ContentPath;
        if (!SidePanelState.IsVisible || string.IsNullOrEmpty(current) || IsThreadPath(current))
            return false;
        if (!string.Equals(current, contextPath, StringComparison.OrdinalIgnoreCase))
        {
            SidePanelState.SetTitle(
                MeshWeaver.AI.NavigationContextProjection.ContextChipLabel(ctx) ?? LastSegmentOf(contextPath));
            SidePanelState.SetContentPath(contextPath);
        }
        return true;
    }

    /// <summary>
    /// Closes the mobile navigation menu when the viewport switches to a desktop size.
    /// </summary>
    protected override void OnParametersSet()
    {
        if (ViewportInformation.IsDesktop && isNavMenuOpen)
        {
            isNavMenuOpen = false;
            CloseMobileNavMenu();
        }
    }

    /// <summary>
    /// Current viewport classification (desktop/mobile, ultra-low), supplied as a cascading value.
    /// </summary>
    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }

    /// <summary>
    /// Toggles the mobile navigation menu open or closed.
    /// </summary>
    protected void ToggleNavMenu()
    {
        isNavMenuOpen = !isNavMenuOpen;
    }

    /// <summary>
    /// Closes the mobile navigation menu and re-renders.
    /// </summary>
    protected void CloseMobileNavMenu()
    {
        isNavMenuOpen = false;
        StateHasChanged();
    }


    /// <summary>
    /// True when the routed view is a chrome-free presentation (<c>/Present</c> — a deck or a slide
    /// presenter). In this mode the portal hides its top bar AND side navigation so the slide stage
    /// is truly full-screen; keyboard navigation (arrows / space / page keys / Esc) drives the walk.
    /// Computed synchronously from the URL so there is no header flash on the first paint of a Present
    /// route; re-evaluated on every navigation (<see cref="OnNavigationContextChanged"/> re-renders).
    /// </summary>
    protected bool IsPresentMode => IsPresentRoute(NavigationManager.Uri);

    /// <summary>
    /// The wire name of the chrome-free presentation area. The area itself is registered by the
    /// Publish pack's in-mesh source (<c>Publish/Deck/Source/DeckLayoutAreas.cs</c>, which owns
    /// this value) — the compiled shell only recognizes the route to suppress its chrome.
    /// </summary>
    private const string PresentArea = "Present";

    /// <summary>True when the URL's node-address path (query/fragment stripped) ends with the <c>/Present</c> area.</summary>
    private bool IsPresentRoute(string uri)
    {
        var path = NavigationManager.ToBaseRelativePath(uri);
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
            path = path[..cut];
        path = path.Trim('/');
        return path.Equals(PresentArea, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith($"/{PresentArea}", StringComparison.OrdinalIgnoreCase);
    }

    // Side panel is gated on auth — anonymous users see neither toggle nor pane.
    /// <summary>
    /// Whether the side panel should render — requires an authenticated circuit, visible state,
    /// and a non-Present route (Present mode is chrome-free, so the side panel is suppressed too).
    /// </summary>
    public bool IsSidePanelVisible => isAuthenticated && SidePanelState.IsVisible && !IsPresentMode;

    /// <summary>
    /// The side panel's current docking position.
    /// </summary>
    protected SidePanelPosition SidePanelPositionValue => SidePanelState.Position;

    /// <summary>
    /// Toggles the side panel. When a thread is shown in the MAIN view, the thread STAYS in the main
    /// view and the panel peeks the thread's MAIN (context) node — opening always brings the main path
    /// (no navigate-away). Otherwise flips visibility normally. Persisted size is applied on open.
    /// </summary>
    /// <returns>A task that completes once panel state and size have been applied.</returns>
    public async Task ToggleSidePanel()
    {
        DismissChatHint();
        var contextPath = CurrentThreadContextPath();

        // On a thread in the main view → the side panel is a peek of the thread's context node.
        if (contextPath is not null)
        {
            if (SidePanelState.IsVisible)
            {
                SidePanelState.SetVisible(false);
            }
            else
            {
                // Set the content BEFORE showing so opening always brings the main path.
                SidePanelState.SetTitle(
                    MeshWeaver.AI.NavigationContextProjection.ContextChipLabel(_currentNavContext)
                    ?? LastSegmentOf(contextPath));
                SidePanelState.OpenWithContent(contextPath);
                await ApplyPersistedSizeAsync();
            }
            return;
        }

        // Not on a thread — normal toggle (new-chat composer / current content).
        SidePanelState.Toggle();
        if (SidePanelState.IsVisible)
        {
            // Apply persisted size when opening
            await ApplyPersistedSizeAsync();
        }
    }

    /// <summary>
    /// When the MAIN view is showing a thread, the path of that thread's MAIN (context) node — the node
    /// the side panel peeks. Null when not on a thread, or the thread is self-referencing (no distinct
    /// context). Drives the side-panel-as-context-peek model AND the context-aware toggle icon.
    /// </summary>
    private string? CurrentThreadContextPath()
    {
        var node = _currentNavContext?.Node;
        if (node is null || !ThreadNodeType.IsThreadNodeType(node.NodeType))
            return null;
        var mainNode = node.MainNode;
        return !string.IsNullOrEmpty(mainNode)
               && !string.Equals(mainNode, node.Path, StringComparison.OrdinalIgnoreCase)
            ? mainNode : null;
    }

    /// <summary>True when the main view is on a thread with a distinct context node to peek.</summary>
    protected bool HasThreadContext => CurrentThreadContextPath() is not null;

    /// <summary>Tooltip for the side-panel toggle, matching its context-aware icon.</summary>
    protected string SidePanelToggleTitle =>
        IsSidePanelVisible ? Access.Localize("chat.closeSidePanel")
        : HasThreadContext ? Access.Localize("chat.showContext")
        : Access.Localize("chat.chat");

    private static string LastSegmentOf(string path)
    {
        var idx = path.LastIndexOf('/');
        return idx >= 0 && idx < path.Length - 1 ? path[(idx + 1)..] : path;
    }

    private static bool IsThreadPath(string path)
        => path.Contains($"/{ThreadNodeType.ThreadPartition}/", StringComparison.OrdinalIgnoreCase);

    private async Task ApplyPersistedSizeAsync()
    {
        await EnsureJsModuleAsync();
        await jsModule!.InvokeVoidAsync("applySidePanelSize", SidePanelState.Width, SidePanelState.Height);
    }

    private async Task EnsureJsModuleAsync()
    {
        jsModule ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./_content/MeshWeaver.Blazor.Portal/Layout/PortalLayoutBase.razor.js");
    }


    // Side panel content state.
    //
    // 🚨 The side-panel chat's identity (its Blazor @key AND its cached control) is keyed
    // ONLY on the stable content path — NEVER on the navigation context's PrimaryPath. A
    // key that embeds PrimaryPath flips on every navigation, so Blazor tears down + recreates
    // the ThreadChatView and DESTROYS the in-progress conversation (the recurring "lost the
    // thread again" nuisance). The context-attachment chip is refreshed LIVE inside
    // ThreadChatView via its NavigationService.NavigationContext subscription
    // (OnNavigationContextChanged) — the component never needs rebuilding to reflect a
    // navigation change. The keying/caching rules live in SidePanelChatKeying so the
    // invariant is unit-testable without a render host.
    private const string sidePanelContentKey = SidePanelChatKeying.NewChatKey;
    private ThreadChatControl? _cachedSidePanelControl;
    private string? _cachedContentPath;

    private LayoutAreaControl? sidePanelViewModel;
    private string? resolvedSidePanelPath;

    /// <summary>
    /// Resolves ContentPath via IPathResolver (same as AreaPage) and builds LayoutAreaControl.
    /// If the content path points to a node that no longer exists (e.g. deleted thread),
    /// the path resolves to a parent with satellite segments as remainder — detect and clear.
    /// </summary>
    private void ResolveSidePanelContent()
    {
        var contentPath = SidePanelState.ContentPath;
        if (contentPath == resolvedSidePanelPath)
            return;

        resolvedSidePanelPath = contentPath;

        if (string.IsNullOrEmpty(contentPath))
        {
            sidePanelViewModel = null;
            return;
        }

        // 🎯 A thread path IS its own node address — it resolves to itself (Prefix=path,
        // Remainder=empty). Render it DIRECTLY and skip PathResolver. Round-tripping a
        // FRESHLY-created thread through the eventually-consistent resolver (IMeshQueryCore
        // .Query) LAGS: the just-created node isn't indexed yet, so ResolvePath emits
        // split-onto-parent states that fail the validity filter below, and under load the
        // first VALID resolution can exceed the 10 s Timeout → onError → SetContentPath(null)
        // NUKES the live side-panel chat ("disappears after it starts executing"). The thread
        // address is authoritative and known here — there is nothing to resolve, so build the
        // SAME LayoutAreaControl a successful resolution would (area null ⇒ the thread's
        // default chat area), synchronously, with no query and no timeout. CQRS: never
        // round-trip a single known node through the lagging query index.
        if (IsThreadPath(contentPath))
        {
            sidePanelViewModel = Controls.LayoutArea(
                (Address)contentPath, new LayoutAreaReference(null) { Id = "" });
            return;
        }

        // Reactive — Subscribe, never await on PathResolver chain (deadlock surface;
        // see Doc/Architecture/AsynchronousCalls.md).
        // 🎯 Wait for a VALID resolution, then take exactly one. ResolvePath is a LIVE stream that re-emits
        // whenever the resolved node changes. Right after a thread is CREATED its node is not yet readable,
        // so the first emissions are transient null / split-onto-the-parent-partition states; and it re-emits
        // again on every chat round as the thread node updates. The old code wiped the side panel to an empty
        // "New Thread" on ANY null/split emission → it BOTH failed a just-created thread (the initial
        // not-ready null wiped it) AND wiped a healthy open thread mid-session (the SidePanelChatTenMessages-
        // Test round-4 vanish). Filtering to the FIRST valid resolution skips the transient states (no wipe
        // on a mid-update re-emit) yet still resolves once the node is readable; a genuinely unresolvable
        // path (a deleted thread) never yields a valid resolution, so the Timeout clears it.
        PathResolver.ResolvePath(contentPath)
            .Where(resolution => resolution != null
                && (string.Equals(resolution.Prefix, contentPath, StringComparison.OrdinalIgnoreCase)
                    || string.IsNullOrEmpty(resolution.Remainder)))
            .Take(1)
            .Timeout(TimeSpan.FromSeconds(10))
            .Subscribe(
                resolution =>
                {
                    var (area, id) = ParseSidePanelRemainder(resolution!.Remainder);
                    var reference = new LayoutAreaReference(area) { Id = id ?? "" };
                    sidePanelViewModel = Controls.LayoutArea((Address)resolution.Prefix, reference);
                    InvokeAsync(StateHasChanged);
                },
                _ =>
                {
                    // No valid resolution within the window → the content path is genuinely unresolvable
                    // (e.g. a deleted thread, or a path that resolves only onto its parent partition). Clear
                    // back to the new-chat state.
                    sidePanelViewModel = null;
                    SidePanelState.SetContentPath(null);
                    resolvedSidePanelPath = null;
                    InvokeAsync(StateHasChanged);
                });
    }

    private static (string? Area, string? Id) ParseSidePanelRemainder(string? remainder)
    {
        if (string.IsNullOrEmpty(remainder))
            return (null, null);
        var parts = remainder.Split('/', 2);
        var area = parts[0];
        var id = parts.Length > 1 ? parts[1] : null;
        return (area, id);
    }

    private ThreadChatControl GetSidePanelControl()
    {
        var contentPath = SidePanelState.ContentPath ?? string.Empty;

        // Return the cached control unless the CONTENT path changed. Navigation
        // (PrimaryPath) is deliberately NOT a cache input: rebuilding on every node
        // click would replace the ViewModel and re-bind the chat (and, combined with a
        // PrimaryPath-keyed @key, tear it down entirely). The current navigation context
        // only seeds the INITIAL attachment chip below; ThreadChatView then keeps the
        // chip in sync via its own NavigationContext subscription — no rebuild needed.
        if (_cachedSidePanelControl != null
            && !SidePanelChatKeying.ShouldRebuildControl(_cachedContentPath, contentPath))
            return _cachedSidePanelControl;

        var context = _currentNavContext;
        var contextPath = context?.PrimaryPath;
        // Label the OWNER, never the navigated satellite (a thread "hi"): ContextChipLabel returns null
        // for a satellite so the chip falls back to the main-node path's last segment, not the thread name.
        var contextDisplayName = MeshWeaver.AI.NavigationContextProjection.ContextChipLabel(context);
        _cachedContentPath = contentPath;
        _cachedSidePanelControl = new ThreadChatControl()
            .WithThreadPath(contentPath)
            .WithInitialContext(contextPath ?? string.Empty)
            .WithInitialContextDisplayName(contextDisplayName ?? string.Empty);
        return _cachedSidePanelControl;
    }

    /// <summary>
    /// Unsubscribes from side-panel, navigation, and menu events and disposes JS interop references.
    /// </summary>
    public void Dispose()
    {
        SidePanelState.OnStateChanged -= OnSidePanelStateChanged;
        NavigationService.SidePanelNavigationRequested -= OnSidePanelNavigation;
        NavigationManager.LocationChanged -= OnLocationChanged;
        _navContextSubscription?.Dispose();
        _nodeMenuSubscription?.Dispose();
        _meshMenuSubscription?.Dispose();
        _aiMenuSubscription?.Dispose();
        _gitHubMenuSubscription?.Dispose();
        _topBarMenusSubscription?.Dispose();
        // An in-flight recycle can legitimately outlive the circuit that asked for it — the
        // teardown it is waiting on is exactly the kind of thing a user navigates away from. Its
        // completion callback marshals onto THIS component, so it must not survive it.
        _recycleSubscription?.Dispose();
        foreach (var subscription in _contributedMenuSubscriptions.Values)
            subscription.Dispose();
        _contributedMenuSubscriptions.Clear();
        dotNetRef?.Dispose();
        // Fire-and-forget, but OBSERVED — and the ValueTask was previously dropped without even a
        // discard. Disposing a JS module after the circuit is gone throws JSDisconnectedException
        // ROUTINELY (the same exception this file already tolerates at two other interop sites), so
        // an unobserved task here is a steady trickle of UnobservedTaskException with nothing
        // pointing back at this line. Blocking is not the alternative: this runs on the circuit's
        // synchronous Dispose.
        if (jsModule is not null)
            ObserveModuleDisposal(jsModule, Logger);
    }

    /// <summary>
    /// Awaits a JS module's disposal off the caller's stack, swallowing the disconnect the circuit
    /// teardown makes expected and logging anything else. Static and parameterised so it holds no
    /// reference to the component it outlives.
    /// </summary>
    private static async void ObserveModuleDisposal(IJSObjectReference module, ILogger logger)
    {
        try
        {
            await module.DisposeAsync();
        }
        catch (Exception ex) when (ex is OperationCanceledException or JSDisconnectedException)
        {
            // The circuit went away first — the module went with it. Nothing to dispose, nothing wrong.
        }
        catch (Exception ex)
        {
            logger.LogWarning(ex, "Disposing the portal layout's JS module faulted");
        }
    }

    /// <summary>
    /// Checks if a string is likely an emoji (short string, not a path/URL).
    /// </summary>
    protected static bool IsEmoji(string? value)
    {
        if (string.IsNullOrEmpty(value))
            return false;

        // Emojis are typically 1-4 characters (including surrogate pairs and modifiers)
        // SVG paths start with / or http or contain .svg
        if (value.Length > 8)
            return false;

        if (value.StartsWith("/") || value.StartsWith("http") || value.Contains(".svg"))
            return false;

        return true;
    }
}


