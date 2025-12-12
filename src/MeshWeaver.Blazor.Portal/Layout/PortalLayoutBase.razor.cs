using MeshWeaver.AI;
using MeshWeaver.Blazor.Chat;
using MeshWeaver.Blazor.Portal.Components;
using MeshWeaver.Blazor.Portal.Resize;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeshWeaver.Blazor.Portal.Layout;

public partial class PortalLayoutBase : LayoutComponentBase, IDisposable
{
    [Inject] protected IJSRuntime JSRuntime { get; set; } = null!;
    [Inject] protected NavigationManager NavigationManager { get; set; } = null!;
    [Inject] protected ChatWindowStateService ChatState { get; set; } = null!;

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

    private readonly AgentChatControl chatControl = new();
    private IJSObjectReference? jsModule;
    private DotNetObjectReference<PortalLayoutBase>? dotNetRef;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        NavigationManager.LocationChanged += OnLocationChanged;
        ChatState.OnStateChanged += OnChatStateChanged;
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
        StateHasChanged();
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
            "import", "./_content/MeshWeaver.Blazor.Chat/AgentChatView.razor.js");
    }

    protected async Task StartResize()
    {
        await EnsureJsModuleAsync();
        await jsModule!.InvokeVoidAsync("startResize");
    }

    /// <summary>
    /// Called from JavaScript when resize ends to persist the new size.
    /// </summary>
    [JSInvokable]
    public void OnResizeEnd(int? width, int? height)
    {
        ChatState.SetSize(width, height);
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
