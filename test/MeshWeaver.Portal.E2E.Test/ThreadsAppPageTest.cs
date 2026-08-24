using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// The chat page at <c>/{user}/Chat</c> (the ChatArea): the node-less composer, and nothing that
/// resolves a layout area on ANOTHER node's hub.
///
/// <para>🚨 This used to assert a vertical RAIL of the owner's open threads whose rows rendered
/// through a <c>RailItem</c> area on each THREAD's own hub. That per-result foreign-area shape is
/// the one that failed in the distributed portal (as "AppTile not found" on the home) while
/// resolving happily in a monolith — so the rail is gone and this test pins what remains: the page
/// renders, and no error boundary fires.</para>
/// </summary>
[Collection("portal-e2e")]
public class ThreadsAppPageTest(PortalFixture fixture)
{
    [Fact(Timeout = 180_000)]
    public async Task ChatPage_RendersTheComposer_NoRenderErrors()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1600, 1000);

        await page.GotoAsync($"{fixture.BaseUrl}/{fixture.UserId}/Chat",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle });

        // The composer: a message box you can actually type into.
        await page.GetByText("Type a message", new() { Exact = false }).First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 90_000 });

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/threads-app.png", FullPage = true });

        // No layout-area error boundary anywhere on the page — neither "failed to render" nor the
        // unresolvable-area text that a foreign ItemArea produces.
        (await page.Locator("text=/failed to render|cannot be found/i").CountAsync())
            .Should().Be(0, "the chat page must render without any area error boundary firing");
    }
}
