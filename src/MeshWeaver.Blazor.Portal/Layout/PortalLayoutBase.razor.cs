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
    [Inject] protected INavigationService NavigationService { get; set; } = null!;

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
    private CancellationTokenSource? loadingCts;

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
        // Start loading creatable types without blocking - will await when menu opens
        StartLoadingCreatableTypes();
    }

    protected override async Task OnParametersSetAsync()
    {
        await base.OnParametersSetAsync();

        // Reload types when URL changes
        var currentPath = NavigationService.CurrentNamespace;
        if (currentPath != lastLoadedPath)
        {
            StartLoadingCreatableTypes();
        }
    }

    /// <summary>
    /// Starts loading creatable types without blocking. Items are added as they arrive.
    /// </summary>
    protected virtual void StartLoadingCreatableTypes()
    {
        var nodeTypeService = Hub?.ServiceProvider.GetService<INodeTypeService>();
        if (nodeTypeService == null)
            return;

        var currentPath = NavigationService.CurrentNamespace;

        // Cancel any previous loading
        loadingCts?.Cancel();
        loadingCts = new CancellationTokenSource();

        lastLoadedPath = currentPath;
        creatableTypes = new(); // Clear existing items

        // Fire and forget - load items incrementally
        _ = LoadCreatableTypesIncrementallyAsync(nodeTypeService, currentPath ?? string.Empty, loadingCts.Token);
    }

    /// <summary>
    /// Loads creatable types incrementally, updating the UI as each item arrives.
    /// </summary>
    private async Task LoadCreatableTypesIncrementallyAsync(
        INodeTypeService nodeTypeService,
        string currentPath,
        CancellationToken ct)
    {
        try
        {
            await foreach (var typeInfo in nodeTypeService.GetCreatableTypesAsync(currentPath, ct).WithCancellation(ct))
            {
                creatableTypes.Add(typeInfo);
                await InvokeAsync(StateHasChanged);
            }
        }
        catch (OperationCanceledException)
        {
            // Path changed, loading was cancelled - this is expected
        }
        catch
        {
            // Fallback on error - keep whatever items we loaded
        }
    }

    /// <summary>
    /// Creates a transient node and navigates to the Create layout area for editing.
    /// </summary>
    /// <summary>
    /// Navigates to the Create page for a specific node type.
    /// </summary>
    protected virtual Task NavigateToCreateAsync(string nodeTypePath)
    {
        isCreateMenuOpen = false;

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

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        // Reload creatable types when URL changes to a different node path
        var currentPath = NavigationService.CurrentNamespace;
        if (currentPath != lastLoadedPath)
        {
            _ = InvokeAsync(() =>
            {
                StartLoadingCreatableTypes();
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
        loadingCts?.Cancel();
        loadingCts?.Dispose();
        NavigationManager.LocationChanged -= OnLocationChanged;
        ChatState.OnStateChanged -= OnChatStateChanged;
        dotNetRef?.Dispose();
        jsModule?.DisposeAsync();
    }
}
