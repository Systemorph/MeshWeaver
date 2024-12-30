using Microsoft.FluentUI.AspNetCore.Components;

namespace MeshWeaver.Portal.Shared.Components;

public partial class SiteSettings
{
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

        var result = await dialog.Result;
    }
}
