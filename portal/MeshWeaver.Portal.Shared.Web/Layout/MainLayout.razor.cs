using MeshWeaver.Portal.Shared.Web.Resize;
using Microsoft.AspNetCore.Components;

namespace MeshWeaver.Portal.Shared.Web.Layout;

public partial class MainLayout
{
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

}
