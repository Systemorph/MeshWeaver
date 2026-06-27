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
using Microsoft.JSInterop;

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

    // Menu context names (must match NodeMenuItemsExtensions.NodeMenuContext / MeshMenuContext).
    private const string NodeMenuContext = "Node";
    private const string MeshMenuContext = "Mesh";
    private const string AiMenuContext = "AI";

    // Menu items per context from IMenuItemsProvider (populated by LayoutAreaView from $Menu:{context} streams)
    private IReadOnlyList<NodeMenuItemDefinition> _nodeMenuItems = [];
    private IReadOnlyList<NodeMenuItemDefinition> _meshMenuItems = [];
    private IReadOnlyList<NodeMenuItemDefinition> _aiMenuItems = [];
    private IDisposable? _nodeMenuSubscription;
    private IDisposable? _meshMenuSubscription;
    private IDisposable? _aiMenuSubscription;


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
    /// Handles a click on a dynamic menu item.
    /// Uses Href for absolute navigation when set, otherwise constructs URL from Area.
    /// </summary>
    private void HandleMenuItemClick(NodeMenuItemDefinition item)
    {
        isNodeMenuOpen = false;
        isMeshMenuOpen = false;
        isAiMenuOpen = false;
        if (!string.IsNullOrEmpty(item.Href))
            NavigationManager.NavigateTo(item.Href);
        else
            NavigateToArea(item.Area);
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

    private void OnNavigationContextChanged(NavigationContext? ctx)
    {
        _currentNavContext = ctx;

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
            SidePanelState.SetVisible(false);
        }

        InvokeAsync(StateHasChanged);
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


    // Side panel is gated on auth — anonymous users see neither toggle nor pane.
    /// <summary>
    /// Whether the side panel should render — requires both an authenticated circuit and visible state.
    /// </summary>
    public bool IsSidePanelVisible => isAuthenticated && SidePanelState.IsVisible;

    /// <summary>
    /// The side panel's current docking position.
    /// </summary>
    protected SidePanelPosition SidePanelPositionValue => SidePanelState.Position;

    /// <summary>
    /// Toggles the side panel. When a thread is shown full-screen, navigates to the thread's content
    /// node and reopens the thread inside the panel; otherwise flips visibility, applying the
    /// persisted size when opening.
    /// </summary>
    /// <returns>A task that completes once panel state and size have been applied.</returns>
    public async Task ToggleSidePanel()
    {
        var context = _currentNavContext;

        // Check if viewing a thread full-screen by checking the node's NodeType
        if (context?.Node != null && ThreadNodeType.IsThreadNodeType(context.Node.NodeType))
        {
            var threadPath = context.Namespace;
            var mainNode = context.Node.MainNode;

            // Navigate to content entity (or home if self-referencing)
            var navigateTo = mainNode != context.Node.Path ? $"/{mainNode}" : "/";
            NavigationManager.NavigateTo(navigateTo);

            // Open panel with thread and set title
            SidePanelState.SetTitle(context.Node.Name ?? "Thread");
            SidePanelState.OpenWithContent(threadPath);
            await ApplyPersistedSizeAsync();
        }
        else
        {
            // Normal toggle
            SidePanelState.Toggle();

            if (SidePanelState.IsVisible)
            {
                // Apply persisted size when opening
                await ApplyPersistedSizeAsync();
            }
        }
    }

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

        // Reactive — Subscribe, never await on PathResolver chain (deadlock surface;
        // see Doc/Architecture/AsynchronousCalls.md).
        PathResolver.ResolvePath(contentPath).Subscribe(resolution =>
        {
            if (resolution == null)
            {
                sidePanelViewModel = null;
                SidePanelState.SetContentPath(null);
                resolvedSidePanelPath = null;
                InvokeAsync(StateHasChanged);
                return;
            }

            if (!string.Equals(resolution.Prefix, contentPath, StringComparison.OrdinalIgnoreCase)
                && !string.IsNullOrEmpty(resolution.Remainder))
            {
                sidePanelViewModel = null;
                SidePanelState.SetContentPath(null);
                resolvedSidePanelPath = null;
                InvokeAsync(StateHasChanged);
                return;
            }

            var (area, id) = ParseSidePanelRemainder(resolution.Remainder);
            var reference = new LayoutAreaReference(area) { Id = id ?? "" };
            sidePanelViewModel = Controls.LayoutArea((Address)resolution.Prefix, reference);
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
        _navContextSubscription?.Dispose();
        _nodeMenuSubscription?.Dispose();
        _meshMenuSubscription?.Dispose();
        _aiMenuSubscription?.Dispose();
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


