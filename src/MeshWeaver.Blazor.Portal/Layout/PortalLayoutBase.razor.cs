using MeshWeaver.AI;
using MeshWeaver.Blazor.Chat;
using MeshWeaver.Blazor.Portal.Components;
using MeshWeaver.Blazor.Portal.Resize;
using MeshWeaver.Mesh;
using MeshWeaver.Mesh.Services;
using MeshWeaver.Messaging;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.Portal.Layout;

public partial class PortalLayoutBase : LayoutComponentBase, IDisposable
{
    [Inject] protected IJSRuntime JSRuntime { get; set; } = null!;
    [Inject] protected NavigationManager NavigationManager { get; set; } = null!;
    [Inject] protected ChatWindowStateService ChatState { get; set; } = null!;
    [Inject] protected IMessageHub Hub { get; set; } = null!;

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

    // Create menu state
    protected bool isCreateMenuOpen;
    protected List<CreatableTypeInfo> creatableTypes = new();
    private string? lastLoadedPath;

    private readonly AgentChatControl chatControl = new();
    private IJSObjectReference? jsModule;
    private DotNetObjectReference<PortalLayoutBase>? dotNetRef;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        NavigationManager.LocationChanged += OnLocationChanged;
        ChatState.OnStateChanged += OnChatStateChanged;
    }

    protected override async Task OnInitializedAsync()
    {
        await base.OnInitializedAsync();
        await LoadCreatableTypesAsync();
    }

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();

        // Reload types when URL changes
        var currentPath = GetCurrentNodePath();
        if (currentPath != lastLoadedPath)
        {
            await LoadCreatableTypesAsync();
        }
    }

    /// <summary>
    /// Loads the creatable types for the current node path.
    /// </summary>
    protected virtual async Task LoadCreatableTypesAsync()
    {
        var nodeTypeService = Hub?.ServiceProvider.GetService<INodeTypeService>();
        if (nodeTypeService == null)
            return;

        var currentPath = GetCurrentNodePath();
        lastLoadedPath = currentPath;

        try
        {
            creatableTypes = await nodeTypeService
                .GetCreatableTypesAsync(currentPath)
                .ToListAsync();
            StateHasChanged();
        }
        catch
        {
            // Fallback to empty list on error
            creatableTypes = new();
        }
    }

    /// <summary>
    /// Extracts the current node path from the URL.
    /// URLs are in format: /{nodePath}/{area} or /{nodePath}
    /// Returns empty string for root level.
    /// </summary>
    protected virtual string GetCurrentNodePath()
    {
        var uri = new Uri(NavigationManager.Uri);
        var path = uri.AbsolutePath.TrimStart('/');

        if (string.IsNullOrEmpty(path))
            return "";

        // Known view areas that should be stripped from the path
        // Note: "Overview" and "Search" are the renamed versions of "Details" and "Catalog"
        // Include common custom view names and framework areas to handle all URL patterns
        var viewAreas = new[] {
            // Framework areas
            "Details", "Overview", "Edit", "Create", "Thumbnail", "Catalog", "Search",
            "LayoutAreas", "Read", "Metadata", "Notebook", "Comments", "Attachments", "Settings",
            "Files", "Children", "NodeTypes", "AccessControl", "Code", "CodeEdit", "HubConfig", "HubConfigEdit",
            // Common custom view names (used in Project, Todo, etc.)
            "Summary", "AllTasks", "TodosByCategory", "Planning", "MyTasks", "Backlog", "TodaysFocus"
        };

        var segments = path.Split('/');

        // If last segment is a known view area, remove it
        if (segments.Length > 1 && viewAreas.Contains(segments[^1], StringComparer.OrdinalIgnoreCase))
        {
            return string.Join("/", segments[..^1]);
        }

        // Return full path as node path
        return path;
    }

    /// <summary>
    /// Navigates to the create page for the specified node type.
    /// </summary>
    protected virtual void NavigateToCreate(string nodeTypePath)
    {
        isCreateMenuOpen = false;

        var currentPath = GetCurrentNodePath();
        if (string.IsNullOrEmpty(currentPath))
        {
            // At root level - navigate to create with type parameter
            NavigationManager.NavigateTo($"/create?type={Uri.EscapeDataString(nodeTypePath)}");
        }
        else
        {
            // Inside a node - navigate to create page with parent and type parameters
            NavigationManager.NavigateTo($"/create?parent={Uri.EscapeDataString(currentPath)}&type={Uri.EscapeDataString(nodeTypePath)}");
        }
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

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        // Reload creatable types when URL changes to a different node path
        var currentPath = GetCurrentNodePath();
        if (currentPath != lastLoadedPath)
        {
            _ = InvokeAsync(async () =>
            {
                await LoadCreatableTypesAsync();
            });
        }
        else
        {
            StateHasChanged();
        }
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

    private IDialogReference? dialog;

    protected async Task OpenSiteSettingsAsync()
    {
        dialog = await DialogService.ShowPanelAsync<SiteSettingsPanel>(new DialogParameters()
        {
            ShowTitle = true,
            Title = "Site settings",
            Alignment = HorizontalAlignment.Right,
            PrimaryAction = "OK",
            SecondaryAction = null,
            ShowDismiss = true
        });

        await dialog.Result;
    }

    public bool IsAIChatVisible => ChatState.IsVisible;
    private AgentChatView? chatComponent;
    protected ChatPosition ChatPositionValue => ChatState.Position;

    public async Task ToggleAIChatVisibility()
    {
        ChatState.Toggle();

        if (ChatState.IsVisible)
        {
            // Apply persisted size when opening
            await ApplyPersistedSizeAsync();
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


    protected async Task HandleNewChatAsync()
    {
        if (chatComponent != null)
        {
            await chatComponent.ResetConversationAsync();
        }
    }

    protected void HandleChatPositionChanged(ChatPosition newPosition)
    {
        ChatState.SetPosition(newPosition);
    }

    public void Dispose()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;
        ChatState.OnStateChanged -= OnChatStateChanged;
        dotNetRef?.Dispose();
        jsModule?.DisposeAsync();
    }
}
