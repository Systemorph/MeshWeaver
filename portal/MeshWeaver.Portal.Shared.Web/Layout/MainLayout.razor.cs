using MeshWeaver.AI;
using MeshWeaver.Blazor.Chat;
using MeshWeaver.Data;
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
    private AgentChatControl chatControl = new();

    protected override void OnInitialized()
    {
        base.OnInitialized();
        NavigationManager.LocationChanged += OnLocationChanged;
        UpdateChatContext();
    }

    private void OnLocationChanged(object? sender, LocationChangedEventArgs e)
    {
        UpdateChatContext();
        StateHasChanged();
    }

    private void UpdateChatContext()
    {
        var context = GetContextFromUrl();
        if (context != null)
        {
            chatControl = chatControl.WithContext(context);
        }
    }

    private AgentContext? GetContextFromUrl()
    {
        var path = NavigationManager.ToBaseRelativePath(NavigationManager.Uri);

        // Skip if path is empty or just "chat"
        if (string.IsNullOrEmpty(path) || path == "chat")
            return null;

        // Split the path into segments
        var segments = path.Split('/', StringSplitOptions.RemoveEmptyEntries);

        // Need at least addressType and addressId
        if (segments.Length < 2)
            return null;

        var addressType = segments[0];
        var addressId = segments[1];

        // Create the Address with the extracted values
        var address = new Address(addressType, addressId);

        var layoutArea = segments.Length == 2 ? null : new LayoutAreaReference(segments[2])
        {
            Id = string.Join('/', segments.Skip(3))
        };

        // Create a new AgentContext with the extracted values
        return new AgentContext
        {
            Address = address,
            LayoutArea = layoutArea
        };
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
    private ChatPosition currentChatPosition = ChatPosition.Right;

    public async Task ToggleAIChatVisibility()
    {
        IsAIChatVisible = !IsAIChatVisible;
        StateHasChanged();

        // Small delay to ensure proper rendering, especially for bottom position
        if (IsAIChatVisible && currentChatPosition == ChatPosition.Bottom)
        {
            await Task.Delay(50);
            StateHasChanged();
        }
    }
    private async Task StartResize()
    {
        // Call the JavaScript function to handle the resize operation
        await JSRuntime.InvokeVoidAsync("chatResizer.startResize");
    }

    private async Task HandleNewChatAsync()
    {
        if (chatComponent != null)
        {
            await chatComponent.ResetConversationAsync();
        }
    }

    private async Task OnChatPositionChanged(ChatPosition newPosition)
    {
        var previousPosition = currentChatPosition;
        currentChatPosition = newPosition;

        // Reset CSS variables when switching position types
        if (previousPosition != newPosition)
        {
            await JSRuntime.InvokeVoidAsync("eval",
                $"document.querySelector('.layout')?.style.removeProperty('--chat-width'); document.querySelector('.layout')?.style.removeProperty('--chat-height');");
        }

        StateHasChanged();

        // Small delay to allow DOM to update before applying new styles
        await Task.Delay(10);
        StateHasChanged();
    }

    public void Dispose()
    {
        NavigationManager.LocationChanged -= OnLocationChanged;
    }
}
