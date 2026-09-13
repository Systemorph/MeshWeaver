// <meshweaver>
// Id: Testing/MemexPortalShared/GuiShellSwitchTest
// DisplayName: Testing/MemexPortalShared/GuiShellSwitchTest — migrated from xunit (convert-xunit-to-inmesh.py)
// </meshweaver>
#nullable enable
using MeshWeaver.Reactive.Assertions;
using MeshWeaver.Testing.InMesh;
using System;
using Memex.Portal.Shared;

/// <summary>
/// Pins the per-browser GUI-shell switch decision (<see cref="GuiShellSwitch.Decide"/>) — the pure
/// half of the both-shells middleware. The contract: only NAVIGATIONS switch (GET + text/html +
/// a page path), <c>?gui=</c> records the choice, the cookie replays it, and the deployment's
/// <c>Features:Gui:Default</c> decides where a fresh browser lands.
/// </summary>
public class GuiShellSwitchTest
{
    private const string Html = "text/html,application/xhtml+xml";

    [MeshFact]
    public void FreshBrowser_DefaultBlazor_PassesThrough()
    {
        var (redirect, setCookie) = GuiShellSwitch.Decide("GET", "/Doc/Architecture", Html, null, null, "Blazor");
        Assert.Null(redirect);
        Assert.Null(setCookie);
    }

    [MeshFact]
    public void FreshBrowser_DefaultNext_LandsOnNext_SamePath()
    {
        var (redirect, _) = GuiShellSwitch.Decide("GET", "/Doc/Architecture", Html, null, null, "Next");
        Assert.Equal("/next/Doc/Architecture", redirect);
    }

    [MeshFact]
    public void QueryGuiNext_SetsCookie_AndRedirects()
    {
        var (redirect, setCookie) = GuiShellSwitch.Decide("GET", "/", Html, "next", null, "Blazor");
        Assert.Equal("next", setCookie);
        Assert.Equal("/next", redirect);
    }

    [MeshFact]
    public void QueryGuiBlazor_OverridesNextCookie_AndStays()
    {
        var (redirect, setCookie) = GuiShellSwitch.Decide("GET", "/rbuergi", Html, "blazor", "next", "Blazor");
        Assert.Equal("blazor", setCookie);
        Assert.Null(redirect);
    }

    [MeshFact]
    public void NextCookie_RedirectsEveryNavigation()
    {
        var (redirect, _) = GuiShellSwitch.Decide("GET", "/Store", Html, null, "next", "Blazor");
        Assert.Equal("/next/Store", redirect);
    }

    [MeshTheory]
    [MeshInlineData("/api/mesh/whoami")]
    [MeshInlineData("/meshweaver.v1.Mesh/Connect")]
    [MeshInlineData("/_blazor/negotiate")]
    [MeshInlineData("/static/NodeTypeIcons/book.svg")]
    [MeshInlineData("/login")]
    [MeshInlineData("/next/Doc")]
    [MeshInlineData("/mcp")]
    [MeshInlineData("/api/mcp")]
    public void NonPageSurfaces_NeverRedirect(string path)
    {
        var (redirect, _) = GuiShellSwitch.Decide("GET", path, Html, null, "next", "Blazor");
        Assert.Null(redirect);
    }

    [MeshFact]
    public void NonHtmlAccept_NeverRedirects_TheMeshSurfacesFlowUntouched()
    {
        var (redirect, _) = GuiShellSwitch.Decide("GET", "/Doc", "application/json", null, "next", "Blazor");
        Assert.Null(redirect);
    }

    [MeshFact]
    public void Post_NeverRedirects_ButStillRecordsAnExplicitChoice()
    {
        var (redirect, setCookie) = GuiShellSwitch.Decide("POST", "/Doc", Html, "next", null, "Blazor");
        Assert.Null(redirect);
        Assert.Equal("next", setCookie);
    }
}
