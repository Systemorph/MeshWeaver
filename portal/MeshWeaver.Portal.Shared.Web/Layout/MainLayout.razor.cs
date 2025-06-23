using MeshWeaver.Blazor.Chat;
using MeshWeaver.Portal.Shared.Web.Components;
using MeshWeaver.Portal.Shared.Web.Resize;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeshWeaver.Portal.Shared.Web.Layout;

public partial class MainLayout
{
    [Inject]
    private IJSRuntime JSRuntime { get; set; }

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
    private IDialogReference dialog;

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
    private AgentChatView chatComponent;

    public void ToggleAIChatVisibility()
    {
        IsAIChatVisible = !IsAIChatVisible;
        StateHasChanged();
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

}
