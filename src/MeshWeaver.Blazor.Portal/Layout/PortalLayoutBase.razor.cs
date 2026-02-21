using MeshWeaver.AI;
using MeshWeaver.Blazor.Chat;
using MeshWeaver.Blazor.Portal.Resize;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.Portal.Layout;

public partial class PortalLayoutBase : LayoutComponentBase, IDisposable
{
    [Inject] protected IJSRuntime JSRuntime { get; set; } = null!;
    [Inject] protected NavigationManager NavigationManager { get; set; } = null!;
    [Inject] protected ChatWindowStateService ChatState { get; set; } = null!;
    [Inject] protected IMessageHub Hub { get; set; } = null!;
    [Inject] protected INavigationService NavigationService { get; set; } = null!;
    [Inject] protected IMenuItemsProvider MenuItemsProvider { get; set; } = null!;

    // Splitter pane sizes - default 3:1 ratio (75% main, 25% chat)
    private string MainPaneSize => ChatState.Width.HasValue ? $"{100 - ChatState.Width.Value}%" : "75%";
    private string MainPaneSizeWithChat => IsAIChatVisible ? MainPaneSize : "100%";
    private string ChatPaneSize => ChatState.Width.HasValue ? $"{ChatState.Width.Value}%" : "25%";
    private string ChatPaneSizeWithVisibility => IsAIChatVisible ? ChatPaneSize : "0%";

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

    // Menu items from IMenuItemsProvider (populated by LayoutAreaView from $Menu stream)
    private IReadOnlyList<NodeMenuItemDefinition> _menuItems = [];
    private IDisposable? _menuSubscription;

    private ChatSidePanel? chatPanel;
    private IJSObjectReference? jsModule;
    private DotNetObjectReference<PortalLayoutBase>? dotNetRef;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        ChatState.OnStateChanged += OnChatStateChanged;
        _menuSubscription = MenuItemsProvider.MenuItems.Subscribe(items =>
        {
            _menuItems = items;
            InvokeAsync(StateHasChanged);
        });
    }

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        await NavigationService.InitializeAsync();
    }

    private void ToggleNodeMenu()
    {
        isNodeMenuOpen = !isNodeMenuOpen;
    }

    private void OnNodeMenuOpenChanged(bool open)
    {
        isNodeMenuOpen = open;
    }

    /// <summary>
    /// Handles a click on a dynamic menu item.
    /// Uses Href for absolute navigation when set, otherwise constructs URL from Area.
    /// </summary>
    private void HandleMenuItemClick(NodeMenuItemDefinition item)
    {
        isNodeMenuOpen = false;
        if (!string.IsNullOrEmpty(item.Href))
            NavigationManager.NavigateTo(item.Href);
        else
            NavigateToArea(item.Area);
    }

    /// <summary>
    /// Navigates to the specified area for the current node (e.g., "Edit", "Suggest").
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
    /// Returns menu items from the layout stream.
    /// Permission filtering is done server-side by the providers.
    /// </summary>
    private IReadOnlyList<NodeMenuItemDefinition> GetVisibleMenuItems()
    {
        return _menuItems;
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
            await EnsureJsModuleAsync();
            dotNetRef = DotNetObjectReference.Create(this);
            await jsModule!.InvokeVoidAsync("initialize", dotNetRef);

            // Apply persisted size if available
            if (ChatState.IsVisible && (ChatState.Width.HasValue || ChatState.Height.HasValue))
            {
                await ApplyPersistedSizeAsync();
            }
        }
    }

    private void OnChatStateChanged()
    {
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


    public bool IsAIChatVisible => ChatState.IsVisible;
    protected ChatPosition ChatPositionValue => ChatState.Position;

    public async Task ToggleAIChatVisibility()
    {
        var context = NavigationService.Context;

        // Check if viewing a thread full-screen by checking the node's NodeType
        if (context?.Node != null && ThreadNodeType.IsThreadNodeType(context.Node.NodeType))
        {
            var threadPath = context.Namespace;
            var threadContent = context.Node.Content as MeshWeaver.AI.Thread;
            var parentPath = threadContent?.ParentPath;

            // Navigate to parent (or home if no parent path)
            var navigateTo = string.IsNullOrEmpty(parentPath) ? "/" : $"/{parentPath}";
            NavigationManager.NavigateTo(navigateTo);

            // Open panel with thread
            ChatState.OpenSidePanelWithThread(threadPath);
            await ApplyPersistedSizeAsync();
        }
        else
        {
            // Normal toggle
            ChatState.Toggle();

            if (ChatState.IsVisible)
            {
                // Apply persisted size when opening
                await ApplyPersistedSizeAsync();
            }
        }
    }

    private async Task ApplyPersistedSizeAsync()
    {
        await EnsureJsModuleAsync();
        await jsModule!.InvokeVoidAsync("applyChatSize", ChatState.Width, ChatState.Height);
    }

    private async Task EnsureJsModuleAsync()
    {
        jsModule ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./_content/MeshWeaver.Blazor.Portal/Layout/PortalLayoutBase.razor.js");
    }


    protected void HandleChatPositionChanged(ChatPosition newPosition)
    {
        ChatState.SetPosition(newPosition);
    }

    public void Dispose()
    {
        ChatState.OnStateChanged -= OnChatStateChanged;
        _menuSubscription?.Dispose();
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
