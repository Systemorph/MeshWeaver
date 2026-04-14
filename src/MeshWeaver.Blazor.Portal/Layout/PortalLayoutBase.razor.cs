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
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.Portal.Layout;

public partial class PortalLayoutBase : LayoutComponentBase, IDisposable
{
    [Inject] protected IJSRuntime JSRuntime { get; set; } = null!;
    [Inject] protected NavigationManager NavigationManager { get; set; } = null!;
    [Inject] protected SidePanelStateService SidePanelState { get; set; } = null!;
    [Inject] protected IMessageHub Hub { get; set; } = null!;
    [Inject] protected INavigationService NavigationService { get; set; } = null!;
    [Inject] protected IMenuItemsProvider MenuItemsProvider { get; set; } = null!;
    [Inject] protected IPathResolver PathResolver { get; set; } = null!;

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

    protected const string MessageBarSection = "MessagesTop";

    private bool isNavMenuOpen;
    protected bool IsNavMenuOpen => isNavMenuOpen;

    private bool isNodeMenuOpen;
    private bool isMeshMenuOpen;

    // Menu context names (must match NodeMenuItemsExtensions.NodeMenuContext / MeshMenuContext).
    private const string NodeMenuContext = "Node";
    private const string MeshMenuContext = "Mesh";

    // Menu items per context from IMenuItemsProvider (populated by LayoutAreaView from $Menu:{context} streams)
    private IReadOnlyList<NodeMenuItemDefinition> _nodeMenuItems = [];
    private IReadOnlyList<NodeMenuItemDefinition> _meshMenuItems = [];
    private IDisposable? _nodeMenuSubscription;
    private IDisposable? _meshMenuSubscription;


