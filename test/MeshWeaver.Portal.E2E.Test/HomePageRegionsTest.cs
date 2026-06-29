using System.Text.Json;
using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// The owner home (the User node's <c>Activity</c> area) is ONE configurable markdown page — the user
/// node's <c>Body</c>, defaulting to a welcome template. This pins what that default renders end-to-end:
/// <list type="bullet">
///   <item>the <b>welcome banner</b> with the small "it's configurable" note linking to the guide;</item>
///   <item>the <b>catalog tabs</b> (embedded via <c>@@("area/Catalog")</c>) filling the width — the
///     <c>TabsControl</c> 100%-width fix.</item>
/// </list>
/// </summary>
[Collection("portal-e2e")]
public class HomePageRegionsTest(PortalFixture fixture)
{
    [Fact(Timeout = 180_000)]
    public async Task OwnerHome_RendersWelcome_ConfigLink_AndFullWidthCatalog()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1600, 1000);

        await page.GotoAsync($"{fixture.BaseUrl}/User/{fixture.UserId}",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // (a) The configurable welcome banner renders — this is the default page when Body is unset.
        await page.GetByText("Welcome back", new() { Exact = false }).First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 90_000 });

        // The small "it's configurable" note links to the config guide page.
        (await page.Locator("a[href*='ConfigurablePages']").CountAsync()).Should().BeGreaterThan(0,
            "the welcome note must link to Doc/GUI/ConfigurablePages");

        // (b) The catalog — a fluent-tabs embedded via @@("area/Catalog") — must fill the home width.
        var tabs = page.Locator("fluent-tabs").First;
        await tabs.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/home-regions.png", FullPage = true });

        var measure = await page.EvaluateAsync<string>("""
            () => {
              const w = el => el ? Math.round(el.getBoundingClientRect().width) : -1;
              const tabs = document.querySelector('fluent-tabs');
              const pageEl = document.querySelector('.body-content, main, body');
              return JSON.stringify({ tabs: w(tabs), page: w(pageEl) });
            }
            """);
        System.IO.File.WriteAllText("/tmp/home-catalog-widths.txt", measure);

        var doc = JsonDocument.Parse(measure).RootElement;
        float tabsW = doc.GetProperty("tabs").GetSingle();
        float pageW = doc.GetProperty("page").GetSingle();
        (tabsW / pageW).Should().BeGreaterThan(0.8f,
            $"the catalog tabs should fill the home width, not shrink to content (tabs={tabsW:F0}px of {pageW:F0}px)");
    }
}
