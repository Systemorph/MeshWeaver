// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.FluentUI.AspNetCore.Components.Icons.Filled;
using Microsoft.JSInterop;

namespace MeshWeaver.Portal.Shared.Web.Layout;

public partial class MobileNavMenu : ComponentBase
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
            "Blog",
            () => NavigateToAsync("/articles"),
            DesktopNavMenu.BlogIcon(),
            LinkMatchRegex: new Regex("^/articles")
        );


        yield return new MobileNavMenuEntry(
            "Todo Areas",
            () => NavigateToAsync("/app/Todo/LayoutAreas"),
            DesktopNavMenu.TodoIcon(),
            LinkMatchRegex: new Regex("^/app/Todo")
        );

        yield return new MobileNavMenuEntry(
            "Northwind Areas",
            () => NavigateToAsync("/app/Northwind/LayoutAreas"),
            DesktopNavMenu.NorthwindIcon(),
            LinkMatchRegex: new Regex("^/app/Northwind")
        );
        yield return new MobileNavMenuEntry(
            "Northwind Articles",
            () => NavigateToAsync("/app/Northwind/Articles"),
            DesktopNavMenu.NorthwindArticleIcon(),
            LinkMatchRegex: new Regex("^/app/Northwind/Articles")
        );
        yield return new MobileNavMenuEntry(
            "Pricing",
            () => NavigateToAsync("/app/Insurance/Pricings"),
            DesktopNavMenu.PricingIcon(),
            LinkMatchRegex: new Regex("^/app/Insurance/Pricings")
        );
        yield return new MobileNavMenuEntry("Documentation Areas",
            () => NavigateToAsync("/app/Documentation/LayoutAreas"),
            DesktopNavMenu.DocumentationIcon(),
            LinkMatchRegex: new Regex("^/app/Documentation"));

        yield return new MobileNavMenuEntry(
            "Agents",
            () => NavigateToAsync("/app/Agents/Overview"),
            DesktopNavMenu.AgentsIcon(),
            LinkMatchRegex: new Regex("^/app/Agents")
        );



        yield return new MobileNavMenuEntry(
            "Chat",
            () => NavigateToAsync("/chat"),
            DesktopNavMenu.ChatIcon(),
            LinkMatchRegex: new Regex("^/chat")
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

    private static Regex GetNonIndexPageRegex(string pageRelativeBasePath)
    {
        pageRelativeBasePath = Regex.Escape(pageRelativeBasePath);
        return new Regex($"^({pageRelativeBasePath}|{pageRelativeBasePath}/.+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }

    private string LoginUrl()
    {
        // For Blazor Server, we directly use the ASP.NET Core Identity endpoints
        var loginPath = "/MicrosoftIdentity/Account/SignIn";

        // Add a redirect URL to return to the current page
        var returnUrl = NavigationManager.Uri;
        return $"{loginPath}?returnUrl={Uri.EscapeDataString(returnUrl)}";
    }

    private void Logout()
    {
        var logoutPath = "/MicrosoftIdentity/Account/SignOut";
        NavigationManager.NavigateTo(logoutPath, forceLoad: true);
    }

}

