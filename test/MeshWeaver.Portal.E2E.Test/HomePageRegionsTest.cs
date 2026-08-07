using System.Text.Json;
using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// The owner home (the User node's <c>Activity</c> area) is ONE configurable markdown page — the user
/// node's <c>Body</c>, defaulting to a welcome template. This pins what that default renders end-to-end:
/// <list type="bullet">
///   <item>the <b>welcome banner</b> with the small "it's configurable" note linking to the guide;</item>
///   <item>the <b>catalog</b> (a MeshSearch embedded via <c>@@("area/Catalog")</c>) filling the width — the
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

        // (b) The catalog — embedded via @@("area/Catalog") — must fill the home width.
        //
        // 🚨 The catalog is a MeshSearch, NOT tabs. UserActivityLayoutAreas.BuildCatalog returns
        // Controls.MeshSearch (a second one is added only for the "Shared with me" band), which
        // renders <div class="mesh-search-container"> — it emits no <fluent-tabs> on any path. The
        // previous selector was left over from when the catalog WAS a tab set, so this assertion
        // could not pass under any build; it timed out for 60s and reported a rendering failure
        // that did not exist.
        var catalog = page.Locator(".mesh-search-container").First;
        await catalog.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/home-regions.png", FullPage = true });

        var measure = await page.EvaluateAsync<string>("""
            () => {
              const w = el => el ? Math.round(el.getBoundingClientRect().width) : -1;
              const catalog = document.querySelector('.mesh-search-container');
              const pageEl = document.querySelector('.body-content, main, body');
              return JSON.stringify({ catalog: w(catalog), page: w(pageEl) });
            }
            """);
        System.IO.File.WriteAllText("/tmp/home-catalog-widths.txt", measure);

        var doc = JsonDocument.Parse(measure).RootElement;
        float catalogW = doc.GetProperty("catalog").GetSingle();
        float pageW = doc.GetProperty("page").GetSingle();
        (catalogW / pageW).Should().BeGreaterThan(0.8f,
            $"the catalog should fill the home width, not shrink to content (catalog={catalogW:F0}px of {pageW:F0}px)");
    }
}
