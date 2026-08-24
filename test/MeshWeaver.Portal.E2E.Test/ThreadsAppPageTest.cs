using Microsoft.Playwright;
using Xunit;

namespace MeshWeaver.Portal.E2E;

/// <summary>
/// The THREADS APP page (<c>/{user}/Chat</c>, the ChatArea) — the agentic-app default view: a
/// collapsible threads side menu (New chat · filter · the viewer's open threads with live
/// activity status) beside the node-less composer, and nothing that resolves a layout area on
/// ANOTHER node's hub. This drives what a user actually does: the page renders (menu + welcome +
/// composer, no area error), sending a first message starts a REAL thread (StartThread) and opens
/// it full-screen with the user's bubble in the conversation and the side menu still present, and
/// the menu collapses/expands like the multi-part doc-index rail.
///
/// <para>🚨 This used to assert a vertical RAIL of the owner's open threads whose rows rendered
/// through a <c>RailItem</c> area on each THREAD's own hub. That per-result foreign-area shape is
/// the one that failed in the distributed portal (as "AppTile not found" on the home) while
/// resolving happily in a monolith — the rail is gone; the side menu is NATIVE to the chat view
/// and paints from the GetQuery snapshot, so nothing here resolves a foreign area.</para>
/// </summary>
[Collection("portal-e2e")]
public class ThreadsAppPageTest(PortalFixture fixture)
{
    [Fact(Timeout = 240_000)]
    public async Task ThreadsApp_RendersMenuAndComposer_SendingStartsAThread()
    {
        Assert.SkipUnless(fixture.Available, fixture.SkipReason);

        await using var context = await fixture.NewAuthenticatedContextAsync();
        var page = await context.NewPageAsync();
        await page.SetViewportSizeAsync(1600, 1000);

        await page.GotoAsync($"{fixture.BaseUrl}/{fixture.UserId}/Chat",
            new PageGotoOptions { WaitUntil = WaitUntilState.NetworkIdle, Timeout = 90_000 });

        // The side menu — the collapsible threads rail with the New-chat button and filter box.
        await page.Locator(".thread-nav-menu").First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 90_000 });
        (await page.Locator(".thread-nav-new").CountAsync())
            .Should().BeGreaterThan(0, "the side menu carries the New-chat entry");
        (await page.Locator(".thread-nav-filter").CountAsync())
            .Should().BeGreaterThan(0, "the side menu carries the thread filter box");

        // The composer + the welcome hero of the node-less page.
        await page.Locator(".thread-chat-footer").First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        (await page.Locator(".thread-welcome").CountAsync())
            .Should().BeGreaterThan(0, "the node-less page shows the start-a-conversation hero");

        // No layout-area error boundary anywhere on the page — neither "failed to render" nor the
        // unresolvable-area text that a foreign ItemArea produces.
        (await page.Locator("text=/failed to render|cannot be found/i").CountAsync())
            .Should().Be(0, "the Threads app must render without any area error boundary firing");

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/threads-app.png", FullPage = true });

        // Send the first message: this must StartThread and navigate to the thread page.
        var editor = page.Locator(".thread-chat-footer .monaco-editor").Last;
        await editor.WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        await editor.ClickAsync();
        await page.Keyboard.TypeAsync("Reply with exactly the word OK.");
        var send = page.Locator(".thread-chat-footer .selector-bar fluent-button").Last;
        await send.ClickAsync(new LocatorClickOptions { Timeout = 30_000 });

        // The thread page: URL under the user's _Thread partition, the user's bubble rendered,
        // the side menu still present (the default view when working with threads).
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(45);
        while (DateTime.UtcNow < deadline
               && !page.Url.Contains("_Thread", StringComparison.OrdinalIgnoreCase))
            await Task.Delay(250);
        page.Url.Should().Contain("_Thread",
            "sending the first message starts a real thread and opens it full-screen");

        await page.Locator(".thread-msg-bubble.thread-msg-user").First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 60_000 });
        (await page.Locator(".thread-chat-footer").CountAsync())
            .Should().BeGreaterThan(0, "the composer stays mounted on the thread page");
        await page.Locator(".thread-nav-menu").First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 30_000 });

        await page.ScreenshotAsync(new PageScreenshotOptions { Path = "/tmp/threads-thread-page.png", FullPage = true });

        (await page.Locator("text=/failed to render|cannot be found/i").CountAsync())
            .Should().Be(0, "the thread page must render without any area error boundary firing");

        // Collapse works like the multi-part menu: the rail gives way to a slim reveal toggle.
        await page.Locator(".thread-nav-collapse").First.ClickAsync();
        await page.Locator(".thread-nav-reveal").First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });
        (await page.Locator(".thread-nav-menu").CountAsync())
            .Should().Be(0, "collapsing hides the rail, leaving only the edge toggle");
        await page.Locator(".thread-nav-reveal").First.ClickAsync();
        await page.Locator(".thread-nav-menu").First
            .WaitForAsync(new LocatorWaitForOptions { State = WaitForSelectorState.Visible, Timeout = 15_000 });
    }
}