    // Editable content collections
    protected IReadOnlyList<ContentCollectionConfig> EditableCollections { get; private set; } = [];
    private IJSObjectReference? jsModule;
    private DotNetObjectReference<PortalLayoutBase>? dotNetRef;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        SidePanelState.OnStateChanged += OnSidePanelStateChanged;
        NavigationService.SidePanelNavigationRequested += OnSidePanelNavigation;
        NavigationService.OnNavigationContextChanged += OnNavigationContextChanged;
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
    }

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        await NavigationService.InitializeAsync();
        // Only resolve side panel content if already visible — defer until opened otherwise
        if (SidePanelState.IsVisible)
            await ResolveSidePanelContentAsync();
    }

    /// <summary>
    /// Display name of the currently-focused node — rendered as a header inside the Node and Mesh menus
    /// so the user can see what they're about to act on. Null when there's no node context (home page).
    /// </summary>
    private string? CurrentNodeName
    {
        get
        {
            var node = NavigationService.Context?.Node;
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

    /// <summary>
    /// Navigates to the Settings page — per-node Settings when on a node, Global Settings at the root.
    /// </summary>
    private void NavigateToSettings()
    {
        var ns = NavigationService.CurrentNamespace;
        var url = string.IsNullOrEmpty(ns)
            ? $"/{GlobalSettingsLayoutArea.GlobalSettingsArea}"
            : $"/{ns}/Settings";
        NavigationManager.NavigateTo(url);
    }

    /// <summary>
    /// Handles a click on a dynamic menu item.
    /// Uses Href for absolute navigation when set, otherwise constructs URL from Area.
    /// </summary>
    private void HandleMenuItemClick(NodeMenuItemDefinition item)
    {
        isNodeMenuOpen = false;
        isMeshMenuOpen = false;
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
                SidePanelState.State = saved;
                await ResolveSidePanelContentAsync();
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
            await ResolveSidePanelContentAsync();
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

    private void OnNavigationContextChanged(NavigationContext? _)
    {
        // Context changed (user navigated) — invalidate cached side panel control
        // so next render picks up the new context path
        InvokeAsync(StateHasChanged);
    }

    protected override void OnParametersSet()
    {
        if (ViewportInformation.IsDesktop && isNavMenuOpen)
        {
            isNavMenuOpen = false;
            CloseMobileNavMenu();
        }
    }

    [CascadingParameter]
    public required ViewportInformation ViewportInformation { get; set; }

    protected void ToggleNavMenu()
    {
        isNavMenuOpen = !isNavMenuOpen;
    }

    protected void CloseMobileNavMenu()
    {
        isNavMenuOpen = false;
        StateHasChanged();
    }


    public bool IsSidePanelVisible => SidePanelState.IsVisible;
    protected SidePanelPosition SidePanelPositionValue => SidePanelState.Position;

    public async Task ToggleSidePanel()
    {
        var context = NavigationService.Context;

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


    // Side panel content state
    // Key derived from the primary path so the ThreadChatView is rebuilt (re-running
    // OnInitialized, re-seeding the context attachment chip) when navigation moves
    // to a different node. Without this, the ThreadChatView stays stuck on the
    // InitialContext it was first rendered with.
    private string sidePanelContentKey => $"newchat-{NavigationService.Context?.PrimaryPath ?? string.Empty}";
    private ThreadChatControl? _cachedSidePanelControl;
    private string? _cachedContentPath;
    private string? _cachedContextPath;

    private LayoutAreaControl? sidePanelViewModel;
    private string? resolvedSidePanelPath;

    /// <summary>
    /// Resolves ContentPath via IPathResolver (same as AreaPage) and builds LayoutAreaControl.
    /// If the content path points to a node that no longer exists (e.g. deleted thread),
    /// the path resolves to a parent with satellite segments as remainder — detect and clear.
    /// </summary>
    private async Task ResolveSidePanelContentAsync()
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

        var resolution = await PathResolver.ResolvePathAsync(contentPath);
        if (resolution == null)
        {
            // Node doesn't exist at all — clear stale content path
            sidePanelViewModel = null;
            SidePanelState.SetContentPath(null);
            resolvedSidePanelPath = null;
            return;
        }

        // If the resolved prefix doesn't match the content path, it means the node
        // no longer exists and resolution fell back to a parent (e.g. _Thread/id became
        // remainder on the parent hub → invalid area). Clear the stale path.
        if (!string.Equals(resolution.Prefix, contentPath, StringComparison.OrdinalIgnoreCase)
            && !string.IsNullOrEmpty(resolution.Remainder))
        {
            sidePanelViewModel = null;
            SidePanelState.SetContentPath(null);
            resolvedSidePanelPath = null;
            return;
        }

        var (area, id) = ParseSidePanelRemainder(resolution.Remainder);
        var reference = new LayoutAreaReference(area) { Id = id ?? "" };
        sidePanelViewModel = Controls.LayoutArea((Address)resolution.Prefix, reference);
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
        var context = NavigationService.Context;
        var contextPath = context?.PrimaryPath;
        var contentPath = SidePanelState.ContentPath ?? string.Empty;

        // Return cached instance if inputs haven't changed
        if (_cachedSidePanelControl != null
            && _cachedContentPath == contentPath
            && _cachedContextPath == contextPath)
            return _cachedSidePanelControl;

        var contextDisplayName = context?.Node?.Name ?? context?.Node?.Id;
        _cachedContentPath = contentPath;
        _cachedContextPath = contextPath;
        _cachedSidePanelControl = new ThreadChatControl()
            .WithThreadPath(contentPath)
            .WithInitialContext(contextPath ?? string.Empty)
            .WithInitialContextDisplayName(contextDisplayName ?? string.Empty);
        return _cachedSidePanelControl;
    }

    public void Dispose()
    {
        SidePanelState.OnStateChanged -= OnSidePanelStateChanged;
        NavigationService.SidePanelNavigationRequested -= OnSidePanelNavigation;
        NavigationService.OnNavigationContextChanged -= OnNavigationContextChanged;
        _nodeMenuSubscription?.Dispose();
        _meshMenuSubscription?.Dispose();
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
