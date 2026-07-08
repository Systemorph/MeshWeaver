using System.Reactive.Linq;
using System.Reactive.Threading.Tasks;
using MeshWeaver.AI;
using MeshWeaver.Blazor.Portal.Resize;
using MeshWeaver.Blazor.Portal.SidePanel;
using MeshWeaver.Blazor.Services;
using MeshWeaver.ContentCollections;
using MeshWeaver.Data;
using MeshWeaver.Graph;
using MeshWeaver.Layout;
using MeshWeaver.Mesh;
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
    }

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
                var userId = AccessService?.Context?.ObjectId;
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
        var userId = AccessService?.Context?.ObjectId;
        if (string.IsNullOrEmpty(userId))
            return;
        NavigationManager.NavigateTo($"/User/{userId}/Activity");
    }

    /// <summary>
    /// Sentinel <see cref="NodeMenuItemDefinition.Area"/> for the AI menu's "New thread" item. It carries
    /// NO Href, so it is handled imperatively in <see cref="HandleMenuItemClick"/> (open the chat panel in
    /// new-thread mode) instead of navigating. Lives here so the menu seed and the handler agree.
    /// </summary>
    public const string AiNewThreadAction = "ai-new-thread";

    /// <summary>
    /// Handles a click on a dynamic menu item.
    /// Uses Href for absolute navigation when set, otherwise constructs URL from Area.
    /// </summary>
    private void HandleMenuItemClick(NodeMenuItemDefinition item)
    {
        isNodeMenuOpen = false;
        isMeshMenuOpen = false;
        isAiMenuOpen = false;
        isGitHubMenuOpen = false;
        // Imperative actions (no Href): the AI menu's "New thread" opens the chat panel fresh.
        if (string.Equals(item.Area, AiNewThreadAction, StringComparison.Ordinal))
        {
            _ = OpenNewThreadInSidePanel();
            return;
        }
        if (!string.IsNullOrEmpty(item.Href))
            NavigationManager.NavigateTo(item.Href);
        else
            NavigateToArea(item.Area);
    }

    /// <summary>
    /// Opens the chat side panel ready for a brand-new thread: clears any shown content (so the new-chat
    /// composer renders), signals a mounted composer to reset to chat mode, and shows the panel (applying
    /// the persisted size). Same end state as the side panel's existing New-thread button.
    /// </summary>
    private async Task OpenNewThreadInSidePanel()
    {
        SidePanelState.SetContentPath(null);
        SidePanelState.SetTitle(null);
        SidePanelState.RequestAction("New");
        if (!SidePanelState.IsVisible)
        {
            SidePanelState.SetVisible(true);
            await ApplyPersistedSizeAsync();
        }
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

    private static readonly NodeMenuItemDefinition Separator = new("", "_separator");

    /// <summary>
    /// Flattens hierarchical menu items: parent items with Children are replaced by
    /// a separator followed by their children inline.
    /// </summary>
    private static IReadOnlyList<NodeMenuItemDefinition> FlattenMenuItems(IReadOnlyList<NodeMenuItemDefinition> items)
    {
        var hasChildren = false;
        foreach (var item in items)
        {
            if (item.Children is { Count: > 0 })
            {
                hasChildren = true;
                break;
            }
        }
        if (!hasChildren)
            return items;

        var result = new List<NodeMenuItemDefinition>();
        foreach (var item in items)
        {
            if (item.Children is { Count: > 0 })
            {
                if (result.Count > 0)
                    result.Add(Separator);
                result.AddRange(item.Children);
            }
            else
            {
                result.Add(item);
            }
        }
        return result;
    }

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

    /// <summary>True when the URL's node-address path (query/fragment stripped) ends with the <c>/Present</c> area.</summary>
    private bool IsPresentRoute(string uri)
    {
        var path = NavigationManager.ToBaseRelativePath(uri);
        var cut = path.IndexOfAny(['?', '#']);
        if (cut >= 0)
            path = path[..cut];
        path = path.Trim('/');
        return path.Equals(DeckLayoutAreas.PresentArea, StringComparison.OrdinalIgnoreCase)
            || path.EndsWith($"/{DeckLayoutAreas.PresentArea}", StringComparison.OrdinalIgnoreCase);
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
        IsSidePanelVisible ? "Close side panel"
        : HasThreadContext ? "Show context"
        : "Chat";

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
        dotNetRef?.Dispose();
        jsModule?.DisposeAsync();
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


