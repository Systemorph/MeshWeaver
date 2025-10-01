using MeshWeaver.Blazor.Chat;
using MeshWeaver.Portal.Shared.Web.Components;
using MeshWeaver.Portal.Shared.Web.Resize;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeshWeaver.Portal.Shared.Web.Layout;

public partial class MainLayout
{
    [Inject] private IJSRuntime JSRuntime { get; set; } = null!;

    private const string MessageBarSection = "MessagesTop";

    private bool isNavMenuOpen;
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
            Alignment = HorizontalAlignment.Right,
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


}
