using Memex.Portal.Shared;
using Xunit;

namespace Memex.Portal.Shared.Test;

/// <summary>
/// Pins the per-browser GUI-shell switch decision (<see cref="GuiShellSwitch.Decide"/>) — the pure
/// half of the both-shells middleware. The contract: only NAVIGATIONS switch (GET + text/html +
/// a page path), <c>?gui=</c> records the choice, the cookie replays it, and the deployment's
/// <c>Features:Gui:Default</c> decides where a fresh browser lands.
/// </summary>
public class GuiShellSwitchTest
{
    private const string Html = "text/html,application/xhtml+xml";

    [Fact]
    public void FreshBrowser_DefaultBlazor_PassesThrough()
    {
        var (redirect, setCookie) = GuiShellSwitch.Decide("GET", "/Doc/Architecture", Html, null, null, "Blazor");
        Assert.Null(redirect);
        Assert.Null(setCookie);
    }

    [Fact]
    public void FreshBrowser_DefaultNext_LandsOnNext_SamePath()
    {
        var (redirect, _) = GuiShellSwitch.Decide("GET", "/Doc/Architecture", Html, null, null, "Next");
        Assert.Equal("/next/Doc/Architecture", redirect);
    }

    [Fact]
    public void QueryGuiNext_SetsCookie_AndRedirects()
    {
        var (redirect, setCookie) = GuiShellSwitch.Decide("GET", "/", Html, "next", null, "Blazor");
        Assert.Equal("next", setCookie);
        Assert.Equal("/next", redirect);
    }

    [Fact]
    public void QueryGuiBlazor_OverridesNextCookie_AndStays()
    {
        var (redirect, setCookie) = GuiShellSwitch.Decide("GET", "/rbuergi", Html, "blazor", "next", "Blazor");
        Assert.Equal("blazor", setCookie);
        Assert.Null(redirect);
    }

    [Fact]
    public void NextCookie_RedirectsEveryNavigation()
    {
        var (redirect, _) = GuiShellSwitch.Decide("GET", "/Store", Html, null, "next", "Blazor");
        Assert.Equal("/next/Store", redirect);
    }

    [Theory]
    [InlineData("/api/mesh/whoami")]
    [InlineData("/meshweaver.v1.Mesh/Connect")]
    [InlineData("/_blazor/negotiate")]
    [InlineData("/static/NodeTypeIcons/book.svg")]
    [InlineData("/login")]
    [InlineData("/next/Doc")]
    [InlineData("/mcp")]
    public void NonPageSurfaces_NeverRedirect(string path)
    {
        var (redirect, _) = GuiShellSwitch.Decide("GET", path, Html, null, "next", "Blazor");
        Assert.Null(redirect);
    }

    [Fact]
    public void NonHtmlAccept_NeverRedirects_TheMeshSurfacesFlowUntouched()
    {
        var (redirect, _) = GuiShellSwitch.Decide("GET", "/Doc", "application/json", null, "next", "Blazor");
        Assert.Null(redirect);
    }

    [Fact]
    public void Post_NeverRedirects_ButStillRecordsAnExplicitChoice()
    {
        var (redirect, setCookie) = GuiShellSwitch.Decide("POST", "/Doc", Html, "next", null, "Blazor");
        Assert.Null(redirect);
        Assert.Equal("next", setCookie);
    }
}
