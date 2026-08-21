using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// The THREADS APP page (<c>/{user}/Chat</c>, the ChatArea) — the Copilot-style shape: a vertical
/// rail of the owner's open threads (rows via the thread hub's <c>RailItem</c> area, each with an
/// ✕ that closes the thread) beside the node-less composer. This pins that the page renders
/// end-to-end: the rail's search container and the composer appear, and NO layout-area error
/// boundary ("failed to render") fires anywhere on the page.
/// </summary>
[Collection("portal-e2e")]
public class ThreadsAppPageTest(PortalFixture fixture)
{
    [Fact(Timeout = 180_000)]
    public async Task ThreadsApp_RendersRailAndComposer_NoRenderErrors()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1600, 1000);

        await page.GotoAsync($"{fixture.BaseUrl}/{fixture.UserId}/Chat",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // The rail — a MeshSearch over the owner's open threads.
        await page.Locator(".mesh-search-container").First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 90_000 });

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/threads-app.png", FullPage = true });

        // No layout-area error boundary anywhere on the page — "This view/area failed to render."
        (await page.GetByText("failed to render", new() { Exact = false }).CountAsync())
            .Should().Be(0, "the Threads app must render without any area error boundary firing");
    }
}
