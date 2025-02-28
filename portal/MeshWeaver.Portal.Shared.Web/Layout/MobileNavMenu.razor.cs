// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System.Text.RegularExpressions;
using Microsoft.AspNetCore.Components;
using Microsoft.JSInterop;

namespace MeshWeaver.Portal.Shared.Web.Layout;

public partial class MobileNavMenu : ComponentBase
{
    [Inject]
    public required NavigationManager NavigationManager { get; init; }

    [Inject]
    public required IJSRuntime JS { get; init; }

    private Task NavigateToAsync(string url)
    {
        NavigationManager.NavigateTo(url);
        return Task.CompletedTask;
    }

    private IEnumerable<MobileNavMenuEntry> GetMobileNavMenuEntries()
    {
            yield return new MobileNavMenuEntry(
                "Articles",
                () => NavigateToAsync("/"),
                DesktopNavMenu.ArticlesIcon(),
                LinkMatchRegex: new Regex("^/$")
            );

            yield return new MobileNavMenuEntry(
                "Documentation Areas",
                () => NavigateToAsync(DesktopNavMenu.LayoutAreas("Documentation")),
                DesktopNavMenu.DocumentationLayoutAreaIcon(),
                LinkMatchRegex: GetNonIndexPageRegex(DesktopNavMenu.LayoutAreas("Documentation"))
            );
        yield return new MobileNavMenuEntry(
            "Northwind Areas",
            () => NavigateToAsync(""),
            DesktopNavMenu.NorthwindLayoutAreaIcon(),
            LinkMatchRegex: GetNonIndexPageRegex(DesktopNavMenu.LayoutAreas("Northwind"))
        );

    }

    private static Regex GetNonIndexPageRegex(string pageRelativeBasePath)
    {
        pageRelativeBasePath = Regex.Escape(pageRelativeBasePath);
        return new Regex($"^({pageRelativeBasePath}|{pageRelativeBasePath}/.+)$", RegexOptions.CultureInvariant | RegexOptions.IgnoreCase);
    }
}

