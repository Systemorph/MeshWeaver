using MeshWeaver.AI;
using MeshWeaver.Blazor.Chat;
using MeshWeaver.Messaging;
using MeshWeaver.Portal.Shared.Web.Components;
using MeshWeaver.Portal.Shared.Web.Resize;
using Microsoft.AspNetCore.Components;
using Microsoft.AspNetCore.Components.Routing;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeshWeaver.Portal.Shared.Web.Layout;

public partial class MainLayout : IDisposable
{
    [Inject] private IJSRuntime JSRuntime { get; set; } = null!;
    [Inject] private NavigationManager NavigationManager { get; set; } = null!;
    [Inject] private IMessageHub Hub { get; set; } = null!;

    private const string MessageBarSection = "MessagesTop";
    private const string ChatAreaName = "AgentChat";

    private bool isNavMenuOpen;
    private readonly AgentChatControl chatControl = new();
    private IJSObjectReference? jsModule;

    protected override void OnInitialized()
    {
        base.OnInitialized();
        NavigationManager.LocationChanged += OnLocationChanged;
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
    public bool IsAIChatVisible { get; private set; }
    private AgentChatView? chatComponent;
    private ChatPosition chatPosition = ChatPosition.Right;

    public void ToggleAIChatVisibility()
    {
        IsAIChatVisible = !IsAIChatVisible;
        StateHasChanged();
    }

    private async Task StartResize()
    {
        // Lazily load the JavaScript module
        jsModule ??= await JSRuntime.InvokeAsync<IJSObjectReference>(
            "import", "./_content/MeshWeaver.Blazor.Chat/AgentChatView.razor.js");

        // Call the JavaScript function to handle the resize operation
        await jsModule.InvokeVoidAsync("startResize");
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
        chatPosition = newPosition;
        StateHasChanged();
    }

    public void Dispose()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;
        jsModule?.DisposeAsync();
    }
}
