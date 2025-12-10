using MeshWeaver.AI;
using MeshWeaver.Blazor.Chat;
using MeshWeaver.Messaging;
using MeshWeaver.Portal.Shared.Web.Components;
using MeshWeaver.Portal.Shared.Web.Resize;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;
// ChatWindowStateService is now in MeshWeaver.Blazor.Chat namespace

namespace MeshWeaver.Portal.Shared.Web.Layout;

public partial class MainLayout : IDisposable
{
    [Inject] private IJSRuntime JSRuntime { get; set; } = null!;
    [Inject] private NavigationManager NavigationManager { get; set; } = null!;
    [Inject] private IMessageHub Hub { get; set; } = null!;
    [Inject] private ChatWindowStateService ChatState { get; set; } = null!;

    private const string MessageBarSection = "MessagesTop";
    private const string ChatAreaName = "AgentChat";

    private bool isNavMenuOpen;
    private readonly AgentChatControl chatControl = new();
    private IJSObjectReference? jsModule;
    private DotNetObjectReference<MainLayout>? dotNetRef;

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

    private void CloseMobileNavMenu()
    {
        isNavMenuOpen = false;
        StateHasChanged();
    }

    private IDialogReference? dialog;

    private async Task OpenSiteSettingsAsync()
    {
        dialog = await DialogService.ShowPanelAsync<SiteSettingsPanel>(new DialogParameters()
        {
            ShowTitle = true,
            Title = "Site settings",
            Alignment = Microsoft.FluentUI.AspNetCore.Components.HorizontalAlignment.Right,
            PrimaryAction = "OK",
            SecondaryAction = null,
            ShowDismiss = true
        });

        await dialog.Result;
    }

    public bool IsAIChatVisible => ChatState.IsVisible;
    private AgentChatView? chatComponent;
    private ChatPosition chatPosition => ChatState.Position;

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

    private async Task StartResize()
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

    private async Task HandleNewChatAsync()
    {
        if (chatComponent != null)
        {
            await chatComponent.ResetConversationAsync();
        }
    }

    private void HandleChatPositionChanged(ChatPosition newPosition)
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
