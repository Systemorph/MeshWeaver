using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// Pins the CLICK-NAVIGATION contract of the React shell — the regression suite for the
/// "clicking a card does nothing / falls out of the React GUI" bug: every internal link the
/// renderer emits (mesh result cards, markdown anchors from the server's Markdig HTML) must
/// (1) actually navigate, (2) navigate CLIENT-SIDE through the Next router (no full page load —
/// a full load on a root-absolute href escapes the /next base path into the Blazor portal), and
/// (3) land on a live-rendered page. The fix is @meshweaver/react's NavigationProvider seam
/// (area/navigation.tsx) wired to the Next router in portal-next's MeshNavigation.tsx.
/// </summary>
[Collection("portal-e2e")]
public class PortalNextNavigationTest(PortalFixture fixture)
{
    private async Task<IBrowserContext> RequireNextAsync()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);
        var context = await fixture.NewAuthenticatedContextAsync();
        var probe = await context.APIRequest.GetAsync($"{fixture.BaseUrl}/next");
        Assert.SkipUnless((int)probe.Status == 200,
            $"/next not deployed on {fixture.BaseUrl} (HTTP {probe.Status}) — run 'memex-local e2e up'.");
        return context;
    }

    private static async Task<IPage> GotoLiveAsync(IBrowserContext context, string baseUrl, string path)
    {
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1600, 1000);
        await page.GotoAsync($"{baseUrl}/next{path}", new PageGotoOptions { WaitUntil = WaitUntilState.Load });
        // 150s: first touch of a per-node hub on a FRESH pod compiles/activates before rendering —
        // the same cold-start budget the parity suite's composer test carries.
        await page.Locator("[data-mw-live-area][data-mw-live='true']")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 150_000 });
        return page;
    }

    /// <summary>A survives-navigation marker: only a full page load wipes window state.</summary>
    private static Task StampSpaMarkerAsync(IPage page) =>
        page.EvaluateAsync("() => { window.__mwSpaMarker = 'alive'; }");

    private static async Task AssertClientSideNavigationAsync(IPage page)
    {
        (await page.EvaluateAsync<string>("() => window.__mwSpaMarker ?? ''"))
            .Should().Be("alive", "internal links must route through the Next router (SPA push), not a full page load");
    }

    // ── Markdown anchors: server-rendered "/Doc/…" hrefs route through the shell ─────────────────

    [Fact(Timeout = 240_000)]
    public async Task MarkdownLink_Click_NavigatesClientSide_InsideTheShell()
    {
        await using var context = await RequireNextAsync();
        var page = await GotoLiveAsync(context, fixture.BaseUrl!, "/Doc/GUI");

        // A rendered internal doc link (the Markdig HTML carries root-absolute "/Doc/…" hrefs).
        var link = page.Locator("[data-mw-live-area] .mw-markdown a[href^='/Doc/']").First;
        await link.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 90_000 });
        var target = (await link.GetAttributeAsync("href"))!;

        await StampSpaMarkerAsync(page);
        await link.ClickAsync();

        // The URL lands INSIDE the /next app (basePath applied by the router), on the link's target.
        await page.WaitForURLAsync($"**/next{target}", new PageWaitForURLOptions { Timeout = 30_000 });
        await AssertClientSideNavigationAsync(page);

        // And the destination renders live in the same shell (150s: destination first-touch
        // pays the cold-start budget, same as GotoLiveAsync).
        await page.Locator("[data-mw-live-area][data-mw-live='true']")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 150_000 });
        (await page.Locator("[data-mw-header]").CountAsync())
            .Should().BeGreaterThan(0, "the React shell chrome must survive the navigation");
    }

    // ── Result cards: mesh search/collection cards carry basePath hrefs and route client-side ────

    [Fact(Timeout = 240_000)]
    public async Task ResultCard_Click_NavigatesClientSide_ToTheNode()
    {
        await using var context = await RequireNextAsync();
        var page = await GotoLiveAsync(context, fixture.BaseUrl!, "");

        // The home regions (Open threads / Spaces tabs) render mesh result cards. Their hrefs must
        // carry the /next base path — a root-absolute href here IS the escape-the-app bug.
        var card = page.Locator("[data-mw-live-area] a[href^='/next/']").First;
        try
        {
            await card.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 90_000 });
        }
        catch (TimeoutException)
        {
            var rootAbsolute = await page.Locator("[data-mw-live-area] a[href^='/']:not([href^='/next'])").CountAsync();
            rootAbsolute.Should().Be(0,
                "result cards must render /next-based hrefs — root-absolute hrefs escape the React app (the click bug)");
            Assert.Skip("no result cards on the home regions (empty e2e data) — nothing to click");
        }

        var href = (await card.GetAttributeAsync("href"))!;
        await StampSpaMarkerAsync(page);
        await card.ClickAsync();

        await page.WaitForURLAsync($"**{href}", new PageWaitForURLOptions { Timeout = 30_000 });
        await AssertClientSideNavigationAsync(page);
        // 150s: the destination page pays the same first-touch cold-start as GotoLiveAsync when the
        // clicked node's hub has never been activated on this pod (e.g. a pinned Doc page).
        await page.Locator("[data-mw-live-area][data-mw-live='true']")
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Attached, Timeout = 150_000 });
    }
}
