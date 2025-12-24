using System.Text.RegularExpressions;
using MeshWeaver.Blazor.Portal.Layout;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components.Icons.Filled;
using Microsoft.JSInterop;

namespace Loom.Portal.Shared.Layout;

public partial class LoomMobileNavMenu : ComponentBase
{
    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    private Task NavigateToAsync(string url, bool forceLoad = false)
    {
        NavigationManager.NavigateTo(url, forceLoad);
        return Task.CompletedTask;
    }

    private IEnumerable<MobileNavMenuEntry> GetMobileNavMenuEntries()
    {
        yield return new MobileNavMenuEntry(
            "Home",
            () => NavigateToAsync("/"),
            LoomDesktopNavMenu.HomeIcon(),
            LinkMatchRegex: new Regex("^/$")
        );

        yield return new MobileNavMenuEntry(
            "Root",
            () => NavigateToAsync("/graph"),
            LoomDesktopNavMenu.RootIcon(),
            LinkMatchRegex: new Regex("^/graph")
        );

        yield return new MobileNavMenuEntry(
            "Organizations",
            () => NavigateToAsync("/Organizations"),
            LoomDesktopNavMenu.OrganizationIcon(),
            LinkMatchRegex: new Regex("^/Organizations")
        );

        yield return new MobileNavMenuEntry(
            "People",
            () => NavigateToAsync("/People"),
            LoomDesktopNavMenu.PersonIcon(),
            LinkMatchRegex: new Regex("^/People")
        );

        yield return new MobileNavMenuEntry(
            "Settings",
            LaunchSettingsAsync,
            new Microsoft.FluentUI.AspNetCore.Components.Icons.Regular.Size24.Settings()
        );

        yield return new MobileNavMenuEntry(
            "Sign in",
            () => NavigateToAsync(LoginUrl(), true),
            new Size24.PersonAccounts()
        );
    }

    private string LoginUrl()
    {
        var loginPath = "/MicrosoftIdentity/Account/SignIn";
        var returnUrl = NavigationManager.Uri;
        return $"{loginPath}?returnUrl={Uri.EscapeDataString(returnUrl)}";
    }
}
